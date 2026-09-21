using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._NSV.Bluespace.Strategy;

/// <summary>
/// Deterministic order in which floors are removed as a ship loses integrity in the data
/// state. Given the full-complement floor set and a target count, "which floors survive"
/// is fully determined by this order alone — nothing about prior damage is stored. Floors
/// are removed front-to-back and repaired back-to-front, so the ordering must be stable.
///
/// This asymmetry (comparator-ordered) applies only to abstract data-state damage; Live
/// combat carves arbitrary holes and is not bound by this order (see doc §8).
/// </summary>
public interface INsvFloorComparator
{
    /// <summary>
    /// Returns the full-complement coordinates sorted so that the floors removed first
    /// come first. Must be a pure function of <paramref name="floors"/>.
    /// </summary>
    IReadOnlyList<Vector2i> OrderForRemoval(IReadOnlyCollection<Vector2i> floors);
}

/// <summary>
/// Placeholder outer-ring-inward order: farthest floors from the grid-local centroid are
/// removed first, with a deterministic tiebreak so equal-distance floors never reorder.
/// The real rule (proper ring peeling) is deferred to P6; this keeps the interface and
/// its consumers exercised in the meantime.
/// </summary>
public sealed class NsvOuterRingFloorComparator : INsvFloorComparator
{
    public IReadOnlyList<Vector2i> OrderForRemoval(IReadOnlyCollection<Vector2i> floors)
    {
        if (floors.Count == 0)
            return System.Array.Empty<Vector2i>();

        var centroid = Vector2.Zero;
        foreach (var floor in floors)
            centroid += floor;
        centroid /= floors.Count;

        var ordered = new List<Vector2i>(floors);
        ordered.Sort((a, b) =>
        {
            var distanceA = Vector2.DistanceSquared(a, centroid);
            var distanceB = Vector2.DistanceSquared(b, centroid);
            var byDistance = distanceB.CompareTo(distanceA);
            if (byDistance != 0)
                return byDistance;

            // Stable tiebreak: equal-distance floors get a fixed order.
            var byX = a.X.CompareTo(b.X);
            return byX != 0 ? byX : a.Y.CompareTo(b.Y);
        });
        return ordered;
    }
}
