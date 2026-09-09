using Content.Client.UserInterface.Fragments;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Client.UserInterface;

namespace Content.Client._NSV.Bluespace.Sectors;

public sealed partial class NsvNavigationUi : UIFragment
{
    private NsvBluespaceNavigationCartridgeUiFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        // Loader states (including PdaUpdateState) re-run Setup on every push; the
        // attach logic early-returns for an already-attached fragment type, so a new
        // instance here would be detached and silently swallow all later UpdateState calls.
        if (_fragment is { Disposed: false })
            return;

        _fragment = new NsvBluespaceNavigationCartridgeUiFragment();
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not NsvBluespaceCartridgeUiState cartridgeState)
            return;

        _fragment?.UpdateState(cartridgeState);
    }
}
