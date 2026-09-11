using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._NSV.Shuttles.Components;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Construction.Components;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.NodeContainer;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Atmos;
using Content.Shared.Construction.Components;
using Content.Shared.Damage;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.Interaction;
using Content.Shared.Power;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NSV.Shuttles;

/// <summary>
/// Server tests for the NSV 2x4 plasma/oxygen combustion thruster: prototype values, the dual-pipe
/// fuel state machine (atomic debit, starvation shutdown, automatic recovery), nozzle blocking in all
/// four rotations, power/signal control, T2 part scaling, destruction footprint and grid mass.
/// </summary>
[TestFixture]
public sealed class NsvCombustionThruster2x4Test
{
    private const string Prototype = "NsvCombustionThruster2x4";
    private const string PrototypeT2 = "NsvCombustionThruster2x4PartsT2";
    private const string BoardPrototype = "NsvCombustionThruster2x4MachineCircuitboard";

    private const float BaseThrust = 2400f;
    private const float PlasmaPerSecond = 1.5f;
    private const float OxygenPerPlasma = 1.4f;

    private const float T2Thrust = 3000f;
    private const float T2Scale = 1.25f;

    private sealed class ThrusterEnv
    {
        public EntityUid Grid;
        public MapGridComponent GridComp = default!;
        public EntityUid Thruster;
        public ThrusterComponent ThrusterComp = default!;
        public NsvCombustionThrusterComponent Combustion = default!;
        public ShuttleComponent Shuttle = default!;
        public PipeNode Plasma = default!;
        public PipeNode Oxygen = default!;
        public Tile SolidTile;
        public float GridMassBaseline;
        public float GridMassAfterAnchor;
    }

    private static async Task<ThrusterEnv> SetupAsync(TestPair pair, string prototype = Prototype)
    {
        var server = pair.Server;
        var entMan = server.EntMan;
        var testMap = await pair.CreateTestMap();
        var mapManager = server.ResolveDependency<IMapManager>();
        var tileManager = server.ResolveDependency<ITileDefinitionManager>();
        var mapSystem = server.System<SharedMapSystem>();
        var xforms = server.System<SharedTransformSystem>();

        var env = new ThrusterEnv();
        if (!tileManager.TryGetDefinition("Plating", out var plating))
            Assert.Fail("Unknown tile: Plating");
        env.SolidTile = new Tile(plating!.TileId);

        await server.WaitPost(() =>
        {
            var gridEnt = mapManager.CreateGridEntity(testMap.MapId);
            env.Grid = gridEnt.Owner;
            env.GridComp = gridEnt.Comp;
            xforms.SetWorldPosition(entMan.GetComponent<TransformComponent>(env.Grid), new Vector2(200f, 0f));
            entMan.EnsureComponent<ShuttleComponent>(env.Grid);
            // The origin needs a tile for snap-grid anchoring; the two nozzle tiles at y=1 stay space.
            mapSystem.SetTile(env.Grid, env.GridComp, new Vector2i(0, 0), env.SolidTile);
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
            env.GridMassBaseline = entMan.GetComponent<PhysicsComponent>(env.Grid).FixturesMass);

        await server.WaitPost(() =>
        {
            env.Thruster = entMan.SpawnEntity(prototype, new EntityCoordinates(env.Grid, 0.5f, 0.5f));
            var receiver = entMan.GetComponent<ApcPowerReceiverComponent>(env.Thruster);
            receiver.NeedsPower = false;
            receiver.Powered = true;
            var xform = entMan.GetComponent<TransformComponent>(env.Thruster);
            if (!xform.Anchored)
                Assert.That(xforms.AnchorEntity(env.Thruster), Is.True);
        });
        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            env.ThrusterComp = entMan.GetComponent<ThrusterComponent>(env.Thruster);
            env.Combustion = entMan.GetComponent<NsvCombustionThrusterComponent>(env.Thruster);
            env.Shuttle = entMan.GetComponent<ShuttleComponent>(env.Grid);
            env.GridMassAfterAnchor = entMan.GetComponent<PhysicsComponent>(env.Grid).FixturesMass;
            var container = entMan.GetComponent<NodeContainerComponent>(env.Thruster);
            env.Plasma = (PipeNode) container.Nodes["plasma"];
            env.Oxygen = (PipeNode) container.Nodes["oxygen"];

            Assert.Multiple(() =>
            {
                Assert.That(env.Plasma.NodeGroup, Is.Not.Null, "Plasma node should form its own pipe network.");
                Assert.That(env.Oxygen.NodeGroup, Is.Not.Null, "Oxygen node should form its own pipe network.");
            });

            Fill(env, 100f, 140f);
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.ThrusterComp.IsOn, Is.True);
        });

        return env;
    }

    private static void Fill(ThrusterEnv env, float plasma, float oxygen)
    {
        env.Plasma.Air.AdjustMoles(Gas.Plasma, plasma - env.Plasma.Air.GetMoles(Gas.Plasma));
        env.Oxygen.Air.AdjustMoles(Gas.Oxygen, oxygen - env.Oxygen.Air.GetMoles(Gas.Oxygen));
    }

    private static void RaiseAtmos(IEntityManager entMan, EntityUid thruster, float dt)
    {
        var ev = new AtmosDeviceUpdateEvent(dt, null, null);
        entMan.EventBus.RaiseLocalEvent(thruster, ref ev);
    }

    private static void StartFiring(IEntityManager entMan, ThrusterEnv env)
    {
        var dirIndex = (int) entMan.GetComponent<TransformComponent>(env.Thruster).LocalRotation.GetCardinalDir() / 2;
        entMan.System<ThrusterSystem>().EnableLinearThrustDirection(env.Shuttle, (DirectionFlag) (1 << dirIndex));
    }

    /// <summary>
    /// Production prototype and auto-stocked machine parts carry the fixed balance values.
    /// </summary>
    [Test]
    public async Task PrototypeDefaultsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var testMap = await pair.CreateTestMap();

        EntityUid thruster = default;
        EntityUid thrusterT2 = default;
        EntityUid board = default;
        EntityUid frame = default;
        EntityUid wreck = default;

        await server.WaitPost(() =>
        {
            thruster = entMan.SpawnEntity(Prototype, testMap.MapCoords);
            thrusterT2 = entMan.SpawnEntity(PrototypeT2, testMap.MapCoords);
            board = entMan.SpawnEntity(BoardPrototype, testMap.MapCoords);
            frame = entMan.SpawnEntity("MachineFrame2x4", testMap.MapCoords);
            wreck = entMan.SpawnEntity("MachineFrameDestroyed2x4", testMap.MapCoords);
        });
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            var t = entMan.GetComponent<ThrusterComponent>(thruster);
            var c = entMan.GetComponent<NsvCombustionThrusterComponent>(thruster);
            var power = entMan.GetComponent<ApcPowerReceiverComponent>(thruster);
            var atmosDevice = entMan.GetComponent<AtmosDeviceComponent>(thruster);
            var machine = entMan.GetComponent<MachineComponent>(thruster);
            var construction = entMan.GetComponent<ConstructionComponent>(thruster);
            var t2 = entMan.GetComponent<ThrusterComponent>(thrusterT2);
            var machineT2 = entMan.GetComponent<MachineComponent>(thrusterT2);
            var boardComp = entMan.GetComponent<MachineBoardComponent>(board);
            var frameComp = entMan.GetComponent<MachineFrameComponent>(frame);
            var wreckBounds = entMan.GetComponent<FixturesComponent>(wreck).Fixtures["fix1"].Shape
                .ComputeAABB(Robust.Shared.Physics.Transform.Empty, 0);
            var bounds = entMan.GetComponent<FixturesComponent>(thruster).Fixtures["fix1"].Shape
                .ComputeAABB(Robust.Shared.Physics.Transform.Empty, 0);

            Assert.Multiple(() =>
            {
                Assert.That(t.Thrust, Is.EqualTo(BaseThrust));
                Assert.That(t.BaseThrust, Is.EqualTo(BaseThrust));
                Assert.That(t.NozzleOffsets, Is.EqualTo(new List<Vector2i> { new(0, 1), new(1, 1) }));
                Assert.That(t.OriginalLoad, Is.EqualTo(18000f));
                Assert.That(c.PlasmaMolesPerSecond, Is.EqualTo(PlasmaPerSecond));
                Assert.That(c.OxygenPerPlasma, Is.EqualTo(OxygenPerPlasma));
                Assert.That(c.MaximumProcessSeconds, Is.EqualTo(1f));
                Assert.That(power.Load, Is.EqualTo(18000f));
                Assert.That(atmosDevice.RequireAnchored, Is.True);
                Assert.That(atmosDevice.JoinSystem, Is.True);
                Assert.That(machine.Board, Is.EqualTo(new EntProtoId<MachineBoardComponent>(BoardPrototype)));
                Assert.That(machine.PartContainer.ContainedEntities,
                    Has.Count.EqualTo(26), // 8 capacitors + 2 bricks + 8 electromagnets + 8 armor plates
                    "MapInit should auto-stock the machine from the board requirements.");
                Assert.That(machine.BoardContainer.ContainedEntities, Has.Count.EqualTo(1));
                Assert.That(machine.PartOverrides, Is.Empty, "T1 variant must not override any stock parts.");
                Assert.That(construction.Graph, Is.EqualTo("Machine2x4"));
                Assert.That(construction.Node, Is.EqualTo("machine"));
                Assert.That(bounds.Width, Is.EqualTo(1.9f).Within(0.05f));
                Assert.That(bounds.Height, Is.EqualTo(3.9f).Within(0.05f));

                Assert.That(machineT2.PartOverrides["Capacitor"],
                    Is.EqualTo(new EntProtoId("AdvancedCapacitorStockPart")));
                Assert.That(t2.Thrust, Is.EqualTo(T2Thrust), "T2 should refresh to 3000 thrust.");

                Assert.That(boardComp.FrameSize, Is.EqualTo("2x4"));
                Assert.That(boardComp.Prototype, Is.EqualTo(Prototype));
                Assert.That(frameComp.FrameSize, Is.EqualTo("2x4"));
                Assert.That(wreckBounds.Width, Is.GreaterThan(1.8f));
                Assert.That(wreckBounds.Height, Is.GreaterThan(3.8f),
                    "Destroyed frame should retain the 2x4 fixture footprint.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// End-to-end over real atmos ticks: the joined device turns on once fueled, consumes both gases
    /// only while firing, shuts down atomically on fuel loss while keeping player intent, and
    /// automatically re-enables (and resumes firing) when fuel returns.
    /// </summary>
    [Test]
    public async Task EndToEndAtmosLifecycleTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var env = await SetupAsync(pair);

        await server.WaitAssertion(() =>
        {
            // Anchored mass is folded into the grid body scaled by 1/2000.
            var thrusterMass = entMan.GetComponent<PhysicsComponent>(env.Thruster).FixturesMass;
            Assert.That(env.GridMassAfterAnchor,
                Is.EqualTo(env.GridMassBaseline + thrusterMass / 2000f).Within(0.5f),
                "2x4 fixture mass should be folded into the grid via NsvGridMassSystem.");
        });

        // ~0.67s: at least one real AtmosDeviceUpdateEvent for the joined device.
        await pair.RunTicksSync(40);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(env.ThrusterComp.IsOn, Is.True, "Thruster should enable once both fuels are available.");
                Assert.That(env.Combustion.FuelStatus, Is.EqualTo(NsvCombustionFuelStatus.Ready));
                Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(BaseThrust));
            });
            StartFiring(entMan, env);
            Assert.That(env.ThrusterComp.Firing, Is.True);
        });

        var plasmaBefore = 0f;
        var oxygenBefore = 0f;
        await server.WaitAssertion(() =>
        {
            plasmaBefore = env.Plasma.Air.GetMoles(Gas.Plasma);
            oxygenBefore = env.Oxygen.Air.GetMoles(Gas.Oxygen);
        });
        await pair.RunTicksSync(40);

        await server.WaitAssertion(() =>
        {
            var plasmaUsed = plasmaBefore - env.Plasma.Air.GetMoles(Gas.Plasma);
            var oxygenUsed = oxygenBefore - env.Oxygen.Air.GetMoles(Gas.Oxygen);
            Assert.Multiple(() =>
            {
                // 0.75 plasma / 1.05 oxygen per atmos tick (dt = 0.5s); tolerate 1-4 updates.
                Assert.That(plasmaUsed, Is.InRange(0.7f, 3.1f), "Firing should consume plasma in whole atmos ticks.");
                Assert.That(oxygenUsed, Is.InRange(0.98f, 4.34f), "Firing should consume oxygen at 1.4x plasma.");
                Assert.That(oxygenUsed / plasmaUsed, Is.EqualTo(OxygenPerPlasma).Within(0.01f));
            });
        });

        // Starve: drain plasma; thruster must stop without touching Enabled or the thrust-direction intent.
        await server.WaitAssertion(() => Fill(env, 0f, 140f));
        await pair.RunTicksSync(40);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(env.Combustion.FuelStatus, Is.EqualTo(NsvCombustionFuelStatus.NoPlasma));
                Assert.That(env.ThrusterComp.IsOn, Is.False);
                Assert.That(env.ThrusterComp.Firing, Is.False);
                Assert.That(env.ThrusterComp.Enabled, Is.True, "Starvation must not clear the player's on intent.");
                Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(0f));
            });
        });

        // Recover: refuel; the thruster re-enables and resumes firing for the held direction.
        await server.WaitAssertion(() => Fill(env, 100f, 140f));
        await pair.RunTicksSync(40);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(env.Combustion.FuelStatus, Is.EqualTo(NsvCombustionFuelStatus.Ready));
                Assert.That(env.ThrusterComp.IsOn, Is.True, "Fuel recovery should re-enable automatically.");
                Assert.That(env.ThrusterComp.Firing, Is.True, "Firing should resume for the still-selected direction.");
                Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(BaseThrust));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Deterministic fuel math driven by manual atmos updates: per-dt debit, dt clamping to 1s,
    /// no consumption while idle, atomic no-debit on single-gas starvation, and exact-to-zero burn.
    /// </summary>
    [Test]
    public async Task FuelConsumptionAtomicityTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var env = await SetupAsync(pair);

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 0.5f);
            Assert.That(env.ThrusterComp.IsOn, Is.True);
            StartFiring(entMan, env);
            Assert.That(env.ThrusterComp.Firing, Is.True);

            // dt = 1: exactly one second of fuel.
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Plasma.Air.GetMoles(Gas.Plasma), Is.EqualTo(98.5f).Within(1e-5f));
                Assert.That(env.Oxygen.Air.GetMoles(Gas.Oxygen), Is.EqualTo(137.9f).Within(1e-4f));
            });

            // dt = 5: clamped to MaximumProcessSeconds (1s).
            RaiseAtmos(entMan, env.Thruster, 5f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Plasma.Air.GetMoles(Gas.Plasma), Is.EqualTo(97f).Within(1e-5f));
                Assert.That(env.Oxygen.Air.GetMoles(Gas.Oxygen), Is.EqualTo(135.8f).Within(1e-4f));
            });

            // Idle: direction released, no debit.
            entMan.System<ThrusterSystem>().DisableLinearThrustDirection(env.Shuttle, DirectionFlag.South);
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Plasma.Air.GetMoles(Gas.Plasma), Is.EqualTo(97f).Within(1e-5f));
                Assert.That(env.Oxygen.Air.GetMoles(Gas.Oxygen), Is.EqualTo(135.8f).Within(1e-4f));
            });

            // Starve on plasma with plenty of oxygen: neither gas may be debited.
            StartFiring(entMan, env);
            Fill(env, 1.0f, 135.8f);
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Combustion.FuelStatus, Is.EqualTo(NsvCombustionFuelStatus.NoPlasma));
                Assert.That(env.ThrusterComp.IsOn, Is.False);
                Assert.That(env.ThrusterComp.Enabled, Is.True);
                Assert.That(env.Plasma.Air.GetMoles(Gas.Plasma), Is.EqualTo(1.0f).Within(1e-5f),
                    "Starved thruster must not partially debit plasma.");
                Assert.That(env.Oxygen.Air.GetMoles(Gas.Oxygen), Is.EqualTo(135.8f).Within(1e-4f),
                    "Starved thruster must not debit oxygen either.");
            });

            // Exactly one firing-second of both gases: burns to exactly zero.
            Fill(env, PlasmaPerSecond, PlasmaPerSecond * OxygenPerPlasma);
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.ThrusterComp.IsOn, Is.True, "Exact fuel should re-enable the thruster.");
                Assert.That(env.ThrusterComp.Firing, Is.True);
            });
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Plasma.Air.GetMoles(Gas.Plasma), Is.EqualTo(0f).Within(1e-5f));
                Assert.That(env.Oxygen.Air.GetMoles(Gas.Oxygen), Is.EqualTo(0f).Within(1e-4f));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Both intakes bridged into a single pipe network: still double-checked then double-debited,
    /// and a single-gas shortage debits nothing from the shared mixture.
    /// </summary>
    [Test]
    public async Task SharedPipeNetAtomicityTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var env = await SetupAsync(pair);

        await server.WaitAssertion(() => env.Plasma.AddAlwaysReachable(env.Oxygen));
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(env.Plasma.NodeGroup, Is.EqualTo(env.Oxygen.NodeGroup),
                "Both intakes should share one pipe network after bridging.");

            Fill(env, 100f, 140f);
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.ThrusterComp.IsOn, Is.True);
            StartFiring(entMan, env);

            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Plasma.Air.GetMoles(Gas.Plasma), Is.EqualTo(98.5f).Within(1e-5f));
                Assert.That(env.Plasma.Air.GetMoles(Gas.Oxygen), Is.EqualTo(137.9f).Within(1e-4f));
            });

            // Shared mixture starved of plasma: nothing is debited at all.
            env.Plasma.Air.AdjustMoles(Gas.Plasma, -env.Plasma.Air.GetMoles(Gas.Plasma) + 0.5f);
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Combustion.FuelStatus, Is.EqualTo(NsvCombustionFuelStatus.NoPlasma));
                Assert.That(env.ThrusterComp.IsOn, Is.False);
                Assert.That(env.Plasma.Air.GetMoles(Gas.Plasma), Is.EqualTo(0.5f).Within(1e-5f));
                Assert.That(env.Plasma.Air.GetMoles(Gas.Oxygen), Is.EqualTo(137.9f).Within(1e-4f));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// DeviceLink On/Off ports control the persistent intent: Off drops the APC load to 1 and fuel
    /// recovery must not re-enable; On restores the load and re-enables while fueled.
    /// </summary>
    [Test]
    public async Task SignalControlTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var env = await SetupAsync(pair);

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.ThrusterComp.IsOn, Is.True);

            var off = new SignalReceivedEvent("Off");
            entMan.EventBus.RaiseLocalEvent(env.Thruster, ref off);
            var power = entMan.GetComponent<ApcPowerReceiverComponent>(env.Thruster);
            Assert.Multiple(() =>
            {
                Assert.That(env.ThrusterComp.Enabled, Is.False);
                Assert.That(env.ThrusterComp.IsOn, Is.False);
                Assert.That(power.Load, Is.EqualTo(1f), "Off signal should drop the APC load to standby.");
            });

            // Fuel is (still) available, but an Off thruster must stay off.
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Combustion.FuelStatus, Is.EqualTo(NsvCombustionFuelStatus.Ready));
                Assert.That(env.ThrusterComp.IsOn, Is.False, "Fuel recovery must not re-enable an Off thruster.");
            });

            var on = new SignalReceivedEvent("On");
            entMan.EventBus.RaiseLocalEvent(env.Thruster, ref on);
            Assert.Multiple(() =>
            {
                Assert.That(env.ThrusterComp.Enabled, Is.True);
                Assert.That(env.ThrusterComp.IsOn, Is.True, "On signal should re-enable while fueled.");
                Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(env.Thruster).Load, Is.EqualTo(18000f));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// APC power loss removes the thrust registration; power restoration re-enables it, but an atmos
    /// update alone must not re-enable an unpowered thruster.
    /// </summary>
    [Test]
    public async Task PowerCycleTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var env = await SetupAsync(pair);

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.ThrusterComp.IsOn, Is.True);
            Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(BaseThrust));

            var receiver = entMan.GetComponent<ApcPowerReceiverComponent>(env.Thruster);
            receiver.Powered = false;
            var ev = new PowerChangedEvent(false, 0f);
            entMan.EventBus.RaiseLocalEvent(env.Thruster, ref ev);
            Assert.Multiple(() =>
            {
                Assert.That(env.ThrusterComp.IsOn, Is.False);
                Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(0f));
            });

            // Fuel is fine but power is not: no re-enable from the atmos path.
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.ThrusterComp.IsOn, Is.False,
                "Atmos update must not re-enable an unpowered thruster.");

            receiver.Powered = true;
            var evOn = new PowerChangedEvent(true, 0f);
            entMan.EventBus.RaiseLocalEvent(env.Thruster, ref evOn);
            Assert.Multiple(() =>
            {
                Assert.That(env.ThrusterComp.IsOn, Is.True);
                Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(BaseThrust));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Blocking any nozzle tile with a floor disables the thruster and unblocking re-enables it,
    /// with the two nozzle tiles correctly rotated through all four cardinal orientations.
    /// </summary>
    [Test]
    public async Task NozzleBlockingRotationsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var xforms = server.System<SharedTransformSystem>();
        var env = await SetupAsync(pair);

        // (LocalRotation, blocked nozzle tile, expected thrust-direction index)
        var cases = new List<(Angle rotation, Vector2i blockTile, int dirIndex)>
        {
            (new Angle(0f), new Vector2i(0, 1), 0),          // South thrust, nozzles above.
            (new Angle(MathF.PI / 2f), new Vector2i(-1, 0), 1), // East thrust, nozzles to the west.
            (new Angle(MathF.PI), new Vector2i(0, -1), 2),   // North thrust, nozzles below.
            (new Angle(3f * MathF.PI / 2f), new Vector2i(1, 0), 3), // West thrust, nozzles to the east.
        };

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.ThrusterComp.IsOn, Is.True);
        });

        foreach (var (rotation, blockTile, dirIndex) in cases)
        {
            await server.WaitAssertion(() =>
            {
                xforms.SetLocalRotation(env.Thruster, rotation);
                Assert.Multiple(() =>
                {
                    Assert.That(env.ThrusterComp.IsOn, Is.True,
                        $"Thruster should stay on after rotating to {rotation}.");
                    Assert.That(env.Shuttle.LinearThrust[dirIndex], Is.EqualTo(BaseThrust),
                        $"Thrust should be registered for direction index {dirIndex} after rotation.");
                });
            });

            await server.WaitAssertion(() =>
            {
                mapSystem.SetTile(env.Grid, env.GridComp, blockTile, env.SolidTile);
                Assert.That(entMan.System<ThrusterSystem>().CanEnable(env.Thruster, env.ThrusterComp), Is.False,
                    $"Nozzle tile {blockTile} should fail direct enable validation at rotation {rotation}.");
            });
            await pair.RunTicksSync(1);

            await server.WaitAssertion(() =>
                Assert.That(env.ThrusterComp.IsOn, Is.False,
                    $"Blocking nozzle tile {blockTile} at rotation {rotation} should disable the thruster."));

            await server.WaitAssertion(() =>
                mapSystem.SetTile(env.Grid, env.GridComp, blockTile, Tile.Empty));
            await pair.RunTicksSync(1);

            await server.WaitAssertion(() =>
                Assert.That(env.ThrusterComp.IsOn, Is.True,
                    $"Unblocking {blockTile} should re-enable the thruster at rotation {rotation}."));
        }

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Deleting an enabled thruster removes its thrust registration and burn state from the shuttle.
    /// </summary>
    [Test]
    public async Task DeletionCleansShuttleThrustTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var env = await SetupAsync(pair);

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 1f);
            StartFiring(entMan, env);
            Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(BaseThrust));

            entMan.DeleteEntity(env.Thruster);
        });
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(0f));
                Assert.That(env.Shuttle.LinearThrusters[0], Is.Empty);
                Assert.That(entMan.GetComponent<ShuttleComponent>(env.Grid).ThrustDirections,
                    Is.EqualTo(DirectionFlag.None).Or.EqualTo(DirectionFlag.South),
                    "Deleting the thruster itself must not disturb the shuttle's held direction input.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Unanchoring disconnects both pipe networks and stops the thruster; re-anchoring reconnects
    /// and the next atmos update re-enables it with the fresh networks.
    /// </summary>
    [Test]
    public async Task UnanchorReanchorTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var xforms = server.System<SharedTransformSystem>();
        var env = await SetupAsync(pair);

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.ThrusterComp.IsOn, Is.True);
            xforms.Unanchor(env.Thruster);
            Assert.That(env.ThrusterComp.IsOn, Is.False);
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.Combustion.FuelStatus, Is.EqualTo(NsvCombustionFuelStatus.Disconnected),
                "Unanchored pipes must read as disconnected.");

            Assert.That(xforms.AnchorEntity(env.Thruster), Is.True);
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Combustion.FuelStatus, Is.EqualTo(NsvCombustionFuelStatus.Ready));
                Assert.That(env.ThrusterComp.IsOn, Is.True,
                    "Re-anchoring with fuel available should re-enable via the atmos update.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Crossing the 7500 damage threshold converts the thruster into a 2x4 destroyed machine frame
    /// and removes its thrust from the shuttle.
    /// </summary>
    [Test]
    public async Task DamageDestructionKeeps2x4FootprintTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var env = await SetupAsync(pair);

        await server.WaitAssertion(() =>
        {
            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(BaseThrust));

            var damage = new DamageSpecifier { DamageDict = new() { ["Blunt"] = 8000 } };
            entMan.System<DamageableSystem>().TryChangeDamage(env.Thruster, damage, true);
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            var wreckFound = false;
            var children = entMan.GetComponent<TransformComponent>(env.Grid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID != "MachineFrameDestroyed2x4")
                    continue;

                wreckFound = true;
                var bounds = entMan.GetComponent<FixturesComponent>(child).Fixtures["fix1"].Shape
                    .ComputeAABB(Robust.Shared.Physics.Transform.Empty, 0);
                Assert.Multiple(() =>
                {
                    Assert.That(bounds.Width, Is.GreaterThan(1.8f));
                    Assert.That(bounds.Height, Is.GreaterThan(3.8f));
                    Assert.That(entMan.GetComponent<PhysicsComponent>(child).FixturesMass, Is.GreaterThan(100f),
                        "Wreck should keep a full 2x4 machine body.");
                });
            }

            Assert.Multiple(() =>
            {
                Assert.That(wreckFound, Is.True,
                    "Destruction should swap the thruster to a MachineFrameDestroyed2x4 wreck.");
                Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(0f));
                Assert.That(env.Shuttle.LinearThrusters[0], Is.Empty);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The T2 variant auto-stocks advanced capacitors on spawn: thrust refreshes to 3000
    /// and fuel consumption scales by 1.25 with an unchanged APC load.
    /// </summary>
    [Test]
    public async Task T2PartsFuelScalingTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var env = await SetupAsync(pair, PrototypeT2);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(env.ThrusterComp.Thrust, Is.EqualTo(T2Thrust));
                Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(env.Thruster).Load,
                    Is.EqualTo(18000f), "T2 parts must not change the APC load.");
            });

            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.That(env.ThrusterComp.IsOn, Is.True);
            StartFiring(entMan, env);

            RaiseAtmos(entMan, env.Thruster, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(env.Plasma.Air.GetMoles(Gas.Plasma),
                    Is.EqualTo(100f - PlasmaPerSecond * T2Scale).Within(1e-4f),
                    "T2 fuel should scale with Thrust / BaseThrust.");
                Assert.That(env.Oxygen.Air.GetMoles(Gas.Oxygen),
                    Is.EqualTo(140f - PlasmaPerSecond * T2Scale * OxygenPerPlasma).Within(1e-3f));
                Assert.That(env.Shuttle.LinearThrust[0], Is.EqualTo(T2Thrust));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The 2x4 board is accepted by a 2x4 machine frame and rejected by a vanilla 1x1 frame.
    /// </summary>
    [Test]
    public async Task BoardFitsOnly2x4FrameTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var testMap = await pair.CreateTestMap();

        EntityUid frame2x4 = default;
        EntityUid frame1x1 = default;
        EntityUid board2x4 = default;
        EntityUid board2x4b = default;

        await server.WaitPost(() =>
        {
            frame2x4 = entMan.SpawnEntity("MachineFrame2x4", testMap.MapCoords);
            frame1x1 = entMan.SpawnEntity("MachineFrame", testMap.MapCoords);
            board2x4 = entMan.SpawnEntity(BoardPrototype, testMap.MapCoords);
            board2x4b = entMan.SpawnEntity(BoardPrototype, testMap.MapCoords);
        });
        await pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            var insert = new InteractUsingEvent(frame2x4, board2x4, frame2x4, new EntityCoordinates(frame2x4, 0.5f, 0.5f));
            entMan.EventBus.RaiseLocalEvent(frame2x4, insert, broadcast: false);
            var frame2x4Comp = entMan.GetComponent<MachineFrameComponent>(frame2x4);
            Assert.Multiple(() =>
            {
                Assert.That(frame2x4Comp.HasBoard, Is.True, "2x4 board should fit the 2x4 frame.");
                Assert.That(frame2x4Comp.BoardContainer.ContainedEntities, Does.Contain(board2x4));
            });

            var reject = new InteractUsingEvent(frame1x1, board2x4b, frame1x1, new EntityCoordinates(frame1x1, 0.5f, 0.5f));
            entMan.EventBus.RaiseLocalEvent(frame1x1, reject, broadcast: false);
            var frame1x1Comp = entMan.GetComponent<MachineFrameComponent>(frame1x1);
            Assert.Multiple(() =>
            {
                Assert.That(frame1x1Comp.HasBoard, Is.False, "2x4 board must be rejected by the 1x1 frame.");
                Assert.That(frame1x1Comp.BoardContainer.ContainedEntities, Does.Not.Contain(board2x4b));
                Assert.That(entMan.EntityExists(board2x4b), Is.True, "Rejected board should not be consumed.");
            });
        });

        await pair.CleanReturnAsync();
    }
}
