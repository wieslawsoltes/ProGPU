using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ProGPU.Scene;
using Xunit;

namespace System.Drawing.Tests;

public sealed class CachedBitmapQualityTests
{
    [Fact]
    public void ConstructorValidatesInputs()
    {
        using var bitmap = new Bitmap(1, 1);
        using Graphics graphics = Graphics.FromImage(bitmap);

        Assert.Throws<ArgumentNullException>(() => new CachedBitmap(null!, graphics));
        Assert.Throws<ArgumentNullException>(() => new CachedBitmap(bitmap, null!));
    }

    [Fact]
    public void DrawUsesImmutableSnapshotAndOutlivesSource()
    {
        var source = new Bitmap(1, 1);
        source.SetPixel(0, 0, Color.Red);
        using var target = new Bitmap(2, 1);
        using Graphics graphics = Graphics.FromImage(target);
        using var cached = new CachedBitmap(source, graphics);

        source.SetPixel(0, 0, Color.Blue);
        source.Dispose();
        graphics.DrawCachedBitmap(cached, 1, 0);

        Assert.Equal(0, target.GetPixel(0, 0).A);
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(1, 0).ToArgb());
    }

    [Fact]
    public void TranslationIsRetainedButScaleAndRotationFailBeforeRecording()
    {
        using var source = new Bitmap(1, 1);
        source.SetPixel(0, 0, Color.Yellow);
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        using var cached = new CachedBitmap(source, graphics);

        graphics.TranslateTransform(2f, 3f);
        graphics.DrawCachedBitmap(cached, 1, 1);
        RenderCommand command = Assert.Single(target.RecordedContext.Commands);
        Assert.Equal(RenderCommandType.DrawTexture, command.Type);
        Assert.Equal(new Rect(1f, 1f, 1f, 1f), command.Rect);

        target.RecordedContext.Clear();
        graphics.ResetTransform();
        graphics.ScaleTransform(2f, 2f);
        Assert.Throws<InvalidOperationException>(() => graphics.DrawCachedBitmap(cached, 0, 0));
        Assert.Empty(target.RecordedContext.Commands);

        graphics.ResetTransform();
        graphics.RotateTransform(10f, MatrixOrder.Prepend);
        Assert.Throws<InvalidOperationException>(() => graphics.DrawCachedBitmap(cached, 0, 0));
        Assert.Empty(target.RecordedContext.Commands);
    }

    [Fact]
    public void DisposedCacheFailsBeforeRecordingAndExistingCommandKeepsLease()
    {
        using var source = new Bitmap(1, 1);
        source.SetPixel(0, 0, Color.Green);
        using var target = new Bitmap(1, 1);
        using Graphics graphics = Graphics.FromImage(target);
        var cached = new CachedBitmap(source, graphics);

        graphics.DrawCachedBitmap(cached, 0, 0);
        cached.Dispose();
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(0, 0).ToArgb());

        Assert.Throws<ArgumentException>(() => graphics.DrawCachedBitmap(cached, 0, 0));
        Assert.Empty(target.RecordedContext.Commands);
    }

    [Fact]
    public void RepeatedDrawsReuseOneRetainedTexture()
    {
        using var source = new Bitmap(2, 2);
        using var target = new Bitmap(8, 2);
        using Graphics graphics = Graphics.FromImage(target);
        using var cached = new CachedBitmap(source, graphics);

        for (int x = 0; x < 4; x++)
        {
            graphics.DrawCachedBitmap(cached, x * 2, 0);
        }

        Assert.Equal(4, target.RecordedContext.Commands.Count);
        Assert.Equal(1, target.RecordedContext.RetainedResourceCount);
    }

    [Fact]
    public void WarmedRecordAndReleaseHasBoundedAllocation()
    {
        using var source = new Bitmap(64, 64);
        using var target = new Bitmap(64, 64);
        using Graphics graphics = Graphics.FromImage(target);
        using var cached = new CachedBitmap(source, graphics);
        graphics.DrawCachedBitmap(cached, 0, 0);
        target.RecordedContext.Clear();

        const int iterations = 128;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            graphics.DrawCachedBitmap(cached, 0, 0);
            target.RecordedContext.Clear();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, iterations * 128L);
    }
}
