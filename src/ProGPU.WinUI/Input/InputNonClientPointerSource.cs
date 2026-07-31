using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010006)]
public sealed class EnteredMoveSizeEventArgs
{
    internal EnteredMoveSizeEventArgs(
        MoveSizeOperation moveSizeOperation,
        PointInt32 pointerScreenPoint)
    {
        MoveSizeOperation = moveSizeOperation;
        PointerScreenPoint = pointerScreenPoint;
    }

    public MoveSizeOperation MoveSizeOperation { get; }

    public PointInt32 PointerScreenPoint { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010006)]
public sealed class EnteringMoveSizeEventArgs
{
    internal EnteringMoveSizeEventArgs(
        MoveSizeOperation moveSizeOperation,
        WindowId moveSizeWindowId,
        PointInt32 pointerScreenPoint)
    {
        MoveSizeOperation = moveSizeOperation;
        MoveSizeWindowId = moveSizeWindowId;
        PointerScreenPoint = pointerScreenPoint;
    }

    public MoveSizeOperation MoveSizeOperation { get; }

    public WindowId MoveSizeWindowId { get; set; }

    public PointInt32 PointerScreenPoint { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010006)]
public sealed class ExitedMoveSizeEventArgs
{
    internal ExitedMoveSizeEventArgs(
        MoveSizeOperation moveSizeOperation,
        PointInt32 pointerScreenPoint)
    {
        MoveSizeOperation = moveSizeOperation;
        PointerScreenPoint = pointerScreenPoint;
    }

    public MoveSizeOperation MoveSizeOperation { get; }

    public PointInt32 PointerScreenPoint { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class NonClientCaptionTappedEventArgs
{
    internal NonClientCaptionTappedEventArgs(
        PointerDeviceType pointerDeviceType,
        Point point)
    {
        PointerDeviceType = pointerDeviceType;
        Point = point;
    }

    public PointerDeviceType PointerDeviceType { get; }

    public Point Point { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class NonClientPointerEventArgs
{
    internal NonClientPointerEventArgs(
        NonClientRegionKind regionKind,
        PointerDeviceType pointerDeviceType,
        bool isPointInRegion,
        Point point)
    {
        RegionKind = regionKind;
        PointerDeviceType = pointerDeviceType;
        IsPointInRegion = isPointInRegion;
        Point = point;
    }

    public NonClientRegionKind RegionKind { get; }

    public PointerDeviceType PointerDeviceType { get; }

    public bool IsPointInRegion { get; }

    public Point Point { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class NonClientRegionsChangedEventArgs
{
    private readonly NonClientRegionKind[] _changedRegions;

    internal NonClientRegionsChangedEventArgs(
        NonClientRegionKind[] changedRegions)
    {
        _changedRegions = changedRegions;
    }

    public NonClientRegionKind[] ChangedRegions =>
        (NonClientRegionKind[])_changedRegions.Clone();
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010006)]
public sealed class WindowRectChangedEventArgs
{
    internal WindowRectChangedEventArgs(
        MoveSizeOperation moveSizeOperation,
        PointInt32 pointerScreenPoint,
        RectInt32 newWindowRect,
        RectInt32 oldWindowRect)
    {
        MoveSizeOperation = moveSizeOperation;
        PointerScreenPoint = pointerScreenPoint;
        NewWindowRect = newWindowRect;
        OldWindowRect = oldWindowRect;
    }

    public MoveSizeOperation MoveSizeOperation { get; }

    public PointInt32 PointerScreenPoint { get; }

    public RectInt32 NewWindowRect { get; }

    public RectInt32 OldWindowRect { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010006)]
public sealed class WindowRectChangingEventArgs
{
    internal WindowRectChangingEventArgs(
        MoveSizeOperation moveSizeOperation,
        PointInt32 pointerScreenPoint,
        RectInt32 newWindowRect,
        RectInt32 oldWindowRect,
        bool showWindow)
    {
        MoveSizeOperation = moveSizeOperation;
        PointerScreenPoint = pointerScreenPoint;
        NewWindowRect = newWindowRect;
        OldWindowRect = oldWindowRect;
        AllowRectChange = true;
        ShowWindow = showWindow;
    }

    public MoveSizeOperation MoveSizeOperation { get; }

    public bool AllowRectChange { get; set; }

    public bool ShowWindow { get; set; }

    public PointInt32 PointerScreenPoint { get; }

    public RectInt32 NewWindowRect { get; set; }

    public RectInt32 OldWindowRect { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class InputNonClientPointerSource
{
    private const int RegionKindCount = 10;
    private static readonly ConditionalWeakTable<
        AppWindow,
        InputNonClientPointerSource> s_windowSources =
        new();

    private readonly RectInt32[][] _regionRects =
        new RectInt32[RegionKindCount][];
    private AppWindow? _appWindow;
    private DispatcherQueue _dispatcherQueue;

    private InputNonClientPointerSource(
        AppWindow appWindow)
    {
        _appWindow = appWindow;
        _dispatcherQueue = appWindow.DispatcherQueue;
        for (int index = 0;
             index < RegionKindCount;
             index++)
        {
            _regionRects[index] =
                Array.Empty<RectInt32>();
        }

        appWindow.Destroying +=
            OnAppWindowDestroying;
    }

    public DispatcherQueue DispatcherQueue =>
        _appWindow?.DispatcherQueue ??
        _dispatcherQueue;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        EnteredMoveSizeEventArgs>? EnteredMoveSize;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        EnteringMoveSizeEventArgs>? EnteringMoveSize;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        ExitedMoveSizeEventArgs>? ExitedMoveSize;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        NonClientCaptionTappedEventArgs>? CaptionTapped;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        NonClientPointerEventArgs>? PointerEntered;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        NonClientPointerEventArgs>? PointerExited;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        NonClientPointerEventArgs>? PointerMoved;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        NonClientPointerEventArgs>? PointerPressed;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        NonClientPointerEventArgs>? PointerReleased;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        NonClientRegionsChangedEventArgs>? RegionsChanged;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        WindowRectChangedEventArgs>? WindowRectChanged;

    public event TypedEventHandler<
        InputNonClientPointerSource,
        WindowRectChangingEventArgs>? WindowRectChanging;

    public static InputNonClientPointerSource GetForWindowId(
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

        return s_windowSources.GetValue(
            appWindow,
            static window =>
                new InputNonClientPointerSource(window));
    }

    public void SetRegionRects(
        NonClientRegionKind region,
        RectInt32[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        VerifyAccess();
        int index = ValidateRegion(region);
        if (_regionRects[index].AsSpan()
            .SequenceEqual(rects))
        {
            return;
        }

        _regionRects[index] =
            rects.Length == 0
                ? Array.Empty<RectInt32>()
                : (RectInt32[])rects.Clone();
        RaiseRegionsChanged(region);
    }

    public RectInt32[] GetRegionRects(
        NonClientRegionKind region)
    {
        VerifyAccess();
        RectInt32[] rects =
            _regionRects[ValidateRegion(region)];
        return rects.Length == 0
            ? Array.Empty<RectInt32>()
            : (RectInt32[])rects.Clone();
    }

    public void ClearRegionRects(
        NonClientRegionKind region)
    {
        VerifyAccess();
        int index = ValidateRegion(region);
        if (_regionRects[index].Length == 0)
            return;
        _regionRects[index] =
            Array.Empty<RectInt32>();
        RaiseRegionsChanged(region);
    }

    public void ClearAllRegionRects()
    {
        VerifyAccess();
        Span<NonClientRegionKind> changed =
            stackalloc NonClientRegionKind[
                RegionKindCount];
        int changedCount = 0;
        for (int index = 0;
             index < RegionKindCount;
             index++)
        {
            if (_regionRects[index].Length == 0)
                continue;
            _regionRects[index] =
                Array.Empty<RectInt32>();
            changed[changedCount++] =
                (NonClientRegionKind)index;
        }

        if (changedCount == 0)
            return;
        TypedEventHandler<
            InputNonClientPointerSource,
            NonClientRegionsChangedEventArgs>? handler =
            RegionsChanged;
        if (handler is null)
            return;
        var changedRegions =
            new NonClientRegionKind[changedCount];
        changed[..changedCount].CopyTo(
            changedRegions);
        handler(
            this,
            new NonClientRegionsChangedEventArgs(
                changedRegions));
    }

    internal bool RaisePointer(
        NonClientPointerEventKind eventKind,
        NonClientRegionKind regionKind,
        PointerDeviceType pointerDeviceType,
        bool isPointInRegion,
        Point point)
    {
        VerifyAccess();
        _ = ValidateRegion(regionKind);
        TypedEventHandler<
            InputNonClientPointerSource,
            NonClientPointerEventArgs>? handler =
            eventKind switch
            {
                NonClientPointerEventKind.Entered =>
                    PointerEntered,
                NonClientPointerEventKind.Exited =>
                    PointerExited,
                NonClientPointerEventKind.Moved =>
                    PointerMoved,
                NonClientPointerEventKind.Pressed =>
                    PointerPressed,
                NonClientPointerEventKind.Released =>
                    PointerReleased,
                _ => throw new
                    ArgumentOutOfRangeException(
                        nameof(eventKind))
            };
        if (handler is null)
            return false;
        handler(
            this,
            new NonClientPointerEventArgs(
                regionKind,
                pointerDeviceType,
                isPointInRegion,
                point));
        return true;
    }

    internal bool RaiseCaptionTapped(
        PointerDeviceType pointerDeviceType,
        Point point)
    {
        VerifyAccess();
        TypedEventHandler<
            InputNonClientPointerSource,
            NonClientCaptionTappedEventArgs>? handler =
            CaptionTapped;
        if (handler is null)
            return false;
        handler(
            this,
            new NonClientCaptionTappedEventArgs(
                pointerDeviceType,
                point));
        return true;
    }

    internal bool RaiseEnteringMoveSize(
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint,
        ref WindowId moveSizeWindowId)
    {
        VerifyAccess();
        TypedEventHandler<
            InputNonClientPointerSource,
            EnteringMoveSizeEventArgs>? handler =
            EnteringMoveSize;
        if (handler is null)
            return false;
        var args =
            new EnteringMoveSizeEventArgs(
                operation,
                moveSizeWindowId,
                pointerScreenPoint);
        handler(this, args);
        moveSizeWindowId = args.MoveSizeWindowId;
        return true;
    }

    internal bool RaiseEnteredMoveSize(
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint)
    {
        VerifyAccess();
        TypedEventHandler<
            InputNonClientPointerSource,
            EnteredMoveSizeEventArgs>? handler =
            EnteredMoveSize;
        if (handler is null)
            return false;
        handler(
            this,
            new EnteredMoveSizeEventArgs(
                operation,
                pointerScreenPoint));
        return true;
    }

    internal bool RaiseExitedMoveSize(
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint)
    {
        VerifyAccess();
        TypedEventHandler<
            InputNonClientPointerSource,
            ExitedMoveSizeEventArgs>? handler =
            ExitedMoveSize;
        if (handler is null)
            return false;
        handler(
            this,
            new ExitedMoveSizeEventArgs(
                operation,
                pointerScreenPoint));
        return true;
    }

    internal bool RaiseWindowRectChanging(
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint,
        RectInt32 oldWindowRect,
        ref RectInt32 newWindowRect,
        out bool allowRectChange,
        ref bool showWindow)
    {
        VerifyAccess();
        TypedEventHandler<
            InputNonClientPointerSource,
            WindowRectChangingEventArgs>? handler =
            WindowRectChanging;
        if (handler is null)
        {
            allowRectChange = true;
            return false;
        }
        var args =
            new WindowRectChangingEventArgs(
                operation,
                pointerScreenPoint,
                newWindowRect,
                oldWindowRect,
                showWindow);
        handler(this, args);
        newWindowRect = args.NewWindowRect;
        allowRectChange = args.AllowRectChange;
        showWindow = args.ShowWindow;
        return true;
    }

    internal bool RaiseWindowRectChanged(
        MoveSizeOperation operation,
        PointInt32 pointerScreenPoint,
        RectInt32 oldWindowRect,
        RectInt32 newWindowRect)
    {
        VerifyAccess();
        TypedEventHandler<
            InputNonClientPointerSource,
            WindowRectChangedEventArgs>? handler =
            WindowRectChanged;
        if (handler is null)
            return false;
        handler(
            this,
            new WindowRectChangedEventArgs(
                operation,
                pointerScreenPoint,
                newWindowRect,
                oldWindowRect));
        return true;
    }

    internal bool IsPointInRegion(
        NonClientRegionKind region,
        Point point)
    {
        VerifyAccess();
        RectInt32[] rects =
            _regionRects[ValidateRegion(region)];
        for (int index = 0;
             index < rects.Length;
             index++)
        {
            RectInt32 rect = rects[index];
            if (point.X >= rect.X &&
                point.Y >= rect.Y &&
                point.X < (double)rect.X +
                    rect.Width &&
                point.Y < (double)rect.Y +
                    rect.Height)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetExisting(
        WindowId windowId,
        out InputNonClientPointerSource source)
    {
        source = null!;
        if (windowId.Value == 0)
            return false;
        AppWindow? appWindow =
            AppWindow.GetFromWindowId(windowId);
        if (appWindow is null ||
            !appWindow.DispatcherQueue.HasThreadAccess ||
            !s_windowSources.TryGetValue(
                appWindow,
                out InputNonClientPointerSource?
                    existing))
        {
            return false;
        }

        source = existing;
        return true;
    }

    private void RaiseRegionsChanged(
        NonClientRegionKind region)
    {
        TypedEventHandler<
            InputNonClientPointerSource,
            NonClientRegionsChangedEventArgs>? handler =
            RegionsChanged;
        if (handler is null)
            return;
        handler(
            this,
            new NonClientRegionsChangedEventArgs(
                [region]));
    }

    private void VerifyAccess()
    {
        AppWindow? appWindow = _appWindow;
        ObjectDisposedException.ThrowIf(
            appWindow is null,
            this);
        appWindow.VerifyAccess();
    }

    private void OnAppWindowDestroying(
        AppWindow sender,
        object args)
    {
        if (!ReferenceEquals(_appWindow, sender))
            return;
        sender.Destroying -=
            OnAppWindowDestroying;
        s_windowSources.Remove(sender);
        _dispatcherQueue =
            sender.DispatcherQueue;
        _appWindow = null;
        Array.Clear(_regionRects);
        for (int index = 0;
             index < RegionKindCount;
             index++)
        {
            _regionRects[index] =
                Array.Empty<RectInt32>();
        }

        EnteredMoveSize = null;
        EnteringMoveSize = null;
        ExitedMoveSize = null;
        CaptionTapped = null;
        PointerEntered = null;
        PointerExited = null;
        PointerMoved = null;
        PointerPressed = null;
        PointerReleased = null;
        RegionsChanged = null;
        WindowRectChanged = null;
        WindowRectChanging = null;
    }

    private static int ValidateRegion(
        NonClientRegionKind region)
    {
        int index = (int)region;
        return (uint)index <
            RegionKindCount
            ? index
            : throw new ArgumentOutOfRangeException(
                nameof(region));
    }
}

internal enum NonClientPointerEventKind
{
    Entered,
    Exited,
    Moved,
    Pressed,
    Released
}
