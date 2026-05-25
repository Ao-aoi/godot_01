using Godot;
using Godot.Collections;
using System.Collections.Generic;
using System.Linq;

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
	private readonly List<CreatureMeta> _generationCandidates = new();

	// フェーズ2用: 個体に関するメタデータ管理
	private class CreatureMeta
	{
		public string DisplayName = string.Empty;
		public List<string> Traits = new();
		public bool Alive = true;
		public int GenerationIndex = 1;
		public float Fitness = 0.0f;
		public int FoodsEaten = 0;
		public CreatureGenome Genome = CreatureGenome.Randomize();
		public CreatureAgent Agent = null;
	}
	private readonly System.Collections.Generic.Dictionary<RigidBody3D, CreatureMeta> _creatureMeta = new();

	// ホバー/ハイライト用
	private RigidBody3D _hoveredCreature = null;
	private MeshInstance3D _hoveredMesh = null;
	private StandardMaterial3D _highlightMaterial = null;
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
		SetupCreatureUI();
		SpawnGeneration();
	}

	public override void _Process(double delta)
	{
		UpdatePredictionLine((float)delta);
		UpdateHoverFromMouse();

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
				HandleCreatureDeath(creature, false);
				UpdateCreatureUI();
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
			// `ui_accept` のうちスペースキーによる発火を無視して、物理的な Enter のみ受け付ける
			if (@event is InputEventKey keyEvent)
			{
				// physical_keycode が Space の場合は無視する
				if (keyEvent.PhysicalKeycode == Key.Space)
				{
					return;
				}
			}
			GD.Print("Enter: 現世代に個体を追加");
			SpawnCreature(CreatureGenome.Randomize(), CreateTraitsForNextCreature());
		}

		// 世代強制終了（以前はスペースキーに割り当て）を入力ハンドラから除去しました。

		if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (mouseButton.Pressed)
			{
				// ホバーしている個体があればそれを選択（ズーム開始）、なければエサ狙い開始
				if (_hoveredCreature != null)
				{
					SelectCreature(_hoveredCreature);
				}
				else
				{
					_isFoodAiming = true;
				}
			}
			else if (_isFoodAiming)
			{
				_isFoodAiming = false;
				SpawnFoodAtPredictionPoint();
			}
		}

		// 右クリックで個体選択（フェーズ2: 3Dクリック選択）
		if (@event is InputEventMouseButton rightButton && rightButton.ButtonIndex == MouseButton.Right && rightButton.Pressed)
		{
			if (_camera == null) return;
			Vector2 mpos = rightButton.Position;
			Vector3 rayOrigin = _camera.ProjectRayOrigin(mpos);
			Vector3 rayDir = _camera.ProjectRayNormal(mpos);
			Vector3 rayTarget = rayOrigin + rayDir * 250.0f;
			PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayTarget);
			query.Exclude = new Array<Rid> { _player.GetRid() };
			query.CollideWithBodies = true;
			var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
			if (result.Count > 0)
			{
				object collider = result["collider"];
				if (collider is RigidBody3D rb && _aliveCreatures.Contains(rb))
				{
					SelectCreature(rb);
				}
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

	private void SelectCreature(RigidBody3D creature)
	{
		if (_camera == null) return;
		if (_camera is CameraController cc)
		{
			cc.StartFollow(creature);
		}
	}

	// ---- フェーズ2: 簡易UIの生成と更新 ----
	private CanvasLayer _uiLayer;
	private VBoxContainer _aliveList;
	private VBoxContainer _deadList;
	private readonly System.Collections.Generic.Dictionary<RigidBody3D, Button> _creatureButtons = new();

	private void SetupCreatureUI()
	{
		_uiLayer = new CanvasLayer();
		AddChild(_uiLayer);

		Panel panel = new Panel();
		// 位置やサイズはエディタ側で調整するのでここでは最低限の構築のみ行う
		_uiLayer.AddChild(panel);

		VBoxContainer root = new VBoxContainer();
		// layout settings can be adjusted in editor if needed
		panel.AddChild(root);

		Label aliveLabel = new Label();
		aliveLabel.Text = "生存中";
		root.AddChild(aliveLabel);

		_aliveList = new VBoxContainer();
		root.AddChild(_aliveList);

		Label deadLabel = new Label();
		deadLabel.Text = "死亡済み";
		root.AddChild(deadLabel);

		_deadList = new VBoxContainer();
		root.AddChild(_deadList);
	}

	private void UpdateCreatureUI()
	{
		if (_uiLayer == null) return;

		// クリア
		ClearContainerChildren(_aliveList);
		ClearContainerChildren(_deadList);
		_creatureButtons.Clear();

		// 生存中リスト
		foreach (var kv in _creatureMeta)
		{
			RigidBody3D c = kv.Key;
			CreatureMeta meta = kv.Value;
			string label = (meta.Alive ? "生存: " : "死亡: ") + meta.DisplayName;
			label += $"  F:{meta.Fitness:0.0}  食:{meta.FoodsEaten}";
			if (meta.Traits.Count > 0)
			{
				label += " [" + string.Join(",", meta.Traits) + "]";
			}
			Button b = new Button();
			b.Text = label;
			b.Disabled = !meta.Alive;
			RigidBody3D captured = c;
			b.Pressed += () => { if (captured != null) SelectCreature(captured); };
			if (meta.Alive) _aliveList.AddChild(b); else _deadList.AddChild(b);
			_creatureButtons[c] = b;
		}
	}

	private void ClearContainerChildren(Control container)
	{
		if (container == null) return;
		for (int i = container.GetChildCount() - 1; i >= 0; i--)
		{
			Node child = container.GetChild(i) as Node;
			if (child != null)
			{
				child.QueueFree();
			}
		}
	}

	// --- ホバー検出とハイライト ---
	private void UpdateHoverFromMouse()
	{
		if (_camera == null) return;
		Vector2 mpos = GetViewport().GetMousePosition();
		Vector3 rayOrigin = _camera.ProjectRayOrigin(mpos);
		Vector3 rayDir = _camera.ProjectRayNormal(mpos);
		Vector3 rayTarget = rayOrigin + rayDir * 250.0f;
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayTarget);
		if (_player != null) query.Exclude = new Array<Rid> { _player.GetRid() };
		query.CollideWithBodies = true;
		var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (result.Count > 0)
		{
			object collider = result["collider"];
			if (collider is RigidBody3D rb && _aliveCreatures.Contains(rb))
			{
				if (rb != _hoveredCreature) HighlightCreature(rb);
				return;
			}
		}
		ClearHighlight();
	}

	private MeshInstance3D GetCreatureMesh(RigidBody3D creature)
	{
		if (creature == null) return null;
		var mesh = creature.GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
		if (mesh != null) return mesh;
		for (int i = 0; i < creature.GetChildCount(); i++)
		{
			if (creature.GetChild(i) is MeshInstance3D m) return m;
		}
		return null;
	}

	private void HighlightCreature(RigidBody3D creature)
	{
		ClearHighlight();
		var mesh = GetCreatureMesh(creature);
		if (mesh == null) return;
		MeshInstance3D outline = new MeshInstance3D();
		outline.Mesh = mesh.Mesh;
		outline.Name = "HoverOutline";
		outline.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		outline.Scale = mesh.Scale * 1.14f;
		outline.Position = Vector3.Up * 0.01f;
		if (_highlightMaterial == null)
		{
			_highlightMaterial = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				AlbedoColor = new Color(0.1f, 0.95f, 1.0f),
				EmissionEnabled = true,
				Emission = new Color(0.1f, 0.95f, 1.0f),
				EmissionEnergyMultiplier = 2.8f,
				CullMode = BaseMaterial3D.CullModeEnum.Front
			};
		}
		outline.MaterialOverride = _highlightMaterial;
		creature.AddChild(outline);
		_hoveredMesh = outline;
		_hoveredCreature = creature;
	}

	private void ClearHighlight()
	{
		if (_hoveredMesh != null)
		{
			if (IsInstanceValid(_hoveredMesh)) _hoveredMesh.QueueFree();
			_hoveredMesh = null;
		}
		_hoveredCreature = null;
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
		bool addedAnyVertices = false;

		for (float segmentStart = phase; segmentStart < totalLength; segmentStart += cycleLength)
		{
			float segmentEnd = Mathf.Min(segmentStart + dashLength, totalLength);
			Vector3 startPoint = localSpawn.Lerp(localHit, segmentStart / totalLength);
			Vector3 endPoint = localSpawn.Lerp(localHit, segmentEnd / totalLength);
			_predictionMesh.SurfaceAddVertex(startPoint);
			_predictionMesh.SurfaceAddVertex(endPoint);
			addedAnyVertices = true;
		}

		if (addedAnyVertices)
		{
			_predictionMesh.SurfaceEnd();
		}
		else
		{
			_predictionMesh.ClearSurfaces();
			_predictionDashMultiMesh.InstanceCount = 0;
			return;
		}

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
		foodInstance.AddToGroup("food");
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
		_generationCandidates.Clear();
		_generationCandidates.AddRange(_creatureMeta.Values.Where(meta => meta.GenerationIndex == _generation));
		_generationCandidates.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));
		_generation++;
		GD.Print($"--- 世代交代: Generation {_generation} ---");
		await ToSignal(GetTree().CreateTimer(GenerationRespawnDelay), SceneTreeTimer.SignalName.Timeout);
		ClearDeadCreatures();
		SpawnGeneration();
		_isGenerationTransitioning = false;
	}

	private void SpawnGeneration()
	{
		int spawnCount = Mathf.Max(1, InitialSpawnCount);
		GD.Print($"Generation {_generation}: {spawnCount}体を孵化");

		for (int i = 0; i < spawnCount; i++)
		{
			SpawnCreature(CreateGenomeForNextCreature(), CreateTraitsForNextCreature());
		}
	}

	private void ForceCullCurrentGeneration()
	{
		if (_aliveCreatures.Count == 0)
		{
			return;
		}

		GD.Print("強制世代終了（手動殺害）");
		for (int i = _aliveCreatures.Count - 1; i >= 0; i--)
		{
			RigidBody3D creature = _aliveCreatures[i];
			if (!IsInstanceValid(creature))
			{
				continue;
			}

			HandleCreatureDeath(creature, true);
			_manualKillCount++;
		}

		_aliveCreatures.Clear();
		GD.Print($"手動殺害ペナルティ累計: {_manualKillCount}");
	}

	private CreatureGenome CreateGenomeForNextCreature()
	{
		if (_generationCandidates.Count == 0)
		{
			return CreatureGenome.Randomize();
		}

		CreatureMeta elite = _generationCandidates[0];
		if (_generationCandidates.Count == 1)
		{
			CreatureGenome single = elite.Genome.Clone();
			single.Mutate(0.10f, 0.25f);
			return single;
		}

		CreatureMeta parentA = _generationCandidates[0];
		CreatureMeta parentB = _generationCandidates[Mathf.Min(1, _generationCandidates.Count - 1)];
		CreatureGenome child = CreatureGenome.Crossover(parentA.Genome, parentB.Genome);
		child.Mutate(0.14f, 0.30f);
		return child;
	}

	private List<string> CreateTraitsForNextCreature()
	{
		string[] pool = new string[] { "目がいい", "鼻がきく", "速い", "力が強い" };
		List<string> traits = new();

		if (_generationCandidates.Count > 0)
		{
			foreach (CreatureMeta parent in _generationCandidates.Take(2))
			{
				foreach (string trait in parent.Traits)
				{
					if (!traits.Contains(trait) && GD.Randf() < 0.72f)
					{
						traits.Add(trait);
					}
				}
			}
		}

		int traitCount = (int)GD.RandRange(0, 2);
		for (int i = 0; i < traitCount; i++)
		{
			string pick = pool[(int)GD.RandRange(0, pool.Length)];
			if (!traits.Contains(pick)) traits.Add(pick);
		}

		return traits;
	}

	private void SpawnCreature(CreatureGenome genome, List<string> traits)
	{
		if (CreatureScene == null)
		{
			GD.PrintErr("エラー：インスペクターで CreatureScene が設定されていません！");
			return;
		}

		CreatureAgent creatureInstance = CreatureScene.Instantiate<CreatureAgent>();
		Vector3 randomOffset = new(
			(float)GD.RandRange(-SpawnRadius, SpawnRadius),
			0,
			(float)GD.RandRange(-SpawnRadius, SpawnRadius)
		);

		// 殺害ペナルティ: 手動殺害回数が増えるほど次世代がタフに
		float toughness = 1.0f + (_manualKillCount * 0.06f);
		creatureInstance.Mass *= toughness;
		creatureInstance.Scale = Vector3.One * Mathf.Clamp(toughness, 1.0f, 2.2f);

		var meta = new CreatureMeta();
		meta.GenerationIndex = _generation;
		meta.Traits = new List<string>(traits);
		meta.Genome = genome.Clone();
		meta.DisplayName = GenerateCreatureName(meta.Traits);
		_creatureMeta[creatureInstance] = meta;
		creatureInstance.Name = meta.DisplayName;
		creatureInstance.AddToGroup("creatures");

		AddChild(creatureInstance);
		creatureInstance.GlobalPosition = _spawnPosition + randomOffset;
		creatureInstance.Configure(meta.Genome, meta.Traits, _generation);
		meta.Agent = creatureInstance;
		creatureInstance.Died += OnCreatureDied;
		_aliveCreatures.Add(creatureInstance);
		_killedByPlayer[creatureInstance] = false;
		UpdateCreatureUI();
	}

	private void OnCreatureDied(CreatureAgent creature)
	{
		HandleCreatureDeath(creature, false);
		UpdateCreatureUI();
	}

	private void HandleCreatureDeath(RigidBody3D creature, bool killedByPlayer)
	{
		if (creature == null || !IsInstanceValid(creature))
		{
			return;
		}

		_aliveCreatures.Remove(creature);
		_killedByPlayer[creature] = killedByPlayer;
		if (_creatureMeta.TryGetValue(creature, out CreatureMeta meta))
		{
			meta.Alive = false;
			if (meta.Agent != null)
			{
				meta.Fitness = meta.Agent.Fitness;
				meta.FoodsEaten = meta.Agent.FoodsEaten;
			}
		}
	}

	private void ClearDeadCreatures()
	{
		foreach (RigidBody3D creature in _creatureMeta.Keys.ToList())
		{
			if (!IsInstanceValid(creature))
			{
				continue;
			}

			if (_creatureMeta.TryGetValue(creature, out CreatureMeta meta) && !meta.Alive)
			{
				creature.QueueFree();
			}
		}
	}

	private string GenerateCreatureName(List<string> traits)
	{
		string[] baseNames = new[] { "モコ", "ピコ", "ルル", "ノノ", "ポポ", "タロ", "ミミ", "キキ" };
		string[] neutralPrefixes = new[] { "ふわり", "すばしこい", "きらめく", "のんびり", "がっしり", "ささやく" };

		string prefix;
		if (traits.Contains("目がいい")) prefix = "千里眼の";
		else if (traits.Contains("鼻がきく")) prefix = "鼻先鋭い";
		else if (traits.Contains("速い")) prefix = "疾風の";
		else if (traits.Contains("力が強い")) prefix = "怪力の";
		else prefix = neutralPrefixes[(int)GD.RandRange(0, neutralPrefixes.Length)];

		string baseName = baseNames[(int)GD.RandRange(0, baseNames.Length)];
		return prefix + baseName;
	}
}
