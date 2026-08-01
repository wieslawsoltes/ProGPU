using Microsoft.UI.Dispatching;
using ProGPU.Backend;
using ProGPU.WinUI.Platform;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics;

namespace Microsoft.UI.Windowing;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class AppWindowChangedEventArgs
{
    internal AppWindowChangedEventArgs(
        bool position,
        bool presenter,
        bool size,
        bool visibility,
        bool zOrder,
        bool atBottom,
        bool atTop,
        WindowId belowWindowId)
    {
        DidPositionChange = position;
        DidPresenterChange = presenter;
        DidSizeChange = size;
        DidVisibilityChange = visibility;
        DidZOrderChange = zOrder;
        IsZOrderAtBottom = atBottom;
        IsZOrderAtTop = atTop;
        ZOrderBelowWindowId = belowWindowId;
    }

    public bool DidPositionChange { get; }

    public bool DidPresenterChange { get; }

    public bool DidSizeChange { get; }

    public bool DidVisibilityChange { get; }

    public bool DidZOrderChange { get; }

    public bool IsZOrderAtBottom { get; }

    public bool IsZOrderAtTop { get; }

    public WindowId ZOrderBelowWindowId { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class AppWindowClosingEventArgs
{
    internal AppWindowClosingEventArgs()
    {
    }

    public bool Cancel { get; set; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class AppWindow
{
    private static readonly object RegistrySync = new();
    private static readonly Dictionary<WindowId, AppWindow> Registry = [];
    private static long s_nextWindowId;

    private readonly Microsoft.UI.Xaml.Window _window;
    private AppWindowPresenter _presenter;
    private DispatcherQueue _dispatcherQueue;
    private PointInt32 _position;
    private AppWindow? _disabledModalOwner;
    private int _modalChildCount;
    private bool _destroyed;
    private bool _canCancelClose = true;
    private bool _isVisible;
    private bool _showOnce;

    private AppWindow(
        AppWindowPresenter presenter,
        WindowId ownerWindowId,
        DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _presenter = presenter;
        _dispatcherQueue = dispatcherQueue;
        OwnerWindowId = ownerWindowId;
        Id = new WindowId(
            unchecked((ulong)Interlocked.Increment(
                ref s_nextWindowId)));
        _window = new Microsoft.UI.Xaml.Window();
        if (ownerWindowId.Value != 0)
        {
            lock (RegistrySync)
            {
                if (Registry.TryGetValue(
                        ownerWindowId,
                        out AppWindow? owner))
                {
                    _window.Owner = owner._window;
                }
            }
        }
        TitleBar = new AppWindowTitleBar(this);
        _presenter.ConfigurationChanged += OnPresenterConfigurationChanged;
        _dispatcherQueue.ShutdownStarting += OnDispatcherQueueShutdownStarting;
        _window.SizeChanged += OnWindowSizeChanged;
        _window.VisibilityChanged += OnWindowVisibilityChanged;
        _window.PositionChanged += OnWindowPositionChanged;
        _window.ClosingRequested += OnWindowClosingRequested;
        _window.Closed += OnWindowClosed;
        lock (RegistrySync)
            Registry.Add(Id, this);
        ApplyPresenter();
    }

    public DispatcherQueue DispatcherQueue => _dispatcherQueue;

    public WindowId Id { get; }

    public bool IsShownInSwitchers
    {
        get => _window.ShowInTaskbar;
        set
        {
            VerifyAccess();
            _window.ShowInTaskbar = value;
        }
    }

    public bool IsVisible => _isVisible;

    public WindowId OwnerWindowId { get; }

    public PointInt32 Position => _position;

    public AppWindowPresenter Presenter => _presenter;

    public SizeInt32 Size =>
        new(_window.Width, _window.Height);

    public SizeInt32 ClientSize
    {
        get
        {
            NativeWindowFrameInsets insets = FrameInsets;
            return new SizeInt32(
                Math.Max(0, _window.Width - insets.Left - insets.Right),
                Math.Max(0, _window.Height - insets.Top - insets.Bottom));
        }
    }

    public string Title
    {
        get => _window.Title;
        set
        {
            VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);
            _window.Title = value;
        }
    }

    public AppWindowTitleBar TitleBar { get; }

    public event TypedEventHandler<AppWindow, AppWindowChangedEventArgs>?
        Changed;

    public event TypedEventHandler<AppWindow, AppWindowClosingEventArgs>?
        Closing;

    public event TypedEventHandler<AppWindow, object>? Destroying;

    public static AppWindow Create() =>
        Create(
            OverlappedPresenter.Create(),
            default,
            RequireCurrentDispatcherQueue());

    public static AppWindow Create(
        AppWindowPresenter appWindowPresenter) =>
        Create(
            appWindowPresenter,
            default,
            RequireCurrentDispatcherQueue());

    public static AppWindow Create(
        AppWindowPresenter appWindowPresenter,
        WindowId ownerWindowId) =>
        Create(
            appWindowPresenter,
            ownerWindowId,
            RequireCurrentDispatcherQueue());

    public static AppWindow Create(
        AppWindowPresenter appWindowPresenter,
        WindowId ownerWindowId,
        DispatcherQueue DispatcherQueue) =>
        new(
            appWindowPresenter,
            ownerWindowId,
            DispatcherQueue);

    public static AppWindow GetFromWindowId(WindowId windowId)
    {
        lock (RegistrySync)
            return Registry.GetValueOrDefault(windowId)!;
    }

    public void AssociateWithDispatcherQueue(
        DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        VerifyAccess();
        if (_isVisible)
        {
            throw new InvalidOperationException(
                "The DispatcherQueue cannot change after the AppWindow is shown.");
        }

        _dispatcherQueue.ShutdownStarting -=
            OnDispatcherQueueShutdownStarting;
        _dispatcherQueue = dispatcherQueue;
        _dispatcherQueue.ShutdownStarting +=
            OnDispatcherQueueShutdownStarting;
    }

    public void Destroy()
    {
        VerifyAccess();
        DestroyCore(canCancel: true);
    }

    public void Hide()
    {
        VerifyAccess();
        ThrowIfDestroyed();
        _window.Hide();
        UpdateVisibility(false);
    }

    public void Move(PointInt32 position)
    {
        VerifyAccess();
        ThrowIfDestroyed();
        if (_position == position)
            return;

        _window.SetPosition(position);
    }

    public void MoveAndResize(RectInt32 rect)
    {
        ValidateRect(rect);
        Move(new PointInt32(rect.X, rect.Y));
        Resize(new SizeInt32(rect.Width, rect.Height));
    }

    public void MoveAndResize(
        RectInt32 rect,
        DisplayArea displayarea)
    {
        ArgumentNullException.ThrowIfNull(displayarea);
        ValidateRect(rect);
        RectInt32 workArea = displayarea.WorkArea;
        MoveAndResize(
            new RectInt32(
                checked(workArea.X + rect.X),
                checked(workArea.Y + rect.Y),
                rect.Width,
                rect.Height));
    }

    public void MoveInZOrderAtBottom() =>
        MoveInZOrder(default, atTop: false, atBottom: true);

    public void MoveInZOrderAtTop() =>
        MoveInZOrder(default, atTop: true, atBottom: false);

    public void MoveInZOrderBelow(WindowId windowId) =>
        MoveInZOrder(windowId, atTop: false, atBottom: false);

    public void Resize(SizeInt32 size)
    {
        VerifyAccess();
        ThrowIfDestroyed();
        ValidateSize(size);
        if (Size == size)
            return;
        _window.Width = size.Width;
        _window.Height = size.Height;
        RaiseChanged(size: true);
    }

    public void ResizeClient(SizeInt32 size)
    {
        ValidateSize(size);
        NativeWindowFrameInsets insets = FrameInsets;
        Resize(
            new SizeInt32(
                checked(size.Width + insets.Left + insets.Right),
                checked(size.Height + insets.Top + insets.Bottom)));
    }

    public void SetIcon(IconId iconId) =>
        RequirePlatformOperation(
            provider => provider.TrySetIcon(Id, iconId),
            "window icon");

    public void SetIcon(string iconPath)
    {
        ValidatePath(iconPath);
        RequirePlatformOperation(
            provider => provider.TrySetIcon(Id, iconPath),
            "window icon");
    }

    public void SetPresenter(
        AppWindowPresenter appWindowPresenter)
    {
        ArgumentNullException.ThrowIfNull(appWindowPresenter);
        VerifyAccess();
        ThrowIfDestroyed();
        if (ReferenceEquals(_presenter, appWindowPresenter))
            return;

        _presenter.ConfigurationChanged -=
            OnPresenterConfigurationChanged;
        _presenter = appWindowPresenter;
        _presenter.ConfigurationChanged +=
            OnPresenterConfigurationChanged;
        ApplyPresenter();
        RaiseChanged(presenter: true);
    }

    public void SetPresenter(
        AppWindowPresenterKind appWindowPresenterKind) =>
        SetPresenter(
            appWindowPresenterKind switch
            {
                AppWindowPresenterKind.CompactOverlay =>
                    CompactOverlayPresenter.Create(),
                AppWindowPresenterKind.FullScreen =>
                    FullScreenPresenter.Create(),
                AppWindowPresenterKind.Overlapped or
                    AppWindowPresenterKind.Default =>
                    OverlappedPresenter.Create(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(appWindowPresenterKind))
            });

    public void SetTaskbarIcon(IconId iconId) =>
        RequirePlatformOperation(
            provider => provider.TrySetTaskbarIcon(Id, iconId),
            "taskbar icon");

    public void SetTaskbarIcon(string iconPath)
    {
        ValidatePath(iconPath);
        RequirePlatformOperation(
            provider => provider.TrySetTaskbarIcon(Id, iconPath),
            "taskbar icon");
    }

    public void SetTitleBarIcon(IconId iconId) =>
        RequirePlatformOperation(
            provider => provider.TrySetTitleBarIcon(Id, iconId),
            "title-bar icon");

    public void SetTitleBarIcon(string iconPath)
    {
        ValidatePath(iconPath);
        RequirePlatformOperation(
            provider => provider.TrySetTitleBarIcon(Id, iconPath),
            "title-bar icon");
    }

    public void Show() => Show(activateWindow: true);

    public void Show(bool activateWindow)
    {
        VerifyAccess();
        ThrowIfDestroyed();
        _window.Activate(activateWindow);
        UpdateVisibility(true);
    }

    public void ShowOnceWithRequestedStartupState()
    {
        VerifyAccess();
        if (_showOnce)
        {
            throw new InvalidOperationException(
                "ShowOnceWithRequestedStartupState can only be called once.");
        }

        _showOnce = true;
        if (_presenter is OverlappedPresenter overlappedPresenter)
            overlappedPresenter.ApplyRequestedStartupState();
        ApplyPresenter();
        Show();
    }

    internal Microsoft.UI.Xaml.Window XamlWindow => _window;

    internal NativeWindowFrameInsets FrameInsets => _window.FrameInsets;

    internal void VerifyAccess()
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "This operation must run on the AppWindow DispatcherQueue.");
        }
    }

    internal void NotifyTitleBarChanged()
    {
        VerifyAccess();
    }

    private static DispatcherQueue RequireCurrentDispatcherQueue() =>
        DispatcherQueue.GetForCurrentThread() ??
        throw new InvalidOperationException(
            "The current thread does not own a DispatcherQueue.");

    private void ApplyPresenter()
    {
        VerifyAccess();
        switch (_presenter)
        {
            case OverlappedPresenter overlapped:
                _window.Decorations = overlapped.HasTitleBar
                    ? NativeWindowDecorations.Full
                    : overlapped.HasBorder
                        ? NativeWindowDecorations.BorderOnly
                        : NativeWindowDecorations.None;
                _window.CanResize = overlapped.IsResizable;
                _window.CanMinimize = overlapped.IsMinimizable;
                _window.CanMaximize = overlapped.IsMaximizable;
                _window.TopMost = overlapped.IsAlwaysOnTop;
                ApplyModalOwner(overlapped.IsModal);
                _window.NativeWindowController?.SetSizeConstraints(
                    new NativeWindowSize(
                        overlapped.PreferredMinimumWidth ?? 0,
                        overlapped.PreferredMinimumHeight ?? 0),
                    new NativeWindowSize(
                        overlapped.PreferredMaximumWidth ?? int.MaxValue,
                        overlapped.PreferredMaximumHeight ?? int.MaxValue));
                ApplyOverlappedState(overlapped.State);
                break;
            case CompactOverlayPresenter:
                ApplyModalOwner(isModal: false);
                _window.NativeWindowState =
                    Silk.NET.Windowing.WindowState.Normal;
                _window.Decorations = NativeWindowDecorations.BorderOnly;
                _window.TopMost = true;
                break;
            case FullScreenPresenter:
                ApplyModalOwner(isModal: false);
                _window.NativeWindowState =
                    Silk.NET.Windowing.WindowState.Fullscreen;
                break;
        }
    }

    private void ApplyOverlappedState(
        OverlappedPresenterState state)
    {
        _window.NativeWindowState = state switch
        {
            OverlappedPresenterState.Maximized =>
                Silk.NET.Windowing.WindowState.Maximized,
            OverlappedPresenterState.Minimized =>
                Silk.NET.Windowing.WindowState.Minimized,
            _ => Silk.NET.Windowing.WindowState.Normal
        };
    }

    private void OnPresenterConfigurationChanged()
    {
        ApplyPresenter();
        RaiseChanged(presenter: true);
    }

    private void OnWindowSizeChanged(
        object sender,
        Microsoft.UI.Xaml.WindowSizeChangedEventArgs args) =>
        RaiseChanged(size: true);

    private void OnWindowVisibilityChanged(
        object sender,
        Microsoft.UI.Xaml.WindowVisibilityChangedEventArgs args) =>
        UpdateVisibility(args.Visible);

    private void OnWindowPositionChanged(PointInt32 position)
    {
        if (_position == position)
            return;
        _position = position;
        RaiseChanged(position: true);
    }

    private void OnWindowClosed(
        object sender,
        Microsoft.UI.Xaml.WindowEventArgs args) =>
        CompleteDestroy();

    private void OnDispatcherQueueShutdownStarting(
        DispatcherQueue sender,
        DispatcherQueueShutdownStartingEventArgs args) =>
        DestroyCore(canCancel: false);

    private void UpdateVisibility(bool visible)
    {
        if (_isVisible == visible)
            return;
        _isVisible = visible;
        RaiseChanged(visibility: true);
    }

    private void CompleteDestroy()
    {
        if (_destroyed)
            return;
        _destroyed = true;
        _isVisible = false;
        ApplyModalOwner(isModal: false);
        _dispatcherQueue.ShutdownStarting -=
            OnDispatcherQueueShutdownStarting;
        _presenter.ConfigurationChanged -=
            OnPresenterConfigurationChanged;
        _window.PositionChanged -= OnWindowPositionChanged;
        _window.ClosingRequested -= OnWindowClosingRequested;
        lock (RegistrySync)
            Registry.Remove(Id);
        Destroying?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyModalOwner(bool isModal)
    {
        _window.IsEnabled = _modalChildCount == 0;
        AppWindow? owner = null;
        if (isModal && OwnerWindowId.Value != 0)
        {
            AppWindow? candidate = GetFromWindowId(OwnerWindowId);
            if (candidate is not null && !ReferenceEquals(candidate, this))
                owner = candidate;
        }

        if (ReferenceEquals(owner, _disabledModalOwner))
            return;

        _disabledModalOwner?.ReleaseModalChild();
        _disabledModalOwner = owner;
        owner?.AddModalChild();
    }

    private void AddModalChild()
    {
        _modalChildCount = checked(_modalChildCount + 1);
        _window.IsEnabled = false;
    }

    private void ReleaseModalChild()
    {
        if (_modalChildCount == 0)
            return;

        _modalChildCount--;
        if (_modalChildCount == 0)
            _window.IsEnabled = true;
    }

    private void DestroyCore(bool canCancel)
    {
        if (_destroyed)
            return;

        _canCancelClose = canCancel;
        try
        {
            if (_window.TryClose())
                CompleteDestroy();
        }
        finally
        {
            _canCancelClose = true;
        }
    }

    private bool OnWindowClosingRequested()
    {
        var args = new AppWindowClosingEventArgs();
        Closing?.Invoke(this, args);
        return !_canCancelClose || !args.Cancel;
    }

    private void MoveInZOrder(
        WindowId belowWindowId,
        bool atTop,
        bool atBottom)
    {
        VerifyAccess();
        ThrowIfDestroyed();
        RequirePlatformOperation(
            provider => provider.TryMoveInZOrder(
                Id,
                belowWindowId,
                atTop,
                atBottom),
            "window Z-order");
        RaiseChanged(
            zOrder: true,
            atTop: atTop,
            atBottom: atBottom,
            belowWindowId: belowWindowId);
    }

    private void RequirePlatformOperation(
        Func<IAppWindowPlatformProvider, bool> operation,
        string operationName)
    {
        VerifyAccess();
        ThrowIfDestroyed();
        IAppWindowPlatformProvider provider =
            WindowingPlatformServices.AppWindows ??
            throw new PlatformNotSupportedException(
                $"The current ProGPU host does not support {operationName}.");
        if (!operation(provider))
        {
            throw new InvalidOperationException(
                $"The platform rejected the {operationName} operation.");
        }
    }

    private void RaiseChanged(
        bool position = false,
        bool presenter = false,
        bool size = false,
        bool visibility = false,
        bool zOrder = false,
        bool atBottom = false,
        bool atTop = false,
        WindowId belowWindowId = default)
    {
        Changed?.Invoke(
            this,
            new AppWindowChangedEventArgs(
                position,
                presenter,
                size,
                visibility,
                zOrder,
                atBottom,
                atTop,
                belowWindowId));
    }

    private void ThrowIfDestroyed() =>
        ObjectDisposedException.ThrowIf(_destroyed, this);

    private static void ValidateSize(SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
    }

    private static void ValidateRect(RectInt32 rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(rect));
    }

    private static void ValidatePath(string iconPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);
    }
}
