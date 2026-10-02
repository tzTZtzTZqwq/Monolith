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

/// <summary>
/// Marks one hostile AI core as a ClearSystem target. Terminating it removes it from the controller's
/// <see cref="NsvEncounterClearObjectiveComponent.RemainingTargets"/>; clearing the set completes.
/// </summary>
[RegisterComponent]
public sealed partial class NsvEncounterClearTargetComponent : Component
{
    [ViewVariables]
    public EntityUid Controller;

    [ViewVariables]
    public EntityUid BlipGrid = EntityUid.Invalid;
}

/// <summary>
/// Controller-side state for a ClearSystem encounter: the set of hostile cores still alive. The set is
/// a snapshot taken at activation — ships that spawn into the node afterwards are not added (v1).
/// </summary>
[RegisterComponent]
public sealed partial class NsvEncounterClearObjectiveComponent : Component
{
    [ViewVariables]
    public readonly HashSet<EntityUid> RemainingTargets = new();
}

/// <summary>
/// Controller-side state for a Hold encounter: the moment the hold is satisfied. Extraction stays
/// gated (<c>CanReturn</c> false) until the kind system completes the encounter at this time.
/// </summary>
[RegisterComponent]
public sealed partial class NsvEncounterHoldObjectiveComponent : Component
{
    [ViewVariables]
    public TimeSpan EndTime;
}
