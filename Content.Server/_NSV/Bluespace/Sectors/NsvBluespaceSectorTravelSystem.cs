using System.Linq;
using System.Numerics;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.GameRule;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors;

public sealed partial class NsvBluespaceSectorTravelSystem : EntitySystem
{
    private const string PlayerFaction = "NSVPlayer";

    [Dependency] private NsvCampaignRuleSystem _campaign = default!;
    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private NsvFtlInterdictionSystem _interdiction = default!;
    [Dependency] private NsvBluespaceDriveSystem _drives = default!;
    [Dependency] private NsvBluespaceSectorSystem _sectors = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;

    public event Action<EntityUid>? SectorDisplayChanged;
    public event Action<EntityUid>? ShuttleDisplayChanged;

    private readonly Dictionary<EntityUid, EntityUid> _arrivalReservations = new();

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

            if (!_encounters.CanReturn(sourceMapUid, shuttleUid, out reason) ||
                _interdiction.IsInterdicted(shuttleUid, out reason))
            {
                return false;
            }

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

        // Checked before the destination sector is created, so a drive that isn't ready costs nothing.
        var fuelCost = starmap.TryGetNode(destinationNodeId, out var destinationNode) ? destinationNode.FuelCost : 0;
        if (!_drives.CanJump(shuttleUid, fuelCost, out reason))
            return false;

        if (!_sectors.TryGetOrCreateNode(
                starmapId,
                destinationNodeId,
                NsvBluespaceSectorWakeReason.Arrival,
                shuttleUid,
                out var destinationMap,
                out var sectorFailure) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(destinationMap, out var destinationSector) ||
            destinationSector.State != NsvBluespaceSectorState.Ready)
        {
            reason = sectorFailure ?? "The starmap node is unavailable.";
            return false;
        }

        return TryStartArrival(shuttleUid, shuttle, destinationMap, destinationSector, returnCoordinates, factionSnapshot, fuelCost, out reason);
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

        // Template sectors aren't starmap nodes and carry no fuel cost, but still need a charged drive.
        if (!_drives.CanJump(shuttleUid, 0, out reason))
            return false;

        if (!_sectors.TryGetOrCreate(
                templateId,
                seed,
                NsvBluespaceSectorWakeReason.Arrival,
                shuttleUid,
                out var mapUid,
                out var sectorFailure) ||
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
        return TryStartArrival(shuttleUid, shuttle, mapUid, sector, returnCoordinates, factionSnapshot, 0, out reason);
    }

    private bool TryStartArrival(
        EntityUid shuttleUid,
        ShuttleComponent shuttle,
        EntityUid destinationMap,
        NsvBluespaceSectorInstanceComponent destinationSector,
        EntityCoordinates returnCoordinates,
        NsvBluespaceFactionSnapshot factionSnapshot,
        int fuelCost,
        out string? reason)
    {
        if (destinationSector.State != NsvBluespaceSectorState.Ready)
        {
            reason = "The bluespace sector is not ready to receive arrivals.";
            return false;
        }

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

        if (_arrivalReservations.ContainsKey(shuttleUid))
        {
            reason = "This shuttle already has a bluespace arrival reservation.";
            return false;
        }

        _arrivalReservations.Add(shuttleUid, destinationMap);
        destinationSector.ForeignGridFactionSnapshots[shuttleUid] = factionSnapshot;
        destinationSector.ReturnDestinations[shuttleUid] = returnCoordinates;
        destinationSector.PendingArrivals.Add(shuttleUid);
        _shuttle.FTLToCoordinates(shuttleUid, shuttle, new EntityCoordinates(destinationMap, Vector2.Zero), Angle.Zero);
        if (!HasComp<FTLComponent>(shuttleUid))
        {
            CancelArrival(shuttleUid);
            reason = "The bluespace drive did not engage.";
            return false;
        }

        _drives.ConsumeJump(shuttleUid, fuelCost);
        NotifySectorChanged(destinationMap);
        NotifyShuttleChanged(shuttleUid);
        reason = null;
        return true;
    }

    private bool TryReturn(EntityUid shuttleUid, EntityUid sectorMap, EntityCoordinates returnCoordinates, out string? reason)
    {
        if (!_encounters.CanReturn(sectorMap, shuttleUid, out reason) ||
            _interdiction.IsInterdicted(shuttleUid, out reason))
        {
            return false;
        }

        if (!TryComp<ShuttleComponent>(shuttleUid, out var shuttle))
        {
            reason = "Only shuttles can leave bluespace sectors.";
            return false;
        }

        if (!_shuttle.CanFTL(shuttleUid, out reason))
            return false;

        // Returning home costs the same as jumping out of the current node.
        var fuelCost = GetNodeFuelCost(sectorMap);
        if (!_drives.CanJump(shuttleUid, fuelCost, out reason))
            return false;

        _shuttle.FTLToCoordinates(shuttleUid, shuttle, returnCoordinates, Angle.Zero);
        if (!HasComp<FTLComponent>(shuttleUid))
        {
            reason = "The bluespace drive did not engage.";
            return false;
        }

        _drives.ConsumeJump(shuttleUid, fuelCost);
        reason = null;
        return true;
    }

    public override void Update(float frameTime)
    {
        var staleReservations = _arrivalReservations.Keys
            .Where(shuttleUid => !HasComp<FTLComponent>(shuttleUid))
            .ToArray();
        foreach (var shuttleUid in staleReservations)
        {
            CancelArrival(shuttleUid);
        }

        var query = EntityManager.EntityQueryEnumerator<NsvBluespaceSectorInstanceComponent>();
        while (query.MoveNext(out var uid, out var sector))
        {
            var staleArrivals = sector.PendingArrivals
                .Where(shuttleUid => !_arrivalReservations.ContainsKey(shuttleUid) && !HasComp<FTLComponent>(shuttleUid))
                .ToArray();
            foreach (var shuttleUid in staleArrivals)
            {
                sector.PendingArrivals.Remove(shuttleUid);
                sector.ForeignGridFactionSnapshots.Remove(shuttleUid);
                sector.ReturnDestinations.Remove(shuttleUid);
            }

            if (staleArrivals.Length > 0)
                NotifySectorChanged(uid);
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
        if (!TryComp<NsvBluespaceSectorInstanceComponent>(ev.MapUid, out var sector))
        {
            CancelArrival(ev.Entity);
            RemovePendingArrival(ev.Entity);
            _encounters.MarkReturnCompleted(ev.Entity, ev.MapUid);
            NotifyShuttleChanged(ev.Entity);
            return;
        }

        sector.ForeignGrids.Add(ev.Entity);
        CompleteArrival(ev.Entity, ev.MapUid);
        _factions.SetFaction(ev.Entity, PlayerFaction);
        _encounters.DispatchArrival(ev.MapUid, ev.Entity);

        // A jump home is still a jump. Count it first: if it completes the objectives, the outcome
        // vote takes over and the Home victory check then sees a decided campaign and stands down.
        _campaign.NotifyJumpArrived();
        if (IsHomeNode(sector))
            _campaign.NotifyHomeArrival();
        NotifySectorChanged(ev.MapUid);
        NotifyShuttleChanged(ev.Entity);
    }

    /// <summary>
    /// Whether <paramref name="sector"/> is a Home node (the extraction point). Node-less template
    /// sectors (empty StarmapId/NodeId) resolve to false.
    /// </summary>
    /// <summary>
    /// The fuel cost of the starmap node <paramref name="sectorMap"/> instances, or 0 for a node-less
    /// template sector.
    /// </summary>
    public int GetNodeFuelCost(EntityUid sectorMap)
    {
        return TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) &&
               _sectors.TryGetStarmap(sector.StarmapId, out var starmap) &&
               starmap.TryGetNode(sector.NodeId, out var node)
            ? node.FuelCost
            : 0;
    }

    private bool IsHomeNode(NsvBluespaceSectorInstanceComponent sector)
    {
        return _sectors.TryGetStarmap(sector.StarmapId, out var starmap) &&
               starmap.TryGetNode(sector.NodeId, out var node) &&
               node.Type == NsvBluespaceStarmapNodeType.Home;
    }

    private void CancelArrival(EntityUid shuttleUid)
    {
        if (!_arrivalReservations.Remove(shuttleUid, out var sectorMap))
            return;

        if (!TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector))
            return;

        sector.PendingArrivals.Remove(shuttleUid);
        sector.ForeignGridFactionSnapshots.Remove(shuttleUid);
        sector.ReturnDestinations.Remove(shuttleUid);
        NotifySectorChanged(sectorMap);
    }

    private void CompleteArrival(EntityUid shuttleUid, EntityUid destinationMap)
    {
        if (!_arrivalReservations.Remove(shuttleUid, out var sectorMap))
        {
            RemovePendingArrival(shuttleUid);
            return;
        }

        if (!TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector))
            return;

        sector.PendingArrivals.Remove(shuttleUid);
        if (sectorMap == destinationMap)
            return;

        sector.ForeignGridFactionSnapshots.Remove(shuttleUid);
        sector.ReturnDestinations.Remove(shuttleUid);
        NotifySectorChanged(sectorMap);
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
        var query = EntityManager.EntityQueryEnumerator<NsvBluespaceSectorInstanceComponent>();
        while (query.MoveNext(out var uid, out var sector))
        {
            if (!sector.PendingArrivals.Remove(shuttleUid))
                continue;

            NotifySectorChanged(uid);
        }
    }
}
