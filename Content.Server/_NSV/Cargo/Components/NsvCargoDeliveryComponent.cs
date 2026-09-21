using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared._NSV.Cargo;
using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Cargo.Components;

[RegisterComponent]
public sealed partial class NsvCargoDeliveryComponent : Component
{
    [ViewVariables]
    public NsvCargoDeliveryState State;

    [ViewVariables]
    public int Amount;

    [ViewVariables]
    public EntityUid ActorUid = EntityUid.Invalid;

    [ViewVariables]
    public EntityUid ConsoleUid = EntityUid.Invalid;

    [ViewVariables]
    public EntityUid GridUid = EntityUid.Invalid;

    [ViewVariables]
    public EntityUid MapUid = EntityUid.Invalid;

    [ViewVariables]
    public ProtoId<NsvBluespaceStarmapPrototype> StarmapId = string.Empty;

    [ViewVariables]
    public string NodeId = string.Empty;

    [ViewVariables]
    public ProtoId<NsvCargoMarketPrototype> MarketId = string.Empty;

    [ViewVariables]
    public readonly List<NsvCargoDeliveryLine> Lines = new();
}

public enum NsvCargoDeliveryState : byte
{
    Idle,
    Committed,
    Fulfilling,
    Faulted
}

public readonly record struct NsvCargoDeliveryLine(
    ProtoId<CargoProductPrototype> CargoProductId,
    EntProtoId ProductId,
    int Quantity,
    int UnitPrice);
