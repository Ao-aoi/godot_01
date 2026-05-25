using Godot;

public enum ShopAction
{
    IncreaseSpawn,
    ReduceKillPenalty,
    UnlockKnife
}

public partial class ShopItem : Resource
{
    [Export] public string ItemName { get; set; } = "";
    [Export] public string Description { get; set; } = "";
    [Export] public int Cost { get; set; } = 10;
    [Export] public ShopAction Action { get; set; } = ShopAction.IncreaseSpawn;
    [Export] public int Value { get; set; } = 1;
}
