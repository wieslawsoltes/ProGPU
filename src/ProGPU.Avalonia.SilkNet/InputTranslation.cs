using Avalonia.Input;
using SilkKey = Silk.NET.Input.Key;

namespace Avalonia.SilkNet;

public readonly record struct SilkNetKeyMapping(
    Key Key,
    PhysicalKey PhysicalKey);

/// <summary>
/// Allocation-free translation from Silk's GLFW-independent key identity to
/// Avalonia logical and physical key contracts.
/// </summary>
public static class SilkNetInputMappings
{
    public static SilkNetKeyMapping MapKey(SilkKey source)
    {
        int value = (int)source;
        if (source is >= SilkKey.Number0 and <= SilkKey.Number9)
        {
            int offset = value - (int)SilkKey.Number0;
            return new(
                Key.D0 + offset,
                PhysicalKey.Digit0 + offset);
        }

        if (source is >= SilkKey.A and <= SilkKey.Z)
        {
            int offset = value - (int)SilkKey.A;
            return new(
                Key.A + offset,
                PhysicalKey.A + offset);
        }

        if (source is >= SilkKey.F1 and <= SilkKey.F24)
        {
            int offset = value - (int)SilkKey.F1;
            return new(
                Key.F1 + offset,
                PhysicalKey.F1 + offset);
        }

        if (source is >= SilkKey.Keypad0 and <= SilkKey.Keypad9)
        {
            int offset = value - (int)SilkKey.Keypad0;
            return new(
                Key.NumPad0 + offset,
                PhysicalKey.NumPad0 + offset);
        }

        return source switch
        {
            SilkKey.Space => new(Key.Space, PhysicalKey.Space),
            SilkKey.Apostrophe => new(Key.OemQuotes, PhysicalKey.Quote),
            SilkKey.Comma => new(Key.OemComma, PhysicalKey.Comma),
            SilkKey.Minus => new(Key.OemMinus, PhysicalKey.Minus),
            SilkKey.Period => new(Key.OemPeriod, PhysicalKey.Period),
            SilkKey.Slash => new(Key.OemQuestion, PhysicalKey.Slash),
            SilkKey.Semicolon => new(Key.OemSemicolon, PhysicalKey.Semicolon),
            SilkKey.Equal => new(Key.OemPlus, PhysicalKey.Equal),
            SilkKey.LeftBracket => new(Key.OemOpenBrackets, PhysicalKey.BracketLeft),
            SilkKey.BackSlash => new(Key.OemPipe, PhysicalKey.Backslash),
            SilkKey.RightBracket => new(Key.OemCloseBrackets, PhysicalKey.BracketRight),
            SilkKey.GraveAccent => new(Key.OemTilde, PhysicalKey.Backquote),
            SilkKey.World1 => new(Key.Oem102, PhysicalKey.IntlBackslash),
            SilkKey.World2 => new(Key.None, PhysicalKey.IntlRo),
            SilkKey.Escape => new(Key.Escape, PhysicalKey.Escape),
            SilkKey.Enter => new(Key.Enter, PhysicalKey.Enter),
            SilkKey.Tab => new(Key.Tab, PhysicalKey.Tab),
            SilkKey.Backspace => new(Key.Back, PhysicalKey.Backspace),
            SilkKey.Insert => new(Key.Insert, PhysicalKey.Insert),
            SilkKey.Delete => new(Key.Delete, PhysicalKey.Delete),
            SilkKey.Right => new(Key.Right, PhysicalKey.ArrowRight),
            SilkKey.Left => new(Key.Left, PhysicalKey.ArrowLeft),
            SilkKey.Down => new(Key.Down, PhysicalKey.ArrowDown),
            SilkKey.Up => new(Key.Up, PhysicalKey.ArrowUp),
            SilkKey.PageUp => new(Key.PageUp, PhysicalKey.PageUp),
            SilkKey.PageDown => new(Key.PageDown, PhysicalKey.PageDown),
            SilkKey.Home => new(Key.Home, PhysicalKey.Home),
            SilkKey.End => new(Key.End, PhysicalKey.End),
            SilkKey.CapsLock => new(Key.CapsLock, PhysicalKey.CapsLock),
            SilkKey.ScrollLock => new(Key.Scroll, PhysicalKey.ScrollLock),
            SilkKey.NumLock => new(Key.NumLock, PhysicalKey.NumLock),
            SilkKey.PrintScreen => new(Key.PrintScreen, PhysicalKey.PrintScreen),
            SilkKey.Pause => new(Key.Pause, PhysicalKey.Pause),
            SilkKey.KeypadDecimal => new(Key.Decimal, PhysicalKey.NumPadDecimal),
            SilkKey.KeypadDivide => new(Key.Divide, PhysicalKey.NumPadDivide),
            SilkKey.KeypadMultiply => new(Key.Multiply, PhysicalKey.NumPadMultiply),
            SilkKey.KeypadSubtract => new(Key.Subtract, PhysicalKey.NumPadSubtract),
            SilkKey.KeypadAdd => new(Key.Add, PhysicalKey.NumPadAdd),
            SilkKey.KeypadEnter => new(Key.Enter, PhysicalKey.NumPadEnter),
            SilkKey.KeypadEqual => new(Key.OemPlus, PhysicalKey.NumPadEqual),
            SilkKey.ShiftLeft => new(Key.LeftShift, PhysicalKey.ShiftLeft),
            SilkKey.ControlLeft => new(Key.LeftCtrl, PhysicalKey.ControlLeft),
            SilkKey.AltLeft => new(Key.LeftAlt, PhysicalKey.AltLeft),
            SilkKey.SuperLeft => new(Key.LWin, PhysicalKey.MetaLeft),
            SilkKey.ShiftRight => new(Key.RightShift, PhysicalKey.ShiftRight),
            SilkKey.ControlRight => new(Key.RightCtrl, PhysicalKey.ControlRight),
            SilkKey.AltRight => new(Key.RightAlt, PhysicalKey.AltRight),
            SilkKey.SuperRight => new(Key.RWin, PhysicalKey.MetaRight),
            SilkKey.Menu => new(Key.Apps, PhysicalKey.ContextMenu),
            _ => new(Key.None, PhysicalKey.None)
        };
    }
}
