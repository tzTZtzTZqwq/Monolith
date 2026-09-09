using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared.CCVar;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Encounters;

[TestFixture]
public sealed class NsvBluespaceEncounterSystemTest
{
    [Test]
    public async Task PatrolContractCompletesAndGatesReturn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var firstShuttle = await pair.CreateTestMap();
        var secondShuttle = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var encounters = entityManager.System<NsvBluespaceEncounterSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var travel = entityManager.System<NsvBluespaceSectorTravelSystem>();
        var config = server.CfgMan;
        var startupTime = config.GetCVar(CCVars.FTLStartupTime);
        var travelTime = config.GetCVar(CCVars.FTLTravelTime);
        var arrivalTime = config.GetCVar(CCVars.FTLArrivalTime);
        var firstShuttleUid = firstShuttle.Grid.Owner;
        var secondShuttleUid = secondShuttle.Grid.Owner;
        var sectorMap = EntityUid.Invalid;
        var controllerUid = EntityUid.Invalid;
        var targetCore = EntityUid.Invalid;

        config.SetCVar(CCVars.FTLStartupTime, 0.1f);
        config.SetCVar(CCVars.FTLTravelTime, 0.3f);
        config.SetCVar(CCVars.FTLArrivalTime, 0.1f);

        try
        {
            await server.WaitPost(() =>
            {
                Assert.That(
                    travel.TryEnter(firstShuttleUid, "NSVBluespaceTestSector", 42, out var reason),
                    Is.True,
                    reason);
            });

            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(
                    entityManager.GetComponent<TransformComponent>(firstShuttleUid).MapUid!.Value);
                sectorMap = firstShuttle.MapUid == EntityUid.Invalid
                    ? EntityUid.Invalid
                    : entityManager.GetComponent<TransformComponent>(firstShuttleUid).MapUid!.Value;
                controllerUid = sector.EncounterController;
                var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid);
                targetCore = encounter.ObjectiveTarget;

                Assert.Multiple(() =>
                {
                    Assert.That(controllerUid, Is.Not.EqualTo(EntityUid.Invalid));
                    Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
                    Assert.That(encounter.Participants, Does.Contain(firstShuttleUid));
                    Assert.That(entityManager.GetComponent<NsvBluespaceEncounterMemberComponent>(firstShuttleUid).Controller, Is.EqualTo(controllerUid));
                    Assert.That(entityManager.GetComponent<NsvBluespaceEncounterMemberComponent>(targetCore).Role, Is.EqualTo(NsvBluespaceEncounterMemberRole.ObjectiveTarget));
                    Assert.That(entityManager.GetComponent<NsvEncounterPatrolCoreObjectiveComponent>(targetCore).Controller, Is.EqualTo(controllerUid));
                    Assert.That(sector.RelationOverrides["NSVFederal"]["NSVHostile"], Is.EqualTo(NsvBluespaceFactionRelation.Neutral));
                    Assert.That(sector.RelationOverrides["NSVHostile"]["NSVFederal"], Is.EqualTo(NsvBluespaceFactionRelation.Neutral));
                });
            });

            await server.WaitPost(() =>
            {
                Assert.That(
                    travel.TryEnter(secondShuttleUid, "NSVBluespaceTestSector", 42, out var reason),
                    Is.True,
                    reason);
            });

            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid);
                Assert.Multiple(() =>
                {
                    Assert.That(sector.EncounterController, Is.EqualTo(controllerUid));
                    Assert.That(encounter.Participants, Is.EquivalentTo(new[] { firstShuttleUid, secondShuttleUid }));
                    Assert.That(entityManager.GetComponent<NsvBluespaceEncounterMemberComponent>(secondShuttleUid).Controller, Is.EqualTo(controllerUid));
                });
            });

            await pair.RunSeconds(11);
            await server.WaitPost(() =>
            {
                Assert.That(travel.TryTravel(firstShuttleUid, default, 42, out var reason), Is.False);
                Assert.That(reason, Is.EqualTo("The encounter objective is not complete."));
            });

            await server.WaitPost(() => entityManager.DeleteEntity(targetCore));
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() =>
            {
                var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid);
                Assert.Multiple(() =>
                {
                    Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.ExtractionOpen));
                    Assert.That(encounters.TryCompleteObjective(controllerUid, targetCore), Is.False);
                });
            });

            await server.WaitPost(() =>
            {
                Assert.That(travel.TryTravel(firstShuttleUid, default, 42, out var reason), Is.True, reason);
                Assert.That(
                    entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid).ReturnedParticipants,
                    Does.Not.Contain(firstShuttleUid));
            });

            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid);
                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                Assert.Multiple(() =>
                {
                    Assert.That(entityManager.GetComponent<TransformComponent>(firstShuttleUid).MapUid, Is.EqualTo(firstShuttle.MapUid));
                    Assert.That(encounter.PendingReturns, Does.Not.Contain(firstShuttleUid));
                    Assert.That(encounter.ReturnedParticipants, Does.Contain(firstShuttleUid));
                    Assert.That(sector.ForeignGrids, Does.Not.Contain(firstShuttleUid));
                });
            });

            // The pool reuses this server process for later tests; the second shuttle
            // must leave and the sector must be disposed so the template-keyed cache
            // does not leak an instance whose patrol core is already deleted.
            await server.WaitPost(() =>
                Assert.That(travel.TryTravel(secondShuttleUid, default, 42, out var reason), Is.True, reason));

            await pair.RunSeconds(1);
            await server.WaitPost(() =>
            {
                Assert.That(entityManager.GetComponent<TransformComponent>(secondShuttleUid).MapUid, Is.EqualTo(secondShuttle.MapUid));
                Assert.That(
                    sectors.TryDispose((sectorMap, entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap))),
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

    [Test]
    public async Task DisposingSectorDoesNotCompleteActivePatrolContract()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var shuttle = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var encounters = entityManager.System<NsvBluespaceEncounterSystem>();
        var patrolContracts = entityManager.System<NsvBluespacePatrolContractSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var sectorMap = EntityUid.Invalid;
        var controllerUid = EntityUid.Invalid;
        var targetCore = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceTestSector", 42, out sectorMap, out var reason),
                Is.True,
                reason);

            patrolContracts.OnSectorArrival(sectorMap, shuttle.Grid.Owner);
            var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
            controllerUid = sector.EncounterController;
            var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid);
            targetCore = encounter.ObjectiveTarget;

            Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
            Assert.That(sectors.TryDispose((sectorMap, sector)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(sector.EncounterController, Is.EqualTo(EntityUid.Invalid));
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Disposed));
                Assert.That(encounters.TryCompleteObjective(controllerUid, targetCore), Is.False);
                Assert.That(sector.RelationOverrides.ContainsKey("NSVFederal"), Is.False);
            });
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(entityManager.EntityExists(sectorMap), Is.False);
            Assert.That(entityManager.EntityExists(controllerUid), Is.False);
        });

        await pair.CleanReturnAsync();
    }

}
