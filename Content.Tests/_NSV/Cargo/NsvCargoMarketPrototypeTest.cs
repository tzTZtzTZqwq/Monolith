using System.Collections.Generic;
using System.IO;
using Content.Shared._NSV.Cargo;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Whitelist;
using NUnit.Framework;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.UnitTesting;

namespace Content.Tests._NSV.Cargo;

[TestFixture]
public sealed class NsvCargoMarketPrototypeTest : RobustUnitTest
{
    [Test]
    public void AcceptsValidOrderedSchema()
    {
        var market = new NsvCargoMarketPrototype
        {
            Buy = new NsvCargoMarketBuyDefinition
            {
                Offers =
                {
                    new NsvCargoMarketBuyOfferDefinition
                    {
                        Product = "EmergencyFire",
                        MaxPerTransaction = 5
                    },
                    new NsvCargoMarketBuyOfferDefinition
                    {
                        Product = "EngineeringRCD",
                        Multiplier = 1.5f,
                        MaxPerTransaction = 2
                    }
                }
            },
            Sell = new NsvCargoMarketSellDefinition
            {
                Rules =
                {
                    new NsvCargoMarketSellRuleDefinition
                    {
                        Whitelist = new EntityWhitelist
                        {
                            Tags = new() { "Ore" }
                        },
                        Multiplier = 1.2f
                    },
                    new NsvCargoMarketSellRuleDefinition
                    {
                        Whitelist = new EntityWhitelist
                        {
                            Components = new[] { "Gun" }
                        },
                        Multiplier = 0.8f
                    }
                }
            }
        };

        Deserialize(market);

        Assert.Multiple(() =>
        {
            Assert.That(market.Buy.DefaultMultiplier, Is.EqualTo(1f));
            Assert.That(market.Buy.Offers[0].Product, Is.EqualTo((ProtoId<CargoProductPrototype>) "EmergencyFire"));
            Assert.That(market.Buy.Offers[0].Multiplier, Is.EqualTo(1f));
            Assert.That(market.Buy.Offers[1].Product, Is.EqualTo((ProtoId<CargoProductPrototype>) "EngineeringRCD"));
            Assert.That(market.Sell.DefaultMultiplier, Is.Null);
            Assert.That(market.Sell.Rules[0].Whitelist.Tags, Is.Not.Null);
            Assert.That(market.Sell.Rules[1].Whitelist.Components, Is.EqualTo(new[] { "Gun" }));
        });
    }

    [TestCaseSource(nameof(InvalidMultipliers))]
    public void RejectsInvalidBuyDefaultMultiplier(float multiplier)
    {
        var market = CreateValidMarket();
        market.Buy!.DefaultMultiplier = multiplier;

        Assert.Throws<InvalidDataException>(() => Deserialize(market));
    }

    [TestCaseSource(nameof(InvalidMultipliers))]
    public void RejectsInvalidBuyOfferMultiplier(float multiplier)
    {
        var market = CreateValidMarket();
        market.Buy!.Offers[0].Multiplier = multiplier;

        Assert.Throws<InvalidDataException>(() => Deserialize(market));
    }

    [TestCaseSource(nameof(InvalidMultipliers))]
    public void RejectsInvalidSellDefaultMultiplier(float multiplier)
    {
        var market = CreateValidMarket();
        market.Sell!.DefaultMultiplier = multiplier;

        Assert.Throws<InvalidDataException>(() => Deserialize(market));
    }

    [TestCaseSource(nameof(InvalidMultipliers))]
    public void RejectsInvalidSellRuleMultiplier(float multiplier)
    {
        var market = CreateValidMarket();
        market.Sell!.Rules[0].Multiplier = multiplier;

        Assert.Throws<InvalidDataException>(() => Deserialize(market));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(NsvCargoMarketPrototype.MaxPerTransactionLimit + 1)]
    public void RejectsInvalidMaxPerTransaction(int maximum)
    {
        var market = CreateValidMarket();
        market.Buy!.Offers[0].MaxPerTransaction = maximum;

        Assert.Throws<InvalidDataException>(() => Deserialize(market));
    }

    [Test]
    public void RejectsDuplicateOfferProducts()
    {
        var market = CreateValidMarket();
        market.Buy!.Offers.Add(new NsvCargoMarketBuyOfferDefinition
        {
            Product = market.Buy.Offers[0].Product,
            MaxPerTransaction = 1
        });

        Assert.Throws<InvalidDataException>(() => Deserialize(market));
    }

    [Test]
    public void RejectsEmptySellRuleWhitelist()
    {
        var market = CreateValidMarket();
        market.Sell!.Rules[0].Whitelist = new EntityWhitelist();

        Assert.Throws<InvalidDataException>(() => Deserialize(market));
    }

    [Test]
    public void AcceptsNullSellDefaultMultiplier()
    {
        var market = CreateValidMarket();
        market.Sell!.DefaultMultiplier = null;

        Assert.DoesNotThrow(() => Deserialize(market));
    }

    private static NsvCargoMarketPrototype CreateValidMarket()
    {
        return new NsvCargoMarketPrototype
        {
            Buy = new NsvCargoMarketBuyDefinition
            {
                Offers =
                {
                    new NsvCargoMarketBuyOfferDefinition
                    {
                        Product = "EmergencyFire",
                        MaxPerTransaction = 1
                    }
                }
            },
            Sell = new NsvCargoMarketSellDefinition
            {
                Rules =
                {
                    new NsvCargoMarketSellRuleDefinition
                    {
                        Whitelist = new EntityWhitelist
                        {
                            Components = new[] { "Gun" }
                        }
                    }
                }
            }
        };
    }

    private static IEnumerable<float> InvalidMultipliers()
    {
        yield return float.NaN;
        yield return float.PositiveInfinity;
        yield return float.NegativeInfinity;
        yield return 0f;
        yield return -1f;
    }

    private static void Deserialize(NsvCargoMarketPrototype market)
    {
        ((ISerializationHooks) market).AfterDeserialization();
    }
}
