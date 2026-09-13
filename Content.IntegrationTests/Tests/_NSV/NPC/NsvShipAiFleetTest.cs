using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.NPC;
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
    private const string TargetPrototype = "NsvShipAiFleetTestTarget";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {CorePrototype}
  components:
  - type: NsvShipAi
    decisionInterval: 0.05

- type: entity
  id: {TargetPrototype}
  components:
  - type: NsvShipTarget
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
                Assert.That(first.Target, Is.Not.EqualTo(second.Target),
                    "Fleet members must not both focus the same equivalent target.");
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
