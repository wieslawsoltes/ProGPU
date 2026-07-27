using System;
using Avalonia;
using Avalonia.Platform;

namespace Avalonia.ProGpu;

/// <summary>
/// Allocation-free rectangle operations used at the Avalonia transport edge.
/// </summary>
internal static class AvaloniaRectMath
{
    public static bool IsEmpty(LtrbPixelRect rectangle) =>
        rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top;

    public static LtrbPixelRect Union(LtrbPixelRect left, LtrbPixelRect right) =>
        new()
        {
            Left = Math.Min(left.Left, right.Left),
            Top = Math.Min(left.Top, right.Top),
            Right = Math.Max(left.Right, right.Right),
            Bottom = Math.Max(left.Bottom, right.Bottom)
        };

    public static Rect ToRect(LtrbPixelRect rectangle) =>
        new(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right - rectangle.Left,
            rectangle.Bottom - rectangle.Top);

    public static bool Intersects(LtrbPixelRect left, LtrbRect right) =>
        right.Left < left.Right &&
        left.Left < right.Right &&
        right.Top < left.Bottom &&
        left.Top < right.Bottom;

    public static bool Intersects(Rect left, Rect right) =>
        left.X < right.Right &&
        right.X < left.Right &&
        left.Y < right.Bottom &&
        right.Y < left.Bottom;
}
