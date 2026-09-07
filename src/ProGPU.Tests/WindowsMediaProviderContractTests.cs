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
using WindowsPcm16Mixer =
    WindowsMediaProvider::ProGPU.Windows.Media
        .WindowsPcm16Mixer;
using WindowsPcm16MixLevels =
    WindowsMediaProvider::ProGPU.Windows.Media
        .WindowsPcm16MixLevels;
using WindowsMediaFoundationAudioMixer =
    WindowsMediaProvider::ProGPU.Windows.Media
        .WindowsMediaFoundationAudioMixer;
using WindowsMediaFoundationAudioPlanner =
    WindowsMediaProvider::ProGPU.Windows.Media
        .WindowsMediaFoundationAudioPlanner;
using WindowsMediaFoundationOverlayFrameComposer =
    WindowsMediaProvider::ProGPU.Windows.Media
        .WindowsMediaFoundationOverlayFrameComposer;
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
                                    {
                                        CustomCompositorDefinition =
                                            new MediaCompositionEffectDefinition(
                                                "ProGPU.Tests.CustomCompositor",
                                                new Dictionary<string, object?>())
                                    }
                                ]
                            }
                    },
                    isWindows: true));
    }

    [Fact]
    public void WindowsOverlayPlansPreserveLayerOrderAndResolveTimeline()
    {
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip clip =
            request.Clips[0];
        var first =
            new MediaCompositionExportOverlay(
                clip,
                TimeSpan.FromSeconds(2),
                128d,
                72d,
                640d,
                360d,
                0.75d,
                false);
        var second =
            first with
            {
                Delay = TimeSpan.FromSeconds(3),
                PositionX = 0d,
                Opacity = 0.5d
            };
        request =
            request with
            {
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                        [first]),
                    new MediaCompositionExportOverlayLayer(
                        [second])
                ]
            };

        Assert.True(
            WindowsMediaFoundationOverlayFrameComposer
                .TryCapturePlans(
                    request,
                    MediaEffectRegistry.Default,
                    includeAudio: true,
                    out var plans));
        Assert.Equal(2, plans.Length);
        Assert.Equal(
            TimeSpan.FromSeconds(2).Ticks,
            plans[0].StartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(3).Ticks,
            plans[1].StartTicks);
        Assert.Equal(0.1f, plans[0].Placement.X);
        Assert.Equal(0.1f, plans[0].Placement.Y);
        Assert.Equal(0.5f, plans[0].Placement.Width);
        Assert.Equal(0.5f, plans[0].Placement.Height);
        Assert.Equal(0.75f, plans[0].Placement.Opacity);
        Assert.False(
            plans[0].TryResolve(
                TimeSpan.FromSeconds(1).Ticks,
                out _));
        Assert.True(
            plans[0].TryResolve(
                TimeSpan.FromSeconds(4).Ticks,
                out long sourceTicks));
        Assert.Equal(
            TimeSpan.FromSeconds(3).Ticks,
            sourceTicks);

        plans[0].TryResolve(
            TimeSpan.FromSeconds(4).Ticks,
            out _);
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0;
             index < 100_000;
             index++)
        {
            plans[0].TryResolve(
                TimeSpan.FromSeconds(4).Ticks,
                out _);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WindowsAudioPlannerPreservesMainBackgroundAndOverlayTiming()
    {
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip clip =
            request.Clips[0];
        request =
            request with
            {
                BackgroundAudioTracks =
                [
                    new MediaCompositionExportAudioTrack(
                        new Uri(
                            "file:///C:/media/background.m4a"),
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(-2),
                        0.5d,
                        new Dictionary<string, string>()),
                    new MediaCompositionExportAudioTrack(
                        new Uri(
                            "file:///C:/media/delayed.m4a"),
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(2),
                        1d,
                        new Dictionary<string, string>())
                ],
                OverlayLayers =
                [
                    new MediaCompositionExportOverlayLayer(
                    [
                        new MediaCompositionExportOverlay(
                            clip,
                            TimeSpan.FromSeconds(2),
                            0d,
                            0d,
                            320d,
                            180d,
                            1d,
                            true)
                    ])
                ]
            };

        Assert.True(
            WindowsMediaFoundationAudioPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                includeAudio: true,
                out var plans,
                out long durationTicks));
        Assert.Equal(
            TimeSpan.FromSeconds(7).Ticks,
            durationTicks);
        Assert.Equal(4, plans.Length);

        Assert.Equal(
            TimeSpan.FromSeconds(1).Ticks,
            plans[0].SourceStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(8).Ticks,
            plans[0].SourceEndTicks);
        Assert.Equal(0, plans[0].DestinationStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(7).Ticks,
            plans[0].DestinationEndTicks);

        Assert.Equal(
            TimeSpan.FromSeconds(3).Ticks,
            plans[1].SourceStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(8).Ticks,
            plans[1].SourceEndTicks);
        Assert.Equal(0, plans[1].DestinationStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(5).Ticks,
            plans[1].DestinationEndTicks);
        Assert.Equal(16_384, plans[1].Levels.Left);
        Assert.Equal(16_384, plans[1].Levels.Right);

        Assert.Equal(
            TimeSpan.FromSeconds(1).Ticks,
            plans[2].SourceStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(6).Ticks,
            plans[2].SourceEndTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(2).Ticks,
            plans[2].DestinationStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(7).Ticks,
            plans[2].DestinationEndTicks);

        Assert.Equal(
            TimeSpan.FromSeconds(1).Ticks,
            plans[3].SourceStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(6).Ticks,
            plans[3].SourceEndTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(2).Ticks,
            plans[3].DestinationStartTicks);
        Assert.Equal(
            TimeSpan.FromSeconds(7).Ticks,
            plans[3].DestinationEndTicks);

        Assert.True(
            WindowsMediaFoundationAudioPlanner.TryCapture(
                request,
                MediaEffectRegistry.Default,
                includeAudio: false,
                out WindowsMediaProvider::ProGPU.Windows.Media
                    .WindowsMediaFoundationAudioPlan[]
                    mutedPlans,
                out long mutedDurationTicks));
        Assert.Empty(mutedPlans);
        Assert.Equal(durationTicks, mutedDurationTicks);
    }

    [Fact]
    public void WindowsPcmMixerUsesWideOrderIndependentSaturation()
    {
        Assert.True(
            WindowsPcm16MixLevels.TryCreate(
                MediaAudioStereoLevels.Identity,
                out WindowsPcm16MixLevels identity));
        Assert.True(
            WindowsPcm16MixLevels.TryCreate(
                new MediaAudioStereoLevels(
                    0.5f,
                    0.25f),
                out WindowsPcm16MixLevels attenuated));
        short[] first =
        [
            30_000,
            -30_000,
            1_000,
            -1_000
        ];
        short[] second =
        [
            20_000,
            20_000,
            -4_000,
            8_000
        ];
        long[] forward = new long[4];
        long[] reverse = new long[4];
        short[] output = new short[4];

        WindowsPcm16Mixer.Add(
            first,
            2,
            identity,
            forward,
            destinationFrameOffset: 0);
        WindowsPcm16Mixer.Add(
            second,
            2,
            attenuated,
            forward,
            destinationFrameOffset: 0);
        WindowsPcm16Mixer.Add(
            second,
            2,
            attenuated,
            reverse,
            destinationFrameOffset: 0);
        WindowsPcm16Mixer.Add(
            first,
            2,
            identity,
            reverse,
            destinationFrameOffset: 0);
        WindowsPcm16Mixer.WriteSaturated(
            forward,
            output);

        Assert.Equal(forward, reverse);
        Assert.Equal(
            [short.MaxValue, -25_000, -1_000, 1_000],
            output);

        WindowsPcm16Mixer.Add(
            first,
            2,
            identity,
            forward,
            destinationFrameOffset: 0);
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0;
             index < 100_000;
             index++)
        {
            forward.AsSpan().Clear();
            WindowsPcm16Mixer.Add(
                first,
                2,
                identity,
                forward,
                destinationFrameOffset: 0);
            WindowsPcm16Mixer.Add(
                second,
                2,
                attenuated,
                forward,
                destinationFrameOffset: 0);
            WindowsPcm16Mixer.WriteSaturated(
                forward,
                output);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void
        WindowsProcessedPcmMixerPreservesHeadroomAndDoesNotAllocate()
    {
        Assert.True(
            WindowsPcm16MixLevels.TryCreate(
                MediaAudioStereoLevels.Identity,
                out WindowsPcm16MixLevels identity));
        Assert.True(
            WindowsPcm16MixLevels.TryCreate(
                new MediaAudioStereoLevels(
                    0.5f,
                    0.5f),
                out WindowsPcm16MixLevels half));
        float[] processed =
        [
            40_000f / 32_768f,
            -40_000f / 32_768f
        ];
        long[] wide = new long[2];
        short[] saturated = new short[2];

        WindowsPcm16Mixer.AddProcessed(
            processed,
            2,
            identity,
            wide,
            destinationFrameOffset: 0);
        Assert.Equal(
            [40_000L, -40_000L],
            wide);
        WindowsPcm16Mixer.WriteSaturated(
            wide,
            saturated);
        Assert.Equal(
            [short.MaxValue, short.MinValue],
            saturated);

        wide.AsSpan().Clear();
        WindowsPcm16Mixer.AddProcessed(
            processed,
            2,
            half,
            wide,
            destinationFrameOffset: 0);
        Assert.Equal(
            [20_000L, -20_000L],
            wide);

        WindowsPcm16Mixer.AddProcessed(
            processed,
            2,
            half,
            wide,
            destinationFrameOffset: 0);
        wide.AsSpan().Clear();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0;
             index < 100_000;
             index++)
        {
            WindowsPcm16Mixer.AddProcessed(
                processed,
                2,
                half,
                wide,
                destinationFrameOffset: 0);
            wide.AsSpan().Clear();
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;
        Assert.Equal(0, allocated);

        processed[0] = float.NaN;
        Assert.Throws<InvalidDataException>(
            () =>
                WindowsPcm16Mixer.AddProcessed(
                    processed,
                    2,
                    identity,
                    wide,
                    destinationFrameOffset: 0));
    }

    [Fact]
    public void WindowsAudioFrameClockRoundsEndpointsDirectionally()
    {
        Assert.Equal(
            48_000,
            WindowsMediaFoundationAudioMixer
                .TicksToFramesFloor(
                    TimeSpan.TicksPerSecond,
                    48_000));
        Assert.Equal(
            0,
            WindowsMediaFoundationAudioMixer
                .TicksToFramesFloor(
                    1,
                    48_000));
        Assert.Equal(
            1,
            WindowsMediaFoundationAudioMixer
                .TicksToFramesCeiling(
                    1,
                    48_000));
        Assert.Equal(
            -1,
            WindowsMediaFoundationAudioMixer
                .TicksToFramesFloor(
                    -1,
                    48_000));
        Assert.Equal(
            0,
            WindowsMediaFoundationAudioMixer
                .TicksToFramesCeiling(
                    -1,
                    48_000));
        Assert.Equal(
            TimeSpan.TicksPerSecond,
            WindowsMediaFoundationAudioMixer
                .FramesToTicksFloor(
                    48_000,
                    48_000));
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
    public void WindowsPreciseExporterAcceptsNativeAudioMixingAndVideoOverlays()
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
        Assert.True(
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
        Assert.True(
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
        Assert.True(
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
                                320d,
                                180d,
                                1d,
                                true)
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
        const string balanceId =
            "ProGPU.Tests.Windows.ExportBalance";
        using IDisposable balanceRegistration =
            registry.Register(
                new MediaAudioStereoBalanceEffectFactory(
                    balanceId));
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
        MediaCompositionEffectDefinition balanceDefinition =
            new(
                balanceId,
                new Dictionary<string, object?>
                {
                    [MediaAudioStereoBalanceEffectFactory
                        .BalancePropertyName] = -0.75d
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
                                Volume = 0.8d,
                                AudioEffectDefinitions =
                                [
                                    definition,
                                    balanceDefinition
                                ]
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
    public void
        WindowsPreciseExporterAcceptsRegisteredTypedAudioEffects()
    {
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new TestWindowsPcmTransformEffectFactory());
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
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip clip =
            request.Clips[0];
        request =
            request with
            {
                Clips =
                [
                    clip with
                    {
                        Volume = 0.5d,
                        AudioEffectDefinitions =
                            definitions
                    }
                ]
            };

        Assert.True(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request,
                    isWindows: true,
                    effects: registry));
        Assert.True(
            WindowsMediaFoundationAudioPlanner.TryCapture(
                request,
                registry,
                includeAudio: true,
                out var plans,
                out _));
        WindowsMediaProvider::ProGPU.Windows.Media
            .WindowsMediaFoundationAudioPlan plan =
                Assert.Single(plans);
        Assert.Equal(16_384, plan.Levels.Left);
        Assert.Equal(16_384, plan.Levels.Right);
        Assert.Equal(
            definitions,
            plan.ProcessorDefinitions);

        MediaCompositionExportCapabilities capabilities =
            WindowsMediaFoundationCompositionExportProvider
                .CreateCapabilities(
                    request,
                    registry);
        Assert.Equal(
            MediaCompositionExportAudioPath.CpuBuffer,
            capabilities.AudioPath);
        Assert.Contains(
            "bounded float workspace",
            capabilities.Limitation,
            StringComparison.OrdinalIgnoreCase);
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
    public void
        WindowsExportAndThumbnailsAcceptTypedVideoDefinitions()
    {
        const string effectId =
            "ProGPU.Tests.Windows.VideoColor";
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new MediaVideoColorEffectFactory(
                    effectId));
        const string blurEffectId =
            "ProGPU.Tests.Windows.VideoGaussian";
        using IDisposable blurRegistration =
            registry.Register(
                new MediaVideoGaussianBlurEffectFactory(
                    blurEffectId));
        MediaCompositionExportRequest request =
            CreatePreciseRequest();
        MediaCompositionExportClip clip =
            request.Clips[0] with
            {
                VideoEffectDefinitions =
                [
                    new MediaCompositionEffectDefinition(
                        effectId,
                        new Dictionary<string, object?>
                        {
                            [
                                MediaVideoColorEffectFactory
                                    .BrightnessPropertyName
                            ] = 0.1d,
                            [
                                MediaVideoColorEffectFactory
                                    .ContrastPropertyName
                            ] = 1.25d,
                            [
                                MediaVideoColorEffectFactory
                                    .SaturationPropertyName
                            ] = 1.5d,
                            [
                                MediaVideoColorEffectFactory
                                    .GrayscalePropertyName
                            ] = 0.2d,
                            [
                                MediaVideoColorEffectFactory
                                    .SepiaPropertyName
                            ] = 0.4d,
                            [
                                MediaVideoColorEffectFactory
                                    .InvertPropertyName
                            ] = 0.1d
                        }),
                    new MediaCompositionEffectDefinition(
                        blurEffectId,
                        new Dictionary<string, object?>
                        {
                            [
                                MediaVideoGaussianBlurEffectFactory
                                    .StandardDeviationPropertyName
                            ] = 4d
                        })
                ]
            };
        request = request with
        {
            Clips = [clip]
        };

        Assert.True(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request,
                    isWindows: true,
                    effects: registry));
        Assert.True(
            WindowsMediaFoundationCompositionThumbnailProvider
                .IsRequestSupported(
                    new MediaCompositionThumbnailRequest(
                        request,
                        [TimeSpan.FromSeconds(1)],
                        1280,
                        720,
                        MediaCompositionThumbnailPrecision
                            .NearestFrame),
                    isWindows: true,
                    effects: registry));
        Assert.True(
            WindowsMediaFoundationCompositionExportProvider
                .TryGetVideoEffectPlan(
                    clip,
                    registry,
                    out var plan));
        Assert.NotEqual(
            ProGPU.Backend.GpuTextureColorTransform
                .Identity,
            plan.ColorTransform);
        Assert.Equal(
            4f,
            plan.BlurStandardDeviation);
        Assert.True(plan.HasSpatialEffect);

        Assert.False(
            WindowsMediaFoundationCompositionExportProvider
                .IsRequestSupported(
                    request,
                    isWindows: true,
                    effects:
                        new MediaEffectRegistry()));
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

    [Fact]
    public void WindowsPcmStereoLevelsPreserveInterleavingAcrossBuffers()
    {
        short[] samples =
        [
            1_000,
            1_000,
            -2_000,
            -2_000,
            4_000,
            4_000
        ];
        var levels =
            new MediaAudioStereoLevels(
                2f,
                0.5f);
        int channelOffset = 0;
        WindowsPcm16GainProcessor.ApplyStereo(
            samples.AsSpan(0, 3),
            channelCount: 2,
            levels,
            ref channelOffset);
        Assert.Equal(1, channelOffset);
        WindowsPcm16GainProcessor.ApplyStereo(
            samples.AsSpan(3),
            channelCount: 2,
            levels,
            ref channelOffset);

        Assert.Equal(0, channelOffset);
        Assert.Equal(
            [
                2_000,
                500,
                -4_000,
                -1_000,
                8_000,
                2_000
            ],
            samples);

        short[] mono = [1_000, -1_000];
        var monoLevels =
            new MediaAudioStereoLevels(
                0.5f,
                0.25f);
        WindowsPcm16GainProcessor.ApplyStereo(
            mono,
            channelCount: 1,
            monoLevels,
            ref channelOffset);
        Assert.Equal([500, -500], mono);

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0;
             iteration < 1_000;
             iteration++)
        {
            WindowsPcm16GainProcessor.ApplyStereo(
                samples,
                channelCount: 2,
                MediaAudioStereoLevels.Identity,
                ref channelOffset);
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
        string audioMixer = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaFoundationAudioMixer.cs");
        string pcmMixer = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsPcm16Mixer.cs");
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
            "WindowsMediaFoundationAudioPlanner",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "audioMixer!.Render(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "stackalloc long[",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreatePcm16Sample(",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaAudioEffectProcessorChain",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "CopyPcm16SampleToFloat(",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "CopyPcm16SampleToFloat(",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPcm16FloatConverter",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddProcessed(",
            pcmMixer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Activator.",
            audioMixer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Assembly.Load",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MFCreateAlignedMemoryBuffer(",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadSourceSample(",
            audioMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Span<long>",
            pcmMixer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            audioMixer,
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
            "GpuTextureGaussianBlur.Blur(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "Windows Media Gaussian Intermediate",
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
            "WindowsMediaFoundationVideoFrameReader?[] readers",
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
            "MediaAudioGraphEffectKind\n                    .StereoBalance",
            provider.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "GetCombinedAudioLevels()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "SupportsFrameStepping: true",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsProviderEnumeratesAndSelectsNativeMediaEngineStreams()
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
            "IMediaPlaybackTrackProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsMediaNative.GetStreams(engine)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsMediaNative.SetExclusiveStreamSelection(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(extended)[54]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(extended)[55]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(extended)[56]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(extended)[57]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"apply Media Engine stream selections\"",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "PropVariantClear(&variant)",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsProviderProjectsNativeTimedTextWithoutRetainingComCues()
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
            "IMediaPlaybackTimedMetadataProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPlaybackTimedMetadataPresentationMode\n                .PlatformPresented",
            provider.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsMediaNative.ReadTimedTextCue(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "DisableInitialTimedTextTracks(timedText)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "!Enum.IsDefined(mode)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoveStaleTimedTextCueStates(timedMetadataIds)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "_timedTextCueEvents.Enqueue(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "cue.Kind is not (1 or 2 or 3)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPlaybackTimedMetadataCueData",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            ".TakeOwnership(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "805ea411-92e0-4e59-9b6e-5c7d7915e64f",
            native,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "1f2a94c9-a3df-430d-9d0f-acd85ddc29af",
            native,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "df6b87b6-ce12-45db-aba7-432fe054e57d",
            native,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "MFGetService(",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(timedText)[11]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "CoTaskMemFree(text)",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(cue)[9]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "VTable(binary)[3]",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumTimedMetadataCueBytes",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsMediaNative.ClearTimedTextNotifications(",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsOverlaysRetainNativeReadersAndSourceOverGpuState()
    {
        string composer = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaFoundationOverlayFrameComposer.cs");
        string reader = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsMediaFoundationVideoFrameReader.cs");
        string sink = ReadRepoFile(
            "src",
            "ProGPU.Windows.Media",
            "WindowsDxgiGpuEffectFrameSink.cs");
        string layer = ReadRepoFile(
            "src",
            "ProGPU.Backend",
            "GpuTextureLayerCompositor.cs");
        string shader = ReadRepoFile(
            "src",
            "ProGPU.Backend",
            "Shaders",
            "TextureLayerCompositor.wgsl");

        Assert.Contains(
            "ReadFrameForward(",
            composer,
            StringComparison.Ordinal);
        Assert.Contains(
            "private nint _currentSample;",
            reader,
            StringComparison.Ordinal);
        Assert.Contains(
            "private nint _nextSample;",
            reader,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompositeDecodedLayer(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureLayerCompositor",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaxRetainedSourceBindings = 64",
            layer,
            StringComparison.Ordinal);
        Assert.Contains(
            "LoadOp = LoadOp.Load",
            layer,
            StringComparison.Ordinal);
        Assert.Contains(
            "OneMinusSrcAlpha",
            layer,
            StringComparison.Ordinal);
        Assert.Contains(
            "sampled.a * parameters.layer.x",
            shader,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels(",
            composer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MapAsync",
            layer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            composer,
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

    private static MediaCompositionEffectDefinition
        CreateTestPcmTransform(
            float scale,
            float offset) =>
        new(
            TestWindowsPcmTransformEffectFactory
                .EffectId,
            new Dictionary<string, object?>
            {
                [
                    TestWindowsPcmTransformEffectFactory
                        .ScalePropertyName
                ] = scale,
                [
                    TestWindowsPcmTransformEffectFactory
                        .OffsetPropertyName
                ] = offset
            });

    private sealed class
        TestWindowsPcmTransformEffectFactory :
        IMediaEffectFactory
    {
        internal const string EffectId =
            "ProGPU.Tests.WindowsPcmTransform";
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
            return new TestWindowsPcmTransformEffect(
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

    private sealed class TestWindowsPcmTransformEffect :
        IMediaAudioEffect
    {
        private readonly float _scale;
        private readonly float _offset;

        internal TestWindowsPcmTransformEffect(
            float scale,
            float offset)
        {
            _scale = scale;
            _offset = offset;
        }

        public string Id =>
            TestWindowsPcmTransformEffectFactory
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
