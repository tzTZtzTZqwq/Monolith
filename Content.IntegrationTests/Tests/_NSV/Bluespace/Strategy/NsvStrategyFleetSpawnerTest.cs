using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Bluespace.Strategy;
using Content.Server._NSV.GameRule;
using Content.Server.GameTicking;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Strategy;

/// <summary>
/// The S7 strategy spawner tops up hostile, unmaterialized strategic-map nodes with background
/// data-state ships, count scaled by campaign threat, idempotent up to the per-node cap. It stays
/// dormant with no active campaign and never spawns onto an already-materialized node.
/// </summary>
[TestFixture]
public sealed class NsvStrategyFleetSpawnerTest
{
    private const string StrategicMapId = "NSVBluespaceStrategicMap";
    private const string HostileNode = "alpha-3";

    [Test]
    public async Task SpawnsThreatScaledFleetsAndIsIdempotentToCap()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();
        var spawner = sysMan.GetEntitySystem<NsvStrategyFleetSpawnerSystem>();
        var fleets = sysMan.GetEntitySystem<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            // baseline 1 + round(10 / 5) = 3, under the cap of 4.
            cfg.SetCVar(NsvCCVars.StrategyFleetBaselineSize, 1);
            cfg.SetCVar(NsvCCVars.StrategyFleetThreatPerShip, 5f);
            cfg.SetCVar(NsvCCVars.StrategyFleetMaxPerNode, 4);

            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            campaign.AdjustThreatElevation(10f);

            var node = new NsvFleetNodeKey(StrategicMapId, HostileNode);

            spawner.ScanAndSpawn();
            Assert.That(CountAvailableAt(fleets, node), Is.EqualTo(3), "threat-scaled target reached");

            // A second pass tops nothing up: the node is already at target.
            spawner.ScanAndSpawn();
            Assert.That(CountAvailableAt(fleets, node), Is.EqualTo(3), "idempotent at target");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// An interval of 0 disables the spawner. It used to divide the accumulator by zero (NaN), after
    /// which every tick ran a spawn pass until the server restarted.
    /// </summary>
    [Test]
    public async Task ZeroIntervalDisablesSpawning()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var fleets = sysMan.GetEntitySystem<NsvFleetRegistrySystem>();
        var before = 0;

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.StrategyFleetSpawnInterval, 0f);
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            before = fleets.Ships.Count;
        });

        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(fleets.Ships.Count, Is.EqualTo(before), "interval 0 must not spawn anything");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DoesNotSpawnAtMaterializedNode()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();
        var spawner = sysMan.GetEntitySystem<NsvStrategyFleetSpawnerSystem>();
        var fleets = sysMan.GetEntitySystem<NsvFleetRegistrySystem>();
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.StrategyFleetBaselineSize, 1);
            cfg.SetCVar(NsvCCVars.StrategyFleetMaxPerNode, 4);

            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);

            // Stand in a materialized sector instance on the hostile node: the live layer owns it,
            // so the background spawner must leave it alone.
            var sectorUid = entityManager.SpawnEntity(null, testMap.MapCoords);
            var sector = entityManager.AddComponent<NsvBluespaceSectorInstanceComponent>(sectorUid);
            sector.StarmapId = StrategicMapId;
            sector.NodeId = HostileNode;

            var node = new NsvFleetNodeKey(StrategicMapId, HostileNode);

            spawner.ScanAndSpawn();
            Assert.That(CountAvailableAt(fleets, node), Is.EqualTo(0), "materialized node is skipped");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DoesNotSpawnWithoutActiveCampaign()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var spawner = sysMan.GetEntitySystem<NsvStrategyFleetSpawnerSystem>();
        var fleets = sysMan.GetEntitySystem<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.StrategyFleetBaselineSize, 1);
            cfg.SetCVar(NsvCCVars.StrategyFleetMaxPerNode, 4);

            var node = new NsvFleetNodeKey(StrategicMapId, HostileNode);

            // No campaign rule started: the world stays dormant.
            spawner.ScanAndSpawn();
            Assert.That(CountAvailableAt(fleets, node), Is.EqualTo(0), "no spawning outside a campaign");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FactionOnWakeStampsTaggedShip()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            // Make the destination map faction-enabled so SetFaction can resolve its faction map.
            entityManager.EnsureComponent<NsvBluespaceFactionMapComponent>(testMap.MapUid);

            var node = new NsvFleetNodeKey(StrategicMapId, HostileNode);
            var grid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var ship = fleets.RegisterShip(grid, new ResPath("/Maps/_NSV/test.yml"), node);
            // The spawner tags its ships; mimic that here so the wake handler applies the faction.
            ship.Faction = HostileFaction;

            Assert.That(fleets.TrySerializeShip(ship.Id, out var serFail), Is.True, serFail);

            var destination = new EntityCoordinates(mapSystem.GetMap(testMap.MapId), System.Numerics.Vector2.Zero);
            fleets.InstantiateNodeFleets(node, destination, out var instFailures);
            Assert.That(instFailures, Is.Empty);

            Assert.That(entityManager.TryGetComponent<NsvBluespaceFactionComponent>(grid, out var faction), Is.True,
                "waking a spawner-tagged ship stamps its faction onto the live grid");
            Assert.That(faction!.Faction.Id, Is.EqualTo(HostileFaction));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FactionOnWakeLeavesUntaggedShipUntouched()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceFactionMapComponent>(testMap.MapUid);

            var node = new NsvFleetNodeKey(StrategicMapId, HostileNode);
            var grid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            // No Faction tag: this stands in for a sector-parked player/encounter grid that
            // SerializeSectorFleets registered. Waking it must never faction it.
            var ship = fleets.RegisterShip(grid, new ResPath("/Maps/_NSV/test.yml"), node);

            Assert.That(fleets.TrySerializeShip(ship.Id, out var serFail), Is.True, serFail);

            var destination = new EntityCoordinates(mapSystem.GetMap(testMap.MapId), System.Numerics.Vector2.Zero);
            fleets.InstantiateNodeFleets(node, destination, out var instFailures);
            Assert.That(instFailures, Is.Empty);

            Assert.That(entityManager.HasComponent<NsvBluespaceFactionComponent>(grid), Is.False,
                "an untagged ship keeps whatever faction it had (none), never the node's");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SeedsBothFactionsAtFederalNode()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();
        var spawner = sysMan.GetEntitySystem<NsvStrategyFleetSpawnerSystem>();
        var fleets = sysMan.GetEntitySystem<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            // Threat 0: hostile incursion sizes to baseline (1); garrison is its own fixed count.
            cfg.SetCVar(NsvCCVars.StrategyFleetBaselineSize, 1);
            cfg.SetCVar(NsvCCVars.StrategyFleetMaxPerNode, 4);
            cfg.SetCVar(NsvCCVars.StrategyFederalGarrisonSize, 2);

            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);

            var node = new NsvFleetNodeKey(StrategicMapId, FederalNode);

            spawner.ScanAndSpawn();
            Assert.Multiple(() =>
            {
                Assert.That(CountFactionAt(fleets, node, FederalFaction), Is.EqualTo(2), "federal garrison seeded");
                Assert.That(CountFactionAt(fleets, node, HostileFaction), Is.EqualTo(1), "hostile incursion seeded");
            });

            // Each fleet tops up independently and idempotently: a second pass adds nothing.
            spawner.ScanAndSpawn();
            Assert.Multiple(() =>
            {
                Assert.That(CountFactionAt(fleets, node, FederalFaction), Is.EqualTo(2), "garrison idempotent");
                Assert.That(CountFactionAt(fleets, node, HostileFaction), Is.EqualTo(1), "incursion idempotent");
            });
        });

        await pair.CleanReturnAsync();
    }

    private static int CountAvailableAt(NsvFleetRegistrySystem fleets, NsvFleetNodeKey key)
    {
        var count = 0;
        foreach (var shipId in fleets.GetResidentShips(key))
        {
            if (fleets.TryGetShip(shipId, out var ship) && ship.State == NsvFleetShipState.Available)
                count++;
        }

        return count;
    }

    private static int CountFactionAt(NsvFleetRegistrySystem fleets, NsvFleetNodeKey key, string faction)
    {
        var count = 0;
        foreach (var shipId in fleets.GetResidentShips(key))
        {
            if (fleets.TryGetShip(shipId, out var ship) &&
                ship.State == NsvFleetShipState.Available && ship.Faction == faction)
            {
                count++;
            }
        }

        return count;
    }

    private const string HostileFaction = "NSVHostile";
    private const string FederalFaction = "NSVFederal";
    private const string FederalNode = "alpha-1";
}
