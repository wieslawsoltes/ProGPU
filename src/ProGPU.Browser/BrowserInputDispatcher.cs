using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using Silk.NET.Input;
using Windows.Devices.Input;
using ProGPU.Scene;

namespace ProGPU.Browser;

/// <summary>
/// Drains one fixed-width batch of DOM input records into the neutral WinUI input seam.
/// High-frequency move and wheel events remain batched. Pointer presses and releases
/// may enter synchronously so browser APIs that require transient user activation can
/// be invoked by the resulting control event.
/// </summary>
public static partial class BrowserInputDispatcher
{
    private const int EventSize = 64;
    private const int EventsPerBatch = 256;
    private const int MaximumBatchesPerFrame = 4;
    private static WindowInputState? s_attachedState;

    public static void Attach(WindowInputState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        s_attachedState = state;
        state.CursorChanged = cursor => SetCanvasCursor(ToCssCursor(cursor));
        state.FocusChanged = OnFocusChanged;
    }

    public static void Detach(WindowInputState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!ReferenceEquals(s_attachedState, state)) return;
        state.CursorChanged = null;
        state.FocusChanged = null;
        HideTextInput();
        s_attachedState = null;
    }

    [JSExport]
    public static bool DispatchTextInput(int kind, string? text, bool isComposing)
    {
        var state = s_attachedState;
        if (state == null || !Enum.IsDefined(typeof(TextInputEventKind), kind)) return false;
        InputSystem.Current = state;
        InputSystem.InjectTextInput((TextInputEventKind)kind, text, isComposing);
        return true;
    }

    [JSExport]
    public static bool DispatchImmediatePointer(
        int kind,
        double x,
        double y,
        int button,
        int buttons,
        int pointerId,
        int pointerType,
        double pressure,
        double width,
        double height,
        bool isPrimary,
        double timestamp,
        int modifiers)
    {
        var state = s_attachedState;
        if (state == null || (kind != (int)BrowserInputKind.PointerDown && kind != (int)BrowserInputKind.PointerUp && kind != (int)BrowserInputKind.PointerCancel))
            return false;

        InputSystem.Current = state;
        InputSystem.InjectPointer(CreatePointerEvent(
            (BrowserInputKind)kind,
            new Vector2((float)x, (float)y),
            button,
            buttons,
            pointerId,
            pointerType,
            (float)pressure,
            (float)width,
            (float)height,
            isPrimary,
            timestamp,
            modifiers));
        return true;
    }

    public static unsafe void Drain(WindowInputState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        InputSystem.Current = state;

        Span<byte> bytes = stackalloc byte[EventSize * EventsPerBatch];
        for (var batch = 0; batch < MaximumBatchesPerFrame; batch++)
        {
            int count;
            fixed (byte* destination = bytes)
                count = DrainInputEvents((nint)destination, EventsPerBatch);

            if (count < 0 || count > EventsPerBatch)
                throw new InvalidOperationException($"The browser returned an invalid input batch size of {count}.");

            for (var index = 0; index < count; index++)
                Dispatch(bytes.Slice(index * EventSize, EventSize));

            if (count < EventsPerBatch) break;
        }
    }

    private static void Dispatch(ReadOnlySpan<byte> record)
    {
        var kind = (BrowserInputKind)ReadUInt32(record, 0);
        var position = new Vector2(ReadSingle(record, 4), ReadSingle(record, 8));
        switch (kind)
        {
            case BrowserInputKind.PointerMove:
            case BrowserInputKind.PointerDown:
            case BrowserInputKind.PointerUp:
            case BrowserInputKind.PointerCancel:
                InputSystem.InjectPointer(CreatePointerEvent(
                    kind,
                    position,
                    ReadInt32(record, 56),
                    ReadInt32(record, 28),
                    ReadInt32(record, 20),
                    ReadInt32(record, 52),
                    ReadSingle(record, 40),
                    ReadSingle(record, 44),
                    ReadSingle(record, 48),
                    (ReadUInt32(record, 28) & 0x10000u) != 0,
                    ReadDouble(record, 32),
                    ReadInt32(record, 24)));
                break;
            case BrowserInputKind.Wheel:
                InputSystem.InjectPointer(new PointerInputEvent(
                    PointerInputKind.Wheel,
                    1,
                    PointerDeviceType.Mouse,
                    position,
                    ToMicroseconds(ReadDouble(record, 32)),
                    WheelDeltaX: ReadSingle(record, 12),
                    WheelDeltaY: ReadSingle(record, 16),
                    Modifiers: ReadModifiers(ReadUInt32(record, 24))));
                break;
            case BrowserInputKind.KeyDown:
                if (TryMapKey((BrowserKey)ReadUInt32(record, 20), out var downKey)) InputSystem.InjectKeyDown(downKey);
                break;
            case BrowserInputKind.KeyUp:
                if (TryMapKey((BrowserKey)ReadUInt32(record, 20), out var upKey)) InputSystem.InjectKeyUp(upKey);
                break;
            case BrowserInputKind.Text:
                var scalar = checked((int)ReadUInt32(record, 20));
                if (scalar is >= 0 and <= 0x10ffff && scalar is not (>= 0xd800 and <= 0xdfff))
                {
                    foreach (var character in char.ConvertFromUtf32(scalar)) InputSystem.InjectKeyChar(character);
                }
                break;
            case BrowserInputKind.FocusLost:
                InputSystem.InjectFocusLost();
                break;
        }
    }

    private static PointerInputEvent CreatePointerEvent(
        BrowserInputKind kind,
        Vector2 position,
        int button,
        int buttons,
        int pointerId,
        int pointerType,
        float pressure,
        float width,
        float height,
        bool isPrimary,
        double timestamp,
        int modifiers)
    {
        var inputKind = kind switch
        {
            BrowserInputKind.PointerDown => PointerInputKind.Pressed,
            BrowserInputKind.PointerUp => PointerInputKind.Released,
            BrowserInputKind.PointerCancel => PointerInputKind.Canceled,
            _ => PointerInputKind.Moved
        };
        var deviceType = pointerType switch
        {
            1 => PointerDeviceType.Touch,
            2 => PointerDeviceType.Pen,
            _ => PointerDeviceType.Mouse
        };
        var buttonMask = buttons & 0xffff;
        var left = (buttonMask & 1) != 0 || (inputKind == PointerInputKind.Pressed && button == 0);
        var right = (buttonMask & 2) != 0 || (inputKind == PointerInputKind.Pressed && button == 2);
        var middle = (buttonMask & 4) != 0 || (inputKind == PointerInputKind.Pressed && button == 1);
        var isInContact = inputKind == PointerInputKind.Pressed ||
            (inputKind == PointerInputKind.Moved && (deviceType != PointerDeviceType.Mouse || buttonMask != 0));
        return new PointerInputEvent(
            inputKind,
            unchecked((uint)Math.Max(1, pointerId)),
            deviceType,
            position,
            ToMicroseconds(timestamp),
            isPrimary,
            isInContact,
            left,
            middle,
            right,
            pressure,
            new Rect(position.X - width * 0.5f, position.Y - height * 0.5f, Math.Max(0, width), Math.Max(0, height)),
            Modifiers: ReadModifiers(unchecked((uint)modifiers)));
    }

    private static ulong ToMicroseconds(double timestampMilliseconds) =>
        (ulong)Math.Max(0d, timestampMilliseconds * 1000d);

    private static VirtualKeyModifiers ReadModifiers(uint value)
    {
        var result = VirtualKeyModifiers.None;
        if ((value & 1) != 0) result |= VirtualKeyModifiers.Shift;
        if ((value & 2) != 0) result |= VirtualKeyModifiers.Control;
        if ((value & 4) != 0) result |= VirtualKeyModifiers.Menu;
        if ((value & 8) != 0) result |= VirtualKeyModifiers.Windows;
        return result;
    }

    private static bool TryMapButton(uint button, out MouseButton result)
    {
        result = button switch
        {
            0 => MouseButton.Left,
            1 => MouseButton.Middle,
            2 => MouseButton.Right,
            _ => default
        };
        return button <= 2;
    }

    private static bool TryMapKey(BrowserKey key, out Key result)
    {
        if (key is >= BrowserKey.A and <= BrowserKey.Z)
        {
            result = (Key)((int)Key.A + ((int)key - (int)BrowserKey.A));
            return true;
        }
        if (key is >= BrowserKey.D0 and <= BrowserKey.D9)
        {
            result = (Key)((int)Key.Number0 + ((int)key - (int)BrowserKey.D0));
            return true;
        }
        if (key is >= BrowserKey.F1 and <= BrowserKey.F12)
        {
            result = (Key)((int)Key.F1 + ((int)key - (int)BrowserKey.F1));
            return true;
        }
        if (key is >= BrowserKey.Keypad0 and <= BrowserKey.Keypad9)
        {
            result = (Key)((int)Key.Keypad0 + ((int)key - (int)BrowserKey.Keypad0));
            return true;
        }

        result = key switch
        {
            BrowserKey.Backspace => Key.Backspace,
            BrowserKey.Tab => Key.Tab,
            BrowserKey.Enter => Key.Enter,
            BrowserKey.Escape => Key.Escape,
            BrowserKey.Space => Key.Space,
            BrowserKey.Insert => Key.Insert,
            BrowserKey.Delete => Key.Delete,
            BrowserKey.Home => Key.Home,
            BrowserKey.End => Key.End,
            BrowserKey.PageUp => Key.PageUp,
            BrowserKey.PageDown => Key.PageDown,
            BrowserKey.Left => Key.Left,
            BrowserKey.Right => Key.Right,
            BrowserKey.Up => Key.Up,
            BrowserKey.Down => Key.Down,
            BrowserKey.ShiftLeft => Key.ShiftLeft,
            BrowserKey.ShiftRight => Key.ShiftRight,
            BrowserKey.ControlLeft => Key.ControlLeft,
            BrowserKey.ControlRight => Key.ControlRight,
            BrowserKey.AltLeft => Key.AltLeft,
            BrowserKey.AltRight => Key.AltRight,
            BrowserKey.SuperLeft => Key.SuperLeft,
            BrowserKey.SuperRight => Key.SuperRight,
            _ => Key.Unknown
        };
        return result != Key.Unknown;
    }

    private static string ToCssCursor(StandardCursor cursor) => cursor switch
    {
        StandardCursor.IBeam => "text",
        StandardCursor.Crosshair => "crosshair",
        StandardCursor.Hand => "pointer",
        StandardCursor.HResize => "ew-resize",
        StandardCursor.VResize => "ns-resize",
        StandardCursor.NotAllowed => "not-allowed",
        StandardCursor.Wait => "wait",
        _ => "default"
    };

    private static void OnFocusChanged(FrameworkElement? element)
    {
        if (element is not ITextInputClient client)
        {
            HideTextInput();
            return;
        }

        var options = client.GetTextInputOptions();
        ConfigureTextInput(
            ToInputMode(options.InputScope),
            options.EnterKeyHint,
            options.AutoCapitalize,
            options.IsSpellCheckEnabled,
            options.IsPassword,
            options.AcceptsReturn,
            options.Bounds.X,
            options.Bounds.Y,
            options.Bounds.Width,
            options.Bounds.Height);
    }

    private static string ToInputMode(InputScopeNameValue scope) => scope switch
    {
        InputScopeNameValue.Url => "url",
        InputScopeNameValue.EmailSmtpAddress => "email",
        InputScopeNameValue.Number => "decimal",
        InputScopeNameValue.NumericPin => "numeric",
        InputScopeNameValue.TelephoneNumber => "tel",
        InputScopeNameValue.Search => "search",
        _ => "text"
    };

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);

    private static double ReadDouble(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]));

    [JSImport("drainInputEvents", "progpu-browser")]
    private static partial int DrainInputEvents(nint destination, int capacity);

    [JSImport("setCanvasCursor", "progpu-browser")]
    private static partial void SetCanvasCursor(string cursor);

    [JSImport("configureTextInput", "progpu-browser")]
    private static partial void ConfigureTextInput(
        string inputMode,
        string enterKeyHint,
        string autoCapitalize,
        bool spellCheck,
        bool isPassword,
        bool acceptsReturn,
        float x,
        float y,
        float width,
        float height);

    [JSImport("hideTextInput", "progpu-browser")]
    private static partial void HideTextInput();

    private enum BrowserInputKind : uint
    {
        PointerMove = 1,
        PointerDown = 2,
        PointerUp = 3,
        Wheel = 4,
        KeyDown = 5,
        KeyUp = 6,
        Text = 7,
        FocusLost = 8,
        PointerCancel = 9
    }

    private enum BrowserKey : uint
    {
        Unknown = 0,
        A = 1, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        D0 = 27, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        F1 = 37, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        Backspace = 49,
        Tab,
        Enter,
        Escape,
        Space,
        Insert,
        Delete,
        Home,
        End,
        PageUp,
        PageDown,
        Left,
        Right,
        Up,
        Down,
        ShiftLeft,
        ShiftRight,
        ControlLeft,
        ControlRight,
        AltLeft,
        AltRight,
        SuperLeft,
        SuperRight,
        Keypad0,
        Keypad1,
        Keypad2,
        Keypad3,
        Keypad4,
        Keypad5,
        Keypad6,
        Keypad7,
        Keypad8,
        Keypad9
    }
}
