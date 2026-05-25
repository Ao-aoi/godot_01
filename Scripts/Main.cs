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

	// フェーズ2用: 個体に関するメタデータ管理
	private class CreatureMeta
	{
		public List<string> Traits = new();
		public bool Alive = true;
		public string Name = "";
	}
	private readonly System.Collections.Generic.Dictionary<RigidBody3D, CreatureMeta> _creatureMeta = new();

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

	// フェーズ3: ショップ / メタゲーム
	private int _currency = 0;
	private bool _shopVisible = false;
	private Control _shopPanel;
	private Label _currencyLabel;
	private System.Collections.Generic.List<ShopItem> _shopItems = new();
	private bool _knifeUnlocked = false;
	private float _inheritanceBoost = 0.0f; // 今は未使用だがデータとして保持

	public override void _Ready()
	{
		GD.Print("Artificial_Selection 起動成功！");
		_player = GetNode<CharacterBody3D>("Player");
		_camera = GetNode<Camera3D>("Player/Camera3D");
		_foodSpawnDistance = Mathf.Clamp(DefaultFoodSpawnDistance, MinFoodSpawnDistance, MaxFoodSpawnDistance);
		SetupPredictionLine();
		SetupCreatureUI();
		SetupShopUI();
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
				if (_creatureMeta.TryGetValue(creature, out CreatureMeta meta) && meta.Alive)
				{
					meta.Alive = false;
					_killedByPlayer[creature] = false;
					UpdateCreatureUI();
				}
				continue;
			}

			if (creature.GlobalPosition.Y < AbyssY)
			{
				HandleCreatureRemoval(creature, false);
			}
		}

		if (!_isGenerationTransitioning && _aliveCreatures.Count == 0)
		{
			StartNextGeneration();
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// ショップ表示トグル: 'S' キーで開閉
		if (@event is InputEventKey kev && kev.Pressed && kev.Keycode == Key.E)
		{
			ToggleShopVisibility();
			return;
		}
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
			// 左クリックされた時、もしショップが開いていたら閉じる
			if (mouseButton.ButtonIndex == MouseButton.Left && _shopVisible)
			{
				// UI（ボタンやパネル）がクリックイベントをすでにハンドル（吸収）していなければ、ここに来る
				ToggleShopVisibility();
				// 他のクリック処理（エサを投げるなど）が暴発しないようにイベントを消費させる
				GetViewport().SetInputAsHandled();
				return;
			}
			
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
					// ショップでナイフをアンロックしていれば、Shift+右クリックで即殺
					if (_knifeUnlocked && Input.IsKeyPressed(Key.Shift))
					{
						_manualKillCount++;
						HandleCreatureRemoval(rb, true);
					}
					else
					{
						SelectCreature(rb);
					}
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
			// CameraController の StartFollow メソッドを呼ぶ
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
				bool isAlive = meta.Alive && IsInstanceValid(c) && _aliveCreatures.Contains(c);
				string label = (isAlive ? "生存: " : "死亡: ") + c.Name;
				if (meta.Traits.Count > 0)
				{
					label += " [" + string.Join(",", meta.Traits) + "]";
				}
				Button b = new Button();
				b.Text = label;
				b.Disabled = !isAlive;
				// クロージャで捕まえる
				RigidBody3D captured = c;
				b.Pressed += () => { if (captured != null) SelectCreature(captured); };
				if (isAlive) _aliveList.AddChild(b); else _deadList.AddChild(b);
				_creatureButtons[c] = b;
			}
		}

		private void HandleCreatureRemoval(RigidBody3D creature, bool killedByPlayer)
		{
			if (creature == null)
			{
				return;
			}

			if (_creatureMeta.TryGetValue(creature, out CreatureMeta meta) && !meta.Alive)
			{
				return;
			}

			if (_creatureMeta.TryGetValue(creature, out CreatureMeta removalMeta))
			{
				removalMeta.Alive = false;
			}

			_aliveCreatures.Remove(creature);
			_killedByPlayer[creature] = killedByPlayer;

			// プレイヤーによる撃破なら報酬を付与
			if (killedByPlayer)
			{
				_currency += 5; // 固定報酬
				UpdateShopUI();
			}
			UpdateCreatureUI();

			if (IsInstanceValid(creature))
			{
				creature.QueueFree();
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

	// ---- フェーズ3: ショップUIとロジック ----
	private void SetupShopUI()
	{
		if (_uiLayer == null)
		{
			_uiLayer = new CanvasLayer();
			AddChild(_uiLayer);
		}

		Panel shopPanel = new Panel();
		shopPanel.Visible = false;
		_uiLayer.AddChild(shopPanel);
		_shopPanel = shopPanel;

		VBoxContainer root = new VBoxContainer();
		shopPanel.AddChild(root);

		Label title = new Label();
		title.Text = "ショップ";
		root.AddChild(title);

		_currencyLabel = new Label();
		_currencyLabel.Text = "Points: 0";
		root.AddChild(_currencyLabel);

		// アイテムをこの場で定義する（将来的には外部リソース化可能）
		_shopItems = new System.Collections.Generic.List<ShopItem>()
		{
			new ShopItem { ItemName = "スポーン数+1", Description = "初期スポーン数を1増やす", Cost = 10, Action = ShopAction.IncreaseSpawn, Value = 1 },
			new ShopItem { ItemName = "ペナルティ軽減", Description = "手動殺害ペナルティを1減らす", Cost = 8, Action = ShopAction.ReduceKillPenalty, Value = 1 },
			new ShopItem { ItemName = "ナイフアンロック", Description = "Shift+右クリックで個体を即殺できる", Cost = 20, Action = ShopAction.UnlockKnife, Value = 1 }
		};

		foreach (var item in _shopItems)
		{
			Button b = new Button();
			b.Text = $"{item.ItemName} ({item.Cost})";
			b.Pressed += () => { TryPurchase(item); };
			root.AddChild(b);
		}

		Button close = new Button();
		close.Text = "閉じる";
		close.Pressed += () => { ToggleShopVisibility(); };
		root.AddChild(close);

		UpdateShopUI();
	}

	private void UpdateShopUI()
	{
		if (_currencyLabel != null)
		{
			_currencyLabel.Text = $"Points: {_currency}";
		}
		if (_shopPanel != null)
		{
			_shopPanel.Visible = _shopVisible;
		}
	}

	private void ToggleShopVisibility()
	{
		_shopVisible = !_shopVisible;
		UpdateShopUI();

		if (_shopVisible)
		{
			Input.MouseMode = Input.MouseModeEnum.Visible; // マウスを自由に動かせるようにする
		}
		else
		{
			Input.MouseMode = Input.MouseModeEnum.Captured; // 再び画面中央に拘束する
		}
	}

	private void TryPurchase(ShopItem item)
	{
		if (item == null) return;
		if (_currency < item.Cost) return;
		_currency -= item.Cost;
		ApplyShopItem(item);
		UpdateShopUI();
	}

	private void ApplyShopItem(ShopItem item)
	{
		switch (item.Action)
		{
			case ShopAction.IncreaseSpawn:
				InitialSpawnCount += item.Value;
				break;
			case ShopAction.ReduceKillPenalty:
				_manualKillCount = Mathf.Max(0, _manualKillCount - item.Value);
				break;
			case ShopAction.UnlockKnife:
				_knifeUnlocked = true;
				break;
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

		// --- 【修正】ツリーに追加する前に、まずメタデータと名前を確定させる ---
		var meta = new CreatureMeta();
		string[] pool = new string[] { "目がいい", "鼻がきく", "速い", "力が強い" };
		
		// ランダムに0~2個の特性を付与
		int traitCount = (int)GD.RandRange(0, 3);
		for (int t = 0; t < traitCount; t++)
		{
			string pick = pool[(int)GD.RandRange(0, pool.Length)];
			if (!meta.Traits.Contains(pick)) meta.Traits.Add(pick);
		}
		
		// 名前を生成してオブジェクトに割り当てる
		meta.Name = GenerateCreatureName(meta);
		creatureInstance.Name = meta.Name;
		_creatureMeta[creatureInstance] = meta;
		// -------------------------------------------------------------

		// --- 【修正】すべての設定が終わった段階でシーンツリーに追加する ---
		AddChild(creatureInstance);
		_aliveCreatures.Add(creatureInstance);
		_killedByPlayer[creatureInstance] = false;
		creatureInstance.TreeExiting += () => OnCreatureTreeExiting(creatureInstance);

		// 最後にUIを更新
		UpdateCreatureUI();
	}

	private void OnCreatureTreeExiting(RigidBody3D creature)
	{
		if (creature == null)
		{
			return;
		}

		if (_creatureMeta.TryGetValue(creature, out CreatureMeta meta) && !meta.Alive)
		{
			return;
		}

		HandleCreatureRemoval(creature, _killedByPlayer.TryGetValue(creature, out bool killedByPlayer) && killedByPlayer);
	}

	// 個体の特性に基づくランダムな名前を生成する
	private string GenerateCreatureName(CreatureMeta meta)
	{
		// 特性に応じた接頭語（日本語短文字）
		var prefixPool = new List<string>();
		if (meta.Traits.Contains("目がいい")) prefixPool.Add("視");
		if (meta.Traits.Contains("鼻がきく")) prefixPool.Add("嗅");
		if (meta.Traits.Contains("速い")) prefixPool.Add("迅");
		if (meta.Traits.Contains("力が強い")) prefixPool.Add("剛");
		if (prefixPool.Count == 0) prefixPool.Add("");

		string[] syllables = new string[] { "アル", "オル", "イナ", "カラ", "シア", "リク", "ノア", "サラ", "トウ", "メル" };
		string prefix = prefixPool[(int)GD.RandRange(0, prefixPool.Count)];
		string baseName = syllables[(int)GD.RandRange(0, syllables.Length)];
		string extra = "";
		// 稀に接尾語を追加
		if (GD.Randf() < 0.25f)
		{
			extra = syllables[(int)GD.RandRange(0, syllables.Length)];
		}
		string name = prefix + baseName + extra;

		// 既存の名前と被らないように調整
		int suffix = 1;
		var existing = new HashSet<string>();
		foreach (var kv in _creatureMeta)
		{
			if (!string.IsNullOrEmpty(kv.Value.Name)) existing.Add(kv.Value.Name);
		}
		string unique = name;
		while (existing.Contains(unique))
		{
			suffix++;
			unique = name + "-" + suffix.ToString();
		}
		return unique;
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

			_manualKillCount++;
			HandleCreatureRemoval(creature, true);
		}

		_aliveCreatures.Clear();
		UpdateCreatureUI();
		GD.Print($"手動殺害ペナルティ累計: {_manualKillCount}");
	}
}
