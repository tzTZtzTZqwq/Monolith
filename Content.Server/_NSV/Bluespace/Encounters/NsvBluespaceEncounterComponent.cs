using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Encounters;

public enum NsvBluespaceEncounterState
{
    Pending,
    Active,
    ObjectiveComplete,
    ExtractionOpen,
    Failed,
    Disposed
}

public enum NsvBluespaceEncounterMemberRole
{
    Participant,
    ObjectiveTarget,
    Spawn,
    Loot
}

public readonly record struct NsvBluespaceEncounterRelationOverride(
    ProtoId<NsvBluespaceFactionPrototype> Source,
    ProtoId<NsvBluespaceFactionPrototype> Target);

[RegisterComponent]
public sealed partial class NsvBluespaceEncounterComponent : Component
{
    [ViewVariables]
    public EntityUid SectorMap;

    [ViewVariables]
    public ProtoId<NsvBluespaceEncounterPrototype> DefinitionId = string.Empty;

    [ViewVariables]
    public NsvBluespaceEncounterState State = NsvBluespaceEncounterState.Pending;

    [ViewVariables]
    public EntityUid ObjectiveTarget = EntityUid.Invalid;

    [ViewVariables]
    public readonly HashSet<EntityUid> Participants = new();

    [ViewVariables]
    public readonly HashSet<EntityUid> PendingReturns = new();

    [ViewVariables]
    public readonly HashSet<EntityUid> ReturnedParticipants = new();

    public readonly HashSet<NsvBluespaceEncounterRelationOverride> RelationOverrides = new();
}

[RegisterComponent]
public sealed partial class NsvBluespaceEncounterMemberComponent : Component
{
    [ViewVariables]
    public EntityUid Controller;

    [ViewVariables]
    public NsvBluespaceEncounterMemberRole Role;
}

[RegisterComponent]
public sealed partial class NsvEncounterPatrolCoreObjectiveComponent : Component
{
    [ViewVariables]
    public EntityUid Controller;

    [ViewVariables]
    public EntityUid BlipGrid = EntityUid.Invalid;
}
