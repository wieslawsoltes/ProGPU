using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Vector;
using Xunit;

namespace System.Drawing.Drawing2D.Tests;

public sealed class HatchBrushQualityTests
{
    [Fact]
    public void HatchStylePreservesOfficialValuesAndLegacyAliases()
    {
        Assert.Equal(0, (int)HatchStyle.Horizontal);
        Assert.Equal(HatchStyle.Horizontal, HatchStyle.Min);
        Assert.Equal(4, (int)HatchStyle.Cross);
        Assert.Equal(HatchStyle.Cross, HatchStyle.LargeGrid);
        Assert.Equal(HatchStyle.LargeGrid, HatchStyle.Max);
        Assert.Equal(5, (int)HatchStyle.DiagonalCross);
        Assert.Equal(17, (int)HatchStyle.Percent90);
        Assert.Equal(52, (int)HatchStyle.SolidDiamond);
    }

    [Fact]
    public void TwoColorConstructorRetainsReadOnlyValueState()
    {
        using var brush = new HatchBrush(
            HatchStyle.DiagonalCross,
            Color.CornflowerBlue,
            Color.FromArgb(64, 10, 20, 30));

        Assert.Equal(HatchStyle.DiagonalCross, brush.HatchStyle);
        Assert.Equal(Color.CornflowerBlue, brush.ForegroundColor);
        Assert.Equal(Color.FromArgb(64, 10, 20, 30), brush.BackgroundColor);
    }

    [Fact]
    public void ForegroundOnlyConstructorUsesOfficialBlackBackground()
    {
        using var brush = new HatchBrush(HatchStyle.Horizontal, Color.Red);

        Assert.Equal(Color.Black, brush.BackgroundColor);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(53)]
    [InlineData(int.MaxValue)]
    public void ConstructorRejectsValuesOutsideConcreteStyleRange(int value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new HatchBrush((HatchStyle)value, Color.Red, Color.Blue));

        Assert.Equal("hatchstyle", error.ParamName);
    }

    [Fact]
    public void CloneOwnsIndependentLifetimeAndExactValueState()
    {
        var original = new HatchBrush(HatchStyle.Weave, Color.Red, Color.Blue);
        var clone = Assert.IsType<HatchBrush>(original.Clone());

        original.Dispose();

        Assert.Throws<ObjectDisposedException>(() => original.ToProGpuBrush());
        Assert.Equal(HatchStyle.Weave, clone.HatchStyle);
        Assert.Equal(Color.Red, clone.ForegroundColor);
        Assert.Equal(Color.Blue, clone.BackgroundColor);
        clone.Dispose();
    }

    [Fact]
    public void BasicStylesLowerToExactDeterministicTiles()
    {
        Assert.Equal(
            0x000000FF000000FFUL,
            LowerPattern(HatchStyle.Horizontal));
        Assert.Equal(
            0x1111111111111111UL,
            LowerPattern(HatchStyle.Vertical));
        Assert.Equal(
            LowerPattern(HatchStyle.Horizontal) |
                LowerPattern(HatchStyle.Vertical),
            LowerPattern(HatchStyle.Cross));
        Assert.Equal(
            LowerPattern(HatchStyle.Cross),
            LowerPattern(HatchStyle.LargeGrid));
    }

    [Fact]
    public void PercentageStylesHaveMonotonicDeclaredPixelDensity()
    {
        int[] expectedCounts = [3, 6, 13, 16, 19, 26, 32, 38, 45, 48, 51, 58];

        for (int index = 0; index < expectedCounts.Length; index++)
        {
            ulong pattern = LowerPattern((HatchStyle)(6 + index));
            Assert.Equal(expectedCounts[index], BitOperations.PopCount(pattern));
        }
    }

    [Fact]
    public void EveryConcreteStyleProducesAVisibleBoundedTile()
    {
        var patterns = new HashSet<ulong>();
        for (int value = 0; value <= (int)HatchStyle.SolidDiamond; value++)
        {
            ulong pattern = LowerPattern((HatchStyle)value);
            Assert.NotEqual(0UL, pattern);
            Assert.NotEqual(ulong.MaxValue, pattern);
            patterns.Add(pattern);
        }

        Assert.InRange(patterns.Count, 48, 53);
    }

    [Fact]
    public void LoweringCarriesBothColorsAndStableNegativeCoordinatePhase()
    {
        using var brush = new HatchBrush(
            HatchStyle.Horizontal,
            Color.FromArgb(128, 255, 64, 32),
            Color.FromArgb(32, 16, 8, 4));

        var lowered = Assert.IsType<TilePatternBrush>(brush.ToProGpuBrush());

        Assert.Equal(new Vector4(1f, 64f / 255f, 32f / 255f, 128f / 255f), lowered.ForegroundColor);
        Assert.Equal(new Vector4(16f / 255f, 8f / 255f, 4f / 255f, 32f / 255f), lowered.BackgroundColor);
        Assert.True(lowered.IsForegroundPixel(-8, -8));
        Assert.False(lowered.IsForegroundPixel(-1, -1));

        var corner = new TilePatternBrush(1UL << 63, Vector4.One, Vector4.Zero);
        Assert.True(corner.IsForegroundPixel(-1, -1));
        Assert.False(corner.IsForegroundPixel(-8, -8));
    }

    [Fact]
    public void RepeatedLoweringHasOneBoundedBrushAllocation()
    {
        using var brush = new HatchBrush(
            HatchStyle.SolidDiamond,
            Color.White,
            Color.Black);
        _ = brush.ToProGpuBrush();

        const int iterations = 1024;
        ProGPU.Vector.Brush? last = null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            last = brush.ToProGpuBrush();
        }
        long bytesPerLowering =
            (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        GC.KeepAlive(last);

        Assert.InRange(bytesPerLowering, 32, 96);
    }

    [Fact]
    public void ProductionVectorShaderSamplesBothPackedMaskWordsAndColors()
    {
        Assert.Contains("brush.brushType == 8u", Shaders.VectorShader);
        Assert.Contains("select(brush.stopCount, brush.stopOffset, bitIndex >= 32u)", Shaders.VectorShader);
        Assert.Contains("select(brush.stopColors1, brush.stopColors0, patternBit != 0u)", Shaders.VectorShader);
        Assert.Contains("((integerCoord.x % 8) + 8) % 8", Shaders.VectorShader);
    }

    [Fact]
    public void NativeBrushTableAcceptsAndPreservesFullTilePayload()
    {
        const ulong pattern = 0xFEDCBA9876543210UL;
        var foreground = new Vector4(1f, 0.5f, 0.25f, 0.75f);
        var background = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);
        NativeSceneBrush brush = NativeSceneBrush.TilePattern(
            pattern,
            foreground,
            background);
        byte[] destination = new byte[
            NativeSceneStreamBuilder.GetRequiredBufferSize(
                commandCapacity: 0,
                resourceCapacity: 1,
                arenaCapacity: 512)];
        var stream = new NativeSceneStreamBuilder(
            destination,
            sceneId: 1,
            generation: 1,
            commandCapacity: 0,
            resourceCapacity: 1);

        Assert.True(stream.TryAddBrushTableResource(
            resourceId: 1,
            generation: 1,
            [brush],
            [],
            out uint resourceIndex));

        Assert.Equal(0U, resourceIndex);
        Assert.Equal(NativeSceneBrushKind.TilePattern, brush.Kind);
        Assert.Equal(unchecked((uint)pattern), brush.StopCount);
        Assert.Equal((uint)(pattern >> 32), brush.StopOffset);
        Assert.Equal(foreground, brush.Color0);
        Assert.Equal(background, brush.Color1);
    }

    private static ulong LowerPattern(HatchStyle style)
    {
        using var brush = new HatchBrush(style, Color.White, Color.Black);
        return Assert.IsType<TilePatternBrush>(brush.ToProGpuBrush()).Pattern;
    }
}
