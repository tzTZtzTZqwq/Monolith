using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors.Generators;

public sealed class NsvBluespaceStationGenerator
{
    public bool TryGenerate(
        IPrototypeManager prototypes,
        NsvBluespaceFactionSystem factions,
        MapLoaderSystem mapLoader,
        MapId mapId,
        NsvBluespaceSectorInstanceComponent instance,
        NsvBluespaceStationGeneratorDefinition definition,
        NsvBluespaceSectorPlacement placement)
    {
        if (!prototypes.HasIndex<NsvBluespaceFactionPrototype>(definition.Faction) ||
            !mapLoader.TryLoadGrid(mapId, definition.GridPath, out var grid, offset: placement.Position))
        {
            return false;
        }

        var rootGrid = grid.Value.Owner;
        if (!factions.SetFaction(rootGrid, definition.Faction))
            return false;

        instance.OwnedGrids.Add(rootGrid);
        return true;
    }
}
