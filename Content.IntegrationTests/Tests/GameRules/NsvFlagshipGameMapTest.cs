using System.Collections.Generic;
using System.Linq;
using Content.Server._NSV.GameRule.Components;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.Maps;
using Content.Server.Station.Systems;
using Content.Shared.Maps;
using Content.Shared.Shuttles.Components;
using Content.Server.Spawners.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.GameRules;

/// <summary>
/// N1: the pure-campaign preset's round map. Loading NsvJupiterG the way the game ticker does turns
/// the jupiterG grid into the station, auto-marks it as the campaign flagship with an IFF, offers the
/// two flagship jobs, and provides a round-start spawn point for each of them plus a late-join point.
/// </summary>
[TestFixture]
public sealed class NsvFlagshipGameMapTest
{
    private const string GameMap = "NsvJupiterG";

    [Test]
    public async Task FlagshipMapBecomesStationWithJobsAndSpawns()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entityManager = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var gameTicker = entityManager.System<GameTicker>();
        var stations = entityManager.System<StationSystem>();
        var jobs = entityManager.System<StationJobsSystem>();

        await server.WaitAssertion(() =>
        {
            var preset = prototypes.Index<GamePresetPrototype>("MonoNsvFlagship");
            var pool = prototypes.Index<GameMapPoolPrototype>(preset.MapPool!);
            Assert.That(pool.Maps, Is.EquivalentTo(new[] { GameMap }), "the preset forces the flagship map");

            var grids = gameTicker.LoadGameMap(prototypes.Index<GameMapPrototype>(GameMap), out _);
            Assert.That(grids, Has.Count.EqualTo(1));
            var ship = grids[0];

            var station = stations.GetOwningStation(ship);
            Assert.That(station, Is.Not.Null, "jupiterG becomes the station");

            var slots = jobs.GetJobs(station!.Value);
            Assert.Multiple(() =>
            {
                Assert.That(slots.Keys.Select(job => job.Id), Is.EquivalentTo(new[] { "NsvCaptain", "NsvCrew" }));
                Assert.That(slots["NsvCaptain"], Is.EqualTo(1), "one captain");
                Assert.That(slots["NsvCrew"], Is.Null, "unlimited crew");
                Assert.That(entityManager.HasComponent<NsvCampaignFlagshipComponent>(ship), Is.True,
                    "the ship is the campaign flagship without admin setup");
                Assert.That(entityManager.HasComponent<IFFComponent>(ship), Is.True);
            });

            // The game map loads paused before the round starts, and the plain query skips paused
            // entities, so walk all of them.
            var jobSpawns = new List<SpawnPointComponent>();
            var query = entityManager.AllEntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
            while (query.MoveNext(out _, out var spawn, out var xform))
            {
                if (xform.GridUid == ship)
                    jobSpawns.Add(spawn);
            }
            Assert.Multiple(() =>
            {
                Assert.That(jobSpawns.Count(s => s.SpawnType == SpawnPointType.Job && s.Job == "NsvCaptain"),
                    Is.GreaterThan(0), "captain spawn point");
                Assert.That(jobSpawns.Count(s => s.SpawnType == SpawnPointType.Job && s.Job == "NsvCrew"),
                    Is.GreaterThan(0), "crew spawn point");
                Assert.That(jobSpawns.Count(s => s.SpawnType == SpawnPointType.LateJoin),
                    Is.GreaterThan(0), "late-join spawn point");
            });
        });

        await pair.CleanReturnAsync();
    }
}
