using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Encounters;

/// <summary>
/// Broadcast when a shuttle completes an FTL arrival into a bluespace sector that resolves to an
/// encounter definition. <see cref="NsvBluespaceEncounterSystem.DispatchArrival"/> centralizes the
/// definition resolution (node encounter id + legacy template fallback) and raises this once; each
/// per-kind behaviour system subscribes and acts only when <see cref="Kind"/> matches its own kind.
/// Adding a new kind therefore never touches the dispatcher or the travel layer.
/// </summary>
public sealed class NsvBluespaceEncounterArrivalEvent : EntityEventArgs
{
    public readonly EntityUid SectorMap;
    public readonly EntityUid Shuttle;
    public readonly ProtoId<NsvBluespaceEncounterPrototype> DefinitionId;
    public readonly NsvBluespaceEncounterKind Kind;

    public NsvBluespaceEncounterArrivalEvent(
        EntityUid sectorMap,
        EntityUid shuttle,
        ProtoId<NsvBluespaceEncounterPrototype> definitionId,
        NsvBluespaceEncounterKind kind)
    {
        SectorMap = sectorMap;
        Shuttle = shuttle;
        DefinitionId = definitionId;
        Kind = kind;
    }
}

/// <summary>
/// Raised directed at an Active encounter's controller to collect its kind-specific progress readout
/// for the navigation console. The kind system that owns the controller's objective component fills
/// <see cref="Progress"/>; kinds with nothing extra to show (Destroy) leave it null.
/// </summary>
[ByRefEvent]
public record struct NsvBluespaceEncounterProgressEvent(NsvBluespaceEncounterProgressState? Progress = null);
