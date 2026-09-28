using System.Numerics;
using System.Runtime.ExceptionServices;
using Silk.NET.Input;

namespace ProGPU.Backend;

internal sealed class CocoaPopupInputContext : IInputContext, INativePointerInputContext
{
    private readonly CocoaPopupWindow _window;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly PopupMouse _mouse;
    private bool _pumping, _disposeRequested, _disposed, _faulted, _cancelPending, _observed;
    private ulong _pressed;
    private NativePointerEvent _last;
    private ExceptionDispatchInfo? _failure;

    internal CocoaPopupInputContext(CocoaPopupWindow window)
    {
        _window = window;
        window.ReadInput([], out var generation); // Validate before subscribing.
        InputGeneration = generation;
        _mouse = new(this, new CocoaPopupCursor(window, CheckConnected));
        Mice = Array.AsReadOnly<IMouse>([_mouse]);
        if (!window.SetCursor(StandardCursor.Arrow, hidden: false))
            throw new NotSupportedException("The owned view rejected its input context's initial cursor.");
        window.InputPending += Pump;
    }

    public nint Handle => _disposed ? 0 : _window.Handle;
    public IReadOnlyList<IMouse> Mice { get; }
    // A non-key popup never supplies a fake keyboard or borrows its owner's
    // mutable polling state. The source dialog continues receiving keyboard input.
    public IReadOnlyList<IKeyboard> Keyboards => Array.Empty<IKeyboard>();
    public IReadOnlyList<IGamepad> Gamepads => Array.Empty<IGamepad>();
    public IReadOnlyList<IJoystick> Joysticks => Array.Empty<IJoystick>();
    public IReadOnlyList<IInputDevice> OtherDevices => Array.Empty<IInputDevice>();
    public event Action<IInputDevice, bool>? ConnectionChanged;
    public event Action<NativePointerEvent>? PointerEvent;
    public NativePointerEvent? CurrentEvent { get; private set; }
    public ulong InputGeneration { get; private set; }
    internal bool AcceptsInput => !_disposed && !_disposeRequested && !_faulted;

    internal void ObservePolicyChange()
    {
        CheckThread();
        if (_disposed) return;
        ulong generation = _window.InputGeneration;
        if (generation != InputGeneration)
        {
            InputGeneration = generation;
            CancelState();
        }
        if (!_pumping && !_window.IsInNativeInputCallback) DrainCancellation();
    }

    private void Pump()
    {
        CheckConnected();
        if (_pumping) throw new InvalidOperationException("Owned popup input delivery cannot be nested.");
        if (_window.IsInNativeInputCallback)
            throw new InvalidOperationException("Owned popup input cannot dispatch from a native callback.");
        _pumping = true;
        Exception? dispatchFailure = null;
        try
        {
            ObservePolicyChange();
            FlushCancellation();
            if (_disposeRequested || _window.IsClosing) return;
            _failure?.Throw();
            Span<CocoaPopupPointerEvent> batch = stackalloc CocoaPopupPointerEvent[32];
            for (int total = 0; total < 256;)
            {
                int count = _window.ReadInput(batch[..Math.Min(batch.Length, 256 - total)], out ulong generation);
                ObservePolicyChange();
                FlushCancellation();
                if (count == 0 || !CanDeliver(generation)) break;
                total += count;
                for (int i = 0; i < count && CanDeliver(generation); i++)
                    Deliver(batch[i], generation);
                if (!CanDeliver(generation)) break;
            }
        }
        catch (Exception failure)
        {
            dispatchFailure = failure;
            RecordFailure(failure);
            throw;
        }
        finally
        {
            CurrentEvent = null;
            try { ObservePolicyChange(); FlushCancellation(); }
            catch (Exception cleanup) when (dispatchFailure is not null)
            {
                dispatchFailure.Data["PopupInputCancellation"] = cleanup;
            }
            catch (Exception cleanup) { RecordFailure(cleanup); throw; }
            finally
            {
                _pumping = false;
                if (_disposeRequested) FinishDispose();
            }
        }
    }

    private bool CanDeliver(ulong generation) => !_disposed && !_disposeRequested && !_faulted &&
        !_window.IsClosing && generation == InputGeneration && generation == _window.InputGeneration;

    private void Deliver(in CocoaPopupPointerEvent native, ulong generation)
    {
        NativePointerEvent value = Translate(native);
        // Validate Silk's narrower projection before publishing either stream.
        var position = new Vector2(ToSingle(value.X), ToSingle(value.Y));
        var wheel = new ScrollWheel(ToSingle(value.ScrollX), ToSingle(value.ScrollY));
        MouseButton button = MapButton(value.Button);
        ulong bit = value.Button is >= 0 and < 64 ? 1ul << value.Button : 0;
        bool wasPressed = (_pressed & bit) != 0;
        _mouse.NativePosition = position;
        if (value.Kind == NativePointerEventKind.Down) _pressed |= bit;
        else if (value.Kind == NativePointerEventKind.Up) _pressed &= ~bit;
        else if (value.Kind == NativePointerEventKind.Cancel) _pressed = 0;
        if (value.Kind == NativePointerEventKind.Scroll) _mouse.SetWheel(wheel);
        _last = value;
        _observed = value.Kind != NativePointerEventKind.Cancel;
        CurrentEvent = value;
        try
        {
            PointerEvent?.Invoke(value);
            if (!CanDeliver(generation)) return;
            switch (value.Kind)
            {
                case NativePointerEventKind.Move:
                case NativePointerEventKind.Drag:
                case NativePointerEventKind.Enter:
                    _mouse.RaiseMove(position);
                    break;
                case NativePointerEventKind.Down:
                    _mouse.RaiseDown(button);
                    break;
                case NativePointerEventKind.Up:
                    _mouse.RaiseUp(button); // Also forward unmatched native up.
                    if (wasPressed && value.ClickCount > 0 && CanDeliver(generation))
                    {
                        _mouse.RaiseClick(button, position);
                        if (value.ClickCount == 2 && CanDeliver(generation)) _mouse.RaiseDoubleClick(button, position);
                    }
                    break;
                case NativePointerEventKind.Scroll:
                    _mouse.RaiseScroll(wheel);
                    break;
            }
        }
        finally { CurrentEvent = null; }
    }

    private void CancelState()
    {
        _cancelPending |= _observed || _pressed != 0;
        _observed = false;
        _pressed = 0;
    }

    private void FlushCancellation()
    {
        if (!_cancelPending || _window.IsInNativeInputCallback) return;
        _cancelPending = false;
        var cancel = new NativePointerEvent(NativePointerEventKind.Cancel,
            _last.X, _last.Y, _last.Timestamp, -1, 0, NativePointerModifiers.None);
        CurrentEvent = cancel;
        try { PointerEvent?.Invoke(cancel); }
        finally { CurrentEvent = null; }
    }

    private void DrainCancellation()
    {
        _pumping = true;
        try { FlushCancellation(); }
        catch (Exception failure) { RecordFailure(failure); throw; }
        finally
        {
            _pumping = false;
            if (_disposeRequested) FinishDispose();
        }
    }

    private void RecordFailure(Exception failure)
    {
        _faulted = true;
        _failure ??= ExceptionDispatchInfo.Capture(failure);
        CancelState();
        try { _window.BlockFaultedInput(); }
        catch (Exception cleanup) { failure.Data["PopupInputBlocking"] = cleanup; }
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposeRequested = true;
        CancelState();
        if (!_pumping && !_window.IsInNativeInputCallback) DrainCancellation();
    }

    private void FinishDispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.InputPending -= Pump;
        try { ConnectionChanged?.Invoke(_mouse, false); }
        finally
        {
            ConnectionChanged = null;
            PointerEvent = null;
            _mouse.ClearEvents();
            _window.ReleaseInput(this);
        }
    }

    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Owned popup input belongs to its creating thread.");
    }
    private void CheckConnected()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private static float ToSingle(double value)
    {
        if (!double.IsFinite(value) || value is < -float.MaxValue or > float.MaxValue)
            throw new InvalidOperationException("Native pointer input cannot be represented by the Silk mouse contract.");
        return (float)value;
    }

    internal static NativePointerEvent Translate(in CocoaPopupPointerEvent value) => new(
        value.Kind switch
        {
            CocoaPopupPointerKind.Move => NativePointerEventKind.Move,
            CocoaPopupPointerKind.Drag => NativePointerEventKind.Drag,
            CocoaPopupPointerKind.Down => NativePointerEventKind.Down,
            CocoaPopupPointerKind.Up => NativePointerEventKind.Up,
            CocoaPopupPointerKind.Enter => NativePointerEventKind.Enter,
            CocoaPopupPointerKind.Leave => NativePointerEventKind.Leave,
            CocoaPopupPointerKind.Scroll => NativePointerEventKind.Scroll,
            CocoaPopupPointerKind.Cancel => NativePointerEventKind.Cancel,
            _ => throw new InvalidOperationException("Unknown native pointer event.")
        }, value.X, value.Y, value.Timestamp, value.Button, value.ClickCount,
        ((value.Modifiers & CocoaPopupModifiers.Shift) != 0 ? NativePointerModifiers.Shift : 0) |
        ((value.Modifiers & CocoaPopupModifiers.Control) != 0 ? NativePointerModifiers.Control : 0) |
        ((value.Modifiers & CocoaPopupModifiers.Option) != 0 ? NativePointerModifiers.Alt : 0) |
        ((value.Modifiers & CocoaPopupModifiers.Command) != 0 ? NativePointerModifiers.Super : 0) |
        ((value.Modifiers & CocoaPopupModifiers.CapsLock) != 0 ? NativePointerModifiers.CapsLock : 0) |
        ((value.Modifiers & CocoaPopupModifiers.NumericPad) != 0 ? NativePointerModifiers.NumericPad : 0) |
        ((value.Modifiers & CocoaPopupModifiers.Help) != 0 ? NativePointerModifiers.Help : 0) |
        ((value.Modifiers & CocoaPopupModifiers.Function) != 0 ? NativePointerModifiers.Function : 0),
        value.ScrollX, value.ScrollY,
        value.PreciseScroll ? NativePointerScrollUnit.Points : NativePointerScrollUnit.Lines,
        value.ScrollPhase, value.MomentumPhase);

    private static MouseButton MapButton(int button) => button switch
    {
        0 => MouseButton.Left, 1 => MouseButton.Right, 2 => MouseButton.Middle,
        3 => MouseButton.Button4, 4 => MouseButton.Button5, 5 => MouseButton.Button6,
        6 => MouseButton.Button7, 7 => MouseButton.Button8, 8 => MouseButton.Button9,
        9 => MouseButton.Button10, 10 => MouseButton.Button11, 11 => MouseButton.Button12,
        _ => MouseButton.Unknown
    };

    private sealed class PopupMouse : IMouse
    {
        private readonly CocoaPopupInputContext _owner;
        private readonly ScrollWheel[] _wheel = new ScrollWheel[1];
        internal Vector2 NativePosition = new(float.NaN, float.NaN);
        internal PopupMouse(CocoaPopupInputContext owner, ICursor cursor)
        {
            _owner = owner; Cursor = cursor;
            ScrollWheels = Array.AsReadOnly(_wheel);
        }
        public string Name => "Owned Cocoa popup pointer";
        public int Index => 0;
        public bool IsConnected => !_owner._disposed && !_owner._faulted;
        public ICursor Cursor { get; }
        public IReadOnlyList<ScrollWheel> ScrollWheels { get; }
        public IReadOnlyList<MouseButton> SupportedButtons { get; } = Array.AsReadOnly(
            new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.Button4,
                MouseButton.Button5, MouseButton.Button6, MouseButton.Button7, MouseButton.Button8,
                MouseButton.Button9, MouseButton.Button10, MouseButton.Button11, MouseButton.Button12 });
        public Vector2 Position
        {
            get { _owner.CheckConnected(); return NativePosition; }
            set => throw new NotSupportedException("An owned popup cannot warp the system pointer.");
        }
        // The OS supplies click counts; do not invent a second timer/distance
        // policy or falsely report an application override as native acceptance.
        public int DoubleClickRange { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public int DoubleClickTime { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public bool IsButtonPressed(MouseButton button)
        {
            _owner.CheckConnected();
            for (int index = 0; index < 12; index++)
                if (MapButton(index) == button) return (_owner._pressed & (1ul << index)) != 0;
            return false;
        }
        public event Action<IMouse, MouseButton>? MouseDown;
        public event Action<IMouse, MouseButton>? MouseUp;
        public event Action<IMouse, Vector2>? MouseMove;
        public event Action<IMouse, ScrollWheel>? Scroll;
        public event Action<IMouse, MouseButton, Vector2>? Click;
        public event Action<IMouse, MouseButton, Vector2>? DoubleClick;
        internal void SetWheel(ScrollWheel wheel) => _wheel[0] = wheel;
        internal void RaiseDown(MouseButton button) => MouseDown?.Invoke(this, button);
        internal void RaiseUp(MouseButton button) => MouseUp?.Invoke(this, button);
        internal void RaiseMove(Vector2 position) => MouseMove?.Invoke(this, position);
        internal void RaiseScroll(ScrollWheel wheel) => Scroll?.Invoke(this, wheel);
        internal void RaiseClick(MouseButton button, Vector2 position) => Click?.Invoke(this, button, position);
        internal void RaiseDoubleClick(MouseButton button, Vector2 position) => DoubleClick?.Invoke(this, button, position);
        internal void ClearEvents()
        {
            MouseDown = MouseUp = null;
            MouseMove = null; Scroll = null;
            Click = DoubleClick = null;
        }
    }
}
