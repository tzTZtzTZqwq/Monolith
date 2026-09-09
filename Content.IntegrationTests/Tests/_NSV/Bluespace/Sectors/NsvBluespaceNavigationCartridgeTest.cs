using System.Linq;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server.CartridgeLoader;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared.CartridgeLoader;
using Content.Shared.CCVar;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Sectors;

[TestFixture]
public sealed class NsvBluespaceNavigationCartridgeTest
{
    private const string CartridgePrototype = "NsvBluespaceNavigationCartridge";

    [Test]
    public async Task CartridgePushesUiStateOnReadyAndSectorChanges()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var shuttle = await pair.CreateTestMap();
        var entities = server.ResolveDependency<IEntityManager>();
        var cartridgeLoader = entities.System<CartridgeLoaderSystem>();
        var ui = entities.System<UserInterfaceSystem>();
        var config = server.CfgMan;
        var shuttleUid = shuttle.Grid.Owner;
        var pdaUid = EntityUid.Invalid;
        var programUid = EntityUid.Invalid;
        var targetCore = EntityUid.Invalid;
        var sectorMap = EntityUid.Invalid;

        var startupTime = config.GetCVar(CCVars.FTLStartupTime);
        var travelTime = config.GetCVar(CCVars.FTLTravelTime);
        var arrivalTime = config.GetCVar(CCVars.FTLArrivalTime);
        config.SetCVar(CCVars.FTLStartupTime, 0.1f);
        config.SetCVar(CCVars.FTLTravelTime, 0.3f);
        config.SetCVar(CCVars.FTLArrivalTime, 0.1f);

        try
        {
            await server.WaitPost(() =>
            {
                pdaUid = entities.SpawnEntity("PassengerPDA", new EntityCoordinates(shuttleUid, 0.5f, 0.5f));
                Assert.That(cartridgeLoader.InstallProgram(pdaUid, CartridgePrototype), Is.True);
            });

            await server.WaitPost(() =>
            {
                programUid = cartridgeLoader.GetInstalled(pdaUid)
                    .Single(uid => entities.HasComponent<NsvBluespaceNavigationCartridgeComponent>(uid));
                cartridgeLoader.ActivateProgram(pdaUid, programUid);
                entities.EventBus.RaiseLocalEvent(programUid, new CartridgeUiReadyEvent(pdaUid));
            });

            await server.WaitAssertion(() =>
            {
                var loader = entities.GetComponent<CartridgeLoaderComponent>(pdaUid);
                Assert.That(ui.TryGetUiState<NsvBluespaceCartridgeUiState>(pdaUid, loader.UiKey, out var state), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(state!.InSector, Is.False);
                    Assert.That(state.SectorName, Is.EqualTo("nsv-bluespace-cartridge-no-sector"));
                    Assert.That(state.HasEncounter, Is.False);
                });
            });

            var travel = entities.System<NsvBluespaceSectorTravelSystem>();
            var sectors = entities.System<NsvBluespaceSectorSystem>();
            await server.WaitPost(() =>
            {
                Assert.That(
                    travel.TryEnter(shuttleUid, "NSVBluespaceTestSector", 42, out var reason),
                    Is.True,
                    reason);
            });

            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                sectorMap = entities.GetComponent<TransformComponent>(shuttleUid).MapUid!.Value;
                var sector = entities.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
                targetCore = entities.GetComponent<NsvBluespaceEncounterComponent>(sector.EncounterController).ObjectiveTarget;
                Assert.That(targetCore, Is.Not.EqualTo(EntityUid.Invalid));
            });

            await server.WaitPost(() =>
            {
                entities.EventBus.RaiseLocalEvent(programUid, new CartridgeUiReadyEvent(pdaUid));
            });

            await server.WaitAssertion(() =>
            {
                var loader = entities.GetComponent<CartridgeLoaderComponent>(pdaUid);
                Assert.That(ui.TryGetUiState<NsvBluespaceCartridgeUiState>(pdaUid, loader.UiKey, out var state), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(state!.InSector, Is.True);
                    Assert.That(state.HasEncounter, Is.True);
                    Assert.That(state.IsParticipant, Is.True);
                    Assert.That(state.CanExtract, Is.False);
                });
            });

            await server.WaitPost(() => entities.DeleteEntity(targetCore));
            await server.WaitRunTicks(2);

            await server.WaitAssertion(() =>
            {
                var loader = entities.GetComponent<CartridgeLoaderComponent>(pdaUid);
                Assert.That(ui.TryGetUiState<NsvBluespaceCartridgeUiState>(pdaUid, loader.UiKey, out var state), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(state!.HasEncounter, Is.True);
                    Assert.That(state.CanExtract, Is.True);
                });
            });

            // The pair's server process is reused by later tests; leave the
            // template-keyed sector cache empty so they create fresh instances.
            // Arrival leaves the shuttle in a ~10s FTL cooldown before the
            // FTL component is removed, so wait that out before returning.
            await pair.RunSeconds(11);
            await server.WaitPost(() =>
                Assert.That(travel.TryTravel(shuttleUid, default, 42, out var reason), Is.True, reason));
            await pair.RunSeconds(1);

            await server.WaitPost(() =>
            {
                Assert.That(entities.GetComponent<TransformComponent>(shuttleUid).MapUid, Is.EqualTo(shuttle.MapUid));
                Assert.That(
                    sectors.TryDispose((sectorMap, entities.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap))),
                    Is.True);
            });
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() => Assert.That(entities.EntityExists(sectorMap), Is.False));
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
