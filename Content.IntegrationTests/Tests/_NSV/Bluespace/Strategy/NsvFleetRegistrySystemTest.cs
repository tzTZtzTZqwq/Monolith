using System.Numerics;
using Content.Server._Mono.FireControl;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Strategy;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Strategy;

/// <summary>
/// P1 born-bound: registering a live grid allocates a ship id, stamps
/// <see cref="NsvSerializableComponent"/> on the grid, and records a matching
/// <see cref="NsvFleetShip"/> whose <see cref="NsvFleetShip.RootGrid"/> and
/// binding generation agree with the stamped component. P2 round-trip: serialize
/// parks the grid on a paused holding map, instantiate brings it back, UID unchanged.
/// </summary>
[TestFixture]
public sealed class NsvFleetRegistrySystemTest
{
    [Test]
    public async Task RegisterShipBindsIdentityToGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var grid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var gridPath = new ResPath("/Maps/_NSV/test.yml");

            var ship = fleets.RegisterShip(grid, gridPath);

            // The allocator is monotonic for the whole server run and the integration pool
            // reuses servers, so assert id shape/consistency, not an absolute count.
            Assert.Multiple(() =>
            {
                Assert.That(ship.Id, Does.StartWith("ship-"));
                Assert.That(ship.GridPath, Is.EqualTo(gridPath));
                Assert.That(ship.RootGrid, Is.EqualTo(grid));
                Assert.That(ship.State, Is.EqualTo(NsvFleetShipState.Live));
            });

            Assert.That(entityManager.TryGetComponent<NsvSerializableComponent>(grid, out var comp), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(comp!.ShipId, Is.EqualTo(ship.Id));
                Assert.That(comp.BindingGeneration, Is.EqualTo(ship.BindingGeneration));
                Assert.That(comp.TargetFloorCount, Is.EqualTo(0));
            });

            Assert.That(fleets.TryGetShip(ship.Id, out var fetched), Is.True);
            Assert.That(fetched, Is.SameAs(ship));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RegisterShipAllocatesDistinctGenerations()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var gridPath = new ResPath("/Maps/_NSV/test.yml");
            var first = fleets.RegisterShip(mapManager.CreateGridEntity(testMap.MapId).Owner, gridPath);
            var second = fleets.RegisterShip(mapManager.CreateGridEntity(testMap.MapId).Owner, gridPath);

            Assert.Multiple(() =>
            {
                Assert.That(second.Id, Is.Not.EqualTo(first.Id));
                Assert.That(second.BindingGeneration, Is.Not.EqualTo(first.BindingGeneration));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SerializeAndInstantiateRoundTripsGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var sectorMapId = testMap.MapId;
            var grid = mapManager.CreateGridEntity(sectorMapId).Owner;
            var ship = fleets.RegisterShip(grid, new ResPath("/Maps/_NSV/test.yml"));

            // Serialize: grid parks on the paused holding map, record goes Available.
            Assert.That(fleets.TrySerializeShip(ship.Id, out var serializeFailure), Is.True, serializeFailure);
            var holdingMapId = fleets.GetOrCreateHoldingMap();
            var holdingMapUid = mapSystem.GetMap(holdingMapId);

            Assert.Multiple(() =>
            {
                Assert.That(ship.State, Is.EqualTo(NsvFleetShipState.Available));
                Assert.That(ship.RootGrid, Is.EqualTo(grid), "grid must not be recreated");
                Assert.That(entityManager.GetComponent<TransformComponent>(grid).MapUid, Is.EqualTo(holdingMapUid));
                Assert.That(mapSystem.IsPaused(holdingMapId), Is.True, "holding map must stay paused");
                Assert.That(entityManager.GetComponent<MetaDataComponent>(grid).EntityPaused, Is.True);
            });

            // Instantiate: grid returns to the sector map, unpauses, record goes Live.
            var destination = new EntityCoordinates(mapSystem.GetMap(sectorMapId), new Vector2(5f, 5f));
            Assert.That(fleets.TryInstantiateShip(ship.Id, destination, out var instantiateFailure), Is.True, instantiateFailure);

            Assert.Multiple(() =>
            {
                Assert.That(ship.State, Is.EqualTo(NsvFleetShipState.Live));
                Assert.That(ship.RootGrid, Is.EqualTo(grid));
                Assert.That(entityManager.GetComponent<TransformComponent>(grid).MapID, Is.EqualTo(sectorMapId));
                Assert.That(entityManager.GetComponent<MetaDataComponent>(grid).EntityPaused, Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SerializeRejectsNonLiveShip()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var grid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var ship = fleets.RegisterShip(grid, new ResPath("/Maps/_NSV/test.yml"));

            Assert.That(fleets.TrySerializeShip(ship.Id, out _), Is.True);
            // Already Available: a second serialize is a no-op failure, not a double move.
            Assert.That(fleets.TrySerializeShip(ship.Id, out var failure), Is.False);
            Assert.That(failure, Is.Not.Null);
            Assert.That(ship.State, Is.EqualTo(NsvFleetShipState.Available));

            Assert.That(fleets.TryInstantiateShip("ship-999", default, out var unknownFailure), Is.False);
            Assert.That(unknownFailure, Is.Not.Null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SnapshotSetsFullComplementAndCompleteness()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapManager.CreateGridEntity(testMap.MapId);
            // 3x3 = 9 floors.
            for (var x = 0; x < 3; x++)
                for (var y = 0; y < 3; y++)
                    mapSystem.SetTile(gridEnt, new Vector2i(x, y), new Tile(1));

            var ship = fleets.RegisterShip(gridEnt.Owner, new ResPath("/Maps/_NSV/test.yml"));

            Assert.Multiple(() =>
            {
                Assert.That(ship.FullComplementFloors, Has.Count.EqualTo(9));
                Assert.That(entityManager.GetComponent<NsvSerializableComponent>(gridEnt.Owner).TargetFloorCount, Is.EqualTo(9));
                Assert.That(fleets.GetCompleteness(ship.Id), Is.EqualTo(1f));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SetFloorCountIsTargetBasedAndIdempotent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapManager.CreateGridEntity(testMap.MapId);
            for (var x = 0; x < 3; x++)
                for (var y = 0; y < 3; y++)
                    mapSystem.SetTile(gridEnt, new Vector2i(x, y), new Tile(1));

            var ship = fleets.RegisterShip(gridEnt.Owner, new ResPath("/Maps/_NSV/test.yml"));

            // Damage down to 4 floors.
            Assert.That(fleets.TrySetFloorCount(ship.Id, 4, out var failure), Is.True, failure);
            Assert.That(CountGridFloors(entityManager, mapSystem, gridEnt.Owner), Is.EqualTo(4));
            Assert.That(fleets.GetCompleteness(ship.Id), Is.EqualTo(4f / 9f).Within(0.001f));

            // Replay the same target: converges, does not stack further damage.
            Assert.That(fleets.TrySetFloorCount(ship.Id, 4, out _), Is.True);
            Assert.That(CountGridFloors(entityManager, mapSystem, gridEnt.Owner), Is.EqualTo(4));

            // Repair back to full: floors restored from the reference set.
            Assert.That(fleets.TrySetFloorCount(ship.Id, 9, out _), Is.True);
            Assert.That(CountGridFloors(entityManager, mapSystem, gridEnt.Owner), Is.EqualTo(9));
            Assert.That(fleets.GetCompleteness(ship.Id), Is.EqualTo(1f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CombatPowerScalesWithCompleteness()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapManager.CreateGridEntity(testMap.MapId);
            // Removing floors can split the grid, reparenting survivors; disable it so this
            // test targets the combat-power formula, not split handling.
            gridEnt.Comp.CanSplit = false;
            for (var x = 0; x < 2; x++)
                for (var y = 0; y < 2; y++)
                    mapSystem.SetTile(gridEnt, new Vector2i(x, y), new Tile(1)); // 4 floors

            // Two turrets anchored on tiles that survive the damage below, so both keep
            // their GridUid and this exercises the combat-power formula (turrets x
            // completeness). Anchoring pins GridUid across the fixture rebuilds SetTile
            // triggers; a turret over a removed floor detaching is a P6 concern. Survivors
            // for a 2x2 grid under the outer-ring comparator are (1,0) and (1,1).
            for (var i = 0; i < 2; i++)
            {
                var indices = new Vector2i(1, i);
                var turret = entityManager.SpawnEntity(null, new EntityCoordinates(gridEnt.Owner, indices));
                entityManager.AddComponent<FireControllableComponent>(turret);
                transform.AnchorEntity(
                    (turret, entityManager.GetComponent<TransformComponent>(turret)),
                    (gridEnt.Owner, gridEnt.Comp),
                    indices);
            }

            var ship = fleets.RegisterShip(gridEnt.Owner, new ResPath("/Maps/_NSV/test.yml"));

            Assert.That(fleets.GetCombatPower(ship.Id), Is.EqualTo(2f), "2 turrets at full integrity");

            // Halve integrity: 2 of 4 floors. Turrets sit on survivors, so both still count.
            Assert.That(fleets.TrySetFloorCount(ship.Id, 2, out var dmgFailure), Is.True, dmgFailure);
            Assert.That(fleets.GetCompleteness(ship.Id), Is.EqualTo(0.5f).Within(0.001f), "completeness after damage");
            Assert.That(fleets.GetCombatPower(ship.Id), Is.EqualTo(2f * 0.5f).Within(0.001f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SerializeBlockedByEncounterParticipant()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var grid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var ship = fleets.RegisterShip(grid, new ResPath("/Maps/_NSV/test.yml"));
            var originMap = entityManager.GetComponent<TransformComponent>(grid).MapUid;

            // An active encounter names this grid as a participant: a live external reference.
            var encounter = entityManager.AddComponent<NsvBluespaceEncounterComponent>(entityManager.SpawnEntity(null, testMap.MapCoords));
            encounter.State = NsvBluespaceEncounterState.Active;
            encounter.Participants.Add(grid);

            Assert.That(fleets.TrySerializeShip(ship.Id, out var failure), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.Not.Null);
                Assert.That(ship.RepresentationFailure, Is.Not.Null);
                Assert.That(ship.State, Is.EqualTo(NsvFleetShipState.Live), "blocked ship must not move");
                // Grid stays put: not deleted, not parked on the holding map.
                Assert.That(entityManager.GetComponent<TransformComponent>(grid).MapUid, Is.EqualTo(originMap));
            });

            // A resolved (Failed) encounter no longer holds a live reference.
            encounter.State = NsvBluespaceEncounterState.Failed;
            Assert.That(fleets.TrySerializeShip(ship.Id, out _), Is.True);
            Assert.That(ship.State, Is.EqualTo(NsvFleetShipState.Available));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SerializeBlockedByEncounterObjective()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var gridEnt = mapManager.CreateGridEntity(testMap.MapId);
            mapSystem.SetTile(gridEnt, new Vector2i(0, 0), new Tile(1));
            var ship = fleets.RegisterShip(gridEnt.Owner, new ResPath("/Maps/_NSV/test.yml"));

            // ObjectiveTarget is an entity ON the grid, not the grid itself: gate resolves via
            // GridUid. Anchor it to a tile so its GridUid pins to this grid.
            var core = entityManager.SpawnEntity(null, new EntityCoordinates(gridEnt.Owner, new Vector2(0.5f, 0.5f)));
            transform.AnchorEntity(
                (core, entityManager.GetComponent<TransformComponent>(core)),
                (gridEnt.Owner, gridEnt.Comp),
                new Vector2i(0, 0));
            Assert.That(entityManager.GetComponent<TransformComponent>(core).GridUid, Is.EqualTo(gridEnt.Owner));

            var encounter = entityManager.AddComponent<NsvBluespaceEncounterComponent>(entityManager.SpawnEntity(null, testMap.MapCoords));
            encounter.State = NsvBluespaceEncounterState.Active;
            encounter.ObjectiveTarget = core;

            Assert.That(fleets.TrySerializeShip(ship.Id, out var failure), Is.False);
            Assert.That(failure, Is.Not.Null);
            Assert.That(ship.State, Is.EqualTo(NsvFleetShipState.Live));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SerializeFleetForcesUndockAndFormationTravelsTogether()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var transform = entityManager.System<SharedTransformSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var gridEntA = mapManager.CreateGridEntity(testMap.MapId);
            var gridEntB = mapManager.CreateGridEntity(testMap.MapId);
            mapSystem.SetTile(gridEntA, new Vector2i(0, 0), new Tile(1));
            mapSystem.SetTile(gridEntB, new Vector2i(0, 0), new Tile(1));
            var gridA = gridEntA.Owner;
            var gridB = gridEntB.Owner;
            var shipA = fleets.RegisterShip(gridA, new ResPath("/Maps/_NSV/a.yml"));
            var shipB = fleets.RegisterShip(gridB, new ResPath("/Maps/_NSV/b.yml"));

            // Wire a mutual docked pair between the two formation grids. UndockDocks enumerates
            // DockingComponents anchored to each grid, so anchor the dock entities to a tile.
            var dockA = entityManager.SpawnEntity(null, new EntityCoordinates(gridA, new Vector2(0.5f, 0.5f)));
            var dockB = entityManager.SpawnEntity(null, new EntityCoordinates(gridB, new Vector2(0.5f, 0.5f)));
            var compA = entityManager.AddComponent<DockingComponent>(dockA);
            var compB = entityManager.AddComponent<DockingComponent>(dockB);
            transform.AnchorEntity((dockA, entityManager.GetComponent<TransformComponent>(dockA)), (gridA, gridEntA.Comp), new Vector2i(0, 0));
            transform.AnchorEntity((dockB, entityManager.GetComponent<TransformComponent>(dockB)), (gridB, gridEntB.Comp), new Vector2i(0, 0));
            compA.DockedWith = dockB;
            compB.DockedWith = dockA;

            // Serialize both together: intra-formation docking is torn down, not treated as
            // a blocking cross-reference, and both ships end up parked.
            Assert.That(fleets.TrySerializeFleet(new[] { shipA.Id, shipB.Id }, out var failure), Is.True, failure);

            var holdingMapUid = mapSystem.GetMap(fleets.GetOrCreateHoldingMap());
            Assert.Multiple(() =>
            {
                Assert.That(compA.DockedWith, Is.Null, "formation dock must be undocked");
                Assert.That(compB.DockedWith, Is.Null);
                Assert.That(shipA.State, Is.EqualTo(NsvFleetShipState.Available));
                Assert.That(shipB.State, Is.EqualTo(NsvFleetShipState.Available));
                Assert.That(entityManager.GetComponent<TransformComponent>(gridA).MapUid, Is.EqualTo(holdingMapUid));
                Assert.That(entityManager.GetComponent<TransformComponent>(gridB).MapUid, Is.EqualTo(holdingMapUid));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RegisterShipRecordsNodeResidency()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var node = new NsvFleetNodeKey("test-starmap", "node-a");
            var grid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var ship = fleets.RegisterShip(grid, new ResPath("/Maps/_NSV/test.yml"), node);

            Assert.Multiple(() =>
            {
                Assert.That(ship.DataNode, Is.EqualTo(node));
                Assert.That(fleets.GetResidentShips(node), Does.Contain(ship.Id));
                Assert.That(fleets.GetResidentShips(new NsvFleetNodeKey("test-starmap", "node-b")), Does.Not.Contain(ship.Id));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WakeGateInstantiatesOnlyResidentShips()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var here = new NsvFleetNodeKey("test-starmap", "here");
            var elsewhere = new NsvFleetNodeKey("test-starmap", "elsewhere");

            var stayGrid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var leaveGrid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var stay = fleets.RegisterShip(stayGrid, new ResPath("/Maps/_NSV/stay.yml"), here);
            var leave = fleets.RegisterShip(leaveGrid, new ResPath("/Maps/_NSV/leave.yml"), here);

            // Batch-serialize the node: both ships park on the holding map, go Available.
            fleets.SerializeSectorFleets(here, out var serializeFailures);
            Assert.That(serializeFailures, Is.Empty);
            Assert.That(stay.State, Is.EqualTo(NsvFleetShipState.Available));
            Assert.That(leave.State, Is.EqualTo(NsvFleetShipState.Available));

            // One ship travels away in the data layer: its residency moves off this node.
            Assert.That(fleets.TryMoveShipToNode(leave.Id, elsewhere, out var moveFailure), Is.True, moveFailure);
            Assert.That(fleets.GetResidentShips(here), Does.Not.Contain(leave.Id));

            // Wake regeneration gate: only the ship still resident here comes back.
            var destination = new EntityCoordinates(mapSystem.GetMap(testMap.MapId), new Vector2(3f, 3f));
            fleets.InstantiateNodeFleets(here, destination, out var instantiateFailures);
            Assert.That(instantiateFailures, Is.Empty);

            Assert.Multiple(() =>
            {
                Assert.That(stay.State, Is.EqualTo(NsvFleetShipState.Live), "resident ship regenerated");
                Assert.That(entityManager.GetComponent<TransformComponent>(stayGrid).MapID, Is.EqualTo(testMap.MapId));
                Assert.That(leave.State, Is.EqualTo(NsvFleetShipState.Available), "travelled-away ship not regenerated");
                Assert.That(entityManager.GetComponent<TransformComponent>(leaveGrid).MapID, Is.Not.EqualTo(testMap.MapId));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AbstractDamageAppliedAtInstantiateAndReplayConverges()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<SharedMapSystem>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var node = new NsvFleetNodeKey("test-starmap", "combat-node");
            var gridEnt = mapManager.CreateGridEntity(testMap.MapId);
            gridEnt.Comp.CanSplit = false;
            for (var x = 0; x < 3; x++)
                for (var y = 0; y < 3; y++)
                    mapSystem.SetTile(gridEnt, new Vector2i(x, y), new Tile(1)); // 9 floors

            var ship = fleets.RegisterShip(gridEnt.Owner, new ResPath("/Maps/_NSV/test.yml"), node);

            // Serialize: real floor count is photographed into the data record.
            Assert.That(fleets.TrySerializeShip(ship.Id, out var serFail), Is.True, serFail);
            Assert.That(ship.DataTargetFloorCount, Is.EqualTo(9));

            // Abstract data-state combat removes 5 floors: only the frozen number changes.
            Assert.That(fleets.TryApplyAbstractDamage(ship.Id, 5, out var dmgFail), Is.True, dmgFail);
            Assert.That(ship.DataTargetFloorCount, Is.EqualTo(4));
            Assert.That(CountGridFloors(entityManager, mapSystem, gridEnt.Owner), Is.EqualTo(9), "parked grid untouched by abstract combat");

            // Instantiate: floors deleted down to the data target.
            var destination = new EntityCoordinates(mapSystem.GetMap(testMap.MapId), new Vector2(4f, 4f));
            Assert.That(fleets.TryInstantiateShip(ship.Id, destination, out var instFail), Is.True, instFail);
            Assert.That(CountGridFloors(entityManager, mapSystem, gridEnt.Owner), Is.EqualTo(4));

            // Replay serialize -> instantiate: converges to the same 4 floors, no further loss.
            Assert.That(fleets.TrySerializeShip(ship.Id, out _), Is.True);
            Assert.That(ship.DataTargetFloorCount, Is.EqualTo(4), "re-photographed from real floor count");
            Assert.That(fleets.TryInstantiateShip(ship.Id, destination, out _), Is.True);
            Assert.That(CountGridFloors(entityManager, mapSystem, gridEnt.Owner), Is.EqualTo(4));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CanResolveAbstractCombatGate()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var fleets = entityManager.System<NsvFleetRegistrySystem>();

        await server.WaitAssertion(() =>
        {
            var node = new NsvFleetNodeKey("test-starmap", "gate-node");
            var grid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var ship = fleets.RegisterShip(grid, new ResPath("/Maps/_NSV/test.yml"), node);

            // A Live resident ship blocks abstract combat: its authority is the grid.
            Assert.That(fleets.CanResolveAbstractCombat(node), Is.False, "Live ship present");

            // Serialize it to the data state: now purely data, gate opens.
            Assert.That(fleets.TrySerializeShip(ship.Id, out var serFail), Is.True, serFail);
            Assert.That(fleets.CanResolveAbstractCombat(node), Is.True, "all resident ships are data");

            // An empty node (no ships, no encounter) also resolves.
            Assert.That(fleets.CanResolveAbstractCombat(new NsvFleetNodeKey("test-starmap", "empty")), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    private static int CountGridFloors(IEntityManager entityManager, SharedMapSystem mapSystem, EntityUid gridUid)
    {
        var grid = entityManager.GetComponent<MapGridComponent>(gridUid);
        var count = 0;
        var enumerator = mapSystem.GetAllTilesEnumerator(gridUid, grid);
        while (enumerator.MoveNext(out _))
            count++;

        return count;
    }
}
