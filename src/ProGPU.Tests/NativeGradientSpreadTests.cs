using System.Numerics;
using ProGPU.Backend.Native;
using Xunit;

namespace Avalonia.ProGpu.UnitTests;

public class NativeGradientSpreadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitIntervalPadRetainsOriginalOffsetsAndLegacyPad(bool radial)
    {
        NativeSceneGradientStop[] stops = [new(Vector4.UnitW, -1f), new(Vector4.One, 1f)];
        var brush = NativeSceneBrush.LinearGradient(Vector2.Zero, Vector2.One, 0U, stops,
            spread: NativeSceneGradientSpread.PadUnitInterval);
        if (radial)
        {
            brush.Kind = NativeSceneBrushKind.RadialGradient;
            brush.Radius = brush.RadiusY = 1f;
        }

        Span<byte> bytes = stackalloc byte[4096];
        var builder = new NativeSceneStreamBuilder(bytes, 1U, 1U, commandCapacity: 0, resourceCapacity: 2);
        Assert.Equal(4U, (uint)NativeSceneGradientSpread.PadUnitInterval);
        Assert.Equal(256, System.Runtime.InteropServices.Marshal.SizeOf<NativeSceneBrush>());
        Assert.True(builder.TryAddBrushTableResource(1U, 1U, [brush], stops, out _));
        brush.Spread = NativeSceneGradientSpread.Pad;
        Assert.True(builder.TryAddBrushTableResource(2U, 1U, [brush], stops, out _));
        Assert.True(builder.TryBuild(out _));
        Assert.Equal(-1f, stops[0].Offset);
        Assert.Equal(1f, stops[1].Offset);
    }

    [Theory]
    [InlineData(5U, NativeSceneBrushKind.LinearGradient)]
    [InlineData(0x20000004U, NativeSceneBrushKind.LinearGradient)]
    [InlineData(0x40000004U, NativeSceneBrushKind.LinearGradient)]
    [InlineData(0x80000004U, NativeSceneBrushKind.RadialGradient)]
    [InlineData(4U, NativeSceneBrushKind.Solid)]
    [InlineData(4U, NativeSceneBrushKind.SweepGradient)]
    [InlineData(4U, NativeSceneBrushKind.TwoPointConicalGradient)]
    public void UnitIntervalPadRejectsUnknownAndIncompatibleValuesAtomically(uint spread, NativeSceneBrushKind kind)
    {
        NativeSceneGradientStop[] stops = [new(Vector4.UnitW, -1f), new(Vector4.One, 1f)];
        var brush = NativeSceneBrush.LinearGradient(Vector2.Zero, Vector2.One, 0U, stops);
        byte[] bytes = new byte[4096];
        var builder = new NativeSceneStreamBuilder(bytes, 1U, 1U, commandCapacity: 0, resourceCapacity: 2);
        Assert.True(builder.TryAddBrushTableResource(1U, 1U, [brush], stops, out _));
        var before = (byte[])bytes.Clone();
        brush.Spread = (NativeSceneGradientSpread)spread;
        brush.Kind = kind;
        brush.Radius = brush.RadiusY = 1f;
        Assert.False(builder.TryAddBrushTableResource(2U, 1U, [brush], stops, out _));
        Assert.Equal(before, bytes);
        Assert.True(builder.TryBuild(out _));
    }
}
