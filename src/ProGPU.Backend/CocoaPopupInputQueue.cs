namespace ProGPU.Backend;

internal enum CocoaPopupPointerKind { None, Move, Drag, Down, Up, Enter, Leave, Scroll, Cancel }

[Flags]
internal enum CocoaPopupModifiers : uint
{
    None = 0, CapsLock = 1 << 16, Shift = 1 << 17, Control = 1 << 18,
    Option = 1 << 19, Command = 1 << 20, NumericPad = 1 << 21,
    Help = 1 << 22, Function = 1 << 23
}

// Coordinates and precise scroll deltas are native view points, never backing
// pixels. A non-precise scroll delta is in lines. Native direction preferences
// have already been applied; neither the queue nor host may invert them again.
internal readonly record struct CocoaPopupPointerEvent(
    CocoaPopupPointerKind Kind, double X, double Y, double Timestamp,
    int Button, int ClickCount, CocoaPopupModifiers Modifiers,
    double ScrollX = 0, double ScrollY = 0, bool PreciseScroll = false,
    uint ScrollPhase = 0, uint MomentumPhase = 0);

internal enum CocoaPopupInputFailure { None, InvalidEvent, CapacityExceeded, NativeCallback }

// Native callbacks only append value records. Application handlers run later,
// outside AppKit dispatch, using caller-owned batches. No motion/edge is dropped
// or coalesced: overflow poisons the queue and is reported before any publication.
internal sealed class CocoaPopupInputQueue
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly CocoaPopupPointerEvent[] _events;
    private int _head, _count;
    private bool _enabled, _visible, _closed;
    private CocoaPopupInputFailure _failure;
    private int _nativeCallbackDepth;

    internal CocoaPopupInputQueue(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 256);
        _events = new CocoaPopupPointerEvent[capacity];
    }

    // Changes across input-policy, hide and close boundaries. The input adapter
    // must discard its held-button/capture state when observing a new generation.
    internal ulong Generation { get; private set; } = 1;
    internal bool CanReceive => !_closed && _enabled && _visible && _failure == CocoaPopupInputFailure.None;
    internal bool Enabled => _enabled;
    internal bool IsInNativeCallback => _nativeCallbackDepth != 0;

    // Balanced solely by the owned native view callbacks. Disposal can close
    // input immediately but native objects must survive until managed pumping
    // has left this callback and explicitly drains pending surface retirement.
    internal void BeginNativeCallback() => ++_nativeCallbackDepth;
    internal void EndNativeCallback() => --_nativeCallbackDepth;

    internal void EnsureHealthy() { CheckThread(); CheckReadable(); }

    internal void SetEnabled(bool enabled)
    {
        CheckThread();
        if (enabled) CheckReadable();
        if (_enabled == enabled) return;
        _enabled = enabled;
        Invalidate();
    }

    internal void SetVisible(bool visible)
    {
        CheckThread();
        if (visible) CheckReadable();
        if (_visible == visible) return;
        _visible = visible;
        Invalidate();
    }

    internal void Close()
    {
        CheckThread();
        if (_closed) return;
        _closed = true;
        _enabled = _visible = false;
        Invalidate();
    }

    // The reverse native callback checks its AppKit thread before reaching this
    // method. Keep it nonthrowing and allocation-free, including the failure path.
    internal bool TryWrite(in CocoaPopupPointerEvent value)
    {
        if (!CanReceive) return false;
        if (!IsValid(value)) { Fail(CocoaPopupInputFailure.InvalidEvent); return false; }
        if (_count == _events.Length) { Fail(CocoaPopupInputFailure.CapacityExceeded); return false; }
        _events[(_head + _count) % _events.Length] = value;
        ++_count;
        return true;
    }

    internal void Fail(CocoaPopupInputFailure failure)
    {
        if (_closed || _failure != CocoaPopupInputFailure.None) return;
        _failure = failure;
        Invalidate();
    }

    internal int Read(Span<CocoaPopupPointerEvent> destination, out ulong generation)
    {
        CheckThread();
        CheckReadable();
        if (IsInNativeCallback)
            throw new InvalidOperationException("Popup input cannot be delivered from a native callback.");
        generation = Generation;
        int count = Math.Min(_count, destination.Length);
        int first = Math.Min(count, _events.Length - _head);
        _events.AsSpan(_head, first).CopyTo(destination);
        _events.AsSpan(0, count - first).CopyTo(destination[first..]);
        _head = (_head + count) % _events.Length;
        _count -= count;
        return count;
    }

    private void CheckReadable()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_failure != CocoaPopupInputFailure.None)
            throw new InvalidOperationException($"Cocoa popup input failed: {_failure}.");
    }

    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Cocoa popup input belongs to its creating thread.");
    }

    private void Invalidate()
    {
        _head = _count = 0;
        // Equality, not ordering, is the caller's generation contract.
        unchecked { ++Generation; }
        if (Generation == 0) Generation = 1;
    }

    internal static bool IsValid(in CocoaPopupPointerEvent value)
    {
        if (value.Kind is <= CocoaPopupPointerKind.None or > CocoaPopupPointerKind.Cancel ||
            !double.IsFinite(value.X) || !double.IsFinite(value.Y) ||
            !double.IsFinite(value.Timestamp) || value.Timestamp < 0 ||
            !double.IsFinite(value.ScrollX) || !double.IsFinite(value.ScrollY) ||
            value.ClickCount < 0) return false;
        if (value.Kind is CocoaPopupPointerKind.Down or CocoaPopupPointerKind.Up or CocoaPopupPointerKind.Drag)
        {
            if (value.Button is < 0 or >= 64) return false;
        }
        else if (value.Button != -1 || value.ClickCount != 0) return false;
        return value.Kind == CocoaPopupPointerKind.Scroll ||
            (value.ScrollX == 0 && value.ScrollY == 0 && !value.PreciseScroll &&
             value.ScrollPhase == 0 && value.MomentumPhase == 0);
    }

    internal static CocoaPopupPointerKind Classify(nuint nativeType) => nativeType switch
    {
        1 or 3 or 25 => CocoaPopupPointerKind.Down,
        2 or 4 or 26 => CocoaPopupPointerKind.Up,
        5 => CocoaPopupPointerKind.Move,
        6 or 7 or 27 => CocoaPopupPointerKind.Drag,
        8 => CocoaPopupPointerKind.Enter,
        9 => CocoaPopupPointerKind.Leave,
        22 => CocoaPopupPointerKind.Scroll,
        40 => CocoaPopupPointerKind.Cancel,
        _ => CocoaPopupPointerKind.None
    };
}
