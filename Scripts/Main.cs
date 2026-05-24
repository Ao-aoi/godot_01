using Godot;
using Godot.Collections;
using System.Collections.Generic;

public partial class Main : Node3D
{
	[Export] public PackedScene CreatureScene;
	[Export] public PackedScene FoodScene;
	[Export] public int InitialSpawnCount = 1;
	[Export] public float SpawnRadius = 1.2f;
	[Export] public float GenerationRespawnDelay = 1.0f;
	[Export] public float AbyssY = -10.0f;

	[ExportGroup("Food Throw")]
	[Export] public float ThrowRayLength = 250.0f;
	[Export] public float DefaultFoodSpawnDistance = 6.0f;
	[Export] public float MinFoodSpawnDistance = 1.0f;
	[Export] public float MaxFoodSpawnDistance = 28.0f;
	[Export] public float FoodSpawnAdjustSensitivity = 0.04f;
	[Export] public float FoodSpawnWheelStep = 1.0f;
	[Export] public float FoodSpawnClearance = 0.35f;
	[Export] public float FoodSpawnHeight = 0.45f;
	[Export] public float PredictionDashLength = 0.5f;
	[Export] public float PredictionGapLength = 0.22f;
	[Export] public float PredictionScrollSpeed = 2.0f;
	[Export] public float PredictionLineThickness = 0.03f;
	[Export] public float PredictionLineThicknessScale = 1.7f;

	private readonly Vector3 _spawnPosition = new(0, 7, 0);
	private readonly List<RigidBody3D> _aliveCreatures = new();
	private readonly System.Collections.Generic.Dictionary<RigidBody3D, bool> _killedByPlayer = new();

	private CharacterBody3D _player;
	private Camera3D _camera;
	private MeshInstance3D _predictionLine;
	private MultiMeshInstance3D _predictionDashLine;
	private MeshInstance3D _groundMarker;
	private MeshInstance3D _spawnMarker;
	private ImmediateMesh _predictionMesh;
	private MultiMesh _predictionDashMultiMesh;
	private BoxMesh _predictionDashMesh;
	private StandardMaterial3D _predictionMaterial;
	private bool _isFoodAiming;
	private float _foodSpawnDistance;

	private int _generation = 1;
	private int _manualKillCount = 0;
	private bool _isGenerationTransitioning;

	public override void _Ready()
	{
		GD.Print("Artificial_Selection 起動成功！");
		_player = GetNode<CharacterBody3D>("Player");
		_camera = GetNode<Camera3D>("Player/Camera3D");
		_foodSpawnDistance = Mathf.Clamp(DefaultFoodSpawnDistance, MinFoodSpawnDistance, MaxFoodSpawnDistance);
		SetupPredictionLine();
		SpawnGeneration();
	}

	public override void _Process(double delta)
	{
		UpdatePredictionLine((float)delta);

		for (int i = _aliveCreatures.Count - 1; i >= 0; i--)
		{
			RigidBody3D creature = _aliveCreatures[i];
			if (!IsInstanceValid(creature))
			{
				_aliveCreatures.RemoveAt(i);
				continue;
			}

			if (creature.GlobalPosition.Y < AbyssY)
			{
				// 事故死: プレイヤー殺害扱いにしない
				_killedByPlayer[creature] = false;
				creature.QueueFree();
			}
		}

		if (!_isGenerationTransitioning && _aliveCreatures.Count == 0)
		{
			StartNextGeneration();
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event.IsActionPressed("ui_accept"))
		{
			GD.Print("Enter: 現世代に個体を追加");
			SpawnCreature();
		}

		if (@event.IsActionPressed("ui_select"))
		{
			ForceCullCurrentGeneration();
		}

		if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (mouseButton.Pressed)
			{
				_isFoodAiming = true;
			}
			else if (_isFoodAiming)
			{
				_isFoodAiming = false;
				SpawnFoodAtPredictionPoint();
			}
		}

		if (@event is InputEventMouseButton wheelButton)
		{
			if (wheelButton.ButtonIndex == MouseButton.WheelUp)
			{
				_foodSpawnDistance = Mathf.Clamp(_foodSpawnDistance + FoodSpawnWheelStep, MinFoodSpawnDistance, MaxFoodSpawnDistance);
			}
			else if (wheelButton.ButtonIndex == MouseButton.WheelDown)
			{
				_foodSpawnDistance = Mathf.Clamp(_foodSpawnDistance - FoodSpawnWheelStep, MinFoodSpawnDistance, MaxFoodSpawnDistance);
			}
		}

		if (_isFoodAiming && @event is InputEventMouseMotion mouseMotion)
		{
			_foodSpawnDistance = Mathf.Clamp(
				_foodSpawnDistance + (mouseMotion.Relative.Y * FoodSpawnAdjustSensitivity),
				MinFoodSpawnDistance,
				MaxFoodSpawnDistance
			);
		}
	}

	private void SetupPredictionLine()
	{
		if (_camera == null)
		{
			return;
		}

		_predictionMesh = new ImmediateMesh();
		_predictionMaterial = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = Colors.White,
			EmissionEnabled = true,
			Emission = Colors.White,
			EmissionEnergyMultiplier = 1.5f
		};

		_predictionLine = new MeshInstance3D
		{
			Mesh = _predictionMesh,
			MaterialOverride = _predictionMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};

		_camera.AddChild(_predictionLine);

		_predictionDashMesh = new BoxMesh
		{
			Size = new Vector3(1, 1, 1)
		};
		_predictionDashMultiMesh = new MultiMesh
		{
			Mesh = _predictionDashMesh,
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			UseColors = false,
			UseCustomData = false
		};

		_predictionDashLine = new MultiMeshInstance3D
		{
			Multimesh = _predictionDashMultiMesh,
			MaterialOverride = _predictionMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		_camera.AddChild(_predictionDashLine);

		_groundMarker = new MeshInstance3D
		{
			Mesh = CreateGroundMarkerMesh(),
			MaterialOverride = _predictionMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = false
		};
		AddChild(_groundMarker);

		_spawnMarker = new MeshInstance3D
		{
			Mesh = CreateSpawnMarkerMesh(),
			MaterialOverride = _predictionMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = false
		};
		AddChild(_spawnMarker);
	}

	private void UpdatePredictionLine(float delta)
	{
		if (_camera == null || _predictionLine == null || _predictionMesh == null || _groundMarker == null || _spawnMarker == null || _predictionDashLine == null || _predictionDashMultiMesh == null)
		{
			return;
		}

		if (!TryGetAimPoints(out Vector3 hitPosition, out Vector3 spawnPosition))
		{
			_predictionLine.Visible = false;
			_predictionDashLine.Visible = false;
			_groundMarker.Visible = false;
			_spawnMarker.Visible = false;
			_predictionMesh.ClearSurfaces();
			_predictionDashMultiMesh.InstanceCount = 0;
			return;
		}

		_groundMarker.Visible = true;
		_spawnMarker.Visible = true;
		_predictionDashLine.Visible = true;
		_groundMarker.GlobalPosition = hitPosition + Vector3.Up * 0.015f;
		_spawnMarker.GlobalPosition = spawnPosition;

		Vector3 localSpawn = _camera.ToLocal(spawnPosition);
		Vector3 localHit = _camera.ToLocal(hitPosition);
		float totalLength = localHit.DistanceTo(localSpawn);
		if (totalLength < 0.01f)
		{
			_predictionLine.Visible = false;
			_predictionDashLine.Visible = false;
			_predictionMesh.ClearSurfaces();
			_predictionDashMultiMesh.InstanceCount = 0;
			return;
		}

		_predictionLine.Visible = false;
		_predictionDashLine.Visible = true;
		_predictionMesh.ClearSurfaces();
		_predictionMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _predictionMaterial);

		float dashLength = Mathf.Max(0.05f, PredictionDashLength);
		float gapLength = Mathf.Max(0.02f, PredictionGapLength);
		float cycleLength = dashLength + gapLength;
		float phase = Mathf.PosMod((float)Time.GetTicksMsec() * 0.001f * PredictionScrollSpeed, cycleLength);

		for (float segmentStart = phase; segmentStart < totalLength; segmentStart += cycleLength)
		{
			float segmentEnd = Mathf.Min(segmentStart + dashLength, totalLength);
			Vector3 startPoint = localSpawn.Lerp(localHit, segmentStart / totalLength);
			Vector3 endPoint = localSpawn.Lerp(localHit, segmentEnd / totalLength);
			_predictionMesh.SurfaceAddVertex(startPoint);
			_predictionMesh.SurfaceAddVertex(endPoint);
		}

		_predictionMesh.SurfaceEnd();

		float thickDashLength = dashLength;
		float thickGapLength = gapLength;
		float thickCycleLength = thickDashLength + thickGapLength;
		int dashCount = Mathf.CeilToInt(Mathf.Max(1.0f, (totalLength - phase) / thickCycleLength));
		_predictionDashMultiMesh.InstanceCount = dashCount;

		float thickness = Mathf.Max(0.006f, PredictionLineThickness * PredictionLineThicknessScale);
		int dashIndex = 0;
		for (float segmentStart = phase; segmentStart < totalLength && dashIndex < dashCount; segmentStart += thickCycleLength, dashIndex++)
		{
			float segmentEnd = Mathf.Min(segmentStart + thickDashLength, totalLength);
			Vector3 startPoint = localSpawn.Lerp(localHit, segmentStart / totalLength);
			Vector3 endPoint = localSpawn.Lerp(localHit, segmentEnd / totalLength);
			Vector3 segmentVector = endPoint - startPoint;
			float segmentLength = segmentVector.Length();
			if (segmentLength < 0.001f)
			{
				continue;
			}

			Vector3 direction = segmentVector / segmentLength;
			Vector3 center = startPoint + (segmentVector * 0.5f);
			Basis basis = Basis.LookingAt(direction, Vector3.Up);
			basis = basis.Scaled(new Vector3(thickness, thickness, segmentLength));
			_predictionDashMultiMesh.SetInstanceTransform(dashIndex, new Transform3D(basis, center));
		}
	}

	private bool TryGetAimPoints(out Vector3 hitPosition, out Vector3 spawnPosition)
	{
		hitPosition = Vector3.Zero;
		spawnPosition = Vector3.Zero;

		if (_camera == null || _player == null)
		{
			return false;
		}

		Vector3 origin = _camera.GlobalPosition;
		Vector3 forward = -_camera.GlobalTransform.Basis.Z.Normalized();
		spawnPosition = origin + (forward * Mathf.Clamp(_foodSpawnDistance, MinFoodSpawnDistance, MaxFoodSpawnDistance));

		Vector3 rayOrigin = spawnPosition + Vector3.Up * 0.01f;
		Vector3 rayTarget = rayOrigin + (Vector3.Down * ThrowRayLength);
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayTarget);
		query.Exclude = new Array<Rid> { _player.GetRid() };
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;

		Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (result.Count == 0)
		{
			return false;
		}

		hitPosition = result["position"].As<Vector3>();
		return true;
	}

	private void SpawnFoodAtPredictionPoint()
	{
		if (FoodScene == null)
		{
			GD.PrintErr("エラー：インスペクターで FoodScene が設定されていません！");
			return;
		}

		if (!TryGetAimPoints(out Vector3 hitPosition, out Vector3 spawnPosition))
		{
			return;
		}

		RigidBody3D foodInstance = FoodScene.Instantiate<RigidBody3D>();
		foodInstance.GlobalPosition = spawnPosition + Vector3.Up * FoodSpawnHeight;
		foodInstance.LinearVelocity = Vector3.Zero;
		foodInstance.AngularVelocity = Vector3.Zero;
		AddChild(foodInstance);
		GD.Print($"エサを投下: {foodInstance.GlobalPosition}");
	}

	private static Mesh CreateGroundMarkerMesh()
	{
		CylinderMesh mesh = new()
		{
			TopRadius = 0.22f,
			BottomRadius = 0.22f,
			Height = 0.02f,
			RadialSegments = 24,
			Rings = 1
		};
		return mesh;
	}

	private static Mesh CreateSpawnMarkerMesh()
	{
		SphereMesh mesh = new()
		{
			Radius = 0.08f,
			Height = 0.16f,
			RadialSegments = 16,
			Rings = 8
		};
		return mesh;
	}

	private async void StartNextGeneration()
	{
		_isGenerationTransitioning = true;
		_generation++;
		GD.Print($"--- 世代交代: Generation {_generation} ---");
		await ToSignal(GetTree().CreateTimer(GenerationRespawnDelay), SceneTreeTimer.SignalName.Timeout);
		SpawnGeneration();
		_isGenerationTransitioning = false;
	}

	private void SpawnGeneration()
	{
		int spawnCount = Mathf.Max(1, InitialSpawnCount);
		GD.Print($"Generation {_generation}: {spawnCount}体を孵化");

		for (int i = 0; i < spawnCount; i++)
		{
			SpawnCreature();
		}
	}

	private void SpawnCreature()
	{
		if (CreatureScene == null)
		{
			GD.PrintErr("エラー：インスペクターで CreatureScene が設定されていません！");
			return;
		}

		RigidBody3D creatureInstance = CreatureScene.Instantiate<RigidBody3D>();
		Vector3 randomOffset = new(
			(float)GD.RandRange(-SpawnRadius, SpawnRadius),
			0,
			(float)GD.RandRange(-SpawnRadius, SpawnRadius)
		);
		creatureInstance.GlobalPosition = _spawnPosition + randomOffset;

		// 殺害ペナルティ: 手動殺害回数が増えるほど次世代がタフに
		float toughness = 1.0f + (_manualKillCount * 0.06f);
		creatureInstance.Mass *= toughness;
		creatureInstance.Scale = Vector3.One * Mathf.Clamp(toughness, 1.0f, 2.2f);

		AddChild(creatureInstance);
		_aliveCreatures.Add(creatureInstance);
		_killedByPlayer[creatureInstance] = false;
	}

	private void ForceCullCurrentGeneration()
	{
		if (_aliveCreatures.Count == 0)
		{
			return;
		}

		GD.Print("Space: 強制世代終了（手動殺害）");
		for (int i = _aliveCreatures.Count - 1; i >= 0; i--)
		{
			RigidBody3D creature = _aliveCreatures[i];
			if (!IsInstanceValid(creature))
			{
				continue;
			}

			_killedByPlayer[creature] = true;
			_manualKillCount++;
			creature.QueueFree();
		}

		_aliveCreatures.Clear();
		GD.Print($"手動殺害ペナルティ累計: {_manualKillCount}");
	}
}
