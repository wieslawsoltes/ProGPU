using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Silk.NET.Input;
using Silk.NET.Windowing;
using AvaloniaKey = Avalonia.Input.Key;
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
internal sealed class SilkNetInputRouter : IDisposable
{
    private readonly WindowImpl _owner;
    private readonly SilkNetClipboard _clipboard;
    private readonly MouseDevice _mouseDevice = new();
    private readonly IKeyboardDevice _keyboardDevice;
    private readonly List<IKeyboard> _keyboards = [];
    private readonly List<IMouse> _mice = [];
    private IInputContext? _context;
    private IInputRoot? _inputRoot;
    private RawInputModifiers _pointerButtons;

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
    }

    internal void ApplyCursor(SilkNetCursorImpl? cursor)
    {
        foreach (IMouse mouse in _mice)
            (cursor ?? new SilkNetCursorImpl(StandardCursor.Arrow))
                .Apply(mouse.Cursor);
    }

    public void Dispose()
    {
        IInputContext? context = _context;
        _context = null;
        if (context is null)
            return;

        context.ConnectionChanged -= OnConnectionChanged;
        while (_keyboards.Count > 0)
            DetachKeyboard(_keyboards[^1]);
        while (_mice.Count > 0)
            DetachMouse(_mice[^1]);
        context.Dispose();
        _mouseDevice.Dispose();
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
        keyboard.KeyChar += OnKeyChar;
        _clipboard.AttachKeyboard(keyboard);
    }

    private void DetachKeyboard(IKeyboard keyboard)
    {
        if (!_keyboards.Remove(keyboard))
            return;
        keyboard.KeyDown -= OnKeyDown;
        keyboard.KeyUp -= OnKeyUp;
        keyboard.KeyChar -= OnKeyChar;
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
            RawKeyEventType.KeyDown);

    private void OnKeyUp(
        IKeyboard keyboard,
        SilkKey key,
        int scanCode) =>
        EmitKey(
            keyboard,
            key,
            RawKeyEventType.KeyUp);

    private void EmitKey(
        IKeyboard keyboard,
        SilkKey key,
        RawKeyEventType eventType)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.AcceptsInput)
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
                keySymbol: null));
    }

    private void OnKeyChar(
        IKeyboard keyboard,
        char character)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.AcceptsInput)
            return;

        _owner.EmitInput(
            new RawTextInputEventArgs(
                _keyboardDevice,
                Timestamp(),
                root,
                character.ToString()));
    }

    private void OnMouseDown(
        IMouse mouse,
        SilkMouseButton button)
    {
        RawPointerEventType? eventType =
            MapButton(button, pressed: true);
        if (eventType is null)
            return;
        _pointerButtons |= ButtonModifier(button);
        EmitPointer(
            mouse.Position,
            eventType.Value);
    }

    private void OnMouseUp(
        IMouse mouse,
        SilkMouseButton button)
    {
        RawPointerEventType? eventType =
            MapButton(button, pressed: false);
        if (eventType is null)
            return;
        _pointerButtons &= ~ButtonModifier(button);
        EmitPointer(
            mouse.Position,
            eventType.Value);
    }

    private void OnMouseMove(
        IMouse mouse,
        Vector2 position) =>
        EmitPointer(
            position,
            RawPointerEventType.Move);

    private void OnMouseScroll(
        IMouse mouse,
        ScrollWheel wheel)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.AcceptsInput)
            return;

        _owner.EmitInput(
            new RawMouseWheelEventArgs(
                _mouseDevice,
                Timestamp(),
                root,
                ToPoint(mouse.Position),
                new Vector(wheel.X, wheel.Y),
                ReadModifiers() | _pointerButtons));
    }

    private void EmitPointer(
        Vector2 position,
        RawPointerEventType eventType)
    {
        IInputRoot? root = _inputRoot;
        if (root is null || !_owner.AcceptsInput)
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

    private RawInputModifiers ReadModifiers()
    {
        IKeyboard? keyboard =
            _keyboards.Count == 0 ? null : _keyboards[0];
        return keyboard is null
            ? RawInputModifiers.None
            : ReadModifiers(keyboard);
    }

    private static RawInputModifiers ReadModifiers(
        IKeyboard keyboard)
    {
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

    private static Point ToPoint(Vector2 point) =>
        new(point.X, point.Y);

    private static ulong Timestamp() =>
        (ulong)(Stopwatch.GetTimestamp() *
                (1000.0 / Stopwatch.Frequency));
}
