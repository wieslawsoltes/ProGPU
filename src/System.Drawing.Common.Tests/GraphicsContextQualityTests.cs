using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

#pragma warning disable SYSLIB0016
public sealed class GraphicsContextQualityTests
{
    [Fact]
    public void DefaultContextHasZeroOffsetAndInfiniteClip()
    {
        using var bitmap = new Bitmap(10, 10);
        using Graphics graphics = Graphics.FromImage(bitmap);

        graphics.GetContextInfo(out PointF offset);
        Assert.True(offset.IsEmpty);

        graphics.GetContextInfo(out offset, out Region? clip);
        Assert.True(offset.IsEmpty);
        Assert.Null(clip);

        object[] legacy = Assert.IsType<object[]>(graphics.GetContextInfo());
        Assert.Equal(2, legacy.Length);
        using Region legacyClip = Assert.IsType<Region>(legacy[0]);
        using Matrix legacyTransform = Assert.IsType<Matrix>(legacy[1]);
        Assert.True(legacyClip.IsInfinite(graphics));
        Assert.True(legacyTransform.IsIdentity);
    }

    [Fact]
    public void ClipRetainsTheTransformActiveWhenItWasApplied()
    {
        using var bitmap = new Bitmap(10, 10);
        using Graphics clipThenTransform = Graphics.FromImage(bitmap);
        clipThenTransform.SetClip(new Rectangle(1, 2, 9, 10));
        clipThenTransform.TransformElements = Matrix3x2.CreateTranslation(1, 2);

        clipThenTransform.GetContextInfo(out PointF firstOffset, out Region? firstClip);
        using (firstClip)
        {
            Assert.Equal(new PointF(1, 2), firstOffset);
            Assert.Equal(new RectangleF(0, 0, 9, 10), firstClip!.GetBounds(clipThenTransform));
        }

        using Graphics transformThenClip = Graphics.FromImage(bitmap);
        transformThenClip.TransformElements = Matrix3x2.CreateTranslation(1, 2);
        transformThenClip.SetClip(new Rectangle(1, 2, 9, 10));

        transformThenClip.GetContextInfo(out PointF secondOffset, out Region? secondClip);
        using (secondClip)
        {
            Assert.Equal(new PointF(1, 2), secondOffset);
            Assert.Equal(new RectangleF(1, 2, 9, 10), secondClip!.GetBounds(transformThenClip));
        }
    }

    [Fact]
    public void SavedContextsAccumulateTransformAndClip()
    {
        using var bitmap = new Bitmap(10, 10);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SetClip(new Rectangle(1, 2, 9, 10));
        graphics.TransformElements = Matrix3x2.CreateTranslation(1, 2);
        GraphicsState state = graphics.Save();

        graphics.GetContextInfo(out PointF offset, out Region? clip);
        using (clip)
        {
            Assert.Equal(new PointF(2, 4), offset);
            Assert.Equal(new RectangleF(0, 0, 8, 8), clip!.GetBounds(graphics));
        }

        object[] legacy = Assert.IsType<object[]>(graphics.GetContextInfo());
        using Region legacyClip = Assert.IsType<Region>(legacy[0]);
        using Matrix legacyTransform = Assert.IsType<Matrix>(legacy[1]);
        Assert.Equal(new RectangleF(0, 0, 8, 8), legacyClip.GetBounds(graphics));
        Assert.Equal([1f, 0f, 0f, 1f, 2f, 4f], legacyTransform.Elements);

        graphics.Restore(state);
    }

    [Fact]
    public void ReturnedClipRemainsOwnedAfterStateRestore()
    {
        using var bitmap = new Bitmap(10, 10);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SetClip(new Rectangle(1, 2, 9, 10));
        graphics.TransformElements = Matrix3x2.CreateTranslation(1, 2);
        GraphicsState state = graphics.Save();

        graphics.GetContextInfo(out PointF offset, out Region? clip);
        graphics.Restore(state);

        using (clip)
        {
            Assert.Equal(new PointF(2, 4), offset);
            Assert.Equal(new RectangleF(0, 0, 8, 8), clip!.GetBounds(graphics));
        }
    }

    [Fact]
    public void OffsetOnlyContextReadIsAllocationFreeWhenWarm()
    {
        using var bitmap = new Bitmap(10, 10);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.TranslateTransform(3, 5);
        PointF offset = default;
        for (int iteration = 0; iteration < 1_024; iteration++)
        {
            graphics.GetContextInfo(out offset);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            graphics.GetContextInfo(out offset);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new PointF(3, 5), offset);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DisposedGraphicsRejectsContextReads()
    {
        using var bitmap = new Bitmap(10, 10);
        Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Dispose();

        Assert.Throws<ArgumentException>(() => graphics.GetContextInfo(out PointF _));
        Assert.Throws<ArgumentException>(() => graphics.GetContextInfo(out PointF _, out Region? _));
        Assert.Throws<ArgumentException>(() => graphics.GetContextInfo());
    }
}
#pragma warning restore SYSLIB0016
