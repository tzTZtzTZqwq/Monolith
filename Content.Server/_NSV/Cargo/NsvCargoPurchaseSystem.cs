using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Server._NF.CrateMachine;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Cargo.Components;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Events;
using Content.Shared._NF.CrateMachine.Components;
using Content.Shared._NF.Market;
using Content.Shared._NF.Market.BUI;
using Content.Shared._NF.Market.Events;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.GameTicking;
using Content.Shared._NSV.Cargo;
using Content.Shared.Popups;
using Content.Shared.Power;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Cargo;

/// <summary>
/// Handles the NSV cargo purchase console: per-actor carts, server-authoritative
/// prices, hub debits and delivery through NSV-owned crate machines.
/// </summary>
public sealed class NsvCargoPurchaseSystem : EntitySystem
{
    [Dependency] private readonly NsvCargoMarketSystem _market = default!;
    [Dependency] private readonly CrateMachineSystem _crateMachine = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private readonly Dictionary<(EntityUid Console, EntityUid Actor), NsvCargoCart> _carts = new();

    public override void Initialize()
    {
        Subs.BuiEvents<NsvCargoMarketConsoleComponent>(MarketConsoleUiKey.Default, subs =>
        {
            subs.Event<MarketConsoleCartMessage>(OnCartMessage);
            subs.Event<MarketPurchaseMessage>(OnPurchaseMessage);
        });

        SubscribeLocalEvent<NsvCargoMarketConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NsvCargoMarketConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NsvCargoMarketConsoleComponent, PowerChangedEvent>(OnConsolePowerChanged);
        SubscribeLocalEvent<NsvCargoMarketConsoleComponent, EntityTerminatingEvent>(OnConsoleTerminating);

        // CrateMachineOpenedEvent is raised directed-only (broadcast: false), so the
        // subscription must be component-typed. NF MarketSystem already owns the
        // (CrateMachineComponent, CrateMachineOpenedEvent) pair, so anchor ours on the
        // NSV delivery component which only NSV machines carry.
        SubscribeLocalEvent<NsvCargoDeliveryComponent, CrateMachineOpenedEvent>(OnCrateMachineOpened);
        SubscribeLocalEvent<NsvCargoDeliveryComponent, EntityTerminatingEvent>(OnDeliveryTerminating);

        SubscribeLocalEvent<EntityTerminatingEvent>(OnEntityTerminating);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<FTLStartedEvent>(OnFtlStarted);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    private void OnUiOpened(EntityUid uid, NsvCargoMarketConsoleComponent component, ref BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (!_market.TryResolveContext(uid, out var context, out _) ||
            !_market.ValidateConsoleRequest(actor, uid).Equals(NsvCargoFailure.None))
        {
            SendDisabledState(uid, context);
            return;
        }

        RefreshUiState(uid, actor, context);
    }

    private void OnConsolePowerChanged(EntityUid uid, NsvCargoMarketConsoleComponent component, ref PowerChangedEvent args)
    {
        if (args.Powered)
            return;

        _ui.CloseUi(uid, MarketConsoleUiKey.Default);
    }

    private void OnCartMessage(
        EntityUid uid,
        NsvCargoMarketConsoleComponent component,
        MarketConsoleCartMessage args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (!_market.TryValidateAndResolve(actor, uid, out var context, out var failure))
        {
            Reject(uid, actor, failure);
            return;
        }

        if (context.Market is not { } market || market.Buy is not { })
        {
            Reject(uid, actor, NsvCargoFailure.MarketUnavailable);
            return;
        }

        if (string.IsNullOrEmpty(args.ItemPrototype) ||
            !_prototypes.TryIndex<EntityPrototype>(args.ItemPrototype, out _))
        {
            Reject(uid, actor, NsvCargoFailure.OfferNotFound);
            return;
        }

        var productId = new EntProtoId(args.ItemPrototype);
        var cart = GetCart(uid, actor, context.Fingerprint);

        if (args.RemoveFromCart)
        {
            cart.Quantities.Remove(productId);
        }
        else
        {
            if (!_market.TryResolveBuyOffer(market, productId, out var offer, out var resolveFailure))
            {
                Reject(uid, actor, resolveFailure);
                return;
            }

            var current = cart.Quantities.GetValueOrDefault(productId, 0);
            if (args.Amount <= 0 && args.Amount != int.MaxValue)
            {
                Reject(uid, actor, NsvCargoFailure.InvalidAmount);
                return;
            }

            var requested = args.Amount == int.MaxValue
                ? offer.Offer.MaxPerTransaction
                : args.Amount;
            var remaining = offer.Offer.MaxPerTransaction - current;
            if (remaining <= 0)
            {
                Reject(uid, actor, NsvCargoFailure.OfferLimitReached);
                return;
            }

            var toAdd = Math.Clamp(requested, 0, remaining);
            if (toAdd <= 0)
            {
                Reject(uid, actor, NsvCargoFailure.InvalidAmount);
                return;
            }

            cart.Quantities[productId] = current + toAdd;
        }

        RefreshUiState(uid, actor, context);
    }

    private void OnPurchaseMessage(
        EntityUid uid,
        NsvCargoMarketConsoleComponent component,
        MarketPurchaseMessage args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (!_market.TryValidateAndResolve(actor, uid, out var context, out var failure))
        {
            Reject(uid, actor, failure);
            return;
        }

        if (context.Market is not { } market || market.Buy is not { })
        {
            Reject(uid, actor, NsvCargoFailure.MarketUnavailable);
            return;
        }

        if (!_carts.TryGetValue((uid, actor), out var cart) || cart.Quantities.Count == 0)
        {
            _market.LogCargoAction(NsvCargoLogAction.RequestRejected, actor, uid, context, 0, 0, NsvCargoFailure.OfferNotFound);
            _popup.PopupEntity(Loc.GetString("nsv-cargo-purchase-cart-empty"), actor, actor);
            RefreshUiState(uid, actor, context);
            return;
        }

        if (!TryBuildOrder(cart, market, out var lines, out var total, out var orderFailure))
        {
            Reject(uid, actor, orderFailure);
            RefreshUiState(uid, actor, context);
            return;
        }

        if (total > context.Hub.Balance)
        {
            Reject(uid, actor, NsvCargoFailure.InsufficientFunds);
            RefreshUiState(uid, actor, context);
            return;
        }

        if (!TryFindDeliveryMachine(uid, component, context, out var machineUid, out var crateMachine, out var delivery))
        {
            Reject(uid, actor, NsvCargoFailure.DeliveryMachineMissing);
            return;
        }

        CommitOrder(uid, actor, context, cart, lines, total, machineUid, crateMachine, delivery);
    }

    private bool TryBuildOrder(
        NsvCargoCart cart,
        NsvCargoMarketPrototype market,
        out List<NsvCargoDeliveryLine> lines,
        out int total,
        out NsvCargoFailure failure)
    {
        lines = new List<NsvCargoDeliveryLine>();
        total = 0;
        failure = NsvCargoFailure.None;

        try
        {
            foreach (var (productId, quantity) in cart.Quantities)
            {
                if (quantity <= 0)
                {
                    failure = NsvCargoFailure.InvalidAmount;
                    return false;
                }

                if (!_market.TryResolveBuyOffer(market, productId, out var offer, out var resolveFailure))
                {
                    failure = resolveFailure;
                    return false;
                }

                if (quantity > offer.Offer.MaxPerTransaction)
                {
                    failure = NsvCargoFailure.OfferLimitReached;
                    return false;
                }

                if (!_market.TryCalculateTotal(offer.UnitPrice, quantity, out var lineTotal))
                {
                    failure = NsvCargoFailure.InvalidPrice;
                    return false;
                }

                total = checked(total + lineTotal);
                if (total <= 0 || total > NsvCargoMarketSystem.TransactionCap)
                {
                    failure = NsvCargoFailure.AccountLimitExceeded;
                    return false;
                }

                lines.Add(new NsvCargoDeliveryLine(offer.CargoProduct.ID, productId, quantity, offer.UnitPrice));
            }
        }
        catch (OverflowException)
        {
            failure = NsvCargoFailure.AccountLimitExceeded;
            return false;
        }

        return lines.Count > 0;
    }

    private bool TryFindDeliveryMachine(
        EntityUid consoleUid,
        NsvCargoMarketConsoleComponent console,
        NsvCargoMarketContext context,
        out EntityUid machineUid,
        out CrateMachineComponent crateMachine,
        [NotNullWhen(true)] out NsvCargoDeliveryComponent? delivery)
    {
        machineUid = EntityUid.Invalid;
        crateMachine = default!;
        delivery = null;

        var consoleXform = Transform(consoleUid);
        if (consoleXform.GridUid != context.GridUid)
            return false;

        var consolePos = _transform.GetWorldPosition(consoleXform);
        var maxRangeSq = console.MaxDeliveryMachineDistance * console.MaxDeliveryMachineDistance;
        var bestDistSq = float.PositiveInfinity;
        var query = EntityQueryEnumerator<CrateMachineComponent, NsvCargoDeliveryComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var machine, out var deliveryComp, out var xform))
        {
            if (deliveryComp.State != NsvCargoDeliveryState.Idle)
                continue;

            if (xform.GridUid != context.GridUid || !xform.Anchored)
                continue;

            if (!TryComp<ApcPowerReceiverComponent>(uid, out var power) || !power.Powered)
                continue;

            if (_crateMachine.IsOccupied(uid, machine))
                continue;

            var distSq = Vector2.DistanceSquared(_transform.GetWorldPosition(xform), consolePos);
            if (distSq > maxRangeSq || distSq >= bestDistSq)
                continue;

            bestDistSq = distSq;
            machineUid = uid;
            crateMachine = machine;
            delivery = deliveryComp;
        }

        return delivery != null;
    }

    private void CommitOrder(
        EntityUid consoleUid,
        EntityUid actor,
        NsvCargoMarketContext context,
        NsvCargoCart cart,
        List<NsvCargoDeliveryLine> lines,
        int total,
        EntityUid machineUid,
        CrateMachineComponent crateMachine,
        NsvCargoDeliveryComponent delivery)
    {
        delivery.ActorUid = actor;
        delivery.ConsoleUid = consoleUid;
        delivery.GridUid = context.GridUid;
        delivery.MapUid = context.MapUid;
        delivery.StarmapId = context.StarmapId;
        delivery.NodeId = context.NodeId;
        delivery.MarketId = context.MarketId ?? string.Empty;
        delivery.Amount = total;
        delivery.Lines.Clear();
        delivery.Lines.AddRange(lines);

        if (!_market.TryDebit(context.Hub, total, out var debitFailure))
        {
            ResetDelivery(delivery);
            Reject(consoleUid, actor, debitFailure);
            RefreshUiState(consoleUid, actor, context);
            return;
        }

        delivery.State = NsvCargoDeliveryState.Committed;
        cart.Quantities.Clear();
        _carts.Remove((consoleUid, actor));

        _market.LogCargoAction(NsvCargoLogAction.Purchase, actor, consoleUid, context, lines.Count, total, NsvCargoFailure.None);
        _popup.PopupEntity(Loc.GetString("nsv-cargo-purchase-dispatched"), actor, actor);

        try
        {
            _crateMachine.OpenFor(machineUid, crateMachine);
        }
        catch (Exception)
        {
            // Extremely unlikely: revert the commit so the hub is not charged.
            delivery.State = NsvCargoDeliveryState.Idle;
            ResetDelivery(delivery);
            if (_market.TryRefund(context.Hub, total, out var refundFailure))
            {
                _market.LogCargoAction(NsvCargoLogAction.Refund, actor, consoleUid, context, 0, total, refundFailure);
            }
            else
            {
                _market.LogCargoAction(NsvCargoLogAction.DeliveryFault, actor, consoleUid, context, 0, total, refundFailure);
                Log.Error($"NSV cargo delivery {ToPrettyString(machineUid)} failed OpenFor; refund failure {refundFailure}");
            }
        }
    }

    private void OnCrateMachineOpened(EntityUid uid, NsvCargoDeliveryComponent delivery, ref CrateMachineOpenedEvent ev)
    {
        if (delivery.State != NsvCargoDeliveryState.Committed)
        {
            return;
        }

        if (!TryComp<CrateMachineComponent>(uid, out var crateMachine))
        {
            FaultDelivery(uid, delivery, "missing CrateMachine component");
            return;
        }

        delivery.State = NsvCargoDeliveryState.Fulfilling;
        try
        {
            var crate = _crateMachine.SpawnCrate(uid, crateMachine);
            foreach (var line in delivery.Lines)
            {
                for (var i = 0; i < line.Quantity; i++)
                {
                    var product = Spawn(line.ProductId, Transform(uid).Coordinates);
                    _crateMachine.InsertIntoCrate(product, crate);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"NSV cargo delivery fault on {ToPrettyString(uid)}: {e}");
            FaultDelivery(uid, delivery, "fulfillment exception");
            return;
        }

        var amount = delivery.Amount;
        ResetDelivery(delivery);
        _market.LogCargoAction(
            NsvCargoLogAction.Purchase,
            delivery.ActorUid,
            delivery.ConsoleUid,
            null,
            0,
            amount,
            NsvCargoFailure.None);
    }

    private void FaultDelivery(EntityUid uid, NsvCargoDeliveryComponent delivery, string reason)
    {
        delivery.State = NsvCargoDeliveryState.Faulted;
        Log.Error($"NSV cargo delivery {ToPrettyString(uid)} faulted: {reason}");
        _market.LogCargoAction(
            NsvCargoLogAction.DeliveryFault,
            delivery.ActorUid,
            delivery.ConsoleUid,
            null,
            0,
            delivery.Amount,
            NsvCargoFailure.None);
    }

    private void OnDeliveryTerminating(EntityUid uid, NsvCargoDeliveryComponent comp, ref EntityTerminatingEvent args)
    {
        if (comp.State != NsvCargoDeliveryState.Committed)
            return;

        // Single pre-fulfillment refund; the machine is going away with the order undelivered.
        comp.State = NsvCargoDeliveryState.Faulted;
        var amount = comp.Amount;
        comp.Amount = 0;

        if (comp.GridUid is { Valid: true } gridUid &&
            TryComp<NsvCargoHubComponent>(gridUid, out var hub) &&
            _market.TryRefund(hub, amount, out var failure))
        {
            _market.LogCargoAction(NsvCargoLogAction.Refund, comp.ActorUid, comp.ConsoleUid, null, 0, amount, failure);
            return;
        }

        _market.LogCargoAction(NsvCargoLogAction.DeliveryFault, comp.ActorUid, comp.ConsoleUid, null, 0, amount, NsvCargoFailure.InvalidRefund);
        Log.Error($"NSV cargo delivery {ToPrettyString(uid)} terminated before fulfillment; refund of {amount} failed");
    }

    private NsvCargoCart GetCart(EntityUid consoleUid, EntityUid actorUid, in NsvCargoMarketFingerprint fingerprint)
    {
        if (!_carts.TryGetValue((consoleUid, actorUid), out var cart))
        {
            cart = new NsvCargoCart { Fingerprint = fingerprint };
            _carts[(consoleUid, actorUid)] = cart;
            return cart;
        }

        if (cart.Fingerprint != fingerprint)
        {
            // The console moved, the ship jumped or the market changed: start fresh.
            cart.Quantities.Clear();
            cart.Fingerprint = fingerprint;
        }

        return cart;
    }

    private void RefreshUiState(EntityUid consoleUid, EntityUid actor, NsvCargoMarketContext context)
    {
        var marketData = new List<MarketData>();
        var cartData = new List<MarketData>();
        var cartBalance = 0;
        var cartEntities = 0;

        var cart = _carts.GetValueOrDefault((consoleUid, actor));

        if (context.Market?.Buy is { } buy)
        {
            foreach (var offer in buy.Offers)
            {
                if (!_prototypes.TryIndex<CargoProductPrototype>(offer.Product, out var product))
                    continue;

                if (!_market.TryGetBuyUnitPrice(product, buy.DefaultMultiplier, offer.Multiplier, out var unitPrice))
                    continue;

                var inCart = cart?.Quantities.GetValueOrDefault(product.Product, 0) ?? 0;
                var available = Math.Clamp(offer.MaxPerTransaction - inCart, 0, offer.MaxPerTransaction);
                marketData.Add(new MarketData(product.Product, null, available, unitPrice));
            }
        }

        if (cart != null && context.Market is { } market)
        {
            try
            {
                foreach (var (productId, quantity) in cart.Quantities)
                {
                    if (!_market.TryResolveBuyOffer(market, productId, out var offer, out _))
                        continue;

                    if (!_market.TryCalculateTotal(offer.UnitPrice, quantity, out var lineTotal))
                        continue;

                    cartBalance = checked(cartBalance + lineTotal);
                    cartEntities = checked(cartEntities + quantity);
                    cartData.Add(new MarketData(productId, null, quantity, offer.UnitPrice));
                }
            }
            catch (OverflowException)
            {
                cartData.Clear();
                cartBalance = 0;
                cartEntities = 0;
            }
        }

        var state = new MarketConsoleInterfaceState(
            context.Hub.Balance,
            1f,
            marketData,
            cartData,
            cartBalance,
            true,
            0,
            cartEntities);
        _ui.SetUiState(consoleUid, MarketConsoleUiKey.Default, state);
    }

    private void SendDisabledState(EntityUid uid, NsvCargoMarketContext? context)
    {
        var balance = 0;
        if (context?.Hub is { } hub)
            balance = hub.Balance;

        var state = new MarketConsoleInterfaceState(balance, 1f, new List<MarketData>(), new List<MarketData>(), 0, false, 0, 0);
        _ui.SetUiState(uid, MarketConsoleUiKey.Default, state);
    }

    private void Reject(EntityUid consoleUid, EntityUid actor, NsvCargoFailure failure)
    {
        _market.LogCargoAction(NsvCargoLogAction.RequestRejected, actor, consoleUid, null, 0, 0, failure);
        _popup.PopupEntity(Loc.GetString("nsv-cargo-purchase-rejected"), actor, actor);
    }

    private void ResetDelivery(NsvCargoDeliveryComponent delivery)
    {
        delivery.State = NsvCargoDeliveryState.Idle;
        delivery.ActorUid = EntityUid.Invalid;
        delivery.ConsoleUid = EntityUid.Invalid;
        delivery.GridUid = EntityUid.Invalid;
        delivery.MapUid = EntityUid.Invalid;
        delivery.StarmapId = string.Empty;
        delivery.NodeId = string.Empty;
        delivery.MarketId = string.Empty;
        delivery.Amount = 0;
        delivery.Lines.Clear();
    }

    private void OnUiClosed(EntityUid uid, NsvCargoMarketConsoleComponent component, BoundUIClosedEvent args)
    {
        if (!args.UiKey.Equals(MarketConsoleUiKey.Default))
            return;

        _carts.Remove((uid, args.Actor));
    }

    private void OnConsoleTerminating(EntityUid uid, NsvCargoMarketConsoleComponent component, ref EntityTerminatingEvent args)
    {
        RemoveCartsWhere(key => key.Console == uid);
    }

    private void OnEntityTerminating(ref EntityTerminatingEvent args)
    {
        var entity = args.Entity.Owner;
        RemoveCartsWhere(key => key.Console == entity || key.Actor == entity);
    }

    private void OnPlayerDetached(PlayerDetachedEvent args)
    {
        RemoveCartsWhere(key => key.Actor == args.Entity);
    }

    private void OnFtlStarted(ref FTLStartedEvent args)
    {
        var grid = args.Entity;
        RemoveCartsWhere(key => _carts[key].Fingerprint.GridUid == grid);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _carts.Clear();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        _carts.Clear();
    }

    private void RemoveCartsWhere(Func<(EntityUid Console, EntityUid Actor), bool> predicate)
    {
        if (_carts.Count == 0)
            return;

        foreach (var key in _carts.Keys.Where(predicate).ToArray())
        {
            _carts.Remove(key);
        }
    }

    private sealed class NsvCargoCart
    {
        public NsvCargoMarketFingerprint Fingerprint;

        public readonly Dictionary<EntProtoId, int> Quantities = new();
    }
}
