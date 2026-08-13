using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class CompositorClipTests
{
    [Fact]
    public void AdvancedBlendSamplingUniformLayoutCarriesRuntimeMode()
    {
        Assert.Equal(32, Marshal.SizeOf<AdvancedBlendSamplingUniforms>());
        Assert.Equal(
            16,
            Marshal.OffsetOf<AdvancedBlendSamplingUniforms>(
                nameof(AdvancedBlendSamplingUniforms.BlendMode)).ToInt32());
    }

    [Fact]
    public void MaskSamplingUniformEqualityIsAllocationFree()
    {
        var left = new MaskSamplingUniforms
        {
            Coordinate0 = new Vector4(1f, 2f, 3f, 4f),
            Coordinate1 = new Vector4(5f, 6f, 7f, 8f),
            Bounds = new Vector4(9f, 10f, 11f, 12f),
            CornerRadiiX = new Vector4(13f, 14f, 15f, 16f),
            CornerRadiiY = new Vector4(17f, 18f, 19f, 20f),
            Options = new Vector4(21f, 22f, 23f, 24f)
        };
        MaskSamplingUniforms right = left;

        Assert.True(left.Equals(right));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allEqual = true;
        for (int iteration = 0; iteration < 10_000; iteration++)
            allEqual &= left.Equals(right);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allEqual);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void NonAxisAlignedRectangularClipPreservesRotatedEdges()
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
            var outsideRotatedRectX = ClampToPixel((int)MathF.Floor(expected.X + 2f), window.Width);
            var outsideRotatedRectY = ClampToPixel((int)MathF.Floor(expected.Y + 4f), window.Height);

            Assert.True(leftProbeX < (int)MathF.Round(legacy.X), $"Left probe {leftProbeX} must sit outside legacy clip {legacy}.");
            Assert.True(rightProbeX > (int)MathF.Round(legacy.Right), $"Right probe {rightProbeX} must sit outside legacy clip {legacy}.");

            AssertPainted(pixels, window.Width, leftProbeX, midY);
            AssertPainted(pixels, window.Width, rightProbeX, midY);
            AssertNotPainted(pixels, window.Width, outsideRotatedRectX, outsideRotatedRectY);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RotatedRectangularClipUsesAnalyticMaskWithoutTextureResidency()
    {
        using var window = new HeadlessWindow(220, 180);
        window.Content = new RotatedClipVisual();

        window.Render();

        Assert.Equal(0UL, window.Compositor.Metrics.MaskTexturePoolBytes);
        Assert.Equal(0UL, window.Compositor.Metrics.MaskRenderScratchTextureBytes);
        Assert.Equal(0UL, window.Compositor.Metrics.MaskCopyBytes);
        Assert.Equal(0, window.Compositor.Metrics.MaskRenderPassCount);
        Assert.True(window.Compositor.Metrics.AffineRectangleMaskPeakDemand >= 1);
        Assert.Equal(1, window.Compositor.Metrics.AnalyticMaskBindGroupCount);
    }

    [Fact]
    public void NestedRotatedGeometryMasksPreserveIntersectionWithBoundedResidency()
    {
        using var window = new HeadlessWindow(160, 120);
        window.Content = new NestedRotatedGeometryMaskVisual();

        window.Render();

        var pixels = window.ReadPixels();
        var insideBoth = ReadPixel(pixels, window.Width, x: 80, y: 60);
        var insideParentOnly = ReadPixel(pixels, window.Width, x: 35, y: 60);
        var outsideBoth = ReadPixel(pixels, window.Width, x: 10, y: 10);

        Assert.True(insideBoth.X > 180f && insideBoth.Z < 80f, $"Expected nested intersection to be red, found {insideBoth}.");
        Assert.True(insideParentOnly.Z > 160f && insideParentOnly.X < 80f, $"Expected parent-only area to retain blue, found {insideParentOnly}.");
        Assert.True(outsideBoth.Z > 160f && outsideBoth.X < 80f, $"Expected outside area to retain blue, found {outsideBoth}.");

        ulong fullTargetBytes = (ulong)window.Width * window.Height;
        Assert.InRange(
            window.Compositor.Metrics.MaskTexturePoolBytes,
            1UL,
            fullTargetBytes * 2UL - 1UL);
        Assert.Equal(0UL, window.Compositor.Metrics.MaskRenderScratchTextureBytes);
        Assert.Equal(0UL, window.Compositor.Metrics.MaskCopyBytes);
    }

    [Fact]
    public void FullTargetGeometryMaskRendersDirectlyWithoutScratchOrCopy()
    {
        using var window = new HeadlessWindow(160, 120);
        window.Content = new FullTargetEllipseClipVisual();

        window.Render();

        var pixels = window.ReadPixels();
        var center = ReadPixel(pixels, window.Width, x: 80, y: 60);
        var corner = ReadPixel(pixels, window.Width, x: 2, y: 2);
        Assert.True(
            center.X > 180f && center.Y < 80f && center.Z < 80f,
            $"Expected the full-target ellipse center to be red, found {center}.");
        Assert.True(
            corner.Z > 160f && corner.X < 80f,
            $"Expected the clipped corner to preserve blue, found {corner}.");

        ulong fullTargetBytes = (ulong)window.Width * window.Height;
        Assert.Equal(
            fullTargetBytes,
            window.Compositor.Metrics.MaskTexturePoolBytes);
        Assert.Equal(
            0UL,
            window.Compositor.Metrics.MaskRenderScratchTextureBytes);
        Assert.Equal(0UL, window.Compositor.Metrics.MaskCopyBytes);
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
    public void AxisAlignedRectangleGeometryClipUsesScissorWithoutMaskTexture()
    {
        using var window = new HeadlessWindow(180, 110);
        window.Content = new OpacityGeometryClipVisual();

        window.Render();

        Assert.Equal(0, window.Compositor.Metrics.MaskBindGroupCount);
    }

    [Fact]
    public void CombinedDifferenceGeometryClipCutsNativeMaskHole()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 90);
        window.Content = new DifferenceGeometryClipVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var outsideHole = ReadPixel(pixels, window.Width, x: 30, y: 45);
            var insideHole = ReadPixel(pixels, window.Width, x: 80, y: 45);

            Assert.True(
                outsideHole.X > 180f && outsideHole.Y < 80f && outsideHole.Z < 80f && outsideHole.W == 255f,
                $"Expected red outside the difference hole, found RGBA({outsideHole.X}, {outsideHole.Y}, {outsideHole.Z}, {outsideHole.W}).");
            Assert.True(
                insideHole.X < 60f && insideHole.Y < 80f && insideHole.Z > 140f && insideHole.W == 255f,
                $"Expected blue background inside the difference hole, found RGBA({insideHole.X}, {insideHole.Y}, {insideHole.Z}, {insideHole.W}).");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void SourceBlendGeometryClipPreservesDestinationBetweenDisjointFigures()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 80);
        window.Content = new SourceBlendGeometryClipVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var left = ReadPixel(pixels, window.Width, x: 30, y: 40);
            var gap = ReadPixel(pixels, window.Width, x: 80, y: 40);
            var right = ReadPixel(pixels, window.Width, x: 130, y: 40);

            Assert.True(left.X > 180f && left.Y < 80f && left.Z < 80f && left.W == 255f);
            Assert.True(right.X > 180f && right.Y < 80f && right.Z < 80f && right.W == 255f);
            Assert.True(
                gap.X < 80f && gap.Y < 80f && gap.Z > 160f && gap.W == 255f,
                $"Expected the clipped gap to preserve its blue destination, found RGBA({gap.X}, {gap.Y}, {gap.Z}, {gap.W}).");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void OuterClipBoundsUseParentCoordinateSpace()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(140, 100);
        window.Content = new OuterClipOffsetVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var inside = ReadPixel(pixels, window.Width, x: 45, y: 35);
            var outside = ReadPixel(pixels, window.Width, x: 85, y: 65);

            Assert.True(
                inside.Y > 180f && inside.X < 80f && inside.Z < 80f && inside.W == 255f,
                $"Expected outer clip to keep parent-space probe painted, found RGBA({inside.X}, {inside.Y}, {inside.Z}, {inside.W}).");
            Assert.False(
                outside.Y > 180f && outside.X < 80f && outside.Z < 80f,
                $"Expected outer clip to reject offset-space probe, found RGBA({outside.X}, {outside.Y}, {outside.Z}, {outside.W}).");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void OuterCompositeClipUsesParentRelativeTransform()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(100, 70);
        window.Content = new OuterCompositeClipVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var inside = ReadPixel(pixels, window.Width, x: 25, y: 15);
            var outside = ReadPixel(pixels, window.Width, x: 45, y: 35);

            Assert.True(
                inside.Y > 180f &&
                inside.X < 80f &&
                inside.Z < 80f &&
                inside.W == 255f,
                $"Expected transformed outer clip to retain its parent-space probe, found RGBA({inside.X}, {inside.Y}, {inside.Z}, {inside.W}).");
            Assert.False(
                outside.Y > 180f &&
                outside.X < 80f &&
                outside.Z < 80f,
                $"Expected transformed outer clip to reject the outside probe, found RGBA({outside.X}, {outside.Y}, {outside.Z}, {outside.W}).");
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

    private static void AssertNotPainted(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        byte r = pixels[index + 0];
        byte g = pixels[index + 1];
        byte b = pixels[index + 2];
        byte a = pixels[index + 3];

        Assert.False(
            r > 20 && g > 120 && b > 180,
            $"Expected rotated clip edge to reject probe at ({x}, {y}), found RGBA({r}, {g}, {b}, {a}).");
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

    private sealed class NestedRotatedGeometryMaskVisual : FrameworkElement
    {
        private readonly PathGeometry _parent =
            PrimitivePathGeometry.CreateEllipse(new Vector2(80f, 60f), 50f, 40f);
        private readonly PathGeometry _child =
            PrimitivePathGeometry.CreateRoundedRectangle(55f, 35f, 50f, 50f, 8f, 8f);
        private readonly SolidColorBrush _background =
            new(new Vector4(0.05f, 0.1f, 0.8f, 1f));
        private readonly SolidColorBrush _foreground =
            new(new Vector4(1f, 0f, 0f, 1f));
        private readonly Matrix4x4 _childTransform =
            Matrix4x4.CreateTranslation(-80f, -60f, 0f) *
            Matrix4x4.CreateRotationZ(0.35f) *
            Matrix4x4.CreateTranslation(80f, 60f, 0f);

        public NestedRotatedGeometryMaskVisual()
        {
            Width = 160f;
            Height = 120f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 160f, 120f));
            context.PushGeometryClip(_parent);
            context.PushGeometryClip(_child, _childTransform);
            context.DrawRectangle(_foreground, null, new Rect(0f, 0f, 160f, 120f));
            context.PopGeometryClip();
            context.PopGeometryClip();
        }
    }

    private sealed class FullTargetEllipseClipVisual : FrameworkElement
    {
        private readonly PathGeometry _clip =
            PrimitivePathGeometry.CreateEllipse(
                new Vector2(80f, 60f),
                80f,
                60f);
        private readonly SolidColorBrush _background =
            new(new Vector4(0f, 0f, 1f, 1f));
        private readonly SolidColorBrush _foreground =
            new(new Vector4(1f, 0f, 0f, 1f));

        public FullTargetEllipseClipVisual()
        {
            Width = 160f;
            Height = 120f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _background,
                null,
                new Rect(0f, 0f, 160f, 120f));
            context.PushGeometryClip(_clip);
            context.DrawRectangle(
                _foreground,
                null,
                new Rect(0f, 0f, 160f, 120f));
            context.PopGeometryClip();
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

    private sealed class OuterClipOffsetVisual : FrameworkElement
    {
        private readonly SolidColorBrush _green = new(new Vector4(0f, 1f, 0f, 1f));

        public OuterClipOffsetVisual()
        {
            Width = 140f;
            Height = 100f;
            Offset = new Vector2(40f, 30f);
            OuterClipBounds = new Rect(30f, 25f, 40f, 40f);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_green, null, new Rect(-200f, -200f, 500f, 500f));
        }
    }

    private sealed class OuterCompositeClipVisual : FrameworkElement
    {
        private readonly SolidColorBrush _green =
            new(new Vector4(0f, 1f, 0f, 1f));

        public OuterCompositeClipVisual()
        {
            Width = 100f;
            Height = 70f;
            Offset = new Vector2(40f, 30f);
            SetOuterCompositeClips(
                new[]
                {
                    new VisualCompositeClip(
                        new Rect(0f, 0f, 20f, 20f),
                        Matrix4x4.CreateTranslation(20f, 10f, 0f))
                });
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                _green,
                null,
                new Rect(-200f, -200f, 500f, 500f));
        }
    }

    private sealed class DifferenceGeometryClipVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background = new(new Vector4(0.05f, 0.1f, 0.7f, 1f));
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));
        private readonly PathGeometry _clip = new()
        {
            IsCombined = true,
            PathA = PrimitivePathGeometry.CreateRectangle(0f, 0f, 160f, 90f),
            PathB = PrimitivePathGeometry.CreateRectangle(60f, 25f, 40f, 40f),
            Op = 0,
            FillRule = FillRule.Nonzero
        };

        public DifferenceGeometryClipVisual()
        {
            Width = 160f;
            Height = 90f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 160f, 90f));
            context.PushGeometryClip(_clip);
            context.DrawRectangle(_red, null, new Rect(0f, 0f, 160f, 90f));
            context.PopGeometryClip();
        }
    }

    private sealed class SourceBlendGeometryClipVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background = new(new Vector4(0.05f, 0.1f, 0.8f, 1f));
        private readonly SolidColorBrush _foreground = new(new Vector4(1f, 0f, 0f, 1f));
        private readonly PathGeometry _clip = new();

        public SourceBlendGeometryClipVisual()
        {
            Width = 160f;
            Height = 80f;
            _clip.Figures.AddRange(PrimitivePathGeometry.CreateRectangle(10f, 15f, 40f, 50f).Figures);
            _clip.Figures.AddRange(PrimitivePathGeometry.CreateRectangle(110f, 15f, 40f, 50f).Figures);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 160f, 80f));
            context.PushGeometryClip(_clip);
            context.PushBlendMode(GpuBlendMode.Src);
            context.DrawRectangle(_foreground, null, new Rect(0f, 0f, 160f, 80f));
            context.PopBlendMode();
            context.PopGeometryClip();
        }
    }
}
