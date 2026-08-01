using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;

namespace ProGPU.WinUI.Input;

/// <summary>
/// Delivers a native light-dismiss trigger to an existing action.
/// </summary>
public static class InputLightDismissRegistration
{
    /// <summary>
    /// Notifies the action associated with a top-level window.
    /// </summary>
    /// <remarks>
    /// Hosts use this for Escape, Alt, app-command, hot-key, and
    /// pointer-outside triggers that are not represented by activation.
    /// </remarks>
    public static bool Notify(
        WindowId windowId)
    {
        AppWindow? appWindow =
            AppWindow.GetFromWindowId(windowId);
        if (appWindow is null ||
            !appWindow.DispatcherQueue.HasThreadAccess)
        {
            return false;
        }

        return InputLightDismissAction
            .TryRaiseForWindow(appWindow);
    }
}
