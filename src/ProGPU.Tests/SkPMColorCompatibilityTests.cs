using System.Runtime.InteropServices;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPMColorCompatibilityTests
{
    [Fact]
    public void PackedValueChannelsEqualityAndFormattingMatchContract()
    {
        SKPMColor color = 0x80402010u;

        Assert.Equal(4, Marshal.SizeOf<SKPMColor>());
        Assert.Equal((byte)0x80, color.Alpha);
        Assert.Equal((byte)0x10, color.Red);
        Assert.Equal((byte)0x20, color.Green);
        Assert.Equal((byte)0x40, color.Blue);
        Assert.Equal(0x80402010u, (uint)color);
        Assert.Equal(color, new SKPMColor(0x80402010u));
        Assert.True(color == new SKPMColor(0x80402010u));
        Assert.True(color != new SKPMColor(0x80402011u));
        Assert.Equal(color.GetHashCode(), new SKPMColor(0x80402010u).GetHashCode());
        Assert.Equal("#80102040", color.ToString());
    }

    [Fact]
    public void PremultiplyUsesRoundedEightBitProducts()
    {
        Assert.Equal(0x00000000u, (uint)SKPMColor.PreMultiply(new SKColor(255, 255, 255, 0)));
        Assert.Equal(0xff332211u, (uint)SKPMColor.PreMultiply(new SKColor(0x11, 0x22, 0x33, 0xff)));
        Assert.Equal(0x80000080u, (uint)SKPMColor.PreMultiply(new SKColor(255, 0, 0, 128)));
        Assert.Equal(0x40102040u, (uint)SKPMColor.PreMultiply(new SKColor(255, 128, 64, 64)));
    }

    [Fact]
    public void EveryPremultipliedComponentIsBoundedByAlpha()
    {
        for (var alpha = 0; alpha <= byte.MaxValue; alpha++)
        {
            for (var component = 0; component <= byte.MaxValue; component++)
            {
                var color = SKPMColor.PreMultiply(
                    new SKColor((byte)component, 0, 0, (byte)alpha));
                Assert.InRange(color.Red, (byte)0, (byte)alpha);
            }
        }
    }

    [Fact]
    public void UnpremultiplyRestoresClosestRepresentableColor()
    {
        Assert.Equal(SKColor.Empty, SKPMColor.UnPreMultiply(new SKPMColor(0x00ffffffu)));
        Assert.Equal(0xff332211u, (uint)SKPMColor.UnPreMultiply(new SKPMColor(0xff112233u)));
        Assert.Equal(0x80ff0000u, (uint)SKPMColor.UnPreMultiply(new SKPMColor(0x80000080u)));
        Assert.Equal(0x40ff8040u, (uint)SKPMColor.UnPreMultiply(new SKPMColor(0x40102040u)));
    }

    [Fact]
    public void ExplicitConversionsUsePremultipliedSemantics()
    {
        var source = new SKColor(255, 128, 64, 64);
        var premultiplied = (SKPMColor)source;

        Assert.Equal(SKPMColor.PreMultiply(source), premultiplied);
        Assert.Equal(SKPMColor.UnPreMultiply(premultiplied), (SKColor)premultiplied);
    }

    [Fact]
    public void ArrayConversionsAllocateIndependentResultsAndPreserveOrder()
    {
        var colors = new[]
        {
            new SKColor(255, 0, 0, 128),
            new SKColor(0, 255, 0, 64),
            new SKColor(0, 0, 255, 32)
        };

        var premultiplied = SKPMColor.PreMultiply(colors);
        Assert.Equal(new uint[] { 0x80000080u, 0x40004000u, 0x20200000u },
            premultiplied.Select(static color => (uint)color));
        Assert.NotSame(colors, premultiplied);

        var unpremultiplied = SKPMColor.UnPreMultiply(premultiplied);
        Assert.Equal(colors, unpremultiplied);
        Assert.NotSame(colors, unpremultiplied);
        Assert.Throws<ArgumentNullException>(() => SKPMColor.PreMultiply(null!));
        Assert.Throws<ArgumentNullException>(() => SKPMColor.UnPreMultiply(null!));
    }
}
