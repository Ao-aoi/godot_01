using Godot;

public partial class Trait : Resource
{
    [Export] public string TraitName { get; set; } = "";
    [Export] public string Description { get; set; } = "";
}
