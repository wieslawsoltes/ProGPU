using Silk.NET.Input;

namespace ProGPU.Backend;

// This surface is intentionally internal until the source hosts own its input
// and presentation adapters. It does not change GLFW or automatic modality.
internal sealed class CocoaOwnedPopupSurface : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private ICocoaOwnedPopupOperations? _operations;
    private readonly CocoaPopupInputQueue _input;
    private int _leases;
    private int _transitionDepth;
    private bool _closeRequested;

    internal CocoaOwnedPopupSurface(ICocoaOwnedPopupOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations;
        _input = operations.Input;
    }

    internal static bool TryCreate(NativeWindowHandle owner, NativeWindowBounds bounds,
        bool transparent, out CocoaOwnedPopupSurface? surface)
    {
        surface = null;
        var operations = CocoaNativePopupWindow.TryCreateOwned(owner, bounds, transparent);
        if (operations is null) return false;
        surface = new(operations);
        return true;
    }

    internal static bool TryCreateUnbound(NativeWindowBounds bounds, bool transparent, out CocoaOwnedPopupSurface? surface)
    {
        surface = null;
        var operations = CocoaNativePopupWindow.TryCreateUnbound(bounds, transparent);
        if (operations is null) return false;
        surface = new(operations);
        return true;
    }

    internal bool IsReleased => _operations is null;
    internal ulong InputGeneration { get { CheckThread(); return _input.Generation; } }
    internal bool IsInNativeCallback { get { CheckThread(); return _input.IsInNativeCallback; } }
    internal void CloseInput() { CheckThread(); _input.Close(); }

    internal bool SupportsCursor(StandardCursor cursor)
    {
        var operations = Enter();
        try { return operations.SupportsCursor(cursor) && !_closeRequested; }
        finally { Exit(); }
    }

    internal bool SetCursor(StandardCursor cursor, bool hidden)
    {
        var operations = Enter();
        try { return operations.SetCursor(cursor, hidden) && !_closeRequested; }
        finally { Exit(); }
    }

    internal bool SetInputAllowed(bool allowed)
    {
        var operations = Enter();
        try
        {
            if (allowed) operations.Input.EnsureHealthy();
            if (!allowed) operations.Input.SetEnabled(false);
            if (!operations.SetInputAllowed(allowed) || _closeRequested)
            {
                operations.Input.SetEnabled(false);
                return false;
            }
            if (allowed) operations.Input.SetEnabled(true);
            return true;
        }
        finally { Exit(); }
    }

    internal bool BindOwner(NativeWindowHandle owner)
    {
        var operations = Enter();
        try
        {
            operations.Input.EnsureHealthy();
            operations.Input.SetEnabled(false);
            if (!operations.SetInputAllowed(false) || _closeRequested) return false;
            return operations.BindOwner(owner) && !_closeRequested;
        }
        finally { Exit(); }
    }

    internal int ReadInput(Span<CocoaPopupPointerEvent> destination, out ulong generation)
    {
        var operations = Enter();
        try { return operations.Input.Read(destination, out generation); }
        finally { Exit(); }
    }

    internal bool Show()
    {
        var operations = Enter();
        try { return operations.Show() && !_closeRequested; }
        finally { Exit(); }
    }

    internal bool Hide()
    {
        var operations = Enter();
        try { return operations.Hide() && !_closeRequested; }
        finally { Exit(); }
    }

    internal bool SetBounds(NativeWindowBounds bounds)
    {
        var operations = Enter();
        try { return operations.SetBounds(bounds) && !_closeRequested; }
        finally { Exit(); }
    }

    internal bool SetTopMost(bool value) => SetOption(value, static (operations, requested) => operations.SetTopMost(requested));

    internal bool SetOpacity(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
        return SetOption(value, static (operations, requested) => operations.SetOpacity(requested));
    }

    internal bool SetZOrder(NativeWindowZOrder value)
    {
        if (value is not NativeWindowZOrder.Front and not NativeWindowZOrder.Back)
            throw new ArgumentOutOfRangeException(nameof(value));
        return SetOption(value, static (operations, requested) => operations.SetZOrder(requested));
    }

    internal bool SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum)
    {
        if (minimum.Width < 0 || minimum.Height < 0 || maximum.Width < minimum.Width || maximum.Height < minimum.Height)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        return SetOption((minimum, maximum), static (operations, requested) => operations.SetSizeConstraints(requested.minimum, requested.maximum));
    }

    private bool SetOption<T>(T value, Func<ICocoaOwnedPopupOperations, T, bool> apply)
    {
        var operations = Enter();
        Exception? failure = null;
        bool accepted = false;
        try { accepted = apply(operations, value) && !_closeRequested && operations.IsCurrent; }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            try { Exit(); }
            catch (Exception cleanup) when (failure is not null) { CocoaPopupFailure.AttachCleanup(failure, "PopupOptionRetirement", cleanup); }
        }
        return accepted && !_closeRequested;
    }

    internal bool TryGetGeometry(out NativeWindowGeometrySnapshot snapshot)
    {
        snapshot = default;
        var operations = Enter();
        try
        {
            if (!operations.TryGetGeometry(out var candidate) || _closeRequested) return false;
            snapshot = candidate;
            return true;
        }
        finally { Exit(); }
    }

    // One lease per retained presentation surface, not per draw/frame. A caller
    // must dispose its GPU surface before releasing this native-view lease.
    internal RenderLease AcquireRenderLease()
    {
        var operations = Enter();
        try
        {
            var lease = new RenderLease(this, operations.Window, operations.ContentView);
            checked { ++_leases; }
            return lease;
        }
        finally { Exit(); }
    }

    public void Dispose()
    {
        CheckThread();
        if (_operations is null) return;
        _closeRequested = true;
        _operations.Input.Close();
        DrainClose();
    }

    // Hosts retain a closing surface and call this after native event polling,
    // just as they already defer GLFW native-window destruction across callbacks.
    internal bool TryCompleteDispose()
    {
        CheckThread();
        DrainClose();
        return IsReleased;
    }

    private ICocoaOwnedPopupOperations Enter()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(_closeRequested || _operations is null, this);
        if (_transitionDepth != 0 || _operations.Input.IsInNativeCallback)
            throw new InvalidOperationException("A Cocoa popup transition is already active.");
        var operations = _operations;
        ++_transitionDepth;
        try
        {
            if (!operations.IsCurrent)
                throw new InvalidOperationException("The owned Cocoa popup identity changed.");
            ObjectDisposedException.ThrowIf(_closeRequested, this);
            return operations;
        }
        catch (Exception failure)
        {
            try { Exit(); }
            catch (Exception cleanup) { CocoaPopupFailure.AttachCleanup(failure, "PopupTransitionRetirement", cleanup); }
            throw;
        }
    }

    private void Exit()
    {
        --_transitionDepth;
        DrainClose();
    }

    private void DrainClose()
    {
        if (!_closeRequested || _operations is null || _transitionDepth != 0 || _operations.Input.IsInNativeCallback) return;
        ++_transitionDepth;
        try
        {
            // Keep ownership on failure. Never release a view still borrowed by
            // a renderer or destroy a panel during a synchronous AppKit callback.
            if (!_operations.Hide())
                throw new InvalidOperationException("The owned Cocoa popup could not be hidden.");
            if (_leases != 0) return;
            _operations.Dispose();
            _operations = null;
        }
        finally { --_transitionDepth; }
    }

    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Cocoa popup ownership is bound to its creating thread.");
    }

    internal sealed class RenderLease : IDisposable
    {
        private CocoaOwnedPopupSurface? _owner;
        private readonly NativeWindowHandle _window;
        private readonly nint _view;

        internal RenderLease(CocoaOwnedPopupSurface owner, NativeWindowHandle window, nint view)
        { _owner = owner; _window = window; _view = view; }

        internal NativeWindowHandle Window { get { Check(); return _window; } }
        internal nint ContentView { get { Check(); return _view; } }

        private void Check()
        {
            ObjectDisposedException.ThrowIf(_owner is null, this);
            _owner.CheckThread();
        }

        public void Dispose()
        {
            if (_owner is null) return;
            _owner.CheckThread();
            var owner = _owner;
            _owner = null;
            --owner._leases;
            owner.DrainClose();
        }
    }
}

internal interface ICocoaOwnedPopupOperations : IDisposable
{
    NativeWindowHandle Window { get; }
    nint ContentView { get; }
    CocoaPopupInputQueue Input { get; }
    bool IsCurrent { get; }
    bool BindOwner(NativeWindowHandle owner);
    bool SetInputAllowed(bool allowed);
    bool Show();
    bool Hide();
    bool SetBounds(NativeWindowBounds bounds);
    bool SetTopMost(bool value);
    bool SetOpacity(double value);
    bool SetZOrder(NativeWindowZOrder value);
    bool SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum);
    bool SupportsCursor(StandardCursor cursor);
    bool SetCursor(StandardCursor cursor, bool hidden);
    bool TryGetGeometry(out NativeWindowGeometrySnapshot snapshot);
}
