using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._NSV.Bluespace.Encounters;

/// <summary>
/// Which per-kind behaviour system owns an encounter definition. The arrival dispatcher
/// (<c>NsvBluespaceEncounterSystem.DispatchArrival</c>) broadcasts this on the arrival event; each kind
/// system filters on its own value. Adding a new kind = add a value here + a system that subscribes to
/// the arrival event and activates on it — the dispatcher and travel layer stay untouched.
/// </summary>
public enum NsvBluespaceEncounterKind
{
    // Destroy a single designated AI core (the baseline Patrol Contract).
    Destroy,

    // Destroy every hostile AI core present in the node at activation time.
    ClearSystem,

    // Survive in a hostile node until a fixed timer elapses; extraction is gated until then.
    Hold
}

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

    /// <summary>
    /// Which kind system drives this encounter. Defaults to <see cref="NsvBluespaceEncounterKind.Destroy"/>
    /// so existing definitions that omit the field keep their Patrol-Contract behaviour.
    /// </summary>
    [DataField]
    public NsvBluespaceEncounterKind Kind = NsvBluespaceEncounterKind.Destroy;
}
