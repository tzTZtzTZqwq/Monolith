using System.Collections.Generic;
using System.Numerics;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.GameRule;
using Content.Server.GameTicking;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Encounters;

[TestFixture]
public sealed class NsvClearSystemContractSystemTest
{
    /// <summary>
    /// A ClearSystem contract snapshots every hostile AI core at activation; destroying all of them
    /// opens extraction and awards the node reward, while an intermediate kill keeps it Active. Built
    /// manually with two hostile grids because no sector template spawns more than one hostile ship.
    /// </summary>
    [Test]
    public async Task ClearSystemCompletesWhenEveryCoreDestroyed()
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
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();
        var encounters = entityManager.System<NsvBluespaceEncounterSystem>();
        var gameTicker = entityManager.System<GameTicker>();
        var campaign = entityManager.System<NsvCampaignRuleSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(0));

            // beta-1 is the PiratePatrol node carrying the ClearSystem/Hold pool; its reward is 2.
            var sector = entityManager.AddComponent<NsvBluespaceSectorInstanceComponent>(sectorMap.MapUid);
            sector.State = NsvBluespaceSectorState.Ready;
            sector.MapId = sectorMap.MapId;
            sector.StarmapId = "NSVBluespaceStrategicMap";
            sector.NodeId = "beta-1";
            sector.EncounterDefinitionId = "NSVClearSystemContract";

            var cores = new List<EntityUid>();
            for (var i = 0; i < 2; i++)
            {
                var gridEnt = mapManager.CreateGridEntity(sectorMap.MapId);
                mapSystem.SetTile(gridEnt, new Vector2i(0, 0), new Tile(1));
                Assert.That(factions.SetFaction(gridEnt.Owner, "NSVHostile"), Is.True);
                sector.OwnedGrids.Add(gridEnt.Owner);

                // Anchor the core to a tile so its GridUid pins to this grid in the tickless harness.
                var core = entityManager.SpawnEntity(null, new EntityCoordinates(gridEnt.Owner, new Vector2(0.5f, 0.5f)));
                transform.AnchorEntity(
                    (core, entityManager.GetComponent<TransformComponent>(core)),
                    (gridEnt.Owner, gridEnt.Comp),
                    new Vector2i(0, 0));
                entityManager.AddComponent<NsvBluespaceShipAiCoreComponent>(core);
                cores.Add(core);
            }

            encounters.DispatchArrival(sectorMap.MapUid, shuttleMap.Grid.Owner);

            var controllerUid = sector.EncounterController;
            Assert.That(controllerUid, Is.Not.EqualTo(EntityUid.Invalid));
            var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid);
            var objective = entityManager.GetComponent<NsvEncounterClearObjectiveComponent>(controllerUid);

            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
                Assert.That(objective.RemainingTargets, Has.Count.EqualTo(2));
            });

            // First kill: one target remains, so the hold on extraction stands and no reward lands.
            entityManager.DeleteEntity(cores[0]);
            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
                Assert.That(objective.RemainingTargets, Has.Count.EqualTo(1));
                Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(0),
                    "no reward until the final core dies");
            });

            // Last kill: the set empties, extraction opens, and the node reward is awarded once.
            entityManager.DeleteEntity(cores[1]);
            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.ExtractionOpen));
                Assert.That(objective.RemainingTargets, Is.Empty);
                Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(2),
                    "clearing every core awards the node reward");
            });
        });

        await pair.CleanReturnAsync();
    }
}
