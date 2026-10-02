using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.GameRule;
using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared._NSV.CCVar;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._NSV.Bluespace.Strategy;

/// <summary>
/// The strategy layer's fleet spawner: the (previously missing) caller of the registry's
/// data-state primitives. On a timer it tops up hostile, unmaterialized starmap nodes with
/// background enemy ships so the world the player hasn't visited keeps filling with threats.
/// Ships are spawned as pure data (grid loaded onto a paused scratch map, registered, serialized
/// to the holding map, scratch map discarded) and materialize only when the player wakes that
/// sector via <see cref="NsvFleetRegistrySystem.InstantiateNodeFleets"/>.
/// </summary>
public sealed partial class NsvStrategyFleetSpawnerSystem : EntitySystem
{
    private const string StrategicMapId = "NSVBluespaceStrategicMap";
    private const string HostileFaction = "NSVHostile";
    private const string FederalFaction = "NSVFederal";
    private static readonly ResPath DataShipGridPath = new("/SharedMaps/_NSV/Bluespace/gust_2.yml");

    [Dependency] private MapSystem _map = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private NsvFleetRegistrySystem _fleets = default!;
    [Dependency] private NsvCampaignRuleSystem _campaign = default!;
    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private IPrototypeManager _protos = default!;
    [Dependency] private IConfigurationManager _cfg = default!;

    private float _accumulator;

    public override void Initialize()
    {
        base.Initialize();
        // Data ships are born on the faction-less holding map, so the spawner can't stamp their
        // faction until they materialize onto a live sector map. The registry echoes each wake as
        // this event; we then apply the ship's intended faction to the now-live grid.
        SubscribeLocalEvent<NsvFleetShipInstantiatedEvent>(OnShipInstantiated);
    }

    /// <summary>
    /// Faction-on-wake: when a data ship materializes, stamp the faction the spawner recorded on
    /// it. Ships without a recorded faction (sector-parked player/encounter grids that
    /// <see cref="NsvFleetRegistrySystem.SerializeSectorFleets"/> registered) are left untouched,
    /// so waking a slept sector never turns the player's own grid hostile.
    /// </summary>
    private void OnShipInstantiated(ref NsvFleetShipInstantiatedEvent args)
    {
        if (!_fleets.TryGetShip(args.ShipId, out var ship) || ship.Faction is not { } faction)
            return;

        _factions.SetFaction(args.RootGrid, faction);
    }

    public override void Update(float frameTime)
    {
        _accumulator += frameTime;
        var interval = _cfg.GetCVar(NsvCCVars.StrategyFleetSpawnInterval);
        if (_accumulator < interval)
            return;

        _accumulator %= interval;
        ScanAndSpawn();
    }

    /// <summary>
    /// One spawn pass. Hostile-faction nodes top up a threat-scaled hostile fleet. Federal-faction
    /// nodes are the contested front line: they get a fixed Federal garrison plus that same
    /// threat-scaled hostile incursion, so the two mutually-hostile sides fight out abstract combat
    /// while the node stays dormant. A materialized node is skipped entirely — its ships are the
    /// live grids on its sector map, owned by the live layer. Each fleet is topped up independently
    /// so a garrison at strength doesn't suppress the incursion (and vice versa). Idempotent up to
    /// each target. Internal so the integration test can drive a pass without pumping
    /// <see cref="Update"/>.
    /// </summary>
    internal void ScanAndSpawn()
    {
        // Background world only ticks during a campaign round; otherwise there's no threat model
        // and no round-scoped registry to own the ships.
        if (!_campaign.HasActiveCampaign())
            return;

        if (!_protos.TryIndex<NsvBluespaceStarmapPrototype>(StrategicMapId, out var starmap))
            return;

        var materialized = GetMaterializedNodes();
        var threat = _campaign.GetThreatElevation();

        var baseline = _cfg.GetCVar(NsvCCVars.StrategyFleetBaselineSize);
        var threatPerShip = _cfg.GetCVar(NsvCCVars.StrategyFleetThreatPerShip);
        var maxPerNode = _cfg.GetCVar(NsvCCVars.StrategyFleetMaxPerNode);
        var garrisonSize = _cfg.GetCVar(NsvCCVars.StrategyFederalGarrisonSize);
        var threatBonus = threatPerShip > 0f ? (int) MathF.Round(threat / threatPerShip) : 0;
        var hostileTarget = Math.Clamp(baseline + threatBonus, 0, maxPerNode);

        foreach (var node in starmap.NodeDefinitions)
        {
            var key = new NsvFleetNodeKey(StrategicMapId, node.ID);
            // A materialized node's ships are the live grids on its sector map, not our data
            // records; leave it to the live layer and never double-spawn there.
            if (materialized.Contains(key))
                continue;

            switch (node.Faction.Id)
            {
                case HostileFaction:
                    TopUpFleet(key, HostileFaction, hostileTarget);
                    break;
                case FederalFaction:
                    TopUpFleet(key, FederalFaction, garrisonSize);
                    TopUpFleet(key, HostileFaction, hostileTarget);
                    break;
            }
        }
    }

    /// <summary>
    /// Tops the <paramref name="faction"/> fleet resident at <paramref name="key"/> up to
    /// <paramref name="target"/>, counting only that faction's Available ships so co-resident
    /// factions on a contested node are sized independently.
    /// </summary>
    private void TopUpFleet(NsvFleetNodeKey key, string faction, int target)
    {
        var current = CountResidentShips(key, faction);
        for (var i = current; i < target; i++)
            SpawnDataShip(key, faction);
    }

    /// <summary>
    /// Node keys of every existing sector instance (any lifecycle state, including Sleeping), so a
    /// node the player has ever materialized is excluded from background spawning.
    /// </summary>
    private HashSet<NsvFleetNodeKey> GetMaterializedNodes()
    {
        var nodes = new HashSet<NsvFleetNodeKey>();
        var query = AllEntityQuery<NsvBluespaceSectorInstanceComponent>();
        while (query.MoveNext(out _, out var sector))
            nodes.Add(new NsvFleetNodeKey(sector.StarmapId, sector.NodeId));

        return nodes;
    }

    /// <summary>
    /// Live count of <paramref name="faction"/>'s data-state ships resident at
    /// <paramref name="key"/>. Only Available records of that faction count toward its target; a
    /// stale/Missing record, or a co-resident other faction, must not suppress a respawn.
    /// </summary>
    private int CountResidentShips(NsvFleetNodeKey key, string faction)
    {
        var count = 0;
        foreach (var shipId in _fleets.GetResidentShips(key))
        {
            if (_fleets.TryGetShip(shipId, out var ship) &&
                ship.State == NsvFleetShipState.Available && ship.Faction == faction)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Spawns one <paramref name="faction"/> ship straight into the data state, reusing the
    /// sector-monitor spawn sequence: load the grid onto a fresh paused scratch map, register it as
    /// a ship resident at <paramref name="key"/>, serialize it onto the holding map, then discard
    /// the scratch map.
    /// </summary>
    private void SpawnDataShip(NsvFleetNodeKey key, string faction)
    {
        _map.CreateMap(out var scratch);
        _map.SetPaused(scratch, true);
        try
        {
            if (!_mapLoader.TryLoadGrid(scratch, DataShipGridPath, out var grid))
                return;

            var ship = _fleets.RegisterShip(grid.Value.Owner, DataShipGridPath, key);
            // Tag the intended faction now; it's applied on wake (OnShipInstantiated), once the
            // grid is on a faction-enabled sector map — the holding map has no faction.
            ship.Faction = faction;
            _fleets.TrySerializeShip(ship.Id, out _);
        }
        finally
        {
            if (_map.MapExists(scratch))
                _map.DeleteMap(scratch);
        }
    }
}
