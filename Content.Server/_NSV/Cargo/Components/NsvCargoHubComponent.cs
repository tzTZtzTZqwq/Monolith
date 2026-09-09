namespace Content.Server._NSV.Cargo.Components;

[RegisterComponent]
public sealed partial class NsvCargoHubComponent : Component
{
    [DataField]
    public int Balance;

    [DataField]
    public int LifetimePurchases;

    [DataField]
    public int LifetimeSales;
}
