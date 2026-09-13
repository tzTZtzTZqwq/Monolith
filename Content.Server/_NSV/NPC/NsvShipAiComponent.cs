using Content.Server._Mono.NPC.HTN;
using Content.Server.NPC;
using Content.Shared.Whitelist;
using Robust.Shared.Maths;

namespace Content.Server._NSV.NPC;

/// <summary>
/// Standalone NSV ship combat AI, independent of HTN. The paired <see cref="NsvShipAiSystem"/> drives
/// the grid by issuing commands to the shared ShipSteeringSystem and ShipTargetingSystem, so all
/// movement/firing execution is reused rather than reimplemented.
///
/// Put this on a ship AI core INSTEAD of an HTN component. Do not put both on one core: they would
/// each try to drive the same ShipSteerer/ShipTargeting components and fight over the ship.
///
/// This component is pure data. Write behavior in <see cref="NsvShipAiSystem"/>.
/// </summary>
[RegisterComponent]
public sealed partial class NsvShipAiComponent : Component
{
    /// <summary>
    /// How far to look for candidate targets.
    /// </summary>
    [DataField]
    public float SearchRange = 4000f;

    /// <summary>
    /// Grids matching this blacklist are never targeted (e.g. entities tagged ShuttleAIIgnore).
    /// </summary>
    [DataField]
    public EntityWhitelist Blacklist = new();

    /// <summary>
    /// Desired engagement distance to keep from the target. Only used when <see cref="AutoEngageRange"/>
    /// is false or the ship has no scannable weapons.
    /// </summary>
    [DataField]
    public float EngageRange = 750f;

    /// <summary>
    /// Allowed slack around <see cref="EngageRange"/>.
    /// </summary>
    [DataField]
    public float EngageRangeTolerance = 150f;

    /// <summary>
    /// Max speed to still count as "arrived" at engagement range. Null = don't care about speed.
    /// </summary>
    [DataField]
    public float? InRangeMaxSpeed = 4f;

    /// <summary>
    /// Gun leading accuracy handed to the targeting system.
    /// </summary>
    [DataField]
    public float LeadingAccuracy = 0.6f;

    /// <summary>
    /// Steering movement behavior handed to the steering system.
    /// GoToRange = close to <see cref="EngageRange"/> and hold; Orbit/OrbitCW = circle the target.
    /// </summary>
    [DataField]
    public ShipSteeringMode SteeringMode = ShipSteeringMode.GoToRange;

    /// <summary>
    /// Facing offset relative to the movement direction, in degrees. 0 = nose on target,
    /// 90 = broadside.
    /// </summary>
    [DataField]
    public float TargetRotation = 0f;

    /// <summary>
    /// Keep the ship's nose pointed at the target while maneuvering.
    /// </summary>
    [DataField]
    public bool AlwaysFaceTarget = true;

    /// <summary>
    /// Dodge incoming shipgun projectiles while maneuvering.
    /// </summary>
    [DataField]
    public bool AvoidProjectiles = true;

    /// <summary>
    /// Derive the engagement range from the ship's own longest weapon range plus shield stress
    /// instead of the fixed <see cref="EngageRange"/>:
    /// range = WeaponRange * <see cref="RangeScale"/> + stress * <see cref="StressRangeBonus"/>.
    /// </summary>
    [DataField]
    public bool AutoEngageRange = true;

    /// <summary>
    /// Fraction of the longest weapon's range to hold as the base engagement distance.
    /// </summary>
    [DataField]
    public float RangeScale = 0.8f;

    /// <summary>
    /// Extra engagement distance added linearly with shield stress (0..1). Soft retreat: the AI
    /// drifts further out as its shields fail before switching to full withdrawal.
    /// </summary>
    [DataField]
    public float StressRangeBonus = 250f;

    /// <summary>
    /// Shield stress (0..1) at which the AI stops maneuvering for advantage and withdraws.
    /// Ships without shield emitters never accumulate stress and thus never withdraw.
    /// </summary>
    [DataField]
    public float WithdrawStressThreshold = 0.85f;

    /// <summary>
    /// How far away hostiles still contribute to the withdraw direction.
    /// </summary>
    [DataField]
    public float ThreatMaxDistance = 1500f;

    /// <summary>
    /// Distance falloff exponent for threat weighting in the withdraw direction.
    /// </summary>
    [DataField]
    public float ThreatDistancePower = 2f;

    /// <summary>
    /// How far along the withdraw direction to place the navigation waypoint.
    /// </summary>
    [DataField]
    public float WithdrawDistance = 1000f;

    /// <summary>
    /// Seconds between AI decisions. Steering/targeting still run every frame off the last decision,
    /// tracking the live target position; this only throttles target choice and tactical decisions.
    /// </summary>
    [DataField]
    public float DecisionInterval = 0.3f;

    /// <summary>
    /// Once a target is chosen, multiply its score by this so we don't flip-flop between roughly
    /// equidistant targets each decision.
    /// </summary>
    [DataField]
    public float TargetStickiness = 1.35f;

    /// <summary>
    /// Distance-squared offset in the targeting score denominator. Keeps near-ties stable when
    /// distances are tiny relative to the scale of the search range.
    /// </summary>
    [DataField]
    public float TargetDistanceOffset = 50000f;

    /// <summary>
    /// Fixed per-ship orbit handedness: +1 counter-clockwise, -1 clockwise. Set once at spawn so
    /// the attack flank choice doesn't flip frame to frame.
    /// </summary>
    [DataField]
    public int OrbitSign = 1;

    /// <summary>
    /// Countdown until the next decision.
    /// </summary>
    [ViewVariables]
    public float DecisionAccumulator;

    /// <summary>
    /// Current target entity, if any.
    /// </summary>
    [ViewVariables]
    public EntityUid? Target;

    /// <summary>
    /// Countdown until the next perception refresh.
    /// </summary>
    [ViewVariables]
    public float PerceptionAccum;

    /// <summary>
    /// Last measured shield stress (0..1): worst emitter Damage/DamageLimit, 1 if any emitter is
    /// in recharge mode. Refreshed every few seconds; 0 for ships without shield emitters.
    /// </summary>
    [ViewVariables]
    public float CachedShieldStress;

    /// <summary>
    /// Last measured longest weapon range in meters (hitscan max distance, or projectile speed
    /// times lifetime). 0 = no scannable weapons on the grid.
    /// </summary>
    [ViewVariables]
    public float CachedWeaponRange;

    /// <summary>
    /// Test behavior: instead of fighting, spin the ship in place at this angular velocity (rad/s).
    /// Null (default) = normal combat behavior.
    /// </summary>
    [DataField]
    public float? TestSpinSpeed;

    /// <summary>
    /// World angle currently chased by the spin test; advances at <see cref="TestSpinSpeed"/>.
    /// </summary>
    [ViewVariables]
    public Angle TestSpinAngle;

    /// <summary>
    /// Test behavior: instead of fighting, keep at least this much clearance (meters, hull-to-hull)
    /// from every other grid on the map. Null (default) = normal combat behavior.
    /// </summary>
    [DataField]
    public float? TestKeepDistance;

    /// <summary>
    /// General-purpose key-value store for AI strategy state (targeting policies, behavior-state
    /// scratch data, cross-decision memory). Heterogeneous values, HTN-style.
    /// Seedable from YAML; every value needs a !type: tag, e.g.
    /// <code>blackboard: { Aggro: !type:Single 1.5, Retreating: !type:Boolean false }</code>
    /// </summary>
    [DataField("blackboard", customTypeSerializer: typeof(NPCBlackboardSerializer))]
    public NPCBlackboard Blackboard = new();
}
