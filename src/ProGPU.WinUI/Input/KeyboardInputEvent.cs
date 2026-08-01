using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ProGPU.WinUI.Input;

public readonly record struct KeyboardInputEvent(
    VirtualKey VirtualKey,
    PhysicalKeyStatus KeyStatus,
    ulong Timestamp,
    bool IsSystemKey,
    bool IsReleased);

public static class KeyboardInputRegistration
{
    public static bool Inject(
        WindowInputState state,
        in KeyboardInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(state);
        return InputSystem.InjectKeyboardInput(
            state,
            input);
    }
}

internal struct VirtualKeyStateMap
{
    private ulong _down0;
    private ulong _down1;
    private ulong _down2;
    private ulong _down3;
    private ulong _locked0;
    private ulong _locked1;
    private ulong _locked2;
    private ulong _locked3;

    public readonly VirtualKeyStates Get(
        VirtualKey key)
    {
        uint value = (uint)key;
        if (value is 0 or > 255)
            return VirtualKeyStates.None;
        int bucket = (int)(value >> 6);
        ulong mask = 1UL << ((int)value & 63);
        VirtualKeyStates result =
            (GetBucket(bucket, false) & mask) != 0
                ? VirtualKeyStates.Down
                : VirtualKeyStates.None;
        if ((GetBucket(bucket, true) & mask) != 0)
            result |= VirtualKeyStates.Locked;
        return result;
    }

    public void SetDown(
        VirtualKey key,
        bool value)
    {
        uint raw = (uint)key;
        if (raw is > 0 and <= 255)
            SetBucket(
                (int)(raw >> 6),
                1UL << ((int)raw & 63),
                value,
                false);
    }

    public void ToggleLocked(
        VirtualKey key)
    {
        uint raw = (uint)key;
        if (raw is 0 or > 255)
            return;
        int bucket = (int)(raw >> 6);
        ulong mask = 1UL << ((int)raw & 63);
        SetBucket(
            bucket,
            mask,
            (GetBucket(bucket, true) & mask) == 0,
            true);
    }

    private readonly ulong GetBucket(
        int bucket,
        bool locked) =>
        (locked, bucket) switch
        {
            (false, 0) => _down0,
            (false, 1) => _down1,
            (false, 2) => _down2,
            (false, 3) => _down3,
            (true, 0) => _locked0,
            (true, 1) => _locked1,
            (true, 2) => _locked2,
            (true, 3) => _locked3,
            _ => 0
        };

    private void SetBucket(
        int bucket,
        ulong mask,
        bool value,
        bool locked)
    {
        if (locked)
        {
            if (bucket == 0) Update(ref _locked0, mask, value);
            else if (bucket == 1) Update(ref _locked1, mask, value);
            else if (bucket == 2) Update(ref _locked2, mask, value);
            else Update(ref _locked3, mask, value);
            return;
        }

        if (bucket == 0) Update(ref _down0, mask, value);
        else if (bucket == 1) Update(ref _down1, mask, value);
        else if (bucket == 2) Update(ref _down2, mask, value);
        else Update(ref _down3, mask, value);
    }

    private static void Update(
        ref ulong target,
        ulong mask,
        bool value)
    {
        if (value)
            target |= mask;
        else
            target &= ~mask;
    }
}

internal static class SilkVirtualKeyMap
{
    public static VirtualKey FromSilk(
        Silk.NET.Input.Key key) =>
        key switch
        {
            Silk.NET.Input.Key.Space => VirtualKey.Space,
            Silk.NET.Input.Key.Apostrophe => VirtualKey.Quote,
            Silk.NET.Input.Key.Comma => VirtualKey.Comma,
            Silk.NET.Input.Key.Minus => VirtualKey.Minus,
            Silk.NET.Input.Key.Period => VirtualKey.Period,
            Silk.NET.Input.Key.Slash => VirtualKey.Slash,
            Silk.NET.Input.Key.Number0 => VirtualKey.Number0,
            Silk.NET.Input.Key.Number1 => VirtualKey.Number1,
            Silk.NET.Input.Key.Number2 => VirtualKey.Number2,
            Silk.NET.Input.Key.Number3 => VirtualKey.Number3,
            Silk.NET.Input.Key.Number4 => VirtualKey.Number4,
            Silk.NET.Input.Key.Number5 => VirtualKey.Number5,
            Silk.NET.Input.Key.Number6 => VirtualKey.Number6,
            Silk.NET.Input.Key.Number7 => VirtualKey.Number7,
            Silk.NET.Input.Key.Number8 => VirtualKey.Number8,
            Silk.NET.Input.Key.Number9 => VirtualKey.Number9,
            Silk.NET.Input.Key.Semicolon => VirtualKey.Semicolon,
            Silk.NET.Input.Key.Equal => VirtualKey.Plus,
            Silk.NET.Input.Key.A => VirtualKey.A,
            Silk.NET.Input.Key.B => VirtualKey.B,
            Silk.NET.Input.Key.C => VirtualKey.C,
            Silk.NET.Input.Key.D => VirtualKey.D,
            Silk.NET.Input.Key.E => VirtualKey.E,
            Silk.NET.Input.Key.F => VirtualKey.F,
            Silk.NET.Input.Key.G => VirtualKey.G,
            Silk.NET.Input.Key.H => VirtualKey.H,
            Silk.NET.Input.Key.I => VirtualKey.I,
            Silk.NET.Input.Key.J => VirtualKey.J,
            Silk.NET.Input.Key.K => VirtualKey.K,
            Silk.NET.Input.Key.L => VirtualKey.L,
            Silk.NET.Input.Key.M => VirtualKey.M,
            Silk.NET.Input.Key.N => VirtualKey.N,
            Silk.NET.Input.Key.O => VirtualKey.O,
            Silk.NET.Input.Key.P => VirtualKey.P,
            Silk.NET.Input.Key.Q => VirtualKey.Q,
            Silk.NET.Input.Key.R => VirtualKey.R,
            Silk.NET.Input.Key.S => VirtualKey.S,
            Silk.NET.Input.Key.T => VirtualKey.T,
            Silk.NET.Input.Key.U => VirtualKey.U,
            Silk.NET.Input.Key.V => VirtualKey.V,
            Silk.NET.Input.Key.W => VirtualKey.W,
            Silk.NET.Input.Key.X => VirtualKey.X,
            Silk.NET.Input.Key.Y => VirtualKey.Y,
            Silk.NET.Input.Key.Z => VirtualKey.Z,
            Silk.NET.Input.Key.LeftBracket => VirtualKey.LeftBracket,
            Silk.NET.Input.Key.BackSlash => VirtualKey.Backslash,
            Silk.NET.Input.Key.RightBracket => VirtualKey.RightBracket,
            Silk.NET.Input.Key.GraveAccent => VirtualKey.Grave,
            Silk.NET.Input.Key.Escape => VirtualKey.Escape,
            Silk.NET.Input.Key.Enter => VirtualKey.Enter,
            Silk.NET.Input.Key.Tab => VirtualKey.Tab,
            Silk.NET.Input.Key.Backspace => VirtualKey.Back,
            Silk.NET.Input.Key.Insert => VirtualKey.Insert,
            Silk.NET.Input.Key.Delete => VirtualKey.Delete,
            Silk.NET.Input.Key.Right => VirtualKey.Right,
            Silk.NET.Input.Key.Left => VirtualKey.Left,
            Silk.NET.Input.Key.Down => VirtualKey.Down,
            Silk.NET.Input.Key.Up => VirtualKey.Up,
            Silk.NET.Input.Key.PageUp => VirtualKey.PageUp,
            Silk.NET.Input.Key.PageDown => VirtualKey.PageDown,
            Silk.NET.Input.Key.Home => VirtualKey.Home,
            Silk.NET.Input.Key.End => VirtualKey.End,
            Silk.NET.Input.Key.CapsLock => VirtualKey.CapitalLock,
            Silk.NET.Input.Key.ScrollLock => VirtualKey.Scroll,
            Silk.NET.Input.Key.NumLock => VirtualKey.NumberKeyLock,
            Silk.NET.Input.Key.PrintScreen => VirtualKey.Snapshot,
            Silk.NET.Input.Key.Pause => VirtualKey.Pause,
            >= Silk.NET.Input.Key.F1 and
                <= Silk.NET.Input.Key.F24 =>
                (VirtualKey)(
                    (int)VirtualKey.F1 +
                    (int)key -
                    (int)Silk.NET.Input.Key.F1),
            Silk.NET.Input.Key.Keypad0 => VirtualKey.NumberPad0,
            Silk.NET.Input.Key.Keypad1 => VirtualKey.NumberPad1,
            Silk.NET.Input.Key.Keypad2 => VirtualKey.NumberPad2,
            Silk.NET.Input.Key.Keypad3 => VirtualKey.NumberPad3,
            Silk.NET.Input.Key.Keypad4 => VirtualKey.NumberPad4,
            Silk.NET.Input.Key.Keypad5 => VirtualKey.NumberPad5,
            Silk.NET.Input.Key.Keypad6 => VirtualKey.NumberPad6,
            Silk.NET.Input.Key.Keypad7 => VirtualKey.NumberPad7,
            Silk.NET.Input.Key.Keypad8 => VirtualKey.NumberPad8,
            Silk.NET.Input.Key.Keypad9 => VirtualKey.NumberPad9,
            Silk.NET.Input.Key.KeypadDecimal => VirtualKey.Decimal,
            Silk.NET.Input.Key.KeypadDivide => VirtualKey.Divide,
            Silk.NET.Input.Key.KeypadMultiply => VirtualKey.Multiply,
            Silk.NET.Input.Key.KeypadSubtract => VirtualKey.Subtract,
            Silk.NET.Input.Key.KeypadAdd => VirtualKey.Add,
            Silk.NET.Input.Key.KeypadEnter => VirtualKey.Enter,
            Silk.NET.Input.Key.ShiftLeft => VirtualKey.LeftShift,
            Silk.NET.Input.Key.ControlLeft => VirtualKey.LeftControl,
            Silk.NET.Input.Key.AltLeft => VirtualKey.LeftMenu,
            Silk.NET.Input.Key.SuperLeft => VirtualKey.LeftWindows,
            Silk.NET.Input.Key.ShiftRight => VirtualKey.RightShift,
            Silk.NET.Input.Key.ControlRight => VirtualKey.RightControl,
            Silk.NET.Input.Key.AltRight => VirtualKey.RightMenu,
            Silk.NET.Input.Key.SuperRight => VirtualKey.RightWindows,
            Silk.NET.Input.Key.Menu => VirtualKey.Application,
            _ => VirtualKey.None
        };
}
