using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using Xunit;

namespace System.Drawing.Tests;

public sealed class PenOwnershipQualityTests
{
    private static Color s_colorSink;
    private static PenType s_penTypeSink;
    private static float s_widthSink;

    [Fact]
    public void BrushConstructorSnapshotsInputAndGetterReturnsIndependentClones()
    {
        using var source = new SolidBrush(Color.Red);
        using var pen = new Pen(source, 2f);

        source.Color = Color.Blue;
        using var first = Assert.IsType<SolidBrush>(pen.Brush);
        first.Color = Color.Green;
        using var second = Assert.IsType<SolidBrush>(pen.Brush);

        Assert.Equal(Color.Red, second.Color);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void BrushSetterSnapshotsNonSolidBrush()
    {
        using var pen = new Pen(Color.Black);
        var source = new HatchBrush(HatchStyle.Cross, Color.Red, Color.Blue);

        pen.Brush = source;
        source.Dispose();
        using var snapshot = Assert.IsType<HatchBrush>(pen.Brush);

        Assert.Equal(HatchStyle.Cross, snapshot.HatchStyle);
        Assert.Equal(Color.Red, snapshot.ForegroundColor);
        Assert.Equal(Color.Blue, snapshot.BackgroundColor);
        Assert.Equal(PenType.HatchFill, pen.PenType);
    }

    [Fact]
    public void CloneOwnsIndependentBrushAndDashState()
    {
        using var source = new Pen(Color.Red, 3f)
        {
            DashPattern = [2f, 4f]
        };
        using var clone = Assert.IsType<Pen>(source.Clone());

        clone.Color = Color.Blue;
        float[] clonePattern = clone.DashPattern;
        clonePattern[0] = 9f;

        Assert.Equal(Color.Red, source.Color);
        Assert.Equal([2f, 4f], source.DashPattern);
        Assert.Equal(Color.Blue, clone.Color);
        Assert.Equal([2f, 4f], clone.DashPattern);
    }

    [Fact]
    public void PenRejectsUseAfterDispose()
    {
        var pen = new Pen(Color.Red);
        pen.Dispose();
        pen.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pen.Clone());
        Assert.Throws<ObjectDisposedException>(() => pen.Brush);
        Assert.Throws<ObjectDisposedException>(() => pen.Color);
        Assert.Throws<ObjectDisposedException>(() => pen.Width = 2f);
        Assert.Throws<ObjectDisposedException>(() => pen.ToProGpuPen());
    }

    [Fact]
    public void KnownColorBrushesAndPensAreImmutableButCloneAsMutable()
    {
        var brush = Assert.IsType<SolidBrush>(Brushes.Red);
        Pen pen = Pens.Red;

        Assert.Throws<ArgumentException>(() => brush.Color = Color.Blue);
        Assert.Throws<ArgumentException>(() => brush.Dispose());
        Assert.Throws<ArgumentException>(() => pen.Color = Color.Blue);
        Assert.Throws<ArgumentException>(() => pen.Width = 2f);
        Assert.Throws<ArgumentException>(() => pen.Brush = new SolidBrush(Color.Blue));
        Assert.Throws<ArgumentException>(() => pen.Dispose());

        using var brushClone = Assert.IsType<SolidBrush>(brush.Clone());
        using var penClone = Assert.IsType<Pen>(pen.Clone());
        brushClone.Color = Color.Blue;
        penClone.Color = Color.Blue;

        Assert.Equal(Color.Red, brush.Color);
        Assert.Equal(Color.Red, pen.Color);
    }

    [Fact]
    public void WarmedKnownPenScalarReadsAreAllocationFree()
    {
        ReadKnownPenState(10_000);
        long before = GC.GetAllocatedBytesForCurrentThread();

        ReadKnownPenState(100_000);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadKnownPenState(int count)
    {
        for (int index = 0; index < count; index++)
        {
            Pen pen = Pens.CornflowerBlue;
            s_colorSink = pen.Color;
            s_penTypeSink = pen.PenType;
            s_widthSink = pen.Width;
        }
    }
}
