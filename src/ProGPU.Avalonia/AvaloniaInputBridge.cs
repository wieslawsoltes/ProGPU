using System;
using Avalonia.Input;
using SilkInput = Silk.NET.Input;

namespace ProGPU.Avalonia;

public static class AvaloniaInputBridge
{
    public static SilkInput.MouseButton TranslateMouseButton(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => SilkInput.MouseButton.Left,
            MouseButton.Right => SilkInput.MouseButton.Right,
            MouseButton.Middle => SilkInput.MouseButton.Middle,
            MouseButton.XButton1 => SilkInput.MouseButton.Button4,
            MouseButton.XButton2 => SilkInput.MouseButton.Button5,
            _ => SilkInput.MouseButton.Unknown
        };
    }

    public static SilkInput.Key TranslateKey(Key key)
    {
        return key switch
        {
            Key.None => SilkInput.Key.Unknown,
            Key.A => SilkInput.Key.A,
            Key.B => SilkInput.Key.B,
            Key.C => SilkInput.Key.C,
            Key.D => SilkInput.Key.D,
            Key.E => SilkInput.Key.E,
            Key.F => SilkInput.Key.F,
            Key.G => SilkInput.Key.G,
            Key.H => SilkInput.Key.H,
            Key.I => SilkInput.Key.I,
            Key.J => SilkInput.Key.J,
            Key.K => SilkInput.Key.K,
            Key.L => SilkInput.Key.L,
            Key.M => SilkInput.Key.M,
            Key.N => SilkInput.Key.N,
            Key.O => SilkInput.Key.O,
            Key.P => SilkInput.Key.P,
            Key.Q => SilkInput.Key.Q,
            Key.R => SilkInput.Key.R,
            Key.S => SilkInput.Key.S,
            Key.T => SilkInput.Key.T,
            Key.U => SilkInput.Key.U,
            Key.V => SilkInput.Key.V,
            Key.W => SilkInput.Key.W,
            Key.X => SilkInput.Key.X,
            Key.Y => SilkInput.Key.Y,
            Key.Z => SilkInput.Key.Z,
            Key.D0 => SilkInput.Key.Number0,
            Key.D1 => SilkInput.Key.Number1,
            Key.D2 => SilkInput.Key.Number2,
            Key.D3 => SilkInput.Key.Number3,
            Key.D4 => SilkInput.Key.Number4,
            Key.D5 => SilkInput.Key.Number5,
            Key.D6 => SilkInput.Key.Number6,
            Key.D7 => SilkInput.Key.Number7,
            Key.D8 => SilkInput.Key.Number8,
            Key.D9 => SilkInput.Key.Number9,
            Key.NumPad0 => SilkInput.Key.Keypad0,
            Key.NumPad1 => SilkInput.Key.Keypad1,
            Key.NumPad2 => SilkInput.Key.Keypad2,
            Key.NumPad3 => SilkInput.Key.Keypad3,
            Key.NumPad4 => SilkInput.Key.Keypad4,
            Key.NumPad5 => SilkInput.Key.Keypad5,
            Key.NumPad6 => SilkInput.Key.Keypad6,
            Key.NumPad7 => SilkInput.Key.Keypad7,
            Key.NumPad8 => SilkInput.Key.Keypad8,
            Key.NumPad9 => SilkInput.Key.Keypad9,
            Key.F1 => SilkInput.Key.F1,
            Key.F2 => SilkInput.Key.F2,
            Key.F3 => SilkInput.Key.F3,
            Key.F4 => SilkInput.Key.F4,
            Key.F5 => SilkInput.Key.F5,
            Key.F6 => SilkInput.Key.F6,
            Key.F7 => SilkInput.Key.F7,
            Key.F8 => SilkInput.Key.F8,
            Key.F9 => SilkInput.Key.F9,
            Key.F10 => SilkInput.Key.F10,
            Key.F11 => SilkInput.Key.F11,
            Key.F12 => SilkInput.Key.F12,
            Key.Up => SilkInput.Key.Up,
            Key.Down => SilkInput.Key.Down,
            Key.Left => SilkInput.Key.Left,
            Key.Right => SilkInput.Key.Right,
            Key.Enter => SilkInput.Key.Enter,
            Key.Escape => SilkInput.Key.Escape,
            Key.Tab => SilkInput.Key.Tab,
            Key.Space => SilkInput.Key.Space,
            Key.Back => SilkInput.Key.Backspace,
            Key.Delete => SilkInput.Key.Delete,
            Key.Insert => SilkInput.Key.Insert,
            Key.Home => SilkInput.Key.Home,
            Key.End => SilkInput.Key.End,
            Key.PageUp => SilkInput.Key.PageUp,
            Key.PageDown => SilkInput.Key.PageDown,
            Key.LeftShift => SilkInput.Key.ShiftLeft,
            Key.RightShift => SilkInput.Key.ShiftRight,
            Key.LeftCtrl => SilkInput.Key.ControlLeft,
            Key.RightCtrl => SilkInput.Key.ControlRight,
            Key.LeftAlt => SilkInput.Key.AltLeft,
            Key.RightAlt => SilkInput.Key.AltRight,
            Key.LWin => SilkInput.Key.SuperLeft,
            Key.RWin => SilkInput.Key.SuperRight,
            _ => SilkInput.Key.Unknown
        };
    }
}
