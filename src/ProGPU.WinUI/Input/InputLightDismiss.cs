using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class InputLightDismissEventArgs
{
    internal InputLightDismissEventArgs()
    {
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class InputLightDismissAction :
    InputObject
{
    private static readonly ConditionalWeakTable<
        AppWindow,
        InputLightDismissAction> s_windowActions =
        new();

    private AppWindow? _appWindow;
    private bool _wasActivated;

    private InputLightDismissAction(
        AppWindow appWindow)
        : base(appWindow.DispatcherQueue)
    {
        _appWindow = appWindow;
        _wasActivated =
            appWindow.XamlWindow.ActivationState !=
            WindowActivationState.Deactivated;
        appWindow.XamlWindow.Activated +=
            OnWindowActivated;
        appWindow.Destroying +=
            OnAppWindowDestroying;
    }

    public event TypedEventHandler<
        InputLightDismissAction,
        InputLightDismissEventArgs>?
        Dismissed;

    public static InputLightDismissAction
        GetForWindowId(
            WindowId windowId)
    {
        if (windowId.Value == 0)
            return null!;
        AppWindow? appWindow =
            AppWindow.GetFromWindowId(windowId);
        if (appWindow is null ||
            !appWindow.DispatcherQueue.HasThreadAccess)
        {
            return null!;
        }

        return s_windowActions.GetValue(
            appWindow,
            static window =>
                new InputLightDismissAction(window));
    }

    internal static bool TryRaiseForWindow(
        AppWindow appWindow)
    {
        if (!appWindow.DispatcherQueue.HasThreadAccess ||
            !s_windowActions.TryGetValue(
                appWindow,
                out InputLightDismissAction? action))
        {
            return false;
        }

        action.RaiseDismissed();
        return true;
    }

    private void OnWindowActivated(
        object sender,
        WindowActivatedEventArgs args)
    {
        bool isActivated =
            args.WindowActivationState !=
            WindowActivationState.Deactivated;
        if (_wasActivated && !isActivated)
            RaiseDismissed();
        _wasActivated = isActivated;
    }

    private void OnAppWindowDestroying(
        AppWindow sender,
        object args) =>
        Detach(sender);

    private void RaiseDismissed()
    {
        TypedEventHandler<
            InputLightDismissAction,
            InputLightDismissEventArgs>? handler =
            Dismissed;
        if (handler is not null)
        {
            handler(
                this,
                new InputLightDismissEventArgs());
        }
    }

    private void Detach(
        AppWindow appWindow)
    {
        if (!ReferenceEquals(_appWindow, appWindow))
            return;
        appWindow.XamlWindow.Activated -=
            OnWindowActivated;
        appWindow.Destroying -=
            OnAppWindowDestroying;
        s_windowActions.Remove(appWindow);
        _appWindow = null;
        _wasActivated = false;
        Dismissed = null;
    }
}
