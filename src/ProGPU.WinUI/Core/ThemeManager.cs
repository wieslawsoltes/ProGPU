using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml;

public enum ElementTheme
{
    Default,
    Light,
    Dark
}

public enum VisualThemeFamily
{
    Default,
    WinUI,
    macOS
}

public static class ThemeManager
{
    private static ElementTheme _currentTheme = ElementTheme.Dark;
    private static VisualThemeFamily _currentThemeFamily = VisualThemeFamily.WinUI;
    public static event Action? ThemeChanged;

    private static bool _isWindowActive = true;
    public static bool IsWindowActive
    {
        get => _isWindowActive;
        set
        {
            if (_isWindowActive != value)
            {
                _isWindowActive = value;
                DarkBrushCache.Clear();
                LightBrushCache.Clear();
                PenCache.Clear();
                ThemeChanged?.Invoke();
            }
        }
    }
    
    private static readonly Dictionary<(Type ControlType, VisualThemeFamily Family), Style> NativeDefaultStyles = new();
    private static readonly Dictionary<(string Key, VisualThemeFamily Family), SolidColorBrush> DarkBrushCache = new();
    private static readonly Dictionary<(string Key, VisualThemeFamily Family), SolidColorBrush> LightBrushCache = new();
    private static readonly Dictionary<(string Key, float Thickness, ElementTheme Theme, VisualThemeFamily Family), Pen> PenCache = new();

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

    public static VisualThemeFamily CurrentThemeFamily
    {
        get => _currentThemeFamily;
        set
        {
            if (_currentThemeFamily != value)
            {
                _currentThemeFamily = value;
                NativeDefaultStyles.Clear();
                DarkBrushCache.Clear();
                LightBrushCache.Clear();
                PenCache.Clear();
                ThemeChanged?.Invoke();
            }
        }
    }

    private static readonly Dictionary<string, Vector4> WinUIDarkPalette = new()
    {
        { "PageBackground", new Vector4(0.08f, 0.08f, 0.12f, 1.0f) }, // Dark Mica: #14141F
        { "CardBackground", new Vector4(0.12f, 0.12f, 0.16f, 1.0f) }, // #1F1F28
        { "ControlBackground", new Vector4(0.14f, 0.14f, 0.17f, 1.0f) }, // #24242B
        { "ControlBackgroundHover", new Vector4(0.18f, 0.18f, 0.22f, 1.0f) }, // #2D2D37
        { "ControlBackgroundPressed", new Vector4(0.09f, 0.09f, 0.11f, 1.0f) }, // #17171C
        { "ControlBorder", new Vector4(1f, 1f, 1f, 0.08f) }, // White 8%
        { "ControlBorderHover", new Vector4(1f, 1f, 1f, 0.15f) }, // White 15%
        { "TextPrimary", new Vector4(1f, 1f, 1f, 1.0f) }, // Solid White
        { "TextSecondary", new Vector4(1f, 1f, 1f, 0.6f) }, // Muted White
        { "TypographySpecimenSurface", new Vector4(0.035f, 0.035f, 0.035f, 1f) },
        { "TypographySpecimenInk", new Vector4(0.98f, 0.98f, 0.98f, 1f) },
        { "TypographySpecimenMuted", new Vector4(0.7f, 0.7f, 0.7f, 1f) },
        { "TypographySpecimenRule", new Vector4(1f, 1f, 1f, 0.18f) },
        { "TypographySpecimenAccent", new Vector4(1f, 0.84f, 0f, 1f) },
        { "TypographySpecimenAccentInk", new Vector4(0f, 0f, 0f, 1f) },
        { "SystemAccentColor", new Vector4(0.0f, 0.47f, 0.83f, 1.0f) }, // Segoe Blue: #0078D4
        { "SystemAccentColorLight1", new Vector4(0.17f, 0.53f, 0.85f, 1.0f) }, // Hover
        { "SystemAccentColorDark1", new Vector4(0.0f, 0.35f, 0.62f, 1.0f) }, // Pressed
        { "SelectionHighlight", new Vector4(0.0f, 0.47f, 0.83f, 0.25f) }, // Translucent Segoe Blue
        { "HeaderBackground", new Vector4(0.05f, 0.05f, 0.07f, 1.0f) }, // Deep Dark
        { "ScrollbarThumb", new Vector4(1f, 1f, 1f, 0.25f) }, // White 25%
        { "ScrollbarThumbHover", new Vector4(1f, 1f, 1f, 0.45f) }, // White 45%
        { "ButtonAmbientShadow", new Vector4(0f, 0f, 0f, 0.04f) },
        { "ButtonPenumbraShadow", new Vector4(0f, 0f, 0f, 0.08f) },
        { "NavigationViewItemBackgroundSelected", new Vector4(1f, 1f, 1f, 0.07f) },
        { "NavigationViewItemBackgroundPointerOver", new Vector4(1f, 1f, 1f, 0.05f) },
        { "TabViewItemCloseHover", new Vector4(1.0f, 0.33f, 0.33f, 1.0f) },
        { "TextOnAccent", new Vector4(1f, 1f, 1f, 1.0f) },
        { "Transparent", new Vector4(0f, 0f, 0f, 0f) }
    };

    private static readonly Dictionary<string, Vector4> WinUILightPalette = new()
    {
        { "PageBackground", new Vector4(0.96f, 0.96f, 0.98f, 1.0f) }, // Light Acrylic: #F5F5F7
        { "CardBackground", new Vector4(1.0f, 1.0f, 1.0f, 1.0f) }, // Solid White
        { "ControlBackground", new Vector4(0.91f, 0.91f, 0.93f, 1.0f) }, // #EAEAEC
        { "ControlBackgroundHover", new Vector4(0.87f, 0.87f, 0.89f, 1.0f) }, // #DFDFE2
        { "ControlBackgroundPressed", new Vector4(0.82f, 0.82f, 0.84f, 1.0f) }, // #D1D1D4
        { "ControlBorder", new Vector4(0f, 0f, 0f, 0.09f) }, // Black 9%
        { "ControlBorderHover", new Vector4(0f, 0f, 0f, 0.18f) }, // Black 18%
        { "TextPrimary", new Vector4(0.08f, 0.08f, 0.12f, 1.0f) }, // Solid Dark
        { "TextSecondary", new Vector4(0.08f, 0.08f, 0.12f, 0.6f) }, // Muted Dark
        { "TypographySpecimenSurface", new Vector4(1f, 1f, 1f, 1f) },
        { "TypographySpecimenInk", new Vector4(0f, 0f, 0f, 1f) },
        { "TypographySpecimenMuted", new Vector4(0.29f, 0.29f, 0.29f, 1f) },
        { "TypographySpecimenRule", new Vector4(0f, 0f, 0f, 0.2f) },
        { "TypographySpecimenAccent", new Vector4(1f, 0.84f, 0f, 1f) },
        { "TypographySpecimenAccentInk", new Vector4(0f, 0f, 0f, 1f) },
        { "SystemAccentColor", new Vector4(0.0f, 0.47f, 0.83f, 1.0f) }, // Segoe Blue: #0078D4
        { "SystemAccentColorLight1", new Vector4(0.17f, 0.53f, 0.85f, 1.0f) }, // Hover
        { "SystemAccentColorDark1", new Vector4(0.0f, 0.35f, 0.62f, 1.0f) }, // Pressed
        { "SelectionHighlight", new Vector4(0.0f, 0.47f, 0.83f, 0.25f) }, // Translucent Segoe Blue
        { "HeaderBackground", new Vector4(0.92f, 0.92f, 0.94f, 1.0f) }, // Lighter header
        { "ScrollbarThumb", new Vector4(0f, 0f, 0f, 0.18f) }, // Black 18%
        { "ScrollbarThumbHover", new Vector4(0f, 0f, 0f, 0.35f) }, // Black 35%
        { "ButtonAmbientShadow", new Vector4(0f, 0f, 0f, 0.04f) },
        { "ButtonPenumbraShadow", new Vector4(0f, 0f, 0f, 0.08f) },
        { "NavigationViewItemBackgroundSelected", new Vector4(0f, 0f, 0f, 0.08f) },
        { "NavigationViewItemBackgroundPointerOver", new Vector4(0f, 0f, 0f, 0.05f) },
        { "TabViewItemCloseHover", new Vector4(1.0f, 0.33f, 0.33f, 1.0f) },
        { "TextOnAccent", new Vector4(1f, 1f, 1f, 1.0f) },
        { "Transparent", new Vector4(0f, 0f, 0f, 0f) }
    };

    private static readonly Dictionary<string, Vector4> MacOsDarkPalette = new()
    {
        { "PageBackground", new Vector4(0.118f, 0.118f, 0.118f, 1f) }, // Dark Cocoa Titlebar Gray: #1E1E1E
        { "CardBackground", new Vector4(0.176f, 0.176f, 0.176f, 1f) }, // Slightly lighter charcoal: #2D2D2D
        { "ControlBackground", new Vector4(0.227f, 0.227f, 0.235f, 1.0f) }, // Dark solid charcoal: #3A3A3C
        { "ControlBackgroundHover", new Vector4(0.282f, 0.282f, 0.29f, 1.0f) }, // #48484A
        { "ControlBackgroundPressed", new Vector4(0.329f, 0.329f, 0.337f, 1.0f) }, // #545456
        { "ControlBorder", new Vector4(1f, 1f, 1f, 0.12f) }, // Sleek dark border
        { "ControlBorderHover", new Vector4(1f, 1f, 1f, 0.25f) },
        { "TextPrimary", new Vector4(0.9f, 0.9f, 0.9f, 1.0f) }, // Crisp Cocoa White
        { "TextSecondary", new Vector4(0.9f, 0.9f, 0.9f, 0.55f) }, // Muted Cocoa Gray
        { "TypographySpecimenSurface", new Vector4(0.035f, 0.035f, 0.035f, 1f) },
        { "TypographySpecimenInk", new Vector4(0.98f, 0.98f, 0.98f, 1f) },
        { "TypographySpecimenMuted", new Vector4(0.7f, 0.7f, 0.7f, 1f) },
        { "TypographySpecimenRule", new Vector4(1f, 1f, 1f, 0.18f) },
        { "TypographySpecimenAccent", new Vector4(1f, 0.84f, 0f, 1f) },
        { "TypographySpecimenAccentInk", new Vector4(0f, 0f, 0f, 1f) },
        { "SystemAccentColor", new Vector4(0.039f, 0.518f, 1.0f, 1.0f) }, // macOS Vibrant Accent Blue: #0A84FF
        { "SystemAccentColorLight1", new Vector4(0.2f, 0.6f, 1.0f, 1.0f) }, // Hover
        { "SystemAccentColorDark1", new Vector4(0.0f, 0.4f, 0.8f, 1.0f) }, // Pressed
        { "InactiveAccentColor", new Vector4(0.243f, 0.243f, 0.243f, 1f) },
        { "SystemGreenAccent", new Vector4(0.188f, 0.82f, 0.345f, 1f) },
        { "SelectionHighlight", new Vector4(0.039f, 0.518f, 1.0f, 0.3f) },
        { "HeaderBackground", new Vector4(0.09f, 0.09f, 0.09f, 1.0f) }, // Deep macOS Header Gray
        { "ScrollbarThumb", new Vector4(1f, 1f, 1f, 0.18f) },
        { "ScrollbarThumbHover", new Vector4(1f, 1f, 1f, 0.35f) },
        { "ButtonAmbientShadow", new Vector4(0f, 0f, 0f, 0.05f) },
        { "ButtonPenumbraShadow", new Vector4(0f, 0f, 0f, 0.1f) },
        { "NavigationViewItemBackgroundSelected", new Vector4(1f, 1f, 1f, 0.08f) },
        { "NavigationViewItemBackgroundPointerOver", new Vector4(1f, 1f, 1f, 0.05f) },
        { "TabViewItemCloseHover", new Vector4(1.0f, 0.2f, 0.2f, 1.0f) },
        { "TextOnAccent", new Vector4(1.0f, 1.0f, 1.0f, 1.0f) },
        { "ButtonBackgroundTop", new Vector4(0.24f, 0.24f, 0.25f, 1.0f) },
        { "ButtonBackgroundBottom", new Vector4(0.21f, 0.21f, 0.22f, 1.0f) },
        { "ButtonBackgroundTopPointerOver", new Vector4(0.29f, 0.29f, 0.30f, 1.0f) },
        { "ButtonBackgroundBottomPointerOver", new Vector4(0.27f, 0.27f, 0.28f, 1.0f) },
        { "ButtonBackgroundTopPressed", new Vector4(0.34f, 0.34f, 0.35f, 1.0f) },
        { "ButtonBackgroundBottomPressed", new Vector4(0.32f, 0.32f, 0.33f, 1.0f) },
        { "AccentButtonBackgroundTop", new Vector4(0.05f, 0.45f, 0.9f, 0.85f) },
        { "AccentButtonBackgroundBottom", new Vector4(0.0f, 0.35f, 0.8f, 0.85f) },
        { "AccentButtonBackgroundTopPointerOver", new Vector4(0.15f, 0.55f, 1.0f, 0.9f) },
        { "AccentButtonBackgroundBottomPointerOver", new Vector4(0.05f, 0.45f, 0.9f, 0.9f) },
        { "AccentButtonBackgroundTopPressed", new Vector4(0.0f, 0.35f, 0.8f, 0.9f) },
        { "AccentButtonBackgroundBottomPressed", new Vector4(0.0f, 0.25f, 0.7f, 0.9f) },
        { "ComboBoxBackgroundTop", new Vector4(0.26f, 0.26f, 0.26f, 0.85f) },
        { "ComboBoxBackgroundBottom", new Vector4(0.22f, 0.22f, 0.22f, 0.85f) },
        { "ComboBoxBackgroundTopPointerOver", new Vector4(0.32f, 0.32f, 0.32f, 0.9f) },
        { "ComboBoxBackgroundBottomPointerOver", new Vector4(0.28f, 0.28f, 0.28f, 0.9f) },
        { "ComboBoxBackgroundTopPressed", new Vector4(0.18f, 0.18f, 0.18f, 0.9f) },
        { "ComboBoxBackgroundBottomPressed", new Vector4(0.16f, 0.16f, 0.16f, 0.9f) },
        { "ButtonBackgroundDisabled", new Vector4(0.2f, 0.2f, 0.2f, 1.0f) },
        { "ButtonBorderBrushDisabled", new Vector4(1f, 1f, 1f, 0.12f) },
        { "CheckboxCheckedBackgroundTop", new Vector4(0.24f, 0.56f, 1f, 1f) },
        { "CheckboxCheckedBackgroundBottom", new Vector4(0.04f, 0.52f, 1f, 1f) },
        { "CheckboxCheckedBorder", new Vector4(0f, 0.43f, 0.88f, 1f) },
        { "CheckboxUncheckedBackgroundTop", new Vector4(0.26f, 0.26f, 0.26f, 1f) },
        { "CheckboxUncheckedBackgroundBottom", new Vector4(0.2f, 0.2f, 0.2f, 1f) },
        { "CheckboxUncheckedBorder", new Vector4(0.33f, 0.33f, 0.33f, 1f) },
        { "Transparent", new Vector4(0f, 0f, 0f, 0f) }
    };

    private static readonly Dictionary<string, Vector4> MacOsLightPalette = new()
    {
        { "PageBackground", new Vector4(0.965f, 0.965f, 0.965f, 1f) }, // Classic Cocoa Window Gray: #ECECEC
        { "CardBackground", new Vector4(1.0f, 1.0f, 1.0f, 1.0f) }, // Solid White
        { "ControlBackground", new Vector4(0.894f, 0.894f, 0.902f, 1.0f) }, // Light solid gray: #E4E4E6
        { "ControlBackgroundHover", new Vector4(0.82f, 0.82f, 0.839f, 1.0f) }, // #D1D1D6
        { "ControlBackgroundPressed", new Vector4(0.78f, 0.78f, 0.80f, 1.0f) }, // #C7C7CC
        { "ControlBorder", new Vector4(0f, 0f, 0f, 0.15f) }, // Sleek Cocoa Border: #C3C3C3
        { "ControlBorderHover", new Vector4(0f, 0f, 0f, 0.28f) },
        { "TextPrimary", new Vector4(0.16f, 0.16f, 0.16f, 1.0f) }, // Deep Cocoa charcoal
        { "TextSecondary", new Vector4(0.16f, 0.16f, 0.16f, 0.5f) }, // Muted Cocoa gray
        { "TypographySpecimenSurface", new Vector4(1f, 1f, 1f, 1f) },
        { "TypographySpecimenInk", new Vector4(0f, 0f, 0f, 1f) },
        { "TypographySpecimenMuted", new Vector4(0.29f, 0.29f, 0.29f, 1f) },
        { "TypographySpecimenRule", new Vector4(0f, 0f, 0f, 0.2f) },
        { "TypographySpecimenAccent", new Vector4(1f, 0.84f, 0f, 1f) },
        { "TypographySpecimenAccentInk", new Vector4(0f, 0f, 0f, 1f) },
        { "SystemAccentColor", new Vector4(0.0f, 0.478f, 1.0f, 1.0f) }, // macOS System Blue: #007AFF
        { "SystemAccentColorLight1", new Vector4(0.15f, 0.55f, 1.0f, 1.0f) },
        { "SystemAccentColorDark1", new Vector4(0.0f, 0.39f, 0.84f, 1.0f) },
        { "InactiveAccentColor", new Vector4(0.863f, 0.863f, 0.863f, 1f) },
        { "SystemGreenAccent", new Vector4(0.203f, 0.78f, 0.349f, 1f) },
        { "SelectionHighlight", new Vector4(0.0f, 0.478f, 1.0f, 0.2f) },
        { "HeaderBackground", new Vector4(0.96f, 0.96f, 0.96f, 1.0f) }, // macOS Light Header
        { "ScrollbarThumb", new Vector4(0f, 0f, 0f, 0.15f) },
        { "ScrollbarThumbHover", new Vector4(0f, 0f, 0f, 0.3f) },
        { "ButtonAmbientShadow", new Vector4(0f, 0f, 0f, 0.03f) },
        { "ButtonPenumbraShadow", new Vector4(0f, 0f, 0f, 0.06f) },
        { "NavigationViewItemBackgroundSelected", new Vector4(0f, 0f, 0f, 0.06f) },
        { "NavigationViewItemBackgroundPointerOver", new Vector4(0f, 0f, 0f, 0.04f) },
        { "TabViewItemCloseHover", new Vector4(0.95f, 0.2f, 0.2f, 1.0f) },
        { "TextOnAccent", new Vector4(1.0f, 1.0f, 1.0f, 1.0f) },
        { "ButtonBackgroundTop", new Vector4(0.92f, 0.92f, 0.93f, 1.0f) },
        { "ButtonBackgroundBottom", new Vector4(0.89f, 0.89f, 0.90f, 1.0f) },
        { "ButtonBackgroundTopPointerOver", new Vector4(0.83f, 0.83f, 0.85f, 1.0f) },
        { "ButtonBackgroundBottomPointerOver", new Vector4(0.81f, 0.81f, 0.83f, 1.0f) },
        { "ButtonBackgroundTopPressed", new Vector4(0.79f, 0.79f, 0.81f, 1.0f) },
        { "ButtonBackgroundBottomPressed", new Vector4(0.77f, 0.77f, 0.79f, 1.0f) },
        { "AccentButtonBackgroundTop", new Vector4(0.2f, 0.55f, 0.95f, 0.9f) },
        { "AccentButtonBackgroundBottom", new Vector4(0.0f, 0.45f, 0.9f, 0.9f) },
        { "AccentButtonBackgroundTopPointerOver", new Vector4(0.3f, 0.65f, 1.0f, 0.95f) },
        { "AccentButtonBackgroundBottomPointerOver", new Vector4(0.1f, 0.55f, 0.95f, 0.95f) },
        { "AccentButtonBackgroundTopPressed", new Vector4(0.1f, 0.45f, 0.85f, 0.95f) },
        { "AccentButtonBackgroundBottomPressed", new Vector4(0.0f, 0.35f, 0.75f, 0.95f) },
        { "ComboBoxBackgroundTop", new Vector4(1.0f, 1.0f, 1.0f, 0.9f) },
        { "ComboBoxBackgroundBottom", new Vector4(0.96f, 0.96f, 0.96f, 0.9f) },
        { "ComboBoxBackgroundTopPointerOver", new Vector4(0.98f, 0.98f, 0.98f, 0.95f) },
        { "ComboBoxBackgroundBottomPointerOver", new Vector4(0.94f, 0.94f, 0.94f, 0.95f) },
        { "ComboBoxBackgroundTopPressed", new Vector4(0.88f, 0.88f, 0.88f, 0.95f) },
        { "ComboBoxBackgroundBottomPressed", new Vector4(0.84f, 0.84f, 0.84f, 0.95f) },
        { "ButtonBackgroundDisabled", new Vector4(0.95f, 0.95f, 0.95f, 1.0f) },
        { "ButtonBorderBrushDisabled", new Vector4(0f, 0f, 0f, 0.15f) },
        { "CheckboxCheckedBackgroundTop", new Vector4(0.21f, 0.55f, 1f, 1f) },
        { "CheckboxCheckedBackgroundBottom", new Vector4(0f, 0.478f, 1f, 1f) },
        { "CheckboxCheckedBorder", new Vector4(0f, 0.39f, 0.84f, 1f) },
        { "CheckboxUncheckedBackgroundTop", new Vector4(1f, 1f, 1f, 1f) },
        { "CheckboxUncheckedBackgroundBottom", new Vector4(0.96f, 0.96f, 0.98f, 1f) },
        { "CheckboxUncheckedBorder", new Vector4(0.76f, 0.76f, 0.76f, 1f) },
        { "Transparent", new Vector4(0f, 0f, 0f, 0f) }
    };

    private static readonly Dictionary<string, string> ResourceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ItemsControlBackground", "Transparent" },
        { "ItemsControlBackgroundPointerOver", "Transparent" },
        { "ItemsControlBackgroundPressed", "Transparent" },
        { "ItemsControlBackgroundFocused", "Transparent" },
        { "ItemsControlBackgroundDisabled", "Transparent" },

        { "ListBoxBackground", "Transparent" },
        { "ListBoxBackgroundPointerOver", "Transparent" },
        { "ListBoxBackgroundPressed", "Transparent" },
        { "ListBoxBackgroundFocused", "Transparent" },
        { "ListBoxBackgroundDisabled", "Transparent" },

        { "ButtonBackground", "ControlBackground" },
        { "ButtonBackgroundPointerOver", "ControlBackgroundHover" },
        { "ButtonBackgroundPressed", "ControlBackgroundPressed" },
        { "ButtonBackgroundFocused", "ControlBackground" },
        { "ButtonBackgroundDisabled", "ControlBackground" },
        { "ButtonForeground", "TextPrimary" },
        { "ButtonForegroundPointerOver", "TextPrimary" },
        { "ButtonForegroundPressed", "TextPrimary" },
        { "ButtonForegroundDisabled", "TextSecondary" },
        { "ButtonBorderBrush", "ControlBorder" },
        { "ButtonBorderBrushPointerOver", "ControlBorderHover" },
        { "ButtonBorderBrushPressed", "ControlBorder" },
        { "ButtonBorderBrushFocused", "ControlBorderHover" },
        { "ButtonBorderBrushDisabled", "ControlBorder" },

        { "AccentButtonBackground", "SystemAccentColor" },
        { "AccentButtonBackgroundPointerOver", "SystemAccentColorLight1" },
        { "AccentButtonBackgroundPressed", "SystemAccentColorDark1" },
        { "AccentButtonBackgroundFocused", "SystemAccentColor" },
        { "AccentButtonBackgroundDisabled", "ControlBackground" },
        { "AccentButtonForeground", "TextOnAccent" },
        { "AccentButtonForegroundPointerOver", "TextOnAccent" },
        { "AccentButtonForegroundPressed", "TextOnAccent" },
        { "AccentButtonForegroundDisabled", "TextSecondary" },
        { "AccentButtonBorderBrush", "SystemAccentColor" },
        { "AccentButtonBorderBrushPointerOver", "SystemAccentColorLight1" },
        { "AccentButtonBorderBrushPressed", "SystemAccentColorDark1" },
        { "AccentButtonBorderBrushFocused", "SystemAccentColor" },
        { "AccentButtonBorderBrushDisabled", "ControlBorder" },

        { "RepeatButtonBackground", "ControlBackground" },
        { "RepeatButtonBackgroundFocused", "ControlBackground" },
        { "RepeatButtonForeground", "TextPrimary" },
        { "RepeatButtonBorderBrush", "ControlBorder" },
        { "RepeatButtonBorderBrushFocused", "ControlBorderHover" },
        { "HyperlinkButtonBackground", "ControlBackground" },
        { "HyperlinkButtonBackgroundFocused", "ControlBackground" },
        { "HyperlinkButtonForeground", "SystemAccentColor" },
        { "HyperlinkButtonBorderBrush", "SystemAccentColor" },
        { "HyperlinkButtonBorderBrushFocused", "SystemAccentColor" },

        { "CheckBoxBackgroundUnchecked", "ControlBackground" },
        { "CheckBoxBackgroundUncheckedPointerOver", "ControlBackgroundHover" },
        { "CheckBoxBackgroundUncheckedPressed", "ControlBackgroundPressed" },
        { "CheckBoxForegroundUnchecked", "TextPrimary" },
        { "CheckBoxBorderBrushUnchecked", "ControlBorder" },
        { "CheckBoxBorderBrushUncheckedPointerOver", "ControlBorderHover" },
        { "CheckBoxCheckBackgroundFillChecked", "SystemAccentColor" },
        { "CheckBoxCheckBackgroundFillCheckedPointerOver", "SystemAccentColorLight1" },
        { "CheckBoxCheckBackgroundFillCheckedPressed", "SystemAccentColorDark1" },
        { "CheckBoxCheckGlyphForegroundChecked", "TextOnAccent" },

        { "CheckBoxBackground", "ControlBackground" },
        { "CheckBoxBackgroundPointerOver", "ControlBackgroundHover" },
        { "CheckBoxBackgroundPressed", "ControlBackgroundPressed" },
        { "CheckBoxBackgroundFocused", "ControlBackground" },
        { "CheckBoxBackgroundDisabled", "ControlBackground" },
        { "CheckBoxForeground", "TextPrimary" },
        { "CheckBoxForegroundPointerOver", "TextPrimary" },
        { "CheckBoxForegroundPressed", "TextPrimary" },
        { "CheckBoxForegroundDisabled", "TextSecondary" },
        { "CheckBoxBorderBrush", "ControlBorder" },
        { "CheckBoxBorderBrushPointerOver", "ControlBorderHover" },
        { "CheckBoxBorderBrushPressed", "ControlBorder" },
        { "CheckBoxBorderBrushFocused", "ControlBorderHover" },
        { "CheckBoxBorderBrushDisabled", "ControlBorder" },

        { "RadioButtonBackground", "ControlBackground" },
        { "RadioButtonBackgroundPointerOver", "ControlBackgroundHover" },
        { "RadioButtonBackgroundPressed", "ControlBackgroundPressed" },
        { "RadioButtonBackgroundFocused", "ControlBackground" },
        { "RadioButtonBackgroundDisabled", "ControlBackground" },
        { "RadioButtonForeground", "TextPrimary" },
        { "RadioButtonForegroundPointerOver", "TextPrimary" },
        { "RadioButtonForegroundPressed", "TextPrimary" },
        { "RadioButtonForegroundDisabled", "TextSecondary" },
        { "RadioButtonBorderBrush", "ControlBorder" },
        { "RadioButtonBorderBrushPointerOver", "ControlBorderHover" },
        { "RadioButtonBorderBrushPressed", "ControlBorder" },
        { "RadioButtonBorderBrushFocused", "ControlBorderHover" },
        { "RadioButtonBorderBrushDisabled", "ControlBorder" },
        { "RadioButtonCheckBackgroundFillChecked", "SystemAccentColor" },
        { "RadioButtonCheckBackgroundFillCheckedPointerOver", "SystemAccentColorLight1" },
        { "RadioButtonCheckBackgroundFillCheckedPressed", "SystemAccentColorDark1" },
        { "RadioButtonCheckGlyphForegroundChecked", "TextOnAccent" },

        { "ComboBoxBackground", "ControlBackground" },
        { "ComboBoxBackgroundPointerOver", "ControlBackgroundHover" },
        { "ComboBoxBackgroundPressed", "ControlBackgroundPressed" },
        { "ComboBoxBackgroundDisabled", "ControlBackground" },
        { "ComboBoxForeground", "TextPrimary" },
        { "ComboBoxForegroundPointerOver", "TextPrimary" },
        { "ComboBoxForegroundPressed", "TextPrimary" },
        { "ComboBoxForegroundDisabled", "TextSecondary" },
        { "ComboBoxBorderBrush", "ControlBorder" },
        { "ComboBoxBorderBrushPointerOver", "ControlBorderHover" },
        { "ComboBoxBorderBrushPressed", "ControlBorder" },
        { "ComboBoxBorderBrushDisabled", "ControlBorder" },
        { "ComboBoxItemBackgroundSelected", "SelectionHighlight" },
        { "ComboBoxItemBackgroundPointerOver", "ControlBackgroundHover" },
        { "ComboBoxItemForeground", "TextPrimary" },

        { "TextControlBackground", "ControlBackground" },
        { "TextControlBackgroundPointerOver", "ControlBackgroundHover" },
        { "TextControlBackgroundFocused", "CardBackground" },
        { "TextControlForeground", "TextPrimary" },
        { "TextControlForegroundPointerOver", "TextPrimary" },
        { "TextControlBorderBrush", "ControlBorder" },
        { "TextControlBorderBrushPointerOver", "ControlBorderHover" },
        { "TextControlBorderBrushFocused", "SystemAccentColor" },
        { "TextControlPlaceholderForeground", "TextSecondary" },

        { "TextBoxBackground", "TextControlBackground" },
        { "TextBoxBackgroundPointerOver", "TextControlBackgroundPointerOver" },
        { "TextBoxBackgroundPressed", "TextControlBackground" },
        { "TextBoxBackgroundFocused", "TextControlBackgroundFocused" },
        { "TextBoxBackgroundDisabled", "TextControlBackground" },
        { "TextBoxForeground", "TextControlForeground" },
        { "TextBoxForegroundPointerOver", "TextControlForegroundPointerOver" },
        { "TextBoxForegroundFocused", "TextControlForeground" },
        { "TextBoxForegroundDisabled", "TextControlPlaceholderForeground" },
        { "TextBoxBorderBrush", "TextControlBorderBrush" },
        { "TextBoxBorderBrushPointerOver", "TextControlBorderBrushPointerOver" },
        { "TextBoxBorderBrushFocused", "TextControlBorderBrushFocused" },
        { "TextBoxBorderBrushDisabled", "TextControlBorderBrush" },

        { "PasswordBoxBackground", "TextControlBackground" },
        { "PasswordBoxBackgroundPointerOver", "TextControlBackgroundPointerOver" },
        { "PasswordBoxBackgroundPressed", "TextControlBackground" },
        { "PasswordBoxBackgroundFocused", "TextControlBackgroundFocused" },
        { "PasswordBoxBackgroundDisabled", "TextControlBackground" },
        { "PasswordBoxForeground", "TextControlForeground" },
        { "PasswordBoxForegroundPointerOver", "TextControlForegroundPointerOver" },
        { "PasswordBoxForegroundFocused", "TextControlForeground" },
        { "PasswordBoxForegroundDisabled", "TextControlPlaceholderForeground" },
        { "PasswordBoxBorderBrush", "TextControlBorderBrush" },
        { "PasswordBoxBorderBrushPointerOver", "TextControlBorderBrushPointerOver" },
        { "PasswordBoxBorderBrushFocused", "TextControlBorderBrushFocused" },
        { "PasswordBoxBorderBrushDisabled", "TextControlBorderBrush" },

        { "SliderTrackFill", "ControlBorder" },
        { "SliderTrackValueFill", "SystemAccentColor" },
        { "SliderThumbBackground", "TextPrimary" },
        { "SliderThumbBorderBrush", "ControlBorder" },
        { "SliderTrackFillPointerOver", "ControlBorderHover" },
        { "SliderTrackValueFillPointerOver", "SystemAccentColorLight1" },
        { "SliderTrackValueFillPressed", "SystemAccentColorDark1" },
        { "SliderTrackFillDisabled", "ControlBackground" },
        { "SliderTrackValueFillDisabled", "ControlBackground" },

        { "ToggleSwitchContentForeground", "TextPrimary" },
        { "ToggleSwitchHeaderForeground", "TextPrimary" },
        { "ToggleSwitchContainerBackground", "ControlBackground" },
        { "ToggleSwitchContainerBackgroundPointerOver", "ControlBackgroundHover" },
        { "ToggleSwitchFillOn", "SystemAccentColor" },
        { "ToggleSwitchFillOnPointerOver", "SystemAccentColorLight1" },
        { "ToggleSwitchFillOnPressed", "SystemAccentColorDark1" },
        { "ToggleSwitchKnobFillOff", "TextSecondary" },
        { "ToggleSwitchKnobFillOn", "TextPrimary" },

        { "GridSplitterBackground", "ControlBorder" },
        { "GridSplitterBackgroundPointerOver", "ControlBorderHover" },
        { "GridSplitterBackgroundPressed", "ControlBorderHover" },
        { "GridSplitterBackgroundFocused", "ControlBorder" },
        { "GridSplitterBackgroundDisabled", "ControlBorder" },
        { "GridSplitterForeground", "TextPrimary" },
        { "GridSplitterBorderBrush", "ControlBorder" },

        { "ProgressBarBackground", "ControlBorder" },
        { "ProgressBarForeground", "SystemAccentColor" },
        { "ProgressRingForeground", "SystemAccentColor" },
        { "ComboBoxBackgroundFocused", "ControlBackground" },
        { "ComboBoxBorderBrushFocused", "SystemAccentColor" },
        { "DatePickerBackground", "TextControlBackground" },
        { "DatePickerBackgroundPointerOver", "TextControlBackgroundPointerOver" },
        { "DatePickerBackgroundPressed", "TextControlBackground" },
        { "DatePickerBackgroundFocused", "TextControlBackgroundFocused" },
        { "DatePickerBackgroundDisabled", "TextControlBackground" },
        { "DatePickerForeground", "TextControlForeground" },
        { "DatePickerForegroundPointerOver", "TextControlForegroundPointerOver" },
        { "DatePickerForegroundPressed", "TextControlForeground" },
        { "DatePickerForegroundDisabled", "TextControlPlaceholderForeground" },
        { "DatePickerBorderBrush", "TextControlBorderBrush" },
        { "DatePickerBorderBrushPointerOver", "TextControlBorderBrushPointerOver" },
        { "DatePickerBorderBrushFocused", "TextControlBorderBrushFocused" },
        { "DatePickerBorderBrushDisabled", "TextControlBorderBrush" },
        { "ToolTipBackground", "CardBackground" },
        { "ToolTipForeground", "TextPrimary" },
        { "ToolTipBorderBrush", "ControlBorder" },
        { "ContentDialogBackground", "CardBackground" },
        { "ContentDialogForeground", "TextPrimary" },
        { "SystemControlBackgroundAccentBrush", "SystemAccentColor" },
        { "SystemControlForegroundBaseHighBrush", "TextPrimary" },
        { "SystemControlForegroundBaseMediumBrush", "TextSecondary" },
        { "SystemControlBackgroundBaseLowBrush", "ControlBackground" },
        { "SystemControlHighlightAccentBrush", "SystemAccentColor" }
    };

    private static string ResolveAlias(string key, VisualThemeFamily family)
    {
        if (string.IsNullOrEmpty(key)) return key;

        if (family == VisualThemeFamily.macOS)
        {
            if (key.Equals("TextControlBackground", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("TextControlBackgroundPointerOver", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("TextControlBackgroundPressed", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("TextBoxBackground", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("TextBoxBackgroundPointerOver", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("TextBoxBackgroundPressed", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("PasswordBoxBackground", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("ComboBoxBackground", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("ComboBoxBackgroundPointerOver", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("CheckBoxBackgroundUnchecked", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("CheckBoxBackgroundUncheckedPointerOver", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("RadioButtonBackground", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("RadioButtonBackgroundPointerOver", StringComparison.OrdinalIgnoreCase))
            {
                return "CardBackground";
            }
        }

        int depth = 0;
        while (ResourceAliases.TryGetValue(key, out var alias) && depth < 20)
        {
            key = alias;
            depth++;
        }
        return key;
    }

    private static object? ResolveValue(object? value)
    {
        if (value is StaticResourceRef r)
        {
            return GetResource(r.ResourceKey);
        }
        return value;
    }

    public static object? GetResource(string key) => GetResource(key, CurrentTheme, CurrentThemeFamily);

    public static object? GetResource(string key, ElementTheme theme) => GetResource(key, theme, CurrentThemeFamily);

    public static object? GetResource(string key, ElementTheme theme, VisualThemeFamily themeFamily)
    {
        if (string.IsNullOrEmpty(key)) return null;

        var actualFamily = themeFamily == VisualThemeFamily.Default ? CurrentThemeFamily : themeFamily;

        if (key.Equals("AccentButtonStyle", StringComparison.OrdinalIgnoreCase))
        {
            var accentStyle = new Style(typeof(Button));
            AddControlChrome(accentStyle, "AccentButtonBackground", "AccentButtonForeground", "AccentButtonBorderBrush", new Thickness(1f), 6f, new Thickness(12f, 6f, 12f, 6f));
            return accentStyle;
        }

        key = ResolveAlias(key, actualFamily);

        var actualTheme = theme == ElementTheme.Default ? CurrentTheme : theme;

        if (actualFamily == VisualThemeFamily.macOS && !IsWindowActive)
        {
            if (key.Equals("SystemAccentColor", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemAccentColorLight1", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemAccentColorDark1", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemGreenAccent", StringComparison.OrdinalIgnoreCase))
            {
                key = "InactiveAccentColor";
            }
        }

        Dictionary<string, Vector4> dict;
        if (actualFamily == VisualThemeFamily.macOS)
        {
            dict = (actualTheme == ElementTheme.Light) ? MacOsLightPalette : MacOsDarkPalette;
        }
        else
        {
            dict = (actualTheme == ElementTheme.Light) ? WinUILightPalette : WinUIDarkPalette;
        }

        if (dict.TryGetValue(key, out var colorVal))
        {
            var cache = (actualTheme == ElementTheme.Light) ? LightBrushCache : DarkBrushCache;
            var cacheKey = (key, actualFamily);
            if (cache.TryGetValue(cacheKey, out var cachedBrush))
            {
                return cachedBrush;
            }
            var newBrush = new SolidColorBrush(colorVal);
            cache[cacheKey] = newBrush;
            return newBrush;
        }

        return null;
    }

    public static Brush GetBrush(string key) => GetBrush(key, CurrentTheme, CurrentThemeFamily);

    public static Brush GetBrush(string key, ElementTheme theme) => GetBrush(key, theme, CurrentThemeFamily);

    public static Brush GetBrush(string key, ElementTheme theme, VisualThemeFamily themeFamily)
    {
        var actualTheme = theme == ElementTheme.Default ? CurrentTheme : theme;
        var actualFamily = themeFamily == VisualThemeFamily.Default ? CurrentThemeFamily : themeFamily;
        var cache = (actualTheme == ElementTheme.Light) ? LightBrushCache : DarkBrushCache;

        key = ResolveAlias(key, actualFamily);

        if (actualFamily == VisualThemeFamily.macOS && !IsWindowActive)
        {
            if (key.Equals("SystemAccentColor", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemAccentColorLight1", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemAccentColorDark1", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemGreenAccent", StringComparison.OrdinalIgnoreCase))
            {
                key = "InactiveAccentColor";
            }
        }

        var cacheKey = (key, actualFamily);

        if (cache.TryGetValue(cacheKey, out var cachedBrush))
        {
            return cachedBrush;
        }

        var colorFallback = GetColor(key, actualTheme, actualFamily);
        var newBrush = new SolidColorBrush(colorFallback);
        cache[cacheKey] = newBrush;
        return newBrush;
    }

    public static Pen GetPen(string key, float thickness = 1.0f) => GetPen(key, thickness, CurrentTheme, CurrentThemeFamily);

    public static Pen GetPen(string key, float thickness, ElementTheme theme) => GetPen(key, thickness, theme, CurrentThemeFamily);

    public static Pen GetPen(string key, float thickness, ElementTheme theme, VisualThemeFamily themeFamily)
    {
        var actualTheme = theme == ElementTheme.Default ? CurrentTheme : theme;
        var actualFamily = themeFamily == VisualThemeFamily.Default ? CurrentThemeFamily : themeFamily;
        key = ResolveAlias(key, actualFamily);

        var cacheKey = (key, thickness, actualTheme, actualFamily);
        if (PenCache.TryGetValue(cacheKey, out var cachedPen))
        {
            return cachedPen;
        }

        var brush = GetBrush(key, actualTheme, actualFamily);
        var newPen = new Pen(brush, thickness);
        PenCache[cacheKey] = newPen;
        return newPen;
    }

    public static Vector4 GetColor(string key) => GetColor(key, CurrentTheme, CurrentThemeFamily);

    public static Vector4 GetColor(string key, ElementTheme theme) => GetColor(key, theme, CurrentThemeFamily);

    public static Vector4 GetColor(string key, ElementTheme theme, VisualThemeFamily themeFamily)
    {
        var actualFamily = themeFamily == VisualThemeFamily.Default ? CurrentThemeFamily : themeFamily;
        key = ResolveAlias(key, actualFamily);

        var actualTheme = theme == ElementTheme.Default ? CurrentTheme : theme;

        if (actualFamily == VisualThemeFamily.macOS && !IsWindowActive)
        {
            if (key.Equals("SystemAccentColor", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemAccentColorLight1", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemAccentColorDark1", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SystemGreenAccent", StringComparison.OrdinalIgnoreCase))
            {
                key = "InactiveAccentColor";
            }
        }

        Dictionary<string, Vector4> dict;
        if (actualFamily == VisualThemeFamily.macOS)
        {
            dict = (actualTheme == ElementTheme.Light) ? MacOsLightPalette : MacOsDarkPalette;
        }
        else
        {
            dict = (actualTheme == ElementTheme.Light) ? WinUILightPalette : WinUIDarkPalette;
        }

        if (dict.TryGetValue(key, out var valHex))
        {
            return valHex;
        }
        return new Vector4(1f, 1f, 1f, 1f); // Default White
    }

    public static Style? GetDefaultStyle(Type controlType) => GetDefaultStyle(controlType, CurrentThemeFamily);

    internal static void ClearHotReloadCaches()
    {
        NativeDefaultStyles.Clear();
        DarkBrushCache.Clear();
        LightBrushCache.Clear();
        PenCache.Clear();
    }

    public static Style? GetDefaultStyle(Type controlType, VisualThemeFamily themeFamily)
    {
        var family = themeFamily == VisualThemeFamily.Default ? CurrentThemeFamily : themeFamily;
        var key = (controlType, family);
        if (NativeDefaultStyles.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var style = CreateNativeDefaultStyle(controlType, family);
        if (style != null)
        {
            NativeDefaultStyles[key] = style;
            return style;
        }

        return null;
    }

    private static Style? CreateNativeDefaultStyle(Type controlType, VisualThemeFamily themeFamily)
    {
        if (!typeof(Control).IsAssignableFrom(controlType))
        {
            return null;
        }

        var style = new Style(controlType);

        if (typeof(ToggleButton).IsAssignableFrom(controlType))
        {
            BuildToggleButtonDefaultStyle(style, themeFamily);
            return style;
        }

        if (typeof(Button).IsAssignableFrom(controlType) || typeof(RepeatButton).IsAssignableFrom(controlType))
        {
            BuildButtonDefaultStyle(style, themeFamily);
            return style;
        }

        if (typeof(CheckBox).IsAssignableFrom(controlType))
        {
            BuildCheckBoxDefaultStyle(style, themeFamily);
            return style;
        }

        if (typeof(RadioButton).IsAssignableFrom(controlType))
        {
            BuildRadioButtonDefaultStyle(style, themeFamily);
            return style;
        }

        if (typeof(ToggleSwitch).IsAssignableFrom(controlType))
        {
            BuildToggleSwitchDefaultStyle(style, themeFamily);
            return style;
        }

        if (typeof(Slider).IsAssignableFrom(controlType))
        {
            BuildSliderDefaultStyle(style, themeFamily);
            return style;
        }

        if (typeof(PasswordBox).IsAssignableFrom(controlType))
        {
            BuildPasswordBoxDefaultStyle(style, themeFamily);
            return style;
        }

        if (typeof(ComboBox).IsAssignableFrom(controlType) || typeof(DatePicker).IsAssignableFrom(controlType) || typeof(TextBox).IsAssignableFrom(controlType))
        {
            BuildTextBoxDefaultStyle(style, themeFamily);
            return style;
        }

        if (typeof(ItemsControl).IsAssignableFrom(controlType))
        {
            BuildItemsControlDefaultStyle(style, themeFamily);
            return style;
        }

        // Default fallback chrome
        AddControlChrome(style, "ControlBackground", "TextPrimary", "ControlBorder", new Thickness(1f), 4f, new Thickness(8f, 4f));
        return style;
    }

    private static void BuildToggleButtonDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("ButtonBackground")),
            new Setter(nameof(Control.Foreground), new ThemeResource("ButtonForeground")),
            new Setter(nameof(Control.BorderBrush), new ThemeResource("ButtonBorderBrush")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.CornerRadius), family == VisualThemeFamily.macOS ? 5f : 6f),
            new Setter(nameof(Control.Padding), family == VisualThemeFamily.macOS ? new Thickness(10f, 4f) : new Thickness(12f, 6f, 12f, 6f)),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(ToggleButton), (parent) =>
            {
                var grid = new Grid();
                
                var chrome = new ToggleButtonChrome();
                TemplateBinding.Bind(chrome, ToggleButtonChrome.IsCheckedProperty, parent, ToggleButton.IsCheckedProperty);
                TemplateBinding.Bind(chrome, ToggleButtonChrome.IsPointerOverProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerOver")!);
                TemplateBinding.Bind(chrome, ToggleButtonChrome.IsPointerPressedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerPressed")!);
                TemplateBinding.Bind(chrome, ToggleButtonChrome.IsFocusedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsFocused")!);
                TemplateBinding.Bind(chrome, ToggleButtonChrome.IsEnabledProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsEnabled")!);
                
                grid.AddChild(chrome);
                
                var presenter = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                TemplateBinding.Bind(presenter, ContentPresenter.PaddingProperty, parent, Control.PaddingProperty);
                grid.AddChild(presenter);
                
                return grid;
            }))
        });
    }

    private static void BuildButtonDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("ButtonBackground")),
            new Setter(nameof(Control.Foreground), new ThemeResource("ButtonForeground")),
            new Setter(nameof(Control.BorderBrush), new ThemeResource("ButtonBorderBrush")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.CornerRadius), family == VisualThemeFamily.macOS ? 5f : 6f),
            new Setter(nameof(Control.Padding), family == VisualThemeFamily.macOS ? new Thickness(10f, 4f) : new Thickness(12f, 6f, 12f, 6f)),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(Button), (parent) =>
            {
                var border = new Border();
                TemplateBinding.Bind(border, Border.BackgroundProperty, parent, Control.BackgroundProperty);
                TemplateBinding.Bind(border, Border.BorderBrushProperty, parent, Control.BorderBrushProperty);
                TemplateBinding.Bind(border, Border.BorderThicknessProperty, parent, Control.BorderThicknessProperty);
                TemplateBinding.Bind(border, Border.CornerRadiusProperty, parent, Control.CornerRadiusProperty);
                TemplateBinding.Bind(border, Border.PaddingProperty, parent, Control.PaddingProperty);

                var presenter = new ContentPresenter();
                TemplateBinding.Bind(presenter, ContentPresenter.HorizontalContentAlignmentProperty, parent, Control.HorizontalContentAlignmentProperty);
                TemplateBinding.Bind(presenter, ContentPresenter.VerticalContentAlignmentProperty, parent, Control.VerticalContentAlignmentProperty);

                border.Child = presenter;
                return border;
            }))
        });
    }

    private static void BuildCheckBoxDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("CheckBoxBackground")),
            new Setter(nameof(Control.Foreground), new ThemeResource("CheckBoxForeground")),
            new Setter(nameof(Control.BorderBrush), new ThemeResource("CheckBoxBorderBrush")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.CornerRadius), family == VisualThemeFamily.macOS ? 3.5f : 4f),
            new Setter(nameof(Control.Padding), new Thickness(8f, 4f)),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(CheckBox), (parent) =>
            {
                var grid = new Grid();
                grid.RowDefinitions.Add(GridLength.Auto);
                grid.ColumnDefinitions.Add(new GridLength(26f, GridUnitType.Absolute));
                grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

                var chrome = new CheckboxChrome();
                TemplateBinding.Bind(chrome, CheckboxChrome.IsCheckedProperty, parent, CheckBox.IsCheckedProperty);
                TemplateBinding.Bind(chrome, CheckboxChrome.IsPointerOverProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerOver")!);
                TemplateBinding.Bind(chrome, CheckboxChrome.IsPointerPressedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerPressed")!);
                TemplateBinding.Bind(chrome, CheckboxChrome.IsFocusedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsFocused")!);
                TemplateBinding.Bind(chrome, CheckboxChrome.IsEnabledProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsEnabled")!);

                grid.AddChild(chrome);
                Grid.SetColumn(chrome, 0);

                var presenter = new ContentPresenter { VerticalContentAlignment = VerticalAlignment.Center };
                TemplateBinding.Bind(presenter, ContentPresenter.PaddingProperty, parent, Control.PaddingProperty);
                grid.AddChild(presenter);
                Grid.SetColumn(presenter, 1);

                return grid;
            }))
        });
    }

    private static void BuildRadioButtonDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("RadioButtonBackground")),
            new Setter(nameof(Control.Foreground), new ThemeResource("RadioButtonForeground")),
            new Setter(nameof(Control.BorderBrush), new ThemeResource("RadioButtonBorderBrush")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.CornerRadius), 9f),
            new Setter(nameof(Control.Padding), new Thickness(8f, 4f)),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(RadioButton), (parent) =>
            {
                var grid = new Grid();
                grid.RowDefinitions.Add(GridLength.Auto);
                grid.ColumnDefinitions.Add(new GridLength(26f, GridUnitType.Absolute));
                grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

                var chrome = new RadioButtonChrome();
                TemplateBinding.Bind(chrome, RadioButtonChrome.IsCheckedProperty, parent, RadioButton.IsCheckedProperty);
                TemplateBinding.Bind(chrome, RadioButtonChrome.IsPointerOverProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerOver")!);
                TemplateBinding.Bind(chrome, RadioButtonChrome.IsPointerPressedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerPressed")!);
                TemplateBinding.Bind(chrome, RadioButtonChrome.IsFocusedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsFocused")!);
                TemplateBinding.Bind(chrome, RadioButtonChrome.IsEnabledProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsEnabled")!);

                grid.AddChild(chrome);
                Grid.SetColumn(chrome, 0);

                var presenter = new ContentPresenter { VerticalContentAlignment = VerticalAlignment.Center };
                TemplateBinding.Bind(presenter, ContentPresenter.PaddingProperty, parent, Control.PaddingProperty);
                grid.AddChild(presenter);
                Grid.SetColumn(presenter, 1);

                return grid;
            }))
        });
    }

    private static void BuildToggleSwitchDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("ToggleSwitchContainerBackground")),
            new Setter(nameof(Control.Foreground), new ThemeResource("ToggleSwitchContentForeground")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.CornerRadius), 10f),
            new Setter(nameof(Control.Padding), new Thickness(6f, 4f)),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(ToggleSwitch), (parent) =>
            {
                var grid = new Grid();
                grid.RowDefinitions.Add(GridLength.Auto);
                float trackColWidth = family == VisualThemeFamily.macOS ? 40f : 48f;
                grid.ColumnDefinitions.Add(new GridLength(trackColWidth, GridUnitType.Absolute));
                grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

                var chrome = new ToggleSwitchChrome();
                TemplateBinding.Bind(chrome, ToggleSwitchChrome.IsOnProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsOn")!);
                TemplateBinding.Bind(chrome, ToggleSwitchChrome.IsPointerOverProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerOver")!);
                TemplateBinding.Bind(chrome, ToggleSwitchChrome.IsPointerPressedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerPressed")!);
                TemplateBinding.Bind(chrome, ToggleSwitchChrome.IsFocusedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsFocused")!);
                TemplateBinding.Bind(chrome, ToggleSwitchChrome.IsEnabledProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsEnabled")!);

                grid.AddChild(chrome);
                Grid.SetColumn(chrome, 0);

                var presenter = new ContentPresenter { VerticalContentAlignment = VerticalAlignment.Center };
                TemplateBinding.Bind(presenter, ContentPresenter.PaddingProperty, parent, Control.PaddingProperty);
                grid.AddChild(presenter);
                Grid.SetColumn(presenter, 1);

                return grid;
            }))
        });
    }

    private static void BuildSliderDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("SliderTrackFill")),
            new Setter(nameof(Control.BorderBrush), new ThemeResource("SliderThumbBorderBrush")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.Height), 32f),
            new Setter(nameof(Control.Width), 200f),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(Slider), (parent) =>
            {
                var chrome = new SliderChrome();
                TemplateBinding.Bind(chrome, SliderChrome.ValueProperty, parent, DependencyProperty.Lookup(parent.GetType(), "Value")!);
                TemplateBinding.Bind(chrome, SliderChrome.MinimumProperty, parent, DependencyProperty.Lookup(parent.GetType(), "Minimum")!);
                TemplateBinding.Bind(chrome, SliderChrome.MaximumProperty, parent, DependencyProperty.Lookup(parent.GetType(), "Maximum")!);
                TemplateBinding.Bind(chrome, SliderChrome.IsPointerOverProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerOver")!);
                TemplateBinding.Bind(chrome, SliderChrome.IsPointerPressedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsPointerPressed")!);
                TemplateBinding.Bind(chrome, SliderChrome.IsFocusedProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsFocused")!);
                TemplateBinding.Bind(chrome, SliderChrome.IsEnabledProperty, parent, DependencyProperty.Lookup(parent.GetType(), "IsEnabled")!);

                return chrome;
            }))
        });
    }

    private static void BuildTextBoxDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("TextBoxBackground")),
            new Setter(nameof(Control.Foreground), new ThemeResource("TextBoxForeground")),
            new Setter(nameof(Control.BorderBrush), new ThemeResource("TextBoxBorderBrush")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.CornerRadius), family == VisualThemeFamily.macOS ? 6f : 4f),
            new Setter(nameof(Control.Padding), new Thickness(10f, 6f)),
            new Setter(nameof(Control.Template), new ControlTemplate(style.TargetType, (parent) =>
            {
                var border = new Border();
                TemplateBinding.Bind(border, Border.BackgroundProperty, parent, Control.BackgroundProperty);
                TemplateBinding.Bind(border, Border.BorderBrushProperty, parent, Control.BorderBrushProperty);
                TemplateBinding.Bind(border, Border.BorderThicknessProperty, parent, Control.BorderThicknessProperty);
                TemplateBinding.Bind(border, Border.CornerRadiusProperty, parent, Control.CornerRadiusProperty);
                TemplateBinding.Bind(border, Border.PaddingProperty, parent, Control.PaddingProperty);

                var presenter = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
                border.Child = presenter;
                return border;
            }))
        });
    }

    private static void BuildPasswordBoxDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("PasswordBoxBackground")),
            new Setter(nameof(Control.Foreground), new ThemeResource("PasswordBoxForeground")),
            new Setter(nameof(Control.BorderBrush), new ThemeResource("PasswordBoxBorderBrush")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.CornerRadius), family == VisualThemeFamily.macOS ? 6f : 4f),
            new Setter(nameof(Control.Padding), new Thickness(10f, 6f, 36f, 6f)),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(PasswordBox), (parent) =>
            {
                var border = new Border();
                TemplateBinding.Bind(border, Border.BackgroundProperty, parent, Control.BackgroundProperty);
                TemplateBinding.Bind(border, Border.BorderBrushProperty, parent, Control.BorderBrushProperty);
                TemplateBinding.Bind(border, Border.BorderThicknessProperty, parent, Control.BorderThicknessProperty);
                TemplateBinding.Bind(border, Border.CornerRadiusProperty, parent, Control.CornerRadiusProperty);
                TemplateBinding.Bind(border, Border.PaddingProperty, parent, Control.PaddingProperty);

                var presenter = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
                border.Child = presenter;
                return border;
            }))
        });
    }

    private static void BuildComboBoxDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), new ThemeResource("ComboBoxBackground")),
            new Setter(nameof(Control.Foreground), new ThemeResource("ComboBoxForeground")),
            new Setter(nameof(Control.BorderBrush), new ThemeResource("ComboBoxBorderBrush")),
            new Setter(nameof(Control.BorderThickness), new Thickness(1f)),
            new Setter(nameof(Control.CornerRadius), family == VisualThemeFamily.macOS ? 6f : 4f),
            new Setter(nameof(Control.Padding), new Thickness(10f, 6f)),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(ComboBox), (parent) =>
            {
                var border = new Border();
                TemplateBinding.Bind(border, Border.BackgroundProperty, parent, Control.BackgroundProperty);
                TemplateBinding.Bind(border, Border.BorderBrushProperty, parent, Control.BorderBrushProperty);
                TemplateBinding.Bind(border, Border.BorderThicknessProperty, parent, Control.BorderThicknessProperty);
                TemplateBinding.Bind(border, Border.CornerRadiusProperty, parent, Control.CornerRadiusProperty);
                TemplateBinding.Bind(border, Border.PaddingProperty, parent, Control.PaddingProperty);

                var presenter = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
                border.Child = presenter;
                return border;
            }))
        });
    }

    private static void BuildItemsControlDefaultStyle(Style style, VisualThemeFamily family)
    {
        style.SetSetters(new List<Setter>
        {
            new Setter(nameof(Control.Background), TransparentBrush()),
            new Setter(nameof(Control.BorderBrush), TransparentBrush()),
            new Setter(nameof(Control.BorderThickness), new Thickness(0f)),
            new Setter(nameof(Control.CornerRadius), 0f),
            new Setter(nameof(Control.Padding), new Thickness(0f)),
            new Setter(nameof(Control.Template), new ControlTemplate(typeof(ItemsControl), (parent) =>
            {
                var border = new Border();
                TemplateBinding.Bind(border, Border.BackgroundProperty, parent, Control.BackgroundProperty);
                TemplateBinding.Bind(border, Border.BorderBrushProperty, parent, Control.BorderBrushProperty);
                TemplateBinding.Bind(border, Border.BorderThicknessProperty, parent, Control.BorderThicknessProperty);
                TemplateBinding.Bind(border, Border.CornerRadiusProperty, parent, Control.CornerRadiusProperty);
                TemplateBinding.Bind(border, Border.PaddingProperty, parent, Control.PaddingProperty);

                var scrollViewer = new ScrollViewer
                {
                    Name = "ScrollViewer",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = TransparentBrush()
                };

                border.Child = scrollViewer;
                return border;
            }))
        });
    }

    private static void AddControlChrome(Style style, string backgroundKey, string foregroundKey, string borderKey, Thickness borderThickness, float cornerRadius, Thickness padding)
    {
        style.Setters.Add(new Setter(nameof(Control.Background), new ThemeResource(backgroundKey)));
        style.Setters.Add(new Setter(nameof(Control.Foreground), new ThemeResource(foregroundKey)));
        style.Setters.Add(new Setter(nameof(Control.BorderBrush), new ThemeResource(borderKey)));
        style.Setters.Add(new Setter(nameof(Control.BorderThickness), borderThickness));
        style.Setters.Add(new Setter(nameof(Control.CornerRadius), cornerRadius));
        style.Setters.Add(new Setter(nameof(Control.Padding), padding));
    }

    private static Brush TransparentBrush()
    {
        return new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));
    }
}

public class StaticResourceRef
{
    public string ResourceKey { get; }
    public StaticResourceRef(string resourceKey)
    {
        ResourceKey = resourceKey;
    }
}

// --------------------------------------------------------------------------------
// LOOKLESS TEMPLATE CHROME HELPERS
// --------------------------------------------------------------------------------

public class CheckboxChrome : FrameworkElement
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register("IsChecked", typeof(bool), typeof(CheckboxChrome),
            new PropertyMetadata(false, (d, e) => ((CheckboxChrome)d).Invalidate()));

    public bool IsChecked
    {
        get => (bool)(GetValue(IsCheckedProperty) ?? false);
        set => SetValue(IsCheckedProperty, value);
    }

    public static readonly new DependencyProperty IsPointerOverProperty =
        DependencyProperty.Register("IsPointerOver", typeof(bool), typeof(CheckboxChrome),
            new PropertyMetadata(false, (d, e) => ((CheckboxChrome)d).Invalidate()));

    public new bool IsPointerOver
    {
        get => (bool)(GetValue(IsPointerOverProperty) ?? false);
        set => SetValue(IsPointerOverProperty, value);
    }

    public static readonly new DependencyProperty IsPointerPressedProperty =
        DependencyProperty.Register("IsPointerPressed", typeof(bool), typeof(CheckboxChrome),
            new PropertyMetadata(false, (d, e) => ((CheckboxChrome)d).Invalidate()));

    public new bool IsPointerPressed
    {
        get => (bool)(GetValue(IsPointerPressedProperty) ?? false);
        set => SetValue(IsPointerPressedProperty, value);
    }

    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.Register("IsFocused", typeof(bool), typeof(CheckboxChrome),
            new PropertyMetadata(false, (d, e) => ((CheckboxChrome)d).Invalidate()));

    public bool IsFocused
    {
        get => (bool)(GetValue(IsFocusedProperty) ?? false);
        set => SetValue(IsFocusedProperty, value);
    }



    public override void OnRender(DrawingContext context)
    {
        var activeFamily = ActualThemeFamily;
        var activeTheme = ActualTheme;

        float boxSize = 18f;
        float boxY = (Size.Y - boxSize) / 2f;
        Rect boxRect = new Rect(0f, boxY, boxSize, boxSize);

        Brush? bg;
        Pen? pen = null;
        float cornerRadius = activeFamily == VisualThemeFamily.macOS ? 3.5f : 4f;

        if (activeFamily == VisualThemeFamily.macOS)
        {
            var startPt = new Vector2(boxRect.X + boxRect.Width / 2f, boxRect.Y);
            var endPt = new Vector2(boxRect.X + boxRect.Width / 2f, boxRect.Y + boxRect.Height);

            if (!IsEnabled)
            {
                bg = ThemeManager.GetBrush("ButtonBackgroundDisabled", activeTheme, activeFamily);
                pen = new Pen(ThemeManager.GetBrush("ButtonBorderBrushDisabled", activeTheme, activeFamily), 0.5f);
            }
            else if (IsChecked)
            {
                var topColor = ThemeManager.GetColor("CheckboxCheckedBackgroundTop", activeTheme, activeFamily);
                var bottomColor = ThemeManager.GetColor("CheckboxCheckedBackgroundBottom", activeTheme, activeFamily);
                bg = new LinearGradientBrush(startPt, endPt, new[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                var borderCol = ThemeManager.GetColor("CheckboxCheckedBorder", activeTheme, activeFamily);
                pen = new Pen(new SolidColorBrush(borderCol), 0.5f);
            }
            else
            {
                var topColor = ThemeManager.GetColor("CheckboxUncheckedBackgroundTop", activeTheme, activeFamily);
                var bottomColor = ThemeManager.GetColor("CheckboxUncheckedBackgroundBottom", activeTheme, activeFamily);
                bg = new LinearGradientBrush(startPt, endPt, new[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                var borderCol = ThemeManager.GetColor("CheckboxUncheckedBorder", activeTheme, activeFamily);
                pen = new Pen(new SolidColorBrush(borderCol), 0.5f);
            }
        }
        else
        {
            if (!IsEnabled)
            {
                bg = ThemeManager.GetBrush("CheckBoxBackgroundDisabled", activeTheme, activeFamily);
                pen = new Pen(ThemeManager.GetBrush("CheckBoxBorderBrushDisabled", activeTheme, activeFamily), 1f);
            }
            else if (IsChecked)
            {
                bg = IsPointerPressed
                    ? ThemeManager.GetBrush("CheckBoxCheckBackgroundFillCheckedPressed", activeTheme, activeFamily)
                    : (IsPointerOver ? ThemeManager.GetBrush("CheckBoxCheckBackgroundFillCheckedPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("CheckBoxCheckBackgroundFillChecked", activeTheme, activeFamily));
            }
            else
            {
                bg = IsPointerPressed
                    ? ThemeManager.GetBrush("CheckBoxBackgroundPressed", activeTheme, activeFamily)
                    : (IsPointerOver ? ThemeManager.GetBrush("CheckBoxBackgroundPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("CheckBoxBackground", activeTheme, activeFamily));
                pen = new Pen(IsPointerOver ? ThemeManager.GetBrush("CheckBoxBorderBrushPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("CheckBoxBorderBrush", activeTheme, activeFamily), 1f);
            }
        }

        context.DrawRoundedRectangle(bg, pen, boxRect, cornerRadius);

        if (IsChecked)
        {
            var checkGeometry = new PathGeometry();
            var checkFigure = new PathFigure(new Vector2(boxRect.X + 4.5f, boxRect.Y + 9f), isClosed: false);
            checkFigure.Segments.Add(new LineSegment(new Vector2(boxRect.X + 8f, boxRect.Y + 12.5f)));
            checkFigure.Segments.Add(new LineSegment(new Vector2(boxRect.X + 13.5f, boxRect.Y + 5f)));
            checkGeometry.Figures.Add(checkFigure);

            var checkBrush = IsEnabled
                ? (activeFamily == VisualThemeFamily.macOS ? new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)) : (ThemeManager.GetBrush("CheckBoxCheckGlyphForegroundChecked", activeTheme, activeFamily) ?? (activeTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground", activeTheme, activeFamily) : ThemeManager.GetBrush("TextPrimary", activeTheme, activeFamily))))
                : ThemeManager.GetBrush("TextSecondary", activeTheme, activeFamily);
            var checkPen = new Pen(checkBrush, 2f);
            context.DrawPath(null, checkPen, checkGeometry);
        }

        if (IsEnabled && IsFocused)
        {
            var accentColor = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
            if (activeFamily == VisualThemeFamily.macOS)
            {
                var accentVec = (accentColor as SolidColorBrush)?.Color ?? new Vector4(0f, 0.478f, 1f, 1f);
                var focusPen = new Pen(new SolidColorBrush(new Vector4(accentVec.X, accentVec.Y, accentVec.Z, 0.5f)), 2f);
                Rect focusRect = new Rect(boxRect.X - 2.5f, boxRect.Y - 2.5f, boxRect.Width + 5f, boxRect.Height + 5f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, cornerRadius + 2.5f);
            }
            else
            {
                var focusPen = new Pen(accentColor, 2f);
                Rect focusRect = new Rect(boxRect.X - 2f, boxRect.Y - 2f, boxRect.Width + 4f, boxRect.Height + 4f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, cornerRadius + 2f);
            }
        }
    }
}

public class RadioButtonChrome : FrameworkElement
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register("IsChecked", typeof(bool), typeof(RadioButtonChrome),
            new PropertyMetadata(false, (d, e) => ((RadioButtonChrome)d).Invalidate()));

    public bool IsChecked
    {
        get => (bool)(GetValue(IsCheckedProperty) ?? false);
        set => SetValue(IsCheckedProperty, value);
    }

    public static readonly new DependencyProperty IsPointerOverProperty =
        DependencyProperty.Register("IsPointerOver", typeof(bool), typeof(RadioButtonChrome),
            new PropertyMetadata(false, (d, e) => ((RadioButtonChrome)d).Invalidate()));

    public new bool IsPointerOver
    {
        get => (bool)(GetValue(IsPointerOverProperty) ?? false);
        set => SetValue(IsPointerOverProperty, value);
    }

    public static readonly new DependencyProperty IsPointerPressedProperty =
        DependencyProperty.Register("IsPointerPressed", typeof(bool), typeof(RadioButtonChrome),
            new PropertyMetadata(false, (d, e) => ((RadioButtonChrome)d).Invalidate()));

    public new bool IsPointerPressed
    {
        get => (bool)(GetValue(IsPointerPressedProperty) ?? false);
        set => SetValue(IsPointerPressedProperty, value);
    }

    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.Register("IsFocused", typeof(bool), typeof(RadioButtonChrome),
            new PropertyMetadata(false, (d, e) => ((RadioButtonChrome)d).Invalidate()));

    public bool IsFocused
    {
        get => (bool)(GetValue(IsFocusedProperty) ?? false);
        set => SetValue(IsFocusedProperty, value);
    }



    public override void OnRender(DrawingContext context)
    {
        var activeFamily = ActualThemeFamily;
        var activeTheme = ActualTheme;

        float boxSize = 18f;
        float boxY = (Size.Y - boxSize) / 2f;
        Rect boxRect = new Rect(0f, boxY, boxSize, boxSize);

        Brush? bg;
        Pen? pen = null;

        if (activeFamily == VisualThemeFamily.macOS)
        {
            var startPt = new Vector2(boxRect.X + boxRect.Width / 2f, boxRect.Y);
            var endPt = new Vector2(boxRect.X + boxRect.Width / 2f, boxRect.Y + boxRect.Height);

            if (!IsEnabled)
            {
                bg = ThemeManager.GetBrush("ButtonBackgroundDisabled", activeTheme, activeFamily);
                pen = new Pen(ThemeManager.GetBrush("ButtonBorderBrushDisabled", activeTheme, activeFamily), 0.5f);
            }
            else if (IsChecked)
            {
                var topColor = ThemeManager.GetColor("CheckboxCheckedBackgroundTop", activeTheme, activeFamily);
                var bottomColor = ThemeManager.GetColor("CheckboxCheckedBackgroundBottom", activeTheme, activeFamily);
                bg = new LinearGradientBrush(startPt, endPt, new[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                var borderCol = ThemeManager.GetColor("CheckboxCheckedBorder", activeTheme, activeFamily);
                pen = new Pen(new SolidColorBrush(borderCol), 0.5f);
            }
            else
            {
                var topColor = ThemeManager.GetColor("CheckboxUncheckedBackgroundTop", activeTheme, activeFamily);
                var bottomColor = ThemeManager.GetColor("CheckboxUncheckedBackgroundBottom", activeTheme, activeFamily);
                bg = new LinearGradientBrush(startPt, endPt, new[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                var borderCol = ThemeManager.GetColor("CheckboxUncheckedBorder", activeTheme, activeFamily);
                pen = new Pen(new SolidColorBrush(borderCol), 0.5f);
            }
        }
        else
        {
            if (!IsEnabled)
            {
                bg = ThemeManager.GetBrush("RadioButtonBackgroundDisabled", activeTheme, activeFamily);
                pen = new Pen(ThemeManager.GetBrush("RadioButtonBorderBrushDisabled", activeTheme, activeFamily), 1f);
            }
            else if (IsChecked)
            {
                bg = IsPointerPressed
                    ? ThemeManager.GetBrush("RadioButtonCheckBackgroundFillCheckedPressed", activeTheme, activeFamily)
                    : (IsPointerOver ? ThemeManager.GetBrush("RadioButtonCheckBackgroundFillCheckedPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("RadioButtonCheckBackgroundFillChecked", activeTheme, activeFamily));
            }
            else
            {
                bg = IsPointerPressed
                    ? ThemeManager.GetBrush("RadioButtonBackgroundPressed", activeTheme, activeFamily)
                    : (IsPointerOver ? ThemeManager.GetBrush("RadioButtonBackgroundPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("RadioButtonBackground", activeTheme, activeFamily));
                pen = new Pen(IsPointerOver ? ThemeManager.GetBrush("RadioButtonBorderBrushPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("RadioButtonBorderBrush", activeTheme, activeFamily), 1f);
            }
        }

        context.DrawRoundedRectangle(bg, pen, boxRect, 9f);

        if (IsChecked)
        {
            var dotBrush = IsEnabled
                ? (activeFamily == VisualThemeFamily.macOS ? new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)) : (ThemeManager.GetBrush("RadioButtonCheckGlyphForegroundChecked", activeTheme, activeFamily) ?? (activeTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground", activeTheme, activeFamily) : ThemeManager.GetBrush("TextPrimary", activeTheme, activeFamily))))
                : ThemeManager.GetBrush("TextSecondary", activeTheme, activeFamily);

            Rect dotRect = new Rect(boxRect.X + 6f, boxRect.Y + 6f, 6f, 6f);
            context.DrawRoundedRectangle(dotBrush, null, dotRect, 3f);
        }

        if (IsEnabled && IsFocused)
        {
            var accentColor = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
            if (activeFamily == VisualThemeFamily.macOS)
            {
                var accentVec = (accentColor as SolidColorBrush)?.Color ?? new Vector4(0f, 0.478f, 1f, 1f);
                var focusPen = new Pen(new SolidColorBrush(new Vector4(accentVec.X, accentVec.Y, accentVec.Z, 0.5f)), 2f);
                Rect focusRect = new Rect(boxRect.X - 2.5f, boxRect.Y - 2.5f, boxRect.Width + 5f, boxRect.Height + 5f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, 11.5f);
            }
            else
            {
                var focusPen = new Pen(accentColor, 2f);
                Rect focusRect = new Rect(boxRect.X - 2f, boxRect.Y - 2f, boxRect.Width + 4f, boxRect.Height + 4f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, 11f);
            }
        }
    }
}

public class ToggleSwitchChrome : FrameworkElement
{
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register("IsOn", typeof(bool), typeof(ToggleSwitchChrome),
            new PropertyMetadata(false, (d, e) => ((ToggleSwitchChrome)d).Invalidate()));

    public bool IsOn
    {
        get => (bool)(GetValue(IsOnProperty) ?? false);
        set => SetValue(IsOnProperty, value);
    }

    public static readonly new DependencyProperty IsPointerOverProperty =
        DependencyProperty.Register("IsPointerOver", typeof(bool), typeof(ToggleSwitchChrome),
            new PropertyMetadata(false, (d, e) => ((ToggleSwitchChrome)d).Invalidate()));

    public new bool IsPointerOver
    {
        get => (bool)(GetValue(IsPointerOverProperty) ?? false);
        set => SetValue(IsPointerOverProperty, value);
    }

    public static readonly new DependencyProperty IsPointerPressedProperty =
        DependencyProperty.Register("IsPointerPressed", typeof(bool), typeof(ToggleSwitchChrome),
            new PropertyMetadata(false, (d, e) => ((ToggleSwitchChrome)d).Invalidate()));

    public new bool IsPointerPressed
    {
        get => (bool)(GetValue(IsPointerPressedProperty) ?? false);
        set => SetValue(IsPointerPressedProperty, value);
    }

    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.Register("IsFocused", typeof(bool), typeof(ToggleSwitchChrome),
            new PropertyMetadata(false, (d, e) => ((ToggleSwitchChrome)d).Invalidate()));

    public bool IsFocused
    {
        get => (bool)(GetValue(IsFocusedProperty) ?? false);
        set => SetValue(IsFocusedProperty, value);
    }



    public override void OnRender(DrawingContext context)
    {
        var activeFamily = ActualThemeFamily;
        var activeTheme = ActualTheme;

        float trackW = activeFamily == VisualThemeFamily.macOS ? 32f : 40f;
        float trackH = activeFamily == VisualThemeFamily.macOS ? 18f : 20f;
        float trackY = (Size.Y - trackH) / 2f;

        Rect trackRect = new Rect(0f, trackY, trackW, trackH);
        float trackRadius = trackH / 2f;

        Brush? trackBg;
        Pen? trackBorder = null;

        if (!IsEnabled)
        {
            trackBg = ThemeManager.GetBrush("ToggleSwitchContainerBackground", activeTheme, activeFamily);
            trackBorder = new Pen(ThemeManager.GetBrush("ControlBorder", activeTheme, activeFamily), 1f);
        }
        else if (IsOn)
        {
            if (activeFamily == VisualThemeFamily.macOS)
            {
                trackBg = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
            }
            else
            {
                trackBg = IsPointerPressed
                    ? ThemeManager.GetBrush("ToggleSwitchFillOnPressed", activeTheme, activeFamily)
                    : (IsPointerOver ? ThemeManager.GetBrush("ToggleSwitchFillOnPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("ToggleSwitchFillOn", activeTheme, activeFamily));
            }
        }
        else
        {
            trackBg = ThemeManager.GetBrush(IsPointerPressed ? "ControlBackgroundPressed" : IsPointerOver ? "ToggleSwitchContainerBackgroundPointerOver" : "ToggleSwitchContainerBackground", activeTheme, activeFamily);
            trackBorder = new Pen(ThemeManager.GetBrush(IsPointerOver ? "ControlBorderHover" : "ControlBorder", activeTheme, activeFamily), 1f);
        }

        context.DrawRoundedRectangle(trackBg, trackBorder, trackRect, trackRadius);

        float thumbRadius = activeFamily == VisualThemeFamily.macOS ? 8f : (IsPointerPressed ? (trackH / 2f - 5f) : (trackH / 2f - 4f));
        if (activeFamily != VisualThemeFamily.macOS && thumbRadius < 3f) thumbRadius = 3f;
        float thumbDiameter = thumbRadius * 2f;
        float thumbMargin = activeFamily == VisualThemeFamily.macOS ? 1f : (trackH - thumbDiameter) / 2f;
        float thumbMinX = trackRect.X + thumbMargin + thumbRadius;
        float thumbMaxX = trackRect.X + trackRect.Width - thumbMargin - thumbRadius;

        float thumbX = IsOn ? thumbMaxX : thumbMinX;
        float thumbY = trackRect.Y + trackRect.Height / 2f;

        Rect thumbRect = new Rect(thumbX - thumbRadius, thumbY - thumbRadius, thumbDiameter, thumbDiameter);

        Brush thumbBg;
        Pen? thumbBorder = null;

        if (!IsEnabled)
        {
            thumbBg = ThemeManager.GetBrush("ControlBackground", activeTheme, activeFamily);
            thumbBorder = new Pen(ThemeManager.GetBrush("ControlBorder", activeTheme, activeFamily), 1f);
        }
        else if (IsOn)
        {
            thumbBg = ThemeManager.GetBrush("ToggleSwitchKnobFillOn", activeTheme, activeFamily);
            if (activeFamily == VisualThemeFamily.macOS)
            {
                thumbBorder = new Pen(new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.1f)), 0.5f);
            }
        }
        else
        {
            thumbBg = IsPointerOver
                ? ThemeManager.GetBrush("ToggleSwitchKnobFillOn", activeTheme, activeFamily)
                : ThemeManager.GetBrush("ToggleSwitchKnobFillOff", activeTheme, activeFamily);
            thumbBorder = new Pen(ThemeManager.GetBrush("ControlBorder", activeTheme, activeFamily), 1f);
        }

        if (activeFamily == VisualThemeFamily.macOS && IsEnabled)
        {
            var shadowColor = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.15f));
            Rect shadowRect = new Rect(thumbRect.X, thumbRect.Y + 1f, thumbRect.Width, thumbRect.Height);
            context.DrawRoundedRectangle(shadowColor, null, shadowRect, thumbRadius);
        }

        context.DrawRoundedRectangle(thumbBg, thumbBorder, thumbRect, thumbRadius);

        if (IsEnabled && IsFocused)
        {
            var accentColor = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
            if (activeFamily == VisualThemeFamily.macOS)
            {
                var accentVec = (accentColor as SolidColorBrush)?.Color ?? new Vector4(0f, 0.478f, 1f, 1f);
                var focusPen = new Pen(new SolidColorBrush(new Vector4(accentVec.X, accentVec.Y, accentVec.Z, 0.5f)), 2f);
                Rect focusRect = new Rect(trackRect.X - 2.5f, trackRect.Y - 2.5f, trackRect.Width + 5f, trackRect.Height + 5f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, trackRadius + 2.5f);
            }
            else
            {
                var focusPen = new Pen(accentColor, 2f);
                Rect focusRect = new Rect(trackRect.X - 2f, trackRect.Y - 2f, trackRect.Width + 4f, trackRect.Height + 4f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, trackRadius + 2f);
            }
        }
    }
}

public class ToggleButtonChrome : FrameworkElement
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register("IsChecked", typeof(bool), typeof(ToggleButtonChrome),
            new PropertyMetadata(false, (d, e) => ((ToggleButtonChrome)d).Invalidate()));

    public bool IsChecked
    {
        get => (bool)(GetValue(IsCheckedProperty) ?? false);
        set => SetValue(IsCheckedProperty, value);
    }

    public static readonly new DependencyProperty IsPointerOverProperty =
        DependencyProperty.Register("IsPointerOver", typeof(bool), typeof(ToggleButtonChrome),
            new PropertyMetadata(false, (d, e) => ((ToggleButtonChrome)d).Invalidate()));

    public new bool IsPointerOver
    {
        get => (bool)(GetValue(IsPointerOverProperty) ?? false);
        set => SetValue(IsPointerOverProperty, value);
    }

    public static readonly new DependencyProperty IsPointerPressedProperty =
        DependencyProperty.Register("IsPointerPressed", typeof(bool), typeof(ToggleButtonChrome),
            new PropertyMetadata(false, (d, e) => ((ToggleButtonChrome)d).Invalidate()));

    public new bool IsPointerPressed
    {
        get => (bool)(GetValue(IsPointerPressedProperty) ?? false);
        set => SetValue(IsPointerPressedProperty, value);
    }

    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.Register("IsFocused", typeof(bool), typeof(ToggleButtonChrome),
            new PropertyMetadata(false, (d, e) => ((ToggleButtonChrome)d).Invalidate()));

    public bool IsFocused
    {
        get => (bool)(GetValue(IsFocusedProperty) ?? false);
        set => SetValue(IsFocusedProperty, value);
    }

    public override void OnRender(DrawingContext context)
    {
        var activeFamily = ActualThemeFamily;
        var activeTheme = ActualTheme;

        float cornerRadius = activeFamily == VisualThemeFamily.macOS ? 5f : 6f;
        Rect rect = new Rect(0f, 0f, Size.X, Size.Y);

        Brush bg;
        Pen pen;

        var startPt = new Vector2(Size.X / 2f, 0f);
        var endPt = new Vector2(Size.X / 2f, Size.Y);

        if (activeFamily == VisualThemeFamily.macOS)
        {
            if (!IsEnabled)
            {
                bg = ThemeManager.GetBrush("ButtonBackgroundDisabled", activeTheme, activeFamily);
                pen = new Pen(ThemeManager.GetBrush("ButtonBorderBrushDisabled", activeTheme, activeFamily), 1f);
            }
            else if (IsChecked)
            {
                // Vibrant macOS blue gel gradient using centralized accent button keys
                Vector4 topColor, bottomColor;
                if (IsPointerPressed)
                {
                    topColor = ThemeManager.GetColor("AccentButtonBackgroundTopPressed", activeTheme, activeFamily);
                    bottomColor = ThemeManager.GetColor("AccentButtonBackgroundBottomPressed", activeTheme, activeFamily);
                }
                else if (IsPointerOver)
                {
                    topColor = ThemeManager.GetColor("AccentButtonBackgroundTopPointerOver", activeTheme, activeFamily);
                    bottomColor = ThemeManager.GetColor("AccentButtonBackgroundBottomPointerOver", activeTheme, activeFamily);
                }
                else
                {
                    topColor = ThemeManager.GetColor("AccentButtonBackgroundTop", activeTheme, activeFamily);
                    bottomColor = ThemeManager.GetColor("AccentButtonBackgroundBottom", activeTheme, activeFamily);
                }

                bg = new LinearGradientBrush(startPt, endPt, new[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                var borderCol = ThemeManager.GetColor("CheckboxCheckedBorder", activeTheme, activeFamily);
                pen = new Pen(new SolidColorBrush(borderCol), 1f);
            }
            else
            {
                // Standard macOS white/gray Aqua vertical gel gradient using centralized button keys
                Vector4 topColor, bottomColor;
                if (IsPointerPressed)
                {
                    topColor = ThemeManager.GetColor("ButtonBackgroundTopPressed", activeTheme, activeFamily);
                    bottomColor = ThemeManager.GetColor("ButtonBackgroundBottomPressed", activeTheme, activeFamily);
                }
                else if (IsPointerOver)
                {
                    topColor = ThemeManager.GetColor("ButtonBackgroundTopPointerOver", activeTheme, activeFamily);
                    bottomColor = ThemeManager.GetColor("ButtonBackgroundBottomPointerOver", activeTheme, activeFamily);
                }
                else
                {
                    topColor = ThemeManager.GetColor("ButtonBackgroundTop", activeTheme, activeFamily);
                    bottomColor = ThemeManager.GetColor("ButtonBackgroundBottom", activeTheme, activeFamily);
                }

                bg = new LinearGradientBrush(startPt, endPt, new[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                var borderCol = ThemeManager.GetColor("CheckboxUncheckedBorder", activeTheme, activeFamily);
                pen = new Pen(new SolidColorBrush(borderCol), 1f);
            }
        }
        else
        {
            // WinUI Fluent styling
            if (!IsEnabled)
            {
                bg = ThemeManager.GetBrush("ButtonBackgroundDisabled", activeTheme, activeFamily);
                pen = new Pen(ThemeManager.GetBrush("ButtonBorderBrushDisabled", activeTheme, activeFamily), 1f);
            }
            else if (IsChecked)
            {
                // Fluent Checked uses Accent color
                bg = IsPointerPressed
                    ? ThemeManager.GetBrush("AccentButtonBackgroundPressed", activeTheme, activeFamily)
                    : (IsPointerOver ? ThemeManager.GetBrush("AccentButtonBackgroundPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("AccentButtonBackground", activeTheme, activeFamily));
                pen = new Pen(ThemeManager.GetBrush("AccentButtonBorderBrush", activeTheme, activeFamily), 1f);
            }
            else
            {
                // Fluent Unchecked uses standard Button styles
                bg = IsPointerPressed
                    ? ThemeManager.GetBrush("ButtonBackgroundPressed", activeTheme, activeFamily)
                    : (IsPointerOver ? ThemeManager.GetBrush("ButtonBackgroundPointerOver", activeTheme, activeFamily) : ThemeManager.GetBrush("ButtonBackground", activeTheme, activeFamily));
                pen = new Pen(ThemeManager.GetBrush("ButtonBorderBrush", activeTheme, activeFamily), 1f);
            }
        }

        context.DrawRoundedRectangle(bg, pen, rect, cornerRadius);
    }
}

public class SliderChrome : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register("Value", typeof(float), typeof(SliderChrome),
            new PropertyMetadata(0f, (d, e) => ((SliderChrome)d).Invalidate()));

    public float Value
    {
        get => (float)(GetValue(ValueProperty) ?? 0f);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register("Minimum", typeof(float), typeof(SliderChrome),
            new PropertyMetadata(0f, (d, e) => ((SliderChrome)d).Invalidate()));

    public float Minimum
    {
        get => (float)(GetValue(MinimumProperty) ?? 0f);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register("Maximum", typeof(float), typeof(SliderChrome),
            new PropertyMetadata(100f, (d, e) => ((SliderChrome)d).Invalidate()));

    public float Maximum
    {
        get => (float)(GetValue(MaximumProperty) ?? 100f);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly new DependencyProperty IsPointerOverProperty =
        DependencyProperty.Register("IsPointerOver", typeof(bool), typeof(SliderChrome),
            new PropertyMetadata(false, (d, e) => ((SliderChrome)d).Invalidate()));

    public new bool IsPointerOver
    {
        get => (bool)(GetValue(IsPointerOverProperty) ?? false);
        set => SetValue(IsPointerOverProperty, value);
    }

    public static readonly new DependencyProperty IsPointerPressedProperty =
        DependencyProperty.Register("IsPointerPressed", typeof(bool), typeof(SliderChrome),
            new PropertyMetadata(false, (d, e) => ((SliderChrome)d).Invalidate()));

    public new bool IsPointerPressed
    {
        get => (bool)(GetValue(IsPointerPressedProperty) ?? false);
        set => SetValue(IsPointerPressedProperty, value);
    }

    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.Register("IsFocused", typeof(bool), typeof(SliderChrome),
            new PropertyMetadata(false, (d, e) => ((SliderChrome)d).Invalidate()));

    public bool IsFocused
    {
        get => (bool)(GetValue(IsFocusedProperty) ?? false);
        set => SetValue(IsFocusedProperty, value);
    }





    public override void OnRender(DrawingContext context)
    {
        var activeFamily = ActualThemeFamily;
        var activeTheme = ActualTheme;



        float baseThumbRadius = activeFamily == VisualThemeFamily.macOS ? 10f : 8f;
        float trackHeight = 4f;
        float yCenter = Size.Y / 2f;

        float width = Size.X;
        float trackWidth = width - 2 * baseThumbRadius;

        float pct = 0f;
        if (Maximum > Minimum)
        {
            pct = (Value - Minimum) / (Maximum - Minimum);
        }

        float thumbX = baseThumbRadius + pct * trackWidth;
        float drawThumbRadius;
        if (activeFamily == VisualThemeFamily.macOS)
        {
            drawThumbRadius = IsPointerPressed ? 12f : IsPointerOver ? 11f : 10f;
        }
        else
        {
            drawThumbRadius = (IsPointerOver || IsPointerPressed) && IsEnabled ? 9f : 7f;
        }

        Rect trackRect = new Rect(baseThumbRadius, yCenter - trackHeight / 2f, trackWidth, trackHeight);
        
        if (activeFamily == VisualThemeFamily.macOS)
        {
            // 1. Draw macOS Track Background (Inactive part)
            Brush inactiveBg = ThemeManager.GetBrush(IsEnabled ? "SliderTrackFill" : "SliderTrackFillDisabled", activeTheme, activeFamily);
            context.DrawRoundedRectangle(inactiveBg, null, trackRect, trackHeight / 2f);

            // 2. Draw macOS Track Progress (Active part)
            if (thumbX > baseThumbRadius)
            {
                Rect activeRect = new Rect(baseThumbRadius, yCenter - trackHeight / 2f, thumbX - baseThumbRadius, trackHeight);
                Brush activeBg = ThemeManager.GetBrush(IsEnabled
                    ? (IsPointerPressed ? "SliderTrackValueFillPressed" : IsPointerOver ? "SliderTrackValueFillPointerOver" : "SliderTrackValueFill")
                    : "SliderTrackValueFillDisabled", activeTheme, activeFamily);
                context.DrawRoundedRectangle(activeBg, null, activeRect, trackHeight / 2f);
            }

            // 2.5. Draw macOS Thumb Shadow
            Rect shadowRect = new Rect(thumbX - drawThumbRadius, yCenter - drawThumbRadius + 1.5f, drawThumbRadius * 2f, drawThumbRadius * 2f);
            Brush shadowBg = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.18f));
            context.DrawRoundedRectangle(shadowBg, null, shadowRect, drawThumbRadius);

            // 3. Draw macOS Thumb (Glossy round sphere)
            Rect thumbRect = new Rect(thumbX - drawThumbRadius, yCenter - drawThumbRadius, drawThumbRadius * 2f, drawThumbRadius * 2f);
            Brush thumbBg = activeTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground", activeTheme, activeFamily) : ThemeManager.GetBrush("TextPrimary", activeTheme, activeFamily);
            Pen? thumbBorder = new Pen(ThemeManager.GetBrush("SliderThumbBorderBrush", activeTheme, activeFamily), 1f);
            context.DrawRoundedRectangle(thumbBg, thumbBorder, thumbRect, drawThumbRadius);

            // 4. Focus visual if focused
            if (IsEnabled && IsFocused)
            {
                var accentColor = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
                var focusPen = new Pen(accentColor, 1.5f);
                Rect focusRect = new Rect(thumbRect.X - 2.5f, thumbRect.Y - 2.5f, thumbRect.Width + 5f, thumbRect.Height + 5f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, drawThumbRadius + 2.5f);
            }
        }
        else
        {
            Rect inactiveRect = new Rect(thumbX, yCenter - trackHeight / 2f, Math.Max(0f, width - baseThumbRadius - thumbX), trackHeight);
            Brush inactiveBg = ThemeManager.GetBrush(IsEnabled ? "SliderTrackFill" : "SliderTrackFillDisabled", activeTheme, activeFamily);
            context.DrawRectangle(inactiveBg, null, inactiveRect);

            if (thumbX > baseThumbRadius)
            {
                Rect activeRect = new Rect(baseThumbRadius, yCenter - trackHeight / 2f, thumbX - baseThumbRadius, trackHeight);
                Brush activeBg = ThemeManager.GetBrush(IsEnabled
                    ? (IsPointerPressed ? "SliderTrackValueFillPressed" : IsPointerOver ? "SliderTrackValueFillPointerOver" : "SliderTrackValueFill")
                    : "SliderTrackValueFillDisabled", activeTheme, activeFamily);
                context.DrawRectangle(activeBg, null, activeRect);
            }

            Rect thumbRect = new Rect(thumbX - drawThumbRadius, yCenter - drawThumbRadius, drawThumbRadius * 2f, drawThumbRadius * 2f);
            Brush thumbBg;
            Pen? thumbBorder;

            if (!IsEnabled)
            {
                thumbBg = ThemeManager.GetBrush("ControlBackground", activeTheme, activeFamily);
                thumbBorder = new Pen(ThemeManager.GetBrush("SliderThumbBorderBrush", activeTheme, activeFamily), 1f);
            }
            else if (IsPointerPressed)
            {
                thumbBg = ThemeManager.GetBrush("SliderTrackValueFillPressed", activeTheme, activeFamily);
                thumbBorder = new Pen(activeTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground", activeTheme, activeFamily) : ThemeManager.GetBrush("TextPrimary", activeTheme, activeFamily), 1.5f);
            }
            else if (IsPointerOver)
            {
                thumbBg = activeTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground", activeTheme, activeFamily) : ThemeManager.GetBrush("TextPrimary", activeTheme, activeFamily);
                thumbBorder = new Pen(ThemeManager.GetBrush("SliderTrackValueFillPointerOver", activeTheme, activeFamily), 1f);
            }
            else
            {
                thumbBg = activeTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground", activeTheme, activeFamily) : ThemeManager.GetBrush("TextPrimary", activeTheme, activeFamily);
                thumbBorder = new Pen(ThemeManager.GetBrush("SliderThumbBorderBrush", activeTheme, activeFamily), 1f);
            }

            context.DrawRoundedRectangle(thumbBg, thumbBorder, thumbRect, drawThumbRadius);

            if (IsEnabled && IsFocused)
            {
                var accentColor = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
                var focusPen = new Pen(accentColor, 1.5f);
                Rect focusRect = new Rect(thumbRect.X - 2.5f, thumbRect.Y - 2.5f, thumbRect.Width + 5f, thumbRect.Height + 5f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, drawThumbRadius + 2.5f);
            }
        }
    }
}
