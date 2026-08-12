using System.Numerics;
using ProGPU.Scene;
using Xunit;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfMatrix = System.Windows.Media.Matrix;
using WpfMatrixTransform = System.Windows.Media.MatrixTransform;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfRectangleGeometry = System.Windows.Media.RectangleGeometry;

namespace ProGPU.Tests;

public sealed class WpfDrawingContextStrokeTransformTests
{
    [Fact]
    public void PushTransformRetainsRectanglePenThicknessInLocalSpace()
    {
        var nativeContext = new DrawingContext();
        using var context = new WpfDrawingContext(nativeContext);
        var transform = new WpfMatrix
        {
            M11 = 3,
            M22 = 3
        };

        context.PushTransform(new WpfMatrixTransform(transform));
        context.DrawRectangle(
            brush: null,
            pen: new WpfPen(WpfBrushes.Black, 2),
            rectangle: new WpfRect(0, 0, 20, 10));

        var command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawRect, command.Type);
        Assert.NotNull(command.Pen);
        AssertNear(2f, command.Pen!.Thickness);
        Assert.True(command.IsPenThicknessLocal);
        AssertMatrixNear(Matrix4x4.CreateScale(3f, 3f, 1f), command.Transform);
    }

    [Fact]
    public void PushTransformRetainsLinePenThicknessInLocalSpace()
    {
        var nativeContext = new DrawingContext();
        using var context = new WpfDrawingContext(nativeContext);
        var transform = new WpfMatrix
        {
            M11 = 2,
            M22 = 5
        };

        context.PushTransform(new WpfMatrixTransform(transform));
        context.DrawLine(
            new WpfPen(WpfBrushes.Black, 1.5),
            new WpfPoint(0, 0),
            new WpfPoint(10, 0));

        var command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawLine, command.Type);
        Assert.NotNull(command.Pen);
        AssertNear(1.5f, command.Pen!.Thickness);
        Assert.True(command.IsPenThicknessLocal);
        AssertMatrixNear(ToMatrix4x4(transform), command.Transform);
    }

    [Fact]
    public void PushTransformRetainsGeometryPenThicknessInLocalSpace()
    {
        var nativeContext = new DrawingContext();
        using var context = new WpfDrawingContext(nativeContext);
        var transform = new WpfMatrix
        {
            M11 = 4,
            M22 = 4
        };

        context.PushTransform(new WpfMatrixTransform(transform));
        context.DrawGeometry(
            brush: null,
            pen: new WpfPen(WpfBrushes.Black, 0.75),
            geometry: new WpfRectangleGeometry(new WpfRect(0, 0, 20, 10)));

        var command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Pen);
        AssertNear(0.75f, command.Pen!.Thickness);
        Assert.True(command.IsPenThicknessLocal);
        AssertMatrixNear(Matrix4x4.CreateScale(4f, 4f, 1f), command.Transform);
    }

    [Fact]
    public void NestedPushTransformAppendsNewTransformInWpfOrder()
    {
        var nativeContext = new DrawingContext();
        using var context = new WpfDrawingContext(nativeContext);
        var scale = new WpfMatrix
        {
            M11 = 2,
            M22 = 2
        };
        var translate = WpfMatrix.Identity;
        translate.Translate(10, 5);

        context.PushTransform(new WpfMatrixTransform(scale));
        context.PushTransform(new WpfMatrixTransform(translate));
        context.DrawLine(
            new WpfPen(WpfBrushes.Black, 1),
            new WpfPoint(0, 0),
            new WpfPoint(10, 0));

        var command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawLine, command.Type);
        AssertMatrixNear(
            Matrix4x4.CreateScale(2f, 2f, 1f) * Matrix4x4.CreateTranslation(10f, 5f, 0f),
            command.Transform);
    }

    [Fact]
    public void MatrixRotateAppendsToOffsets()
    {
        var matrix = WpfMatrix.Identity;
        matrix.Translate(10, 5);

        matrix.Rotate(90);

        AssertNear(-5, matrix.OffsetX);
        AssertNear(10, matrix.OffsetY);
    }

    [Fact]
    public void MatrixScaleAppendsToOffsets()
    {
        var matrix = WpfMatrix.Identity;
        matrix.Translate(10, 5);

        matrix.Scale(2, 3);

        AssertNear(20, matrix.OffsetX);
        AssertNear(15, matrix.OffsetY);
    }

    [Fact]
    public void DefaultMatrixMutatorsStartFromIdentity()
    {
        var translate = new WpfMatrix();
        translate.Translate(10, 5);

        AssertNear(1, translate.M11);
        AssertNear(1, translate.M22);
        AssertNear(10, translate.OffsetX);
        AssertNear(5, translate.OffsetY);

        var scale = default(WpfMatrix);
        scale.Scale(2, 3);

        AssertNear(2, scale.M11);
        AssertNear(3, scale.M22);
        AssertNear(0, scale.M12);
        AssertNear(0, scale.M21);

        var rotate = new WpfMatrix();
        rotate.Rotate(90);

        AssertNear(0, rotate.M11);
        AssertNear(1, rotate.M12);
        AssertNear(-1, rotate.M21);
        AssertNear(0, rotate.M22);
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

    private static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        AssertNear(expected.M11, actual.M11);
        AssertNear(expected.M12, actual.M12);
        AssertNear(expected.M21, actual.M21);
        AssertNear(expected.M22, actual.M22);
        AssertNear(expected.M41, actual.M41);
        AssertNear(expected.M42, actual.M42);
    }

    private static void AssertNear(float expected, float actual)
    {
        Assert.InRange(MathF.Abs(expected - actual), 0f, 0.0001f);
    }

    private static void AssertNear(double expected, double actual)
    {
        Assert.InRange(Math.Abs(expected - actual), 0d, 0.0001d);
    }
}
