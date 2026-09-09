using Robust.Shared.Serialization;

namespace Content.Shared._NSV.Bluespace.Sectors;

[Serializable, NetSerializable]
public sealed class NsvBluespaceCartridgeUiState : BoundUserInterfaceState
{
    public readonly bool InSector;
    public readonly string SectorName;
    public readonly string SectorStatus;
    public readonly int? OwnedGridCount;
    public readonly int? OwnedEntityCount;
    public readonly bool HasEncounter;
    public readonly string? EncounterName;
    public readonly string? EncounterObjective;
    public readonly string? EncounterStatus;
    public readonly int ParticipantCount;
    public readonly bool IsParticipant;
    public readonly bool CanExtract;

    public NsvBluespaceCartridgeUiState(
        bool inSector,
        string sectorName,
        string sectorStatus,
        int? ownedGridCount,
        int? ownedEntityCount,
        bool hasEncounter,
        string? encounterName,
        string? encounterObjective,
        string? encounterStatus,
        int participantCount,
        bool isParticipant,
        bool canExtract)
    {
        InSector = inSector;
        SectorName = sectorName;
        SectorStatus = sectorStatus;
        OwnedGridCount = ownedGridCount;
        OwnedEntityCount = ownedEntityCount;
        HasEncounter = hasEncounter;
        EncounterName = encounterName;
        EncounterObjective = encounterObjective;
        EncounterStatus = encounterStatus;
        ParticipantCount = participantCount;
        IsParticipant = isParticipant;
        CanExtract = canExtract;
    }
}
