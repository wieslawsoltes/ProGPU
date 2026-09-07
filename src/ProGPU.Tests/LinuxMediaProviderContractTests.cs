using Xunit;
using ProGPU.Linux.Media;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Backend;
using ProGPU.Browser;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Containers;
using ProGPU.Media.Effects;
using ProGPU.Media.Playback;
using Windows.Media.Editing;
using Windows.Storage;

namespace ProGPU.Tests;

public sealed class LinuxMediaProviderContractTests
{
    [Fact]
    public void LinuxColorOverlayPlanPreservesWinUiOrderAndTiming()
    {
        MediaCompositionExportRequest baseline =
            CreateLinuxPreciseRequest(
                [
                    new MediaCompositionExportClip(
                        null,
                        TimeSpan.FromSeconds(8),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        0xFF101820u,
                        new Dictionary<string, string>())
                ]);
        MediaCompositionExportRequest request =
            baseline with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        CreateLinuxColorOverlay(
                            0x80FF0000u,
                            TimeSpan.FromSeconds(2),
                            -32d,
                            18d,
                            160d,
                            90d,
                            0.75d),
                        CreateLinuxColorOverlay(
                            0xFF00FF00u,
                            TimeSpan.FromSeconds(1),
                            0d,
                            0d,
                            80d,
                            45d,
                            0.5d)
                    ])
                ]
            };

        Assert.True(
            LinuxMediaOverlayPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                out LinuxMediaOverlayPlan[] plans));
        Assert.Equal(2, plans.Length);
        Assert.Equal(0x80FF0000u, plans[0].ArgbColor);
        Assert.Equal(
            TimeSpan.FromSeconds(2).Ticks,
            plans[0].StartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(5).Ticks,
            plans[0].EndTicks);
        Assert.Equal(-0.1f, plans[0].Placement.X, 5);
        Assert.Equal(0.1f, plans[0].Placement.Y, 5);
        Assert.Equal(0.5f, plans[0].Placement.Width);
        Assert.Equal(0.5f, plans[0].Placement.Height);
        Assert.Equal(0.75f, plans[0].Placement.Opacity);
        Assert.False(
            plans[0].IsActive(
                TimeSpan.FromSeconds(2).Ticks - 1));
        Assert.True(
            plans[0].IsActive(
                TimeSpan.FromSeconds(2).Ticks));
        Assert.False(
            plans[0].IsActive(
                TimeSpan.FromSeconds(5).Ticks));

        Assert.True(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: false));
        Assert.Equal(
            MediaCompositionExportVideoPath.GpuCopy,
            LinuxV4l2PreciseMediaCompositionExportProvider
                .GetCapabilitiesForRequest(request)
                .VideoPath);
    }

    [Fact]
    public void LinuxOverlayPlanAcceptsLocalUriAndRejectsUnsupportedContracts()
    {
        MediaCompositionExportRequest baseline =
            CreateLinuxPreciseRequest(
                [
                    new MediaCompositionExportClip(
                        null,
                        TimeSpan.FromSeconds(4),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        0xFF000000u,
                        new Dictionary<string, string>())
                ]);
        MediaCompositionExportOverlay uriOverlay =
            new(
                new MediaCompositionExportClip(
                    new Uri("file:///tmp/overlay.mp4"),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(250),
                    1d,
                    null,
                    new Dictionary<string, string>()),
                TimeSpan.FromSeconds(1),
                0d,
                0d,
                160d,
                90d,
                1d,
                AudioEnabled: false);
        MediaCompositionExportRequest uriRequest =
            baseline with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                        [uriOverlay])
                ]
            };
        Assert.True(
            LinuxMediaOverlayPlanner.TryCapture(
                uriRequest,
                MediaEffectRegistry.Default,
                out LinuxMediaOverlayPlan[]
                    uriPlans));
        LinuxMediaOverlayPlan uriPlan =
            Assert.Single(uriPlans);
        Assert.True(uriPlan.IsUri);
        Assert.Equal(
            new Uri("file:///tmp/overlay.mp4"),
            uriPlan.SourceUri);
        Assert.Null(uriPlan.ArgbColor);
        Assert.Equal(
            TimeSpan.FromMilliseconds(250).Ticks,
            uriPlan.SourceStartTicks);
        Assert.Equal(
            TimeSpan.FromMilliseconds(250).Ticks,
            uriPlan.SourceEndTrimTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(1).Ticks,
            uriPlan.StartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(2.5).Ticks,
            uriPlan.EndTicks);
        Assert.Equal(
            TimeSpan.FromMilliseconds(750).Ticks,
            uriPlan.GetSourceTicks(
                TimeSpan.FromSeconds(1.5)
                    .Ticks));
        Assert.True(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    uriRequest,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    uriRequest,
                    isLinux: true,
                    hasNativeH264Path: false,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));

        MediaCompositionExportRequest audioRequest =
            uriRequest with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        uriOverlay with
                        {
                            AudioEnabled = true
                        }
                    ])
                ]
            };
        Assert.False(
            LinuxMediaOverlayPlanner.TryCapture(
                audioRequest,
                MediaEffectRegistry.Default,
                out _));

        MediaCompositionExportRequest customRequest =
            baseline with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                        [
                            CreateLinuxColorOverlay(
                                0xFFFFFFFFu,
                                TimeSpan.Zero,
                                0d,
                                0d,
                                160d,
                                90d,
                                1d)
                        ])
                    {
                        CustomCompositorDefinition =
                            new MediaCompositionEffectDefinition(
                                "test.custom",
                                new Dictionary<string, object?>())
                    }
                ]
            };
        Assert.False(
            LinuxMediaOverlayPlanner.TryCapture(
                customRequest,
                MediaEffectRegistry.Default,
                out _));
    }

    [Fact]
    public void LinuxOverlayTimelineHotPathAllocatesNothing()
    {
        var plan =
            new LinuxMediaOverlayPlan(
                null,
                0xFFFFFFFFu,
                0,
                0,
                10,
                100,
                new GpuTextureLayerPlacement(
                    0f,
                    0f,
                    1f,
                    1f,
                    1f),
                LinuxGpuVideoEffectPlan.Identity);
        var uriPlan =
            new LinuxMediaOverlayPlan(
                new Uri("file:///tmp/overlay.mp4"),
                null,
                1_000,
                0,
                10,
                100,
                new GpuTextureLayerPlacement(
                    0f,
                    0f,
                    1f,
                    1f,
                    1f),
                LinuxGpuVideoEffectPlan.Identity);
        long checksum = 0;
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0;
             index < 100_000;
             index++)
        {
            if (plan.IsActive(index % 120))
            {
                checksum++;
            }
            long ticks = index % 120;
            if (uriPlan.IsActive(ticks))
            {
                checksum +=
                    uriPlan.GetSourceTicks(ticks);
            }
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        Assert.NotEqual(0, checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void LinuxUriOverlayFrameSelectionUsesHalfOpenTrimAndLookAhead()
    {
        const long trimStart = 100;
        const long trimEnd = 500;
        const long target = 300;

        Assert.Equal(
            target,
            LinuxMediaOverlayFrameSelector
                .GetInitialDecodeTicks(
                    trimStart,
                    target));
        Assert.Equal(
            LinuxMediaOverlayFrameDisposition
                .Discard,
            LinuxMediaOverlayFrameSelector
                .Classify(
                    99,
                    trimStart,
                    trimEnd,
                    target));
        Assert.Equal(
            LinuxMediaOverlayFrameDisposition
                .Candidate,
            LinuxMediaOverlayFrameSelector
                .Classify(
                    100,
                    trimStart,
                    trimEnd,
                    target));
        Assert.Equal(
            LinuxMediaOverlayFrameDisposition
                .Candidate,
            LinuxMediaOverlayFrameSelector
                .Classify(
                    300,
                    trimStart,
                    trimEnd,
                    target));
        Assert.Equal(
            LinuxMediaOverlayFrameDisposition
                .LookAhead,
            LinuxMediaOverlayFrameSelector
                .Classify(
                    301,
                    trimStart,
                    trimEnd,
                    target));
        Assert.Equal(
            LinuxMediaOverlayFrameDisposition
                .Discard,
            LinuxMediaOverlayFrameSelector
                .Classify(
                    500,
                    trimStart,
                    trimEnd,
                    target));
    }

    [Fact]
    public void LinuxThumbnailWorkOrderIsStableAfterKeyFrameNormalization()
    {
        Assert.True(
            LinuxMediaThumbnailWorkOrder.Compare(
                100,
                300,
                2,
                200,
                100,
                0) < 0);
        Assert.True(
            LinuxMediaThumbnailWorkOrder.Compare(
                100,
                200,
                2,
                100,
                300,
                0) < 0);
        Assert.True(
            LinuxMediaThumbnailWorkOrder.Compare(
                100,
                200,
                1,
                100,
                200,
                2) < 0);

        _ = LinuxMediaThumbnailWorkOrder.Compare(
            100,
            200,
            1,
            100,
            200,
            2);
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0;
             index < 100_000;
             index++)
        {
            checksum +=
                LinuxMediaThumbnailWorkOrder.Compare(
                    index,
                    index + 1L,
                    index,
                    index + 1L,
                    index + 2L,
                    index + 1);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        Assert.Equal(-100_000, checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void LinuxPreciseCapabilityAcceptsOrderedUriAndColorClips()
    {
        MediaCompositionExportRequest request =
            CreateLinuxPreciseRequest(
                [
                    new MediaCompositionExportClip(
                        new Uri(
                            "file:///tmp/first.mp4"),
                        TimeSpan.FromSeconds(3),
                        TimeSpan.FromMilliseconds(250),
                        TimeSpan.FromMilliseconds(500),
                        1d,
                        null,
                        new Dictionary<string, string>()),
                    new MediaCompositionExportClip(
                        null,
                        TimeSpan.FromSeconds(2),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        0xFF7C3AEDu,
                        new Dictionary<string, string>
                        {
                            ["progpu.grayscale"] =
                                "0.5"
                        })
                ]);

        Assert.True(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: false));
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        false,
                    gpuAvailable: true));
    }

    [Fact]
    public void LinuxPreciseCapabilityAcceptsRegisteredGaussianPlan()
    {
        const string effectId =
            "ProGPU.Tests.Linux.Gaussian";
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new MediaVideoGaussianBlurEffectFactory(
                    effectId));
        var clip =
            new MediaCompositionExportClip(
                new Uri("file:///tmp/source.mp4"),
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                TimeSpan.Zero,
                1d,
                null,
                new Dictionary<string, string>())
            {
                VideoEffectDefinitions =
                [
                    new MediaCompositionEffectDefinition(
                        effectId,
                        new Dictionary<string, object?>
                        {
                            [
                                MediaVideoGaussianBlurEffectFactory
                                    .StandardDeviationPropertyName
                            ] = 5d
                        })
                ]
            };
        MediaCompositionExportRequest request =
            CreateLinuxPreciseRequest([clip]);

        Assert.True(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true,
                    effectRegistry: registry));
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: false,
                    effectRegistry: registry));
        MediaCompositionExportCapabilities capabilities =
            LinuxV4l2PreciseMediaCompositionExportProvider
                .GetCapabilitiesForRequest(
                    request,
                    registry);
        Assert.Equal(
            MediaCompositionExportVideoPath.GpuCopy,
            capabilities.VideoPath);
        Assert.True(capabilities.EffectsBakedOnGpu);
    }

    [Fact]
    public void LinuxPreciseCapabilityRejectsInvalidTimelineClip()
    {
        MediaCompositionExportRequest request =
            CreateLinuxPreciseRequest(
                [
                    new MediaCompositionExportClip(
                        new Uri(
                            "file:///tmp/first.mp4"),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.Zero,
                        1d,
                        null,
                        new Dictionary<string, string>()),
                    new MediaCompositionExportClip(
                        null,
                        TimeSpan.FromSeconds(1),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        0xFF000000u,
                        new Dictionary<string, string>())
                ]);

        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));
    }

    [Fact]
    public void LinuxPreciseScalingSelectsGpuComposition()
    {
        var scaledClip =
            new MediaCompositionExportClip(
                new Uri("file:///tmp/source.mp4"),
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                TimeSpan.Zero,
                1d,
                null,
                new Dictionary<string, string>())
            {
                SourceVideoWidth = 640,
                SourceVideoHeight = 360
            };
        MediaCompositionExportRequest request =
            CreateLinuxPreciseRequest([scaledClip]);

        Assert.True(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        false,
                    gpuAvailable: true));
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: false));
    }

    [Fact]
    public void LinuxPreciseCapabilityAcceptsMatchingCompressedAac()
    {
        var clip =
            new MediaCompositionExportClip(
                new Uri("file:///tmp/source.mp4"),
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                TimeSpan.Zero,
                1d,
                null,
                new Dictionary<string, string>())
            {
                SourceAudioSubtype = "AAC",
                SourceAudioBitrate = 128_000,
                SourceAudioSampleRate = 48_000,
                SourceAudioChannelCount = 2
            };
        MediaCompositionExportRequest baseline =
            CreateLinuxPreciseRequest([clip]);
        MediaCompositionExportRequest request =
            baseline with
            {
                EncodingProfile =
                    baseline.EncodingProfile with
                    {
                        AudioSubtype = "AAC",
                        AudioBitrate = 128_000,
                        AudioSampleRate = 48_000,
                        AudioChannelCount = 2
                    }
            };

        Assert.True(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));
        MediaCompositionExportCapabilities capabilities =
            LinuxV4l2PreciseMediaCompositionExportProvider
                .GetCapabilitiesForRequest(request);
        Assert.Equal(
            MediaCompositionExportVideoPath.NativeGpuSurface,
            capabilities.VideoPath);
        Assert.Equal(
            MediaCompositionExportAudioPath
                .CompressedSampleCopy,
            capabilities.AudioPath);
        Assert.False(
            capabilities.EffectsBakedOnGpu);

        MediaCompositionExportRequest mismatch =
            request with
            {
                EncodingProfile =
                    request.EncodingProfile with
                    {
                        AudioSampleRate = 44_100
                    }
            };
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    mismatch,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));
    }

    [Fact]
    public void PreciseAacPlannerHonorsPreCanceledRequest()
    {
        MediaCompositionExportRequest baseline =
            CreateLinuxPreciseRequest(
                [
                    new MediaCompositionExportClip(
                        new Uri("file:///tmp/source.mp4"),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        null,
                        new Dictionary<string, string>())
                ]);
        MediaCompositionExportRequest request =
            baseline with
            {
                EncodingProfile =
                    baseline.EncodingProfile with
                    {
                        AudioSubtype = "AAC",
                        AudioBitrate = 128_000,
                        AudioSampleRate = 48_000,
                        AudioChannelCount = 2
                    }
            };
        using var cancellation =
            new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () =>
                IsoBmffPreciseAacTimelinePlanner
                    .Create(
                        request,
                        cancellation.Token));
    }

    [Fact]
    public void LinuxTimelineTimestampComposesTrimAndClipOffset()
    {
        TimeSpan timestamp =
            LinuxV4l2PreciseMediaCompositionExportProvider
                .GetTimelinePresentationTime(
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromMilliseconds(1250),
                    TimeSpan.FromMilliseconds(250));

        Assert.Equal(
            TimeSpan.FromSeconds(4),
            timestamp);
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                LinuxV4l2PreciseMediaCompositionExportProvider
                    .GetTimelinePresentationTime(
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(249),
                        TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void PortableMetadataReaderUsesContainerTablesOnly()
    {
        using var stream =
            new MemoryStream(BuildSyntheticH264Movie());

        MediaFileMetadata metadata =
            MediaFileMetadataReader.ReadIsoBmff(stream);

        MediaVideoStreamMetadata video =
            Assert.Single(metadata.VideoStreams);
        Assert.Empty(metadata.AudioStreams);
        Assert.Equal("H264", video.Subtype);
        Assert.Equal(1_920u, video.Width);
        Assert.Equal(1_080u, video.Height);
        Assert.Equal(TimeSpan.FromSeconds(2), video.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), metadata.Duration);
        Assert.Equal(1u, video.FrameRateNumerator);
        Assert.Equal(1u, video.FrameRateDenominator);
        Assert.Equal(48u, video.Bitrate);
    }

    [Fact]
    public async Task OfficialClipFactoryPopulatesPortableMetadata()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"progpu-metadata-{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllBytesAsync(
                path,
                BuildSyntheticH264Movie());

            MediaClip clip =
                await MediaClip.CreateFromFileAsync(
                    new StorageFile(path));

            Assert.Equal(
                TimeSpan.FromSeconds(2),
                clip.OriginalDuration);
            Assert.Equal(
                1_920u,
                clip.GetVideoEncodingProperties()
                    .Width);
            Assert.Equal(
                1u,
                clip.GetVideoEncodingProperties()
                    .FrameRate.Numerator);
            Assert.Empty(clip.EmbeddedAudioTracks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LinuxCapabilityProbeUsesKernelAndPipeWireApisOnly()
    {
        string source = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxNativeMediaCapabilities.cs");
        string project = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "ProGPU.Linux.Media.csproj");

        Assert.Contains(
            "VideoQueryCapabilities = 0x8068_5600",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VideoEnumerateFormat = 0xC040_5602",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "libpipewire-0.3.so.0",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[LibraryImport(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinuxWebGpuEffectExportUsesBoundedExplicitlySynchronizedDmaBufs()
    {
        string sink = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxWebGpuNv12EncoderFrameSink.cs");
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxV4l2PreciseMediaCompositionExportProvider.cs");
        string planner = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxMediaOverlayPlanner.cs");
        string overlayRuntime = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxMediaOverlayRuntime.cs");
        string processor = ReadRepoFile(
            "src",
            "ProGPU.Backend",
            "GpuNv12Processor.cs");
        string shader = ReadRepoFile(
            "src",
            "ProGPU.Backend",
            "Shaders",
            "Nv12GpuProcessor.wgsl");

        Assert.Contains(
            "GpuNv12Processor.MaxInFlightSlots",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const uint UniformStride = 256",
            processor,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuNv12Processor.Process(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureGaussianBlur.Blur(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureLayerCompositor",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompositeOverlays(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuNv12Processor.ProcessRgbaToNv12(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuNv12Processor.RenderSolidColor(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryProcessColorFrame(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "TranscodeColor(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "TranscodeTimeline(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcessTimelineUriClip(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcessTimelineColorClip(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new LinuxMediaOverlayRuntime(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "overlays.Prepare(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxV4l2UriOverlayFrameSource",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "V4l2DecodedFrame _candidate",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "V4l2DecodedFrame _lookAhead",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReleaseCandidate(",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "ref _candidate",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "ref _lookAhead",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryImportExternalTexture(",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "owner.Dispose();",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "_decoder?.Dispose();",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "_context.CleanupPendingResources();",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuNv12Processor.ProcessToRgba(",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureGaussianBlur.Blur(",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetUriTexture(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureColorTransform",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetNextColorFrameTimestamp(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionVideoEffectResolver",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxMediaOverlayPlanner.TryCapture(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "compositionTicks < EndTicks",
            planner,
            StringComparison.Ordinal);
        Assert.Contains(
            "SourceStartTicks",
            planner,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetSourceTicks(",
            planner,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxGpuVideoEffectPlan effectPlan",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "redTransform: vec4<f32>",
            shader,
            StringComparison.Ordinal);
        Assert.Contains(
            "Size = 64",
            processor,
            StringComparison.Ordinal);
        Assert.Contains(
            "two attachment clears in one",
            processor,
            StringComparison.Ordinal);
        Assert.Contains(
            "DMA_BUF_IOCTL_IMPORT_SYNC_FILE",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxGbmNative.UseLinear",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "DawnExplicitSharedTextureAccess",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "encoder.TryQueueFrame(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportVideoPath",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            ".GpuCopy",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "EffectsBakedOnGpu: effects",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShaderResource.Load",
            processor,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sourceLuma.Width != destinationLuma.Width",
            processor,
            StringComparison.Ordinal);
        Assert.Contains(
            "(destinationLuma.Width + 1) / 2",
            processor,
            StringComparison.Ordinal);
        Assert.Contains(
            "Linearly resample",
            shader,
            StringComparison.Ordinal);
        Assert.Contains(
            "FilterMode.Linear",
            processor,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "// Algorithm:",
            shader,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MapMemory(",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WritePixels",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WaitIdle",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WaitIdle(",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels",
            sink,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxThumbnailCapabilityAcceptsGpuComposableTimelines()
    {
        MediaCompositionExportRequest composition =
            CreateLinuxPreciseRequest(
                [
                    new MediaCompositionExportClip(
                        new Uri("file:///tmp/source.mp4"),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromMilliseconds(250),
                        TimeSpan.FromMilliseconds(250),
                        1d,
                        null,
                        new Dictionary<string, string>()),
                    new MediaCompositionExportClip(
                        null,
                        TimeSpan.FromSeconds(1),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        0xFF2563EBu,
                        new Dictionary<string, string>
                        {
                            ["progpu.saturation"] = "0.75"
                        })
                ]);
        var request =
            new MediaCompositionThumbnailRequest(
                composition,
                [
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2.5)
                ],
                composition.EncodingProfile.Width,
                composition.EncodingProfile.Height,
                MediaCompositionThumbnailPrecision
                    .NearestFrame);

        Assert.True(
            LinuxV4l2MediaCompositionThumbnailProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasH264Decoder: true,
                    hasVulkanWebGpu: true));
        Assert.False(
            LinuxV4l2MediaCompositionThumbnailProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasH264Decoder: false,
                    hasVulkanWebGpu: true));
        Assert.False(
            LinuxV4l2MediaCompositionThumbnailProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasH264Decoder: true,
                    hasVulkanWebGpu: false));
    }

    [Fact]
    public void LinuxThumbnailCapabilityAcceptsStandardOverlaysAcrossMainClipKinds()
    {
        MediaCompositionExportRequest baseline =
            CreateLinuxPreciseRequest(
                [
                    new MediaCompositionExportClip(
                        null,
                        TimeSpan.FromSeconds(4),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        0xFF111827u,
                        new Dictionary<string, string>())
                ]);
        MediaCompositionExportRequest composition =
            baseline with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        CreateLinuxColorOverlay(
                            0x80F97316u,
                            TimeSpan.FromSeconds(1),
                            16d,
                            9d,
                            160d,
                            90d,
                            0.5d)
                    ])
                ]
            };
        var request =
            new MediaCompositionThumbnailRequest(
                composition,
                [TimeSpan.FromSeconds(2)],
                composition.EncodingProfile.Width,
                composition.EncodingProfile.Height,
                MediaCompositionThumbnailPrecision
                    .NearestFrame);

        Assert.True(
            LinuxV4l2MediaCompositionThumbnailProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasH264Decoder: false,
                    hasVulkanWebGpu: true));

        MediaCompositionExportRequest uriComposition =
            composition with
            {
                Clips =
                [
                    new MediaCompositionExportClip(
                        new Uri("file:///tmp/source.mp4"),
                        TimeSpan.FromSeconds(4),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        null,
                        new Dictionary<string, string>())
                ]
            };
        MediaCompositionThumbnailRequest uriRequest =
            request with
            {
                Composition = uriComposition
            };
        Assert.True(
            LinuxV4l2MediaCompositionThumbnailProvider
                .CanRenderRequest(
                    uriRequest,
                    isLinux: true,
                    hasH264Decoder: true,
                    hasVulkanWebGpu: true));
        Assert.False(
            LinuxV4l2MediaCompositionThumbnailProvider
                .CanRenderRequest(
                    uriRequest,
                    isLinux: true,
                    hasH264Decoder: false,
                    hasVulkanWebGpu: true));

        MediaCompositionExportRequest
            uriOverlayComposition =
                baseline with
                {
                    OverlayLayers =
                    [
                        new MediaCompositionExportOverlayLayer(
                        [
                            new MediaCompositionExportOverlay(
                                new MediaCompositionExportClip(
                                    new Uri(
                                        "file:///tmp/overlay.mp4"),
                                    TimeSpan.FromSeconds(2),
                                    TimeSpan.Zero,
                                    TimeSpan.Zero,
                                    1d,
                                    null,
                                    new Dictionary<
                                        string,
                                        string>()),
                                TimeSpan.Zero,
                                0d,
                                0d,
                                160d,
                                90d,
                                1d,
                                AudioEnabled: false)
                        ])
                    ]
                };
        Assert.True(
            LinuxV4l2MediaCompositionThumbnailProvider
                .CanRenderRequest(
                    request with
                    {
                        Composition =
                            uriOverlayComposition
                    },
                    isLinux: true,
                    hasH264Decoder: true,
                    hasVulkanWebGpu: true));
        Assert.False(
            LinuxV4l2MediaCompositionThumbnailProvider
                .CanRenderRequest(
                    request with
                    {
                        Composition =
                            uriOverlayComposition
                    },
                    isLinux: true,
                    hasH264Decoder: false,
                    hasVulkanWebGpu: true));
    }

    [Fact]
    public void LinuxThumbnailsRetainDecodeGpuAndReadbackState()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxV4l2MediaCompositionThumbnailProvider.cs");
        string renderer = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxWebGpuCompositionThumbnailRenderer.cs");
        string planner = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxMediaOverlayPlanner.cs");
        string overlayRuntime = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxMediaOverlayRuntime.cs");
        string registration = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxMediaPlaybackProvider.cs");
        string project = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "ProGPU.Linux.Media.csproj");
        string processor = ReadRepoFile(
            "src",
            "ProGPU.Backend",
            "GpuNv12Processor.cs");
        string shader = ReadRepoFile(
            "src",
            "ProGPU.Backend",
            "Shaders",
            "Nv12GpuProcessor.wgsl");

        Assert.Contains(
            "new LinuxV4l2MediaCompositionThumbnailProvider(",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionThumbnailRegistry.Default.Register(",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            @"..\ProGPU.Media.Editing\ProGPU.Media.Editing.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "new V4l2StatefulVideoDecoder(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new GpuTextureReadbackBuffer(",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuNv12Processor.ProcessToRgba(",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetVideoEffectPlan(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureGaussianBlur.Blur(",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureLayerCompositor",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompositeOverlays(",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "new LinuxMediaOverlayRuntime(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "renderer.Context",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "overlays.Prepare(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "work.Sort(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompareWork",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetUriTexture(",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureColorTransform",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "V4l2DecodedFrame _candidate",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "V4l2DecodedFrame _lookAhead",
            overlayRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int SnapshotSlotCount = 2",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "CaptureFrame(",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "RenderSnapshot(",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "_target.CopyBaseLevelFrom(snapshot)",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReleaseSnapshot(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompositionTicks",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "current?.Dispose();",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxMediaOverlayPlanner.TryCapture(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "Custom compositor definitions are rejected",
            planner,
            StringComparison.Ordinal);
        Assert.Contains(
            "TextureUsage.RenderAttachment |",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "TextureUsage.CopySrc",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPngEncoder.Encode(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static void ProcessToRgba(",
            processor,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static void ProcessRgbaToNv12(",
            processor,
            StringComparison.Ordinal);
        Assert.Contains(
            "fn fs_rgba(",
            shader,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WritePixels",
            renderer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            renderer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            provider,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinuxProbeRecognizesStatefulHardwareCodecFormats()
    {
        string source = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxNativeMediaCapabilities.cs");

        Assert.Contains("FourCc(\"H264\")", source);
        Assert.Contains("FourCc(\"HEVC\")", source);
        Assert.Contains("FourCc(\"VP80\")", source);
        Assert.Contains("FourCc(\"VP90\")", source);
        Assert.Contains("FourCc(\"AV01\")", source);
        Assert.Contains(
            "CapabilityVideoMemoryToMemoryMultiPlanar",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CapabilityStreaming",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VideoCaptureMultiPlanar",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VideoEncoders",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxRawVideoFormat.Nv12MultiPlanar",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VideoRequestBuffers = 0xC014_5608",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MemoryDmaBuf",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IsoBmffDemuxerBuildsOffsetsTimestampsAndSyncIndex()
    {
        byte[] source = BuildSyntheticH264Movie();
        using var stream =
            new MemoryStream(source, writable: false);

        IsoBmffMovie movie =
            new IsoBmffDemuxer(stream).Parse();

        IsoBmffTrack track = Assert.Single(movie.Tracks);
        Assert.Equal(IsoBmffTrackKind.Video, track.Kind);
        Assert.Equal(IsoBmffCodec.H264, track.Codec);
        Assert.Equal(1_000u, track.Timescale);
        Assert.Equal(2_000, track.Duration);
        Assert.Equal(1_000u, track.MovieTimescale);
        IsoBmffEdit edit = Assert.Single(
            track.EditList);
        Assert.Equal(2_000ul, edit.SegmentDuration);
        Assert.Equal(0, edit.MediaTime);
        Assert.Equal((short)1, edit.MediaRateInteger);
        Assert.Equal((short)0, edit.MediaRateFraction);
        Assert.Equal((ushort)1920, track.Width);
        Assert.Equal((ushort)1080, track.Height);
        Assert.Equal(4, track.NalLengthSize);
        Assert.Equal(2, track.Samples.Length);
        Assert.Equal(1_000, track.Samples[0].Offset);
        Assert.Equal(1_005, track.Samples[1].Offset);
        Assert.Equal(0, track.Samples[0].DecodeTime);
        Assert.Equal(1_000, track.Samples[1].DecodeTime);
        Assert.Equal(900, track.Samples[1].PresentationTime);
        Assert.True(track.Samples[0].IsSync);
        Assert.False(track.Samples[1].IsSync);
        Assert.Equal(
            0x6176_6331u,
            track.SampleEntryType);
        Assert.Equal(91, track.SampleEntryPayload.Length);
    }

    [Fact]
    public void IsoBmffWebVttTracksProjectStableTimedTextCues()
    {
        byte[] source =
            BuildSyntheticH264WebVttMovie();
        using var stream =
            new MemoryStream(source, writable: false);

        IsoBmffMovie movie =
            new IsoBmffDemuxer(stream).Parse();

        Assert.Equal(2, movie.Tracks.Length);
        IsoBmffTrack track = movie.Tracks[1];
        Assert.Equal(
            IsoBmffTrackKind.TimedMetadata,
            track.Kind);
        Assert.Equal(
            IsoBmffCodec.WebVtt,
            track.Codec);
        Assert.Equal("eng", track.Language);
        Assert.Equal("English", track.Name);
        Assert.Equal(
            0x7776_7474u,
            track.SampleEntryType);

        MediaPlaybackTimedMetadataCueSnapshot snapshot =
            IsoBmffWebVttCueReader.ReadAll(
                stream,
                track,
                "isobmff:1");

        Assert.Equal(
            "isobmff:1",
            snapshot.ProviderTrackId);
        Assert.Equal(2, snapshot.Cues.Count);
        Assert.Equal(
            "opening",
            snapshot.Cues[0].CueId);
        Assert.Equal(
            TimeSpan.Zero,
            snapshot.Cues[0].StartTime);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            snapshot.Cues[0].Duration);
        Assert.Equal(
            "Hello GPU & effects\nReady",
            snapshot.Cues[0].Text);
        Assert.NotNull(snapshot.Cues[0].Presentation);
        MediaPlaybackTimedTextCuePresentation
            presentation =
                snapshot.Cues[0].Presentation!;
        Assert.Equal(2, presentation.Lines.Count);
        Assert.Equal(
            "Hello GPU & effects",
            presentation.Lines[0].Text);
        Assert.Equal(
            "Ready",
            presentation.Lines[1].Text);
        Assert.Collection(
            presentation.Lines[0].Subformats,
            subformat =>
            {
                Assert.Equal(6, subformat.StartIndex);
                Assert.Equal(6, subformat.Length);
                Assert.Equal(
                    MediaPlaybackTimedTextWeight.Bold,
                    subformat.Style.FontWeight);
                Assert.Null(subformat.Style.FontStyle);
            },
            subformat =>
            {
                Assert.Equal(12, subformat.StartIndex);
                Assert.Equal(7, subformat.Length);
                Assert.Equal(
                    MediaPlaybackTimedTextWeight.Bold,
                    subformat.Style.FontWeight);
                Assert.Equal(
                    MediaPlaybackTimedTextFontStyle.Italic,
                    subformat.Style.FontStyle);
            });
        MediaPlaybackTimedTextSubformatDescriptor
            underline =
                Assert.Single(
                    presentation.Lines[1].Subformats);
        Assert.True(
            underline.Style.IsUnderlineEnabled);
        Assert.Equal(
            "captions",
            presentation.Layout.RegionName);
        Assert.Equal(
            80d,
            presentation.Layout.LinePosition);
        Assert.Equal(
            MediaPlaybackTimedTextLinePositionUnit
                .Percentage,
            presentation.Layout.LinePositionUnit);
        Assert.Equal(
            MediaPlaybackTimedTextAlignment.Center,
            presentation.Layout.LineAlignment);
        Assert.Equal(
            25d,
            presentation.Layout.TextPositionPercentage);
        Assert.Equal(
            MediaPlaybackTimedTextAlignment.Start,
            presentation.Layout.PositionAlignment);
        Assert.Equal(
            50d,
            presentation.Layout.SizePercentage);
        Assert.Equal(
            MediaPlaybackTimedTextAlignment.Center,
            presentation.Layout.TextAlignment);
        Assert.Equal(
            MediaPlaybackTimedTextWritingMode
                .TopBottomRightLeft,
            presentation.Layout.WritingMode);
        Assert.Equal(
            "isobmff:1:0:1",
            snapshot.Cues[1].CueId);
        Assert.Equal(
            "Second line",
            snapshot.Cues[1].Text);
    }

    [Fact]
    public void LinuxPlaybackExposesTypedTimedMetadataCapability()
    {
        Assert.True(
            typeof(IMediaPlaybackTrackProvider)
                .IsAssignableFrom(
                    typeof(LinuxMediaPlaybackProvider)));
        Assert.True(
            typeof(IMediaPlaybackTimedMetadataProvider)
                .IsAssignableFrom(
                    typeof(LinuxMediaPlaybackProvider)));

        string source = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxMediaPlaybackProvider.cs");
        Assert.Contains(
            "IsoBmffWebVttCueReader.ReadAll(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".PlatformPresented ||",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TakeTrackSelectionRequests()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "the previous V4L2 track was restored",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryQueueFrameStep(direction: 1)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SupportsFrameStepping: true",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxTrackSelectionMapsNativeIndicesAndSupport()
    {
        IsoBmffTrack h264 =
            CreateSyntheticIndexedTrack(
                IsoBmffTrackKind.Video,
                IsoBmffCodec.H264);
        IsoBmffTrack aac =
            CreateSyntheticIndexedTrack(
                IsoBmffTrackKind.Audio,
                IsoBmffCodec.Aac);
        IsoBmffTrack pcm =
            CreateSyntheticIndexedTrack(
                IsoBmffTrackKind.Audio,
                IsoBmffCodec.Pcm) with
            {
                AudioChannelCount = 2,
                AudioBitsPerSample = 16,
                AudioSampleRate = 48_000,
                PcmEncoding =
                    IsoBmffPcmEncoding
                        .SignedLittleEndian,
                Language = "eng",
                Name = "English"
            };
        IsoBmffTrack h265 =
            CreateSyntheticIndexedTrack(
                IsoBmffTrackKind.Video,
                IsoBmffCodec.H265);
        IsoBmffTrack webVtt =
            CreateSyntheticIndexedTrack(
                IsoBmffTrackKind.TimedMetadata,
                IsoBmffCodec.WebVtt);
        var movie = new IsoBmffMovie(
            [h264, aac, pcm, h265, webVtt]);
        var capabilities =
            new LinuxNativeMediaCapabilitySnapshot(
                [
                    new LinuxVideoDecoderDevice(
                        "/dev/video0",
                        "test",
                        "test",
                        LinuxHardwareVideoCodec.H264 |
                        LinuxHardwareVideoCodec.H265,
                        UsesMultiPlanarQueues: true,
                        SupportsStreaming: true)
                ],
                PipeWireAvailable: true);

        LinuxMediaTrackSelectionState selection =
            LinuxMediaPlaybackProvider
                .CreateTrackSelectionState(
                    movie,
                    in capabilities,
                    h264,
                    pcm,
                    audioActive: true);
        MediaPlaybackTracksSnapshot snapshot =
            LinuxMediaPlaybackProvider
                .CreateTrackSnapshot(
                    movie,
                    selection);

        Assert.Equal([1, 2],
            selection.AudioNativeIndices);
        Assert.Equal([false, true],
            selection.AudioSupported);
        Assert.Equal([0, 3],
            selection.VideoNativeIndices);
        Assert.Equal([true, true],
            selection.VideoSupported);
        Assert.Equal(
            1,
            selection.SelectedAudioTrackIndex);
        Assert.Equal(
            0,
            selection.SelectedVideoTrackIndex);
        Assert.Equal(
            1,
            snapshot.SelectedAudioTrackIndex);
        Assert.Equal(
            0,
            snapshot.SelectedVideoTrackIndex);
        Assert.Equal(
            MediaPlaybackTrackSupport.Unsupported,
            snapshot.AudioTracks[0].Support);
        Assert.Equal(
            MediaPlaybackTrackSupport.Supported,
            snapshot.AudioTracks[1].Support);
        Assert.All(
            snapshot.VideoTracks,
            static track => Assert.Equal(
                MediaPlaybackTrackSupport.Supported,
                track.Support));
        Assert.Equal(
            "eng",
            snapshot.AudioTracks[1].Language);
        Assert.Equal(
            "English",
            snapshot.AudioTracks[1].Label);
        Assert.Single(
            snapshot.TimedMetadataTracks);
    }

    [Fact]
    public void LinuxTrackSelectionRequiresNativeCapabilities()
    {
        IsoBmffTrack video =
            CreateSyntheticIndexedTrack(
                IsoBmffTrackKind.Video,
                IsoBmffCodec.H264);
        IsoBmffTrack audio =
            CreateSyntheticIndexedTrack(
                IsoBmffTrackKind.Audio,
                IsoBmffCodec.Pcm) with
            {
                AudioChannelCount = 2,
                AudioBitsPerSample = 16,
                AudioSampleRate = 48_000,
                PcmEncoding =
                    IsoBmffPcmEncoding
                        .SignedLittleEndian
            };
        var movie =
            new IsoBmffMovie([video, audio]);
        var capabilities =
            new LinuxNativeMediaCapabilitySnapshot(
                Array.Empty<
                    LinuxVideoDecoderDevice>(),
                PipeWireAvailable: false);

        LinuxMediaTrackSelectionState selection =
            LinuxMediaPlaybackProvider
                .CreateTrackSelectionState(
                    movie,
                    in capabilities,
                    video,
                    audio,
                    audioActive: false);
        MediaPlaybackTracksSnapshot snapshot =
            LinuxMediaPlaybackProvider
                .CreateTrackSnapshot(
                    movie,
                    selection);

        Assert.Equal([false],
            selection.VideoSupported);
        Assert.Equal([false],
            selection.AudioSupported);
        Assert.Equal(
            -1,
            snapshot.SelectedAudioTrackIndex);
        Assert.Equal(
            MediaPlaybackTrackSupport.Unsupported,
            snapshot.VideoTracks[0].Support);
        Assert.Equal(
            MediaPlaybackTrackSupport.Unsupported,
            snapshot.AudioTracks[0].Support);
    }

    [Fact]
    public void LinuxFrameStepUsesCompositionOrderAndWinUiBackwardInterval()
    {
        IsoBmffTrack track =
            CreateSyntheticIndexedTrack(
                IsoBmffTrackKind.Video,
                IsoBmffCodec.H264) with
            {
                Samples =
                [
                    new IsoBmffSample(
                        0,
                        1,
                        0,
                        0,
                        40,
                        IsSync: true),
                    new IsoBmffSample(
                        1,
                        1,
                        40,
                        120,
                        40,
                        IsSync: false),
                    new IsoBmffSample(
                        2,
                        1,
                        80,
                        40,
                        40,
                        IsSync: true),
                    new IsoBmffSample(
                        3,
                        1,
                        120,
                        80,
                        40,
                        IsSync: false)
                ]
            };

        Assert.True(
            LinuxMediaPlaybackProvider
                .TryGetFrameStepPosition(
                    track,
                    TimeSpan.FromMilliseconds(45),
                    forward: true,
                    out TimeSpan forward));
        Assert.Equal(
            TimeSpan.FromMilliseconds(80),
            forward);
        Assert.True(
            LinuxMediaPlaybackProvider
                .TryGetFrameStepPosition(
                    track,
                    TimeSpan.FromMilliseconds(100),
                    forward: false,
                    out TimeSpan backward));
        Assert.Equal(
            TimeSpan.FromMilliseconds(58),
            backward);
        Assert.True(
            LinuxMediaPlaybackProvider
                .TryGetFrameStepPosition(
                    track,
                    TimeSpan.FromMilliseconds(20),
                    forward: false,
                    out TimeSpan clamped));
        Assert.Equal(
            TimeSpan.Zero,
            clamped);
        Assert.False(
            LinuxMediaPlaybackProvider
                .TryGetFrameStepPosition(
                    track,
                    TimeSpan.FromMilliseconds(120),
                    forward: true,
                    out TimeSpan exhausted));
        Assert.Equal(
            TimeSpan.FromMilliseconds(120),
            exhausted);
        Assert.False(
            LinuxMediaPlaybackProvider
                .TryGetFrameStepPosition(
                    track,
                    TimeSpan.Zero,
                    forward: false,
                    out TimeSpan beginning));
        Assert.Equal(
            TimeSpan.Zero,
            beginning);
        Assert.Equal(
            2,
            LinuxMediaPlaybackProvider
                .FindResumeSample(
                    track,
                    TimeSpan.FromMilliseconds(90)));
    }

    [Fact]
    public void IsoBmffWebVttRejectsMalformedNestedBoxSize()
    {
        byte[] malformed =
        [
            0, 0, 0, 32,
            (byte)'v', (byte)'t',
            (byte)'t', (byte)'c'
        ];
        using var stream =
            new MemoryStream(
                malformed,
                writable: false);

        Assert.Throws<InvalidDataException>(
            () => IsoBmffWebVttCueReader.ReadAll(
                stream,
                CreateSyntheticWebVttTrack(
                    malformed.Length),
                "isobmff:0"));
    }

    [Fact]
    public void IsoBmffWebVttRejectsInvalidUtf8Payload()
    {
        byte[] malformed = Box(
            "vttc",
            Box(
                "payl",
                [0xC3, 0x28]));
        using var stream =
            new MemoryStream(
                malformed,
                writable: false);

        Assert.Throws<InvalidDataException>(
            () => IsoBmffWebVttCueReader.ReadAll(
                stream,
                CreateSyntheticWebVttTrack(
                    malformed.Length),
                "isobmff:0"));
    }

    [Fact]
    public void IsoBmffWebVttRejectsDuplicateSettingsBoxes()
    {
        byte[] malformed = Box(
            "vttc",
            Box(
                "sttg",
                Encoding.UTF8.GetBytes(
                    "align:start")),
            Box(
                "sttg",
                Encoding.UTF8.GetBytes(
                    "align:end")),
            Box(
                "payl",
                Encoding.UTF8.GetBytes(
                    "duplicate settings")));
        using var stream =
            new MemoryStream(
                malformed,
                writable: false);

        Assert.Throws<InvalidDataException>(
            () => IsoBmffWebVttCueReader.ReadAll(
                stream,
                CreateSyntheticWebVttTrack(
                    malformed.Length),
                "isobmff:0"));
    }

    [Fact]
    public async Task IsoBmffFastExporterPreservesIndexedPayloadAndTiming()
    {
        string sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"progpu-source-{Guid.NewGuid():N}.mp4");
        string destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"progpu-export-{Guid.NewGuid():N}.mp4");
        try
        {
            byte[] source = BuildSyntheticH264Movie();
            Array.Resize(ref source, 1_012);
            new byte[]
            {
                1, 2, 3, 4, 5,
                6, 7, 8, 9, 10, 11, 12
            }.CopyTo(source, 1_000);
            await File.WriteAllBytesAsync(
                sourcePath,
                source);

            var request =
                new MediaCompositionExportRequest(
                    destinationPath,
                    new[]
                    {
                        new MediaCompositionExportClip(
                            new Uri(sourcePath),
                            TimeSpan.FromSeconds(2),
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            1d,
                            null,
                            new Dictionary<string, string>())
                    },
                    MediaCompositionTrimmingMode.Fast,
                    new MediaCompositionEncodingProfile(
                        "MPEG4",
                        "H264",
                        null,
                        1_920,
                        1_080,
                        8_000_000,
                        30,
                        1,
                        0,
                        0,
                        0),
                    new Dictionary<string, string>());
            IsoBmffCompositionPlan plan =
                IsoBmffCompositionPlanner.Create(request);

            await IsoBmffCompositionWriter.WriteAsync(
                plan,
                destinationPath,
                progress: null,
                CancellationToken.None);

            await using var output =
                File.OpenRead(destinationPath);
            IsoBmffTrack track = Assert.Single(
                new IsoBmffDemuxer(output).Parse().Tracks);
            Assert.Equal(2, track.Samples.Length);
            Assert.Equal(
                0,
                track.Samples[0].PresentationTime);
            Assert.Equal(
                900,
                track.Samples[1].PresentationTime);
            Assert.True(track.Samples[0].IsSync);
            Assert.False(track.Samples[1].IsSync);

            output.Position = track.Samples[0].Offset;
            var payload = new byte[12];
            await output.ReadExactlyAsync(payload);
            Assert.Equal(
                new byte[]
                {
                    1, 2, 3, 4, 5,
                    6, 7, 8, 9, 10, 11, 12
                },
                payload);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task BrowserFastExporterCommitsCompletedMp4ThroughStorageSeam()
    {
        string sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"progpu-browser-source-{Guid.NewGuid():N}.mp4");
        Func<string, byte[], Task<bool>>? previousWriter =
            StoragePlatformServices.WriteBytesAsync;
        byte[]? committed = null;
        string? committedPath = null;
        try
        {
            byte[] source = BuildSyntheticH264Movie();
            Array.Resize(ref source, 1_012);
            new byte[]
            {
                1, 2, 3, 4, 5,
                6, 7, 8, 9, 10, 11, 12
            }.CopyTo(source, 1_000);
            await File.WriteAllBytesAsync(
                sourcePath,
                source);
            StoragePlatformServices.WriteBytesAsync =
                (path, bytes) =>
                {
                    committedPath = path;
                    committed = bytes.ToArray();
                    return Task.FromResult(true);
                };
            const string destination =
                "/tmp/progpu-browser-save/download/output.mp4";
            var request =
                new MediaCompositionExportRequest(
                    destination,
                    new[]
                    {
                        new MediaCompositionExportClip(
                            new Uri(sourcePath),
                            TimeSpan.FromSeconds(2),
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            1d,
                            null,
                            new Dictionary<string, string>())
                    },
                    MediaCompositionTrimmingMode.Fast,
                    new MediaCompositionEncodingProfile(
                        "MPEG4",
                        "H264",
                        null,
                        1_920,
                        1_080,
                        8_000_000,
                        30,
                        1,
                        0,
                        0,
                        0),
                    new Dictionary<string, string>());
            var progressValues = new List<double>();
            var provider =
                new BrowserFastMediaCompositionExportProvider();
            MediaCompositionExportCapabilities capabilities =
                provider.GetCapabilities(request);

            MediaCompositionExportFailure result =
                await provider.RenderAsync(
                    request,
                    new InlineProgress<double>(
                        progressValues.Add),
                    CancellationToken.None);

            Assert.Equal(
                MediaCompositionExportFailure.None,
                result);
            Assert.Equal(
                "progpu.browser.isobmff.fast-export",
                capabilities.ProviderId);
            Assert.Equal(
                MediaCompositionExportVideoPath
                    .CompressedSampleCopy,
                capabilities.VideoPath);
            Assert.Equal(
                MediaCompositionExportAudioPath.None,
                capabilities.AudioPath);
            Assert.False(capabilities.EffectsBakedOnGpu);
            Assert.Equal(destination, committedPath);
            Assert.NotNull(committed);
            using var output = new MemoryStream(
                committed!,
                writable: false);
            Assert.Single(
                new IsoBmffDemuxer(output).Parse().Tracks);
            Assert.NotEmpty(progressValues);
            Assert.Equal(100d, progressValues[^1]);
        }
        finally
        {
            StoragePlatformServices.WriteBytesAsync =
                previousWriter;
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public void IsoBmffFastExporterSelectsOnlyFaithfulPassthroughRequests()
    {
        var clip = new MediaCompositionExportClip(
            new Uri(
                Path.Combine(
                    Path.GetTempPath(),
                    "source.mp4")),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.Zero,
            1d,
            null,
            new Dictionary<string, string>());
        var profile =
            new MediaCompositionEncodingProfile(
                "MPEG4",
                "H264",
                "AAC",
                1_920,
                1_080,
                8_000_000,
                30,
                1,
                192_000,
                48_000,
                2);
        var provider =
            new IsoBmffFastMediaCompositionExportProvider();
        var request =
            new MediaCompositionExportRequest(
                "destination.mp4",
                new[] { clip },
                MediaCompositionTrimmingMode.Fast,
                profile,
                new Dictionary<string, string>());

        Assert.True(provider.CanRender(request));
        Assert.True(
            provider.CanRender(
                request with
                {
                    Clips = new[]
                    {
                        clip with
                        {
                            SourceUri = new Uri(
                                "https://example.invalid/source.mp4")
                        }
                    }
                }));
        Assert.False(
            provider.CanRender(
                request with
                {
                    Clips = new[]
                    {
                        clip with
                        {
                            SourceUri = new Uri(
                                "ftp://example.invalid/source.mp4")
                        }
                    }
                }));
        Assert.False(
            provider.CanRender(
                request with
                {
                    TrimmingMode =
                        MediaCompositionTrimmingMode.Precise
                }));
        Assert.False(
            provider.CanRender(
                request with
                {
                    Clips = new[]
                    {
                        clip with
                        {
                            Volume = 0.5d
                        }
                    }
                }));
        Assert.False(
            provider.CanRender(
                request with
                {
                    Clips = new[]
                    {
                        clip with
                        {
                            UserData =
                                new Dictionary<string, string>
                                {
                                    ["progpu.saturation"] =
                                        "0.5"
                                }
                        }
                    }
                }));
        Assert.False(
            provider.CanRender(
                request with
                {
                    BackgroundAudioTracks =
                        new[]
                        {
                            new MediaCompositionExportAudioTrack(
                                new Uri(
                                    "https://example.invalid/music.m4a"),
                                TimeSpan.FromSeconds(1),
                                TimeSpan.Zero,
                                TimeSpan.Zero,
                                TimeSpan.Zero,
                                1d,
                                new Dictionary<string, string>())
                        }
                }));
        Assert.False(
            provider.CanRender(
                request with
                {
                    Clips = new[]
                    {
                        clip with
                        {
                            VideoEffectDefinitions =
                                new[]
                                {
                                    new MediaCompositionEffectDefinition(
                                        "ProGPU.Test.Effect",
                                        new Dictionary<string, object?>())
                                }
                        }
                    }
                }));
        Assert.False(
            provider.CanRender(
                request with
                {
                    OverlayLayers =
                        new[]
                        {
                            new MediaCompositionExportOverlayLayer(
                                Array.Empty<
                                    MediaCompositionExportOverlay>())
                        }
                }));
    }

    [Fact]
    public void IsoBmffDemuxerRejectsTruncatedBox()
    {
        byte[] source =
        [
            0, 0, 0, 24,
            (byte)'m', (byte)'o', (byte)'o', (byte)'v'
        ];
        using var stream =
            new MemoryStream(source, writable: false);

        Assert.Throws<InvalidDataException>(
            () => new IsoBmffDemuxer(stream).Parse());
    }

    [Fact]
    public void NalReaderProducesReusableAnnexBAccessUnit()
    {
        byte[] sample =
        [
            0, 0, 0, 3,
            0x65, 0x01, 0x02,
            0, 0, 0, 2,
            0x41, 0x03
        ];
        byte[] avcConfiguration =
        [
            1, 100, 0, 31, 0xFF,
            0xE1,
            0, 2, 0x67, 0x64,
            1,
            0, 2, 0x68, 0xEE
        ];
        var track = new IsoBmffTrack(
            IsoBmffTrackKind.Video,
            IsoBmffCodec.H264,
            1_000,
            1_000,
            640,
            360,
            4,
            avcConfiguration,
            [
                new IsoBmffSample(
                    0,
                    sample.Length,
                    0,
                    0,
                    1_000,
                    IsSync: true)
            ]);
        using var stream =
            new MemoryStream(sample, writable: false);
        using var reader =
            new IsoBmffNalAccessUnitReader(
                stream,
                track);

        byte[] converted =
            reader.Read(0).ToArray();

        Assert.Equal(
            new byte[]
            {
                0, 0, 0, 1, 0x67, 0x64,
                0, 0, 0, 1, 0x68, 0xEE,
                0, 0, 0, 1, 0x65, 0x01, 0x02,
                0, 0, 0, 1, 0x41, 0x03
            },
            converted);
        Assert.Equal(converted, reader.Current.ToArray());
    }

    [Fact]
    public async Task H264EncoderSpoolBuildsAvcConfigurationAndTimedSamples()
    {
        string spoolPath = Path.Combine(
            Path.GetTempPath(),
            $"progpu-h264-{Guid.NewGuid():N}.bin");
        string moviePath = Path.Combine(
            Path.GetTempPath(),
            $"progpu-h264-{Guid.NewGuid():N}.mp4");
        try
        {
            using (var spool =
                   new IsoBmffH264AccessUnitSpool(
                       spoolPath,
                       640,
                       360,
                       30,
                       1))
            {
                spool.Append(
                    [
                        0, 0, 0, 1,
                        0x67, 0x64, 0x00, 0x1F, 0x01,
                        0, 0, 1,
                        0x68, 0xEE, 0x3C, 0x80,
                        0, 0, 0, 1,
                        0x65, 0x11, 0x22
                    ],
                    TimeSpan.Zero,
                    isKeyFrame: true);
                spool.Append(
                    [
                        0, 0, 1,
                        0x41, 0x33, 0x44
                    ],
                    TimeSpan.FromSeconds(2d / 30d),
                    isKeyFrame: false);

                IsoBmffCompositionPlan plan =
                    spool.CreatePlan();
                Assert.Equal(2, spool.SampleCount);
                Assert.Equal(
                    3_000,
                    plan.Video.Samples[0].Duration);
                Assert.Equal(
                    3_000,
                    plan.Video.Samples[1]
                        .CompositionOffset);

                await IsoBmffCompositionWriter.WriteAsync(
                    plan,
                    moviePath,
                    progress: null,
                    CancellationToken.None);
            }

            await using var movieStream =
                File.OpenRead(moviePath);
            IsoBmffTrack track = Assert.Single(
                new IsoBmffDemuxer(movieStream)
                    .Parse()
                    .Tracks);
            Assert.Equal((ushort)640, track.Width);
            Assert.Equal((ushort)360, track.Height);
            Assert.Equal(4, track.NalLengthSize);
            Assert.Equal(2, track.Samples.Length);
            Assert.True(track.Samples[0].IsSync);
            Assert.False(track.Samples[1].IsSync);
            Assert.Equal(
                3_000,
                track.Samples[1].PresentationTime -
                track.Samples[1].DecodeTime);
            Assert.Equal(
                new byte[]
                {
                    1, 100, 0, 31
                },
                track.CodecConfiguration[..4]);

            movieStream.Position =
                track.Samples[1].Offset;
            var secondSample =
                new byte[track.Samples[1].Size];
            await movieStream.ReadExactlyAsync(
                secondSample);
            Assert.Equal(
                new byte[]
                {
                    0, 0, 0, 3,
                    0x41, 0x33, 0x44
                },
                secondSample);
        }
        finally
        {
            File.Delete(spoolPath);
            File.Delete(moviePath);
        }
    }

    [Fact]
    public async Task PreciseAacPlannerUsesExactTrimAndSilenceEdits()
    {
        string sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"progpu-aac-source-{Guid.NewGuid():N}.mp4");
        string destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"progpu-aac-output-{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllBytesAsync(
                sourcePath,
                BuildSyntheticH264AacMovie());
            var firstUri =
                new MediaCompositionExportClip(
                    new Uri(sourcePath),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(20),
                    1d,
                    null,
                    new Dictionary<string, string>());
            var secondUri =
                firstUri with
                {
                    TrimTimeFromStart =
                        TimeSpan.FromMilliseconds(30),
                    TrimTimeFromEnd =
                        TimeSpan.FromMilliseconds(40)
                };
            MediaCompositionExportRequest request =
                new(
                    destinationPath,
                    [
                        new MediaCompositionExportClip(
                            null,
                            TimeSpan.FromMilliseconds(500),
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            1d,
                            0xFF000000u,
                            new Dictionary<string, string>()),
                        firstUri,
                        new MediaCompositionExportClip(
                            null,
                            TimeSpan.FromMilliseconds(250),
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            1d,
                            0xFF000000u,
                            new Dictionary<string, string>()),
                        secondUri
                    ],
                    MediaCompositionTrimmingMode.Precise,
                    new MediaCompositionEncodingProfile(
                        "MPEG4",
                        "H264",
                        "AAC",
                        1_920,
                        1_080,
                        8_000_000,
                        30,
                        1,
                        1_500,
                        48_000,
                        2),
                    new Dictionary<string, string>());

            IsoBmffCompositionTrack audio =
                IsoBmffPreciseAacTimelinePlanner
                    .Create(request);

            Assert.Equal(
                IsoBmffTrackKind.Audio,
                audio.Kind);
            Assert.Equal(184, audio.Samples.Length);
            Assert.Equal(4, audio.Edits.Length);
            Assert.Equal(
                new IsoBmffCompositionEdit(
                    500,
                    -1),
                audio.Edits[0]);
            Assert.Equal(
                new IsoBmffCompositionEdit(
                    1_970,
                    480),
                audio.Edits[1]);
            Assert.Equal(
                new IsoBmffCompositionEdit(
                    250,
                    -1),
                audio.Edits[2]);
            Assert.Equal(
                new IsoBmffCompositionEdit(
                    1_930,
                    95_648),
                audio.Edits[3]);

            MediaCompositionExportRequest videoRequest =
                request with
                {
                    Clips = [firstUri with
                    {
                        TrimTimeFromStart =
                            TimeSpan.Zero,
                        TrimTimeFromEnd =
                            TimeSpan.Zero
                    }],
                    EncodingProfile =
                        request.EncodingProfile with
                        {
                            AudioSubtype = null,
                            AudioBitrate = 0,
                            AudioSampleRate = 0,
                            AudioChannelCount = 0
                        }
                };
            IsoBmffCompositionPlan videoPlan =
                IsoBmffCompositionPlanner.Create(
                    videoRequest);
            await IsoBmffCompositionWriter.WriteAsync(
                videoPlan with
                {
                    Audio = audio
                },
                destinationPath,
                progress: null,
                CancellationToken.None);

            byte[] output =
                await File.ReadAllBytesAsync(
                    destinationPath);
            Assert.Equal(
                audio.Edits,
                ReadFirstEditList(output));
            await using FileStream outputStream =
                File.OpenRead(destinationPath);
            IsoBmffTrack outputAudio =
                new IsoBmffDemuxer(outputStream)
                    .Parse()
                    .Tracks
                    .Single(
                        static track =>
                            track.Kind ==
                                IsoBmffTrackKind.Audio);
            Assert.Equal(
                IsoBmffCodec.Aac,
                outputAudio.Codec);
            Assert.Equal(
                184,
                outputAudio.Samples.Length);
            Assert.Equal(
                IsoBmffCompositionWriter.MovieTimescale,
                outputAudio.MovieTimescale);
            Assert.Equal(
                audio.Edits,
                outputAudio.EditList
                    .Select(
                        static edit =>
                            new IsoBmffCompositionEdit(
                                edit.SegmentDuration,
                                edit.MediaTime))
                    .ToArray());
            Assert.All(
                outputAudio.EditList,
                static edit =>
                {
                    Assert.Equal(
                        (short)1,
                        edit.MediaRateInteger);
                    Assert.Equal(
                        (short)0,
                        edit.MediaRateFraction);
                });

            outputStream.Position = 0;
            MediaFileMetadata outputMetadata =
                MediaFileMetadataReader
                    .ReadIsoBmff(outputStream);
            MediaAudioStreamMetadata
                outputAudioMetadata =
                    Assert.Single(
                        outputMetadata.AudioStreams);
            Assert.Equal(
                TimeSpan.FromMilliseconds(4_650),
                outputAudioMetadata.Duration);
            Assert.Equal(
                TimeSpan.FromMilliseconds(4_650),
                outputMetadata.Duration);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public void V4l2InteropMatchesTheOfficial64BitUapiLayout()
    {
        Assert.Equal(204, Unsafe.SizeOf<V4l2Format>());
        Assert.Equal(
            192,
            Unsafe.SizeOf<V4l2PixelFormatMultiPlanar>());
        Assert.Equal(20, Unsafe.SizeOf<V4l2RequestBuffers>());
        Assert.Equal(64, Unsafe.SizeOf<V4l2Plane>());
        Assert.Equal(88, Unsafe.SizeOf<V4l2Buffer>());
        Assert.Equal(64, Unsafe.SizeOf<V4l2ExportBuffer>());
        Assert.Equal(
            32,
            Unsafe.SizeOf<V4l2EventSubscription>());
        Assert.Equal(136, Unsafe.SizeOf<V4l2Event>());
        Assert.Equal(
            72,
            Unsafe.SizeOf<V4l2DecoderCommand>());
        Assert.Equal(
            40,
            Unsafe.SizeOf<V4l2EncoderCommand>());
        Assert.Equal(8, Unsafe.SizeOf<V4l2Control>());
        Assert.Equal(
            204,
            Unsafe.SizeOf<V4l2StreamParameters>());

        Assert.Equal(
            (nuint)0xC040_5602,
            V4l2Constants.EnumerateFormat);
        Assert.Equal(
            (nuint)0xC0CC_5605,
            V4l2Constants.SetFormat);
        Assert.Equal(
            (nuint)0xC014_5608,
            V4l2Constants.RequestBuffers);
        Assert.Equal(
            (nuint)0xC058_5609,
            V4l2Constants.QueryBuffer);
        Assert.Equal(
            (nuint)0xC058_560F,
            V4l2Constants.QueueBuffer);
        Assert.Equal(
            (nuint)0xC040_5610,
            V4l2Constants.ExportBuffer);
        Assert.Equal(
            (nuint)0x8088_5659,
            V4l2Constants.DequeueEvent);
        Assert.Equal(
            (nuint)0xC048_5660,
            V4l2Constants.DecoderCommand);
        Assert.Equal(
            (nuint)0xC028_564D,
            V4l2Constants.EncoderCommand);
        Assert.Equal(
            (nuint)0xC0CC_5616,
            V4l2Constants.SetStreamParameters);
        Assert.Equal(
            (nuint)0xC008_561C,
            V4l2Constants.SetControl);
        Assert.Equal(
            0x0099_09CFu,
            V4l2Constants.VideoBitrateControl);
    }

    [Fact]
    public void V4l2EncoderImportsGpuFramesAndRetainsOwnersUntilDequeue()
    {
        string source = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "V4l2StatefulVideoEncoder.cs");

        Assert.Contains(
            "V4l2Constants.MemoryDmaBuf",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VideoOutputMultiPlanar",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VideoCaptureMultiPlanar",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "slot.Owner = owner",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "slot.Owner?.Dispose()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "V4l2EncodedAccessUnit",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BufferFlagTimestampCopy",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VideoBitrateControl",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetStreamParameters",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Marshal.Copy",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V4l2FramesExposeRgbButRejectNv12AsOrdinaryTexture()
    {
        var dmaBuf = new ProGpuDmaBufDescriptor(
            V4l2Constants.DrmArgb8888,
            0,
            1,
            new ProGpuDmaBufPlane(19, 0, 2_560));
        using var owner = new TestOwner();
        var rgb = new V4l2DecodedFrame(
            1,
            TimeSpan.Zero,
            640,
            360,
            V4l2DecodedPixelFormat.Bgra8,
            dmaBuf,
            owner);

        Assert.True(
            rgb.TryCreateExternalDescriptor(
                out ProGpuExternalTextureDescriptor descriptor));
        Assert.Equal(
            ProGpuExternalTextureHandleKind.DmaBuf,
            descriptor.HandleKind);
        Assert.Equal(640u, descriptor.Width);
        Assert.Equal(
            Silk.NET.WebGPU.TextureFormat.Bgra8Unorm,
            descriptor.Format);

        var nv12 = rgb with
        {
            PixelFormat =
                V4l2DecodedPixelFormat.Nv12
        };
        Assert.False(
            nv12.TryCreateExternalDescriptor(out _));
        Assert.True(
            nv12.TryCreatePlanarExternalDescriptors(
                out ProGpuExternalTextureDescriptor luma,
                out ProGpuExternalTextureDescriptor chroma));
        Assert.Equal(
            Silk.NET.WebGPU.TextureFormat.R8Unorm,
            luma.Format);
        Assert.Equal(
            Silk.NET.WebGPU.TextureFormat.RG8Unorm,
            chroma.Format);
        Assert.Equal(640u, luma.Width);
        Assert.Equal(360u, luma.Height);
        Assert.Equal(320u, chroma.Width);
        Assert.Equal(180u, chroma.Height);
        Assert.Equal(
            2_560ul * 360,
            chroma.DmaBuf.Plane0.Offset);

        var p010 = rgb with
        {
            PixelFormat =
                V4l2DecodedPixelFormat.P010
        };
        Assert.False(
            p010.TryCreateExternalDescriptor(out _));
        Assert.True(
            p010.TryCreatePlanarExternalDescriptors(
                out ProGpuExternalTextureDescriptor
                    p010Luma,
                out ProGpuExternalTextureDescriptor
                    p010Chroma));
        Assert.Equal(
            ProGpuTextureFormats.R16Unorm,
            p010Luma.Format);
        Assert.Equal(
            ProGpuTextureFormats.RG16Unorm,
            p010Chroma.Format);
        Assert.Equal(
            V4l2Constants.DrmR16,
            p010Luma.DmaBuf.DrmFormat);
        Assert.Equal(
            V4l2Constants.DrmGr1616,
            p010Chroma.DmaBuf.DrmFormat);
        Assert.Equal(640u, p010Luma.Width);
        Assert.Equal(360u, p010Luma.Height);
        Assert.Equal(320u, p010Chroma.Width);
        Assert.Equal(180u, p010Chroma.Height);
        Assert.Equal(
            2_560ul * 360,
            p010Chroma.DmaBuf.Plane0.Offset);
    }

    [Fact]
    public void LinuxProviderIsExplicitAndDependencyFree()
    {
        string source = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxMediaPlaybackProvider.cs");
        string decoder = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "V4l2StatefulVideoDecoder.cs");
        string preciseExporter = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxV4l2PreciseMediaCompositionExportProvider.cs");
        string audioPlanner = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxCompositionAudioPlanner.cs");
        string audioMixer = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxPcm16TimelineMixer.cs");
        string audioMixKernel = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxPcm16Mixer.cs");
        string audioProcessorChain = ReadRepoFile(
            "src",
            "ProGPU.Media",
            "Audio",
            "MediaAudioEffectProcessorChain.cs");
        string audioEncoder = ReadRepoFile(
            "src",
            "ProGPU.Linux.Media",
            "LinuxAacCompositionEncoder.cs");

        Assert.Contains(
            "progpu.linux.v4l2",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PipeWirePcmOutput",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IMediaAudioEffect",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaTransferMode.NativeZeroCopy",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExternalPlanarMediaGpuFrame",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "V4l2Constants.DecoderCommand",
            decoder,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxV4l2PreciseMediaCompositionExportProvider",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportVideoPath",
            preciseExporter,
            StringComparison.Ordinal);
        Assert.Contains(
            ".NativeGpuSurface",
            preciseExporter,
            StringComparison.Ordinal);
        Assert.Contains(
            "preferNv12Capture: true",
            preciseExporter,
            StringComparison.Ordinal);
        Assert.Contains(
            "encoder.TryQueueFrame",
            preciseExporter,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsoBmffH264AccessUnitSpool",
            preciseExporter,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels",
            preciseExporter,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Marshal.Copy",
            preciseExporter,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "FFmpeg",
            preciseExporter,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            preciseExporter,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "LinuxCompositionAudioSourceKind",
            audioPlanner,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxPcm16Mixer.Accumulate(",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaAudioEffectProcessorChain",
            audioPlanner,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcessEffects(",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPcm16FloatConverter",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "AccumulateProcessed(",
            audioMixKernel,
            StringComparison.Ordinal);
        Assert.Contains(
            "registry.TryCreate(",
            audioProcessorChain,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Activator.",
            audioProcessorChain,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Assembly.Load",
            audioProcessorChain,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            audioPlanner,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            audioPlanner,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "FFmpeg",
            audioMixer,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            audioMixer,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "FFmpeg",
            audioMixKernel,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            audioMixKernel,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "FFmpeg",
            audioProcessorChain,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            audioProcessorChain,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ILinuxAacEncoderFactory",
            audioEncoder,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxPcm16TimelineMixer.Mix(",
            audioEncoder,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsoBmffCompositionTrack",
            audioEncoder,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            audioEncoder,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GStreamer",
            audioEncoder,
            StringComparison.OrdinalIgnoreCase);
    }

    private static MediaCompositionExportRequest
        CreateLinuxPreciseRequest(
            IReadOnlyList<
                MediaCompositionExportClip> clips) =>
        new(
            "/tmp/output.mp4",
            clips,
            MediaCompositionTrimmingMode.Precise,
            new MediaCompositionEncodingProfile(
                "MPEG4",
                "H264",
                null,
                320,
                180,
                2_000_000,
                30_000,
                1_001,
                0,
                0,
                0),
            new Dictionary<string, string>());

    private static MediaCompositionExportOverlay
        CreateLinuxColorOverlay(
        uint color,
        TimeSpan delay,
        double x,
        double y,
        double width,
        double height,
        double opacity) =>
        new(
            new MediaCompositionExportClip(
                null,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(750),
                1d,
                color,
                new Dictionary<string, string>()),
            delay,
            x,
            y,
            width,
            height,
            opacity,
            AudioEnabled: false);

    [Fact]
    public void LinuxAudioPlanCapturesMainBackgroundAndAudibleOverlay()
    {
        const string gainId =
            "ProGPU.Tests.Linux.AudioGain";
        const string balanceId =
            "ProGPU.Tests.Linux.AudioBalance";
        var effects = new MediaEffectRegistry();
        using IDisposable gainRegistration =
            effects.Register(
                new MediaAudioGainEffectFactory(
                    gainId));
        using IDisposable balanceRegistration =
            effects.Register(
                new MediaAudioStereoBalanceEffectFactory(
                    balanceId));
        MediaCompositionEffectDefinition gain =
            new(
                gainId,
                new Dictionary<string, object?>
                {
                    [MediaAudioGainEffectFactory
                        .GainPropertyName] = 0.5d
                });
        MediaCompositionEffectDefinition balance =
            new(
                balanceId,
                new Dictionary<string, object?>
                {
                    [MediaAudioStereoBalanceEffectFactory
                        .BalancePropertyName] = 0.5d
                });
        var main =
            new MediaCompositionExportClip(
                new Uri(
                    "file:///media/main.mp4"),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                0.5d,
                null,
                new Dictionary<string, string>())
            {
                SourceAudioTrackIndex = 2,
                AudioEffectDefinitions =
                    [gain, balance]
            };
        var overlayClip =
            new MediaCompositionExportClip(
                new Uri(
                    "file:///media/overlay.mp4"),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                1d,
                null,
                new Dictionary<string, string>());
        var request =
            new MediaCompositionExportRequest(
                "/tmp/output.mp4",
                [main],
                MediaCompositionTrimmingMode.Precise,
                new MediaCompositionEncodingProfile(
                    "MPEG4",
                    "H264",
                    "AAC",
                    320,
                    180,
                    2_000_000,
                    30,
                    1,
                    192_000,
                    48_000,
                    2),
                new Dictionary<string, string>())
            {
                BackgroundAudioTracks =
                [
                    new MediaCompositionExportAudioTrack(
                        new Uri(
                            "file:///media/music.mov"),
                        TimeSpan.FromSeconds(9),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(-2),
                        0.25d,
                        new Dictionary<string, string>())
                    {
                        SourceAudioSubtype =
                            "PCM",
                        SourceAudioTrackIndex = 3,
                        SourceAudioBitrate = 768_000,
                        SourceAudioSampleRate =
                            48_000,
                        SourceAudioChannelCount = 2
                    }
                ],
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        new MediaCompositionExportOverlay(
                            overlayClip,
                            TimeSpan.FromSeconds(1.5),
                            0d,
                            0d,
                            100d,
                            100d,
                            1d,
                            AudioEnabled: true),
                        new MediaCompositionExportOverlay(
                            overlayClip with
                            {
                                SourceUri =
                                    new Uri(
                                        "file:///media/silent.mp4")
                            },
                            TimeSpan.Zero,
                            0d,
                            0d,
                            100d,
                            100d,
                            1d,
                            AudioEnabled: false)
                    ])
                ]
            };

        Assert.True(
            LinuxCompositionAudioPlanner.TryCapture(
                request,
                effects,
                out LinuxCompositionAudioSourcePlan[]
                    plans,
                out long frameCount));
        Assert.Equal(240_000, frameCount);
        Assert.Equal(3, plans.Length);

        LinuxCompositionAudioSourcePlan mainPlan =
            Assert.Single(
                plans,
                static plan =>
                    plan.Kind ==
                    LinuxCompositionAudioSourceKind
                        .MainClip);
        Assert.Equal(2u, mainPlan.SourceTrackIndex);
        Assert.Equal(
            TimeSpan.FromSeconds(1).Ticks,
            mainPlan.SourceStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(6).Ticks,
            mainPlan.SourceEndTicks);
        Assert.Equal(0, mainPlan.DestinationStartFrame);
        Assert.Equal(240_000, mainPlan.DestinationEndFrame);
        Assert.Equal(4_096, mainPlan.Levels.Left);
        Assert.Equal(8_192, mainPlan.Levels.Right);

        LinuxCompositionAudioSourcePlan background =
            Assert.Single(
                plans,
                static plan =>
                    plan.Kind ==
                    LinuxCompositionAudioSourceKind
                        .BackgroundTrack);
        Assert.Equal(3u, background.SourceTrackIndex);
        Assert.Equal(
            TimeSpan.FromSeconds(3).Ticks,
            background.SourceStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(8).Ticks,
            background.SourceEndTicks);
        Assert.Equal(0, background.DestinationStartFrame);
        Assert.Equal(
            240_000,
            background.DestinationEndFrame);
        Assert.Equal(8_192, background.Levels.Left);
        Assert.Equal(8_192, background.Levels.Right);

        LinuxCompositionAudioSourcePlan overlay =
            Assert.Single(
                plans,
                static plan =>
                    plan.Kind ==
                    LinuxCompositionAudioSourceKind
                        .Overlay);
        Assert.Equal(72_000, overlay.DestinationStartFrame);
        Assert.Equal(216_000, overlay.DestinationEndFrame);
        Assert.Equal(
            TimeSpan.FromSeconds(1).Ticks,
            overlay.SourceStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(4).Ticks,
            overlay.SourceEndTicks);
    }

    [Fact]
    public void LinuxAudioPlanLeavesKnownVideoOnlyClipsSilent()
    {
        var clip =
            new MediaCompositionExportClip(
                new Uri(
                    "file:///media/video-only.mp4"),
                TimeSpan.FromSeconds(2),
                TimeSpan.Zero,
                TimeSpan.Zero,
                1d,
                null,
                new Dictionary<string, string>())
            {
                SourceVideoWidth = 320,
                SourceVideoHeight = 180,
                SourceAudioSubtype = null
            };
        var request =
            new MediaCompositionExportRequest(
                "/tmp/output.mp4",
                [clip],
                MediaCompositionTrimmingMode.Precise,
                new MediaCompositionEncodingProfile(
                    "MPEG4",
                    "H264",
                    "AAC",
                    320,
                    180,
                    2_000_000,
                    30,
                    1,
                    96_000,
                    8_000,
                    2),
                new Dictionary<string, string>());

        Assert.True(
            LinuxCompositionAudioPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                out LinuxCompositionAudioSourcePlan[]
                    plans,
                out long frameCount));
        Assert.Empty(plans);
        Assert.Equal(16_000, frameCount);
    }

    [Fact]
    public void LinuxWidePcmMixerIsOrderIndependentAndAllocationFree()
    {
        var full =
            new LinuxPcm16MixLevels(
                32_768,
                32_768);
        Span<short> first =
            stackalloc short[]
            {
                30_000, -30_000,
                10_000, -10_000
            };
        Span<short> second =
            stackalloc short[]
            {
                20_000, -20_000,
                -30_000, 30_000
            };
        Span<long> forward =
            stackalloc long[4];
        Span<long> reverse =
            stackalloc long[4];
        Span<short> forwardOutput =
            stackalloc short[4];
        Span<short> reverseOutput =
            stackalloc short[4];

        LinuxPcm16Mixer.Accumulate(
            first,
            2,
            full,
            forward,
            0);
        LinuxPcm16Mixer.Accumulate(
            second,
            2,
            full,
            forward,
            0);
        LinuxPcm16Mixer.Accumulate(
            second,
            2,
            full,
            reverse,
            0);
        LinuxPcm16Mixer.Accumulate(
            first,
            2,
            full,
            reverse,
            0);
        LinuxPcm16Mixer.Saturate(
            forward,
            forwardOutput);
        LinuxPcm16Mixer.Saturate(
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

        forward.Clear();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0;
             index < 100_000;
             index++)
        {
            LinuxPcm16Mixer.Accumulate(
                first,
                2,
                full,
                forward,
                0);
            LinuxPcm16Mixer.Saturate(
                forward,
                forwardOutput);
            forward.Clear();
        }
        Assert.Equal(
            0,
            GC.GetAllocatedBytesForCurrentThread() -
            before);
    }

    [Fact]
    public void LinuxTimelineMixerStreamsFixedBlocksAndExactOverlaps()
    {
        var full =
            new LinuxPcm16MixLevels(
                32_768,
                32_768);
        LinuxCompositionAudioSourcePlan[] plans =
        [
            new(
                LinuxCompositionAudioSourceKind
                    .MainClip,
                new Uri(
                    "file:///media/main.mov"),
                0,
                0,
                TimeSpan.FromSeconds(2).Ticks,
                0,
                1_500,
                full,
                []),
            new(
                LinuxCompositionAudioSourceKind
                    .BackgroundTrack,
                new Uri(
                    "file:///media/music.mov"),
                0,
                TimeSpan.FromMilliseconds(250)
                    .Ticks,
                TimeSpan.FromSeconds(1).Ticks,
                512,
                1_300,
                full,
                [])
        ];
        ILinuxPcm16TimelineSource[] sources =
        [
            new ConstantPcm16TimelineSource(
                20_000),
            new ConstantPcm16TimelineSource(
                -5_000)
        ];
        var collector =
            new Pcm16BlockCollector(
                frameCount: 1_500,
                channelCount: 2);

        try
        {
            LinuxPcm16TimelineMixer.MixCore(
                plans,
                sources,
                compositionFrameCount: 1_500,
                sampleRate: 8_000,
                channelCount: 2,
                collector.Accept);
        }
        finally
        {
            foreach (ILinuxPcm16TimelineSource
                     source in sources)
            {
                source.Dispose();
            }
        }

        Assert.Equal(2, collector.BlockCount);
        Assert.Equal(
            [0L, 1_024L],
            collector.BlockStarts);
        Assert.Equal(
            20_000,
            collector.SampleAt(
                frame: 511,
                channel: 0));
        Assert.Equal(
            15_000,
            collector.SampleAt(
                frame: 512,
                channel: 0));
        Assert.Equal(
            15_000,
            collector.SampleAt(
                frame: 1_299,
                channel: 1));
        Assert.Equal(
            20_000,
            collector.SampleAt(
                frame: 1_300,
                channel: 1));
        Assert.All(
            sources.Cast<
                ConstantPcm16TimelineSource>(),
            static source =>
                Assert.True(source.IsDisposed));
    }

    [Fact]
    public void LinuxIsoBmffPcmTimelineSourceReadsAcrossSampleBoundaries()
    {
        string path =
            Path.Combine(
                Path.GetTempPath(),
                $"progpu-linux-pcm-{Guid.NewGuid():N}.mov");
        File.WriteAllBytes(
            path,
            BuildSyntheticPcmMovie());
        try
        {
            var plan =
                new LinuxCompositionAudioSourcePlan(
                    LinuxCompositionAudioSourceKind
                        .MainClip,
                    new Uri(path),
                    SourceTrackIndex: 0,
                    SourceStartTicks: 0,
                    SourceEndTicks:
                        TimeSpan.FromMilliseconds(
                            0.5).Ticks,
                    DestinationStartFrame: 0,
                    DestinationEndFrame: 4,
                    new LinuxPcm16MixLevels(
                        32_768,
                        32_768),
                    ProcessorDefinitions: []);
            using var source =
                new IsoBmffPcm16TimelineSource(
                    plan,
                    sampleRate: 8_000,
                    channelCount: 2);
            Span<short> samples =
                stackalloc short[6];

            source.ReadFrames(
                firstFrame: 1,
                samples);

            Assert.True(
                samples.SequenceEqual(
                    new short[]
                    {
                        300, 400,
                        500, 600,
                        700, 800
                    }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LinuxTimelineMixerWarmBlocksAllocateNothing()
    {
        LinuxCompositionAudioSourcePlan[] plans =
        [
            new(
                LinuxCompositionAudioSourceKind
                    .MainClip,
                new Uri(
                    "file:///media/main.mov"),
                0,
                0,
                TimeSpan.FromSeconds(1).Ticks,
                0,
                64,
                new LinuxPcm16MixLevels(
                    24_576,
                    16_384),
                [])
        ];
        ILinuxPcm16TimelineSource[] sources =
        [
            new ConstantPcm16TimelineSource(
                1_000)
        ];
        var sink =
            new Pcm16ChecksumSink();
        LinuxPcm16BlockHandler handler =
            sink.Accept;
        try
        {
            LinuxPcm16TimelineMixer.MixCore(
                plans,
                sources,
                compositionFrameCount: 64,
                sampleRate: 8_000,
                channelCount: 2,
                handler);
            sink.Reset();

            long before =
                GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0;
                 index < 100;
                 index++)
            {
                LinuxPcm16TimelineMixer.MixCore(
                    plans,
                    sources,
                    compositionFrameCount: 64,
                    sampleRate: 8_000,
                    channelCount: 2,
                    handler);
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.Equal(0, allocated);
            Assert.Equal(
                100,
                sink.BlockCount);
            Assert.NotEqual(0, sink.Checksum);
        }
        finally
        {
            sources[0].Dispose();
        }
    }

    [Fact]
    public void
        LinuxTimelineMixerRunsTypedEffectsInOrderWithoutWarmAllocations()
    {
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new TestLinuxPcmTransformEffectFactory());
        MediaCompositionEffectDefinition[]
            definitions =
            [
                CreateTestPcmTransform(
                    scale: 2f,
                    offset: 0f),
                CreateTestPcmTransform(
                    scale: 1f,
                    offset: 0.25f)
            ];
        Assert.True(
            MediaAudioEffectProcessorChain
                .TryCreate(
                    registry,
                    definitions,
                    out MediaAudioEffectProcessorChain?
                        chain));
        Assert.NotNull(chain);
        using (chain)
        {
            LinuxCompositionAudioSourcePlan[] plans =
            [
                new(
                    LinuxCompositionAudioSourceKind
                        .MainClip,
                    new Uri(
                        "file:///media/main.mov"),
                    0,
                    0,
                    TimeSpan.FromSeconds(1).Ticks,
                    0,
                    64,
                    new LinuxPcm16MixLevels(
                        32_768,
                        32_768),
                    definitions)
            ];
            ILinuxPcm16TimelineSource[] sources =
            [
                new ConstantPcm16TimelineSource(
                    1_000)
            ];
            MediaAudioEffectProcessorChain?[]
                processorChains = [chain];
            var sink = new Pcm16ChecksumSink();
            LinuxPcm16BlockHandler handler =
                sink.Accept;
            try
            {
            for (int index = 0;
                 index < 100;
                 index++)
            {
                LinuxPcm16TimelineMixer.MixCore(
                    plans,
                    sources,
                    compositionFrameCount: 64,
                    sampleRate: 8_000,
                    channelCount: 2,
                    handler,
                    processorChains:
                        processorChains);
            }
            Assert.Equal(
                100L *
                64 *
                2 *
                10_192,
                sink.Checksum);
                sink.Reset();

                long before =
                    GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0;
                     index < 100;
                     index++)
                {
                    LinuxPcm16TimelineMixer.MixCore(
                        plans,
                        sources,
                        compositionFrameCount: 64,
                        sampleRate: 8_000,
                        channelCount: 2,
                        handler,
                        processorChains:
                            processorChains);
                }
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() -
                    before;

                Assert.Equal(0, allocated);
                Assert.Equal(
                    100,
                    sink.BlockCount);
                Assert.Equal(
                    100L *
                    64 *
                    2 *
                    10_192,
                    sink.Checksum);
            }
            finally
            {
                sources[0].Dispose();
            }
        }
    }

    [Fact]
    public void
        LinuxTimelineMixerPreservesTypedEffectHeadroomUntilFinalSaturation()
    {
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new TestLinuxPcmTransformEffectFactory());
        MediaCompositionEffectDefinition[]
            definitions =
            [
                CreateTestPcmTransform(
                    scale: 2f,
                    offset: 0f)
            ];
        Assert.True(
            MediaAudioEffectProcessorChain
                .TryCreate(
                    registry,
                    definitions,
                    out MediaAudioEffectProcessorChain?
                        chain));
        Assert.NotNull(chain);
        using (chain)
        {
            LinuxCompositionAudioSourcePlan[] plans =
            [
                new(
                    LinuxCompositionAudioSourceKind
                        .MainClip,
                    new Uri(
                        "file:///media/main.mov"),
                    0,
                    0,
                    TimeSpan.FromSeconds(1).Ticks,
                    0,
                    1,
                    new LinuxPcm16MixLevels(
                        16_384,
                        16_384),
                    definitions)
            ];
            ILinuxPcm16TimelineSource[] sources =
            [
                new ConstantPcm16TimelineSource(
                    20_000)
            ];
            MediaAudioEffectProcessorChain?[]
                processorChains = [chain];
            var samples =
                new Pcm16BlockCollector(
                    frameCount: 1,
                    channelCount: 2);
            try
            {
                LinuxPcm16TimelineMixer.MixCore(
                    plans,
                    sources,
                    compositionFrameCount: 1,
                    sampleRate: 8_000,
                    channelCount: 2,
                    samples.Accept,
                    processorChains:
                        processorChains);

                Assert.Equal(
                    (short)20_000,
                    samples.SampleAt(0, 0));
                Assert.Equal(
                    (short)20_000,
                    samples.SampleAt(0, 1));
            }
            finally
            {
                sources[0].Dispose();
            }
        }
    }

    [Fact]
    public void
        LinuxTimelineMixerCompensatesLatencyAndDrainsFiniteEffectTail()
    {
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new TestLinuxTimedEchoEffectFactory());
        MediaCompositionEffectDefinition[]
            definitions =
            [
                new(
                    TestLinuxTimedEchoEffectFactory
                        .EffectId,
                    new Dictionary<string, object?>())
            ];
        Assert.True(
            MediaAudioEffectProcessorChain
                .TryCreate(
                    registry,
                    definitions,
                    out MediaAudioEffectProcessorChain?
                        chain));
        Assert.NotNull(chain);
        using (chain)
        {
            var timing =
                new MediaAudioProcessorTiming(
                    latencyFrameCount: 2,
                    tailFrameCount: 2);
            LinuxCompositionAudioSourcePlan[] plans =
            [
                new(
                    LinuxCompositionAudioSourceKind
                        .MainClip,
                    new Uri(
                        "file:///media/impulse.mov"),
                    0,
                    0,
                    TimeSpan.FromSeconds(1).Ticks,
                    0,
                    4,
                    new LinuxPcm16MixLevels(
                        32_768,
                        32_768),
                    definitions)
                {
                    ProcessorTiming = timing
                }
            ];
            ILinuxPcm16TimelineSource[] sources =
            [
                new ImpulsePcm16TimelineSource(
                    amplitude: 16_000,
                    channelCount: 2)
            ];
            MediaAudioEffectProcessorChain?[]
                processorChains = [chain];
            var samples =
                new Pcm16BlockCollector(
                    frameCount: 6,
                    channelCount: 2);
            try
            {
                LinuxPcm16TimelineMixer.MixCore(
                    plans,
                    sources,
                    compositionFrameCount: 6,
                    sampleRate: 8_000,
                    channelCount: 2,
                    samples.Accept,
                    processorChains:
                        processorChains);

                Assert.Equal(
                    (short)16_000,
                    samples.SampleAt(0, 0));
                Assert.Equal(
                    (short)16_000,
                    samples.SampleAt(0, 1));
                Assert.Equal(
                    (short)8_000,
                    samples.SampleAt(2, 0));
                Assert.Equal(
                    (short)8_000,
                    samples.SampleAt(2, 1));
                Assert.Equal(
                    (short)0,
                    samples.SampleAt(1, 0));
                Assert.Equal(
                    (short)0,
                    samples.SampleAt(5, 1));
            }
            finally
            {
                sources[0].Dispose();
            }
        }
    }

    [Fact]
    public void
        LinuxTimedAudioPlanExtendsEncoderTimelineByDeclaredTail()
    {
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new TestLinuxTimedEchoEffectFactory());
        var request =
            new MediaCompositionExportRequest(
                "/tmp/output.mp4",
                [
                    new MediaCompositionExportClip(
                        new Uri(
                            "file:///media/timed-pcm.mov"),
                        TimeSpan.FromTicks(5_000),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        null,
                        new Dictionary<
                            string,
                            string>())
                    {
                        SourceAudioSubtype = "PCM",
                        SourceAudioSampleRate = 8_000,
                        SourceAudioChannelCount = 2,
                        AudioEffectDefinitions =
                        [
                            new(
                                TestLinuxTimedEchoEffectFactory
                                    .EffectId,
                                new Dictionary<
                                    string,
                                    object?>())
                        ]
                    }
                ],
                MediaCompositionTrimmingMode.Precise,
                new MediaCompositionEncodingProfile(
                    "MPEG4",
                    "H264",
                    "AAC",
                    320,
                    180,
                    2_000_000,
                    30,
                    1,
                    96_000,
                    8_000,
                    2),
                new Dictionary<string, string>());

        Assert.True(
            LinuxAacCompositionEncoder.TryPrepare(
                request,
                registry,
                new TestLinuxAacEncoderFactory(),
                out LinuxCompositionAudioSourcePlan[]
                    plans,
                out LinuxAacEncoderConfiguration
                    configuration));
        LinuxCompositionAudioSourcePlan plan =
            Assert.Single(plans);
        Assert.Equal(
            new MediaAudioProcessorTiming(2, 2),
            plan.ProcessorTiming);
        Assert.Equal(6, configuration.TotalFrameCount);
    }

    [Fact]
    public async Task
        LinuxPluggableAacEncoderSpoolsExactPcmTimeline()
    {
        string sourcePath =
            Path.Combine(
                Path.GetTempPath(),
                $"progpu-linux-aac-source-{Guid.NewGuid():N}.mov");
        string spoolPath =
            Path.Combine(
                Path.GetTempPath(),
                $"progpu-linux-aac-spool-{Guid.NewGuid():N}.bin");
        string videoSpoolPath =
            Path.Combine(
                Path.GetTempPath(),
                $"progpu-linux-aac-video-{Guid.NewGuid():N}.bin");
        string moviePath =
            Path.Combine(
                Path.GetTempPath(),
                $"progpu-linux-aac-output-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(
            sourcePath,
            BuildSyntheticPcmMovie());
        try
        {
            var request =
                new MediaCompositionExportRequest(
                    "/tmp/output.mp4",
                    [
                        new MediaCompositionExportClip(
                            new Uri(sourcePath),
                            TimeSpan.FromTicks(5_000),
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            1d,
                            null,
                            new Dictionary<
                                string,
                                string>())
                        {
                            SourceAudioSubtype =
                                "PCM",
                            SourceAudioSampleRate =
                                8_000,
                            SourceAudioChannelCount =
                                2,
                            AudioEffectDefinitions =
                            [
                                CreateTestPcmTransform(
                                    scale: 2f,
                                    offset: 0f),
                                CreateTestPcmTransform(
                                    scale: 1f,
                                    offset: 0.25f)
                            ]
                        }
                    ],
                    MediaCompositionTrimmingMode
                        .Precise,
                    new MediaCompositionEncodingProfile(
                        "MPEG4",
                        "H264",
                        "AAC",
                        320,
                        180,
                        2_000_000,
                        30,
                        1,
                        96_000,
                        8_000,
                        2),
                    new Dictionary<string, string>());
            var factory =
                new TestLinuxAacEncoderFactory();
            var effects =
                new MediaEffectRegistry();
            using IDisposable effectRegistration =
                effects.Register(
                    new TestLinuxPcmTransformEffectFactory());

            IsoBmffCompositionTrack track =
                LinuxAacCompositionEncoder.Encode(
                    request,
                    effects,
                    factory,
                    spoolPath,
                    CancellationToken.None);

            TestLinuxAacEncoder encoder =
                Assert.IsType<TestLinuxAacEncoder>(
                    factory.Encoder);
            Assert.Equal(1, encoder.BlockCount);
            Assert.Equal(0, encoder.LastFirstFrame);
            Assert.Equal(8, encoder.ScalarSampleCount);
            Assert.Equal(72_736, encoder.Checksum);
            Assert.Equal(
                IsoBmffTrackKind.Audio,
                track.Kind);
            Assert.Equal(8_000u, track.Timescale);
            Assert.Equal(0x6D703461u, track.SampleEntryType);
            IsoBmffCompositionSample sample =
                Assert.Single(track.Samples);
            Assert.Equal(4, sample.Duration);
            Assert.Equal(4, sample.Size);
            Assert.Equal(
                [
                    new IsoBmffCompositionEdit(
                        SegmentDuration: 1,
                        MediaTime: 0)
                ],
                track.Edits);
            Assert.Equal(
                2,
                BinaryPrimitives
                    .ReadUInt16BigEndian(
                        track.SampleEntryPayload
                            .AsSpan(16)));
            Assert.Equal(
                8_000u << 16,
                BinaryPrimitives
                    .ReadUInt32BigEndian(
                        track.SampleEntryPayload
                            .AsSpan(24)));
            Assert.True(
                track.SampleEntryPayload
                    .AsSpan()
                    .IndexOf(
                        "esds"u8) >= 0);
            Assert.Equal(
                4,
                new FileInfo(spoolPath).Length);

            using (var videoSpool =
                   new IsoBmffH264AccessUnitSpool(
                       videoSpoolPath,
                       320,
                       180,
                       30,
                       1))
            {
                videoSpool.Append(
                    [
                        0, 0, 0, 1,
                        0x67, 0x64, 0x00, 0x1F, 0x01,
                        0, 0, 1,
                        0x68, 0xEE, 0x3C, 0x80,
                        0, 0, 0, 1,
                        0x65, 0x11, 0x22
                    ],
                    TimeSpan.Zero,
                    isKeyFrame: true);
                IsoBmffCompositionPlan plan =
                    videoSpool.CreatePlan() with
                    {
                        Audio = track
                    };
                await IsoBmffCompositionWriter
                    .WriteAsync(
                        plan,
                        moviePath,
                        progress: null,
                        CancellationToken.None);
            }

            await using FileStream movie =
                File.OpenRead(moviePath);
            IsoBmffTrack parsedAudio =
                new IsoBmffDemuxer(movie)
                    .Parse()
                    .Tracks
                    .Single(
                        static candidate =>
                            candidate.Kind ==
                                IsoBmffTrackKind.Audio);
            Assert.Equal(
                IsoBmffCodec.Aac,
                parsedAudio.Codec);
            Assert.Equal(
                8_000u,
                parsedAudio.AudioSampleRate);
            Assert.Equal(
                2u,
                parsedAudio.AudioChannelCount);
            Assert.Equal(4, parsedAudio.Duration);
            Assert.Equal(
                track.Edits,
                parsedAudio.EditList
                    .Select(
                        static edit =>
                            new IsoBmffCompositionEdit(
                                edit.SegmentDuration,
                                edit.MediaTime))
                    .ToArray());
        }
        finally
        {
            File.Delete(moviePath);
            File.Delete(videoSpoolPath);
            File.Delete(spoolPath);
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public void LinuxPreciseProviderSelectsExplicitPcmToAacExtension()
    {
        var request =
            new MediaCompositionExportRequest(
                "/tmp/output.mp4",
                [
                    new MediaCompositionExportClip(
                        null,
                        TimeSpan.FromSeconds(1),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        0xFF102030u,
                        new Dictionary<string, string>())
                ],
                MediaCompositionTrimmingMode.Precise,
                new MediaCompositionEncodingProfile(
                    "MPEG4",
                    "H264",
                    "AAC",
                    320,
                    180,
                    2_000_000,
                    30,
                    1,
                    96_000,
                    8_000,
                    2),
                new Dictionary<string, string>())
            {
                BackgroundAudioTracks =
                [
                    new MediaCompositionExportAudioTrack(
                        new Uri(
                            "file:///media/music.mov"),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        1d,
                        new Dictionary<string, string>())
                ]
            };
        var factory =
            new TestLinuxAacEncoderFactory();

        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true));
        Assert.True(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    request,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true,
                    audioEncoderFactory: factory));
        Assert.Equal(
            MediaCompositionExportAudioPath.CpuBuffer,
            LinuxV4l2PreciseMediaCompositionExportProvider
                .GetCapabilitiesForRequest(
                    request,
                    audioEncoderFactory:
                        factory)
                .AudioPath);

        MediaCompositionExportRequest
            knownCompressedBackground =
                request with
                {
                    BackgroundAudioTracks =
                    [
                        request.BackgroundAudioTracks[0]
                            with
                            {
                                SourceAudioSubtype =
                                    "AAC",
                                SourceAudioSampleRate =
                                    8_000,
                                SourceAudioChannelCount =
                                    2
                            }
                    ]
                };
        Assert.False(
            LinuxV4l2PreciseMediaCompositionExportProvider
                .CanRenderRequest(
                    knownCompressedBackground,
                    isLinux: true,
                    hasNativeH264Path: true,
                    hasNativeTwoPlaneH264EncoderPath:
                        true,
                    gpuAvailable: true,
                    audioEncoderFactory: factory));
    }

    [Fact]
    public void LinuxAacSinkRejectsMismatchedOrIncompleteOutput()
    {
        string spoolPath =
            Path.Combine(
                Path.GetTempPath(),
                $"progpu-linux-aac-contract-{Guid.NewGuid():N}.bin");
        var configuration =
            new LinuxAacEncoderConfiguration(
                SampleRate: 8_000,
                ChannelCount: 2,
                Bitrate: 96_000,
                TotalFrameCount: 4);
        try
        {
            using var sink =
                new LinuxAacAccessUnitSpool(
                    spoolPath,
                    configuration);
            Assert.Throws<
                InvalidOperationException>(
                () => sink.WriteAccessUnit(
                    [1],
                    1));
            Assert.Throws<
                ArgumentException>(
                () => sink.Configure(
                    [0x11, 0x90],
                    encoderDelayFrameCount: 0));

            sink.Configure(
                [0x15, 0x90],
                encoderDelayFrameCount: 0);
            sink.WriteAccessUnit(
                [1, 2, 3],
                decodedFrameCount: 3);
            Assert.Throws<
                InvalidDataException>(
                () => sink.CompleteTrack());
        }
        finally
        {
            File.Delete(spoolPath);
        }
    }

    [Fact]
    public void PipeWireInteropAndRawAudioPodMatchSpaAbi()
    {
        Assert.Equal(
            16,
            Unsafe.SizeOf<PipeWireDictionaryItem>());
        Assert.Equal(
            16,
            Unsafe.SizeOf<PipeWireDictionary>());
        Assert.Equal(
            96,
            Unsafe.SizeOf<PipeWireStreamEvents>());
        Assert.Equal(
            40,
            Unsafe.SizeOf<PipeWireBuffer>());
        Assert.Equal(
            24,
            Unsafe.SizeOf<SpaBuffer>());
        Assert.Equal(
            40,
            Unsafe.SizeOf<SpaData>());
        Assert.Equal(
            16,
            Unsafe.SizeOf<SpaChunk>());
        Assert.Equal(
            64,
            Unsafe.SizeOf<PipeWireTime>());
        Assert.Equal(
            136,
            Unsafe.SizeOf<PipeWireAudioFormatPod>());
        Assert.Equal(
            24,
            Unsafe.SizeOf<PipeWirePodProperty>());

        PipeWireAudioFormatPod pod =
            PipeWireAudioFormatPod.Create(
                48_000,
                2);
        ReadOnlySpan<byte> bytes =
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref pod,
                    1));
        static uint Value(
            ReadOnlySpan<byte> source,
            int offset) =>
            BinaryPrimitives
                .ReadUInt32LittleEndian(
                    source[offset..]);

        Assert.Equal(128u, Value(bytes, 0));
        Assert.Equal(15u, Value(bytes, 4));
        Assert.Equal(
            0x0004_0003u,
            Value(bytes, 8));
        Assert.Equal(3u, Value(bytes, 12));
        Assert.Equal(1u, Value(bytes, 16));
        Assert.Equal(3u, Value(bytes, 28));
        Assert.Equal(1u, Value(bytes, 32));
        Assert.Equal(
            0x0001_0001u,
            Value(bytes, 64));
        Assert.Equal(
            0x011Bu,
            Value(bytes, 80));
        Assert.Equal(48_000u, Value(bytes, 104));
        Assert.Equal(2u, Value(bytes, 128));
    }

    [Fact]
    public void PipeWirePcmRingIsBoundedAndFrameAligned()
    {
        using var output =
            new PipeWirePcmOutput(
                48_000,
                2,
                ringFrameCapacity: 256);
        var samples = new float[600];

        int written =
            output.Write(samples);

        Assert.True(written > 0);
        Assert.True(written <= 300);
        Assert.Equal(written, output.QueuedFrames);
        Assert.Throws<ArgumentException>(
            () => output.Write(
                new float[3]));
    }

    [Fact]
    public void IsoBmffPcmReaderConvertsSignedSamplesWithoutAllocation()
    {
        byte[] sample =
        [
            0x00, 0x80,
            0x00, 0x00,
            0xFF, 0x7F
        ];
        var track = new IsoBmffTrack(
            IsoBmffTrackKind.Audio,
            IsoBmffCodec.Pcm,
            48_000,
            3,
            0,
            0,
            0,
            [],
            [
                new IsoBmffSample(
                    0,
                    sample.Length,
                    0,
                    0,
                    3,
                    IsSync: true)
            ])
        {
            AudioChannelCount = 1,
            AudioBitsPerSample = 16,
            AudioSampleRate = 48_000,
            PcmEncoding =
                IsoBmffPcmEncoding
                    .SignedLittleEndian
        };
        using var stream =
            new MemoryStream(
                sample,
                writable: false);
        using var reader =
            new IsoBmffPcmSampleReader(
                stream,
                track);

        ReadOnlySpan<float> converted =
            reader.Read(0);

        Assert.Equal(3, converted.Length);
        Assert.Equal(-1f, converted[0]);
        Assert.Equal(0f, converted[1]);
        Assert.InRange(
            converted[2],
            0.9999f,
            1f);
        Assert.Equal(
            converted.ToArray(),
            reader.Current.ToArray());

        ReadOnlySpan<short> pcm16 =
            reader.ReadPcm16(0);
        Assert.True(
            pcm16.SequenceEqual(
                new short[]
                {
                    short.MinValue,
                    0,
                    short.MaxValue
                }));
        _ = reader.ReadPcm16(0);
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0;
             index < 10_000;
             index++)
        {
            _ = reader.ReadPcm16(0);
        }
        Assert.Equal(
            0,
            GC.GetAllocatedBytesForCurrentThread() -
            before);
    }

    private static MediaCompositionEffectDefinition
        CreateTestPcmTransform(
            float scale,
            float offset) =>
        new(
            TestLinuxPcmTransformEffectFactory
                .EffectId,
            new Dictionary<string, object?>
            {
                [
                    TestLinuxPcmTransformEffectFactory
                        .ScalePropertyName
                ] = scale,
                [
                    TestLinuxPcmTransformEffectFactory
                        .OffsetPropertyName
                ] = offset
            });

    private sealed class
        TestLinuxPcmTransformEffectFactory :
        IMediaEffectFactory
    {
        internal const string EffectId =
            "ProGPU.Tests.LinuxPcmTransform";
        internal const string ScalePropertyName =
            "Scale";
        internal const string OffsetPropertyName =
            "Offset";

        public string ActivatableClassId =>
            EffectId;

        public IMediaEffect Create(
            in MediaEffectDescriptor descriptor)
        {
            Assert.Equal(
                MediaEffectKind.Audio,
                descriptor.Kind);
            return new TestLinuxPcmTransformEffect(
                Read(
                    descriptor.Properties,
                    ScalePropertyName),
                Read(
                    descriptor.Properties,
                    OffsetPropertyName));
        }

        private static float Read(
            IReadOnlyDictionary<string, object?>
                properties,
            string name) =>
            properties[name] switch
            {
                float value => value,
                double value =>
                    checked((float)value),
                _ => throw new
                    InvalidOperationException()
            };
    }

    private sealed class TestLinuxPcmTransformEffect :
        IMediaAudioEffect
    {
        private readonly float _scale;
        private readonly float _offset;

        internal TestLinuxPcmTransformEffect(
            float scale,
            float offset)
        {
            _scale = scale;
            _offset = offset;
        }

        public string Id =>
            TestLinuxPcmTransformEffectFactory
                .EffectId;

        public MediaEffectKind Kind =>
            MediaEffectKind.Audio;

        public void Process(
            Span<float> interleavedSamples,
            in MediaAudioProcessContext context)
        {
            int sampleCount = checked(
                context.FrameCount *
                context.Format.ChannelCount);
            Span<float> samples =
                interleavedSamples[
                    ..sampleCount];
            for (int index = 0;
                 index < samples.Length;
                 index++)
            {
                samples[index] =
                    samples[index] *
                    _scale +
                    _offset;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class
        TestLinuxTimedEchoEffectFactory :
        IMediaEffectFactory
    {
        internal const string EffectId =
            "ProGPU.Tests.LinuxTimedEcho";

        public string ActivatableClassId =>
            EffectId;

        public IMediaEffect Create(
            in MediaEffectDescriptor descriptor) =>
            new TestLinuxTimedEchoEffect();
    }

    private sealed class TestLinuxTimedEchoEffect :
        IMediaAudioEffect,
        IMediaAudioProcessorTiming
    {
        private const int LatencyFrames = 2;
        private const int TailFrames = 2;
        private readonly float[] _history =
            new float[
                (LatencyFrames +
                 TailFrames +
                 1) *
                2];
        private int _cursor;

        public string Id =>
            TestLinuxTimedEchoEffectFactory
                .EffectId;

        public MediaEffectKind Kind =>
            MediaEffectKind.Audio;

        public MediaAudioProcessorTiming GetTiming(
            in MediaAudioFormat format) =>
            new(
                LatencyFrames,
                TailFrames);

        public void Process(
            Span<float> interleavedSamples,
            in MediaAudioProcessContext context)
        {
            int channels =
                context.Format.ChannelCount;
            Assert.Equal(2, channels);
            int historyFrames =
                _history.Length /
                channels;
            for (int frame = 0;
                 frame < context.FrameCount;
                 frame++)
            {
                int writeFrame = _cursor;
                int latencyFrame =
                    (_cursor -
                     LatencyFrames +
                     historyFrames) %
                    historyFrames;
                int echoFrame =
                    (_cursor -
                     LatencyFrames -
                     TailFrames +
                     historyFrames) %
                    historyFrames;
                for (int channel = 0;
                     channel < channels;
                     channel++)
                {
                    int sample =
                        frame *
                        channels +
                        channel;
                    int write =
                        writeFrame *
                        channels +
                        channel;
                    float input =
                        interleavedSamples[sample];
                    float output =
                        _history[
                            latencyFrame *
                            channels +
                            channel] +
                        _history[
                            echoFrame *
                            channels +
                            channel] *
                        0.5f;
                    _history[write] = input;
                    interleavedSamples[sample] =
                        output;
                }
                _cursor =
                    (_cursor + 1) %
                    historyFrames;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestLinuxAacEncoderFactory :
        ILinuxAacEncoderFactory
    {
        internal ILinuxAacEncoder? Encoder
        {
            get;
            private set;
        }

        public bool CanEncode(
            in LinuxAacEncoderConfiguration configuration) =>
            configuration.SampleRate == 8_000 &&
            configuration.ChannelCount == 2 &&
            configuration.Bitrate == 96_000 &&
            configuration.TotalFrameCount > 0;

        public ILinuxAacEncoder Create(
            in LinuxAacEncoderConfiguration configuration,
            ILinuxAacAccessUnitSink sink)
        {
            Assert.True(
                CanEncode(in configuration));
            var encoder =
                new TestLinuxAacEncoder(
                    configuration,
                    sink);
            Encoder = encoder;
            return encoder;
        }
    }

    private sealed class TestLinuxAacEncoder :
        ILinuxAacEncoder
    {
        private readonly LinuxAacEncoderConfiguration
            _configuration;
        private readonly ILinuxAacAccessUnitSink _sink;
        private long _nextFrame;
        private bool _completed;

        internal TestLinuxAacEncoder(
            in LinuxAacEncoderConfiguration configuration,
            ILinuxAacAccessUnitSink sink)
        {
            _configuration = configuration;
            _sink = sink;
            sink.Configure(
                [0x15, 0x90],
                encoderDelayFrameCount: 0);
        }

        internal int BlockCount { get; private set; }
        internal long LastFirstFrame { get; private set; }
        internal int ScalarSampleCount { get; private set; }
        internal int Checksum { get; private set; }

        public void EncodePcm16(
            long firstFrame,
            ReadOnlySpan<short> interleavedSamples)
        {
            Assert.False(_completed);
            Assert.Equal(_nextFrame, firstFrame);
            Assert.Equal(
                0,
                interleavedSamples.Length %
                _configuration.ChannelCount);
            uint frameCount =
                checked(
                    (uint)(
                        interleavedSamples.Length /
                        _configuration.ChannelCount));
            for (int index = 0;
                 index <
                    interleavedSamples.Length;
                 index++)
            {
                Checksum = checked(
                    Checksum +
                    interleavedSamples[index]);
            }
            Span<byte> packet =
                stackalloc byte[4];
            BinaryPrimitives
                .WriteInt32BigEndian(
                    packet,
                    Checksum);
            _sink.WriteAccessUnit(
                packet,
                frameCount);
            _nextFrame = checked(
                _nextFrame +
                frameCount);
            LastFirstFrame = firstFrame;
            ScalarSampleCount = checked(
                ScalarSampleCount +
                interleavedSamples.Length);
            BlockCount++;
        }

        public void Complete()
        {
            Assert.Equal(
                _configuration.TotalFrameCount,
                _nextFrame);
            _completed = true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ConstantPcm16TimelineSource :
        ILinuxPcm16TimelineSource
    {
        private readonly short _sample;

        internal ConstantPcm16TimelineSource(
            short sample)
        {
            _sample = sample;
        }

        internal bool IsDisposed { get; private set; }

        public void ReadFrames(
            long firstFrame,
            Span<short> destination)
        {
            Assert.False(IsDisposed);
            Assert.True(firstFrame >= 0);
            destination.Fill(_sample);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class ImpulsePcm16TimelineSource :
        ILinuxPcm16TimelineSource
    {
        private readonly short _amplitude;
        private readonly int _channelCount;

        internal ImpulsePcm16TimelineSource(
            short amplitude,
            int channelCount)
        {
            _amplitude = amplitude;
            _channelCount = channelCount;
        }

        public void ReadFrames(
            long firstFrame,
            Span<short> destination)
        {
            destination.Clear();
            int frameCount =
                destination.Length /
                _channelCount;
            for (int frame = 0;
                 frame < frameCount;
                 frame++)
            {
                if (firstFrame + frame != 0)
                {
                    continue;
                }
                for (int channel = 0;
                     channel < _channelCount;
                     channel++)
                {
                    destination[
                        frame *
                        _channelCount +
                        channel] =
                        _amplitude;
                }
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Pcm16BlockCollector
    {
        private readonly short[] _samples;
        private readonly int _channels;
        private readonly List<long>
            _blockStarts = [];

        internal Pcm16BlockCollector(
            int frameCount,
            int channelCount)
        {
            _samples =
                new short[
                    checked(
                        frameCount *
                        channelCount)];
            _channels = channelCount;
        }

        internal int BlockCount =>
            _blockStarts.Count;

        internal IReadOnlyList<long>
            BlockStarts =>
                _blockStarts;

        internal void Accept(
            long firstFrame,
            ReadOnlySpan<short> samples)
        {
            _blockStarts.Add(firstFrame);
            samples.CopyTo(
                _samples.AsSpan(
                    checked(
                        (int)firstFrame *
                        _channels)));
        }

        internal short SampleAt(
            int frame,
            int channel) =>
            _samples[
                checked(
                    frame *
                    _channels +
                    channel)];
    }

    private sealed class Pcm16ChecksumSink
    {
        internal int BlockCount { get; private set; }

        internal long Checksum { get; private set; }

        internal void Accept(
            long firstFrame,
            ReadOnlySpan<short> samples)
        {
            BlockCount++;
            long checksum = Checksum + firstFrame;
            for (int index = 0;
                 index < samples.Length;
                 index++)
            {
                checksum += samples[index];
            }
            Checksum = checksum;
        }

        internal void Reset()
        {
            BlockCount = 0;
            Checksum = 0;
        }
    }

    private static byte[] BuildSyntheticH264Movie()
    {
        byte[] avcConfiguration =
            [1, 100, 0, 31, 0xFF];
        byte[] avcC = Box("avcC", avcConfiguration);
        var visualHeader = new byte[78];
        BinaryPrimitives.WriteUInt16BigEndian(
            visualHeader.AsSpan(24),
            1920);
        BinaryPrimitives.WriteUInt16BigEndian(
            visualHeader.AsSpan(26),
            1080);
        byte[] avc1 = Box(
            "avc1",
            Concat(visualHeader, avcC));

        byte[] stsd = Box(
            "stsd",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                avc1));
        byte[] stts = Box(
            "stts",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                UInt32(2),
                UInt32(1_000)));
        byte[] ctts = Box(
            "ctts",
            Concat(
                FullBoxHeader(1),
                UInt32(2),
                UInt32(1),
                Int32(0),
                UInt32(1),
                Int32(-100)));
        byte[] stsc = Box(
            "stsc",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                UInt32(1),
                UInt32(2),
                UInt32(1)));
        byte[] stsz = Box(
            "stsz",
            Concat(
                FullBoxHeader(0),
                UInt32(0),
                UInt32(2),
                UInt32(5),
                UInt32(7)));
        byte[] stco = Box(
            "stco",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                UInt32(1_000)));
        byte[] stss = Box(
            "stss",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                UInt32(1)));
        byte[] stbl = Box(
            "stbl",
            Concat(
                stsd,
                stts,
                ctts,
                stsc,
                stsz,
                stco,
                stss));
        byte[] minf = Box("minf", stbl);

        byte[] mdhd = Box(
            "mdhd",
            Concat(
                FullBoxHeader(0),
                UInt32(0),
                UInt32(0),
                UInt32(1_000),
                UInt32(2_000),
                UInt32(0)));
        byte[] hdlr = Box(
            "hdlr",
            Concat(
                FullBoxHeader(0),
                UInt32(0),
                FourCc("vide")));
        byte[] mdia = Box(
            "mdia",
            Concat(mdhd, hdlr, minf));
        byte[] elst = Box(
            "elst",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                UInt32(2_000),
                Int32(0),
                Int16(1),
                Int16(0)));
        byte[] mvhd = Box(
            "mvhd",
            Concat(
                FullBoxHeader(0),
                UInt32(0),
                UInt32(0),
                UInt32(1_000),
                UInt32(2_000)));
        return Box(
            "moov",
            Concat(
                mvhd,
                Box(
                    "trak",
                    Concat(
                        Box("edts", elst),
                        mdia))));
    }

    private static byte[] BuildSyntheticH264AacMovie()
    {
        const int videoOffset = 4_096;
        const int audioOffset = 8_192;
        const int audioSampleCount = 96;
        const int audioSampleSize = 4;

        byte[] avcConfiguration =
            [1, 100, 0, 31, 0xFF];
        byte[] avcC = Box(
            "avcC",
            avcConfiguration);
        var visualHeader = new byte[78];
        BinaryPrimitives.WriteUInt16BigEndian(
            visualHeader.AsSpan(6),
            1);
        BinaryPrimitives.WriteUInt16BigEndian(
            visualHeader.AsSpan(24),
            1_920);
        BinaryPrimitives.WriteUInt16BigEndian(
            visualHeader.AsSpan(26),
            1_080);
        byte[] videoEntry = Box(
            "avc1",
            Concat(
                visualHeader,
                avcC));
        byte[] videoTrack =
            BuildSyntheticTrack(
                "vide",
                timescale: 1_000,
                duration: 2_000,
                videoEntry,
                sampleCount: 2,
                sampleDuration: 1_000,
                uniformSampleSize: 0,
                sampleSizes:
                    [5, 7],
                chunkOffset: videoOffset,
                syncSamples: [1]);

        var audioHeader = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(
            audioHeader.AsSpan(6),
            1);
        BinaryPrimitives.WriteUInt16BigEndian(
            audioHeader.AsSpan(16),
            2);
        BinaryPrimitives.WriteUInt16BigEndian(
            audioHeader.AsSpan(18),
            16);
        BinaryPrimitives.WriteUInt32BigEndian(
            audioHeader.AsSpan(24),
            48_000u << 16);
        byte[] esds = Box(
            "esds",
            [
                0, 0, 0, 0,
                0x03, 0x19, 0, 1, 0,
                0x04, 0x11, 0x40, 0x15,
                0, 0, 0,
                0, 0, 5, 0xDC,
                0, 0, 5, 0xDC,
                0x05, 0x02, 0x11, 0x90,
                0x06, 0x01, 0x02
            ]);
        byte[] audioEntry = Box(
            "mp4a",
            Concat(
                audioHeader,
                esds));
        byte[] audioTrack =
            BuildSyntheticTrack(
                "soun",
                timescale: 48_000,
                duration:
                    audioSampleCount * 1_024,
                audioEntry,
                sampleCount:
                    audioSampleCount,
                sampleDuration: 1_024,
                uniformSampleSize:
                    audioSampleSize,
                sampleSizes: [],
                chunkOffset: audioOffset,
                syncSamples: []);

        byte[] movie = Box(
            "moov",
            Concat(
                videoTrack,
                audioTrack));
        if (movie.Length >= videoOffset)
        {
            throw new InvalidOperationException(
                "The synthetic movie metadata overlaps its video payload.");
        }
        var result = new byte[
            audioOffset +
            audioSampleCount *
            audioSampleSize];
        movie.CopyTo(result, 0);
        new byte[]
        {
            1, 2, 3, 4, 5,
            6, 7, 8, 9, 10, 11, 12
        }.CopyTo(
            result,
            videoOffset);
        for (int sample = 0;
             sample < audioSampleCount;
             sample++)
        {
            int offset = checked(
                audioOffset +
                sample *
                audioSampleSize);
            result[offset] =
                checked((byte)sample);
            result[offset + 1] = 0xAA;
            result[offset + 2] = 0x55;
            result[offset + 3] = 0xCC;
        }
        return result;
    }

    private static byte[] BuildSyntheticPcmMovie()
    {
        const int payloadOffset = 4_096;
        const int sampleCount = 2;
        const int framesPerSample = 2;
        const int channels = 2;
        const int bytesPerScalar = 2;
        const int sampleSize =
            framesPerSample *
            channels *
            bytesPerScalar;

        var audioHeader = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(
            audioHeader.AsSpan(6),
            1);
        BinaryPrimitives.WriteUInt16BigEndian(
            audioHeader.AsSpan(16),
            channels);
        BinaryPrimitives.WriteUInt16BigEndian(
            audioHeader.AsSpan(18),
            16);
        BinaryPrimitives.WriteUInt32BigEndian(
            audioHeader.AsSpan(24),
            8_000u << 16);
        byte[] sampleEntry =
            Box(
                "sowt",
                audioHeader);
        byte[] track =
            BuildSyntheticTrack(
                "soun",
                timescale: 8_000,
                duration:
                    sampleCount *
                    framesPerSample,
                sampleEntry,
                sampleCount,
                sampleDuration:
                    framesPerSample,
                uniformSampleSize:
                    sampleSize,
                sampleSizes: [],
                chunkOffset:
                    payloadOffset,
                syncSamples: []);
        byte[] movie =
            Box(
                "moov",
                track);
        Assert.True(
            movie.Length < payloadOffset);

        var result =
            new byte[
                payloadOffset +
                sampleCount *
                sampleSize];
        movie.CopyTo(result, 0);
        short[] samples =
        [
            100, 200,
            300, 400,
            500, 600,
            700, 800
        ];
        for (int index = 0;
             index < samples.Length;
             index++)
        {
            BinaryPrimitives
                .WriteInt16LittleEndian(
                    result.AsSpan(
                        payloadOffset +
                        index *
                        bytesPerScalar),
                    samples[index]);
        }
        return result;
    }

    private static byte[]
        BuildSyntheticH264WebVttMovie()
    {
        const int videoOffset = 4_096;
        const int textOffset = 8_192;

        byte[] avcConfiguration =
            [1, 100, 0, 31, 0xFF];
        byte[] avcC = Box(
            "avcC",
            avcConfiguration);
        var visualHeader = new byte[78];
        BinaryPrimitives.WriteUInt16BigEndian(
            visualHeader.AsSpan(6),
            1);
        BinaryPrimitives.WriteUInt16BigEndian(
            visualHeader.AsSpan(24),
            1_920);
        BinaryPrimitives.WriteUInt16BigEndian(
            visualHeader.AsSpan(26),
            1_080);
        byte[] videoEntry = Box(
            "avc1",
            Concat(
                visualHeader,
                avcC));
        byte[] videoTrack =
            BuildSyntheticTrack(
                "vide",
                timescale: 1_000,
                duration: 2_000,
                videoEntry,
                sampleCount: 2,
                sampleDuration: 1_000,
                uniformSampleSize: 0,
                sampleSizes:
                    [5, 7],
                chunkOffset: videoOffset,
                syncSamples: [1]);

        var textHeader = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(
            textHeader.AsSpan(6),
            1);
        byte[] textEntry = Box(
            "wvtt",
            Concat(
                textHeader,
                Box(
                    "vttC",
                    Encoding.UTF8.GetBytes(
                        "WEBVTT\n"))));
        byte[] firstTextSample = Concat(
            Box(
                "vttc",
                Box(
                    "iden",
                    Encoding.UTF8.GetBytes(
                        "opening")),
                Box(
                    "sttg",
                    Encoding.UTF8.GetBytes(
                        "line:80%,center position:25%,start size:50% align:center vertical:rl region:captions")),
                Box(
                    "payl",
                    Encoding.UTF8.GetBytes(
                        "Hello <b>GPU &amp; <i>effects</i></b>\n<u>Ready</u>"))),
            Box(
                "vttc",
                Box(
                    "payl",
                    Encoding.UTF8.GetBytes(
                        "Second line"))));
        byte[] secondTextSample =
            Box("vtte", []);
        byte[] textTrack =
            BuildSyntheticTrack(
                "text",
                timescale: 1_000,
                duration: 2_000,
                textEntry,
                sampleCount: 2,
                sampleDuration: 1_000,
                uniformSampleSize: 0,
                sampleSizes:
                    [
                        firstTextSample.Length,
                        secondTextSample.Length
                    ],
                chunkOffset: textOffset,
                syncSamples: [],
                language: "eng",
                handlerName: "English");

        byte[] movie = Box(
            "moov",
            Concat(
                videoTrack,
                textTrack));
        if (movie.Length >= videoOffset)
        {
            throw new InvalidOperationException(
                "The synthetic movie metadata overlaps its video payload.");
        }
        var result = new byte[
            textOffset +
            firstTextSample.Length +
            secondTextSample.Length];
        movie.CopyTo(result, 0);
        new byte[]
        {
            1, 2, 3, 4, 5,
            6, 7, 8, 9, 10, 11, 12
        }.CopyTo(
            result,
            videoOffset);
        firstTextSample.CopyTo(
            result,
            textOffset);
        secondTextSample.CopyTo(
            result,
            textOffset +
            firstTextSample.Length);
        return result;
    }

    private static IsoBmffTrack
        CreateSyntheticWebVttTrack(
            int sampleSize) =>
        new(
            IsoBmffTrackKind.TimedMetadata,
            IsoBmffCodec.WebVtt,
            1_000,
            1_000,
            0,
            0,
            0,
            [],
            [
                new IsoBmffSample(
                    0,
                    sampleSize,
                    0,
                    0,
                    1_000,
                    IsSync: true)
            ]);

    private static IsoBmffTrack
        CreateSyntheticIndexedTrack(
            IsoBmffTrackKind kind,
            IsoBmffCodec codec) =>
        new(
            kind,
            codec,
            1_000,
            2_000,
            kind == IsoBmffTrackKind.Video
                ? (ushort)1_920
                : (ushort)0,
            kind == IsoBmffTrackKind.Video
                ? (ushort)1_080
                : (ushort)0,
            codec is
                IsoBmffCodec.H264 or
                IsoBmffCodec.H265
                    ? 4
                    : 0,
            [],
            [
                new IsoBmffSample(
                    0,
                    1,
                    0,
                    0,
                    1_000,
                    IsSync: true)
            ]);

    private static byte[] BuildSyntheticTrack(
        string handler,
        uint timescale,
        int duration,
        byte[] sampleEntry,
        int sampleCount,
        int sampleDuration,
        int uniformSampleSize,
        int[] sampleSizes,
        int chunkOffset,
        int[] syncSamples,
        string language = "",
        string handlerName = "")
    {
        byte[] stsd = Box(
            "stsd",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                sampleEntry));
        byte[] stts = Box(
            "stts",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                UInt32(
                    checked((uint)sampleCount)),
                UInt32(
                    checked(
                        (uint)sampleDuration))));
        byte[] stsc = Box(
            "stsc",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                UInt32(1),
                UInt32(
                    checked((uint)sampleCount)),
                UInt32(1)));
        var sizeValues =
            new List<byte[]>
            {
                FullBoxHeader(0),
                UInt32(
                    checked(
                        (uint)uniformSampleSize)),
                UInt32(
                    checked((uint)sampleCount))
            };
        for (int index = 0;
             index < sampleSizes.Length;
             index++)
        {
            sizeValues.Add(
                UInt32(
                    checked(
                        (uint)sampleSizes[index])));
        }
        byte[] stsz = Box(
            "stsz",
            Concat(sizeValues.ToArray()));
        byte[] stco = Box(
            "stco",
            Concat(
                FullBoxHeader(0),
                UInt32(1),
                UInt32(
                    checked((uint)chunkOffset))));
        var tableChildren =
            new List<byte[]>
            {
                stsd,
                stts,
                stsc,
                stsz,
                stco
            };
        if (syncSamples.Length != 0)
        {
            var syncValues =
                new List<byte[]>
                {
                    FullBoxHeader(0),
                    UInt32(
                        checked(
                            (uint)syncSamples
                                .Length))
                };
            for (int index = 0;
                 index < syncSamples.Length;
                 index++)
            {
                syncValues.Add(
                    UInt32(
                        checked(
                            (uint)syncSamples[index])));
            }
            tableChildren.Add(
                Box(
                    "stss",
                    Concat(
                        syncValues.ToArray())));
        }
        byte[] stbl = Box(
            "stbl",
            Concat(
                tableChildren.ToArray()));
        byte[] minf = Box(
            "minf",
            stbl);
        byte[] mdhd = Box(
            "mdhd",
            Concat(
                FullBoxHeader(0),
                UInt32(0),
                UInt32(0),
                UInt32(timescale),
                UInt32(
                    checked((uint)duration)),
                UInt16(PackLanguage(language)),
                UInt16(0)));
        byte[] handlerNameBytes =
            string.IsNullOrEmpty(handlerName)
                ? []
                : Concat(
                    Encoding.UTF8.GetBytes(
                        handlerName),
                    [0]);
        byte[] hdlr = Box(
            "hdlr",
            Concat(
                FullBoxHeader(0),
                UInt32(0),
                FourCc(handler),
                new byte[12],
                handlerNameBytes));
        byte[] mdia = Box(
            "mdia",
            Concat(
                mdhd,
                hdlr,
                minf));
        return Box(
            "trak",
            mdia);
    }

    private static ushort PackLanguage(
        string language)
    {
        if (language.Length != 3)
        {
            return 0;
        }
        int first = language[0] - 'a' + 1;
        int second = language[1] - 'a' + 1;
        int third = language[2] - 'a' + 1;
        if (first is < 1 or > 26 ||
            second is < 1 or > 26 ||
            third is < 1 or > 26)
        {
            return 0;
        }
        return checked(
            (ushort)(
                first << 10 |
                second << 5 |
                third));
    }

    private static IsoBmffCompositionEdit[]
        ReadFirstEditList(byte[] movie)
    {
        ReadOnlySpan<byte> marker =
            [(byte)'e', (byte)'l', (byte)'s', (byte)'t'];
        int typeOffset =
            movie.AsSpan().IndexOf(marker);
        Assert.True(
            typeOffset >= 4,
            "The output contains no elst box.");
        int position = checked(
            typeOffset + marker.Length);
        Assert.Equal(1, movie[position]);
        position = checked(position + 4);
        uint count =
            BinaryPrimitives
                .ReadUInt32BigEndian(
                    movie.AsSpan(
                        position,
                        4));
        position = checked(position + 4);
        var result =
            new IsoBmffCompositionEdit[
                checked((int)count)];
        for (int index = 0;
             index < result.Length;
             index++)
        {
            ulong segmentDuration =
                BinaryPrimitives
                    .ReadUInt64BigEndian(
                        movie.AsSpan(
                            position,
                            8));
            long mediaTime =
                BinaryPrimitives
                    .ReadInt64BigEndian(
                        movie.AsSpan(
                            position + 8,
                            8));
            ushort rate =
                BinaryPrimitives
                    .ReadUInt16BigEndian(
                        movie.AsSpan(
                            position + 16,
                            2));
            ushort rateFraction =
                BinaryPrimitives
                    .ReadUInt16BigEndian(
                        movie.AsSpan(
                            position + 18,
                            2));
            Assert.Equal(
                (ushort)1,
                rate);
            Assert.Equal(
                (ushort)0,
                rateFraction);
            result[index] =
                new IsoBmffCompositionEdit(
                    segmentDuration,
                    mediaTime);
            position = checked(
                position + 20);
        }
        return result;
    }

    private sealed class TestOwner : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class InlineProgress<T>(
        Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static byte[] Box(
        string type,
        params byte[][] payloads)
    {
        byte[] payload = Concat(payloads);
        byte[] result = new byte[checked(payload.Length + 8)];
        BinaryPrimitives.WriteUInt32BigEndian(
            result,
            checked((uint)result.Length));
        FourCc(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] FullBoxHeader(byte version) =>
        [version, 0, 0, 0];

    private static byte[] UInt32(uint value)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, value);
        return result;
    }

    private static byte[] UInt16(ushort value)
    {
        var result = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(result, value);
        return result;
    }

    private static byte[] Int32(int value)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(result, value);
        return result;
    }

    private static byte[] Int16(short value)
    {
        var result = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(result, value);
        return result;
    }

    private static byte[] FourCc(string value) =>
        [(byte)value[0], (byte)value[1], (byte)value[2], (byte)value[3]];

    private static byte[] Concat(params byte[][] values)
    {
        int length = 0;
        foreach (byte[] value in values)
        {
            length = checked(length + value.Length);
        }
        var result = new byte[length];
        int offset = 0;
        foreach (byte[] value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }
        return result;
    }

    private static string ReadRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory =
                 new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate =
                Path.Combine(
                    [directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(pathParts)}'.");
    }
}
