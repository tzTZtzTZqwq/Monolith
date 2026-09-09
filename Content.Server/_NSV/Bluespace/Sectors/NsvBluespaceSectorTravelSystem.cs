using System.Linq;
using System.Numerics;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NSV.Bluespace.Sectors;

public sealed partial class NsvBluespaceSectorTravelSystem : EntitySystem
{
    private const string PlayerFaction = "NSVPlayer";
    private static readonly TimeSpan CleanupDelay = TimeSpan.FromMinutes(2);

    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private NsvBluespacePatrolContractSystem _patrolContracts = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NsvBluespaceSectorSystem _sectors = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;

    private readonly Dictionary<EntityUid, TimeSpan> _emptySince = new();

    public event Action<EntityUid>? SectorDisplayChanged;
    public event Action<EntityUid>? ShuttleDisplayChanged;

    public override void Initialize()
    {
        SubscribeLocalEvent<FTLStartedEvent>(OnFtlStarted);
        SubscribeLocalEvent<FTLCompletedEvent>(OnFtlCompleted);
    }

    public bool TryTravel(EntityUid shuttleUid, Entity<NsvBluespaceJumpPointComponent?> jumpPoint, int seed, out string? reason)
    {
        var mapUid = Transform(shuttleUid).MapUid;
        if (mapUid is { } sectorMap &&
            TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) &&
            sector.ForeignGrids.Contains(shuttleUid) &&
            sector.ReturnDestinations.TryGetValue(shuttleUid, out var returnCoordinates))
        {
            return TryReturn(shuttleUid, sectorMap, returnCoordinates, out reason);
        }

        return TryEnter(shuttleUid, jumpPoint, seed, out reason);
    }

    public bool TryTravelToNode(
        EntityUid shuttleUid,
        ProtoId<NsvBluespaceStarmapPrototype> starmapId,
        string destinationNodeId,
        out string? reason)
    {
        if (!TryComp<ShuttleComponent>(shuttleUid, out var shuttle))
        {
            reason = "Only shuttles can enter bluespace sectors.";
            return false;
        }

        if (!_shuttle.CanFTL(shuttleUid, out reason))
            return false;

        if (!_sectors.TryGetStarmap(starmapId, out var starmap))
        {
            reason = "The configured starmap is unavailable.";
            return false;
        }

        var shuttleTransform = Transform(shuttleUid);
        if (shuttleTransform.MapUid is not { } sourceMapUid)
        {
            reason = "This shuttle has no valid departure map.";
            return false;
        }

        EntityCoordinates returnCoordinates;
        NsvBluespaceFactionSnapshot factionSnapshot;
        if (TryComp<NsvBluespaceSectorInstanceComponent>(sourceMapUid, out var sourceSector))
        {
            if (!sourceSector.ForeignGrids.Contains(shuttleUid))
            {
                reason = "This shuttle is not registered in the current bluespace sector.";
                return false;
            }

            if (string.IsNullOrEmpty(sourceSector.StarmapId) || sourceSector.StarmapId != starmapId)
            {
                reason = "This shuttle is already in a different bluespace sector.";
                return false;
            }

            if (sourceSector.NodeId == destinationNodeId)
            {
                reason = "This shuttle is already at that starmap node.";
                return false;
            }

            if (!starmap.IsConnected(sourceSector.NodeId, destinationNodeId))
            {
                reason = "The selected starmap node is not connected to the current node.";
                return false;
            }

            if (!_encounters.CanReturn(sourceMapUid, shuttleUid, out reason))
                return false;

            if (!sourceSector.ReturnDestinations.TryGetValue(shuttleUid, out returnCoordinates) ||
                !sourceSector.ForeignGridFactionSnapshots.TryGetValue(shuttleUid, out factionSnapshot))
            {
                reason = "This shuttle has no valid return destination.";
                return false;
            }
        }
        else
        {
            returnCoordinates = new EntityCoordinates(sourceMapUid, shuttleTransform.LocalPosition);
            factionSnapshot = TryComp<NsvBluespaceFactionComponent>(shuttleUid, out var faction)
                ? new NsvBluespaceFactionSnapshot(true, faction.Faction)
                : new NsvBluespaceFactionSnapshot(false, default);
        }

        if (!_sectors.TryGetOrCreateNode(starmapId, destinationNodeId, out var destinationMap, out var sectorFailure) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(destinationMap, out var destinationSector) ||
            destinationSector.State != NsvBluespaceSectorState.Ready)
        {
            reason = sectorFailure ?? "The starmap node is unavailable.";
            return false;
        }

        return TryStartArrival(shuttleUid, shuttle, destinationMap, destinationSector, returnCoordinates, factionSnapshot, out reason);
    }

    public bool TryReturnToDeparture(EntityUid shuttleUid, out string? reason)
    {
        var mapUid = Transform(shuttleUid).MapUid;
        if (mapUid is not { } sectorMap ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) ||
            !sector.ForeignGrids.Contains(shuttleUid) ||
            !sector.ReturnDestinations.TryGetValue(shuttleUid, out var returnCoordinates))
        {
            reason = "This shuttle has no bluespace departure destination.";
            return false;
        }

        return TryReturn(shuttleUid, sectorMap, returnCoordinates, out reason);
    }

    public bool TryEnter(EntityUid shuttleUid, Entity<NsvBluespaceJumpPointComponent?> jumpPoint, int seed, out string? reason)
    {
        if (!Resolve(jumpPoint, ref jumpPoint.Comp, false) || string.IsNullOrEmpty(jumpPoint.Comp.TemplateId))
        {
            reason = "Invalid bluespace jump point.";
            return false;
        }

        return TryEnter(shuttleUid, jumpPoint.Comp.TemplateId, seed, out reason);
    }

    public bool TryEnter(
        EntityUid shuttleUid,
        ProtoId<NsvBluespaceSectorTemplatePrototype> templateId,
        int seed,
        out string? reason)
    {
        if (!TryComp<ShuttleComponent>(shuttleUid, out var shuttle))
        {
            reason = "Only shuttles can enter bluespace sectors.";
            return false;
        }

        if (!_shuttle.CanFTL(shuttleUid, out reason))
            return false;

        if (!_sectors.TryGetOrCreate(templateId, seed, out var mapUid, out var sectorFailure) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector) ||
            sector.State != NsvBluespaceSectorState.Ready)
        {
            reason = sectorFailure ?? "The bluespace sector is unavailable.";
            return false;
        }

        var shuttleTransform = Transform(shuttleUid);
        if (shuttleTransform.MapUid is not { } sourceMapUid)
        {
            reason = "This shuttle has no valid return destination.";
            return false;
        }

        var returnCoordinates = new EntityCoordinates(sourceMapUid, shuttleTransform.LocalPosition);
        var factionSnapshot = TryComp<NsvBluespaceFactionComponent>(shuttleUid, out var faction)
            ? new NsvBluespaceFactionSnapshot(true, faction.Faction)
            : new NsvBluespaceFactionSnapshot(false, default);
        return TryStartArrival(shuttleUid, shuttle, mapUid, sector, returnCoordinates, factionSnapshot, out reason);
    }

    private bool TryStartArrival(
        EntityUid shuttleUid,
        ShuttleComponent shuttle,
        EntityUid destinationMap,
        NsvBluespaceSectorInstanceComponent destinationSector,
        EntityCoordinates returnCoordinates,
        NsvBluespaceFactionSnapshot factionSnapshot,
        out string? reason)
    {
        if (destinationSector.PendingArrivals.Contains(shuttleUid))
        {
            reason = "This shuttle is already entering the bluespace sector.";
            return false;
        }

        if (destinationSector.ForeignGrids.Contains(shuttleUid))
        {
            reason = "This shuttle is already in the bluespace sector.";
            return false;
        }

        destinationSector.ForeignGridFactionSnapshots[shuttleUid] = factionSnapshot;
        destinationSector.ReturnDestinations[shuttleUid] = returnCoordinates;
        destinationSector.PendingArrivals.Add(shuttleUid);
        _emptySince.Remove(destinationMap);
        _shuttle.FTLToCoordinates(shuttleUid, shuttle, new EntityCoordinates(destinationMap, Vector2.Zero), Angle.Zero);
        if (!HasComp<FTLComponent>(shuttleUid))
        {
            destinationSector.PendingArrivals.Remove(shuttleUid);
            destinationSector.ForeignGridFactionSnapshots.Remove(shuttleUid);
            destinationSector.ReturnDestinations.Remove(shuttleUid);
            NotifySectorChanged(destinationMap);
            reason = "The bluespace drive did not engage.";
            return false;
        }

        NotifySectorChanged(destinationMap);
        NotifyShuttleChanged(shuttleUid);
        reason = null;
        return true;
    }

    private bool TryReturn(EntityUid shuttleUid, EntityUid sectorMap, EntityCoordinates returnCoordinates, out string? reason)
    {
        if (!_encounters.CanReturn(sectorMap, shuttleUid, out reason))
            return false;

        if (!TryComp<ShuttleComponent>(shuttleUid, out var shuttle))
        {
            reason = "Only shuttles can leave bluespace sectors.";
            return false;
        }

        if (!_shuttle.CanFTL(shuttleUid, out reason))
            return false;

        _shuttle.FTLToCoordinates(shuttleUid, shuttle, returnCoordinates, Angle.Zero);
        if (!HasComp<FTLComponent>(shuttleUid))
        {
            reason = "The bluespace drive did not engage.";
            return false;
        }

        reason = null;
        return true;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<NsvBluespaceSectorInstanceComponent>();
        while (query.MoveNext(out var uid, out var sector))
        {
            var staleArrivals = sector.PendingArrivals.Where(shuttleUid => !HasComp<FTLComponent>(shuttleUid)).ToArray();
            foreach (var shuttleUid in staleArrivals)
            {
                sector.PendingArrivals.Remove(shuttleUid);
                sector.ForeignGridFactionSnapshots.Remove(shuttleUid);
                sector.ReturnDestinations.Remove(shuttleUid);
            }

            if (staleArrivals.Length > 0)
            {
                _emptySince.Remove(uid);
                NotifySectorChanged(uid);
            }

            if (sector.State != NsvBluespaceSectorState.Ready ||
                sector.ForeignGrids.Count != 0 ||
                sector.PendingArrivals.Count != 0)
            {
                _emptySince.Remove(uid);
                continue;
            }

            if (!_emptySince.TryGetValue(uid, out var emptySince))
            {
                _emptySince[uid] = _timing.CurTime;
                continue;
            }

            if (_timing.CurTime - emptySince >= CleanupDelay &&
                _sectors.TryDispose((uid, sector)))
            {
                _emptySince.Remove(uid);
            }
        }
    }

    private void OnFtlStarted(ref FTLStartedEvent ev)
    {
        if (ev.FromMapUid is not { } mapUid ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector) ||
            !sector.ForeignGrids.Contains(ev.Entity))
        {
            return;
        }

        _encounters.TryMarkReturnStarted(mapUid, ev.Entity);
        sector.ForeignGrids.Remove(ev.Entity);
        sector.ReturnDestinations.Remove(ev.Entity);
        RestoreForeignGridFaction(ev.Entity, mapUid, sector);
        NotifySectorChanged(mapUid);
        NotifyShuttleChanged(ev.Entity);
    }

    private void OnFtlCompleted(ref FTLCompletedEvent ev)
    {
        RemovePendingArrival(ev.Entity);

        if (!TryComp<NsvBluespaceSectorInstanceComponent>(ev.MapUid, out var sector))
        {
            _encounters.MarkReturnCompleted(ev.Entity, ev.MapUid);
            NotifyShuttleChanged(ev.Entity);
            return;
        }

        _factions.SetFaction(ev.Entity, PlayerFaction);
        sector.ForeignGrids.Add(ev.Entity);
        _patrolContracts.OnSectorArrival(ev.MapUid, ev.Entity);
        _emptySince.Remove(ev.MapUid);
        NotifySectorChanged(ev.MapUid);
        NotifyShuttleChanged(ev.Entity);
    }

    private void RestoreForeignGridFaction(
        EntityUid shuttleUid,
        EntityUid sectorMap,
        NsvBluespaceSectorInstanceComponent sector)
    {
        if (!sector.ForeignGridFactionSnapshots.TryGetValue(shuttleUid, out var snapshot))
            return;

        sector.ForeignGridFactionSnapshots.Remove(shuttleUid);
        _factions.RestoreFaction(shuttleUid, sectorMap, snapshot);
    }

    private void NotifySectorChanged(EntityUid sectorMap)
    {
        SectorDisplayChanged?.Invoke(sectorMap);
    }

    private void NotifyShuttleChanged(EntityUid shuttleUid)
    {
        ShuttleDisplayChanged?.Invoke(shuttleUid);
    }

    private void RemovePendingArrival(EntityUid shuttleUid)
    {
        var query = EntityQueryEnumerator<NsvBluespaceSectorInstanceComponent>();
        while (query.MoveNext(out var uid, out var sector))
        {
            if (!sector.PendingArrivals.Remove(shuttleUid))
                continue;

            _emptySince.Remove(uid);
            NotifySectorChanged(uid);
        }
    }
}
