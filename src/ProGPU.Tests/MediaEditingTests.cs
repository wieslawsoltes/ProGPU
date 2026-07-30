using Windows.Media.Editing;
using Windows.Storage;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using Silk.NET.WebGPU;
using System.Numerics;
using System.Reflection;
using Xunit;

namespace ProGPU.Tests;

public sealed class MediaEditingTests
{
    [Fact]
    public void TypedVideoEffectResolverPreservesDeclaredOrder()
    {
        const string effectId =
            "ProGPU.Tests.VideoColor";
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new MediaVideoColorEffectFactory(
                    effectId));
        var definitions =
            new MediaCompositionEffectDefinition[]
            {
                new(
                    effectId,
                    new Dictionary<string, object?>
                    {
                        [
                            MediaVideoColorEffectFactory
                                .InvertPropertyName
                        ] = 1f
                    }),
                new(
                    effectId,
                    new Dictionary<string, object?>
                    {
                        [
                            MediaVideoColorEffectFactory
                                .BrightnessPropertyName
                        ] = 0.1f
                    })
            };

        Assert.True(
            MediaCompositionVideoEffectResolver
                .TryCaptureColorTransform(
                    registry,
                    definitions,
                    out MediaVideoColorTransform
                        transform));

        Vector3 result =
            transform.Transform(
                new Vector3(1f, 0f, 0f));
        Assert.Equal(0.1f, result.X, 5);
        Assert.Equal(1.1f, result.Y, 5);
        Assert.Equal(1.1f, result.Z, 5);
    }

    [Fact]
    public void TypedVideoEffectPlanCombinesClampedGaussianVariance()
    {
        const string colorEffectId =
            "ProGPU.Tests.VideoPlan.Color";
        const string blurEffectId =
            "ProGPU.Tests.VideoPlan.Blur";
        var registry = new MediaEffectRegistry();
        using IDisposable colorRegistration =
            registry.Register(
                new MediaVideoColorEffectFactory(
                    colorEffectId));
        using IDisposable blurRegistration =
            registry.Register(
                new MediaVideoGaussianBlurEffectFactory(
                    blurEffectId));
        var definitions =
            new MediaCompositionEffectDefinition[]
            {
                new(
                    blurEffectId,
                    new Dictionary<string, object?>
                    {
                        [
                            MediaVideoGaussianBlurEffectFactory
                                .StandardDeviationPropertyName
                        ] = 3f
                    }),
                new(
                    colorEffectId,
                    new Dictionary<string, object?>
                    {
                        [
                            MediaVideoColorEffectFactory
                                .InvertPropertyName
                        ] = 1f
                    }),
                new(
                    blurEffectId,
                    new Dictionary<string, object?>
                    {
                        [
                            MediaVideoGaussianBlurEffectFactory
                                .StandardDeviationPropertyName
                        ] = 4f
                    })
            };

        Assert.True(
            MediaCompositionVideoEffectResolver
                .TryCapturePlan(
                    registry,
                    definitions,
                    out MediaVideoEffectPlan plan));
        Assert.True(plan.HasSpatialEffect);
        Assert.Equal(
            5f,
            plan.BlurStandardDeviation,
            5);
        Assert.Equal(
            Vector3.Zero,
            plan.ColorTransform.Transform(
                Vector3.One));
        Assert.False(
            MediaCompositionVideoEffectResolver
                .TryCaptureColorTransform(
                    registry,
                    definitions,
                    out _));
        Assert.True(
            default(MediaVideoEffectPlan).IsIdentity);
        Assert.Equal(
            Vector3.One,
            default(MediaVideoEffectPlan)
                .ColorTransform
                .Transform(Vector3.One));
    }

    [Fact]
    public void TypedGaussianBlurRejectsInvalidPortableSigma()
    {
        const string effectId =
            "ProGPU.Tests.VideoBlur.Invalid";
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new MediaVideoGaussianBlurEffectFactory(
                    effectId));

        Assert.False(
            MediaCompositionVideoEffectResolver
                .TryCapturePlan(
                    registry,
                    [
                        new(
                            effectId,
                            new Dictionary<
                                string,
                                object?>
                            {
                                [
                                    MediaVideoGaussianBlurEffectFactory
                                        .StandardDeviationPropertyName
                                ] = 33f
                            })
                    ],
                    out _));
    }

    [Fact]
    public void TypedVideoEffectResolverRejectsUnknownAndInvalidNodes()
    {
        const string effectId =
            "ProGPU.Tests.VideoColor.Invalid";
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new MediaVideoColorEffectFactory(
                    effectId));

        Assert.False(
            MediaCompositionVideoEffectResolver
                .TryCaptureColorTransform(
                    registry,
                    [
                        new(
                            "ProGPU.Tests.Unregistered",
                            new Dictionary<
                                string,
                                object?>())
                    ],
                    out _));
        Assert.False(
            MediaCompositionVideoEffectResolver
                .TryCaptureColorTransform(
                    registry,
                    [
                        new(
                            effectId,
                            new Dictionary<
                                string,
                                object?>
                            {
                                [
                                    MediaVideoColorEffectFactory
                                        .ContrastPropertyName
                                ] = double.NaN
                            })
                    ],
                    out _));
    }

    [Fact]
    public void SharedThumbnailPngEncoderPreservesBgraRowsAndStride()
    {
        byte[] pixels =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            9, 9, 9, 9,
            255, 0, 0, 255,
            255, 255, 255, 255
        ];

        byte[] encoded =
            MediaPngEncoder.Encode(
                pixels,
                2,
                2,
                12,
                MediaPngPixelOrder.Bgra);

        Assert.True(
            encoded.AsSpan(0, 8).SequenceEqual(
                new byte[]
                {
                    137, 80, 78, 71,
                    13, 10, 26, 10
                }));
        using SkiaSharp.SKBitmap bitmap =
            SkiaSharp.SKBitmap.Decode(encoded);
        Assert.Equal(2, bitmap.Width);
        Assert.Equal(2, bitmap.Height);
        Assert.Equal(
            new SkiaSharp.SKColor(
                255,
                0,
                0,
                255),
            bitmap.GetPixel(0, 0));
        Assert.Equal(
            new SkiaSharp.SKColor(
                0,
                255,
                0,
                255),
            bitmap.GetPixel(1, 0));
        Assert.Equal(
            new SkiaSharp.SKColor(
                0,
                0,
                255,
                255),
            bitmap.GetPixel(0, 1));
        Assert.Equal(
            new SkiaSharp.SKColor(
                255,
                255,
                255,
                255),
            bitmap.GetPixel(1, 1));
    }

    [Fact]
    public void CompositionLoadMatchesOfficialStaticFactory()
    {
        MethodInfo? official = typeof(MediaComposition)
            .GetMethod(
                nameof(MediaComposition.LoadAsync),
                BindingFlags.Public |
                BindingFlags.Static,
                [typeof(StorageFile)]);
        MethodInfo? mutating = typeof(MediaComposition)
            .GetMethod(
                nameof(MediaComposition.LoadProjectAsync),
                BindingFlags.Public |
                BindingFlags.Instance,
                [typeof(StorageFile)]);

        Assert.NotNull(official);
        Assert.Equal(
            typeof(Task<MediaComposition>),
            official.ReturnType);
        Assert.NotNull(mutating);
        Assert.Null(
            typeof(MediaComposition).GetMethod(
                nameof(MediaComposition.LoadAsync),
                BindingFlags.Public |
                BindingFlags.Instance,
                [typeof(StorageFile)]));
    }

    [Fact]
    public void ClipMetadataAndEmbeddedAudioHaveDetachedOwnership()
    {
        MediaClip clip = MediaClip.CreateFromUri(
            new Uri("https://example.test/clip.mp4"),
            TimeSpan.FromSeconds(8));
        var video = new VideoEncodingProperties
        {
            Subtype = "H264",
            Width = 1_920,
            Height = 1_080,
            Bitrate = 8_000_000
        };
        video.FrameRate.Numerator = 30_000;
        video.FrameRate.Denominator = 1_001;
        var audio = new AudioEncodingProperties
        {
            Subtype = "AAC",
            Bitrate = 192_000,
            SampleRate = 48_000,
            ChannelCount = 2
        };

        clip.SetProGpuEncodingProperties(
            video,
            [audio]);
        VideoEncodingProperties detachedVideo =
            clip.GetVideoEncodingProperties();
        AudioEncodingProperties detachedAudio =
            clip.EmbeddedAudioTracks[0]
                .GetAudioEncodingProperties();
        detachedVideo.Width = 1;
        detachedAudio.SampleRate = 8_000;

        Assert.Equal(
            1_920u,
            clip.GetVideoEncodingProperties().Width);
        Assert.Equal(
            48_000u,
            clip.EmbeddedAudioTracks[0]
                .GetAudioEncodingProperties()
                .SampleRate);
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                clip.SelectedEmbeddedAudioTrackIndex =
                    1);

        BackgroundAudioTrack background =
            BackgroundAudioTrack
                .CreateFromEmbeddedAudioTrack(
                    clip.EmbeddedAudioTracks[0]);
        Assert.Equal(
            clip.ProGpuSourceUri,
            background.ProGpuSourceUri);
        Assert.Equal(
            clip.OriginalDuration,
            background.OriginalDuration);
        Assert.Equal(
            "AAC",
            background.GetAudioEncodingProperties()
                .Subtype);

        MediaClip clone = clip.Clone();
        Assert.Single(clone.EmbeddedAudioTracks);
        Assert.Equal(
            30_000u,
            clone.GetVideoEncodingProperties()
                .FrameRate.Numerator);
    }

    [Fact]
    public void EncoderFrameSinkCapabilitiesRejectNonGpuPaths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MediaGpuEncoderFrameSinkCapabilities(
                "test",
                MediaCompositionExportVideoPath.CpuBuffer,
                TextureFormat.Rgba8Unorm,
                hardwareEncoderSurface: true,
                supportsExplicitPresentationTime: true,
                supportsGpuEffects: true,
                maximumFramesInFlight: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MediaGpuEncoderFrameSinkCapabilities(
                "test",
                MediaCompositionExportVideoPath.NativeGpuSurface,
                TextureFormat.Rgba8Unorm,
                hardwareEncoderSurface: true,
                supportsExplicitPresentationTime: true,
                supportsGpuEffects: true,
                maximumFramesInFlight: 0));
    }

    [Fact]
    public void EncoderFrameCompletesNativeSlotExactlyOnce()
    {
        using var texture = new GpuTexture(
            ProGPU.Tests.Headless.HeadlessWindow.Shared.Context,
            2,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment,
            "Encoder frame lifecycle test");
        var frame = new RecordingEncoderFrame(
            texture,
            TimeSpan.FromMilliseconds(125));

        frame.Complete(renderSucceeded: true);
        frame.Dispose();

        Assert.True(frame.IsCompleted);
        Assert.Equal(1, frame.CompletionCount);
        Assert.True(frame.RenderSucceeded);
        Assert.Throws<InvalidOperationException>(
            () => frame.Complete(renderSucceeded: true));
    }

    [Fact]
    public void DisposingIncompleteEncoderFrameAbortsNativeSlot()
    {
        using var texture = new GpuTexture(
            ProGPU.Tests.Headless.HeadlessWindow.Shared.Context,
            2,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment,
            "Encoder frame abort test");
        var frame = new RecordingEncoderFrame(
            texture,
            TimeSpan.Zero);

        frame.Dispose();
        frame.Dispose();

        Assert.True(frame.IsCompleted);
        Assert.Equal(1, frame.CompletionCount);
        Assert.False(frame.RenderSucceeded);
    }

    [Fact]
    public void EditingAssemblyIsIndependentFromUiFrameworks()
    {
        string assemblyName =
            typeof(MediaComposition).Assembly.GetName().Name!;
        string[] references =
            typeof(MediaComposition).Assembly
                .GetReferencedAssemblies()
                .Select(static value => value.Name!)
                .ToArray();

        Assert.Equal(
            "ProGPU.Media.Editing",
            assemblyName);
        Assert.Contains("ProGPU.Media", references);
        Assert.Contains("ProGPU.WinRT", references);
        Assert.DoesNotContain("ProGPU.WinUI", references);
        Assert.DoesNotContain(
            references,
            static value =>
                value.Contains(
                    "Avalonia",
                    StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            references,
            static value =>
                value.Contains(
                    "PresentationCore",
                    StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            references,
            static value =>
                value.Contains(
                    "System.Windows.Forms",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompositionUsesOfficialTrimAndTimelineSemantics()
    {
        var composition = new MediaComposition();
        MediaClip first = MediaClip.CreateFromUri(
            new Uri("https://example.test/first.mp4"),
            TimeSpan.FromSeconds(10));
        first.TrimTimeFromStart =
            TimeSpan.FromSeconds(2);
        first.TrimTimeFromEnd =
            TimeSpan.FromSeconds(3);
        MediaClip second = MediaClip.CreateFromUri(
            new Uri("https://example.test/second.mp4"),
            TimeSpan.FromSeconds(4));

        composition.Clips.Add(first);
        composition.Clips.Add(second);

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            first.TrimmedDuration);
        Assert.Equal(
            TimeSpan.Zero,
            first.StartTimeInComposition);
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            first.EndTimeInComposition);
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            second.StartTimeInComposition);
        Assert.Equal(
            TimeSpan.FromSeconds(9),
            composition.Duration);
    }

    [Fact]
    public void CloneOwnsIndependentClipsAndUserData()
    {
        var composition = new MediaComposition();
        MediaClip clip = MediaClip.CreateFromUri(
            new Uri("https://example.test/clip.mp4"),
            TimeSpan.FromSeconds(8));
        clip.UserData["name"] = "Original";
        clip.Volume = 1.5d;
        clip.VideoEffectDefinitions.Add(
            new VideoEffectDefinition(
                "ProGPU.Test.VideoEffect",
                new Windows.Foundation.Collections.PropertySet
                {
                    ["amount"] = 0.5d
                }));
        composition.Clips.Add(clip);
        BackgroundAudioTrack background =
            BackgroundAudioTrack.CreateFromUri(
                new Uri("https://example.test/music.m4a"),
                TimeSpan.FromSeconds(20));
        background.Delay = TimeSpan.FromSeconds(2);
        composition.BackgroundAudioTracks.Add(background);
        var layer = new MediaOverlayLayer(
            new VideoCompositorDefinition(
                "ProGPU.Test.Compositor",
                new Windows.Foundation.Collections.PropertySet
                {
                    ["mode"] = "screen"
                }));
        layer.Overlays.Add(
            new MediaOverlay(
                MediaClip.CreateFromColor(
                    Windows.UI.Color.FromArgb(
                        255, 10, 20, 30),
                    TimeSpan.FromSeconds(3)),
                new Windows.Foundation.Rect(
                    10, 20, 320, 180),
                0.8d));
        composition.OverlayLayers.Add(layer);
        composition.UserData["project"] = "A";

        MediaComposition clone =
            composition.Clone();
        clone.Clips[0].UserData["name"] =
            "Clone";
        clone.Clips[0].VideoEffectDefinitions[0]
            .Properties["amount"] = 0.75d;
        clone.BackgroundAudioTracks[0].Volume = 0.25d;
        clone.OverlayLayers[0].Overlays[0].Opacity =
            0.25d;
        clone.UserData["project"] = "B";

        Assert.NotSame(
            composition.Clips[0],
            clone.Clips[0]);
        Assert.Equal(
            "Original",
            composition.Clips[0].UserData["name"]);
        Assert.Equal(
            "A",
            composition.UserData["project"]);
        Assert.Equal(
            1.5d,
            clone.Clips[0].Volume);
        Assert.Equal(
            0.5d,
            composition.Clips[0]
                .VideoEffectDefinitions[0]
                .Properties["amount"]);
        Assert.Equal(
            1d,
            composition.BackgroundAudioTracks[0].Volume);
        Assert.Equal(
            0.8d,
            composition.OverlayLayers[0]
                .Overlays[0].Opacity);
    }

    [Fact]
    public void MetadataDurationUpdateClampsExistingTrims()
    {
        MediaClip clip = MediaClip.CreateFromUri(
            new Uri("https://example.test/clip.mp4"),
            TimeSpan.FromSeconds(10));
        clip.TrimTimeFromStart =
            TimeSpan.FromSeconds(4);
        clip.TrimTimeFromEnd =
            TimeSpan.FromSeconds(3);

        clip.SetProGpuOriginalDuration(
            TimeSpan.FromSeconds(5));

        Assert.Equal(
            TimeSpan.FromSeconds(4),
            clip.TrimTimeFromStart);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            clip.TrimTimeFromEnd);
        Assert.Equal(
            TimeSpan.Zero,
            clip.TrimmedDuration);
    }

    [Fact]
    public void ClipCannotBeSharedOrDuplicated()
    {
        var first = new MediaComposition();
        var second = new MediaComposition();
        MediaClip clip = MediaClip.CreateFromUri(
            new Uri("https://example.test/clip.mp4"),
            TimeSpan.FromSeconds(1));
        first.Clips.Add(clip);

        Assert.Throws<InvalidOperationException>(
            () => first.Clips.Add(clip));
        Assert.Throws<InvalidOperationException>(
            () => second.Clips.Add(clip));
    }

    [Fact]
    public async Task SaveAndLoadRoundTripEditableProject()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"progpu-media-{Guid.NewGuid():N}.pgmedia");
        try
        {
            var original = new MediaComposition();
            original.UserData["title"] = "Timeline";
            MediaClip source = MediaClip.CreateFromUri(
                new Uri("https://example.test/clip.mp4"),
                TimeSpan.FromSeconds(12));
            source.TrimTimeFromStart =
                TimeSpan.FromSeconds(2);
            source.TrimTimeFromEnd =
                TimeSpan.FromSeconds(3);
            source.Volume = 0.75;
            source.UserData["effect"] = "grayscale";
            var sourceVideo =
                new VideoEncodingProperties
                {
                    Subtype = "H264",
                    Width = 1_920,
                    Height = 1_080,
                    Bitrate = 8_000_000
                };
            sourceVideo.FrameRate.Numerator =
                30_000;
            sourceVideo.FrameRate.Denominator =
                1_001;
            source.SetProGpuEncodingProperties(
                sourceVideo,
                [
                    new AudioEncodingProperties
                    {
                        Subtype = "AAC",
                        Bitrate = 192_000,
                        SampleRate = 48_000,
                        ChannelCount = 2
                    }
                ]);
            source.AudioEffectDefinitions.Add(
                new AudioEffectDefinition(
                    "ProGPU.Test.Gain",
                    new Windows.Foundation.Collections.PropertySet
                    {
                        ["enabled"] = true,
                        ["gain"] = 0.8f,
                        ["label"] = "voice"
                    }));
            source.VideoEffectDefinitions.Add(
                new VideoEffectDefinition(
                    "ProGPU.Test.Color",
                    new Windows.Foundation.Collections.PropertySet
                    {
                        ["strength"] = 3
                    }));
            original.Clips.Add(source);
            original.Clips.Add(MediaClip.CreateFromColor(
                Windows.UI.Color.FromArgb(255, 1, 2, 3),
                TimeSpan.FromSeconds(4)));
            BackgroundAudioTrack background =
                BackgroundAudioTrack.CreateFromUriCore(
                    new Uri("https://example.test/music.m4a"),
                    TimeSpan.FromSeconds(20),
                    sourceAudioTrackIndex: 3);
            background.TrimTimeFromStart =
                TimeSpan.FromSeconds(1);
            background.TrimTimeFromEnd =
                TimeSpan.FromSeconds(2);
            background.Delay =
                TimeSpan.FromSeconds(-3);
            background.Volume = 0.4d;
            background.SetProGpuEncodingProperties(
                new AudioEncodingProperties
                {
                    Subtype = "AAC",
                    Bitrate = 128_000,
                    SampleRate = 44_100,
                    ChannelCount = 2
                });
            background.UserData["role"] = "music";
            background.AudioEffectDefinitions.Add(
                new AudioEffectDefinition(
                    "ProGPU.Test.Limiter",
                    new Windows.Foundation.Collections.PropertySet
                    {
                        ["ceiling"] = -1.5d
                    }));
            original.BackgroundAudioTracks.Add(background);
            var overlayLayer = new MediaOverlayLayer(
                new VideoCompositorDefinition(
                    "ProGPU.Test.Compositor",
                    new Windows.Foundation.Collections.PropertySet
                    {
                        ["blend"] = "normal"
                    }));
            var overlay = new MediaOverlay(
                MediaClip.CreateFromColor(
                    Windows.UI.Color.FromArgb(
                        200, 20, 40, 60),
                    TimeSpan.FromSeconds(3)),
                new Windows.Foundation.Rect(
                    32, 48, 640, 360),
                0.6d)
            {
                AudioEnabled = false,
                Delay = TimeSpan.FromSeconds(2)
            };
            overlayLayer.Overlays.Add(overlay);
            original.OverlayLayers.Add(overlayLayer);

            var file = new StorageFile(path);
            await original.SaveAsync(file);
            MediaComposition loaded =
                await MediaComposition.LoadAsync(file);

            Assert.Equal(
                TimeSpan.FromSeconds(14),
                loaded.Duration);
            Assert.Equal("Timeline", loaded.UserData["title"]);
            Assert.Equal(
                source.ProGpuSourceUri,
                loaded.Clips[0].ProGpuSourceUri);
            Assert.Equal(0.75, loaded.Clips[0].Volume);
            Assert.Equal(
                "grayscale",
                loaded.Clips[0].UserData["effect"]);
            Assert.Equal(
                1_920u,
                loaded.Clips[0]
                    .GetVideoEncodingProperties()
                    .Width);
            Assert.Single(
                loaded.Clips[0].EmbeddedAudioTracks);
            Assert.Equal(
                48_000u,
                loaded.Clips[0]
                    .EmbeddedAudioTracks[0]
                    .GetAudioEncodingProperties()
                    .SampleRate);
            Assert.Equal(
                Windows.UI.Color.FromArgb(255, 1, 2, 3),
                loaded.Clips[1].ProGpuColor);
            Assert.Equal(
                true,
                loaded.Clips[0]
                    .AudioEffectDefinitions[0]
                    .Properties["enabled"]);
            Assert.Equal(
                3,
                loaded.Clips[0]
                    .VideoEffectDefinitions[0]
                    .Properties["strength"]);
            Assert.Single(loaded.BackgroundAudioTracks);
            Assert.Equal(
                TimeSpan.FromSeconds(-3),
                loaded.BackgroundAudioTracks[0].Delay);
            Assert.Equal(
                0.4d,
                loaded.BackgroundAudioTracks[0].Volume);
            Assert.Equal(
                44_100u,
                loaded.BackgroundAudioTracks[0]
                    .GetAudioEncodingProperties()
                    .SampleRate);
            Assert.Equal(
                3u,
                loaded.BackgroundAudioTracks[0]
                    .ProGpuSourceAudioTrackIndex);
            Assert.Equal(
                "music",
                loaded.BackgroundAudioTracks[0]
                    .UserData["role"]);
            Assert.Equal(
                -1.5d,
                loaded.BackgroundAudioTracks[0]
                    .AudioEffectDefinitions[0]
                    .Properties["ceiling"]);
            Assert.Single(loaded.OverlayLayers);
            Assert.Equal(
                "ProGPU.Test.Compositor",
                loaded.OverlayLayers[0]
                    .CustomCompositorDefinition!
                    .ActivatableClassId);
            Assert.Equal(
                TimeSpan.FromSeconds(2),
                loaded.OverlayLayers[0]
                    .Overlays[0].Delay);
            Assert.Equal(
                new Windows.Foundation.Rect(
                    32, 48, 640, 360),
                loaded.OverlayLayers[0]
                    .Overlays[0].Position);
            Assert.False(
                loaded.OverlayLayers[0]
                    .Overlays[0].AudioEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidProjectLoadIsTransactional()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"progpu-media-{Guid.NewGuid():N}.pgmedia");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """{"version":999,"clips":[]}""");
            var composition = new MediaComposition();
            composition.Clips.Add(MediaClip.CreateFromColor(
                Windows.UI.Color.FromArgb(255, 0, 0, 0),
                TimeSpan.FromSeconds(1)));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => composition.LoadProjectAsync(
                    new StorageFile(path)));

            Assert.Single(composition.Clips);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ThumbnailApiBatchesRequestsAndPreservesAspectRatio()
    {
        var provider = new TestThumbnailProvider();
        using IDisposable registration =
            MediaCompositionThumbnailRegistry.Default.Register(
                provider);
        var composition = new MediaComposition();
        MediaClip clip = MediaClip.CreateFromUri(
            new Uri(
                "https://thumbnail.example.test/clip.mp4"),
            TimeSpan.FromSeconds(8));
        clip.SetProGpuEncodingProperties(
            new VideoEncodingProperties
            {
                Subtype = "H264",
                Width = 640,
                Height = 360
            });
        composition.Clips.Add(clip);
        TimeSpan[] positions =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8)
        ];

        IReadOnlyList<ImageStream> thumbnails =
            await composition.GetThumbnailsAsync(
                positions,
                scaledWidth: 320,
                scaledHeight: 0,
                VideoFramePrecision.NearestFrame);

        Assert.Equal(1, provider.CallCount);
        Assert.NotNull(provider.Request);
        Assert.Equal(positions, provider.Request!.Positions);
        Assert.Equal(320u, provider.Request.PixelWidth);
        Assert.Equal(180u, provider.Request.PixelHeight);
        Assert.Equal(
            MediaCompositionThumbnailPrecision
                .NearestFrame,
            provider.Request.Precision);
        Assert.Equal(3, thumbnails.Count);
        Assert.All(
            thumbnails,
            thumbnail =>
            {
                Assert.Equal(
                    "image/test-thumbnail",
                    thumbnail.ContentType);
                Assert.Equal(3UL, thumbnail.Size);
            });

        ImageStream first = thumbnails[0];
        provider.FirstBuffer![0] = 99;
        Assert.Equal(1, first.AsStream().ReadByte());
        first.Seek(0);
        using IRandomAccessStream clone =
            first.CloneStream();
        Assert.Equal(0UL, first.Position);
        Assert.Equal(0UL, clone.Position);
        first.Seek(2);
        Assert.Equal(2UL, first.Position);
        Assert.Equal(0UL, clone.Position);
    }

    [Fact]
    public async Task ThumbnailRegistryRejectsIncompleteProviderBatch()
    {
        var registry =
            new MediaCompositionThumbnailRegistry();
        using IDisposable registration =
            registry.Register(
                new IncompleteThumbnailProvider());
        MediaCompositionExportRequest composition =
            CreateCapabilityRequest();
        var request =
            new MediaCompositionThumbnailRequest(
                composition,
                [
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(500)
                ],
                160,
                90,
                MediaCompositionThumbnailPrecision
                    .NearestKeyFrame);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                async () =>
                    await registry.RenderAsync(request));

        Assert.Contains(
            "returned 1 images for 2 requested",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderUsesRegisteredNativeProviderAndOfficialResult()
    {
        var provider = new TestExportProvider();
        using IDisposable registration =
            MediaCompositionExportRegistry.Default.Register(provider);
        var composition = new MediaComposition();
        MediaClip sourceClip =
            MediaClip.CreateFromUri(
            new Uri("https://example.test/clip.mp4"),
            TimeSpan.FromSeconds(1));
        sourceClip.SetProGpuEncodingProperties(
            new VideoEncodingProperties
            {
                Subtype = "H264",
                Width = 640,
                Height = 360,
                Bitrate = 2_000_000
            },
            [
                new AudioEncodingProperties
                {
                    Subtype = "PCM",
                    Bitrate = 1_536_000,
                    SampleRate = 48_000,
                    ChannelCount = 2
                },
                new AudioEncodingProperties
                {
                    Subtype = "AAC",
                    Bitrate = 128_000,
                    SampleRate = 48_000,
                    ChannelCount = 2
                }
            ]);
        sourceClip.SelectedEmbeddedAudioTrackIndex = 1;
        composition.Clips.Add(sourceClip);
        BackgroundAudioTrack background =
            BackgroundAudioTrack
                .CreateFromEmbeddedAudioTrack(
                    sourceClip
                        .EmbeddedAudioTracks[1]);
        background.Delay = TimeSpan.FromSeconds(3);
        composition.BackgroundAudioTracks.Add(background);
        var layer = new MediaOverlayLayer();
        layer.Overlays.Add(
            new MediaOverlay(
                MediaClip.CreateFromColor(
                    Windows.UI.Color.FromArgb(
                        255, 1, 2, 3),
                    TimeSpan.FromSeconds(1))));
        composition.OverlayLayers.Add(layer);
        var progressValues = new List<double>();

        TranscodeFailureReason result =
            await composition.RenderToFileAsync(
                new StorageFile("/tmp/progpu-output.mp4"),
                MediaTrimmingPreference.Precise,
                MediaEncodingProfile.CreateMp4(
                    VideoEncodingQuality.HD1080p),
                new Progress<double>(
                    value => progressValues.Add(value)));

        Assert.Equal(TranscodeFailureReason.None, result);
        Assert.NotNull(provider.Request);
        Assert.Equal(
            MediaCompositionTrimmingMode.Precise,
            provider.Request!.TrimmingMode);
        Assert.Equal(1920u, provider.Request.EncodingProfile.Width);
        Assert.Single(provider.Request.Clips);
        Assert.Equal(
            640u,
            provider.Request.Clips[0].SourceVideoWidth);
        Assert.Equal(
            360u,
            provider.Request.Clips[0].SourceVideoHeight);
        Assert.Equal(
            "AAC",
            provider.Request.Clips[0].SourceAudioSubtype);
        Assert.Equal(
            1u,
            provider.Request.Clips[0].SourceAudioTrackIndex);
        Assert.Equal(
            128_000u,
            provider.Request.Clips[0].SourceAudioBitrate);
        Assert.Equal(
            48_000u,
            provider.Request.Clips[0].SourceAudioSampleRate);
        Assert.Equal(
            2u,
            provider.Request.Clips[0].SourceAudioChannelCount);
        Assert.Single(provider.Request.BackgroundAudioTracks);
        Assert.Single(provider.Request.OverlayLayers);
        Assert.Single(
            provider.Request.OverlayLayers[0].Overlays);
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            provider.Request.BackgroundAudioTracks[0].Delay);
        Assert.Equal(
            "AAC",
            provider.Request.BackgroundAudioTracks[0]
                .SourceAudioSubtype);
        Assert.Equal(
            1u,
            provider.Request.BackgroundAudioTracks[0]
                .SourceAudioTrackIndex);
        Assert.Equal(
            128_000u,
            provider.Request.BackgroundAudioTracks[0]
                .SourceAudioBitrate);
        Assert.Equal(
            48_000u,
            provider.Request.BackgroundAudioTracks[0]
                .SourceAudioSampleRate);
        Assert.Equal(
            2u,
            provider.Request.BackgroundAudioTracks[0]
                .SourceAudioChannelCount);

        MediaEncodingProfile profile =
            MediaComposition.CreateDefaultEncodingProfile();
        Assert.Equal("MPEG4", profile.Container!.Subtype);
        Assert.Equal("H264", profile.Video!.Subtype);
        Assert.Equal("AAC", profile.Audio!.Subtype);
        Assert.Equal(30u, profile.Video.FrameRate.Numerator);
    }

    [Fact]
    public void ExportRegistryReportsSelectedProviderCapabilities()
    {
        var provider = new TestCapabilityExportProvider();
        using IDisposable registration =
            MediaCompositionExportRegistry.Default.Register(provider);
        MediaCompositionExportRequest request =
            CreateCapabilityRequest();

        Assert.True(
            MediaCompositionExportRegistry.Default
                .TryGetCapabilities(
                    request,
                    out MediaCompositionExportCapabilities
                        capabilities));
        Assert.Equal(provider.Id, capabilities.ProviderId);
        Assert.Equal(
            MediaCompositionExportVideoPath.NativeGpuSurface,
            capabilities.VideoPath);
        Assert.Equal(
            MediaCompositionExportAudioPath.NativeBuffer,
            capabilities.AudioPath);
        Assert.False(capabilities.HardwareVideoEncoderGuaranteed);
    }

    [Fact]
    public void MediaCompositionReportsProGpuExportCapabilities()
    {
        var provider = new TestCapabilityExportProvider();
        using IDisposable registration =
            MediaCompositionExportRegistry.Default.Register(provider);
        var composition = new MediaComposition();
        composition.Clips.Add(
            MediaClip.CreateFromUri(
                new Uri("https://example.test/clip.mp4"),
                TimeSpan.FromSeconds(1)));

        Assert.True(
            composition.TryGetProGpuExportCapabilities(
                new StorageFile(
                    "/tmp/progpu-composition-capability.mp4"),
                MediaTrimmingPreference.Precise,
                MediaEncodingProfile.CreateMp4(
                    VideoEncodingQuality.HD1080p),
                out MediaCompositionExportCapabilities
                    capabilities));
        Assert.Equal(provider.Id, capabilities.ProviderId);
        Assert.Equal(
            MediaCompositionExportVideoPath.NativeGpuSurface,
            capabilities.VideoPath);
    }

    private static MediaCompositionExportRequest
        CreateCapabilityRequest()
    {
        var clip = new MediaCompositionExportClip(
            new Uri("https://example.test/clip.mp4"),
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
                1920,
                1080,
                8_000_000,
                30,
                1,
                192_000,
                48_000,
                2);
        return new MediaCompositionExportRequest(
            "/tmp/progpu-capability.mp4",
            [clip],
            MediaCompositionTrimmingMode.Precise,
            profile,
            new Dictionary<string, string>());
    }

    private sealed class RecordingEncoderFrame :
        MediaGpuEncoderFrame
    {
        public RecordingEncoderFrame(
            GpuTexture texture,
            TimeSpan presentationTime)
            : base(texture, presentationTime)
        {
        }

        public int CompletionCount { get; private set; }

        public bool RenderSucceeded { get; private set; }

        protected override void CompleteCore(
            bool renderSucceeded)
        {
            CompletionCount++;
            RenderSucceeded = renderSucceeded;
        }
    }

    private sealed class TestExportProvider :
        IMediaCompositionExportProvider
    {
        public string Id => "test";
        public int Priority => int.MaxValue;
        public MediaCompositionExportRequest? Request { get; private set; }

        public bool CanRender(MediaCompositionExportRequest request) =>
            request.DestinationPath.EndsWith(
                ".mp4",
                StringComparison.OrdinalIgnoreCase);

        public ValueTask<MediaCompositionExportFailure> RenderAsync(
            MediaCompositionExportRequest request,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            progress?.Report(100d);
            return ValueTask.FromResult(
                MediaCompositionExportFailure.None);
        }
    }

    private sealed class TestCapabilityExportProvider :
        IMediaCompositionExportProvider,
        IMediaCompositionExportCapabilityProvider
    {
        public string Id => "test.capability";
        public int Priority => int.MaxValue;

        public bool CanRender(
            MediaCompositionExportRequest request) =>
            request.DestinationPath.EndsWith(
                ".mp4",
                StringComparison.OrdinalIgnoreCase);

        public MediaCompositionExportCapabilities
            GetCapabilities(
            MediaCompositionExportRequest request) =>
            new(
                Id,
                MediaCompositionExportVideoPath.NativeGpuSurface,
                MediaCompositionExportAudioPath.NativeBuffer,
                HardwareVideoEncoderRequested: true,
                HardwareVideoEncoderGuaranteed: false,
                EffectsBakedOnGpu: false,
                Limitation: "Test provider.");

        public ValueTask<MediaCompositionExportFailure> RenderAsync(
            MediaCompositionExportRequest request,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                MediaCompositionExportFailure.None);
    }

    private sealed class TestThumbnailProvider :
        IMediaCompositionThumbnailProvider
    {
        public string Id => "test.thumbnail";
        public int Priority => int.MaxValue;
        public int CallCount { get; private set; }
        public byte[]? FirstBuffer { get; private set; }
        public MediaCompositionThumbnailRequest? Request
        {
            get;
            private set;
        }

        public bool CanRender(
            MediaCompositionThumbnailRequest request) =>
            request.Composition.Clips.Any(
                static clip =>
                    clip.SourceUri?.Host ==
                    "thumbnail.example.test");

        public ValueTask<IReadOnlyList<
            MediaCompositionThumbnail>> RenderAsync(
            MediaCompositionThumbnailRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Request = request;
            var result =
                new MediaCompositionThumbnail[
                    request.Positions.Count];
            for (int index = 0;
                 index < result.Length;
                 index++)
            {
                byte[] bytes =
                    [1, 2, checked((byte)index)];
                if (index == 0)
                {
                    FirstBuffer = bytes;
                }
                result[index] =
                    new MediaCompositionThumbnail(
                        bytes,
                        "image/test-thumbnail",
                        request.PixelWidth,
                        request.PixelHeight);
            }
            return ValueTask.FromResult<
                IReadOnlyList<
                    MediaCompositionThumbnail>>(
                Array.AsReadOnly(result));
        }
    }

    private sealed class IncompleteThumbnailProvider :
        IMediaCompositionThumbnailProvider
    {
        public string Id => "test.incomplete-thumbnail";
        public int Priority => 0;

        public bool CanRender(
            MediaCompositionThumbnailRequest request) =>
            true;

        public ValueTask<IReadOnlyList<
            MediaCompositionThumbnail>> RenderAsync(
            MediaCompositionThumbnailRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<
                IReadOnlyList<
                    MediaCompositionThumbnail>>(
                [
                    new MediaCompositionThumbnail(
                        [1],
                        "image/test",
                        request.PixelWidth,
                        request.PixelHeight)
                ]);
    }
}
