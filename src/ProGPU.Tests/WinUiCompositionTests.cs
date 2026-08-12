using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using ProGPU.Backend;
using ProGPU.Media;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Windows.Graphics;
using Windows.Media.Playback;
using Windows.UI;
using Xunit;
using Color = Windows.UI.Color;

namespace ProGPU.Tests;

public sealed class WinUiCompositionTests
{
    [Fact]
    public void SurfaceBrushPreservesOfficialDefaultsAndInvalidatesOwners()
    {
        using var compositor = new Compositor();
        CompositionSurfaceBrush brush = compositor.CreateSurfaceBrush();
        SpriteVisual visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(40f, 24f);
        visual.Brush = brush;

        Assert.Null(brush.Surface);
        Assert.Equal(
            CompositionBitmapInterpolationMode.Linear,
            brush.BitmapInterpolationMode);
        Assert.Equal(CompositionStretch.Uniform, brush.Stretch);
        Assert.Equal(0.5f, brush.HorizontalAlignmentRatio);
        Assert.Equal(0.5f, brush.VerticalAlignmentRatio);
        Assert.Equal(Vector2.Zero, brush.AnchorPoint);
        Assert.Equal(Vector2.Zero, brush.CenterPoint);
        Assert.Equal(Vector2.Zero, brush.Offset);
        Assert.Equal(Vector2.One, brush.Scale);
        Assert.Equal(0f, brush.RotationAngle);
        Assert.Equal(0f, brush.RotationAngleInDegrees);
        Assert.Equal(Matrix3x2.Identity, brush.TransformMatrix);
        Assert.False(brush.SnapToPixels);

        brush.HorizontalAlignmentRatio = -2f;
        brush.VerticalAlignmentRatio = 3f;
        Assert.Equal(0f, brush.HorizontalAlignmentRatio);
        Assert.Equal(1f, brush.VerticalAlignmentRatio);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => brush.Scale = new Vector2(float.NaN, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => brush.TransformMatrix = new Matrix3x2(
                1f,
                0f,
                0f,
                1f,
                float.PositiveInfinity,
                0f));

        long before = visual.SceneNode.ChangeVersion;
        brush.Offset = Vector2.One;
        Assert.True(visual.SceneNode.ChangeVersion > before);

        brush.Offset = Vector2.Zero;
        brush.Scale = Vector2.One;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            brush.Offset = new Vector2(index & 1, 0f);
            brush.BitmapInterpolationMode = (index & 1) == 0
                ? CompositionBitmapInterpolationMode.Linear
                : CompositionBitmapInterpolationMode.NearestNeighbor;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() -
            allocatedBefore;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SurfaceBrushAcceptsStableMediaPlayerCompositionSurface()
    {
        using var player = new MediaPlayer();
        ICompositionSurface first =
            player.GetProGpuCompositionSurface();
        ICompositionSurface second =
            player.GetProGpuCompositionSurface();

        Assert.Same(first, second);
        Assert.IsAssignableFrom<IProGpuInvalidatingTextureSource>(first);

        using var compositor = new Compositor();
        CompositionSurfaceBrush brush =
            compositor.CreateSurfaceBrush(first);
        Assert.Same(first, brush.Surface);
    }

    [Fact]
    public void SurfaceBrushSamplesRetainedGpuLeaseAndTracksFrameChanges()
    {
        using var window = new HeadlessWindow(64, 48);
        var host = new FrameworkElement
        {
            Width = 64f,
            Height = 48f
        };
        window.Content = host;

        try
        {
            window.Render();
            var texture = new GpuTexture(
                window.Context,
                2,
                1,
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding | TextureUsage.CopyDst,
                "WinUI composition surface",
                alphaMode: GpuTextureAlphaMode.Straight);
            using var surface = new TestCompositionSurface(texture);
            texture.WritePixels<byte>(
                [255, 0, 0, 255, 0, 0, 255, 255]);

            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            CompositionSurfaceBrush brush =
                compositor.CreateSurfaceBrush(surface);
            brush.BitmapInterpolationMode =
                CompositionBitmapInterpolationMode.NearestNeighbor;
            brush.Stretch = CompositionStretch.Fill;
            SpriteVisual visual = compositor.CreateSpriteVisual();
            visual.Size = new Vector2(64f, 48f);
            visual.Brush = brush;
            ElementCompositionPreview.SetElementChildVisual(host, visual);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 8, 24));
            AssertBlue(ReadPixel(pixels, window.Width, 56, 24));
            Assert.Equal(1, surface.AcquireCount);

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, surface.AcquireCount);

            texture.WritePixels<byte>(
                [0, 255, 0, 255, 0, 255, 0, 255]);
            surface.NotifyTextureChanged();
            window.Render();
            AssertGreen(ReadPixel(
                window.ReadPixels(),
                window.Width,
                32,
                24));
            Assert.Equal(2, surface.AcquireCount);

            brush.Stretch = CompositionStretch.Uniform;
            visual.Size = new Vector2(64f, 48f);
            window.Render();
            pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 32, 4));
            AssertGreen(ReadPixel(pixels, window.Width, 32, 24));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void SurfaceBrushUsesGpuGeometryAndOpacityMaskPaths()
    {
        using var window = new HeadlessWindow(108, 36);
        var host = new FrameworkElement
        {
            Width = 108f,
            Height = 36f
        };
        window.Content = host;

        try
        {
            window.Render();
            var texture = new GpuTexture(
                window.Context,
                1,
                1,
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding | TextureUsage.CopyDst,
                "WinUI composition surface masks",
                alphaMode: GpuTextureAlphaMode.Straight);
            texture.WritePixels<byte>([255, 0, 0, 255]);
            using var surface = new TestCompositionSurface(texture);
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            CompositionSurfaceBrush surfaceBrush =
                compositor.CreateSurfaceBrush(surface);
            surfaceBrush.Stretch = CompositionStretch.Fill;

            ShapeVisual shapeVisual = compositor.CreateShapeVisual();
            shapeVisual.Size = new Vector2(36f, 36f);
            CompositionEllipseGeometry ellipse =
                compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2(18f);
            ellipse.Radius = new Vector2(14f);
            CompositionSpriteShape shape =
                compositor.CreateSpriteShape(ellipse);
            shape.FillBrush = surfaceBrush;
            shapeVisual.Shapes.Add(shape);

            SpriteVisual masked = compositor.CreateSpriteVisual();
            masked.Offset = new Vector3(36f, 0f, 0f);
            masked.Size = new Vector2(36f, 36f);
            CompositionMaskBrush mask = compositor.CreateMaskBrush();
            mask.Source = surfaceBrush;
            mask.Mask = compositor.CreateColorBrush(
                Color.FromArgb(128, 255, 255, 255));
            masked.Brush = mask;

            ShapeVisual strokeVisual = compositor.CreateShapeVisual();
            strokeVisual.Offset = new Vector3(72f, 0f, 0f);
            strokeVisual.Size = new Vector2(36f, 36f);
            CompositionEllipseGeometry strokeEllipse =
                compositor.CreateEllipseGeometry();
            strokeEllipse.Center = new Vector2(18f);
            strokeEllipse.Radius = new Vector2(14f);
            CompositionSpriteShape strokeShape =
                compositor.CreateSpriteShape(strokeEllipse);
            strokeShape.StrokeBrush = surfaceBrush;
            strokeShape.StrokeThickness = 4f;
            strokeVisual.Shapes.Add(strokeShape);

            ContainerVisual root = compositor.CreateContainerVisual();
            root.Children.InsertAtTop(shapeVisual);
            root.Children.InsertAtTop(masked);
            root.Children.InsertAtTop(strokeVisual);
            ElementCompositionPreview.SetElementChildVisual(host, root);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 2, 2));
            AssertRed(ReadPixel(pixels, window.Width, 18, 18));
            RgbaPixel maskedRed = ReadPixel(
                pixels,
                window.Width,
                54,
                18);
            Assert.InRange(maskedRed.R, 115, 140);
            AssertRed(ReadPixel(pixels, window.Width, 90, 4));
            AssertDark(ReadPixel(pixels, window.Width, 90, 18));
            Assert.True(
                window.Compositor.Metrics.OpacityMaskPeakDemand > 0);
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void PropertySetPreservesTypedValuesAndStatus()
    {
        using var compositor = new Compositor();
        CompositionPropertySet properties = compositor.CreatePropertySet();
        var color = Color.FromArgb(255, 12, 34, 56);
        var matrix3x2 = Matrix3x2.CreateTranslation(3f, 4f);
        var matrix4x4 = Matrix4x4.CreateRotationZ(0.25f);
        var quaternion = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);

        properties.InsertBoolean("Boolean", true);
        properties.InsertColor("Color", color);
        properties.InsertMatrix3x2("Matrix3x2", matrix3x2);
        properties.InsertMatrix4x4("Matrix4x4", matrix4x4);
        properties.InsertQuaternion("Quaternion", quaternion);
        properties.InsertScalar("Scalar", 42f);
        properties.InsertVector2("Vector2", new Vector2(1f, 2f));
        properties.InsertVector3("Vector3", new Vector3(3f, 4f, 5f));
        properties.InsertVector4("Vector4", new Vector4(6f, 7f, 8f, 9f));

        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetBoolean("Boolean", out bool boolean));
        Assert.True(boolean);
        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetColor("Color", out Color actualColor));
        Assert.Equal(color, actualColor);
        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetMatrix3x2("Matrix3x2", out Matrix3x2 actual3x2));
        Assert.Equal(matrix3x2, actual3x2);
        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetMatrix4x4("Matrix4x4", out Matrix4x4 actual4x4));
        Assert.Equal(matrix4x4, actual4x4);
        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetQuaternion("Quaternion", out Quaternion actualQuaternion));
        Assert.Equal(quaternion, actualQuaternion);
        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetScalar("Scalar", out float scalar));
        Assert.Equal(42f, scalar);
        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetVector2("Vector2", out Vector2 vector2));
        Assert.Equal(new Vector2(1f, 2f), vector2);
        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetVector3("Vector3", out Vector3 vector3));
        Assert.Equal(new Vector3(3f, 4f, 5f), vector3);
        Assert.Equal(
            CompositionGetValueStatus.Succeeded,
            properties.TryGetVector4("Vector4", out Vector4 vector4));
        Assert.Equal(new Vector4(6f, 7f, 8f, 9f), vector4);
        Assert.Equal(
            CompositionGetValueStatus.TypeMismatch,
            properties.TryGetScalar("Vector4", out _));
        Assert.Equal(
            CompositionGetValueStatus.NotFound,
            properties.TryGetScalar("Missing", out _));
    }

    [Fact]
    public void PropertyAndVisualUpdatesAreAllocationFreeAfterWarmup()
    {
        using var compositor = new Compositor();
        CompositionPropertySet properties = compositor.CreatePropertySet();
        SpriteVisual visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(16f, 12f);
        visual.Brush = compositor.CreateColorBrush(
            Color.FromArgb(255, 255, 0, 0));

        properties.InsertScalar("Progress", 0f);
        visual.Offset = new Vector3(1f, 0f, 0f);
        visual.Offset = Vector3.Zero;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            properties.InsertScalar("Progress", index);
            visual.Offset = new Vector3(index & 1, 0f, 0f);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void GradientFactoriesPreserveDefaultsCollectionsAndOwnership()
    {
        using var compositor = new Compositor();
        CompositionLinearGradientBrush linear =
            compositor.CreateLinearGradientBrush();
        CompositionRadialGradientBrush radial =
            compositor.CreateRadialGradientBrush();
        CompositionColorGradientStop empty =
            compositor.CreateColorGradientStop();
        CompositionColorGradientStop red =
            compositor.CreateColorGradientStop(
                1f,
                Color.FromArgb(255, 255, 0, 0));

        Assert.Equal(Vector2.Zero, linear.AnchorPoint);
        Assert.Equal(Vector2.Zero, linear.CenterPoint);
        Assert.Equal(Vector2.Zero, linear.Offset);
        Assert.Equal(Vector2.One, linear.Scale);
        Assert.Equal(Matrix3x2.Identity, linear.TransformMatrix);
        Assert.Equal(Vector2.Zero, linear.StartPoint);
        Assert.Equal(Vector2.One, linear.EndPoint);
        Assert.Equal(CompositionGradientExtendMode.Clamp, linear.ExtendMode);
        Assert.Equal(CompositionColorSpace.Auto, linear.InterpolationSpace);
        Assert.Equal(CompositionMappingMode.Relative, linear.MappingMode);
        Assert.Empty(linear.ColorStops);
        Assert.Equal(default, empty.Color);
        Assert.Equal(0f, empty.Offset);
        Assert.Equal(new Vector2(0.5f), radial.EllipseCenter);
        Assert.Equal(new Vector2(0.5f), radial.EllipseRadius);
        Assert.Equal(Vector2.Zero, radial.GradientOriginOffset);

        linear.ColorStops.Add(red);
        linear.ColorStops.Insert(0, empty);
        Assert.Equal([empty, red], linear.ColorStops.ToArray());
        Assert.Same(red, linear.ColorStops[1]);
        linear.ColorStops[1] = empty;
        Assert.Equal([empty, empty], linear.ColorStops.ToArray());
        Assert.True(linear.ColorStops.Remove(empty));
        Assert.Single(linear.ColorStops);
        linear.ColorStops.Clear();
        Assert.Empty(linear.ColorStops);

        using var foreignCompositor = new Compositor();
        CompositionColorGradientStop foreign =
            foreignCompositor.CreateColorGradientStop();
        Assert.Throws<InvalidOperationException>(
            () => linear.ColorStops.Add(foreign));
        Assert.Empty(linear.ColorStops);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => empty.Offset = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => empty.Offset = 1.1f);
        Assert.Throws<NotSupportedException>(
            () => linear.InterpolationSpace = CompositionColorSpace.Hsl);
    }

    [Fact]
    public void LinearAndRadialGradientsRenderThroughRetainedWebGpuScene()
    {
        using var window = new HeadlessWindow(72, 52);
        var host = new FrameworkElement
        {
            Width = 72f,
            Height = 52f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            SpriteVisual linearVisual = compositor.CreateSpriteVisual();
            linearVisual.Size = new Vector2(72f, 24f);
            CompositionLinearGradientBrush linear =
                compositor.CreateLinearGradientBrush();
            linear.StartPoint = Vector2.Zero;
            linear.EndPoint = Vector2.UnitX;
            CompositionColorGradientStop linearEnd =
                compositor.CreateColorGradientStop(
                    1f,
                    Color.FromArgb(255, 0, 0, 255));
            linear.ColorStops.Add(linearEnd);
            linear.ColorStops.Add(compositor.CreateColorGradientStop(
                0f,
                Color.FromArgb(255, 255, 0, 0)));
            linearVisual.Brush = linear;

            SpriteVisual radialVisual = compositor.CreateSpriteVisual();
            radialVisual.Offset = new Vector3(0f, 28f, 0f);
            radialVisual.Size = new Vector2(40f, 24f);
            CompositionRadialGradientBrush radial =
                compositor.CreateRadialGradientBrush();
            radial.ColorStops.Add(compositor.CreateColorGradientStop(
                0f,
                Color.FromArgb(255, 255, 0, 0)));
            radial.ColorStops.Add(compositor.CreateColorGradientStop(
                1f,
                Color.FromArgb(255, 0, 0, 255)));
            radialVisual.Brush = radial;

            ContainerVisual root = compositor.CreateContainerVisual();
            root.RelativeSizeAdjustment = Vector2.One;
            root.Children.InsertAtTop(linearVisual);
            root.Children.InsertAtTop(radialVisual);
            ElementCompositionPreview.SetElementChildVisual(host, root);
            window.Render();

            byte[] pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 2, 12));
            RgbaPixel linearMiddle =
                ReadPixel(pixels, window.Width, 36, 12);
            Assert.True(
                linearMiddle.R > 90 && linearMiddle.B > 90,
                $"Expected a mixed linear-gradient color, got {linearMiddle}.");
            AssertBlue(ReadPixel(pixels, window.Width, 70, 12));
            AssertRed(ReadPixel(pixels, window.Width, 20, 40));
            AssertBlue(ReadPixel(pixels, window.Width, 38, 40));

            linearEnd.Color = Color.FromArgb(255, 0, 255, 0);
            window.Render();
            AssertGreen(ReadPixel(
                window.ReadPixels(),
                window.Width,
                70,
                12));

            linearEnd.Color = Color.FromArgb(255, 0, 0, 255);
            linear.MappingMode = CompositionMappingMode.Absolute;
            linear.StartPoint = Vector2.Zero;
            linear.EndPoint = new Vector2(20f, 0f);
            linear.ExtendMode = CompositionGradientExtendMode.Wrap;
            linear.TransformMatrix =
                Matrix3x2.CreateTranslation(10f, 0f);
            window.Render();
            pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 10, 12));
            RgbaPixel wrapped = ReadPixel(pixels, window.Width, 1, 12);
            Assert.True(
                wrapped.R > 80 && wrapped.B > 80,
                $"Expected a wrapped gradient color, got {wrapped}.");

            linear.MappingMode = CompositionMappingMode.Relative;
            linear.StartPoint = Vector2.Zero;
            linear.EndPoint = Vector2.UnitX;
            linear.ExtendMode = CompositionGradientExtendMode.Clamp;
            linear.TransformMatrix = Matrix3x2.Identity;
            linear.InterpolationSpace = CompositionColorSpace.RgbLinear;
            window.Render();
            RgbaPixel linearLight =
                ReadPixel(window.ReadPixels(), window.Width, 36, 12);
            Assert.True(
                linearLight.R > 160 && linearLight.B > 160,
                $"Expected linear-RGB interpolation, got {linearLight}.");

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void CompositionShapeUsesTheSameRetainedGradientBrushPath()
    {
        using var window = new HeadlessWindow(64, 28);
        var host = new FrameworkElement
        {
            Width = 64f,
            Height = 28f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(64f, 28f);
            CompositionRectangleGeometry geometry =
                compositor.CreateRectangleGeometry();
            geometry.Size = visual.Size;
            CompositionLinearGradientBrush brush =
                compositor.CreateLinearGradientBrush();
            brush.StartPoint = Vector2.Zero;
            brush.EndPoint = Vector2.UnitX;
            brush.ColorStops.Add(compositor.CreateColorGradientStop(
                0f,
                Color.FromArgb(255, 255, 0, 0)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(
                1f,
                Color.FromArgb(255, 0, 0, 255)));
            CompositionSpriteShape shape =
                compositor.CreateSpriteShape(geometry);
            shape.FillBrush = brush;
            visual.Shapes.Add(shape);
            ElementCompositionPreview.SetElementChildVisual(host, visual);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 2, 14));
            AssertBlue(ReadPixel(pixels, window.Width, 62, 14));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void WarmedGradientPropertyAndStopUpdatesAllocateNoManagedMemory()
    {
        using var compositor = new Compositor();
        SpriteVisual visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(40f, 24f);
        CompositionLinearGradientBrush brush =
            compositor.CreateLinearGradientBrush();
        CompositionColorGradientStop start =
            compositor.CreateColorGradientStop(
                0f,
                Color.FromArgb(255, 255, 0, 0));
        brush.ColorStops.Add(start);
        brush.ColorStops.Add(compositor.CreateColorGradientStop(
            1f,
            Color.FromArgb(255, 0, 0, 255)));
        visual.Brush = brush;

        brush.StartPoint = Vector2.UnitY;
        brush.StartPoint = Vector2.Zero;
        start.Color = Color.FromArgb(255, 0, 255, 0);
        start.Color = Color.FromArgb(255, 255, 0, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            brush.StartPoint = new Vector2(0f, index & 1);
            start.Color = (index & 1) == 0
                ? Color.FromArgb(255, 255, 0, 0)
                : Color.FromArgb(255, 0, 255, 0);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void MaskBrushPreservesDefaultsOwnershipAndSupportedInputs()
    {
        using var compositor = new Compositor();
        CompositionMaskBrush brush = compositor.CreateMaskBrush();
        CompositionColorBrush source = compositor.CreateColorBrush();
        CompositionLinearGradientBrush mask =
            compositor.CreateLinearGradientBrush();

        Assert.Null(brush.Source);
        Assert.Null(brush.Mask);

        brush.Source = source;
        brush.Mask = mask;
        Assert.Same(source, brush.Source);
        Assert.Same(mask, brush.Mask);

        using var foreignCompositor = new Compositor();
        Assert.Throws<InvalidOperationException>(
            () => brush.Source = foreignCompositor.CreateColorBrush());
        Assert.Throws<InvalidOperationException>(
            () => brush.Mask = foreignCompositor.CreateColorBrush());

        CompositionMaskBrush nested = compositor.CreateMaskBrush();
        Assert.Throws<ArgumentException>(() => brush.Source = nested);
        Assert.Throws<ArgumentException>(() => brush.Mask = nested);
        Assert.Same(source, brush.Source);
        Assert.Same(mask, brush.Mask);

        brush.Mask = source;
        brush.Source = null;
        Assert.Null(brush.Source);
        Assert.Same(source, brush.Mask);
        SpriteVisual visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(8f, 8f);
        visual.Brush = brush;
        long version = visual.SceneNode.ChangeVersion;
        source.Color = Color.FromArgb(255, 1, 2, 3);
        Assert.True(visual.SceneNode.ChangeVersion > version);
    }

    [Fact]
    public void MaskBrushRendersAndInvalidatesThroughRetainedWebGpuScene()
    {
        using var window = new HeadlessWindow(64, 28);
        var host = new FrameworkElement
        {
            Width = 64f,
            Height = 28f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            SpriteVisual visual = compositor.CreateSpriteVisual();
            visual.Size = new Vector2(64f, 28f);
            CompositionColorBrush source = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 0, 0));
            CompositionLinearGradientBrush mask =
                compositor.CreateLinearGradientBrush();
            CompositionColorGradientStop transparent =
                compositor.CreateColorGradientStop(
                    0f,
                    Color.FromArgb(0, 255, 255, 255));
            CompositionColorGradientStop opaque =
                compositor.CreateColorGradientStop(
                    1f,
                    Color.FromArgb(255, 255, 255, 255));
            mask.StartPoint = Vector2.Zero;
            mask.EndPoint = Vector2.UnitX;
            mask.ColorStops.Add(transparent);
            mask.ColorStops.Add(opaque);
            CompositionMaskBrush brush = compositor.CreateMaskBrush();
            brush.Source = source;
            brush.Mask = mask;
            visual.Brush = brush;
            ElementCompositionPreview.SetElementChildVisual(host, visual);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 1, 14));
            AssertRed(ReadPixel(pixels, window.Width, 62, 14));
            RgbaPixel middle = ReadPixel(pixels, window.Width, 32, 14);
            Assert.True(
                middle.R > 70 && middle.R < 210,
                $"Expected a partially masked red pixel, got {middle}.");

            source.Color = Color.FromArgb(255, 0, 255, 0);
            opaque.Color = Color.FromArgb(0, 255, 255, 255);
            window.Render();
            AssertDark(ReadPixel(
                window.ReadPixels(),
                window.Width,
                62,
                14));

            opaque.Color = Color.FromArgb(255, 255, 255, 255);
            window.Render();
            AssertGreen(ReadPixel(
                window.ReadPixels(),
                window.Width,
                62,
                14));
            Assert.Equal(
                3,
                ((CompositionSceneNode)visual.SceneNode).RenderCommandCount);
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void ShapeFillAndStrokeUseIndependentMaskBrushScopes()
    {
        using var window = new HeadlessWindow(56, 40);
        var host = new FrameworkElement
        {
            Width = 56f,
            Height = 40f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(56f, 40f);
            CompositionRectangleGeometry geometry =
                compositor.CreateRectangleGeometry();
            geometry.Offset = new Vector2(8f, 8f);
            geometry.Size = new Vector2(40f, 24f);

            CompositionColorBrush fillMask = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 255, 255));
            CompositionMaskBrush fill = compositor.CreateMaskBrush();
            fill.Source = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 0, 0));
            fill.Mask = fillMask;

            CompositionColorBrush strokeMask = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 255, 255));
            CompositionMaskBrush stroke = compositor.CreateMaskBrush();
            stroke.Source = compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 0, 255));
            stroke.Mask = strokeMask;

            CompositionSpriteShape shape =
                compositor.CreateSpriteShape(geometry);
            shape.FillBrush = fill;
            shape.StrokeBrush = stroke;
            shape.StrokeThickness = 4f;
            visual.Shapes.Add(shape);
            ElementCompositionPreview.SetElementChildVisual(host, visual);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 28, 20));
            AssertBlue(ReadPixel(pixels, window.Width, 8, 20));

            fillMask.Color = Color.FromArgb(0, 255, 255, 255);
            window.Render();
            pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 28, 20));
            AssertBlue(ReadPixel(pixels, window.Width, 8, 20));

            strokeMask.Color = Color.FromArgb(0, 255, 255, 255);
            window.Render();
            AssertDark(ReadPixel(
                window.ReadPixels(),
                window.Width,
                8,
                20));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void WarmedMaskBrushInputUpdatesAllocateNoManagedMemory()
    {
        using var compositor = new Compositor();
        SpriteVisual visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(40f, 24f);
        CompositionColorBrush source = compositor.CreateColorBrush(
            Color.FromArgb(255, 255, 0, 0));
        CompositionColorBrush mask = compositor.CreateColorBrush(
            Color.FromArgb(255, 255, 255, 255));
        CompositionMaskBrush brush = compositor.CreateMaskBrush();
        brush.Source = source;
        brush.Mask = mask;
        visual.Brush = brush;

        source.Color = Color.FromArgb(255, 0, 255, 0);
        source.Color = Color.FromArgb(255, 255, 0, 0);
        mask.Color = Color.FromArgb(0, 255, 255, 255);
        mask.Color = Color.FromArgb(255, 255, 255, 255);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            source.Color = (index & 1) == 0
                ? Color.FromArgb(255, 255, 0, 0)
                : Color.FromArgb(255, 0, 255, 0);
            mask.Color = (index & 1) == 0
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(0, 255, 255, 255);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DropShadowPreservesWinUiDefaultsOwnershipAndValidation()
    {
        using var compositor = new Compositor();
        DropShadow shadow = compositor.CreateDropShadow();
        SpriteVisual sprite = compositor.CreateSpriteVisual();
        LayerVisual layer = compositor.CreateLayerVisual();

        Assert.Equal(9f, shadow.BlurRadius);
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), shadow.Color);
        Assert.Null(shadow.Mask);
        Assert.Equal(Vector3.Zero, shadow.Offset);
        Assert.Equal(1f, shadow.Opacity);
        Assert.Equal(
            CompositionDropShadowSourcePolicy.Default,
            shadow.SourcePolicy);

        sprite.Shadow = shadow;
        layer.Shadow = shadow;
        Assert.Same(shadow, sprite.Shadow);
        Assert.Same(shadow, layer.Shadow);

        using var foreignCompositor = new Compositor();
        Assert.Throws<InvalidOperationException>(
            () => shadow.Mask = foreignCompositor.CreateColorBrush());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => shadow.BlurRadius = -1f);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => shadow.BlurRadius = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => shadow.Opacity = 1.1f);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => shadow.Offset = new Vector3(float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => shadow.SourcePolicy =
                (CompositionDropShadowSourcePolicy)42);

        shadow.Dispose();
        Assert.Null(sprite.Shadow);
        Assert.Null(layer.Shadow);
    }

    [Fact]
    public void SpriteDropShadowSwitchesBetweenRectangleContentAndExplicitMask()
    {
        using var window = new HeadlessWindow(64, 36);
        var host = new FrameworkElement
        {
            Width = 64f,
            Height = 36f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            SpriteVisual sprite = compositor.CreateSpriteVisual();
            sprite.Offset = new Vector3(8f, 8f, 0f);
            sprite.Size = new Vector2(16f, 16f);
            sprite.Brush = compositor.CreateColorBrush(
                Color.FromArgb(0, 255, 0, 0));
            DropShadow shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = 0f;
            shadow.Color = Color.FromArgb(255, 0, 255, 0);
            shadow.Offset = new Vector3(24f, 0f, 0f);
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);

            window.Render();
            AssertGreen(ReadPixel(
                window.ReadPixels(),
                window.Width,
                40,
                16));

            shadow.SourcePolicy =
                CompositionDropShadowSourcePolicy.InheritFromVisualContent;
            window.Render();
            AssertDark(ReadPixel(
                window.ReadPixels(),
                window.Width,
                40,
                16));

            CompositionColorBrush mask = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 255, 255));
            shadow.Mask = mask;
            window.Render();
            AssertGreen(ReadPixel(
                window.ReadPixels(),
                window.Width,
                40,
                16));

            mask.Color = Color.FromArgb(0, 255, 255, 255);
            window.Render();
            AssertDark(ReadPixel(
                window.ReadPixels(),
                window.Width,
                40,
                16));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void LayerDropShadowInheritsChildAlphaAndUsesGpuBlur()
    {
        using var window = new HeadlessWindow(72, 40);
        var host = new FrameworkElement
        {
            Width = 72f,
            Height = 40f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            LayerVisual layer = compositor.CreateLayerVisual();
            layer.Offset = new Vector3(8f, 8f, 0f);
            layer.Size = new Vector2(20f, 20f);
            SpriteVisual child = compositor.CreateSpriteVisual();
            child.Offset = new Vector3(6f, 6f, 0f);
            child.Size = new Vector2(8f, 8f);
            child.Brush = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 0, 0));
            layer.Children.InsertAtTop(child);
            DropShadow shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = 3f;
            shadow.Color = Color.FromArgb(255, 0, 0, 255);
            shadow.Offset = new Vector3(28f, 0f, 0f);
            layer.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, layer);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 18, 18));
            RgbaPixel center = ReadPixel(pixels, window.Width, 46, 18);
            Assert.True(
                center.B > 80 && center.B > center.R && center.B > center.G,
                $"Expected a blue inherited shadow, got {center}.");
            RgbaPixel softEdge = ReadPixel(pixels, window.Width, 41, 18);
            Assert.True(
                softEdge.B > 0 && softEdge.B < center.B,
                $"Expected a soft GPU-blurred edge, got {softEdge} versus {center}.");

            child.Offset = new Vector3(2f, 6f, 0f);
            window.Render();
            pixels = window.ReadPixels();
            Assert.True(ReadPixel(pixels, window.Width, 42, 18).B > 80);
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void WarmedDropShadowPropertyUpdatesAllocateNoManagedMemory()
    {
        using var compositor = new Compositor();
        SpriteVisual visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(40f, 24f);
        DropShadow shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 0f;
        visual.Shadow = shadow;

        shadow.Offset = Vector3.UnitX;
        shadow.Offset = Vector3.Zero;
        shadow.Color = Color.FromArgb(255, 0, 255, 0);
        shadow.Color = Color.FromArgb(255, 0, 0, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            shadow.Offset = new Vector3(index & 1, 0f, 0f);
            shadow.Color = (index & 1) == 0
                ? Color.FromArgb(255, 0, 0, 0)
                : Color.FromArgb(255, 0, 255, 0);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void VisualCollectionKeepsBottomToTopOrderAndOwnership()
    {
        using var compositor = new Compositor();
        ContainerVisual root = compositor.CreateContainerVisual();
        ContainerVisual other = compositor.CreateContainerVisual();
        SpriteVisual first = compositor.CreateSpriteVisual();
        SpriteVisual second = compositor.CreateSpriteVisual();
        SpriteVisual third = compositor.CreateSpriteVisual();

        root.Children.InsertAtTop(first);
        root.Children.InsertAtTop(third);
        root.Children.InsertBelow(second, third);

        Assert.Equal([first, second, third], root.Children.ToArray());
        Assert.Same(root, second.Parent);

        other.Children.InsertAtBottom(second);

        Assert.Equal([first, third], root.Children.ToArray());
        Assert.Equal([second], other.Children.ToArray());
        Assert.Same(other, second.Parent);
        Assert.Throws<InvalidOperationException>(
            () => second.Children.InsertAtTop(other));

        using var foreignCompositor = new Compositor();
        SpriteVisual foreign = foreignCompositor.CreateSpriteVisual();
        Assert.Throws<InvalidOperationException>(
            () => root.Children.InsertAtTop(foreign));
    }

    [Fact]
    public void DisposingContainerDetachesItsPublicAndSceneChildren()
    {
        using var compositor = new Compositor();
        ContainerVisual root = compositor.CreateContainerVisual();
        SpriteVisual first = compositor.CreateSpriteVisual();
        SpriteVisual second = compositor.CreateSpriteVisual();
        root.Children.InsertAtTop(first);
        root.Children.InsertAtTop(second);

        root.Dispose();

        Assert.Empty(root.Children);
        Assert.Null(first.Parent);
        Assert.Null(second.Parent);
        Assert.Null(first.SceneNode.Parent);
        Assert.Null(second.SceneNode.Parent);
        Assert.Throws<ObjectDisposedException>(
            () => root.Children.InsertAtTop(first));
    }

    [Fact]
    public void DisposingShapeOwnersDetachesTheirShapeCollections()
    {
        using var compositor = new Compositor();
        ShapeVisual visual = compositor.CreateShapeVisual();
        CompositionContainerShape container =
            compositor.CreateContainerShape();
        CompositionSpriteShape visualShape =
            compositor.CreateSpriteShape();
        CompositionSpriteShape nestedShape =
            compositor.CreateSpriteShape();
        visual.Shapes.Add(visualShape);
        container.Shapes.Add(nestedShape);

        visual.Dispose();
        container.Dispose();

        Assert.Empty(visual.Shapes);
        Assert.Empty(container.Shapes);
        Assert.Throws<ObjectDisposedException>(
            () => visual.Shapes.Add(visualShape));
        Assert.Throws<ObjectDisposedException>(
            () => container.Shapes.Add(nestedShape));

        ShapeVisual replacement = compositor.CreateShapeVisual();
        replacement.Shapes.Add(visualShape);
        replacement.Shapes.Add(nestedShape);
        Assert.Equal([visualShape, nestedShape], replacement.Shapes);
    }

    [Fact]
    public void RejectedElementChildReplacementIsTransactional()
    {
        var host = new FrameworkElement();
        Visual elementVisual =
            ElementCompositionPreview.GetElementVisual(host);
        SpriteVisual original =
            elementVisual.Compositor.CreateSpriteVisual();
        ElementCompositionPreview.SetElementChildVisual(host, original);

        try
        {
            using var foreignCompositor = new Compositor();
            SpriteVisual foreign = foreignCompositor.CreateSpriteVisual();

            Assert.Throws<InvalidOperationException>(
                () => ElementCompositionPreview.SetElementChildVisual(
                    host,
                    foreign));
            Assert.Same(
                original,
                ElementCompositionPreview.GetElementChildVisual(host));
            Assert.Same(original.SceneNode, host.Children[^1]);
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
        }
    }

    [Fact]
    public void ElementChildSpriteRendersThroughRetainedWebGpuScene()
    {
        using var window = new HeadlessWindow(64, 48);
        var host = new FrameworkElement
        {
            Width = 64f,
            Height = 48f
        };
        window.Content = host;

        try
        {
            window.Render();
            Visual elementVisual =
                ElementCompositionPreview.GetElementVisual(host);
            SpriteVisual sprite =
                elementVisual.Compositor.CreateSpriteVisual();
            sprite.Offset = new Vector3(8f, 6f, 0f);
            sprite.Size = new Vector2(24f, 18f);
            CompositionColorBrush brush =
                elementVisual.Compositor.CreateColorBrush(
                    Color.FromArgb(255, 255, 0, 0));
            sprite.Brush = brush;

            ElementCompositionPreview.SetElementChildVisual(host, sprite);
            var laterOrdinaryChild = new ProGPU.Scene.DrawingVisual();
            laterOrdinaryChild.Context.DrawRectangle(
                new ProGPU.Vector.SolidColorBrush(0x0000FFFF),
                null,
                new ProGPU.Scene.Rect(8f, 6f, 24f, 18f));
            host.AddChild(laterOrdinaryChild);
            window.Render();

            Assert.Same(
                sprite,
                ElementCompositionPreview.GetElementChildVisual(host));
            AssertRed(ReadPixel(window.ReadPixels(), window.Width, 16, 12));
            Assert.Same(sprite.SceneNode, host.Children[^1]);
            host.RemoveChild(laterOrdinaryChild);

            brush.Color = Color.FromArgb(255, 0, 255, 0);
            window.Render();
            AssertGreen(ReadPixel(window.ReadPixels(), window.Width, 16, 12));

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);

            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Render();
            Assert.Null(ElementCompositionPreview.GetElementChildVisual(host));
            AssertDark(ReadPixel(window.ReadPixels(), window.Width, 16, 12));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void RelativeSizeTracksElementResizeWithoutPolling()
    {
        using var window = new HeadlessWindow(40, 30);
        var host = new FrameworkElement
        {
            Width = 40f,
            Height = 30f
        };
        window.Content = host;

        try
        {
            window.Render();
            Visual elementVisual =
                ElementCompositionPreview.GetElementVisual(host);
            SpriteVisual sprite =
                elementVisual.Compositor.CreateSpriteVisual();
            sprite.RelativeSizeAdjustment = Vector2.One;
            sprite.Brush = elementVisual.Compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 0, 255));
            ElementCompositionPreview.SetElementChildVisual(host, sprite);

            window.Render();
            AssertBlue(ReadPixel(window.ReadPixels(), window.Width, 35, 25));

            host.Width = 20f;
            host.Height = 15f;
            window.Render();

            byte[] pixels = window.ReadPixels();
            AssertBlue(ReadPixel(pixels, window.Width, 10, 10));
            AssertDark(ReadPixel(pixels, window.Width, 35, 25));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void ClipFactoriesPreserveDefaultsOwnershipAndValidation()
    {
        using var compositor = new Compositor();
        SpriteVisual visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(80f, 60f);
        InsetClip inset = compositor.CreateInsetClip(1f, 2f, 3f, 4f);
        RectangleClip rectangle = compositor.CreateRectangleClip(
            5f,
            6f,
            7f,
            8f,
            new Vector2(1f, 2f),
            new Vector2(3f, 4f),
            new Vector2(5f, 6f),
            new Vector2(7f, 8f));
        CompositionGeometricClip geometric =
            compositor.CreateGeometricClip();

        Assert.Equal(Vector2.Zero, inset.AnchorPoint);
        Assert.Equal(Vector2.Zero, inset.CenterPoint);
        Assert.Equal(Vector2.Zero, inset.Offset);
        Assert.Equal(Vector2.One, inset.Scale);
        Assert.Equal(Matrix3x2.Identity, inset.TransformMatrix);
        Assert.Equal(1f, inset.LeftInset);
        Assert.Equal(2f, inset.TopInset);
        Assert.Equal(3f, inset.RightInset);
        Assert.Equal(4f, inset.BottomInset);
        Assert.Equal(new Vector2(1f, 2f), rectangle.TopLeftRadius);
        Assert.Equal(new Vector2(3f, 4f), rectangle.TopRightRadius);
        Assert.Equal(new Vector2(5f, 6f), rectangle.BottomRightRadius);
        Assert.Equal(new Vector2(7f, 8f), rectangle.BottomLeftRadius);
        Assert.Null(geometric.Geometry);
        Assert.Null(geometric.ViewBox);

        visual.Clip = geometric;
        Assert.Null(visual.SceneNode.LocalCompositeClip);
        visual.Clip = inset;
        Assert.NotNull(visual.SceneNode.LocalCompositeClip);
        inset.Dispose();
        Assert.Null(visual.Clip);
        Assert.Null(visual.SceneNode.LocalCompositeClip);

        using var foreignCompositor = new Compositor();
        Assert.Throws<InvalidOperationException>(
            () => visual.Clip = foreignCompositor.CreateInsetClip());
        Assert.Throws<InvalidOperationException>(
            () => geometric.Geometry =
                foreignCompositor.CreateEllipseGeometry());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => rectangle.TopLeftRadius = new Vector2(-1f, 2f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => rectangle.RotationAngle = float.NaN);
    }

    [Fact]
    public void SharedClipsAndGeometriesInvalidateEveryLiveVisualOwner()
    {
        using var compositor = new Compositor();
        SpriteVisual first = compositor.CreateSpriteVisual();
        SpriteVisual second = compositor.CreateSpriteVisual();
        first.Size = second.Size = new Vector2(40f, 30f);
        InsetClip inset = compositor.CreateInsetClip(1f, 2f, 3f, 4f);
        first.Clip = inset;
        second.Clip = inset;
        long firstVersion = first.SceneNode.ChangeVersion;
        long secondVersion = second.SceneNode.ChangeVersion;

        inset.LeftInset = 5f;

        Assert.True(first.SceneNode.ChangeVersion > firstVersion);
        Assert.True(second.SceneNode.ChangeVersion > secondVersion);

        CompositionEllipseGeometry ellipse =
            compositor.CreateEllipseGeometry();
        ellipse.Center = new Vector2(10f);
        ellipse.Radius = new Vector2(8f);
        first.Clip = compositor.CreateGeometricClip(ellipse);
        second.Clip = compositor.CreateGeometricClip(ellipse);
        firstVersion = first.SceneNode.ChangeVersion;
        secondVersion = second.SceneNode.ChangeVersion;

        ellipse.Radius = new Vector2(6f);

        Assert.True(first.SceneNode.ChangeVersion > firstVersion);
        Assert.True(second.SceneNode.ChangeVersion > secondVersion);
    }

    [Fact]
    public void InsetAndRoundedRectangleClipsRenderOnRetainedWebGpuScene()
    {
        using var window = new HeadlessWindow(72, 52);
        var host = new FrameworkElement
        {
            Width = 72f,
            Height = 52f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            SpriteVisual sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(72f, 52f);
            sprite.Brush = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 0, 0));
            InsetClip inset = compositor.CreateInsetClip(8f, 6f, 10f, 8f);
            sprite.Clip = inset;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 4, 20));
            AssertRed(ReadPixel(pixels, window.Width, 20, 20));
            AssertDark(ReadPixel(pixels, window.Width, 66, 20));

            inset.Offset = new Vector2(4f, 0f);
            window.Render();
            pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 9, 20));
            AssertRed(ReadPixel(pixels, window.Width, 14, 20));

            inset.RotationAngle = 0.1f;
            window.Render();
            Assert.True(
                window.Compositor.Metrics.AffineRectangleMaskPeakDemand > 0);

            RectangleClip rounded = compositor.CreateRectangleClip(
                8f,
                6f,
                10f,
                8f,
                new Vector2(10f),
                new Vector2(10f),
                new Vector2(10f),
                new Vector2(10f));
            sprite.Clip = rounded;
            window.Render();
            pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 9, 7));
            AssertRed(ReadPixel(pixels, window.Width, 18, 16));
            Assert.True(
                window.Compositor.Metrics.RoundedGeometryMaskPeakDemand > 0);

            rounded.TopLeftRadius = Vector2.Zero;
            window.Render();
            pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 9, 7));

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void GeometricClipAndViewBoxRenderAndTrackGeometryChanges()
    {
        using var window = new HeadlessWindow(64, 48);
        var host = new FrameworkElement
        {
            Width = 64f,
            Height = 48f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            SpriteVisual sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(64f, 48f);
            sprite.Brush = compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 0, 255));
            CompositionEllipseGeometry ellipse =
                compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2(8f, 8f);
            ellipse.Radius = new Vector2(6f, 5f);
            CompositionViewBox viewBox = compositor.CreateViewBox();
            viewBox.Size = new Vector2(16f, 16f);
            viewBox.Stretch = CompositionStretch.Fill;
            CompositionGeometricClip clip =
                compositor.CreateGeometricClip(ellipse);
            clip.ViewBox = viewBox;
            sprite.Clip = clip;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 2, 2));
            AssertBlue(ReadPixel(pixels, window.Width, 32, 24));
            AssertDark(ReadPixel(pixels, window.Width, 58, 42));

            ellipse.Radius = new Vector2(8f, 8f);
            window.Render();
            pixels = window.ReadPixels();
            AssertBlue(ReadPixel(pixels, window.Width, 4, 24));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void WarmedClipScalarAndTransformUpdatesAllocateNoManagedMemory()
    {
        using var compositor = new Compositor();
        SpriteVisual visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2(40f, 30f);
        InsetClip clip = compositor.CreateInsetClip(1f, 2f, 3f, 4f);
        visual.Clip = clip;

        clip.LeftInset = 2f;
        clip.LeftInset = 1f;
        clip.Offset = Vector2.One;
        clip.Offset = Vector2.Zero;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            clip.LeftInset = (index & 1) + 1f;
            clip.Offset = new Vector2(index & 1, 0f);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ShapeCollectionsPreserveOwnershipAndRejectCyclesTransactionally()
    {
        using var compositor = new Compositor();
        ShapeVisual visual = compositor.CreateShapeVisual();
        CompositionContainerShape group = compositor.CreateContainerShape();
        CompositionSpriteShape first = compositor.CreateSpriteShape();
        CompositionSpriteShape second = compositor.CreateSpriteShape();
        CompositionViewBox viewBox = compositor.CreateViewBox();

        Assert.Equal(CompositionStretch.Uniform, viewBox.Stretch);
        Assert.Equal(0.5f, viewBox.HorizontalAlignmentRatio);
        Assert.Equal(0.5f, viewBox.VerticalAlignmentRatio);
        Assert.Equal(Vector2.One, first.Scale);
        Assert.Equal(1f, compositor.CreateLineGeometry().TrimEnd);
        Assert.Equal(10f, first.StrokeMiterLimit);

        visual.Shapes.Add(first);
        visual.Shapes.Insert(0, second);
        group.Shapes.Add(first);

        Assert.Equal([second], visual.Shapes.ToArray());
        Assert.Equal([first], group.Shapes.ToArray());

        visual.Shapes.Add(group);
        Assert.Throws<InvalidOperationException>(
            () => group.Shapes.Add(group));
        Assert.Equal([first], group.Shapes.ToArray());

        using var foreignCompositor = new Compositor();
        CompositionSpriteShape foreign =
            foreignCompositor.CreateSpriteShape();
        Assert.Throws<InvalidOperationException>(
            () => visual.Shapes[0] = foreign);
        Assert.Same(second, visual.Shapes[0]);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => visual.Shapes.Insert(3, second));
        Assert.Equal([second, group], visual.Shapes.ToArray());
    }

    [Fact]
    public void RetainedCompositionShapesRenderAndInvalidateThroughWebGpu()
    {
        using var window = new HeadlessWindow(80, 56);
        var host = new FrameworkElement
        {
            Width = 80f,
            Height = 56f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(80f, 56f);
            CompositionEllipseGeometry ellipse =
                compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2(20f, 20f);
            ellipse.Radius = new Vector2(10f, 8f);
            CompositionColorBrush fill = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 0, 0));
            CompositionSpriteShape ellipseShape =
                compositor.CreateSpriteShape(ellipse);
            ellipseShape.FillBrush = fill;

            CompositionRectangleGeometry rectangle =
                compositor.CreateRectangleGeometry();
            rectangle.Offset = new Vector2(42f, 10f);
            rectangle.Size = new Vector2(20f, 18f);
            CompositionSpriteShape rectangleShape =
                compositor.CreateSpriteShape(rectangle);
            rectangleShape.FillBrush = compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 0, 255));

            visual.Shapes.Add(ellipseShape);
            visual.Shapes.Add(rectangleShape);
            ElementCompositionPreview.SetElementChildVisual(host, visual);
            window.Render();

            byte[] pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 20, 20));
            AssertBlue(ReadPixel(pixels, window.Width, 50, 18));

            ellipseShape.Offset = new Vector2(8f, 0f);
            fill.Color = Color.FromArgb(255, 0, 255, 0);
            window.Render();
            pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 17, 20));
            AssertGreen(ReadPixel(pixels, window.Width, 28, 20));

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void WarmedCompositionShapeUpdatesAllocateNoManagedMemory()
    {
        using var compositor = new Compositor();
        ShapeVisual visual = compositor.CreateShapeVisual();
        CompositionEllipseGeometry ellipse =
            compositor.CreateEllipseGeometry();
        ellipse.Center = new Vector2(8f, 8f);
        ellipse.Radius = new Vector2(6f, 4f);
        CompositionColorBrush brush = compositor.CreateColorBrush(
            Color.FromArgb(255, 255, 0, 0));
        CompositionSpriteShape shape =
            compositor.CreateSpriteShape(ellipse);
        shape.FillBrush = brush;
        visual.Shapes.Add(shape);

        shape.Offset = Vector2.One;
        shape.Offset = Vector2.Zero;
        brush.Color = Color.FromArgb(255, 0, 255, 0);
        brush.Color = Color.FromArgb(255, 255, 0, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            shape.Offset = new Vector2(index & 1, 0f);
            brush.Color = (index & 1) == 0
                ? Color.FromArgb(255, 255, 0, 0)
                : Color.FromArgb(255, 0, 255, 0);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ViewBoxAndTrimmedLineMapThroughRetainedShapeCommands()
    {
        using var window = new HeadlessWindow(80, 40);
        var host = new FrameworkElement
        {
            Width = 80f,
            Height = 40f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(80f, 40f);
            CompositionViewBox viewBox = compositor.CreateViewBox();
            viewBox.Size = new Vector2(20f, 20f);
            viewBox.Stretch = CompositionStretch.Uniform;
            visual.ViewBox = viewBox;

            CompositionLineGeometry line = compositor.CreateLineGeometry();
            line.Start = new Vector2(0f, 10f);
            line.End = new Vector2(20f, 10f);
            line.TrimStart = 0.25f;
            line.TrimEnd = 0.75f;
            CompositionSpriteShape shape =
                compositor.CreateSpriteShape(line);
            shape.StrokeBrush = compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 0, 255));
            shape.StrokeThickness = 4f;
            shape.IsStrokeNonScaling = true;
            visual.Shapes.Add(shape);
            ElementCompositionPreview.SetElementChildVisual(host, visual);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 25, 20));
            AssertBlue(ReadPixel(pixels, window.Width, 40, 20));
            AssertDark(ReadPixel(pixels, window.Width, 55, 20));

            viewBox.HorizontalAlignmentRatio = 0f;
            window.Render();
            pixels = window.ReadPixels();
            AssertBlue(ReadPixel(pixels, window.Width, 20, 20));
            AssertDark(ReadPixel(pixels, window.Width, 40, 20));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void NonScalingStrokeKeepsDeviceWidthUnderAnisotropicShapeScale()
    {
        using var window = new HeadlessWindow(96, 48);
        var host = new FrameworkElement
        {
            Width = 96f,
            Height = 48f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(96f, 48f);
            CompositionLineGeometry line = compositor.CreateLineGeometry();
            line.Start = new Vector2(10f, 8f);
            line.End = new Vector2(10f, 40f);
            CompositionSpriteShape shape = compositor.CreateSpriteShape(line);
            shape.Scale = new Vector2(4f, 1f);
            shape.StrokeBrush = compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 0, 255));
            shape.StrokeThickness = 4f;
            shape.IsStrokeNonScaling = true;
            visual.Shapes.Add(shape);
            ElementCompositionPreview.SetElementChildVisual(host, visual);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertBlue(ReadPixel(pixels, window.Width, 41, 24));
            AssertDark(ReadPixel(pixels, window.Width, 44, 24));

            shape.IsStrokeNonScaling = false;
            window.Render();
            pixels = window.ReadPixels();
            AssertBlue(ReadPixel(pixels, window.Width, 44, 24));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void PathAndRoundedRectangleFactoriesPreserveTypedContracts()
    {
        using var compositor = new Compositor();
        CompositionPathGeometry empty =
            compositor.CreatePathGeometry();
        CompositionRoundedRectangleGeometry rounded =
            compositor.CreateRoundedRectangleGeometry();

        Assert.Null(empty.Path);
        Assert.Equal(Vector2.Zero, rounded.CornerRadius);
        Assert.Equal(Vector2.Zero, rounded.Offset);
        Assert.Equal(Vector2.Zero, rounded.Size);

        PathGeometry source = PrimitivePathGeometry.CreateRectangle(
            1f,
            2f,
            3f,
            4f);
        var path = new CompositionPath(source);
        CompositionPathGeometry initialized =
            compositor.CreatePathGeometry(path);
        Assert.Same(path, initialized.Path);
        Assert.IsAssignableFrom<IGeometrySource2D>(path);
        Assert.IsAssignableFrom<IGeometrySource2D>(source);

        Assert.Throws<ArgumentNullException>(
            () => compositor.CreatePathGeometry(null!));
        Assert.Throws<NotSupportedException>(
            () => new CompositionPath(new UnknownGeometrySource()));
        Assert.Throws<NotSupportedException>(
            () => new CompositionPath(new PathGeometry
            {
                IsCombined = true,
                PathA = source,
                PathB = source
            }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => rounded.CornerRadius = new Vector2(-1f, 2f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => rounded.Size = new Vector2(float.NaN, 2f));
    }

    [Fact]
    public void PathAndRoundedRectangleRenderAndInvalidateThroughWebGpu()
    {
        using var window = new HeadlessWindow(96, 64);
        var host = new FrameworkElement
        {
            Width = 96f,
            Height = 64f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(96f, 64f);

            CompositionRoundedRectangleGeometry rounded =
                compositor.CreateRoundedRectangleGeometry();
            rounded.Offset = new Vector2(8f, 8f);
            rounded.Size = new Vector2(32f, 24f);
            rounded.CornerRadius = new Vector2(8f, 8f);
            CompositionSpriteShape roundedShape =
                compositor.CreateSpriteShape(rounded);
            roundedShape.FillBrush = compositor.CreateColorBrush(
                Color.FromArgb(255, 255, 0, 0));

            var source = new PathGeometry();
            var figure = new PathFigure(new Vector2(50f, 42f))
            {
                IsFilled = false
            };
            var sourceLine = new LineSegment(new Vector2(90f, 42f));
            figure.Segments.Add(sourceLine);
            source.Figures.Add(figure);
            var path = new CompositionPath(source);
            CompositionPathGeometry pathGeometry =
                compositor.CreatePathGeometry(path);
            pathGeometry.TrimStart = 0.25f;
            pathGeometry.TrimEnd = 0.75f;
            CompositionSpriteShape pathShape =
                compositor.CreateSpriteShape(pathGeometry);
            pathShape.StrokeBrush = compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 0, 255));
            pathShape.StrokeThickness = 4f;

            sourceLine.Point = new Vector2(52f, 42f);
            visual.Shapes.Add(roundedShape);
            visual.Shapes.Add(pathShape);
            ElementCompositionPreview.SetElementChildVisual(host, visual);
            window.Render();

            byte[] pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 8, 8));
            AssertRed(ReadPixel(pixels, window.Width, 24, 20));
            AssertDark(ReadPixel(pixels, window.Width, 54, 42));
            AssertBlue(ReadPixel(pixels, window.Width, 70, 42));
            AssertDark(ReadPixel(pixels, window.Width, 86, 42));

            rounded.CornerRadius = Vector2.Zero;
            pathGeometry.Path = null;
            window.Render();
            pixels = window.ReadPixels();
            AssertRed(ReadPixel(pixels, window.Width, 8, 8));
            AssertDark(ReadPixel(pixels, window.Width, 70, 42));

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void TrimmedRoundedRectangleUsesRetainedExactArcSegments()
    {
        using var window = new HeadlessWindow(56, 48);
        var host = new FrameworkElement
        {
            Width = 56f,
            Height = 48f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(56f, 48f);
            CompositionRoundedRectangleGeometry rounded =
                compositor.CreateRoundedRectangleGeometry();
            rounded.Offset = new Vector2(8f, 8f);
            rounded.Size = new Vector2(32f, 24f);
            rounded.CornerRadius = new Vector2(8f, 6f);
            rounded.TrimStart = 0f;
            rounded.TrimEnd = 0.25f;
            CompositionSpriteShape shape =
                compositor.CreateSpriteShape(rounded);
            shape.StrokeBrush = compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 255, 0));
            shape.StrokeThickness = 3f;
            visual.Shapes.Add(shape);
            ElementCompositionPreview.SetElementChildVisual(host, visual);

            window.Render();
            byte[] pixels = window.ReadPixels();
            AssertGreen(ReadPixel(pixels, window.Width, 24, 8));
            AssertGreen(ReadPixel(pixels, window.Width, 36, 9));
            AssertDark(ReadPixel(pixels, window.Width, 24, 32));

            rounded.TrimOffset = 0.5f;
            window.Render();
            pixels = window.ReadPixels();
            AssertDark(ReadPixel(pixels, window.Width, 24, 8));
            AssertGreen(ReadPixel(pixels, window.Width, 24, 32));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void TrimmedCompositionPathPreservesBezierAndArcSegments()
    {
        using var window = new HeadlessWindow(96, 40);
        var host = new FrameworkElement
        {
            Width = 96f,
            Height = 40f
        };
        window.Content = host;

        try
        {
            window.Render();
            Compositor compositor = ElementCompositionPreview
                .GetElementVisual(host)
                .Compositor;
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(96f, 40f);
            var source = new PathGeometry();

            var quadratic = new PathFigure(new Vector2(4f, 24f))
            {
                IsFilled = false
            };
            quadratic.Segments.Add(
                new QuadraticBezierSegment(
                    new Vector2(16f, 4f),
                    new Vector2(28f, 24f)));
            source.Figures.Add(quadratic);

            var cubic = new PathFigure(new Vector2(34f, 24f))
            {
                IsFilled = false
            };
            cubic.Segments.Add(
                new CubicBezierSegment(
                    new Vector2(40f, 4f),
                    new Vector2(52f, 36f),
                    new Vector2(58f, 16f)));
            source.Figures.Add(cubic);

            var arc = new PathFigure(new Vector2(64f, 24f))
            {
                IsFilled = false
            };
            arc.Segments.Add(
                new ArcSegment(
                    new Vector2(88f, 24f),
                    new Vector2(12f, 10f),
                    0f,
                    false,
                    SweepDirection.Clockwise));
            source.Figures.Add(arc);

            CompositionPathGeometry geometry =
                compositor.CreatePathGeometry(
                    new CompositionPath(source));
            geometry.TrimStart = 0.05f;
            geometry.TrimEnd = 0.95f;
            CompositionSpriteShape shape =
                compositor.CreateSpriteShape(geometry);
            shape.StrokeBrush = compositor.CreateColorBrush(
                Color.FromArgb(255, 0, 0, 255));
            shape.StrokeThickness = 2f;
            visual.Shapes.Add(shape);
            ElementCompositionPreview.SetElementChildVisual(host, visual);

            window.Render();
            byte[] first = window.ReadPixels();
            int bluePixels = 0;
            for (int index = 0; index < first.Length; index += 4)
            {
                if (first[index + 2] > 150 &&
                    first[index] < 80 &&
                    first[index + 1] < 80)
                {
                    bluePixels++;
                }
            }
            Assert.True(
                bluePixels > 50,
                $"Expected retained curved stroke pixels, got {bluePixels}.");

            geometry.TrimOffset = 0.2f;
            window.Render();
            byte[] shifted = window.ReadPixels();
            Assert.False(first.SequenceEqual(shifted));
        }
        finally
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            window.Content = null;
        }
    }

    [Fact]
    public void WarmedPathAndRoundedRectangleUpdatesAllocateNoManagedMemory()
    {
        using var compositor = new Compositor();
        ShapeVisual visual = compositor.CreateShapeVisual();
        CompositionRoundedRectangleGeometry rounded =
            compositor.CreateRoundedRectangleGeometry();
        rounded.Size = new Vector2(20f, 12f);
        CompositionSpriteShape roundedShape =
            compositor.CreateSpriteShape(rounded);
        visual.Shapes.Add(roundedShape);

        CompositionPath first = new(
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 8f, 8f));
        CompositionPath second = new(
            PrimitivePathGeometry.CreateEllipse(
                new Vector2(4f, 4f),
                4f,
                4f));
        CompositionPathGeometry path =
            compositor.CreatePathGeometry(first);
        CompositionSpriteShape pathShape =
            compositor.CreateSpriteShape(path);
        visual.Shapes.Add(pathShape);

        CompositionLineGeometry line = compositor.CreateLineGeometry();
        line.Start = Vector2.Zero;
        line.End = new Vector2(12f, 0f);
        line.TrimStart = 0.25f;
        line.TrimEnd = 0.75f;
        visual.Shapes.Add(compositor.CreateSpriteShape(line));

        CompositionEllipseGeometry ellipse =
            compositor.CreateEllipseGeometry();
        ellipse.Center = new Vector2(4f, 4f);
        ellipse.Radius = new Vector2(4f, 3f);
        ellipse.TrimStart = 0.1f;
        ellipse.TrimEnd = 0.9f;
        visual.Shapes.Add(compositor.CreateSpriteShape(ellipse));

        CompositionRectangleGeometry rectangle =
            compositor.CreateRectangleGeometry();
        rectangle.Size = new Vector2(8f, 6f);
        rectangle.TrimStart = 0.1f;
        rectangle.TrimEnd = 0.9f;
        visual.Shapes.Add(compositor.CreateSpriteShape(rectangle));

        rounded.Offset = Vector2.One;
        rounded.Offset = Vector2.Zero;
        path.Path = second;
        path.Path = first;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            rounded.Offset = new Vector2(index & 1, 0f);
            path.Path = (index & 1) == 0 ? first : second;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static RgbaPixel ReadPixel(
        byte[] pixels,
        uint width,
        int x,
        int y)
    {
        int index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(
            pixels[index],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static void AssertRed(RgbaPixel pixel) =>
        Assert.True(
            pixel.R > 220 && pixel.G < 30 && pixel.B < 30,
            $"Expected red, got {pixel}.");

    private static void AssertGreen(RgbaPixel pixel) =>
        Assert.True(
            pixel.G > 220 && pixel.R < 30 && pixel.B < 30,
            $"Expected green, got {pixel}.");

    private static void AssertBlue(RgbaPixel pixel) =>
        Assert.True(
            pixel.B > 220 && pixel.R < 30 && pixel.G < 30,
            $"Expected blue, got {pixel}.");

    private static void AssertDark(RgbaPixel pixel) =>
        Assert.True(
            pixel.R < 40 && pixel.G < 40 && pixel.B < 40,
            $"Expected dark background, got {pixel}.");

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class TestCompositionSurface :
        ICompositionSurface,
        IProGpuInvalidatingTextureSource,
        IDisposable
    {
        private readonly SharedGpuTextureSource _source;

        public TestCompositionSurface(GpuTexture texture)
        {
            _source = new SharedGpuTextureSource(texture);
        }

        public event EventHandler? TextureChanged;

        public int AcquireCount { get; private set; }

        public void NotifyTextureChanged() =>
            TextureChanged?.Invoke(this, EventArgs.Empty);

        public bool TryGetGpuTexture(out GpuTexture texture) =>
            _source.TryGetGpuTexture(out texture);

        public bool TryAcquireGpuTextureLease(
            out IProGpuTextureLease lease)
        {
            AcquireCount++;
            return _source.TryAcquireGpuTextureLease(out lease);
        }

        public void Dispose() => _source.Dispose();
    }

    private sealed class UnknownGeometrySource : IGeometrySource2D
    {
    }
}
