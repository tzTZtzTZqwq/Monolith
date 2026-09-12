using System.Numerics;
using Content.Server._Mono.NPC.HTN;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.NPC.HTN;
using Content.Server.NPC.HTN;
using Content.Server.Power.EntitySystems;
using Content.Shared.Whitelist;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._NSV.NPC;

/// <summary>
/// Standalone NSV ship combat AI. Replaces HTN for cores carrying <see cref="NsvShipAiComponent"/>.
///
/// Decision layer only: it selects a target and tactical parameters, then hands execution to the
/// shared <see cref="ShipSteeringSystem"/> (movement, collision/projectile avoidance, real thrust)
/// and <see cref="ShipTargetingSystem"/> (per-gun ballistics, firing). It never touches thrusters,
/// physics, or guns directly, so all of that battle-tested behavior is reused.
///
/// This is intentionally simple for now (nearest hostile, single engage range). It is the seam where
/// threat fields, shield/hull/engine awareness, and approach/brawl/retreat states will plug in.
/// </summary>
public sealed class NsvShipAiSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private readonly ShipSteeringSystem _steering = default!;
    [Dependency] private readonly ShipTargetingSystem _targeting = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;

    private readonly HashSet<Entity<NsvShipTargetComponent>> _candidates = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NsvShipAiComponent, MapInitEvent>(OnMapInit);
    }

    /// <summary>
    /// This AI and HTN both drive the same steering/targeting; never let them coexist on one core.
    /// Cores often inherit an HTN component from their Mono parent prototype, so strip it here.
    /// </summary>
    private void OnMapInit(Entity<NsvShipAiComponent> ent, ref MapInitEvent args)
    {
        if (HasComp<HTNComponent>(ent))
            RemComp<HTNComponent>(ent);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<NsvShipAiComponent>();

        while (query.MoveNext(out var uid, out var ai))
        {
            var shipUid = Transform(uid).GridUid;
            if (shipUid == null)
                continue;

            // Test mode: spin in place instead of fighting (see TestSpinSpeed).
            if (ai.TestSpinSpeed is { } spinSpeed)
            {
                SpinInPlace((uid, ai), spinSpeed, frameTime);
                continue;
            }

            // Test mode: keep clearance from nearby grids instead of fighting (see TestKeepDistance).
            if (ai.TestKeepDistance is { } keepDist)
            {
                ai.Target = null;
                KeepDistanceFromGrids((uid, ai), shipUid.Value, keepDist);
                continue;
            }

            ai.DecisionAccumulator -= frameTime;
            if (ai.DecisionAccumulator <= 0f)
            {
                ai.DecisionAccumulator += ai.DecisionInterval;
                Decide((uid, ai), shipUid.Value);
            }

            // Every frame: keep steering/targeting pointed at the live target position so leading and
            // avoidance stay accurate between decisions. Stop cleanly if the target is gone.
            if (ai.Target is not { } target || TerminatingOrDeleted(target))
            {
                ClearCommands(uid);
                ai.Target = null;
                continue;
            }

            var targetCoords = new EntityCoordinates(target, Vector2.Zero);

            var steerer = _steering.Steer(uid, targetCoords);
            if (steerer != null)
            {
                steerer.Range = ai.EngageRange;
                steerer.RangeTolerance = ai.EngageRangeTolerance;
                steerer.InRangeMaxSpeed = ai.InRangeMaxSpeed;
                steerer.AlwaysFaceTarget = ai.AlwaysFaceTarget;
                steerer.AvoidProjectiles = ai.AvoidProjectiles;
                steerer.TargetRotation = ai.TargetRotation;
                steerer.Mode = ai.SteeringMode;
            }

            var targeter = _targeting.Target(uid, targetCoords);
            if (targeter != null)
                targeter.LeadingAccuracy = ai.LeadingAccuracy;
        }
    }

    /// <summary>
    /// Pick or keep a target. Nearest hostile wins, with stickiness toward the current one.
    /// </summary>
    private void Decide(Entity<NsvShipAiComponent> ent, EntityUid shipUid)
    {
        var ai = ent.Comp;
        var xform = Transform(ent);
        var ownPos = _transform.GetMapCoordinates(xform);

        _candidates.Clear();
        _lookup.GetEntitiesInRange(ownPos, ai.SearchRange, _candidates);

        EntityUid? best = null;
        var bestScore = float.NegativeInfinity;

        foreach (var (candidate, targetComp) in _candidates)
        {
            if (!IsValidTarget(ent, shipUid, candidate, targetComp, ownPos, out var distSq))
                continue;

            // Closer is better; sticky bonus keeps us from flip-flopping between equidistant targets.
            var score = -distSq;
            if (candidate == ai.Target)
                score *= 1f / ai.TargetStickiness;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        ai.Target = best;
    }

    private bool IsValidTarget(
        Entity<NsvShipAiComponent> ent,
        EntityUid shipUid,
        EntityUid candidate,
        NsvShipTargetComponent targetComp,
        MapCoordinates ownPos,
        out float distSq)
    {
        distSq = float.MaxValue;

        if (TerminatingOrDeleted(candidate))
            return false;

        var targetXform = Transform(candidate);
        var targetGrid = targetXform.GridUid;

        // same grid-mode rules the HTN query uses
        if (targetComp.NeedGrid != NsvShipTargetGridMode.Either &&
            (targetComp.NeedGrid == NsvShipTargetGridMode.OnGrid) == (targetGrid == null))
            return false;

        // never target our own ship
        if (targetGrid == shipUid)
            return false;

        if (targetComp.NeedPower && !this.IsPowered(candidate, EntityManager))
            return false;

        if (targetGrid != null && _whitelist.IsBlacklistPass(ent.Comp.Blacklist, targetGrid.Value))
            return false;

        if (!_factions.IsHostile(ent.Owner, candidate))
            return false;

        var targetPos = _transform.GetMapCoordinates(candidate);
        if (targetPos.MapId != ownPos.MapId)
            return false;

        distSq = (targetPos.Position - ownPos.Position).LengthSquared();
        return distSq <= ent.Comp.SearchRange * ent.Comp.SearchRange;
    }

    private void ClearCommands(EntityUid uid)
    {
        _steering.Stop(uid);
        _targeting.Stop(uid);
    }

    /// <summary>
    /// Test behavior: hold position and spin at a fixed angular velocity.
    /// The steerer's destination is our own core (zero distance, no translation) while
    /// <see cref="ShipSteererComponent.InRangeRotation"/> advances every frame, so the rotation PID
    /// chases a moving angle and never catches it. Avoidance is off so nothing overrides the test;
    /// MaxRotateRate below the spin speed keeps the "arrived" check from cutting rotation.
    /// </summary>
    private void SpinInPlace(Entity<NsvShipAiComponent> ent, float spinSpeed, float frameTime)
    {
        var ai = ent.Comp;
        ai.TestSpinAngle += new Angle(spinSpeed * frameTime);

        var steerer = _steering.Steer(ent.Owner, new EntityCoordinates(ent.Owner, Vector2.Zero));
        if (steerer != null)
        {
            steerer.Range = 1f;
            steerer.RangeTolerance = null;
            steerer.InRangeMaxSpeed = null;
            steerer.MaxRotateRate = spinSpeed * 0.5f;
            steerer.AlwaysFaceTarget = false;
            steerer.AvoidCollisions = false;
            steerer.AvoidProjectiles = false;
            steerer.Mode = ShipSteeringMode.GoToRange;
            steerer.InRangeRotation = ai.TestSpinAngle;
        }

        _targeting.Stop(ent.Owner);
    }

    /// <summary>
    /// Test behavior: hold a minimum hull-to-hull clearance from every other grid on the map.
    /// Each frame we sum a repulsion vector from every grid that is too close (weighted by how
    /// badly it violates the desired clearance) and steer towards a waypoint along that vector.
    /// With no violating grids the waypoint collapses onto ourselves and the ship holds position.
    /// This is the "AI feeds parameterized waypoints" pattern: the steering system still owns all
    /// actual thrust/navigation, including its own collision avoidance while repositioning.
    /// </summary>
    private void KeepDistanceFromGrids(Entity<NsvShipAiComponent> ent, EntityUid shipUid, float desired)
    {
        var ownPos = _transform.GetMapCoordinates(Transform(ent));
        var repulsion = Vector2.Zero;

        var grids = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (grids.MoveNext(out var gridUid, out var grid, out var gridXform))
        {
            if (gridUid == shipUid || gridXform.MapID != ownPos.MapId)
                continue;

            var away = ownPos.Position - _transform.GetWorldPosition(gridXform);
            var dist = away.Length();
            // treat the grid as a circle around its center for a cheap hull-to-hull estimate
            var radius = MathF.Max(grid.LocalAABB.Width, grid.LocalAABB.Height) / 2f;
            var violation = desired - (dist - radius);
            if (violation <= 0f)
                continue;

            var dir = dist > 0f ? away / dist : Vector2.UnitY;
            repulsion += dir * violation;
        }

        _targeting.Stop(ent.Owner);

        if (repulsion.LengthSquared() <= 0f)
        {
            // nothing too close: hold position (destination = self, zero thrust)
            var hold = _steering.Steer(ent.Owner, new EntityCoordinates(ent.Owner, Vector2.Zero));
            if (hold != null)
            {
                hold.Range = 1f;
                hold.RangeTolerance = null;
                hold.InRangeMaxSpeed = null;
                hold.AlwaysFaceTarget = false;
                hold.AvoidCollisions = false;
                hold.AvoidProjectiles = false;
                hold.Mode = ShipSteeringMode.GoToRange;
            }
            return;
        }

        var waypoint = _transform.ToCoordinates(new MapCoordinates(ownPos.Position + repulsion, ownPos.MapId));
        var steerer = _steering.Steer(ent.Owner, waypoint);
        if (steerer != null)
        {
            steerer.Range = 1f;
            steerer.RangeTolerance = null;
            steerer.InRangeMaxSpeed = null;
            steerer.AlwaysFaceTarget = false;
            steerer.AvoidCollisions = true; // don't ram something else while backing off
            steerer.AvoidProjectiles = false;
            steerer.Mode = ShipSteeringMode.GoToRange;
        }
    }
}
