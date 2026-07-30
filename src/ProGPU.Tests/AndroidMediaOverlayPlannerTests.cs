using ProGPU.Android.Media;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using Xunit;

namespace ProGPU.Tests;

public sealed class AndroidMediaOverlayPlannerTests
{
    [Fact]
    public void PlannerPreservesDeclaredOrderTimingAndPlacement()
    {
        MediaCompositionExportRequest request =
            CreateRequest() with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        CreateOverlay(
                            new Uri(
                                "file:///media/lower.mp4"),
                            TimeSpan.FromSeconds(2),
                            -64d,
                            72d,
                            640d,
                            360d,
                            0.75d)
                    ]),
                    new MediaCompositionExportOverlayLayer(
                    [
                        new MediaCompositionExportOverlay(
                            CreateColorClip(
                                0x80ff0000),
                            TimeSpan.FromSeconds(1),
                            0d,
                            0d,
                            320d,
                            180d,
                            0.5d,
                            AudioEnabled: false)
                    ])
                ]
            };

        Assert.True(
            AndroidMediaCodecOverlayPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                out AndroidMediaCodecOverlayPlan[] plans));
        Assert.Equal(2, plans.Length);

        AndroidMediaCodecOverlayPlan lower =
            plans[0];
        Assert.EndsWith(
            "lower.mp4",
            lower.Clip.SourceUri!.AbsolutePath,
            StringComparison.Ordinal);
        Assert.Equal(
            2_000_000,
            lower.StartMicroseconds);
        Assert.Equal(
            7_000_000,
            lower.EndMicroseconds);
        Assert.Equal(
            1_000_000,
            lower.SourceStartMicroseconds);
        Assert.Equal(
            6_000_000,
            lower.SourceEndMicroseconds);
        Assert.Equal(
            -0.05f,
            lower.Placement.X,
            5);
        Assert.Equal(
            0.1f,
            lower.Placement.Y,
            5);
        Assert.Equal(
            0.5f,
            lower.Placement.Width);
        Assert.Equal(
            0.5f,
            lower.Placement.Height);
        Assert.Equal(
            0.75f,
            lower.Placement.Opacity);
        Assert.True(lower.AudioEnabled);
        Assert.False(
            lower.TryResolve(
                1_999_999,
                out _));
        Assert.True(
            lower.TryResolve(
                2_000_000,
                out long sourceStart));
        Assert.Equal(
            1_000_000,
            sourceStart);
        Assert.True(
            lower.TryResolve(
                6_999_999,
                out long sourceEnd));
        Assert.Equal(
            5_999_999,
            sourceEnd);
        Assert.False(
            lower.TryResolve(
                7_000_000,
                out _));

        Assert.Equal(
            0x80ff0000u,
            plans[1].Clip.ArgbColor);
        Assert.False(plans[1].AudioEnabled);
    }

    [Fact]
    public void PlannerRejectsCustomCompositorDefinition()
    {
        MediaCompositionExportRequest request =
            CreateRequest() with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        CreateOverlay(
                            new Uri(
                                "file:///media/overlay.mp4"),
                            TimeSpan.Zero,
                            0d,
                            0d,
                            320d,
                            180d,
                            1d)
                    ])
                    {
                        CustomCompositorDefinition =
                            new MediaCompositionEffectDefinition(
                                "example.custom.compositor",
                                new Dictionary<string, object?>())
                    }
                ]
            };

        Assert.False(
            AndroidMediaCodecOverlayPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                out AndroidMediaCodecOverlayPlan[] plans));
        Assert.Empty(plans);
    }

    [Fact]
    public void ResolvedTimelineHotPathAllocatesNothing()
    {
        MediaCompositionExportRequest request =
            CreateRequest() with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        CreateOverlay(
                            new Uri(
                                "file:///media/overlay.mp4"),
                            TimeSpan.FromSeconds(1),
                            0d,
                            0d,
                            320d,
                            180d,
                            1d)
                    ])
                ]
            };
        Assert.True(
            AndroidMediaCodecOverlayPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                out AndroidMediaCodecOverlayPlan[] plans));
        AndroidMediaCodecOverlayPlan plan =
            Assert.Single(plans);
        Assert.True(
            plan.TryResolve(
                1_000_000,
                out _));

        long checksum = 0;
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0;
             index < 100_000;
             index++)
        {
            if (plan.TryResolve(
                    1_000_000 + index % 4_000_000,
                    out long source))
            {
                checksum += source;
            }
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        Assert.NotEqual(0, checksum);
        Assert.Equal(0, allocated);
    }

    private static MediaCompositionExportOverlay
        CreateOverlay(
        Uri source,
        TimeSpan delay,
        double x,
        double y,
        double width,
        double height,
        double opacity) =>
        new(
            new MediaCompositionExportClip(
                source,
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                1d,
                null,
                new Dictionary<string, string>()),
            delay,
            x,
            y,
            width,
            height,
            opacity,
            AudioEnabled: true);

    private static MediaCompositionExportClip
        CreateColorClip(
        uint color) =>
        new(
            null,
            TimeSpan.FromSeconds(3),
            TimeSpan.Zero,
            TimeSpan.Zero,
            1d,
            color,
            new Dictionary<string, string>());

    private static MediaCompositionExportRequest
        CreateRequest()
    {
        var clip =
            new MediaCompositionExportClip(
                new Uri(
                    "file:///media/main.mp4"),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                1d,
                null,
                new Dictionary<string, string>());
        var profile =
            new MediaCompositionEncodingProfile(
                "MPEG4",
                "H264",
                "AAC",
                1_280,
                720,
                8_000_000,
                30,
                1,
                192_000,
                48_000,
                2);
        return new MediaCompositionExportRequest(
            Path.Combine(
                Path.GetTempPath(),
                "progpu-android-overlay.mp4"),
            [clip],
            MediaCompositionTrimmingMode.Precise,
            profile,
            new Dictionary<string, string>());
    }
}
