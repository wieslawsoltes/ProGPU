using System;
using Avalonia;
using ProGPU.Backend;

namespace Avalonia.SilkNet;

/// <summary>
/// Converts Silk.NET screen-coordinate sizes to Avalonia's logical/physical
/// desktop contracts without allocating per frame.
/// </summary>
internal static class SilkNetDisplayMetrics
{
    internal static double ResolveDesktopScaling(
        bool isMacOS,
        double renderScaling) =>
        isMacOS
            ? 1d
            : DisplayScaleResolver.NormalizeDisplayScale(
                renderScaling);

    internal static Size? ResolveFrameSize(
        Size clientSize,
        NativeWindowFrameInsets? frameInsets)
    {
        if (frameInsets is not { } insets)
            return null;

        return new Size(
            Math.Max(
                0,
                clientSize.Width + insets.Left + insets.Right),
            Math.Max(
                0,
                clientSize.Height + insets.Top + insets.Bottom));
    }

    internal static double ResolveRenderScaling(
        int windowWidth,
        int windowHeight,
        int framebufferWidth,
        int framebufferHeight,
        double? nativeDisplayScale)
    {
        double reportedScale = ResolveReportedFramebufferScale(
            windowWidth,
            windowHeight,
            framebufferWidth,
            framebufferHeight);
        if (reportedScale > 1d)
            return reportedScale;

        return nativeDisplayScale.HasValue
            ? DisplayScaleResolver.NormalizeDisplayScale(
                nativeDisplayScale.Value)
            : reportedScale;
    }

    internal static double ResolveReportedFramebufferScale(
        int windowWidth,
        int windowHeight,
        int framebufferWidth,
        int framebufferHeight)
    {
        if (windowWidth <= 0 ||
            windowHeight <= 0 ||
            framebufferWidth <= 0 ||
            framebufferHeight <= 0)
        {
            return 1d;
        }

        double x = framebufferWidth / (double)windowWidth;
        double y = framebufferHeight / (double)windowHeight;
        return DisplayScaleResolver.NormalizeDisplayScale(
            Math.Max(1d, (x + y) * 0.5d));
    }

    internal static PixelSize ResolveFramebufferPixelSize(
        int windowWidth,
        int windowHeight,
        int framebufferWidth,
        int framebufferHeight,
        double renderScaling)
    {
        double scale =
            DisplayScaleResolver.NormalizeDisplayScale(renderScaling);
        int scaledWidth = ScaleLength(windowWidth, scale);
        int scaledHeight = ScaleLength(windowHeight, scale);
        return new PixelSize(
            Math.Max(
                Math.Max(1, framebufferWidth),
                scaledWidth),
            Math.Max(
                Math.Max(1, framebufferHeight),
                scaledHeight));
    }

    private static int ScaleLength(int value, double scale) =>
        Math.Max(
            1,
            checked((int)Math.Ceiling(Math.Max(0, value) * scale)));
}
