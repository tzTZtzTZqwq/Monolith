using System.Linq;
using System.Numerics;
using Content.Server._Mono.FireControl;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server.Shuttles.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Server._NSV.Bluespace.Strategy;

/// <summary>
/// Sole authority for ship identity and position: allocates ship ids, owns the ship
/// records, binds each to its root grid via <see cref="NsvSerializableComponent"/>, and
/// is the only writer that moves a ship between its Live grid and the data state. Ids
/// must never be constructed elsewhere. Fleet grouping and scheduling belong to the
/// strategy system and are out of scope here.
/// </summary>
public sealed class NsvFleetRegistrySystem : EntitySystem
{
    [Dependency] private MapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private DockingSystem _docking = default!;
    [Dependency] private IMapManager _mapManager = default!;

    // Instantiate placement search: expanding ring of candidate spots around the caller's
    // anchor so waking fleets fan out instead of stacking on (0,0) and on the station.
    private const float FreeSpotClearance = 2f;
    private const int FreeSpotMaxRings = 32;
    private const int FreeSpotDirections = 8;

    private readonly NsvFleetIdAllocator _allocator = new();
    private readonly Dictionary<string, NsvFleetShip> _ships = new();
    private readonly INsvFloorComparator _floorComparator = new NsvOuterRingFloorComparator();

    // Node residency: node -> shipIds whose data record currently sits at that node. A
    // derived cache kept in step with NsvFleetShip.DataNode; rebuildable from _ships.
    private readonly Dictionary<NsvFleetNodeKey, HashSet<string>> _residency = new();

    private ulong _nextBindingGeneration = 1;
    private MapId _holdingMap = MapId.Nullspace;

    public override void Shutdown()
    {
        base.Shutdown();
        _ships.Clear();
        _residency.Clear();
        _holdingMap = MapId.Nullspace;
    }

    /// <summary>
    /// Binds a freshly spawned live grid as a ship: allocates a ship id, stamps the
    /// grid with <see cref="NsvSerializableComponent"/>, photographs the pristine floor
    /// set as the 100% integrity baseline, records the ship, and (if a node is given)
    /// registers it as resident at that node.
    /// </summary>
    public NsvFleetShip RegisterShip(EntityUid gridUid, ResPath gridPath, NsvFleetNodeKey? node = null)
    {
        var shipId = _allocator.AllocateShipId();
        var generation = _nextBindingGeneration++;

        var fullComplement = SnapshotFloors(gridUid);
        var ship = new NsvFleetShip
        {
            Id = shipId,
            GridPath = gridPath,
            RootGrid = gridUid,
            State = NsvFleetShipState.Live,
            BindingGeneration = generation,
            FullComplementFloors = fullComplement,
        };
        _ships.Add(shipId, ship);
        SetResidency(ship, node);

        var comp = EnsureComp<NsvSerializableComponent>(gridUid);
        comp.ShipId = shipId;
        comp.BindingGeneration = generation;
        comp.TargetFloorCount = fullComplement.Count;

        return ship;
    }

    /// <summary>
    /// Ships whose data record currently resides at <paramref name="node"/> (any state).
    /// The wake regeneration gate reads this to instantiate only the ships that belong at
    /// the waking node — ships that travelled away carry a different <see cref="NsvFleetShip.DataNode"/>.
    /// </summary>
    public IReadOnlyCollection<string> GetResidentShips(NsvFleetNodeKey node)
    {
        return _residency.TryGetValue(node, out var ships)
            ? ships
            : (IReadOnlyCollection<string>) System.Array.Empty<string>();
    }

    /// <summary>
    /// Strategic-map move of a ship's data record from its current node to <paramref name="destination"/>,
    /// the sole API for changing residency (doc §9: position writes go only through the registry).
    /// It moves the record, not the grid — a data-state ship "travels" by rebinding its node
    /// while its grid stays parked on the holding map. Rejected for a Live ship, whose position
    /// is the grid itself and must change via serialize/instantiate.
    /// </summary>
    public bool TryMoveShipToNode(string shipId, NsvFleetNodeKey destination, out string? failure)
    {
        failure = null;
        if (!_ships.TryGetValue(shipId, out var ship))
        {
            failure = $"Ship '{shipId}' is unknown.";
            return false;
        }

        if (ship.State == NsvFleetShipState.Live)
        {
            failure = $"Ship '{shipId}' is Live; move its grid via serialize/instantiate, not the strategic map.";
            return false;
        }

        SetResidency(ship, destination);
        return true;
    }

    /// <summary>
    /// Moves a ship's residency to <paramref name="node"/> (or clears it when null), keeping
    /// <see cref="NsvFleetShip.DataNode"/> and the <c>_residency</c> index in step.
    /// </summary>
    private void SetResidency(NsvFleetShip ship, NsvFleetNodeKey? node)
    {
        if (ship.DataNode is { } previous && _residency.TryGetValue(previous, out var previousSet))
        {
            previousSet.Remove(ship.Id);
            if (previousSet.Count == 0)
                _residency.Remove(previous);
        }

        ship.DataNode = node;
        if (node is { } target)
        {
            if (!_residency.TryGetValue(target, out var set))
                _residency[target] = set = new HashSet<string>();
            set.Add(ship.Id);
        }
    }

    /// <summary>
    /// Batch serialize on sector sleep: parks every marked ship resident at <paramref name="node"/>
    /// onto the holding map as one group, running the P5 exit-reference gate. Reparenting
    /// targets the always-paused holding map, so this never triggers a wake. Ships that fail
    /// the gate stay Live and are reported in <paramref name="failures"/>; the rest still park.
    /// </summary>
    public void SerializeSectorFleets(NsvFleetNodeKey node, out IReadOnlyList<string> failures)
    {
        var problems = new List<string>();
        var liveShips = new List<string>();
        foreach (var shipId in GetResidentShips(node))
        {
            if (_ships.TryGetValue(shipId, out var ship) && ship.State == NsvFleetShipState.Live)
                liveShips.Add(shipId);
        }

        // One ship failing the gate must not strand the others; serialize each independently.
        foreach (var shipId in liveShips)
        {
            if (!TrySerializeShip(shipId, out var failure))
                problems.Add(failure ?? $"Ship '{shipId}' failed to serialize.");
        }

        failures = problems;
    }

    /// <summary>
    /// Batch instantiate on sector wake — the wake regeneration gate. Materializes every
    /// Available ship resident at <paramref name="node"/> back onto the sector map at
    /// <paramref name="destination"/>. Ships that travelled away in the data layer carry a
    /// different <see cref="NsvFleetShip.DataNode"/> and are not resident here, so they are
    /// not regenerated. Call this while the sector map is still paused, before unpausing.
    /// </summary>
    public void InstantiateNodeFleets(NsvFleetNodeKey node, EntityCoordinates destination, out IReadOnlyList<string> failures)
    {
        var problems = new List<string>();
        var available = new List<string>();
        foreach (var shipId in GetResidentShips(node))
        {
            if (_ships.TryGetValue(shipId, out var ship) && ship.State == NsvFleetShipState.Available)
                available.Add(shipId);
        }

        foreach (var shipId in available)
        {
            if (!TryInstantiateShip(shipId, destination, out var failure))
                problems.Add(failure ?? $"Ship '{shipId}' failed to instantiate.");
        }

        failures = problems;
    }

    /// <summary>
    /// Serializes a single Live ship. Convenience wrapper over <see cref="TrySerializeFleet"/>
    /// with a one-ship set, so a lone ship still runs the exit-reference gate.
    /// </summary>
    public bool TrySerializeShip(string shipId, out string? failure)
    {
        return TrySerializeFleet(new[] { shipId }, out failure);
    }

    /// <summary>
    /// Serializes a fleet (one or more ships) into the data state as one atomic group.
    /// Before moving anything it runs the exit-reference gate on every ship: forces
    /// undocking, and refuses if a ship is still referenced by an active encounter. Grids
    /// inside the same batch reference each other legitimately (a formation), so intra-batch
    /// references never block. If any ship fails the gate, nothing is serialized (they leave
    /// together). On success each ship's root grid reparents onto the paused holding map and
    /// its record flips to Available. Grids are never copied or deleted; UID/contents are
    /// preserved.
    /// </summary>
    public bool TrySerializeFleet(IReadOnlyCollection<string> shipIds, out string? failure)
    {
        failure = null;

        var ships = new List<NsvFleetShip>(shipIds.Count);
        foreach (var shipId in shipIds)
        {
            if (!_ships.TryGetValue(shipId, out var ship))
            {
                failure = $"Ship '{shipId}' is unknown.";
                return false;
            }

            if (ship.State != NsvFleetShipState.Live)
            {
                failure = $"Ship '{shipId}' is {ship.State}, not Live.";
                return false;
            }

            if (!GridIsBound(ship, out failure))
                return false;

            ships.Add(ship);
        }

        // Gate every ship before moving any: the batch leaves together or not at all.
        foreach (var ship in ships)
        {
            if (!TryClearReferences(ship, out failure))
                return false;
        }

        var holdingMap = _map.GetMap(GetOrCreateHoldingMap());
        foreach (var ship in ships)
        {
            _transform.SetCoordinates(ship.RootGrid, new EntityCoordinates(holdingMap, Vector2.Zero));
            ship.State = NsvFleetShipState.Available;
            // Photograph the real floor count into the data record so live battle damage
            // carries into the data state; abstract combat lowers this while parked.
            ship.DataTargetFloorCount = CountFloors(ship.RootGrid);
        }

        return true;
    }

    /// <summary>
    /// The exit-reference gate: a ship may only leave once nothing outside itself holds a
    /// live reference to it. Forces undocking (a repair, not a failure), then blocks if the
    /// grid is an active encounter's participant or objective target. Encounter references
    /// are always external — the encounter points at the ship — so they always block.
    /// Unhandled references are recorded in <see cref="NsvFleetShip.RepresentationFailure"/>
    /// and the ship stays put.
    /// </summary>
    private bool TryClearReferences(NsvFleetShip ship, out string? failure)
    {
        failure = null;
        var gridUid = ship.RootGrid;

        // Undocking is a safe, executable fix; run it up front so a merely-docked ship departs.
        _docking.UndockDocks(gridUid);

        var query = EntityQueryEnumerator<NsvBluespaceEncounterComponent>();
        while (query.MoveNext(out _, out var encounter))
        {
            if (encounter.State is NsvBluespaceEncounterState.Failed or NsvBluespaceEncounterState.Disposed)
                continue;

            // Participants are grid UIDs.
            if (encounter.Participants.Contains(gridUid))
            {
                ship.RepresentationFailure = $"referenced as encounter participant ({encounter.DefinitionId})";
                failure = $"Ship '{ship.Id}' is {ship.RepresentationFailure}.";
                return false;
            }

            // ObjectiveTarget is an entity on a grid; resolve to its root grid.
            if (encounter.ObjectiveTarget != EntityUid.Invalid &&
                Transform(encounter.ObjectiveTarget).GridUid == gridUid)
            {
                ship.RepresentationFailure = $"referenced as encounter objective ({encounter.DefinitionId})";
                failure = $"Ship '{ship.Id}' is {ship.RepresentationFailure}.";
                return false;
            }
        }

        ship.RepresentationFailure = null;
        return true;
    }

    /// <summary>
    /// Instantiates an Available ship back into a Live grid: reparents its root grid
    /// from the holding map onto <paramref name="destination"/> (which unpauses it), applies
    /// the data-state integrity by deleting floors down to <see cref="NsvFleetShip.DataTargetFloorCount"/>
    /// (comparator order, idempotent — replaying converges), and flips the record to Live.
    /// The caller supplies <paramref name="destination"/> as a preferred anchor; the ship lands
    /// on the nearest clear spot found by an outward ring search from there, so a batch of
    /// waking ships fans out instead of stacking. Residency/node selection is out of scope here.
    /// </summary>
    public bool TryInstantiateShip(string shipId, EntityCoordinates destination, out string? failure)
    {
        failure = null;
        if (!_ships.TryGetValue(shipId, out var ship))
        {
            failure = $"Ship '{shipId}' is unknown.";
            return false;
        }

        if (ship.State != NsvFleetShipState.Available)
        {
            failure = $"Ship '{shipId}' is {ship.State}, not Available.";
            return false;
        }

        if (!GridIsBound(ship, out failure))
            return false;

        _transform.SetCoordinates(ship.RootGrid, FindFreeSpot(ship.RootGrid, destination));
        ship.State = NsvFleetShipState.Live;

        // Apply data-state damage taken while parked. Target-based and idempotent: a retry
        // converges to the same grid rather than damaging further.
        if (ship.DataTargetFloorCount >= 0)
            TrySetFloorCount(shipId, ship.DataTargetFloorCount, out _);

        return true;
    }

    /// <summary>
    /// Treats <paramref name="preferred"/> as an anchor and searches outward on a deterministic
    /// ring pattern for a position where the grid's footprint clears every other grid on the
    /// destination map, so instantiated ships fan out instead of stacking on the anchor (and on
    /// the station sitting there). Deterministic — no RNG — so replaying a wake converges to the
    /// same layout. Falls back to the anchor when the grid is empty or nothing clear is found.
    /// </summary>
    private EntityCoordinates FindFreeSpot(EntityUid gridUid, EntityCoordinates preferred)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return preferred;

        var localAabb = grid.LocalAABB;
        // An empty grid has a degenerate AABB the spatial query can't detect; leave it as-is.
        if (localAabb.Width <= 0f || localAabb.Height <= 0f)
            return preferred;

        var mapCoords = _transform.ToMapCoordinates(preferred);
        if (mapCoords.MapId == MapId.Nullspace)
            return preferred;

        var anchor = mapCoords.Position;
        if (IsSpotClear(gridUid, mapCoords.MapId, localAabb.Translated(anchor)))
            return preferred;

        var step = MathF.Max(localAabb.Width, localAabb.Height) + FreeSpotClearance * 2f;
        for (var ring = 1; ring <= FreeSpotMaxRings; ring++)
        {
            for (var dir = 0; dir < FreeSpotDirections; dir++)
            {
                var angle = dir * (MathF.PI * 2f / FreeSpotDirections);
                var candidate = anchor + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (ring * step);
                if (IsSpotClear(gridUid, mapCoords.MapId, localAabb.Translated(candidate)))
                    return new EntityCoordinates(_map.GetMap(mapCoords.MapId), candidate);
            }
        }

        return preferred;
    }

    /// <summary>
    /// Whether <paramref name="worldBox"/> (a candidate grid footprint) is free of every grid on
    /// the map except the ship's own. The map entity is excluded so an otherwise-empty destination
    /// reads as clear.
    /// </summary>
    private bool IsSpotClear(EntityUid gridUid, MapId mapId, Box2 worldBox)
    {
        var grids = new List<Entity<MapGridComponent>>();
        _mapManager.FindGridsIntersecting(mapId, worldBox.Enlarged(FreeSpotClearance), ref grids, includeMap: false);
        foreach (var other in grids)
        {
            if (other.Owner != gridUid)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Abstract data-state combat: lowers a ship's stored integrity by <paramref name="floorsLost"/>
    /// floors, clamped to [0, current]. Only edits the frozen record's
    /// <see cref="NsvFleetShip.DataTargetFloorCount"/>; the parked grid is untouched until it
    /// instantiates. Requires the ship to be in the data state (Available) and photographed.
    /// </summary>
    public bool TryApplyAbstractDamage(string shipId, int floorsLost, out string? failure)
    {
        failure = null;
        if (!_ships.TryGetValue(shipId, out var ship))
        {
            failure = $"Ship '{shipId}' is unknown.";
            return false;
        }

        if (ship.State != NsvFleetShipState.Available)
        {
            failure = $"Ship '{shipId}' is {ship.State}, not in the data state.";
            return false;
        }

        if (ship.DataTargetFloorCount < 0)
        {
            failure = $"Ship '{shipId}' has no photographed integrity.";
            return false;
        }

        ship.DataTargetFloorCount = Math.Clamp(ship.DataTargetFloorCount - Math.Max(0, floorsLost), 0, ship.DataTargetFloorCount);
        return true;
    }

    public bool TryGetShip(string shipId, out NsvFleetShip ship)
    {
        return _ships.TryGetValue(shipId, out ship!);
    }

    public IReadOnlyCollection<NsvFleetShip> Ships => _ships.Values;

    /// <summary>
    /// Completeness = current floor count / full-complement count, in [0, 1]. Derived
    /// straight from the grid, never stored; returns 0 if the grid is gone or had no
    /// floors at spawn.
    /// </summary>
    public float GetCompleteness(string shipId)
    {
        if (!_ships.TryGetValue(shipId, out var ship) ||
            ship.FullComplementFloors.Count == 0 ||
            !GridIsBound(ship, out _))
        {
            return 0f;
        }

        return Math.Clamp((float) CountFloors(ship.RootGrid) / ship.FullComplementFloors.Count, 0f, 1f);
    }

    /// <summary>
    /// Idempotent, target-based floor edit: brings the grid to exactly
    /// <paramref name="targetFloorCount"/> floors. Below the current count it deletes the
    /// outermost floors (comparator order); above it repairs the innermost missing floors
    /// from the full-complement reference. Replaying the same target converges to the same
    /// grid — it does not stack. Only touches floor tiles, not entities on them.
    /// </summary>
    public bool TrySetFloorCount(string shipId, int targetFloorCount, out string? failure)
    {
        failure = null;
        if (!_ships.TryGetValue(shipId, out var ship))
        {
            failure = $"Ship '{shipId}' is unknown.";
            return false;
        }

        if (!GridIsBound(ship, out failure) ||
            !TryComp<MapGridComponent>(ship.RootGrid, out var grid))
        {
            failure ??= $"Ship '{shipId}' has no grid.";
            return false;
        }

        var target = Math.Clamp(targetFloorCount, 0, ship.FullComplementFloors.Count);
        // Removal order is over the full complement so the result is a pure function of
        // the target: the first `target` entries survive, the rest are empty.
        var removalOrder = _floorComparator.OrderForRemoval(ship.FullComplementFloors.Keys.ToArray());
        var survivorCount = removalOrder.Count - target;
        for (var i = 0; i < removalOrder.Count; i++)
        {
            var indices = removalOrder[i];
            // Entries are ordered outermost-first; the last `target` are the innermost survivors.
            var shouldExist = i >= survivorCount;
            var tile = shouldExist ? ship.FullComplementFloors[indices] : Tile.Empty;
            _map.SetTile((ship.RootGrid, grid), indices, tile);
        }

        return true;
    }

    /// <summary>
    /// Base combat power = number of turrets (<see cref="FireControllableComponent"/>) on
    /// the grid, scaled by current completeness. Derived, never stored; per-weapon weights
    /// and ammo are deferred (doc §5).
    /// </summary>
    public float GetCombatPower(string shipId)
    {
        if (!_ships.TryGetValue(shipId, out var ship) || !GridIsBound(ship, out _))
            return 0f;

        var turrets = 0;
        var query = EntityQueryEnumerator<FireControllableComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid == ship.RootGrid)
                turrets++;
        }

        return turrets * GetCompleteness(shipId);
    }

    /// <summary>
    /// Whether a node's ships may resolve abstract (data-state) combat right now. Returns
    /// false if any ship resident at the node is still Live (its authority is the grid, not
    /// the data number — doc invariant 14), or if an active encounter runs at that node. The
    /// caller must additionally confirm the sector is not mid-transition (lifecycle state is
    /// the lifecycle layer's authority, not the registry's).
    /// </summary>
    public bool CanResolveAbstractCombat(NsvFleetNodeKey node)
    {
        foreach (var shipId in GetResidentShips(node))
        {
            if (_ships.TryGetValue(shipId, out var ship) && ship.State == NsvFleetShipState.Live)
                return false;
        }

        var query = EntityQueryEnumerator<NsvBluespaceEncounterComponent>();
        while (query.MoveNext(out _, out var encounter))
        {
            if (encounter.State is NsvBluespaceEncounterState.Failed or NsvBluespaceEncounterState.Disposed)
                continue;

            if (TryComp<Sectors.NsvBluespaceSectorInstanceComponent>(encounter.SectorMap, out var sector) &&
                new NsvFleetNodeKey(sector.StarmapId, sector.NodeId) == node)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The global holding map that parks serialized grids. Created once per server run
    /// and kept paused for its whole lifetime; grids on it do not simulate.
    /// </summary>
    public MapId GetOrCreateHoldingMap()
    {
        if (_holdingMap != MapId.Nullspace && _map.MapExists(_holdingMap))
            return _holdingMap;

        _map.CreateMap(out _holdingMap);
        _map.SetPaused(_holdingMap, true);
        return _holdingMap;
    }

    /// <summary>
    /// Confirms the record still owns a live grid whose binding generation matches, so a
    /// stale record cannot move an unrelated or rebound entity. Marks the ship Missing on
    /// failure; it is not auto-respawned.
    /// </summary>
    private bool GridIsBound(NsvFleetShip ship, out string? failure)
    {
        failure = null;
        if (TerminatingOrDeleted(ship.RootGrid) ||
            !TryComp<NsvSerializableComponent>(ship.RootGrid, out var comp) ||
            comp.ShipId != ship.Id ||
            comp.BindingGeneration != ship.BindingGeneration)
        {
            ship.State = NsvFleetShipState.Missing;
            failure = $"Ship '{ship.Id}' has no bound grid.";
            return false;
        }

        return true;
    }

    private Dictionary<Vector2i, Tile> SnapshotFloors(EntityUid gridUid)
    {
        var floors = new Dictionary<Vector2i, Tile>();
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return floors;

        var enumerator = _map.GetAllTilesEnumerator(gridUid, grid);
        while (enumerator.MoveNext(out var tile))
            floors[tile.Value.GridIndices] = tile.Value.Tile;

        return floors;
    }

    private int CountFloors(EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return 0;

        var count = 0;
        var enumerator = _map.GetAllTilesEnumerator(gridUid, grid);
        while (enumerator.MoveNext(out _))
            count++;

        return count;
    }
}
