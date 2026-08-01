using Windows.Foundation.Metadata;

namespace Microsoft.UI.Windowing;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum AppWindowPresenterKind
{
    Default = 0,
    CompactOverlay = 1,
    FullScreen = 2,
    Overlapped = 3
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum CompactOverlaySize
{
    Small = 0,
    Medium = 1,
    Large = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum DisplayAreaFallback
{
    None = 0,
    Primary = 1,
    Nearest = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum DisplayAreaWatcherStatus
{
    Created = 0,
    Started = 1,
    EnumerationCompleted = 2,
    Stopping = 3,
    Stopped = 4,
    Aborted = 5
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum IconShowOptions
{
    ShowIconAndSystemMenu = 0,
    HideIconAndSystemMenu = 1
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum OverlappedPresenterState
{
    Maximized = 0,
    Minimized = 1,
    Restored = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010001)]
public enum TitleBarHeightOption
{
    Standard = 0,
    Tall = 1,
    Collapsed = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010007)]
public enum TitleBarTheme
{
    Legacy = 0,
    UseDefaultAppMode = 1,
    Light = 2,
    Dark = 3
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public class AppWindowPresenter
{
    private readonly AppWindowPresenterKind _kind;

    protected internal AppWindowPresenter(
        WinRT.IObjectReference objRef)
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected AppWindowPresenter(
        WinRT.DerivedComposed _)
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal AppWindowPresenter(
        AppWindowPresenterKind kind)
    {
        _kind = kind;
    }

    public AppWindowPresenterKind Kind => _kind;

    internal event Action? ConfigurationChanged;

    internal void NotifyConfigurationChanged() =>
        ConfigurationChanged?.Invoke();
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class CompactOverlayPresenter :
    AppWindowPresenter
{
    private CompactOverlaySize _initialSize;

    private CompactOverlayPresenter()
        : base(AppWindowPresenterKind.CompactOverlay)
    {
    }

    public CompactOverlaySize InitialSize
    {
        get => _initialSize;
        set
        {
            if (_initialSize == value)
                return;
            _initialSize = value;
            NotifyConfigurationChanged();
        }
    }

    public static CompactOverlayPresenter Create() => new();
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class FullScreenPresenter :
    AppWindowPresenter
{
    private FullScreenPresenter()
        : base(AppWindowPresenterKind.FullScreen)
    {
    }

    public static FullScreenPresenter Create() => new();
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class OverlappedPresenter :
    AppWindowPresenter
{
    private bool _hasBorder;
    private bool _hasTitleBar;
    private bool _isAlwaysOnTop;
    private bool _isMaximizable;
    private bool _isMinimizable;
    private bool _isModal;
    private bool _isResizable;
    private int? _preferredMaximumHeight;
    private int? _preferredMaximumWidth;
    private int? _preferredMinimumHeight;
    private int? _preferredMinimumWidth;
    private static OverlappedPresenterState s_requestedStartupState =
        OverlappedPresenterState.Restored;
    private OverlappedPresenterState _state;

    private OverlappedPresenter(
        bool hasBorder,
        bool hasTitleBar,
        bool isMaximizable,
        bool isMinimizable,
        bool isResizable)
        : base(AppWindowPresenterKind.Overlapped)
    {
        _hasBorder = hasBorder;
        _hasTitleBar = hasTitleBar;
        _isMaximizable = isMaximizable;
        _isMinimizable = isMinimizable;
        _isResizable = isResizable;
        _state = OverlappedPresenterState.Restored;
    }

    public bool HasBorder => _hasBorder;

    public bool HasTitleBar => _hasTitleBar;

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        set => SetField(ref _isAlwaysOnTop, value);
    }

    public bool IsMaximizable
    {
        get => _isMaximizable;
        set => SetField(ref _isMaximizable, value);
    }

    public bool IsMinimizable
    {
        get => _isMinimizable;
        set => SetField(ref _isMinimizable, value);
    }

    public bool IsModal
    {
        get => _isModal;
        set => SetField(ref _isModal, value);
    }

    public bool IsResizable
    {
        get => _isResizable;
        set => SetField(ref _isResizable, value);
    }

    public int? PreferredMaximumHeight
    {
        get => _preferredMaximumHeight;
        set => SetDimension(
            ref _preferredMaximumHeight,
            value,
            nameof(value));
    }

    public int? PreferredMaximumWidth
    {
        get => _preferredMaximumWidth;
        set => SetDimension(
            ref _preferredMaximumWidth,
            value,
            nameof(value));
    }

    public int? PreferredMinimumHeight
    {
        get => _preferredMinimumHeight;
        set => SetDimension(
            ref _preferredMinimumHeight,
            value,
            nameof(value));
    }

    public int? PreferredMinimumWidth
    {
        get => _preferredMinimumWidth;
        set => SetDimension(
            ref _preferredMinimumWidth,
            value,
            nameof(value));
    }

    public static OverlappedPresenterState RequestedStartupState =>
        s_requestedStartupState;

    public OverlappedPresenterState State => _state;

    public static OverlappedPresenter Create() =>
        new(
            hasBorder: true,
            hasTitleBar: true,
            isMaximizable: true,
            isMinimizable: true,
            isResizable: true);

    public static OverlappedPresenter CreateForContextMenu() =>
        new(
            hasBorder: true,
            hasTitleBar: false,
            isMaximizable: false,
            isMinimizable: false,
            isResizable: false);

    public static OverlappedPresenter CreateForDialog() =>
        new(
            hasBorder: true,
            hasTitleBar: true,
            isMaximizable: false,
            isMinimizable: false,
            isResizable: false);

    public static OverlappedPresenter CreateForToolWindow() =>
        new(
            hasBorder: true,
            hasTitleBar: true,
            isMaximizable: true,
            isMinimizable: true,
            isResizable: true);

    public void Maximize() =>
        SetState(OverlappedPresenterState.Maximized);

    public void Minimize() => Minimize(activateWindow: true);

    public void Minimize(bool activateWindow) =>
        SetState(OverlappedPresenterState.Minimized);

    public void Restore() => Restore(activateWindow: true);

    public void Restore(bool activateWindow) =>
        SetState(OverlappedPresenterState.Restored);

    public void SetBorderAndTitleBar(
        bool hasBorder,
        bool hasTitleBar)
    {
        if (_hasBorder == hasBorder &&
            _hasTitleBar == hasTitleBar)
        {
            return;
        }

        _hasBorder = hasBorder;
        _hasTitleBar = hasTitleBar;
        NotifyConfigurationChanged();
    }

    internal void ApplyRequestedStartupState() =>
        SetState(RequestedStartupState);

    private void SetState(OverlappedPresenterState state)
    {
        if (_state == state)
            return;

        _state = state;
        NotifyConfigurationChanged();
    }

    private void SetField(
        ref bool field,
        bool value)
    {
        if (field == value)
            return;
        field = value;
        NotifyConfigurationChanged();
    }

    private void SetDimension(
        ref int? field,
        int? value,
        string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName);
        if (field == value)
            return;
        field = value;
        NotifyConfigurationChanged();
    }
}
