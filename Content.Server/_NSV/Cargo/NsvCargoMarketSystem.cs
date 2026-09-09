using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Cargo.Components;
using Content.Server.Power.Components;
using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared._NSV.Cargo;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Administration.Logs;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Cargo;

public sealed partial class NsvCargoMarketSystem : EntitySystem
{
    public const int TransactionCap = 1_000_000_000;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISharedAdminLogManager _adminLog = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;

    public bool TryResolveContext(
        EntityUid consoleUid,
        out NsvCargoMarketContext context,
        out NsvCargoFailure failure)
    {
        context = default;
        if (!consoleUid.IsValid() || !Exists(consoleUid) ||
            !TryComp(consoleUid, out TransformComponent? transform))
        {
            failure = NsvCargoFailure.InvalidConsole;
            return false;
        }

        if (transform.GridUid is not { Valid: true } gridUid || !Exists(gridUid))
        {
            failure = NsvCargoFailure.ConsoleOffGrid;
            return false;
        }

        if (!TryComp<NsvCargoHubComponent>(gridUid, out var hub))
        {
            failure = NsvCargoFailure.CargoHubMissing;
            return false;
        }

        if (transform.MapUid is not { Valid: true } mapUid ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector))
        {
            failure = NsvCargoFailure.SectorMissing;
            return false;
        }

        if (!sector.ForeignGrids.Contains(gridUid))
        {
            failure = NsvCargoFailure.GridNotRegistered;
            return false;
        }

        if (!_prototypes.TryIndex<NsvBluespaceStarmapPrototype>(sector.StarmapId, out var starmap))
        {
            failure = NsvCargoFailure.StarmapUnavailable;
            return false;
        }

        if (!starmap.TryGetNode(sector.NodeId, out var node))
        {
            failure = NsvCargoFailure.NodeUnavailable;
            return false;
        }

        NsvCargoMarketPrototype? market = null;
        if (node.Market is { } marketId && !_prototypes.TryIndex(marketId, out market))
        {
            failure = NsvCargoFailure.MarketUnavailable;
            return false;
        }

        context = new NsvCargoMarketContext(
            consoleUid,
            gridUid,
            gridUid,
            mapUid,
            hub,
            sector,
            sector.StarmapId,
            starmap,
            node.ID,
            node,
            node.Market,
            market);
        failure = NsvCargoFailure.None;
        return true;
    }

    public NsvCargoFailure ValidateConsoleRequest(EntityUid actorUid, EntityUid consoleUid)
    {
        if (!actorUid.IsValid() || !Exists(actorUid))
            return NsvCargoFailure.InvalidActor;

        if (!consoleUid.IsValid() || !Exists(consoleUid) ||
            !TryComp(consoleUid, out TransformComponent? transform))
        {
            return NsvCargoFailure.InvalidConsole;
        }

        if (!transform.Anchored)
            return NsvCargoFailure.ConsoleNotAnchored;

        if (!TryComp<ApcPowerReceiverComponent>(consoleUid, out var power) || !power.Powered)
            return NsvCargoFailure.ConsoleUnpowered;

        if (TryComp<AccessReaderComponent>(consoleUid, out var reader) &&
            !_access.IsAllowed(actorUid, consoleUid, reader))
        {
            return NsvCargoFailure.AccessDenied;
        }

        if (!_interaction.InRangeUnobstructed(actorUid, consoleUid))
            return NsvCargoFailure.OutOfRange;

        return NsvCargoFailure.None;
    }

    public bool TryGetBuyUnitPrice(
        CargoProductPrototype product,
        float defaultMultiplier,
        float offerMultiplier,
        out int unitPrice)
    {
        unitPrice = 0;
        if (product.Cost <= 0 ||
            !float.IsFinite(defaultMultiplier) || defaultMultiplier <= 0f ||
            !float.IsFinite(offerMultiplier) || offerMultiplier <= 0f)
        {
            return false;
        }

        var rawPrice = (double) product.Cost * defaultMultiplier * offerMultiplier;
        if (!double.IsFinite(rawPrice) || rawPrice <= 0d || rawPrice > TransactionCap)
            return false;

        var rounded = Math.Ceiling(rawPrice);
        if (rounded <= 0d || rounded > TransactionCap)
            return false;

        try
        {
            unitPrice = checked((int) rounded);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public bool TryGetSellAmount(double basePrice, float multiplier, out int amount)
    {
        amount = 0;
        if (!double.IsFinite(basePrice) || basePrice <= 0d ||
            !float.IsFinite(multiplier) || multiplier <= 0f)
        {
            return false;
        }

        var rawAmount = basePrice * multiplier;
        if (!double.IsFinite(rawAmount) || rawAmount <= 0d || rawAmount > TransactionCap)
            return false;

        var rounded = Math.Floor(rawAmount);
        if (rounded <= 0d || rounded > TransactionCap)
            return false;

        try
        {
            amount = checked((int) rounded);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public bool TryCalculateTotal(int unitPrice, int quantity, out int total)
    {
        total = 0;
        if (unitPrice <= 0 || unitPrice > TransactionCap || quantity <= 0 ||
            quantity > NsvCargoMarketPrototype.MaxPerTransactionLimit)
        {
            return false;
        }

        try
        {
            total = checked(unitPrice * quantity);
            return total is > 0 and <= TransactionCap;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public bool TryDebit(NsvCargoHubComponent hub, int amount, out NsvCargoFailure failure)
    {
        if (!ValidAccount(hub))
        {
            failure = NsvCargoFailure.AccountLimitExceeded;
            return false;
        }

        if (!ValidAmount(amount))
        {
            failure = NsvCargoFailure.InvalidAmount;
            return false;
        }

        if (hub.Balance < amount)
        {
            failure = NsvCargoFailure.InsufficientFunds;
            return false;
        }

        try
        {
            var balance = checked(hub.Balance - amount);
            var lifetimePurchases = checked(hub.LifetimePurchases + amount);
            if (balance < 0 || balance > TransactionCap || lifetimePurchases < 0)
            {
                failure = NsvCargoFailure.AccountLimitExceeded;
                return false;
            }

            hub.Balance = balance;
            hub.LifetimePurchases = lifetimePurchases;
            failure = NsvCargoFailure.None;
            return true;
        }
        catch (OverflowException)
        {
            failure = NsvCargoFailure.AccountLimitExceeded;
            return false;
        }
    }

    public bool TryCredit(NsvCargoHubComponent hub, int amount, out NsvCargoFailure failure)
    {
        if (!ValidAccount(hub))
        {
            failure = NsvCargoFailure.AccountLimitExceeded;
            return false;
        }

        if (!ValidAmount(amount))
        {
            failure = NsvCargoFailure.InvalidAmount;
            return false;
        }

        try
        {
            var balance = checked(hub.Balance + amount);
            var lifetimeSales = checked(hub.LifetimeSales + amount);
            if (balance < 0 || balance > TransactionCap || lifetimeSales < 0)
            {
                failure = NsvCargoFailure.AccountLimitExceeded;
                return false;
            }

            hub.Balance = balance;
            hub.LifetimeSales = lifetimeSales;
            failure = NsvCargoFailure.None;
            return true;
        }
        catch (OverflowException)
        {
            failure = NsvCargoFailure.AccountLimitExceeded;
            return false;
        }
    }

    public bool TryRefund(NsvCargoHubComponent hub, int amount, out NsvCargoFailure failure)
    {
        if (!ValidAccount(hub))
        {
            failure = NsvCargoFailure.AccountLimitExceeded;
            return false;
        }

        if (!ValidAmount(amount) || hub.LifetimePurchases < amount)
        {
            failure = NsvCargoFailure.InvalidRefund;
            return false;
        }

        try
        {
            var balance = checked(hub.Balance + amount);
            var lifetimePurchases = checked(hub.LifetimePurchases - amount);
            if (balance < 0 || balance > TransactionCap || lifetimePurchases < 0)
            {
                failure = NsvCargoFailure.AccountLimitExceeded;
                return false;
            }

            hub.Balance = balance;
            hub.LifetimePurchases = lifetimePurchases;
            failure = NsvCargoFailure.None;
            return true;
        }
        catch (OverflowException)
        {
            failure = NsvCargoFailure.AccountLimitExceeded;
            return false;
        }
    }

    public bool TryResolveBuyOffer(
        NsvCargoMarketPrototype market,
        EntProtoId requestedProduct,
        out NsvCargoResolvedBuyOffer resolved,
        out NsvCargoFailure failure)
    {
        resolved = default;
        if (market.Buy is not { } buy)
        {
            failure = NsvCargoFailure.OfferNotFound;
            return false;
        }

        NsvCargoMarketBuyOfferDefinition? matchedOffer = null;
        CargoProductPrototype? matchedProduct = null;
        foreach (var offer in buy.Offers)
        {
            if (!_prototypes.TryIndex<CargoProductPrototype>(offer.Product, out var product))
            {
                failure = NsvCargoFailure.ProductUnavailable;
                return false;
            }

            if (product.Product != requestedProduct)
                continue;

            if (matchedOffer != null)
            {
                failure = NsvCargoFailure.OfferAmbiguous;
                return false;
            }

            matchedOffer = offer;
            matchedProduct = product;
        }

        if (matchedOffer == null || matchedProduct == null)
        {
            failure = NsvCargoFailure.OfferNotFound;
            return false;
        }

        if (!_prototypes.TryIndex<EntityPrototype>(matchedProduct.Product, out _))
        {
            failure = NsvCargoFailure.ProductUnavailable;
            return false;
        }

        if (!TryGetBuyUnitPrice(
                matchedProduct,
                buy.DefaultMultiplier,
                matchedOffer.Multiplier,
                out var unitPrice))
        {
            failure = NsvCargoFailure.InvalidPrice;
            return false;
        }

        resolved = new NsvCargoResolvedBuyOffer(
            matchedOffer,
            matchedProduct,
            matchedProduct.Product,
            unitPrice);
        failure = NsvCargoFailure.None;
        return true;
    }

    public bool TryGetSellMultiplier(
        NsvCargoMarketPrototype market,
        EntityUid root,
        out float multiplier,
        out NsvCargoFailure failure)
    {
        multiplier = 0f;
        if (!root.IsValid() || !Exists(root))
        {
            failure = NsvCargoFailure.InvalidEntity;
            return false;
        }

        if (market.Sell is not { } sell)
        {
            failure = NsvCargoFailure.SellingUnavailable;
            return false;
        }

        foreach (var rule in sell.Rules)
        {
            if (!_whitelist.IsValid(rule.Whitelist, root))
                continue;

            if (!float.IsFinite(rule.Multiplier) || rule.Multiplier <= 0f)
            {
                failure = NsvCargoFailure.InvalidPrice;
                return false;
            }

            multiplier = rule.Multiplier;
            failure = NsvCargoFailure.None;
            return true;
        }

        if (sell.DefaultMultiplier is not { } defaultMultiplier)
        {
            failure = NsvCargoFailure.SellRuleNotFound;
            return false;
        }

        if (!float.IsFinite(defaultMultiplier) || defaultMultiplier <= 0f)
        {
            failure = NsvCargoFailure.InvalidPrice;
            return false;
        }

        multiplier = defaultMultiplier;
        failure = NsvCargoFailure.None;
        return true;
    }

    public void LogCargoAction(
        NsvCargoLogAction action,
        EntityUid actorUid,
        EntityUid consoleUid,
        NsvCargoMarketContext? context = null,
        int count = 0,
        int amount = 0,
        NsvCargoFailure failure = NsvCargoFailure.None)
    {
        var impact = GetLogImpact(action, failure);
        if (context is { } marketContext)
        {
            _adminLog.Add(LogType.Action, impact,
                $"NSV cargo {action}: actor={ToPrettyString(actorUid):actor} console={ToPrettyString(consoleUid):console} grid={ToPrettyString(marketContext.GridUid):grid} starmap={marketContext.StarmapId} node={marketContext.NodeId} market={marketContext.MarketId?.ToString() ?? "none"} count={count} amount={amount} failure={failure}");
            return;
        }

        _adminLog.Add(LogType.Action, impact,
            $"NSV cargo {action}: actor={ToPrettyString(actorUid):actor} console={ToPrettyString(consoleUid):console} grid=unknown starmap=unknown node=unknown market=unknown count={count} amount={amount} failure={failure}");
    }

    private static bool ValidAccount(NsvCargoHubComponent hub)
    {
        return hub.Balance is >= 0 and <= TransactionCap &&
               hub.LifetimePurchases >= 0 &&
               hub.LifetimeSales >= 0;
    }

    private static bool ValidAmount(int amount)
    {
        return amount is > 0 and <= TransactionCap;
    }

    private static LogImpact GetLogImpact(NsvCargoLogAction action, NsvCargoFailure failure)
    {
        if (action == NsvCargoLogAction.DeliveryFault ||
            failure is NsvCargoFailure.AccountLimitExceeded or
                NsvCargoFailure.InvalidRefund or
                NsvCargoFailure.OfferAmbiguous)
        {
            return LogImpact.High;
        }

        if (failure != NsvCargoFailure.None || action is NsvCargoLogAction.Refund or NsvCargoLogAction.DeliveryFault)
            return LogImpact.Medium;

        return LogImpact.Low;
    }
}

public readonly record struct NsvCargoMarketContext(
    EntityUid ConsoleUid,
    EntityUid GridUid,
    EntityUid HubUid,
    EntityUid MapUid,
    NsvCargoHubComponent Hub,
    NsvBluespaceSectorInstanceComponent Sector,
    ProtoId<NsvBluespaceStarmapPrototype> StarmapId,
    NsvBluespaceStarmapPrototype Starmap,
    string NodeId,
    NsvBluespaceStarmapNodeDefinition Node,
    ProtoId<NsvCargoMarketPrototype>? MarketId,
    NsvCargoMarketPrototype? Market)
{
    public NsvCargoMarketFingerprint Fingerprint => new(
        ConsoleUid,
        GridUid,
        HubUid,
        MapUid,
        StarmapId,
        NodeId,
        MarketId?.ToString());
}

public readonly record struct NsvCargoMarketFingerprint(
    EntityUid ConsoleUid,
    EntityUid GridUid,
    EntityUid HubUid,
    EntityUid MapUid,
    ProtoId<NsvBluespaceStarmapPrototype> StarmapId,
    string NodeId,
    string? MarketId);

public readonly record struct NsvCargoResolvedBuyOffer(
    NsvCargoMarketBuyOfferDefinition Offer,
    CargoProductPrototype CargoProduct,
    EntProtoId ProductId,
    int UnitPrice);

public enum NsvCargoFailure : byte
{
    None,
    InvalidActor,
    InvalidConsole,
    InvalidEntity,
    ConsoleNotAnchored,
    ConsoleUnpowered,
    AccessDenied,
    OutOfRange,
    ConsoleOffGrid,
    CargoHubMissing,
    SectorMissing,
    GridNotRegistered,
    StarmapUnavailable,
    NodeUnavailable,
    MarketUnavailable,
    SellingUnavailable,
    OfferNotFound,
    OfferAmbiguous,
    ProductUnavailable,
    SellRuleNotFound,
    InvalidPrice,
    InvalidAmount,
    OfferLimitReached,
    DeliveryMachineMissing,
    InsufficientFunds,
    AccountLimitExceeded,
    InvalidRefund
}

public enum NsvCargoLogAction : byte
{
    Purchase,
    Sale,
    Refund,
    RequestRejected,
    DeliveryFault
}
