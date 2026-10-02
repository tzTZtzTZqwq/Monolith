using Content.Server._NSV.Bluespace.Strategy;
using Content.Server._NSV.GameRule.Components;
using Content.Server.Chat.Managers;
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
using Robust.Shared.Timing;

namespace Content.Server._NSV.GameRule;

/// <summary>
/// Drives the NSV campaign round loop. Round-scoped state lives on
/// <see cref="NsvCampaignRuleComponent"/> (the game-rule entity); this system reacts to the
/// rule's lifecycle and is otherwise stateless.
/// </summary>
public sealed partial class NsvCampaignRuleSystem : GameRuleSystem<NsvCampaignRuleComponent>
{
    // Baseline "perform N jumps" objective range (ROUND-001, S2).
    private const int MinBaselineJumps = 6;
    private const int MaxBaselineJumps = 10;

    // Outcome vote shown when every objective is complete (S3).
    private static readonly TimeSpan OutcomeVoteDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExtensionDuration = TimeSpan.FromMinutes(60);

    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IVoteManager _votes = default!;
    [Dependency] private RoundEndSystem _roundEnd = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private NsvFleetRegistrySystem _fleets = default!;
    [Dependency] private IPrototypeManager _protos = default!;

    /// <summary>
    /// Raised when the campaign readout (score / threat) changes outside a jump so listeners like the
    /// navigation console can push a fresh state without waiting for the next sector event.
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
    }

    /// <summary>
    /// Passive threat growth (S5): after a grace period of active-campaign time, threat rises by a
    /// fixed amount every fixed interval. Kills add threat on top of this via the kill-threat path.
    /// </summary>
    protected override void ActiveTick(EntityUid uid, NsvCampaignRuleComponent component, GameRuleComponent gameRule, float frameTime)
    {
        var interval = _cfg.GetCVar(NsvCCVars.CampaignThreatGrowthInterval);
        if (interval <= 0f)
            return;

        component.ActiveTime += frameTime;
        if (component.ActiveTime < _cfg.GetCVar(NsvCCVars.CampaignThreatGracePeriod))
            return;

        var amount = _cfg.GetCVar(NsvCCVars.CampaignThreatGrowthAmount);
        component.ThreatGrowthAccumulator += frameTime;
        var grew = false;
        while (component.ThreatGrowthAccumulator >= interval)
        {
            component.ThreatGrowthAccumulator -= interval;
            component.ThreatElevation += amount;
            grew = true;
        }

        if (grew)
            CampaignDisplayChanged?.Invoke();
    }

    /// <summary>
    /// Advances every in-progress PerformJumps objective on the active campaign by one, flipping it
    /// to Completed once its target is met. Called on each player FTL arrival into a bluespace sector.
    /// </summary>
    public void NotifyJumpArrived()
    {
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var component, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            foreach (var objective in component.Objectives)
            {
                if (objective.Kind != NsvCampaignObjectiveKind.PerformJumps ||
                    objective.Status != NsvCampaignObjectiveStatus.InProgress)
                {
                    continue;
                }

                objective.Tally++;
                if (objective.Tally >= objective.Target)
                {
                    objective.Status = NsvCampaignObjectiveStatus.Completed;
                    NegateThreatForObjective(objective);
                }
            }

            TryStartOutcomeVote(component);
        }
    }

    /// <summary>
    /// Eases campaign threat when an objective is first completed (S5), rewarding progress. Guarded by
    /// <see cref="NsvCampaignObjective.ThreatNegated"/> so each objective decays threat at most once,
    /// which also stops a re-completed objective from farming the reduction.
    /// </summary>
    private void NegateThreatForObjective(NsvCampaignObjective objective)
    {
        if (objective.ThreatNegated)
            return;

        objective.ThreatNegated = true;

        var negation = _cfg.GetCVar(NsvCCVars.CampaignObjectiveThreatNegation);
        if (negation > 0f)
            AdjustThreatElevation(-negation);
    }

    /// <summary>
    /// Awards a cleared encounter's starmap-node reward to every active campaign's score (S4). Called
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
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var component, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            component.Score += node.Reward;
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
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var component, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule) || component.Outcome != NsvCampaignOutcome.None)
                continue;

            if (component.Score < threshold)
            {
                if (blockedScore < 0)
                    blockedScore = component.Score;
                continue;
            }

            component.Outcome = NsvCampaignOutcome.Victory;
            component.Phase = NsvCampaignPhase.Ended;
            concluded = true;
        }

        if (concluded)
        {
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
    /// Awards the entity's <see cref="NsvCampaignKillRewardComponent.Score"/> to every active campaign
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
    /// Adds the reward's score to every active campaign, at most once per entity. Ships dying in the
    /// data state (their grid parked on the registry holding map) are AI-vs-AI abstract-combat losses,
    /// not player kills, so they never score.
    /// </summary>
    private void AwardKill(EntityUid uid, NsvCampaignKillRewardComponent reward)
    {
        if (reward.Rewarded || IsDataStateGrid(uid))
            return;

        reward.Rewarded = true;

        var awarded = false;
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var ruleUid, out var component, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(ruleUid, gameRule))
                continue;

            component.Score += reward.Score;
            awarded = true;
        }

        if (awarded)
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
    /// Adds the component's threat to every active campaign via <see cref="AdjustThreatElevation"/>
    /// (floored at 0 there), at most once per entity. Data-state (parked) deaths are AI-vs-AI abstract
    /// losses and never raise threat.
    /// </summary>
    private void ContributeKillThreat(EntityUid uid, NsvCampaignKillThreatComponent threat)
    {
        if (threat.Contributed || IsDataStateGrid(uid))
            return;

        threat.Contributed = true;
        AdjustThreatElevation(threat.Threat);
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
    /// campaign that has already reached an outcome (a won round mid-extension) isn't overwritten.
    /// </summary>
    private void OnFlagshipTerminating(EntityUid uid, NsvCampaignFlagshipComponent component, ref EntityTerminatingEvent args)
    {
        var concluded = false;
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var ruleUid, out var rule, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(ruleUid, gameRule) || rule.Outcome != NsvCampaignOutcome.None)
                continue;

            rule.Outcome = NsvCampaignOutcome.Defeat;
            concluded = true;
        }

        if (!concluded)
            return;

        _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-defeat-flagship-lost"));
        _roundEnd.EndRound();
    }

    /// <summary>
    /// When every round objective is complete, puts the outcome vote to the crew. Guarded by
    /// <see cref="NsvCampaignRuleComponent.Outcome"/> so it fires at most once per round.
    /// </summary>
    private void TryStartOutcomeVote(NsvCampaignRuleComponent component)
    {
        if (component.Outcome != NsvCampaignOutcome.None || component.Objectives.Count == 0)
            return;

        foreach (var objective in component.Objectives)
        {
            if (objective.Status != NsvCampaignObjectiveStatus.Completed)
                return;
        }

        StartOutcomeVote(component);
    }

    /// <summary>
    /// Admin override: puts the outcome vote to the crew on the first active campaign that isn't
    /// already resolving one, regardless of objective progress. Exposed for the sector-monitor admin
    /// panel so the vote flow can be exercised without completing every objective. Returns false when
    /// no eligible campaign exists.
    /// </summary>
    public bool ForceOutcomeVote()
    {
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var component, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule) || component.Outcome != NsvCampaignOutcome.None)
                continue;

            StartOutcomeVote(component);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Records a victory and puts the "press on or conclude" decision to a crew vote: press on
    /// extends the round by 60 minutes, otherwise it ends now.
    /// </summary>
    private void StartOutcomeVote(NsvCampaignRuleComponent component)
    {
        component.Outcome = NsvCampaignOutcome.Victory;

        var options = new VoteOptions
        {
            Title = Loc.GetString("nsv-campaign-vote-title"),
            Options =
            {
                (Loc.GetString("nsv-campaign-vote-extend"), "extend"),
                (Loc.GetString("nsv-campaign-vote-end"), "end"),
            },
            Duration = OutcomeVoteDuration,
        };
        options.SetInitiatorOrServer(null);

        var vote = _votes.CreateVote(options);
        vote.OnFinished += (_, args) =>
        {
            // Ties (Winner == null) and an "end" majority both conclude the round; only an explicit
            // "extend" majority keeps it going.
            if (args.Winner is "extend")
            {
                component.Phase = NsvCampaignPhase.Extending;
                _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-vote-extended"));
                Timer.Spawn(ExtensionDuration, () => _roundEnd.EndRound());
            }
            else
            {
                _chat.DispatchServerAnnouncement(Loc.GetString("nsv-campaign-vote-ended"));
                _roundEnd.EndRound();
            }
        };
    }

    /// <summary>
    /// Whether any campaign rule is currently active. Cheap gate for background systems (S7 fleet
    /// spawning) that should stay dormant outside a campaign round.
    /// </summary>
    public bool HasActiveCampaign()
    {
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out _, out var gameRule))
        {
            if (GameTicker.IsGameRuleActive(uid, gameRule))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Adjusts the threat elevation on every active campaign by <paramref name="delta"/> (may be
    /// negative), floored at 0. The single mutation entry point for S5 passive growth / kill bumps /
    /// objective-completion decay and S7 fleet-size scaling; the field is otherwise [Access]-locked.
    /// </summary>
    public void AdjustThreatElevation(float delta)
    {
        var adjusted = false;
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var component, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            component.ThreatElevation = MathF.Max(0f, component.ThreatElevation + delta);
            adjusted = true;
        }

        if (adjusted)
            CampaignDisplayChanged?.Invoke();
    }

    /// <summary>
    /// Current threat elevation of the active campaign, or 0 when no campaign rule is running.
    /// Read-only accessor for systems that scale off threat (S7) without touching the locked field.
    /// </summary>
    public float GetThreatElevation()
    {
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var component, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            return component.ThreatElevation;
        }

        return 0f;
    }

    /// <summary>
    /// Builds a client-facing readout of the active campaign, or null when no campaign rule is
    /// running. Enum-to-loc-key mapping stays here so the shared DTO carries only strings.
    /// </summary>
    public NsvCampaignSummaryState? TryBuildSummary()
    {
        var query = EntityQueryEnumerator<NsvCampaignRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var component, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

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

        return null;
    }

    private static string GetPhase(NsvCampaignPhase phase)
    {
        return phase switch
        {
            NsvCampaignPhase.Briefing => "nsv-campaign-phase-briefing",
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
            NsvCampaignObjectiveStatus.Failed => "nsv-campaign-objective-status-failed",
            NsvCampaignObjectiveStatus.Override => "nsv-campaign-objective-status-override",
            _ => "nsv-campaign-objective-status-inprogress"
        };
    }
}
