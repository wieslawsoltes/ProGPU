using System;
using System.Collections.Generic;
using Avalonia.Controls;
#if !AVALONIA11
using Avalonia.Controls.Chrome;
using Avalonia.Input;
#endif
using Avalonia.Platform;
#if !AVALONIA11
using Avalonia.VisualTree;
#endif
#if !AVALONIA11
using Avalonia.Controls.Platform;
#endif
using ProGPU.Backend;
using Silk.NET.Windowing;

namespace Avalonia.SilkNet;

internal readonly record struct SilkNetTransparencyChoice(
    WindowTransparencyLevel Level,
    NativeWindowBackdrop Backdrop);

internal static class SilkNetWindowChrome
{
#if AVALONIA11
    internal static NativeWindowChromeHints MapChromeHints(
        ExtendClientAreaChromeHints hints) =>
        (NativeWindowChromeHints)(int)hints;
#endif

#if !AVALONIA11
    internal static NativeWindowTheme MapFrameTheme(
        PlatformThemeVariant? requested,
        PlatformThemeVariant platformDefault)
    {
        PlatformThemeVariant resolved =
            requested ?? platformDefault;
        return resolved == PlatformThemeVariant.Dark
            ? NativeWindowTheme.Dark
            : NativeWindowTheme.Light;
    }
#endif

    internal static SilkNetTransparencyChoice SelectTransparency(
        IReadOnlyList<WindowTransparencyLevel> requested,
        NativeWindowCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(requested);
        for (int index = 0; index < requested.Count; index++)
        {
            WindowTransparencyLevel level = requested[index];
            if (level == WindowTransparencyLevel.Mica &&
                capabilities.Supports(
                    NativeWindowFeatures.Mica))
            {
                return new SilkNetTransparencyChoice(
                    WindowTransparencyLevel.Mica,
                    NativeWindowBackdrop.Mica);
            }
            if (level == WindowTransparencyLevel.AcrylicBlur &&
                capabilities.Supports(
                    NativeWindowFeatures.Acrylic))
            {
                return new SilkNetTransparencyChoice(
                    WindowTransparencyLevel.AcrylicBlur,
                    NativeWindowBackdrop.Acrylic);
            }
            if (level == WindowTransparencyLevel.Blur &&
                capabilities.Supports(
                    NativeWindowFeatures.Blur))
            {
                return new SilkNetTransparencyChoice(
                    WindowTransparencyLevel.Blur,
                    NativeWindowBackdrop.Blur);
            }
            if (level == WindowTransparencyLevel.Transparent &&
                capabilities.Supports(
                    NativeWindowFeatures.Transparent))
            {
                return new SilkNetTransparencyChoice(
                    WindowTransparencyLevel.Transparent,
                    NativeWindowBackdrop.Transparent);
            }
            if (level == WindowTransparencyLevel.None)
            {
                return new SilkNetTransparencyChoice(
                    WindowTransparencyLevel.None,
                    NativeWindowBackdrop.None);
            }
        }

        return new SilkNetTransparencyChoice(
            WindowTransparencyLevel.None,
            NativeWindowBackdrop.None);
    }

    internal static WindowBorder GetInitialWindowBorder(
        NativeWindowDecorations decorations,
        bool extendClientArea,
        bool canResize)
    {
        if (decorations == NativeWindowDecorations.None ||
            extendClientArea)
        {
            return WindowBorder.Hidden;
        }
        if (decorations == NativeWindowDecorations.BorderOnly ||
            !canResize)
        {
            return WindowBorder.Fixed;
        }
        return WindowBorder.Resizable;
    }

    internal static NativeResizeEdge MapResizeEdge(
        WindowEdge edge) =>
        edge switch
        {
            WindowEdge.West => NativeResizeEdge.Left,
            WindowEdge.North => NativeResizeEdge.Top,
            WindowEdge.East => NativeResizeEdge.Right,
            WindowEdge.South => NativeResizeEdge.Bottom,
            WindowEdge.NorthWest => NativeResizeEdge.TopLeft,
            WindowEdge.NorthEast => NativeResizeEdge.TopRight,
            WindowEdge.SouthWest => NativeResizeEdge.BottomLeft,
            WindowEdge.SouthEast => NativeResizeEdge.BottomRight,
            _ => throw new ArgumentOutOfRangeException(
                nameof(edge),
                edge,
                "Unsupported resize edge.")
        };

#if !AVALONIA11
    internal static bool TryMapResizeRole(
        WindowDecorationsElementRole role,
        out NativeResizeEdge edge)
    {
        edge = role switch
        {
            WindowDecorationsElementRole.ResizeN => NativeResizeEdge.Top,
            WindowDecorationsElementRole.ResizeS => NativeResizeEdge.Bottom,
            WindowDecorationsElementRole.ResizeE => NativeResizeEdge.Right,
            WindowDecorationsElementRole.ResizeW => NativeResizeEdge.Left,
            WindowDecorationsElementRole.ResizeNE => NativeResizeEdge.TopRight,
            WindowDecorationsElementRole.ResizeNW => NativeResizeEdge.TopLeft,
            WindowDecorationsElementRole.ResizeSE => NativeResizeEdge.BottomRight,
            WindowDecorationsElementRole.ResizeSW => NativeResizeEdge.BottomLeft,
            _ => default
        };
        return role is
            WindowDecorationsElementRole.ResizeN or
            WindowDecorationsElementRole.ResizeS or
            WindowDecorationsElementRole.ResizeE or
            WindowDecorationsElementRole.ResizeW or
            WindowDecorationsElementRole.ResizeNE or
            WindowDecorationsElementRole.ResizeNW or
            WindowDecorationsElementRole.ResizeSE or
            WindowDecorationsElementRole.ResizeSW;
    }

    internal static WindowDecorationsElementRole? ResolveChromeRole(
        IInputRoot inputRoot,
        Point point)
    {
        ArgumentNullException.ThrowIfNull(inputRoot);
        Visual? current = inputRoot.FocusRoot.InputHitTest(
            point,
            enabledElementsOnly: false) as Visual;
        for (; current is not null; current = current.GetVisualParent())
        {
            WindowDecorationsElementRole role =
                WindowDecorationProperties.GetElementRole(current);
            if (role != WindowDecorationsElementRole.None)
                return role;
        }

        return null;
    }
#endif

    internal static NativeWindowSize ToMinimumSize(
        Size size,
        double desktopScaling = 1d) =>
        new(
            NormalizeMinimum(size.Width, desktopScaling),
            NormalizeMinimum(size.Height, desktopScaling));

    internal static NativeWindowSize ToMaximumSize(
        Size size,
        double desktopScaling = 1d) =>
        new(
            NormalizeMaximum(size.Width, desktopScaling),
            NormalizeMaximum(size.Height, desktopScaling));

#if !AVALONIA11
    internal static PlatformAllowedWindowActions
        GetAllowedWindowActions(
            bool canResize,
            bool canMinimize,
            bool canMaximize)
    {
        PlatformAllowedWindowActions actions =
            PlatformAllowedWindowActions.Fullscreen;
        if (canMinimize)
            actions |= PlatformAllowedWindowActions.Minimize;
        if (canResize && canMaximize)
            actions |= PlatformAllowedWindowActions.Maximize;
        return actions;
    }

    internal static PlatformRequestedDrawnDecoration
        MapRequestedDrawnDecorations(
            NativeDrawnDecorationParts requested)
    {
        PlatformRequestedDrawnDecoration result =
            PlatformRequestedDrawnDecoration.None;
        if ((requested &
             NativeDrawnDecorationParts.TitleBar) != 0)
        {
            result |=
                PlatformRequestedDrawnDecoration.TitleBar;
        }
        if ((requested &
             NativeDrawnDecorationParts.Border) != 0)
        {
            result |=
                PlatformRequestedDrawnDecoration.Border;
        }
        if ((requested &
             NativeDrawnDecorationParts.ResizeGrips) != 0)
        {
            result |=
                PlatformRequestedDrawnDecoration.ResizeGrips;
        }
        if ((requested &
             NativeDrawnDecorationParts.Shadow) != 0)
        {
            result |=
                PlatformRequestedDrawnDecoration.Shadow;
        }
        return result;
    }
#endif

    private static int NormalizeMinimum(
        double value,
        double desktopScaling)
    {
        if (!double.IsFinite(value) || value <= 0)
            return 0;
        double scale =
            DisplayScaleResolver.NormalizeDisplayScale(
                desktopScaling);
        return checked(
            (int)Math.Min(
                int.MaxValue,
                Math.Ceiling(value * scale)));
    }

    private static int NormalizeMaximum(
        double value,
        double desktopScaling)
    {
        if (!double.IsFinite(value) || value <= 0)
            return int.MaxValue;
        double scale =
            DisplayScaleResolver.NormalizeDisplayScale(
                desktopScaling);
        return checked(
            (int)Math.Min(
                int.MaxValue,
                Math.Floor(value * scale)));
    }
}
