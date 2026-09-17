using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._NSV.Administration;

[Serializable, NetSerializable]
public sealed class NsvSectorMonitorEuiState : EuiStateBase
{
    public NsvSectorMonitorEuiState(NsvSectorMonitorRow[] rows)
    {
        Rows = rows;
    }

    public NsvSectorMonitorRow[] Rows { get; }
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
        int pendingArrivals)
    {
        NameLocId = nameLocId;
        NameFallback = nameFallback;
        Id = id;
        State = state;
        OwnedGrids = ownedGrids;
        OwnedEntities = ownedEntities;
        ForeignGrids = foreignGrids;
        PendingArrivals = pendingArrivals;
    }

    public string NameLocId { get; }
    public string NameFallback { get; }
    public string Id { get; }
    public string State { get; }
    public int OwnedGrids { get; }
    public int OwnedEntities { get; }
    public int ForeignGrids { get; }
    public int PendingArrivals { get; }
}

public static class NsvSectorMonitorEuiMsg
{
    [Serializable, NetSerializable]
    public sealed class RefreshRequest : EuiMessageBase
    {
    }
}
