using Content.Server._NSV.Bluespace.Strategy;
using Content.Server._NSV.GameRule.Components;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.RoundEnd;
using Content.Server.Voting;
using Content.Server.Voting.Managers;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared._NSV.CCVar;
using Content.Shared.GameTicking.Components;
using Content.Shared.Power;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.GameRule;

/// <summary>
/// Drives the NSV campaign round loop. Round-scoped state lives on
/// <see cref="NsvCampaignRuleComponent"/> (the game-rule entity); this system reacts to the
/// rule's lifecycle and is otherwise stateless.
/// </summary>
/// <remarks>
/// Two views of "the running campaign": <see cref="ActiveCampaigns"/> (rule active) for read-only
/// accessors, and <see cref="LiveCampaigns"/> (rule active, round in progress, not yet concluded) for
/// every mutation. The narrower gate matters at round restart: the rule stays active while the
/// entity flush deletes ships and flagships, and those deaths must not score or end the next round.
/// </remarks>
public sealed partial class NsvCampaignRuleSystem : GameRuleSystem<NsvCampaignRuleComponent>
{
    // Baseline "perform N jumps" objective range (ROUND-001, S2).
    private const int MinBaselineJumps = 6;
    private const int MaxBaselineJumps = 10;

    // Outcome vote shown when every objective is complete (S3).
    private static readonly TimeSpan OutcomeVoteDuration = TimeSpan.FromSeconds(60);

    private const string ExtendOption = "extend";
    private const string EndOption = "end";

    // Stalled-objective reminders (N6): 1 warns, FirstPenaltyStage..4 deduct score, 5 = blockade.
    private const int MaxReminderStage = 5;
    private const int FirstPenaltyStage = 2;

    [Dependency] private IChatManager _chat = default!;
    [Dependency] private ChatSystem _chatSystem = default!;
    [Dependency] private IVoteManager _votes = default!;
    [Dependency] private RoundEndSystem _roundEnd = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private NsvFleetRegistrySystem _fleets = default!;
    [Dependency] private IPrototypeManager _protos = default!;

    /// <summary>
    /// Raised when the campaign readout (phase / score / threat / objective tally) changes so
    /// listeners like the navigation console can push a fresh state without waiting for a sector
    /// event. Only raised when a value actually changed.
    /// </summary>
    public event Action? CampaignDisplayChanged;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NsvCampaignKillRewardComponent, EntityTerminatingEvent>(OnKillRewardTerminating);
        SubscribeLocalEvent<NsvCampaignKillRewardComponent, PowerChangedEvent>(OnKillRewardPowerChanged);
        SubscribeLocalEvent<NsvCampaignKillThreatComponent, EntityTerminatingEvent>(OnKillThreatTerminating);
        SubscribeLocalEvent<NsvCampaignKillThreatComponent, PowerChangedEvent>(OnKillThreatPowerChanged);
        SubscribeLocalEvent<NsvCampaignFlagshipComponent, EntityTerminatingEvent>(OnFlagshipTerminating);
    }

    protected override void Started(EntityUid uid, NsvCampaignRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        component.Phase = NsvCampaignPhase.Active;

        component.Objectives.Add(new NsvCampaignObjective
        {
            Kind = NsvCampaignObjectiveKind.PerformJumps,
            Target = RobustRandom.Next(MinBaselineJumps, MaxBaselineJumps + 1),
        });
    }

    protected override void Ended(EntityUid uid, NsvCampaignRuleComponent component, GameRuleComponent gameRule, GameRuleEndedEvent args)
    {
        component.Phase = NsvCampaignPhase.Ended;
        component.ExtensionRemaining = null;

        // A vote still open when the rule ends (e.g. an admin restart) must not resolve into the next
        // round. Its OnFinished also re-checks liveness, so this is belt and braces.
        if (component.OutcomeVote is { Finished: false, Cancelled: false } vote)
            vote.Cancel();
        component.OutcomeVote = null;
    }

    protected override void ActiveTick(EntityUid uid, NsvCampaignRuleComponent component, GameRuleComponent gameRule, float frameTime)
    {
        if (GameTicker.RunLevel != GameRunLevel.InRound || component.Phase == NsvCampaignPhase.Ended)
            return;

        component.ActiveTime += frameTime;
        if (TickExtension(component, frameTime))
            return;

        TickBriefing(component);
        TickReminders(component, frameTime);
        TickThreatGrowth(component, frameTime);
    }

    /// <summary>
    /// Announces Naval Command's mission briefing once, a short delay into the campaign (NSV13
    /// ROUND-005): the round objectives plus how the operation is won and lost.
    /// </summary>
    private void TickBriefing(NsvCampaignRuleComponent component)
    {
        var delay = _cfg.GetCVar(NsvCCVars.CampaignBriefingDelay);
        if (component.BriefingDelivered || delay < 0f || component.ActiveTime < delay)
            return;

        DeliverBriefing(component);
    }

    private void DeliverBriefing(NsvCampaignRuleComponent component)
    {
        component.BriefingDelivered = true;

        var lines = new List<string> { Loc.GetString("nsv-campaign-briefing-intro") };
        foreach (var objective in component.Objectives)
        {
            lines.Add(Loc.GetString("nsv-campaign-briefing-objective",
                ("objective", GetObjectiveBrief(objective))));
        }

        lines.Add(Loc.GetString("nsv-campaign-briefing-victory",
            ("threshold", _cfg.GetCVar(NsvCCVars.CampaignVictoryScoreThreshold))));
        lines.Add(Loc.GetString("nsv-campaign-briefing-defeat"));
        Announce(string.Join("\n", lines));
    }

    /// <summary>
    /// Escalating reminders while objectives stall (NSV13 ROUND-006). Each interval without progress
    /// delivers the next reminder: 1 warns, 2–4 also deduct victory score, 5 sends a blockade fleet to
    /// the crew; the cycle then wraps to 1. Only runs while the campaign is undecided — once the
    /// outcome vote starts or the round is extending, there's nothing left to push toward.
    /// </summary>
    internal void TickReminders(NsvCampaignRuleComponent component, float frameTime)
    {
        var interval = _cfg.GetCVar(NsvCCVars.CampaignReminderInterval);
        if (interval <= 0f ||
            component.Phase != NsvCampaignPhase.Active ||
            component.Outcome != NsvCampaignOutcome.None)
        {
            return;
        }

        component.StallTime += frameTime;
        while (component.StallTime >= interval)
        {
            component.StallTime -= interval;
            component.ReminderStage = component.ReminderStage % MaxReminderStage + 1;
            DeliverReminder(component, component.ReminderStage);
        }
    }

    private void DeliverReminder(NsvCampaignRuleComponent component, int stage)
    {
        if (stage < MaxReminderStage)
        {
            var penalty = 0;
            if (stage >= FirstPenaltyStage)
            {
                penalty = Math.Min(component.Score, Math.Max(0, _cfg.GetCVar(NsvCCVars.CampaignReminderScorePenalty)));
                if (penalty > 0)
                {
                    component.Score -= penalty;
                    CampaignDisplayChanged?.Invoke();
                }
            }

            Announce(Loc.GetString($"nsv-campaign-reminder-{stage}", ("penalty", penalty)));
            return;
        }

        var blockade = new NsvCampaignBlockadeEvent();
        RaiseLocalEvent(ref blockade);
        if (blockade.Dispatched)
        {
            Announce(Loc.GetString("nsv-campaign-reminder-blockade"));
            return;
        }

        if (AdjustThreat(component, _cfg.GetCVar(NsvCCVars.CampaignBlockadeFallbackThreat)))
            CampaignDisplayChanged?.Invoke();

        Announce(Loc.GetString("nsv-campaign-reminder-blockade-fallback"));
    }

    /// <summary>
    /// Objective progress restarts the stall clock and the reminder escalation.
    /// </summary>
    private static void ResetReminders(NsvCampaignRuleComponent component)
    {
        component.StallTime = 0f;
        component.ReminderStage = 0;
    }

    /// <summary>
    /// A campaign-wide announcement from Naval Command (briefing, reminders).
    /// </summary>
    private void Announce(string message)
    {
        _chatSystem.DispatchGlobalAnnouncement(
            message,
            Loc.GetString("nsv-campaign-command-sender"),
            colorOverride: Color.Gold);
    }

    private string GetObjectiveBrief(NsvCampaignObjective objective)
    {
        return objective.Kind switch
        {
            NsvCampaignObjectiveKind.PerformJumps => Loc.GetString("nsv-campaign-briefing-performjumps",
                ("target", objective.Target)),
            _ => Loc.GetString(GetObjectiveLabel(objective.Kind)),
        };
    }

    /// <summary>
    /// Counts down a voted extension and concludes the round when it runs out. Returns true once the
    /// round has been concluded this tick.
    /// </summary>
    private bool TickExtension(NsvCampaignRuleComponent component, float frameTime)
    {
        if (component.ExtensionRemaining is not { } remaining)
            return false;

        remaining -= frameTime;
        if (remaining > 0f)
        {
            component.ExtensionRemaining = remaining;
            return false;
        }

        Conclude(component, null);
        CampaignDisplayChanged?.Invoke();
        _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-extension-over"));
        _roundEnd.EndRound();
        return true;
    }

    /// <summary>
    /// Passive threat growth (S5): after a grace period of active-campaign time, threat rises by a
    /// fixed amount every fixed interval. Kills add threat on top of this via the kill-threat path.
    /// The grace clock always runs, so disabling growth (interval &lt;= 0) and re-enabling it later
    /// doesn't restart the grace period.
    /// </summary>
    private void TickThreatGrowth(NsvCampaignRuleComponent component, float frameTime)
    {
        var interval = _cfg.GetCVar(NsvCCVars.CampaignThreatGrowthInterval);
        if (interval <= 0f || component.ActiveTime < _cfg.GetCVar(NsvCCVars.CampaignThreatGracePeriod))
            return;

        var amount = _cfg.GetCVar(NsvCCVars.CampaignThreatGrowthAmount);
        component.ThreatGrowthAccumulator += frameTime;
        var grew = false;
        while (component.ThreatGrowthAccumulator >= interval)
        {
            component.ThreatGrowthAccumulator -= interval;
            grew |= AdjustThreat(component, amount);
        }

        if (grew)
            CampaignDisplayChanged?.Invoke();
    }

    /// <summary>
    /// Advances every in-progress PerformJumps objective on the live campaign by one, flipping it to
    /// Completed once its target is met. Called on each player FTL arrival into a bluespace sector
    /// (including Home arrivals, which then also go through <see cref="NotifyHomeArrival"/>).
    /// </summary>
    public void NotifyJumpArrived()
    {
        var changed = false;
        foreach (var campaign in LiveCampaigns())
        {
            foreach (var objective in campaign.Comp.Objectives)
            {
                if (objective.Kind != NsvCampaignObjectiveKind.PerformJumps ||
                    objective.Status != NsvCampaignObjectiveStatus.InProgress)
                {
                    continue;
                }

                objective.Tally++;
                changed = true;
                ResetReminders(campaign.Comp);
                if (objective.Tally >= objective.Target)
                {
                    objective.Status = NsvCampaignObjectiveStatus.Completed;
                    NegateThreatForObjective(campaign.Comp, objective);
                }
            }

            TryStartOutcomeVote(campaign);
        }

        if (changed)
            CampaignDisplayChanged?.Invoke();
    }

    /// <summary>
    /// Eases the owning campaign's threat when an objective is first completed (S5), rewarding
    /// progress. Guarded by <see cref="NsvCampaignObjective.ThreatNegated"/> so each objective decays
    /// threat at most once, which also stops a re-completed objective from farming the reduction.
    /// </summary>
    private void NegateThreatForObjective(NsvCampaignRuleComponent component, NsvCampaignObjective objective)
    {
        if (objective.ThreatNegated)
            return;

        objective.ThreatNegated = true;

        var negation = _cfg.GetCVar(NsvCCVars.CampaignObjectiveThreatNegation);
        if (negation > 0f)
            AdjustThreat(component, -negation);
    }

    /// <summary>
    /// Awards a cleared encounter's starmap-node reward to every live campaign's score (S4). Called
    /// from the encounter-completion path with the sector's originating starmap node; a template sector
    /// with no starmap node, an unknown node, or a zero-reward node is a no-op.
    /// </summary>
    public void NotifyEncounterComplete(string starmapId, string nodeId)
    {
        if (string.IsNullOrEmpty(starmapId) || string.IsNullOrEmpty(nodeId) ||
            !_protos.TryIndex<NsvBluespaceStarmapPrototype>(starmapId, out var starmap) ||
            !starmap.TryGetNode(nodeId, out var node) || node.Reward <= 0)
        {
            return;
        }

        var awarded = false;
        foreach (var campaign in LiveCampaigns())
        {
            campaign.Comp.Score += node.Reward;
            ResetReminders(campaign.Comp);
            awarded = true;
        }

        if (awarded)
            CampaignDisplayChanged?.Invoke();
    }

    /// <summary>
    /// Concludes the round in victory when the crew returns to a Home node (the extraction point),
    /// provided accumulated <see cref="NsvCampaignRuleComponent.Score"/> meets the victory threshold
    /// (S4 gate). Only fires on campaigns that haven't already reached an outcome, so a lost round
    /// (Defeat) or an in-flight outcome vote (Victory) isn't overwritten. Arriving home under the
    /// threshold announces the shortfall and is otherwise a no-op. Called on FTL arrival into a Home
    /// sector.
    /// </summary>
    public void NotifyHomeArrival()
    {
        var threshold = _cfg.GetCVar(NsvCCVars.CampaignVictoryScoreThreshold);

        var concluded = false;
        var blockedScore = -1;
        foreach (var campaign in LiveCampaigns())
        {
            if (campaign.Comp.Outcome != NsvCampaignOutcome.None)
                continue;

            if (campaign.Comp.Score < threshold)
            {
                if (blockedScore < 0)
                    blockedScore = campaign.Comp.Score;
                continue;
            }

            Conclude(campaign.Comp, NsvCampaignOutcome.Victory);
            concluded = true;
        }

        if (concluded)
        {
            CampaignDisplayChanged?.Invoke();
            _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-victory-home"));
            _roundEnd.EndRound();
            return;
        }

        if (blockedScore >= 0)
        {
            _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-home-insufficient",
                ("score", blockedScore), ("threshold", threshold)));
        }
    }

    /// <summary>
    /// Awards the entity's <see cref="NsvCampaignKillRewardComponent.Score"/> to every live campaign
    /// when it is destroyed. Any termination counts (first pass; no killer-faction gating).
    /// </summary>
    private void OnKillRewardTerminating(EntityUid uid, NsvCampaignKillRewardComponent reward, ref EntityTerminatingEvent args)
    {
        AwardKill(uid, reward);
    }

    /// <summary>
    /// Treats losing power as a kill for entities that need it, so a disabled-but-not-destroyed target
    /// still scores. Only fires for entities with a power receiver; regaining power does nothing.
    /// </summary>
    private void OnKillRewardPowerChanged(EntityUid uid, NsvCampaignKillRewardComponent reward, ref PowerChangedEvent args)
    {
        if (args.Powered)
            return;

        AwardKill(uid, reward);
    }

    /// <summary>
    /// Adds the reward's score to every live campaign, at most once per entity. Ships dying in the
    /// data state (their grid parked on the registry holding map) are AI-vs-AI abstract-combat losses,
    /// not player kills, so they never score; neither do deaths outside a live round (e.g. the
    /// restart flush).
    /// </summary>
    private void AwardKill(EntityUid uid, NsvCampaignKillRewardComponent reward)
    {
        if (reward.Rewarded || IsDataStateGrid(uid))
            return;

        var campaigns = LiveCampaigns();
        if (campaigns.Count == 0)
            return;

        reward.Rewarded = true;
        foreach (var campaign in campaigns)
        {
            campaign.Comp.Score += reward.Score;
        }

        CampaignDisplayChanged?.Invoke();
    }

    /// <summary>
    /// Raises campaign threat by the entity's <see cref="NsvCampaignKillThreatComponent.Threat"/> when
    /// it is destroyed. Any termination counts (mirrors the kill-reward pass; no killer-faction gating).
    /// </summary>
    private void OnKillThreatTerminating(EntityUid uid, NsvCampaignKillThreatComponent threat, ref EntityTerminatingEvent args)
    {
        ContributeKillThreat(uid, threat);
    }

    /// <summary>
    /// Treats losing power as a kill for entities that need it, so a disabled-but-not-destroyed target
    /// still raises threat. Only fires for entities with a power receiver; regaining power does nothing.
    /// </summary>
    private void OnKillThreatPowerChanged(EntityUid uid, NsvCampaignKillThreatComponent threat, ref PowerChangedEvent args)
    {
        if (args.Powered)
            return;

        ContributeKillThreat(uid, threat);
    }

    /// <summary>
    /// Adds the component's threat to every live campaign (floored at 0), at most once per entity.
    /// Data-state (parked) deaths are AI-vs-AI abstract losses and never raise threat.
    /// </summary>
    private void ContributeKillThreat(EntityUid uid, NsvCampaignKillThreatComponent threat)
    {
        if (threat.Contributed || IsDataStateGrid(uid))
            return;

        var campaigns = LiveCampaigns();
        if (campaigns.Count == 0)
            return;

        threat.Contributed = true;
        var adjusted = false;
        foreach (var campaign in campaigns)
        {
            adjusted |= AdjustThreat(campaign.Comp, threat.Threat);
        }

        if (adjusted)
            CampaignDisplayChanged?.Invoke();
    }

    /// <summary>
    /// Whether <paramref name="uid"/> sits on the registry's holding map, i.e. it is a parked data-state
    /// ship rather than a live-world entity. Kill-scoring uses this to ignore abstract (AI-vs-AI)
    /// combat deaths, which delete parked grids.
    /// </summary>
    private bool IsDataStateGrid(EntityUid uid)
    {
        return _fleets.HoldingMap != MapId.Nullspace && Transform(uid).MapID == _fleets.HoldingMap;
    }

    /// <summary>
    /// Designates <paramref name="target"/> as the campaign flagship: losing it (the entity
    /// terminating) concludes the round in defeat. Idempotent. Admin-driven (see the flagship verb)
    /// since there is no automatic player-ship entry yet.
    /// </summary>
    public void SetFlagship(EntityUid target)
    {
        EnsureComp<NsvCampaignFlagshipComponent>(target);
    }

    /// <summary>
    /// Removes the flagship designation from <paramref name="target"/>, if present. Returns whether
    /// the entity was a flagship.
    /// </summary>
    public bool ClearFlagship(EntityUid target)
    {
        return RemComp<NsvCampaignFlagshipComponent>(target);
    }

    /// <summary>
    /// Whether <paramref name="target"/> is currently designated as a campaign flagship.
    /// </summary>
    public bool IsFlagship(EntityUid target)
    {
        return HasComp<NsvCampaignFlagshipComponent>(target);
    }

    /// <summary>
    /// The recorded campaign outcome (None until a victory vote starts or the flagship is lost).
    /// Reads the rule regardless of active state so the final outcome is observable as the round
    /// winds down.
    /// </summary>
    public NsvCampaignOutcome GetOutcome()
    {
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent>();
        while (query.MoveNext(out _, out var component))
        {
            if (component.Outcome != NsvCampaignOutcome.None)
                return component.Outcome;
        }

        return NsvCampaignOutcome.None;
    }

    /// <summary>
    /// Losing the flagship (the marked entity terminating — e.g. the ship's main console destroyed
    /// with the ship) concludes the round in defeat. Guarded by <see cref="NsvCampaignOutcome"/> so a
    /// campaign that has already reached an outcome (a won round mid-extension) isn't overwritten, and
    /// by <see cref="LiveCampaigns"/> so the restart flush deleting the flagship isn't a defeat.
    /// </summary>
    private void OnFlagshipTerminating(EntityUid uid, NsvCampaignFlagshipComponent component, ref EntityTerminatingEvent args)
    {
        var concluded = false;
        foreach (var campaign in LiveCampaigns())
        {
            if (campaign.Comp.Outcome != NsvCampaignOutcome.None)
                continue;

            Conclude(campaign.Comp, NsvCampaignOutcome.Defeat);
            concluded = true;
        }

        if (!concluded)
            return;

        CampaignDisplayChanged?.Invoke();
        _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-defeat-flagship-lost"));
        _roundEnd.EndRound();
    }

    /// <summary>
    /// When every round objective is complete, puts the outcome vote to the crew. Guarded by
    /// <see cref="NsvCampaignRuleComponent.Outcome"/> so it fires at most once per round.
    /// </summary>
    private void TryStartOutcomeVote(Entity<NsvCampaignRuleComponent> campaign)
    {
        var component = campaign.Comp;
        if (component.Outcome != NsvCampaignOutcome.None || component.Objectives.Count == 0)
            return;

        foreach (var objective in component.Objectives)
        {
            if (objective.Status != NsvCampaignObjectiveStatus.Completed)
                return;
        }

        StartOutcomeVote(campaign);
    }

    /// <summary>
    /// Admin override: puts the outcome vote to the crew on the first live campaign that isn't
    /// already resolving one, regardless of objective progress. Exposed for the sector-monitor admin
    /// panel so the vote flow can be exercised without completing every objective. Returns false when
    /// no eligible campaign exists.
    /// </summary>
    public bool ForceOutcomeVote()
    {
        foreach (var campaign in LiveCampaigns())
        {
            if (campaign.Comp.Outcome != NsvCampaignOutcome.None)
                continue;

            StartOutcomeVote(campaign);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Records a victory and puts the "press on or conclude" decision to a crew vote; the result is
    /// applied by <see cref="ApplyOutcomeVoteResult"/>.
    /// </summary>
    private void StartOutcomeVote(Entity<NsvCampaignRuleComponent> campaign)
    {
        campaign.Comp.Outcome = NsvCampaignOutcome.Victory;

        var minutes = (int) MathF.Round(_cfg.GetCVar(NsvCCVars.CampaignExtensionDuration) / 60f);
        var options = new VoteOptions
        {
            Title = Loc.GetString("nsv-campaign-vote-title"),
            Options =
            {
                (Loc.GetString("nsv-campaign-vote-extend", ("minutes", minutes)), ExtendOption),
                (Loc.GetString("nsv-campaign-vote-end"), EndOption),
            },
            Duration = OutcomeVoteDuration,
        };
        options.SetInitiatorOrServer(null);

        var ruleUid = campaign.Owner;
        var vote = _votes.CreateVote(options);
        campaign.Comp.OutcomeVote = vote;
        vote.OnFinished += (_, args) => ApplyOutcomeVoteResult(ruleUid, args.Winner as string);
    }

    /// <summary>
    /// Applies a finished outcome vote. Ties (null winner) and an "end" majority both conclude the
    /// round; only an explicit "extend" majority keeps it going, for
    /// <see cref="NsvCCVars.CampaignExtensionDuration"/> seconds. A no-op unless the voting rule is
    /// still live and undecided, so a vote outliving its round never touches the next one. Internal
    /// so tests can resolve a vote without driving the vote manager.
    /// </summary>
    internal void ApplyOutcomeVoteResult(EntityUid ruleUid, string? winner)
    {
        if (!TryGetLiveCampaign(ruleUid, out var component) || component.Phase != NsvCampaignPhase.Active)
            return;

        component.OutcomeVote = null;

        if (winner == ExtendOption)
        {
            var duration = MathF.Max(0f, _cfg.GetCVar(NsvCCVars.CampaignExtensionDuration));
            component.Phase = NsvCampaignPhase.Extending;
            component.ExtensionRemaining = duration;
            CampaignDisplayChanged?.Invoke();
            _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-vote-extended",
                ("minutes", (int) MathF.Round(duration / 60f))));
            return;
        }

        Conclude(component, null);
        CampaignDisplayChanged?.Invoke();
        _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-vote-ended"));
        _roundEnd.EndRound();
    }

    /// <summary>
    /// Marks a campaign concluded: optionally records the outcome, ends the phase, and stops any
    /// extension countdown. Callers announce and end the round.
    /// </summary>
    private static void Conclude(NsvCampaignRuleComponent component, NsvCampaignOutcome? outcome)
    {
        if (outcome is { } recorded)
            component.Outcome = recorded;

        component.Phase = NsvCampaignPhase.Ended;
        component.ExtensionRemaining = null;
    }

    /// <summary>
    /// Whether any campaign rule is currently active. Cheap gate for background systems (S7 fleet
    /// spawning) that should stay dormant outside a campaign round.
    /// </summary>
    public bool HasActiveCampaign()
    {
        return ActiveCampaigns().Count > 0;
    }

    /// <summary>
    /// Adjusts the threat elevation on every live campaign by <paramref name="delta"/> (may be
    /// negative), floored at 0. Public entry point for external threat sources; the field is
    /// otherwise [Access]-locked.
    /// </summary>
    public void AdjustThreatElevation(float delta)
    {
        var adjusted = false;
        foreach (var campaign in LiveCampaigns())
        {
            adjusted |= AdjustThreat(campaign.Comp, delta);
        }

        if (adjusted)
            CampaignDisplayChanged?.Invoke();
    }

    /// <summary>
    /// Single mutation point for one campaign's threat: applies <paramref name="delta"/> floored at 0
    /// and returns whether the value changed. Callers raise <see cref="CampaignDisplayChanged"/>.
    /// </summary>
    private static bool AdjustThreat(NsvCampaignRuleComponent component, float delta)
    {
        var next = MathF.Max(0f, component.ThreatElevation + delta);
        if (next.Equals(component.ThreatElevation))
            return false;

        component.ThreatElevation = next;
        return true;
    }

    /// <summary>
    /// Current threat elevation of the active campaign, or 0 when no campaign rule is running.
    /// Read-only accessor for systems that scale off threat (S7) without touching the locked field.
    /// </summary>
    public float GetThreatElevation()
    {
        var campaigns = ActiveCampaigns();
        return campaigns.Count > 0 ? campaigns[0].Comp.ThreatElevation : 0f;
    }

    /// <summary>
    /// Builds a client-facing readout of the active campaign, or null when no campaign rule is
    /// running. Enum-to-loc-key mapping stays here so the shared DTO carries only strings.
    /// </summary>
    public NsvCampaignSummaryState? TryBuildSummary()
    {
        var campaigns = ActiveCampaigns();
        if (campaigns.Count == 0)
            return null;

        var component = campaigns[0].Comp;
        var objectives = new List<NsvCampaignObjectiveReadout>(component.Objectives.Count);
        foreach (var objective in component.Objectives)
        {
            objectives.Add(new NsvCampaignObjectiveReadout(
                GetObjectiveLabel(objective.Kind),
                GetObjectiveStatus(objective.Status),
                objective.Tally,
                objective.Target));
        }

        return new NsvCampaignSummaryState(
            GetPhase(component.Phase),
            component.Score,
            (int) MathF.Round(component.ThreatElevation),
            objectives);
    }

    /// <summary>
    /// Every campaign rule that is currently active. Snapshot list, safe to mutate while iterating.
    /// Use for read-only accessors.
    /// </summary>
    private List<Entity<NsvCampaignRuleComponent>> ActiveCampaigns()
    {
        var campaigns = new List<Entity<NsvCampaignRuleComponent>>();
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var component, out var gameRule))
        {
            if (GameTicker.IsGameRuleActive(uid, gameRule))
                campaigns.Add((uid, component));
        }

        return campaigns;
    }

    /// <summary>
    /// Active campaigns that can still change: the round is in progress and the campaign hasn't been
    /// concluded. Every mutation (scoring, threat, objectives, outcomes) goes through this gate.
    /// </summary>
    private List<Entity<NsvCampaignRuleComponent>> LiveCampaigns()
    {
        if (GameTicker.RunLevel != GameRunLevel.InRound)
            return new List<Entity<NsvCampaignRuleComponent>>();

        var campaigns = ActiveCampaigns();
        campaigns.RemoveAll(campaign => campaign.Comp.Phase == NsvCampaignPhase.Ended);
        return campaigns;
    }

    private bool TryGetLiveCampaign(EntityUid ruleUid, out NsvCampaignRuleComponent component)
    {
        component = default!;
        if (GameTicker.RunLevel != GameRunLevel.InRound ||
            !TryComp<NsvCampaignRuleComponent>(ruleUid, out var found) ||
            !TryComp<GameRuleComponent>(ruleUid, out var gameRule) ||
            !GameTicker.IsGameRuleActive(ruleUid, gameRule) ||
            found.Phase == NsvCampaignPhase.Ended)
        {
            return false;
        }

        component = found;
        return true;
    }

    private static string GetPhase(NsvCampaignPhase phase)
    {
        return phase switch
        {
            NsvCampaignPhase.Active => "nsv-campaign-phase-active",
            NsvCampaignPhase.Extending => "nsv-campaign-phase-extending",
            NsvCampaignPhase.Ended => "nsv-campaign-phase-ended",
            _ => "nsv-campaign-phase-active"
        };
    }

    private static string GetObjectiveLabel(NsvCampaignObjectiveKind kind)
    {
        return kind switch
        {
            NsvCampaignObjectiveKind.PerformJumps => "nsv-campaign-objective-performjumps",
            _ => "nsv-campaign-objective-performjumps"
        };
    }

    private static string GetObjectiveStatus(NsvCampaignObjectiveStatus status)
    {
        return status switch
        {
            NsvCampaignObjectiveStatus.InProgress => "nsv-campaign-objective-status-inprogress",
            NsvCampaignObjectiveStatus.Completed => "nsv-campaign-objective-status-completed",
            _ => "nsv-campaign-objective-status-inprogress"
        };
    }
}
