using System.IO;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Whitelist;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NSV.Cargo;

[Prototype("nsvCargoMarket")]
public sealed partial class NsvCargoMarketPrototype : IPrototype, ISerializationHooks
{
    public const int MaxPerTransactionLimit = 1000;

    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField]
    public NsvCargoMarketBuyDefinition? Buy;

    [DataField]
    public NsvCargoMarketSellDefinition? Sell;

    void ISerializationHooks.AfterDeserialization()
    {
        if (Buy != null)
        {
            ValidateMultiplier(Buy.DefaultMultiplier, "buy default");

            var products = new HashSet<ProtoId<CargoProductPrototype>>();
            foreach (var offer in Buy.Offers)
            {
                ValidateMultiplier(offer.Multiplier, $"buy offer '{offer.Product}'");

                if (offer.MaxPerTransaction is <= 0 or > MaxPerTransactionLimit)
                {
                    throw new InvalidDataException(
                        $"Cargo market '{ID}' buy offer '{offer.Product}' maxPerTransaction must be between 1 and {MaxPerTransactionLimit}.");
                }

                if (!products.Add(offer.Product))
                    throw new InvalidDataException($"Cargo market '{ID}' contains duplicate buy offer product '{offer.Product}'.");
            }
        }

        if (Sell == null)
            return;

        if (Sell.DefaultMultiplier is { } defaultMultiplier)
            ValidateMultiplier(defaultMultiplier, "sell default");

        foreach (var rule in Sell.Rules)
        {
            ValidateMultiplier(rule.Multiplier, "sell rule");

            if (!HasCriteria(rule.Whitelist))
                throw new InvalidDataException($"Cargo market '{ID}' contains a sell rule with an empty whitelist.");
        }
    }

    private void ValidateMultiplier(float multiplier, string field)
    {
        if (!float.IsFinite(multiplier) || multiplier <= 0f)
            throw new InvalidDataException($"Cargo market '{ID}' {field} multiplier must be finite and positive.");
    }

    private static bool HasCriteria(EntityWhitelist whitelist)
    {
        return whitelist.Components is { Length: > 0 } ||
               whitelist.MindRoles is { Length: > 0 } ||
               whitelist.Sizes is { Count: > 0 } ||
               whitelist.Tags is { Count: > 0 };
    }
}

[DataDefinition]
public sealed partial class NsvCargoMarketBuyDefinition
{
    [DataField]
    public float DefaultMultiplier = 1f;

    [DataField]
    public List<NsvCargoMarketBuyOfferDefinition> Offers = new();
}

[DataDefinition]
public sealed partial class NsvCargoMarketBuyOfferDefinition
{
    [DataField(required: true)]
    public ProtoId<CargoProductPrototype> Product = string.Empty;

    [DataField]
    public float Multiplier = 1f;

    [DataField(required: true)]
    public int MaxPerTransaction;
}

[DataDefinition]
public sealed partial class NsvCargoMarketSellDefinition
{
    [DataField]
    public float? DefaultMultiplier;

    [DataField]
    public List<NsvCargoMarketSellRuleDefinition> Rules = new();
}

[DataDefinition]
public sealed partial class NsvCargoMarketSellRuleDefinition
{
    [DataField(required: true)]
    public EntityWhitelist Whitelist = new();

    [DataField]
    public float Multiplier = 1f;
}
