using Content.IntegrationTests.Pair;
using Content.Shared._Mono.Grid;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Utility;

/// <summary>
/// Regression tests for the <see cref="GridModEntityReplace"/> blacklist: when a dedicated
/// replacement rule for a multi-tile thruster fails its chance roll, the generic 1x1 rule must
/// not catch the entity afterwards.
/// </summary>
[TestFixture]
public sealed class GridModEntityReplaceBlacklistTest
{
    // Dedicated rule always fails its roll; the generic rule would catch anything with a
    // Thruster component except entities blacklisted for carrying the combustion component.
    private const string ChanceFailModifier = "NsvTestGridModDedicatedChanceFail";

    // Dedicated rule always succeeds, swapping the combustion thruster for a 2x4 wreck.
    private const string ChanceHitModifier = "NsvTestGridModDedicatedChanceHit";

    [TestPrototypes]
    private const string Prototypes = @"
- type: gridModifier
  id: NsvTestGridModDedicatedChanceFail
  modifiers:
  - !type:GridModEntityReplace
    comp: Thruster
    data:
    - chance: 0
      toReplace: NsvCombustionThruster2x4
      replaceWith: MachineFrameDestroyed2x4
    - chance: 1
      whitelist:
        components:
        - Thruster
      blacklist:
        components:
        - NsvCombustionThruster
      replaceWith: MachineFrameDestroyed
- type: gridModifier
  id: NsvTestGridModDedicatedChanceHit
  modifiers:
  - !type:GridModEntityReplace
    comp: Thruster
    data:
    - chance: 1
      toReplace: NsvCombustionThruster2x4
      replaceWith: MachineFrameDestroyed2x4
";

    private static async Task<EntityUid> SetupGrid(TestPair pair)
    {
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var mapSystem = server.System<SharedMapSystem>();

        await server.WaitPost(() =>
        {
            server.EntMan.EnsureComponent<Content.Server.Shuttles.Components.ShuttleComponent>(testMap.Grid.Owner);
            mapSystem.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(1, 0), new Tile(1));
            mapSystem.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(2, 0), new Tile(1));
        });
        await pair.RunTicksSync(1);
        return testMap.Grid.Owner;
    }

    private static void ApplyModifier(TestPair pair, EntityUid grid, string modifierId)
    {
        var server = pair.Server;
        var proto = server.ProtoMan.Index<GridModificationPrototype>(modifierId);
        var factory = server.ResolveDependency<IComponentFactory>();
        foreach (var modifier in proto.Modifiers)
            modifier.Modify(grid, (EntityManager) server.EntMan, factory);
    }

    private static int CountPrototype(TestPair pair, EntityUid grid, string prototypeId)
    {
        var entMan = pair.Server.EntMan;
        var count = 0;
        var children = entMan.GetComponent<TransformComponent>(grid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == prototypeId)
                count++;
        }
        return count;
    }

    /// <summary>
    /// When the dedicated 2x4 rule's chance roll fails, the blacklisted generic 1x1 rule must skip
    /// the combustion thruster entirely while still replacing a plain shuttle thruster.
    /// </summary>
    [Test]
    public async Task BlacklistPreventsGenericFallbackTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var grid = await SetupGrid(pair);

        EntityUid combustion = default;
        EntityUid plain = default;
        await server.WaitPost(() =>
        {
            combustion = server.EntMan.SpawnEntity("NsvCombustionThruster2x4", new EntityCoordinates(grid, 0.5f, 0.5f));
            plain = server.EntMan.SpawnEntity("Thruster", new EntityCoordinates(grid, 2.5f, 0.5f));
        });
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(server.EntMan.HasComponent<Content.Server.Shuttles.Components.ThrusterComponent>(combustion), Is.True);
                Assert.That(server.EntMan.HasComponent<Content.Server.Shuttles.Components.ThrusterComponent>(plain), Is.True);
                Assert.That(server.EntMan.GetComponent<TransformComponent>(combustion).GridUid, Is.EqualTo(grid));
                Assert.That(server.EntMan.GetComponent<TransformComponent>(plain).GridUid, Is.EqualTo(grid));
                Assert.That(server.EntMan.GetComponent<MetaDataComponent>(combustion).EntityPrototype?.ID,
                    Is.EqualTo("NsvCombustionThruster2x4"));
                Assert.That(server.EntMan.GetComponent<MetaDataComponent>(plain).EntityPrototype?.ID, Is.EqualTo("Thruster"));
            });
        });

        await server.WaitAssertion(() => ApplyModifier(pair, grid, ChanceFailModifier));
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(server.EntMan.EntityExists(combustion), Is.True,
                    "A chance-failed dedicated rule must not fall through to the blacklisted generic rule.");
                Assert.That(server.EntMan.EntityExists(plain), Is.False,
                    "The generic rule should still replace the plain thruster.");
                Assert.That(CountPrototype(pair, grid, "MachineFrameDestroyed"), Is.EqualTo(1));
                Assert.That(CountPrototype(pair, grid, "MachineFrameDestroyed2x4"), Is.EqualTo(0));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// When the dedicated rule's chance roll succeeds, the combustion thruster is swapped for a
    /// 2x4 destroyed frame, keeping its multi-tile footprint.
    /// </summary>
    [Test]
    public async Task DedicatedRuleProduces2x4WreckTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var grid = await SetupGrid(pair);

        EntityUid combustion = default;
        await server.WaitPost(() =>
        {
            combustion = server.EntMan.SpawnEntity("NsvCombustionThruster2x4", new EntityCoordinates(grid, 0.5f, 0.5f));
        });
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() => ApplyModifier(pair, grid, ChanceHitModifier));
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(server.EntMan.EntityExists(combustion), Is.False);
                Assert.That(CountPrototype(pair, grid, "MachineFrameDestroyed2x4"), Is.EqualTo(1),
                    "A successful dedicated rule should swap in the 2x4 destroyed frame.");
                Assert.That(CountPrototype(pair, grid, "MachineFrameDestroyed"), Is.EqualTo(0));
            });
        });

        await pair.CleanReturnAsync();
    }
}
