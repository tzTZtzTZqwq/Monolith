using Robust.Shared.Prototypes;

namespace Content.Shared._NSV.Bluespace.Sectors;

[RegisterComponent]
public sealed partial class NsvBluespaceFactionComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NsvBluespaceFactionPrototype> Faction = string.Empty;
}

[RegisterComponent]
public sealed partial class NsvBluespaceFactionMapComponent : Component
{
}
