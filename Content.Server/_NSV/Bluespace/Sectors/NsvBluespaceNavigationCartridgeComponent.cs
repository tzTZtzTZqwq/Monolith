using Robust.Shared.GameObjects;

namespace Content.Server._NSV.Bluespace.Sectors;

/// <summary>
/// Marker for the NSV bluespace navigation PDA program; the paired system
/// resolves the loader's sector and pushes read-only snapshots to it.
/// </summary>
[RegisterComponent]
public sealed partial class NsvBluespaceNavigationCartridgeComponent : Component
{
}
