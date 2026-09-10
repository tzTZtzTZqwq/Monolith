using System.Numerics;
using Content.Server._NSV.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests._NSV.Shuttles;

[TestFixture]
public sealed class NsvGridMassSystemTest
{
    private const string WallPrototype = "NsvGridMassTestWall";

    // A 1x1 fixture at density 1000 => FixturesMass 1000, a controlled stand-in for a wall/machine.
    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {WallPrototype}
  components:
  - type: Physics
    bodyType: Static
  - type: Fixtures
    fixtures:
      fix1:
        shape:
          !type:PhysShapeAabb
          bounds: ""-0.5,-0.5,0.5,0.5""
        density: 1000
";

    /// <summary>
    /// Anchoring an entity folds its mass into the grid body; unanchoring removes it again;
    /// and a subsequent tile change preserves the anchored contribution rather than resetting it.
    /// </summary>
    [Test]
    public async Task AnchoredEntityFoldsIntoGridMass()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var xforms = server.System<SharedTransformSystem>();

        var grid = EntityUid.Invalid;
        var wall = EntityUid.Invalid;
        var wallMass = 0f;
        var baseline = 0f;

        await server.WaitPost(() =>
        {
            var gridEnt = mapManager.CreateGridEntity(testMap.MapId);
            grid = gridEnt.Owner;
            xforms.SetWorldPosition(entMan.GetComponent<TransformComponent>(grid), new Vector2(100f, 0f));

            // 2x2 = 4 tiles of area, base tile mass only.
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
                mapSystem.SetTile(grid, gridEnt.Comp, new Vector2i(x, y), new Tile(1));

            wall = entMan.SpawnEntity(WallPrototype, new EntityCoordinates(grid, 0.5f, 0.5f));
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            wallMass = entMan.GetComponent<PhysicsComponent>(wall).FixturesMass;
            baseline = entMan.GetComponent<PhysicsComponent>(grid).FixturesMass;
            Assert.Multiple(() =>
            {
                Assert.That(wallMass, Is.GreaterThan(0f), "Test wall should have positive fixtures mass.");
                Assert.That(entMan.GetComponent<TransformComponent>(wall).Anchored, Is.False,
                    "Wall should spawn unanchored, contributing nothing yet.");
            });
        });

        // Anchor: the grid should gain exactly the wall's mass.
        await server.WaitPost(() => Assert.That(xforms.AnchorEntity(wall), Is.True));
        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<PhysicsComponent>(grid).FixturesMass,
                    Is.EqualTo(baseline + wallMass).Within(1f),
                    "Grid mass should increase by the anchored wall mass.");
                Assert.That(entMan.GetComponent<NsvGridAnchoredMassComponent>(grid).AnchoredMass,
                    Is.EqualTo((double) wallMass).Within(1.0),
                    "Tracked anchored mass should equal the wall's fixtures mass.");
            });
        });

        // Unanchor: the grid should fall back to its tile-only baseline.
        await server.WaitPost(() => xforms.Unanchor(wall));
        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<PhysicsComponent>(grid).FixturesMass,
                Is.EqualTo(baseline).Within(1f),
                "Grid mass should return to baseline after unanchoring."));

        // Re-anchor, then add a tile: the anchored contribution must survive the tile fixture rebuild.
        await server.WaitPost(() => Assert.That(xforms.AnchorEntity(wall), Is.True));
        await pair.RunTicksSync(2);
        await server.WaitPost(() =>
            mapSystem.SetTile(grid, entMan.GetComponent<MapGridComponent>(grid), new Vector2i(2, 0), new Tile(1)));
        await pair.RunTicksSync(5);
        await server.WaitAssertion(() =>
        {
            // baseline was 4 tiles of base density; per-tile base scales the fifth tile too.
            var perTile = baseline / 4f;
            var expected = perTile * 5f + wallMass;
            Assert.That(entMan.GetComponent<PhysicsComponent>(grid).FixturesMass,
                Is.EqualTo(expected).Within(1f),
                "Anchored mass should persist across a tile change, on top of the new tile's base mass.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Deleting an anchored entity (e.g. a destroyed wall) removes its contribution from the grid mass.
    /// </summary>
    [Test]
    public async Task DeletingAnchoredEntityRemovesGridMass()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var xforms = server.System<SharedTransformSystem>();

        var grid = EntityUid.Invalid;
        var wall = EntityUid.Invalid;
        var baseline = 0f;
        var wallMass = 0f;

        await server.WaitPost(() =>
        {
            var gridEnt = mapManager.CreateGridEntity(testMap.MapId);
            grid = gridEnt.Owner;
            xforms.SetWorldPosition(entMan.GetComponent<TransformComponent>(grid), new Vector2(100f, 0f));

            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
                mapSystem.SetTile(grid, gridEnt.Comp, new Vector2i(x, y), new Tile(1));

            wall = entMan.SpawnEntity(WallPrototype, new EntityCoordinates(grid, 0.5f, 0.5f));
        });
        await pair.RunTicksSync(2);

        await server.WaitPost(() =>
        {
            baseline = entMan.GetComponent<PhysicsComponent>(grid).FixturesMass;
            wallMass = entMan.GetComponent<PhysicsComponent>(wall).FixturesMass;
            Assert.That(xforms.AnchorEntity(wall), Is.True);
        });
        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<PhysicsComponent>(grid).FixturesMass,
                Is.EqualTo(baseline + wallMass).Within(1f),
                "Grid mass should include the anchored wall before deletion."));

        await server.WaitPost(() => entMan.DeleteEntity(wall));
        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<PhysicsComponent>(grid).FixturesMass,
                Is.EqualTo(baseline).Within(1f),
                "Grid mass should drop back to baseline after the anchored entity is deleted."));

        await pair.CleanReturnAsync();
    }
}
