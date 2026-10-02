using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.GameRule;
using Content.Server.GameTicking;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Encounters;

[TestFixture]
public sealed class NsvHoldContractSystemTest
{
    /// <summary>
    /// A Hold contract keeps extraction gated (<see cref="NsvBluespaceEncounterSystem.CanReturn"/>
    /// false) for the whole Active window, then completes and awards the node reward once its timer
    /// elapses. CheckHolds is driven directly so the test needn't pump a real hold duration of ticks.
    /// </summary>
    [Test]
    public async Task HoldGatesExtractionUntilTimerElapses()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sectorMap = await pair.CreateTestMap();
        var shuttleMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var encounters = entityManager.System<NsvBluespaceEncounterSystem>();
        var hold = entityManager.System<NsvHoldContractSystem>();
        var gameTicker = entityManager.System<GameTicker>();
        var campaign = entityManager.System<NsvCampaignRuleSystem>();
        var shuttleUid = shuttleMap.Grid.Owner;

        await server.WaitAssertion(() =>
        {
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(0));

            // beta-1 carries the Hold pool; its reward is 2.
            var sector = entityManager.AddComponent<NsvBluespaceSectorInstanceComponent>(sectorMap.MapUid);
            sector.State = NsvBluespaceSectorState.Ready;
            sector.MapId = sectorMap.MapId;
            sector.StarmapId = "NSVBluespaceStrategicMap";
            sector.NodeId = "beta-1";
            sector.EncounterDefinitionId = "NSVHoldContract";

            encounters.DispatchArrival(sectorMap.MapUid, shuttleUid);

            var controllerUid = sector.EncounterController;
            Assert.That(controllerUid, Is.Not.EqualTo(EntityUid.Invalid));
            var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid);
            var objective = entityManager.GetComponent<NsvEncounterHoldObjectiveComponent>(controllerUid);

            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
                Assert.That(objective.EndTime, Is.GreaterThan(timing.CurTime), "the hold timer runs into the future");
                // Active hold blocks FTL extraction: that gate is the entire tension of the encounter.
                Assert.That(encounters.CanReturn(sectorMap.MapUid, shuttleUid, out _), Is.False);
            });

            // Timer not yet elapsed: a check is a no-op, nothing completes, no reward.
            hold.CheckHolds();
            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
                Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(0));
            });

            // Advance the deadline into the past and re-check: the hold is satisfied.
            objective.EndTime = timing.CurTime - TimeSpan.FromSeconds(1);
            hold.CheckHolds();
            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.ExtractionOpen));
                Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(2),
                    "surviving the hold awards the node reward");
                Assert.That(encounters.CanReturn(sectorMap.MapUid, shuttleUid, out _), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }
}
