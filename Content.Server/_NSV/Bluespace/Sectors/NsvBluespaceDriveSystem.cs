using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;
using Content.Shared.Examine;
using Content.Shared.Materials;
using Content.Shared.Power;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

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

    /// <summary>
    /// Power draw (W) while charging. Spinning up a jump competes with shields and weapons for the
    /// ship's power: if the grid can't supply this, the core goes unpowered and stops charging.
    /// </summary>
    [DataField]
    public float ChargingLoad = 15000f;

    /// <summary>
    /// Power draw (W) once fully charged, just holding the charge.
    /// </summary>
    [DataField]
    public float IdleLoad = 1500f;

    // Display bookkeeping so consoles refresh on visible changes, not every tick.
    public int LastDisplayedStep = -1;
    public bool LastPowered;
}

/// <summary>
/// The CTLA-160 jump console that pairs with the core: a jump needs one aboard and powered. Its main
/// window shows the core's state; the original power monitor is still one right-click away.
/// </summary>
[RegisterComponent]
public sealed partial class NsvBluespaceDriveConsoleComponent : Component
{
}

public readonly record struct NsvBluespaceDriveStatus(
    bool HasDrive,
    bool Powered,
    float Charge,
    int FuelUnits,
    bool HasConsole,
    bool ConsolePowered);

/// <summary>
/// Charges drives, gates bluespace jumps on a core + console pair, and spends charge and fuel when a
/// jump engages.
/// </summary>
public sealed partial class NsvBluespaceDriveSystem : EntitySystem
{
    // Material units in one sheet; fuel is shown to players in sheets.
    public const int UnitsPerSheet = 100;

    // Consoles refresh each time the charge crosses a 5% step.
    private const int DisplaySteps = 20;

    [Dependency] private SharedMaterialStorageSystem _materials = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private IConfigurationManager _cfg = default!;

    /// <summary>
    /// Raised with the ship's grid when its jump core or console visibly changes (charge step, power,
    /// fuel, a part added or destroyed).
    /// </summary>
    public event Action<EntityUid>? DriveDisplayChanged;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NsvBluespaceDriveComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<NsvBluespaceDriveComponent, ComponentStartup>(OnPartChanged);
        SubscribeLocalEvent<NsvBluespaceDriveComponent, ComponentShutdown>(OnPartChanged);
        SubscribeLocalEvent<NsvBluespaceDriveComponent, MaterialAmountChangedEvent>(OnFuelChanged);

        SubscribeLocalEvent<NsvBluespaceDriveConsoleComponent, ComponentStartup>(OnPartChanged);
        SubscribeLocalEvent<NsvBluespaceDriveConsoleComponent, ComponentShutdown>(OnPartChanged);
        SubscribeLocalEvent<NsvBluespaceDriveConsoleComponent, PowerChangedEvent>(OnConsolePowerChanged);
        SubscribeLocalEvent<NsvBluespaceDriveConsoleComponent, BoundUIOpenedEvent>(OnConsoleOpened);
        SubscribeLocalEvent<NsvBluespaceDriveConsoleComponent, GetVerbsEvent<AlternativeVerb>>(OnConsoleVerbs);
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
        var query = EntityQueryEnumerator<NsvBluespaceDriveComponent, ApcPowerReceiverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var drive, out var receiver, out var xform))
        {
            var powered = this.IsPowered(uid, EntityManager, receiver);
            drive.Charge = Math.Clamp(drive.Charge + (powered ? rate : -rate), 0f, 1f);

            // Heavy draw only while there's charging to do; a full core idles.
            var load = drive.Charge < 1f ? drive.ChargingLoad : drive.IdleLoad;
            if (!receiver.Load.Equals(load))
                _power.SetLoad(receiver, load);

            var step = (int) (drive.Charge * DisplaySteps);
            if (step == drive.LastDisplayedStep && powered == drive.LastPowered)
                continue;

            drive.LastDisplayedStep = step;
            drive.LastPowered = powered;
            if (xform.GridUid is { } grid)
                NotifyChanged(grid);
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
        var hasConsole = false;
        var consolePowered = false;
        var consoles = AllEntityQuery<NsvBluespaceDriveConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var console, out _, out var xform))
        {
            if (xform.GridUid != shuttleUid || TerminatingOrDeleted(console))
                continue;

            hasConsole = true;
            consolePowered |= this.IsPowered(console, EntityManager);
        }

        if (!TryGetDrive(shuttleUid, out var drive))
            return new NsvBluespaceDriveStatus(false, false, 0f, 0, hasConsole, consolePowered);

        return new NsvBluespaceDriveStatus(
            true,
            this.IsPowered(drive, EntityManager),
            drive.Comp.Charge,
            _materials.GetMaterialAmount(drive, drive.Comp.FuelMaterial),
            hasConsole,
            consolePowered);
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
        return !Required || CanJump(GetStatus(shuttleUid), fuelCost, out reason);
    }

    /// <summary>
    /// The jump rules applied to an already-read <paramref name="status"/>, so callers that check many
    /// destinations (the navigation console) read the ship once. Ignores <see cref="Required"/>.
    /// </summary>
    public bool CanJump(NsvBluespaceDriveStatus status, int fuelCost, out string? reason)
    {
        var required = FuelRequired(fuelCost);
        if (!status.HasDrive)
            reason = Loc.GetString("nsv-bluespace-drive-missing");
        else if (!status.HasConsole)
            reason = Loc.GetString("nsv-bluespace-drive-no-console");
        else if (!status.Powered)
            reason = Loc.GetString("nsv-bluespace-drive-unpowered");
        else if (!status.ConsolePowered)
            reason = Loc.GetString("nsv-bluespace-drive-console-unpowered");
        else if (status.Charge < 1f)
            reason = Loc.GetString("nsv-bluespace-drive-charging", ("percent", (int) (status.Charge * 100)));
        else if (status.FuelUnits < required)
        {
            reason = Loc.GetString("nsv-bluespace-drive-no-fuel",
                ("fuel", ToSheets(status.FuelUnits)), ("required", ToSheets(required)));
        }
        else
            reason = null;

        return reason == null;
    }

    /// <summary>
    /// Spends a jump: empties the drive's charge and burns the jump's fuel. Call once the FTL has
    /// actually engaged, after <see cref="CanJump(EntityUid, int, out string?)"/> passed.
    /// </summary>
    public void ConsumeJump(EntityUid shuttleUid, int fuelCost)
    {
        if (!Required || !TryGetDrive(shuttleUid, out var drive))
            return;

        drive.Comp.Charge = 0f;
        var required = FuelRequired(fuelCost);
        if (required > 0)
            _materials.TryChangeMaterialAmount(drive, drive.Comp.FuelMaterial, -required);

        NotifyChanged(shuttleUid);
    }

    public static float ToSheets(int units)
    {
        return MathF.Round(units / (float) UnitsPerSheet, 1);
    }

    private void NotifyChanged(EntityUid grid)
    {
        UpdateConsoles(grid);
        DriveDisplayChanged?.Invoke(grid);
    }

    /// <summary>
    /// Pushes a fresh state to every jump console window open on <paramref name="grid"/>.
    /// </summary>
    private void UpdateConsoles(EntityUid grid)
    {
        var consoles = AllEntityQuery<NsvBluespaceDriveConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var console, out _, out var xform))
        {
            if (xform.GridUid == grid)
                UpdateConsole(console, grid);
        }
    }

    private void UpdateConsole(EntityUid console, EntityUid grid)
    {
        if (!_ui.IsUiOpen(console, NsvBluespaceDriveConsoleUiKey.Key))
            return;

        var status = GetStatus(grid);
        var ready = CanJump(status, 0, out var reason);
        var hasDrive = TryGetDrive(grid, out var drive);
        var capacity = hasDrive &&
                       TryComp<MaterialStorageComponent>(drive, out var storage) &&
                       storage.StorageLimit is { } limit
            ? ToSheets(limit)
            : 0f;
        var load = hasDrive && TryComp<ApcPowerReceiverComponent>(drive, out var receiver)
            ? (int) receiver.Load
            : 0;

        _ui.SetUiState(console, NsvBluespaceDriveConsoleUiKey.Key, new NsvBluespaceDriveConsoleState(
            status.HasDrive,
            status.Powered,
            status.ConsolePowered,
            (int) (status.Charge * 100),
            ToSheets(status.FuelUnits),
            capacity,
            ready,
            ready ? Loc.GetString("nsv-bluespace-drive-console-ready") : reason!,
            load));
    }

    private void OnExamined(Entity<NsvBluespaceDriveComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        args.PushMarkup(Loc.GetString("nsv-bluespace-drive-examine",
            ("percent", (int) (ent.Comp.Charge * 100)),
            ("fuel", ToSheets(_materials.GetMaterialAmount(ent, ent.Comp.FuelMaterial)))));
    }

    private void OnPartChanged<TComp, TEvent>(EntityUid uid, TComp component, TEvent args)
    {
        if (Transform(uid).GridUid is { } grid)
            NotifyChanged(grid);
    }

    private void OnFuelChanged(Entity<NsvBluespaceDriveComponent> ent, ref MaterialAmountChangedEvent args)
    {
        if (Transform(ent).GridUid is { } grid)
            NotifyChanged(grid);
    }

    private void OnConsolePowerChanged(Entity<NsvBluespaceDriveConsoleComponent> ent, ref PowerChangedEvent args)
    {
        if (Transform(ent).GridUid is { } grid)
            NotifyChanged(grid);
    }

    private void OnConsoleOpened(Entity<NsvBluespaceDriveConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (Transform(ent).GridUid is { } grid)
            UpdateConsole(ent, grid);
    }

    /// <summary>
    /// Keeps the CTLA-160 console's original job: its power monitor opens from the right-click menu.
    /// </summary>
    private void OnConsoleVerbs(Entity<NsvBluespaceDriveConsoleComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_ui.HasUi(ent, PowerMonitoringConsoleUiKey.Key))
            return;

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("nsv-bluespace-drive-console-power-monitor"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/light.svg.192dpi.png")),
            Act = () => _ui.OpenUi(ent.Owner, PowerMonitoringConsoleUiKey.Key, user),
        });
    }
}
