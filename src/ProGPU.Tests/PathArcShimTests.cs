using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Tests.Headless;
using Xunit;
using VectorArcSegment = ProGPU.Vector.ArcSegment;
using VectorLineSegment = ProGPU.Vector.LineSegment;
using VectorPen = ProGPU.Vector.Pen;
using VectorSolidColorBrush = ProGPU.Vector.SolidColorBrush;
using VectorSweepDirection = ProGPU.Vector.SweepDirection;
using WinUiArcSegment = Microsoft.UI.Xaml.Media.ArcSegment;
using WinUiMatrixTransform = Microsoft.UI.Xaml.Media.MatrixTransform;
using WinUiPathFigure = Microsoft.UI.Xaml.Media.PathFigure;
using WinUiPathGeometry = Microsoft.UI.Xaml.Media.PathGeometry;
using WinUiSweepDirection = Microsoft.UI.Xaml.Media.SweepDirection;
using WpfArcSegment = System.Windows.Media.ArcSegment;
using WpfMatrix = System.Windows.Media.Matrix;
using WpfMatrixTransform = System.Windows.Media.MatrixTransform;
using WpfPathFigure = System.Windows.Media.PathFigure;
using WpfPathGeometry = System.Windows.Media.PathGeometry;
using WpfSweepDirection = System.Windows.Media.SweepDirection;

namespace ProGPU.Tests;

public sealed class PathArcShimTests
{
    [Fact]
    public void WinUiPathGeometryParsePreservesSvgArcSegment()
    {
        var geometry = WinUiPathGeometry.Parse("M 0 0 A 20 10 30 0 1 40 0");

        var figure = Assert.Single(geometry.Figures);
        var parsedArc = Assert.IsType<WinUiArcSegment>(Assert.Single(figure.Segments));
        Assert.Equal(new Vector2(40f, 0f), parsedArc.Point);
        Assert.Equal(new Vector2(20f, 10f), parsedArc.Size);
        Assert.Equal(30f, parsedArc.RotationAngle);
        Assert.False(parsedArc.IsLargeArc);
        Assert.Equal(WinUiSweepDirection.Clockwise, parsedArc.SweepDirection);

        var internalArc = Assert.IsType<VectorArcSegment>(
            Assert.Single(geometry.GetTransformedInternalGeometry().Figures[0].Segments));
        Assert.Equal(new Vector2(40f, 0f), internalArc.Point);
        Assert.Equal(VectorSweepDirection.Clockwise, internalArc.SweepDirection);
    }

    [Fact]
    public void WinUiPathGeometryTransformPreservesNativeArcSegment()
    {
        var geometry = CreateWinUiArcGeometry();
        geometry.Transform = new WinUiMatrixTransform
        {
            Matrix = new Matrix4x4(
                1.2f, 0.2f, 0f, 0f,
                0.35f, 1.4f, 0f, 0f,
                0f, 0f, 1f, 0f,
                3f, -2f, 0f, 1f)
        };

        var internalGeometry = geometry.GetTransformedInternalGeometry();
        var segment = Assert.Single(internalGeometry.Figures[0].Segments);

        Assert.IsType<VectorArcSegment>(segment);
        Assert.IsNotType<VectorLineSegment>(segment);
    }

    [Fact]
    public void PresentationCorePathGeometryParsePreservesSvgArcSegment()
    {
        var geometry = WpfPathGeometry.Parse("M 0 0 A 20 10 30 0 1 40 0");

        var figure = Assert.Single(geometry.Figures);
        var parsedArc = Assert.IsType<WpfArcSegment>(Assert.Single(figure.Segments));
        Assert.Equal(new Vector2(40f, 0f), parsedArc.Point);
        Assert.Equal(new Vector2(20f, 10f), parsedArc.Size);
        Assert.Equal(30f, parsedArc.RotationAngle);
        Assert.False(parsedArc.IsLargeArc);
        Assert.Equal(WpfSweepDirection.Clockwise, parsedArc.SweepDirection);
    }

    [Fact]
    public void PresentationCorePathGeometryRenderPreservesNativeArcSegment()
    {
        var geometry = CreateWpfArcGeometry();
        var transform = new WpfMatrix
        {
            M11 = 1.2,
            M12 = 0.2,
            M21 = 0.35,
            M22 = 1.4,
            OffsetX = 3,
            OffsetY = -2
        };
        geometry.Transform = new WpfMatrixTransform(transform);
        var context = new ProGPU.Scene.DrawingContext();

        geometry.Draw(
            context,
            fill: null,
            pen: new VectorPen(new VectorSolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), 4f));

        var command = Assert.Single(context.Commands);
        Assert.NotNull(command.Path);
        var segment = Assert.Single(command.Path.Figures[0].Segments);
        Assert.IsType<VectorArcSegment>(segment);
        Assert.IsNotType<VectorLineSegment>(segment);
        Assert.Equal(ToMatrix4x4(transform), command.Transform);
    }

    [Fact]
    public void VectorPathGeometryCreateTransformedPreservesNativeArcSegment()
    {
        var path = new ProGPU.Vector.PathGeometry();
        var figure = new ProGPU.Vector.PathFigure(new Vector2(30f, 100f));
        figure.Segments.Add(new VectorArcSegment(
            new Vector2(210f, 100f),
            new Vector2(110f, 70f),
            20f,
            isLargeArc: false,
            VectorSweepDirection.Clockwise));
        path.Figures.Add(figure);

        var transformed = path.CreateTransformed(new Matrix4x4(
            1.05f, 0.18f, 0f, 0f,
            0.32f, 0.92f, 0f, 0f,
            0f, 0f, 1f, 0f,
            8f, -4f, 0f, 1f));

        var segment = Assert.Single(transformed.Figures[0].Segments);
        Assert.IsType<VectorArcSegment>(segment);
        Assert.IsNotType<VectorLineSegment>(segment);
    }

    [Fact]
    public void WinUiPathGeometryRenderUsesGpuArcShader()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(240, 160);
        window.Content = new WinUiArcPathVisual();

        window.Render();

        Assert.Equal(4, window.Compositor.VectorVertices.Count(vertex => DecodeShapeType(vertex.ShapeType) == 12));
        Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 11);
        Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 3);

        window.Content = null;
    }

    [Fact]
    public void PresentationCorePathGeometryRenderUsesGpuArcShader()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(260, 180);
        window.Content = new WpfArcPathVisual();

        try
        {
            window.Render();

            Assert.Equal(4, window.Compositor.VectorVertices.Count(vertex => DecodeShapeType(vertex.ShapeType) == 12));
            Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 11);
            Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 3);
        }
        finally
        {
            window.Content = null;
        }
    }

    private static WinUiPathGeometry CreateWinUiArcGeometry()
    {
        var geometry = new WinUiPathGeometry();
        var figure = new WinUiPathFigure(new Vector2(30f, 100f));
        figure.Segments.Add(new WinUiArcSegment(
            new Vector2(210f, 100f),
            new Vector2(110f, 70f),
            20f,
            isLargeArc: false,
            WinUiSweepDirection.Clockwise));
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static WpfPathGeometry CreateWpfArcGeometry()
    {
        var geometry = new WpfPathGeometry();
        var figure = new WpfPathFigure
        {
            StartPoint = new Vector2(30f, 100f)
        };
        figure.Segments.Add(new WpfArcSegment(
            new Vector2(210f, 100f),
            new Vector2(110f, 70f),
            20f,
            isLargeArc: false,
            WpfSweepDirection.Clockwise));
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Matrix4x4 ToMatrix4x4(WpfMatrix matrix)
    {
        return new Matrix4x4(
            (float)matrix.M11,
            (float)matrix.M12,
            0f,
            0f,
            (float)matrix.M21,
            (float)matrix.M22,
            0f,
            0f,
            0f,
            0f,
            1f,
            0f,
            (float)matrix.OffsetX,
            (float)matrix.OffsetY,
            0f,
            1f);
    }

    private static int DecodeShapeType(float shapeType)
    {
        if (shapeType >= 1000f)
        {
            shapeType -= 1000f;
        }

        if (shapeType >= 195f)
        {
            shapeType -= 200f;
        }
        else if (shapeType >= 95f)
        {
            shapeType -= 100f;
        }

        return (int)MathF.Round(shapeType);
    }

    private sealed class WinUiArcPathVisual : FrameworkElement
    {
        public WinUiArcPathVisual()
        {
            Width = 240f;
            Height = 160f;
        }

        public override void OnRender(ProGPU.Scene.DrawingContext context)
        {
            var geometry = CreateWinUiArcGeometry();
            geometry.Transform = new WinUiMatrixTransform
            {
                Matrix = Matrix4x4.CreateTranslation(0f, 0f, 0f)
            };

            geometry.Draw(
                context,
                fill: null,
                pen: new VectorPen(new VectorSolidColorBrush(new Vector4(0.1f, 0.6f, 1f, 1f)), 8f));
        }
    }

    private sealed class WpfArcPathVisual : FrameworkElement
    {
        public WpfArcPathVisual()
        {
            Width = 260f;
            Height = 180f;
        }

        public override void OnRender(ProGPU.Scene.DrawingContext context)
        {
            var geometry = CreateWpfArcGeometry();
            geometry.Transform = new WpfMatrixTransform(new WpfMatrix
            {
                M11 = 1.05,
                M12 = 0.18,
                M21 = 0.32,
                M22 = 0.92,
                OffsetX = 8,
                OffsetY = -4
            });

            geometry.Draw(
                context,
                fill: null,
                pen: new VectorPen(new VectorSolidColorBrush(new Vector4(0.9f, 0.25f, 0.1f, 1f)), 7f));
        }
    }
}
