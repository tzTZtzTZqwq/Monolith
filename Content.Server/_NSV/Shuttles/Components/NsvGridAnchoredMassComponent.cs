namespace Content.Server._NSV.Shuttles.Components;

/// <summary>
/// Tracks the summed physics mass of anchored entities (walls, machines, etc.) on this grid.
/// <see cref="NsvGridMassSystem"/> folds this into the grid body's mass via tile fixture density,
/// so a more heavily built ship is physically heavier for FTL, propulsion and mobility.
/// Runtime-only state: recomputed from a full child scan when marked dirty, never serialized.
/// </summary>
[RegisterComponent]
public sealed partial class NsvGridAnchoredMassComponent : Component
{
    /// <summary>
    /// Sum of the <c>FixturesMass</c> of every anchored entity currently on this grid.
    /// Kept as a double so many small contributions accumulate without single-precision drift;
    /// it is cast to float only when written into fixture density.
    /// </summary>
    public double AnchoredMass;
}
