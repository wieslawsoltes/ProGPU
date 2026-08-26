using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Avalonia.Input;
using Avalonia.Input.Raw;
using ProGPU.Backend;
using Silk.NET.GLFW;
using Silk.NET.Input;
using Silk.NET.Windowing;
using AvaloniaKey = Avalonia.Input.Key;
using GlfwInputAction = Silk.NET.GLFW.InputAction;
using GlfwKey = Silk.NET.GLFW.Keys;
using GlfwKeyModifiers = Silk.NET.GLFW.KeyModifiers;
using GlfwWindowHandle = Silk.NET.GLFW.WindowHandle;
using SilkInputDevice = Silk.NET.Input.IInputDevice;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace Avalonia.SilkNet;

/// <summary>
/// Routes Silk input devices directly into Avalonia raw input contracts.
/// </summary>
/// <remarks>
/// Pointer and key events use O(1) work and allocate only when text input
/// requires a managed string.
/// </remarks>
internal sealed unsafe class SilkNetInputRouter : IDisposable
{
    private static readonly bool s_traceChromeDrag =
        string.Equals(
            Environment.GetEnvironmentVariable(
                "PROGPU_AVALONIA_TRACE_CHROME_DRAG"),
            "1",
            StringComparison.Ordinal);
    private static readonly string? s_chromeDragTracePath =
        Environment.GetEnvironmentVariable(
            "PROGPU_AVALONIA_TRACE_WINDOW_EVENTS_PATH");
    private readonly WindowImpl _owner;
    private readonly SilkNetClipboard _clipboard;
    private readonly MouseDevice _mouseDevice = new();
    private readonly TouchDevice _touchDevice = new();
    private readonly IKeyboardDevice _keyboardDevice;
    private readonly List<IKeyboard> _keyboards = [];
    private readonly List<IMouse> _mice = [];
    private readonly HashSet<uint> _emulatedTouchIds = [];
    private readonly Glfw _glfw = Glfw.GetApi();
    private IInputContext? _context;
    private IInputRoot? _inputRoot;
    private X11TouchInputSource? _x11Touch;
    private MacOsGestureInputSource? _macOsGestures;
    private GlfwWindowHandle* _glfwWindow;
    private GlfwCallbacks.KeyCallback? _keyCallback;
    private GlfwCallbacks.KeyCallback? _previousKeyCallback;
    private GlfwCallbacks.CharCallback? _charCallback;
    private GlfwCallbacks.CharCallback? _previousCharCallback;
    private GlfwCallbacks.CursorEnterCallback? _cursorEnterCallback;
    private GlfwCallbacks.CursorEnterCallback? _previousCursorEnterCallback;
    private RawInputModifiers _pointerButtons;
    private RawInputModifiers _glfwKeyModifiers;
    private Vector2 _lastPointerPosition;
    private bool _hasPointerPosition;
    private bool _pointerInside;
    private bool _insideGlfwKeyCallback;
    private bool _suppressPromotedMouseForPoll;
    private bool _chromeDragActive;

    internal SilkNetInputRouter(
        WindowImpl owner,
        SilkNetClipboard clipboard)
    {
        _owner = owner;
        _clipboard = clipboard;
        _keyboardDevice =
            AvaloniaLocator.Current
                .GetRequiredService<IKeyboardDevice>();
    }

    internal void SetInputRoot(IInputRoot inputRoot) =>
        _inputRoot = inputRoot;

    internal void Attach(IWindow window)
    {
        if (_context is not null)
            return;

        _context = window.CreateInput();
        _context.ConnectionChanged += OnConnectionChanged;
        foreach (IKeyboard keyboard in _context.Keyboards)
            AttachKeyboard(keyboard);
        foreach (IMouse mouse in _context.Mice)
            AttachMouse(mouse);
        AttachGlfwCallbacks(window);
        _owner.SetNativeTouchHandler(OnNativeTouch);
        _x11Touch = X11TouchInputSource.TryCreate(
            window,
            OnNativeTouch);
        _macOsGestures = MacOsGestureInputSource.TryCreate(
            window,
            OnMacOsGesture);
    }

    internal void ApplyCursor(SilkNetCursorImpl? cursor)
    {
        foreach (IMouse mouse in _mice)
            (cursor ?? new SilkNetCursorImpl(StandardCursor.Arrow))
                .Apply(mouse.Cursor);
    }

    internal NativeWindowPoint CurrentNativePointer =>
        _owner.ToNativeScreenPoint(
            _lastPointerPosition.X,
            _lastPointerPosition.Y);

    internal void ProcessNativeState(IWindow window)
    {
        bool hovered = _owner.Platform.Monitors
            .IsWindowHovered(window.Handle);
        if (!ShouldEmitPointerLeave(_pointerInside, hovered))
            return;

        _pointerInside = false;
        EmitPointer(
            _lastPointerPosition,
            RawPointerEventType.LeaveWindow);
    }

    internal void PollSupplementalEvents() => _x11Touch?.Poll();

    internal void CompleteSupplementalEventPoll() =>
        _suppressPromotedMouseForPoll = false;

    internal static bool ShouldEmitPointerLeave(
        bool wasInside,
        bool isHovered) =>
        wasInside && !isHovered;

    public void Dispose()
    {
        IInputContext? context = _context;
        _context = null;
        if (context is null)
            return;

        context.ConnectionChanged -= OnConnectionChanged;
        _owner.SetNativeTouchHandler(null);
        _x11Touch?.Dispose();
        _x11Touch = null;
        _macOsGestures?.Dispose();
        _macOsGestures = null;
        DetachGlfwCallbacks();
        while (_keyboards.Count > 0)
            DetachKeyboard(_keyboards[^1]);
        while (_mice.Count > 0)
            DetachMouse(_mice[^1]);
        context.Dispose();
        _mouseDevice.Dispose();
        _touchDevice.Dispose();
    }

    private void OnConnectionChanged(
        SilkInputDevice device,
        bool connected)
    {
        if (device is IKeyboard keyboard)
        {
            if (connected)
                AttachKeyboard(keyboard);
            else
                DetachKeyboard(keyboard);
        }

        if (device is IMouse mouse)
        {
            if (connected)
                AttachMouse(mouse);
            else
                DetachMouse(mouse);
        }
    }

    private void AttachKeyboard(IKeyboard keyboard)
    {
        if (_keyboards.Contains(keyboard))
            return;
        _keyboards.Add(keyboard);
        keyboard.KeyDown += OnKeyDown;
        keyboard.KeyUp += OnKeyUp;
        _clipboard.AttachKeyboard(keyboard);
    }

    private void DetachKeyboard(IKeyboard keyboard)
    {
        if (!_keyboards.Remove(keyboard))
            return;
        keyboard.KeyDown -= OnKeyDown;
        keyboard.KeyUp -= OnKeyUp;
        _clipboard.DetachKeyboard(keyboard);
    }

    private void AttachMouse(IMouse mouse)
    {
        if (_mice.Contains(mouse))
            return;
        _mice.Add(mouse);
        mouse.MouseDown += OnMouseDown;
        mouse.MouseUp += OnMouseUp;
        mouse.MouseMove += OnMouseMove;
        mouse.Scroll += OnMouseScroll;
    }

    private void DetachMouse(IMouse mouse)
    {
        if (!_mice.Remove(mouse))
            return;
        mouse.MouseDown -= OnMouseDown;
        mouse.MouseUp -= OnMouseUp;
        mouse.MouseMove -= OnMouseMove;
        mouse.Scroll -= OnMouseScroll;
    }

    private void OnKeyDown(
        IKeyboard keyboard,
        SilkKey key,
        int scanCode) =>
        EmitKey(
            keyboard,
            key,
            scanCode,
            RawKeyEventType.KeyDown);

    private void OnKeyUp(
        IKeyboard keyboard,
        SilkKey key,
        int scanCode) =>
        EmitKey(
            keyboard,
            key,
            scanCode,
            RawKeyEventType.KeyUp);

    private void EmitKey(
        IKeyboard keyboard,
        SilkKey key,
        int scanCode,
        RawKeyEventType eventType)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.TryAcceptInput())
            return;

        SilkNetKeyMapping mapped =
            SilkNetInputMappings.MapKey(key);
        _owner.EmitInput(
            new RawKeyEventArgs(
                _keyboardDevice,
                Timestamp(),
                root,
                eventType,
                mapped.Key,
                ReadModifiers(keyboard),
                mapped.PhysicalKey,
                ResolveKeySymbol(key, scanCode)));
    }

    private void EmitText(string text)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.TryAcceptInput())
            return;

        _owner.EmitInput(
            new RawTextInputEventArgs(
                _keyboardDevice,
                Timestamp(),
                root,
                text));
    }

    private void AttachGlfwCallbacks(IWindow window)
    {
        nint handle = window.Native?.Glfw ?? window.Handle;
        if (handle == 0)
            return;

        _glfwWindow = (GlfwWindowHandle*)handle;
        _keyCallback = OnGlfwKey;
        _charCallback = OnGlfwChar;
        _cursorEnterCallback = OnGlfwCursorEnter;
        _previousKeyCallback =
            _glfw.SetKeyCallback(_glfwWindow, _keyCallback);
        _previousCharCallback =
            _glfw.SetCharCallback(_glfwWindow, _charCallback);
        _previousCursorEnterCallback =
            _glfw.SetCursorEnterCallback(
                _glfwWindow,
                _cursorEnterCallback);
    }

    private void DetachGlfwCallbacks()
    {
        GlfwWindowHandle* window = _glfwWindow;
        _glfwWindow = null;
        if (window is null)
            return;

        _glfw.SetKeyCallback(
            window,
            _previousKeyCallback!);
        _glfw.SetCharCallback(
            window,
            _previousCharCallback!);
        _glfw.SetCursorEnterCallback(
            window,
            _previousCursorEnterCallback!);
        _keyCallback = null;
        _previousKeyCallback = null;
        _charCallback = null;
        _previousCharCallback = null;
        _cursorEnterCallback = null;
        _previousCursorEnterCallback = null;
    }

    private void OnGlfwKey(
        GlfwWindowHandle* window,
        GlfwKey key,
        int scanCode,
        GlfwInputAction action,
        GlfwKeyModifiers modifiers)
    {
        bool previousInsideCallback = _insideGlfwKeyCallback;
        RawInputModifiers previousModifiers = _glfwKeyModifiers;
        _insideGlfwKeyCallback = true;
        _glfwKeyModifiers = MapGlfwModifiers(modifiers);
        try
        {
            _previousKeyCallback?.Invoke(
                window,
                key,
                scanCode,
                action,
                modifiers);
            if (action != GlfwInputAction.Repeat ||
                _keyboards.Count == 0)
            {
                return;
            }

            EmitKey(
                _keyboards[0],
                (SilkKey)(int)key,
                scanCode,
                RawKeyEventType.KeyDown);
        }
        finally
        {
            _insideGlfwKeyCallback = previousInsideCallback;
            _glfwKeyModifiers = previousModifiers;
        }
    }

    private void OnGlfwChar(
        GlfwWindowHandle* window,
        uint codePoint)
    {
        _previousCharCallback?.Invoke(window, codePoint);
        string? text = ConvertUnicodeScalar(codePoint);
        if (text is not null)
            EmitText(text);
    }

    private void OnGlfwCursorEnter(
        GlfwWindowHandle* window,
        bool entered)
    {
        _previousCursorEnterCallback?.Invoke(window, entered);
        if (entered)
        {
            _pointerInside = true;
            return;
        }

        if (!_pointerInside)
            return;
        _pointerInside = false;
        EmitPointer(
            _lastPointerPosition,
            RawPointerEventType.LeaveWindow);
    }

    private string? ResolveKeySymbol(
        SilkKey key,
        int scanCode)
    {
        string? symbol =
            _glfw.GetKeyName((int)key, scanCode);
        return string.IsNullOrEmpty(symbol)
            ? null
            : symbol;
    }

    internal static string? ConvertUnicodeScalar(uint codePoint)
    {
        if (codePoint > 0x10ffff ||
            codePoint is >= 0xd800 and <= 0xdfff)
        {
            return null;
        }

        return char.ConvertFromUtf32((int)codePoint);
    }

    private void OnNativeTouch(NativeTouchEvent touch)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.TryAcceptInput())
            return;

        Point position = ToLogicalPoint(
            new Vector2((float)touch.X, (float)touch.Y),
            _owner.NativeCoordinateScaling);
        var point = new RawPointerPoint
        {
            Position = position,
            ContactRect = new Rect(
                position.X - touch.ContactWidth /
                    _owner.NativeCoordinateScaling / 2d,
                position.Y - touch.ContactHeight /
                    _owner.NativeCoordinateScaling / 2d,
                touch.ContactWidth /
                    _owner.NativeCoordinateScaling,
                touch.ContactHeight /
                    _owner.NativeCoordinateScaling)
        };
        RawInputModifiers modifiers =
            _keyboards.Count > 0
                ? ReadModifiers(_keyboards[0])
                : RawInputModifiers.None;
        if (touch.IsPointerEmulation &&
            touch.Phase == NativeTouchPhase.Begin)
        {
            _emulatedTouchIds.Add(touch.Id);
        }
        if (touch.IsPointerEmulation)
            _suppressPromotedMouseForPoll = true;

        _owner.EmitInput(
            new RawTouchEventArgs(
                _touchDevice,
                touch.Timestamp,
                root,
                MapTouchPhase(touch.Phase),
                point,
                modifiers,
                touch.Id));
        if (touch.Phase is NativeTouchPhase.End or NativeTouchPhase.Cancel)
            _emulatedTouchIds.Remove(touch.Id);
    }

    internal static RawPointerEventType MapTouchPhase(
        NativeTouchPhase phase) => phase switch
        {
            NativeTouchPhase.Begin => RawPointerEventType.TouchBegin,
            NativeTouchPhase.Update => RawPointerEventType.TouchUpdate,
            NativeTouchPhase.End => RawPointerEventType.TouchEnd,
            _ => RawPointerEventType.TouchCancel
        };

    private void OnMacOsGesture(MacOsGestureEvent gesture)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.TryAcceptInput())
            return;

        RawInputModifiers modifiers =
            _keyboards.Count > 0
                ? ReadModifiers(_keyboards[0])
                : RawInputModifiers.None;
        Point position = ToLogicalPoint(
            _lastPointerPosition,
            _owner.NativeCoordinateScaling);
        _owner.EmitInput(
            new RawPointerGestureEventArgs(
                _mouseDevice,
                Timestamp(),
                root,
                MapMacOsGesture(gesture.Kind),
                position,
                new Vector(gesture.DeltaX, gesture.DeltaY),
                modifiers));
    }

    internal static RawPointerEventType MapMacOsGesture(
        MacOsGestureKind kind) => kind switch
        {
            MacOsGestureKind.Magnify => RawPointerEventType.Magnify,
            MacOsGestureKind.Rotate => RawPointerEventType.Rotate,
            _ => RawPointerEventType.Swipe
        };

    private void OnMouseDown(
        IMouse mouse,
        SilkMouseButton button)
    {
        if (ShouldSuppressPromotedMouse())
            return;
        Vector2 position = ResolvePointerPosition(
            _hasPointerPosition,
            _lastPointerPosition,
            mouse.Position);
        _pointerInside = true;
        _hasPointerPosition = true;
        _lastPointerPosition = position;
        RawPointerEventType? eventType =
            MapButton(button, pressed: true);
        if (eventType is null)
            return;
        if (button == SilkMouseButton.Left)
            _chromeDragActive = false;
        if (button == SilkMouseButton.Left &&
            TryBeginChromeDrag(position))
        {
            _chromeDragActive = true;
            return;
        }
        _pointerButtons |= ButtonModifier(button);
        EmitPointer(
            position,
            eventType.Value);
    }

    private void OnMouseUp(
        IMouse mouse,
        SilkMouseButton button)
    {
        if (ShouldSuppressPromotedMouse())
            return;
        Vector2 position = ResolvePointerPosition(
            _hasPointerPosition,
            _lastPointerPosition,
            mouse.Position);
        _pointerInside = true;
        _hasPointerPosition = true;
        _lastPointerPosition = position;
        if (button == SilkMouseButton.Left && _chromeDragActive)
        {
            _chromeDragActive = false;
            _owner.EndNativeDrag();
            return;
        }
        _owner.UpdateNativeDrag(
            CurrentNativePointer);
        if (button == SilkMouseButton.Left)
            _owner.EndNativeDrag();
        RawPointerEventType? eventType =
            MapButton(button, pressed: false);
        if (eventType is null)
            return;
        _pointerButtons &= ~ButtonModifier(button);
        EmitPointer(
            position,
            eventType.Value);
    }

    private bool TryBeginChromeDrag(Vector2 position)
    {
#if AVALONIA11
        _ = position;
        return false;
#else
        IInputRoot? root = _inputRoot;
        if (root is null)
            return false;

        Point logicalPosition = ToPoint(position);
        WindowDecorationsElementRole? role =
            SilkNetWindowChrome.ResolveChromeRole(
                root,
                logicalPosition);
        if (role == WindowDecorationsElementRole.TitleBar)
        {
            bool started = _owner.BeginNativeMoveDrag();
            if (s_traceChromeDrag)
            {
                TraceChromeDrag(
                    $"title start={started} position={position}");
            }
            return started;
        }

        if (role is { } resizeRole &&
            SilkNetWindowChrome.TryMapResizeRole(
                resizeRole,
                out NativeResizeEdge edge))
        {
            bool started = _owner.BeginNativeResizeDrag(edge);
            if (s_traceChromeDrag)
            {
                TraceChromeDrag(
                    $"resize edge={edge} start={started} " +
                    $"position={position}");
            }
            return started;
        }

        if (s_traceChromeDrag)
        {
            TraceChromeDrag(
                $"no role position={position} " +
                $"logical={logicalPosition}");
        }
        return false;
#endif
    }

    private void OnMouseMove(
        IMouse mouse,
        Vector2 position)
    {
        if (ShouldSuppressPromotedMouse())
            return;
        _pointerInside = true;
        _hasPointerPosition = true;
        _lastPointerPosition = position;
        if (s_traceChromeDrag)
        {
            TraceChromeDrag(
                $"pointer move position={position} " +
                $"logical={ToPoint(position)}");
        }
        bool dragUpdated = _owner.UpdateNativeDrag(
            CurrentNativePointer);
        if (dragUpdated && s_traceChromeDrag)
            TraceChromeDrag($"update position={position}");
        EmitPointer(
            position,
            RawPointerEventType.Move);
    }

    private static void TraceChromeDrag(string message)
    {
        string line = $"[Avalonia.SilkNet] chrome drag: {message}";
        string? path = s_chromeDragTracePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine(line);
            return;
        }

        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (IOException)
        {
            Console.Error.WriteLine(line);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine(line);
        }
    }

    private void OnMouseScroll(
        IMouse mouse,
        ScrollWheel wheel)
    {
        if (ShouldSuppressPromotedMouse())
            return;
        Vector2 position = ResolvePointerPosition(
            _hasPointerPosition,
            _lastPointerPosition,
            mouse.Position);
        _pointerInside = true;
        _hasPointerPosition = true;
        _lastPointerPosition = position;
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.TryAcceptInput())
            return;

        _owner.EmitInput(
            new RawMouseWheelEventArgs(
                _mouseDevice,
                Timestamp(),
                root,
                ToPoint(position),
                new Vector(wheel.X, wheel.Y),
                ReadModifiers() | _pointerButtons));
    }

    internal static Vector2 ResolvePointerPosition(
        bool hasCallbackPosition,
        Vector2 callbackPosition,
        Vector2 reportedPosition) =>
        hasCallbackPosition ? callbackPosition : reportedPosition;

    private void EmitPointer(
        Vector2 position,
        RawPointerEventType eventType)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.TryAcceptInput())
            return;

        _owner.EmitInput(
            new RawPointerEventArgs(
                _mouseDevice,
                Timestamp(),
                root,
                eventType,
                ToPoint(position),
                ReadModifiers() | _pointerButtons));
    }

    private bool ShouldSuppressPromotedMouse() =>
        _owner.IsProcessingPromotedTouchMouse ||
        _suppressPromotedMouseForPoll ||
        _emulatedTouchIds.Count > 0;

    private RawInputModifiers ReadModifiers()
    {
        IKeyboard? keyboard =
            _keyboards.Count == 0 ? null : _keyboards[0];
        return keyboard is null
            ? RawInputModifiers.None
            : ReadModifiers(keyboard);
    }

    private RawInputModifiers ReadModifiers(
        IKeyboard keyboard)
    {
        if (_insideGlfwKeyCallback)
            return _glfwKeyModifiers;

        RawInputModifiers result = RawInputModifiers.None;
        if (keyboard.IsKeyPressed(SilkKey.AltLeft) ||
            keyboard.IsKeyPressed(SilkKey.AltRight))
            result |= RawInputModifiers.Alt;
        if (keyboard.IsKeyPressed(SilkKey.ControlLeft) ||
            keyboard.IsKeyPressed(SilkKey.ControlRight))
            result |= RawInputModifiers.Control;
        if (keyboard.IsKeyPressed(SilkKey.ShiftLeft) ||
            keyboard.IsKeyPressed(SilkKey.ShiftRight))
            result |= RawInputModifiers.Shift;
        if (keyboard.IsKeyPressed(SilkKey.SuperLeft) ||
            keyboard.IsKeyPressed(SilkKey.SuperRight))
            result |= RawInputModifiers.Meta;
        return result;
    }

    internal static RawInputModifiers MapGlfwModifiers(
        GlfwKeyModifiers modifiers)
    {
        RawInputModifiers result = RawInputModifiers.None;
        if ((modifiers & GlfwKeyModifiers.Alt) != 0)
            result |= RawInputModifiers.Alt;
        if ((modifiers & GlfwKeyModifiers.Control) != 0)
            result |= RawInputModifiers.Control;
        if ((modifiers & GlfwKeyModifiers.Shift) != 0)
            result |= RawInputModifiers.Shift;
        if ((modifiers & GlfwKeyModifiers.Super) != 0)
            result |= RawInputModifiers.Meta;
        return result;
    }

    private static RawPointerEventType? MapButton(
        SilkMouseButton button,
        bool pressed) =>
        button switch
        {
            SilkMouseButton.Left =>
                pressed
                    ? RawPointerEventType.LeftButtonDown
                    : RawPointerEventType.LeftButtonUp,
            SilkMouseButton.Right =>
                pressed
                    ? RawPointerEventType.RightButtonDown
                    : RawPointerEventType.RightButtonUp,
            SilkMouseButton.Middle =>
                pressed
                    ? RawPointerEventType.MiddleButtonDown
                    : RawPointerEventType.MiddleButtonUp,
            SilkMouseButton.Button4 =>
                pressed
                    ? RawPointerEventType.XButton1Down
                    : RawPointerEventType.XButton1Up,
            SilkMouseButton.Button5 =>
                pressed
                    ? RawPointerEventType.XButton2Down
                    : RawPointerEventType.XButton2Up,
            _ => null
        };

    private static RawInputModifiers ButtonModifier(
        SilkMouseButton button) =>
        button switch
        {
            SilkMouseButton.Left =>
                RawInputModifiers.LeftMouseButton,
            SilkMouseButton.Right =>
                RawInputModifiers.RightMouseButton,
            SilkMouseButton.Middle =>
                RawInputModifiers.MiddleMouseButton,
            SilkMouseButton.Button4 =>
                RawInputModifiers.XButton1MouseButton,
            SilkMouseButton.Button5 =>
                RawInputModifiers.XButton2MouseButton,
            _ => RawInputModifiers.None
        };

    private Point ToPoint(Vector2 point) =>
        ToLogicalPoint(
            point,
            _owner.NativeCoordinateScaling);

    internal static Point ToLogicalPoint(
        Vector2 point,
        double desktopScaling)
    {
        double scaling =
            DisplayScaleResolver.NormalizeDisplayScale(
                desktopScaling);
        return new Point(
            point.X / scaling,
            point.Y / scaling);
    }

    private static ulong Timestamp() =>
        (ulong)(Stopwatch.GetTimestamp() *
                (1000.0 / Stopwatch.Frequency));
}
