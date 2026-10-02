namespace Content.Server._NSV.GameRule.Components;

/// <summary>
/// Marks an entity as raising campaign threat elevation when it is "killed" (S5). Mirrors
/// <see cref="NsvCampaignKillRewardComponent"/>'s kill detection (the entity terminating, or — if it
/// needs power — losing power). Contributes at most once per entity.
/// </summary>
[RegisterComponent, Access(typeof(NsvCampaignRuleSystem))]
public sealed partial class NsvCampaignKillThreatComponent : Component
{
    // Threat elevation added to the active campaign when this entity is killed.
    [DataField]
    public float Threat = 1f;

    // Set once the threat has been contributed, so power flicker or a later termination can't re-apply.
    [ViewVariables]
    public bool Contributed;
}
