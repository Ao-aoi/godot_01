using Godot;
using System.Collections.Generic;

public partial class CreatureAgent : Node3D
{
	private const int GeneralInputCount = 8;
	private const int PerBoneInputCount = 6;
	private const int GeneralOutputCount = 3;
	private const int PerBoneOutputCount = 3;

	private static readonly BoneDefinition[] BoneDefinitions = new BoneDefinition[]
	{
		new("Root", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone Root", false, 1.20f),
		new("Spine", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone Spine", false, 1.10f),
		new("Head", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone Head", false, 0.75f),
		new("Leg_L", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone Leg_L", false, 1.15f),
		new("Foot_L", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone Foot_L", true, 0.85f),
		new("Leg_R", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone Leg_R", false, 1.15f),
		new("Foot_R", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone Foot_R", true, 0.85f),
		new("UpperArm_L", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone UpperArm_L", false, 0.95f),
		new("LowerArm_L", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone LowerArm_L", false, 0.75f),
		new("UpperArm_R", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone UpperArm_R", false, 0.95f),
		new("LowerArm_R", "Armature/Skeleton3D/PhysicalBoneSimulator3D/Physical Bone LowerArm_R", false, 0.75f),
	};

	[Export] public string FoodGroupName = "food";
	[Export] public float BaseSightRange = 14.0f;
	[Export] public float BaseSmellRange = 26.0f;
	[Export] public float BaseFieldOfViewDegrees = 90.0f;
	[Export] public float JointTorque = 28.0f;
	[Export] public float JointDamping = 5.5f;
	[Export] public float RootDriveForce = 14.0f;
	[Export] public float RootYawTorque = 10.0f;
	[Export] public float JumpImpulse = 2.1f;
	[Export] public float GroundProbeDistance = 0.95f;
	[Export] public float EatRadius = 0.72f;
	[Export] public float GroundRewardPerSecond = 0.05f;
	[Export] public float FoodReward = 12.0f;
	[Export] public float FallingPenalty = 4.0f;
	[Export] public float TorqueOutputScale = 1.0f;

	public float Fitness => _fitness;
	public int FoodsEaten => _foodsEaten;

	private sealed class BoneSlot
	{
		public string Name;
		public string NodePath;
		public bool IsFoot;
		public float Weight;
		public PhysicalBone3D Bone;
		public Basis RestLocalBasis;

		public BoneSlot(string name, string nodePath, bool isFoot, float weight)
		{
			Name = name;
			NodePath = nodePath;
			IsFoot = isFoot;
			Weight = weight;
			RestLocalBasis = Basis.Identity;
		}
	}

	private CreatureGenome _genome = CreatureGenome.Randomize();
	private CreatureBrain _brain;
	private List<string> _traits = new();
	private CreatureTraitModifiers _modifiers = CreatureTraitModifiers.Default;
	private readonly List<BoneSlot> _boneSlots = new();
	private PhysicalBone3D _rootBone;
	private PhysicalBone3D _spineBone;
	private PhysicalBone3D _headBone;
	private float _fitness;
	private int _foodsEaten;
	private float _lastTargetDistance = -1.0f;
	private float _lastUprightScore = 1.0f;
	private bool _configured;
	private int _generationIndex;
	private bool _rigInitialized;

	public override void _Ready()
	{
		InitializeRig();
		_brain ??= new CreatureBrain(_genome);
	}

	public void Configure(CreatureGenome genome, IReadOnlyList<string> traits, int generationIndex)
	{
		_genome = genome != null ? genome.Clone() : CreatureGenome.Randomize();
		_brain = new CreatureBrain(_genome);
		_traits = traits != null ? new List<string>(traits) : new List<string>();
		_modifiers = CreatureTraitModifiers.FromTraits(_traits);
		_generationIndex = generationIndex;
		_configured = true;
		InitializeRig();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!_rigInitialized)
		{
			InitializeRig();
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
	}

	private void InitializeRig()
	{
		if (_rigInitialized || !IsInsideTree())
		{
			return;
		}

		// Ensure the PhysicalBoneSimulator3D is active / running so child PhysicalBone3D nodes simulate
		PhysicalBoneSimulator3D simulator = GetNodeOrNull<PhysicalBoneSimulator3D>("Armature/Skeleton3D/PhysicalBoneSimulator3D");
		if (simulator != null)
		{
			try
			{
				simulator.Set("active", true);
				if (simulator.HasMethod("physical_bones_start_simulation"))
				{
					simulator.Call("physical_bones_start_simulation");
				}
				else if (simulator.HasMethod("start_simulation"))
				{
					simulator.Call("start_simulation");
				}
			}
			catch
			{
				// best-effort; ignore if API differs across Godot versions
			}
		}

		GD.Print($"CreatureAgent: PhysicalBoneSimulator3D found: {simulator != null}");

		_boneSlots.Clear();
		for (int i = 0; i < BoneDefinitions.Length; i++)
		{
			BoneDefinition definition = BoneDefinitions[i];
			PhysicalBone3D bone = GetNodeOrNull<PhysicalBone3D>(definition.NodePath);
			BoneSlot slot = new BoneSlot(definition.Name, definition.NodePath, definition.IsFoot, definition.Weight)
			{
				Bone = bone
			};

			if (bone != null)
			{
				bone.CanSleep = false;
				bone.CustomIntegrator = true;
				bone.LinearDamp = 0.18f;
				bone.AngularDamp = 0.5f;
				slot.RestLocalBasis = bone.Transform.Basis;
			}

			_boneSlots.Add(slot);
		}

		int foundCount = 0;
		for (int i = 0; i < _boneSlots.Count; i++) if (_boneSlots[i].Bone != null) foundCount++;
		GD.Print($"CreatureAgent: bone slots total={_boneSlots.Count}, found={foundCount}");

		_rootBone = GetBoneByName("Root") ?? _boneSlots[0].Bone;
		_spineBone = GetBoneByName("Spine") ?? _rootBone;
		_headBone = GetBoneByName("Head") ?? _spineBone ?? _rootBone;
		_rigInitialized = _boneSlots.Exists(slot => slot.Bone != null);
	}

	private PhysicalBone3D GetBoneByName(string name)
	{
		for (int i = 0; i < _boneSlots.Count; i++)
		{
			BoneSlot slot = _boneSlots[i];
			if (slot.Name == name)
			{
				return slot.Bone;
			}
		}

		return null;
	}

	private float[] BuildInputs()
	{
		float[] inputs = new float[CreatureGenome.InputCount];

		Vector3 foodDirectionWorld;
		float foodDistanceNormalized;
		float visibleSignal;
		float smellSignal;
		FindFoodSignal(out foodDirectionWorld, out foodDistanceNormalized, out visibleSignal, out smellSignal);

		Node3D referenceNode = GetReferenceNode();
		Vector3 localFoodDirection = referenceNode.GlobalTransform.Basis.Inverse() * foodDirectionWorld;
		Vector3 localVelocity = _rootBone != null
			? _rootBone.GlobalTransform.Basis.Inverse() * _rootBone.LinearVelocity
			: Vector3.Zero;
		Vector3 localAngularVelocity = _rootBone != null
			? _rootBone.GlobalTransform.Basis.Inverse() * _rootBone.AngularVelocity
			: Vector3.Zero;
		float uprightScore = GetUprightScore();
		float groundedFeetRatio = GetGroundedFeetRatio();

		inputs[0] = Mathf.Clamp(localFoodDirection.X, -1.0f, 1.0f);
		inputs[1] = Mathf.Clamp(localFoodDirection.Y, -1.0f, 1.0f);
		inputs[2] = Mathf.Clamp(localFoodDirection.Z, -1.0f, 1.0f);
		inputs[3] = Mathf.Clamp(foodDistanceNormalized, 0.0f, 1.0f);
		inputs[4] = Mathf.Clamp(visibleSignal, 0.0f, 1.0f);
		inputs[5] = Mathf.Clamp(smellSignal, 0.0f, 1.0f);
		inputs[6] = Mathf.Clamp(uprightScore, -1.0f, 1.0f);
		inputs[7] = Mathf.Clamp(groundedFeetRatio, 0.0f, 1.0f);

		for (int i = 0; i < _boneSlots.Count; i++)
		{
			BoneSlot slot = _boneSlots[i];
			int offset = GeneralInputCount + (i * PerBoneInputCount);
			if (offset + PerBoneInputCount > inputs.Length)
			{
				break;
			}

			if (slot.Bone == null)
			{
				continue;
			}

			Basis relativeBasis = slot.RestLocalBasis.Inverse() * slot.Bone.Transform.Basis;
			Vector3 relativeEuler = relativeBasis.GetEuler();
			Vector3 localBoneAngularVelocity = slot.Bone.GlobalTransform.Basis.Inverse() * slot.Bone.AngularVelocity;

			inputs[offset + 0] = Mathf.Clamp(relativeEuler.X / Mathf.Pi, -1.0f, 1.0f);
			inputs[offset + 1] = Mathf.Clamp(relativeEuler.Y / Mathf.Pi, -1.0f, 1.0f);
			inputs[offset + 2] = Mathf.Clamp(relativeEuler.Z / Mathf.Pi, -1.0f, 1.0f);
			inputs[offset + 3] = Mathf.Clamp(localBoneAngularVelocity.X * 0.12f, -1.0f, 1.0f);
			inputs[offset + 4] = Mathf.Clamp(localBoneAngularVelocity.Y * 0.12f, -1.0f, 1.0f);
			inputs[offset + 5] = Mathf.Clamp(localBoneAngularVelocity.Z * 0.12f, -1.0f, 1.0f);
			if (slot.IsFoot)
			{
				inputs[offset + 5] = IsFootGrounded(slot.Bone) ? 1.0f : 0.0f;
			}
		}

		return inputs;
	}

	private void FindFoodSignal(out Vector3 bestDirectionWorld, out float bestDistanceNormalized, out float visibleSignal, out float smellSignal)
	{
		bestDirectionWorld = Vector3.Zero;
		bestDistanceNormalized = 1.0f;
		visibleSignal = 0.0f;
		smellSignal = 0.0f;

		Node3D sensorNode = GetReferenceNode();
		float smellRange = BaseSmellRange * _modifiers.SmellRangeMultiplier;
		float sightRange = BaseSightRange * _modifiers.SightRangeMultiplier;
		float fovHalfAngle = (BaseFieldOfViewDegrees * _modifiers.FieldOfViewMultiplier) * 0.5f;
		Vector3 forward = -sensorNode.GlobalTransform.Basis.Z.Normalized();
		float strongestScore = float.NegativeInfinity;

		foreach (Node node in GetTree().GetNodesInGroup(FoodGroupName))
		{
			if (node is not Node3D food)
			{
				continue;
			}

			Vector3 offset = food.GlobalPosition - sensorNode.GlobalPosition;
			float distance = offset.Length();
			if (distance <= 0.001f || distance > smellRange)
			{
				continue;
			}

			Vector3 directionWorld = offset / distance;
			Vector3 directionLocal = sensorNode.GlobalTransform.Basis.Inverse() * directionWorld;
			float smell = 1.0f - Mathf.Clamp(distance / smellRange, 0.0f, 1.0f);
			float visibility = 0.0f;
			float angleDegrees = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(forward.Dot(directionWorld), -1.0f, 1.0f)));
			bool inVisionCone = distance <= sightRange && angleDegrees <= fovHalfAngle && HasLineOfSight(sensorNode.GlobalPosition, food.GlobalPosition);

			if (inVisionCone)
			{
				visibility = 1.0f - Mathf.Clamp(distance / sightRange, 0.0f, 1.0f);
			}

			float score = (visibility * 2.0f) + smell;
			if (score > strongestScore)
			{
				strongestScore = score;
				bestDirectionWorld = directionLocal.Length() > 0.0f ? directionLocal.Normalized() : Vector3.Zero;
				bestDistanceNormalized = Mathf.Clamp(distance / smellRange, 0.0f, 1.0f);
				visibleSignal = visibility;
				smellSignal = smell;
			}
		}
	}

	private bool HasLineOfSight(Vector3 fromPosition, Vector3 toPosition)
	{
		if (!IsInsideTree())
		{
			return false;
		}

		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(fromPosition, toPosition);
		Godot.Collections.Array<Rid> exclude = new Godot.Collections.Array<Rid>();
		for (int i = 0; i < _boneSlots.Count; i++)
		{
			if (_boneSlots[i].Bone != null)
			{
				exclude.Add(_boneSlots[i].Bone.GetRid());
			}
		}
		query.Exclude = exclude;
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;

		Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (result.Count == 0)
		{
			return false;
		}

		Node collider = result["collider"].As<Node>();
		return collider != null && collider.IsInGroup(FoodGroupName);
	}

	private void ApplyActions(float[] outputs, float delta)
	{
		if (_rootBone != null && outputs.Length >= 3)
		{
			Vector3 rootForward = -_rootBone.GlobalTransform.Basis.Z.Normalized();
			float drive = outputs[0] * RootDriveForce * _modifiers.MoveForceMultiplier;
			_rootBone.LinearVelocity += rootForward * (drive * delta);
			_rootBone.AngularVelocity += Vector3.Up * (outputs[2] * RootYawTorque * delta);

			if (outputs[1] > 0.55f && IsGrounded())
			{
				_rootBone.ApplyCentralImpulse(Vector3.Up * JumpImpulse);
			}
		}

		for (int i = 0; i < _boneSlots.Count; i++)
		{
			BoneSlot slot = _boneSlots[i];
			if (slot.Bone == null)
			{
				continue;
			}

			int offset = GeneralOutputCount + (i * PerBoneOutputCount);
			if (offset + PerBoneOutputCount > outputs.Length)
			{
				break;
			}

			Vector3 desiredLocalTorque = new Vector3(outputs[offset + 0], outputs[offset + 1], outputs[offset + 2]) * TorqueOutputScale * JointTorque * slot.Weight * _modifiers.MoveForceMultiplier;
			Vector3 localAngularVelocity = slot.Bone.GlobalTransform.Basis.Inverse() * slot.Bone.AngularVelocity;
			Vector3 dampingTorque = -localAngularVelocity * JointDamping * slot.Weight * _modifiers.StabilityMultiplier;
			Vector3 angularDelta = slot.Bone.GlobalTransform.Basis * (desiredLocalTorque + dampingTorque);
			slot.Bone.AngularVelocity += angularDelta * delta;
		}
	}

	private void UpdateFitness(float[] inputs, float delta)
	{
		float currentDistance = inputs[3];
		float uprightScore = inputs[6];
		float groundedRatio = inputs[7];

		if (_lastTargetDistance >= 0.0f)
		{
			float progress = _lastTargetDistance - currentDistance;
			_fitness += Mathf.Clamp(progress * 6.0f, -0.12f, 0.18f);
		}

		_fitness += groundedRatio * GroundRewardPerSecond * delta;

		if (uprightScore < 0.5f)
		{
			_fitness -= (0.5f - uprightScore) * FallingPenalty * delta;
		}

		if (_foodsEaten > 0)
		{
			_fitness += _foodsEaten * 0.0025f;
		}

		_lastTargetDistance = currentDistance;
		_lastUprightScore = uprightScore;
	}

	private void TryEatNearbyFood()
	{
		Node3D sensorNode = GetMouthNode();
		float eatRadiusSquared = EatRadius * EatRadius;
		foreach (Node node in GetTree().GetNodesInGroup(FoodGroupName))
		{
			if (node is not Node3D food)
			{
				continue;
			}

			Vector3 offset = food.GlobalPosition - sensorNode.GlobalPosition;
			if (offset.LengthSquared() <= eatRadiusSquared)
			{
				_foodsEaten++;
				_fitness += FoodReward;
				food.QueueFree();
				return;
			}
		}
	}

	private bool IsGrounded()
	{
		Node3D sensorNode = _rootBone != null ? _rootBone : this;
		Vector3 origin = sensorNode.GlobalPosition + Vector3.Up * 0.14f;
		Vector3 target = origin + Vector3.Down * GroundProbeDistance;
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target);
		Godot.Collections.Array<Rid> exclude = new Godot.Collections.Array<Rid>();
		for (int i = 0; i < _boneSlots.Count; i++)
		{
			if (_boneSlots[i].Bone != null)
			{
				exclude.Add(_boneSlots[i].Bone.GetRid());
			}
		}
		query.Exclude = exclude;
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;
		return GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
	}

	private bool IsFootGrounded(PhysicalBone3D footBone)
	{
		if (footBone == null)
		{
			return false;
		}

		Vector3 origin = footBone.GlobalPosition + Vector3.Up * 0.06f;
		Vector3 target = origin + Vector3.Down * GroundProbeDistance;
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target);
		Godot.Collections.Array<Rid> exclude = new Godot.Collections.Array<Rid> { footBone.GetRid() };
		query.Exclude = exclude;
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;
		return GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
	}

	private float GetGroundedFeetRatio()
	{
		int footCount = 0;
		int groundedCount = 0;

		for (int i = 0; i < _boneSlots.Count; i++)
		{
			BoneSlot slot = _boneSlots[i];
			if (!slot.IsFoot || slot.Bone == null)
			{
				continue;
			}

			footCount++;
			if (IsFootGrounded(slot.Bone))
			{
				groundedCount++;
			}
		}

		return footCount == 0 ? 0.0f : (float)groundedCount / footCount;
	}

	private float GetUprightScore()
	{
		Node3D reference = GetReferenceNode();
		Vector3 up = reference.GlobalTransform.Basis.Y.Normalized();
		return Mathf.Clamp(up.Dot(Vector3.Up), -1.0f, 1.0f);
	}

	private Node3D GetReferenceNode()
	{
		if (_spineBone != null)
		{
			return _spineBone;
		}

		if (_rootBone != null)
		{
			return _rootBone;
		}

		for (int i = 0; i < _boneSlots.Count; i++)
		{
			if (_boneSlots[i].Bone != null)
			{
				return _boneSlots[i].Bone;
			}
		}

		return this;
	}

	private Node3D GetMouthNode()
	{
		if (_headBone != null)
		{
			return _headBone;
		}

		if (_spineBone != null)
		{
			return _spineBone;
		}

		return GetReferenceNode();
	}

	private sealed class BoneDefinition
	{
		public string Name;
		public string NodePath;
		public bool IsFoot;
		public float Weight;

		public BoneDefinition(string name, string nodePath, bool isFoot, float weight)
		{
			Name = name;
			NodePath = nodePath;
			IsFoot = isFoot;
			Weight = weight;
		}
	}
}
