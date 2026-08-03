using ProGPU.Backend;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkBlenderCompatibilityTests
{
    [Fact]
    public void BlendModeFactorySupportsEveryModeAndRejectsInvalidValues()
    {
        foreach (var mode in Enum.GetValues<SKBlendMode>())
        {
            using var blender = SKBlender.CreateBlendMode(mode);
            Assert.NotEqual(IntPtr.Zero, blender.Handle);
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SKBlender.CreateBlendMode((SKBlendMode)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SKBlender.CreateBlendMode((SKBlendMode)29));
    }

    [Fact]
    public void ArithmeticFactoryRejectsNonFiniteCoefficients()
    {
        using var blender = SKBlender.CreateArithmetic(.1f, .2f, .3f, .4f, enforcePMColor: true);
        Assert.NotNull(blender);
        Assert.NotEqual(IntPtr.Zero, blender.Handle);
        Assert.Null(SKBlender.CreateArithmetic(float.NaN, 0f, 0f, 0f, false));
        Assert.Null(SKBlender.CreateArithmetic(0f, float.PositiveInfinity, 0f, 0f, false));
        Assert.Null(SKBlender.CreateArithmetic(0f, 0f, float.NegativeInfinity, 0f, false));
        Assert.Null(SKBlender.CreateArithmetic(0f, 0f, 0f, float.NaN, false));
    }

    [Fact]
    public void PaintBlendModeAndBlenderShareNativeStateSemantics()
    {
        using var paint = new SKPaint();
        Assert.Null(paint.Blender);
        Assert.Equal(SKBlendMode.SrcOver, paint.BlendMode);

        paint.BlendMode = SKBlendMode.SrcOver;
        Assert.Null(paint.Blender);

        paint.BlendMode = SKBlendMode.Clear;
        Assert.NotNull(paint.Blender);
        Assert.Equal(SKBlendMode.Clear, paint.BlendMode);

        using var source = SKBlender.CreateBlendMode(SKBlendMode.Src);
        paint.Blender = source;
        Assert.Same(source, paint.Blender);
        Assert.Equal(SKBlendMode.Src, paint.BlendMode);

        using var arithmetic = SKBlender.CreateArithmetic(.1f, .2f, .3f, .4f, true);
        paint.Blender = arithmetic;
        Assert.Same(arithmetic, paint.Blender);
        Assert.Equal(SKBlendMode.SrcOver, paint.BlendMode);

        using var clone = paint.Clone();
        Assert.Same(arithmetic, clone.Blender);
        Assert.Equal(SKBlendMode.SrcOver, clone.BlendMode);

        paint.Reset();
        Assert.Null(paint.Blender);
        Assert.Equal(SKBlendMode.SrcOver, paint.BlendMode);
    }

    [Fact]
    public void ReusedPaintBlendModeStateIsAllocationFreeUntilBlenderIsObserved()
    {
        using var paint = new SKPaint();
        _ = CycleBlendModeState(paint, 1);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var checksum = CycleBlendModeState(paint, 10_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.NotEqual(0u, checksum);
        Assert.Equal(0, allocated);

        paint.BlendMode = SKBlendMode.DstIn;
        Assert.NotNull(paint.Blender);
        Assert.Equal(SKBlendMode.DstIn, paint.BlendMode);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => paint.BlendMode = (SKBlendMode)(-1));
    }

    private static uint CycleBlendModeState(SKPaint paint, int count)
    {
        var checksum = 2166136261u;
        for (var index = 0; index < count; index++)
        {
            paint.BlendMode = (index & 1) == 0
                ? SKBlendMode.DstIn
                : SKBlendMode.Src;
            checksum = (checksum ^ (uint)paint.BlendMode) * 16777619u;
            paint.Reset();
            checksum = (checksum ^ (uint)paint.BlendMode) * 16777619u;
        }

        return checksum;
    }

    [Fact]
    public void BuiltInBlenderRecordsExistingGpuBlendScope()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 20f, 20f));
        using var blender = SKBlender.CreateBlendMode(SKBlendMode.Modulate);
        using var paint = new SKPaint { Blender = blender };

        canvas.DrawRect(new SKRect(1f, 2f, 10f, 12f), paint);
        using var picture = recorder.EndRecording();

        Assert.Collection(
            picture.Picture.Commands,
            command =>
            {
                Assert.Equal(RenderCommandType.PushBlendMode, command.Type);
                Assert.Equal((int)GpuBlendMode.Modulate, command.IntParam);
            },
            command => Assert.Equal(RenderCommandType.DrawRect, command.Type),
            command => Assert.Equal(RenderCommandType.PopBlendMode, command.Type));
    }

    [Fact]
    public void ArithmeticBlenderFailsBeforeRecordingIncorrectPixels()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 20f, 20f));
        using var blender = SKBlender.CreateArithmetic(.1f, .2f, .3f, .4f, true);
        using var paint = new SKPaint { Blender = blender };

        var error = Assert.Throws<NotSupportedException>(
            () => canvas.DrawRect(new SKRect(1f, 2f, 10f, 12f), paint));
        Assert.Contains("destination-sampling", error.Message, StringComparison.Ordinal);

        using var picture = recorder.EndRecording();
        Assert.Empty(picture.Picture.Commands);
    }
}
