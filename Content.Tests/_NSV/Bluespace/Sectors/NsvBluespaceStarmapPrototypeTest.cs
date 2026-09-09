using System.IO;
using System.Numerics;
using Content.Shared._NSV.Bluespace.Starmap;
using NUnit.Framework;
using Robust.Shared.Serialization;
using Robust.UnitTesting;

namespace Content.Tests._NSV.Bluespace.Sectors;

[TestFixture]
public sealed class NsvBluespaceStarmapPrototypeTest : RobustUnitTest
{
    [Test]
    public void BuildsReciprocalGraphLookup()
    {
        var starmap = new NsvBluespaceStarmapPrototype
        {
            Nodes =
            {
                new NsvBluespaceStarmapNodeDefinition
                {
                    ID = "Home",
                    Position = Vector2.Zero,
                    Connections = { "Asteroid" }
                },
                new NsvBluespaceStarmapNodeDefinition
                {
                    ID = "Asteroid",
                    Position = Vector2.One,
                    Connections = { "Home" }
                }
            }
        };

        ((ISerializationHooks) starmap).AfterDeserialization();

        Assert.Multiple(() =>
        {
            Assert.That(starmap.TryGetNode("Home", out var home), Is.True);
            Assert.That(home.ID, Is.EqualTo("Home"));
            Assert.That(starmap.IsConnected("Home", "Asteroid"), Is.True);
            Assert.That(starmap.IsConnected("Asteroid", "Home"), Is.True);
            Assert.That(starmap.IsConnected("Home", "Missing"), Is.False);
        });
    }

    [Test]
    public void RejectsOneWayGraphLink()
    {
        var starmap = new NsvBluespaceStarmapPrototype
        {
            Nodes =
            {
                new NsvBluespaceStarmapNodeDefinition
                {
                    ID = "Home",
                    Position = Vector2.Zero,
                    Connections = { "Asteroid" }
                },
                new NsvBluespaceStarmapNodeDefinition
                {
                    ID = "Asteroid",
                    Position = Vector2.One
                }
            }
        };

        Assert.Throws<InvalidDataException>(() => ((ISerializationHooks) starmap).AfterDeserialization());
    }
}
