namespace Content.Server._NSV.GameRule.Components;

public enum NsvCampaignObjectiveStatus
{
    InProgress,
    Completed
}

public enum NsvCampaignObjectiveKind
{
    // Baseline objective every round: perform Target FTL jumps.
    PerformJumps
}

/// <summary>
/// A single round-level objective tracked on <see cref="NsvCampaignRuleComponent"/>. Runtime state
/// only; progress (<see cref="Tally"/>) is advanced by the campaign system, not by YAML.
/// </summary>
public sealed class NsvCampaignObjective
{
    public NsvCampaignObjectiveKind Kind;

    public NsvCampaignObjectiveStatus Status = NsvCampaignObjectiveStatus.InProgress;

    // Amount required to complete (e.g. jump count for PerformJumps).
    public int Target;

    // Progress so far toward Target.
    public int Tally;

    // Whether completing this objective has already eased threat (S5); guards the one-time decay.
    public bool ThreatNegated;
}
