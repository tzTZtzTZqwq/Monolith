using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Server._NSV.Bluespace.Strategy;

public enum NsvFleetShipState
{
    Available, // pure data, may be materialized
    Live,
    Destroyed,
    Missing,   // should have an entity but none found; not auto-respawned
}

/// <summary>
/// A strategic-map node a ship is resident at. Local to the strategy layer so the registry
/// need not depend on the sector lifecycle's own node-key type. StarmapId + NodeId together
/// identify one logical node slot.
/// </summary>
public readonly record struct NsvFleetNodeKey(string StarmapId, string NodeId);

/// <summary>
/// A single logical ship, one-to-one with a root grid. Under S2 the grid is never
/// copied: <see cref="RootGrid"/> references the live entity, which rests on the
/// holding map while in data state and on a sector map while Live.
/// </summary>
public sealed class NsvFleetShip
{
    public string Id = string.Empty;
    public ResPath GridPath;
    public EntityUid RootGrid = EntityUid.Invalid;
    public NsvFleetShipState State = NsvFleetShipState.Available;
    public ulong BindingGeneration;
    public string? RepresentationFailure;

    /// <summary>
    /// The strategic node this ship's data record currently resides at. A Live ship on a
    /// sector node and a parked Available ship both carry it; it is the sole authority the
    /// wake regeneration gate consults to decide "does this ship belong at the waking node".
    /// Null only before the ship is bound to a node.
    /// </summary>
    public NsvFleetNodeKey? DataNode;

    /// <summary>
    /// Data-state integrity, as a floor count. While the grid is frozen on the holding map
    /// its live floor count cannot change, so abstract combat edits this number instead.
    /// Photographed from the real floor count at serialize time (so live battle damage is
    /// captured), lowered by abstract combat, and applied to the grid at instantiate time by
    /// deleting floors down to it. -1 means "not yet photographed"; instantiate then leaves
    /// the grid as-is.
    /// </summary>
    public int DataTargetFloorCount = -1;

    /// <summary>
    /// The pristine floor set photographed at spawn (100% integrity). It is the sole
    /// repair reference: abstract damage/repair delete or restore from this set by
    /// comparator order, never storing which floors were removed. Its size is the
    /// full-complement floor count (= <see cref="NsvSerializableComponent.TargetFloorCount"/>).
    /// </summary>
    public IReadOnlyDictionary<Vector2i, Tile> FullComplementFloors = new Dictionary<Vector2i, Tile>();
}
