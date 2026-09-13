using System.Numerics;
using Content.Server._Mono.FireControl;
using Content.Server._Mono.NPC.HTN;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.NPC.HTN;
using Content.Server.NPC.HTN;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Spawners;

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
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly ShipSteeringSystem _steering = default!;
    [Dependency] private readonly ShipTargetingSystem _targeting = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;

    private const float PerceptionSpacing = 3f;

    private readonly HashSet<Entity<NsvShipTargetComponent>> _candidates = new();
    private readonly HashSet<Entity<NsvShipTargetComponent>> _threats = new();
    private readonly HashSet<Entity<ShipShieldEmitterComponent>> _emitters = new();
    private readonly HashSet<Entity<FireControllableComponent>> _guns = new();

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

            // Perception: refresh shield stress and weapon range every few seconds; cheap reads
            // of cached values happen every frame.
            ai.PerceptionAccum -= frameTime;
            if (ai.PerceptionAccum <= 0f)
            {
                ai.PerceptionAccum += PerceptionSpacing;
                RefreshPerception((uid, ai), shipUid.Value);
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

            // Full withdrawal: shields critical, stop maneuvering for advantage and open distance
            // from the hostile mass while still facing and firing at the target.
            if (ai.CachedShieldStress >= ai.WithdrawStressThreshold)
            {
                SteerWithdraw((uid, ai), targetCoords);
            }
            else
            {
                var engage = ai.EngageRange;
                if (ai.AutoEngageRange && ai.CachedWeaponRange > 0f)
                    engage = ai.CachedWeaponRange * ai.RangeScale + ai.CachedShieldStress * ai.StressRangeBonus;

                // Navigation point: the target itself, or a flank point orbiting it when the
                // threat layout suggests an attack side.
                var navCoords = targetCoords;
                var attackVec = CalcAttackVector((uid, ai), shipUid.Value, target);
                if (attackVec is { } vec)
                {
                    var targetMap = _transform.GetMapCoordinates(target);
                    navCoords = _transform.ToCoordinates(
                        new MapCoordinates(targetMap.Position + vec * engage, targetMap.MapId));
                }

                var steerer = _steering.Steer(uid, navCoords);
                if (steerer != null)
                {
                    steerer.Range = attackVec != null ? 1f : engage;
                    steerer.RangeTolerance = ai.EngageRangeTolerance;
                    steerer.InRangeMaxSpeed = ai.InRangeMaxSpeed;
                    steerer.AlwaysFaceTarget = ai.AlwaysFaceTarget;
                    steerer.AvoidProjectiles = ai.AvoidProjectiles;
                    steerer.TargetRotation = ai.TargetRotation;
                    steerer.Mode = ai.SteeringMode;
                    steerer.FacingCoordinates = null;
                }
            }

            var targeter = _targeting.Target(uid, targetCoords);
            if (targeter != null)
                targeter.LeadingAccuracy = ai.LeadingAccuracy;
        }
    }

    /// <summary>
    /// Pick or keep a target. Threat value over squared distance wins (a heavily-armed distant
    /// hostile can outrank a nearby gunboat), with stickiness toward the current one.
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

            // Threat value (longest weapon range of the target's grid) over squared distance.
            // The offset keeps near-ties from flip-flopping when distances are tiny.
            var targetGrid = Transform(candidate).GridUid;
            var value = CalcWeaponRange(targetGrid ?? candidate);
            var score = (value + 100f) / (distSq + ai.TargetDistanceOffset);
            if (candidate == ai.Target)
                score *= ai.TargetStickiness;

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

    /// <summary>
    /// Rescan the ship's shield emitters and weapons and update the cached perception values
    /// plus the blackboard. Grids have no DamageableComponent in this codebase, so stress is
    /// shield-only; hull awareness is deferred until a grid-level damage metric exists.
    /// </summary>
    private void RefreshPerception(Entity<NsvShipAiComponent> ent, EntityUid shipUid)
    {
        var ai = ent.Comp;
        ai.CachedShieldStress = CalcShieldStress(shipUid);
        ai.CachedWeaponRange = CalcWeaponRange(shipUid);

        ai.Blackboard.SetValue(NsvAiKeys.ShieldStress, ai.CachedShieldStress);
        ai.Blackboard.SetValue(NsvAiKeys.WeaponRange, ai.CachedWeaponRange);
        ai.Blackboard.SetValue(NsvAiKeys.Withdrawing, ai.CachedShieldStress >= ai.WithdrawStressThreshold);
    }

    /// <summary>
    /// Worst-case shield stress on the grid: the highest emitter Damage/DamageLimit ratio, or 1
    /// if any emitter is in recharge mode (already overloaded). 0 for ships without emitters.
    /// </summary>
    private float CalcShieldStress(EntityUid shipUid)
    {
        if (!TryComp<MapGridComponent>(shipUid, out var grid))
            return 0f;

        _emitters.Clear();
        _lookup.GetLocalEntitiesIntersecting(shipUid, grid.LocalAABB, _emitters);

        var stress = 0f;
        foreach (var emitter in _emitters)
        {
            if (emitter.Comp.Recharging)
            {
                stress = 1f;
                break;
            }

            stress = MathF.Max(stress, emitter.Comp.Damage / emitter.Comp.DamageLimit);
        }

        _emitters.Clear();
        return Math.Clamp(stress, 0f, 1f);
    }

    /// <summary>
    /// Longest weapon range on the grid in meters: hitscan reads its raycast max distance,
    /// projectiles get muzzle speed times despawn lifetime. 0 = no scannable weapons.
    /// </summary>
    private float CalcWeaponRange(EntityUid shipUid)
    {
        if (!TryComp<MapGridComponent>(shipUid, out var grid))
            return 0f;

        _guns.Clear();
        _lookup.GetLocalEntitiesIntersecting(shipUid, grid.LocalAABB, _guns);

        var best = 0f;
        foreach (var gunEnt in _guns)
        {
            if (!Transform(gunEnt).Anchored || !TryComp<GunComponent>(gunEnt, out var gun))
                continue;

            if (!_gun.TryNextShootPrototype((gunEnt, gun), out var proto))
                continue;

            float range;
            if (proto.TryGetComponent<HitscanAmmoComponent>(out _, Factory))
            {
                if (!proto.TryGetComponent<HitscanBasicRaycastComponent>(out var raycast, Factory))
                    continue;

                range = raycast.MaxDistance;
            }
            else if (proto.TryGetComponent<TimedDespawnComponent>(out var despawn, Factory))
            {
                range = gun.ProjectileSpeedModified * despawn.Lifetime;
            }
            else
            {
                continue;
            }

            best = MathF.Max(best, range);
        }

        _guns.Clear();
        return best;
    }

    /// <summary>
    /// Withdrawal steering: build a threat vector from every hostile in range (weight falls off
    /// with distance) and navigate along its inverse, while <see cref="ShipSteererComponent.FacingCoordinates"/>
    /// keeps the nose on the current target so the ship fights while retreating. This is the
    /// keep-distance pattern with hostile ships as the repulsion source.
    /// </summary>
    private void SteerWithdraw(Entity<NsvShipAiComponent> ent, EntityCoordinates targetCoords)
    {
        var ai = ent.Comp;
        var ownPos = _transform.GetMapCoordinates(Transform(ent));
        var away = Vector2.Zero;

        _threats.Clear();
        _lookup.GetEntitiesInRange(ownPos, ai.ThreatMaxDistance, _threats);

        foreach (var threat in _threats)
        {
            var candidate = threat.Owner;
            if (candidate == ent.Owner || TerminatingOrDeleted(candidate))
                continue;

            if (!_factions.IsHostile(ent.Owner, candidate))
                continue;

            var pos = _transform.GetMapCoordinates(candidate);
            if (pos.MapId != ownPos.MapId)
                continue;

            var to = pos.Position - ownPos.Position;
            var d = to.Length();
            if (d <= 0f)
                continue;

            var w = 1f - MathF.Pow(d / ai.ThreatMaxDistance, ai.ThreatDistancePower);
            away -= to / d * w;
        }

        _threats.Clear();

        var awayDir = away.LengthSquared() > 0f
            ? away.Normalized()
            : NormalizedOrZero(ownPos.Position - _transform.GetMapCoordinates(targetCoords.EntityId).Position);

        // No withdraw direction available (no hostiles, target on top of us): stand and fight.
        if (awayDir == Vector2.Zero)
            return;

        var waypoint = _transform.ToCoordinates(
            new MapCoordinates(ownPos.Position + awayDir * ai.WithdrawDistance, ownPos.MapId));

        var steerer = _steering.Steer(ent.Owner, waypoint);
        if (steerer != null)
        {
            steerer.Range = 1f;
            steerer.RangeTolerance = null;
            steerer.InRangeMaxSpeed = null;
            steerer.AlwaysFaceTarget = false;
            steerer.AvoidCollisions = true;
            steerer.AvoidProjectiles = true;
            steerer.Mode = ShipSteeringMode.GoToRange;
            steerer.FacingCoordinates = targetCoords;
        }
    }

    private static Vector2 NormalizedOrZero(Vector2 vec)
    {
        return vec.LengthSquared() == 0 ? Vector2.Zero : vec.Normalized();
    }

    /// <summary>
    /// Pick the direction (from the target towards us) to establish our engagement position on.
    /// With one dominant threat nearby we take the flank: rotate the away-from-threat direction
    /// by orbitSign * 90 degrees, so multiple AI ships spread to different sides instead of
    /// stacking on one approach vector. With no meaningful threat layout we keep whatever side
    /// we're already on (approach straight along the current bearing).
    /// </summary>
    private Vector2? CalcAttackVector(Entity<NsvShipAiComponent> ent, EntityUid shipUid, EntityUid target)
    {
        var ai = ent.Comp;
        var ownPos = _transform.GetMapCoordinates(Transform(ent));
        var targetPos = _transform.GetMapCoordinates(target);
        if (targetPos.MapId != ownPos.MapId)
            return null;

        var away = Vector2.Zero;
        var threats = 0;

        _threats.Clear();
        _lookup.GetEntitiesInRange(ownPos, ai.ThreatMaxDistance, _threats);

        foreach (var threat in _threats)
        {
            var candidate = threat.Owner;
            if (candidate == ent.Owner || candidate == target || TerminatingOrDeleted(candidate))
                continue;

            if (Transform(candidate).GridUid == shipUid)
                continue;

            if (!_factions.IsHostile(ent.Owner, candidate))
                continue;

            var pos = _transform.GetMapCoordinates(candidate);
            if (pos.MapId != ownPos.MapId)
                continue;

            var to = pos.Position - ownPos.Position;
            var d = to.Length();
            if (d <= 0f)
                continue;

            var w = 1f - MathF.Pow(d / ai.ThreatMaxDistance, ai.ThreatDistancePower);
            away += to / d * w;
            threats++;
        }

        _threats.Clear();

        if (threats == 0)
            return null;

        var awayDir = NormalizedOrZero(away);
        if (awayDir == Vector2.Zero)
            return null;

        // No other threats: pick the flank of the current target itself.
        if (threats == 1)
        {
            var fromTarget = NormalizedOrZero(ownPos.Position - targetPos.Position);
            if (fromTarget != Vector2.Zero)
                awayDir = fromTarget;
        }

        // Rotate 90 degrees by the ship's fixed orbit handedness: perpendicular of awayDir.
        var attackVec = new Vector2(-awayDir.Y, awayDir.X);
        if (ai.OrbitSign < 0)
            attackVec = -attackVec;

        return NormalizedOrZero(attackVec);
    }
}
