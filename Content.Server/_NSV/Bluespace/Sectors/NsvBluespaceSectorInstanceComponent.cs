using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors;

public enum NsvBluespaceSectorState
{
    Applying,
    Ready,
    PreparingSleep,
    Sleeping,
    Waking,
    Draining,
    Failed
}

public readonly record struct NsvBluespaceFactionSnapshot(
    bool HasFaction,
    ProtoId<NsvBluespaceFactionPrototype> Faction);

[RegisterComponent]
public sealed partial class NsvBluespaceSectorInstanceComponent : Component
{
    [ViewVariables]
    public ProtoId<NsvBluespaceSectorTemplatePrototype> TemplateId = string.Empty;

    [ViewVariables]
    public ProtoId<NsvBluespaceStarmapPrototype> StarmapId = string.Empty;

    [ViewVariables]
    public string NodeId = string.Empty;

    [ViewVariables]
    public string EncounterDefinitionId = string.Empty;

    [ViewVariables]
    public int Seed;

    [ViewVariables]
    public MapId MapId;

    [ViewVariables]
    public NsvBluespaceSectorState State = NsvBluespaceSectorState.Applying;

    [ViewVariables]
    public uint TransitionEpoch;

    [ViewVariables]
    public readonly HashSet<EntityUid> OwnedGrids = new();

    [ViewVariables]
    public readonly HashSet<EntityUid> OwnedEntities = new();

    [ViewVariables]
    public readonly HashSet<EntityUid> ForeignGrids = new();

    [ViewVariables]
    public readonly HashSet<EntityUid> PendingArrivals = new();

    [ViewVariables]
    public readonly Dictionary<EntityUid, EntityCoordinates> ReturnDestinations = new();

    [ViewVariables]
    public readonly Dictionary<ProtoId<NsvBluespaceFactionPrototype>, Dictionary<ProtoId<NsvBluespaceFactionPrototype>, NsvBluespaceFactionRelation>> RelationOverrides = new();

    [ViewVariables]
    public readonly Dictionary<EntityUid, NsvBluespaceFactionSnapshot> ForeignGridFactionSnapshots = new();

    [ViewVariables]
    public EntityUid EncounterController = EntityUid.Invalid;
}
