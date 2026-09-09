using System.IO;
using System.Linq;
using System.Numerics;
using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Cargo;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._NSV.Bluespace.Starmap;

[Serializable, NetSerializable]
public enum NsvBluespaceStarmapNodeType : byte
{
    Home,
    Asteroid,
    Distress,
    PiratePatrol,
    UnknownSignal
}

[Prototype("nsvBluespaceStarmap")]
public sealed partial class NsvBluespaceStarmapPrototype : IPrototype, ISerializationHooks
{
    private readonly Dictionary<string, NsvBluespaceStarmapNodeDefinition> _nodes = new(StringComparer.Ordinal);

    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField(required: true)]
    public LocId Description = string.Empty;

    [DataField(required: true)]
    public List<NsvBluespaceStarmapNodeDefinition> Nodes = new();

    public IReadOnlyCollection<NsvBluespaceStarmapNodeDefinition> NodeDefinitions => _nodes.Values;

    public bool TryGetNode(string nodeId, out NsvBluespaceStarmapNodeDefinition node)
    {
        return _nodes.TryGetValue(nodeId, out node!);
    }

    public bool IsConnected(string sourceNodeId, string destinationNodeId)
    {
        return _nodes.TryGetValue(sourceNodeId, out var source) &&
               source.Connections.Contains(destinationNodeId, StringComparer.Ordinal);
    }

    void ISerializationHooks.AfterDeserialization()
    {
        _nodes.Clear();
        foreach (var node in Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.ID))
                throw new InvalidDataException($"Starmap '{ID}' contains a node without an ID.");

            if (!_nodes.TryAdd(node.ID, node))
                throw new InvalidDataException($"Starmap '{ID}' contains duplicate node ID '{node.ID}'.");

            if (!float.IsFinite(node.Position.X) || !float.IsFinite(node.Position.Y))
                throw new InvalidDataException($"Starmap '{ID}' node '{node.ID}' has a non-finite position.");

            if (node.Threat < 0 || node.Reward < 0 || node.FuelCost < 0)
                throw new InvalidDataException($"Starmap '{ID}' node '{node.ID}' has a negative value.");
        }

        foreach (var node in Nodes)
        {
            var links = new HashSet<string>(StringComparer.Ordinal);
            foreach (var targetId in node.Connections)
            {
                if (string.IsNullOrWhiteSpace(targetId))
                    throw new InvalidDataException($"Starmap '{ID}' node '{node.ID}' has an empty link.");

                if (!links.Add(targetId))
                    throw new InvalidDataException($"Starmap '{ID}' node '{node.ID}' links to '{targetId}' more than once.");

                if (targetId == node.ID)
                    throw new InvalidDataException($"Starmap '{ID}' node '{node.ID}' cannot link to itself.");

                if (!_nodes.TryGetValue(targetId, out var target))
                    throw new InvalidDataException($"Starmap '{ID}' node '{node.ID}' links to unknown node '{targetId}'.");

                if (!target.Connections.Contains(node.ID, StringComparer.Ordinal))
                    throw new InvalidDataException($"Starmap '{ID}' link '{node.ID}' to '{targetId}' must be reciprocal.");
            }
        }
    }
}

[DataDefinition]
public sealed partial class NsvBluespaceStarmapNodeDefinition
{
    [DataField("id", required: true)]
    public string ID = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField(required: true)]
    public LocId Description = string.Empty;

    [DataField(required: true)]
    public NsvBluespaceStarmapNodeType Type;

    [DataField(required: true)]
    public Vector2 Position;

    [DataField(required: true)]
    public ProtoId<NsvBluespaceSectorTemplatePrototype> SectorTemplate = string.Empty;

    [DataField(required: true)]
    public ProtoId<NsvBluespaceFactionPrototype> Faction = string.Empty;

    [DataField]
    public ProtoId<NsvCargoMarketPrototype>? Market;

    [DataField]
    public int Seed;

    [DataField]
    public int Threat;

    [DataField]
    public int Reward;

    [DataField]
    public int FuelCost;

    [DataField]
    public List<ProtoId<NsvBluespaceEncounterPrototype>> EncounterPool = new();

    [DataField]
    public List<string> Connections = new();
}
