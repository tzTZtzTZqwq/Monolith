using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Sectors;

/// <summary>
/// The jupiterG flagship candidate loads cleanly (the pool fails the test on any error log, e.g. a
/// container referencing a deleted entity) and carries exactly one CTLA-160 jump core, one jump
/// console and one bluespace navigation console, all anchored to the ship.
/// </summary>
[TestFixture]
public sealed class NsvJupiterGMapTest
{
    private static readonly ResPath MapPath = new("/Maps/_Mono/Supercapitals/jupiterG.yml");

    [Test]
    public async Task JupiterGLoadsWithJumpEquipment()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            entityManager.System<MapSystem>().CreateMap(out var mapId);
            Assert.That(entityManager.System<MapLoaderSystem>().TryLoadGrid(mapId, MapPath, out var grid), Is.True);
            var ship = grid!.Value.Owner;

            Assert.Multiple(() =>
            {
                Assert.That(CountAnchored<NsvBluespaceDriveComponent>(entityManager, ship), Is.EqualTo(1), "jump core");
                Assert.That(CountAnchored<NsvBluespaceDriveConsoleComponent>(entityManager, ship), Is.EqualTo(1), "jump console");
                Assert.That(CountAnchored<NsvBluespaceJumpPointComponent>(entityManager, ship), Is.EqualTo(1), "navigation console");
            });
        });

        await pair.CleanReturnAsync();
    }

    private static int CountAnchored<T>(IEntityManager entityManager, EntityUid ship) where T : IComponent
    {
        var count = 0;
        var query = entityManager.AllEntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid == ship && xform.Anchored)
                count++;
        }

        return count;
    }
}
