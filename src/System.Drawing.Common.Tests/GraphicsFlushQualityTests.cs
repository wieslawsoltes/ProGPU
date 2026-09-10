using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class GraphicsFlushQualityTests
{
    [Fact]
    public void FlushIntentionHasOfficialValues()
    {
        Assert.Equal(["Flush", "Sync"], Enum.GetNames<FlushIntention>());
        Assert.Equal(0, (int)FlushIntention.Flush);
        Assert.Equal(1, (int)FlushIntention.Sync);
    }

    [Fact]
    public void BitmapFlushMaterializesPixelsAndPreservesClip()
    {
        using var bitmap = new Bitmap(3, 1);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SetClip(new Rectangle(0, 0, 2, 1));
        graphics.FillRectangle(Brushes.Red, 0, 0, 3, 1);

        graphics.Flush();

        Assert.Equal([RenderCommandType.PushGeometryClip],
            bitmap.RecordedContext.Commands.Select(command => command.Type));

        graphics.FillRectangle(Brushes.Blue, 1, 0, 2, 1);
        graphics.Flush(FlushIntention.Sync);

        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
        Assert.Equal(0, bitmap.GetPixel(2, 0).A);
    }

    [Fact]
    public void HostFlushConsumesBalancedBatchesAndDrawingContinues()
    {
        var context = new DrawingContext();
        using var targetContext = new WgpuContext();
        var intentions = new List<FlushIntention>();
        var batches = new List<RenderCommandType[]>();
        int completed = 0;
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0, 0, 32, 32),
            Matrix4x4.Identity,
            targetContext,
            intention =>
            {
                intentions.Add(intention);
                batches.Add(context.Commands.Select(command => command.Type).ToArray());
                context.Clear();
            },
            () => completed++);

        graphics.SetClip(new Rectangle(0, 0, 16, 16));
        graphics.FillRectangle(Brushes.Red, 0, 0, 4, 4);
        graphics.Flush();

        Assert.Equal([RenderCommandType.PushGeometryClip],
            context.Commands.Select(command => command.Type));

        graphics.FillRectangle(Brushes.Blue, 4, 0, 4, 4);
        graphics.Flush(FlushIntention.Sync);

        Assert.Equal([FlushIntention.Flush, FlushIntention.Sync], intentions);
        Assert.Equal(2, batches.Count);
        Assert.All(batches, batch =>
        {
            Assert.Contains(RenderCommandType.PushGeometryClip, batch);
            Assert.Contains(RenderCommandType.PopGeometryClip, batch);
        });

        graphics.Dispose();
        Assert.Equal(1, completed);
    }

    [Fact]
    public void RawRecorderAndDisposedGraphicsFailAtExplicitBoundaries()
    {
        var context = new DrawingContext();
        Graphics raw = Graphics.FromProGpuDrawingContext(context);
        Assert.Throws<InvalidOperationException>(raw.Flush);
        raw.Dispose();
        Assert.Throws<ArgumentException>(raw.Flush);
        Assert.Throws<ArgumentException>(() => raw.Flush(FlushIntention.Sync));
    }

    [Fact]
    public void HostCallbackMustConsumeTheBatch()
    {
        var context = new DrawingContext();
        using var targetContext = new WgpuContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0, 0, 16, 16),
            Matrix4x4.Identity,
            targetContext,
            static _ => { },
            static () => { });
        graphics.FillRectangle(Brushes.Red, 0, 0, 1, 1);

        Assert.Throws<InvalidOperationException>(graphics.Flush);
    }

    [Fact]
    public void WarmedHostedRecordAndFlushHasBoundedAllocation()
    {
        var context = new DrawingContext();
        using var targetContext = new WgpuContext();
        int flushed = 0;
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0, 0, 16, 16),
            Matrix4x4.Identity,
            targetContext,
            _ =>
            {
                flushed++;
                context.Clear();
            },
            static () => { });

        graphics.FillRectangle(Brushes.Red, 0, 0, 1, 1);
        graphics.Flush();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_024; index++)
        {
            graphics.FillRectangle(Brushes.Red, index & 15, index >> 6, 1, 1);
            graphics.Flush();
        }
        long bytesPerRecordAndFlush =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 1_024;

        Assert.Equal(1_025, flushed);
        Assert.InRange(bytesPerRecordAndFlush, 0, 64);
    }
}
