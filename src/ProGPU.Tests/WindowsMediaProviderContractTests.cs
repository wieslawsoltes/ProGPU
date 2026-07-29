extern alias WindowsMediaProvider;

using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using WindowsMediaFoundationCompositionExportProvider =
    WindowsMediaProvider::ProGPU.Windows.Media
        .WindowsMediaFoundationCompositionExportProvider;
using WindowsMediaFoundationCompositionThumbnailProvider =
    WindowsMediaProvider::ProGPU.Windows.Media
        .WindowsMediaFoundationCompositionThumbnailProvider;
using WindowsPcm16GainProcessor =
    WindowsMediaProvider::ProGPU.Windows.Media
        .WindowsPcm16GainProcessor;
using Xunit;

namespace ProGPU.Tests;

public sealed class WindowsMediaProviderContractTests
{
    [Fact]
    public void WindowsThumbnailProviderAcceptsGpuComposableTimelines()
    {
        MediaCompositionExportRequest composition =
            CreatePreciseRequest();
        var request =
            new MediaCompositionThumbnailRequest(
                composition,
                [
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(7)
                ],
                1280,
                720,
                MediaCompositionThumbnailPrecision
                    .NearestFrame);

        Assert.True(
            WindowsMediaFoundationCompositionThumbnailProvider
                .IsRequestSupported(
                    request,
                    isWindows: true));
        Assert.False(
            WindowsMediaFoundationCompositionThumbnailProvider
                .IsRequestSupported(
                    request,
                    isWindows: false));
        Assert.False(
            WindowsMediaFoundationCompositionThumbnailProvider
                .IsRequestSupported(
                    request with
                    {
                        Positions =
                        [
                            TimeSpan.FromSeconds(8)
                        ]
                    },
                    isWindows: true));
        Assert.False(
            WindowsMediaFoundationCompositionThumbnailProvider
                .IsRequestSupported(
                    request with
                    {
                        Composition =
                            composition with
                            {
                                OverlayLayers =
                                [
                                    new MediaCompositionExportOverlayLayer(
                                        [])
                                ]
                            }
                    },
                    isWindows: true));
    }

    [Fact]
    public void WindowsPreciseExporterAcceptsOnlyItsNativeContract()
    {
        MediaCompositionExportRequest request =
            CreatePreciseRequest();

        Assert.True(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request,
                    isWindows: true));
        Assert.False(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request with
                    {
                        TrimmingMode =
                            MediaCompositionTrimmingMode.Fast
                    },
                    isWindows: true));
        Assert.False(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request,
                    isWindows: false));
    }

    [Fact]
    public void WindowsPreciseExporterAcceptsAttenuationAndRejectsOtherCompositionWork()
    {
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip clip =
            request.Clips[0];
        var effect = new MediaCompositionEffectDefinition(
            "ProGPU.Media.Brightness",
            new Dictionary<string, object?>
            {
                ["Amount"] = 0.5d
            });

        Assert.True(
            IsSupported(
                request with
                {
                    Clips =
                    [
                        clip with
                        {
                            Volume = 0.5d
                        }
                    ]
                }));
        Assert.False(
            IsSupported(
                request with
                {
                    Clips =
                    [
                        clip with
                        {
                            Volume = 1.01d
                        }
                    ]
                }));
        Assert.False(
            IsSupported(
                request with
                {
                    Clips =
                    [
                        clip with
                        {
                            VideoEffectDefinitions =
                                [effect]
                        }
                    ]
                }));
        Assert.False(
            IsSupported(
                request with
                {
                    BackgroundAudioTracks =
                    [
                        new MediaCompositionExportAudioTrack(
                            clip.SourceUri!,
                            clip.OriginalDuration,
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            1d,
                            new Dictionary<string, string>())
                    ]
                }));
        Assert.False(
            IsSupported(
                request with
                {
                    OverlayLayers =
                    [
                        new MediaCompositionExportOverlayLayer(
                        [
                            new MediaCompositionExportOverlay(
                                clip,
                                TimeSpan.Zero,
                                0d,
                                0d,
                                1d,
                                1d,
                                1d,
                                false)
                        ])
                    ]
                }));
    }

    [Fact]
    public void WindowsPreciseExporterAcceptsRegisteredGainDefinitions()
    {
        const string gainId =
            "ProGPU.Tests.Windows.ExportGain";
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new MediaAudioGainEffectFactory(
                    gainId));
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip clip =
            request.Clips[0];
        MediaCompositionEffectDefinition definition =
            new(
                gainId,
                new Dictionary<string, object?>
                {
                    [MediaAudioGainEffectFactory
                        .GainPropertyName] = 0.5d
                });

        Assert.True(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request with
                    {
                        Clips =
                        [
                            clip with
                            {
                                Volume = 0.8d,
                                AudioEffectDefinitions =
                                    [definition]
                            }
                        ]
                    },
                    isWindows: true,
                    effects: registry));
        Assert.True(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request with
                    {
                        Clips =
                        [
                            clip with
                            {
                                AudioEffectDefinitions =
                                [
                                    definition with
                                    {
                                        Properties =
                                            new Dictionary<
                                                string,
                                                object?>
                                            {
                                                [MediaAudioGainEffectFactory
                                                    .GainPropertyName] =
                                                    2d
                                            }
                                    }
                                ]
                            }
                        ]
                    },
                    isWindows: true,
                    effects: registry));
        Assert.False(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request with
                    {
                        Clips =
                        [
                            clip with
                            {
                                AudioEffectDefinitions =
                                [
                                    definition with
                                    {
                                        Properties =
                                            new Dictionary<
                                                string,
                                                object?>
                                            {
                                                [MediaAudioGainEffectFactory
                                                    .GainPropertyName] =
                                                    2.01d
                                            }
                                    }
                                ]
                            }
                        ]
                    },
                    isWindows: true,
                    effects: registry));
        Assert.False(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request with
                    {
                        Clips =
                        [
                            clip with
                            {
                                AudioEffectDefinitions =
                                [
                                    new(
                                        "ProGPU.Tests.Unregistered",
                                        new Dictionary<
                                            string,
                                            object?>())
                                ]
                            }
                        ]
                    },
                    isWindows: true,
                    effects: registry));
    }

    [Fact]
    public void WindowsPreciseExporterValidatesProfileAndTrimBounds()
    {
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip clip =
            request.Clips[0];

        Assert.False(
            IsSupported(
                request with
                {
                    EncodingProfile =
                        request.EncodingProfile with
                        {
                            VideoSubtype = "HEVC"
                        }
                }));
        Assert.False(
            IsSupported(
                request with
                {
                    EncodingProfile =
                        request.EncodingProfile with
                        {
                            Width = 0
                        }
                }));
        Assert.False(
            IsSupported(
                request with
                {
                    Clips =
                    [
                        clip with
                        {
                            TrimTimeFromStart =
                                clip.OriginalDuration
                        }
                    ]
                }));
        Assert.False(
            IsSupported(
                request with
                {
                    DestinationPath = " "
                }));
    }

    [Fact]
    public void WindowsPreciseExporterReportsNativeCopyPathHonestly()
    {
        MediaCompositionExportCapabilities capabilities =
            WindowsMediaFoundationCompositionExportProvider
                .CreateCapabilities(CreatePreciseRequest());

        Assert.Equal(
            "progpu.windows.mediafoundation.export",
            capabilities.ProviderId);
        Assert.Equal(
            MediaCompositionExportVideoPath.NativeGpuSurface,
            capabilities.VideoPath);
        Assert.Equal(
            MediaCompositionExportAudioPath.NativeBuffer,
            capabilities.AudioPath);
        Assert.True(
            capabilities.HardwareVideoEncoderRequested);
        Assert.False(
            capabilities.HardwareVideoEncoderGuaranteed);
        Assert.False(capabilities.EffectsBakedOnGpu);
        Assert.Contains(
            "runtime",
            capabilities.Limitation,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsPreciseExporterAcceptsBuiltInGpuEffects()
    {
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip clip =
            request.Clips[0];
        request = request with
        {
            Clips =
            [
                clip with
                {
                    UserData =
                        new Dictionary<string, string>
                        {
                            ["progpu.saturation"] = "0.5",
                            ["progpu.grayscale"] = "0.25"
                        }
                }
            ]
        };

        Assert.True(IsSupported(request));
        MediaCompositionExportCapabilities capabilities =
            WindowsMediaFoundationCompositionExportProvider
                .CreateCapabilities(request);
        Assert.Equal(
            MediaCompositionExportVideoPath.GpuCopy,
            capabilities.VideoPath);
        Assert.True(capabilities.EffectsBakedOnGpu);

        request = request with
        {
            Clips =
            [
                clip with
                {
                    UserData =
                        new Dictionary<string, string>
                        {
                            ["progpu.grayscale"] = "NaN"
                        }
                }
            ]
        };
        Assert.False(IsSupported(request));
    }

    [Fact]
    public void WindowsPreciseExporterAcceptsGpuGeneratedColorClips()
    {
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip sourceClip =
            request.Clips[0];
        var colorClip =
            new MediaCompositionExportClip(
                null,
                TimeSpan.FromSeconds(2),
                TimeSpan.Zero,
                TimeSpan.Zero,
                1d,
                0xff_20_40_80u,
                new Dictionary<string, string>());
        request = request with
        {
            Clips = [colorClip]
        };

        Assert.True(IsSupported(request));
        MediaCompositionExportCapabilities capabilities =
            WindowsMediaFoundationCompositionExportProvider
                .CreateCapabilities(request);
        Assert.Equal(
            MediaCompositionExportVideoPath.GpuCopy,
            capabilities.VideoPath);
        Assert.False(capabilities.EffectsBakedOnGpu);

        MediaCompositionExportRequest effectedRequest =
            request with
            {
                Clips =
                [
                    colorClip with
                    {
                        UserData =
                            new Dictionary<string, string>
                            {
                                ["progpu.grayscale"] = "1"
                            }
                    }
                ]
            };
        Assert.True(IsSupported(effectedRequest));
        Assert.True(
            WindowsMediaFoundationCompositionExportProvider
                .CreateCapabilities(effectedRequest)
                .EffectsBakedOnGpu);

        Assert.False(
            IsSupported(
                request with
                {
                    Clips =
                    [
                        colorClip with
                        {
                            SourceUri =
                                sourceClip.SourceUri
                        }
                    ]
                }));
        Assert.False(
            IsSupported(
                request with
                {
                    Clips =
                    [
                        colorClip with
                        {
                            ArgbColor = null
                        }
                    ]
                }));
    }

    [Fact]
    public void WindowsColorClipFrameClockHasNoNtscDrift()
    {
        long timestamp = 0;
        ulong remainder = 0;
        long[] firstThree = new long[3];
        for (int index = 0;
             index < 30_000;
             index++)
        {
            timestamp =
                WindowsMediaFoundationCompositionExportProvider
                    .GetNextColorFrameTimestamp(
                        timestamp,
                        ref remainder,
                        30_000,
                        1_001);
            if (index < firstThree.Length)
            {
                firstThree[index] = timestamp;
            }
        }

        Assert.Equal(
            [333_666, 667_333, 1_001_000],
            firstThree);
        Assert.Equal(
            TimeSpan.FromSeconds(1_001).Ticks,
            timestamp);
        Assert.Equal(0ul, remainder);
    }

    [Theory]
    [InlineData(1u, 44_100u, 96_000u, true)]
    [InlineData(2u, 48_000u, 192_000u, true)]
    [InlineData(6u, 48_000u, 192_000u, false)]
    [InlineData(2u, 96_000u, 192_000u, false)]
    [InlineData(2u, 48_000u, 256_000u, false)]
    public void WindowsPreciseExporterUsesDocumentedAacProfiles(
        uint channels,
        uint sampleRate,
        uint bitrate,
        bool expected)
    {
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        request = request with
        {
            EncodingProfile =
                request.EncodingProfile with
                {
                    AudioChannelCount = channels,
                    AudioSampleRate = sampleRate,
                    AudioBitrate = bitrate
                }
        };

        Assert.Equal(expected, IsSupported(request));
    }

    [Fact]
    public void WindowsPcmGainIsDeterministicSaturatingAndAllocationFree()
    {
        short[] samples =
        [
            short.MinValue,
            -3,
            -2,
            -1,
            0,
            1,
            2,
            3,
            short.MaxValue
        ];
        WindowsPcm16GainProcessor.Apply(
            samples,
            0.5d);
        Assert.Equal(
            [
                -16_384,
                -1,
                -1,
                0,
                0,
                0,
                1,
                1,
                16_383
            ],
            samples);

        short[] amplified =
        [
            short.MinValue,
            -20_000,
            -16_384,
            -1,
            0,
            1,
            16_384,
            20_000,
            short.MaxValue
        ];
        WindowsPcm16GainProcessor.Apply(
            amplified,
            2d);
        Assert.Equal(
            [
                short.MinValue,
                short.MinValue,
                short.MinValue,
                -2,
                0,
                2,
                short.MaxValue,
                short.MaxValue,
                short.MaxValue
            ],
            amplified);

        WindowsPcm16GainProcessor.Apply(
            samples,
            0d);
        Assert.All(
            samples,
            static value => Assert.Equal(0, value));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0;
             iteration < 1_000;
             iteration++)
        {
            WindowsPcm16GainProcessor.Apply(
                samples,
                1d);
        }
        long after =
            GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(-0.01d)]
    [InlineData(2.01d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WindowsPcmGainRejectsInvalidGain(
        double gain)
    {
        short[] samples = [1];
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                WindowsPcm16GainProcessor.Apply(
                    samples,
                    gain));
    }

    [Fact]
    public async Task WindowsPreciseExporterObservesPreCanceledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider =
            new WindowsMediaFoundationCompositionExportProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
                await provider.RenderAsync(
                    CreatePreciseRequest(),
                    progress: null,
                    cancellation.Token));
    }

    [Fact]
    public void WindowsPreciseExporterUsesDxgiNv12AndTransactionalOutput()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaFoundationCompositionExportProvider.cs");
        string native = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaTranscodeNative.cs");
        string registration = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaPlaybackProvider.cs");

        Assert.Contains(
            "CreateTranscodeSourceReader(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateTranscodeSinkWriter(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateNv12VideoType(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "File.Move(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetSampleDuration(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetSampleDuration(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            ".tmp.mp4",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ec822da2-e1e9-4b29-a0d8-563c719f5269",
            native,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "3231564e-0000-0010-8000-00aa00389b71",
            native,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "MFCreateSourceReaderFromURL",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "MFCreateSinkWriterFromURL",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TextureWrite",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsMediaFoundationCompositionExportProvider",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryCaptureAudioGains(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "audioGains[index]",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyPcm16Gain(",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsGpuEffectExportUsesBoundedTrackedDxgiTargets()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaFoundationCompositionExportProvider.cs");
        string sink = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsDxgiGpuEffectFrameSink.cs");
        string callback = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaTrackedSampleCallback.cs");
        string native = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaTranscodeNative.cs");

        Assert.Contains(
            "CreateArgb32VideoType(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsDxgiGpuEffectFrameSink",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int RingSize = 3;",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureBlitter.Blit(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureClearer.Clear(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcessColorAndWrite(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryImportDxgiRenderTarget(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateTrackedDxgiSample(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "MFCreateTrackedSample",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "MFCreateDXGISurfaceBuffer",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "e7174cfa-1c9e-48b1-8866-626226bfc258",
            native,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "a27003cf-2354-4f2a-8d6a-ab7cff15437e",
            callback,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "[UnmanagedCallersOnly(",
            callback,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MapAsync",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "SendSinkStreamTick(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetSampleDiscontinuity(",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WaitIdle(",
            sink,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsThumbnailsRetainNativeDecodeGpuAndReadbackState()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaFoundationCompositionThumbnailProvider.cs");
        string sink = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsDxgiGpuEffectFrameSink.cs");
        string native = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaNative.cs");
        string registration = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaPlaybackProvider.cs");
        string project = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "ProGPU.Windows.Media.csproj");

        Assert.Contains(
            "IMediaCompositionThumbnailProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClipReader?[] readers",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsDxgiGpuEffectFrameSink",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcessAndReadback(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcessColorAndReadback(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPngEncoder.Encode(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new WindowsMediaFoundationCompositionThumbnailProvider(",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            @"..\ProGPU.Media.Editing\ProGPU.Media.Editing.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly nint _readbackTexture;",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateBgraReadbackTexture(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadBgraTexture(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureBlitter.Blit(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(immediateContext)[14]",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            provider,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsProviderUsesStaticAotCompatibleNativeInterop()
    {
        string project = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "ProGPU.Windows.Media.csproj");
        string native = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaNative.cs");

        Assert.Contains(
            "<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "[LibraryImport(",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "[UnmanagedCallersOnly(",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeLibrary.",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetProcAddress",
            native,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsProviderKeepsVideoOnGpuAndReportsGpuCopy()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaPlaybackProvider.cs");
        string native = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaNative.cs");

        Assert.Contains(
            "WindowsMediaNative.TransferVideoFrame(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGpuExternalTextureHandleKind.DxgiSharedHandle",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "UsesKeyedMutex = true",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaTransferMode.GpuCopy",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "D3D11ResourceMiscSharedKeyedMutex",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "D3D11ResourceMiscSharedNtHandle",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaEngineAudioEndpointRole",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TextureWrite",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedTexturePoolDefersReleaseUntilDawnOwnerReturns()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaPlaybackProvider.cs");
        string dawn = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnGpuContext.cs");

        Assert.Contains(
            "_disposeRequested && _active == 0",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ExternalMediaGpuFrame(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "sharedMemory?.EndAccess(_texture);",
            dawn,
            StringComparison.Ordinal);
        Assert.True(
            dawn.IndexOf(
                "sharedMemory?.EndAccess(_texture);",
                StringComparison.Ordinal) <
            dawn.IndexOf(
                "nativeOwner?.Dispose();",
                StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsProviderUsesOfficialMediaEngineExAudioAndFrameContracts()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaPlaybackProvider.cs");
        string native = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaNative.cs");

        Assert.Contains(
            "83015ead-b1e6-40d0-a98a-37145ffe1ad1",
            native,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "VTable(extended)[49]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(extended)[51]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsMediaNative.SetBalance(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsMediaNative.FrameStep(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "IMediaAudioGraphEffect",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "SupportsFrameStepping: true",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AvaloniaWindowsSampleSelectsNativeDawnPresentation()
    {
        string program = ReadRepoFile(
            "src",
            "ProGPU.Samples.Avalonia",
            "Program.cs");
        string project = ReadRepoFile(
            "src",
            "ProGPU.Samples.Avalonia",
            "ProGPU.Samples.Avalonia.csproj");

        Assert.Contains(
            "ProGPU.Windows.Media.WindowsMedia.Register()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            ".UseWin32()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "UseDawnNativePresentation = true",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Windows.Media.csproj",
            project,
            StringComparison.Ordinal);
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

    private static bool IsSupported(
        MediaCompositionExportRequest request) =>
        WindowsMediaFoundationCompositionExportProvider
            .IsRequestSupported(
                request,
                isWindows: true);

    private static MediaCompositionExportRequest
        CreatePreciseRequest()
    {
        var clip = new MediaCompositionExportClip(
            new Uri("file:///C:/media/input.mp4"),
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
                1280,
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
                "progpu-windows-export.mp4"),
            [clip],
            MediaCompositionTrimmingMode.Precise,
            profile,
            new Dictionary<string, string>());
    }
}
