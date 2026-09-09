using System.Linq;
using System.Numerics;
using Content.Server._Mono.NPC.HTN;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.NPC.HTN;
using Content.Server.NPC;
using Content.Server.NPC.Systems;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NSV.NPC;

[TestFixture]
public sealed class NsvShipTargetQueryTest
{
    private const string OwnerPrototype = "NsvShipTargetQueryOwner";
    private const string NsvTargetPrototype = "NsvShipTargetQueryNsvTarget";
    private const string MonoTargetPrototype = "NsvShipTargetQueryMonoTarget";
    private const string QueryPrototype = "NsvShipTargetQuery";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {OwnerPrototype}

- type: entity
  id: {NsvTargetPrototype}
  components:
  - type: NsvShipTarget

- type: entity
  id: {MonoTargetPrototype}
  components:
  - type: ShipNpcTarget

- type: utilityQuery
  id: {QueryPrototype}
  query:
  - !type:NsvNearbyShipTargetsQuery
    range: 100
";

    [Test]
    public async Task ExistingShipTargetsHaveNsvEquivalents()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ProtoMan;
        var componentFactory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var shipTargets = prototypes.EnumeratePrototypes<EntityPrototype>()
                .Where(prototype => !pair.IsTestPrototype(prototype))
                .Where(prototype => prototype.TryGetComponent<ShipNpcTargetComponent>(out _, componentFactory))
                .ToList();

            Assert.That(shipTargets, Is.Not.Empty);
            Assert.Multiple(() =>
            {
                foreach (var prototype in shipTargets)
                {
                    var hasNsvTarget = prototype.TryGetComponent<NsvShipTargetComponent>(out var nsvTarget, componentFactory);
                    var hasMonoTarget = prototype.TryGetComponent<ShipNpcTargetComponent>(out var monoTarget, componentFactory);

                    Assert.That(hasNsvTarget, Is.True, $"{prototype.ID} has ShipNpcTarget but no NsvShipTarget.");
                    Assert.That(hasMonoTarget, Is.True, $"{prototype.ID} has no ShipNpcTarget.");
                    if (!hasNsvTarget || !hasMonoTarget)
                        continue;

                    Assert.That(nsvTarget!.NeedPower, Is.EqualTo(monoTarget!.NeedPower), $"{prototype.ID} has mismatched target power requirements.");
                    Assert.That((int) nsvTarget.NeedGrid, Is.EqualTo((int) monoTarget.NeedGrid), $"{prototype.ID} has mismatched target grid requirements.");
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task QueryUsesNsvTargetsAndFactionRelations()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        var utility = server.System<NPCUtilitySystem>();
        var owner = EntityUid.Invalid;
        var nsvTarget = EntityUid.Invalid;
        var monoTarget = EntityUid.Invalid;
        var sameGridTarget = EntityUid.Invalid;
        var factionlessTarget = EntityUid.Invalid;
        var targetGrid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid);

            var target = mapManager.CreateGridEntity(testMap.MapId);
            transform.SetWorldPosition(entityManager.GetComponent<TransformComponent>(target.Owner), new Vector2(20f, 0f));
            mapSystem.SetTile(target.Owner, target.Comp, Vector2i.Zero, new Tile(1));
            targetGrid = target.Owner;

            var factionless = mapManager.CreateGridEntity(testMap.MapId);
            transform.SetWorldPosition(entityManager.GetComponent<TransformComponent>(factionless.Owner), new Vector2(40f, 0f));
            mapSystem.SetTile(factionless.Owner, factionless.Comp, Vector2i.Zero, new Tile(1));

            owner = entityManager.SpawnEntity(OwnerPrototype, testMap.GridCoords);
            nsvTarget = entityManager.SpawnEntity(NsvTargetPrototype, new EntityCoordinates(target.Owner, 0, 0));
            monoTarget = entityManager.SpawnEntity(MonoTargetPrototype, new EntityCoordinates(target.Owner, 0, 0));
            sameGridTarget = entityManager.SpawnEntity(NsvTargetPrototype, testMap.GridCoords);
            factionlessTarget = entityManager.SpawnEntity(NsvTargetPrototype, new EntityCoordinates(factionless.Owner, 0, 0));

            Assert.That(factions.SetFaction(testMap.Grid, "NSVHostile"), Is.True);
            Assert.That(factions.SetFaction(target.Owner, "NSVPlayer"), Is.True);
        });
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(factions.TryGetSectorMap(owner, out var ownerMap, out _), Is.True);
                Assert.That(factions.TryGetSectorMap(nsvTarget, out var targetMap, out _), Is.True);
                Assert.That(ownerMap, Is.EqualTo(testMap.MapUid));
                Assert.That(targetMap, Is.EqualTo(testMap.MapUid));
                Assert.That(entityManager.HasComponent<NsvBluespaceFactionComponent>(testMap.Grid), Is.True);
                Assert.That(entityManager.HasComponent<NsvBluespaceFactionComponent>(targetGrid), Is.True);
                Assert.That(entityManager.GetComponent<TransformComponent>(owner).GridUid?.ToString(), Is.EqualTo(testMap.Grid.Owner.ToString()));
                Assert.That(entityManager.GetComponent<TransformComponent>(nsvTarget).GridUid?.ToString(), Is.EqualTo(targetGrid.ToString()));
                Assert.That(factions.TryGetFaction(owner, out var ownerFaction), Is.True);
                Assert.That(factions.TryGetFaction(nsvTarget, out var targetFaction), Is.True);
                Assert.That(ownerFaction.ToString(), Is.EqualTo("NSVHostile"));
                Assert.That(targetFaction.ToString(), Is.EqualTo("NSVPlayer"));
                Assert.That(factions.IsHostile(owner, nsvTarget), Is.True);
            });

            Assert.Multiple(() =>
            {
                Assert.That(Selects(utility, owner, nsvTarget), Is.True);
                Assert.That(Selects(utility, owner, monoTarget), Is.False);
                Assert.That(Selects(utility, owner, sameGridTarget), Is.False);
                Assert.That(Selects(utility, owner, factionlessTarget), Is.False);
            });

            Assert.That(factions.SetSectorRelation(testMap.MapUid, "NSVHostile", "NSVPlayer", NsvBluespaceFactionRelation.Neutral), Is.True);
            Assert.That(Selects(utility, owner, nsvTarget), Is.False);

            Assert.That(factions.ClearSectorRelation(testMap.MapUid, "NSVHostile", "NSVPlayer"), Is.True);
            Assert.That(Selects(utility, owner, nsvTarget), Is.True);

            Assert.That(factions.SetFaction(targetGrid, "NSVNeutral"), Is.True);
            Assert.That(Selects(utility, owner, nsvTarget), Is.False);

            Assert.That(factions.SetFaction(nsvTarget, "NSVPlayer"), Is.True);
            Assert.That(Selects(utility, owner, nsvTarget), Is.True);

            Assert.That(factions.ClearFaction(nsvTarget), Is.True);
            Assert.That(Selects(utility, owner, nsvTarget), Is.False);

            Assert.That(factions.SetSectorRelation(testMap.MapUid, "NSVHostile", "NSVNeutral", NsvBluespaceFactionRelation.Hostile), Is.True);
            Assert.That(Selects(utility, owner, nsvTarget), Is.True);

            Assert.That(factions.ClearSectorRelation(testMap.MapUid, "NSVHostile", "NSVNeutral"), Is.True);
            Assert.That(Selects(utility, owner, nsvTarget), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RelationsDoNotCrossSectors()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var firstMap = await pair.CreateTestMap();
        var secondMap = await pair.CreateTestMap();
        var entityManager = server.EntMan;
        var factions = server.System<NsvBluespaceFactionSystem>();
        var owner = EntityUid.Invalid;
        var target = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(firstMap.MapUid);
            entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(secondMap.MapUid);
            Assert.That(factions.SetFaction(firstMap.Grid, "NSVHostile"), Is.True);
            Assert.That(factions.SetFaction(secondMap.Grid, "NSVPlayer"), Is.True);

            owner = entityManager.SpawnEntity(OwnerPrototype, firstMap.GridCoords);
            target = entityManager.SpawnEntity(NsvTargetPrototype, secondMap.GridCoords);
        });
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() => Assert.That(factions.IsHostile(owner, target), Is.False));
        await pair.CleanReturnAsync();
    }

    private static bool Selects(NPCUtilitySystem utility, EntityUid owner, EntityUid target)
    {
        var blackboard = new NPCBlackboard();
        blackboard.SetValue(NPCBlackboard.Owner, owner);
        return utility.GetEntities(blackboard, QueryPrototype, false).Entities.ContainsKey(target);
    }
}
