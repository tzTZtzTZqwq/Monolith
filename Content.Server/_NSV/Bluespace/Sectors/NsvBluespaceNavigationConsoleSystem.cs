using System.Linq;
using Content.Server._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Server.GameObjects;

namespace Content.Server._NSV.Bluespace.Sectors;

public sealed class NsvBluespaceNavigationConsoleSystem : EntitySystem
{
    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private NsvBluespaceSectorTravelSystem _travel = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        Subs.BuiEvents<NsvBluespaceJumpPointComponent>(NsvBluespaceNavigationConsoleUiKey.Key, subs =>
        {
            subs.Event<NsvBluespaceJumpRequestMessage>(OnJumpRequest);
        });
        SubscribeLocalEvent<NsvBluespaceJumpPointComponent, BoundUIOpenedEvent>(OnUiOpened);
        _travel.SectorDisplayChanged += RefreshSector;
        _travel.ShuttleDisplayChanged += RefreshShuttle;
        _encounters.SectorDisplayChanged += RefreshSector;
    }

    public override void Shutdown()
    {
        _travel.SectorDisplayChanged -= RefreshSector;
        _travel.ShuttleDisplayChanged -= RefreshShuttle;
        _encounters.SectorDisplayChanged -= RefreshSector;
        base.Shutdown();
    }

    private void OnUiOpened(EntityUid uid, NsvBluespaceJumpPointComponent component, ref BoundUIOpenedEvent args)
    {
        UpdateState(uid, component);
    }

    private void OnJumpRequest(Entity<NsvBluespaceJumpPointComponent> ent, ref NsvBluespaceJumpRequestMessage args)
    {
        var shuttleUid = Transform(ent.Owner).GridUid;
        if (shuttleUid == null)
        {
            _popup.PopupEntity("The bluespace navigation console must be installed on a shuttle.", args.Actor);
            return;
        }

        bool succeeded;
        string? reason;
        if (args.ReturnToDeparture)
        {
            succeeded = _travel.TryReturnToDeparture(shuttleUid.Value, out reason);
        }
        else if (!string.IsNullOrEmpty(ent.Comp.StarmapId))
        {
            if (string.IsNullOrEmpty(args.DestinationNodeId))
            {
                _popup.PopupEntity("Select a starmap destination before initiating a jump.", args.Actor);
                return;
            }

            succeeded = _travel.TryTravelToNode(shuttleUid.Value, ent.Comp.StarmapId, args.DestinationNodeId, out reason);
        }
        else
        {
            succeeded = _travel.TryTravel(shuttleUid.Value, (ent.Owner, ent.Comp), _random.Next(), out reason);
        }

        _popup.PopupEntity(succeeded ? "Bluespace jump initiated." : reason ?? "The bluespace jump failed.", args.Actor);
    }

    private void RefreshSector(EntityUid sectorMap)
    {
        var query = EntityQueryEnumerator<NsvBluespaceJumpPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var jumpPoint, out var transform))
        {
            if (transform.MapUid == sectorMap)
                UpdateState(uid, jumpPoint);
        }
    }

    private void RefreshShuttle(EntityUid shuttleUid)
    {
        var query = EntityQueryEnumerator<NsvBluespaceJumpPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var jumpPoint, out var transform))
        {
            if (transform.GridUid == shuttleUid)
                UpdateState(uid, jumpPoint);
        }
    }

    private void UpdateState(EntityUid uid, NsvBluespaceJumpPointComponent jumpPoint)
    {
        if (!_ui.HasUi(uid, NsvBluespaceNavigationConsoleUiKey.Key))
            return;

        _ui.SetUiState(uid, NsvBluespaceNavigationConsoleUiKey.Key, BuildState(uid, jumpPoint));
    }

    private NsvBluespaceNavigationConsoleState BuildState(EntityUid consoleUid, NsvBluespaceJumpPointComponent jumpPoint)
    {
        NsvBluespaceStarmapPrototype? starmap = null;
        if (!string.IsNullOrEmpty(jumpPoint.StarmapId) &&
            _prototypes.TryIndex<NsvBluespaceStarmapPrototype>(jumpPoint.StarmapId, out var configuredStarmap))
        {
            starmap = configuredStarmap;
        }

        var transform = Transform(consoleUid);
        var shuttleUid = transform.GridUid;
        NsvBluespaceSectorInstanceComponent? sector = null;
        EntityUid sectorMap = EntityUid.Invalid;
        if (transform.MapUid is { } mapUid && TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var foundSector))
        {
            sectorMap = mapUid;
            sector = foundSector;
        }

        var sectorName = "nsv-bluespace-console-unconfigured-name";
        var sectorDescription = "nsv-bluespace-console-unconfigured-description";
        if (sector != null)
        {
            var template = _prototypes.Index<NsvBluespaceSectorTemplatePrototype>(sector.TemplateId);
            sectorName = template.Name.ToString();
            sectorDescription = template.Description.ToString();
        }
        else if (starmap != null)
        {
            sectorName = starmap.Name.ToString();
            sectorDescription = starmap.Description.ToString();
        }
        else if (!string.IsNullOrEmpty(jumpPoint.TemplateId))
        {
            var template = _prototypes.Index<NsvBluespaceSectorTemplatePrototype>(jumpPoint.TemplateId);
            sectorName = template.Name.ToString();
            sectorDescription = template.Description.ToString();
        }

        var currentNodeId = sector != null &&
                            !string.IsNullOrEmpty(sector.StarmapId) &&
                            sector.StarmapId == jumpPoint.StarmapId
            ? sector.NodeId
            : null;
        var isParticipant = false;
        var canExtract = false;
        var encounterName = default(string);
        var encounterObjective = default(string);
        var encounterStatus = default(string);
        var participantCount = 0;
        var hasEncounter = false;
        NsvBluespaceEncounterComponent? encounter = null;
        if (sector != null &&
            sector.EncounterController != EntityUid.Invalid &&
            TryComp<NsvBluespaceEncounterComponent>(sector.EncounterController, out var foundEncounter))
        {
            hasEncounter = true;
            encounter = foundEncounter;
        }

        if (hasEncounter)
        {
            var definition = _prototypes.Index<NsvBluespaceEncounterPrototype>(encounter!.DefinitionId);
            encounterName = definition.Name.ToString();
            encounterObjective = definition.Objective.ToString();
            encounterStatus = GetEncounterStatus(encounter.State);
            participantCount = encounter.Participants.Count;
            isParticipant = shuttleUid != null && encounter.Participants.Contains(shuttleUid.Value);
            canExtract = isParticipant && encounter.State is NsvBluespaceEncounterState.ExtractionOpen or NsvBluespaceEncounterState.Failed;
        }

        var canLeaveCurrentNode = sector != null &&
                                  shuttleUid != null &&
                                  sector.ForeignGrids.Contains(shuttleUid.Value) &&
                                  _encounters.CanReturn(sectorMap, shuttleUid.Value, out _);
        var canReturnToDeparture = canLeaveCurrentNode &&
                                   sector!.ReturnDestinations.ContainsKey(shuttleUid!.Value);
        var starmapNodes = BuildStarmapNodes(starmap, currentNodeId, sector, canLeaveCurrentNode);
        return new NsvBluespaceNavigationConsoleState(
            sectorName,
            sectorDescription,
            sector == null ? "nsv-bluespace-console-sector-status-awaiting-jump" : GetSectorStatus(sector.State),
            sector?.OwnedGrids.Count,
            sector?.OwnedEntities.Count,
            hasEncounter,
            encounterName,
            encounterObjective,
            encounterStatus,
            participantCount,
            isParticipant,
            canExtract,
            starmapNodes,
            currentNodeId,
            canReturnToDeparture);
    }

    private List<NsvBluespaceStarmapNodeState> BuildStarmapNodes(
        NsvBluespaceStarmapPrototype? starmap,
        string? currentNodeId,
        NsvBluespaceSectorInstanceComponent? sector,
        bool canLeaveCurrentNode)
    {
        var result = new List<NsvBluespaceStarmapNodeState>();
        if (starmap == null)
            return result;

        var canSelectAnyNode = sector == null;
        foreach (var node in starmap.NodeDefinitions)
        {
            var isCurrent = node.ID == currentNodeId;
            var isSelectable = !isCurrent && (canSelectAnyNode ||
                canLeaveCurrentNode && currentNodeId != null && starmap.IsConnected(currentNodeId, node.ID));
            var encounterNames = node.EncounterPool
                .Select(id => _prototypes.Index<NsvBluespaceEncounterPrototype>(id).Name.ToString())
                .ToList();
            result.Add(new NsvBluespaceStarmapNodeState(
                node.ID,
                node.Name.ToString(),
                node.Description.ToString(),
                node.Type,
                node.Position,
                node.Threat,
                node.Reward,
                node.FuelCost,
                node.Faction.ToString(),
                encounterNames,
                new List<string>(node.Connections),
                isCurrent,
                isSelectable));
        }

        return result;
    }

    public static string GetSectorStatus(NsvBluespaceSectorState state)
    {
        return state switch
        {
            NsvBluespaceSectorState.Requested => "nsv-bluespace-console-sector-status-requested",
            NsvBluespaceSectorState.Planning => "nsv-bluespace-console-sector-status-planning",
            NsvBluespaceSectorState.Applying => "nsv-bluespace-console-sector-status-applying",
            NsvBluespaceSectorState.Ready => "nsv-bluespace-console-sector-status-ready",
            NsvBluespaceSectorState.Draining => "nsv-bluespace-console-sector-status-draining",
            NsvBluespaceSectorState.Disposed => "nsv-bluespace-console-sector-status-disposed",
            NsvBluespaceSectorState.Failed => "nsv-bluespace-console-sector-status-failed",
            _ => "nsv-bluespace-console-sector-status-awaiting-jump"
        };
    }

    public static string GetEncounterStatus(NsvBluespaceEncounterState state)
    {
        return state switch
        {
            NsvBluespaceEncounterState.Pending => "nsv-bluespace-console-encounter-status-pending",
            NsvBluespaceEncounterState.Active => "nsv-bluespace-console-encounter-status-active",
            NsvBluespaceEncounterState.ObjectiveComplete => "nsv-bluespace-console-encounter-status-objective-complete",
            NsvBluespaceEncounterState.ExtractionOpen => "nsv-bluespace-console-encounter-status-extraction-open",
            NsvBluespaceEncounterState.Failed => "nsv-bluespace-console-encounter-status-failed",
            NsvBluespaceEncounterState.Disposed => "nsv-bluespace-console-encounter-status-disposed",
            _ => "nsv-bluespace-console-encounter-none"
        };
    }
}
