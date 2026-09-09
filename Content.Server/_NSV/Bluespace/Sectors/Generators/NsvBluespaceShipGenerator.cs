using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors.Generators;

public sealed class NsvBluespaceShipGenerator
{
    public bool TryGenerate(
        IPrototypeManager prototypes,
        NsvBluespaceFactionSystem factions,
        MapLoaderSystem mapLoader,
        MapId mapId,
        NsvBluespaceSectorInstanceComponent instance,
        NsvBluespaceShipGeneratorDefinition definition,
        NsvBluespaceSectorPlacement placement,
        out string? failure)
    {
        failure = null;
        if (!prototypes.HasIndex<NsvBluespaceFactionPrototype>(definition.Faction))
        {
            failure = $"Faction '{definition.Faction}' does not exist.";
            return false;
        }

        if (!mapLoader.TryLoadGrid(mapId, definition.GridPath, out var grid, offset: placement.Position))
        {
            failure = $"Could not load ship grid '{definition.GridPath}'.";
            return false;
        }

        var rootGrid = grid.Value.Owner;
        if (!factions.SetFaction(rootGrid, definition.Faction))
        {
            failure = $"Could not set faction '{definition.Faction}' on ship grid '{definition.GridPath}'.";
            return false;
        }

        instance.OwnedGrids.Add(rootGrid);
        return true;
    }
}
