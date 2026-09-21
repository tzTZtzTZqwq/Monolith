using System.Collections.Generic;
using Content.Server._NSV.Bluespace.Strategy;
using NUnit.Framework;
using Robust.UnitTesting;

namespace Content.Tests._NSV.Bluespace.Strategy;

[TestFixture]
public sealed class NsvFleetIdAllocatorTest : RobustUnitTest
{
    [Test]
    public void AllocatesMonotonicShipIds()
    {
        var allocator = new NsvFleetIdAllocator();

        Assert.That(allocator.AllocateShipId(), Is.EqualTo("ship-1"));
        Assert.That(allocator.AllocateShipId(), Is.EqualTo("ship-2"));
        Assert.That(allocator.AllocateShipId(), Is.EqualTo("ship-3"));
    }

    [Test]
    public void NeverReusesAnId()
    {
        var allocator = new NsvFleetIdAllocator();
        var seen = new HashSet<string>();

        for (var i = 0; i < 1000; i++)
            Assert.That(seen.Add(allocator.AllocateShipId()), Is.True);
    }
}
