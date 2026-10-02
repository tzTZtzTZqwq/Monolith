using System.Numerics;
using Content.Server._Mono.FireControl;
using Content.Server._NSV.Bluespace.Strategy;
using Content.Shared._NSV.CCVar;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Strategy;

/// <summary>
/// The S7 abstract-combat driver resolves one dice exchange per dormant node per pass between a
/// mutually-hostile pair of parked data ships, grinding the loser's data-state integrity down and
/// destroying it at zero. It never fires at a node holding fewer than two data ships, a same-faction
/// group, or a node where a ship is still Live (the live layer owns it).
/// </summary>
[TestFixture]
public sealed class NsvAbstractCombatSystemTest
{
    private const string StrategicMapId = "NSVBluespaceStrategicMap";
    private const string HostileFaction = "NSVHostile";
    private const string FederalFaction = "NSVFederal";

    [Test]
    public async Task ResolvesExchangesAndDestroysLoserOverPasses()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();
        var combat = entityManager.System<NsvAbstractCombatSystem>();

        await server.WaitAssertion(() =>
        {
            // Two lost floors per exchange out of a 4-floor complement: 4 -> 2 -> destroyed.
            cfg.SetCVar(NsvCCVars.StrategyCombatDamageFraction, 0.5f);

            // Distinct node per test: the Dirty pool recycles servers without a round restart, so
            // registry residency leaks between methods; a shared node would let one test's ships
            // freeze another's (e.g. NoCombatWhileAShipIsLive's Live ship blocking this exchange).
            var node = new NsvFleetNodeKey(StrategicMapId, "combat-exchange");
            // The armed hostile always wins: a turretless federal ship has zero power, so its
            // score is zero and it is always the loser regardless of the dice roll.
            var winner = SpawnDataShip(entityManager, mapManager, mapSystem, transform, fleets,
                testMap.MapId, node, HostileFaction, turrets: 2);
            var loser = SpawnDataShip(entityManager, mapManager, mapSystem, transform, fleets,
                testMap.MapId, node, FederalFaction, turrets: 0);

            combat.ResolveAll();
            Assert.That(fleets.TryGetShip(loser, out var loserAfterOne), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(loserAfterOne.State, Is.EqualTo(NsvFleetShipState.Available), "one exchange doesn't kill");
                Assert.That(loserAfterOne.DataTargetFloorCount, Is.EqualTo(2), "half the complement lost");
            });

            combat.ResolveAll();
            Assert.That(fleets.TryGetShip(loser, out var loserAfterTwo), Is.True);
            Assert.That(loserAfterTwo.State, Is.EqualTo(NsvFleetShipState.Destroyed),
                "a second exchange drops it to zero and destroys it");

            // The winner is untouched: full integrity, still parked and available.
            Assert.That(fleets.TryGetShip(winner, out var winnerShip), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(winnerShip.State, Is.EqualTo(NsvFleetShipState.Available));
                Assert.That(winnerShip.DataTargetFloorCount, Is.EqualTo(4));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NoCombatBetweenSameFaction()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();
        var combat = entityManager.System<NsvAbstractCombatSystem>();

        await server.WaitAssertion(() =>
        {
            var node = new NsvFleetNodeKey(StrategicMapId, "combat-samefaction");
            var a = SpawnDataShip(entityManager, mapManager, mapSystem, transform, fleets,
                testMap.MapId, node, HostileFaction, turrets: 2);
            var b = SpawnDataShip(entityManager, mapManager, mapSystem, transform, fleets,
                testMap.MapId, node, HostileFaction, turrets: 2);

            combat.ResolveAll();

            Assert.That(fleets.TryGetShip(a, out var shipA), Is.True);
            Assert.That(fleets.TryGetShip(b, out var shipB), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(shipA.DataTargetFloorCount, Is.EqualTo(4), "allies never fight");
                Assert.That(shipB.DataTargetFloorCount, Is.EqualTo(4), "allies never fight");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NoCombatWhileAShipIsLive()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();
        var combat = entityManager.System<NsvAbstractCombatSystem>();

        await server.WaitAssertion(() =>
        {
            var node = new NsvFleetNodeKey(StrategicMapId, "combat-live");
            var a = SpawnDataShip(entityManager, mapManager, mapSystem, transform, fleets,
                testMap.MapId, node, HostileFaction, turrets: 2);
            var b = SpawnDataShip(entityManager, mapManager, mapSystem, transform, fleets,
                testMap.MapId, node, FederalFaction, turrets: 0);

            // A third ship stays Live at the node (the player materialized here): its authority is
            // the grid, not the data number, so the whole node is off-limits to abstract combat.
            var liveGrid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            fleets.RegisterShip(liveGrid, new ResPath("/Maps/_NSV/test.yml"), node);

            combat.ResolveAll();

            Assert.That(fleets.TryGetShip(a, out var shipA), Is.True);
            Assert.That(fleets.TryGetShip(b, out var shipB), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(shipA.DataTargetFloorCount, Is.EqualTo(4), "a live ship freezes the node");
                Assert.That(shipB.DataTargetFloorCount, Is.EqualTo(4), "a live ship freezes the node");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Builds one data-state ship resident at <paramref name="node"/>: a 2x2 (4-floor) grid with
    /// <paramref name="turrets"/> anchored turrets, registered, faction-tagged, and serialized into
    /// the data state (Available, integrity photographed at 4). Turrets sit on the outer-ring
    /// survivors so they persist under abstract damage.
    /// </summary>
    private static string SpawnDataShip(
        IEntityManager entityManager,
        IMapManager mapManager,
        SharedMapSystem mapSystem,
        SharedTransformSystem transform,
        NsvFleetRegistrySystem fleets,
        MapId mapId,
        NsvFleetNodeKey node,
        string faction,
        int turrets)
    {
        var gridEnt = mapManager.CreateGridEntity(mapId);
        // Removing floors can split the grid; disable it so damage targets the integrity number.
        gridEnt.Comp.CanSplit = false;
        for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
                mapSystem.SetTile(gridEnt, new Vector2i(x, y), new Tile(1));

        for (var i = 0; i < turrets; i++)
        {
            var indices = new Vector2i(1, i);
            var turret = entityManager.SpawnEntity(null, new EntityCoordinates(gridEnt.Owner, indices));
            entityManager.AddComponent<FireControllableComponent>(turret);
            transform.AnchorEntity(
                (turret, entityManager.GetComponent<TransformComponent>(turret)),
                (gridEnt.Owner, gridEnt.Comp),
                indices);
        }

        var ship = fleets.RegisterShip(gridEnt.Owner, new ResPath("/Maps/_NSV/test.yml"), node);
        ship.Faction = faction;
        Assert.That(fleets.TrySerializeShip(ship.Id, out var failure), Is.True, failure);
        return ship.Id;
    }
}
