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
        string starmapId)
    {
        Rows = rows;
        TotalSectorCount = totalSectorCount;
        TotalSectorSoftCapacity = totalSectorSoftCapacity;
        ActiveSectorCount = activeSectorCount;
        ActiveSectorSoftCapacity = activeSectorSoftCapacity;
        Nodes = nodes;
        Fleets = fleets;
        StarmapId = starmapId;
    }

    public NsvSectorMonitorRow[] Rows { get; }
    public int TotalSectorCount { get; }
    public int TotalSectorSoftCapacity { get; }
    public int ActiveSectorCount { get; }
    public int ActiveSectorSoftCapacity { get; }
    public NsvSectorMonitorNode[] Nodes { get; }
    public NsvSectorMonitorFleet[] Fleets { get; }
    public string StarmapId { get; }
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
        float completeness)
    {
        ShipId = shipId;
        GridPath = gridPath;
        State = state;
        NodeId = nodeId;
        Completeness = completeness;
    }

    public string ShipId { get; }
    public string GridPath { get; }
    public string State { get; }
    public string? NodeId { get; }
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
    public sealed class SpawnDataFleetRequest : EuiMessageBase
    {
        public SpawnDataFleetRequest(string starmapId, string nodeId)
        {
            StarmapId = starmapId;
            NodeId = nodeId;
        }

        public string StarmapId { get; }
        public string NodeId { get; }
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
