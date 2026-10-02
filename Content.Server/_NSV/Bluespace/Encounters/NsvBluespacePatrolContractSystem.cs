using System.Linq;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Encounters;

public sealed partial class NsvBluespacePatrolContractSystem : EntitySystem
{
    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NsvBluespaceEncounterArrivalEvent>(OnArrival);
        SubscribeLocalEvent<NsvEncounterPatrolCoreObjectiveComponent, EntityTerminatingEvent>(OnPatrolCoreTerminating);
    }

    private void OnArrival(NsvBluespaceEncounterArrivalEvent ev)
    {
        if (ev.Kind != NsvBluespaceEncounterKind.Destroy)
            return;

        if (!_encounters.TryGetOrCreate(ev.SectorMap, ev.DefinitionId, ev.Shuttle, out var controllerUid, out var created, out _))
            return;

        if (created && !TryActivate(controllerUid))
            _encounters.Fail(controllerUid);
    }

    private bool TryActivate(EntityUid controllerUid)
    {
        if (!TryComp<NsvBluespaceEncounterComponent>(controllerUid, out var encounter) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(encounter.SectorMap, out var sector) ||
            sector.State != NsvBluespaceSectorState.Ready)
        {
            return false;
        }

        var definition = _prototypes.Index(encounter.DefinitionId);
        var targetGrid = sector.OwnedGrids
            .Where(grid => _factions.TryGetFaction(grid, out var faction) && faction == definition.TargetFaction)
            .OrderBy(grid => grid.Id)
            .FirstOrDefault();

        if (targetGrid == EntityUid.Invalid || !TryFindPatrolCore(encounter.SectorMap, targetGrid, out var targetCore) ||
            HasComp<NsvBluespaceEncounterMemberComponent>(targetCore) ||
            HasComp<NsvEncounterPatrolCoreObjectiveComponent>(targetCore))
        {
            return false;
        }

        if (!_encounters.TryNeutralizeFederalTowards(controllerUid, definition.TargetFaction))
            return false;

        _encounters.MarkObjectiveTarget(controllerUid, targetCore, targetGrid);
        var objective = EnsureComp<NsvEncounterPatrolCoreObjectiveComponent>(targetCore);
        objective.Controller = controllerUid;
        objective.BlipGrid = targetGrid;

        encounter.ObjectiveTarget = targetCore;
        encounter.State = NsvBluespaceEncounterState.Active;
        _encounters.NotifySectorChanged(encounter.SectorMap);
        return true;
    }

    private void OnPatrolCoreTerminating(
        EntityUid uid,
        NsvEncounterPatrolCoreObjectiveComponent objective,
        ref EntityTerminatingEvent args)
    {
        if (!TryComp<NsvBluespaceEncounterComponent>(objective.Controller, out var encounter) ||
            encounter.State != NsvBluespaceEncounterState.Active ||
            encounter.ObjectiveTarget != uid ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(encounter.SectorMap, out var sector) ||
            sector.State is not (NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.PreparingSleep))
        {
            return;
        }

        if (_encounters.TryCompleteObjective(objective.Controller, uid))
            _encounters.ClearObjectiveBlip(objective.BlipGrid);
    }

    private bool TryFindPatrolCore(EntityUid sectorMap, EntityUid gridUid, out EntityUid coreUid)
    {
        coreUid = EntityUid.Invalid;
        var query = EntityQueryEnumerator<NsvBluespaceShipAiCoreComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.MapUid != sectorMap || transform.GridUid != gridUid)
                continue;

            if (coreUid != EntityUid.Invalid)
                return false;

            coreUid = uid;
        }

        return coreUid != EntityUid.Invalid;
    }
}
