using Content.Shared._NSV.Cargo;
using JetBrains.Annotations;

namespace Content.Client._NSV.Cargo;

[UsedImplicitly]
public sealed class NsvCargoSellConsoleBoundUserInterface : BoundUserInterface
{
    private NsvCargoSellConsoleWindow? _window;

    public NsvCargoSellConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = new NsvCargoSellConsoleWindow();
        _window.AppraiseRequested += () => SendMessage(new NsvCargoSellAppraiseMessage());
        _window.SellRequested += () => SendMessage(new NsvCargoSellRequestMessage());
        _window.OnClose += Close;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not NsvCargoSellInterfaceState uiState)
            return;

        _window?.UpdateState(
            uiState.Balance,
            uiState.Appraisal,
            uiState.Count,
            uiState.MarketName,
            uiState.Enabled);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        _window?.Dispose();
    }
}
