using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Mono.NPC.HTN;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.NPC;
using Content.Server.Power.Components;
using Content.IntegrationTests.Pair;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NSV.NPC;

/// <summary>
/// Implicit fleet behavior of <see cref="NsvShipAiSystem"/>: cores on the same map with the same
/// faction form a fleet (UID-sorted slots, spread angle offsets), and already-claimed targets are
/// de-prioritized so fleet members spread across targets instead of focus-firing one.
/// </summary>
[TestFixture]
public sealed class NsvShipAiFleetTest
{
    private const string CorePrototype = "NsvShipAiFleetTestCore";
    private const string PoweredCorePrototype = "NsvShipAiFleetTestPoweredCore";
    private const string TargetPrototype = "NsvShipAiFleetTestTarget";
    private const string PoweredTargetPrototype = "NsvShipAiFleetTestPoweredTarget";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {CorePrototype}
  components:
  - type: NsvShipAi
    decisionInterval: 0.05

- type: entity
  id: {PoweredCorePrototype}
  components:
  - type: NsvShipAi
    decisionInterval: 0.05
  - type: NsvShipTarget
    needPower: true
  - type: ApcPowerReceiver

- type: entity
  id: {TargetPrototype}
  components:
  - type: NsvShipTarget

- type: entity
  id: {PoweredTargetPrototype}
  components:
  - type: NsvShipTarget
    needPower: true
  - type: ApcPowerReceiver
";

    private Entity<MapGridComponent> CreateGrid(
        IEntityManager entityManager,
        IMapManager mapManager,
        SharedMapSystem mapSystem,
        SharedTransformSystem transform,
        MapId mapId,
        Vector2 position)
    {
        var grid = mapManager.CreateGridEntity(mapId);
        transform.SetWorldPosition(entityManager.GetComponent<TransformComponent>(grid.Owner), position);
        mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
        return grid;
    }

    [Test]
    public async Task SameFactionCoresFormFleetWithSpreadOffsets()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        var cores = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid);

            for (var i = 0; i < 3; i++)
            {
                var grid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(i * 100f, 0f));
                Assert.That(factions.SetFaction(grid.Owner, "NSVHostile"), Is.True);
                cores.Add(entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(grid.Owner, 0, 0)));
            }
        });
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var ais = cores.Select(entityManager.GetComponent<NsvShipAiComponent>).ToList();
            Assert.Multiple(() =>
            {
                foreach (var ai in ais)
                {
                    Assert.That(ai.FleetSize, Is.EqualTo(3));
                    Assert.That(ai.CachedOtherThreats, Is.EqualTo(0), "Same-faction cores must not count as threats.");
                }

                var sorted = cores.OrderBy(uid => uid.Id).ToList();
                var expectedOffsets = new[] { -35f, 0f, 35f };
                for (var i = 0; i < sorted.Count; i++)
                {
                    var ai = entityManager.GetComponent<NsvShipAiComponent>(sorted[i]);
                    Assert.That(ai.FleetIndex, Is.EqualTo(i));
                    Assert.That(ai.FleetAngleOffset, Is.EqualTo(expectedOffsets[i]).Within(0.01f));
                }
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FleetExcludesOtherFactionsMapsAndRange()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var firstMap = await pair.CreateTestMap();
        var secondMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        EntityUid differentFaction = default;
        EntityUid otherMap = default;
        EntityUid farAway = default;

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(firstMap.MapUid);
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(secondMap.MapUid);

            var own = CreateGrid(entityManager, mapManager, mapSystem, transform, firstMap.MapId, Vector2.Zero);
            Assert.That(factions.SetFaction(own.Owner, "NSVHostile"), Is.True);
            entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(own.Owner, 0, 0));

            var neutral = CreateGrid(entityManager, mapManager, mapSystem, transform, firstMap.MapId, new Vector2(100f, 0f));
            Assert.That(factions.SetFaction(neutral.Owner, "NSVNeutral"), Is.True);
            differentFaction = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(neutral.Owner, 0, 0));

            var second = CreateGrid(entityManager, mapManager, mapSystem, transform, secondMap.MapId, Vector2.Zero);
            Assert.That(factions.SetFaction(second.Owner, "NSVHostile"), Is.True);
            otherMap = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(second.Owner, 0, 0));

            var far = CreateGrid(entityManager, mapManager, mapSystem, transform, firstMap.MapId, new Vector2(5000f, 0f));
            Assert.That(factions.SetFaction(far.Owner, "NSVHostile"), Is.True);
            farAway = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(far.Owner, 0, 0));
        });
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entityManager.GetComponent<NsvShipAiComponent>(differentFaction).FleetSize, Is.EqualTo(1));
                Assert.That(entityManager.GetComponent<NsvShipAiComponent>(otherMap).FleetSize, Is.EqualTo(1));
                Assert.That(entityManager.GetComponent<NsvShipAiComponent>(farAway).FleetSize, Is.EqualTo(1));
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FleetMembersSpreadAcrossEquivalentTargets()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        var cores = new List<EntityUid>();
        var targets = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid);

            for (var i = 0; i < 2; i++)
            {
                var grid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(-200f, i * 100f));
                Assert.That(factions.SetFaction(grid.Owner, "NSVHostile"), Is.True);
                cores.Add(entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(grid.Owner, 0, 0)));
            }

            for (var i = 0; i < 2; i++)
            {
                var grid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(200f, (i - 0.5f) * 100f));
                Assert.That(factions.SetFaction(grid.Owner, "NSVPlayer"), Is.True);
                targets.Add(entityManager.SpawnEntity(TargetPrototype, new EntityCoordinates(grid.Owner, 0, 0)));
                if (i == 0)
                    entityManager.SpawnEntity(TargetPrototype, new EntityCoordinates(grid.Owner, 0, 0));
            }
        });
        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                var first = entityManager.GetComponent<NsvShipAiComponent>(cores[0]);
                var second = entityManager.GetComponent<NsvShipAiComponent>(cores[1]);

                Assert.That(first.FleetSize, Is.EqualTo(2));
                Assert.That(second.FleetSize, Is.EqualTo(2));
                Assert.That(first.Target, Is.Not.Null);
                Assert.That(second.Target, Is.Not.Null);
                Assert.That(entityManager.GetComponent<TransformComponent>(first.Target!.Value).GridUid,
                    Is.Not.EqualTo(entityManager.GetComponent<TransformComponent>(second.Target!.Value).GridUid),
                    "Fleet claims must apply to the target grid, not just one marker entity on it.");

                var firstSteerer = entityManager.GetComponent<ShipSteererComponent>(cores[0]);
                Assert.That(firstSteerer.FacingCoordinates?.EntityId, Is.EqualTo(first.Target),
                    "A ship navigating to a flank waypoint must keep facing its combat target.");
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnpoweredCoreDoesNotRunOrJoinFleet()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        EntityUid poweredCore = default;
        EntityUid unpoweredCore = default;

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid);

            var poweredGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(-200f, 0f));
            Assert.That(factions.SetFaction(poweredGrid.Owner, "NSVHostile"), Is.True);
            poweredCore = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(poweredGrid.Owner, 0, 0));

            var unpoweredGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(-100f, 0f));
            Assert.That(factions.SetFaction(unpoweredGrid.Owner, "NSVHostile"), Is.True);
            unpoweredCore = entityManager.SpawnEntity(PoweredCorePrototype, new EntityCoordinates(unpoweredGrid.Owner, 0, 0));

            var enemyGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(200f, 0f));
            Assert.That(factions.SetFaction(enemyGrid.Owner, "NSVPlayer"), Is.True);
            entityManager.SpawnEntity(TargetPrototype, new EntityCoordinates(enemyGrid.Owner, 0, 0));
        });
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var activeAi = entityManager.GetComponent<NsvShipAiComponent>(poweredCore);
            var inactiveAi = entityManager.GetComponent<NsvShipAiComponent>(unpoweredCore);
            Assert.Multiple(() =>
            {
                Assert.That(entityManager.GetComponent<ApcPowerReceiverComponent>(unpoweredCore).Powered, Is.False);
                Assert.That(activeAi.FleetSize, Is.EqualTo(1));
                Assert.That(inactiveAi.FleetSize, Is.EqualTo(1));
                Assert.That(inactiveAi.Target, Is.Null);
                Assert.That(entityManager.HasComponent<ShipSteererComponent>(unpoweredCore), Is.False);
                Assert.That(entityManager.HasComponent<ShipTargetingComponent>(unpoweredCore), Is.False);
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnpoweredTargetIsSkipped()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        EntityUid core = default;
        EntityUid validTarget = default;

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid);

            var ownGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(-200f, 0f));
            Assert.That(factions.SetFaction(ownGrid.Owner, "NSVHostile"), Is.True);
            core = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(ownGrid.Owner, 0, 0));

            var unpoweredGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, Vector2.Zero);
            Assert.That(factions.SetFaction(unpoweredGrid.Owner, "NSVPlayer"), Is.True);
            entityManager.SpawnEntity(PoweredTargetPrototype, new EntityCoordinates(unpoweredGrid.Owner, 0, 0));

            var validGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(200f, 0f));
            Assert.That(factions.SetFaction(validGrid.Owner, "NSVPlayer"), Is.True);
            validTarget = entityManager.SpawnEntity(TargetPrototype, new EntityCoordinates(validGrid.Owner, 0, 0));
        });
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var ai = entityManager.GetComponent<NsvShipAiComponent>(core);
            Assert.That(ai.Target, Is.EqualTo(validTarget));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CartridgeWeaponRangeUsesProjectileLifetime()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        EntityUid core = default;

        await server.WaitPost(() =>
        {
            var grid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, Vector2.Zero);
            core = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(grid.Owner, 0, 0));
            entityManager.SpawnEntity("WeaponTurretL85Autocannon", new EntityCoordinates(grid.Owner, 0, 0));
        });
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var ai = entityManager.GetComponent<NsvShipAiComponent>(core);
            Assert.That(ai.CachedWeaponRange, Is.EqualTo(384f).Within(0.01f));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShieldStressScalesEngagementRange()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        EntityUid core = default;

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid);

            var ownGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, Vector2.Zero);
            Assert.That(factions.SetFaction(ownGrid.Owner, "NSVHostile"), Is.True);
            core = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(ownGrid.Owner, 0, 0));
            entityManager.SpawnEntity("WeaponTurretL85Autocannon", new EntityCoordinates(ownGrid.Owner, 0, 0));

            var targetGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(1000f, 0f));
            Assert.That(factions.SetFaction(targetGrid.Owner, "NSVPlayer"), Is.True);
            entityManager.SpawnEntity(TargetPrototype, new EntityCoordinates(targetGrid.Owner, 0, 0));
        });
        await pair.RunTicksSync(30);

        await server.WaitPost(() =>
        {
            var ai = entityManager.GetComponent<NsvShipAiComponent>(core);
            Assert.That(ai.CachedWeaponRange, Is.EqualTo(384f).Within(0.01f));
            ai.CachedShieldStress = 0.5f;
            ai.PerceptionAccum = 100f;
        });
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            var steerer = entityManager.GetComponent<ShipSteererComponent>(core);
            Assert.That(steerer.Range, Is.EqualTo(384f * (0.6f + 0.45f * 0.5f)).Within(0.01f));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MapLeashIsOptionalAndBiasesNavigationInward()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        EntityUid core = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceFactionMapComponent>(testMap.MapUid);
            entityManager.EnsureComponent<NsvShipAiMapComponent>(testMap.MapUid);

            var ownGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(3100f, 0f));
            Assert.That(factions.SetFaction(ownGrid.Owner, "NSVHostile"), Is.True);
            core = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(ownGrid.Owner, 0, 0));

            var targetGrid = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(3500f, 0f));
            Assert.That(factions.SetFaction(targetGrid.Owner, "NSVPlayer"), Is.True);
            target = entityManager.SpawnEntity(TargetPrototype, new EntityCoordinates(targetGrid.Owner, 0, 0));
        });
        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            var leash = entityManager.GetComponent<NsvShipAiMapComponent>(testMap.MapUid);
            var steerer = entityManager.GetComponent<ShipSteererComponent>(core);
            Assert.Multiple(() =>
            {
                Assert.That(leash.LeashRadius, Is.Null);
                Assert.That(steerer.Coordinates.EntityId, Is.EqualTo(target));
            });
        });

        await server.WaitPost(() =>
            entityManager.GetComponent<NsvShipAiMapComponent>(testMap.MapUid).LeashRadius = 3000f);
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var steerer = entityManager.GetComponent<ShipSteererComponent>(core);
            var ownPos = transform.GetMapCoordinates(core);
            var targetPos = transform.GetMapCoordinates(target);
            var waypoint = transform.ToMapCoordinates(steerer.Coordinates);
            Assert.Multiple(() =>
            {
                Assert.That(waypoint.Position.X, Is.GreaterThan(ownPos.Position.X));
                Assert.That(waypoint.Position.X, Is.LessThan(targetPos.Position.X));
                Assert.That(steerer.Mode, Is.EqualTo(ShipSteeringMode.GoToRange));
                Assert.That(steerer.FacingCoordinates?.EntityId, Is.EqualTo(target));
            });
        });

        await server.WaitPost(() => entityManager.DeleteEntity(target));
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var ai = entityManager.GetComponent<NsvShipAiComponent>(core);
            var steerer = entityManager.GetComponent<ShipSteererComponent>(core);
            var waypoint = transform.ToMapCoordinates(steerer.Coordinates);
            Assert.Multiple(() =>
            {
                Assert.That(ai.Target, Is.Null);
                Assert.That(waypoint.Position, Is.EqualTo(Vector2.Zero));
                Assert.That(steerer.Range, Is.EqualTo(3000f));
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SoloCoreKeepsStraightApproach()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        EntityUid core = default;

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid);

            var own = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(-200f, 0f));
            Assert.That(factions.SetFaction(own.Owner, "NSVHostile"), Is.True);
            core = entityManager.SpawnEntity(CorePrototype, new EntityCoordinates(own.Owner, 0, 0));

            var enemy = CreateGrid(entityManager, mapManager, mapSystem, transform, testMap.MapId, new Vector2(200f, 0f));
            Assert.That(factions.SetFaction(enemy.Owner, "NSVPlayer"), Is.True);
            entityManager.SpawnEntity(TargetPrototype, new EntityCoordinates(enemy.Owner, 0, 0));
        });
        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            var ai = entityManager.GetComponent<NsvShipAiComponent>(core);
            Assert.Multiple(() =>
            {
                Assert.That(ai.FleetSize, Is.EqualTo(1));
                Assert.That(ai.FleetAngleOffset, Is.EqualTo(0f));
                Assert.That(ai.Target, Is.Not.Null);
                // No other hostiles besides the target: the solo ship approaches straight
                // (attack vector is null, the pre-fleet behavior).
                Assert.That(ai.CachedOtherThreats, Is.EqualTo(0));
            });
        });
        await pair.CleanReturnAsync();
    }
}
