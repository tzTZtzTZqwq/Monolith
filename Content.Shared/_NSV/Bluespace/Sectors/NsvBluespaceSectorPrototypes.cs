using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Utility;

namespace Content.Shared._NSV.Bluespace.Sectors;

[Prototype("nsvBluespaceSectorTemplate")]
public sealed partial class NsvBluespaceSectorTemplatePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField(required: true)]
    public LocId Description = string.Empty;

    [DataField]
    public float Radius = 1200f;

    [DataField]
    public float EntrySafeRadius = 200f;

    [DataField]
    public float? ShipAiLeashRadius;

    [DataField]
    public float ShipAiLeashStrength = 0.6f;

    [DataField]
    public List<ProtoId<NsvBluespaceSectorModulePrototype>> Modules = new();
}

[Prototype("nsvBluespaceSectorModule")]
public sealed partial class NsvBluespaceSectorModulePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public NsvBluespaceSectorGeneratorDefinition Generator = default!;

    [DataField]
    public int MinCount = 1;

    [DataField]
    public int MaxCount = 1;

    [DataField]
    public float FootprintRadius = 32f;

    [DataField]
    public float MinimumDistance = 64f;

    [DataField]
    public bool Required;

    [DataField]
    public int MaxPlacementAttempts = 24;
}

[ImplicitDataDefinitionForInheritors]
public abstract partial class NsvBluespaceSectorGeneratorDefinition
{
}

[DataDefinition]
public sealed partial class NsvBluespaceAsteroidFieldGeneratorDefinition : NsvBluespaceSectorGeneratorDefinition
{
    [DataField(required: true)]
    public float Radius;

    [DataField(required: true)]
    public float Density;

    [DataField]
    public float MinimumSpacing = 16f;

    [DataField]
    public int MaxSpawnAttempts = 24;

    [DataField(required: true)]
    public List<NsvBluespaceWeightedEntityDefinition> AsteroidTypes = new();
}

[DataDefinition]
public sealed partial class NsvBluespaceStationGeneratorDefinition : NsvBluespaceSectorGeneratorDefinition
{
    [DataField(required: true)]
    public ResPath GridPath;

    [DataField(required: true)]
    public ProtoId<NsvBluespaceFactionPrototype> Faction = string.Empty;
}

[DataDefinition]
public sealed partial class NsvBluespaceShipGeneratorDefinition : NsvBluespaceSectorGeneratorDefinition
{
    [DataField(required: true)]
    public ResPath GridPath;

    [DataField(required: true)]
    public ProtoId<NsvBluespaceFactionPrototype> Faction = string.Empty;
}

[DataDefinition]
public sealed partial class NsvBluespaceEntityGeneratorDefinition : NsvBluespaceSectorGeneratorDefinition
{
    [DataField(required: true)]
    public EntProtoId EntityPrototype;
}

[DataDefinition]
public sealed partial class NsvBluespaceWeightedEntityDefinition
{
    [DataField(required: true)]
    public EntProtoId Prototype;

    [DataField(required: true)]
    public float Weight;
}

public enum NsvBluespaceFactionRelation
{
    Neutral,
    Hostile
}

[Prototype("nsvBluespaceFaction")]
public sealed partial class NsvBluespaceFactionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public Dictionary<ProtoId<NsvBluespaceFactionPrototype>, NsvBluespaceFactionRelation> Relations = new();
}
