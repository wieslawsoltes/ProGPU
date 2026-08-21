using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeAnalyticPrimitiveReplayTests
{
    [Fact]
    public void UnequalRoundedRectanglePictureReplayRetainsLocalPenWithoutAllocation()
    {
        using var window = new HeadlessWindow(96, 64);
        using var target = new GpuTexture(
            window.Context,
            96,
            64,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Unequal rounded-rectangle replay target");
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        var pen = new Pen(
            new LinearGradientBrush(
                Vector2.Zero,
                new Vector2(96f, 0f),
                [
                    new GradientStop(Vector4.One, 0f),
                    new GradientStop(new Vector4(0.1f, 0.7f, 1f, 1f), 1f)
                ]),
            thickness: 3f,
            lineJoin: PenLineJoin.Round);
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(new Vector4(0.1f, 0.2f, 0.35f, 1f)),
            pen,
            new Rect(8f, 8f, 80f, 48f),
            radiusX: 15f,
            radiusY: 7f);
        using GpuPicture picture = recorder.EndRecording();
        var visual = new DrawingVisual { Size = new Vector2(96f, 64f) };
        visual.Context.DrawPicture(picture);

        for (int index = 0; index < 32; index++)
        {
            Render();
        }

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 64; index++)
        {
            Render();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);

        void Render()
        {
            window.Compositor.RenderOffscreen(
                visual,
                width: 96,
                height: 64,
                targetTexture: target,
                padding: 0f,
                dpiScale: 1f);
            window.Context.PollDevice(wait: true);
        }
    }
}
