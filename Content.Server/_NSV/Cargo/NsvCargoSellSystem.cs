using System.Linq;
using System.Numerics;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Cargo.Components;
using Content.Shared._NSV.Cargo;
using Content.Shared.Containers;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Power;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Cargo;

/// <summary>
/// Handles the NSV cargo sell console: server-authoritative appraisal and sale
/// of goods on nearby cargo sell pallets into the ship's cargo hub.
/// </summary>
public sealed class NsvCargoSellSystem : EntitySystem
{
    [Dependency] private readonly NsvCargoMarketSystem _market = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private readonly HashSet<EntityUid> _activeSales = new();

    public override void Initialize()
    {
        Subs.BuiEvents<NsvCargoSellConsoleComponent>(NsvCargoSellUiKey.Key, subs =>
        {
            subs.Event<NsvCargoSellAppraiseMessage>(OnAppraiseMessage);
            subs.Event<NsvCargoSellRequestMessage>(OnSellMessage);
        });

        SubscribeLocalEvent<NsvCargoSellConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NsvCargoSellConsoleComponent, PowerChangedEvent>(OnPowerChanged);
    }

    private void OnPowerChanged(EntityUid uid, NsvCargoSellConsoleComponent component, ref PowerChangedEvent args)
    {
        if (args.Powered)
            return;

        _ui.CloseUi(uid, NsvCargoSellUiKey.Key);
    }

    private void OnUiOpened(EntityUid uid, NsvCargoSellConsoleComponent component, ref BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (!_market.TryResolveContext(uid, out var context, out _) ||
            !_market.ValidateConsoleRequest(actor, uid).Equals(NsvCargoFailure.None))
        {
            SendDisabledState(uid, context);
            return;
        }

        RefreshState(uid, actor, context);
    }

    private void OnAppraiseMessage(EntityUid uid, NsvCargoSellConsoleComponent component, NsvCargoSellAppraiseMessage args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (!ValidateRequest(uid, actor, out var context, out var market))
            return;

        // Appraisal never modifies the world.
        RefreshState(uid, actor, context);
    }

    private void OnSellMessage(EntityUid uid, NsvCargoSellConsoleComponent component, NsvCargoSellRequestMessage args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (!ValidateRequest(uid, actor, out var context, out var market))
            return;

        if (_activeSales.Contains(uid))
            return;

        _activeSales.Add(uid);
        try
        {
            SellPallets(uid, actor, context, market, component);
        }
        finally
        {
            _activeSales.Remove(uid);
        }
    }

    private bool ValidateRequest(
        EntityUid uid,
        EntityUid actor,
        out NsvCargoMarketContext context,
        out NsvCargoMarketPrototype market)
    {
        context = default;
        market = null!;

        var failure = _market.ValidateConsoleRequest(actor, uid);
        if (failure != NsvCargoFailure.None)
        {
            Reject(uid, actor, failure);
            return false;
        }

        if (!_market.TryResolveContext(uid, out context, out failure) ||
            context.Market is not { } resolvedMarket || resolvedMarket.Sell is not { } sell)
        {
            Reject(uid, actor, context.Market is null
                ? NsvCargoFailure.SellingUnavailable
                : failure);
            return false;
        }

        market = resolvedMarket;
        return true;
    }

    private void SellPallets(
        EntityUid uid,
        EntityUid actor,
        NsvCargoMarketContext context,
        NsvCargoMarketPrototype market,
        NsvCargoSellConsoleComponent console)
    {
        if (!TryGatherPalletGoods(uid, console, context, market, out var roots, out var total, out var failure))
        {
            Reject(uid, actor, failure);
            RefreshState(uid, actor, context);
            return;
        }

        if (roots.Count == 0)
        {
            _popup.PopupEntity(Loc.GetString("nsv-cargo-sell-no-goods"), actor, actor);
            RefreshState(uid, actor, context);
            return;
        }

        // Pre-check the account math before anything is deleted.
        try
        {
            var newBalance = checked(context.Hub.Balance + total);
            if (newBalance < 0 || newBalance > NsvCargoMarketSystem.TransactionCap)
            {
                Reject(uid, actor, NsvCargoFailure.AccountLimitExceeded);
                RefreshState(uid, actor, context);
                return;
            }
        }
        catch (OverflowException)
        {
            Reject(uid, actor, NsvCargoFailure.AccountLimitExceeded);
            return;
        }

        var ev = new EntitySoldEvent(roots, context.GridUid);
        RaiseLocalEvent(ref ev);

        foreach (var root in roots.ToArray())
        {
            if (!Terminating(root))
                Del(root);
        }

        if (!_market.TryCredit(context.Hub, total, out var creditFailure))
        {
            Log.Error($"NSV cargo sale on {ToPrettyString(uid)} deleted {roots.Count} roots but crediting {total} failed: {creditFailure}");
            _market.LogCargoAction(NsvCargoLogAction.RequestRejected, actor, uid, context, roots.Count, total, creditFailure);
            _popup.PopupEntity(Loc.GetString("nsv-cargo-sell-failed"), actor, actor);
            RefreshState(uid, actor, context);
            return;
        }

        _market.LogCargoAction(NsvCargoLogAction.Sale, actor, uid, context, roots.Count, total, NsvCargoFailure.None);
        _popup.PopupEntity(Loc.GetString("nsv-cargo-sell-sold", ("amount", total)), actor, actor);
        RefreshState(uid, actor, context);
    }

    private bool TryGatherPalletGoods(
        EntityUid consoleUid,
        NsvCargoSellConsoleComponent console,
        NsvCargoMarketContext context,
        NsvCargoMarketPrototype market,
        out HashSet<EntityUid> roots,
        out int total,
        out NsvCargoFailure failure)
    {
        roots = new HashSet<EntityUid>();
        total = 0;
        failure = NsvCargoFailure.None;

        var consoleXform = Transform(consoleUid);
        if (consoleXform.GridUid != context.GridUid)
        {
            failure = NsvCargoFailure.ConsoleOffGrid;
            return false;
        }

        var consolePos = _transform.GetWorldPosition(consoleXform);
        var maxRangeSq = console.MaxPalletDistance * console.MaxPalletDistance;

        var pallets = new List<EntityUid>();
        var query = AllEntityQuery<CargoPalletComponent, TransformComponent>();
        while (query.MoveNext(out var palletUid, out var pallet, out var palletXform))
        {
            if ((pallet.PalletType & BuySellType.Sell) == 0)
                continue;

            if (palletXform.ParentUid != context.GridUid || !palletXform.Anchored)
                continue;

            if (Vector2.DistanceSquared(_transform.GetWorldPosition(palletXform), consolePos) > maxRangeSq)
                continue;

            pallets.Add(palletUid);
        }

        if (pallets.Count == 0)
        {
            failure = NsvCargoFailure.SellRuleNotFound;
            return false;
        }

        // Overlapping pallets may return the same entity twice; the set deduplicates.
        var collected = new HashSet<EntityUid>();
        foreach (var palletUid in pallets)
        {
            _lookup.GetEntitiesIntersecting(palletUid, collected, LookupFlags.Dynamic | LookupFlags.Sundries);
        }

        // Entities contained inside other collected entities are not roots;
        // their value is included in the root's recursive price.
        var contained = new HashSet<EntityUid>();
        foreach (var ent in collected)
        {
            GatherContainedEntities(ent, contained);
        }

        collected.ExceptWith(contained);

        try
        {
            foreach (var ent in collected)
            {
                if (Terminating(ent))
                    continue;

                var xform = Transform(ent);
                if (xform.Anchored || !CanSell(ent, xform))
                    continue;

                // Reject the whole root if no sell rule matches it.
                if (!_market.TryGetSellMultiplier(market, ent, out var multiplier, out var ruleFailure))
                    continue;

                var basePrice = _pricing.GetPriceWithVendingDiscount(ent, context.GridUid);
                if (basePrice <= 0d)
                    continue;

                if (!_market.TryGetSellAmount(basePrice, multiplier, out var amount))
                    continue;

                total = checked(total + amount);
                if (total < 0 || total > NsvCargoMarketSystem.TransactionCap)
                {
                    failure = NsvCargoFailure.AccountLimitExceeded;
                    return false;
                }

                roots.Add(ent);
            }
        }
        catch (OverflowException)
        {
            failure = NsvCargoFailure.AccountLimitExceeded;
            return false;
        }

        return true;
    }

    private void GatherContainedEntities(EntityUid uid, HashSet<EntityUid> contained)
    {
        if (TryComp<ContainerManagerComponent>(uid, out var containerManager))
        {
            foreach (var container in containerManager.Containers.Values)
            {
                foreach (var entity in container.ContainedEntities)
                {
                    if (!contained.Add(entity))
                        continue;

                    GatherContainedEntities(entity, contained);
                }
            }
        }
    }

    private bool CanSell(EntityUid uid, TransformComponent xform)
    {
        if (HasComp<CargoSellBlacklistComponent>(uid))
            return false;

        if (TryComp<MobStateComponent>(uid, out var mob) && mob.CurrentState != MobState.Dead)
            return false;

        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (!CanSell(child, Transform(child)))
                return false;
        }

        return true;
    }

    private void RefreshState(EntityUid uid, EntityUid actor, NsvCargoMarketContext context)
    {
        var marketName = string.Empty;
        var enabled = false;
        var appraisal = 0;
        var count = 0;

        if (context.Market is { } market &&
            market.Sell is not null &&
            TryComp<NsvCargoSellConsoleComponent>(uid, out var console))
        {
            marketName = string.IsNullOrEmpty(market.Name)
                ? market.ID
                : Loc.GetString(market.Name);
            enabled = true;

            if (TryGatherPalletGoods(uid, console, context, market, out var roots, out var total, out _))
            {
                appraisal = total;
                count = roots.Count;
            }
        }

        var state = new NsvCargoSellInterfaceState(context.Hub.Balance, appraisal, count, marketName, enabled);
        _ui.SetUiState(uid, NsvCargoSellUiKey.Key, state);
    }

    private void SendDisabledState(EntityUid uid, NsvCargoMarketContext? context)
    {
        var balance = 0;
        if (context?.Hub is { } hub)
            balance = hub.Balance;

        var state = new NsvCargoSellInterfaceState(balance, 0, 0, string.Empty, false);
        _ui.SetUiState(uid, NsvCargoSellUiKey.Key, state);
    }

    private void Reject(EntityUid consoleUid, EntityUid actor, NsvCargoFailure failure)
    {
        _market.LogCargoAction(NsvCargoLogAction.RequestRejected, actor, consoleUid, null, 0, 0, failure);
        _popup.PopupEntity(Loc.GetString("nsv-cargo-sell-rejected"), actor, actor);
    }
}
