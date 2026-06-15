using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class CompositorClipTests
{
    [Fact]
    public void RectangularClipUsesAllTransformedCorners()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(220, 180);
        var visual = new RotatedClipVisual();
        window.Content = visual;

        try
        {
            window.Render();

            var expected = ComputeTransformedBounds(visual.ClipRect, visual.GetGlobalTransformMatrix());
            var legacy = ComputeTwoCornerBounds(visual.ClipRect, visual.GetGlobalTransformMatrix());

            Assert.True(expected.X < legacy.X, $"Expected four-corner bounds to extend left of legacy bounds: {expected} vs {legacy}.");
            Assert.True(expected.Right > legacy.Right, $"Expected four-corner bounds to extend right of legacy bounds: {expected} vs {legacy}.");

            var pixels = window.ReadPixels();
            var midY = ClampToPixel((int)MathF.Round(expected.Y + expected.Height * 0.5f), window.Height);
            var leftProbeX = ClampToPixel((int)MathF.Floor(expected.X + 2f), window.Width);
            var rightProbeX = ClampToPixel((int)MathF.Ceiling(expected.Right - 2f), window.Width);

            Assert.True(leftProbeX < (int)MathF.Round(legacy.X), $"Left probe {leftProbeX} must sit outside legacy clip {legacy}.");
            Assert.True(rightProbeX > (int)MathF.Round(legacy.Right), $"Right probe {rightProbeX} must sit outside legacy clip {legacy}.");

            AssertPainted(pixels, window.Width, leftProbeX, midY);
            AssertPainted(pixels, window.Width, rightProbeX, midY);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void GeometryClipMaskDoesNotInheritActiveOpacity()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(180, 110);
        window.Content = new OpacityGeometryClipVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var unclipped = ReadPixel(pixels, window.Width, x: 45, y: 55);
            var clipped = ReadPixel(pixels, window.Width, x: 125, y: 55);

            AssertColorNear(unclipped, clipped, tolerance: 10);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void PopOpacityRestoresAfterZeroOpacityScope()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(80, 80);
        window.Content = new ZeroOpacityRestoreVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var restored = ReadPixel(pixels, window.Width, x: 40, y: 40);

            Assert.True(
                restored.Y > 160f && restored.X < 80f && restored.Z < 80f && restored.W == 255f,
                $"Expected restored green draw after zero-opacity pop, found RGBA({restored.X}, {restored.Y}, {restored.Z}, {restored.W}).");
        }
        finally
        {
            window.Content = null;
        }
    }

    private static Rect ComputeTransformedBounds(Rect rect, Matrix4x4 transform)
    {
        var p0 = Vector2.Transform(new Vector2(rect.X, rect.Y), transform);
        var p1 = Vector2.Transform(new Vector2(rect.X + rect.Width, rect.Y), transform);
        var p2 = Vector2.Transform(new Vector2(rect.X + rect.Width, rect.Y + rect.Height), transform);
        var p3 = Vector2.Transform(new Vector2(rect.X, rect.Y + rect.Height), transform);

        var minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        var minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        var maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        var maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static Rect ComputeTwoCornerBounds(Rect rect, Matrix4x4 transform)
    {
        var topLeft = Vector2.Transform(new Vector2(rect.X, rect.Y), transform);
        var bottomRight = Vector2.Transform(new Vector2(rect.X + rect.Width, rect.Y + rect.Height), transform);

        var minX = MathF.Min(topLeft.X, bottomRight.X);
        var minY = MathF.Min(topLeft.Y, bottomRight.Y);
        var maxX = MathF.Max(topLeft.X, bottomRight.X);
        var maxY = MathF.Max(topLeft.Y, bottomRight.Y);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static int ClampToPixel(int value, uint extent)
    {
        return Math.Clamp(value, 0, (int)extent - 1);
    }

    private static void AssertPainted(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        byte r = pixels[index + 0];
        byte g = pixels[index + 1];
        byte b = pixels[index + 2];
        byte a = pixels[index + 3];

        Assert.True(
            r > 20 && g > 120 && b > 180 && a == 255,
            $"Expected painted clip probe at ({x}, {y}), found RGBA({r}, {g}, {b}, {a}).");
    }

    private static Vector4 ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new Vector4(
            pixels[index + 0],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static void AssertColorNear(Vector4 expected, Vector4 actual, int tolerance)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, tolerance);
        Assert.InRange(MathF.Abs(expected.W - actual.W), 0, tolerance);
    }

    private sealed class RotatedClipVisual : FrameworkElement
    {
        public Rect ClipRect { get; } = new(30f, 25f, 80f, 35f);

        public RotatedClipVisual()
        {
            Width = 220f;
            Height = 180f;
            ClipBounds = ClipRect;
            Transform = new Matrix4x4(
                1f, 0.35f, 0f, 0f,
                -0.2f, 1.1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                15f, 8f, 0f, 1f);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0.1f, 0.6f, 0.9f, 1f)),
                null,
                new Rect(-200f, -200f, 500f, 500f));
        }
    }

    private sealed class OpacityGeometryClipVisual : FrameworkElement
    {
        private readonly PathGeometry _clip = PrimitivePathGeometry.CreateRectangle(100f, 30f, 50f, 50f);
        private readonly SolidColorBrush _brush = new(new Vector4(0.1f, 0.8f, 0.25f, 1f));

        public OpacityGeometryClipVisual()
        {
            Width = 180f;
            Height = 110f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushOpacity(0.5f);
            context.DrawRectangle(_brush, null, new Rect(20f, 30f, 50f, 50f));
            context.PopOpacity();

            context.PushOpacity(0.5f);
            context.PushGeometryClip(_clip);
            context.DrawRectangle(_brush, null, new Rect(100f, 30f, 50f, 50f));
            context.PopGeometryClip();
            context.PopOpacity();
        }
    }

    private sealed class ZeroOpacityRestoreVisual : FrameworkElement
    {
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));
        private readonly SolidColorBrush _green = new(new Vector4(0f, 1f, 0f, 1f));

        public ZeroOpacityRestoreVisual()
        {
            Width = 80f;
            Height = 80f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushOpacity(0f);
            context.DrawRectangle(_red, null, new Rect(10f, 10f, 60f, 60f));
            context.PopOpacity();
            context.DrawRectangle(_green, null, new Rect(10f, 10f, 60f, 60f));
        }
    }
}
