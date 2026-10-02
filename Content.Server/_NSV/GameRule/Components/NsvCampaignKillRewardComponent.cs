namespace Content.Server._NSV.GameRule.Components;

/// <summary>
/// Marks an entity as worth campaign victory score when it is "killed" (S4). A kill is either the
/// entity terminating, or — if it needs power — losing power. Scores at most once per entity.
/// </summary>
[RegisterComponent, Access(typeof(NsvCampaignRuleSystem))]
public sealed partial class NsvCampaignKillRewardComponent : Component
{
    // Victory score granted to the active campaign when this entity is killed.
    [DataField]
    public int Score = 1;

    // Set once the reward has been granted, so power flicker or a later termination can't re-award.
    [ViewVariables]
    public bool Rewarded;
}
