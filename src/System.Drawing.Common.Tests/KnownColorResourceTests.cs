using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit;

namespace System.Drawing.Tests;

public sealed class KnownColorResourceTests
{
    private static Brush? s_brushSink;
    private static Pen? s_penSink;

    [Theory]
    [InlineData(KnownColor.AliceBlue)]
    [InlineData(KnownColor.CornflowerBlue)]
    [InlineData(KnownColor.DarkGoldenrod)]
    [InlineData(KnownColor.Transparent)]
    [InlineData(KnownColor.YellowGreen)]
    public void StandardResources_ExposeExpectedKnownColor(KnownColor knownColor)
    {
        Brush brush = GetStandardBrush(knownColor);
        Pen pen = GetStandardPen(knownColor);

        Assert.Equal(Color.FromKnownColor(knownColor), Assert.IsType<SolidBrush>(brush).Color);
        Assert.Equal(Color.FromKnownColor(knownColor), pen.Color);
        Assert.Same(brush, GetStandardBrush(knownColor));
        Assert.Same(pen, GetStandardPen(knownColor));
    }

    [Fact]
    public void SystemResources_ExposeExpectedKnownColor()
    {
        Assert.Equal(SystemColors.Control, Assert.IsType<SolidBrush>(SystemBrushes.Control).Color);
        Assert.Equal(SystemColors.Highlight, SystemPens.Highlight.Color);
        Assert.Same(SystemBrushes.WindowText, SystemBrushes.WindowText);
        Assert.Same(SystemPens.WindowText, SystemPens.WindowText);
    }

    [Fact]
    public void ConcurrentFirstAccess_PublishesOneResource()
    {
        var brushes = new ConcurrentBag<Brush>();
        var pens = new ConcurrentBag<Pen>();

        Parallel.For(0, 128, _ =>
        {
            brushes.Add(Brushes.MediumVioletRed);
            pens.Add(Pens.MediumVioletRed);
        });

        Brush expectedBrush = brushes.First();
        Pen expectedPen = pens.First();
        Assert.All(brushes, brush => Assert.Same(expectedBrush, brush));
        Assert.All(pens, pen => Assert.Same(expectedPen, pen));
    }

    [Fact]
    public void WarmedKnownColorAccess_IsAllocationFree()
    {
        AccessWarmedResources(10_000);
        long before = GC.GetAllocatedBytesForCurrentThread();

        AccessWarmedResources(100_000);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AccessWarmedResources(int count)
    {
        for (int index = 0; index < count; index++)
        {
            s_brushSink = Brushes.CornflowerBlue;
            s_penSink = Pens.CornflowerBlue;
        }
    }

    private static Brush GetStandardBrush(KnownColor knownColor) => knownColor switch
    {
        KnownColor.AliceBlue => Brushes.AliceBlue,
        KnownColor.CornflowerBlue => Brushes.CornflowerBlue,
        KnownColor.DarkGoldenrod => Brushes.DarkGoldenrod,
        KnownColor.Transparent => Brushes.Transparent,
        KnownColor.YellowGreen => Brushes.YellowGreen,
        _ => throw new ArgumentOutOfRangeException(nameof(knownColor))
    };

    private static Pen GetStandardPen(KnownColor knownColor) => knownColor switch
    {
        KnownColor.AliceBlue => Pens.AliceBlue,
        KnownColor.CornflowerBlue => Pens.CornflowerBlue,
        KnownColor.DarkGoldenrod => Pens.DarkGoldenrod,
        KnownColor.Transparent => Pens.Transparent,
        KnownColor.YellowGreen => Pens.YellowGreen,
        _ => throw new ArgumentOutOfRangeException(nameof(knownColor))
    };
}
