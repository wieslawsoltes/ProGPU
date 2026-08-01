using System.Runtime.InteropServices;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPMColorCompatibilityTests
{
    private static bool UsesRgbaLayout =>
        OperatingSystem.IsMacOS() ||
        OperatingSystem.IsIOS() ||
        OperatingSystem.IsMacCatalyst() ||
        OperatingSystem.IsTvOS() ||
        OperatingSystem.IsBrowser();

    [Fact]
    public void PackedValueChannelsEqualityAndFormattingMatchContract()
    {
        SKPMColor color = 0x80402010u;

        Assert.Equal(4, Marshal.SizeOf<SKPMColor>());
        Assert.Equal((byte)0x80, color.Alpha);
        Assert.Equal(UsesRgbaLayout ? (byte)0x10 : (byte)0x40, color.Red);
        Assert.Equal((byte)0x20, color.Green);
        Assert.Equal(UsesRgbaLayout ? (byte)0x40 : (byte)0x10, color.Blue);
        Assert.Equal(0x80402010u, (uint)color);
        Assert.Equal(color, new SKPMColor(0x80402010u));
        Assert.True(color == new SKPMColor(0x80402010u));
        Assert.True(color != new SKPMColor(0x80402011u));
        Assert.Equal(color.GetHashCode(), new SKPMColor(0x80402010u).GetHashCode());
        Assert.Equal(UsesRgbaLayout ? "#80102040" : "#80402010", color.ToString());
    }

    [Fact]
    public void PremultiplyUsesRoundedEightBitProducts()
    {
        Assert.Equal(0x00000000u, (uint)SKPMColor.PreMultiply(new SKColor(255, 255, 255, 0)));
        Assert.Equal(NativePacked(0xff112233u), (uint)SKPMColor.PreMultiply(new SKColor(0x11, 0x22, 0x33, 0xff)));
        Assert.Equal(NativePacked(0x80800000u), (uint)SKPMColor.PreMultiply(new SKColor(255, 0, 0, 128)));
        Assert.Equal(NativePacked(0x40402010u), (uint)SKPMColor.PreMultiply(new SKColor(255, 128, 64, 64)));
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
        Assert.Equal(0xff112233u, (uint)SKPMColor.UnPreMultiply(new SKPMColor(NativePacked(0xff112233u))));
        Assert.Equal(0x80ff0000u, (uint)SKPMColor.UnPreMultiply(new SKPMColor(NativePacked(0x80800000u))));
        Assert.Equal(0x40ff8040u, (uint)SKPMColor.UnPreMultiply(new SKPMColor(NativePacked(0x40402010u))));
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
        Assert.Equal(new uint[]
            {
                NativePacked(0x80800000u),
                NativePacked(0x40004000u),
                NativePacked(0x20000020u)
            },
            premultiplied.Select(static color => (uint)color));
        Assert.NotSame(colors, premultiplied);

        var unpremultiplied = SKPMColor.UnPreMultiply(premultiplied);
        Assert.Equal(colors, unpremultiplied);
        Assert.NotSame(colors, unpremultiplied);
        Assert.Throws<ArgumentNullException>(() => SKPMColor.PreMultiply(null!));
        Assert.Throws<ArgumentNullException>(() => SKPMColor.UnPreMultiply(null!));
    }

    private static uint NativePacked(uint argb) =>
        UsesRgbaLayout
            ? (argb & 0xff00ff00u) |
              ((argb & 0x00ff0000u) >> 16) |
              ((argb & 0x000000ffu) << 16)
            : argb;
}
