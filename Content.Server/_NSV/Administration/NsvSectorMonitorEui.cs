using System.Linq;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Bluespace.Strategy;
using Content.Server._NSV.GameRule;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._NSV.Administration;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared._NSV.CCVar;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using static Content.Shared._NSV.Administration.NsvSectorMonitorEuiMsg;

namespace Content.Server._NSV.Administration;

public sealed partial class NsvSectorMonitorEui : BaseEui
{
    [Dependency] private IAdminManager _adminManager = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IGameTiming _timing = default!;

    public NsvSectorMonitorEui()
    {
        IoCManager.InjectDependencies(this);
    }

    // Result of the last admin action, echoed back in the panel header until the next action.
    private string? _statusMessage;

    public override void Opened()
    {
        base.Opened();
        _adminManager.OnPermsChanged += OnPermsChanged;
        Campaign.CampaignDisplayChanged += StateDirty;
        StateDirty();
    }

    private NsvCampaignRuleSystem Campaign => _entityManager.System<NsvCampaignRuleSystem>();

    private const string StarmapId = "NSVBluespaceStrategicMap";
    private static readonly ResPath DataShipGridPath = new("/SharedMaps/_NSV/Bluespace/gust_2.yml");

    public override EuiStateBase GetNewState()
    {
        var lifecycle = _entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var fleets = _entityManager.System<NsvFleetRegistrySystem>();
        var rows = new List<(int MapId, NsvSectorMonitorRow Row)>();
        var sectorStates = new Dictionary<(string Starmap, string Node), NsvBluespaceSectorState>();
        var totalSectorCount = 0;
        var activeSectorCount = 0;
        var query = _entityManager.AllEntityQueryEnumerator<NsvBluespaceSectorInstanceComponent>();
        while (query.MoveNext(out var uid, out var sector))
        {
            totalSectorCount++;
            if (IsActiveSectorState(sector.State))
                activeSectorCount++;

            if (!string.IsNullOrEmpty(sector.StarmapId) && !string.IsNullOrEmpty(sector.NodeId))
                sectorStates[(sector.StarmapId, sector.NodeId)] = sector.State;

            var nameLocId = string.Empty;
            var nameFallback = sector.TemplateId.ToString();
            if (_prototype.TryIndex<NsvBluespaceSectorTemplatePrototype>(sector.TemplateId, out var template))
                nameLocId = template.Name.ToString();

            var mustRunTaskBlockers = 0;
            int? sleepHoldSeconds = null;
            if (lifecycle.TryGetRegistryEntry(uid, out var entry))
            {
                mustRunTaskBlockers = entry.MustRunTaskBlockerCount;
                if (entry.SleepDeadline is { } deadline)
                {
                    sleepHoldSeconds = Math.Max(
                        0,
                        (int) Math.Ceiling((deadline - _timing.CurTime).TotalSeconds));
                }
            }

            rows.Add(((int) sector.MapId, new NsvSectorMonitorRow(
                nameLocId,
                nameFallback,
                $"{uid} / Map {sector.MapId}",
                sector.State.ToString(),
                sector.OwnedGrids.Count,
                sector.OwnedEntities.Count,
                sector.ForeignGrids.Count,
                sector.PendingArrivals.Count,
                mustRunTaskBlockers,
                sleepHoldSeconds)));
        }

        var nodes = new List<NsvSectorMonitorNode>();
        if (_prototype.TryIndex<NsvBluespaceStarmapPrototype>(StarmapId, out var starmap))
        {
            foreach (var node in starmap.NodeDefinitions)
            {
                var hasSector = sectorStates.TryGetValue((StarmapId, node.ID), out var sectorState);
                var residents = fleets.GetResidentShips(new NsvFleetNodeKey(StarmapId, node.ID)).ToArray();
                nodes.Add(new NsvSectorMonitorNode(
                    node.ID,
                    node.Name.ToString(),
                    node.Position.X,
                    node.Position.Y,
                    node.Connections.ToArray(),
                    hasSector,
                    hasSector ? sectorState.ToString() : null,
                    residents));
            }
        }

        var fleetStates = fleets.Ships
            .Select(ship => new NsvSectorMonitorFleet(
                ship.Id,
                ship.GridPath.ToString(),
                ship.State.ToString(),
                ship.DataNode?.NodeId,
                ship.Faction,
                fleets.GetCompleteness(ship.Id)))
            .OrderBy(fleet => fleet.ShipId)
            .ToArray();

        return new NsvSectorMonitorEuiState(
            rows.OrderBy(row => row.MapId)
                .Select(row => row.Row)
                .ToArray(),
            totalSectorCount,
            _configuration.GetCVar(NsvCCVars.BluespaceSectorTotalSoftCapacity),
            activeSectorCount,
            _configuration.GetCVar(NsvCCVars.BluespaceSectorActiveSoftCapacity),
            nodes.ToArray(),
            fleetStates,
            StarmapId,
            BuildCampaignState(),
            _statusMessage);
    }

    private NsvSectorMonitorCampaign? BuildCampaignState()
    {
        if (Campaign.GetAdminView() is not { } view)
            return null;

        return new NsvSectorMonitorCampaign(
            view.Summary,
            view.Outcome.ToString(),
            view.BriefingDelivered,
            view.ReminderStage,
            ToSeconds(view.NextReminderIn),
            ToSeconds(view.ExtensionRemaining),
            (int) view.ActiveTime);
    }

    private static int? ToSeconds(float? seconds)
    {
        return seconds is { } value ? (int) MathF.Ceiling(value) : null;
    }

    private static bool IsActiveSectorState(NsvBluespaceSectorState state)
    {
        return state is
            NsvBluespaceSectorState.Applying or
            NsvBluespaceSectorState.Ready or
            NsvBluespaceSectorState.PreparingSleep or
            NsvBluespaceSectorState.Waking;
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!_adminManager.HasAdminFlag(Player, AdminFlags.Admin))
        {
            Close();
            return;
        }

        switch (msg)
        {
            case RefreshRequest:
                StateDirty();
                break;
            case StartOutcomeVoteRequest:
                Report(Campaign.ForceOutcomeVote() ? "nsv-sector-monitor-status-vote" : "nsv-sector-monitor-status-vote-failed");
                break;
            case AdjustScoreRequest score:
                Report(
                    score.Absolute ? Campaign.AdminSetScore(score.Amount) : Campaign.AdminAdjustScore(score.Amount),
                    "nsv-sector-monitor-status-score");
                break;
            case AdjustThreatRequest threat:
                var newThreat = threat.Absolute ? threat.Amount : Campaign.GetThreatElevation() + threat.Amount;
                Report(Campaign.AdminSetThreat(newThreat), "nsv-sector-monitor-status-threat");
                break;
            case AnnounceBriefingRequest:
                Report(Campaign.AdminAnnounceBriefing(), "nsv-sector-monitor-status-briefing");
                break;
            case SendReminderRequest:
                var stage = Campaign.AdminSendNextReminder();
                _statusMessage = stage > 0
                    ? Loc.GetString("nsv-sector-monitor-status-reminder", ("stage", stage))
                    : Loc.GetString("nsv-sector-monitor-status-no-campaign");
                StateDirty();
                break;
            case DispatchBlockadeRequest:
                var arrived = _entityManager.System<NsvCampaignBlockadeSystem>().DispatchBlockade();
                Report(arrived > 0
                    ? Loc.GetString("nsv-sector-monitor-status-blockade", ("count", arrived))
                    : Loc.GetString("nsv-sector-monitor-status-blockade-failed"));
                break;
            case SpawnDataFleetRequest spawn:
                Report(SpawnDataFleet(spawn.StarmapId, spawn.NodeId, spawn.Faction, out var spawnFailure)
                    ? Loc.GetString("nsv-sector-monitor-status-spawned", ("node", spawn.NodeId))
                    : Loc.GetString("nsv-sector-monitor-status-failed", ("reason", spawnFailure ?? string.Empty)));
                break;
            case MoveFleetRequest move:
                Report(_entityManager.System<NsvFleetRegistrySystem>()
                        .TryMoveShipToNode(move.ShipId, new NsvFleetNodeKey(move.StarmapId, move.NodeId), out var moveFailure)
                    ? Loc.GetString("nsv-sector-monitor-status-moved", ("ship", move.ShipId), ("node", move.NodeId))
                    : Loc.GetString("nsv-sector-monitor-status-failed", ("reason", moveFailure ?? string.Empty)));
                break;
        }
    }

    private bool SpawnDataFleet(string starmapId, string nodeId, string faction, out string? failure)
    {
        var fleets = _entityManager.System<NsvFleetRegistrySystem>();
        var map = _entityManager.System<MapSystem>();
        var mapLoader = _entityManager.System<MapLoaderSystem>();

        map.CreateMap(out var scratchMap);
        map.SetPaused(scratchMap, true);
        try
        {
            if (!mapLoader.TryLoadGrid(scratchMap, DataShipGridPath, out var grid))
            {
                failure = $"Could not load '{DataShipGridPath}'.";
                return false;
            }

            var ship = fleets.RegisterShip(grid.Value.Owner, DataShipGridPath, new NsvFleetNodeKey(starmapId, nodeId));
            // Empty means "no faction" — leave the grid's faction untouched on wake, as the
            // registry does for sector-parked player/encounter grids.
            ship.Faction = string.IsNullOrEmpty(faction) ? null : faction;
            if (fleets.TrySerializeShip(ship.Id, out failure))
                return true;

            // The grid dies with the scratch map; don't leave a Live record pointing at it.
            fleets.DiscardShip(ship.Id);
            return false;
        }
        finally
        {
            if (map.MapExists(scratchMap))
                map.DeleteMap(scratchMap);
        }
    }

    /// <summary>
    /// Records <paramref name="message"/> (a loc id or an already-localized string) as the action
    /// result and pushes a fresh state.
    /// </summary>
    private void Report(string message)
    {
        _statusMessage = Loc.TryGetString(message, out var localized) ? localized : message;
        StateDirty();
    }

    private void Report(bool succeeded, string successLocId)
    {
        Report(succeeded ? successLocId : "nsv-sector-monitor-status-no-campaign");
    }

    public override void Closed()
    {
        base.Closed();
        _adminManager.OnPermsChanged -= OnPermsChanged;
        Campaign.CampaignDisplayChanged -= StateDirty;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player == Player && !_adminManager.HasAdminFlag(Player, AdminFlags.Admin))
            Close();
    }
}
