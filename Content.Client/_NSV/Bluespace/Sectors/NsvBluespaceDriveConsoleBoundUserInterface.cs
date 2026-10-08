using Content.Shared._NSV.Bluespace.Sectors;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._NSV.Bluespace.Sectors;

/// <summary>
/// The CTLA-160 jump console window: the paired jump core's charge, plasma and power, and whether the
/// ship can jump right now.
/// </summary>
[UsedImplicitly]
public sealed class NsvBluespaceDriveConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NsvBluespaceDriveConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NsvBluespaceDriveConsoleWindow>();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NsvBluespaceDriveConsoleState driveState)
            _window?.SetState(driveState);
    }
}

public sealed class NsvBluespaceDriveConsoleWindow : DefaultWindow
{
    private static readonly Color Good = Color.FromHex("#00ff2a");
    private static readonly Color Bad = Color.FromHex("#ff6b4a");

    private readonly ProgressBar _charge;
    private readonly Label _chargeValue;
    private readonly Label _fuelValue;
    private readonly Label _corePowerValue;
    private readonly Label _consolePowerValue;
    private readonly Label _status;

    public NsvBluespaceDriveConsoleWindow()
    {
        Title = Loc.GetString("nsv-bluespace-drive-console-title");
        MinSize = new Vector2i(380, 0);

        _charge = new ProgressBar { MinValue = 0, MaxValue = 100, MinHeight = 18, HorizontalExpand = true };
        _chargeValue = new Label();
        _fuelValue = new Label();
        _corePowerValue = new Label();
        _consolePowerValue = new Label();
        _status = new Label { Margin = new Thickness(0, 8, 0, 0) };

        var grid = new GridContainer { Columns = 2, HorizontalExpand = true };
        AddRow(grid, "nsv-bluespace-drive-console-charge", _chargeValue);
        AddRow(grid, "nsv-bluespace-drive-console-fuel", _fuelValue);
        AddRow(grid, "nsv-bluespace-drive-console-core-power", _corePowerValue);
        AddRow(grid, "nsv-bluespace-drive-console-console-power", _consolePowerValue);

        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(8) };
        root.AddChild(_charge);
        root.AddChild(grid);
        root.AddChild(_status);
        Contents.AddChild(root);
    }

    public void SetState(NsvBluespaceDriveConsoleState state)
    {
        _charge.Value = state.HasCore ? state.ChargePercent : 0;
        _chargeValue.Text = state.HasCore ? $"{state.ChargePercent}%" : Loc.GetString("nsv-bluespace-drive-console-no-core");
        _fuelValue.Text = state.HasCore
            ? Loc.GetString("nsv-bluespace-drive-console-fuel-value",
                ("fuel", state.FuelSheets), ("capacity", state.FuelCapacitySheets))
            : "—";
        SetPower(_corePowerValue, state.HasCore && state.CorePowered);
        SetPower(_consolePowerValue, state.ConsolePowered);
        _status.Text = state.Status;
        _status.FontColorOverride = state.Ready ? Good : Bad;
    }

    private static void SetPower(Label label, bool powered)
    {
        label.Text = Loc.GetString(powered ? "nsv-bluespace-drive-console-power-on" : "nsv-bluespace-drive-console-power-off");
        label.FontColorOverride = powered ? Good : Bad;
    }

    private static void AddRow(GridContainer grid, string captionLoc, Label value)
    {
        grid.AddChild(new Label { Text = Loc.GetString(captionLoc), MinWidth = 150 });
        value.HorizontalExpand = true;
        value.Align = Label.AlignMode.Right;
        grid.AddChild(value);
    }
}
