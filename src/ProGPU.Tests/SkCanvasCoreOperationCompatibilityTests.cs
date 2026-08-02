using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasCoreOperationCompatibilityTests
{
    [Fact]
    public void ColorFClearUsesClampedDeviceSpaceSourceColor()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 20f, 20f));

        canvas.Clear(new SKColorF(-1f, 0.25f, 2f, 0.5f));
        using var picture = recorder.EndRecording();

        Assert.Collection(
            picture.Picture.Commands,
            command => Assert.Equal(RenderCommandType.PushBlendMode, command.Type),
            command =>
            {
                Assert.Equal(RenderCommandType.DrawRect, command.Type);
                var brush = Assert.IsType<SolidColorBrush>(command.Brush);
                Assert.Equal(new Vector4(0f, 0.25f, 1f, 0.5f), brush.Color);
                Assert.Equal(Matrix4x4.Identity, command.Transform);
            },
            command => Assert.Equal(RenderCommandType.PopBlendMode, command.Type));
    }

    [Fact]
    public void DrawColorUsesRequestedBlendModeWithoutCanvasTransform()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 20f, 20f));
        canvas.Translate(7f, 9f);

        canvas.DrawColor(SKColors.Red, SKBlendMode.Multiply);
        using var picture = recorder.EndRecording();

        Assert.Collection(
            picture.Picture.Commands,
            command => Assert.Equal(RenderCommandType.PushBlendMode, command.Type),
            command =>
            {
                Assert.Equal(RenderCommandType.DrawRect, command.Type);
                Assert.Equal(Matrix4x4.Identity, command.Transform);
            },
            command => Assert.Equal(RenderCommandType.PopBlendMode, command.Type));
    }

    [Fact]
    public void ArcCenterFlagControlsContourClosure()
    {
        using var paint = new SKPaint { Style = SKPaintStyle.Stroke };
        using var openRecorder = new SKPictureRecorder();
        var openCanvas = openRecorder.BeginRecording(new SKRect(0f, 0f, 30f, 30f));
        openCanvas.DrawArc(new SKRect(2f, 4f, 22f, 16f), 0f, 90f, false, paint);
        using var openPicture = openRecorder.EndRecording();
        var openPath = Assert.Single(openPicture.Picture.Commands).Path!;

        using var wedgeRecorder = new SKPictureRecorder();
        var wedgeCanvas = wedgeRecorder.BeginRecording(new SKRect(0f, 0f, 30f, 30f));
        wedgeCanvas.DrawArc(new SKRect(2f, 4f, 22f, 16f), 0f, 90f, true, paint);
        using var wedgePicture = wedgeRecorder.EndRecording();
        var wedgePath = Assert.Single(wedgePicture.Picture.Commands).Path!;

        Assert.True(openPath.TryGetBounds(out var openMin, out var openMax));
        Assert.True(wedgePath.TryGetBounds(out var wedgeMin, out var wedgeMax));
        Assert.True(wedgeMin.X <= openMin.X);
        Assert.True(wedgeMax.Y >= openMax.Y);
    }

    [Fact]
    public void FullArcRecordsOneOvalPath()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 30f, 30f));
        using var paint = new SKPaint();

        canvas.DrawArc(new SKRect(2f, 4f, 22f, 16f), 15f, 720f, false, paint);
        using var picture = recorder.EndRecording();

        var command = Assert.Single(picture.Picture.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.True(command.Path!.TryGetBounds(out var min, out var max));
        Assert.Equal(new Vector2(2f, 4f), min);
        Assert.Equal(new Vector2(22f, 16f), max);
    }

    [Fact]
    public void DiscardIsRetainedNoOpAndTotalMatrixIsReadOnly()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 20f, 20f));
        canvas.Translate(3f, 5f);
        var before = canvas.TotalMatrix;

        canvas.Discard();
        Assert.Equal(before, canvas.TotalMatrix);
        Assert.False(typeof(SKCanvas).GetProperty(nameof(SKCanvas.TotalMatrix))!.CanWrite);
        using var picture = recorder.EndRecording();
        Assert.Empty(picture.Picture.Commands);
    }

    [Fact]
    public void EmptyClipScopeIsElidedFromTheRetainedPicture()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 20f, 20f));

        int restoreCount = canvas.Save();
        canvas.ClipRect(new SKRect(2f, 3f, 18f, 17f));
        canvas.RestoreToCount(restoreCount);

        using var picture = recorder.EndRecording();
        Assert.Empty(picture.Picture.Commands);
    }

    [Fact]
    public void ClipScopeWithDrawingRetainsBalancedCommands()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 20f, 20f));
        using var paint = new SKPaint { Color = SKColors.Red };

        int restoreCount = canvas.Save();
        canvas.ClipRect(new SKRect(2f, 3f, 18f, 17f));
        canvas.DrawRect(new SKRect(0f, 0f, 20f, 20f), paint);
        canvas.RestoreToCount(restoreCount);

        using var picture = recorder.EndRecording();
        Assert.Collection(
            picture.Picture.Commands,
            command => Assert.Equal(RenderCommandType.PushClip, command.Type),
            command => Assert.Equal(RenderCommandType.DrawRect, command.Type),
            command => Assert.Equal(RenderCommandType.PopClip, command.Type));
    }

    [Fact]
    public void EmptyClipStateCyclesAllocateNothingAfterWarmup()
    {
        using var canvas = new SKCanvas(new DrawingContext(), 20f, 20f);
        var clip = new SKRect(2f, 3f, 18f, 17f);

        CycleEmptyClip(canvas, clip);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100_000; index++)
        {
            CycleEmptyClip(canvas, clip);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Empty(canvas.DrawingContext.Commands);
    }

    [Fact]
    public void RetainedCanvasStateInitializationAvoidsDuplicateClipCommandStorage()
    {
        const int iterations = 128;
        for (var index = 0; index < 8; index++)
        {
            RecordOneEmptyClipCycle();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            RecordOneEmptyClipCycle();
        }

        long bytesPerCycle =
            (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        Assert.True(
            bytesPerCycle <= 6_000,
            $"Expected no more than 6,000 managed bytes per retained canvas cycle, actual: {bytesPerCycle}.");
    }

    private static void CycleEmptyClip(SKCanvas canvas, SKRect clip)
    {
        int restoreCount = canvas.Save();
        canvas.ClipRect(clip);
        canvas.RestoreToCount(restoreCount);
    }

    private static void RecordOneEmptyClipCycle()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 20f, 20f));
        CycleEmptyClip(canvas, new SKRect(2f, 3f, 18f, 17f));
        using var picture = recorder.EndRecording();
        GC.KeepAlive(picture);
    }
}
