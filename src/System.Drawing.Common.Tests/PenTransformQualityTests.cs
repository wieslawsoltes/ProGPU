using ProGPU.Scene;
using ProGPU.Vector;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using Xunit;

namespace System.Drawing.Tests;

public sealed class PenTransformQualityTests
{
    private static float s_transformSink;
    private static int s_pointCountSink;

    [Fact]
    public void TransformSnapshotsInputGetterAndCloneState()
    {
        using var pen = new Pen(Color.Navy, 4f);
        using var input = new Matrix(2f, 0.5f, 0.25f, 3f, 7f, 11f);
        using var expected = input.Clone();

        pen.Transform = input;
        input.Translate(100f, 200f);

        using Matrix first = pen.Transform;
        Assert.Equal(expected, first);
        first.Reset();
        using Matrix second = pen.Transform;
        Assert.Equal(expected, second);

        using var clone = Assert.IsType<Pen>(pen.Clone());
        clone.ResetTransform();
        using Matrix cloneTransform = clone.Transform;
        Assert.True(cloneTransform.IsIdentity);
        using Matrix sourceTransform = pen.Transform;
        Assert.Equal(expected, sourceTransform);
    }

    [Fact]
    public void TransformOperationsHonorMatrixOrderAndRejectInvalidDirectMatrices()
    {
        using var pen = new Pen(Color.Black);
        using var original = new Matrix(1f, 2f, 3f, 4f, 5f, 6f);
        using var operation = new Matrix(2f, 0f, 0f, 3f, 7f, 8f);
        pen.Transform = original;

        using var expected = original.Clone();
        expected.Multiply(operation, MatrixOrder.Append);
        pen.MultiplyTransform(operation, MatrixOrder.Append);
        using Matrix actual = pen.Transform;
        Assert.Equal(expected, actual);

        using var singular = new Matrix(1f, 2f, 2f, 4f, 0f, 0f);
        Assert.Throws<ArgumentException>(() => pen.Transform = singular);
        Assert.Throws<ArgumentException>(() => pen.MultiplyTransform(singular));
        Assert.Throws<ArgumentNullException>(() => pen.Transform = null!);
        Assert.Throws<ArgumentNullException>(() => pen.MultiplyTransform(null!));

        var disposed = new Matrix();
        disposed.Dispose();
        pen.MultiplyTransform(disposed);
        Assert.Throws<ArgumentException>(() => pen.Transform = disposed);
        using Matrix unchanged = pen.Transform;
        Assert.Equal(expected, unchanged);
    }

    [Fact]
    public void AnisotropicTipWidensWithoutMovingCenterlineAndTranslationIsIgnored()
    {
        using var path = new GraphicsPath();
        path.AddLine(20f, 10f, 20f, 50f);
        using var pen = new Pen(Color.Red, 4f);
        pen.ScaleTransform(3f, 1f);
        pen.TranslateTransform(100f, 200f, MatrixOrder.Append);

        RectangleF bounds = path.GetBounds(null, pen);
        Assert.Equal(14f, bounds.Left, 3);
        Assert.Equal(26f, bounds.Right, 3);
        Assert.Equal(10f, bounds.Top, 3);
        Assert.Equal(50f, bounds.Bottom, 3);
        Assert.True(path.IsOutlineVisible(25f, 30f, pen));
        Assert.False(path.IsOutlineVisible(27f, 30f, pen));

        path.Widen(pen);
        RectangleF widenedBounds = path.GetBounds();
        Assert.Equal(bounds.Left, widenedBounds.Left, 3);
        Assert.Equal(bounds.Right, widenedBounds.Right, 3);
        Assert.Equal(bounds.Top, widenedBounds.Top, 3);
        Assert.Equal(bounds.Bottom, widenedBounds.Bottom, 3);
    }

    [Fact]
    public void DrawingUsesRetainedFilledStrokeGeometryForTransformedTips()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 80f, 80f));
        using var pen = new Pen(Color.Blue, 4f);
        pen.ScaleTransform(3f, 1f);

        graphics.DrawLine(pen, 20f, 10f, 20f, 50f);

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Brush);
        Assert.Null(command.Pen);
        Assert.NotNull(command.Path);
        Assert.True(command.Path!.TryGetBounds(out var minimum, out var maximum));
        Assert.Equal(14f, minimum.X, 3);
        Assert.Equal(26f, maximum.X, 3);
        Assert.Equal(10f, minimum.Y, 3);
        Assert.Equal(50f, maximum.Y, 3);

        context.Commands.Clear();
        pen.ScaleTransform(0f, 0f);
        graphics.DrawLine(pen, 20f, 10f, 20f, 50f);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void TransformedDashedTipUsesTheSameWidenedGeometryForHitTesting()
    {
        using var path = new GraphicsPath();
        path.AddLine(10f, 20f, 70f, 20f);
        using var pen = new Pen(Color.Green, 4f)
        {
            DashStyle = DashStyle.Dash,
        };
        pen.ScaleTransform(1f, 3f);

        Assert.True(path.IsOutlineVisible(15f, 25f, pen));
        Assert.False(path.IsOutlineVisible(24f, 20f, pen));
        Assert.False(path.IsOutlineVisible(15f, 27f, pen));
    }

    [Fact]
    public void TransformedTipRendersThroughBitmapBackend()
    {
        using var bitmap = new Bitmap(48, 64);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var pen = new Pen(Color.Blue, 4f))
        {
            pen.ScaleTransform(3f, 1f);
            graphics.DrawLine(pen, 20f, 8f, 20f, 56f);
        }

        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(25, 32).ToArgb());
        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), bitmap.GetPixel(28, 32).ToArgb());
    }

    [Fact]
    public void WarmedTransformMutationIsAllocationFree()
    {
        using var pen = new Pen(Color.Black);
        MutateTransform(pen, 1_000);
        long before = GC.GetAllocatedBytesForCurrentThread();

        MutateTransform(pen, 10_000);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(float.IsFinite(s_transformSink));
    }

    [Fact]
    public void WarmedAnisotropicWideningHasBoundedAllocation()
    {
        using var path = new GraphicsPath();
        path.AddLines(
        [
            new PointF(0f, 0f),
            new PointF(128f, 0f),
            new PointF(128f, 64f),
            new PointF(16f, 64f),
        ]);
        using var pen = new Pen(Color.Black, 3f) { LineJoin = LineJoin.Round };
        pen.ScaleTransform(2.5f, 0.75f);
        pen.RotateTransform(20f, MatrixOrder.Append);
        WidenPath(path, pen, 16);
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int Iterations = 128;
        WidenPath(path, pen, Iterations);

        long bytesPerWiden = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
        Assert.InRange(bytesPerWiden, 6_500, 8_500);
        Assert.True(s_pointCountSink > 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MutateTransform(Pen pen, int count)
    {
        for (int index = 0; index < count; index++)
        {
            pen.ResetTransform();
            pen.ScaleTransform(1.5f, 0.75f);
            pen.RotateTransform(15f, MatrixOrder.Append);
            pen.TranslateTransform(2f, 3f);
            s_transformSink = pen.Width;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WidenPath(GraphicsPath path, Pen pen, int count)
    {
        for (int index = 0; index < count; index++)
        {
            using var clone = (GraphicsPath)path.Clone();
            clone.Widen(pen);
            s_pointCountSink = clone.PointCount;
        }
    }
}
