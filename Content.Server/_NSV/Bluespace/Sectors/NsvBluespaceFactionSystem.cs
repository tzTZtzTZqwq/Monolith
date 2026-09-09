using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors;

public sealed class NsvBluespaceFactionSystem : EntitySystem
{
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private NPCSystem _npc = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public bool TryGetFaction(EntityUid entity, out ProtoId<NsvBluespaceFactionPrototype> faction)
    {
        if (TryComp<NsvBluespaceFactionComponent>(entity, out var factionComponent))
        {
            faction = factionComponent.Faction;
            return true;
        }

        if (Transform(entity).GridUid is { } gridUid &&
            TryComp<NsvBluespaceFactionComponent>(gridUid, out factionComponent))
        {
            faction = factionComponent.Faction;
            return true;
        }

        faction = default;
        return false;
    }

    public bool TryGetSectorMap(
        EntityUid entity,
        out EntityUid mapUid,
        out NsvBluespaceSectorInstanceComponent sector)
    {
        if (Transform(entity).MapUid is not { } map)
        {
            mapUid = EntityUid.Invalid;
            sector = default!;
            return false;
        }

        if (!TryComp<NsvBluespaceSectorInstanceComponent>(map, out var sectorComponent))
        {
            mapUid = EntityUid.Invalid;
            sector = default!;
            return false;
        }

        mapUid = map;
        sector = sectorComponent;
        return true;
    }

    public NsvBluespaceFactionRelation GetRelation(EntityUid source, EntityUid target)
    {
        if (!TryGetSectorMap(source, out var sourceMap, out var sector) ||
            !TryGetSectorMap(target, out var targetMap, out _) ||
            sourceMap != targetMap ||
            !TryGetFaction(source, out var sourceFaction) ||
            !TryGetFaction(target, out var targetFaction))
        {
            return NsvBluespaceFactionRelation.Neutral;
        }

        if (sector.RelationOverrides.TryGetValue(sourceFaction, out var overrides) &&
            overrides.TryGetValue(targetFaction, out var relation))
        {
            return relation;
        }

        var prototype = _prototypes.Index(sourceFaction);
        return prototype.Relations.TryGetValue(targetFaction, out relation)
            ? relation
            : NsvBluespaceFactionRelation.Neutral;
    }

    public bool IsHostile(EntityUid source, EntityUid target)
    {
        return GetRelation(source, target) == NsvBluespaceFactionRelation.Hostile;
    }

    public bool SetFaction(EntityUid entity, ProtoId<NsvBluespaceFactionPrototype> faction)
    {
        if (!_prototypes.HasIndex<NsvBluespaceFactionPrototype>(faction) ||
            !TryGetSectorMap(entity, out var mapUid, out _))
        {
            return false;
        }

        var component = EnsureComp<NsvBluespaceFactionComponent>(entity);
        if (component.Faction == faction)
            return true;

        component.Faction = faction;
        ReplanSector(mapUid);
        return true;
    }

    public bool ClearFaction(EntityUid entity)
    {
        if (!TryGetSectorMap(entity, out var mapUid, out _) ||
            !HasComp<NsvBluespaceFactionComponent>(entity))
        {
            return false;
        }

        RemComp<NsvBluespaceFactionComponent>(entity);
        ReplanSector(mapUid);
        return true;
    }

    public bool RestoreFaction(
        EntityUid entity,
        EntityUid sectorMap,
        NsvBluespaceFactionSnapshot snapshot)
    {
        if (!HasComp<NsvBluespaceSectorInstanceComponent>(sectorMap) ||
            snapshot.HasFaction && !_prototypes.HasIndex<NsvBluespaceFactionPrototype>(snapshot.Faction))
        {
            return false;
        }

        if (snapshot.HasFaction)
        {
            var component = EnsureComp<NsvBluespaceFactionComponent>(entity);
            if (component.Faction == snapshot.Faction)
                return true;

            component.Faction = snapshot.Faction;
        }
        else
        {
            if (!HasComp<NsvBluespaceFactionComponent>(entity))
                return true;

            RemComp<NsvBluespaceFactionComponent>(entity);
        }

        ReplanSector(sectorMap);
        return true;
    }

    public bool SetSectorRelation(
        EntityUid sectorMap,
        ProtoId<NsvBluespaceFactionPrototype> source,
        ProtoId<NsvBluespaceFactionPrototype> target,
        NsvBluespaceFactionRelation relation)
    {
        if (!TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) ||
            !_prototypes.HasIndex<NsvBluespaceFactionPrototype>(source) ||
            !_prototypes.HasIndex<NsvBluespaceFactionPrototype>(target))
        {
            return false;
        }

        if (!sector.RelationOverrides.TryGetValue(source, out var overrides))
        {
            overrides = new Dictionary<ProtoId<NsvBluespaceFactionPrototype>, NsvBluespaceFactionRelation>();
            sector.RelationOverrides.Add(source, overrides);
        }

        if (overrides.TryGetValue(target, out var current) && current == relation)
            return true;

        overrides[target] = relation;
        ReplanSector(sectorMap);
        return true;
    }

    public bool ClearSectorRelation(
        EntityUid sectorMap,
        ProtoId<NsvBluespaceFactionPrototype> source,
        ProtoId<NsvBluespaceFactionPrototype> target)
    {
        if (!TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) ||
            !sector.RelationOverrides.TryGetValue(source, out var overrides) ||
            !overrides.Remove(target))
        {
            return false;
        }

        if (overrides.Count == 0)
            sector.RelationOverrides.Remove(source);

        ReplanSector(sectorMap);
        return true;
    }

    private void ReplanSector(EntityUid sectorMap)
    {
        var query = EntityQueryEnumerator<NsvBluespaceShipAiCoreComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out _, out var htn))
        {
            if (Transform(uid).MapUid != sectorMap)
                continue;

            _npc.WakeNPC(uid, htn);
            _htn.Replan(htn);
        }
    }
}
