using System.Collections.Generic;
using System.Linq;
using Content.Server._NSV.Administration;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Administration;
using Content.Shared._NSV.CCVar;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NSV.Administration;

[TestFixture]
public sealed class NsvSectorMonitorEuiTest
{
    [Test]
    public async Task ReportsConfiguredCapacitiesAndLifecycleStateCounts()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var map = entityManager.System<MapSystem>();
        var config = server.CfgMan;
        var oldTotalCapacity = config.GetCVar(NsvCCVars.BluespaceSectorTotalSoftCapacity);
        var oldActiveCapacity = config.GetCVar(NsvCCVars.BluespaceSectorActiveSoftCapacity);
        var createdMaps = new List<MapId>();

        try
        {
            await server.WaitAssertion(() =>
            {
                var initialState = GetMonitorState();
                Assert.Multiple(() =>
                {
                    Assert.That(initialState.TotalSectorSoftCapacity,
                        Is.EqualTo(NsvCCVars.BluespaceSectorTotalSoftCapacity.DefaultValue));
                    Assert.That(initialState.ActiveSectorSoftCapacity,
                        Is.EqualTo(NsvCCVars.BluespaceSectorActiveSoftCapacity.DefaultValue));
                });

                config.SetCVar(NsvCCVars.BluespaceSectorTotalSoftCapacity, 23);
                config.SetCVar(NsvCCVars.BluespaceSectorActiveSoftCapacity, 5);

                var monitoredSectors = new Dictionary<EntityUid, NsvBluespaceSectorState>();
                foreach (var sectorState in Enum.GetValues<NsvBluespaceSectorState>())
                {
                    var mapUid = map.CreateMap(out var mapId, false);
                    createdMaps.Add(mapId);
                    var instance = entityManager.EnsureComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
                    instance.MapId = mapId;
                    instance.State = sectorState;
                    monitoredSectors.Add(mapUid, sectorState);

                    if (sectorState == NsvBluespaceSectorState.Sleeping)
                        map.SetPaused(mapId, true);
                }

                var state = GetMonitorState();
                var monitoredRows = state.Rows
                    .Where(row => monitoredSectors.Keys.Any(uid => row.Id.StartsWith($"{uid} / ")))
                    .ToArray();

                Assert.Multiple(() =>
                {
                    Assert.That(state.TotalSectorCount - initialState.TotalSectorCount, Is.EqualTo(7));
                    Assert.That(state.ActiveSectorCount - initialState.ActiveSectorCount, Is.EqualTo(4));
                    Assert.That(state.TotalSectorSoftCapacity, Is.EqualTo(23));
                    Assert.That(state.ActiveSectorSoftCapacity, Is.EqualTo(5));
                    Assert.That(monitoredRows, Has.Length.EqualTo(7));
                    Assert.That(monitoredRows.Count(row => row.State == nameof(NsvBluespaceSectorState.Sleeping)), Is.EqualTo(1));
                });
            });
        }
        finally
        {
            config.SetCVar(NsvCCVars.BluespaceSectorTotalSoftCapacity, oldTotalCapacity);
            config.SetCVar(NsvCCVars.BluespaceSectorActiveSoftCapacity, oldActiveCapacity);

            await server.WaitPost(() =>
            {
                foreach (var mapId in createdMaps)
                {
                    if (map.MapExists(mapId))
                        map.DeleteMap(mapId);
                }
            });
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ZeroCapacitiesDoNotBlockCreateSleepOrWake()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var lifecycle = entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var map = entityManager.System<MapSystem>();
        var sectors = entityManager.System<NsvBluespaceSectorSystem>();
        var config = server.CfgMan;
        var oldTotalCapacity = config.GetCVar(NsvCCVars.BluespaceSectorTotalSoftCapacity);
        var oldActiveCapacity = config.GetCVar(NsvCCVars.BluespaceSectorActiveSoftCapacity);
        var mapUid = EntityUid.Invalid;

        config.SetCVar(NsvCCVars.BluespaceSectorTotalSoftCapacity, 0);
        config.SetCVar(NsvCCVars.BluespaceSectorActiveSoftCapacity, 0);

        try
        {
            await server.WaitPost(() =>
            {
                Assert.That(
                    sectors.TryGetOrCreate("NSVBluespaceTestSector", 170, out mapUid, out var createFailure),
                    Is.True,
                    createFailure);

                var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
                lifecycle.RefreshRegistry();
                Assert.That(lifecycle.TryCommitSleep(mapUid), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Sleeping));
                    Assert.That(map.IsPaused(instance.MapId), Is.True);
                });

                Assert.That(
                    lifecycle.RequestWake(
                        mapUid,
                        NsvBluespaceSectorWakeReason.SectorAccess,
                        null,
                        out var wakeFailure),
                    Is.True,
                    wakeFailure);

                var state = GetMonitorState();
                Assert.Multiple(() =>
                {
                    Assert.That(instance.State, Is.EqualTo(NsvBluespaceSectorState.Ready));
                    Assert.That(map.IsPaused(instance.MapId), Is.False);
                    Assert.That(state.TotalSectorSoftCapacity, Is.Zero);
                    Assert.That(state.ActiveSectorSoftCapacity, Is.Zero);
                    Assert.That(state.TotalSectorCount, Is.GreaterThan(state.TotalSectorSoftCapacity));
                    Assert.That(state.ActiveSectorCount, Is.GreaterThan(state.ActiveSectorSoftCapacity));
                });

                Assert.That(sectors.TryDispose((mapUid, instance)), Is.True);
            });

            await server.WaitRunTicks(1);
            await server.WaitAssertion(() => Assert.That(entityManager.EntityExists(mapUid), Is.False));
        }
        finally
        {
            config.SetCVar(NsvCCVars.BluespaceSectorTotalSoftCapacity, oldTotalCapacity);
            config.SetCVar(NsvCCVars.BluespaceSectorActiveSoftCapacity, oldActiveCapacity);

            if (entityManager.EntityExists(mapUid))
            {
                await server.WaitPost(() =>
                {
                    if (!entityManager.EntityExists(mapUid))
                        return;

                    var instance = entityManager.GetComponent<NsvBluespaceSectorInstanceComponent>(mapUid);
                    if (map.MapExists(instance.MapId))
                        map.DeleteMap(instance.MapId);
                });
            }
        }

        await pair.CleanReturnAsync();
    }

    private static NsvSectorMonitorEuiState GetMonitorState()
    {
        return (NsvSectorMonitorEuiState) new NsvSectorMonitorEui().GetNewState();
    }
}
