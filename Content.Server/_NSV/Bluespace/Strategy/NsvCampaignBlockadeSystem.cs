using System.Numerics;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.GameRule.Components;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server._NSV.Bluespace.Strategy;

/// <summary>
/// The last stalled-objective reminder's consequence (NSV13 ROUND-006): a hostile blockade fleet
/// arrives in the crew's current sector. Each ship's AI core is an FTL interdictor, so the crew can't
/// simply jump away — they have to fight through it. Grids load straight into the live sector (as the
/// sector ship generator does): the crew's sector is already awake, so there's no need to round-trip
/// through the holding map. They are registered resident at the node, so they sleep and wake with it
/// like any fleet.
/// </summary>
public sealed partial class NsvCampaignBlockadeSystem : EntitySystem
{
    private const string HostileFaction = "NSVHostile";
    private const string PlayerFaction = "NSVPlayer";

    private static readonly ResPath BlockadeShipGridPath = new("/SharedMaps/_NSV/Bluespace/gust_2.yml");

    // Spacing between blockade ships along the arrival line, so their grids don't overlap.
    private const float ShipSpacing = 80f;

    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private NsvFleetRegistrySystem _fleets = default!;
    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NsvCampaignBlockadeEvent>(OnBlockade);
    }

    private void OnBlockade(ref NsvCampaignBlockadeEvent args)
    {
        if (!args.Dispatched)
            args.Dispatched = DispatchBlockade() > 0;
    }

    /// <summary>
    /// Sends the blockade to the crew's sector and returns how many ships arrived (0 when the crew
    /// isn't in an awake bluespace sector). Internal so tests can dispatch without a reminder cycle.
    /// </summary>
    internal int DispatchBlockade()
    {
        if (!TryFindCrewSector(out _, out var sector, out var anchor))
            return 0;

        var size = _cfg.GetCVar(NsvCCVars.CampaignBlockadeSize);
        var distance = _cfg.GetCVar(NsvCCVars.CampaignBlockadeDistance);
        var direction = _random.NextAngle().ToVec();
        var center = _transform.GetWorldPosition(anchor) + direction * distance;
        var across = new Vector2(-direction.Y, direction.X);
        var node = new NsvFleetNodeKey(sector.StarmapId, sector.NodeId);

        var arrived = 0;
        for (var i = 0; i < size; i++)
        {
            var offset = (i - (size - 1) / 2f) * ShipSpacing;
            if (!_mapLoader.TryLoadGrid(sector.MapId, BlockadeShipGridPath, out var grid, offset: center + across * offset))
            {
                Log.Error($"Could not load blockade ship grid '{BlockadeShipGridPath}' at {node}.");
                continue;
            }

            var gridUid = grid.Value.Owner;
            _factions.SetFaction(gridUid, HostileFaction);
            _fleets.RegisterShip(gridUid, BlockadeShipGridPath, node).Faction = HostileFaction;
            MarkInterdictor(gridUid);
            arrived++;
        }

        return arrived;
    }

    /// <summary>
    /// The awake sector the crew is in, and an entity to arrive next to: the flagship if one is
    /// designated and in a sector, otherwise any player-faction shuttle in a sector.
    /// </summary>
    private bool TryFindCrewSector(
        out EntityUid sectorMap,
        out NsvBluespaceSectorInstanceComponent sector,
        out EntityUid anchor)
    {
        var flagships = AllEntityQuery<NsvCampaignFlagshipComponent, TransformComponent>();
        while (flagships.MoveNext(out var flagship, out _, out var xform))
        {
            if (xform.MapUid is { } map && TryGetAwakeSector(map, out sector))
            {
                sectorMap = map;
                anchor = xform.GridUid ?? flagship;
                return true;
            }
        }

        var sectors = AllEntityQuery<NsvBluespaceSectorInstanceComponent>();
        while (sectors.MoveNext(out var map, out var candidate))
        {
            if (!IsAwake(candidate))
                continue;

            foreach (var grid in candidate.ForeignGrids)
            {
                if (TerminatingOrDeleted(grid) ||
                    !_factions.TryGetFaction(grid, out var faction) ||
                    faction != PlayerFaction)
                {
                    continue;
                }

                sectorMap = map;
                sector = candidate;
                anchor = grid;
                return true;
            }
        }

        sectorMap = EntityUid.Invalid;
        sector = default!;
        anchor = EntityUid.Invalid;
        return false;
    }

    private bool TryGetAwakeSector(EntityUid map, out NsvBluespaceSectorInstanceComponent sector)
    {
        return TryComp(map, out sector!) && IsAwake(sector);
    }

    private static bool IsAwake(NsvBluespaceSectorInstanceComponent sector)
    {
        return sector.State is NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.PreparingSleep;
    }

    /// <summary>
    /// Makes the ship's AI core an FTL interdictor, so destroying the core lifts the interdiction. A
    /// ship without a core interdicts from its grid instead.
    /// </summary>
    private void MarkInterdictor(EntityUid gridUid)
    {
        var cores = AllEntityQuery<NsvBluespaceShipAiCoreComponent, TransformComponent>();
        while (cores.MoveNext(out var core, out _, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            EnsureComp<NsvFtlInterdictorComponent>(core);
            return;
        }

        EnsureComp<NsvFtlInterdictorComponent>(gridUid);
    }
}
