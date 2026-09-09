using System;
using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Pair;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server._NSV.Cargo;
using Content.Server._NSV.Cargo.Components;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared._NF.Market;
using Content.Shared._NF.Market.Events;
using Content.Shared._NSV.Cargo;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Starmap;
using Content.Shared.Access.Components;
using Content.Shared.CCVar;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Mobs.Components;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using TransformComponent = Robust.Shared.GameObjects.TransformComponent;
using TransformSystem = Robust.Server.GameObjects.TransformSystem;

namespace Content.IntegrationTests.Tests._NSV.Cargo;

/// <summary>
/// End-to-end server tests for the NSV node cargo trading flow: purchase with
/// crate delivery, pallet selling, per-actor carts and hub lifecycle across jumps.
/// </summary>
[TestFixture]
public sealed class NsvCargoTradingTest
{
    private const string Starmap = "NSVBluespaceStrategicMap";
    private const string HomeNode = "Home";
    private const string AsteroidNode = "Asteroid";
    private const string ThrusterProduct = "CrateEngineeringThruster";
    private const int InitialBalance = 10000;
    // ceil(1500 * 1 * 0.9) at Home, ceil(1500 * 1 * 1.4) at the pirate market.
    private const int ThrusterUnitPriceHome = 1350;
    private const int ThrusterUnitPricePirate = 2100;

    private sealed class NsvCargoEnv
    {
        public TestPair Pair = default!;
        public IEntityManager Ent = default!;
        public UserInterfaceSystem Ui = default!;
        public TransformSystem Xform = default!;
        public SharedMapSystem Map = default!;
        public NsvCargoMarketSystem Market = default!;
        public NsvBluespaceSectorSystem Sectors = default!;
        public NsvBluespaceSectorTravelSystem Travel = default!;
        public PricingSystem Pricing = default!;
        public IPrototypeManager Protos = default!;

        public EntityUid Grid;
        public EntityUid BuyConsole;
        public EntityUid Machine;
        public EntityUid SellConsole;
        public EntityUid PalletA;
        public EntityUid PalletB;
        public EntityUid Buyer;
        public EntityUid Seller;
        public NsvCargoHubComponent Hub = default!;

        public readonly List<EntityUid> SectorMaps = new();
        public float OldStartup;
        public float OldTravel;
        public float OldArrival;

        public EntityCoordinates Coords(float x, float y)
        {
            return new EntityCoordinates(Grid, x, y);
        }
    }

    private static async Task<NsvCargoEnv> CreateEnvAsync()
    {
        var env = new NsvCargoEnv();
        env.Pair = await PoolManager.GetServerClient();
        var server = env.Pair.Server;
        env.Ent = server.ResolveDependency<IEntityManager>();
        env.Ui = server.System<UserInterfaceSystem>();
        env.Xform = server.System<TransformSystem>();
        env.Map = server.System<SharedMapSystem>();
        env.Market = env.Ent.System<NsvCargoMarketSystem>();
        env.Sectors = env.Ent.System<NsvBluespaceSectorSystem>();
        env.Travel = env.Ent.System<NsvBluespaceSectorTravelSystem>();
        env.Pricing = env.Ent.System<PricingSystem>();
        env.Protos = server.ResolveDependency<IPrototypeManager>();

        var config = server.CfgMan;
        env.OldStartup = config.GetCVar(CCVars.FTLStartupTime);
        env.OldTravel = config.GetCVar(CCVars.FTLTravelTime);
        env.OldArrival = config.GetCVar(CCVars.FTLArrivalTime);
        config.SetCVar(CCVars.FTLStartupTime, 0.1f);
        config.SetCVar(CCVars.FTLTravelTime, 0.3f);
        config.SetCVar(CCVars.FTLArrivalTime, 0.1f);

        var testMap = await env.Pair.CreateTestMap();
        env.Grid = testMap.Grid.Owner;

        var defMan = server.ResolveDependency<ITileDefinitionManager>();
        if (!defMan.TryGetDefinition("Plating", out var plating))
            Assert.Fail("Unknown tile: Plating");
        var tileId = plating!.TileId;

        await server.WaitAssertion(() =>
        {
            for (var x = 0; x <= 6; x++)
            {
                for (var y = 0; y <= 2; y++)
                    env.Map.SetTile(env.Grid, testMap.Grid.Comp, env.Coords(x + 0.5f, y + 0.5f), new Tile(tileId));
            }

            env.Ent.EnsureComponent<ShuttleComponent>(env.Grid);
            env.Ent.EnsureComponent<NsvBluespaceFactionComponent>(env.Grid).Faction = "NSVNeutral";
            env.Hub = env.Ent.EnsureComponent<NsvCargoHubComponent>(env.Grid);
            env.Hub.Balance = InitialBalance;

            // BaseStructure-derived prototypes self-anchor during transform init;
            // anchoring again would double-register them on the snapgrid.
            env.BuyConsole = env.Ent.SpawnEntity("NsvCargoBuyConsole", env.Coords(0.5f, 0.5f));
            EnsureAnchored(env, env.BuyConsole);
            env.Machine = env.Ent.SpawnEntity("NsvCargoDeliveryMachine", env.Coords(1.5f, 0.5f));
            EnsureAnchored(env, env.Machine);
            env.SellConsole = env.Ent.SpawnEntity("NsvCargoSellConsole", env.Coords(3.5f, 0.5f));
            EnsureAnchored(env, env.SellConsole);
            env.PalletA = env.Ent.SpawnEntity("CargoPalletSell", env.Coords(4.5f, 0.5f));
            EnsureAnchored(env, env.PalletA);
            env.PalletB = env.Ent.SpawnEntity("CargoPalletSell", env.Coords(5.5f, 0.5f));
            EnsureAnchored(env, env.PalletB);

            PowerOn(env, env.BuyConsole);
            PowerOn(env, env.Machine);
            PowerOn(env, env.SellConsole);

            env.Buyer = MakeActor(env, env.Coords(0.5f, 1.5f));
            env.Seller = MakeActor(env, env.Coords(3.5f, 1.5f));
        });

        return env;
    }

    private static async Task CleanUpAsync(NsvCargoEnv env)
    {
        var server = env.Pair.Server;
        var config = server.CfgMan;
        try
        {
            await server.WaitPost(() => env.Travel.TryReturnToDeparture(env.Grid, out _));
            await env.Pair.RunSeconds(1);

            await server.WaitPost(() =>
            {
                foreach (var mapUid in env.SectorMaps.ToArray())
                {
                    if (env.Ent.TryGetComponent<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector))
                        env.Sectors.TryDispose((mapUid, sector));
                }
            });
        }
        finally
        {
            config.SetCVar(CCVars.FTLStartupTime, env.OldStartup);
            config.SetCVar(CCVars.FTLTravelTime, env.OldTravel);
            config.SetCVar(CCVars.FTLArrivalTime, env.OldArrival);
            await env.Pair.CleanReturnAsync();
        }
    }

    private static async Task<EntityUid> JumpToNodeAsync(NsvCargoEnv env, string nodeId)
    {
        var server = env.Pair.Server;

        // FTL arrival leaves a ~10s hard-coded cooldown on the shuttle.
        var waited = 0;
        while (waited < 15)
        {
            var ready = false;
            await server.WaitAssertion(() => ready = !env.Ent.HasComponent<FTLComponent>(env.Grid));
            if (ready)
                break;

            await env.Pair.RunSeconds(2);
            waited += 2;
        }

        await server.WaitPost(() =>
        {
            Assert.That(
                env.Travel.TryTravelToNode(env.Grid, Starmap, nodeId, out var reason),
                Is.True,
                reason ?? $"Travel to node {nodeId} failed without a reason.");
        });

        await env.Pair.RunSeconds(1);

        var sectorMap = EntityUid.Invalid;
        await server.WaitAssertion(() =>
        {
            sectorMap = env.Ent.GetComponent<TransformComponent>(env.Grid).MapUid!.Value;
            if (!env.SectorMaps.Contains(sectorMap))
                env.SectorMaps.Add(sectorMap);

            var sector = env.Ent.GetComponent<NsvBluespaceSectorInstanceComponent>(sectorMap);
            Assert.That(sector.ForeignGrids, Does.Contain(env.Grid), $"Grid is not registered in sector node {nodeId}.");
            Assert.That(sector.NodeId, Is.EqualTo(nodeId));
        });

        return sectorMap;
    }

    private static void EnsureAnchored(NsvCargoEnv env, EntityUid uid)
    {
        if (!env.Ent.GetComponent<TransformComponent>(uid).Anchored)
            env.Xform.AnchorEntity(uid);
    }

    private static void PowerOn(NsvCargoEnv env, EntityUid uid)
    {
        var power = env.Ent.EnsureComponent<ApcPowerReceiverComponent>(uid);
        power.NeedsPower = false;
        power.Powered = true;
    }

    private static EntityUid MakeActor(NsvCargoEnv env, EntityCoordinates coords)
    {
        var actor = env.Ent.SpawnEntity(null, coords);
        env.Ent.EnsureComponent<AccessComponent>(actor).Tags.Add("Maintenance");
        // ActivatableUI defaults to RequiresComplex: actors must qualify for
        // complex interactions to open cargo console UIs.
        env.Ent.EnsureComponent<Content.Shared.Interaction.Components.ComplexInteractionComponent>(actor);
        return actor;
    }

    private static void AddToCart(NsvCargoEnv env, EntityUid actor, int amount, string product)
    {
        var message = new MarketConsoleCartMessage(amount, product) { Actor = actor };
        env.Ui.RaiseUiMessage(env.BuyConsole, MarketConsoleUiKey.Default, message);
    }

    private static void RemoveFromCart(NsvCargoEnv env, EntityUid actor, string product)
    {
        var message = new MarketConsoleCartMessage(0, product, true) { Actor = actor };
        env.Ui.RaiseUiMessage(env.BuyConsole, MarketConsoleUiKey.Default, message);
    }

    private static void Purchase(NsvCargoEnv env, EntityUid actor)
    {
        var message = new MarketPurchaseMessage { Actor = actor };
        env.Ui.RaiseUiMessage(env.BuyConsole, MarketConsoleUiKey.Default, message);
    }

    private static void Appraise(NsvCargoEnv env, EntityUid actor)
    {
        var message = new NsvCargoSellAppraiseMessage { Actor = actor };
        env.Ui.RaiseUiMessage(env.SellConsole, NsvCargoSellUiKey.Key, message);
    }

    private static void SellAll(NsvCargoEnv env, EntityUid actor)
    {
        var message = new NsvCargoSellRequestMessage { Actor = actor };
        env.Ui.RaiseUiMessage(env.SellConsole, NsvCargoSellUiKey.Key, message);
    }

    private static List<EntityUid> FindByProto(NsvCargoEnv env, string protoId, EntityUid? grid = null, EntityUid? parent = null)
    {
        var results = new List<EntityUid>();
        var query = env.Ent.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var metadata, out var xform))
        {
            if (metadata.EntityPrototype?.ID != protoId)
                continue;

            if (grid is { } gridUid && xform.GridUid != gridUid)
                continue;

            if (parent is { } parentUid && xform.ParentUid != parentUid)
                continue;

            results.Add(uid);
        }

        return results;
    }

    [Test]
    public async Task PurchaseHomeDeliversCrateAndDebitsOnce()
    {
        var env = await CreateEnvAsync();
        try
        {
            await JumpToNodeAsync(env, HomeNode);

            var homeMarket = env.Protos.Index<NsvCargoMarketPrototype>("NSVHomeMarket");
            var pirateMarket = env.Protos.Index<NsvCargoMarketPrototype>("NSVPirateMarket");
            var thrusterProduct = env.Protos.Index<CargoProductPrototype>("ShuttleThruster");

            await env.Pair.Server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    // Server prices differ between markets for the same product.
                    Assert.That(env.Market.TryGetBuyUnitPrice(thrusterProduct, 1f, 0.9f, out var home), Is.True);
                    Assert.That(home, Is.EqualTo(ThrusterUnitPriceHome));
                    Assert.That(env.Market.TryGetBuyUnitPrice(thrusterProduct, 1f, 1.4f, out var pirate), Is.True);
                    Assert.That(pirate, Is.EqualTo(ThrusterUnitPricePirate));

                    // Ordered sell rules: Ore matches the first rule at both markets.
                    var ore = env.Ent.SpawnEntity("GoldOre", env.Coords(4.5f, 0.5f));
                    Assert.That(env.Market.TryGetSellMultiplier(homeMarket, ore, out var homeOreMultiplier, out _), Is.True);
                    Assert.That(homeOreMultiplier, Is.EqualTo(1.1f));
                    Assert.That(env.Market.TryGetSellMultiplier(pirateMarket, ore, out var pirateOreMultiplier, out _), Is.True);
                    Assert.That(pirateOreMultiplier, Is.EqualTo(0.8f));
                    env.Ent.DeleteEntity(ore);
                });

                Assert.That(env.Ui.TryOpenUi(env.BuyConsole, MarketConsoleUiKey.Default, env.Buyer), Is.True);

                AddToCart(env, env.Buyer, 2, ThrusterProduct);
                // Cart mutations alone never debit the hub.
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));

                Purchase(env, env.Buyer);
                Assert.Multiple(() =>
                {
                    Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - 2 * ThrusterUnitPriceHome));
                    Assert.That(env.Hub.LifetimePurchases, Is.EqualTo(2 * ThrusterUnitPriceHome));
                    Assert.That(env.Hub.LifetimeSales, Is.EqualTo(0));
                    var delivery = env.Ent.GetComponent<NsvCargoDeliveryComponent>(env.Machine);
                    Assert.That(delivery.State, Is.EqualTo(NsvCargoDeliveryState.Committed));
                    Assert.That(delivery.Amount, Is.EqualTo(2 * ThrusterUnitPriceHome));
                });
            });

            // Opening animation is 3.2s; wait for the crate machine to fulfill.
            await env.Pair.RunSeconds(5);

            await env.Pair.Server.WaitAssertion(() =>
            {
                var delivery = env.Ent.GetComponent<NsvCargoDeliveryComponent>(env.Machine);
                Assert.That(delivery.State, Is.EqualTo(NsvCargoDeliveryState.Idle));
                Assert.That(delivery.Amount, Is.EqualTo(0));

                var crate = FindByProto(env, "CrateGenericSteel", env.Grid).FirstOrDefault();
                Assert.That(crate, Is.Not.EqualTo(EntityUid.Invalid), "The delivery machine did not dispense a crate.");
                var contents = FindByProto(env, ThrusterProduct, parent: crate);
                Assert.That(contents.Count, Is.EqualTo(2), "The delivered crate does not contain the purchased goods.");

                // Exactly one debit happened for the whole delivery.
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - 2 * ThrusterUnitPriceHome));
            });
        }
        finally
        {
            await CleanUpAsync(env);
        }
    }

    [Test]
    public async Task PurchaseRejectionsNeverDebitHub()
    {
        var env = await CreateEnvAsync();
        try
        {
            await JumpToNodeAsync(env, HomeNode);

            var server = env.Pair.Server;

            await server.WaitAssertion(() =>
            {
                Assert.That(env.Ui.TryOpenUi(env.BuyConsole, MarketConsoleUiKey.Default, env.Buyer), Is.True);

                // Forged / unknown product entity.
                AddToCart(env, env.Buyer, 1, "DoesNotExistAtAll");
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));

                // Zero amount is rejected outright.
                AddToCart(env, env.Buyer, 0, ThrusterProduct);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));

                // Actor with no console access: the UI opens but the NSV system
                // must reject the transaction itself.
                var noAccess = env.Ent.SpawnEntity(null, env.Coords(1.5f, 1.5f));
                env.Ent.EnsureComponent<Content.Shared.Interaction.Components.ComplexInteractionComponent>(noAccess);
                Assert.That(env.Ui.TryOpenUi(env.BuyConsole, MarketConsoleUiKey.Default, noAccess), Is.True);
                AddToCart(env, noAccess, 1, ThrusterProduct);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));

                // Actor far out of interaction range never gets a UI subscription;
                // its forged messages are dropped by the bus.
                var farActor = MakeActor(env, env.Coords(50.5f, 0.5f));
                AddToCart(env, farActor, 1, ThrusterProduct);
                Purchase(env, farActor);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));

                // Insufficient funds: the cart is valid but the hub cannot cover it.
                env.Hub.Balance = 100;
                AddToCart(env, env.Buyer, 2, ThrusterProduct);
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(100));
                RemoveFromCart(env, env.Buyer, ThrusterProduct);
                env.Hub.Balance = InitialBalance;

                // Unpowered console.
                var power = env.Ent.GetComponent<ApcPowerReceiverComponent>(env.BuyConsole);
                power.Powered = false;
                AddToCart(env, env.Buyer, 1, ThrusterProduct);
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));
                power.Powered = true;

                // Unanchored console.
                env.Xform.Unanchor(env.BuyConsole, env.Ent.GetComponent<TransformComponent>(env.BuyConsole));
                AddToCart(env, env.Buyer, 1, ThrusterProduct);
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));
                env.Xform.AnchorEntity(env.BuyConsole);

                // No delivery machine in range.
                env.Ent.DeleteEntity(env.Machine);
                env.Machine = EntityUid.Invalid;
                AddToCart(env, env.Buyer, 1, ThrusterProduct);
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));

                Assert.That(env.Hub.LifetimePurchases, Is.EqualTo(0));
            });
        }
        finally
        {
            await CleanUpAsync(env);
        }
    }

    [Test]
    public async Task PurchaseCartsAreIsolatedPerActor()
    {
        var env = await CreateEnvAsync();
        try
        {
            await JumpToNodeAsync(env, HomeNode);
            var server = env.Pair.Server;
            var secondBuyer = EntityUid.Invalid;

            await server.WaitAssertion(() =>
            {
                secondBuyer = MakeActor(env, env.Coords(1.5f, 1.5f));
                Assert.That(env.Ui.TryOpenUi(env.BuyConsole, MarketConsoleUiKey.Default, env.Buyer), Is.True);
                Assert.That(env.Ui.TryOpenUi(env.BuyConsole, MarketConsoleUiKey.Default, secondBuyer), Is.True);

                AddToCart(env, env.Buyer, 2, ThrusterProduct);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));

                // The second actor has an empty cart; their purchase must not
                // spend the first actor's cart.
                Purchase(env, secondBuyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));

                // The first actor's cart survives and purchases exactly its own lines.
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - 2 * ThrusterUnitPriceHome));
            });
        }
        finally
        {
            await CleanUpAsync(env);
        }
    }

    [Test]
    public async Task DeletingMachineBeforeFulfillmentRefundsOnce()
    {
        var env = await CreateEnvAsync();
        try
        {
            await JumpToNodeAsync(env, HomeNode);

            await env.Pair.Server.WaitAssertion(() =>
            {
                Assert.That(env.Ui.TryOpenUi(env.BuyConsole, MarketConsoleUiKey.Default, env.Buyer), Is.True);
                AddToCart(env, env.Buyer, 1, ThrusterProduct);
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - ThrusterUnitPriceHome));

                // Delete the committed machine before the opening animation can
                // fulfill the order: the hub must be refunded exactly once.
                env.Ent.DeleteEntity(env.Machine);

                Assert.Multiple(() =>
                {
                    Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));
                    Assert.That(env.Hub.LifetimePurchases, Is.EqualTo(0));
                });
            });
        }
        finally
        {
            await CleanUpAsync(env);
        }
    }

    [Test]
    public async Task SellCreditsHubAndDeletesOnlyValidRoots()
    {
        var env = await CreateEnvAsync();
        try
        {
            await JumpToNodeAsync(env, HomeNode);
            var server = env.Pair.Server;

            EntityUid ore = default;
            EntityUid blacklistedOre = default;
            EntityUid anchoredOre = default;
            EntityUid mouse = default;
            double expectedPrice = 0;

            await server.WaitAssertion(() =>
            {
                Assert.That(env.Ui.TryOpenUi(env.SellConsole, NsvCargoSellUiKey.Key, env.Seller), Is.True);

                ore = env.Ent.SpawnEntity("GoldOre", env.Coords(4.5f, 0.5f));
                blacklistedOre = env.Ent.SpawnEntity("GoldOre1", env.Coords(4.5f, 0.5f));
                env.Ent.EnsureComponent<CargoSellBlacklistComponent>(blacklistedOre);
                anchoredOre = env.Ent.SpawnEntity("GoldOre1", env.Coords(4.5f, 0.5f));
                env.Xform.AnchorEntity(anchoredOre);
                mouse = env.Ent.SpawnEntity("MobMouse", env.Coords(5.5f, 0.5f));

                expectedPrice = env.Pricing.GetPriceWithVendingDiscount(ore, env.Grid);
                Assert.That(expectedPrice, Is.GreaterThan(0d));

                // Appraisal never modifies the world.
                Appraise(env, env.Seller);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance));
                Assert.That(env.Ent.EntityExists(ore), Is.True);
            });

            await server.WaitAssertion(() =>
            {
                SellAll(env, env.Seller);

                var expectedAmount = (int) Math.Floor(expectedPrice * 1.1);
                Assert.That(expectedAmount, Is.GreaterThan(0));
                Assert.Multiple(() =>
                {
                    Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance + expectedAmount));
                    Assert.That(env.Hub.LifetimeSales, Is.EqualTo(expectedAmount));
                    Assert.That(env.Ent.EntityExists(ore), Is.False, "Sold ore was not deleted.");
                    Assert.That(env.Ent.EntityExists(blacklistedOre), Is.True, "Blacklisted root must be retained.");
                    Assert.That(env.Ent.EntityExists(anchoredOre), Is.True, "Anchored root must be retained.");
                    Assert.That(env.Ent.EntityExists(mouse), Is.True, "Living mobs must be retained.");
                });

                // Selling again with only invalid goods credits nothing.
                SellAll(env, env.Seller);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance + expectedAmount));
            });
        }
        finally
        {
            await CleanUpAsync(env);
        }
    }

    [Test]
    public async Task HubSurvivesJumpsWhileCartsAndMarketlessNodesReset()
    {
        var env = await CreateEnvAsync();
        try
        {
            await JumpToNodeAsync(env, HomeNode);
            var server = env.Pair.Server;

            await server.WaitAssertion(() =>
            {
                Assert.That(env.Ui.TryOpenUi(env.BuyConsole, MarketConsoleUiKey.Default, env.Buyer), Is.True);
                AddToCart(env, env.Buyer, 1, ThrusterProduct);
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - ThrusterUnitPriceHome));
            });

            // Let the delivery complete before jumping.
            await env.Pair.RunSeconds(5);

            // The delivered crate physically blocks the machine until hauled away.
            await server.WaitAssertion(() =>
            {
                foreach (var crate in FindByProto(env, "CrateGenericSteel", env.Grid))
                    env.Ent.DeleteEntity(crate);
            });

            // The hub balance is part of the ship grid and survives the jump.
            await JumpToNodeAsync(env, AsteroidNode);

            await server.WaitAssertion(() =>
            {
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - ThrusterUnitPriceHome));

                // The Asteroid node has no market: cart and purchase requests are rejected.
                AddToCart(env, env.Buyer, 1, ThrusterProduct);
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - ThrusterUnitPriceHome));

                // Sell requests are rejected at marketless nodes as well.
                Assert.That(env.Ui.TryOpenUi(env.SellConsole, NsvCargoSellUiKey.Key, env.Seller), Is.True);
                var ore = env.Ent.SpawnEntity("GoldOre", env.Coords(4.5f, 0.5f));
                SellAll(env, env.Seller);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - ThrusterUnitPriceHome));
                Assert.That(env.Ent.EntityExists(ore), Is.True, "Goods must not be sold at a marketless node.");
                env.Ent.DeleteEntity(ore);
            });

            // Back at Home the surviving hub can trade again.
            await JumpToNodeAsync(env, HomeNode);

            await server.WaitAssertion(() =>
            {
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - ThrusterUnitPriceHome));
                AddToCart(env, env.Buyer, 1, ThrusterProduct);
                Purchase(env, env.Buyer);
                Assert.That(env.Hub.Balance, Is.EqualTo(InitialBalance - 2 * ThrusterUnitPriceHome));
            });
        }
        finally
        {
            await CleanUpAsync(env);
        }
    }
}
