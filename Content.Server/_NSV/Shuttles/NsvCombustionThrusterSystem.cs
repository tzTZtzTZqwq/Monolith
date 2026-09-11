using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server._NSV.Shuttles.Components;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Atmos;
using Content.Shared.Examine;

namespace Content.Server._NSV.Shuttles;

/// <summary>
/// Server-only fuel logic for combustion thrusters: consumes Plasma and Oxygen from two
/// independent pipe networks while actually firing, atomically debiting both gases, and
/// stops/re-enables the generic thruster on fuel loss/recovery without touching the
/// player's persistent <see cref="ThrusterComponent.Enabled"/> intent.
/// </summary>
public sealed class NsvCombustionThrusterSystem : EntitySystem
{
    [Dependency] private readonly NodeContainerSystem _nodeContainer = default!;
    [Dependency] private readonly ThrusterSystem _thruster = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NsvCombustionThrusterComponent, AtmosDeviceUpdateEvent>(OnAtmosUpdate);
        SubscribeLocalEvent<NsvCombustionThrusterComponent, ThrusterEnableAttemptEvent>(OnEnableAttempt);
        SubscribeLocalEvent<NsvCombustionThrusterComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<NsvCombustionThrusterComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<NsvCombustionThrusterComponent, GasAnalyzerScanEvent>(OnAnalyzed);
    }

    private void OnAtmosUpdate(EntityUid uid, NsvCombustionThrusterComponent comp, ref AtmosDeviceUpdateEvent args)
    {
        var thruster = Comp<ThrusterComponent>(uid);
        var dt = Math.Clamp(args.dt, 0f, comp.MaximumProcessSeconds);

        var status = CheckFuel(uid, comp, thruster, dt, out var plasma, out var oxygen);

        if (thruster is { Enabled: true, IsOn: true, Firing: true } &&
            status == NsvCombustionFuelStatus.Ready &&
            plasma != null &&
            oxygen != null)
        {
            plasma.Air.AdjustMoles(Gas.Plasma, -GetPlasmaRequired(comp, thruster, dt));
            oxygen.Air.AdjustMoles(Gas.Oxygen, -GetOxygenRequired(comp, thruster, dt));
        }

        if (status == comp.FuelStatus)
            return;

        comp.FuelStatus = status;

        switch (status)
        {
            case NsvCombustionFuelStatus.Ready:
                if (thruster is { Enabled: true, IsOn: false })
                    _thruster.TryEnableThruster(uid, thruster);
                break;
            case NsvCombustionFuelStatus.Disconnected:
            case NsvCombustionFuelStatus.NoPlasma:
            case NsvCombustionFuelStatus.NoOxygen:
            case NsvCombustionFuelStatus.NoFuel:
                if (thruster.IsOn)
                    _thruster.DisableThruster(uid, thruster);
                break;
        }
    }

    private void OnEnableAttempt(EntityUid uid, NsvCombustionThrusterComponent comp, ref ThrusterEnableAttemptEvent args)
    {
        // Refuse to enable without at least one full minimum interval of fuel; consume nothing here.
        var thruster = Comp<ThrusterComponent>(uid);
        var status = CheckFuel(uid, comp, thruster, comp.MinimumFuelSeconds, out _, out _);

        if (status != NsvCombustionFuelStatus.Ready)
        {
            comp.FuelStatus = status;
            args.Cancelled = true;
        }
    }

    private void OnAnchorChanged(EntityUid uid, NsvCombustionThrusterComponent comp, ref AnchorStateChangedEvent args)
    {
        // Pipes only connect while anchored; the next atmos tick re-resolves both networks.
        comp.FuelStatus = NsvCombustionFuelStatus.Disconnected;
    }

    private void OnExamined(EntityUid uid, NsvCombustionThrusterComponent comp, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var thruster = Comp<ThrusterComponent>(uid);
        var scale = thruster.BaseThrust > 0f ? thruster.Thrust / thruster.BaseThrust : 1f;
        var plasmaRate = comp.PlasmaMolesPerSecond * scale;
        var oxygenRate = plasmaRate * comp.OxygenPerPlasma;

        using (args.PushGroup(nameof(NsvCombustionThrusterComponent)))
        {
            args.PushMarkup(Loc.GetString("nsv-combustion-thruster-status",
                ("status", Loc.GetString($"nsv-combustion-thruster-status-{comp.FuelStatus.ToString().ToLowerInvariant()}"))));
            args.PushMarkup(Loc.GetString("nsv-combustion-thruster-consumption",
                ("plasma", plasmaRate.ToString("0.###")),
                ("oxygen", oxygenRate.ToString("0.###"))));
        }
    }

    private void OnAnalyzed(EntityUid uid, NsvCombustionThrusterComponent comp, GasAnalyzerScanEvent args)
    {
        args.GasMixtures ??= new List<(string, GasMixture?)>();

        if (_nodeContainer.TryGetNode(uid, comp.PlasmaNode, out PipeNode? plasma) && plasma.Air.Volume != 0f)
            args.GasMixtures.Add((Loc.GetString("nsv-combustion-thruster-plasma-intake"), CloneNodeAir(plasma)));

        if (_nodeContainer.TryGetNode(uid, comp.OxygenNode, out PipeNode? oxygen) && oxygen.Air.Volume != 0f)
            args.GasMixtures.Add((Loc.GetString("nsv-combustion-thruster-oxygen-intake"), CloneNodeAir(oxygen)));
    }

    private static GasMixture CloneNodeAir(PipeNode node)
    {
        var air = node.Air.Clone();
        air.Multiply(node.Volume / node.Air.Volume);
        air.Volume = node.Volume;
        return air;
    }

    /// <summary>
    /// Resolves both intake nodes and reports fuel availability without modifying any gas.
    /// Out nodes are only non-null when the corresponding network is connected.
    /// </summary>
    private NsvCombustionFuelStatus CheckFuel(
        EntityUid uid,
        NsvCombustionThrusterComponent comp,
        ThrusterComponent thruster,
        float seconds,
        out PipeNode? plasma,
        out PipeNode? oxygen)
    {
        plasma = null;
        oxygen = null;

        if (!Transform(uid).Anchored ||
            !_nodeContainer.TryGetNodes(uid, comp.PlasmaNode, comp.OxygenNode, out PipeNode? plasmaNode, out PipeNode? oxygenNode) ||
            plasmaNode.NodeGroup == null ||
            oxygenNode.NodeGroup == null)
        {
            return NsvCombustionFuelStatus.Disconnected;
        }

        plasma = plasmaNode;
        oxygen = oxygenNode;

        var plasmaRequired = GetPlasmaRequired(comp, thruster, seconds);
        var oxygenRequired = GetOxygenRequired(comp, thruster, seconds);
        var hasPlasma = plasmaNode.Air.GetMoles(Gas.Plasma) >= plasmaRequired;
        var hasOxygen = oxygenNode.Air.GetMoles(Gas.Oxygen) >= oxygenRequired;

        if (hasPlasma && hasOxygen)
            return NsvCombustionFuelStatus.Ready;

        if (!hasPlasma && !hasOxygen)
            return NsvCombustionFuelStatus.NoFuel;

        return hasPlasma
            ? NsvCombustionFuelStatus.NoOxygen
            : NsvCombustionFuelStatus.NoPlasma;
    }

    private static float GetThrustScale(ThrusterComponent thruster)
    {
        return thruster.BaseThrust > 0f ? thruster.Thrust / thruster.BaseThrust : 1f;
    }

    private static float GetPlasmaRequired(NsvCombustionThrusterComponent comp, ThrusterComponent thruster, float seconds)
    {
        return comp.PlasmaMolesPerSecond * seconds * GetThrustScale(thruster);
    }

    private static float GetOxygenRequired(NsvCombustionThrusterComponent comp, ThrusterComponent thruster, float seconds)
    {
        return GetPlasmaRequired(comp, thruster, seconds) * comp.OxygenPerPlasma;
    }
}
