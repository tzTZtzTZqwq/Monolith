using Content.Server._NSV.GameRule.Components;
using Content.Server.Power.EntitySystems;

namespace Content.Server._NSV.GameRule;

/// <summary>
/// Critical-system defeat: a flagship system marked <see cref="NsvCampaignCriticalSystemComponent"/>
/// that stays destroyed or unpowered past its grace period loses the campaign.
/// </summary>
public sealed partial class NsvCampaignRuleSystem
{
    // An outage is only announced once it outlasts this, so the power solver's start-up tick (every
    // receiver reads unpowered until the first solve) doesn't flash "offline" at round start.
    private const float CriticalAnnounceDelay = 2f;

    // A last warning goes out when this much grace is left.
    private const float CriticalFinalWarning = 60f;

    /// <summary>
    /// Advances every critical group's countdown on the flagship. Returns true once the campaign has
    /// been lost (the round is ending).
    /// </summary>
    private bool TickCriticalSystems(NsvCampaignRuleComponent component, float frameTime)
    {
        if (component.Outcome != NsvCampaignOutcome.None)
            return false;

        var flagships = GetFlagshipGrids();
        if (flagships.Count == 0)
            return false;

        // Register groups seen aboard and note which have a powered, intact member this tick.
        var online = new HashSet<string>();
        var query = AllEntityQuery<NsvCampaignCriticalSystemComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var critical, out var xform))
        {
            if (xform.GridUid is not { } grid || !flagships.Contains(grid) || TerminatingOrDeleted(uid))
                continue;

            if (!component.CriticalSystems.ContainsKey(critical.Group))
            {
                component.CriticalSystems[critical.Group] = new NsvCampaignCriticalState
                {
                    Name = critical.Name,
                    GracePeriod = critical.GracePeriod,
                };
            }

            if (this.IsPowered(uid, EntityManager))
                online.Add(critical.Group);
        }

        foreach (var (group, state) in component.CriticalSystems)
        {
            var name = Loc.GetString(state.Name);
            if (online.Contains(group))
            {
                if (state.OfflineAnnounced)
                    Announce(Loc.GetString("nsv-campaign-critical-restored", ("system", name)));

                state.OfflineTime = null;
                state.OfflineAnnounced = false;
                state.FinalWarningSent = false;
                continue;
            }

            state.OfflineTime = (state.OfflineTime ?? 0f) + frameTime;
            var left = state.GracePeriod - state.OfflineTime.Value;

            if (!state.OfflineAnnounced && state.OfflineTime >= CriticalAnnounceDelay)
            {
                state.OfflineAnnounced = true;
                Announce(Loc.GetString("nsv-campaign-critical-offline",
                    ("system", name), ("seconds", (int) MathF.Ceiling(MathF.Max(0f, left)))));
            }

            if (state.OfflineAnnounced && !state.FinalWarningSent &&
                left <= CriticalFinalWarning && state.GracePeriod > CriticalFinalWarning)
            {
                state.FinalWarningSent = true;
                Announce(Loc.GetString("nsv-campaign-critical-warning",
                    ("system", name), ("seconds", (int) MathF.Ceiling(MathF.Max(0f, left)))));
            }

            if (left > 0f)
                continue;

            Conclude(component, NsvCampaignOutcome.Defeat);
            CampaignDisplayChanged?.Invoke();
            _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-defeat-critical", ("system", name)));
            _roundEnd.EndRound();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Grids carrying a campaign flagship marker: the marked grid itself, or the grid of a marked
    /// entity (the admin verb can mark a console instead of the ship).
    /// </summary>
    private HashSet<EntityUid> GetFlagshipGrids()
    {
        var grids = new HashSet<EntityUid>();
        var query = AllEntityQuery<NsvCampaignFlagshipComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (!TerminatingOrDeleted(uid))
                grids.Add(xform.GridUid ?? uid);
        }

        return grids;
    }
}
