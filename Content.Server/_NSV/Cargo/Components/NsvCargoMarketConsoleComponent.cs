namespace Content.Server._NSV.Cargo.Components;

[RegisterComponent]
public sealed partial class NsvCargoMarketConsoleComponent : Component
{
    [DataField]
    public float MaxDeliveryMachineDistance = 8f;
}
