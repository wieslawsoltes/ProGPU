using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.WinUI;

public enum ElementTheme
{
    Default,
    Light,
    Dark
}

public static class ThemeManager
{
    private static ElementTheme _currentTheme = ElementTheme.Dark;
    public static event Action? ThemeChanged;

    public static ElementTheme CurrentTheme
    {
        get => _currentTheme;
        set
        {
            if (_currentTheme != value)
            {
                _currentTheme = value;
                ThemeChanged?.Invoke();
            }
        }
    }

    private static readonly Dictionary<string, Vector4> DarkPalette = new()
    {
        { "PageBackground", new Vector4(0.08f, 0.08f, 0.12f, 1.0f) }, // Dark Mica: #14141F
        { "CardBackground", new Vector4(0.12f, 0.12f, 0.16f, 0.98f) }, // #1F1F28
        { "ControlBackground", new Vector4(1f, 1f, 1f, 0.05f) }, // White 5%
        { "ControlBackgroundHover", new Vector4(1f, 1f, 1f, 0.09f) }, // White 9%
        { "ControlBackgroundPressed", new Vector4(0f, 0f, 0f, 0.15f) }, // Black 15%
        { "ControlBorder", new Vector4(1f, 1f, 1f, 0.08f) }, // White 8%
        { "ControlBorderHover", new Vector4(1f, 1f, 1f, 0.15f) }, // White 15%
        { "TextPrimary", new Vector4(1f, 1f, 1f, 1.0f) }, // Solid White
        { "TextSecondary", new Vector4(1f, 1f, 1f, 0.6f) }, // Muted White
        { "SystemAccentColor", new Vector4(0.0f, 0.47f, 0.83f, 1.0f) }, // Segoe Blue: #0078D4
        { "SystemAccentColorLight1", new Vector4(0.17f, 0.53f, 0.85f, 1.0f) }, // Hover
        { "SystemAccentColorDark1", new Vector4(0.0f, 0.35f, 0.62f, 1.0f) }, // Pressed
        { "SelectionHighlight", new Vector4(0.0f, 0.47f, 0.83f, 0.25f) }, // Translucent Segoe Blue
        { "HeaderBackground", new Vector4(0.05f, 0.05f, 0.07f, 1.0f) }, // Deep Dark
        { "ScrollbarThumb", new Vector4(1f, 1f, 1f, 0.25f) }, // White 25%
        { "ScrollbarThumbHover", new Vector4(1f, 1f, 1f, 0.45f) } // White 45%
    };

    private static readonly Dictionary<string, Vector4> LightPalette = new()
    {
        { "PageBackground", new Vector4(0.96f, 0.96f, 0.98f, 1.0f) }, // Light Acrylic: #F5F5F7
        { "CardBackground", new Vector4(1.0f, 1.0f, 1.0f, 1.0f) }, // Solid White
        { "ControlBackground", new Vector4(0f, 0f, 0f, 0.04f) }, // Black 4%
        { "ControlBackgroundHover", new Vector4(0f, 0f, 0f, 0.07f) }, // Black 7%
        { "ControlBackgroundPressed", new Vector4(0f, 0f, 0f, 0.12f) }, // Black 12%
        { "ControlBorder", new Vector4(0f, 0f, 0f, 0.09f) }, // Black 9%
        { "ControlBorderHover", new Vector4(0f, 0f, 0f, 0.18f) }, // Black 18%
        { "TextPrimary", new Vector4(0.08f, 0.08f, 0.12f, 1.0f) }, // Solid Dark
        { "TextSecondary", new Vector4(0.08f, 0.08f, 0.12f, 0.6f) }, // Muted Dark
        { "SystemAccentColor", new Vector4(0.0f, 0.47f, 0.83f, 1.0f) }, // Segoe Blue: #0078D4
        { "SystemAccentColorLight1", new Vector4(0.17f, 0.53f, 0.85f, 1.0f) }, // Hover
        { "SystemAccentColorDark1", new Vector4(0.0f, 0.35f, 0.62f, 1.0f) }, // Pressed
        { "SelectionHighlight", new Vector4(0.0f, 0.47f, 0.83f, 0.25f) }, // Translucent Segoe Blue
        { "HeaderBackground", new Vector4(0.92f, 0.92f, 0.94f, 1.0f) }, // Lighter header
        { "ScrollbarThumb", new Vector4(0f, 0f, 0f, 0.18f) }, // Black 18%
        { "ScrollbarThumbHover", new Vector4(0f, 0f, 0f, 0.35f) } // Black 35%
    };

    public static Brush GetBrush(string key)
    {
        var color = GetColor(key);
        return new SolidColorBrush(color);
    }

    public static Vector4 GetColor(string key)
    {
        var dict = (CurrentTheme == ElementTheme.Light) ? LightPalette : DarkPalette;
        if (dict.TryGetValue(key, out var val))
        {
            return val;
        }
        return new Vector4(1f, 1f, 1f, 1f); // Default White
    }
}
