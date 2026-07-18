using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml.Input;
using Silk.NET.Input;

namespace ProGPU.Browser;

/// <summary>
/// Drains one fixed-width batch of DOM input records into the neutral WinUI input seam.
/// High-frequency move and wheel events remain batched. Pointer presses and releases
/// may enter synchronously so browser APIs that require transient user activation can
/// be invoked by the resulting control event.
/// </summary>
public static partial class BrowserInputDispatcher
{
    private const int EventSize = 32;
    private const int EventsPerBatch = 256;
    private const int MaximumBatchesPerFrame = 4;
    private static WindowInputState? s_attachedState;

    public static void Attach(WindowInputState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        s_attachedState = state;
        state.CursorChanged = cursor => SetCanvasCursor(ToCssCursor(cursor));
    }

    public static void Detach(WindowInputState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!ReferenceEquals(s_attachedState, state)) return;
        state.CursorChanged = null;
        s_attachedState = null;
    }

    [JSExport]
    public static bool DispatchImmediatePointer(int kind, double x, double y, int button)
    {
        var state = s_attachedState;
        if (state == null || (kind != (int)BrowserInputKind.PointerDown && kind != (int)BrowserInputKind.PointerUp))
            return false;

        InputSystem.Current = state;
        DispatchPointer((BrowserInputKind)kind, new Vector2((float)x, (float)y), unchecked((uint)button));
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
                InputSystem.InjectMouseMove(position);
                break;
            case BrowserInputKind.PointerDown:
            case BrowserInputKind.PointerUp:
                DispatchPointer(kind, position, ReadUInt32(record, 20));
                break;
            case BrowserInputKind.Wheel:
                InputSystem.InjectMouseMove(position);
                InputSystem.InjectMouseScroll(new Vector2(ReadSingle(record, 12), ReadSingle(record, 16)));
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

    private static void DispatchPointer(BrowserInputKind kind, Vector2 position, uint button)
    {
        InputSystem.InjectMouseMove(position);
        if (!TryMapButton(button, out var mappedButton)) return;
        if (kind == BrowserInputKind.PointerDown) InputSystem.InjectMouseDown(mappedButton);
        else InputSystem.InjectMouseUp(mappedButton);
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

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    [JSImport("drainInputEvents", "progpu-browser")]
    private static partial int DrainInputEvents(nint destination, int capacity);

    [JSImport("setCanvasCursor", "progpu-browser")]
    private static partial void SetCanvasCursor(string cursor);

    private enum BrowserInputKind : uint
    {
        PointerMove = 1,
        PointerDown = 2,
        PointerUp = 3,
        Wheel = 4,
        KeyDown = 5,
        KeyUp = 6,
        Text = 7,
        FocusLost = 8
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
