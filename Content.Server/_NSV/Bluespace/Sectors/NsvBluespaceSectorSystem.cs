using System.Linq;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Server._NSV.Bluespace.Sectors.Generators;
using Content.Server._NSV.NPC;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors;

public sealed partial class NsvBluespaceSectorSystem : EntitySystem
{
    [Dependency] private MapSystem _map = default!;
    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private NsvBluespaceSectorLifecycleSystem _lifecycle = default!;
    [Dependency] private IMapManager _mapManager = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private IPrototypeManager _prototype = default!;

    private readonly Dictionary<ProtoId<NsvBluespaceSectorTemplatePrototype>, EntityUid> _activeTemplateSectors = new();
    private readonly Dictionary<NsvBluespaceStarmapNodeKey, EntityUid> _activeNodeSectors = new();
    private readonly NsvBluespaceAsteroidFieldGenerator _asteroidFieldGenerator = new();
    private readonly NsvBluespaceEntityGenerator _entityGenerator = new();
    private readonly NsvBluespaceSectorLayoutPlanner _planner = new();
    private readonly NsvBluespaceShipGenerator _shipGenerator = new();
    private readonly NsvBluespaceStationGenerator _stationGenerator = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<NsvBluespaceSectorInstanceComponent, ComponentShutdown>(OnInstanceShutdown);
    }

    public bool TryGetStarmap(
        ProtoId<NsvBluespaceStarmapPrototype> starmapId,
        out NsvBluespaceStarmapPrototype starmap)
    {
        return _prototype.TryIndex(starmapId, out starmap!);
    }

    public bool TryGetOrCreate(
        ProtoId<NsvBluespaceSectorTemplatePrototype> templateId,
        int seed,
        out EntityUid mapUid)
    {
        return TryGetOrCreate(templateId, seed, NsvBluespaceSectorWakeReason.SectorAccess, null, out mapUid, out _);
    }

    public bool TryGetOrCreate(
        ProtoId<NsvBluespaceSectorTemplatePrototype> templateId,
        int seed,
        out EntityUid mapUid,
        out string? failure)
    {
        return TryGetOrCreate(
            templateId,
            seed,
            NsvBluespaceSectorWakeReason.SectorAccess,
            null,
            out mapUid,
            out failure);
    }

    public bool TryGetOrCreate(
        ProtoId<NsvBluespaceSectorTemplatePrototype> templateId,
        int seed,
        NsvBluespaceSectorWakeReason wakeReason,
        EntityUid? requester,
        out EntityUid mapUid,
        out string? failure)
    {
        return TryGetOrCreate(
            templateId,
            _activeTemplateSectors,
            templateId,
            seed,
            string.Empty,
            string.Empty,
            string.Empty,
            wakeReason,
            requester,
            out mapUid,
            out failure);
    }

    public bool TryGetOrCreateNode(
        ProtoId<NsvBluespaceStarmapPrototype> starmapId,
        string nodeId,
        out EntityUid mapUid,
        out string? failure)
    {
        return TryGetOrCreateNode(
            starmapId,
            nodeId,
            NsvBluespaceSectorWakeReason.SectorAccess,
            null,
            out mapUid,
            out failure);
    }

    public bool TryGetOrCreateNode(
        ProtoId<NsvBluespaceStarmapPrototype> starmapId,
        string nodeId,
        NsvBluespaceSectorWakeReason wakeReason,
        EntityUid? requester,
        out EntityUid mapUid,
        out string? failure)
    {
        mapUid = EntityUid.Invalid;
        failure = null;
        if (!_prototype.TryIndex<NsvBluespaceStarmapPrototype>(starmapId, out var starmap))
        {
            failure = $"Starmap '{starmapId}' is unavailable.";
            return false;
        }

        if (!starmap.TryGetNode(nodeId, out var node))
        {
            failure = $"Starmap node '{nodeId}' is unavailable.";
            return false;
        }

        string encounterDefinitionId = node.EncounterPool.Count == 0
            ? string.Empty
            : node.EncounterPool[(int) ((uint) node.Seed % (uint) node.EncounterPool.Count)];
        var key = new NsvBluespaceStarmapNodeKey(starmapId, node.ID);
        return TryGetOrCreate(
            key,
            _activeNodeSectors,
            node.SectorTemplate,
            node.Seed,
            starmapId,
            node.ID,
            encounterDefinitionId,
            wakeReason,
            requester,
            out mapUid,
            out failure);
    }

    private bool TryGetOrCreate<TKey>(
        TKey key,
        Dictionary<TKey, EntityUid> activeSectors,
        ProtoId<NsvBluespaceSectorTemplatePrototype> templateId,
        int seed,
        ProtoId<NsvBluespaceStarmapPrototype> starmapId,
        string nodeId,
        string encounterDefinitionId,
        NsvBluespaceSectorWakeReason wakeReason,
        EntityUid? requester,
        out EntityUid mapUid,
        out string? failure)
        where TKey : notnull
    {
        failure = null;
        if (activeSectors.TryGetValue(key, out mapUid) && !TerminatingOrDeleted(mapUid))
        {
            if (!TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var active))
            {
                failure = $"Sector '{templateId}' has no instance state.";
                return false;
            }

            if (!_lifecycle.RequestWake(mapUid, wakeReason, requester, out failure))
                return false;

            if (active.State == NsvBluespaceSectorState.Ready)
                return true;

            failure = $"Sector '{templateId}' is {active.State}.";
            return false;
        }

        var template = _prototype.Index(templateId);
        var modules = template.Modules
            .Select(moduleId => _prototype.Index(moduleId))
            .Select(module => new NsvBluespaceSectorModuleDefinition(
                module.ID,
                module.MinCount,
                module.MaxCount,
                module.FootprintRadius,
                module.MinimumDistance,
                module.Required,
                module.MaxPlacementAttempts))
            .ToArray();

        var layout = _planner.Plan(template.Radius, template.EntrySafeRadius, modules, seed);
        if (!layout.Succeeded)
        {
            failure = layout.Failure ?? $"Sector '{templateId}' layout failed.";
            mapUid = EntityUid.Invalid;
            return false;
        }

        mapUid = _map.CreateMap(out var mapId, false);
        var instance = EnsureComp<NsvBluespaceSectorInstanceComponent>(mapUid);
        instance.TemplateId = templateId;
        instance.StarmapId = starmapId;
        instance.NodeId = nodeId;
        instance.EncounterDefinitionId = encounterDefinitionId;
        instance.Seed = seed;
        instance.State = NsvBluespaceSectorState.Applying;
        instance.MapId = mapId;
        if (template.ShipAiLeashRadius is { } leashRadius)
        {
            var leash = EnsureComp<NsvShipAiMapComponent>(mapUid);
            leash.LeashRadius = leashRadius;
            leash.LeashStrength = template.ShipAiLeashStrength;
        }

        activeSectors[key] = mapUid;
        _factions.SetSectorRelation(mapUid, "NSVHostile", "NSVPlayer", NsvBluespaceFactionRelation.Hostile);

        try
        {
            foreach (var placement in layout.Placements)
            {
                var module = _prototype.Index<NsvBluespaceSectorModulePrototype>(placement.ModuleId);
                if (!TryApplyModule(mapUid, mapId, instance, module, placement, out var moduleFailure))
                {
                    failure = $"Module '{module.ID}' ({module.Generator.GetType().Name}) failed: {moduleFailure ?? "unknown error"}";
                    instance.State = NsvBluespaceSectorState.Failed;
                    _map.DeleteMap(mapId);
                    mapUid = EntityUid.Invalid;
                    return false;
                }
            }

            _mapManager.DoMapInitialize(mapId);
            _map.SetPaused(mapId, false);
            instance.State = NsvBluespaceSectorState.Ready;
            return true;
        }
        catch (Exception e)
        {
            Logger.ErrorS("nsv.bluespace", $"Failed to create sector '{templateId}' with seed {seed}: {e}");
            failure = $"Sector '{templateId}' failed during map initialization; check the server log.";
            instance.State = NsvBluespaceSectorState.Failed;
            _map.DeleteMap(mapId);
            mapUid = EntityUid.Invalid;
            return false;
        }
    }

    public bool TryDispose(Entity<NsvBluespaceSectorInstanceComponent?> sector)
    {
        if (!Resolve(sector, ref sector.Comp, false) ||
            sector.Comp.State is not (NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.Sleeping) ||
            sector.Comp.ForeignGrids.Count != 0 ||
            sector.Comp.PendingArrivals.Count != 0 ||
            _lifecycle.HasMustRunTaskBlockers(sector.Owner))
        {
            return false;
        }

        sector.Comp.State = NsvBluespaceSectorState.Draining;
        _encounters.PreDisposeSector(sector);
        _map.DeleteMap(sector.Comp.MapId);
        return true;
    }

    private bool TryApplyModule(
        EntityUid mapUid,
        MapId mapId,
        NsvBluespaceSectorInstanceComponent instance,
        NsvBluespaceSectorModulePrototype module,
        NsvBluespaceSectorPlacement placement,
        out string? failure)
    {
        failure = null;
        var succeeded = module.Generator switch
        {
            NsvBluespaceAsteroidFieldGeneratorDefinition asteroidField => _asteroidFieldGenerator.TryGenerate(
                EntityManager,
                _prototype,
                mapUid,
                instance,
                asteroidField,
                placement),
            NsvBluespaceEntityGeneratorDefinition entity => _entityGenerator.TryGenerate(
                EntityManager,
                _prototype,
                mapUid,
                instance,
                entity,
                placement),
            NsvBluespaceShipGeneratorDefinition ship => _shipGenerator.TryGenerate(
                _prototype,
                _factions,
                _mapLoader,
                mapId,
                instance,
                ship,
                placement,
                out failure),
            NsvBluespaceStationGeneratorDefinition station => _stationGenerator.TryGenerate(
                _prototype,
                _factions,
                _mapLoader,
                mapId,
                instance,
                station,
                placement),
            _ => false
        };

        if (!succeeded && failure == null)
            failure = "Generator rejected the module configuration.";

        return succeeded;
    }

    private void OnInstanceShutdown(EntityUid uid, NsvBluespaceSectorInstanceComponent component, ComponentShutdown args)
    {
        if (!string.IsNullOrEmpty(component.StarmapId) && !string.IsNullOrEmpty(component.NodeId))
        {
            var key = new NsvBluespaceStarmapNodeKey(component.StarmapId, component.NodeId);
            if (_activeNodeSectors.TryGetValue(key, out var active) && active == uid)
                _activeNodeSectors.Remove(key);

            return;
        }

        if (_activeTemplateSectors.TryGetValue(component.TemplateId, out var templateActive) && templateActive == uid)
            _activeTemplateSectors.Remove(component.TemplateId);
    }

    private readonly record struct NsvBluespaceStarmapNodeKey(
        ProtoId<NsvBluespaceStarmapPrototype> StarmapId,
        string NodeId);
}
