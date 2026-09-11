using System.Numerics;
using Content.Server.Audio;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Shuttles.Components;
using Content.Shared.Temperature;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Content.Shared.Localizations;
using Content.Shared.Power;
using Content.Server.Construction; // Frontier
using Content.Shared.DeviceLinking.Events; // Frontier

namespace Content.Server.Shuttles.Systems;

public sealed partial class ThrusterSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private AmbientSoundSystem _ambient = default!;
    [Dependency] private FixtureSystem _fixtureSystem = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedPointLightSystem _light = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private TurfSystem _turf = default!;

    // Essentially whenever thruster enables we update the shuttle's available impulses which are used for movement.
    // This is done for each direction available.

    public const string BurnFixture = "thruster-burn";

    private readonly HashSet<Vector2i> _nozzleSearchOffsets = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ThrusterComponent, ActivateInWorldEvent>(OnActivateThruster);
        SubscribeLocalEvent<ThrusterComponent, ComponentInit>(OnThrusterInit);
        SubscribeLocalEvent<ThrusterComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ThrusterComponent, ComponentShutdown>(OnThrusterShutdown);
        SubscribeLocalEvent<ThrusterComponent, PowerChangedEvent>(OnPowerChange);
        SubscribeLocalEvent<ThrusterComponent, AnchorStateChangedEvent>(OnAnchorChange);
        SubscribeLocalEvent<ThrusterComponent, MoveEvent>(OnRotate);
        SubscribeLocalEvent<ThrusterComponent, IsHotEvent>(OnIsHotEvent);
        SubscribeLocalEvent<ThrusterComponent, StartCollideEvent>(OnStartCollide);
        SubscribeLocalEvent<ThrusterComponent, EndCollideEvent>(OnEndCollide);

        SubscribeLocalEvent<ThrusterComponent, ExaminedEvent>(OnThrusterExamine);

        SubscribeLocalEvent<ShuttleComponent, TileChangedEvent>(OnShuttleTileChange);

        SubscribeLocalEvent<ThrusterComponent, RefreshPartsEvent>(OnRefreshParts);
        SubscribeLocalEvent<ThrusterComponent, UpgradeExamineEvent>(OnUpgradeExamine);
        SubscribeLocalEvent<ThrusterComponent, SignalReceivedEvent>(OnSignalReceived); // Frontier
    }

    // Frontier: signal handler
    private void OnSignalReceived(EntityUid uid, ThrusterComponent component, ref SignalReceivedEvent args)
    {
        if (args.Port == component.OffPort)
            SetRequestedEnabled(uid, component, false);
        else if (args.Port == component.OnPort)
            SetRequestedEnabled(uid, component, true);
        else if (args.Port == component.TogglePort)
            SetRequestedEnabled(uid, component, !component.Enabled);
    }
    // End Frontier: signal handler

    private void OnThrusterExamine(EntityUid uid, ThrusterComponent component, ExaminedEvent args)
    {
        // Powered is already handled by other power components
        var enabled = Loc.GetString(component.Enabled ? "thruster-comp-enabled" : "thruster-comp-disabled");

        using (args.PushGroup(nameof(ThrusterComponent)))
        {
            args.PushMarkup(enabled);

            if (component.Type == ThrusterType.Linear &&
                EntityManager.TryGetComponent(uid, out TransformComponent? xform) &&
                xform.Anchored)
            {
                var nozzleLocalization = ContentLocalizationManager.FormatDirection(xform.LocalRotation.Opposite().ToWorldVec().GetDir()).ToLower();
                var nozzleDir = Loc.GetString("thruster-comp-nozzle-direction",
                    ("direction", nozzleLocalization));

                args.PushMarkup(nozzleDir);

                var exposed = NozzlesExposed(component, xform);

                var nozzleText =
                    Loc.GetString(exposed ? "thruster-comp-nozzle-exposed" : "thruster-comp-nozzle-not-exposed");

                args.PushMarkup(nozzleText);
            }
        }
    }

    private void OnIsHotEvent(EntityUid uid, ThrusterComponent component, IsHotEvent args)
    {
        args.IsHot = component.Type != ThrusterType.Angular && component.IsOn;
    }

    private void OnShuttleTileChange(EntityUid uid, ShuttleComponent component, ref TileChangedEvent args)
    {
        var grid = Comp<MapGridComponent>(uid);
        var thrusterQuery = GetEntityQuery<ThrusterComponent>();
        var candidates = new HashSet<EntityUid>();

        foreach (var change in args.Changes)
        {
            if (_turf.IsSpace(change.NewTile) == _turf.IsSpace(change.OldTile))
                continue;

            candidates.Clear();
            foreach (var offset in _nozzleSearchOffsets)
            {
                var origin = change.GridIndices - offset;
                var enumerator = _mapSystem.GetAnchoredEntitiesEnumerator(uid, grid, origin);
                while (enumerator.MoveNext(out var ent))
                {
                    if (thrusterQuery.HasComponent(ent.Value))
                        candidates.Add(ent.Value);
                }
            }

            foreach (var ent in candidates)
            {
                if (!thrusterQuery.TryGetComponent(ent, out var thruster) || !thruster.RequireSpace)
                    continue;

                if (thruster.IsOn)
                {
                    if (!CanEnable(ent, thruster))
                        DisableThruster(ent, thruster);
                }
                else
                {
                    TryEnableThruster(ent, thruster);
                }
            }
        }
    }

    private void OnActivateThruster(EntityUid uid, ThrusterComponent component, ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        SetRequestedEnabled(uid, component, !component.Enabled);
        args.Handled = true;
    }

    /// <summary>
    /// If the thruster rotates change the direction where the linear thrust is applied
    /// </summary>
    private void OnRotate(EntityUid uid, ThrusterComponent component, ref MoveEvent args)
    {
        // TODO: Disable visualizer for old direction
        // TODO: Don't make them rotatable and make it require anchoring.

        if (!component.Enabled ||
            !EntityManager.TryGetComponent(uid, out TransformComponent? xform) ||
            !EntityManager.TryGetComponent(xform.GridUid, out ShuttleComponent? shuttleComponent))
        {
            return;
        }

        var canEnable = CanEnable(uid, component);

        // If it's not on then don't enable it inadvertantly (given we don't have an old rotation)
        if (!canEnable && !component.IsOn)
            return;

        // Enable it if it was turned off but new tile is valid
        if (!component.IsOn && canEnable)
        {
            TryEnableThruster(uid, component, xform);
            return;
        }

        // Disable if new tile invalid
        if (component.IsOn && !canEnable)
        {
            DisableThruster(uid, component, args.OldPosition.EntityId, xform, args.OldRotation);
            return;
        }

        var oldDirection = (int)args.OldRotation.GetCardinalDir() / 2;
        var direction = (int)args.NewRotation.GetCardinalDir() / 2;
        var oldShuttleComponent = shuttleComponent;

        if (args.ParentChanged)
        {
            oldShuttleComponent = Comp<ShuttleComponent>(args.OldPosition.EntityId);

            // If no parent change doesn't matter for angular.
            if (component.Type == ThrusterType.Angular)
            {
                oldShuttleComponent.AngularThrust -= component.Thrust;
                DebugTools.Assert(oldShuttleComponent.AngularThrusters.Contains(uid));
                oldShuttleComponent.AngularThrusters.Remove(uid);

                shuttleComponent.AngularThrust += component.Thrust;
                DebugTools.Assert(!shuttleComponent.AngularThrusters.Contains(uid));
                shuttleComponent.AngularThrusters.Add(uid);
                return;
            }
        }

        if (component.Type == ThrusterType.Linear)
        {
            oldShuttleComponent.LinearThrust[oldDirection] -= component.Thrust;
            oldShuttleComponent.BaseLinearThrust[oldDirection] -= component.BaseThrust;
            DebugTools.Assert(oldShuttleComponent.LinearThrusters[oldDirection].Contains(uid));
            oldShuttleComponent.LinearThrusters[oldDirection].Remove(uid);

            shuttleComponent.LinearThrust[direction] += component.Thrust;
            shuttleComponent.BaseLinearThrust[direction] += component.BaseThrust;
            DebugTools.Assert(!shuttleComponent.LinearThrusters[direction].Contains(uid));
            shuttleComponent.LinearThrusters[direction].Add(uid);
            SyncLinearFiring(uid, component, shuttleComponent, direction);
        }
    }

    private void OnAnchorChange(EntityUid uid, ThrusterComponent component, ref AnchorStateChangedEvent args)
    {
        if (args.Anchored)
            TryEnableThruster(uid, component);
        else
            DisableThruster(uid, component);
    }

    private void OnThrusterInit(EntityUid uid, ThrusterComponent component, ComponentInit args)
    {
        // Frontier: togglable thrusters
        if (TryComp<ApcPowerReceiverComponent>(uid, out var apcPower) && component.OriginalLoad == 0)
        {
            component.OriginalLoad = apcPower.Load;
        }
        // End Frontier: togglable thrusters

        RegisterNozzleOffsets(component);
        _ambient.SetAmbience(uid, false);

        if (component.Enabled)
            TryEnableThruster(uid, component);
    }

    private void OnMapInit(Entity<ThrusterComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextFire = _timing.CurTime + ent.Comp.FireCooldown;
    }

    private void OnThrusterShutdown(EntityUid uid, ThrusterComponent component, ComponentShutdown args)
    {
        DisableThruster(uid, component);
    }

    private void OnPowerChange(EntityUid uid, ThrusterComponent component, ref PowerChangedEvent args)
    {
        if (args.Powered)
            TryEnableThruster(uid, component);
        else
            DisableThruster(uid, component);
    }

    private void SetRequestedEnabled(EntityUid uid, ThrusterComponent component, bool enabled)
    {
        component.Enabled = enabled;

        if (TryComp<ApcPowerReceiverComponent>(uid, out var apcPower) && component.OriginalLoad != 0f)
        {
            var load = enabled ? component.OriginalLoad : 1f;
            if (apcPower.Load != load)
                apcPower.Load = load;
        }

        if (enabled)
            TryEnableThruster(uid, component);
        else
            DisableThruster(uid, component);
    }

    private void RegisterNozzleOffsets(ThrusterComponent component)
    {
        foreach (var offset in component.NozzleOffsets)
        {
            for (var rotation = 0; rotation < 4; rotation++)
                _nozzleSearchOffsets.Add(RotateNozzleOffset(offset, rotation));
        }
    }

    public bool TryEnableThruster(EntityUid uid, ThrusterComponent component, TransformComponent? xform = null)
    {
        if (component.IsOn)
            return true;

        if (!Resolve(uid, ref xform) || !CanEnable(uid, component))
            return false;

        EnableThruster(uid, component, xform);
        return component.IsOn;
    }

    /// <summary>
    /// Tries to enable the thruster and turn it on. If it's already enabled it does nothing.
    /// </summary>
    private void EnableThruster(EntityUid uid, ThrusterComponent component, TransformComponent? xform = null)
    {
        if (component.IsOn ||
            !Resolve(uid, ref xform) ||
            !EntityManager.TryGetComponent(xform.GridUid, out ShuttleComponent? shuttleComponent))
        {
            return;
        }

        component.IsOn = true;

        // Logger.DebugS("thruster", $"Enabled thruster {uid}");

        switch (component.Type)
        {
            case ThrusterType.Linear:
                var direction = (int)xform.LocalRotation.GetCardinalDir() / 2;

                shuttleComponent.LinearThrust[direction] += component.Thrust;
                shuttleComponent.BaseLinearThrust[direction] += component.BaseThrust;
                DebugTools.Assert(!shuttleComponent.LinearThrusters[direction].Contains(uid));
                shuttleComponent.LinearThrusters[direction].Add(uid);
                SyncLinearFiring(uid, component, shuttleComponent, direction);

                // Don't just add / remove the fixture whenever the thruster fires because perf
                if (EntityManager.TryGetComponent(uid, out PhysicsComponent? physicsComponent) &&
                    component.BurnPoly.Count > 0)
                {
                    var shape = new PolygonShape();
                    shape.Set(component.BurnPoly);
                    _fixtureSystem.TryCreateFixture(uid, shape, BurnFixture, hard: false, collisionLayer: (int)CollisionGroup.FullTileMask, body: physicsComponent);
                }

                break;
            case ThrusterType.Angular:
                shuttleComponent.AngularThrust += component.Thrust;
                DebugTools.Assert(!shuttleComponent.AngularThrusters.Contains(uid));
                shuttleComponent.AngularThrusters.Add(uid);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        if (EntityManager.TryGetComponent(uid, out AppearanceComponent? appearance))
        {
            _appearance.SetData(uid, ThrusterVisualState.State, true, appearance);
        }

        if (_light.TryGetLight(uid, out var pointLightComponent))
        {
            _light.SetEnabled(uid, true, pointLightComponent);
        }

        _ambient.SetAmbience(uid, true);
        RefreshCenter(uid, shuttleComponent);
    }

    private void SyncLinearFiring(EntityUid uid, ThrusterComponent component, ShuttleComponent shuttle, int direction)
    {
        var flag = (DirectionFlag) (1 << direction);
        component.Firing = (shuttle.ThrustDirections & flag) != 0;

        if (TryComp<AppearanceComponent>(uid, out var appearance))
            _appearance.SetData(uid, ThrusterVisualState.Thrusting, component.Firing, appearance);
    }

    /// <summary>
    /// Refreshes the center of thrust for movement calculations.
    /// </summary>
    private void RefreshCenter(EntityUid uid, ShuttleComponent shuttle)
    {
        // TODO: Only refresh relevant directions.
        var center = Vector2.Zero;
        var thrustQuery = GetEntityQuery<ThrusterComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        foreach (var dir in new[]
                     { Direction.South, Direction.East, Direction.North, Direction.West })
        {
            var index = (int)dir / 2;
            var pop = shuttle.LinearThrusters[index];
            var totalThrust = 0f;

            foreach (var ent in pop)
            {
                if (!thrustQuery.TryGetComponent(ent, out var thruster) || !xformQuery.TryGetComponent(ent, out var xform))
                    continue;

                center += xform.LocalPosition * thruster.Thrust;
                totalThrust += thruster.Thrust;
            }

            center /= pop.Count * totalThrust;
            shuttle.CenterOfThrust[index] = center;
        }
    }

    public void DisableThruster(EntityUid uid, ThrusterComponent component, TransformComponent? xform = null, Angle? angle = null)
    {
        if (!Resolve(uid, ref xform))
            return;

        DisableThruster(uid, component, xform.GridUid, xform, angle);
    }

    /// <summary>
    /// Tries to disable the thruster.
    /// </summary>
    public void DisableThruster(EntityUid uid, ThrusterComponent component, EntityUid? gridId, TransformComponent? xform = null, Angle? angle = null)
    {
        if (!Resolve(uid, ref xform))
            return;

        var wasOn = component.IsOn;
        component.IsOn = false;
        component.Firing = false;

        if (TryComp<AppearanceComponent>(uid, out var appearance))
        {
            _appearance.SetData(uid, ThrusterVisualState.State, false, appearance);
            _appearance.SetData(uid, ThrusterVisualState.Thrusting, false, appearance);
        }

        if (_light.TryGetLight(uid, out var pointLightComponent))
            _light.SetEnabled(uid, false, pointLightComponent);

        _ambient.SetAmbience(uid, false);

        if (TryComp<PhysicsComponent>(uid, out var physicsComponent))
            _fixtureSystem.DestroyFixture(uid, BurnFixture, body: physicsComponent);

        component.Colliding.Clear();

        if (!wasOn || gridId == null || !TryComp<ShuttleComponent>(gridId.Value, out var shuttleComponent))
            return;

        switch (component.Type)
        {
            case ThrusterType.Linear:
                angle ??= xform.LocalRotation;
                var direction = (int) angle.Value.GetCardinalDir() / 2;
                shuttleComponent.LinearThrust[direction] -= component.Thrust;
                shuttleComponent.BaseLinearThrust[direction] -= component.BaseThrust;
                shuttleComponent.LinearThrusters[direction].Remove(uid);
                break;
            case ThrusterType.Angular:
                shuttleComponent.AngularThrust -= component.Thrust;
                shuttleComponent.AngularThrusters.Remove(uid);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        RefreshCenter(uid, shuttleComponent);
    }

    public bool CanEnable(EntityUid uid, ThrusterComponent component)
    {
        if (!component.Enabled || component.LifeStage > ComponentLifeStage.Running)
            return false;

        var xform = Transform(uid);
        if (!xform.Anchored ||
            xform.GridUid == null ||
            !HasComp<ShuttleComponent>(xform.GridUid.Value) ||
            !this.IsPowered(uid, EntityManager))
        {
            return false;
        }

        if (component.RequireSpace && !NozzlesExposed(component, xform))
            return false;

        var attempt = new ThrusterEnableAttemptEvent(false);
        RaiseLocalEvent(uid, ref attempt);
        return !attempt.Cancelled;
    }

    private bool NozzlesExposed(ThrusterComponent component, TransformComponent xform)
    {
        if (xform.GridUid == null)
            return true;

        var grid = Comp<MapGridComponent>(xform.GridUid.Value);
        var origin = new Vector2i(
            (int) Math.Floor(xform.LocalPosition.X),
            (int) Math.Floor(xform.LocalPosition.Y));

        foreach (var localOffset in component.NozzleOffsets)
        {
            var offset = RotateNozzleOffset(localOffset, (int) xform.LocalRotation.GetCardinalDir() / 2);
            var tile = _mapSystem.GetTileRef(xform.GridUid.Value, grid, origin + offset);
            if (!_turf.IsSpace(tile))
                return false;
        }

        return true;
    }

    private static Vector2i RotateNozzleOffset(Vector2i offset, int rotation)
    {
        return (rotation & 3) switch
        {
            0 => offset,
            1 => new Vector2i(-offset.Y, offset.X),
            2 => new Vector2i(-offset.X, -offset.Y),
            3 => new Vector2i(offset.Y, -offset.X),
            _ => offset,
        };
    }

    #region Burning

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ThrusterComponent>();
        var curTime = _timing.CurTime;

        while (query.MoveNext(out var comp))
        {
            if (comp.NextFire > curTime)
                continue;

            comp.NextFire += comp.FireCooldown;

            if (!comp.Firing || comp.Colliding.Count == 0 || comp.Damage == null)
                continue;

            foreach (var uid in comp.Colliding.ToArray())
            {
                _damageable.TryChangeDamage(uid, comp.Damage);
            }
        }
    }

    private void OnStartCollide(EntityUid uid, ThrusterComponent component, ref StartCollideEvent args)
    {
        if (args.OurFixtureId != BurnFixture)
            return;

        component.Colliding.Add(args.OtherEntity);
    }

    private void OnEndCollide(EntityUid uid, ThrusterComponent component, ref EndCollideEvent args)
    {
        if (args.OurFixtureId != BurnFixture)
            return;

        component.Colliding.Remove(args.OtherEntity);
    }

    /// <summary>
    /// Considers a thrust direction as being active.
    /// </summary>
    public void EnableLinearThrustDirection(ShuttleComponent component, DirectionFlag direction)
    {
        if ((component.ThrustDirections & direction) != 0x0)
            return;

        component.ThrustDirections |= direction;

        var index = GetFlagIndex(direction);
        var appearanceQuery = GetEntityQuery<AppearanceComponent>();
        var thrusterQuery = GetEntityQuery<ThrusterComponent>();

        foreach (var uid in component.LinearThrusters[index])
        {
            if (!thrusterQuery.TryGetComponent(uid, out var comp))
                continue;

            comp.Firing = true;
            appearanceQuery.TryGetComponent(uid, out var appearance);
            _appearance.SetData(uid, ThrusterVisualState.Thrusting, true, appearance);
        }
    }

    /// <summary>
    /// Disables a thrust direction.
    /// </summary>
    public void DisableLinearThrustDirection(ShuttleComponent component, DirectionFlag direction)
    {
        if ((component.ThrustDirections & direction) == 0x0)
            return;

        component.ThrustDirections &= ~direction;

        var index = GetFlagIndex(direction);
        var appearanceQuery = GetEntityQuery<AppearanceComponent>();
        var thrusterQuery = GetEntityQuery<ThrusterComponent>();

        foreach (var uid in component.LinearThrusters[index])
        {
            if (!thrusterQuery.TryGetComponent(uid, out var comp))
                continue;

            appearanceQuery.TryGetComponent(uid, out var appearance);
            comp.Firing = false;
            _appearance.SetData(uid, ThrusterVisualState.Thrusting, false, appearance);
        }
    }

    public void DisableLinearThrusters(ShuttleComponent component)
    {
        foreach (DirectionFlag dir in Enum.GetValues(typeof(DirectionFlag)))
        {
            DisableLinearThrustDirection(component, dir);
        }

        DebugTools.Assert(component.ThrustDirections == DirectionFlag.None);
    }

    public void SetAngularThrust(ShuttleComponent component, bool on)
    {
        var appearanceQuery = GetEntityQuery<AppearanceComponent>();
        var thrusterQuery = GetEntityQuery<ThrusterComponent>();

        if (on)
        {
            foreach (var uid in component.AngularThrusters)
            {
                if (!thrusterQuery.TryGetComponent(uid, out var comp))
                    continue;

                appearanceQuery.TryGetComponent(uid, out var appearance);
                comp.Firing = true;
                _appearance.SetData(uid, ThrusterVisualState.Thrusting, true, appearance);
            }
        }
        else
        {
            foreach (var uid in component.AngularThrusters)
            {
                if (!thrusterQuery.TryGetComponent(uid, out var comp))
                    continue;

                appearanceQuery.TryGetComponent(uid, out var appearance);
                comp.Firing = false;
                _appearance.SetData(uid, ThrusterVisualState.Thrusting, false, appearance);
            }
        }
    }

    private void OnRefreshParts(EntityUid uid, ThrusterComponent component, RefreshPartsEvent args)
    {
        if (component.IsOn) // safely disable thruster to prevent negative thrust
            DisableThruster(uid, component);

        var thrustRating = args.PartRatings[component.MachinePartThrust];

        component.Thrust = component.BaseThrust * MathF.Pow(component.PartRatingThrustMultiplier, thrustRating - 1);

        if (component.Enabled)
            TryEnableThruster(uid, component);
    }

    private void OnUpgradeExamine(EntityUid uid, ThrusterComponent component, UpgradeExamineEvent args)
    {
        args.AddPercentageUpgrade("thruster-comp-upgrade-thrust", component.Thrust / component.BaseThrust);
    }

    //private void OnEmpPulse(EntityUid uid, ThrusterComponent component, ref EmpPulseEvent args)
    //{
    //    if (component.Enabled && !component.ThrusterIgnoreEmp)
    //    {
    //        args.Affected = true;
    //        args.Disabled = true;
    //    }
    //}

    //[ByRefEvent]
    //public record struct ThrusterToggleAttemptEvent(bool Cancelled);

    #endregion

    private int GetFlagIndex(DirectionFlag flag)
    {
        return (int)Math.Log2((int)flag);
    }
}

[ByRefEvent]
public record struct ThrusterEnableAttemptEvent(bool Cancelled);
