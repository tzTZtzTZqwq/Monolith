using Robust.Shared.Serialization;

namespace Content.Shared._NSV.Cargo;

[NetSerializable, Serializable]
public enum NsvCargoSellUiKey : byte
{
    Key
}

/// <summary>
/// Requests a refreshed, read-only appraisal of goods on nearby sell pallets.
/// </summary>
[Serializable, NetSerializable]
public sealed class NsvCargoSellAppraiseMessage : BoundUserInterfaceMessage
{
}

/// <summary>
/// Requests that the server sell all valid goods on nearby sell pallets.
/// The server re-collects and re-prices everything; this message carries no data.
/// </summary>
[Serializable, NetSerializable]
public sealed class NsvCargoSellRequestMessage : BoundUserInterfaceMessage
{
}

[NetSerializable, Serializable]
public sealed class NsvCargoSellInterfaceState : BoundUserInterfaceState
{
    /// <summary>
    /// Current NSV cargo hub balance for the ship.
    /// </summary>
    public int Balance;

    /// <summary>
    /// Server-appraised value of all sellable goods on nearby pallets.
    /// </summary>
    public int Appraisal;

    /// <summary>
    /// Number of root entities that would be sold.
    /// </summary>
    public int Count;

    /// <summary>
    /// Localized name of the market currently trading, or empty if unavailable.
    /// </summary>
    public string MarketName;

    public bool Enabled;

    public NsvCargoSellInterfaceState(int balance, int appraisal, int count, string marketName, bool enabled)
    {
        Balance = balance;
        Appraisal = appraisal;
        Count = count;
        MarketName = marketName;
        Enabled = enabled;
    }
}
