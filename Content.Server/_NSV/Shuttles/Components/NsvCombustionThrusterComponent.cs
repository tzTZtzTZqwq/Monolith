namespace Content.Server._NSV.Shuttles.Components;

[RegisterComponent]
public sealed partial class NsvCombustionThrusterComponent : Component
{
    [DataField]
    public string PlasmaNode = "plasma";

    [DataField]
    public string OxygenNode = "oxygen";

    [DataField]
    public float PlasmaMolesPerSecond = 1.5f;

    [DataField]
    public float OxygenPerPlasma = 1.4f;

    [DataField]
    public float MaximumProcessSeconds = 1f;

    [DataField]
    public float MinimumFuelSeconds = 1f;

    [ViewVariables]
    public NsvCombustionFuelStatus FuelStatus = NsvCombustionFuelStatus.Disconnected;
}

public enum NsvCombustionFuelStatus : byte
{
    Disconnected,
    NoPlasma,
    NoOxygen,
    NoFuel,
    Ready,
}
