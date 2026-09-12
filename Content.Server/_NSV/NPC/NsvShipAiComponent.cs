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
    /// Desired engagement distance to keep from the target.
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
