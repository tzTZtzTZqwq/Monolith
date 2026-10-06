using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._NSV.Administration;

[Serializable, NetSerializable]
public sealed class NsvSectorMonitorEuiState : EuiStateBase
{
    public NsvSectorMonitorEuiState(
        NsvSectorMonitorRow[] rows,
        int totalSectorCount,
        int totalSectorSoftCapacity,
        int activeSectorCount,
        int activeSectorSoftCapacity,
        NsvSectorMonitorNode[] nodes,
        NsvSectorMonitorFleet[] fleets,
        string starmapId,
        NsvSectorMonitorCampaign? campaign,
        string? statusMessage)
    {
        Rows = rows;
        TotalSectorCount = totalSectorCount;
        TotalSectorSoftCapacity = totalSectorSoftCapacity;
        ActiveSectorCount = activeSectorCount;
        ActiveSectorSoftCapacity = activeSectorSoftCapacity;
        Nodes = nodes;
        Fleets = fleets;
        StarmapId = starmapId;
        Campaign = campaign;
        StatusMessage = statusMessage;
    }

    public NsvSectorMonitorRow[] Rows { get; }
    public int TotalSectorCount { get; }
    public int TotalSectorSoftCapacity { get; }
    public int ActiveSectorCount { get; }
    public int ActiveSectorSoftCapacity { get; }
    public NsvSectorMonitorNode[] Nodes { get; }
    public NsvSectorMonitorFleet[] Fleets { get; }
    public string StarmapId { get; }

    // Null when no campaign rule is running.
    public NsvSectorMonitorCampaign? Campaign { get; }

    // Result of the last admin action, shown in the panel header.
    public string? StatusMessage { get; }
}

[Serializable, NetSerializable]
public sealed class NsvSectorMonitorNode
{
    public NsvSectorMonitorNode(
        string id,
        string nameLocId,
        float posX,
        float posY,
        string[] connections,
        bool hasSector,
        string? sectorState,
        string[] residentShipIds)
    {
        Id = id;
        NameLocId = nameLocId;
        PosX = posX;
        PosY = posY;
        Connections = connections;
        HasSector = hasSector;
        SectorState = sectorState;
        ResidentShipIds = residentShipIds;
    }

    public string Id { get; }
    public string NameLocId { get; }
    public float PosX { get; }
    public float PosY { get; }
    public string[] Connections { get; }
    public bool HasSector { get; }
    public string? SectorState { get; }
    public string[] ResidentShipIds { get; }
}

[Serializable, NetSerializable]
public sealed class NsvSectorMonitorFleet
{
    public NsvSectorMonitorFleet(
        string shipId,
        string gridPath,
        string state,
        string? nodeId,
        string? faction,
        float completeness)
    {
        ShipId = shipId;
        GridPath = gridPath;
        State = state;
        NodeId = nodeId;
        Faction = faction;
        Completeness = completeness;
    }

    public string ShipId { get; }
    public string GridPath { get; }
    public string State { get; }
    public string? NodeId { get; }
    public string? Faction { get; }
    public float Completeness { get; }
}

[Serializable, NetSerializable]
public sealed class NsvSectorMonitorRow
{
    public NsvSectorMonitorRow(
        string nameLocId,
        string nameFallback,
        string id,
        string state,
        int ownedGrids,
        int ownedEntities,
        int foreignGrids,
        int pendingArrivals,
        int mustRunTaskBlockers,
        int? sleepHoldSeconds)
    {
        NameLocId = nameLocId;
        NameFallback = nameFallback;
        Id = id;
        State = state;
        OwnedGrids = ownedGrids;
        OwnedEntities = ownedEntities;
        ForeignGrids = foreignGrids;
        PendingArrivals = pendingArrivals;
        MustRunTaskBlockers = mustRunTaskBlockers;
        SleepHoldSeconds = sleepHoldSeconds;
    }

    public string NameLocId { get; }
    public string NameFallback { get; }
    public string Id { get; }
    public string State { get; }
    public int OwnedGrids { get; }
    public int OwnedEntities { get; }
    public int ForeignGrids { get; }
    public int PendingArrivals { get; }
    public int MustRunTaskBlockers { get; }
    public int? SleepHoldSeconds { get; }
}

public static class NsvSectorMonitorEuiMsg
{
    [Serializable, NetSerializable]
    public sealed class RefreshRequest : EuiMessageBase
    {
    }

    [Serializable, NetSerializable]
    public sealed class StartOutcomeVoteRequest : EuiMessageBase
    {
    }

    [Serializable, NetSerializable]
    public sealed class AdjustScoreRequest : EuiMessageBase
    {
        public AdjustScoreRequest(int amount, bool absolute)
        {
            Amount = amount;
            Absolute = absolute;
        }

        // Added to the score, or the new score when Absolute.
        public int Amount { get; }
        public bool Absolute { get; }
    }

    [Serializable, NetSerializable]
    public sealed class AdjustThreatRequest : EuiMessageBase
    {
        public AdjustThreatRequest(float amount, bool absolute)
        {
            Amount = amount;
            Absolute = absolute;
        }

        // Added to the threat, or the new threat when Absolute.
        public float Amount { get; }
        public bool Absolute { get; }
    }

    [Serializable, NetSerializable]
    public sealed class AnnounceBriefingRequest : EuiMessageBase
    {
    }

    [Serializable, NetSerializable]
    public sealed class SendReminderRequest : EuiMessageBase
    {
    }

    [Serializable, NetSerializable]
    public sealed class DispatchBlockadeRequest : EuiMessageBase
    {
    }

    [Serializable, NetSerializable]
    public sealed class SpawnDataFleetRequest : EuiMessageBase
    {
        public SpawnDataFleetRequest(string starmapId, string nodeId, string faction)
        {
            StarmapId = starmapId;
            NodeId = nodeId;
            Faction = faction;
        }

        public string StarmapId { get; }
        public string NodeId { get; }
        public string Faction { get; }
    }

    [Serializable, NetSerializable]
    public sealed class MoveFleetRequest : EuiMessageBase
    {
        public MoveFleetRequest(string shipId, string starmapId, string nodeId)
        {
            ShipId = shipId;
            StarmapId = starmapId;
            NodeId = nodeId;
        }

        public string ShipId { get; }
        public string StarmapId { get; }
        public string NodeId { get; }
    }
}

/// <summary>
/// The running campaign as the admin panel shows it: the player-facing summary plus pacing state.
/// Times are seconds at the moment the state was built.
/// </summary>
[Serializable, NetSerializable]
public sealed class NsvSectorMonitorCampaign
{
    public NsvSectorMonitorCampaign(
        NsvCampaignSummaryState summary,
        string outcome,
        bool briefingDelivered,
        int reminderStage,
        int? nextReminderSeconds,
        int? extensionSeconds,
        int activeSeconds)
    {
        Summary = summary;
        Outcome = outcome;
        BriefingDelivered = briefingDelivered;
        ReminderStage = reminderStage;
        NextReminderSeconds = nextReminderSeconds;
        ExtensionSeconds = extensionSeconds;
        ActiveSeconds = activeSeconds;
    }

    public NsvCampaignSummaryState Summary { get; }
    public string Outcome { get; }
    public bool BriefingDelivered { get; }
    public int ReminderStage { get; }
    public int? NextReminderSeconds { get; }
    public int? ExtensionSeconds { get; }
    public int ActiveSeconds { get; }
}
