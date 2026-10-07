using Content.Server.Power.EntitySystems;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;
using Content.Shared.Examine;
using Content.Shared.Materials;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;

namespace Content.Server._NSV.Bluespace.Sectors;

/// <summary>
/// A bluespace jump drive (N4, NSV13 FTL-003/004/005), used on the CTLA-160 heavy core. It charges
/// while powered and bleeds charge while not; a jump needs a full charge and burns fuel — material
/// in the drive's <see cref="MaterialStorageComponent"/> — in proportion to the jump's node fuel cost.
/// Destroying the drive loses its charge and fuel with it.
/// </summary>
[RegisterComponent]
public sealed partial class NsvBluespaceDriveComponent : Component
{
    // 0..1.
    [ViewVariables]
    public float Charge;

    [DataField]
    public string FuelMaterial = "Plasma";

    // Display bookkeeping so consoles refresh on visible changes, not every tick.
    public int LastDisplayedStep = -1;
    public bool LastPowered;
}

public readonly record struct NsvBluespaceDriveStatus(bool HasDrive, bool Powered, float Charge, int FuelUnits);

/// <summary>
/// Charges drives, gates bluespace jumps on them, and spends charge and fuel when a jump engages.
/// </summary>
public sealed partial class NsvBluespaceDriveSystem : EntitySystem
{
    // Material units in one sheet; fuel is shown to players in sheets.
    public const int UnitsPerSheet = 100;

    // Consoles refresh each time the charge crosses a 5% step.
    private const int DisplaySteps = 20;

    [Dependency] private SharedMaterialStorageSystem _materials = default!;
    [Dependency] private IConfigurationManager _cfg = default!;

    /// <summary>
    /// Raised with the drive's grid when its visible state (charge step, power) changes.
    /// </summary>
    public event Action<EntityUid>? DriveDisplayChanged;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NsvBluespaceDriveComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<NsvBluespaceDriveComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Shutdown()
    {
        DriveDisplayChanged = null;
        base.Shutdown();
    }

    public bool Required => _cfg.GetCVar(NsvCCVars.BluespaceDriveRequired);

    public override void Update(float frameTime)
    {
        var rate = frameTime / MathF.Max(0.01f, _cfg.GetCVar(NsvCCVars.BluespaceDriveChargeTime));
        var query = EntityQueryEnumerator<NsvBluespaceDriveComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var drive, out var xform))
        {
            var powered = this.IsPowered(uid, EntityManager);
            drive.Charge = Math.Clamp(drive.Charge + (powered ? rate : -rate), 0f, 1f);

            var step = (int) (drive.Charge * DisplaySteps);
            if (step == drive.LastDisplayedStep && powered == drive.LastPowered)
                continue;

            drive.LastDisplayedStep = step;
            drive.LastPowered = powered;
            if (xform.GridUid is { } grid)
                DriveDisplayChanged?.Invoke(grid);
        }
    }

    /// <summary>
    /// The best drive aboard <paramref name="shuttleUid"/>: powered first, then the most charged.
    /// </summary>
    public bool TryGetDrive(EntityUid shuttleUid, out Entity<NsvBluespaceDriveComponent> drive)
    {
        drive = default;
        var found = false;
        var bestPowered = false;
        var bestCharge = 0f;
        var query = AllEntityQuery<NsvBluespaceDriveComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var candidate, out var xform))
        {
            if (xform.GridUid != shuttleUid || TerminatingOrDeleted(uid))
                continue;

            var powered = this.IsPowered(uid, EntityManager);
            if (found && (bestPowered && !powered ||
                          bestPowered == powered && candidate.Charge <= bestCharge))
            {
                continue;
            }

            drive = (uid, candidate);
            bestPowered = powered;
            bestCharge = candidate.Charge;
            found = true;
        }

        return found;
    }

    public NsvBluespaceDriveStatus GetStatus(EntityUid shuttleUid)
    {
        if (!TryGetDrive(shuttleUid, out var drive))
            return new NsvBluespaceDriveStatus(false, false, 0f, 0);

        return new NsvBluespaceDriveStatus(
            true,
            this.IsPowered(drive, EntityManager),
            drive.Comp.Charge,
            _materials.GetMaterialAmount(drive, drive.Comp.FuelMaterial));
    }

    /// <summary>
    /// Fuel units a jump with node fuel cost <paramref name="fuelCost"/> burns.
    /// </summary>
    public int FuelRequired(int fuelCost)
    {
        return Math.Max(0, fuelCost) * Math.Max(0, _cfg.GetCVar(NsvCCVars.BluespaceDriveFuelPerCost));
    }

    /// <summary>
    /// Whether <paramref name="shuttleUid"/> can make a bluespace jump costing <paramref name="fuelCost"/>
    /// right now. Always true when drives aren't required.
    /// </summary>
    public bool CanJump(EntityUid shuttleUid, int fuelCost, out string? reason)
    {
        reason = null;
        if (!Required)
            return true;

        var status = GetStatus(shuttleUid);
        var required = FuelRequired(fuelCost);
        if (!status.HasDrive)
            reason = Loc.GetString("nsv-bluespace-drive-missing");
        else if (!status.Powered)
            reason = Loc.GetString("nsv-bluespace-drive-unpowered");
        else if (status.Charge < 1f)
            reason = Loc.GetString("nsv-bluespace-drive-charging", ("percent", (int) (status.Charge * 100)));
        else if (status.FuelUnits < required)
        {
            reason = Loc.GetString("nsv-bluespace-drive-no-fuel",
                ("fuel", ToSheets(status.FuelUnits)), ("required", ToSheets(required)));
        }

        return reason == null;
    }

    /// <summary>
    /// Spends a jump: empties the drive's charge and burns the jump's fuel. Call once the FTL has
    /// actually engaged, after <see cref="CanJump"/> passed.
    /// </summary>
    public void ConsumeJump(EntityUid shuttleUid, int fuelCost)
    {
        if (!Required || !TryGetDrive(shuttleUid, out var drive))
            return;

        drive.Comp.Charge = 0f;
        var required = FuelRequired(fuelCost);
        if (required > 0)
            _materials.TryChangeMaterialAmount(drive, drive.Comp.FuelMaterial, -required);

        DriveDisplayChanged?.Invoke(shuttleUid);
    }

    public static float ToSheets(int units)
    {
        return MathF.Round(units / (float) UnitsPerSheet, 1);
    }

    private void OnExamined(Entity<NsvBluespaceDriveComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        args.PushMarkup(Loc.GetString("nsv-bluespace-drive-examine",
            ("percent", (int) (ent.Comp.Charge * 100)),
            ("fuel", ToSheets(_materials.GetMaterialAmount(ent, ent.Comp.FuelMaterial)))));
    }

    private void OnShutdown(Entity<NsvBluespaceDriveComponent> ent, ref ComponentShutdown args)
    {
        if (Transform(ent).GridUid is { } grid)
            DriveDisplayChanged?.Invoke(grid);
    }
}
