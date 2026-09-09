using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._NSV.Bluespace.Encounters;

[Prototype("nsvBluespaceEncounter")]
public sealed partial class NsvBluespaceEncounterPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField(required: true)]
    public LocId Objective = string.Empty;

    [DataField(required: true)]
    public ProtoId<NsvBluespaceFactionPrototype> TargetFaction = string.Empty;
}
