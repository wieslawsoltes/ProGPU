using System.Diagnostics;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

// A view of our own NSPanel, never a wrapper around a GLFW pointer. Keep this
// internal until input, presentation leases and both source factories connect.
internal sealed class CocoaPopupWindow : IWindow
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Action _wakeHost;
    private readonly bool _allowOwnerBinding;
    private readonly Func<NativeWindowBounds, bool, CocoaOwnedPopupSurface> _create;
    private readonly NativeView _native;
    private readonly long _start = Stopwatch.GetTimestamp();
    private CocoaOwnedPopupSurface? _surface;
    private CocoaPopupInputContext? _inputContext;
    private NativeWindowGeometrySnapshot _geometry;
    private Vector2D<int> _position, _size, _framebufferSize;
    private bool _initialized, _initializing, _visible, _closing, _disposeRequested;
    private bool _retiring, _bindingOwner, _showingOwned, _inputTransparent, _inputAllowed = true;
    private int _dispatchDepth;
    private ulong _geometryVersion;
    private double _lastUpdate, _lastRender;
    private bool _vsync, _eventDriven;

    internal CocoaPopupWindow(IWindowHost parent, NativeWindowHandle owner, WindowOptions options,
        Action wakeOwner, Func<NativeWindowBounds, bool, CocoaOwnedPopupSurface> create)
        : this(parent, owner, options, wakeOwner, create, allowOwnerBinding: false) { }

    internal CocoaPopupWindow(WindowOptions options, Action wakeHost,
        Func<NativeWindowBounds, bool, CocoaOwnedPopupSurface> create)
        : this(null, NativeWindowHandle.Empty, options, wakeHost, create, allowOwnerBinding: true) { }

    private CocoaPopupWindow(IWindowHost? parent, NativeWindowHandle owner, WindowOptions options,
        Action wakeHost, Func<NativeWindowBounds, bool, CocoaOwnedPopupSurface> create, bool allowOwnerBinding)
    {
        if (!allowOwnerBinding) ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(wakeHost);
        ArgumentNullException.ThrowIfNull(create);
        if (!allowOwnerBinding && (owner.Kind != NativeWindowKind.Cocoa || !owner.IsValid || owner.Display != 0))
            throw new ArgumentException("A live Cocoa owner is required.", nameof(owner));
        if (options.API.API != ContextAPI.None || options.IsVisible || options.TopMost ||
            options.WindowState != WindowState.Normal || options.WindowBorder != WindowBorder.Hidden ||
            options.ShouldSwapAutomatically || !options.IsContextControlDisabled ||
            options.SharedContext is not null || !string.IsNullOrEmpty(options.Title) ||
            options.FramesPerSecond != 0 || options.UpdatesPerSecond != 0)
            throw new NotSupportedException("Owned Cocoa popups require hidden, untitled, borderless, nonactivating NoAPI options.");
        ValidateSize(options.Size);
        // A source-dispatcher-owned popup has no inferred Silk parent. Native
        // owner handles do not authorize inventing a managed IWindowHost.
        Parent = parent!;
        Owner = owner;
        _allowOwnerBinding = allowOwnerBinding;
        TransparentFramebuffer = options.TransparentFramebuffer;
        _position = options.Position;
        _size = options.Size;
        _vsync = options.VSync;
        _eventDriven = options.IsEventDriven;
        _wakeHost = wakeHost;
        _create = create;
        _native = new(this);
    }

    internal NativeWindowHandle Owner { get; private set; }
    internal NativeWindowHandle NativeHandle =>
        _surface is { IsReleased: false } ? _geometry.Window : NativeWindowHandle.Empty;
    internal bool IsReleased => _disposeRequested && !_initializing && (_surface is null || _surface.IsReleased);

    // Drained by the input adapter only after native event dispatch returns.
    internal event Action? InputPending;
    internal ulong InputGeneration => _surface?.InputGeneration ?? 0;
    internal bool IsInNativeInputCallback => _surface?.IsInNativeCallback ?? false;

    internal IInputContext CreateInput()
    {
        CheckUsable();
        if (!_initialized) throw new InvalidOperationException("Initialize the owned popup before attaching input.");
        if (_inputContext is not null) throw new InvalidOperationException("The owned popup already has an input context.");
        var context = new CocoaPopupInputContext(this);
        _inputContext = context;
        try
        {
            if (!RequireSurface().SetInputAllowed(_inputAllowed && !_inputTransparent && Owner.IsValid))
                throw new InvalidOperationException("The owned popup rejected input context admission.");
            context.ObservePolicyChange();
            return context;
        }
        catch { context.Dispose(); throw; }
    }

    internal void ReleaseInput(CocoaPopupInputContext context)
    {
        CheckThread();
        if (!ReferenceEquals(_inputContext, context)) return;
        _inputContext = null;
        if (_disposeRequested)
        {
            if (!_retiring) TryCompleteDispose();
        }
        else if (!_closing && !RequireSurface().SetInputAllowed(false))
            throw new InvalidOperationException("The owned popup could not stop detached input.");
    }

    internal bool SupportsCursor(StandardCursor cursor)
    {
        CheckUsable();
        return RequireSurface().SupportsCursor(cursor);
    }

    internal bool SetCursor(StandardCursor cursor, bool hidden)
    {
        CheckUsable();
        return RequireSurface().SetCursor(cursor, hidden);
    }

    internal void BlockFaultedInput()
    {
        CheckThread();
        if (!_closing && !_disposeRequested && !RequireSurface().SetInputAllowed(false))
            throw new InvalidOperationException("The owned popup rejected blocking faulted input.");
    }
    internal int ReadInput(Span<CocoaPopupPointerEvent> destination, out ulong generation)
    {
        CheckUsable();
        return RequireSurface().ReadInput(destination, out generation);
    }

    internal CocoaOwnedPopupSurface.RenderLease AcquireRenderLease()
    {
        CheckUsable();
        return RequireSurface().AcquireRenderLease();
    }

    internal bool SetInputAllowed(bool allowed)
    {
        CheckUsable();
        _inputAllowed = allowed;
        return ApplyInputPolicy();
    }

    internal bool SetInputTransparent(bool transparent)
    {
        CheckUsable();
        if (!_initialized) throw new InvalidOperationException("Initialize the owned popup before setting mouse pass-through.");
        _inputTransparent = transparent;
        return ApplyInputPolicy();
    }

    private bool ApplyInputPolicy()
    {
        try { return RequireSurface().SetInputAllowed(_inputAllowed && !_inputTransparent && Owner.IsValid && _inputContext is { AcceptsInput: true }); }
        finally { _inputContext?.ObservePolicyChange(); }
    }

    internal bool BindOwner(NativeWindowHandle owner)
    {
        CheckUsable();
        if (_bindingOwner || _showingOwned || _initializing)
            throw new InvalidOperationException("Popup ownership cannot change during an active native transition.");
        if (!_initialized || owner != NativeWindowHandle.Empty &&
            (owner.Kind != NativeWindowKind.Cocoa || !owner.IsValid || owner.Display != 0)) return false;
        if (owner == Owner) return TryGetGeometry(out _);
        // The original owner-scheduled factory retains its fixed managed parent
        // and wake callback. Only explicit source-scheduled popups may rebind.
        if (_visible || !_allowOwnerBinding) return false;
        _bindingOwner = true;
        ++_dispatchDepth;
        bool accepted = false;
        Exception? bindingFailure = null;
        try
        {
            var surface = RequireSurface();
            accepted = surface.BindOwner(owner) && !_closing && !_disposeRequested &&
                surface.SetInputAllowed(owner.IsValid && _inputAllowed && !_inputTransparent && _inputContext is { AcceptsInput: true });
            if (accepted && !_closing && !_disposeRequested) Owner = owner;
        }
        catch (Exception failure) { bindingFailure = failure; throw; }
        finally
        {
            try { _inputContext?.ObservePolicyChange(); }
            catch (Exception cleanup) when (bindingFailure is not null)
            {
                bindingFailure.Data["PopupOwnerCancellation"] = cleanup;
            }
            catch (Exception cleanup) { bindingFailure = cleanup; throw; }
            finally
            {
                _bindingOwner = false;
                try { EndDispatch(); }
                catch (Exception cleanup) when (bindingFailure is not null)
                {
                    bindingFailure.Data["PopupOwnerRetirement"] = cleanup;
                }
            }
        }
        return accepted && !_closing && !_disposeRequested;
    }

    internal bool ShowOwned(NativeWindowHandle owner, Action show)
    {
        CheckUsable();
        if (_showingOwned || _bindingOwner || _initializing)
            throw new InvalidOperationException("Popup display cannot reenter an active native transition.");
        if (!owner.IsValid || owner != Owner || _visible || !TryGetGeometry(out _))
            return false;
        _showingOwned = true;
        ++_dispatchDepth;
        Exception? failure = null;
        try
        {
            show();
            return !_closing && !_disposeRequested && _visible && owner == Owner && TryGetGeometry(out _);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            _showingOwned = false;
            try { EndDispatch(); }
            catch (Exception cleanup) when (failure is not null)
            {
                failure.Data["PopupShowRetirement"] = cleanup;
            }
        }
    }

    internal bool TryGetGeometry(out NativeWindowGeometrySnapshot snapshot)
    {
        CheckThread();
        snapshot = default;
        if (!_initialized || _closing || _disposeRequested || _surface is null ||
            !_surface.TryGetGeometry(out var current) || current.Window != _geometry.Window ||
            current.ContentView != _geometry.ContentView || current.CocoaWindowNumber != _geometry.CocoaWindowNumber)
            return false;
        snapshot = current;
        return true;
    }

    public IWindowHost Parent { get; }
    public INativeWindow? Native => NativeHandle.IsValid ? _native : null;
    // Silk's generic native-source contract requires a nonzero handle. This is
    // an NSPanel; Native.Glfw stays null and platform dispatch is explicitly typed.
    public nint Handle => NativeHandle.Handle;
    public bool IsInitialized => _initialized && !IsReleased;
    public bool IsClosing
    {
        get => _closing;
        set
        {
            CheckThread();
            if (value) Close();
            else if (_closing) throw new NotSupportedException("A closing owned popup cannot be reopened.");
        }
    }
    public bool IsVisible
    {
        get => _visible;
        set
        {
            CheckUsable();
            if (_visible == value) return;
            if (value && !Owner.IsValid)
                throw new InvalidOperationException("Bind a live source owner before showing this popup.");
            var surface = RequireSurface();
            bool accepted = value ? surface.Show() : surface.Hide();
            if (!accepted || _closing || _disposeRequested)
                throw new InvalidOperationException("The owned Cocoa popup rejected visibility.");
            _visible = value;
            _inputContext?.ObservePolicyChange();
        }
    }
    public Vector2D<int> Position
    {
        get => _position;
        set => SetBounds(value, _size);
    }
    public Vector2D<int> Size
    {
        get => _size;
        set => SetBounds(_position, value);
    }
    public Vector2D<int> FramebufferSize => _framebufferSize;
    public Rectangle<int> BorderSize => default;
    public double Time => Stopwatch.GetElapsedTime(_start).TotalSeconds;
    public GraphicsAPI API => GraphicsAPI.None;
    public bool TransparentFramebuffer { get; }
    public string WindowClass => "ProGPUOwnedPopupPanelV1";
    public IGLContext? GLContext => null;
    public IGLContext? SharedContext => null;
    public IVkSurface? VkSurface => null;
    public Vector4D<int>? PreferredBitDepth => null;
    public int? PreferredDepthBufferBits => null;
    public int? PreferredStencilBufferBits => null;
    public int? Samples => null;
    public VideoMode VideoMode => default;
    public IMonitor? Monitor
    {
        get => null; // Desktop placement, not an exclusive/fullscreen monitor.
        set => throw new NotSupportedException("Place an owned popup with desktop coordinates.");
    }
    public string Title
    {
        get => string.Empty;
        set { CheckUsable(); if (!string.IsNullOrEmpty(value)) throw new NotSupportedException("Owned popup panels are untitled."); }
    }
    public bool TopMost
    {
        get => false;
        set { CheckUsable(); if (value) throw new NotSupportedException("Popup stacking belongs to its native owner."); }
    }
    public WindowBorder WindowBorder
    {
        get => WindowBorder.Hidden;
        set { CheckUsable(); if (value != WindowBorder.Hidden) throw new NotSupportedException("Owned popup panels are borderless."); }
    }
    public WindowState WindowState
    {
        get => WindowState.Normal;
        set { CheckUsable(); if (value != WindowState.Normal) throw new NotSupportedException("Owned popup panels cannot minimize, maximize or enter fullscreen independently."); }
    }
    public bool IsContextControlDisabled
    {
        get => true;
        set { CheckUsable(); if (!value) throw new NotSupportedException("The shared WebGPU renderer owns presentation."); }
    }
    public bool ShouldSwapAutomatically
    {
        get => false;
        set { CheckUsable(); if (value) throw new NotSupportedException("The shared WebGPU renderer owns presentation."); }
    }
    public bool VSync { get => _vsync; set { CheckUsable(); _vsync = value; } }
    public bool IsEventDriven { get => _eventDriven; set { CheckUsable(); _eventDriven = value; } }
    public double FramesPerSecond
    {
        get => 0;
        set { CheckUsable(); if (value != 0) throw new NotSupportedException("The source host owns frame scheduling."); }
    }
    public double UpdatesPerSecond
    {
        get => 0;
        set { CheckUsable(); if (value != 0) throw new NotSupportedException("The source host owns update scheduling."); }
    }

    public event Action? Load;
    public event Action? Closing;
    public event Action<double>? Update;
    public event Action<double>? Render;
    public event Action<Vector2D<int>>? Move;
    public event Action<Vector2D<int>>? Resize;
    public event Action<Vector2D<int>>? FramebufferResize;
    // These panels never become key, change normal-window state, or register
    // native file-drop destinations. Subscriptions do not fabricate such events.
    public event Action<bool>? FocusChanged { add { CheckThread(); } remove { CheckThread(); } }
    public event Action<WindowState>? StateChanged { add { CheckThread(); } remove { CheckThread(); } }
    public event Action<string[]>? FileDrop { add { CheckThread(); } remove { CheckThread(); } }

    public void Initialize()
    {
        CheckUsable();
        if (_initialized) return;
        if (_initializing) throw new InvalidOperationException("Owned popup initialization cannot be nested.");
        _initializing = true;
        // Assign ownership before any callback or read that could fail. Cleanup
        // failures leave the surface here for a later explicit retirement retry.
        try
        {
            _surface = _create(new(_position.X, _position.Y, _size.X, _size.Y), TransparentFramebuffer);
            CheckUsable(); // Native creation may have reentered close/disposal.
            PublishGeometry(ReadGeometry(), notify: false);
            _initialized = true;
            _lastUpdate = _lastRender = Time;
            ++_dispatchDepth;
            try { Load?.Invoke(); }
            finally { EndDispatch(); }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            _initializing = false;
            if (_disposeRequested) TryCompleteDispose();
        }
    }

    // The source owner already polls GLFW/AppKit (or the modal session). Polling
    // here would recursively dispatch unrelated native windows a second time.
    public void DoEvents()
    {
        CheckThread();
        if (_disposeRequested) { TryCompleteDispose(); return; }
        if (_closing) return;
        CheckNotDispatching();
        ++_dispatchDepth;
        try { ProcessPending(); }
        finally { EndDispatch(); }
    }

    public void DoUpdate()
    {
        CheckThread();
        if (_disposeRequested) { TryCompleteDispose(); return; }
        if (_closing) return;
        CheckNotDispatching();
        ++_dispatchDepth;
        try
        {
            ProcessPending();
            if (_closing || _disposeRequested) return;
            double now = Time, elapsed = now - _lastUpdate;
            _lastUpdate = now;
            Update?.Invoke(elapsed);
        }
        finally { EndDispatch(); }
    }

    public void DoRender()
    {
        CheckThread();
        if (_disposeRequested) { TryCompleteDispose(); return; }
        if (_closing || !_visible) return;
        CheckNotDispatching();
        RequireSurface();
        ++_dispatchDepth;
        try
        {
            double now = Time, elapsed = now - _lastRender;
            _lastRender = now;
            Render?.Invoke(elapsed);
        }
        finally { EndDispatch(); }
    }

    public void Close()
    {
        CheckThread();
        if (_closing) return;
        _closing = true;
        _visible = false;
        if (_surface is not null && !_disposeRequested && !_surface.Hide())
            throw new InvalidOperationException("The closing owned popup could not be hidden.");
        _inputContext?.ObservePolicyChange();
        ++_dispatchDepth;
        try { Closing?.Invoke(); }
        finally { EndDispatch(); }
    }

    public void Dispose()
    {
        CheckThread();
        _disposeRequested = _closing = true;
        _visible = false;
        _surface?.CloseInput();
        _inputContext?.ObservePolicyChange();
        TryCompleteDispose();
    }

    internal bool TryCompleteDispose()
    {
        CheckThread();
        if (!_disposeRequested || _initializing || _dispatchDepth != 0 || _retiring || IsInNativeInputCallback) return false;
        _retiring = true;
        try
        {
            _inputContext?.Dispose();
            if (_inputContext is not null) return false;
            if (_surface is not null)
            {
                _surface.Dispose();
                if (!_surface.IsReleased) return false;
            }
            _initialized = false;
            Load = Closing = InputPending = null;
            Update = Render = null;
            Move = Resize = FramebufferResize = null;
            return true;
        }
        finally { _retiring = false; }
    }

    public void ContinueEvents() { CheckThread(); if (!_disposeRequested) _wakeHost(); }
    public void Focus() => throw new NotSupportedException("Keyboard focus stays with the source popup owner.");
    public void Reset() => throw new NotSupportedException("Create a new owned popup after disposal.");
    public void Run(Action onFrame) => throw new NotSupportedException("The source owner runs the native loop.");
    public object Invoke(Delegate method, params object[] args) =>
        throw new NotSupportedException("Use the source owner's dispatcher.");
    public IWindow CreateWindow(WindowOptions options) =>
        throw new NotSupportedException("Create native popup children through the shared owned-popup factory.");
    public void SetWindowIcon(ReadOnlySpan<RawImage> icons)
    {
        CheckUsable();
        if (!icons.IsEmpty) throw new NotSupportedException("Owned popup panels do not have a taskbar icon.");
    }

    public Vector2D<int> PointToClient(Vector2D<int> point)
    {
        var geometry = ReadGeometry();
        return new(Round(point.X - geometry.ContentBounds.X), Round(point.Y - geometry.ContentBounds.Y));
    }
    public Vector2D<int> PointToScreen(Vector2D<int> point)
    {
        var geometry = ReadGeometry();
        return new(Round(point.X + geometry.ContentBounds.X), Round(point.Y + geometry.ContentBounds.Y));
    }
    public Vector2D<int> PointToFramebuffer(Vector2D<int> point)
    {
        var geometry = ReadGeometry();
        return new(Round(point.X * geometry.BackingScale), Round(point.Y * geometry.BackingScale));
    }

    private void ProcessPending()
    {
        PublishGeometry(ReadGeometry(), notify: true);
        if (!_closing && !_disposeRequested) InputPending?.Invoke();
    }

    private void SetBounds(Vector2D<int> position, Vector2D<int> size)
    {
        CheckUsable();
        ValidateSize(size);
        if (_initializing && !_initialized)
            throw new InvalidOperationException("Owned popup bounds cannot change during native creation.");
        if (!_initialized) { _position = position; _size = size; return; }
        if (!RequireSurface().SetBounds(new(position.X, position.Y, size.X, size.Y)))
            throw new InvalidOperationException("The owned Cocoa popup rejected its bounds.");
        ++_dispatchDepth;
        try { PublishGeometry(ReadGeometry(), notify: true); }
        finally { EndDispatch(); }
    }

    private NativeWindowGeometrySnapshot ReadGeometry()
    {
        CheckUsable();
        if (!RequireSurface().TryGetGeometry(out var geometry))
            throw new InvalidOperationException("The owned Cocoa popup geometry is unavailable.");
        return geometry;
    }

    private void PublishGeometry(NativeWindowGeometrySnapshot geometry, bool notify)
    {
        var bounds = geometry.ContentBounds;
        if (geometry.Window.Kind != NativeWindowKind.Cocoa || !geometry.Window.IsValid ||
            geometry.ContentView == 0 || geometry.CocoaWindowNumber <= 0 ||
            !double.IsFinite(geometry.BackingScale) || geometry.BackingScale <= 0 ||
            bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Invalid owned Cocoa popup geometry.");
        if (_initialized && (geometry.Window != _geometry.Window ||
            geometry.ContentView != _geometry.ContentView ||
            geometry.CocoaWindowNumber != _geometry.CocoaWindowNumber))
            throw new InvalidOperationException("The owned Cocoa popup geometry changed identity.");
        var position = new Vector2D<int>(Round(bounds.X), Round(bounds.Y));
        var size = new Vector2D<int>(Round(bounds.Width), Round(bounds.Height));
        // Desktop coordinates remain native points; only the presentation extent
        // is converted to backing pixels. Never scale a global desktop origin.
        var framebuffer = new Vector2D<int>(Round(bounds.Width * geometry.BackingScale),
            Round(bounds.Height * geometry.BackingScale));
        ValidateSize(size);
        ValidateSize(framebuffer);
        bool moved = position != _position, resized = size != _size, scaled = framebuffer != _framebufferSize;
        _geometry = geometry;
        _position = position; _size = size; _framebufferSize = framebuffer;
        ulong version = ++_geometryVersion;
        if (!notify) return;
        if (moved) Move?.Invoke(position);
        if (_closing || _disposeRequested || version != _geometryVersion) return;
        if (resized) Resize?.Invoke(size);
        if (_closing || _disposeRequested || version != _geometryVersion) return;
        if (scaled) FramebufferResize?.Invoke(framebuffer);
    }

    private void EndDispatch()
    {
        --_dispatchDepth;
        if (_disposeRequested) TryCompleteDispose();
    }
    private CocoaOwnedPopupSurface RequireSurface() => _surface ??
        throw new InvalidOperationException("Initialize the owned Cocoa popup first.");
    private void CheckUsable()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        if (_closing) throw new InvalidOperationException("The owned Cocoa popup is closing.");
    }
    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Owned Cocoa popup operations require their creating thread.");
    }
    private void CheckNotDispatching()
    {
        if (_dispatchDepth != 0) throw new InvalidOperationException("Owned popup dispatch cannot be nested.");
    }
    private static int Round(double value) => checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    private static void ValidateSize(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0) throw new ArgumentOutOfRangeException(nameof(size));
    }

    private sealed class NativeView(CocoaPopupWindow owner) : INativeWindow
    {
        public NativeWindowFlags Kind => NativeWindowFlags.Cocoa;
        public nint? Cocoa => owner.NativeHandle.IsValid ? owner.NativeHandle.Handle : null;
        public nint? Glfw => null;
        public nint? Sdl => null;
        public nint? DXHandle => null;
        public nint? WinRT => null;
        public (nint, nint)? Android => null;
        public (nint?, nint?)? EGL => null;
        public (nint, uint, uint, uint)? UIKit => null;
        public (nint, nint)? Vivante => null;
        public (nint, nint)? Wayland => null;
        public (nint, nint, nint)? Win32 => null;
        public (nint, nuint)? X11 => null;
    }
}
