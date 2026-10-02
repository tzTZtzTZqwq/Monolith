using System.Collections.Generic;
using System.Numerics;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Bluespace.Strategy;
using Content.Server._NSV.GameRule;
using Content.Server.GameTicking;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Encounters;

[TestFixture]
public sealed class NsvClearSystemContractSystemTest
{
    /// <summary>
    /// A ClearSystem contract snapshots every hostile AI core at activation; destroying all of them
    /// opens extraction and awards the node reward, while an intermediate kill keeps it Active. The
    /// console progress readout tracks the remaining count. Built manually with two hostile grids
    /// because no sector template spawns more than one hostile ship.
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
        var encounters = entityManager.System<NsvBluespaceEncounterSystem>();
        var gameTicker = entityManager.System<GameTicker>();
        var campaign = entityManager.System<NsvCampaignRuleSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(0));

            var sector = SetUpSector(server, sectorMap, hostileShips: 2, out var cores);
            encounters.DispatchArrival(sectorMap.MapUid, shuttleMap.Grid.Owner);

            var controllerUid = sector.EncounterController;
            Assert.That(controllerUid, Is.Not.EqualTo(EntityUid.Invalid));
            var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(controllerUid);
            var objective = entityManager.GetComponent<NsvEncounterClearObjectiveComponent>(controllerUid);

            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
                Assert.That(objective.RemainingTargets, Has.Count.EqualTo(2));
                Assert.That(encounters.GetProgress(controllerUid)?.Count, Is.EqualTo(2));
            });

            // First kill: one target remains, so extraction stays locked and no reward lands.
            entityManager.DeleteEntity(cores[0]);
            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));
                Assert.That(objective.RemainingTargets, Has.Count.EqualTo(1));
                Assert.That(encounters.GetProgress(controllerUid)?.Count, Is.EqualTo(1));
                Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(0),
                    "no reward until the final core dies");
            });

            // Last kill: the set empties, extraction opens, and the node reward is awarded once.
            entityManager.DeleteEntity(cores[1]);
            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.ExtractionOpen));
                Assert.That(objective.RemainingTargets, Is.Empty);
                Assert.That(encounters.GetProgress(controllerUid), Is.Null, "no progress once complete");
                Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(2),
                    "clearing every core awards the node reward");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A ClearSystem contract in a node with no hostile cores has nothing to clear: activation fails
    /// and the encounter is Failed (extraction open) rather than stuck Active with an empty target set.
    /// </summary>
    [Test]
    public async Task ClearSystemWithoutTargetsFails()
    {
        // Dirty: the hand-built sector and its encounter controller aren't torn down by the pool.
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var sectorMap = await pair.CreateTestMap();
        var shuttleMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var encounters = entityManager.System<NsvBluespaceEncounterSystem>();

        await server.WaitAssertion(() =>
        {
            var sector = SetUpSector(server, sectorMap, hostileShips: 0, out _);
            encounters.DispatchArrival(sectorMap.MapUid, shuttleMap.Grid.Owner);

            var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(sector.EncounterController);
            Assert.Multiple(() =>
            {
                Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Failed));
                Assert.That(encounters.CanReturn(sectorMap.MapUid, shuttleMap.Grid.Owner, out _), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A grid carrying a live ClearSystem target must not be parked by the fleet registry: ClearSystem
    /// doesn't use the single ObjectiveTarget field, so the registry has to recognise the target via
    /// its member role. Parking it (sector sleep or strategic FTL) would leave the target unreachable
    /// and the crew's extraction locked forever.
    /// </summary>
    [Test]
    public async Task RegistryRefusesToParkClearSystemTarget()
    {
        // Dirty: the hand-built sector and its encounter controller aren't torn down by the pool.
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var sectorMap = await pair.CreateTestMap();
        var shuttleMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var encounters = entityManager.System<NsvBluespaceEncounterSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var sector = SetUpSector(server, sectorMap, hostileShips: 1, out var cores);
            var targetGrid = entityManager.GetComponent<TransformComponent>(cores[0]).GridUid!.Value;
            var ship = fleets.RegisterShip(targetGrid, new ResPath("/Maps/_NSV/test.yml"));

            encounters.DispatchArrival(sectorMap.MapUid, shuttleMap.Grid.Owner);
            var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(sector.EncounterController);
            Assert.That(encounter.State, Is.EqualTo(NsvBluespaceEncounterState.Active));

            Assert.That(fleets.TrySerializeShip(ship.Id, out var failure), Is.False,
                "a live ClearSystem target's grid must stay in the live world");
            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.Not.Null);
                Assert.That(ship.State, Is.EqualTo(NsvFleetShipState.Live));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Turns a test map into a Ready ClearSystem sector at beta-1 (reward 2) holding
    /// <paramref name="hostileShips"/> NSVHostile grids, each with one AI core anchored to a tile so its
    /// GridUid pins to the grid in the tickless harness. Must run inside a server Wait block.
    /// </summary>
    private static NsvBluespaceSectorInstanceComponent SetUpSector(
        RobustIntegrationTest.ServerIntegrationInstance server,
        TestMapData sectorMap,
        int hostileShips,
        out List<EntityUid> cores)
    {
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();

        var sector = entityManager.AddComponent<NsvBluespaceSectorInstanceComponent>(sectorMap.MapUid);
        sector.State = NsvBluespaceSectorState.Ready;
        sector.MapId = sectorMap.MapId;
        sector.StarmapId = "NSVBluespaceStrategicMap";
        sector.NodeId = "beta-1";
        sector.EncounterDefinitionId = "NSVClearSystemContract";

        cores = new List<EntityUid>();
        for (var i = 0; i < hostileShips; i++)
        {
            var gridEnt = mapManager.CreateGridEntity(sectorMap.MapId);
            mapSystem.SetTile(gridEnt, new Vector2i(0, 0), new Tile(1));
            Assert.That(factions.SetFaction(gridEnt.Owner, "NSVHostile"), Is.True);
            sector.OwnedGrids.Add(gridEnt.Owner);

            var core = entityManager.SpawnEntity(null, new EntityCoordinates(gridEnt.Owner, new Vector2(0.5f, 0.5f)));
            transform.AnchorEntity(
                (core, entityManager.GetComponent<TransformComponent>(core)),
                (gridEnt.Owner, gridEnt.Comp),
                new Vector2i(0, 0));
            entityManager.AddComponent<NsvBluespaceShipAiCoreComponent>(core);
            cores.Add(core);
        }

        return sector;
    }
}
