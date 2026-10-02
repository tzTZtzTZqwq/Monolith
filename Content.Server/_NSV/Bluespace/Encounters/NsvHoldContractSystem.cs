using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
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
public sealed partial class NsvHoldContractSystem : EntitySystem
{
    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NsvBluespaceEncounterArrivalEvent>(OnArrival);
        SubscribeLocalEvent<NsvEncounterHoldObjectiveComponent, NsvBluespaceEncounterProgressEvent>(OnProgress);
    }

    private void OnProgress(EntityUid uid, NsvEncounterHoldObjectiveComponent objective, ref NsvBluespaceEncounterProgressEvent args)
    {
        args.Progress = new NsvBluespaceEncounterProgressState(
            "nsv-bluespace-encounter-progress-hold",
            deadline: objective.EndTime);
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
    /// Completes any active hold whose timer has elapsed while the sector is still being held. Internal
    /// so the integration test can drive a check deterministically (set EndTime, call this) without
    /// pumping a real duration of ticks.
    /// </summary>
    internal void CheckHolds()
    {
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<NsvEncounterHoldObjectiveComponent, NsvBluespaceEncounterComponent>();
        while (query.MoveNext(out var controllerUid, out var objective, out var encounter))
        {
            if (encounter.State != NsvBluespaceEncounterState.Active ||
                now < objective.EndTime ||
                !IsBeingHeld(encounter))
            {
                continue;
            }

            _encounters.TryComplete(controllerUid);
        }
    }

    /// <summary>
    /// Whether the crew is actually holding the sector: it is awake (Ready / PreparingSleep — a sector
    /// only sleeps once no living player is in it) and a participant shuttle still exists. A crew that
    /// died or disconnected lets the sector sleep, and the timer running out then must not pay out.
    /// The hold stays Active, so a crew that comes back (waking the sector) finishes it on return.
    /// </summary>
    private bool IsBeingHeld(NsvBluespaceEncounterComponent encounter)
    {
        if (!TryComp<NsvBluespaceSectorInstanceComponent>(encounter.SectorMap, out var sector) ||
            sector.State is not (NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.PreparingSleep))
        {
            return false;
        }

        foreach (var participant in encounter.Participants)
        {
            if (Exists(participant))
                return true;
        }

        return false;
    }
}
