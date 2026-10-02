using System.Linq;
using Content.Server._Mono.Radar;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._Mono.Radar;
using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Encounters;

/// <summary>
/// ClearSystem encounter: destroy every hostile AI core present in the node when the encounter
/// activates. Mirrors <see cref="NsvBluespacePatrolContractSystem"/> but tracks a set of targets
/// instead of one. Snapshot semantics: the target set is fixed at activation — ships that spawn into
/// the node afterwards are not added (v1). Completion/reward/notification are owned by
/// <see cref="NsvBluespaceEncounterSystem"/>; this system only decides when the win condition is met.
/// </summary>
public sealed class NsvClearSystemContractSystem : EntitySystem
{
    private static readonly ProtoId<NsvBluespaceFactionPrototype> FederalFaction = "NSVFederal";
    private static readonly ProtoId<NsvBluespaceFactionPrototype> HostileFaction = "NSVHostile";

    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NsvBluespaceEncounterArrivalEvent>(OnArrival);
        SubscribeLocalEvent<NsvEncounterClearTargetComponent, EntityTerminatingEvent>(OnClearTargetTerminating);
    }

    private void OnArrival(NsvBluespaceEncounterArrivalEvent ev)
    {
        if (ev.Kind != NsvBluespaceEncounterKind.ClearSystem)
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
        var hostileGrids = sector.OwnedGrids
            .Where(grid => _factions.TryGetFaction(grid, out var faction) && faction == definition.TargetFaction)
            .ToHashSet();

        if (hostileGrids.Count == 0)
            return false;

        // Collect candidate cores before tagging so a relation failure leaves nothing half-applied.
        var cores = new List<(EntityUid Core, EntityUid Grid)>();
        var query = EntityQueryEnumerator<NsvBluespaceShipAiCoreComponent, TransformComponent>();
        while (query.MoveNext(out var coreUid, out _, out var transform))
        {
            if (transform.MapUid != encounter.SectorMap ||
                transform.GridUid is not { } gridUid ||
                !hostileGrids.Contains(gridUid) ||
                HasComp<NsvBluespaceEncounterMemberComponent>(coreUid) ||
                HasComp<NsvEncounterClearTargetComponent>(coreUid))
            {
                continue;
            }

            cores.Add((coreUid, gridUid));
        }

        if (cores.Count == 0)
            return false;

        if (!_factions.SetSectorRelation(encounter.SectorMap, FederalFaction, HostileFaction, NsvBluespaceFactionRelation.Neutral))
            return false;

        _encounters.TrackRelationOverride(controllerUid, FederalFaction, HostileFaction);
        if (!_factions.SetSectorRelation(encounter.SectorMap, HostileFaction, FederalFaction, NsvBluespaceFactionRelation.Neutral))
            return false;

        _encounters.TrackRelationOverride(controllerUid, HostileFaction, FederalFaction);

        var objective = EnsureComp<NsvEncounterClearObjectiveComponent>(controllerUid);
        foreach (var (coreUid, gridUid) in cores)
        {
            var member = EnsureComp<NsvBluespaceEncounterMemberComponent>(coreUid);
            member.Controller = controllerUid;
            member.Role = NsvBluespaceEncounterMemberRole.ObjectiveTarget;

            var target = EnsureComp<NsvEncounterClearTargetComponent>(coreUid);
            target.Controller = controllerUid;
            target.BlipGrid = gridUid;

            var blip = EnsureComp<RadarBlipComponent>(gridUid);
            blip.Config = new BlipConfig
            {
                Color = Color.Red,
                Shape = RadarBlipShape.Star,
                Bounds = new Box2(-4.5f, -4.5f, 4.5f, 4.5f),
            };

            objective.RemainingTargets.Add(coreUid);
        }

        encounter.State = NsvBluespaceEncounterState.Active;
        _encounters.NotifySectorChanged(encounter.SectorMap);
        return true;
    }

    private void OnClearTargetTerminating(
        EntityUid uid,
        NsvEncounterClearTargetComponent target,
        ref EntityTerminatingEvent args)
    {
        if (!TryComp<NsvBluespaceEncounterComponent>(target.Controller, out var encounter) ||
            encounter.State != NsvBluespaceEncounterState.Active ||
            !TryComp<NsvEncounterClearObjectiveComponent>(target.Controller, out var objective) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(encounter.SectorMap, out var sector) ||
            sector.State is not (NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.PreparingSleep))
        {
            return;
        }

        if (!objective.RemainingTargets.Remove(uid))
            return;

        if (target.BlipGrid != EntityUid.Invalid)
            RemComp<RadarBlipComponent>(target.BlipGrid);

        if (objective.RemainingTargets.Count > 0)
        {
            _encounters.NotifySectorChanged(encounter.SectorMap);
            return;
        }

        _encounters.TryComplete(target.Controller);
    }
}
