using System.Linq;
using System.Numerics;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Bluespace.Strategy;
using Content.Server.Shuttles.Events;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Sectors;

/// <summary>
/// N7 FTL interdiction and the N6 blockade fleet that carries it.
/// </summary>
[TestFixture]
public sealed class NsvFtlInterdictionTest
{
    /// <summary>
    /// A live, hostile interdictor on the shuttle's map blocks both bluespace travel's check and the
    /// shuttle console FTL attempt; destroying its core lifts it. A non-hostile one never blocks.
    /// </summary>
    [Test]
    public async Task HostileInterdictorBlocksFtlUntilDestroyed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var sectorMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var interdiction = entityManager.System<NsvFtlInterdictionSystem>();

        await server.WaitAssertion(() =>
        {
            var shuttle = MakeSector(server, sectorMap);

            var federal = SpawnShipWithCore(server, sectorMap.MapId, "NSVFederal", new Vector2(50f, 0f));
            entityManager.AddComponent<NsvFtlInterdictorComponent>(federal);
            Assert.That(interdiction.IsInterdicted(shuttle, out _), Is.False, "a friendly interdictor doesn't block");

            var hostile = SpawnShipWithCore(server, sectorMap.MapId, "NSVHostile", new Vector2(-50f, 0f));
            entityManager.AddComponent<NsvFtlInterdictorComponent>(hostile);
            Assert.That(interdiction.IsInterdicted(shuttle, out var reason), Is.True);
            Assert.That(reason, Is.Not.Null);

            var attempt = new ConsoleFTLAttemptEvent(shuttle, false, string.Empty);
            entityManager.EventBus.RaiseLocalEvent(shuttle, ref attempt, true);
            Assert.That(attempt.Cancelled, Is.True, "the shuttle console FTL is blocked too");

            entityManager.DeleteEntity(hostile);
            Assert.That(interdiction.IsInterdicted(shuttle, out _), Is.False, "destroying the core lifts it");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The final stall reminder's blockade lands the configured number of hostile ships in the crew's
    /// sector, each with an interdicting core, so the crew is held until they fight through.
    /// </summary>
    [Test]
    public async Task BlockadeArrivesAtCrewSectorAndInterdicts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var sectorMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var interdiction = entityManager.System<NsvFtlInterdictionSystem>();
        var blockade = entityManager.System<NsvCampaignBlockadeSystem>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();

        var shuttle = EntityUid.Invalid;
        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.CampaignBlockadeSize, 2);
            shuttle = MakeSector(server, sectorMap);

            Assert.That(blockade.DispatchBlockade(), Is.EqualTo(2));

            var hostileGrids = entityManager.EntityQuery<MapGridComponent>()
                .Select(grid => grid.Owner)
                .Where(grid => entityManager.GetComponent<TransformComponent>(grid).MapID == sectorMap.MapId &&
                               factions.TryGetFaction(grid, out var faction) && faction == "NSVHostile")
                .ToList();
            var interdictors = entityManager.EntityQuery<NsvFtlInterdictorComponent>().Count();

            Assert.Multiple(() =>
            {
                Assert.That(hostileGrids, Has.Count.EqualTo(2), "both blockade ships arrive, hostile");
                Assert.That(interdictors, Is.EqualTo(2), "each carries an interdictor");
            });
        });

        // The freshly loaded ships' power network needs a few ticks before their cores read powered.
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            Assert.That(interdiction.IsInterdicted(shuttle, out _), Is.True, "the blockade holds the crew");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Makes the test map a Ready bluespace sector (so factions resolve) holding a player shuttle,
    /// and returns the shuttle.
    /// </summary>
    private static EntityUid MakeSector(RobustIntegrationTest.ServerIntegrationInstance server, TestMapData map)
    {
        var entityManager = server.ResolveDependency<IEntityManager>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();

        var sector = entityManager.AddComponent<NsvBluespaceSectorInstanceComponent>(map.MapUid);
        sector.State = NsvBluespaceSectorState.Ready;
        sector.MapId = map.MapId;

        var shuttle = map.Grid.Owner;
        Assert.That(factions.SetFaction(shuttle, "NSVPlayer"), Is.True);
        sector.ForeignGrids.Add(shuttle);
        return shuttle;
    }

    /// <summary>
    /// A one-tile grid of <paramref name="faction"/> with an AI core anchored on it; returns the core.
    /// </summary>
    private static EntityUid SpawnShipWithCore(
        RobustIntegrationTest.ServerIntegrationInstance server,
        MapId mapId,
        string faction,
        Vector2 position)
    {
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();

        var gridEnt = mapManager.CreateGridEntity(mapId);
        transform.SetWorldPosition(gridEnt.Owner, position);
        mapSystem.SetTile(gridEnt, new Vector2i(0, 0), new Tile(1));
        Assert.That(factions.SetFaction(gridEnt.Owner, faction), Is.True);

        var core = entityManager.SpawnEntity(null, new EntityCoordinates(gridEnt.Owner, new Vector2(0.5f, 0.5f)));
        transform.AnchorEntity(
            (core, entityManager.GetComponent<TransformComponent>(core)),
            (gridEnt.Owner, gridEnt.Comp),
            new Vector2i(0, 0));
        entityManager.AddComponent<NsvBluespaceShipAiCoreComponent>(core);
        return core;
    }
}
