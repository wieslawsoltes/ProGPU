using Xunit;
using ProGPU.Linux.Media;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Browser;
using ProGPU.Media.Editing;
using ProGPU.Media.Containers;
using Windows.Media.Editing;
using Windows.Storage;

namespace ProGPU.Tests;

public sealed class LinuxMediaProviderContractTests
{
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
            "GetNextColorFrameTimestamp(",
            provider,
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
            "WaitIdle(",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels",
            sink,
            StringComparison.Ordinal);
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
        int[] syncSamples)
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
                UInt32(0)));
        byte[] hdlr = Box(
            "hdlr",
            Concat(
                FullBoxHeader(0),
                UInt32(0),
                FourCc(handler)));
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
