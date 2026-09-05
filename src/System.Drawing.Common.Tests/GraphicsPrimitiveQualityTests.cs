using ProGPU.Scene;
using ProGPU.Vector;
using System.Drawing.Drawing2D;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsPrimitiveQualityTests
{
    [Fact]
    public void PrimitiveOverloadsRecordTypedPathsAndPreserveFillRules()
    {
        using var target = new Bitmap(96, 96);
        using Graphics graphics = Graphics.FromImage(target);
        using var pen = new Pen(Color.Navy, 2f);
        using var brush = new SolidBrush(Color.Orange);
        ReadOnlySpan<PointF> curve =
        [
            new(5f, 20f),
            new(20f, 5f),
            new(35f, 35f),
            new(50f, 20f)
        ];
        ReadOnlySpan<Point> polygon = [new(8, 8), new(28, 8), new(18, 28)];

        graphics.DrawArc(pen, new RectangleF(2f, 2f, 30f, 20f), 15f, 210f);
        graphics.DrawBezier(pen, curve[0], curve[1], curve[2], curve[3]);
        graphics.DrawBeziers(pen, curve);
        graphics.DrawClosedCurve(pen, curve, 0.4f, FillMode.Winding);
        graphics.DrawCurve(pen, curve, 0, 3, 0.4f);
        graphics.DrawPie(pen, new RectangleF(32f, 2f, 24f, 24f), 0f, 120f);
        graphics.FillPie(brush, new RectangleF(58f, 2f, 24f, 24f), 0f, 120f);
        graphics.FillClosedCurve(brush, curve, FillMode.Winding, 0.4f);
        graphics.FillPolygon(brush, polygon, FillMode.Alternate);
        graphics.DrawRoundedRectangle(pen, new RectangleF(2f, 40f, 30f, 20f), new SizeF(6f, 8f));
        graphics.FillRoundedRectangle(brush, new RectangleF(36f, 40f, 30f, 20f), new SizeF(6f, 8f));

        RenderCommand[] paths = graphics.DrawingContext.Commands
            .Where(static command => command.Type == RenderCommandType.DrawPath)
            .ToArray();

        Assert.Equal(11, paths.Length);
        Assert.All(paths, static command => Assert.NotNull(command.Path));
        Assert.Equal(FillRule.Nonzero, paths[7].Path!.FillRule);
        Assert.Equal(FillRule.EvenOdd, paths[8].Path!.FillRule);
        Assert.Contains(paths, static command => command.Pen is not null);
        Assert.Contains(paths, static command => command.Brush is not null);
        Assert.Contains(
            paths,
            static command => command.Path!.Figures.Any(static figure => figure.IsClosed));
    }

    [Fact]
    public void RectangleSpanOverloadsRecordEveryPrimitiveWithoutArrayCopies()
    {
        using var target = new Bitmap(64, 64);
        using Graphics graphics = Graphics.FromImage(target);
        using var pen = new Pen(Color.Black);
        using var brush = new SolidBrush(Color.Red);
        ReadOnlySpan<RectangleF> rectangles =
        [
            new(2f, 2f, 8f, 9f),
            new(14f, 2f, 10f, 11f)
        ];

        graphics.DrawRectangle(pen, new RectangleF(2f, 20f, 8f, 9f));
        graphics.FillRectangle(brush, new RectangleF(14f, 20f, 10f, 11f));
        graphics.DrawRectangles(pen, rectangles);
        graphics.FillRectangles(brush, rectangles);

        Assert.Equal(
            6,
            graphics.DrawingContext.Commands.Count(
                static command => command.Type == RenderCommandType.DrawRect));
    }

    [Fact]
    public void FilledPieProducesExpectedInteriorAndExteriorPixels()
    {
        using var target = new Bitmap(40, 40);
        using (Graphics graphics = Graphics.FromImage(target))
        using (var brush = new SolidBrush(Color.Red))
        {
            graphics.FillPie(brush, new RectangleF(4f, 4f, 32f, 32f), 0f, 90f);
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(26, 26).ToArgb());
        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), target.GetPixel(12, 12).ToArgb());
    }

    [Fact]
    public void PrimitiveFamiliesRejectInvalidGeometryBeforeRecording()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 64f, 64f));
        using var pen = new Pen(Color.Black);
        using var brush = new SolidBrush(Color.Red);

        Assert.Throws<ArgumentException>(() =>
            graphics.DrawBeziers(pen, [new Point(0, 0), new Point(1, 1), new Point(2, 2)]));
        Assert.Throws<ArgumentException>(() =>
            graphics.DrawClosedCurve(pen, [new Point(0, 0), new Point(1, 1)]));
        Assert.Throws<ArgumentException>(() =>
            graphics.FillPolygon(brush, [new Point(0, 0), new Point(1, 1)], FillMode.Winding));
        Assert.Throws<ArgumentException>(() =>
            graphics.FillClosedCurve(brush, [new Point(0, 0), new Point(1, 1)]));

        Assert.Empty(context.Commands);
    }

    [Fact]
    public void WarmedCurveSpanRecordingHasBoundedManagedAllocation()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 64f, 64f));
        using var pen = new Pen(Color.Black);
        PointF[] points = [new(0f, 10f), new(10f, 0f), new(20f, 20f), new(30f, 10f)];
        graphics.DrawCurve(pen, points.AsSpan(), 0, 3, 0.5f);
        context.Commands.Clear();

        const int Iterations = 128;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Iterations; index++)
        {
            graphics.DrawCurve(pen, points.AsSpan(), 0, 3, 0.5f);
            context.Commands.Clear();
        }

        long bytesPerRecord = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
        Assert.InRange(bytesPerRecord, 512, 1_024);
    }
}
