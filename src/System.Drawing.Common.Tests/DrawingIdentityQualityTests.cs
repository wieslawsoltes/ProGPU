using System.Drawing.Drawing2D;
using Xunit;

namespace System.Drawing.Tests;

public sealed class DrawingIdentityQualityTests
{
    [Fact]
    public void QualityModeHasOfficialValues()
    {
        Assert.Equal(-1, (int)QualityMode.Invalid);
        Assert.Equal(0, (int)QualityMode.Default);
        Assert.Equal(1, (int)QualityMode.Low);
        Assert.Equal(2, (int)QualityMode.High);
    }

    [Fact]
    public void StringUnitHasOfficialValues()
    {
        Assert.Equal(0, (int)StringUnit.World);
        Assert.Equal(1, (int)StringUnit.Display);
        Assert.Equal(2, (int)StringUnit.Pixel);
        Assert.Equal(3, (int)StringUnit.Point);
        Assert.Equal(4, (int)StringUnit.Inch);
        Assert.Equal(5, (int)StringUnit.Document);
        Assert.Equal(6, (int)StringUnit.Millimeter);
        Assert.Equal(32, (int)StringUnit.Em);
    }

    [Fact]
    public void PenTypeHasOfficialValues()
    {
        Assert.Equal(0, (int)PenType.SolidColor);
        Assert.Equal(1, (int)PenType.HatchFill);
        Assert.Equal(2, (int)PenType.TextureFill);
        Assert.Equal(3, (int)PenType.PathGradient);
        Assert.Equal(4, (int)PenType.LinearGradient);
    }

    [Fact]
    public void PenTypeTracksSupportedBrushKind()
    {
        using var bitmap = new Bitmap(1, 1);
        using var solid = new SolidBrush(Color.Red);
        using var hatch = new HatchBrush(HatchStyle.Cross, Color.Red, Color.Blue);
        using var texture = new TextureBrush(bitmap);
        using var gradient = new LinearGradientBrush(
            new PointF(0f, 0f),
            new PointF(1f, 0f),
            Color.Red,
            Color.Blue);
        using var solidPen = new Pen(solid);
        using var hatchPen = new Pen(hatch);
        using var texturePen = new Pen(texture);
        using var gradientPen = new Pen(gradient);

        Assert.Equal(PenType.SolidColor, solidPen.PenType);
        Assert.Equal(PenType.HatchFill, hatchPen.PenType);
        Assert.Equal(PenType.TextureFill, texturePen.PenType);
        Assert.Equal(PenType.LinearGradient, gradientPen.PenType);
    }

    [Fact]
    public void WarmedPenTypeReadsAllocateNothing()
    {
        using var pen = new Pen(Color.Red);
        _ = pen.PenType;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 4_096; index++)
        {
            _ = pen.PenType;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
