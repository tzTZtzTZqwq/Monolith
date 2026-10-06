using System.Numerics;
using Content.Shared._NSV.Bluespace.Starmap;
using Robust.Shared.Serialization;

namespace Content.Shared._NSV.Bluespace.Sectors;

[Serializable, NetSerializable]
public enum NsvBluespaceNavigationConsoleUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public sealed class NsvBluespaceJumpRequestMessage : BoundUserInterfaceMessage
{
    public readonly string? DestinationNodeId;
    public readonly bool ReturnToDeparture;

    public NsvBluespaceJumpRequestMessage(string? destinationNodeId, bool returnToDeparture)
    {
        DestinationNodeId = destinationNodeId;
        ReturnToDeparture = returnToDeparture;
    }
}

[Serializable, NetSerializable]
public sealed class NsvBluespaceStarmapNodeState
{
    public readonly string Id;
    public readonly string Name;
    public readonly string Description;
    public readonly NsvBluespaceStarmapNodeType Type;
    public readonly Vector2 Position;
    public readonly int Threat;
    public readonly int Reward;
    public readonly int FuelCost;
    public readonly string Faction;
    public readonly List<string> EncounterNames;
    public readonly List<string> Connections;
    public readonly bool IsCurrent;
    public readonly bool IsSelectable;

    public NsvBluespaceStarmapNodeState(
        string id,
        string name,
        string description,
        NsvBluespaceStarmapNodeType type,
        Vector2 position,
        int threat,
        int reward,
        int fuelCost,
        string faction,
        List<string> encounterNames,
        List<string> connections,
        bool isCurrent,
        bool isSelectable)
    {
        Id = id;
        Name = name;
        Description = description;
        Type = type;
        Position = position;
        Threat = threat;
        Reward = reward;
        FuelCost = fuelCost;
        Faction = faction;
        EncounterNames = encounterNames;
        Connections = connections;
        IsCurrent = isCurrent;
        IsSelectable = isSelectable;
    }
}

[Serializable, NetSerializable]
public sealed class NsvCampaignObjectiveReadout
{
    public readonly string LabelLoc;
    public readonly string StatusLoc;
    public readonly int Tally;
    public readonly int Target;

    public NsvCampaignObjectiveReadout(string labelLoc, string statusLoc, int tally, int target)
    {
        LabelLoc = labelLoc;
        StatusLoc = statusLoc;
        Tally = tally;
        Target = target;
    }
}

[Serializable, NetSerializable]
public sealed class NsvCampaignSummaryState
{
    public readonly string PhaseLoc;
    public readonly int Score;
    public readonly int ThreatElevation;
    public readonly List<NsvCampaignObjectiveReadout> Objectives;

    public NsvCampaignSummaryState(string phaseLoc, int score, int threatElevation, List<NsvCampaignObjectiveReadout> objectives)
    {
        PhaseLoc = phaseLoc;
        Score = score;
        ThreatElevation = threatElevation;
        Objectives = objectives;
    }
}

[Serializable, NetSerializable]
public sealed class NsvBluespaceNavigationConsoleState : BoundUserInterfaceState
{
    public readonly string SectorName;
    public readonly string SectorDescription;
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
    public readonly List<NsvBluespaceStarmapNodeState> StarmapNodes;
    public readonly string? CurrentNodeId;
    public readonly bool CanReturnToDeparture;
    public readonly NsvCampaignSummaryState? Campaign;
    public readonly NsvBluespaceEncounterProgressState? EncounterProgress;

    // A hostile FTL interdictor in this sector is holding the shuttle in place.
    public readonly bool Interdicted;

    public NsvBluespaceNavigationConsoleState(
        string sectorName,
        string sectorDescription,
        string sectorStatus,
        int? ownedGridCount,
        int? ownedEntityCount,
        bool hasEncounter,
        string? encounterName,
        string? encounterObjective,
        string? encounterStatus,
        int participantCount,
        bool isParticipant,
        bool canExtract,
        List<NsvBluespaceStarmapNodeState>? starmapNodes = null,
        string? currentNodeId = null,
        bool canReturnToDeparture = false,
        NsvCampaignSummaryState? campaign = null,
        NsvBluespaceEncounterProgressState? encounterProgress = null,
        bool interdicted = false)
    {
        SectorName = sectorName;
        SectorDescription = sectorDescription;
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
        StarmapNodes = starmapNodes ?? new List<NsvBluespaceStarmapNodeState>();
        CurrentNodeId = currentNodeId;
        CanReturnToDeparture = canReturnToDeparture;
        Campaign = campaign;
        EncounterProgress = encounterProgress;
        Interdicted = interdicted;
    }
}

/// <summary>
/// Kind-specific live progress of an active encounter, e.g. targets remaining (ClearSystem) or the
/// moment extraction unlocks (Hold). <see cref="LabelLoc"/> is formatted with <c>$count</c> when
/// <see cref="Count"/> is set and with <c>$time</c> (mm:ss left) when <see cref="Deadline"/> is set;
/// the deadline is in server <c>CurTime</c> so the client can count down without new states.
/// </summary>
[Serializable, NetSerializable]
public sealed class NsvBluespaceEncounterProgressState
{
    public readonly string LabelLoc;
    public readonly int? Count;
    public readonly TimeSpan? Deadline;

    public NsvBluespaceEncounterProgressState(string labelLoc, int? count = null, TimeSpan? deadline = null)
    {
        LabelLoc = labelLoc;
        Count = count;
        Deadline = deadline;
    }
}
