using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class GdiClassDiagramReportingContractTests
{
    [Fact]
    public void ClassDiagramGradientTransformMapsUnitGradientToItemBounds()
    {
        using var gradient = new LinearGradientBrush(
            new PointF(0f, 0f),
            new PointF(1f, 0f),
            Color.CornflowerBlue,
            Color.White);

        gradient.ResetTransform();
        gradient.TranslateTransform(25f, 40f);
        gradient.ScaleTransform(120f, 1f);

        var native = Assert.IsType<ProGPU.Vector.LinearGradientBrush>(gradient.ToProGpuBrush());
        Vector2 start = Vector2.Transform(new Vector2(25f, 40f), ToMatrix3x2(native.CoordinateTransform));
        Vector2 end = Vector2.Transform(new Vector2(145f, 40f), ToMatrix3x2(native.CoordinateTransform));

        AssertNear(Vector2.Zero, start);
        AssertNear(Vector2.UnitX, end);
    }

    [Fact]
    public void GradientResetTransformRestoresIdentityCommandState()
    {
        var context = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(context);
        using var gradient = new LinearGradientBrush(
            new PointF(0f, 0f),
            new PointF(1f, 0f),
            Color.Black,
            Color.White);

        gradient.TranslateTransform(10f, 20f, MatrixOrder.Append);
        gradient.ScaleTransform(2f, 3f, MatrixOrder.Append);
        gradient.ResetTransform();
        graphics.FillRectangle(gradient, 0f, 0f, 40f, 20f);

        RenderCommand command = Assert.Single(context.Commands);
        var native = Assert.IsType<ProGPU.Vector.LinearGradientBrush>(command.Brush);
        Assert.Equal(Matrix4x4.Identity, native.CoordinateTransform);
        Assert.Throws<ArgumentException>(() => gradient.TranslateTransform(0f, 0f, (MatrixOrder)2));
        Assert.Throws<ArgumentException>(() => gradient.ScaleTransform(1f, 1f, (MatrixOrder)(-1)));
    }

    [Fact]
    public void ReportingSetLineCapFlowsToDrawLineCommand()
    {
        var context = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(context);
        using var pen = new Pen(Color.Black, 2f);

        pen.SetLineCap(LineCap.Round, LineCap.Triangle, DashCap.Flat);
        graphics.DrawLine(pen, new Point(10, 20), new Point(100, 20));

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawLine, command.Type);
        Assert.Equal(ProGPU.Vector.PenLineCap.Round, command.Pen!.StartLineCap);
        Assert.Equal(ProGPU.Vector.PenLineCap.Triangle, command.Pen.EndLineCap);
        Assert.Equal(ProGPU.Vector.PenLineCap.Flat, command.Pen.DashCap);
    }

    [Fact]
    public void PenJoinMiterAndDashCapsFlowToNativeCommandState()
    {
        var context = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(context);
        using var pen = new Pen(Color.Black, 3f)
        {
            DashStyle = DashStyle.Dash,
            DashCap = DashCap.Round,
            LineJoin = LineJoin.Bevel,
            MiterLimit = 6f,
            StartCap = LineCap.Square,
            EndCap = LineCap.Round
        };

        graphics.DrawLine(pen, 0f, 0f, 20f, 0f);

        ProGPU.Vector.Pen native = Assert.Single(context.Commands).Pen!;
        Assert.Equal(ProGPU.Vector.PenLineCap.Square, native.StartLineCap);
        Assert.Equal(ProGPU.Vector.PenLineCap.Round, native.EndLineCap);
        Assert.Equal(ProGPU.Vector.PenLineCap.Round, native.DashCap);
        Assert.Equal(ProGPU.Vector.PenLineJoin.Bevel, native.LineJoin);
        Assert.Equal(6f, native.MiterLimit);
        Assert.Equal([3d, 1d], native.DashArray!);
    }

    [Fact]
    public void PenPropertiesValidateEnumsAndMatchGdiMiterClamping()
    {
        using var pen = new Pen(Color.Black);

        Assert.Throws<InvalidEnumArgumentException>(() => pen.StartCap = (LineCap)4);
        Assert.Throws<InvalidEnumArgumentException>(() => pen.EndCap = (LineCap)0x15);
        Assert.Throws<InvalidEnumArgumentException>(() => pen.DashCap = (DashCap)1);
        Assert.Throws<InvalidEnumArgumentException>(() => pen.LineJoin = (LineJoin)4);

        pen.MiterLimit = -1f;
        Assert.Equal(1f, pen.MiterLimit);
        pen.SetLineCap((LineCap)int.MaxValue, (LineCap)int.MaxValue, (DashCap)int.MaxValue);
        Assert.Equal((LineCap)int.MaxValue, pen.StartCap);
        Assert.Equal((LineCap)int.MaxValue, pen.EndCap);
        Assert.Equal(DashCap.Flat, pen.DashCap);
    }

    private static Matrix3x2 ToMatrix3x2(Matrix4x4 matrix)
    {
        return new Matrix3x2(
            matrix.M11,
            matrix.M12,
            matrix.M21,
            matrix.M22,
            matrix.M41,
            matrix.M42);
    }

    private static void AssertNear(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(actual.X, expected.X - 0.0001f, expected.X + 0.0001f);
        Assert.InRange(actual.Y, expected.Y - 0.0001f, expected.Y + 0.0001f);
    }
}
