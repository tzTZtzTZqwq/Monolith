using System.Linq;
using System.Numerics;
using Content.Server._NSV.Bluespace.Strategy;
using Content.Server._NSV.GameRule;
using Content.Server._NSV.GameRule.Components;
using Content.Server.GameTicking;
using Content.Shared._NSV.CCVar;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.GameRules;

[TestFixture]
public sealed class NsvCampaignRuleTest
{
    private const string InProgress = "nsv-campaign-objective-status-inprogress";
    private const string Completed = "nsv-campaign-objective-status-completed";

    /// <summary>
    ///     The baseline PerformJumps objective advances one tally per FTL arrival and flips to
    ///     completed once it reaches its target, and extra arrivals don't push it past target.
    /// </summary>
    [Test]
    public async Task PerformJumpsCompletesOnArrivals()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);

            var summary = campaign.TryBuildSummary();
            Assert.That(summary, Is.Not.Null, "Summary should exist once the campaign rule is active.");
            Assert.That(summary!.Objectives, Has.Count.EqualTo(1));

            var target = summary.Objectives[0].Target;
            Assert.Multiple(() =>
            {
                Assert.That(target, Is.GreaterThan(0));
                Assert.That(summary.Objectives[0].Tally, Is.EqualTo(0));
                Assert.That(summary.Objectives[0].StatusLoc, Is.EqualTo(InProgress));
            });

            // One short of target: still in progress.
            for (var i = 0; i < target - 1; i++)
                campaign.NotifyJumpArrived();

            summary = campaign.TryBuildSummary();
            Assert.Multiple(() =>
            {
                Assert.That(summary!.Objectives[0].Tally, Is.EqualTo(target - 1));
                Assert.That(summary.Objectives[0].StatusLoc, Is.EqualTo(InProgress));
            });

            // The final arrival completes the objective.
            campaign.NotifyJumpArrived();
            summary = campaign.TryBuildSummary();
            Assert.Multiple(() =>
            {
                Assert.That(summary!.Objectives[0].Tally, Is.EqualTo(target));
                Assert.That(summary.Objectives[0].StatusLoc, Is.EqualTo(Completed));
            });

            // Arrivals after completion must not advance the tally further.
            campaign.NotifyJumpArrived();
            summary = campaign.TryBuildSummary();
            Assert.That(summary!.Objectives[0].Tally, Is.EqualTo(target));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Losing the designated flagship (the marked entity terminating) concludes the campaign in
    ///     defeat.
    /// </summary>
    [Test]
    public async Task FlagshipLostConcludesInDefeat()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            Assert.That(campaign.GetOutcome(), Is.EqualTo(NsvCampaignOutcome.None));

            // Stand in for the ship's main console: an ordinary entity the admin verb would mark.
            var flagship = entityManager.SpawnEntity(null, testMap.MapCoords);
            campaign.SetFlagship(flagship);
            Assert.That(campaign.IsFlagship(flagship), Is.True);

            // Destroying it must lose the round.
            entityManager.DeleteEntity(flagship);
            Assert.That(campaign.GetOutcome(), Is.EqualTo(NsvCampaignOutcome.Defeat),
                "losing the flagship concludes the campaign in defeat");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Returning to a Home node only concludes the campaign in victory once accumulated score
    ///     meets the victory threshold; arriving under the threshold leaves the outcome unresolved.
    /// </summary>
    [Test]
    public async Task HomeArrivalConcludesInVictory()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.CampaignVictoryScoreThreshold, 2);
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            Assert.That(campaign.GetOutcome(), Is.EqualTo(NsvCampaignOutcome.None));

            // Under the score threshold: returning home must not win.
            campaign.NotifyHomeArrival();
            Assert.That(campaign.GetOutcome(), Is.EqualTo(NsvCampaignOutcome.None),
                "returning home below the score threshold does not conclude the campaign");

            // Earn score by destroying two kill-reward entities (1 each).
            for (var i = 0; i < 2; i++)
            {
                var kill = entityManager.SpawnEntity(null, testMap.MapCoords);
                entityManager.AddComponent<NsvCampaignKillRewardComponent>(kill);
                entityManager.DeleteEntity(kill);
            }

            // Threshold met: returning home now wins.
            campaign.NotifyHomeArrival();
            Assert.That(campaign.GetOutcome(), Is.EqualTo(NsvCampaignOutcome.Victory),
                "returning home at or above the score threshold concludes the campaign in victory");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     A data-state (AI-vs-AI) death must never score: a kill-reward entity destroyed on the
    ///     registry's holding map is invisible to the campaign scoreboard, so returning home
    ///     afterward stays below the victory threshold. Contrasts <see cref="HomeArrivalConcludesInVictory"/>,
    ///     where the same kills on a live map do count.
    /// </summary>
    [Test]
    public async Task DataStateDeathDoesNotScore()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();
        var fleets = sysMan.GetEntitySystem<NsvFleetRegistrySystem>();
        var mapSystem = entityManager.System<SharedMapSystem>();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.CampaignVictoryScoreThreshold, 1);
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);

            // A kill-reward entity parked on the holding map stands in for a data ship destroyed by
            // abstract combat. Its death is AI attrition, not a player kill.
            var holdingMap = mapSystem.GetMap(fleets.GetOrCreateHoldingMap());
            var dataKill = entityManager.SpawnEntity(null, new EntityCoordinates(holdingMap, Vector2.Zero));
            entityManager.AddComponent<NsvCampaignKillRewardComponent>(dataKill);
            entityManager.DeleteEntity(dataKill);

            // No score was earned, so arriving home at threshold 1 must not win.
            campaign.NotifyHomeArrival();
            Assert.That(campaign.GetOutcome(), Is.EqualTo(NsvCampaignOutcome.None),
                "an abstract (holding-map) death does not count toward campaign score");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Threat elevation rises passively once the grace period has elapsed.
    /// </summary>
    [Test]
    public async Task ThreatGrowsPassivelyAfterGrace()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();

        await server.WaitPost(() =>
        {
            // No grace, grow (almost) every tick, so a handful of ticks is enough to observe growth.
            cfg.SetCVar(NsvCCVars.CampaignThreatGracePeriod, 0f);
            cfg.SetCVar(NsvCCVars.CampaignThreatGrowthInterval, 0.01f);
            cfg.SetCVar(NsvCCVars.CampaignThreatGrowthAmount, 1f);
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            Assert.That(campaign.GetThreatElevation(), Is.EqualTo(0f));
        });

        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(campaign.GetThreatElevation(), Is.GreaterThan(0f),
                "threat elevation rises passively after the grace period");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Completing a round objective eases threat by the negation amount, and does so at most once
    ///     per objective even if further arrivals arrive after completion.
    /// </summary>
    [Test]
    public async Task CompletingObjectiveEasesThreatOnce()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.CampaignObjectiveThreatNegation, 3f);
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);

            // Bank threat so the objective reward has room to eat into it.
            campaign.AdjustThreatElevation(10f);
            Assert.That(campaign.GetThreatElevation(), Is.EqualTo(10f));

            var summary = campaign.TryBuildSummary();
            var target = summary!.Objectives[0].Target;

            // Drive the PerformJumps objective to completion.
            for (var i = 0; i < target; i++)
                campaign.NotifyJumpArrived();

            Assert.That(campaign.GetThreatElevation(), Is.EqualTo(7f),
                "completing the objective eases threat by the negation amount");

            // Arrivals past completion must not decay again: the objective is completed and negated.
            campaign.NotifyJumpArrived();
            Assert.That(campaign.GetThreatElevation(), Is.EqualTo(7f),
                "threat is eased at most once per objective");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Clearing an encounter awards its starmap node's reward to the campaign score. alpha-3 is
    ///     the PiratePatrol node carrying the NSVPatrolContract encounter with reward 4.
    /// </summary>
    [Test]
    public async Task EncounterCompletionAwardsNodeReward()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var sysMan = server.ResolveDependency<IEntitySystemManager>();
        var gameTicker = sysMan.GetEntitySystem<GameTicker>();
        var campaign = sysMan.GetEntitySystem<NsvCampaignRuleSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(0));

            campaign.NotifyEncounterComplete("NSVBluespaceStrategicMap", "alpha-3");
            Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(4),
                "clearing the encounter adds the node's reward to the score");

            // A template sector (no starmap node) or unknown node contributes nothing.
            campaign.NotifyEncounterComplete(string.Empty, string.Empty);
            campaign.NotifyEncounterComplete("NSVBluespaceStrategicMap", "does-not-exist");
            Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(4),
                "an unresolved node awards no score");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     The admin panel's campaign controls: the view reflects live state, score and threat can be
    ///     set and nudged (floored at 0), and the briefing / next reminder can be fired on demand.
    /// </summary>
    [Test]
    public async Task AdminControlsAdjustAndBroadcast()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = entityManager.System<GameTicker>();
        var campaign = entityManager.System<NsvCampaignRuleSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(campaign.GetAdminView(), Is.Null, "no view without a campaign");
            Assert.That(campaign.AdminSetScore(3), Is.False, "no live campaign to change");

            cfg.SetCVar(NsvCCVars.CampaignReminderScorePenalty, 1);
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);

            Assert.That(campaign.AdminSetScore(6), Is.True);
            Assert.That(campaign.AdminAdjustScore(-2), Is.True);
            Assert.That(campaign.AdminSetThreat(7.5f), Is.True);
            var view = campaign.GetAdminView()!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(view.Summary.Score, Is.EqualTo(4));
                Assert.That(view.Summary.ThreatElevation, Is.EqualTo(8), "summary threat is rounded");
                Assert.That(view.BriefingDelivered, Is.False);
            });

            campaign.AdminAdjustScore(-100);
            campaign.AdminSetThreat(-3f);
            view = campaign.GetAdminView()!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(view.Summary.Score, Is.EqualTo(0), "score is floored at 0");
                Assert.That(view.Summary.ThreatElevation, Is.EqualTo(0), "threat is floored at 0");
            });

            Assert.That(campaign.AdminAnnounceBriefing(), Is.True);
            campaign.AdminSetScore(2);
            Assert.That(campaign.AdminSendNextReminder(), Is.EqualTo(1));
            Assert.That(campaign.AdminSendNextReminder(), Is.EqualTo(2));
            view = campaign.GetAdminView()!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(view.BriefingDelivered, Is.True);
                Assert.That(view.ReminderStage, Is.EqualTo(2));
                Assert.That(view.Summary.Score, Is.EqualTo(1), "reminder 2 really withholds a point");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Naval Command's mission briefing is announced once, after the configured delay.
    /// </summary>
    [Test]
    public async Task BriefingAnnouncedAfterDelay()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = entityManager.System<GameTicker>();
        var ruleUid = EntityUid.Invalid;

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.CampaignBriefingDelay, 0.1f);
            Assert.That(gameTicker.StartGameRule("NsvCampaign", out ruleUid), Is.True);
            Assert.That(entityManager.GetComponent<NsvCampaignRuleComponent>(ruleUid).BriefingDelivered, Is.False);
        });

        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(entityManager.GetComponent<NsvCampaignRuleComponent>(ruleUid).BriefingDelivered, Is.True);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Stalled objectives escalate (NSV13 ROUND-006): reminder 1 warns, 2–4 each withhold victory
    ///     score, 5 sends a blockade — or, with the crew outside any bluespace sector, raises threat —
    ///     and the cycle then wraps. Objective progress resets the escalation.
    /// </summary>
    [Test]
    public async Task StalledObjectivesEscalateAndProgressResets()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = entityManager.System<GameTicker>();
        var campaign = entityManager.System<NsvCampaignRuleSystem>();

        await server.WaitAssertion(() =>
        {
            const float interval = 10f;
            cfg.SetCVar(NsvCCVars.CampaignReminderInterval, interval);
            cfg.SetCVar(NsvCCVars.CampaignReminderScorePenalty, 1);
            cfg.SetCVar(NsvCCVars.CampaignBlockadeFallbackThreat, 5f);
            Assert.That(gameTicker.StartGameRule("NsvCampaign", out var ruleUid), Is.True);
            var rule = entityManager.GetComponent<NsvCampaignRuleComponent>(ruleUid);

            // alpha-3 rewards 4, giving the penalties something to withhold.
            campaign.NotifyEncounterComplete("NSVBluespaceStrategicMap", "alpha-3");
            Assert.That(rule.Score, Is.EqualTo(4));

            campaign.TickReminders(rule, interval);
            Assert.Multiple(() =>
            {
                Assert.That(rule.ReminderStage, Is.EqualTo(1));
                Assert.That(rule.Score, Is.EqualTo(4), "the first reminder only warns");
            });

            for (var i = 0; i < 3; i++)
                campaign.TickReminders(rule, interval);
            Assert.Multiple(() =>
            {
                Assert.That(rule.ReminderStage, Is.EqualTo(4));
                Assert.That(rule.Score, Is.EqualTo(1), "reminders 2-4 each withhold one point");
            });

            var threatBefore = rule.ThreatElevation;
            campaign.TickReminders(rule, interval);
            Assert.Multiple(() =>
            {
                Assert.That(rule.ReminderStage, Is.EqualTo(5));
                Assert.That(rule.ThreatElevation, Is.EqualTo(threatBefore + 5f),
                    "no crew in a sector, so the blockade falls back to threat");
            });

            campaign.TickReminders(rule, interval);
            Assert.That(rule.ReminderStage, Is.EqualTo(1), "the escalation wraps after the blockade");

            campaign.NotifyJumpArrived();
            Assert.Multiple(() =>
            {
                Assert.That(rule.ReminderStage, Is.EqualTo(0), "objective progress resets the escalation");
                Assert.That(rule.StallTime, Is.EqualTo(0f));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     An "extend" vote keeps the round going for the configured extension, after which the
    ///     campaign concludes and the round ends. A tie or "end" result ends the round straight away.
    /// </summary>
    [Test]
    public async Task ExtensionRunsOutAndEndsRound()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = entityManager.System<GameTicker>();
        var campaign = entityManager.System<NsvCampaignRuleSystem>();
        var ruleUid = EntityUid.Invalid;

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.CampaignExtensionDuration, 0.2f);
            Assert.That(gameTicker.StartGameRule("NsvCampaign", out ruleUid), Is.True);

            campaign.ApplyOutcomeVoteResult(ruleUid, "extend");
            var rule = entityManager.GetComponent<NsvCampaignRuleComponent>(ruleUid);
            Assert.Multiple(() =>
            {
                Assert.That(rule.Phase, Is.EqualTo(NsvCampaignPhase.Extending));
                Assert.That(gameTicker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            });
        });

        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entityManager.GetComponent<NsvCampaignRuleComponent>(ruleUid).Phase,
                    Is.EqualTo(NsvCampaignPhase.Ended));
                Assert.That(gameTicker.RunLevel, Is.EqualTo(GameRunLevel.PostRound),
                    "the extension running out ends the round");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Round-restart safety: once the campaign rule has ended, neither a pending extension nor a
    ///     late vote result may touch the round that follows. (The old detached 60-minute timer would
    ///     have ended whatever round was running when it fired.)
    /// </summary>
    [Test]
    public async Task EndedRuleIgnoresExtensionAndLateVote()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entityManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var gameTicker = entityManager.System<GameTicker>();
        var campaign = entityManager.System<NsvCampaignRuleSystem>();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(NsvCCVars.CampaignExtensionDuration, 0.2f);
            Assert.That(gameTicker.StartGameRule("NsvCampaign", out var extendedRule), Is.True);
            campaign.ApplyOutcomeVoteResult(extendedRule, "extend");
            Assert.That(gameTicker.EndGameRule(extendedRule), Is.True);

            // A second campaign whose vote resolves only after its rule has already ended.
            Assert.That(gameTicker.StartGameRule("NsvCampaign", out var votingRule), Is.True);
            Assert.That(gameTicker.EndGameRule(votingRule), Is.True);
            campaign.ApplyOutcomeVoteResult(votingRule, "end");

            Assert.That(gameTicker.RunLevel, Is.EqualTo(GameRunLevel.InRound),
                "a vote resolving after its rule ended does not end the round");
        });

        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(gameTicker.RunLevel, Is.EqualTo(GameRunLevel.InRound),
                "an ended rule's extension never fires");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Outside a live round (post-round, or the restart flush) deaths must not score and losing
    ///     the flagship must not record a defeat or announce one.
    /// </summary>
    [Test]
    public async Task DeathsOutsideLiveRoundDoNothing()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false
        });
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entityManager = server.ResolveDependency<IEntityManager>();
        var gameTicker = entityManager.System<GameTicker>();
        var campaign = entityManager.System<NsvCampaignRuleSystem>();
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            Assert.That(gameTicker.StartGameRule("NsvCampaign"), Is.True);
            var flagship = entityManager.SpawnEntity(null, testMap.MapCoords);
            campaign.SetFlagship(flagship);
            var kill = entityManager.SpawnEntity(null, testMap.MapCoords);
            entityManager.AddComponent<NsvCampaignKillRewardComponent>(kill);

            gameTicker.EndRound();
            Assert.That(gameTicker.RunLevel, Is.Not.EqualTo(GameRunLevel.InRound));

            entityManager.DeleteEntity(kill);
            entityManager.DeleteEntity(flagship);

            Assert.Multiple(() =>
            {
                Assert.That(campaign.GetOutcome(), Is.EqualTo(NsvCampaignOutcome.None),
                    "losing the flagship after the round ended is not a defeat");
                Assert.That(campaign.TryBuildSummary()!.Score, Is.EqualTo(0),
                    "a kill after the round ended does not score");
            });
        });

        await pair.CleanReturnAsync();
    }
}
