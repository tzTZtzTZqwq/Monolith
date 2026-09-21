using System.Linq;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Bluespace.Strategy;
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

    public override void Opened()
    {
        base.Opened();
        _adminManager.OnPermsChanged += OnPermsChanged;
        StateDirty();
    }

    private const string StarmapId = "NSVBluespaceStrategicMap";
    private static readonly ResPath KestrelGridPath = new("/SharedMaps/_Mono/Shuttles/kestrel.yml");

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
            StarmapId);
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
            case SpawnDataFleetRequest spawn:
                SpawnDataFleet(spawn.StarmapId, spawn.NodeId);
                StateDirty();
                break;
            case MoveFleetRequest move:
                _entityManager.System<NsvFleetRegistrySystem>()
                    .TryMoveShipToNode(move.ShipId, new NsvFleetNodeKey(move.StarmapId, move.NodeId), out _);
                StateDirty();
                break;
        }
    }

    private void SpawnDataFleet(string starmapId, string nodeId)
    {
        var fleets = _entityManager.System<NsvFleetRegistrySystem>();
        var map = _entityManager.System<MapSystem>();
        var mapLoader = _entityManager.System<MapLoaderSystem>();

        map.CreateMap(out var scratchMap);
        map.SetPaused(scratchMap, true);
        try
        {
            if (!mapLoader.TryLoadGrid(scratchMap, KestrelGridPath, out var grid))
                return;

            var ship = fleets.RegisterShip(grid.Value.Owner, KestrelGridPath, new NsvFleetNodeKey(starmapId, nodeId));
            fleets.TrySerializeShip(ship.Id, out _);
        }
        finally
        {
            if (map.MapExists(scratchMap))
                map.DeleteMap(scratchMap);
        }
    }

    public override void Closed()
    {
        base.Closed();
        _adminManager.OnPermsChanged -= OnPermsChanged;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player == Player && !_adminManager.HasAdminFlag(Player, AdminFlags.Admin))
            Close();
    }
}
