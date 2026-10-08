using Robust.Shared.Serialization;

namespace Content.Shared._NSV.Bluespace.Sectors;

[Serializable, NetSerializable]
public enum NsvBluespaceDriveConsoleUiKey : byte
{
    Key
}

/// <summary>
/// What the CTLA-160 jump console shows about its ship's jump core. <see cref="Status"/> is already
/// localized: "ready", or why a jump is blocked right now (ignoring the destination's fuel cost).
/// </summary>
[Serializable, NetSerializable]
public sealed class NsvBluespaceDriveConsoleState : BoundUserInterfaceState
{
    public readonly bool HasCore;
    public readonly bool CorePowered;
    public readonly bool ConsolePowered;
    public readonly int ChargePercent;
    public readonly float FuelSheets;
    public readonly float FuelCapacitySheets;
    public readonly bool Ready;
    public readonly string Status;

    // The core's current draw in watts: high while charging, low once full.
    public readonly int CoreLoadWatts;

    public NsvBluespaceDriveConsoleState(
        bool hasCore,
        bool corePowered,
        bool consolePowered,
        int chargePercent,
        float fuelSheets,
        float fuelCapacitySheets,
        bool ready,
        string status,
        int coreLoadWatts)
    {
        CoreLoadWatts = coreLoadWatts;
        HasCore = hasCore;
        CorePowered = corePowered;
        ConsolePowered = consolePowered;
        ChargePercent = chargePercent;
        FuelSheets = fuelSheets;
        FuelCapacitySheets = fuelCapacitySheets;
        Ready = ready;
        Status = status;
    }
}
