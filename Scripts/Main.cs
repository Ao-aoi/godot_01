using Godot;
using System;

public partial class Main : Node3D
{
	// Unityの [SerializeField] private GameObject creaturePrefab; に相当します。
	// インスペクターから Creature.tscn を紐付けられるようになります。
	[Export] public PackedScene CreatureScene; 

	// 生き物が生まれる山の頂上（卵の位置）の座標
	private Vector3 _spawnPosition = new Vector3(0, 7, 0); 

	public override void _Ready()
	{
		GD.Print("Artificial_Selection 起動成功！");
		
		// 最初の一体をスポンしてみる
		SpawnCreature();
	}

	// 画面のどこかをマウスクリックした時に実行されるイベント
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouseEvent)
		{
			// 左クリックが「押された瞬間」だけ検知
			if (mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
			{
				GD.Print("クリックされたので生き物を追加します！");
				SpawnCreature();
			}
		}
	}

	// 生き物を生成する関数
	private void SpawnCreature()
	{
		if (CreatureScene == null)
		{
			GD.PrintErr("エラー：インスペクターで CreatureScene が設定されていません！");
			return;
		}

		// Godotでのプレハブ生成（インスタンス化）
		// Instantiate() ではなく、PackedSceneの Instantiate<T>() を使います。
		RigidBody3D creatureInstance = CreatureScene.Instantiate<RigidBody3D>();
		
		// 生まれた生き物の位置を、山の頂上に設定
		creatureInstance.GlobalPosition = _spawnPosition;
		
		// メインシーンの子供（下位ノード）として画面（ツリー）に追加
		AddChild(creatureInstance);
	}
}
