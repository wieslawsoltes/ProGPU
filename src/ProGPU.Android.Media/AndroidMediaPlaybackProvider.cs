using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Media;
using Android.Media.Audiofx;
using Android.OS;
using Android.Runtime;
using ProGPU.Backend;
using ProGPU.Media.Audio;
using ProGPU.Media.Diagnostics;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;
using Silk.NET.WebGPU;

namespace ProGPU.Android.Media;

/// <summary>
/// Registers the Android platform media provider. Registration is explicit so
/// applications can replace it with a higher-priority provider.
/// </summary>
public static class AndroidMedia
{
    public static IDisposable Register(
        MediaProviderRegistry? registry = null,
        int priority = 100)
    {
        IDisposable playback =
            (registry ?? MediaProviderRegistry.Default).Register(
                new AndroidMediaPlaybackProviderFactory(priority));
        IDisposable preciseExport =
            MediaCompositionExportRegistry.Default.Register(
                new AndroidMediaCodecCompositionExportProvider(
                    priority));
        IDisposable thumbnails =
            MediaCompositionThumbnailRegistry.Default.Register(
                new AndroidMediaCompositionThumbnailProvider(
                    priority));
        IDisposable fastExport =
            MediaCompositionExportRegistry.Default.Register(
                new IsoBmffFastMediaCompositionExportProvider(
                    LowerPriority(priority)));
        return new AndroidMediaRegistrations(
            playback,
            preciseExport,
            thumbnails,
            fastExport);
    }

    private static int LowerPriority(int priority) =>
        priority == int.MinValue
            ? int.MinValue
            : priority - 1;

    private sealed class AndroidMediaRegistrations :
        IDisposable
    {
        private IDisposable? _playback;
        private IDisposable? _preciseExport;
        private IDisposable? _thumbnails;
        private IDisposable? _fastExport;

        public AndroidMediaRegistrations(
            IDisposable playback,
            IDisposable preciseExport,
            IDisposable thumbnails,
            IDisposable fastExport)
        {
            _playback = playback;
            _preciseExport = preciseExport;
            _thumbnails = thumbnails;
            _fastExport = fastExport;
        }

        public void Dispose()
        {
            Interlocked.Exchange(
                ref _fastExport,
                null)?.Dispose();
            Interlocked.Exchange(
                ref _thumbnails,
                null)?.Dispose();
            Interlocked.Exchange(
                ref _preciseExport,
                null)?.Dispose();
            Interlocked.Exchange(
                ref _playback,
                null)?.Dispose();
        }
    }
}

public sealed class AndroidMediaPlaybackProviderFactory :
    IMediaPlaybackProviderFactory
{
    public AndroidMediaPlaybackProviderFactory(int priority = 100)
    {
        Priority = priority;
    }

    public string Id => "progpu.android.media";
    public int Priority { get; }

    public bool CanOpen(MediaSourceDescriptor source) =>
        OperatingSystem.IsAndroid() &&
        source.Kind == MediaSourceKind.Uri &&
        source.Uri is { IsAbsoluteUri: true };

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
                "The Android platform provider accepts absolute URI media sources.");
        }

        return ValueTask.FromResult<IMediaPlaybackProvider>(
            new AndroidMediaPlaybackProvider(source.Uri!, sink));
    }
}

/// <summary>
/// Uses Android's native MediaPlayer/MediaCodec stack for demux, decode, and
/// audio. Video is rendered into GPU-sampleable ImageReader buffers and each
/// AHardwareBuffer is retained until the WebGPU texture lease ends.
/// </summary>
internal sealed class AndroidMediaPlaybackProvider :
    IMediaPlaybackProvider,
    IMediaPlaybackConfigurationProvider,
    IMediaPlaybackTrackProvider,
    IMediaPlaybackTimedMetadataProvider
{
    private const int ImageRingSize = 3;
    private const string ImportDiagnostic =
        "AHardwareBuffer import is zero-copy only when the active WebGPU Vulkan device exposes Dawn shared-texture-memory AHardwareBuffer support.";
    private static readonly TimeSpan s_shutdownTimeout =
        TimeSpan.FromSeconds(5);

    private readonly Uri _uri;
    private readonly IMediaPlaybackSink _sink;
    private readonly HandlerThread _thread;
    private readonly Handler _handler;
    private readonly object _audioEffectGate = new();
    private readonly object _timedMetadataGate = new();
    private readonly List<AudioGraphEffectBinding>
        _audioEffects = [];
    private readonly TaskCompletionSource _opened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SnapshotRunnable _snapshotRunnable;
    private readonly ImageListener _imageListener;
    private MediaPlaybackConfiguration _configuration =
        MediaPlaybackConfiguration.Default;
    private global::Android.Media.MediaPlayer? _player;
    private LoudnessEnhancer? _loudnessEnhancer;
    private ImageReader? _imageReader;
    private MediaPlaybackSnapshot _snapshot =
        MediaPlaybackSnapshot.Empty;
    private MediaPlaybackTracksSnapshot _tracks =
        MediaPlaybackTracksSnapshot.Empty;
    private int[] _audioTrackNativeIndices = [];
    private int[] _videoTrackNativeIndices = [];
    private int[] _timedMetadataTrackNativeIndices = [];
    private MediaPlaybackTimedTextCueAccumulator?[]
        _timedTextCueAccumulators = [];
    private int _selectedTimedMetadataTrack = -1;
    private double _volume = 1d;
    private double _balance;
    private double _rate = 1d;
    private bool _muted;
    private bool _looping;
    private bool _playRequested;
    private long _sequence;
    private long _droppedFrames;
    private int _handlerThreadId;
    private int _openedFlag;
    private int _disposed;

    internal AndroidMediaPlaybackProvider(
        Uri uri,
        IMediaPlaybackSink sink)
    {
        _uri = uri;
        _sink = sink;
        _thread = new HandlerThread(
            "ProGPU Android Media",
            (int)global::Android.OS.ThreadPriority.Display);
        _thread.Start();
        _handler = new Handler(
            _thread.Looper ??
            throw new InvalidOperationException(
                "Android could not create the media looper."));
        _snapshotRunnable = new SnapshotRunnable(this);
        _imageListener = new ImageListener(this);
    }

    public string Id => "progpu.android.media";

    public async ValueTask OpenAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsAndroid())
        {
            throw new PlatformNotSupportedException(
                "The Android media provider is available only on Android.");
        }

        using CancellationTokenRegistration registration =
            cancellationToken.Register(
                static state =>
                    ((AndroidMediaPlaybackProvider)state!).CancelOpen(),
                this);
        Post(Initialize);
        await _opened.Task.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void Play()
    {
        ThrowIfDisposed();
        _playRequested = true;
        Post(() =>
        {
            global::Android.Media.MediaPlayer? player = _player;
            if (player is null)
            {
                return;
            }
            ApplyPlaybackRate(player);
            player.Start();
        });
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _playRequested = false;
        Post(() => _player?.Pause());
    }

    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        long milliseconds = checked(
            (long)Math.Clamp(
                position.TotalMilliseconds,
                0d,
                int.MaxValue));
        Post(() =>
            _player?.SeekTo(
                milliseconds,
                MediaPlayerSeekMode.Closest));
    }

    public void SetPlaybackRate(double value)
    {
        ThrowIfDisposed();
        _rate = value;
        if (_playRequested)
        {
            Post(() =>
            {
                if (_player is { } player)
                {
                    ApplyPlaybackRate(player);
                }
            });
        }
    }

    public void SetVolume(
        double volume,
        double balance,
        bool muted)
    {
        ThrowIfDisposed();
        _volume = volume;
        _balance = balance;
        _muted = muted;
        Post(() =>
        {
            if (_player is { } player)
            {
                ApplyAudioGraph(player);
            }
        });
    }

    public void SetLooping(bool enabled)
    {
        ThrowIfDisposed();
        _looping = enabled;
        Post(() =>
        {
            if (_player is { } player)
            {
                player.Looping = enabled;
            }
        });
    }

    public bool StepForwardOneFrame() => false;
    public bool StepBackwardOneFrame() => false;

    public bool TrySelectTrack(
        MediaPlaybackTrackKind kind,
        int index)
    {
        if (kind is not (
                MediaPlaybackTrackKind.Audio or
                MediaPlaybackTrackKind.Video))
        {
            return false;
        }

        int[] nativeIndices = kind ==
            MediaPlaybackTrackKind.Audio
                ? Volatile.Read(
                    ref _audioTrackNativeIndices)
                : Volatile.Read(
                    ref _videoTrackNativeIndices);
        if (index < -1 || index >= nativeIndices.Length)
        {
            return false;
        }
        if (kind == MediaPlaybackTrackKind.Video)
        {
            // Android MediaPlayer exposes video tracks but its documented
            // SelectTrack contract supports audio and text selection only.
            return false;
        }

        Post(() =>
        {
            if (_player is not { } player)
            {
                return;
            }
            try
            {
                if (index < 0)
                {
                    int selected = player.GetSelectedTrack(
                        MediaTrackType.Audio);
                    if (selected >= 0)
                    {
                        player.DeselectTrack(selected);
                    }
                }
                else
                {
                    player.SelectTrack(nativeIndices[index]);
                }
                PublishTracks(player);
            }
            catch (Exception exception)
            {
                _sink.Failed(
                    MediaPlaybackFailure.Decode,
                    $"Android could not select audio track {index}: {exception.Message}",
                    exception);
            }
        });
        return true;
    }

    public bool TrySetTimedMetadataPresentationMode(
        int index,
        MediaPlaybackTimedMetadataPresentationMode mode)
    {
        if (!Enum.IsDefined(mode) ||
            mode ==
                MediaPlaybackTimedMetadataPresentationMode
                    .PlatformPresented)
        {
            // MediaPlayer.TimedText is an application-rendered callback.
            // Android exposes encoded SubtitleData separately and does not
            // provide a native subtitle view anchored to this GPU surface.
            return false;
        }

        int nativeIndex;
        MediaPlaybackTimedTextCueAccumulator accumulator;
        bool disable;
        lock (_timedMetadataGate)
        {
            ThrowIfDisposed();
            if (_player is null ||
                (uint)index >=
                    (uint)_timedMetadataTrackNativeIndices
                        .Length ||
                _timedTextCueAccumulators[index] is not
                    { } parsedAccumulator)
            {
                return false;
            }

            disable =
                mode ==
                MediaPlaybackTimedMetadataPresentationMode
                    .Disabled;
            if (!disable &&
                _selectedTimedMetadataTrack >= 0 &&
                _selectedTimedMetadataTrack != index)
            {
                // MediaPlayer permits only the most recently selected text
                // track of a type. Preserve WinUI's independent mode model
                // by requiring the active track to be disabled first.
                return false;
            }

            nativeIndex =
                _timedMetadataTrackNativeIndices[index];
            accumulator = parsedAccumulator;
            if (disable)
            {
                if (_selectedTimedMetadataTrack != index)
                {
                    return true;
                }
                _selectedTimedMetadataTrack = -1;
            }
            else
            {
                _selectedTimedMetadataTrack = index;
            }
        }

        Post(() =>
        {
            if (_player is not { } player)
            {
                return;
            }
            try
            {
                if (disable)
                {
                    player.DeselectTrack(nativeIndex);
                    TimeSpan position = TimeSpan.FromMilliseconds(
                        Math.Max(0, player.CurrentPosition));
                    _sink.UpdateTimedMetadataCues(
                        accumulator.Flush(position));
                }
                else
                {
                    player.SelectTrack(nativeIndex);
                }
            }
            catch (Exception exception)
            {
                lock (_timedMetadataGate)
                {
                    if (_selectedTimedMetadataTrack == index)
                    {
                        _selectedTimedMetadataTrack = -1;
                    }
                }
                _sink.Failed(
                    MediaPlaybackFailure.Decode,
                    $"Android could not change timed-text track {index} to {mode}: {exception.Message}",
                    exception);
            }
        });
        return true;
    }

    public void AddEffect(IMediaEffect effect, bool optional)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (effect is not IMediaAudioGraphEffect
            graphEffect ||
            graphEffect.CaptureState().Kind is not (
                MediaAudioGraphEffectKind.Gain or
                MediaAudioGraphEffectKind
                    .StereoBalance))
        {
            if (!optional)
            {
                throw new NotSupportedException(
                    "Android MediaPlayer accepts typed gain and stereo-balance IMediaAudioGraphEffect nodes. Arbitrary managed PCM effects require the direct MediaCodec/AAudio lane.");
            }
            return;
        }

        lock (_audioEffectGate)
        {
            ThrowIfDisposed();
            _audioEffects.Add(
                new AudioGraphEffectBinding(
                    graphEffect,
                    OnAudioEffectStateChanged));
        }
        Post(ApplyAudioGraph);
    }

    public void RemoveAllEffects()
    {
        AudioGraphEffectBinding[] bindings;
        lock (_audioEffectGate)
        {
            bindings = [.. _audioEffects];
            _audioEffects.Clear();
        }
        for (int index = 0;
             index < bindings.Length;
             index++)
        {
            bindings[index].Dispose();
        }
        Post(ApplyAudioGraph);
    }

    public void ApplyConfiguration(
        in MediaPlaybackConfiguration configuration)
    {
        ThrowIfDisposed();
        _configuration = configuration;
        if (Volatile.Read(ref _openedFlag) != 0)
        {
            PublishDiagnostics(
                "Android audio usage is selected when MediaPlayer is prepared; category changes take effect on the next source.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _opened.TrySetCanceled();
        if (System.Environment.CurrentManagedThreadId == _handlerThreadId)
        {
            Cleanup();
            _thread.QuitSafely();
            return;
        }

        using var completed = new ManualResetEventSlim();
        if (_handler.Post(() =>
            {
                try
                {
                    Cleanup();
                }
                finally
                {
                    completed.Set();
                }
            }))
        {
            _ = completed.Wait(s_shutdownTimeout);
        }
        _thread.QuitSafely();
        _thread.Join(checked((long)s_shutdownTimeout.TotalMilliseconds));
        _handler.Dispose();
        _thread.Dispose();
        _snapshotRunnable.Dispose();
        _imageListener.Dispose();
    }

    private void Initialize()
    {
        _handlerThreadId =
            System.Environment.CurrentManagedThreadId;
        try
        {
            using var metadata = new MediaMetadataRetriever();
            metadata.SetDataSource(_uri.AbsoluteUri);
            uint width = ParseDimension(
                metadata.ExtractMetadata(
                    MetadataKey.VideoWidth));
            uint height = ParseDimension(
                metadata.ExtractMetadata(
                    MetadataKey.VideoHeight));
            TimeSpan duration = TimeSpan.FromMilliseconds(
                ParsePositiveLong(
                    metadata.ExtractMetadata(
                        MetadataKey.Duration)));
            bool hasVideo =
                string.Equals(
                    metadata.ExtractMetadata(
                        MetadataKey.HasVideo),
                    "yes",
                    StringComparison.OrdinalIgnoreCase) ||
                (width != 0 && height != 0);
            bool hasAudio =
                string.Equals(
                    metadata.ExtractMetadata(
                        MetadataKey.HasAudio),
                    "yes",
                    StringComparison.OrdinalIgnoreCase);

            if (hasVideo)
            {
                if (width == 0 || height == 0)
                {
                    throw new NotSupportedException(
                        "Android did not expose the native video dimensions.");
                }
                _imageReader = ImageReader.NewInstance(
                    checked((int)width),
                    checked((int)height),
                    (ImageFormatType)Format.Rgba8888,
                    ImageRingSize,
                    global::Android.Hardware.HardwareBuffer
                        .UsageGpuSampledImage);
                _imageReader.SetOnImageAvailableListener(
                    _imageListener,
                    _handler);
            }

            var player = new global::Android.Media.MediaPlayer();
            _player = player;
            player.Prepared += OnPrepared;
            player.Completion += OnCompletion;
            player.SeekComplete += OnSeekCompleted;
            player.BufferingUpdate += OnBufferingUpdate;
            player.Error += OnError;
            player.TimedText += OnTimedText;
            using AudioAttributes attributes =
                CreateAudioAttributes(_configuration.AudioCategory);
            player.SetAudioAttributes(attributes);
            if (_imageReader is not null)
            {
                player.SetSurface(_imageReader.Surface);
            }
            player.SetDataSource(_uri.AbsoluteUri);

            _snapshot = new MediaPlaybackSnapshot(
                MediaEnginePlaybackState.Opening,
                TimeSpan.Zero,
                duration,
                width,
                height,
                BufferingProgress: 0d,
                DownloadProgress: 0d,
                PlaybackRate: _rate,
                new MediaProviderCapabilities(
                    CanPause: true,
                    CanSeek: duration > TimeSpan.Zero,
                    SupportsRate: true,
                    SupportsFrameStepping: false,
                    HardwareDecoded: true,
                    HasAudio: hasAudio,
                    HasVideo: hasVideo));
            player.PrepareAsync();
        }
        catch (Exception exception)
        {
            _opened.TrySetException(exception);
            Cleanup();
        }
    }

    private void OnPrepared(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Volatile.Read(ref _disposed) != 0 ||
            _player is not { } player)
        {
            return;
        }

        ApplyAudioGraph(player);
        player.Looping = _looping;
        _snapshot = _snapshot with
        {
            State = MediaEnginePlaybackState.Paused,
            NaturalDuration = TimeSpan.FromMilliseconds(
                Math.Max(0, player.Duration)),
            BufferingProgress = 1d
        };
        Volatile.Write(ref _openedFlag, 1);
        PublishTracks(player);
        _sink.Opened(in _snapshot);
        PublishDiagnostics(ImportDiagnostic);
        _opened.TrySetResult();
        _handler.Post(_snapshotRunnable);
    }

    private void OnCompletion(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        MediaPlaybackTimedMetadataCueSnapshot?
            timedTextSnapshot =
                FlushSelectedTimedText(
                    _snapshot.NaturalDuration);
        if (timedTextSnapshot is not null)
        {
            _sink.UpdateTimedMetadataCues(
                timedTextSnapshot);
        }
        if (!_looping)
        {
            _sink.Ended();
        }
    }

    private void OnSeekCompleted(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Volatile.Read(ref _disposed) == 0 &&
            _player is { } player)
        {
            TimeSpan position = TimeSpan.FromMilliseconds(
                Math.Max(0, player.CurrentPosition));
            MediaPlaybackTimedMetadataCueSnapshot?
                timedTextSnapshot =
                    FlushSelectedTimedText(position);
            if (timedTextSnapshot is not null)
            {
                _sink.UpdateTimedMetadataCues(
                    timedTextSnapshot);
            }
            _sink.SeekCompleted(position);
        }
    }

    private void OnTimedText(
        object? sender,
        global::Android.Media.MediaPlayer.TimedTextEventArgs
            args)
    {
        _ = sender;
        if (Volatile.Read(ref _disposed) != 0 ||
            _player is not { } player)
        {
            return;
        }

        MediaPlaybackTimedMetadataCueSnapshot? snapshot;
        lock (_timedMetadataGate)
        {
            int selected = _selectedTimedMetadataTrack;
            if ((uint)selected >=
                    (uint)_timedTextCueAccumulators.Length ||
                _timedTextCueAccumulators[selected] is not
                    { } accumulator)
            {
                return;
            }

            TimeSpan position = TimeSpan.FromMilliseconds(
                Math.Max(0, player.CurrentPosition));
            string? text = args.Text?.Text;
            snapshot = string.IsNullOrEmpty(text)
                ? accumulator.Flush(position)
                : accumulator.Update(
                    position,
                    [text],
                    _snapshot.NaturalDuration);
        }
        _sink.UpdateTimedMetadataCues(snapshot);
    }

    private void OnBufferingUpdate(
        object? sender,
        global::Android.Media.MediaPlayer.BufferingUpdateEventArgs args)
    {
        _ = sender;
        _snapshot = _snapshot with
        {
            DownloadProgress =
                Math.Clamp(args.Percent / 100d, 0d, 1d)
        };
    }

    private void OnError(
        object? sender,
        global::Android.Media.MediaPlayer.ErrorEventArgs args)
    {
        _ = sender;
        args.Handled = true;
        var exception = new InvalidOperationException(
            $"Android MediaPlayer error {args.What}/{args.Extra}.");
        if (!_opened.Task.IsCompleted)
        {
            _opened.TrySetException(exception);
        }
        else if (Volatile.Read(ref _disposed) == 0)
        {
            _sink.Failed(
                MediaPlaybackFailure.Decode,
                exception.Message,
                exception);
        }
    }

    private void OnImageAvailable(ImageReader reader)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Image? image = null;
        global::Android.Hardware.HardwareBuffer? hardwareBuffer = null;
        AndroidHardwareBufferOwner? owner = null;
        try
        {
            image = reader.AcquireLatestImage();
            if (image is null)
            {
                return;
            }
            hardwareBuffer = image.HardwareBuffer;
            if (hardwareBuffer is null ||
                hardwareBuffer.Handle == IntPtr.Zero)
            {
                Interlocked.Increment(ref _droppedFrames);
                return;
            }

            nint nativeBuffer =
                AndroidHardwareBufferNative.FromJavaHardwareBuffer(
                    JNIEnv.Handle,
                    hardwareBuffer.Handle);
            if (nativeBuffer == 0)
            {
                Interlocked.Increment(ref _droppedFrames);
                return;
            }
            uint width = checked((uint)image.Width);
            uint height = checked((uint)image.Height);
            long timestampNanoseconds = image.Timestamp;
            AndroidHardwareBufferNative.Acquire(nativeBuffer);
            owner = new AndroidHardwareBufferOwner(
                nativeBuffer,
                image);
            nativeBuffer = 0;
            image = null;

            var descriptor = new MediaGpuFrameDescriptor(
                Interlocked.Increment(ref _sequence),
                timestampNanoseconds > 0
                    ? TimeSpan.FromTicks(
                        timestampNanoseconds /
                        TimeSpan.NanosecondsPerTick)
                    : TimeSpan.Zero,
                TimeSpan.Zero,
                width,
                height,
                MediaVideoPixelFormat.Rgba8,
                MediaTransferMode.NativeZeroCopy,
                new MediaColorInfo(
                    MediaColorPrimaries.Bt709,
                    MediaTransferFunction.Srgb,
                    MediaMatrixCoefficients.Identity,
                    FullRange: true));
            var externalDescriptor =
                new ProGpuExternalTextureDescriptor(
                    ProGpuExternalTextureHandleKind
                        .AndroidHardwareBuffer,
                    owner.Handle,
                    width,
                    height,
                    TextureFormat.Rgba8Unorm,
                    TextureUsage.TextureBinding,
                    GpuTextureAlphaMode.Straight,
                    IsInitialized: true);
            _sink.Present(new ExternalMediaGpuFrame(
                in descriptor,
                in externalDescriptor,
                owner));
            owner = null;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _droppedFrames);
            PublishDiagnostics(exception.Message);
        }
        finally
        {
            owner?.Dispose();
            hardwareBuffer?.Dispose();
            image?.Close();
            image?.Dispose();
        }
    }

    private void PublishTracks(
        global::Android.Media.MediaPlayer player)
    {
        global::Android.Media.MediaPlayer.TrackInfo[] trackInfo =
            player.GetTrackInfo() ?? [];
        var audio =
            new List<MediaPlaybackTrackDescriptor>();
        var video =
            new List<MediaPlaybackTrackDescriptor>();
        var timedMetadata =
            new List<MediaPlaybackTrackDescriptor>();
        var audioNative = new List<int>();
        var videoNative = new List<int>();
        var timedMetadataNative = new List<int>();
        var timedTextParsed = new List<bool>();
        try
        {
            for (int nativeIndex = 0;
                 nativeIndex < trackInfo.Length;
                 nativeIndex++)
            {
                global::Android.Media.MediaPlayer.TrackInfo info =
                    trackInfo[nativeIndex];
                MediaTrackType trackType = info.TrackType;
                MediaPlaybackTrackKind kind;
                List<MediaPlaybackTrackDescriptor> destination;
                List<int> nativeDestination;
                if (trackType == MediaTrackType.Audio)
                {
                    kind = MediaPlaybackTrackKind.Audio;
                    destination = audio;
                    nativeDestination = audioNative;
                }
                else if (trackType == MediaTrackType.Video)
                {
                    kind = MediaPlaybackTrackKind.Video;
                    destination = video;
                    nativeDestination = videoNative;
                }
                else if (trackType is
                    MediaTrackType.Timedtext or
                    MediaTrackType.Subtitle)
                {
                    kind =
                        MediaPlaybackTrackKind
                            .TimedMetadata;
                    destination = timedMetadata;
                    nativeDestination =
                        timedMetadataNative;
                }
                else
                {
                    continue;
                }

                MediaFormat? format = info.Format;
                string language = info.Language ?? string.Empty;
                string mime = ReadFormatString(
                    format,
                    MediaFormat.KeyMime);
                uint frameRate =
                    ReadFormatUInt(format, MediaFormat.KeyFrameRate);
                destination.Add(
                    new MediaPlaybackTrackDescriptor(
                        $"android:{nativeIndex}",
                        kind,
                        kind == MediaPlaybackTrackKind.Audio
                            ? $"Audio {destination.Count + 1}"
                            : kind ==
                                MediaPlaybackTrackKind.Video
                                ? $"Video {destination.Count + 1}"
                                : $"Timed text {destination.Count + 1}",
                        language,
                        language,
                        new MediaPlaybackTrackEncoding(
                            Subtype: mime,
                            Bitrate:
                                ReadFormatUInt(
                                    format,
                                    MediaFormat.KeyBitRate),
                            Width:
                                ReadFormatUInt(
                                    format,
                                    MediaFormat.KeyWidth),
                            Height:
                                ReadFormatUInt(
                                    format,
                                    MediaFormat.KeyHeight),
                            FrameRateNumerator: frameRate,
                            FrameRateDenominator:
                                frameRate == 0 ? 0u : 1u,
                            SampleRate:
                                ReadFormatUInt(
                                    format,
                                    MediaFormat.KeySampleRate),
                            ChannelCount:
                                ReadFormatUInt(
                                    format,
                                    MediaFormat.KeyChannelCount)),
                        trackType ==
                            MediaTrackType.Subtitle
                            ? MediaPlaybackTrackSupport
                                .Unsupported
                            : MediaPlaybackTrackSupport
                                .Supported,
                        kind ==
                            MediaPlaybackTrackKind
                                .TimedMetadata
                            ? MediaPlaybackTimedMetadataKind
                                .Subtitle
                            : MediaPlaybackTimedMetadataKind
                                .Custom,
                        kind ==
                            MediaPlaybackTrackKind
                                .TimedMetadata
                            ? mime
                            : string.Empty));
                nativeDestination.Add(nativeIndex);
                if (kind ==
                    MediaPlaybackTrackKind.TimedMetadata)
                {
                    timedTextParsed.Add(
                        trackType ==
                            MediaTrackType.Timedtext);
                }
            }
        }
        finally
        {
            for (int index = 0;
                 index < trackInfo.Length;
                 index++)
            {
                trackInfo[index]?.Dispose();
            }
        }

        int selectedAudioNative = player.GetSelectedTrack(
            MediaTrackType.Audio);
        int selectedVideoNative = player.GetSelectedTrack(
            MediaTrackType.Video);
        int selectedAudio =
            audioNative.IndexOf(selectedAudioNative);
        int selectedVideo =
            videoNative.IndexOf(selectedVideoNative);
        int[] audioIndices = audioNative.ToArray();
        int[] videoIndices = videoNative.ToArray();
        int[] timedMetadataIndices =
            timedMetadataNative.ToArray();
        lock (_timedMetadataGate)
        {
            int[] previousIndices =
                _timedMetadataTrackNativeIndices;
            MediaPlaybackTimedTextCueAccumulator?[]
                previousAccumulators =
                    _timedTextCueAccumulators;
            var accumulators =
                new MediaPlaybackTimedTextCueAccumulator?[
                    timedMetadataIndices.Length];
            for (int index = 0;
                 index < accumulators.Length;
                 index++)
            {
                if (!timedTextParsed[index])
                {
                    continue;
                }

                int previousIndex = Array.IndexOf(
                    previousIndices,
                    timedMetadataIndices[index]);
                accumulators[index] =
                    previousIndex >= 0 &&
                    previousIndex <
                        previousAccumulators.Length
                        ? previousAccumulators[
                            previousIndex]
                        : null;
                accumulators[index] ??=
                    new MediaPlaybackTimedTextCueAccumulator(
                        timedMetadata[index]
                            .ProviderTrackId);
            }

            if (_selectedTimedMetadataTrack >= 0)
            {
                int selectedNative =
                    _selectedTimedMetadataTrack <
                        previousIndices.Length
                        ? previousIndices[
                            _selectedTimedMetadataTrack]
                        : -1;
                _selectedTimedMetadataTrack =
                    Array.IndexOf(
                        timedMetadataIndices,
                        selectedNative);
            }
            _timedMetadataTrackNativeIndices =
                timedMetadataIndices;
            _timedTextCueAccumulators = accumulators;
        }
        Volatile.Write(
            ref _audioTrackNativeIndices,
            audioIndices);
        Volatile.Write(
            ref _videoTrackNativeIndices,
            videoIndices);
        _tracks = new MediaPlaybackTracksSnapshot(
            audio,
            selectedAudio,
            video,
            selectedVideo,
            timedMetadata);
        _sink.UpdateTracks(_tracks);
    }

    private MediaPlaybackTimedMetadataCueSnapshot?
        FlushSelectedTimedText(TimeSpan position)
    {
        lock (_timedMetadataGate)
        {
            int selected = _selectedTimedMetadataTrack;
            return (uint)selected <
                    (uint)_timedTextCueAccumulators.Length &&
                _timedTextCueAccumulators[selected] is
                    { } accumulator
                    ? accumulator.Flush(position)
                    : null;
        }
    }

    private static string ReadFormatString(
        MediaFormat? format,
        string key)
    {
        if (format is null || !format.ContainsKey(key))
        {
            return string.Empty;
        }
        return format.GetString(key) ?? string.Empty;
    }

    private static uint ReadFormatUInt(
        MediaFormat? format,
        string key)
    {
        if (format is null || !format.ContainsKey(key))
        {
            return 0;
        }
        int value = format.GetInteger(key);
        return value <= 0 ? 0u : checked((uint)value);
    }

    private void PublishSnapshot()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _openedFlag) == 0 ||
            _player is not { } player)
        {
            return;
        }

        _snapshot = _snapshot with
        {
            State = player.IsPlaying
                ? MediaEnginePlaybackState.Playing
                : MediaEnginePlaybackState.Paused,
            Position = TimeSpan.FromMilliseconds(
                Math.Max(0, player.CurrentPosition)),
            PlaybackRate = _rate
        };
        _sink.Update(in _snapshot);
        _handler.PostDelayed(_snapshotRunnable, 16);
    }

    private void ApplyPlaybackRate(
        global::Android.Media.MediaPlayer player)
    {
        using var parameters = new PlaybackParams();
        _ = parameters.SetSpeed((float)_rate);
        player.PlaybackParams = parameters;
    }

    private void ApplyVolume(
        global::Android.Media.MediaPlayer player,
        in MediaAudioStereoLevels levels)
    {
        float volume = _muted
            ? 0f
            : (float)Math.Clamp(
                _volume,
                0d,
                1d);
        float nativeBoost =
            Math.Max(1f, levels.Peak);
        float left =
            volume *
            levels.Left /
            nativeBoost;
        float right =
            volume *
            levels.Right /
            nativeBoost;
        player.SetVolume(left, right);
    }

    private void ApplyAudioGraph()
    {
        if (_player is { } player)
        {
            ApplyAudioGraph(player);
        }
    }

    private void ApplyAudioGraph(
        global::Android.Media.MediaPlayer player)
    {
        MediaAudioStereoLevels levels =
            GetCombinedAudioLevels();
        float gain = levels.Peak;
        ApplyVolume(player, in levels);

        if (gain <= 1f)
        {
            if (_loudnessEnhancer is { } inactive)
            {
                _ = inactive.SetEnabled(false);
            }
            return;
        }

        try
        {
            LoudnessEnhancer enhancer =
                _loudnessEnhancer ??=
                    new LoudnessEnhancer(
                        player.AudioSessionId);
            int millibels = checked(
                (int)Math.Clamp(
                    Math.Round(
                        2000d *
                        Math.Log10(gain)),
                    0d,
                    int.MaxValue));
            enhancer.SetTargetGain(millibels);
            _ = enhancer.SetEnabled(true);
        }
        catch (Exception exception)
        {
            PublishDiagnostics(
                $"Android could not activate LoudnessEnhancer for gain above unity: {exception.Message}");
        }
    }

    private MediaAudioStereoLevels
        GetCombinedAudioLevels()
    {
        MediaAudioStereoLevels levels =
            MediaAudioStereoLevels.FromBalance(
                (float)Math.Clamp(
                    _balance,
                    -1d,
                    1d));
        lock (_audioEffectGate)
        {
            for (int index = 0;
                 index < _audioEffects.Count;
                 index++)
            {
                MediaAudioGraphEffectState state =
                    _audioEffects[index]
                        .Effect
                        .CaptureState();
                levels = levels.Apply(in state);
            }
        }
        return levels;
    }

    private void OnAudioEffectStateChanged() =>
        Post(ApplyAudioGraph);

    private static AudioAttributes CreateAudioAttributes(
        MediaAudioCategory category)
    {
        AudioUsageKind usage = category switch
        {
            MediaAudioCategory.Communications or
            MediaAudioCategory.GameChat or
            MediaAudioCategory.Speech =>
                AudioUsageKind.VoiceCommunication,
            MediaAudioCategory.GameEffects or
            MediaAudioCategory.GameMedia =>
                AudioUsageKind.Game,
            MediaAudioCategory.Alerts =>
                AudioUsageKind.Alarm,
            MediaAudioCategory.SoundEffects =>
                AudioUsageKind.AssistanceSonification,
            _ => AudioUsageKind.Media
        };
        using var builder = new AudioAttributes.Builder();
        _ = builder.SetUsage(usage);
        return builder.Build() ??
            throw new InvalidOperationException(
                "Android could not create audio attributes.");
    }

    private void Cleanup()
    {
        _handler.RemoveCallbacks(_snapshotRunnable);
        AudioGraphEffectBinding[] bindings;
        lock (_audioEffectGate)
        {
            bindings = [.. _audioEffects];
            _audioEffects.Clear();
        }
        for (int index = 0;
             index < bindings.Length;
             index++)
        {
            bindings[index].Dispose();
        }
        LoudnessEnhancer? loudnessEnhancer =
            Interlocked.Exchange(
                ref _loudnessEnhancer,
                null);
        if (loudnessEnhancer is not null)
        {
            loudnessEnhancer.Release();
            loudnessEnhancer.Dispose();
        }
        ImageReader? reader =
            Interlocked.Exchange(ref _imageReader, null);
        if (reader is not null)
        {
            reader.SetOnImageAvailableListener(null, null);
        }
        global::Android.Media.MediaPlayer? player =
            Interlocked.Exchange(ref _player, null);
        if (player is not null)
        {
            player.Prepared -= OnPrepared;
            player.Completion -= OnCompletion;
            player.SeekComplete -= OnSeekCompleted;
            player.BufferingUpdate -= OnBufferingUpdate;
            player.Error -= OnError;
            player.TimedText -= OnTimedText;
            try
            {
                player.Stop();
            }
            catch
            {
            }
            player.Release();
            player.Dispose();
        }
        lock (_timedMetadataGate)
        {
            _selectedTimedMetadataTrack = -1;
            _timedMetadataTrackNativeIndices = [];
            _timedTextCueAccumulators = [];
        }
        reader?.Close();
        reader?.Dispose();
    }

    private void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _disposed) == 0)
        {
            _handler.Post(action);
        }
    }

    private void CancelOpen()
    {
        _opened.TrySetCanceled();
        Dispose();
    }

    private void PublishDiagnostics(string? fallbackReason)
    {
        _sink.UpdateDiagnostics(
            new MediaProviderDiagnostics(
                HardwareDecoded: true,
                TransferMode: MediaTransferMode.NativeZeroCopy,
                DroppedFrames:
                    Interlocked.Read(ref _droppedFrames),
                VideoQueueDepth: ImageRingSize,
                AudioQueueDepth: 0,
                AudioLatency: TimeSpan.Zero,
                LastFallbackReason: fallbackReason));
    }

    private static uint ParseDimension(string? value) =>
        uint.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out uint result)
            ? result
            : 0;

    private static long ParsePositiveLong(string? value) =>
        long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long result) &&
        result > 0
            ? result
            : 0;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    private sealed class SnapshotRunnable :
        Java.Lang.Object,
        Java.Lang.IRunnable
    {
        private AndroidMediaPlaybackProvider? _owner;

        internal SnapshotRunnable(
            AndroidMediaPlaybackProvider owner)
        {
            _owner = owner;
        }

        public void Run() =>
            Volatile.Read(ref _owner)?.PublishSnapshot();

        protected override void Dispose(bool disposing)
        {
            _owner = null;
            base.Dispose(disposing);
        }
    }

    private sealed class ImageListener :
        Java.Lang.Object,
        ImageReader.IOnImageAvailableListener
    {
        private AndroidMediaPlaybackProvider? _owner;

        internal ImageListener(
            AndroidMediaPlaybackProvider owner)
        {
            _owner = owner;
        }

        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null)
            {
                Volatile.Read(ref _owner)?.OnImageAvailable(reader);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _owner = null;
            base.Dispose(disposing);
        }
    }

    private sealed class AudioGraphEffectBinding :
        IDisposable
    {
        private readonly Action _changed;

        public AudioGraphEffectBinding(
            IMediaAudioGraphEffect effect,
            Action changed)
        {
            Effect = effect;
            _changed = changed;
            Effect.StateChanged += _changed;
        }

        public IMediaAudioGraphEffect Effect { get; }

        public void Dispose() =>
            Effect.StateChanged -= _changed;
    }
}

internal sealed class AndroidHardwareBufferOwner : IDisposable
{
    private nint _handle;
    private Image? _image;

    internal AndroidHardwareBufferOwner(
        nint handle,
        Image image)
    {
        _handle = handle;
        _image = image;
    }

    internal nint Handle => Volatile.Read(ref _handle);

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            AndroidHardwareBufferNative.Release(handle);
        }
        Image? image = Interlocked.Exchange(ref _image, null);
        image?.Close();
        image?.Dispose();
    }
}

internal static partial class AndroidHardwareBufferNative
{
    [LibraryImport(
        "android",
        EntryPoint = "AHardwareBuffer_fromHardwareBuffer")]
    internal static partial nint FromJavaHardwareBuffer(
        nint environment,
        nint hardwareBuffer);

    [LibraryImport(
        "android",
        EntryPoint = "AHardwareBuffer_acquire")]
    internal static partial void Acquire(nint hardwareBuffer);

    [LibraryImport(
        "android",
        EntryPoint = "AHardwareBuffer_release")]
    internal static partial void Release(nint hardwareBuffer);
}
