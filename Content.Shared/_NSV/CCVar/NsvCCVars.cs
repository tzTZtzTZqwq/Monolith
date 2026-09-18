using Robust.Shared.Configuration;

namespace Content.Shared._NSV.CCVar;

/// <summary>
/// Contains configuration variables used by NSV systems.
/// </summary>
[CVarDefs]
public sealed class NsvCCVars
{
    /// <summary>
    /// Display-only soft capacity for all persistent bluespace sectors.
    /// This must not be used to reject sector creation or access.
    /// </summary>
    public static readonly CVarDef<int> BluespaceSectorTotalSoftCapacity =
        CVarDef.Create("nsv.bluespace.sectors.total_soft_capacity", 100, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    /// Display-only soft capacity for active persistent bluespace sectors.
    /// This must not be used to reject sector creation, wake, or travel.
    /// </summary>
    public static readonly CVarDef<int> BluespaceSectorActiveSoftCapacity =
        CVarDef.Create("nsv.bluespace.sectors.active_soft_capacity", 15, CVar.SERVERONLY | CVar.ARCHIVE);
}
