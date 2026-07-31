using Microsoft.UI.Content;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010001)]
public sealed class InputActivationListenerActivationChangedEventArgs
{
    internal InputActivationListenerActivationChangedEventArgs()
    {
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010001)]
public sealed class InputActivationListener :
    InputObject
{
    private static readonly ConditionalWeakTable<
        AppWindow,
        InputActivationListener> s_windowListeners =
        new();

    private WindowInputState? _inputState;
    private AppWindow? _appWindow;
    private InputActivationState _state;

    private InputActivationListener(
        ContentIsland island)
        : base(island.DispatcherQueue)
    {
        Attach(island.InputState);
    }

    private InputActivationListener(
        AppWindow appWindow)
        : base(appWindow.DispatcherQueue)
    {
        _appWindow = appWindow;
        _state = MapState(
            appWindow.XamlWindow.ActivationState);
        appWindow.XamlWindow.Activated +=
            OnWindowActivated;
        appWindow.Destroying +=
            OnAppWindowDestroying;
    }

    public InputActivationState State
    {
        get
        {
            VerifyAccess();
            return _state;
        }
    }

    public event TypedEventHandler<
        InputActivationListener,
        InputActivationListenerActivationChangedEventArgs>?
        InputActivationChanged;

    public static InputActivationListener GetForIsland(
        ContentIsland island)
    {
        ArgumentNullException.ThrowIfNull(island);
        if (island.IsClosed ||
            !island.DispatcherQueue.HasThreadAccess)
        {
            return null!;
        }

        return island.ActivationListener ??=
            new InputActivationListener(island);
    }

    public static InputActivationListener GetForWindowId(
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

        return s_windowListeners.GetValue(
            appWindow,
            static window =>
                new InputActivationListener(window));
    }

    internal void Attach(
        WindowInputState? inputState)
    {
        if (ReferenceEquals(_inputState, inputState))
            return;
        if (_inputState is not null)
            _inputState.HostFocusChanged -=
                OnIslandFocusChanged;
        _inputState = inputState;
        if (_inputState is null)
        {
            SetState(InputActivationState.None);
            return;
        }

        _inputState.HostFocusChanged +=
            OnIslandFocusChanged;
        SetState(
            _inputState.HasHostFocus
                ? InputActivationState.Activated
                : InputActivationState.Deactivated);
    }

    internal void Detach()
    {
        if (_inputState is not null)
            _inputState.HostFocusChanged -=
                OnIslandFocusChanged;
        _inputState = null;
        if (_appWindow is not null)
        {
            _appWindow.XamlWindow.Activated -=
                OnWindowActivated;
            _appWindow.Destroying -=
                OnAppWindowDestroying;
        }
        _appWindow = null;
        _state = InputActivationState.None;
        InputActivationChanged = null;
    }

    private void OnIslandFocusChanged(
        bool hasFocus) =>
        SetState(
            hasFocus
                ? InputActivationState.Activated
                : InputActivationState.Deactivated);

    private void OnWindowActivated(
        object sender,
        WindowActivatedEventArgs args) =>
        SetState(MapState(
            args.WindowActivationState));

    private void OnAppWindowDestroying(
        AppWindow sender,
        object args) =>
        Detach();

    private void SetState(
        InputActivationState state)
    {
        if (_state == state)
            return;
        _state = state;
        TypedEventHandler<
            InputActivationListener,
            InputActivationListenerActivationChangedEventArgs>?
            handler = InputActivationChanged;
        if (handler is not null)
        {
            handler(
                this,
                new
                    InputActivationListenerActivationChangedEventArgs());
        }
    }

    private static InputActivationState MapState(
        WindowActivationState state) =>
        state == WindowActivationState.Deactivated
            ? InputActivationState.Deactivated
            : InputActivationState.Activated;
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class InputPreTranslateKeyboardSource :
    InputObject
{
    private ContentIsland? _island;

    private InputPreTranslateKeyboardSource(
        ContentIsland island)
        : base(island.DispatcherQueue)
    {
        _island = island;
    }

    public static InputPreTranslateKeyboardSource
        GetForIsland(
            ContentIsland island)
    {
        ArgumentNullException.ThrowIfNull(island);
        if (island.IsClosed ||
            !island.DispatcherQueue.HasThreadAccess)
        {
            return null!;
        }

        return island.PreTranslateKeyboardSource ??=
            new InputPreTranslateKeyboardSource(
                island);
    }

    internal void Detach() =>
        _island = null;
}
