namespace Content.Server._NSV.GameRule.Components;

/// <summary>
/// Marks an entity as the campaign flagship (S3 defeat path). When it terminates, the active
/// campaign is lost and the round ends. Designated at runtime by an admin verb (there is no
/// automatic player-ship entry yet), typically stamped onto a ship's main console.
/// </summary>
[RegisterComponent, Access(typeof(NsvCampaignRuleSystem))]
public sealed partial class NsvCampaignFlagshipComponent : Component
{
}
