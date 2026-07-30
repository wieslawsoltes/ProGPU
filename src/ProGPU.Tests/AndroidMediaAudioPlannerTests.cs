using ProGPU.Android.Media;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using Xunit;

namespace ProGPU.Tests;

public sealed class AndroidMediaAudioPlannerTests
{
    [Fact]
    public void PlannerPreservesMainAndSignedBackgroundTiming()
    {
        MediaCompositionExportRequest request =
            CreateRequest() with
            {
                BackgroundAudioTracks =
                [
                    new MediaCompositionExportAudioTrack(
                        new Uri(
                            "file:///media/early.m4a"),
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(-2),
                        0.5d,
                        new Dictionary<string, string>()),
                    new MediaCompositionExportAudioTrack(
                        new Uri(
                            "file:///media/delayed.m4a"),
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(2),
                        1d,
                        new Dictionary<string, string>())
                ]
            };

        Assert.True(
            AndroidMediaCodecAudioPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                out AndroidMediaCodecAudioPlan[] plans,
                out long compositionFrames));
        Assert.Equal(336_000, compositionFrames);
        Assert.Equal(3, plans.Length);

        AndroidMediaCodecAudioPlan main =
            Assert.Single(
                plans,
                plan =>
                    plan.SourceUri.AbsolutePath
                        .EndsWith(
                            "input.mp4",
                            StringComparison.Ordinal));
        Assert.Equal(1_000_000, main.SourceStartMicroseconds);
        Assert.Equal(8_000_000, main.SourceEndMicroseconds);
        Assert.Equal(0, main.DestinationStartFrame);
        Assert.Equal(336_000, main.DestinationEndFrame);

        AndroidMediaCodecAudioPlan early =
            Assert.Single(
                plans,
                plan =>
                    plan.SourceUri.AbsolutePath
                        .EndsWith(
                            "early.m4a",
                            StringComparison.Ordinal));
        Assert.Equal(3_000_000, early.SourceStartMicroseconds);
        Assert.Equal(8_000_000, early.SourceEndMicroseconds);
        Assert.Equal(0, early.DestinationStartFrame);
        Assert.Equal(240_000, early.DestinationEndFrame);
        Assert.Equal(16_384, early.Levels.Left);
        Assert.Equal(16_384, early.Levels.Right);

        AndroidMediaCodecAudioPlan delayed =
            Assert.Single(
                plans,
                plan =>
                    plan.SourceUri.AbsolutePath
                        .EndsWith(
                            "delayed.m4a",
                            StringComparison.Ordinal));
        Assert.Equal(1_000_000, delayed.SourceStartMicroseconds);
        Assert.Equal(6_000_000, delayed.SourceEndMicroseconds);
        Assert.Equal(96_000, delayed.DestinationStartFrame);
        Assert.Equal(336_000, delayed.DestinationEndFrame);
    }

    [Fact]
    public void PlannerClipsMutedAndOutOfRangeBackgrounds()
    {
        MediaCompositionExportRequest request =
            CreateRequest() with
            {
                BackgroundAudioTracks =
                [
                    new MediaCompositionExportAudioTrack(
                        new Uri(
                            "file:///media/muted.m4a"),
                        TimeSpan.FromSeconds(3),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        0d,
                        new Dictionary<string, string>()),
                    new MediaCompositionExportAudioTrack(
                        new Uri(
                            "file:///media/late.m4a"),
                        TimeSpan.FromSeconds(3),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(8),
                        1d,
                        new Dictionary<string, string>())
                ]
            };

        Assert.True(
            AndroidMediaCodecAudioPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                out AndroidMediaCodecAudioPlan[] plans,
                out long compositionFrames));
        Assert.Equal(336_000, compositionFrames);
        Assert.Single(plans);
        Assert.EndsWith(
            "input.mp4",
            plans[0].SourceUri.AbsolutePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerIncludesOnlyAudioEnabledUriOverlays()
    {
        var audibleClip =
            new MediaCompositionExportClip(
                new Uri(
                    "file:///media/overlay.mp4"),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                0.25d,
                null,
                new Dictionary<string, string>());
        var mutedClip =
            audibleClip with
            {
                SourceUri =
                    new Uri(
                        "file:///media/muted-overlay.mp4")
            };
        MediaCompositionExportRequest request =
            CreateRequest() with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        new MediaCompositionExportOverlay(
                            audibleClip,
                            TimeSpan.FromSeconds(1.5),
                            10d,
                            20d,
                            320d,
                            180d,
                            0.75d,
                            AudioEnabled: true),
                        new MediaCompositionExportOverlay(
                            mutedClip,
                            TimeSpan.Zero,
                            20d,
                            30d,
                            320d,
                            180d,
                            1d,
                            AudioEnabled: false)
                    ])
                ]
            };

        Assert.True(
            AndroidMediaCodecAudioPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                out AndroidMediaCodecAudioPlan[] plans,
                out long compositionFrames));
        Assert.Equal(336_000, compositionFrames);
        Assert.Equal(2, plans.Length);

        AndroidMediaCodecAudioPlan overlay =
            Assert.Single(
                plans,
                plan =>
                    plan.SourceUri.AbsolutePath
                        .EndsWith(
                            "overlay.mp4",
                            StringComparison.Ordinal));
        Assert.Equal(
            1_000_000,
            overlay.SourceStartMicroseconds);
        Assert.Equal(
            6_000_000,
            overlay.SourceEndMicroseconds);
        Assert.Equal(
            72_000,
            overlay.DestinationStartFrame);
        Assert.Equal(
            312_000,
            overlay.DestinationEndFrame);
        Assert.Equal(8_192, overlay.Levels.Left);
        Assert.Equal(8_192, overlay.Levels.Right);
        Assert.DoesNotContain(
            plans,
            plan =>
                plan.SourceUri.AbsolutePath
                    .EndsWith(
                        "muted-overlay.mp4",
                        StringComparison.Ordinal));
    }

    [Fact]
    public void WideMixerSaturatesOnceAndIsOrderIndependent()
    {
        var full =
            new AndroidPcm16MixLevels(
                32_768,
                32_768);
        short[] first =
            [30_000, -30_000, 10_000, -10_000];
        short[] second =
            [20_000, -20_000, -30_000, 30_000];
        Span<long> forward =
            stackalloc long[4];
        Span<long> reverse =
            stackalloc long[4];
        AndroidPcm16Mixer.Add(
            first,
            2,
            full,
            forward,
            0);
        AndroidPcm16Mixer.Add(
            second,
            2,
            full,
            forward,
            0);
        AndroidPcm16Mixer.Add(
            second,
            2,
            full,
            reverse,
            0);
        AndroidPcm16Mixer.Add(
            first,
            2,
            full,
            reverse,
            0);

        Span<short> forwardOutput =
            stackalloc short[4];
        Span<short> reverseOutput =
            stackalloc short[4];
        AndroidPcm16Mixer.WriteSaturated(
            forward,
            forwardOutput);
        AndroidPcm16Mixer.WriteSaturated(
            reverse,
            reverseOutput);

        Assert.True(
            forwardOutput.SequenceEqual(
                reverseOutput));
        Assert.True(
            forwardOutput.SequenceEqual(
                new short[]
                {
                    short.MaxValue,
                    short.MinValue,
                    -20_000,
                    20_000
                }));
    }

    [Fact]
    public void WideMixerHotKernelAllocatesNothing()
    {
        var levels =
            new AndroidPcm16MixLevels(
                24_576,
                16_384);
        Span<short> source =
            stackalloc short[8];
        Span<long> accumulator =
            stackalloc long[8];
        Span<short> output =
            stackalloc short[8];
        source.Fill(1_000);
        AndroidPcm16Mixer.Add(
            source,
            2,
            levels,
            accumulator,
            0);
        AndroidPcm16Mixer.WriteSaturated(
            accumulator,
            output);
        accumulator.Clear();

        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0;
             index < 100_000;
             index++)
        {
            AndroidPcm16Mixer.Add(
                source,
                2,
                levels,
                accumulator,
                0);
            AndroidPcm16Mixer.WriteSaturated(
                accumulator,
                output);
            accumulator.Clear();
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        Assert.Equal(0, allocated);
    }

    private static MediaCompositionExportRequest
        CreateRequest()
    {
        var clip =
            new MediaCompositionExportClip(
                new Uri(
                    "file:///media/input.mp4"),
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
                "progpu-android-audio.mp4"),
            [clip],
            MediaCompositionTrimmingMode.Precise,
            profile,
            new Dictionary<string, string>());
    }
}
