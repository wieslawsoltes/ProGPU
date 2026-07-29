using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Backend;
using ProGPU.Layout;
using ProGPU.Media;
using ProGPU.Media.Audio;
using ProGPU.Media.Diagnostics;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;
using ProGPU.Media.Rendering;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Tests.Headless;
using Silk.NET.WebGPU;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Media.MediaProperties;
using Xunit;

namespace ProGPU.Tests;

public sealed class MediaPlaybackEngineTests
{
    [Fact]
    public void PlaybackRotationUsesOfficialWinUiMediaPropertiesType()
    {
        Assert.Equal(
            "Windows.Media.MediaProperties",
            typeof(MediaRotation).Namespace);
        Assert.Equal(
            typeof(MediaRotation),
            typeof(Windows.Media.Playback.MediaPlaybackSession)
                .GetProperty("PlaybackRotation")!
                .PropertyType);
    }

    [Fact]
    public void SharedPresenterCoalescesFrameworkInvalidationDispatch()
    {
        using var surface = new MediaGpuSurface();
        var context = new QueuedSynchronizationContext();
        int invalidations = 0;
        using var presenter =
            new MediaGpuSurfacePresenter(
                surface,
                () => invalidations++,
                context);

        presenter.RequestInvalidation();
        presenter.RequestInvalidation();

        Assert.Equal(1, context.PendingCount);
        Assert.Equal(0, invalidations);
        context.Drain();
        Assert.Equal(1, invalidations);
        Assert.Equal(Vector2.Zero, presenter.NaturalSize);
    }

    [Fact]
    public async Task SharedPresenterUsesOwnerDispatcherWithoutSynchronizationContext()
    {
        using var surface = new MediaGpuSurface();
        var pending = new Queue<Action>();
        int invalidations = 0;
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        MediaGpuSurfacePresenter presenter;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            presenter = new MediaGpuSurfacePresenter(
                surface,
                () => invalidations++,
                ownerContext: null,
                ownerDispatcher: pending.Enqueue);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        using (presenter)
        {
            await Task.Run(presenter.RequestInvalidation);

            Assert.Equal(0, invalidations);
            Action dispatch = Assert.Single(pending);
            dispatch();
            Assert.Equal(1, invalidations);
        }
    }

    [Fact]
    public void SharedPresenterRecordsThroughLibreWpfContextWithOuterTransform()
    {
        using var surface = new MediaGpuSurface();
        surface.Publish(CreateFrame(sequence: 2));
        var nativeContext = new DrawingContext();
        using var wpfContext =
            new System.Windows.Media.DrawingContext(
                nativeContext);
        wpfContext.PushTransform(
            new System.Windows.Media.MatrixTransform(
                2d,
                0d,
                0d,
                3d,
                11d,
                13d));
        using var presenter =
            new MediaGpuSurfacePresenter(
                surface,
                static () => { });

        Assert.True(presenter.Record(
            (IProGpuDrawingContextSource)wpfContext,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f)));

        RenderCommand command =
            Assert.Single(nativeContext.Commands);
        Assert.Equal(2f, command.Transform.M11);
        Assert.Equal(3f, command.Transform.M22);
        Assert.Equal(11f, command.Transform.M41);
        Assert.Equal(13f, command.Transform.M42);

        nativeContext.Clear();
    }

    [Fact]
    public void SharedPresenterRecordsThroughLibreWinFormsGraphicsTransform()
    {
        using var surface = new MediaGpuSurface();
        surface.Publish(CreateFrame(sequence: 3));
        var nativeContext = new DrawingContext();
        Matrix4x4 outerTransform =
            Matrix4x4.CreateTranslation(
                11f,
                13f,
                0f);
        using var graphics =
            System.Drawing.Graphics.FromProGpuDrawingContext(
                nativeContext,
                outerTransform);
        graphics.TranslateTransform(5f, 7f);
        using var presenter =
            new MediaGpuSurfacePresenter(
                surface,
                static () => { });

        Assert.True(presenter.Record(
            (IProGpuDrawingContextSource)graphics,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f)));

        RenderCommand command =
            Assert.Single(nativeContext.Commands);
        Assert.Equal(16f, command.Transform.M41);
        Assert.Equal(20f, command.Transform.M42);

        nativeContext.Clear();
    }

    [Fact]
    public async Task EngineProjectsProviderStateAndForwardsControls()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/video.mp4"));

        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        Assert.Equal(
            MediaEnginePlaybackState.Paused,
            engine.Snapshot.State);
        Assert.True(engine.Snapshot.Capabilities.HardwareDecoded);
        Assert.Equal("test-provider", engine.Diagnostics.ProviderId);
        Assert.Equal(2, engine.Diagnostics.VideoQueueDepth);
        Assert.Equal(TimeSpan.FromMilliseconds(8), engine.Diagnostics.AudioLatency);

        engine.Volume = 0.4d;
        engine.AudioBalance = -0.25d;
        engine.IsMuted = true;
        engine.IsLoopingEnabled = true;
        engine.SetPlaybackRate(1.5d);
        engine.Play();
        engine.Seek(TimeSpan.FromSeconds(3));

        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(0.4d, provider.Volume);
        Assert.Equal(-0.25d, provider.Balance);
        Assert.True(provider.Muted);
        Assert.True(provider.Looping);
        Assert.Equal(1.5d, provider.Rate);
        Assert.Equal(TimeSpan.FromSeconds(3), provider.LastSeek);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public async Task EngineProjectsBoundedSourceRangeAndEndsAtLimit()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/range.mp4"));
        int ended = 0;
        engine.Ended += (_, _) => ended++;

        await engine.SetSourceAsync(
            source,
            new MediaPlaybackRange(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(10)));

        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            provider.LastSeek);
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            engine.Snapshot.NaturalDuration);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);

        engine.Seek(TimeSpan.FromSeconds(4));

        Assert.Equal(
            TimeSpan.FromSeconds(34),
            provider.LastSeek);
        Assert.Equal(
            TimeSpan.FromSeconds(4),
            engine.Snapshot.Position);

        engine.Play();
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(36),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));
        Assert.Equal(
            TimeSpan.FromSeconds(6),
            engine.Snapshot.Position);

        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(40),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));

        Assert.Equal(1, provider.PauseCalls);
        Assert.Equal(1, ended);
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            engine.Snapshot.Position);
        Assert.Equal(
            MediaEnginePlaybackState.Paused,
            engine.Snapshot.State);

        engine.Play();

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            provider.LastSeek);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
        Assert.Equal(2, provider.PlayCalls);
    }

    [Fact]
    public async Task BoundedRangeLoopUsesEngineRelativeBoundary()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry)
        {
            IsLoopingEnabled = true
        };
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/range-loop.mp4"));

        await engine.SetSourceAsync(
            source,
            new MediaPlaybackRange(
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(5)));

        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.False(provider.Looping);

        engine.Play();
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(25),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));

        Assert.Equal(0, provider.PauseCalls);
        Assert.Equal(
            TimeSpan.FromSeconds(20),
            provider.LastSeek);
        Assert.Equal(2, provider.PlayCalls);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public async Task ProviderRegistryUsesHighestPriorityWithoutReflection()
    {
        var registry = new MediaProviderRegistry();
        var low = new RecordingProviderFactory(priority: 1);
        var high = new RecordingProviderFactory(priority: 100);
        using IDisposable lowRegistration = registry.Register(low);
        using IDisposable highRegistration = registry.Register(high);
        using var engine = new MediaPlaybackEngine(registry);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/priority.mp4"));

        await engine.SetSourceAsync(source);

        Assert.Null(low.LastProvider);
        Assert.NotNull(high.LastProvider);
        Assert.Equal("test-provider", engine.Diagnostics.ProviderId);
    }

    [Fact]
    public async Task AutoPlayAndPlaybackRateSurviveAsynchronousOpen()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry)
        {
            AutoPlay = true
        };
        engine.SetPlaybackRate(1.25d);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/autoplay.mp4"));

        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(1.25d, provider.Rate);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public async Task PlayAfterEndRestartsBeforeProviderPlayback()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/replay.mp4"));

        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Paused,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));
        engine.IsLoopingEnabled = true;

        engine.Play();

        Assert.Equal(TimeSpan.Zero, provider.LastSeek);
        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public async Task LoopingProviderEndSeeksAndReplaysWithoutBlankState()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry)
        {
            IsLoopingEnabled = true
        };
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/loop.mp4"));

        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Paused,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));

        provider.ReportEnded();

        Assert.Equal(TimeSpan.Zero, provider.LastSeek);
        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public void LatestFrameReplacementKeepsBorrowedTextureAlive()
    {
        var surface = new MediaGpuSurface();
        var first = CreateFrame(sequence: 1);
        var second = CreateFrame(sequence: 2);

        surface.Publish(first);
        Assert.True(surface.TryAcquireGpuTextureLease(out var lease));
        GpuTexture firstTexture = lease.Texture;

        surface.Publish(second);

        Assert.True(first.IsDisposed);
        Assert.False(firstTexture.IsDisposed);
        Assert.Equal(2, surface.CurrentDescriptor.Sequence);

        lease.Dispose();
        Assert.True(firstTexture.IsDisposed);

        GpuTexture secondTexture = second.Texture;
        surface.Dispose();
        Assert.True(second.IsDisposed);
        Assert.True(secondTexture.IsDisposed);
    }

    [Fact]
    public void WinUiPlayerUsesPlaybackSessionAndPresenterRecordsOneGpuDraw()
    {
        var registry = new MediaProviderRegistry();
        TestGpuFrame presentedFrame = CreateFrame(sequence: 7);
        var factory = new RecordingProviderFactory(
            priority: 10,
            frameFactory: () => presentedFrame);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/presenter.mp4"));

        player.Source = source;
        player.PlaybackSession.NormalizedSourceRect =
            new Windows.Foundation.Rect(0.25d, 0d, 0.5d, 1d);
        player.PlaybackSession.PlaybackRotation =
            MediaRotation.Clockwise90Degrees;
        player.PlaybackSession.IsMirroring = true;
        var presenter = new MediaPlayerPresenter
        {
            MediaPlayer = player,
            Stretch = Stretch.UniformToFill
        };
        presenter.Measure(new System.Numerics.Vector2(400f, 200f));
        presenter.Arrange(new Rect(0f, 0f, 400f, 200f));
        var drawingContext = new DrawingContext();
        WgpuContext? previousContext = WgpuContext.Current;

        try
        {
            WgpuContext.Current = HeadlessWindow.Shared.Context;
            presenter.OnRender(drawingContext);

            Assert.Equal(
                MediaPlaybackState.Paused,
                player.PlaybackSession.PlaybackState);
            Assert.Equal(
                (uint)1920,
                player.PlaybackSession.NaturalVideoWidth);
            RenderCommand textureCommand = Assert.Single(
                drawingContext.Commands,
                command =>
                    command.Type == RenderCommandType.DrawTexture);
            Assert.Equal(1f, textureCommand.SrcRect.X);
            Assert.Equal(2f, textureCommand.SrcRect.Width);
            Assert.NotEqual(
                System.Numerics.Matrix4x4.Identity,
                textureCommand.Transform);
            Assert.Equal(1, drawingContext.RetainedResourceCount);
            Assert.Same(
                HeadlessWindow.Shared.Context,
                presentedFrame.LastRequiredContext);
        }
        finally
        {
            drawingContext.Clear();
            WgpuContext.Current = previousContext;
        }
    }

    [Fact]
    public void AudioProcessorChainIsAllocationFreeAfterConfiguration()
    {
        var gain = new MediaAudioGainProcessor { Gain = 0.5f };
        var chain = new MediaAudioProcessorChain();
        chain.SetProcessors([gain]);
        var samples = new float[480 * 2];
        Array.Fill(samples, 1f);
        var context = new MediaAudioProcessContext(
            new MediaAudioFormat(48_000, 2),
            FrameCount: 480,
            PresentationTime: TimeSpan.Zero);

        chain.Process(samples, context);
        Array.Fill(samples, 1f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 100; iteration++)
        {
            Array.Fill(samples, 1f);
            chain.Process(samples, context);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.All(samples, sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void PortableGainEffectUpdatesPcmAndNativeGraphState()
    {
        var factory =
            new MediaAudioGainEffectFactory(
                "ProGPU.Tests.AudioGain");
        using IMediaEffect effect = factory.Create(
            new MediaEffectDescriptor(
                factory.ActivatableClassId,
                MediaEffectKind.Audio,
                new Dictionary<string, object?>()));
        var graphEffect =
            Assert.IsAssignableFrom<
                IMediaAudioGraphEffect>(effect);
        int changes = 0;
        graphEffect.StateChanged += () => changes++;

        factory.Gain = 0.25f;
        MediaAudioGraphEffectState state =
            graphEffect.CaptureState();
        var samples = new float[] { 1f, -1f, 0.5f, -0.5f };
        graphEffect.Process(
            samples,
            new MediaAudioProcessContext(
                new MediaAudioFormat(48_000, 2),
                FrameCount: 2,
                PresentationTime: TimeSpan.Zero));

        Assert.Equal(1, changes);
        Assert.Equal(
            MediaAudioGraphEffectKind.Gain,
            state.Kind);
        Assert.Equal(0.25f, state.Parameter0);
        Assert.Equal(
            [0.25f, -0.25f, 0.125f, -0.125f],
            samples);
    }

    [Fact]
    public void GainEffectDefinitionOwnsSerializedGainState()
    {
        var registry = new MediaEffectRegistry();
        var factory =
            new MediaAudioGainEffectFactory(
                "ProGPU.Tests.SerializedAudioGain");
        using IDisposable registration =
            registry.Register(factory);
        Assert.True(
            registry.IsRegistered(
                factory.ActivatableClassId));

        var descriptor = new MediaEffectDescriptor(
            factory.ActivatableClassId,
            MediaEffectKind.Audio,
            new Dictionary<string, object?>
            {
                [MediaAudioGainEffectFactory
                    .GainPropertyName] = 0.2d
            });
        Assert.True(
            registry.TryCreate(
                descriptor,
                out IMediaEffect? created));
        using IMediaEffect effect = created!;
        var graphEffect =
            Assert.IsAssignableFrom<
                IMediaAudioGraphEffect>(effect);

        factory.Gain = 0.75f;
        Assert.Equal(
            0.2f,
            graphEffect.CaptureState().Parameter0,
            precision: 6);
    }

    [Fact]
    public void AudioGraphResolverCombinesSerializedGainDefinitions()
    {
        const string gainId =
            "ProGPU.Tests.CompositionAudioGain";
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new MediaAudioGainEffectFactory(
                    gainId));
        MediaCompositionEffectDefinition[] definitions =
        [
            new(
                gainId,
                new Dictionary<string, object?>
                {
                    [MediaAudioGainEffectFactory
                        .GainPropertyName] = 0.5d
                }),
            new(
                gainId,
                new Dictionary<string, object?>
                {
                    [MediaAudioGainEffectFactory
                        .GainPropertyName] = 0.25f
                })
        ];

        Assert.True(
            MediaAudioGraphEffectResolver
                .TryCaptureCombinedGain(
                    registry,
                    definitions,
                    out double gain));
        Assert.Equal(0.125d, gain);
        Assert.False(
            MediaAudioGraphEffectResolver
                .TryCaptureCombinedGain(
                    registry,
                    [
                        new MediaCompositionEffectDefinition(
                            "ProGPU.Tests.Unregistered",
                            new Dictionary<string, object?>())
                    ],
                    out _));
    }

    [Fact]
    public void AudioTimelineProcessesOnlyScheduledFramesWithoutAllocating()
    {
        var gain = new MediaAudioGainProcessor
        {
            Gain = 0.25f
        };
        var timeline = new MediaAudioTimelineProcessor(
            [
                new MediaAudioTimelineSegment(
                    TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(10),
                    [gain])
            ]);
        var samples = new float[160 * 2];
        var context = new MediaAudioProcessContext(
            new MediaAudioFormat(8_000, 2),
            FrameCount: 160,
            PresentationTime: TimeSpan.Zero);

        Array.Fill(samples, 1f);
        timeline.Process(samples, context);
        for (int frame = 0; frame < 160; frame++)
        {
            float expected =
                frame is >= 40 and < 120
                    ? 0.25f
                    : 1f;
            Assert.Equal(expected, samples[frame * 2]);
            Assert.Equal(expected, samples[frame * 2 + 1]);
        }

        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0;
             iteration < 100;
             iteration++)
        {
            Array.Fill(samples, 1f);
            timeline.Process(samples, context);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SharedSceneAdapterRecordsTypedGpuEffectWithoutPayloadAllocation()
    {
        using var surface = new MediaGpuSurface();
        surface.Publish(CreateFrame(sequence: 12));
        var context = new DrawingContext();
        var options = new MediaVideoPresentationOptions(
            stretch: MediaVideoStretch.Fill,
            normalizedSourceRect:
                new System.Numerics.Vector4(
                    0.25f,
                    0f,
                    0.5f,
                    1f),
            effects: new MediaVideoEffectOptions(
                brightness: 0.1f,
                contrast: 1.2f,
                invert: 1f));

        Assert.True(context.DrawLatestFrame(
            surface,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f),
            in options));

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(
            CompositorBuiltInExtensions.ImageEffect,
            command.ExtensionId);
        Assert.True(command.HasImageEffect);
        Assert.Null(command.DataParam);
        Assert.Equal(new Rect(1f, 0f, 2f, 2f), command.SrcRect);
        Assert.Equal(0.1f, command.ImageEffect.Brightness);
        Assert.Equal(1.2f, command.ImageEffect.Contrast);
        Assert.Equal(1f, command.ImageEffect.Invert);
        Assert.Equal(1, context.RetainedResourceCount);

        context.Clear();
    }

    [Fact]
    public void SharedSceneAdapterRetainsAndFusesNv12Planes()
    {
        using var surface = new MediaGpuSurface();
        var frame = new TestPlanarGpuFrame(
            HeadlessWindow.Shared.Context);
        surface.Publish(frame);
        var context = new DrawingContext();
        var options = new MediaVideoPresentationOptions(
            stretch: MediaVideoStretch.Fill);

        Assert.True(context.DrawLatestFrame(
            surface,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f),
            in options));

        RenderCommand command = Assert.Single(context.Commands);
        Assert.True(command.HasImageEffect);
        Assert.Same(
            frame.LumaTexture,
            command.Texture);
        Assert.Same(
            frame.ChromaTexture,
            command.ImageEffect.ChromaTexture);
        Assert.True(
            command.ImageEffect.YuvConversion.HasValue);
        Assert.Equal(2, context.RetainedResourceCount);

        context.Clear();
        Assert.False(frame.LumaTexture.IsDisposed);
        Assert.False(frame.ChromaTexture.IsDisposed);
    }

    [Fact]
    public void SharedMesh3DAdapterBindsPlanarSurfaceAndEffects()
    {
        using var surface = new MediaGpuSurface();
        var frame = new TestPlanarGpuFrame(
            HeadlessWindow.Shared.Context);
        surface.Publish(frame);
        var entry = new MeshCompilationEntry();
        var effects = new MediaVideoEffectOptions(
            brightness: 0.1f,
            grayscale: 0.4f,
            samplingMode: TextureSamplingMode.Nearest);
        var presentation = new MediaVideoPresentationOptions(
            stretch: MediaVideoStretch.Fill,
            normalizedSourceRect:
                new Vector4(0.25f, 0f, 0.5f, 1f),
            rotation:
                MediaVideoRotation.Clockwise270Degrees,
            isMirrored: true,
            effects: effects);

        Assert.True(entry.UseLatestFrame(
            surface,
            in presentation));

        Assert.Same(surface, entry.TextureSource);
        Assert.True(entry.YuvConversion.HasValue);
        Assert.Equal(0.1f, entry.TextureEffect.Brightness);
        Assert.Equal(0.4f, entry.TextureEffect.Grayscale);
        Assert.Equal(
            TextureSamplingMode.Nearest,
            entry.TextureSamplingMode);
        Assert.Equal(
            new Vector4(0.25f, 0f, 0.5f, 1f),
            entry.TexturePresentation.NormalizedSourceRect);
        Assert.Equal(
            3,
            entry.TexturePresentation.ClockwiseQuarterTurns);
        Assert.True(entry.TexturePresentation.IsMirrored);
    }

    [Fact]
    public void Mesh3DShadersSharePlanarStorageRecordAbi()
    {
        string solid = ShaderResource.Load(
            typeof(Mesh3DExtensionPipeline),
            "Mesh3DSolid.wgsl");
        string wireframe = ShaderResource.Load(
            typeof(Mesh3DExtensionPipeline),
            "Mesh3DWireframe.wgsl");

        Assert.Equal(448, System.Runtime.InteropServices.Marshal
            .SizeOf<GpuMesh3DRecord>());
        Assert.Equal(
            GetRecordDeclaration(solid),
            GetRecordDeclaration(wireframe));
        Assert.Contains(
            "yuvRange: vec4<f32>",
            solid,
            StringComparison.Ordinal);
        Assert.Contains(
            "TransformMaterialCoordinate",
            solid,
            StringComparison.Ordinal);

        static string GetRecordDeclaration(string shader)
        {
            const string prefix =
                "struct GpuMesh3DRecord {";
            int start = shader.IndexOf(
                prefix,
                StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = shader.IndexOf(
                "};",
                start,
                StringComparison.Ordinal);
            Assert.True(end > start);
            return shader.Substring(
                start,
                end + 2 - start);
        }
    }

    [Fact]
    public void Mesh3DCompileScratchReusesPeakCapacity()
    {
        var scratch = new Mesh3DCompileScratch();
        scratch.EnsureCapacity(3);

        Assert.Equal(4, scratch.Capacity);
        scratch.Records[0] = new GpuMesh3DRecord
        {
            Opacity = 0.75f
        };
        scratch.TextureBindGroups[0] = (nint)42;

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0;
             iteration < 4_096;
             iteration++)
        {
            scratch.EnsureCapacity(3);
            scratch.Records[1].Opacity =
                iteration;
            scratch.TextureBindGroups[1] =
                (nint)iteration;
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        Assert.Equal(0, allocated);
        Assert.Equal(
            4_095f,
            scratch.Records[1].Opacity);
        Assert.Equal(
            (nint)4_095,
            scratch.TextureBindGroups[1]);

        scratch.EnsureCapacity(5);
        Assert.Equal(8, scratch.Capacity);
        Assert.Equal(
            0.75f,
            scratch.Records[0].Opacity);
        Assert.Equal(
            (nint)42,
            scratch.TextureBindGroups[0]);
    }

    [Fact]
    public void WinUiMesh3DMaterialRendersNv12WithoutFallbackTexture()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 90);
        using var player =
            new Windows.Media.Playback.MediaPlayer();
        MediaGpuSurface surface =
            player.GetProGpuSurface();
        var frame = new TestPlanarGpuFrame(window.Context);
        frame.LumaTexture.WritePixels(
            new byte[] { 63, 63, 63, 63, 63, 63, 63, 63 });
        frame.ChromaTexture.WritePixels(
            new byte[] { 102, 240, 102, 240 });
        surface.Publish(frame);
        using var material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player
            };
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new Vector3(-1.5f, -0.8f, 0f),
                new Vector3(1.5f, -0.8f, 0f),
                new Vector3(1.5f, 0.8f, 0f),
                new Vector3(-1.5f, 0.8f, 0f)
            ],
            Normals =
            [
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ
            ],
            TextureCoordinates =
            [
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0f)
            ],
            TriangleIndices = [0, 1, 2, 0, 2, 3]
        };
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Width = 4f
            },
            ShadingMode = ShadingMode3D.Flat
        };
        viewport.Children.Add(
            new ModelVisual3D
            {
                Content = new GeometryModel3D
                {
                    Geometry = mesh,
                    Material = material,
                    BackMaterial = material
                }
            });
        window.Content = viewport;

        try
        {
            window.Render();
            byte[] pixels = window.ReadPixels();
            int redVideoPixels = 0;
            for (int offset = 0;
                 offset < pixels.Length;
                 offset += 4)
            {
                if (pixels[offset] >= 180 &&
                    pixels[offset + 1] <= 60 &&
                    pixels[offset + 2] <= 60 &&
                    pixels[offset + 3] == 255)
                {
                    redVideoPixels++;
                }
            }

            Assert.True(
                redVideoPixels >= 1_000,
                $"Expected a filled converted-red video quad, " +
                $"found {redVideoPixels} red pixels.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WinUiMesh3DMaterialAppliesSessionCropRotationAndMirror()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 90);
        using var player =
            new Windows.Media.Playback.MediaPlayer();
        MediaGpuSurface surface =
            player.GetProGpuSurface();
        TestGpuFrame frame = CreateFrame(sequence: 21);
        frame.Texture.WritePixels(
        new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            255, 255, 0, 255,
            255, 0, 255, 255,
            0, 255, 255, 255,
            255, 255, 255, 255,
            0, 0, 0, 255
        });
        surface.Publish(frame);
        player.PlaybackSession.NormalizedSourceRect =
            new Windows.Foundation.Rect(0d, 0d, 0.5d, 1d);
        player.PlaybackSession.PlaybackRotation =
            MediaRotation.Clockwise90Degrees;
        using var material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player,
                SamplingMode = TextureSamplingMode.Nearest
            };
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new Vector3(-1.5f, -0.8f, 0f),
                new Vector3(1.5f, -0.8f, 0f),
                new Vector3(1.5f, 0.8f, 0f),
                new Vector3(-1.5f, 0.8f, 0f)
            ],
            Normals =
            [
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ
            ],
            TextureCoordinates =
            [
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0f)
            ],
            TriangleIndices = [0, 1, 2, 0, 2, 3]
        };
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Width = 4f
            },
            ShadingMode = ShadingMode3D.Flat
        };
        viewport.Children.Add(
            new ModelVisual3D
            {
                Content = new GeometryModel3D
                {
                    Geometry = mesh,
                    Material = material,
                    BackMaterial = material
                }
            });
        window.Content = viewport;

        try
        {
            window.Render();
            long versionBeforePresentationChange =
                viewport.ChangeVersion;
            player.PlaybackSession.IsMirroring = true;
            Assert.True(
                viewport.ChangeVersion >
                    versionBeforePresentationChange);
            window.Render();
            byte[] pixels = window.ReadPixels();
            (int Count, long X, long Y) red = default;
            (int Count, long X, long Y) magenta = default;
            (int Count, long X, long Y) green = default;
            (int Count, long X, long Y) cyan = default;
            for (int y = 0; y < 90; y++)
            {
                for (int x = 0; x < 160; x++)
                {
                    int offset = (y * 160 + x) * 4;
                    byte r = pixels[offset];
                    byte g = pixels[offset + 1];
                    byte b = pixels[offset + 2];
                    if (r > 220 && g < 35 && b < 35)
                    {
                        red.Count++;
                        red.X += x;
                        red.Y += y;
                    }
                    else if (r > 220 && g < 35 && b > 220)
                    {
                        magenta.Count++;
                        magenta.X += x;
                        magenta.Y += y;
                    }
                    else if (r < 35 && g > 220 && b < 35)
                    {
                        green.Count++;
                        green.X += x;
                        green.Y += y;
                    }
                    else if (r < 35 && g > 220 && b > 220)
                    {
                        cyan.Count++;
                        cyan.X += x;
                        cyan.Y += y;
                    }
                }
            }

            string counts =
                $"red={red.Count}, magenta={magenta.Count}, " +
                $"green={green.Count}, cyan={cyan.Count}";
            Assert.True(red.Count > 200, counts);
            Assert.True(magenta.Count > 200, counts);
            Assert.True(green.Count > 200, counts);
            Assert.True(cyan.Count > 200, counts);
            double redX = (double)red.X / red.Count;
            double redY = (double)red.Y / red.Count;
            double magentaX =
                (double)magenta.X / magenta.Count;
            double magentaY =
                (double)magenta.Y / magenta.Count;
            double greenX = (double)green.X / green.Count;
            double greenY = (double)green.Y / green.Count;
            double cyanX = (double)cyan.X / cyan.Count;
            double cyanY = (double)cyan.Y / cyan.Count;
            string layout =
                $"{counts}; red=({redX:F1},{redY:F1}), " +
                $"magenta=({magentaX:F1},{magentaY:F1}), " +
                $"green=({greenX:F1},{greenY:F1}), " +
                $"cyan=({cyanX:F1},{cyanY:F1})";
            // The default Viewport3D camera reverses screen X. The expected
            // UV arrangement after crop, clockwise rotation, then mirror is
            // red/magenta over green/cyan.
            Assert.True(magentaX < redX, layout);
            Assert.True(redY < greenY, layout);
            Assert.True(cyanX < greenX, layout);
            Assert.True(magentaY < cyanY, layout);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public async Task TypedEffectRegistryReplaysEffectsToNewProvider()
    {
        var providers = new MediaProviderRegistry();
        var providerFactory = new RecordingProviderFactory(priority: 10);
        using IDisposable providerRegistration =
            providers.Register(providerFactory);
        var effects = new MediaEffectRegistry();
        var effectFactory = new RecordingEffectFactory();
        using IDisposable effectRegistration =
            effects.Register(effectFactory);
        using var engine = new MediaPlaybackEngine(
            providers,
            effects);

        engine.AddEffect(
            effectFactory.ActivatableClassId,
            MediaEffectKind.Audio,
            optional: true,
            new Dictionary<string, object?>());
        engine.AddEffect(
            "missing.optional.effect",
            MediaEffectKind.Video,
            optional: true,
            new Dictionary<string, object?>());
        Assert.Throws<NotSupportedException>(() =>
            engine.AddEffect(
                "missing.required.effect",
                MediaEffectKind.Video,
                optional: false,
                new Dictionary<string, object?>()));

        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/effects.mp4"));
        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            providerFactory.LastProvider);
        Assert.Equal(1, provider.AddEffectCalls);
        Assert.True(provider.LastEffectOptional);
        RecordingEffect effect = Assert.IsType<RecordingEffect>(
            effectFactory.LastEffect);

        engine.RemoveAllEffects();

        Assert.Equal(1, provider.RemoveAllEffectsCalls);
        Assert.True(effect.IsDisposed);
    }

    [Fact]
    public void WinUiPlaybackSessionProjectsOfficialTimeRangesAndEvents()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/ranges.mp4"));
        player.Source = source;
        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        object? bufferingArgs = null;
        int bufferedChanges = 0;
        int playedChanges = 0;
        player.PlaybackSession.BufferingStarted +=
            (_, args) => bufferingArgs = args;
        player.PlaybackSession.BufferedRangesChanged +=
            (_, _) => bufferedChanges++;
        player.PlaybackSession.PlayedRangesChanged +=
            (_, _) => playedChanges++;

        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Buffering,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
            1920,
            1080,
            BufferingProgress: 0.25d,
            DownloadProgress: 0.5d,
            PlaybackRate: 1d,
            new MediaProviderCapabilities(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: true,
                HardwareDecoded: true,
                HasAudio: true,
                HasVideo: true)));

        MediaTimeRange buffered = Assert.Single(
            player.PlaybackSession.GetBufferedRanges());
        Assert.Equal(TimeSpan.Zero, buffered.Start);
        Assert.Equal(TimeSpan.FromSeconds(5), buffered.End);
        MediaTimeRange played = Assert.Single(
            player.PlaybackSession.GetPlayedRanges());
        Assert.Equal(TimeSpan.FromSeconds(3), played.End);
        MediaTimeRange seekable = Assert.Single(
            player.PlaybackSession.GetSeekableRanges());
        Assert.Equal(TimeSpan.FromSeconds(10), seekable.End);
        Assert.True(
            player.PlaybackSession.IsSupportedPlaybackRateRange(
                0.5d,
                2d));
        Assert.False(
            player.PlaybackSession.IsSupportedPlaybackRateRange(
                0.25d,
                4d));
        Assert.IsType<
            MediaPlaybackSessionBufferingStartedEventArgs>(
            bufferingArgs);
        Assert.True(bufferedChanges > 0);
        Assert.True(playedChanges > 0);
    }

    [Fact]
    public void WinUiPlaybackListAdvancesAtItemDurationLimit()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/first-range.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/second-range.mp4"));
        var first = new MediaPlaybackItem(
            firstSource,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5));
        var second = new MediaPlaybackItem(
            secondSource,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(7));
        var list = new MediaPlaybackList();
        list.Items.Add(first);
        list.Items.Add(second);
        CurrentMediaPlaybackItemChangedEventArgs? changed = null;
        list.CurrentItemChanged +=
            (_, args) => changed = args;

        player.Source = list;
        RecordingProvider firstProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            firstProvider.LastSeek);
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            player.PlaybackSession.NaturalDuration);

        player.Play();
        firstProvider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(35),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            new MediaProviderCapabilities(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: true,
                HardwareDecoded: true,
                HasAudio: true,
                HasVideo: true)));

        Assert.Same(second, list.CurrentItem);
        Assert.NotNull(changed);
        Assert.Equal(
            MediaPlaybackItemChangedReason.EndOfStream,
            changed.Reason);
        RecordingProvider secondProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.NotSame(firstProvider, secondProvider);
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            secondProvider.LastSeek);
        Assert.Equal(
            TimeSpan.FromSeconds(7),
            player.PlaybackSession.NaturalDuration);
        Assert.Equal(1, secondProvider.PlayCalls);
    }

    [Fact]
    public void WinUiPlaybackListNavigationReturnsCurrentItem()
    {
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/first.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/second.mp4"));
        using MediaSource thirdSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/third.mp4"));
        var first = new MediaPlaybackItem(firstSource);
        var second = new MediaPlaybackItem(secondSource)
        {
            IsDisabledInPlaybackList = true
        };
        var third = new MediaPlaybackItem(thirdSource);
        var list = new MediaPlaybackList();
        list.Items.Add(first);
        list.Items.Add(second);
        list.Items.Add(third);

        Assert.Equal(
            typeof(MediaPlaybackItem),
            typeof(MediaPlaybackList)
                .GetMethod(nameof(MediaPlaybackList.MoveNext))!
                .ReturnType);
        Assert.Same(first, list.CurrentItem);
        Assert.Same(third, list.MoveNext());
        Assert.Null(list.MoveNext());
        Assert.Same(third, list.CurrentItem);
        Assert.Same(first, list.MovePrevious());
        Assert.Same(third, list.MoveTo(2));
        Assert.Null(list.MoveTo(3));

        list.AutoRepeatEnabled = true;

        Assert.Same(first, list.MoveNext());
    }

    [Fact]
    public void WinUiPlayerProjectsLegacyStateAndProviderConfiguration()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry())
        {
            AudioCategory = MediaPlayerAudioCategory.Movie,
            AudioDeviceType =
                MediaPlayerAudioDeviceType.Communications,
            RealTimePlayback = true,
            StereoscopicVideoRenderMode =
                StereoscopicVideoRenderMode.Stereo
        };
        using MediaSource source = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/legacy.mp4"));
        int bufferingStarted = 0;
        int bufferingEnded = 0;
        int stateChanges = 0;
        double changedRate = 0d;
#pragma warning disable CS0618
        player.BufferingStarted += (_, _) => bufferingStarted++;
        player.BufferingEnded += (_, _) => bufferingEnded++;
        player.CurrentStateChanged += (_, _) => stateChanges++;
        player.MediaPlayerRateChanged +=
            (_, args) => changedRate = args.NewRate;
#pragma warning restore CS0618

        player.Source = source;
        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);

        Assert.Equal(
            MediaAudioCategory.Movie,
            provider.Configuration.AudioCategory);
        Assert.Equal(
            MediaAudioDeviceRole.Communications,
            provider.Configuration.AudioDeviceRole);
        Assert.True(provider.Configuration.RealTimePlayback);
        Assert.Equal(
            MediaStereoscopicRenderMode.Stereo,
            provider.Configuration.StereoscopicRenderMode);
#pragma warning disable CS0618
        Assert.Equal(TimeSpan.FromMinutes(2), player.NaturalDuration);
        Assert.Equal(1d, player.BufferingProgress);
        Assert.Equal(MediaPlayerState.Paused, player.CurrentState);
#pragma warning restore CS0618

        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Buffering,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            0.5d,
            1d,
            1d,
            engineCapabilities()));
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Paused,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engineCapabilities()));
        player.PlaybackSession.PlaybackRate = 1.5d;

        Assert.Equal(1, bufferingStarted);
        Assert.Equal(1, bufferingEnded);
        Assert.Equal(4, stateChanges);
        Assert.Equal(1.5d, changedRate);

        static MediaProviderCapabilities engineCapabilities() =>
            new(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: true,
                HardwareDecoded: true,
                HasAudio: true,
                HasVideo: true);
    }

    [Fact]
    public void WinUiCommandManagerBehaviorsFollowPlaybackState()
    {
        using var player = new MediaPlayer(
            new MediaProviderRegistry(),
            new MediaEffectRegistry());
        var list = new MediaPlaybackList();
        using MediaSource first = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/one.mp4"));
        using MediaSource second = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/two.mp4"));
        list.Items.Add(new MediaPlaybackItem(first));
        list.Items.Add(new MediaPlaybackItem(second));

        player.Source = list;

        Assert.Same(player, player.CommandManager.MediaPlayer);
        Assert.True(player.CommandManager.NextBehavior.IsEnabled);
        Assert.False(
            player.CommandManager.PreviousBehavior.IsEnabled);
        int enabledChanges = 0;
        player.CommandManager.NextBehavior.IsEnabledChanged +=
            (_, _) => enabledChanges++;

        player.CommandManager.NextBehavior.EnablingRule =
            MediaCommandEnablingRule.Never;

        Assert.False(player.CommandManager.NextBehavior.IsEnabled);
        Assert.Equal(1, enabledChanges);
        Assert.Same(
            player.CommandManager,
            player.CommandManager.NextBehavior.CommandManager);
    }

    [Fact]
    public void NativeCommandSeamHonorsWinUiDeferralAndHandled()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/commands.mp4"));
        player.Source = source;
        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Windows.Foundation.Deferral? deferral = null;
        MediaPlaybackCommandManagerPlayReceivedEventArgs?
            received = null;
        player.CommandManager.PlayReceived += (_, args) =>
        {
            received = args;
            deferral = args.GetDeferral();
        };

        bool dispatched = player.TryDispatchProGpuCommand(
            new ProGpuMediaPlaybackCommand(
                ProGpuMediaPlaybackCommandKind.Play));

        Assert.True(dispatched);
        Assert.Equal(0, provider.PlayCalls);
        Assert.NotNull(received);
        received.Handled = true;
        Assert.NotNull(deferral);
        deferral.Complete();
        Assert.Equal(0, provider.PlayCalls);
    }

    [Fact]
    public void ExternalFrameRetainsRejectedNativeOwnerUntilDisposal()
    {
        using var context = new WgpuContext();
        context.SetExternalTextureImporter(
            new RejectingMediaTextureImporter());
        var owner = new RecordingNativeOwner();
        using var frame = CreateExternalFrame(owner);

        Assert.False(
            frame.TryAcquireGpuTextureLease(
                context,
                out IProGpuTextureLease lease));
        Assert.Null(lease);
        Assert.False(owner.IsDisposed);

        frame.Dispose();

        Assert.True(owner.IsDisposed);
    }

    [Fact]
    public void ExternalFrameLeaseDefersImportedNativeOwnerRelease()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        context.SetExternalTextureImporter(
            new AllocatingMediaTextureImporter());
        var owner = new RecordingNativeOwner();
        var frame = CreateExternalFrame(owner);

        Assert.True(
            frame.TryAcquireGpuTextureLease(
                context,
                out IProGpuTextureLease lease));
        frame.Dispose();
        Assert.False(owner.IsDisposed);

        lease.Dispose();
        Assert.False(owner.IsDisposed);
        context.CleanupPendingResources();

        Assert.True(owner.IsDisposed);
    }

    private static ExternalMediaGpuFrame CreateExternalFrame(
        IDisposable owner)
    {
        var descriptor = new MediaGpuFrameDescriptor(
            1,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(16),
            4,
            2,
            MediaVideoPixelFormat.Bgra8,
            MediaTransferMode.NativeZeroCopy,
            new MediaColorInfo(
                MediaColorPrimaries.Bt709,
                MediaTransferFunction.Srgb,
                MediaMatrixCoefficients.Identity,
                FullRange: true));
        var externalDescriptor =
            new ProGpuExternalTextureDescriptor(
                ProGpuExternalTextureHandleKind.IOSurface,
                1,
                descriptor.Width,
                descriptor.Height,
                TextureFormat.Bgra8Unorm,
                TextureUsage.TextureBinding,
                GpuTextureAlphaMode.Straight,
                IsInitialized: true);
        return new ExternalMediaGpuFrame(
            in descriptor,
            in externalDescriptor,
            owner);
    }

    private static TestGpuFrame CreateFrame(long sequence)
    {
        var texture = new GpuTexture(
            HeadlessWindow.Shared.Context,
            4,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            $"Media test frame {sequence}");
        return new TestGpuFrame(
            texture,
            new MediaGpuFrameDescriptor(
                sequence,
                TimeSpan.FromMilliseconds(sequence * 16),
                TimeSpan.FromMilliseconds(16),
                4,
                2,
                MediaVideoPixelFormat.Rgba8,
                MediaTransferMode.NativeZeroCopy,
                new MediaColorInfo(
                    MediaColorPrimaries.Bt709,
                    MediaTransferFunction.Srgb,
                    MediaMatrixCoefficients.Identity,
                    FullRange: true)));
    }

    private sealed class RecordingProviderFactory :
        IMediaPlaybackProviderFactory
    {
        private readonly Func<IMediaGpuFrame>? _frameFactory;

        public RecordingProviderFactory(
            int priority,
            Func<IMediaGpuFrame>? frameFactory = null)
        {
            Priority = priority;
            _frameFactory = frameFactory;
        }

        public string Id => $"test-factory-{Priority}";
        public int Priority { get; }
        public RecordingProvider? LastProvider { get; private set; }

        public bool CanOpen(MediaSourceDescriptor source) => true;

        public ValueTask<IMediaPlaybackProvider> CreateAsync(
            MediaSourceDescriptor source,
            IMediaPlaybackSink sink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastProvider = new RecordingProvider(
                sink,
                _frameFactory);
            return ValueTask.FromResult<IMediaPlaybackProvider>(
                LastProvider);
        }
    }

    private sealed class RecordingProvider :
        IMediaPlaybackProvider,
        IMediaPlaybackConfigurationProvider
    {
        private readonly IMediaPlaybackSink _sink;
        private readonly Func<IMediaGpuFrame>? _frameFactory;

        public RecordingProvider(
            IMediaPlaybackSink sink,
            Func<IMediaGpuFrame>? frameFactory)
        {
            _sink = sink;
            _frameFactory = frameFactory;
        }

        public string Id => "test-provider";
        public int PlayCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public TimeSpan LastSeek { get; private set; }
        public double Rate { get; private set; } = 1d;
        public double Volume { get; private set; } = 1d;
        public double Balance { get; private set; }
        public bool Muted { get; private set; }
        public bool Looping { get; private set; }
        public int AddEffectCalls { get; private set; }
        public bool LastEffectOptional { get; private set; }
        public int RemoveAllEffectsCalls { get; private set; }
        public MediaPlaybackConfiguration Configuration
        {
            get;
            private set;
        } = MediaPlaybackConfiguration.Default;

        public ValueTask OpenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sink.Opened(new MediaPlaybackSnapshot(
                MediaEnginePlaybackState.Paused,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(2),
                1920,
                1080,
                1d,
                1d,
                Rate,
                new MediaProviderCapabilities(
                    CanPause: true,
                    CanSeek: true,
                    SupportsRate: true,
                    SupportsFrameStepping: true,
                    HardwareDecoded: true,
                    HasAudio: true,
                    HasVideo: true)));
            if (_frameFactory is not null)
            {
                _sink.Present(_frameFactory());
            }
            _sink.UpdateDiagnostics(new MediaProviderDiagnostics(
                HardwareDecoded: true,
                TransferMode: _frameFactory is null
                    ? null
                    : MediaTransferMode.NativeZeroCopy,
                DroppedFrames: 0,
                VideoQueueDepth: 2,
                AudioQueueDepth: 1,
                AudioLatency: TimeSpan.FromMilliseconds(8),
                LastFallbackReason: null));
            return ValueTask.CompletedTask;
        }

        public void Play() => PlayCalls++;
        public void Pause() => PauseCalls++;
        public void Seek(TimeSpan position)
        {
            LastSeek = position;
            _sink.SeekCompleted(position);
        }
        public void SetPlaybackRate(double value) => Rate = value;

        public void SetVolume(
            double volume,
            double balance,
            bool muted)
        {
            Volume = volume;
            Balance = balance;
            Muted = muted;
        }

        public void SetLooping(bool enabled) => Looping = enabled;
        public bool StepForwardOneFrame() => true;
        public bool StepBackwardOneFrame() => true;
        public void AddEffect(IMediaEffect effect, bool optional)
        {
            AddEffectCalls++;
            LastEffectOptional = optional;
        }

        public void RemoveAllEffects() =>
            RemoveAllEffectsCalls++;
        public void ApplyConfiguration(
            in MediaPlaybackConfiguration configuration) =>
            Configuration = configuration;
        public void Report(MediaPlaybackSnapshot snapshot) =>
            _sink.Update(in snapshot);
        public void ReportEnded() => _sink.Ended();
        public void Dispose() { }
    }

    private sealed class RecordingEffectFactory :
        IMediaEffectFactory
    {
        public string ActivatableClassId =>
            "ProGPU.Tests.RecordingAudioEffect";

        public RecordingEffect? LastEffect { get; private set; }

        public IMediaEffect Create(
            in MediaEffectDescriptor descriptor)
        {
            LastEffect = new RecordingEffect(descriptor.Kind);
            return LastEffect;
        }
    }

    private sealed class RecordingEffect : IMediaEffect
    {
        private int _disposed;

        public RecordingEffect(MediaEffectKind kind)
        {
            Kind = kind;
        }

        public string Id => "recording-effect";
        public MediaEffectKind Kind { get; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() =>
            Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class TestGpuFrame :
        IMediaGpuFrame,
        IProGpuContextTextureLeaseSource
    {
        private readonly SharedGpuTextureSource _source;
        private int _disposed;

        public TestGpuFrame(
            GpuTexture texture,
            MediaGpuFrameDescriptor descriptor)
        {
            Texture = texture;
            Descriptor = descriptor;
            _source = new SharedGpuTextureSource(texture);
        }

        public GpuTexture Texture { get; }
        public MediaGpuFrameDescriptor Descriptor { get; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public WgpuContext? LastRequiredContext { get; private set; }

        public bool TryGetGpuTexture(out GpuTexture texture) =>
            _source.TryGetGpuTexture(out texture);

        public bool TryAcquireGpuTextureLease(
            out IProGpuTextureLease lease) =>
            _source.TryAcquireGpuTextureLease(out lease);

        public bool TryGetGpuTexture(
            WgpuContext requiredContext,
            out GpuTexture texture)
        {
            LastRequiredContext = requiredContext;
            if (!Texture.Context.SharesDeviceWith(requiredContext))
            {
                texture = null!;
                return false;
            }
            return TryGetGpuTexture(out texture);
        }

        public bool TryAcquireGpuTextureLease(
            WgpuContext requiredContext,
            out IProGpuTextureLease lease)
        {
            LastRequiredContext = requiredContext;
            if (!Texture.Context.SharesDeviceWith(requiredContext))
            {
                lease = null!;
                return false;
            }
            return TryAcquireGpuTextureLease(out lease);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _source.Dispose();
            }
        }
    }

    private sealed class TestPlanarGpuFrame :
        IMediaGpuPlanarFrame
    {
        private readonly SharedGpuTextureSource _luma;
        private readonly SharedGpuTextureSource _chroma;
        private int _disposed;

        public TestPlanarGpuFrame(WgpuContext context)
        {
            LumaTexture = new GpuTexture(
                context,
                4,
                2,
                TextureFormat.R8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.CopyDst,
                "Test NV12 luma");
            ChromaTexture = new GpuTexture(
                context,
                2,
                1,
                TextureFormat.RG8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.CopyDst,
                "Test NV12 chroma");
            _luma = new SharedGpuTextureSource(LumaTexture);
            _chroma =
                new SharedGpuTextureSource(ChromaTexture);
            Descriptor = new MediaGpuFrameDescriptor(
                1,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(16),
                4,
                2,
                MediaVideoPixelFormat.Nv12,
                MediaTransferMode.NativeZeroCopy,
                new MediaColorInfo(
                    MediaColorPrimaries.Bt709,
                    MediaTransferFunction.Bt709,
                    MediaMatrixCoefficients.Bt709,
                    FullRange: false));
        }

        public GpuTexture LumaTexture { get; }
        public GpuTexture ChromaTexture { get; }
        public MediaGpuFrameDescriptor Descriptor { get; }

        public bool TryGetGpuTexture(out GpuTexture texture)
        {
            texture = null!;
            return false;
        }

        public bool TryAcquireGpuTextureLease(
            out IProGpuTextureLease lease)
        {
            lease = null!;
            return false;
        }

        public bool TryAcquireGpuPlaneTextureLeases(
            WgpuContext requiredContext,
            out IProGpuTextureLease lumaLease,
            out IProGpuTextureLease chromaLease)
        {
            if (!LumaTexture.Context.SharesDeviceWith(
                    requiredContext) ||
                !_luma.TryAcquireGpuTextureLease(
                    out lumaLease))
            {
                lumaLease = null!;
                chromaLease = null!;
                return false;
            }
            if (!_chroma.TryAcquireGpuTextureLease(
                    out chromaLease))
            {
                lumaLease.Dispose();
                lumaLease = null!;
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) == 0)
            {
                _luma.Dispose();
                _chroma.Dispose();
            }
        }
    }

    private sealed class RecordingNativeOwner : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            Assert.False(IsDisposed);
            IsDisposed = true;
        }
    }

    private sealed class QueuedSynchronizationContext :
        SynchronizationContext
    {
        private readonly Queue<(
            SendOrPostCallback Callback,
            object? State)> _queue = new();

        public int PendingCount => _queue.Count;

        public override void Post(
            SendOrPostCallback callback,
            object? state) =>
            _queue.Enqueue((callback, state));

        public void Drain()
        {
            while (_queue.TryDequeue(
                       out (
                           SendOrPostCallback Callback,
                           object? State) item))
            {
                item.Callback(item.State);
            }
        }
    }

    private sealed class RejectingMediaTextureImporter :
        IProGpuExternalTextureImporter
    {
        public bool TryImportExternalTexture(
            WgpuContext targetContext,
            in ProGpuExternalTextureDescriptor descriptor,
            IDisposable nativeOwner,
            out GpuTexture texture)
        {
            texture = null!;
            return false;
        }
    }

    private sealed unsafe class AllocatingMediaTextureImporter :
        IProGpuExternalTextureImporter
    {
        public bool TryImportExternalTexture(
            WgpuContext targetContext,
            in ProGpuExternalTextureDescriptor descriptor,
            IDisposable nativeOwner,
            out GpuTexture texture)
        {
            var textureDescriptor = new TextureDescriptor
            {
                Usage = descriptor.Usage,
                Dimension = TextureDimension.Dimension2D,
                Size = new Extent3D
                {
                    Width = descriptor.Width,
                    Height = descriptor.Height,
                    DepthOrArrayLayers = 1
                },
                Format = descriptor.Format,
                MipLevelCount = 1,
                SampleCount = 1
            };
            Texture* nativeTexture =
                targetContext.Api.DeviceCreateTexture(
                    targetContext.Device,
                    &textureDescriptor);
            Assert.True(nativeTexture != null);
            texture = GpuTexture.WrapOwnedExternal(
                targetContext,
                nativeTexture,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.Usage,
                "Synthetic external media frame",
                descriptor.AlphaMode,
                nativeOwner);
            return true;
        }
    }
}
