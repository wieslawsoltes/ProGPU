using AVFoundation;
using CoreFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;
using ProGPU.Backend;
using ProGPU.Media.Audio;
using ProGPU.Media.Diagnostics;
using ProGPU.Media.Effects;
using ProGPU.Media.Editing;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;
using Silk.NET.WebGPU;

namespace ProGPU.Apple.Media;

/// <summary>
/// Registers AVFoundation as the native Apple media provider. Registration is
/// explicit so applications can replace it with a higher-priority provider.
/// </summary>
public static class AppleMedia
{
    public static IDisposable Register(
        MediaProviderRegistry? registry = null,
        int priority = 100)
    {
        IDisposable playback =
            (registry ?? MediaProviderRegistry.Default).Register(
                new AppleMediaPlaybackProviderFactory(priority));
        IDisposable export =
            MediaCompositionExportRegistry.Default.Register(
                new AppleMediaCompositionExportProvider(priority));
        IDisposable fastExport =
            MediaCompositionExportRegistry.Default.Register(
                new IsoBmffFastMediaCompositionExportProvider(
                    priority == int.MinValue
                        ? int.MinValue
                        : priority - 1));
        return new AppleMediaRegistrations(
            playback,
            export,
            fastExport);
    }

    private sealed class AppleMediaRegistrations : IDisposable
    {
        private IDisposable? _playback;
        private IDisposable? _export;
        private IDisposable? _fastExport;

        public AppleMediaRegistrations(
            IDisposable playback,
            IDisposable export,
            IDisposable fastExport)
        {
            _playback = playback;
            _export = export;
            _fastExport = fastExport;
        }

        public void Dispose()
        {
            Interlocked.Exchange(
                ref _fastExport,
                null)?.Dispose();
            Interlocked.Exchange(ref _export, null)?.Dispose();
            Interlocked.Exchange(ref _playback, null)?.Dispose();
        }
    }
}

public sealed class AppleMediaPlaybackProviderFactory :
    IMediaPlaybackProviderFactory
{
    public AppleMediaPlaybackProviderFactory(int priority = 100)
    {
        Priority = priority;
    }

    public string Id => "progpu.apple.avfoundation";
    public int Priority { get; }

    public bool CanOpen(MediaSourceDescriptor source) =>
        (OperatingSystem.IsIOS() || OperatingSystem.IsMacOS()) &&
        source.Kind == MediaSourceKind.Uri &&
        source.Uri is not null;

    public ValueTask<IMediaPlaybackProvider> CreateAsync(
        MediaSourceDescriptor source,
        IMediaPlaybackSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanOpen(source))
        {
            throw new NotSupportedException(
                "The AVFoundation provider accepts URI media sources on iOS and macOS.");
        }

        return ValueTask.FromResult<IMediaPlaybackProvider>(
            new AppleMediaPlaybackProvider(source.Uri!, sink));
    }
}

/// <summary>
/// Uses AVPlayer for native hardware decode and audio presentation, and
/// exposes BGRA IOSurface-backed CVPixelBuffers for lazy WebGPU import.
/// Frame extraction is O(1) per callback with no CPU pixel conversion.
/// </summary>
internal sealed class AppleMediaPlaybackProvider :
    IMediaPlaybackProvider,
    IMediaPlaybackConfigurationProvider
{
    private const int MediaTimeScale = 600;
    private static readonly CMTime s_observerInterval =
        CMTime.FromSeconds(1d / 60d, MediaTimeScale);

    private readonly object _gate = new();
    private readonly Uri _uri;
    private readonly IMediaPlaybackSink _sink;
    private AVPlayerItem? _item;
    private AVPlayerItemVideoOutput? _videoOutput;
    private AVPlayer? _player;
    private DispatchQueue? _videoQueue;
    private NSObject? _timeObserver;
    private NSObject? _endedObserver;
    private AppleAudioEffectGraph? _audioEffectGraph;
    private readonly List<IMediaAudioProcessor>
        _audioProcessors = [];
    private IMediaAudioProcessor[] _audioProcessorSnapshot = [];
    private MediaPlaybackConfiguration _configuration =
        MediaPlaybackConfiguration.Default;
    private MediaPlaybackSnapshot _snapshot =
        MediaPlaybackSnapshot.Empty;
    private double _volume = 1d;
    private double _balance;
    private double _rate = 1d;
    private bool _muted;
    private bool _looping;
    private bool _playRequested;
    private bool _seekInProgress;
    private long _seekGeneration;
    private long _sequence;
    private long _droppedFrames;
    private int _audioTapFormatDiagnosticPublished;
    private int _endSignaled;
    private int _opened;
    private int _disposed;

    public AppleMediaPlaybackProvider(
        Uri uri,
        IMediaPlaybackSink sink)
    {
        _uri = uri;
        _sink = sink;
    }

    public string Id => "progpu.apple.avfoundation";

    public async ValueTask OpenAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        NSUrl? nativeUrl = NSUrl.FromString(_uri.AbsoluteUri);
        if (nativeUrl is null)
        {
            throw new NotSupportedException(
                $"AVFoundation cannot represent '{_uri}'.");
        }

        AVPlayerItem? item = null;
        AVPlayerItemVideoOutput? output = null;
        AVPlayer? player = null;
        DispatchQueue? queue = null;
        NSObject? timeObserver = null;
        NSObject? endedObserver = null;
        AppleAudioEffectGraph? audioEffectGraph = null;
        try
        {
            using (nativeUrl)
            {
                item = AVPlayerItem.FromUrl(nativeUrl);
            }

            using var ioSurfaceProperties = new NSDictionary();
            using var pixelFormat =
                NSNumber.FromUInt32(
                    (uint)CVPixelFormatType.CV32BGRA);
            using var metalCompatible =
                NSNumber.FromBoolean(true);
            using var attributeDictionary =
                NSDictionary.FromObjectsAndKeys(
                    new NSObject[]
                    {
                        pixelFormat,
                        metalCompatible,
                        ioSurfaceProperties
                    },
                    new NSObject[]
                    {
                        CVPixelBuffer.PixelFormatTypeKey,
                        CVPixelBuffer.MetalCompatibilityKey,
                        CVPixelBuffer.IOSurfacePropertiesKey
                    });
            var attributes =
                new CVPixelBufferAttributes(attributeDictionary);
            output = new AVPlayerItemVideoOutput(attributes);
            output.SuppressesPlayerRendering = true;
            item.AddOutput(output);

            if (Volatile.Read(
                    ref _audioProcessorSnapshot).Length != 0)
            {
                audioEffectGraph =
                    new AppleAudioEffectGraph(item);
                audioEffectGraph.SetProcessors(
                    Volatile.Read(
                        ref _audioProcessorSnapshot));
            }

            player = new AVPlayer(item);
            player.ActionAtItemEnd = _looping
                ? AVPlayerActionAtItemEnd.None
                : AVPlayerActionAtItemEnd.Pause;
            queue = new DispatchQueue(
                "org.progpu.media.avfoundation.video");

            ApplyConfiguration(player, in _configuration);
            ApplyAudio(player);
            player.AutomaticallyWaitsToMinimizeStalling =
                !_configuration.RealTimePlayback;

            while (item.Status == AVPlayerItemStatus.Unknown)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(10, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (item.Status == AVPlayerItemStatus.Failed)
            {
                throw new InvalidOperationException(
                    item.Error?.LocalizedDescription ??
                    "AVFoundation could not open the media source.");
            }

            bool hasVideo =
                item.Asset.GetTracks(AVMediaTypes.Video).Length != 0;
            bool hasAudio =
                item.Asset.GetTracks(AVMediaTypes.Audio).Length != 0;
            TimeSpan duration = ToTimeSpan(item.Duration);
            uint width = hasVideo
                ? ToDimension(item.PresentationSize.Width)
                : 0;
            uint height = hasVideo
                ? ToDimension(item.PresentationSize.Height)
                : 0;
            var capabilities = new MediaProviderCapabilities(
                CanPause: true,
                CanSeek: item.Duration.IsNumeric,
                SupportsRate: true,
                SupportsFrameStepping:
                    item.CanStepForward || item.CanStepBackward,
                HardwareDecoded: true,
                HasAudio: hasAudio,
                HasVideo: hasVideo);
            _snapshot = new MediaPlaybackSnapshot(
                MediaEnginePlaybackState.Paused,
                TimeSpan.Zero,
                duration,
                width,
                height,
                BufferingProgress: 1d,
                DownloadProgress: 0d,
                PlaybackRate: _rate,
                capabilities);

            AVPlayer capturedPlayer = player;
            AVPlayerItemVideoOutput capturedOutput = output;
            timeObserver = player.AddPeriodicTimeObserver(
                s_observerInterval,
                queue,
                time => OnPeriodicTime(
                    capturedPlayer,
                    capturedOutput,
                    time));
            endedObserver =
                AVPlayerItem.Notifications.ObserveDidPlayToEndTime(
                    item,
                    (_, _) => OnEnded(capturedPlayer));

            lock (_gate)
            {
                ThrowIfDisposed();
                _item = item;
                _videoOutput = output;
                _player = player;
                _videoQueue = queue;
                _timeObserver = timeObserver;
                _endedObserver = endedObserver;
                _audioEffectGraph = audioEffectGraph;
                item = null;
                output = null;
                player = null;
                queue = null;
                timeObserver = null;
                endedObserver = null;
                audioEffectGraph = null;
                Volatile.Write(ref _opened, 1);
            }

            _sink.Opened(in _snapshot);
            PublishDiagnostics(
                "IOSurface import is zero-copy only when the active WebGPU context exposes Dawn shared-texture-memory IOSurface support.");
        }
        catch
        {
            if (timeObserver is not null && player is not null)
            {
                player.RemoveTimeObserver(timeObserver);
            }
            timeObserver?.Dispose();
            endedObserver?.Dispose();
            audioEffectGraph?.Dispose();
            player?.Dispose();
            output?.Dispose();
            item?.Dispose();
            queue?.Dispose();
            throw;
        }
    }

    public void Play()
    {
        ThrowIfDisposed();
        AVPlayer? player;
        bool seekInProgress;
        lock (_gate)
        {
            _playRequested = true;
            player = _player;
            seekInProgress = _seekInProgress;
        }
        if (!seekInProgress)
        {
            player?.PlayImmediatelyAtRate((float)_rate);
        }
    }

    public void Pause()
    {
        ThrowIfDisposed();
        AVPlayer? player;
        lock (_gate)
        {
            _playRequested = false;
            player = _player;
        }
        player?.Pause();
    }

    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        AVPlayer? player;
        long generation;
        lock (_gate)
        {
            player = _player;
            generation = ++_seekGeneration;
            _seekInProgress = player is not null;
        }
        if (player is null)
        {
            return;
        }

        CMTime target = CMTime.FromSeconds(
            Math.Max(0d, position.TotalSeconds),
            MediaTimeScale);
        player.Seek(target, finished =>
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            bool resume;
            double rate;
            lock (_gate)
            {
                if (generation != _seekGeneration)
                {
                    return;
                }
                _seekInProgress = false;
                resume = _playRequested;
                rate = _rate;
            }

            if (finished)
            {
                _sink.SeekCompleted(ToTimeSpan(player.CurrentTime));
            }
            if (resume)
            {
                player.PlayImmediatelyAtRate((float)rate);
            }
        });
    }

    public void SetPlaybackRate(double value)
    {
        ThrowIfDisposed();
        AVPlayer? player;
        bool playRequested;
        lock (_gate)
        {
            _rate = value;
            player = _player;
            playRequested = _playRequested;
        }
        if (playRequested)
        {
            player?.PlayImmediatelyAtRate((float)value);
        }
    }

    public void SetVolume(
        double volume,
        double balance,
        bool muted)
    {
        ThrowIfDisposed();
        AVPlayer? player;
        lock (_gate)
        {
            _volume = volume;
            _balance = balance;
            _muted = muted;
            player = _player;
        }
        if (player is not null)
        {
            ApplyAudio(player);
        }
        if (balance != 0d)
        {
            PublishDiagnostics(
                "AVPlayer has no per-player stereo-pan control; non-zero AudioBalance requires a future AVAudioEngine output adapter.");
        }
    }

    public void SetLooping(bool enabled)
    {
        ThrowIfDisposed();
        AVPlayer? player;
        lock (_gate)
        {
            _looping = enabled;
            player = _player;
        }
        if (player is not null)
        {
            player.ActionAtItemEnd = enabled
                ? AVPlayerActionAtItemEnd.None
                : AVPlayerActionAtItemEnd.Pause;
        }
    }

    public bool StepForwardOneFrame() => StepFrame(1);
    public bool StepBackwardOneFrame() => StepFrame(-1);

    public void AddEffect(IMediaEffect effect, bool optional)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(effect);
        if (effect is IMediaAudioEffect audioEffect)
        {
            lock (_gate)
            {
                _audioProcessors.Add(audioEffect);
                Volatile.Write(
                    ref _audioProcessorSnapshot,
                    _audioProcessors.ToArray());
            }
            try
            {
                UpdateAudioEffectGraph();
            }
            catch
            {
                lock (_gate)
                {
                    _audioProcessors.Remove(audioEffect);
                    Volatile.Write(
                        ref _audioProcessorSnapshot,
                        _audioProcessors.ToArray());
                }
                throw;
            }
            return;
        }
        if (!optional)
        {
            throw new NotSupportedException(
                "AVFoundation playback accepts typed ProGPU audio effects. GPU video effects are applied through MediaVideoPresentationOptions.");
        }
    }

    public void RemoveAllEffects()
    {
        ThrowIfDisposed();
        AppleAudioEffectGraph? graph;
        lock (_gate)
        {
            _audioProcessors.Clear();
            Volatile.Write(
                ref _audioProcessorSnapshot,
                []);
            graph = _audioEffectGraph;
        }
        graph?.SetProcessors([]);
    }

    public void ApplyConfiguration(
        in MediaPlaybackConfiguration configuration)
    {
        ThrowIfDisposed();
        AVPlayer? player;
        lock (_gate)
        {
            _configuration = configuration;
            player = _player;
        }
        if (player is not null)
        {
            ApplyConfiguration(player, in configuration);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AVPlayerItem? item;
        AVPlayerItemVideoOutput? output;
        AVPlayer? player;
        DispatchQueue? queue;
        NSObject? timeObserver;
        NSObject? endedObserver;
        AppleAudioEffectGraph? audioEffectGraph;
        lock (_gate)
        {
            item = _item;
            output = _videoOutput;
            player = _player;
            queue = _videoQueue;
            timeObserver = _timeObserver;
            endedObserver = _endedObserver;
            audioEffectGraph = _audioEffectGraph;
            _item = null;
            _videoOutput = null;
            _player = null;
            _videoQueue = null;
            _timeObserver = null;
            _endedObserver = null;
            _audioEffectGraph = null;
            _audioProcessors.Clear();
            Volatile.Write(
                ref _audioProcessorSnapshot,
                []);
        }

        if (timeObserver is not null && player is not null)
        {
            player.RemoveTimeObserver(timeObserver);
        }
        timeObserver?.Dispose();
        endedObserver?.Dispose();
        player?.Pause();
        audioEffectGraph?.Dispose();
        player?.Dispose();
        output?.Dispose();
        item?.Dispose();
        queue?.Dispose();
    }

    private void OnPeriodicTime(
        AVPlayer player,
        AVPlayerItemVideoOutput output,
        CMTime itemTime)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _opened) == 0)
        {
            return;
        }

        AppleAudioEffectGraph? audioEffectGraph =
            Volatile.Read(ref _audioEffectGraph);
        if (audioEffectGraph?.HasUnsupportedFormat == true)
        {
            if (Interlocked.Exchange(
                    ref _audioTapFormatDiagnosticPublished,
                    1) == 0)
            {
                PublishDiagnostics(
                    "The AVFoundation audio tap delivered a non-float PCM format; typed audio effects are bypassed for this source.");
            }
        }

        MediaPlaybackSnapshot current = _snapshot;
        MediaEnginePlaybackState state =
            player.TimeControlStatus switch
            {
                AVPlayerTimeControlStatus.Playing =>
                    MediaEnginePlaybackState.Playing,
                AVPlayerTimeControlStatus
                    .WaitingToPlayAtSpecifiedRate =>
                    MediaEnginePlaybackState.Buffering,
                _ => MediaEnginePlaybackState.Paused
            };
        _snapshot = current with
        {
            State = state,
            Position = ToTimeSpan(itemTime),
            BufferingProgress =
                state == MediaEnginePlaybackState.Buffering
                    ? 0d
                    : 1d,
            PlaybackRate = _rate
        };

        if (_snapshot.Capabilities.HasVideo &&
            output.HasNewPixelBufferForItemTime(itemTime))
        {
            CMTime displayTime = default;
            CVPixelBuffer? pixelBuffer =
                output.CopyPixelBuffer(
                    itemTime,
                    ref displayTime);
            if (pixelBuffer is not null)
            {
                PresentPixelBuffer(pixelBuffer, displayTime);
            }
        }

        _sink.Update(in _snapshot);

        bool reachedEnd =
            _snapshot.NaturalDuration > TimeSpan.Zero &&
            _snapshot.Position >=
                _snapshot.NaturalDuration -
                TimeSpan.FromMilliseconds(2d);
        if (reachedEnd)
        {
            SignalEndedOnce();
        }
        else
        {
            Volatile.Write(ref _endSignaled, 0);
        }
    }

    private void PresentPixelBuffer(
        CVPixelBuffer pixelBuffer,
        CMTime displayTime)
    {
        IOSurface.IOSurface? surface = null;
        try
        {
            surface = pixelBuffer.GetIOSurface();
            if (surface is null ||
                (nint)surface.Handle == 0)
            {
                Interlocked.Increment(ref _droppedFrames);
                PublishDiagnostics(
                    "AVFoundation returned a video buffer without IOSurface backing.");
                return;
            }

            uint width = checked((uint)pixelBuffer.Width);
            uint height = checked((uint)pixelBuffer.Height);
            var descriptor = new MediaGpuFrameDescriptor(
                Interlocked.Increment(ref _sequence),
                ToTimeSpan(displayTime),
                TimeSpan.Zero,
                width,
                height,
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
                    (nint)surface.Handle,
                    width,
                    height,
                    TextureFormat.Bgra8Unorm,
                    TextureUsage.TextureBinding,
                    GpuTextureAlphaMode.Straight,
                    IsInitialized: true);
            _sink.Present(new ExternalMediaGpuFrame(
                in descriptor,
                in externalDescriptor,
                pixelBuffer));
            pixelBuffer = null!;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _droppedFrames);
            PublishDiagnostics(
                $"AVFoundation frame extraction failed: {exception.Message}");
        }
        finally
        {
            surface?.Dispose();
            pixelBuffer?.Dispose();
        }
    }

    private void OnEnded(AVPlayer player)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        MediaPlaybackSnapshot snapshot = _snapshot;
        if (snapshot.NaturalDuration > TimeSpan.Zero &&
            ToTimeSpan(player.CurrentTime) <
                snapshot.NaturalDuration -
                TimeSpan.FromMilliseconds(100d))
        {
            return;
        }
        SignalEndedOnce();
    }

    private void SignalEndedOnce()
    {
        if (Interlocked.Exchange(ref _endSignaled, 1) == 0 &&
            Volatile.Read(ref _disposed) == 0)
        {
            _sink.Ended();
        }
    }

    private bool StepFrame(nint count)
    {
        ThrowIfDisposed();
        AVPlayerItem? item;
        lock (_gate)
        {
            item = _item;
        }
        if (item is null ||
            (count > 0 && !item.CanStepForward) ||
            (count < 0 && !item.CanStepBackward))
        {
            return false;
        }
        item.StepByCount(count);
        return true;
    }

    private void ApplyAudio(AVPlayer player)
    {
        player.Volume = (float)_volume;
        player.Muted = _muted;
    }

    private void UpdateAudioEffectGraph()
    {
        AppleAudioEffectGraph? graph;
        AVPlayerItem? item;
        IMediaAudioProcessor[] processors;
        lock (_gate)
        {
            graph = _audioEffectGraph;
            item = _item;
            processors =
                Volatile.Read(
                    ref _audioProcessorSnapshot);
        }

        if (graph is not null)
        {
            graph.SetProcessors(processors);
            return;
        }
        if (item is null || processors.Length == 0)
        {
            return;
        }

        var created = new AppleAudioEffectGraph(item);
        created.SetProcessors(processors);
        lock (_gate)
        {
            if (_audioEffectGraph is null &&
                ReferenceEquals(_item, item) &&
                Volatile.Read(ref _disposed) == 0)
            {
                _audioEffectGraph = created;
                return;
            }
            graph = _audioEffectGraph;
        }

        created.Dispose();
        graph?.SetProcessors(processors);
    }

    private static void ApplyConfiguration(
        AVPlayer player,
        in MediaPlaybackConfiguration configuration)
    {
        player.AutomaticallyWaitsToMinimizeStalling =
            !configuration.RealTimePlayback;

#if IOS
        AVAudioSessionCategory category =
            configuration.AudioCategory switch
            {
                MediaAudioCategory.Communications or
                MediaAudioCategory.GameChat or
                MediaAudioCategory.Speech =>
                    AVAudioSessionCategory.PlayAndRecord,
                MediaAudioCategory.Alerts or
                MediaAudioCategory.SoundEffects or
                MediaAudioCategory.GameEffects =>
                    AVAudioSessionCategory.Ambient,
                _ => AVAudioSessionCategory.Playback
            };
        AVAudioSession session = AVAudioSession.SharedInstance();
        session.SetCategory(category);
        session.SetActive(true);
#endif
    }

    private void PublishDiagnostics(string? fallbackReason)
    {
        _sink.UpdateDiagnostics(
            new MediaProviderDiagnostics(
                HardwareDecoded: true,
                TransferMode: MediaTransferMode.NativeZeroCopy,
                DroppedFrames:
                    Interlocked.Read(ref _droppedFrames),
                VideoQueueDepth: 1,
                AudioQueueDepth: 0,
                AudioLatency: TimeSpan.Zero,
                LastFallbackReason: fallbackReason));
    }

    private static TimeSpan ToTimeSpan(CMTime time) =>
        time.IsNumeric &&
        double.IsFinite(time.Seconds) &&
        time.Seconds > 0d
            ? TimeSpan.FromSeconds(time.Seconds)
            : TimeSpan.Zero;

    private static uint ToDimension(nfloat value) =>
        value > 0d && double.IsFinite(value)
            ? checked((uint)Math.Ceiling(value))
            : 0;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}
