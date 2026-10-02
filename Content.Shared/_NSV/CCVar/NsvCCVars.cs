using Robust.Shared.Configuration;

namespace Content.Shared._NSV.CCVar;

/// <summary>
/// Contains configuration variables used by NSV systems.
/// </summary>
[CVarDefs]
public sealed class NsvCCVars
{
    /// <summary>
    /// Display-only soft capacity for all persistent bluespace sectors.
    /// This must not be used to reject sector creation or access.
    /// </summary>
    public static readonly CVarDef<int> BluespaceSectorTotalSoftCapacity =
        CVarDef.Create("nsv.bluespace.sectors.total_soft_capacity", 100, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Display-only soft capacity for active persistent bluespace sectors.
    /// This must not be used to reject sector creation, wake, or travel.
    /// </summary>
    public static readonly CVarDef<int> BluespaceSectorActiveSoftCapacity =
        CVarDef.Create("nsv.bluespace.sectors.active_soft_capacity", 15, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Seconds between strategy-fleet spawn scans. Each scan tops up hostile dormant nodes
    /// toward their threat-scaled target.
    /// </summary>
    public static readonly CVarDef<float> StrategyFleetSpawnInterval =
        CVarDef.Create("nsv.bluespace.strategy.fleet_spawn_interval", 180f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Ships a hostile node holds at zero threat, before any threat scaling.
    /// </summary>
    public static readonly CVarDef<int> StrategyFleetBaselineSize =
        CVarDef.Create("nsv.bluespace.strategy.fleet_baseline_size", 1, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Threat elevation that adds one ship to a hostile node's target count.
    /// </summary>
    public static readonly CVarDef<float> StrategyFleetThreatPerShip =
        CVarDef.Create("nsv.bluespace.strategy.fleet_threat_per_ship", 5f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Hard cap on the hostile (NSVHostile) strategy fleet per node. Federal front-line nodes also
    /// hold a separate garrison (<see cref="StrategyFederalGarrisonSize"/>) on top of this, so their
    /// total can exceed it.
    /// </summary>
    public static readonly CVarDef<int> StrategyFleetMaxPerNode =
        CVarDef.Create("nsv.bluespace.strategy.fleet_max_per_node", 4, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Defending ships a contested (Federal-faction) node holds. Seeded alongside a threat-scaled
    /// hostile incursion so the two sides fight out abstract combat while the node is dormant.
    /// </summary>
    public static readonly CVarDef<int> StrategyFederalGarrisonSize =
        CVarDef.Create("nsv.bluespace.strategy.federal_garrison_size", 2, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Seconds between abstract (data-state) AI-vs-AI combat resolution passes on dormant nodes.
    /// </summary>
    public static readonly CVarDef<float> StrategyCombatInterval =
        CVarDef.Create("nsv.bluespace.strategy.combat_interval", 180f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Fraction of the losing ship's full-complement floor count destroyed in one lost abstract-combat
    /// exchange, so a ship dies in a few exchanges rather than one. At least one floor is always lost so
    /// low-power fights never stall.
    /// </summary>
    public static readonly CVarDef<float> StrategyCombatDamageFraction =
        CVarDef.Create("nsv.bluespace.strategy.combat_damage_fraction", 0.34f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Minimum accumulated victory score before returning to a Home node concludes the campaign in
    /// victory (S4). Below this, arriving home is a no-op — the crew must earn more first.
    /// </summary>
    public static readonly CVarDef<int> CampaignVictoryScoreThreshold =
        CVarDef.Create("nsv.campaign.victory_score_threshold", 5, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Seconds of active-campaign time before passive threat growth begins (S5 grace period).
    /// </summary>
    public static readonly CVarDef<float> CampaignThreatGracePeriod =
        CVarDef.Create("nsv.campaign.threat_grace_period", 1500f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Seconds between passive threat-growth ticks once the grace period has elapsed (S5).
    /// </summary>
    public static readonly CVarDef<float> CampaignThreatGrowthInterval =
        CVarDef.Create("nsv.campaign.threat_growth_interval", 60f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Threat elevation added on each passive growth tick (S5).
    /// </summary>
    public static readonly CVarDef<float> CampaignThreatGrowthAmount =
        CVarDef.Create("nsv.campaign.threat_growth_amount", 1f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Threat elevation removed when a round objective is first completed (S5), rewarding progress by
    /// easing off pressure. Applied once per objective.
    /// </summary>
    public static readonly CVarDef<float> CampaignObjectiveThreatNegation =
        CVarDef.Create("nsv.campaign.objective_threat_negation", 3f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// How long, in seconds, the round runs on after the crew votes to press on (S3). Read when the
    /// vote passes; the countdown dies with the rule, so it never ends a later round.
    /// </summary>
    public static readonly CVarDef<float> CampaignExtensionDuration =
        CVarDef.Create("nsv.campaign.extension_duration", 3600f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Seconds a Hold encounter requires the crew to survive in a hostile node before it completes.
    /// Extraction stays gated for the whole duration, so this is how long the crew is pinned under fire.
    /// </summary>
    public static readonly CVarDef<float> EncounterHoldDuration =
        CVarDef.Create("nsv.bluespace.encounter.hold_duration", 300f, CVar.SERVERONLY | CVar.ARCHIVE);
}
