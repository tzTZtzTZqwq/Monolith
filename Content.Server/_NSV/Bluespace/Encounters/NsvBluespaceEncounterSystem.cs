using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.GameRule;
using Content.Server.CartridgeLoader;
using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared.CartridgeLoader;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Encounters;

public sealed class NsvBluespaceEncounterSystem : EntitySystem
{
    // Legacy fallback for node-less template sectors (no StarmapId): the baseline Patrol Contract.
    private static readonly ProtoId<NsvBluespaceEncounterPrototype> LegacyTemplateEncounter = "NSVPatrolContract";

    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private NsvCampaignRuleSystem _campaign = default!;
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public event Action<EntityUid>? SectorDisplayChanged;

    /// <summary>
    /// Resolves the arriving shuttle's sector to an encounter definition and broadcasts
    /// <see cref="NsvBluespaceEncounterArrivalEvent"/> so the matching per-kind system can activate.
    /// This is the single place the definition id is resolved (node encounter id, with the legacy
    /// template fallback for node-less sectors); the travel layer calls it kind-agnostically and never
    /// knows which kind runs.
    /// </summary>
    public void DispatchArrival(EntityUid sectorMap, EntityUid shuttleUid)
    {
        if (!TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector))
            return;

        ProtoId<NsvBluespaceEncounterPrototype> definitionId = sector.EncounterDefinitionId;
        if (string.IsNullOrEmpty(definitionId))
        {
            // A starmap node with an empty pool simply has no encounter; only node-less template
            // sectors fall back to the baseline Patrol Contract.
            if (!string.IsNullOrEmpty(sector.StarmapId))
                return;

            definitionId = LegacyTemplateEncounter;
        }

        if (!_prototypes.TryIndex(definitionId, out var definition))
            return;

        RaiseLocalEvent(new NsvBluespaceEncounterArrivalEvent(sectorMap, shuttleUid, definitionId, definition.Kind));
    }

    public bool TryGetOrCreate(
        EntityUid sectorMap,
        ProtoId<NsvBluespaceEncounterPrototype> definitionId,
        EntityUid participant,
        out EntityUid controllerUid,
        out bool created,
        out string? failure)
    {
        controllerUid = EntityUid.Invalid;
        created = false;
        failure = null;

        if (!TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) ||
            sector.State != NsvBluespaceSectorState.Ready)
        {
            failure = "The bluespace sector is unavailable.";
            return false;
        }

        if (!_prototypes.HasIndex<NsvBluespaceEncounterPrototype>(definitionId))
        {
            failure = $"Encounter '{definitionId}' is unavailable.";
            return false;
        }

        if (sector.EncounterController != EntityUid.Invalid &&
            TryComp<NsvBluespaceEncounterComponent>(sector.EncounterController, out var existing))
        {
            if (existing.DefinitionId != definitionId)
            {
                failure = "The bluespace sector already has a different active encounter.";
                return false;
            }

            controllerUid = sector.EncounterController;
            var added = TryAddParticipant(controllerUid, existing, participant, out failure);
            if (added)
                NotifySectorChanged(sectorMap);
            return added;
        }

        controllerUid = Spawn(null, MapCoordinates.Nullspace);
        var encounter = EnsureComp<NsvBluespaceEncounterComponent>(controllerUid);
        encounter.SectorMap = sectorMap;
        encounter.DefinitionId = definitionId;
        sector.EncounterController = controllerUid;

        if (!TryAddParticipant(controllerUid, encounter, participant, out failure))
        {
            sector.EncounterController = EntityUid.Invalid;
            QueueDel(controllerUid);
            controllerUid = EntityUid.Invalid;
            return false;
        }

        created = true;
        NotifySectorChanged(sectorMap);
        return true;
    }

    public bool CanReturn(EntityUid sectorMap, EntityUid shuttleUid, out string? reason)
    {
        reason = null;
        if (!TryGetParticipantEncounter(sectorMap, shuttleUid, out _, out var encounter))
        {
            if (TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) &&
                sector.EncounterController == EntityUid.Invalid)
            {
                return true;
            }

            reason = "This shuttle is not registered for the active encounter.";
            return false;
        }

        if (encounter.State is NsvBluespaceEncounterState.ExtractionOpen or NsvBluespaceEncounterState.Failed)
            return true;

        reason = "The encounter objective is not complete.";
        return false;
    }

    public bool TryMarkReturnStarted(EntityUid sectorMap, EntityUid shuttleUid)
    {
        if (!CanReturn(sectorMap, shuttleUid, out _) ||
            !TryGetParticipantEncounter(sectorMap, shuttleUid, out _, out var encounter))
        {
            return false;
        }

        var marked = encounter.PendingReturns.Add(shuttleUid);
        if (marked)
            NotifySectorChanged(sectorMap);
        return marked;
    }

    public void MarkReturnCompleted(EntityUid shuttleUid, EntityUid destinationMap)
    {
        if (!TryComp<NsvBluespaceEncounterMemberComponent>(shuttleUid, out var member) ||
            member.Role != NsvBluespaceEncounterMemberRole.Participant ||
            !TryComp<NsvBluespaceEncounterComponent>(member.Controller, out var encounter) ||
            destinationMap == encounter.SectorMap ||
            !encounter.PendingReturns.Remove(shuttleUid))
        {
            return;
        }

        encounter.ReturnedParticipants.Add(shuttleUid);
        NotifySectorChanged(encounter.SectorMap);
    }

    /// <summary>
    /// Completes a single-target encounter (e.g. Destroy): requires the controller's recorded
    /// <see cref="NsvBluespaceEncounterComponent.ObjectiveTarget"/> to equal <paramref name="targetUid"/>.
    /// </summary>
    public bool TryCompleteObjective(EntityUid controllerUid, EntityUid targetUid)
    {
        if (!TryComp<NsvBluespaceEncounterComponent>(controllerUid, out var encounter) ||
            encounter.State != NsvBluespaceEncounterState.Active ||
            encounter.ObjectiveTarget != targetUid)
        {
            return false;
        }

        OpenExtraction(encounter);
        return true;
    }

    /// <summary>
    /// Completes an objectiveless encounter (e.g. ClearSystem, Hold): the only gate is that the
    /// encounter is still Active. The owning kind system decides when its win condition is met.
    /// </summary>
    public bool TryComplete(EntityUid controllerUid)
    {
        if (!TryComp<NsvBluespaceEncounterComponent>(controllerUid, out var encounter) ||
            encounter.State != NsvBluespaceEncounterState.Active)
        {
            return false;
        }

        OpenExtraction(encounter);
        return true;
    }

    /// <summary>
    /// Shared completion transition: flip the encounter open for extraction, settle the kind-agnostic
    /// completion side effects (campaign reward + participant notification), and refresh displays.
    /// </summary>
    private void OpenExtraction(NsvBluespaceEncounterComponent encounter)
    {
        encounter.State = NsvBluespaceEncounterState.ObjectiveComplete;
        encounter.State = NsvBluespaceEncounterState.ExtractionOpen;
        Complete(encounter);
        NotifySectorChanged(encounter.SectorMap);
    }

    /// <summary>
    /// Completion side effects shared by every encounter kind: award the sector node's campaign reward
    /// (the only payout seam — never direct credits) and push the completion notification to every
    /// participant's navigation cartridge.
    /// </summary>
    private void Complete(NsvBluespaceEncounterComponent encounter)
    {
        if (TryComp<NsvBluespaceSectorInstanceComponent>(encounter.SectorMap, out var sector))
            _campaign.NotifyEncounterComplete(sector.StarmapId, sector.NodeId);

        var header = Loc.GetString("nsv-bluespace-encounter-complete-header");
        var message = Loc.GetString("nsv-bluespace-encounter-complete-text");
        var query = EntityQueryEnumerator<NsvBluespaceNavigationCartridgeComponent, CartridgeComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var cartridge, out var transform))
        {
            if (cartridge.LoaderUid is not { } loaderUid || transform.GridUid is not { } gridUid)
                continue;

            if (!encounter.Participants.Contains(gridUid))
                continue;

            _cartridgeLoader.SendNotification(loaderUid, header, message);
        }
    }

    public void Fail(EntityUid controllerUid)
    {
        if (!TryComp<NsvBluespaceEncounterComponent>(controllerUid, out var encounter) ||
            encounter.State is NsvBluespaceEncounterState.ExtractionOpen or NsvBluespaceEncounterState.Failed or NsvBluespaceEncounterState.Disposed)
        {
            return;
        }

        encounter.State = NsvBluespaceEncounterState.Failed;
        ClearRelationOverrides(encounter);
        NotifySectorChanged(encounter.SectorMap);
    }

    public void TrackRelationOverride(
        EntityUid controllerUid,
        ProtoId<NsvBluespaceFactionPrototype> source,
        ProtoId<NsvBluespaceFactionPrototype> target)
    {
        if (TryComp<NsvBluespaceEncounterComponent>(controllerUid, out var encounter))
            encounter.RelationOverrides.Add(new NsvBluespaceEncounterRelationOverride(source, target));
    }

    public void PreDisposeSector(EntityUid sectorMap)
    {
        if (!TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) ||
            sector.EncounterController == EntityUid.Invalid ||
            !TryComp<NsvBluespaceEncounterComponent>(sector.EncounterController, out var encounter))
        {
            return;
        }

        var controllerUid = sector.EncounterController;
        sector.EncounterController = EntityUid.Invalid;

        if (encounter.State is NsvBluespaceEncounterState.Pending or NsvBluespaceEncounterState.Active)
            encounter.State = NsvBluespaceEncounterState.Failed;

        ClearRelationOverrides(encounter);
        encounter.State = NsvBluespaceEncounterState.Disposed;

        foreach (var participant in encounter.Participants)
        {
            if (TryComp<NsvBluespaceEncounterMemberComponent>(participant, out var member) &&
                member.Controller == controllerUid)
            {
                RemComp<NsvBluespaceEncounterMemberComponent>(participant);
            }
        }

        NotifySectorChanged(sectorMap);
        QueueDel(controllerUid);
    }

    public void NotifySectorChanged(EntityUid sectorMap)
    {
        SectorDisplayChanged?.Invoke(sectorMap);
    }

    private bool TryAddParticipant(
        EntityUid controllerUid,
        NsvBluespaceEncounterComponent encounter,
        EntityUid shuttleUid,
        out string? failure)
    {
        failure = null;
        if (TryComp<NsvBluespaceEncounterMemberComponent>(shuttleUid, out var member))
        {
            if (member.Controller != controllerUid ||
                member.Role != NsvBluespaceEncounterMemberRole.Participant)
            {
                failure = "This shuttle already belongs to a different encounter.";
                return false;
            }
        }
        else
        {
            member = EnsureComp<NsvBluespaceEncounterMemberComponent>(shuttleUid);
            member.Controller = controllerUid;
            member.Role = NsvBluespaceEncounterMemberRole.Participant;
        }

        encounter.Participants.Add(shuttleUid);
        return true;
    }

    private bool TryGetParticipantEncounter(
        EntityUid sectorMap,
        EntityUid shuttleUid,
        out EntityUid controllerUid,
        out NsvBluespaceEncounterComponent encounter)
    {
        controllerUid = EntityUid.Invalid;
        encounter = default!;
        if (!TryComp<NsvBluespaceSectorInstanceComponent>(sectorMap, out var sector) ||
            sector.EncounterController == EntityUid.Invalid ||
            !TryComp<NsvBluespaceEncounterMemberComponent>(shuttleUid, out var member) ||
            member.Role != NsvBluespaceEncounterMemberRole.Participant ||
            member.Controller != sector.EncounterController ||
            !TryComp<NsvBluespaceEncounterComponent>(member.Controller, out var foundEncounter) ||
            !foundEncounter!.Participants.Contains(shuttleUid))
        {
            return false;
        }

        controllerUid = member.Controller;
        encounter = foundEncounter!;
        return true;
    }

    private void ClearRelationOverrides(NsvBluespaceEncounterComponent encounter)
    {
        foreach (var relation in encounter.RelationOverrides)
        {
            _factions.ClearSectorRelation(encounter.SectorMap, relation.Source, relation.Target);
        }

        encounter.RelationOverrides.Clear();
    }
}
