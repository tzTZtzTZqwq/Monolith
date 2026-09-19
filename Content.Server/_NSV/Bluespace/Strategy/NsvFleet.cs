using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Strategy;

public sealed class NsvFleet
{
    public string Id = string.Empty;
    public ProtoId<NsvBluespaceFactionPrototype> Faction = string.Empty;
    public ProtoId<NsvBluespaceStarmapPrototype> StarmapId = string.Empty;
    public string CurrentNodeId = string.Empty;
    public string? DestinationNodeId;
    public float Fatigue;
    public List<NsvFleetShip> Ships = new();
}
