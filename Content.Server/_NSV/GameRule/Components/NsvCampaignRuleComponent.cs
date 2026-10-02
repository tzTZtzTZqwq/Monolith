using Content.Server.Voting;

namespace Content.Server._NSV.GameRule.Components;

public enum NsvCampaignPhase
{
    Active,
    Extending,
    Ended
}

public enum NsvCampaignOutcome
{
    None,
    Victory,
    Defeat
}

/// <summary>
/// Authoritative per-round state container for the NSV campaign loop. Lives on the game-rule
/// entity, so its lifetime is scoped to the round and cleaned up when the rule ends.
/// </summary>
[RegisterComponent, Access(typeof(NsvCampaignRuleSystem))]
public sealed partial class NsvCampaignRuleComponent : Component
{
    [ViewVariables]
    public NsvCampaignPhase Phase = NsvCampaignPhase.Active;

    [ViewVariables]
    public NsvCampaignOutcome Outcome = NsvCampaignOutcome.None;

    // Round-level objectives (S2). Includes the baseline "perform N jumps" objective.
    [ViewVariables]
    public readonly List<NsvCampaignObjective> Objectives = new();

    // Accumulated victory score (S4). First pass tracks the player faction only as a single tally.
    [ViewVariables]
    public int Score;

    // Time/kill-driven pressure (S5). Passively grows, drops on objective completion, floored at 0.
    [ViewVariables]
    public float ThreatElevation;

    // S5 passive-growth bookkeeping: accumulated active time (for the grace period) and the interval
    // accumulator that spends down each time a growth tick is applied.
    [ViewVariables]
    public float ActiveTime;

    [ViewVariables]
    public float ThreatGrowthAccumulator;

    // Seconds left on a voted extension (S3/S8), or null when not extending. Counted down in
    // ActiveTick rather than a detached timer so it dies with the rule on round restart.
    [ViewVariables]
    public float? ExtensionRemaining;

    // The in-flight outcome vote, cancelled if the rule ends before the vote finishes.
    public IVoteHandle? OutcomeVote;
}
