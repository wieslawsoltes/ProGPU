using Microsoft.UI.Content;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Platform;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using XamlPointerDeviceType =
    Windows.Devices.Input.PointerDeviceType;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class InputPointerSource :
    InputObject
{
    private readonly HashSet<uint> _insidePointers =
        new();
    private WindowInputState? _state;
    private InputCursor? _cursor =
        InputSystemCursor.Create(
            InputSystemCursorShape.Arrow);

    private InputPointerSource(
        ContentIsland island)
        : base(island.DispatcherQueue)
    {
        Attach(island.InputState);
    }

    public InputCursor Cursor
    {
        get
        {
            VerifyAccess();
            return _cursor!;
        }
        set
        {
            VerifyAccess();
            if (ReferenceEquals(_cursor, value))
                return;
            _cursor = value;
            if (_state is not null)
            {
                InputSystem.RefreshInputCursor(
                    _state);
            }
        }
    }

    public InputPointerSourceDeviceKinds DeviceKinds
    {
        get
        {
            VerifyAccess();
            return
                InputPointerSourceDeviceKinds.Touch |
                InputPointerSourceDeviceKinds.Pen |
                InputPointerSourceDeviceKinds.Mouse;
        }
    }

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerCaptureLost;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerEntered;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerExited;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerMoved;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerPressed;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerReleased;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerRoutedAway;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerRoutedReleased;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerRoutedTo;

    public event TypedEventHandler<
        InputPointerSource,
        PointerEventArgs>? PointerWheelChanged;

    public static InputPointerSource GetForIsland(
        ContentIsland island)
    {
        ArgumentNullException.ThrowIfNull(island);
        if (!island.DispatcherQueue.HasThreadAccess ||
            island.IsClosed)
        {
            return null!;
        }

        return island.PointerSource ??=
            new InputPointerSource(island);
    }

    internal InputCursor? CursorCore =>
        _cursor?.IsDisposed == true
            ? null
            : _cursor;

    internal void Attach(
        WindowInputState? state)
    {
        WindowInputState? previousState = _state;
        if (_state?.PointerSource == this)
            _state.PointerSource = null;
        _state = state;
        if (_state is not null)
            _state.PointerSource = this;
        if (previousState is not null)
            InputSystem.RefreshInputCursor(
                previousState);
        if (_state is not null)
            InputSystem.RefreshInputCursor(
                _state);
    }

    internal void Detach()
    {
        WindowInputState? state = _state;
        Attach(null);
        _insidePointers.Clear();
        PointerCaptureLost = null;
        PointerEntered = null;
        PointerExited = null;
        PointerMoved = null;
        PointerPressed = null;
        PointerReleased = null;
        PointerRoutedAway = null;
        PointerRoutedReleased = null;
        PointerRoutedTo = null;
        PointerWheelChanged = null;
        if (state is not null)
            InputSystem.RefreshInputCursor(state);
    }

    internal bool Process(
        PointerInputEvent input)
    {
        bool entered =
            _insidePointers.Add(input.PointerId);
        TypedEventHandler<
            InputPointerSource,
            PointerEventArgs>? primaryHandler =
            input.Kind switch
            {
                PointerInputKind.Pressed =>
                    PointerPressed,
                PointerInputKind.Moved =>
                    PointerMoved,
                PointerInputKind.Released =>
                    PointerReleased,
                PointerInputKind.Canceled =>
                    PointerCaptureLost,
                PointerInputKind.Wheel =>
                    PointerWheelChanged,
                _ => null
            };
        bool raisesExited =
            input.Kind ==
                PointerInputKind.Released &&
            input.DeviceType !=
                XamlPointerDeviceType.Mouse;
        bool endsTracking =
            raisesExited ||
            input.Kind ==
                PointerInputKind.Canceled;
        TypedEventHandler<
            InputPointerSource,
            PointerEventArgs>? enteredHandler =
            entered ? PointerEntered : null;
        TypedEventHandler<
            InputPointerSource,
            PointerEventArgs>? exitedHandler =
            raisesExited ? PointerExited : null;

        if (endsTracking)
            _insidePointers.Remove(input.PointerId);
        if (enteredHandler is null &&
            primaryHandler is null &&
            exitedHandler is null)
        {
            return false;
        }

        PointerEventArgs args =
            CreateArgs(input);
        enteredHandler?.Invoke(this, args);
        primaryHandler?.Invoke(this, args);
        exitedHandler?.Invoke(this, args);
        return args.Handled;
    }

    internal bool RaiseExternal(
        InputPointerSourceEventKind kind,
        PointerEventArgs args)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(args);
        TypedEventHandler<
            InputPointerSource,
            PointerEventArgs>? handler =
            kind switch
            {
                InputPointerSourceEventKind
                    .CaptureLost =>
                    PointerCaptureLost,
                InputPointerSourceEventKind
                    .Entered =>
                    PointerEntered,
                InputPointerSourceEventKind
                    .Exited =>
                    PointerExited,
                InputPointerSourceEventKind
                    .Moved =>
                    PointerMoved,
                InputPointerSourceEventKind
                    .Pressed =>
                    PointerPressed,
                InputPointerSourceEventKind
                    .Released =>
                    PointerReleased,
                InputPointerSourceEventKind
                    .RoutedAway =>
                    PointerRoutedAway,
                InputPointerSourceEventKind
                    .RoutedReleased =>
                    PointerRoutedReleased,
                InputPointerSourceEventKind
                    .RoutedTo =>
                    PointerRoutedTo,
                InputPointerSourceEventKind
                    .WheelChanged =>
                    PointerWheelChanged,
                _ => throw new
                    ArgumentOutOfRangeException(
                        nameof(kind))
            };
        handler?.Invoke(this, args);
        return args.Handled;
    }

    private static PointerEventArgs CreateArgs(
        PointerInputEvent input)
    {
        PointerPoint point =
            PointerPoint.FromInput(input);
        return new PointerEventArgs(
            point,
            (Windows.System.VirtualKeyModifiers)
                input.Modifiers);
    }
}
