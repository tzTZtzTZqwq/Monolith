using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared.CCVar;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Sectors;

[TestFixture]
public sealed class NsvBluespaceStarmapSystemTest
{
    private const string StarmapId = "NSVBluespaceStrategicMap";
    private const string FixtureStarmapId = "NSVBluespaceTestStarmap";

    [Test]
    public async Task NodeIdentityKeepsSameTemplateSectorsSeparate()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var alphaMap = EntityUid.Invalid;
        var betaMap = EntityUid.Invalid;

        await server.WaitAssertion(() =>
        {
            Assert.That(sectors.TryGetOrCreateNode(FixtureStarmapId, "SharedAlpha", out alphaMap, out var alphaFailure), Is.True, alphaFailure);
            Assert.That(sectors.TryGetOrCreateNode(FixtureStarmapId, "SharedBeta", out betaMap, out var betaFailure), Is.True, betaFailure);
            Assert.That(sectors.TryGetOrCreateNode(FixtureStarmapId, "SharedAlpha", out var repeatedAlphaMap, out var repeatedFailure), Is.True, repeatedFailure);

            var alpha = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(alphaMap);
            var beta = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(betaMap);
            Assert.Multiple(() =>
            {
                Assert.That(alphaMap, Is.Not.EqualTo(betaMap));
                Assert.That(repeatedAlphaMap, Is.EqualTo(alphaMap));
                Assert.That(alpha.TemplateId, Is.EqualTo(beta.TemplateId));
                Assert.That(alpha.StarmapId.ToString(), Is.EqualTo(FixtureStarmapId));
                Assert.That(beta.StarmapId.ToString(), Is.EqualTo(FixtureStarmapId));
                Assert.That(alpha.NodeId, Is.EqualTo("SharedAlpha"));
                Assert.That(beta.NodeId, Is.EqualTo("SharedBeta"));
                Assert.That(alpha.Seed, Is.Not.EqualTo(beta.Seed));
            });

            Assert.That(sectors.TryDispose((alphaMap, alpha)), Is.True);
            Assert.That(sectors.TryDispose((betaMap, beta)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(entityManager.EntityExists(alphaMap), Is.False);
            Assert.That(entityManager.EntityExists(betaMap), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StarmapTravelRequiresEdgeAndEncounterExtraction()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var shuttle = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var travel = entityManager.System<NsvBluespaceSectorTravelSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var config = server.CfgMan;
        var startupTime = config.GetCVar(CCVars.FTLStartupTime);
        var travelTime = config.GetCVar(CCVars.FTLTravelTime);
        var arrivalTime = config.GetCVar(CCVars.FTLArrivalTime);
        var shuttleUid = shuttle.Grid.Owner;
        var pirateMap = EntityUid.Invalid;
        var asteroidMapUid = EntityUid.Invalid;

        config.SetCVar(CCVars.FTLStartupTime, 0.1f);
        config.SetCVar(CCVars.FTLTravelTime, 0.3f);
        config.SetCVar(CCVars.FTLArrivalTime, 0.1f);

        try
        {
            await server.WaitPost(() =>
                Assert.That(travel.TryTravelToNode(shuttleUid, StarmapId, "alpha-3", out var reason), Is.True, reason));
            await pair.RunSeconds(11);
            await server.WaitAssertion(() =>
            {
                pirateMap = entityManager.GetComponent<TransformComponent>(shuttleUid).MapUid!.Value;
                var pirate = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(pirateMap);
                var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(pirate.EncounterController);
                Assert.Multiple(() =>
                {
                    Assert.That(pirate.NodeId, Is.EqualTo("alpha-3"));
                    Assert.That(pirate.EncounterDefinitionId, Is.EqualTo("NSVPatrolContract"));
                    Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
                    Assert.That(encounter.Participants, Does.Contain(shuttleUid));
                });
            });

            await server.WaitPost(() =>
            {
                Assert.That(travel.TryTravelToNode(shuttleUid, StarmapId, "delta-1", out var edgeReason), Is.False);
                Assert.That(edgeReason, Is.EqualTo("The selected starmap node is not connected to the current node."));
                Assert.That(travel.TryTravelToNode(shuttleUid, StarmapId, "beta-1", out var extractionReason), Is.False);
                Assert.That(extractionReason, Is.EqualTo("The encounter objective is not complete."));
                Assert.That(travel.TryReturnToDeparture(shuttleUid, out var returnReason), Is.False);
                Assert.That(returnReason, Is.EqualTo("The encounter objective is not complete."));

                var pirate = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(pirateMap);
                var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(pirate.EncounterController);
                entityManager.DeleteEntity(encounter.ObjectiveTarget);
            });
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() =>
            {
                var pirate = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(pirateMap);
                Assert.That(
                    entityManager.GetComponent<NsvBluespaceEncounterComponent>(pirate.EncounterController).State,
                    Is.EqualTo(NsvBluespaceEncounterState.ExtractionOpen));
            });

            await server.WaitPost(() =>
                Assert.That(travel.TryTravelToNode(shuttleUid, StarmapId, "beta-1", out var reason), Is.True, reason));
            await pair.RunSeconds(11);
            await server.WaitAssertion(() =>
            {
                asteroidMapUid = entityManager.GetComponent<TransformComponent>(shuttleUid).MapUid!.Value;
                var asteroid = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(asteroidMapUid);
                Assert.Multiple(() =>
                {
                    Assert.That(asteroid.NodeId, Is.EqualTo("beta-1"));
                    Assert.That(asteroid.TemplateId, Is.EqualTo("NSVBluespaceHunterSector"));
                    Assert.That(asteroid.EncounterController, Is.EqualTo(EntityUid.Invalid));
                    Assert.That(asteroid.ForeignGrids, Does.Contain(shuttleUid));
                    Assert.That(asteroid.ReturnDestinations, Does.ContainKey(shuttleUid));
                });
            });

            await server.WaitPost(() =>
                Assert.That(travel.TryReturnToDeparture(shuttleUid, out var reason), Is.True, reason));
            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                Assert.That(entityManager.GetComponent<TransformComponent>(shuttleUid).MapUid, Is.EqualTo(shuttle.MapUid));
                Assert.That(entityManager.HasComponent<NsvBluespaceFactionComponent>(shuttleUid), Is.False);
            });

            // The pool reuses this server process; dispose both node sectors so the
            // node-keyed cache does not leak instances whose encounter state was
            // mutated by this test.
            await server.WaitPost(() =>
            {
                Assert.That(
                    sectors.TryDispose((pirateMap, entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(pirateMap))),
                    Is.True);
                Assert.That(
                    sectors.TryDispose((asteroidMapUid, entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(asteroidMapUid))),
                    Is.True);
            });
        }
        finally
        {
            config.SetCVar(CCVars.FTLStartupTime, startupTime);
            config.SetCVar(CCVars.FTLTravelTime, travelTime);
            config.SetCVar(CCVars.FTLArrivalTime, arrivalTime);
        }

        await pair.CleanReturnAsync();
    }
}
