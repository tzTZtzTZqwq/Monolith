using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Mono.FireControl;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Bluespace.Sectors.Generators;
using Content.Server._NSV.NPC;
using Content.Server._NSV.NPC.HTN;
using Content.Server.NPC.HTN;
using Content.Server.Spawners.Components;
using Content.Shared.CCVar;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared.Ghost;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
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
            var leash = entityManager.GetComponent<NsvShipAiMapComponent>(testMap.MapUid);

            Assert.Multiple(() =>
            {
                Assert.That(entityManager.HasComponent<NsvBluespaceFactionMapComponent>(testMap.MapUid), Is.True);
                Assert.That(entityManager.HasComponent<NsvBluespaceSectorInstanceComponent>(testMap.MapUid), Is.False);
                Assert.That(leash.LeashRadius, Is.EqualTo(3000f));
                Assert.That(leash.LeashStrength, Is.EqualTo(0.6f));
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
    public async Task RefreshesRegistryForPausedSector()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var map = entityManager.System<MapSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceTestSector", 47, out mapUid, out var failure),
                Is.True,
                failure);

            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var initial), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(initial.MapUid, Is.EqualTo(mapUid));
                Assert.That(initial.MapId, Is.EqualTo(instance.MapId));
                Assert.That(initial.State, Is.EqualTo(NsvBluespaceSectorState.PreparingSleep));
                Assert.That(initial.ForeignGridCount, Is.Zero);
                Assert.That(initial.PendingArrivalCount, Is.Zero);
                Assert.That(map.IsPaused(instance.MapId), Is.False);
            });

            instance.ForeignGrids.Add(mapUid);
            instance.PendingArrivals.Add(EntityUid.Invalid);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var occupied), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(occupied.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(occupied.ForeignGridCount, Is.EqualTo(1));
                Assert.That(occupied.PendingArrivalCount, Is.EqualTo(1));
                Assert.That(occupied.SleepEligibleSince, Is.Null);
                Assert.That(occupied.SleepDeadline, Is.Null);
            });

            instance.ForeignGrids.Clear();
            instance.PendingArrivals.Clear();
        });

        await pair.RunSeconds(5.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var refreshed), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(refreshed.State, Is.EqualTo(NsvBluespaceSectorState.PreparingSleep));
                Assert.That(refreshed.ForeignGridCount, Is.Zero);
                Assert.That(refreshed.PendingArrivalCount, Is.Zero);
            });
        });

        await server.WaitPost(() =>
        {
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            Assert.That(lifecycle.TryCommitSleep(mapUid), Is.True);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var paused), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(paused.MapUid, Is.EqualTo(mapUid));
                Assert.That(paused.State, Is.EqualTo(NsvBluespaceSectorState.Sleeping));
                Assert.That(map.IsPaused(instance.MapId), Is.True);
            });
            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SleepsAutomaticallyAndPreservesSectorState()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var map = entityManager.System<MapSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;
        var markerUid = EntityUid.Invalid;
        MapId mapId = default;
        var observedStates = new List<NsvBluespaceSectorState>();
        Action<EntityUid>? onSectorDisplayChanged = null;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceAsteroidSector", 64, out mapUid, out var failure),
                Is.True,
                failure);

            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            mapId = instance.MapId;
            markerUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, new Vector2(3f, 4f)));
            entityManager.EnsureComponent<NsvBluespaceFactionComponent>(markerUid).Faction = "NSVNeutral";
            onSectorDisplayChanged = changedMap =>
            {
                if (changedMap == mapUid &&
                    entityManager.TryGetComponent<NsvBluespaceSectorInstanceComponent>(mapUid, out var changedSector))
                {
                    observedStates.Add(changedSector.State);
                }
            };
            lifecycle.SectorDisplayChanged += onSectorDisplayChanged;

            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var preparing), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(preparing.State, Is.EqualTo(NsvBluespaceSectorState.PreparingSleep));
                Assert.That(preparing.SleepDeadline - preparing.SleepEligibleSince, Is.EqualTo(TimeSpan.FromSeconds(30)));
                Assert.That(map.IsPaused(mapId), Is.False);
                Assert.That(observedStates, Does.Contain(NsvBluespaceSectorState.PreparingSleep));
            });
        });

        await pair.RunSeconds(35.1f);
        await server.WaitAssertion(() =>
        {
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var sleeping), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Sleeping));
                Assert.That(instance.MapId, Is.EqualTo(mapId));
                Assert.That(sleeping.MapUid, Is.EqualTo(mapUid));
                Assert.That(sleeping.MapId, Is.EqualTo(mapId));
                Assert.That(sleeping.State, Is.EqualTo(NsvBluespaceSectorState.Sleeping));
                Assert.That(sleeping.SleepEligibleSince, Is.Null);
                Assert.That(sleeping.SleepDeadline, Is.Null);
                Assert.That(map.IsPaused(mapId), Is.True);
                Assert.That(entityManager.EntityExists(markerUid), Is.True);
                Assert.That(entityManager.GetComponent<TransformComponent>(markerUid).MapUid, Is.EqualTo(mapUid));
                Assert.That(entityManager.GetComponent<NsvBluespaceFactionComponent>(markerUid).Faction.ToString(), Is.EqualTo("NSVNeutral"));
                Assert.That(observedStates, Does.Contain(NsvBluespaceSectorState.Sleeping));
            });
        });

        await server.WaitPost(() =>
        {
            map.SetPaused(mapId, false);
            Assert.That(map.IsPaused(mapId), Is.False);
            lifecycle.RefreshRegistry();
            Assert.Multiple(() =>
            {
                Assert.That(entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid).State, Is.EqualTo(NsvBluespaceSectorState.Sleeping));
                Assert.That(map.IsPaused(mapId), Is.True);
            });
        });

        await server.WaitPost(() =>
        {
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            var sleepingEpoch = instance.TransitionEpoch;
            uint nestedWakeEpoch = 0;
            lifecycle.MapUnpausedTestHook = wakingMap =>
            {
                Assert.That(wakingMap, Is.EqualTo(mapUid));
                Assert.That(
                    lifecycle.RequestWake(
                        mapUid,
                        NsvBluespaceSectorWakeReason.Reconciliation,
                        null,
                        out var nestedFailure),
                    Is.True,
                    nestedFailure);
                nestedWakeEpoch = instance.TransitionEpoch;
            };

            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceAsteroidSector", 64, out var repeatedMap, out var sectorFailure),
                Is.True,
                sectorFailure);
            var readyEpoch = instance.TransitionEpoch;
            Assert.Multiple(() =>
            {
                Assert.That(repeatedMap, Is.EqualTo(mapUid));
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(map.IsPaused(mapId), Is.False);
                Assert.That(readyEpoch, Is.GreaterThan(sleepingEpoch));
                Assert.That(nestedWakeEpoch, Is.EqualTo(readyEpoch));
                Assert.That(observedStates, Does.Contain(NsvBluespaceSectorState.Waking));
                Assert.That(observedStates, Does.Contain(NsvBluespaceSectorState.Ready));
                Assert.That(entityManager.EntityExists(markerUid), Is.True);
                Assert.That(entityManager.GetComponent<TransformComponent>(markerUid).MapUid, Is.EqualTo(mapUid));
                Assert.That(entityManager.GetComponent<NsvBluespaceFactionComponent>(markerUid).Faction.ToString(), Is.EqualTo("NSVNeutral"));
            });

            Assert.That(
                lifecycle.RequestWake(
                    mapUid,
                    NsvBluespaceSectorWakeReason.SectorAccess,
                    null,
                    out var idempotentFailure),
                Is.True,
                idempotentFailure);
            Assert.That(instance.TransitionEpoch, Is.EqualTo(readyEpoch));

            if (onSectorDisplayChanged != null)
                lifecycle.SectorDisplayChanged -= onSectorDisplayChanged;
            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RollsBackSleepWhenBlockerAppearsAfterPause()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var map = entityManager.System<MapSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceAsteroidSector", 65, out mapUid, out var failure),
                Is.True,
                failure);

            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            lifecycle.RefreshRegistry();
            lifecycle.MapPausedTestHook = pausedMap =>
            {
                Assert.That(pausedMap, Is.EqualTo(mapUid));
                instance.PendingArrivals.Add(EntityUid.Invalid);
            };

            Assert.That(lifecycle.TryCommitSleep(mapUid), Is.False);
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var rolledBack), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(rolledBack.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(rolledBack.PendingArrivalCount, Is.EqualTo(1));
                Assert.That(rolledBack.SleepEligibleSince, Is.Null);
                Assert.That(rolledBack.SleepDeadline, Is.Null);
                Assert.That(map.IsPaused(instance.MapId), Is.False);
            });

            instance.PendingArrivals.Clear();
            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TravelWakesSleepingSectorBeforeArrivalReservation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var shuttle = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var map = entityManager.System<MapSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var travel = entityManager.System<NsvBluespaceSectorTravelSystem>();
        var shuttleUid = shuttle.Grid.Owner;
        var mapUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceTestSector", 66, out mapUid, out var failure),
                Is.True,
                failure);
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryCommitSleep(mapUid), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Sleeping));
                Assert.That(map.IsPaused(instance.MapId), Is.True);
            });

            Assert.That(
                travel.TryEnter(shuttleUid, "NSVBluespaceTestSector", 66, out var travelFailure),
                Is.True,
                travelFailure);
            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(map.IsPaused(instance.MapId), Is.False);
                Assert.That(instance.PendingArrivals, Does.Contain(shuttleUid));
                Assert.That(entityManager.HasComponent<FTLComponent>(shuttleUid), Is.True);
            });

            entityManager.RemoveComponent<FTLComponent>(shuttleUid);
        });

        await server.WaitRunTicks(1);
        await server.WaitPost(() =>
        {
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            Assert.Multiple(() =>
            {
                Assert.That(instance.PendingArrivals, Is.Empty);
                Assert.That(instance.ForeignGridFactionSnapshots, Is.Empty);
                Assert.That(instance.ReturnDestinations, Is.Empty);
                Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
            });
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MustRunTaskBlockerPreventsSleepAndWakesSleepingSector()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var map = entityManager.System<MapSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;
        var ownerUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceTestSector", 67, out mapUid, out var failure),
                Is.True,
                failure);
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            ownerUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, Vector2.Zero));
            lifecycle.RefreshRegistry();
            Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.PreparingSleep));

            Assert.That(lifecycle.RegisterMustRunTaskBlocker(mapUid, ownerUid, out var blockerFailure), Is.True, blockerFailure);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var blocked), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(blocked.MustRunTaskBlockerCount, Is.EqualTo(1));
                Assert.That(blocked.SleepDeadline, Is.Null);
                Assert.That(sectors.TryDispose((mapUid, instance)), Is.False);
            });

            Assert.That(lifecycle.UnregisterMustRunTaskBlocker(mapUid, ownerUid), Is.True);
            lifecycle.RefreshRegistry();
            Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.PreparingSleep));
            Assert.That(lifecycle.TryCommitSleep(mapUid), Is.True);
            Assert.That(map.IsPaused(instance.MapId), Is.True);

            Assert.That(lifecycle.RegisterMustRunTaskBlocker(mapUid, ownerUid, out blockerFailure), Is.True, blockerFailure);
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var woke), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(map.IsPaused(instance.MapId), Is.False);
                Assert.That(woke.MustRunTaskBlockerCount, Is.EqualTo(1));
            });

            Assert.That(lifecycle.UnregisterMustRunTaskBlocker(mapUid, ownerUid), Is.True);
            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AttachingLivingPlayerWakesSleepingSectorImmediately()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var map = entityManager.System<MapSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var session = server.PlayerMan.Sessions.Single();
        var originalAttached = session.AttachedEntity;
        var mapUid = EntityUid.Invalid;
        var playerUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceTestSector", 68, out mapUid, out var failure),
                Is.True,
                failure);
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryCommitSleep(mapUid), Is.True);
            var sleepingEpoch = instance.TransitionEpoch;

            playerUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, Vector2.Zero));
            entityManager.AddComponent<MobStateComponent>(playerUid);
            server.PlayerMan.SetAttachedEntity(session, playerUid);

            Assert.Multiple(() =>
            {
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(instance.TransitionEpoch, Is.GreaterThan(sleepingEpoch));
                Assert.That(map.IsPaused(instance.MapId), Is.False);
            });

            server.PlayerMan.SetAttachedEntity(session, originalAttached);
            entityManager.DeleteEntity(playerUid);
            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AggregatesLivingPlayerStates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var mobState = entityManager.System<MobStateSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var session = server.PlayerMan.Sessions.Single();
        var originalAttached = session.AttachedEntity;
        var mapUid = EntityUid.Invalid;
        var playerUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceAsteroidSector", 61, out mapUid, out var failure),
                Is.True,
                failure);
            playerUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, Vector2.Zero));
            entityManager.AddComponent<MobStateComponent>(playerUid);
            server.PlayerMan.SetAttachedEntity(session, playerUid);

            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var alive), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(alive.ActiveLivingPlayers, Is.EqualTo(1));
                Assert.That(alive.SleepEligibleSince, Is.Null);
                Assert.That(alive.SleepDeadline, Is.Null);
            });

            mobState.ChangeMobState(playerUid, MobState.Critical);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var critical), Is.True);
            Assert.That(critical.ActiveLivingPlayers, Is.EqualTo(1));

            mobState.ChangeMobState(playerUid, MobState.Dead);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var dead), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(dead.ActiveLivingPlayers, Is.Zero);
                Assert.That(dead.SleepEligibleSince, Is.Not.Null);
                Assert.That(dead.SleepDeadline - dead.SleepEligibleSince, Is.EqualTo(TimeSpan.FromSeconds(30)));
            });

            mobState.ChangeMobState(playerUid, MobState.Alive);
            entityManager.AddComponent<GhostComponent>(playerUid);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var ghost), Is.True);
            Assert.That(ghost.ActiveLivingPlayers, Is.Zero);

            entityManager.RemoveComponent<GhostComponent>(playerUid);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var returned), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(returned.ActiveLivingPlayers, Is.EqualTo(1));
                Assert.That(returned.SleepEligibleSince, Is.Null);
                Assert.That(returned.SleepDeadline, Is.Null);
            });

            server.PlayerMan.SetAttachedEntity(session, originalAttached);
            Assert.That(sectors.TryDispose((mapUid, entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid))), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AppliesDisconnectedPlayerGrace()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;
        var playerUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceAsteroidSector", 62, out mapUid, out var failure),
                Is.True,
                failure);
            playerUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, Vector2.Zero));
            entityManager.AddComponent<MobStateComponent>(playerUid);
            lifecycle.DisconnectedPlayerGrace = TimeSpan.FromSeconds(1);
            server.PlayerMan.SetAttachedEntity(server.PlayerMan.Sessions.Single(), playerUid);
        });

        await pair.Disconnect("Testing NSV sector grace expiry.");
        await server.WaitPost(() =>
        {
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var disconnected), Is.True);
            Assert.That(disconnected.ActiveLivingPlayers, Is.EqualTo(1));
        });

        await server.WaitRunTicks(pair.SecondsToTicks(1.1f));
        await server.WaitPost(() =>
        {
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var expired), Is.True);
            Assert.That(expired.ActiveLivingPlayers, Is.Zero);
        });

        await pair.Connect();
        await server.WaitPost(() =>
        {
            lifecycle.DisconnectedPlayerGrace = TimeSpan.FromSeconds(30);
            server.PlayerMan.SetAttachedEntity(server.PlayerMan.Sessions.Single(), playerUid);
        });

        await pair.Disconnect("Testing NSV sector grace invalidation.");
        await server.WaitPost(() =>
        {
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var disconnected), Is.True);
            Assert.That(disconnected.ActiveLivingPlayers, Is.EqualTo(1));

            entityManager.System<MobStateSystem>().ChangeMobState(playerUid, MobState.Dead);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var dead), Is.True);
            Assert.That(dead.ActiveLivingPlayers, Is.Zero);
        });

        await pair.Connect();
        await server.WaitPost(() =>
        {
            lifecycle.DisconnectedPlayerGrace = TimeSpan.FromSeconds(30);
            entityManager.DeleteEntity(playerUid);
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            instance.PendingArrivals.Add(EntityUid.Invalid);
            lifecycle.RefreshRegistry();
            instance.PendingArrivals.Clear();
            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExtendsDynamicDeadlineWithoutSlidingOrShrinking()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entityManager.System<MapSystem>();
        var factions = entityManager.System<NsvBluespaceFactionSystem>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;
        var playerFactionGrid = EntityUid.Invalid;
        TimeSpan? extendedDeadline = null;
        TimeSpan? initialEligibleSince = null;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceAsteroidSector", 63, out mapUid, out var failure),
                Is.True,
                failure);
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);

            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var baseline), Is.True);
            initialEligibleSince = baseline.SleepEligibleSince;
            Assert.Multiple(() =>
            {
                Assert.That(baseline.State, Is.EqualTo(NsvBluespaceSectorState.PreparingSleep));
                Assert.That(baseline.ActiveAiShips, Is.Zero);
                Assert.That(baseline.HasPlayerFactionShip, Is.False);
                Assert.That(baseline.SleepDeadline - baseline.SleepEligibleSince, Is.EqualTo(TimeSpan.FromSeconds(30)));
            });

            for (var i = 0; i < 13; i++)
            {
                var grid = mapManager.CreateGridEntity(instance.MapId);
                mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
                var gridUid = grid.Owner;
                var coreUid = entityManager.SpawnEntity(null, new EntityCoordinates(gridUid, Vector2.Zero));
                entityManager.AddComponent<NsvBluespaceShipAiCoreComponent>(coreUid);
                if (i == 0)
                {
                    playerFactionGrid = gridUid;
                    var duplicateCoreUid = entityManager.SpawnEntity(null, new EntityCoordinates(gridUid, Vector2.One));
                    entityManager.AddComponent<NsvBluespaceShipAiCoreComponent>(duplicateCoreUid);
                }
            }

            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var aiShips), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(aiShips.ActiveAiShips, Is.EqualTo(13));
                Assert.That(aiShips.SleepEligibleSince, Is.EqualTo(initialEligibleSince));
                Assert.That(aiShips.SleepDeadline - aiShips.SleepEligibleSince, Is.EqualTo(TimeSpan.FromSeconds(150)));
            });

            Assert.That(factions.SetFaction(playerFactionGrid, "NSVPlayer"), Is.True);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var playerShip), Is.True);
            extendedDeadline = playerShip.SleepDeadline;
            Assert.Multiple(() =>
            {
                Assert.That(playerShip.HasPlayerFactionShip, Is.True);
                Assert.That(playerShip.SleepEligibleSince, Is.EqualTo(initialEligibleSince));
                Assert.That(playerShip.SleepDeadline - playerShip.SleepEligibleSince, Is.EqualTo(TimeSpan.FromSeconds(240)));
            });

            entityManager.RemoveComponent<NsvBluespaceFactionComponent>(playerFactionGrid);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var reduced), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(reduced.HasPlayerFactionShip, Is.False);
                Assert.That(reduced.SleepDeadline, Is.EqualTo(extendedDeadline));
            });

            instance.PendingArrivals.Add(EntityUid.Invalid);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var blocked), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(blocked.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(blocked.SleepEligibleSince, Is.Null);
                Assert.That(blocked.SleepDeadline, Is.Null);
            });
        });

        await server.WaitRunTicks(1);
        await server.WaitPost(() =>
        {
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            Assert.That(instance.PendingArrivals, Is.Empty);
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var restarted), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(restarted.State, Is.EqualTo(NsvBluespaceSectorState.PreparingSleep));
                Assert.That(restarted.SleepEligibleSince, Is.GreaterThan(initialEligibleSince));
                Assert.That(restarted.SleepDeadline - restarted.SleepEligibleSince, Is.EqualTo(TimeSpan.FromSeconds(150)));
            });

            var restartedDeadline = restarted.SleepDeadline;
            lifecycle.RefreshRegistry();
            Assert.That(lifecycle.TryGetRegistryEntry(mapUid, out var unchanged), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(unchanged.SleepEligibleSince, Is.EqualTo(restarted.SleepEligibleSince));
                Assert.That(unchanged.SleepDeadline, Is.EqualTo(restartedDeadline));
            });

            instance.PendingArrivals.Add(EntityUid.Invalid);
            lifecycle.RefreshRegistry();
            instance.PendingArrivals.Clear();
            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClearsStaleArrivalStateWithoutDisposingSector()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var mapUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceTestSector", 46, out mapUid, out var failure),
                Is.True,
                failure);

            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            instance.PendingArrivals.Add(EntityUid.Invalid);
            instance.ForeignGridFactionSnapshots[EntityUid.Invalid] = new NsvBluespaceFactionSnapshot(false, default);
            instance.ReturnDestinations[EntityUid.Invalid] = new EntityCoordinates(mapUid, Vector2.Zero);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            Assert.Multiple(() =>
            {
                Assert.That(instance.PendingArrivals, Is.Empty);
                Assert.That(instance.ForeignGridFactionSnapshots, Is.Empty);
                Assert.That(instance.ReturnDestinations, Is.Empty);
                Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                Assert.That(entityManager.EntityExists(mapUid), Is.True);
            });
            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClearsCancelledArrivalReservation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var shuttle = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var travel = entityManager.System<NsvBluespaceSectorTravelSystem>();
        var shuttleUid = shuttle.Grid.Owner;
        var mapUid = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            Assert.That(
                sectors.TryGetOrCreate("NSVBluespaceTestSector", 51, out mapUid, out var failure),
                Is.True,
                failure);
            Assert.That(travel.TryEnter(shuttleUid, "NSVBluespaceTestSector", 51, out var reason), Is.True, reason);
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            Assert.Multiple(() =>
            {
                Assert.That(instance.PendingArrivals, Does.Contain(shuttleUid));
                Assert.That(sectors.TryDispose((mapUid, instance)), Is.False);
            });

            entityManager.RemoveComponent<FTLComponent>(shuttleUid);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
            Assert.Multiple(() =>
            {
                Assert.That(instance.PendingArrivals, Is.Empty);
                Assert.That(instance.ForeignGridFactionSnapshots, Is.Empty);
                Assert.That(instance.ReturnDestinations, Is.Empty);
            });

            Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
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
            var leash = entityManager.GetComponent<NsvShipAiMapComponent>(mapUid);
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
                Assert.That(leash.LeashRadius, Is.EqualTo(3000f));
                Assert.That(leash.LeashStrength, Is.EqualTo(0.6f));
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
            {
                var sector = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                sector.PendingArrivals.Add(EntityUid.Invalid);
                entityManager.System<NsvBluespaceSectorLifecycleSystem>().RefreshRegistry();
                sector.PendingArrivals.Clear();
                Assert.That(sectors.TryDispose((sectorMap, sector)), Is.True);
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
