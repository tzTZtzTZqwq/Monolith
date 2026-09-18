using Content.Client.Eui;
using Content.Shared._NSV.Administration;
using Content.Shared.Eui;
using JetBrains.Annotations;
using static Content.Shared._NSV.Administration.NsvSectorMonitorEuiMsg;

namespace Content.Client._NSV.Administration.UI;

[UsedImplicitly]
public sealed class NsvSectorMonitorEui : BaseEui
{
    private readonly NsvSectorMonitorWindow _window;

    public NsvSectorMonitorEui()
    {
        _window = new NsvSectorMonitorWindow();
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.RefreshButton.OnPressed += _ => RequestRefresh();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is NsvSectorMonitorEuiState monitorState)
            _window.SetState(monitorState);
    }

    public override void Opened()
    {
        base.Opened();
        _window.OpenCentered();
        RequestRefresh();
    }

    public override void Closed()
    {
        base.Closed();
        _window.Dispose();
    }

    private void RequestRefresh()
    {
        SendMessage(new RefreshRequest());
    }
}
