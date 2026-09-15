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
using Robust.Shared.Timing;

namespace Content.Server._NSV.NPC;

/// <summary>
/// Standalone NSV ship combat AI. Replaces HTN for cores carrying <see cref="NsvShipAiComponent"/>.
///
/// Decision layer only: it selects a target and tactical parameters, then hands execution to the
/// shared <see cref="ShipSteeringSystem"/> (movement, collision/projectile avoidance, real thrust)
/// and <see cref="ShipTargetingSystem"/> (per-gun ballistics, firing). It never touches thrusters,
/// physics, or guns directly, so all of that battle-tested behavior is reused.
///
/// Tactical state and perception stay on <see cref="NsvShipAiComponent"/>; this system owns the
/// decision cadence and translates those decisions into steering and targeting commands.
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
    [Dependency] private readonly IGameTiming _timing = default!;

    private const float PerceptionSpacing = 3f;

    private readonly HashSet<Entity<NsvShipTargetComponent>> _candidates = new();
    private readonly HashSet<Entity<NsvShipTargetComponent>> _threats = new();
    private readonly HashSet<Entity<NsvShipAiComponent>> _fleetCandidates = new();
    private readonly HashSet<EntityUid> _threatIdentities = new();
    private readonly HashSet<Entity<ShipShieldEmitterComponent>> _emitters = new();
    private readonly HashSet<Entity<FireControllableComponent>> _guns = new();
    private readonly List<Entity<NsvShipAiComponent>> _fleet = new();
    private readonly Dictionary<EntityUid, int> _claimedTargets = new();
    private readonly Dictionary<EntityUid, (TimeSpan Expires, float Range)> _weaponRangeCache = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NsvShipAiComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NsvShipAiComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(Entity<NsvShipAiComponent> ent, ref ComponentStartup args)
    {
        if (HasComp<HTNComponent>(ent))
            RemComp<HTNComponent>(ent);
    }

    private void OnShutdown(Entity<NsvShipAiComponent> ent, ref ComponentShutdown args)
    {
        if (!TerminatingOrDeleted(ent))
            ClearCommands(ent);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<NsvShipAiComponent>();

        while (query.MoveNext(out var uid, out var ai))
        {
            var shipUid = Transform(uid).GridUid;
            if (shipUid == null || !this.IsPowered(uid, EntityManager))
            {
                Deactivate((uid, ai));
                continue;
            }

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
                UpdateFleet((uid, ai));
                Decide((uid, ai), shipUid.Value);
                ScanThreats((uid, ai), shipUid.Value);
            }

            // Every frame: keep steering/targeting pointed at the live target position so leading and
            // avoidance stay accurate between decisions. Stop cleanly if the target is gone.
            if (ai.Target is not { } target || TerminatingOrDeleted(target) ||
                !TryComp<NsvShipTargetComponent>(target, out var targetComp) ||
                targetComp.NeedPower && !this.IsPowered(target, EntityManager))
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
                Vector2? attackVec = null;
                if (ai.SteeringMode == ShipSteeringMode.GoToRange)
                    attackVec = CalcAttackVector((uid, ai), target);

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
                    steerer.FacingCoordinates = attackVec != null && ai.AlwaysFaceTarget ? targetCoords : null;
                }
            }

            var targeter = _targeting.Target(uid, targetCoords);
            if (targeter != null)
                targeter.LeadingAccuracy = ai.LeadingAccuracy;
        }
    }

    /// <summary>
    /// Builds this core's local fleet view. UID rank makes slot assignment deterministic, while
    /// offsets recenter when membership changes.
    /// </summary>
    private void UpdateFleet(Entity<NsvShipAiComponent> ent)
    {
        var ai = ent.Comp;
        _fleet.Clear();
        _fleetCandidates.Clear();

        if (_factions.TryGetFaction(ent, out var ownFaction))
        {
            var ownPos = _transform.GetMapCoordinates(Transform(ent));
            _lookup.GetEntitiesInRange(ownPos, ai.FleetRange, _fleetCandidates);

            foreach (var mate in _fleetCandidates)
            {
                if (mate.Owner == ent.Owner || TerminatingOrDeleted(mate) ||
                    !this.IsPowered(mate.Owner, EntityManager))
                {
                    continue;
                }

                if (!_factions.TryGetFaction(mate, out var mateFaction) || mateFaction != ownFaction)
                    continue;

                var matePos = _transform.GetMapCoordinates(Transform(mate));
                if (matePos.MapId != ownPos.MapId ||
                    (matePos.Position - ownPos.Position).LengthSquared() > ai.FleetRange * ai.FleetRange)
                {
                    continue;
                }

                _fleet.Add(mate);
            }
        }

        _fleetCandidates.Clear();
        _fleet.Add(ent);

        ai.FleetSize = _fleet.Count;
        ai.FleetIndex = 0;
        foreach (var mate in _fleet)
        {
            if (mate.Owner.Id < ent.Owner.Id)
                ai.FleetIndex++;
        }

        var offset = (ai.FleetIndex - (ai.FleetSize - 1) / 2f) * ai.FleetSpreadStep;
        ai.FleetAngleOffset = Math.Clamp(offset, -ai.FleetSpreadMax, ai.FleetSpreadMax);
    }

    private void ScanThreats(Entity<NsvShipAiComponent> ent, EntityUid shipUid)
    {
        var ai = ent.Comp;
        var otherThreatSum = Vector2.Zero;
        var withdrawSum = Vector2.Zero;
        var otherThreats = 0;
        var ownPos = _transform.GetMapCoordinates(Transform(ent));
        EntityUid? currentTargetIdentity = null;
        if (ai.Target is { } currentTarget && TryGetTargetIdentity(currentTarget, out var identity))
            currentTargetIdentity = identity;

        _threats.Clear();
        _threatIdentities.Clear();
        _lookup.GetEntitiesInRange(ownPos, ai.ThreatMaxDistance, _threats);

        foreach (var (candidate, targetComp) in _threats)
        {
            if (!TryGetTargetOffset(ent, shipUid, candidate, targetComp, ownPos, ai.ThreatMaxDistance,
                    out var offset, out var targetIdentity) ||
                !_threatIdentities.Add(targetIdentity))
            {
                continue;
            }

            var distance = offset.Length();
            if (distance <= 0f)
                continue;

            var weightedDirection = offset / distance *
                                    (1f - MathF.Pow(distance / ai.ThreatMaxDistance, ai.ThreatDistancePower));
            withdrawSum -= weightedDirection;

            if (targetIdentity == currentTargetIdentity)
                continue;

            otherThreatSum += weightedDirection;
            otherThreats++;
        }

        _threats.Clear();
        _threatIdentities.Clear();

        ai.CachedOtherThreats = otherThreats;
        ai.CachedThreatDir = NormalizedOrZero(otherThreatSum);
        ai.CachedWithdrawDir = NormalizedOrZero(withdrawSum);
    }

    private void Decide(Entity<NsvShipAiComponent> ent, EntityUid shipUid)
    {
        var ai = ent.Comp;
        var ownPos = _transform.GetMapCoordinates(Transform(ent));

        _claimedTargets.Clear();
        foreach (var mate in _fleet)
        {
            if (mate.Owner.Id >= ent.Owner.Id || mate.Comp.Target is not { } mateTarget ||
                !TryGetTargetIdentity(mateTarget, out var targetIdentity))
            {
                continue;
            }

            _claimedTargets.TryGetValue(targetIdentity, out var claims);
            _claimedTargets[targetIdentity] = claims + 1;
        }

        _candidates.Clear();
        _lookup.GetEntitiesInRange(ownPos, ai.SearchRange, _candidates);

        EntityUid? best = null;
        var bestScore = float.NegativeInfinity;

        foreach (var (candidate, targetComp) in _candidates)
        {
            if (!TryGetTargetOffset(ent, shipUid, candidate, targetComp, ownPos, ai.SearchRange,
                    out var offset, out var targetIdentity))
            {
                continue;
            }

            var value = GetCachedWeaponRange(targetIdentity);
            var score = (value + 100f) / (offset.LengthSquared() + ai.TargetDistanceOffset);
            if (candidate == ai.Target)
                score *= ai.TargetStickiness;

            if (_claimedTargets.TryGetValue(targetIdentity, out var claimed))
                score /= 1f + claimed * ai.FleetTargetPenalty;

            if (score > bestScore ||
                score == bestScore && (best == null || candidate.Id < best.Value.Id))
            {
                bestScore = score;
                best = candidate;
            }
        }

        _candidates.Clear();
        _claimedTargets.Clear();
        ai.Target = best;
    }

    private bool TryGetTargetOffset(
        Entity<NsvShipAiComponent> ent,
        EntityUid shipUid,
        EntityUid candidate,
        NsvShipTargetComponent targetComp,
        MapCoordinates ownPos,
        float maxDistance,
        out Vector2 offset,
        out EntityUid targetIdentity)
    {
        offset = default;
        targetIdentity = candidate;

        if (TerminatingOrDeleted(candidate))
            return false;

        var targetXform = Transform(candidate);
        var targetGrid = targetXform.GridUid;
        targetIdentity = targetGrid ?? candidate;

        if (targetComp.NeedGrid != NsvShipTargetGridMode.Either &&
            (targetComp.NeedGrid == NsvShipTargetGridMode.OnGrid) == (targetGrid == null))
        {
            return false;
        }

        if (targetGrid == shipUid ||
            targetComp.NeedPower && !this.IsPowered(candidate, EntityManager) ||
            targetGrid != null && _whitelist.IsBlacklistPass(ent.Comp.Blacklist, targetGrid.Value))
        {
            return false;
        }

        var targetPos = _transform.GetMapCoordinates(targetXform);
        if (targetPos.MapId != ownPos.MapId || !_factions.IsHostile(ent.Owner, candidate))
            return false;

        offset = targetPos.Position - ownPos.Position;
        return offset.LengthSquared() <= maxDistance * maxDistance;
    }

    private bool TryGetTargetIdentity(EntityUid target, out EntityUid identity)
    {
        identity = target;
        if (TerminatingOrDeleted(target) || !TryComp<TransformComponent>(target, out var xform))
            return false;

        identity = xform.GridUid ?? target;
        return true;
    }

    private void Deactivate(Entity<NsvShipAiComponent> ent)
    {
        ClearCommands(ent);

        var ai = ent.Comp;
        ai.Target = null;
        ai.DecisionAccumulator = 0f;
        ai.PerceptionAccum = 0f;
        ai.FleetIndex = 0;
        ai.FleetSize = 1;
        ai.FleetAngleOffset = 0f;
        ai.CachedThreatDir = Vector2.Zero;
        ai.CachedWithdrawDir = Vector2.Zero;
        ai.CachedOtherThreats = 0;
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
        _weaponRangeCache[shipUid] = (_timing.CurTime + TimeSpan.FromSeconds(PerceptionSpacing),
            ai.CachedWeaponRange);

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

    private float GetCachedWeaponRange(EntityUid shipUid)
    {
        if (_weaponRangeCache.TryGetValue(shipUid, out var cached) && cached.Expires > _timing.CurTime)
            return cached.Range;

        if (_weaponRangeCache.Count > 256)
            _weaponRangeCache.Clear();

        var range = CalcWeaponRange(shipUid);
        _weaponRangeCache[shipUid] = (_timing.CurTime + TimeSpan.FromSeconds(PerceptionSpacing), range);
        return range;
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

            var bulletProto = _gun.GetBulletPrototype(proto);
            float range;
            if (bulletProto.TryGetComponent<HitscanAmmoComponent>(out _, Factory))
            {
                if (!bulletProto.TryGetComponent<HitscanBasicRaycastComponent>(out var raycast, Factory))
                    continue;

                range = raycast.MaxDistance;
            }
            else if (bulletProto.TryGetComponent<TimedDespawnComponent>(out var despawn, Factory))
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

    private void SteerWithdraw(Entity<NsvShipAiComponent> ent, EntityCoordinates targetCoords)
    {
        var ai = ent.Comp;
        var ownPos = _transform.GetMapCoordinates(Transform(ent));
        var awayDir = ai.CachedWithdrawDir != Vector2.Zero
            ? ai.CachedWithdrawDir
            : NormalizedOrZero(ownPos.Position - _transform.GetMapCoordinates(targetCoords.EntityId).Position);

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
    /// Per-frame attack-vector composition from decision-tick caches (no entity queries).
    /// Base flank direction: two or more other hostiles use their summed geometry; one other
    /// hostile or any fleet presence flanks the target itself; a lone ship with no other
    /// hostiles approaches straight (null). The fleet-assigned angle offset spreads members
    /// around the base flank instead of all taking the same side.
    /// </summary>
    private Vector2? CalcAttackVector(Entity<NsvShipAiComponent> ent, EntityUid target)
    {
        var ai = ent.Comp;
        var ownPos = _transform.GetMapCoordinates(Transform(ent));
        var targetPos = _transform.GetMapCoordinates(target);
        if (targetPos.MapId != ownPos.MapId)
            return null;

        Vector2 baseDir;
        if (ai.CachedOtherThreats >= 2)
        {
            baseDir = ai.CachedThreatDir;
        }
        else if (ai.CachedOtherThreats == 1 || ai.FleetSize > 1)
        {
            baseDir = NormalizedOrZero(ownPos.Position - targetPos.Position);
        }
        else
        {
            return null;
        }

        if (baseDir == Vector2.Zero)
            return null;

        // Rotate the base flank by the fleet slot offset, mirrored by the ship's handedness:
        // offset 0 with OrbitSign +1 is exactly the historical 90-degree perpendicular.
        var angle = MathF.PI / 2f + ai.FleetAngleOffset * MathF.PI / 180f;
        if (ai.OrbitSign < 0)
            angle = -angle;

        return NormalizedOrZero(new Angle(angle).RotateVec(baseDir));
    }
}
