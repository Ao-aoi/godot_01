using Godot;
using System.Collections.Generic;

public partial class CreatureAgent : RigidBody3D
{
	private enum DeathCause
	{
		None,
		Starvation,
		FallDamage,
	}

	[Signal] public delegate void DiedEventHandler(CreatureAgent creature);

	[Export] public string FoodGroupName = "food";
	[Export] public float BaseSightRange = 14.0f;
	[Export] public float BaseSmellRange = 26.0f;
	[Export] public float BaseFieldOfViewDegrees = 90.0f;
	[Export] public float MoveForce = 32.0f;
	[Export] public float TurnTorque = 12.0f;
	[Export] public float JumpImpulse = 2.3f;
	[Export] public float GroundProbeDistance = 0.95f;
	[Export] public float EatRadius = 0.72f;
	[Export] public float UprightCorrection = 8.0f;
	[Export] public float GroundRewardPerSecond = 0.05f;
	[Export] public float FoodReward = 12.0f;
	[Export] public float FallingPenalty = 4.0f;
	[Export] public float MaxHealth = 100.0f;
	[Export] public float FallDamageSafeSpeed = 11.0f;
	[Export] public float FallDamagePerSpeed = 4.2f;
	[Export] public float StarvationGracePeriod = 12.0f;
	[Export] public float StarvationDamagePerSecond = 3.5f;
	[Export] public float HealthBarHeight = 1.35f;
	[Export] public Vector2 HealthBarSize = new Vector2(140.0f, 18.0f);

	public float Fitness => _fitness;
	public int FoodsEaten => _foodsEaten;
	public float Health => _health;
	public bool IsDead => _isDead;
	public string DeathCauseLabel => _deathCauseLabel;

	private CreatureGenome _genome = CreatureGenome.Randomize();
	private CreatureBrain _brain;
	private List<string> _traits = new();
	private CreatureTraitModifiers _modifiers = CreatureTraitModifiers.Default;
	private float _fitness;
	private int _foodsEaten;
	private float _health;
	private float _lastTargetDistance = -1.0f;
	private float _lastUprightScore = 1.0f;
	private bool _configured;
	private bool _isDead;
	private bool _wasGrounded;
	private float _peakFallSpeed;
	private float _timeSinceLastMeal;
	private DeathCause _deathCause = DeathCause.None;
	private string _deathCauseLabel = "不明";
	private SubViewport _healthViewport;
	private ColorRect _healthFill;

	public override void _Ready()
	{
		ContactMonitor = true;
		MaxContactsReported = 8;
		CanSleep = false;
		LinearDamp = 0.18f;
		AngularDamp = 0.45f;
		_health = MaxHealth;
		_timeSinceLastMeal = 0.0f;
		SetupHealthBar();
		UpdateHealthBar();
	}

	public void Configure(CreatureGenome genome, IReadOnlyList<string> traits, int generationIndex)
	{
		_genome = genome != null ? genome.Clone() : CreatureGenome.Randomize();
		_brain = new CreatureBrain(_genome);
		_traits = traits != null ? new List<string>(traits) : new List<string>();
		_modifiers = CreatureTraitModifiers.FromTraits(_traits);
		configuredGeneration = generationIndex;
		_configured = true;
	}

	private int configuredGeneration;

	public override void _PhysicsProcess(double delta)
	{
		if (_isDead)
		{
			return;
		}

		if (_brain == null)
		{
			_brain = new CreatureBrain(_genome);
		}

		float[] inputs = BuildInputs();
		float[] outputs = _brain.Evaluate(inputs);

		ApplyActions(outputs, (float)delta);
		UpdateFitness(inputs, (float)delta);
		TryEatNearbyFood();
		ApplyStarvation((float)delta);
		HandleFallDamage(_wasGrounded);
		UpdateHealthBar();
	}

	private float[] BuildInputs()
	{
		float[] inputs = new float[CreatureGenome.InputCount];
		Vector3 targetDirection = Vector3.Zero;
		float targetDistance = 0.0f;
		float visibleSignal = 0.0f;
		float smellSignal = 0.0f;
		FindFoodSignal(out targetDirection, out targetDistance, out visibleSignal, out smellSignal);

		Vector3 localTarget = GlobalTransform.Basis.Inverse() * targetDirection;
		Vector3 localVelocity = GlobalTransform.Basis.Inverse() * LinearVelocity;
		Vector3 localAngularVelocity = GlobalTransform.Basis.Inverse() * AngularVelocity;
		float uprightScore = GetUprightScore();
		bool grounded = IsGrounded();

		inputs[0] = Mathf.Clamp(localTarget.X, -1.0f, 1.0f);
		inputs[1] = Mathf.Clamp(localTarget.Y, -1.0f, 1.0f);
		inputs[2] = Mathf.Clamp(localTarget.Z, -1.0f, 1.0f);
		inputs[3] = Mathf.Clamp(targetDistance, 0.0f, 1.0f);
		inputs[4] = visibleSignal;
		inputs[5] = smellSignal;
		inputs[6] = Mathf.Clamp(localVelocity.X * 0.18f, -1.0f, 1.0f);
		inputs[7] = Mathf.Clamp(localVelocity.Y * 0.18f, -1.0f, 1.0f);
		inputs[8] = Mathf.Clamp(localVelocity.Z * 0.18f, -1.0f, 1.0f);
		inputs[9] = Mathf.Clamp(localAngularVelocity.Y * 0.12f, -1.0f, 1.0f);
		inputs[10] = grounded ? 1.0f : 0.0f;
		inputs[11] = uprightScore;
		inputs[12] = Mathf.Clamp(Mathf.Abs(localVelocity.X) + Mathf.Abs(localVelocity.Z), 0.0f, 1.0f);
		inputs[13] = Mathf.Clamp(Mathf.Abs(localAngularVelocity.X) + Mathf.Abs(localAngularVelocity.Z), 0.0f, 1.0f);
		inputs[14] = Mathf.Clamp(_lastTargetDistance < 0.0f ? 0.0f : _lastTargetDistance, 0.0f, 1.0f);
		inputs[15] = _configured ? 1.0f : 0.0f;

		return inputs;
	}

	private void FindFoodSignal(out Vector3 bestDirection, out float bestDistance, out float visibleSignal, out float smellSignal)
	{
		bestDirection = Vector3.Zero;
		bestDistance = 1.0f;
		visibleSignal = 0.0f;
		smellSignal = 0.0f;

		float smellRange = BaseSmellRange * _modifiers.SmellRangeMultiplier;
		float sightRange = BaseSightRange * _modifiers.SightRangeMultiplier;
		float fovHalfAngle = (BaseFieldOfViewDegrees * _modifiers.FieldOfViewMultiplier) * 0.5f;
		Vector3 forward = -GlobalTransform.Basis.Z.Normalized();
		float strongestScore = float.NegativeInfinity;

		foreach (Node node in GetTree().GetNodesInGroup(FoodGroupName))
		{
			if (node is not Node3D food)
			{
				continue;
			}

			Vector3 offset = food.GlobalPosition - GlobalPosition;
			float distance = offset.Length();
			if (distance <= 0.001f || distance > smellRange)
			{
				continue;
			}

			Vector3 direction = offset / distance;
			Vector3 localDirection = GlobalTransform.Basis.Inverse() * direction;
			float visibility = 0.0f;
			float smell = 1.0f - Mathf.Clamp(distance / smellRange, 0.0f, 1.0f);
			float forwardDot = Mathf.Clamp(forward.Dot(direction), -1.0f, 1.0f);
			float angleDegrees = Mathf.RadToDeg(Mathf.Acos(forwardDot));
			bool inVisionCone = distance <= sightRange && angleDegrees <= fovHalfAngle && HasLineOfSight(food);

			if (inVisionCone)
			{
				visibility = 1.0f - Mathf.Clamp(distance / sightRange, 0.0f, 1.0f);
			}

			float score = (visibility * 2.0f) + smell;
			if (score > strongestScore)
			{
				strongestScore = score;
				bestDirection = localDirection.Normalized();
				bestDistance = Mathf.Clamp(distance / smellRange, 0.0f, 1.0f);
				visibleSignal = visibility;
				smellSignal = smell;
			}
		}
	}

	private bool HasLineOfSight(Node3D food)
	{
		Vector3 rayOrigin = GlobalPosition + Vector3.Up * 0.15f;
		Vector3 rayTarget = food.GlobalPosition + Vector3.Up * 0.1f;
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayTarget);
		query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;

		Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (result.Count == 0)
		{
			return false;
		}

		object collider = result["collider"];
		return collider == food || collider == food.GetParent();
	}

	private void ApplyActions(float[] outputs, float delta)
	{
		if (_isDead)
		{
			return;
		}

		Vector3 localMove = new Vector3(outputs[0], 0.0f, outputs[1]);
		if (localMove.Length() > 1.0f)
		{
			localMove = localMove.Normalized();
		}

		Vector3 worldMove = GlobalTransform.Basis * localMove;
		float moveMultiplier = _modifiers.MoveForceMultiplier;
		ApplyCentralForce(worldMove * MoveForce * moveMultiplier * Mass);
		ApplyTorque(Vector3.Up * outputs[2] * TurnTorque * moveMultiplier);

		Vector3 uprightAxis = GlobalTransform.Basis.Y.Normalized();
		Vector3 correctionAxis = uprightAxis.Cross(Vector3.Up);
		ApplyTorque(correctionAxis * UprightCorrection * _modifiers.StabilityMultiplier * Mass);

		if (outputs[3] > 0.55f && IsGrounded())
		{
			ApplyCentralImpulse(Vector3.Up * JumpImpulse * Mass);
		}
	}

	private void UpdateFitness(float[] inputs, float delta)
	{
		float currentDistance = inputs[3];
		float uprightScore = inputs[11];
		bool grounded = inputs[10] > 0.5f;

		if (_lastTargetDistance >= 0.0f)
		{
			float progress = _lastTargetDistance - currentDistance;
			_fitness += Mathf.Clamp(progress * 6.0f, -0.12f, 0.18f);
		}

		if (grounded)
		{
			_fitness += GroundRewardPerSecond * delta;
		}

		if (uprightScore < 0.5f)
		{
			_fitness -= (0.5f - uprightScore) * FallingPenalty * delta;
		}

		if (_lastUprightScore > 0.0f)
		{
			float stabilityDelta = uprightScore - _lastUprightScore;
			_fitness += stabilityDelta * 0.2f;
		}

		_lastTargetDistance = currentDistance;
		_lastUprightScore = uprightScore;
	}

	private void TryEatNearbyFood()
	{
		if (_isDead)
		{
			return;
		}

		float eatRadiusSquared = EatRadius * EatRadius;
		foreach (Node node in GetTree().GetNodesInGroup(FoodGroupName))
		{
			if (node is not Node3D food)
			{
				continue;
			}

			Vector3 offset = food.GlobalPosition - GlobalPosition;
			if (offset.LengthSquared() <= eatRadiusSquared)
			{
				_foodConsumed(food);
				return;
			}
		}
	}

	private void _foodConsumed(Node3D food)
	{
		if (!IsInstanceValid(food))
		{
			return;
		}
		// ログ出力を追加して、食べられたことを確認できるようにする
		_foodsEaten++;
		GD.Print($"{Name} がエサを食べた: {food.GlobalPosition}");
		_fitness += FoodReward;
		_timeSinceLastMeal = 0.0f;
		food.QueueFree();
	}

	private void ApplyStarvation(float delta)
	{
		_timeSinceLastMeal += delta;
		if (_timeSinceLastMeal <= StarvationGracePeriod)
		{
			return;
		}

		ApplyDamage(StarvationDamagePerSecond * delta, DeathCause.Starvation);
	}

	private void HandleFallDamage(bool wasGrounded)
	{
		bool groundedNow = IsGrounded();
		float fallingSpeed = Mathf.Max(0.0f, -LinearVelocity.Y);

		if (!groundedNow)
		{
			_peakFallSpeed = Mathf.Max(_peakFallSpeed, fallingSpeed);
		}
		else if (!wasGrounded && _peakFallSpeed > FallDamageSafeSpeed)
		{
			float damage = (_peakFallSpeed - FallDamageSafeSpeed) * FallDamagePerSpeed;
			ApplyDamage(damage, DeathCause.FallDamage);
			_peakFallSpeed = 0.0f;
		}
		else if (groundedNow)
		{
			_peakFallSpeed = 0.0f;
		}

		_wasGrounded = groundedNow;
	}

	private void ApplyDamage(float amount, DeathCause cause = DeathCause.None)
	{
		if (_isDead || amount <= 0.0f)
		{
			return;
		}

		if (cause != DeathCause.None)
		{
			_deathCause = cause;
			_deathCauseLabel = cause == DeathCause.Starvation ? "餓死" : "落下死";
		}

		CreateDamagePopup(Mathf.Max(1, Mathf.RoundToInt(amount)));
		_health = Mathf.Max(0.0f, _health - amount);
		if (_health <= 0.0f)
		{
			if (_deathCause == DeathCause.None)
			{
				_deathCauseLabel = "死亡";
			}
			Die();
		}
	}

	private void CreateDamagePopup(int amount)
	{
		Label3D damageLabel = new Label3D();
		damageLabel.Text = $"-{amount}";
		damageLabel.Modulate = new Color(1.0f, 0.18f, 0.18f, 1.0f);
		damageLabel.Position = new Vector3(0.0f, HealthBarHeight + 0.9f, 0.0f);
		damageLabel.Scale = Vector3.One * 0.35f;
		damageLabel.FontSize = 28;
		damageLabel.Set("billboard", 1);
		damageLabel.Set("fixed_size", true);
		damageLabel.Set("no_depth_test", true);
		AddChild(damageLabel);

		Tween tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(damageLabel, "position", damageLabel.Position + new Vector3(0.0f, 0.35f, 0.0f), 0.35f);
		tween.TweenProperty(damageLabel, "modulate:a", 0.0f, 0.35f);
		tween.TweenCallback(Callable.From(damageLabel.QueueFree));
	}

	private void Die()
	{
		if (_isDead)
		{
			return;
		}

		_isDead = true;
		LinearVelocity = Vector3.Zero;
		AngularVelocity = Vector3.Zero;
		Freeze = true;
		UpdateHealthBar();
		EmitSignal(SignalName.Died, this);
	}

	private void SetupHealthBar()
	{
		_healthViewport = new SubViewport();
		_healthViewport.Disable3D = true;
		_healthViewport.TransparentBg = true;
		_healthViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		_healthViewport.Size = new Vector2I((int)HealthBarSize.X, (int)HealthBarSize.Y);
		AddChild(_healthViewport);

		Control root = new Control();
		root.Size = HealthBarSize;
		_healthViewport.AddChild(root);

		ColorRect background = new ColorRect();
		background.Color = new Color(0.08f, 0.08f, 0.08f, 0.9f);
		background.Position = Vector2.Zero;
		background.Size = HealthBarSize;
		root.AddChild(background);

		_healthFill = new ColorRect();
		_healthFill.Color = new Color(0.15f, 0.85f, 0.22f, 0.95f);
		_healthFill.Position = new Vector2(2.0f, 2.0f);
		_healthFill.Size = new Vector2(Mathf.Max(1.0f, HealthBarSize.X - 4.0f), Mathf.Max(1.0f, HealthBarSize.Y - 4.0f));
		root.AddChild(_healthFill);

		Sprite3D healthSprite = new Sprite3D();
		healthSprite.Texture = _healthViewport.GetTexture();
		healthSprite.Set("billboard", 1);
		healthSprite.Set("fixed_size", true);
		healthSprite.PixelSize = 0.0025f;
		healthSprite.Scale = Vector3.One * 0.25f;
		healthSprite.Position = new Vector3(0.0f, HealthBarHeight, 0.0f);
		AddChild(healthSprite);
	}

	private void UpdateHealthBar()
	{
		if (_healthFill == null)
		{
			return;
		}

		float ratio = MaxHealth <= 0.0f ? 0.0f : Mathf.Clamp(_health / MaxHealth, 0.0f, 1.0f);
		float usableWidth = Mathf.Max(1.0f, HealthBarSize.X - 4.0f);
		float usableHeight = Mathf.Max(1.0f, HealthBarSize.Y - 4.0f);
		_healthFill.Size = new Vector2(usableWidth * ratio, usableHeight);

		if (ratio > 0.5f)
		{
			_healthFill.Color = new Color(0.15f, 0.85f, 0.22f, 0.95f);
		}
		else if (ratio > 0.25f)
		{
			_healthFill.Color = new Color(0.95f, 0.68f, 0.16f, 0.95f);
		}
		else
		{
			_healthFill.Color = new Color(0.92f, 0.18f, 0.18f, 0.95f);
		}
	}

	private bool IsGrounded()
	{
		Vector3 origin = GlobalPosition + Vector3.Up * 0.12f;
		Vector3 target = origin + Vector3.Down * GroundProbeDistance;
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target);
		query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;
		return GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
	}

	private float GetUprightScore()
	{
		Vector3 up = GlobalTransform.Basis.Y.Normalized();
		return Mathf.Clamp(up.Dot(Vector3.Up), -1.0f, 1.0f);
	}
}
