namespace Content.Server._NSV.Cargo.Components;

[RegisterComponent]
public sealed partial class NsvCargoSellConsoleComponent : Component
{
    [DataField]
    public float MaxPalletDistance = 8f;
}
