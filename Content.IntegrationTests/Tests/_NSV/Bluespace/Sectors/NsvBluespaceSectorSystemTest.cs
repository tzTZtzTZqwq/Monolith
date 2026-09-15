using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Mono.FireControl;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Bluespace.Sectors.Generators;
using Content.Server._NSV.NPC.HTN;
using Content.Server.NPC.HTN;
using Content.Server.Spawners.Components;
using Content.Shared.CCVar;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Sectors;

[TestFixture]
public sealed class NsvBluespaceSectorSystemTest
{
    [Test]
    public async Task EnablesFactionsOnOrdinaryMap()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();

        await server.WaitAssertion(() =>
        {
            var playerGrid = testMap.Grid.Owner;
            var hostileGrid = mapManager.CreateGridEntity(testMap.MapId).Owner;
            var federalGrid = mapManager.CreateGridEntity(testMap.MapId).Owner;

            Assert.That(factions.SetFaction(playerGrid, "NSVPlayer"), Is.False);
            Assert.That(factions.EnableFactionMap(playerGrid), Is.True);
            Assert.That(factions.SetFaction(playerGrid, "NSVPlayer"), Is.True);
            Assert.That(factions.SetFaction(hostileGrid, "NSVHostile"), Is.True);
            Assert.That(factions.SetFaction(federalGrid, "NSVFederal"), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(entityManager.HasComponent<NsvBluespaceFactionMapComponent>(testMap.MapUid), Is.True);
                Assert.That(entityManager.HasComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid), Is.False);
                Assert.That(factions.IsHostile(playerGrid, hostileGrid), Is.True);
                Assert.That(factions.IsHostile(hostileGrid, playerGrid), Is.True);
                Assert.That(factions.IsHostile(federalGrid, hostileGrid), Is.True);
                Assert.That(factions.IsHostile(hostileGrid, federalGrid), Is.True);
                Assert.That(factions.IsHostile(playerGrid, federalGrid), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CreatesAndDisposesModularSector()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;

        await server.WaitAssertion(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceTestSector", 42, out mapUid, out var failure),
                Is.True,
                failure ?? "No sector creation failure reason was returned.");

            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            var hostileGrids = instance.OwnedGrids.Where(grid =>
                entityManager.GetComponent<NsvBluespaceFactionComponent>(grid).Faction.ToString() == "NSVHostile").ToHashSet();
            var nsvAiCores = 0;
            var nsvHtns = new List<HTNComponent>();
            var monoAiCores = 0;
            var coreQuery = entityManager.EntityQueryEnumerator<NsvBluespaceShipAiCoreComponent, NsvShipTargetComponent, HTNComponent, TransformComponent>();
            while (coreQuery.MoveNext(out _, out _, out _, out var htn, out var coreTransform))
            {
                if (coreTransform.GridUid is { } gridUid && hostileGrids.Contains(gridUid))
                {
                    htn.PlanAccumulator = 42f;
                    nsvHtns.Add(htn);
                    nsvAiCores++;
                }
            }

            var entityQuery = entityManager.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (entityQuery.MoveNext(out _, out var metadata, out var entityTransform))
            {
                if (entityTransform.GridUid is { } gridUid &&
                    hostileGrids.Contains(gridUid) &&
                    metadata.EntityPrototype?.ID == "NpcStationAiAttackerStaticSmart")
                {
                    monoAiCores++;
                }
            }

            var oldCoreMarkers = 0;
            var spawnerQuery = entityManager.EntityQueryEnumerator<ConditionalSpawnerComponent, TransformComponent>();
            while (spawnerQuery.MoveNext(out _, out var spawner, out var spawnerTransform))
            {
                if (spawnerTransform.GridUid is { } gridUid &&
                    hostileGrids.Contains(gridUid) &&
                    spawner.Prototypes.Any(prototype => prototype.Id == "NpcStationAiAttackerStaticSmart"))
                {
                    oldCoreMarkers++;
                }
            }

            Assert.That(instance.RelationOverrides["NSVHostile"]["NSVPlayer"], Is.EqualTo(NsvBluespaceFactionRelation.Hostile));
            Assert.That(factions.SetSectorRelation(mapUid, "NSVHostile", "NSVPlayer", NsvBluespaceFactionRelation.Neutral), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(instance.OwnedGrids.Count, Is.GreaterThanOrEqualTo(2));
                Assert.That(instance.OwnedEntities, Is.Not.Empty);
                Assert.That(instance.OwnedGrids.All(entityManager.HasComponent<MapGridComponent>), Is.True);
                Assert.That(instance.OwnedGrids.All(entityManager.HasComponent<NsvBluespaceFactionComponent>), Is.True);
                Assert.That(instance.OwnedGrids.Any(grid =>
                    entityManager.GetComponent<NsvBluespaceFactionComponent>(grid).Faction.ToString() == "NSVNeutral"), Is.True);
                Assert.That(instance.OwnedGrids.Any(grid =>
                    entityManager.GetComponent<NsvBluespaceFactionComponent>(grid).Faction.ToString() == "NSVHostile"), Is.True);
                Assert.That(nsvAiCores, Is.EqualTo(hostileGrids.Count));
                Assert.That(nsvHtns.All(htn => htn.PlanAccumulator == 0f), Is.True);
                Assert.That(instance.RelationOverrides["NSVHostile"]["NSVPlayer"], Is.EqualTo(NsvBluespaceFactionRelation.Neutral));
                Assert.That(monoAiCores, Is.Zero);
                Assert.That(oldCoreMarkers, Is.Zero);
            });

            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CreatesHunterSectorWithBroadsideCore()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;

        await server.WaitAssertion(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceHunterSector", 42, out mapUid, out var failure),
                Is.True,
                failure ?? "No sector creation failure reason was returned.");

            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            var hostileGrids = instance.OwnedGrids.Where(grid =>
                entityManager.GetComponent<NsvBluespaceFactionComponent>(grid).Faction.ToString() == "NSVHostile").ToHashSet();

            var broadsideCores = new List<EntityUid>();
            var coreQuery = entityManager.EntityQueryEnumerator<NsvBluespaceShipAiCoreComponent, NsvShipTargetComponent, HTNComponent, TransformComponent>();
            while (coreQuery.MoveNext(out var coreUid, out _, out _, out _, out var coreTransform))
            {
                if (coreTransform.GridUid is { } gridUid && hostileGrids.Contains(gridUid))
                    broadsideCores.Add(coreUid);
            }

            var fireControllableGuns = 0;
            var gunQuery = entityManager.EntityQueryEnumerator<FireControllableComponent, TransformComponent>();
            while (gunQuery.MoveNext(out _, out var gunTransform))
            {
                if (gunTransform.GridUid is { } gridUid && hostileGrids.Contains(gridUid))
                    fireControllableGuns++;
            }

            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(hostileGrids.Count, Is.EqualTo(1));
                Assert.That(broadsideCores.Count, Is.EqualTo(1));
                Assert.That(fireControllableGuns, Is.GreaterThanOrEqualTo(1));
            });

            var core = broadsideCores[0];
            var htn = entityManager.GetComponent<HTNComponent>(core);
            Assert.Multiple(() =>
            {
                Assert.That(entityManager.GetComponent<MetaDataComponent>(core).EntityPrototype?.ID, Is.EqualTo("NsvBluespaceBroadsideCore"));
                Assert.That(htn.RootTask, Is.InstanceOf<HTNCompoundTask>());
                Assert.That(((HTNCompoundTask)htn.RootTask).Task, Is.EqualTo("NsvBluespaceBroadsideCompound"));
            });

            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RestoresForeignGridFactionAfterReturn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var shuttle = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var travel = entityManager.System<NsvBluespaceSectorTravelSystem>();
        var config = server.CfgMan;
        var startupTime = config.GetCVar(CCVars.FTLStartupTime);
        var travelTime = config.GetCVar(CCVars.FTLTravelTime);
        var arrivalTime = config.GetCVar(CCVars.FTLArrivalTime);
        var sectorMap = EntityUid.Invalid;
        var shuttleUid = shuttle.Grid.Owner;

        config.SetCVar(CCVars.FTLStartupTime, 0.1f);
        config.SetCVar(CCVars.FTLTravelTime, 0.3f);
        config.SetCVar(CCVars.FTLArrivalTime, 0.1f);

        try
        {
            await server.WaitPost(() =>
            {
                entityManager.EnsureComponent<NsvBluespaceFactionComponent>(shuttleUid).Faction = "NSVNeutral";
                Assert.That(
                    sectors.TryGetOrCreate("NSVBluespaceTestSector", 42, out sectorMap, out var failure),
                    Is.True,
                    failure ?? "No sector creation failure reason was returned.");
                Assert.That(travel.TryEnter(shuttleUid, "NSVBluespaceTestSector", 42, out var reason), Is.True, reason);

                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                Assert.That(sector.PendingArrivals, Does.Contain(shuttleUid));
                Assert.That(sector.ForeignGridFactionSnapshots[shuttleUid], Is.EqualTo(new NsvBluespaceFactionSnapshot(true, "NSVNeutral")));
            });

            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                Assert.Multiple(() =>
                {
                    Assert.That(entityManager.GetComponent<TransformComponent>(shuttleUid).MapUid, Is.EqualTo(sectorMap));
                    Assert.That(entityManager.GetComponent<NsvBluespaceFactionComponent>(shuttleUid).Faction.ToString(), Is.EqualTo("NSVPlayer"));
                    Assert.That(sector.ForeignGrids, Does.Contain(shuttleUid));
                    Assert.That(sector.PendingArrivals, Does.Not.Contain(shuttleUid));
                });
            });

            await server.WaitPost(() =>
            {
                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                var encounter = entityManager.GetComponent<NsvBluespaceEncounterComponent>(sector.EncounterController);
                entityManager.DeleteEntity(encounter.ObjectiveTarget);
            });
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() =>
            {
                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                Assert.That(
                    entityManager.GetComponent<NsvBluespaceEncounterComponent>(sector.EncounterController).State,
                    Is.EqualTo(NsvBluespaceEncounterState.ExtractionOpen));
            });

            await pair.RunSeconds(11);
            await server.WaitPost(() =>
                Assert.That(travel.TryTravel(shuttleUid, default, 42, out var reason), Is.True, reason));

            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                Assert.Multiple(() =>
                {
                    Assert.That(entityManager.GetComponent<TransformComponent>(shuttleUid).MapUid, Is.EqualTo(shuttle.MapUid));
                    Assert.That(entityManager.GetComponent<NsvBluespaceFactionComponent>(shuttleUid).Faction.ToString(), Is.EqualTo("NSVNeutral"));
                    Assert.That(sector.ForeignGrids, Does.Not.Contain(shuttleUid));
                    Assert.That(sector.ReturnDestinations, Does.Not.ContainKey(shuttleUid));
                    Assert.That(sector.ForeignGridFactionSnapshots, Does.Not.ContainKey(shuttleUid));
                });
            });

            // The pool reuses this server process; dispose the sector so the
            // template-keyed cache does not leak an instance whose patrol core
            // was deleted by this test.
            await server.WaitPost(() =>
                Assert.That(
                    sectors.TryDispose((sectorMap, entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap))),
                    Is.True));
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
    public async Task RejectsAsteroidFieldWithInvalidWeight()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var map = entityManager.System<MapSystem>();
        var generator = new NsvBluespaceAsteroidFieldGenerator();

        await server.WaitAssertion(() =>
        {
            var mapUid = map.CreateMap(out var mapId, false);
            var instance = entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            var definition = new NsvBluespaceAsteroidFieldGeneratorDefinition
            {
                Radius = 16f,
                Density = 0.01f,
                AsteroidTypes = new List<NsvBluespaceWeightedEntityDefinition>
                {
                    new()
                    {
                        Prototype = "AsteroidDebrisMedium",
                        Weight = 0f
                    }
                }
            };
            var placement = new NsvBluespaceSectorPlacement("Test", Vector2.Zero, 16f, 0f, 42);

            Assert.That(generator.TryGenerate(entityManager, prototypes, mapUid, instance, definition, placement), Is.False);
            Assert.That(instance.OwnedEntities, Is.Empty);
            map.DeleteMap(mapId);
        });

        await pair.CleanReturnAsync();
    }
}
