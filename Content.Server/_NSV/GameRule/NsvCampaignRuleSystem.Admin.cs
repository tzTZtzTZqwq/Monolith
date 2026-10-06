using Content.Server._NSV.GameRule.Components;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;

namespace Content.Server._NSV.GameRule;

/// <summary>
/// A read-only snapshot of the running campaign for the admin sector monitor: the player-facing
/// summary plus the internal pacing state (briefing, reminders, extension) admins need when testing.
/// </summary>
public readonly record struct NsvCampaignAdminView(
    NsvCampaignSummaryState Summary,
    NsvCampaignOutcome Outcome,
    bool BriefingDelivered,
    int ReminderStage,
    float? NextReminderIn,
    float? ExtensionRemaining,
    float ActiveTime);

/// <summary>
/// Admin entry points for the campaign (sector monitor panel). The rule component is [Access]-locked,
/// so admin inspection and overrides go through here. Mutations act on the live campaign only and
/// return false when there isn't one.
/// </summary>
public sealed partial class NsvCampaignRuleSystem
{
    public NsvCampaignAdminView? GetAdminView()
    {
        var campaigns = ActiveCampaigns();
        if (campaigns.Count == 0 || TryBuildSummary() is not { } summary)
            return null;

        var component = campaigns[0].Comp;
        var interval = _cfg.GetCVar(NsvCCVars.CampaignReminderInterval);
        float? nextReminder = interval > 0f &&
                              component.Phase == NsvCampaignPhase.Active &&
                              component.Outcome == NsvCampaignOutcome.None
            ? MathF.Max(0f, interval - component.StallTime)
            : null;

        return new NsvCampaignAdminView(
            summary,
            component.Outcome,
            component.BriefingDelivered,
            component.ReminderStage,
            nextReminder,
            component.ExtensionRemaining,
            component.ActiveTime);
    }

    /// <summary>
    /// Sets the live campaign's victory score (floored at 0).
    /// </summary>
    public bool AdminSetScore(int score)
    {
        var campaigns = LiveCampaigns();
        if (campaigns.Count == 0)
            return false;

        foreach (var campaign in campaigns)
        {
            campaign.Comp.Score = Math.Max(0, score);
        }

        CampaignDisplayChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to the live campaign's victory score (floored at 0).
    /// </summary>
    public bool AdminAdjustScore(int delta)
    {
        var campaigns = LiveCampaigns();
        return campaigns.Count > 0 && AdminSetScore(campaigns[0].Comp.Score + delta);
    }

    /// <summary>
    /// Sets the live campaign's threat elevation (floored at 0).
    /// </summary>
    public bool AdminSetThreat(float threat)
    {
        var campaigns = LiveCampaigns();
        if (campaigns.Count == 0)
            return false;

        var changed = false;
        foreach (var campaign in campaigns)
        {
            changed |= AdjustThreat(campaign.Comp, threat - campaign.Comp.ThreatElevation);
        }

        if (changed)
            CampaignDisplayChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Announces the mission briefing now, even if it was already delivered (for testing the text).
    /// </summary>
    public bool AdminAnnounceBriefing()
    {
        var campaigns = LiveCampaigns();
        if (campaigns.Count == 0)
            return false;

        DeliverBriefing(campaigns[0].Comp);
        return true;
    }

    /// <summary>
    /// Delivers the next stalled-objective reminder immediately, with its real consequence (score
    /// penalty, or the blockade at stage 5), and restarts the stall clock. Ignores the phase/outcome
    /// gating so it can be exercised at any point. Returns the stage delivered, or 0 with no campaign.
    /// </summary>
    public int AdminSendNextReminder()
    {
        var campaigns = LiveCampaigns();
        if (campaigns.Count == 0)
            return 0;

        var component = campaigns[0].Comp;
        component.StallTime = 0f;
        component.ReminderStage = component.ReminderStage % MaxReminderStage + 1;
        DeliverReminder(component, component.ReminderStage);
        return component.ReminderStage;
    }
}
