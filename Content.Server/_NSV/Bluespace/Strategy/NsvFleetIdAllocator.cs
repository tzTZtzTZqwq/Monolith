namespace Content.Server._NSV.Bluespace.Strategy;

/// <summary>
/// Sole source of ship identifiers for a single server run. Ids are monotonic
/// ("ship-N") and never reused; there is no cross-restart persistence. Fleet-level
/// identity and grouping are deferred to the strategy system.
/// </summary>
public sealed class NsvFleetIdAllocator
{
    private ulong _nextShip = 1;

    public string AllocateShipId()
    {
        var id = $"ship-{_nextShip}";
        _nextShip++;
        return id;
    }
}
