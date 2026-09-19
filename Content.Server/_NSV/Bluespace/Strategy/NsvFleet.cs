using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Strategy;

public sealed class NsvFleet
{
    public string Id = string.Empty;
    public ProtoId<NsvBluespaceFactionPrototype> Faction = string.Empty;
    public List<NsvFleetShip> Ships = new();
}
