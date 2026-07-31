using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ProGPU.WinUI.Input;

public static class InputActivationRegistration
{
    public static bool NotifyWindowActivation(
        WindowId windowId,
        InputActivationState state)
    {
        AppWindow? appWindow =
            AppWindow.GetFromWindowId(windowId);
        if (appWindow is null ||
            !appWindow.DispatcherQueue.HasThreadAccess)
        {
            return false;
        }

        appWindow.XamlWindow.NotifyHostActivationChanged(
            state switch
            {
                InputActivationState.Activated =>
                    WindowActivationState.CodeActivated,
                InputActivationState.Deactivated =>
                    WindowActivationState.Deactivated,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(state))
            });
        return true;
    }
}
