namespace Content.Server._NSV.NPC;

/// <summary>
/// Canonical blackboard key names for <see cref="NsvShipAiComponent.Blackboard"/>.
/// Always use these constants instead of string literals: a typo in a key fails silently
/// (returns default / creates an orphan entry), which the compiler cannot catch.
/// </summary>
public static class NsvAiKeys
{
    /// <summary>float: last measured shield stress, 0..1.</summary>
    public const string ShieldStress = "ShieldStress";

    /// <summary>float: longest weapon range in meters; 0 = no scannable weapons.</summary>
    public const string WeaponRange = "WeaponRange";

    /// <summary>bool: currently withdrawing from threats due to shield stress.</summary>
    public const string Withdrawing = "Withdrawing";
}
