using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server._NSV.Bluespace.Encounters;

/// <summary>
/// Hold encounter: survive in a hostile node until a fixed timer elapses. The tension is structural —
/// <see cref="NsvBluespaceEncounterSystem.CanReturn"/> only opens extraction once the encounter reaches
/// ExtractionOpen/Failed, so the crew cannot FTL out until the hold is satisfied, and the node's own
/// NSVHostile population keeps attacking the whole time. v1 does not spawn its own reinforcement waves;
/// the threat comes from the node (hostile generation + the strategy fleet spawner), so Hold must be
/// placed on a hostile node.
///
/// Extension point (v1 does not do this): spawn reinforcement waves on a CVar interval by reusing
/// NsvBluespaceShipGenerator.TryGenerate (load grid -> SetFaction(NSVHostile) -> add to OwnedGrids ->
/// fleets.RegisterShip, placed via FindFreeSpot), scaling wave size with the campaign ThreatElevation.
/// </summary>
public sealed class NsvHoldContractSystem : EntitySystem
{
    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NsvBluespaceEncounterArrivalEvent>(OnArrival);
    }

    private void OnArrival(NsvBluespaceEncounterArrivalEvent ev)
    {
        if (ev.Kind != NsvBluespaceEncounterKind.Hold)
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

        // Deliberately no relation neutralization: NSVHostile must keep attacking the crew for the hold
        // to mean anything.
        var duration = TimeSpan.FromSeconds(_cfg.GetCVar(NsvCCVars.EncounterHoldDuration));
        var objective = EnsureComp<NsvEncounterHoldObjectiveComponent>(controllerUid);
        objective.Controller = controllerUid;
        objective.EndTime = _timing.CurTime + duration;

        encounter.State = NsvBluespaceEncounterState.Active;
        _encounters.NotifySectorChanged(encounter.SectorMap);
        return true;
    }

    public override void Update(float frameTime)
    {
        CheckHolds();
    }

    /// <summary>
    /// Completes any active hold whose timer has elapsed and still has a participant present. Internal so
    /// the integration test can drive a check deterministically (set EndTime, call this) without pumping
    /// a real duration of ticks.
    /// </summary>
    internal void CheckHolds()
    {
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<NsvEncounterHoldObjectiveComponent, NsvBluespaceEncounterComponent>();
        while (query.MoveNext(out var controllerUid, out var objective, out var encounter))
        {
            if (encounter.State != NsvBluespaceEncounterState.Active ||
                now < objective.EndTime ||
                !IsAnyParticipantAlive(encounter))
            {
                continue;
            }

            _encounters.TryComplete(controllerUid);
        }
    }

    /// <summary>
    /// Whether any participant shuttle still exists. During Active the extraction gate keeps
    /// participants in the sector, so an existing participant is necessarily still holding here; this
    /// only stops a hold from "completing" for a sector whose crew was wiped out (that sector disposes
    /// and fails on its own instead).
    /// </summary>
    private bool IsAnyParticipantAlive(NsvBluespaceEncounterComponent encounter)
    {
        foreach (var participant in encounter.Participants)
        {
            if (Exists(participant))
                return true;
        }

        return false;
    }
}
