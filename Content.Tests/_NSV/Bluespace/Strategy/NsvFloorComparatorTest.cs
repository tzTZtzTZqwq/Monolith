using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._NSV.Bluespace.Strategy;
using NUnit.Framework;
using Robust.Shared.Maths;
using Robust.UnitTesting;

namespace Content.Tests._NSV.Bluespace.Strategy;

[TestFixture]
public sealed class NsvFloorComparatorTest : RobustUnitTest
{
    [Test]
    public void OrdersOutermostFirst()
    {
        var comparator = new NsvOuterRingFloorComparator();
        // A plus/cross around origin: centre plus four arms at distance 2.
        var floors = new[]
        {
            new Vector2i(0, 0),
            new Vector2i(2, 0),
            new Vector2i(-2, 0),
            new Vector2i(0, 2),
            new Vector2i(0, -2),
        };

        var ordered = comparator.OrderForRemoval(floors);

        // Centre is closest to the centroid, so it must be removed last.
        Assert.That(ordered[^1], Is.EqualTo(new Vector2i(0, 0)));
        Assert.That(ordered, Has.Count.EqualTo(5));
    }

    [Test]
    public void IsDeterministic()
    {
        var comparator = new NsvOuterRingFloorComparator();
        var floors = new[]
        {
            new Vector2i(1, 1),
            new Vector2i(-3, 2),
            new Vector2i(4, -1),
            new Vector2i(0, 0),
            new Vector2i(2, 2),
        };

        var first = comparator.OrderForRemoval(floors);
        var second = comparator.OrderForRemoval(floors.Reverse().ToArray());

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void HandlesEmpty()
    {
        var comparator = new NsvOuterRingFloorComparator();
        Assert.That(comparator.OrderForRemoval(new List<Vector2i>()), Is.Empty);
    }
}
