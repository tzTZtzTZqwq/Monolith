using Robust.Shared.GameObjects;
using Robust.Shared.ViewVariables;

namespace Content.Server._NSV.Bluespace.Strategy;

/// <summary>
/// Marks a grid as belonging to a strategic fleet and eligible for serialization.
/// Carries the identity binding assigned by <see cref="NsvFleetRegistrySystem"/>.
/// Presence declares capability, not permission: whether the grid may actually be
/// serialized is decided by runtime gates, not by this component alone.
/// </summary>
[RegisterComponent]
public sealed partial class NsvSerializableComponent : Component
{
    [ViewVariables]
    public string FleetId = string.Empty;

    [ViewVariables]
    public string ShipId = string.Empty;

    [ViewVariables]
    public ulong BindingGeneration;

    /// <summary>
    /// Floor count at 100% integrity, fixed when the grid is first serialized (P3).
    /// P1 leaves this at 0.
    /// </summary>
    [ViewVariables]
    public int TargetFloorCount;

    [ViewVariables]
    public string? RepresentationFailure;
}
