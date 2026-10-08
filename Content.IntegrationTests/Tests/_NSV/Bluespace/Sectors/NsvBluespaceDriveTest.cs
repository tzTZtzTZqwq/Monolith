using System.Linq;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;
using Content.Shared.Materials;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NSV.Bluespace.Sectors;

/// <summary>
/// N4 bluespace jump core: it charges only while powered, a jump needs it powered, fully charged and
/// fuelled for the node's cost, and a jump spends the charge and the fuel.
/// </summary>
[TestFixture]
public sealed class NsvBluespaceDriveTest
{
    private const string DrivePrototype = "NSVBluespaceDriveCore";
    private const string ConsolePrototype = "NSVBluespaceDriveConsole";

    [Test]
    public async Task DriveChargesWhilePoweredAndJumpSpendsChargeAndFuel()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var shipMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var drives = entityManager.System<NsvBluespaceDriveSystem>();
        var power = entityManager.System<SharedPowerReceiverSystem>();
        var materials = entityManager.System<SharedMaterialStorageSystem>();
        var shuttle = shipMap.Grid.Owner;
        var drive = EntityUid.Invalid;
        var console = EntityUid.Invalid;

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.BluespaceDriveRequired, true);
            cfg.SetCVar(NsvCCVars.BluespaceDriveChargeTime, 0.2f);
            cfg.SetCVar(NsvCCVars.BluespaceDriveFuelPerCost, 100);

            Assert.That(drives.CanJump(shuttle, 0, out var missing), Is.False);
            Assert.That(missing, Is.EqualTo(Loc("nsv-bluespace-drive-missing")));

            drive = entityManager.SpawnEntity(DrivePrototype, shipMap.GridCoords);
            Assert.That(entityManager.GetComponent<TransformComponent>(drive).GridUid, Is.EqualTo(shuttle));
            Assert.That(drives.CanJump(shuttle, 0, out var noConsole), Is.False);
            Assert.That(noConsole, Is.EqualTo(Loc("nsv-bluespace-drive-no-console")), "the core needs its console");

            console = entityManager.SpawnEntity(ConsolePrototype, shipMap.GridCoords);
            Assert.That(drives.CanJump(shuttle, 0, out var unpowered), Is.False);
            Assert.That(unpowered, Is.EqualTo(Loc("nsv-bluespace-drive-unpowered")));

            power.SetNeedsPower(drive, false);
        });

        // Mono runs the power solver every 0.5 s, so power changes take up to ~15 ticks to land.
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            Assert.That(drives.CanJump(shuttle, 0, out var consoleUnpowered), Is.False);
            Assert.That(consoleUnpowered, Is.EqualTo(Loc("nsv-bluespace-drive-console-unpowered")));
            power.SetNeedsPower(console, false);
        });

        // A powered drive charges to full well within these ticks at a 0.2 s charge time.
        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(drives.GetStatus(shuttle).Charge, Is.EqualTo(1f));
            Assert.That(drives.CanJump(shuttle, 0, out _), Is.True, "a free jump only needs a charged, powered core");
            Assert.That(drives.CanJump(shuttle, 2, out var noFuel), Is.False);
            Assert.That(noFuel, Does.Contain("0/2"));

            Assert.That(materials.TryChangeMaterialAmount(drive, "Plasma", 300), Is.True);
            Assert.That(drives.CanJump(shuttle, 2, out var reason), Is.True, reason);

            drives.ConsumeJump(shuttle, 2);
            var status = drives.GetStatus(shuttle);
            Assert.Multiple(() =>
            {
                Assert.That(status.Charge, Is.EqualTo(0f), "a jump empties the charge");
                Assert.That(status.FuelUnits, Is.EqualTo(100), "two sheets burned for a fuel cost of 2");
                Assert.That(drives.CanJump(shuttle, 0, out _), Is.False, "must recharge before the next jump");
            });

            // Cut power: the core can't charge (and bleeds charge) without it.
            power.SetNeedsPower(drive, true);
        });

        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(drives.GetStatus(shuttle).Charge, Is.EqualTo(0f), "an unpowered core doesn't charge");

            // Destroying the console grounds the ship even with the core intact.
            entityManager.DeleteEntity(console);
            Assert.That(drives.CanJump(shuttle, 0, out var consoleGone), Is.False);
            Assert.That(consoleGone, Is.EqualTo(Loc("nsv-bluespace-drive-no-console")));

            cfg.SetCVar(NsvCCVars.BluespaceDriveRequired, false);
            Assert.That(drives.CanJump(shuttle, 5, out _), Is.True, "nothing is required when drives are off");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// With drives required, starmap travel refuses a shuttle without a core before it creates the
    /// destination sector, so a failed jump has no side effects.
    /// </summary>
    [Test]
    public async Task TravelRefusedWithoutDriveBeforeCreatingSector()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var shipMap = await pair.CreateTestMap();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var travel = entityManager.System<NsvBluespaceSectorTravelSystem>();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.BluespaceDriveRequired, true);
            Assert.That(travel.TryTravelToNode(shipMap.Grid.Owner, "NSVBluespaceStrategicMap", "alpha-2", out var reason),
                Is.False);
            Assert.That(reason, Is.EqualTo(Loc("nsv-bluespace-drive-missing")));

            var alpha2Exists = entityManager.EntityQuery<NsvBluespaceSectorInstanceComponent>()
                .Any(sector => sector.NodeId == "alpha-2");
            Assert.That(alpha2Exists, Is.False, "no sector is created for a refused jump");
        });

        await pair.CleanReturnAsync();
    }

    private static string Loc(string id)
    {
        return Robust.Shared.Localization.Loc.GetString(id);
    }
}
