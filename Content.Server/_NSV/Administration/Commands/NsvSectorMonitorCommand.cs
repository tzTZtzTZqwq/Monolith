using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._NSV.Administration.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class NsvSectorMonitorCommand : IConsoleCommand
{
    public string Command => "nsvsectormonitor";
    public string Description => "Opens the NSV sector monitor.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        var eui = IoCManager.Resolve<EuiManager>();
        eui.OpenEui(new NsvSectorMonitorEui(), player);
    }
}
