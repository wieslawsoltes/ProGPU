using System.Numerics;
using ProGPU.Scene;
using Xunit;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfCombinedGeometry = System.Windows.Media.CombinedGeometry;
using WpfGeometryCombineMode = System.Windows.Media.GeometryCombineMode;
using WpfLineSegment = System.Windows.Media.LineSegment;
using WpfMatrix = System.Windows.Media.Matrix;
using WpfMatrixTransform = System.Windows.Media.MatrixTransform;
using WpfPathFigure = System.Windows.Media.PathFigure;
using WpfPathGeometry = System.Windows.Media.PathGeometry;

namespace ProGPU.Tests;

public sealed class WpfDrawingContextClipTests
{
    [Fact]
    public void PushClipPathGeometryRecordsNativeGeometryClip()
    {
        var nativeContext = new DrawingContext();
        using var drawingContext = new WpfDrawingContext(nativeContext);
        var geometry = CreateTriangleClip();

        drawingContext.PushClip(geometry);
        drawingContext.Pop();

        Assert.Equal(2, nativeContext.Commands.Count);
        Assert.Equal(RenderCommandType.PushGeometryClip, nativeContext.Commands[0].Type);
        Assert.NotNull(nativeContext.Commands[0].Path);
        Assert.True(IsDefaultOrIdentity(nativeContext.Commands[0].Transform));
        Assert.Equal(RenderCommandType.PopGeometryClip, nativeContext.Commands[1].Type);
    }

    [Fact]
    public void PushClipComposesGeometryAndDrawingTransformsOnNativeCommand()
    {
        var nativeContext = new DrawingContext();
        using var drawingContext = new WpfDrawingContext(nativeContext);
        var geometryTransform = new WpfMatrix
        {
            M11 = 1.2,
            M12 = 0.1,
            M21 = 0.2,
            M22 = 1.1,
            OffsetX = 3,
            OffsetY = 5
        };
        var drawingTransform = new WpfMatrix
        {
            M11 = 0.9,
            M12 = 0.3,
            M21 = -0.2,
            M22 = 1.4,
            OffsetX = 7,
            OffsetY = 11
        };
        var geometry = CreateTriangleClip();
        geometry.Transform = new WpfMatrixTransform(geometryTransform);

        drawingContext.PushTransform(new WpfMatrixTransform(drawingTransform));
        drawingContext.PushClip(geometry);
        drawingContext.Pop();
        drawingContext.Pop();

        var command = Assert.Single(nativeContext.Commands, c => c.Type == RenderCommandType.PushGeometryClip);
        Assert.NotNull(command.Path);
        Assert.Equal(ToMatrix4x4(geometryTransform) * ToMatrix4x4(drawingTransform), command.Transform);
    }

    [Fact]
    public void PushClipCombinedGeometryRecordsNativePathOperationClip()
    {
        var nativeContext = new DrawingContext();
        using var drawingContext = new WpfDrawingContext(nativeContext);
        var geometry = new WpfCombinedGeometry(
            WpfGeometryCombineMode.Intersect,
            CreateTriangleClip(),
            CreateOffsetTriangleClip());

        drawingContext.PushClip(geometry);
        drawingContext.Pop();

        Assert.Equal(2, nativeContext.Commands.Count);
        Assert.Equal(RenderCommandType.PushGeometryClip, nativeContext.Commands[0].Type);
        Assert.Equal(RenderCommandType.PopGeometryClip, nativeContext.Commands[1].Type);

        var nativePath = nativeContext.Commands[0].Path;
        Assert.NotNull(nativePath);
        Assert.True(nativePath!.IsCombined);
        Assert.Equal(1, nativePath.Op);
        Assert.NotNull(nativePath.PathA);
        Assert.NotNull(nativePath.PathB);
    }

    [Fact]
    public void PushClipCombinedGeometryBakesChildTransformsAndComposesOwnTransform()
    {
        var nativeContext = new DrawingContext();
        using var drawingContext = new WpfDrawingContext(nativeContext);
        var childTransform = new WpfMatrix
        {
            M11 = 1,
            M22 = 1,
            OffsetX = 5,
            OffsetY = 7
        };
        var combinedTransform = new WpfMatrix
        {
            M11 = 1.1,
            M12 = 0.2,
            M21 = 0.3,
            M22 = 1.2,
            OffsetX = 11,
            OffsetY = 13
        };
        var drawingTransform = new WpfMatrix
        {
            M11 = 0.9,
            M12 = -0.1,
            M21 = 0.25,
            M22 = 1.3,
            OffsetX = 17,
            OffsetY = 19
        };
        var geometry1 = CreateTriangleClip();
        geometry1.Transform = new WpfMatrixTransform(childTransform);
        var geometry = new WpfCombinedGeometry(
            WpfGeometryCombineMode.Union,
            geometry1,
            CreateOffsetTriangleClip())
        {
            Transform = new WpfMatrixTransform(combinedTransform)
        };

        drawingContext.PushTransform(new WpfMatrixTransform(drawingTransform));
        drawingContext.PushClip(geometry);
        drawingContext.Pop();
        drawingContext.Pop();

        var command = Assert.Single(nativeContext.Commands, c => c.Type == RenderCommandType.PushGeometryClip);
        Assert.NotNull(command.Path);
        Assert.True(command.Path!.IsCombined);
        var firstFigure = Assert.Single(command.Path.PathA!.Figures);
        Assert.Equal(new Vector2(15f, 17f), firstFigure.StartPoint);
        Assert.Equal(ToMatrix4x4(combinedTransform) * ToMatrix4x4(drawingTransform), command.Transform);
    }

    private static WpfPathGeometry CreateTriangleClip()
    {
        var geometry = new WpfPathGeometry();
        var figure = new WpfPathFigure
        {
            StartPoint = new Vector2(10f, 10f),
            IsClosed = true
        };
        figure.Segments.Add(new WpfLineSegment(new Vector2(80f, 20f)));
        figure.Segments.Add(new WpfLineSegment(new Vector2(20f, 70f)));
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static WpfPathGeometry CreateOffsetTriangleClip()
    {
        var geometry = new WpfPathGeometry();
        var figure = new WpfPathFigure
        {
            StartPoint = new Vector2(35f, 15f),
            IsClosed = true
        };
        figure.Segments.Add(new WpfLineSegment(new Vector2(100f, 35f)));
        figure.Segments.Add(new WpfLineSegment(new Vector2(50f, 90f)));
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

    private static bool IsDefaultOrIdentity(Matrix4x4 matrix)
    {
        return matrix == default || matrix.IsIdentity;
    }
}
