using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using ProGPU.Tests.Headless;
using Windows.UI;
using Xunit;

namespace ProGPU.Tests;

public sealed class WinUiCompositionTests
{
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
}
