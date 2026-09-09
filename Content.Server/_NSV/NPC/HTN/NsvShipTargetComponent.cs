namespace Content.Server._NSV.NPC.HTN;

[RegisterComponent]
public sealed partial class NsvShipTargetComponent : Component
{
    [DataField]
    public bool NeedPower;

    [DataField]
    public NsvShipTargetGridMode NeedGrid = NsvShipTargetGridMode.OnGrid;
}

public enum NsvShipTargetGridMode
{
    OnGrid,
    Either,
    NoGrid
}
