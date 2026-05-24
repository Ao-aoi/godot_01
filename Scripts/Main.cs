using Godot;
using System.Collections.Generic;

public partial class Main : Node3D
{
	[Export] public PackedScene CreatureScene;
	[Export] public int InitialSpawnCount = 1;
	[Export] public float SpawnRadius = 1.2f;
	[Export] public float GenerationRespawnDelay = 1.0f;
	[Export] public float AbyssY = -10.0f;

	private readonly Vector3 _spawnPosition = new(0, 7, 0);
	private readonly List<RigidBody3D> _aliveCreatures = new();
	private readonly Dictionary<RigidBody3D, bool> _killedByPlayer = new();

	private int _generation = 1;
	private int _manualKillCount = 0;
	private bool _isGenerationTransitioning;

	public override void _Ready()
	{
		GD.Print("Artificial_Selection 起動成功！");
		SpawnGeneration();
	}

	public override void _Process(double delta)
	{
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
