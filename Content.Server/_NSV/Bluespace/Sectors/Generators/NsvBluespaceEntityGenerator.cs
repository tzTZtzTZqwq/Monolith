using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors.Generators;

public sealed class NsvBluespaceEntityGenerator
{
    public bool TryGenerate(
        IEntityManager entityManager,
        IPrototypeManager prototypes,
        EntityUid mapUid,
        NsvBluespaceSectorInstanceComponent instance,
        NsvBluespaceEntityGeneratorDefinition definition,
        NsvBluespaceSectorPlacement placement)
    {
        if (!prototypes.HasIndex<EntityPrototype>(definition.EntityPrototype))
            return false;

        instance.OwnedEntities.Add(entityManager.SpawnEntity(
            definition.EntityPrototype,
            new EntityCoordinates(mapUid, placement.Position)));
        return true;
    }
}
