using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors;

[RegisterComponent]
public sealed partial class NsvBluespaceJumpPointComponent : Component
{
    [DataField]
    public ProtoId<NsvBluespaceStarmapPrototype> StarmapId = string.Empty;

    [DataField]
    public ProtoId<NsvBluespaceSectorTemplatePrototype> TemplateId = string.Empty;
}
