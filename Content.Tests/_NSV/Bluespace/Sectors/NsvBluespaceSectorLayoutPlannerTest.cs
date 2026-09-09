using System;
using System.Linq;
using System.Numerics;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Sectors;
using NUnit.Framework;
using Robust.UnitTesting;

namespace Content.Tests._NSV.Bluespace.Sectors;

[TestFixture]
public sealed class NsvBluespaceSectorLayoutPlannerTest : RobustUnitTest
{
    private static readonly NsvBluespaceSectorModuleDefinition[] Modules =
    {
        new("Station", 1, 1, 80f, 100f, true, 40),
        new("Asteroid", 8, 8, 8f, 24f, false, 40)
    };

    [Test]
    public void ProducesDeterministicLayout()
    {
        var planner = new NsvBluespaceSectorLayoutPlanner();

        var first = planner.Plan(1000f, 180f, Modules, 42);
        var second = planner.Plan(1000f, 180f, Modules, 42);

        Assert.That(first.Succeeded, Is.True);
        Assert.That(second.Succeeded, Is.True);
        Assert.That(second.Placements, Is.EqualTo(first.Placements));
        Assert.That(second.Placements.Select(placement => placement.ContentSeed),
            Is.EqualTo(first.Placements.Select(placement => placement.ContentSeed)));
    }

    [Test]
    public void KeepsPlacementsOutsideReservedAndOccupiedSpace()
    {
        var planner = new NsvBluespaceSectorLayoutPlanner();
        var layout = planner.Plan(1000f, 180f, Modules, 42);

        Assert.That(layout.Succeeded, Is.True);
        foreach (var placement in layout.Placements)
        {
            Assert.That(placement.Position.Length(), Is.GreaterThanOrEqualTo(180f + placement.FootprintRadius));
        }

        for (var firstIndex = 0; firstIndex < layout.Placements.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < layout.Placements.Count; secondIndex++)
            {
                var first = layout.Placements[firstIndex];
                var second = layout.Placements[secondIndex];
                var requiredDistance = first.FootprintRadius + second.FootprintRadius + MathF.Max(first.MinimumDistance, second.MinimumDistance);
                Assert.That(Vector2.Distance(first.Position, second.Position), Is.GreaterThanOrEqualTo(requiredDistance));
            }
        }
    }

    [Test]
    public void SkipsUnplaceableOptionalModule()
    {
        var planner = new NsvBluespaceSectorLayoutPlanner();
        var modules = new[]
        {
            new NsvBluespaceSectorModuleDefinition("Station", 1, 1, 100f, 0f, true, 1),
            new NsvBluespaceSectorModuleDefinition("Debris", 1, 1, 100f, 0f, false, 1)
        };

        var layout = planner.Plan(250f, 0f, modules, 42);

        Assert.That(layout.Succeeded, Is.True);
        Assert.That(layout.Placements, Has.Count.EqualTo(1));
        Assert.That(layout.Placements[0].ModuleId, Is.EqualTo("Station"));
    }

    [Test]
    public void RejectsInvalidRequiredModuleSettings()
    {
        var planner = new NsvBluespaceSectorLayoutPlanner();
        var modules = new[]
        {
            new NsvBluespaceSectorModuleDefinition("Station", 0, 1, 80f, 100f, true, 40)
        };

        var layout = planner.Plan(1000f, 180f, modules, 42);

        Assert.That(layout.Succeeded, Is.False);
    }

    [Test]
    public void RejectsUnplaceableRequiredModule()
    {
        var planner = new NsvBluespaceSectorLayoutPlanner();
        var modules = new[]
        {
            new NsvBluespaceSectorModuleDefinition("Station", 1, 1, 100f, 0f, true, 1)
        };

        var layout = planner.Plan(200f, 150f, modules, 42);

        Assert.That(layout.Succeeded, Is.False);
    }
}
