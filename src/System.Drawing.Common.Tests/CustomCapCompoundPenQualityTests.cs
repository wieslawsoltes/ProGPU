using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using Xunit;

namespace System.Drawing.Tests;

public sealed class CustomCapCompoundPenQualityTests
{
    private static float s_capSink;

    [Fact]
    public void CustomLineCapOwnsPathAndMutableState()
    {
        using var fill = new GraphicsPath();
        fill.AddPolygon([
            new PointF(-1f, 0f),
            new PointF(0f, 2f),
            new PointF(1f, 0f),
        ]);
        using var cap = new CustomLineCap(fill, null, LineCap.Round, 0.5f)
        {
            StrokeJoin = LineJoin.Bevel,
            WidthScale = 2f,
        };
        cap.SetStrokeCaps(LineCap.Square, LineCap.Round);
        fill.Reset();

        using var clone = (CustomLineCap)cap.Clone();
        clone.GetStrokeCaps(out LineCap start, out LineCap end);

        Assert.Equal(LineCap.Round, clone.BaseCap);
        Assert.Equal(0.5f, clone.BaseInset);
        Assert.Equal(LineJoin.Bevel, clone.StrokeJoin);
        Assert.Equal(2f, clone.WidthScale);
        Assert.Equal(LineCap.Square, start);
        Assert.Equal(LineCap.Round, end);
    }

    [Fact]
    public void CustomFillPathRequiresClosureAndAttachmentAxis()
    {
        using var incomplete = new GraphicsPath();
        incomplete.AddLine(0f, -2f, 0f, 2f);
        Assert.Throws<ArgumentException>(() => new CustomLineCap(incomplete, null));

        using var detached = new GraphicsPath();
        detached.AddLines([
            new PointF(-2f, 1f),
            new PointF(2f, 1f),
            new PointF(0f, 3f),
            new PointF(-2f, 1f),
        ]);
        Assert.Throws<NotImplementedException>(() => new CustomLineCap(detached, null));

        using var attached = new GraphicsPath();
        attached.AddLines([
            new PointF(-2f, -1f),
            new PointF(0f, 3f),
            new PointF(2f, -1f),
            new PointF(-2f, -1f),
        ]);
        using var cap = new CustomLineCap(attached, null);
        Assert.Equal(LineCap.Flat, cap.BaseCap);
    }

    [Fact]
    public void AdjustableArrowCapCloneRetainsDerivedState()
    {
        using var arrow = new AdjustableArrowCap(3f, 5f, isFilled: false)
        {
            MiddleInset = 1.25f,
            WidthScale = 1.5f,
        };
        using var clone = Assert.IsType<AdjustableArrowCap>(arrow.Clone());

        Assert.Equal(3f, clone.Width);
        Assert.Equal(5f, clone.Height);
        Assert.Equal(1.25f, clone.MiddleInset);
        Assert.False(clone.Filled);
        Assert.Equal(LineCap.Triangle, clone.BaseCap);
        Assert.Equal(1.5f, clone.WidthScale);
    }

    [Fact]
    public void PenSnapshotsCompoundBandsAndCustomCaps()
    {
        using var arrow = new AdjustableArrowCap(3f, 4f) { MiddleInset = 0.5f };
        using var pen = new Pen(Color.Black, 4f)
        {
            CompoundArray = [0f, 0.2f, 0.8f, 1f],
            CustomEndCap = arrow,
        };
        arrow.Height = 9f;

        float[] compound = pen.CompoundArray;
        compound[1] = 0.5f;
        using CustomLineCap snapshot = pen.CustomEndCap;

        Assert.Equal(new[] { 0f, 0.2f, 0.8f, 1f }, pen.CompoundArray);
        Assert.Equal(4f, Assert.IsType<AdjustableArrowCap>(snapshot).Height);
        Assert.Equal(LineCap.Custom, pen.EndCap);
    }

    [Fact]
    public void CompoundBandsRenderWithARealCenterGap()
    {
        using var bitmap = new Bitmap(64, 64);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var pen = new Pen(Color.Black, 10f)
        {
            CompoundArray = [0f, 0.2f, 0.8f, 1f],
        };

        graphics.DrawLine(pen, 8, 32, 56, 32);

        Assert.Equal(0, bitmap.GetPixel(32, 32).A);
        Assert.True(bitmap.GetPixel(32, 27).A > 0);
        Assert.True(bitmap.GetPixel(32, 36).A > 0);
        Assert.Equal(0, bitmap.GetPixel(32, 22).A);
    }

    [Fact]
    public void AdjustableArrowCapAffectsPixelsBoundsAndOutlineHitTesting()
    {
        using var bitmap = new Bitmap(80, 64);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var arrow = new AdjustableArrowCap(4f, 4f);
        using var pen = new Pen(Color.Black, 4f) { CustomEndCap = arrow };
        using var path = new GraphicsPath();
        path.AddLine(20, 32, 44, 32);

        graphics.DrawPath(pen, path);
        RectangleF bounds = path.GetBounds(null, pen);

        Assert.True(bitmap.GetPixel(55, 32).A > 0);
        Assert.Equal(0, bitmap.GetPixel(55, 20).A);
        Assert.True(bounds.Right >= 59f);
        Assert.True(path.IsOutlineVisible(55f, 32f, pen));
        Assert.False(path.IsOutlineVisible(55f, 20f, pen));
    }

    [Fact]
    public void GenericFillCapUsesEndpointOrientationAndBaseInset()
    {
        using var fill = new GraphicsPath();
        fill.AddPolygon([
            new PointF(-1f, 0f),
            new PointF(0f, 3f),
            new PointF(1f, 0f),
        ]);
        using var cap = new CustomLineCap(fill, null) { BaseInset = 1f };
        using var pen = new Pen(Color.Black, 3f) { CustomEndCap = cap };
        using var path = new GraphicsPath();
        path.AddLine(10, 20, 30, 20);
        using var widened = (GraphicsPath)path.Clone();

        widened.Widen(pen);
        RectangleF bounds = widened.GetBounds();

        Assert.True(bounds.Right >= 35.5f);
        Assert.True(bounds.Top <= 17f);
        Assert.True(bounds.Bottom >= 23f);
    }

    [Fact]
    public void GenericStrokeCapUsesItsOwnJoinAndEndpointCaps()
    {
        using var stroke = new GraphicsPath();
        stroke.AddLines([
            new PointF(-1f, 0f),
            new PointF(0f, 2f),
            new PointF(1f, 0f),
        ]);
        using var cap = new CustomLineCap(null, stroke)
        {
            StrokeJoin = LineJoin.Round,
            WidthScale = 1.25f,
        };
        cap.SetStrokeCaps(LineCap.Round, LineCap.Round);
        using var pen = new Pen(Color.Black, 4f) { CustomEndCap = cap };
        using var bitmap = new Bitmap(64, 48);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);

        graphics.DrawLine(pen, 8, 24, 40, 24);

        Assert.True(bitmap.GetPixel(49, 24).A > 0);
        Assert.Equal(0, bitmap.GetPixel(49, 12).A);
    }

    [Fact]
    public void InvalidCompoundShapesFailBeforeChangingOwnedState()
    {
        using var pen = new Pen(Color.Black) { CompoundArray = [0f, 1f] };

        Assert.Throws<ArgumentNullException>(() => pen.CompoundArray = null!);
        Assert.Throws<ArgumentException>(() => pen.CompoundArray = []);
        Assert.Throws<ArgumentException>(() => pen.CompoundArray = [0f, 0.5f, 1f]);
        Assert.Throws<ArgumentException>(() => pen.CompoundArray = [-0.1f, 1f]);
        Assert.Throws<ArgumentException>(() => pen.CompoundArray = [0.8f, 0.2f]);
        Assert.Throws<ArgumentException>(() => pen.CompoundArray = [0f, float.PositiveInfinity]);
        Assert.Equal(new[] { 0f, 1f }, pen.CompoundArray);
    }

    [Fact]
    public void CustomCapRetainsPermittedNonfiniteScalarsAndArbitraryJoinValue()
    {
        using var stroke = new GraphicsPath();
        using var cap = new CustomLineCap(null, stroke)
        {
            BaseInset = float.NaN,
            WidthScale = float.PositiveInfinity,
            StrokeJoin = (LineJoin)(-1),
        };

        Assert.True(float.IsNaN(cap.BaseInset));
        Assert.Equal(float.PositiveInfinity, cap.WidthScale);
        Assert.Equal((LineJoin)(-1), cap.StrokeJoin);
    }

    [Fact]
    public void WarmedArrowMutationIsAllocationFree()
    {
        using var arrow = new AdjustableArrowCap(3f, 4f);
        Mutate(arrow, 1_000);
        long before = GC.GetAllocatedBytesForCurrentThread();

        Mutate(arrow, 10_000);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(s_capSink > 0f);
    }

    [Fact]
    public void CompoundArrowWidenHasBoundedAllocation()
    {
        using var path = new GraphicsPath();
        path.AddLines([
            new PointF(0f, 0f),
            new PointF(128f, 0f),
            new PointF(128f, 64f),
            new PointF(16f, 64f),
        ]);
        using var arrow = new AdjustableArrowCap(3f, 4f) { MiddleInset = 0.5f };
        using var pen = new Pen(Color.Black, 6f)
        {
            CompoundArray = [0f, 0.2f, 0.8f, 1f],
            CustomEndCap = arrow,
            LineJoin = LineJoin.Round,
        };
        Widen(path, pen, 8);
        long before = GC.GetAllocatedBytesForCurrentThread();

        int points = Widen(path, pen, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(points > 20);
        Assert.InRange(allocated, 8_000, 12_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Mutate(AdjustableArrowCap arrow, int count)
    {
        for (int index = 0; index < count; index++)
        {
            float value = (index & 7) + 1f;
            arrow.Width = value;
            arrow.Height = value + 1f;
            arrow.MiddleInset = value * 0.25f;
            arrow.Filled = (index & 1) == 0;
            s_capSink = arrow.Width + arrow.Height + arrow.MiddleInset;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Widen(GraphicsPath source, Pen pen, int count)
    {
        int points = 0;
        for (int index = 0; index < count; index++)
        {
            using var clone = (GraphicsPath)source.Clone();
            clone.Widen(pen);
            points = clone.PointCount;
        }

        return points;
    }
}
