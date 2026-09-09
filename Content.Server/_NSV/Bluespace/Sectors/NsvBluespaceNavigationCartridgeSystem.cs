using Content.Server._NSV.Bluespace.Encounters;
using Content.Server.CartridgeLoader;
using Content.Shared._NSV.Bluespace.Encounters;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared.CartridgeLoader;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors;

/// <summary>
/// Pushes read-only sector/encounter snapshots to the NSV bluespace navigation
/// PDA program. The program has no jump controls; all travel still goes through
/// the navigation console.
/// </summary>
public sealed class NsvBluespaceNavigationCartridgeSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private NsvBluespaceEncounterSystem _encounters = default!;
    [Dependency] private NsvBluespaceSectorTravelSystem _travel = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NsvBluespaceNavigationCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
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

    private void OnUiReady(EntityUid uid, NsvBluespaceNavigationCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        _cartridgeLoader.UpdateCartridgeUiState(args.Loader, BuildState(args.Loader));
    }

    private void RefreshSector(EntityUid sectorMap)
    {
        var query = EntityQueryEnumerator<NsvBluespaceNavigationCartridgeComponent, CartridgeComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var cartridge, out var transform))
        {
            if (cartridge.LoaderUid is not { } loaderUid || transform.MapUid != sectorMap)
                continue;

            _cartridgeLoader.UpdateCartridgeUiState(loaderUid, BuildState(loaderUid));
        }
    }

    private void RefreshShuttle(EntityUid shuttleUid)
    {
        var query = EntityQueryEnumerator<NsvBluespaceNavigationCartridgeComponent, CartridgeComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var cartridge, out var transform))
        {
            if (cartridge.LoaderUid is not { } loaderUid || transform.GridUid != shuttleUid)
                continue;

            _cartridgeLoader.UpdateCartridgeUiState(loaderUid, BuildState(loaderUid));
        }
    }

    private NsvBluespaceCartridgeUiState BuildState(EntityUid loaderUid)
    {
        var transform = Transform(loaderUid);
        var gridUid = transform.GridUid;
        NsvBluespaceSectorInstanceComponent? sector = null;
        if (transform.MapUid is { } mapUid && TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var foundSector))
            sector = foundSector;

        NsvBluespaceEncounterComponent? encounter = null;
        if (sector != null &&
            sector.EncounterController != EntityUid.Invalid &&
            TryComp<NsvBluespaceEncounterComponent>(sector.EncounterController, out var foundEncounter))
        {
            encounter = foundEncounter;
        }

        string? encounterName = null;
        string? encounterObjective = null;
        string? encounterStatus = null;
        var participantCount = 0;
        var isParticipant = false;
        var canExtract = false;
        if (encounter != null)
        {
            var definition = _prototypes.Index<NsvBluespaceEncounterPrototype>(encounter.DefinitionId);
            encounterName = definition.Name.ToString();
            encounterObjective = definition.Objective.ToString();
            encounterStatus = NsvBluespaceNavigationConsoleSystem.GetEncounterStatus(encounter.State);
            participantCount = encounter.Participants.Count;
            isParticipant = gridUid != null && encounter.Participants.Contains(gridUid.Value);
            canExtract = isParticipant && encounter.State is NsvBluespaceEncounterState.ExtractionOpen or NsvBluespaceEncounterState.Failed;
        }

        string sectorName;
        string sectorStatus;
        if (sector != null)
        {
            var template = _prototypes.Index<NsvBluespaceSectorTemplatePrototype>(sector.TemplateId);
            sectorName = template.Name.ToString();
            sectorStatus = NsvBluespaceNavigationConsoleSystem.GetSectorStatus(sector.State);
        }
        else
        {
            sectorName = "nsv-bluespace-cartridge-no-sector";
            sectorStatus = "nsv-bluespace-console-sector-status-awaiting-jump";
        }

        return new NsvBluespaceCartridgeUiState(
            sector != null,
            sectorName,
            sectorStatus,
            sector?.OwnedGrids.Count,
            sector?.OwnedEntities.Count,
            encounter != null,
            encounterName,
            encounterObjective,
            encounterStatus,
            participantCount,
            isParticipant,
            canExtract);
    }
}
