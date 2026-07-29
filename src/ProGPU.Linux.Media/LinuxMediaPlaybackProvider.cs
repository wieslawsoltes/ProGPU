using System.Diagnostics;
using ProGPU.Backend;
using ProGPU.Media.Audio;
using ProGPU.Media.Diagnostics;
using ProGPU.Media.Editing;
using ProGPU.Media.Containers;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;

namespace ProGPU.Linux.Media;

/// <summary>
/// Registers the dependency-free Linux V4L2/DMA-BUF playback provider and
/// V4L2 precise and ISO-BMFF fast exporters. Registration is explicit so
/// applications can replace any typed provider with a higher-priority
/// implementation.
/// </summary>
public static class LinuxMedia
{
    public static IDisposable Register(
        MediaProviderRegistry? registry = null,
        int priority = 100)
    {
        IDisposable playback =
            (registry ?? MediaProviderRegistry.Default).Register(
                new LinuxMediaPlaybackProviderFactory(
                    priority));
        LinuxNativeMediaCapabilitySnapshot capabilities =
            LinuxNativeMediaCapabilities.Probe();
        IDisposable preciseExport =
            MediaCompositionExportRegistry.Default.Register(
                new LinuxV4l2PreciseMediaCompositionExportProvider(
                    capabilities,
                    priority));
        IDisposable fastExport =
            MediaCompositionExportRegistry.Default.Register(
                new IsoBmffFastMediaCompositionExportProvider(
                    priority));
        IDisposable thumbnails =
            MediaCompositionThumbnailRegistry.Default.Register(
                new LinuxV4l2MediaCompositionThumbnailProvider(
                    capabilities,
                    priority));
        return new LinuxMediaRegistrations(
            playback,
            preciseExport,
            fastExport,
            thumbnails);
    }

    private sealed class LinuxMediaRegistrations :
        IDisposable
    {
        private IDisposable? _playback;
        private IDisposable? _preciseExport;
        private IDisposable? _fastExport;
        private IDisposable? _thumbnails;

        public LinuxMediaRegistrations(
            IDisposable playback,
            IDisposable preciseExport,
            IDisposable fastExport,
            IDisposable thumbnails)
        {
            _playback = playback;
            _preciseExport = preciseExport;
            _fastExport = fastExport;
            _thumbnails = thumbnails;
        }

        public void Dispose()
        {
            Interlocked.Exchange(
                ref _thumbnails,
                null)?.Dispose();
            Interlocked.Exchange(
                ref _fastExport,
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

public sealed class LinuxMediaPlaybackProviderFactory :
    IMediaPlaybackProviderFactory
{
    public LinuxMediaPlaybackProviderFactory(
        int priority = 100)
    {
        Priority = priority;
    }

    public string Id =>
        "progpu.linux.v4l2";

    public int Priority { get; }

    public bool CanOpen(
        MediaSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        return source.Kind switch
        {
            MediaSourceKind.Stream =>
                source.Stream is
                { CanRead: true, CanSeek: true },
            MediaSourceKind.Uri =>
                source.Uri is
                { IsFile: true },
            _ => false
        };
    }

    public ValueTask<IMediaPlaybackProvider>
        CreateAsync(
            MediaSourceDescriptor source,
            IMediaPlaybackSink sink,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken
            .ThrowIfCancellationRequested();
        if (!CanOpen(source))
        {
            throw new NotSupportedException(
                "The Linux V4L2 provider accepts seekable streams and local file URIs on Linux.");
        }
        return ValueTask.FromResult<IMediaPlaybackProvider>(
            new LinuxMediaPlaybackProvider(
                source,
                sink));
    }
}

/// <summary>
/// Clean-room Linux playback lane using the repository ISO-BMFF reader and a
/// V4L2 stateful hardware decoder. Decoded RGB or NV12 surfaces remain in
/// driver-owned DMA-BUF memory through WebGPU presentation. Scheduling work is
/// O(1) per pump plus O(B) compressed-byte copies into V4L2 OUTPUT buffers;
/// queue storage is bounded by driver buffer counts. This provider currently
/// advertises video-only playback because PipeWire is an audio transport, not
/// an AAC decoder.
/// </summary>
internal sealed class LinuxMediaPlaybackProvider :
    IMediaPlaybackProvider,
    IMediaPlaybackConfigurationProvider
{
    private const double DecodeAheadSeconds = 0.35;
    private static readonly TimeSpan s_joinTimeout =
        TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly MediaSourceDescriptor _source;
    private readonly IMediaPlaybackSink _sink;
    private readonly ManualResetEventSlim _signal =
        new(false);
    private readonly ManualResetEventSlim _stop =
        new(false);
    private readonly TaskCompletionSource _opened =
        new(
            TaskCreationOptions
                .RunContinuationsAsynchronously);
    private readonly List<IMediaAudioProcessor>
        _audioProcessors = [];

    private Thread? _worker;
    private MediaPlaybackSnapshot _snapshot =
        MediaPlaybackSnapshot.Empty;
    private TimeSpan _anchorPosition;
    private long _anchorTimestamp;
    private TimeSpan? _seekRequest;
    private double _rate = 1d;
    private double _volume = 1d;
    private double _balance;
    private MediaPlaybackConfiguration _configuration =
        MediaPlaybackConfiguration.Default;
    private bool _muted;
    private bool _looping;
    private bool _playRequested;
    private bool _endedRaised;
    private PipeWirePcmOutput? _audioOutput;
    private int _started;
    private int _disposed;
    private long _droppedFrames;

    internal LinuxMediaPlaybackProvider(
        MediaSourceDescriptor source,
        IMediaPlaybackSink sink)
    {
        _source = source;
        _sink = sink;
        _anchorTimestamp =
            Stopwatch.GetTimestamp();
    }

    public string Id =>
        "progpu.linux.v4l2";

    public async ValueTask OpenAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken
            .ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "V4L2 playback is available only on Linux.");
        }
        if (Interlocked.Exchange(
                ref _started,
                1) != 0)
        {
            throw new InvalidOperationException(
                "The Linux media provider is already open.");
        }

        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "ProGPU Linux Media"
        };
        _worker.Start();
        using CancellationTokenRegistration
            registration =
                cancellationToken.Register(
                    static state =>
                    {
                        var owner =
                            (LinuxMediaPlaybackProvider)
                            state!;
                        owner._stop.Set();
                        owner._signal.Set();
                    },
                    this);
        await _opened.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void Play()
    {
        ThrowIfDisposed();
        MediaPlaybackSnapshot snapshot;
        PipeWirePcmOutput? audio;
        lock (_gate)
        {
            if (_endedRaised ||
                (_snapshot.NaturalDuration >
                     TimeSpan.Zero &&
                 _anchorPosition >=
                     _snapshot.NaturalDuration))
            {
                _anchorPosition =
                    TimeSpan.Zero;
                _seekRequest =
                    TimeSpan.Zero;
            }
            if (!_playRequested)
            {
                _anchorTimestamp =
                    Stopwatch.GetTimestamp();
                _playRequested = true;
            }
            _endedRaised = false;
            _snapshot = _snapshot with
            {
                State =
                    MediaEnginePlaybackState.Playing
            };
            snapshot = _snapshot;
            audio = _audioOutput;
        }
        audio?.SetActive(true);
        _sink.Update(in snapshot);
        _signal.Set();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        TimeSpan position =
            CurrentPosition();
        MediaPlaybackSnapshot snapshot;
        PipeWirePcmOutput? audio;
        lock (_gate)
        {
            _anchorPosition = position;
            _anchorTimestamp =
                Stopwatch.GetTimestamp();
            _playRequested = false;
            _snapshot = _snapshot with
            {
                State =
                    MediaEnginePlaybackState.Paused,
                Position = _anchorPosition
            };
            snapshot = _snapshot;
            audio = _audioOutput;
        }
        audio?.SetActive(false);
        _sink.Update(in snapshot);
        _signal.Set();
    }

    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        lock (_gate)
        {
            if (_snapshot.NaturalDuration >
                    TimeSpan.Zero &&
                position >
                    _snapshot.NaturalDuration)
            {
                position =
                    _snapshot.NaturalDuration;
            }
            _anchorPosition = position;
            _anchorTimestamp =
                Stopwatch.GetTimestamp();
            _seekRequest = position;
            _endedRaised = false;
            _snapshot = _snapshot with
            {
                Position = position
            };
        }
        _signal.Set();
    }

    public void SetPlaybackRate(double value)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(value) ||
            value <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value));
        }
        lock (_gate)
        {
            if (_audioOutput is not null &&
                value != 1d)
            {
                throw new NotSupportedException(
                    "The built-in PipeWire PCM lane does not resample audio for non-unity playback rates.");
            }
            UpdateAnchorLocked();
            _rate = value;
            _snapshot = _snapshot with
            {
                PlaybackRate = value
            };
        }
        _signal.Set();
    }

    public void SetVolume(
        double volume,
        double balance,
        bool muted)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            _volume =
                Math.Clamp(volume, 0d, 1d);
            _balance =
                Math.Clamp(balance, -1d, 1d);
            _muted = muted;
            _audioOutput?.SetVolume(
                _volume,
                _balance,
                _muted);
        }
        if (_audioOutput is null &&
            (volume != 1d ||
            balance != 0d ||
            muted))
        {
            PublishDiagnostics(
                "The current Linux V4L2 provider is video-only; volume, balance, and mute will apply when the native audio lane is selected.");
        }
    }

    public void SetLooping(bool enabled)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            _looping = enabled;
        }
        _signal.Set();
    }

    public bool StepForwardOneFrame() =>
        false;

    public bool StepBackwardOneFrame() =>
        false;

    public void AddEffect(
        IMediaEffect effect,
        bool optional)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (effect is IMediaAudioEffect
            audioEffect &&
            effect.Kind ==
                MediaEffectKind.Audio)
        {
            IMediaAudioProcessor[] snapshot;
            PipeWirePcmOutput? output;
            lock (_gate)
            {
                _audioProcessors.Add(
                    audioEffect);
                snapshot =
                    [.. _audioProcessors];
                output = _audioOutput;
            }
            output?.SetProcessors(
                snapshot);
            return;
        }
        if (!optional)
        {
            throw new NotSupportedException(
                "Only typed IMediaAudioEffect processors can execute in the PipeWire callback. GPU video effects remain available through MediaVideoPresentationOptions.");
        }
    }

    public void RemoveAllEffects()
    {
        PipeWirePcmOutput? output;
        lock (_gate)
        {
            _audioProcessors.Clear();
            output = _audioOutput;
        }
        output?.ClearProcessors();
    }

    public void ApplyConfiguration(
        in MediaPlaybackConfiguration configuration)
    {
        ThrowIfDisposed();
        bool opened;
        lock (_gate)
        {
            _configuration = configuration;
            opened = _audioOutput is not null;
        }
        if (opened)
        {
            PublishDiagnostics(
                "PipeWire media-role changes take effect the next time the source is opened.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }

        _stop.Set();
        _signal.Set();
        Thread? worker = _worker;
        if (worker is not null &&
            worker != Thread.CurrentThread &&
            !worker.Join(s_joinTimeout))
        {
            _opened.TrySetException(
                new TimeoutException(
                    "The Linux media worker did not stop within five seconds."));
        }
        _stop.Dispose();
        _signal.Dispose();
    }

    private void WorkerMain()
    {
        Stream? ownedStream = null;
        V4l2StatefulVideoDecoder? decoder = null;
        IsoBmffNalAccessUnitReader? reader = null;
        IsoBmffPcmSampleReader? pcmReader = null;
        PipeWirePcmOutput? audioOutput = null;
        V4l2DecodedFrame pendingFrame = default;
        bool hasPendingFrame = false;
        try
        {
            Stream stream =
                OpenSource(out ownedStream);
            IsoBmffMovie movie =
                new IsoBmffDemuxer(
                    stream).Parse();
            IsoBmffTrack track =
                SelectVideoTrack(movie);
            IsoBmffTrack? audioTrack =
                SelectPcmTrack(movie);
            LinuxNativeMediaCapabilitySnapshot
                nativeCapabilities =
                    LinuxNativeMediaCapabilities
                        .Probe();
            LinuxVideoDecoderDevice device =
                SelectDecoder(
                    track,
                    in nativeCapabilities);
            reader =
                new IsoBmffNalAccessUnitReader(
                    stream,
                    track);
            decoder =
                CreateDecoder(
                    device,
                    track);

            string? audioFallback = null;
            if (audioTrack is not null &&
                nativeCapabilities
                    .PipeWireAvailable &&
                CurrentRate() == 1d)
            {
                try
                {
                    pcmReader =
                        new IsoBmffPcmSampleReader(
                            stream,
                            audioTrack);
                    audioOutput =
                        new PipeWirePcmOutput(
                            audioTrack
                                .AudioSampleRate,
                            audioTrack
                                .AudioChannelCount,
                            CurrentPipeWireRole());
                    lock (_gate)
                    {
                        audioOutput.SetVolume(
                            _volume,
                            _balance,
                            _muted);
                        audioOutput.SetProcessors(
                            _audioProcessors);
                    }
                    audioOutput
                        .StartAsync(
                            CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                    bool active =
                        IsPlaying();
                    audioOutput.SetActive(
                        active);
                    lock (_gate)
                    {
                        _audioOutput =
                            audioOutput;
                    }
                }
                catch (Exception exception)
                {
                    audioOutput?.Dispose();
                    audioOutput = null;
                    pcmReader?.Dispose();
                    pcmReader = null;
                    audioFallback =
                        $"PipeWire PCM output could not start: {exception.Message}";
                }
            }
            else if (HasAudioTrack(movie))
            {
                audioFallback =
                    CurrentRate() != 1d
                        ? "Built-in PipeWire PCM audio is disabled for non-unity playback rate because this lane does not perform hidden resampling."
                        : audioTrack is null
                        ? "This ISO-BMFF source has audio, but the built-in Linux lane currently decodes only version-zero sowt/twos PCM; AAC remains unsupported."
                        : "PipeWire is unavailable, so the Linux provider is presenting video only.";
            }

            TimeSpan duration =
                FromMediaTime(
                    track.Duration,
                    track.Timescale);
            if (audioTrack is not null)
            {
                duration = TimeSpan.FromTicks(
                    Math.Max(
                        duration.Ticks,
                        FromMediaTime(
                            audioTrack.Duration,
                            audioTrack.Timescale)
                        .Ticks));
            }
            TimeSpan frameDuration =
                track.Samples.Length == 0
                    ? TimeSpan.Zero
                    : FromMediaTime(
                        track.Samples[0].Duration,
                        track.Timescale);
            var capabilities =
                new MediaProviderCapabilities(
                    CanPause: true,
                    CanSeek: true,
                    SupportsRate:
                        audioOutput is null,
                    SupportsFrameStepping: false,
                    HardwareDecoded: true,
                    HasAudio:
                        audioOutput is not null,
                    HasVideo: true);
            lock (_gate)
            {
                _snapshot =
                    new MediaPlaybackSnapshot(
                        _playRequested
                            ? MediaEnginePlaybackState
                                .Playing
                            : MediaEnginePlaybackState
                                .Paused,
                        _anchorPosition,
                        duration,
                        track.Width,
                        track.Height,
                        BufferingProgress: 1d,
                        DownloadProgress: 1d,
                        _rate,
                        capabilities);
            }
            MediaPlaybackSnapshot opened =
                Snapshot();
            _sink.Opened(in opened);
            PublishDiagnostics(
                audioFallback ??
                "V4L2 is parsing stream metadata before configuring the DMA-BUF CAPTURE queue.");
            _opened.TrySetResult();

            int sampleIndex =
                FindResumeSample(
                    track,
                    TimeSpan.Zero);
            int audioSampleIndex =
                audioTrack is null
                    ? 0
                    : FindResumeSample(
                        audioTrack,
                        TimeSpan.Zero);
            int audioScalarOffset = 0;
            bool draining = false;
            TimeSpan discardBefore =
                TimeSpan.Zero;
            long lastSnapshotTimestamp = 0;

            while (!_stop.IsSet)
            {
                TimeSpan? seek =
                    TakeSeekRequest();
                if (seek.HasValue)
                {
                    if (hasPendingFrame)
                    {
                        pendingFrame.Owner.Dispose();
                        pendingFrame = default;
                        hasPendingFrame = false;
                    }
                    decoder.Dispose();
                    decoder =
                        CreateDecoder(
                            device,
                            track);
                    sampleIndex =
                        FindResumeSample(
                            track,
                            seek.Value);
                    if (audioOutput is not null &&
                        audioTrack is not null)
                    {
                        audioOutput.SetActive(false);
                        audioOutput.Reset(
                            seek.Value);
                        audioSampleIndex =
                            FindResumeSample(
                                audioTrack,
                                seek.Value);
                        audioScalarOffset = 0;
                        audioOutput.SetActive(
                            IsPlaying());
                    }
                    discardBefore =
                        seek.Value;
                    draining = false;
                    _sink.SeekCompleted(
                        seek.Value);
                }

                TimeSpan position =
                    CurrentPosition();
                bool playing =
                    IsPlaying();
                TimeSpan feedLimit =
                    playing
                        ? position +
                          TimeSpan.FromSeconds(
                              DecodeAheadSeconds)
                        : position +
                          frameDuration;
                if (audioOutput is not null &&
                    pcmReader is not null &&
                    audioTrack is not null)
                {
                    TimeSpan audioFeedLimit =
                        position +
                        TimeSpan.FromMilliseconds(
                            playing ? 250 : 100);
                    while (audioSampleIndex <
                           audioTrack.Samples.Length)
                    {
                        if (audioScalarOffset == 0 &&
                            PresentationTime(
                                audioTrack,
                                audioSampleIndex) >
                            audioFeedLimit)
                        {
                            break;
                        }
                        ReadOnlySpan<float> pcm =
                            audioScalarOffset == 0
                                ? pcmReader.Read(
                                    audioSampleIndex)
                                : pcmReader.Current;
                        int writtenFrames =
                            audioOutput.Write(
                                pcm[
                                    audioScalarOffset..]);
                        if (writtenFrames == 0)
                        {
                            break;
                        }
                        audioScalarOffset +=
                            checked(
                                writtenFrames *
                                (int)audioTrack
                                    .AudioChannelCount);
                        if (audioScalarOffset ==
                            pcm.Length)
                        {
                            audioScalarOffset = 0;
                            audioSampleIndex++;
                        }
                    }
                }
                while (sampleIndex <
                           track.Samples.Length &&
                       (!decoder.IsCaptureConfigured ||
                        PresentationTime(
                            track,
                            sampleIndex) <=
                            feedLimit))
                {
                    ReadOnlySpan<byte>
                        accessUnit =
                            reader.Read(
                                sampleIndex);
                    if (!decoder
                            .TryQueueAccessUnit(
                                accessUnit,
                                PresentationTime(
                                    track,
                                    sampleIndex)))
                    {
                        break;
                    }
                    sampleIndex++;
                }

                V4l2DecoderPumpResult
                    pumpResult =
                        decoder.Pump(
                            timeoutMilliseconds: 4);
                if (pumpResult ==
                        V4l2DecoderPumpResult
                            .SourceChanged)
                {
                    if (decoder.IsCaptureConfigured)
                    {
                        if (hasPendingFrame)
                        {
                            pendingFrame.Owner.Dispose();
                            pendingFrame = default;
                            hasPendingFrame = false;
                        }
                        TimeSpan resume =
                            CurrentPosition();
                        decoder.Dispose();
                        decoder =
                            CreateDecoder(
                                device,
                                track);
                        sampleIndex =
                            FindResumeSample(
                                track,
                                resume);
                        discardBefore = resume;
                        draining = false;
                        PublishDiagnostics(
                            "V4L2 reported a dynamic source change; the provider reopened the decoder at the preceding sync sample while old DMA-BUF leases drain independently.");
                        continue;
                    }
                    decoder.ConfigureCapture();
                    UpdateNaturalVideoSize(
                        decoder.CaptureWidth,
                        decoder.CaptureHeight);
                    PublishDiagnostics(
                        decoder.DecodedPixelFormat ==
                            V4l2DecodedPixelFormat.Nv12
                            ? "V4L2 NV12/NV12M is imported as separate R8/RG8 DMA-BUF planes and converted by the ProGPU WebGPU shader."
                            : null);
                }

                if (!hasPendingFrame &&
                    decoder.TryDequeueFrame(
                        out pendingFrame))
                {
                    hasPendingFrame = true;
                }
                if (hasPendingFrame &&
                    pendingFrame.PresentationTime <
                        discardBefore)
                {
                    pendingFrame.Owner.Dispose();
                    pendingFrame = default;
                    hasPendingFrame = false;
                    continue;
                }
                if (hasPendingFrame)
                {
                    discardBefore =
                        TimeSpan.Zero;
                }

                position = CurrentPosition();
                if (hasPendingFrame &&
                    pendingFrame.PresentationTime <=
                        position +
                        TimeSpan.FromMilliseconds(2))
                {
                    PresentFrame(
                        in pendingFrame,
                        frameDuration);
                    pendingFrame = default;
                    hasPendingFrame = false;
                }

                if (sampleIndex ==
                        track.Samples.Length &&
                    decoder.IsCaptureConfigured &&
                    !decoder.HasQueuedOutput &&
                    !draining)
                {
                    decoder.BeginDrain();
                    draining = true;
                }
                if (draining &&
                    decoder.EndOfStreamReached &&
                    !hasPendingFrame &&
                    (audioOutput is null ||
                     audioTrack is null ||
                     (audioSampleIndex ==
                          audioTrack.Samples.Length &&
                      audioScalarOffset == 0 &&
                      audioOutput.QueuedFrames == 0)))
                {
                    if (ShouldLoop())
                    {
                        RequestWorkerSeek(
                            TimeSpan.Zero);
                        draining = false;
                    }
                    else
                    {
                        RaiseEnded(duration);
                    }
                }

                long now =
                    Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(
                        lastSnapshotTimestamp,
                        now) >=
                    TimeSpan.FromMilliseconds(100))
                {
                    UpdateSnapshotPosition(
                        position);
                    lastSnapshotTimestamp = now;
                }

                _signal.Wait(
                    playing ? 1 : 8);
                _signal.Reset();
            }
        }
        catch (Exception exception)
        {
            if (!_opened.TrySetException(
                    exception) &&
                !_stop.IsSet)
            {
                _sink.Failed(
                    MediaPlaybackFailure.Decode,
                    exception.Message,
                    exception);
            }
        }
        finally
        {
            if (hasPendingFrame)
            {
                pendingFrame.Owner.Dispose();
            }
            reader?.Dispose();
            pcmReader?.Dispose();
            decoder?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(
                        _audioOutput,
                        audioOutput))
                {
                    _audioOutput = null;
                }
            }
            audioOutput?.Dispose();
            ownedStream?.Dispose();
        }
    }

    private Stream OpenSource(
        out Stream? owned)
    {
        _source.ThrowIfDisposed();
        if (_source.Kind ==
                MediaSourceKind.Stream &&
            _source.Stream is
            { CanRead: true, CanSeek: true }
                stream)
        {
            owned = null;
            return stream;
        }
        if (_source.Kind ==
                MediaSourceKind.Uri &&
            _source.Uri is
            { IsFile: true } uri)
        {
            owned = new FileStream(
                uri.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess);
            return owned;
        }
        throw new NotSupportedException(
            "The Linux V4L2 provider requires a seekable ISO-BMFF source.");
    }

    private static IsoBmffTrack SelectVideoTrack(
        IsoBmffMovie movie)
    {
        foreach (IsoBmffTrack track in
                 movie.Tracks)
        {
            if (track.Kind ==
                    IsoBmffTrackKind.Video &&
                track.Codec is
                    IsoBmffCodec.H264 or
                    IsoBmffCodec.H265 &&
                track.Samples.Length != 0)
            {
                return track;
            }
        }
        throw new NotSupportedException(
            "The Linux V4L2 provider currently accepts H.264 or H.265 video tracks in seekable ISO-BMFF sources.");
    }

    private static LinuxVideoDecoderDevice
        SelectDecoder(
            IsoBmffTrack track,
            in LinuxNativeMediaCapabilitySnapshot
                capabilities)
    {
        LinuxHardwareVideoCodec required =
            track.Codec == IsoBmffCodec.H264
                ? LinuxHardwareVideoCodec.H264
                : LinuxHardwareVideoCodec.H265;
        foreach (LinuxVideoDecoderDevice device in
                 capabilities.VideoDecoders)
        {
            if (device.UsesMultiPlanarQueues &&
                device.SupportsStreaming &&
                (device.Codecs & required) != 0)
            {
                return device;
            }
        }
        throw new NotSupportedException(
            $"No streaming multi-planar V4L2 stateful decoder exposes {required}.");
    }

    private static IsoBmffTrack? SelectPcmTrack(
        IsoBmffMovie movie)
    {
        foreach (IsoBmffTrack track in
                 movie.Tracks)
        {
            if (track.Kind ==
                    IsoBmffTrackKind.Audio &&
                track.Codec ==
                    IsoBmffCodec.Pcm &&
                track.PcmEncoding !=
                    IsoBmffPcmEncoding.Unknown &&
                track.AudioChannelCount is
                    > 0 and <= 8 &&
                track.AudioBitsPerSample is
                    16 or 24 or 32 &&
                track.AudioSampleRate is
                    >= 8_000 and <= 384_000)
            {
                return track;
            }
        }
        return null;
    }

    private static bool HasAudioTrack(
        IsoBmffMovie movie)
    {
        foreach (IsoBmffTrack track in
                 movie.Tracks)
        {
            if (track.Kind ==
                IsoBmffTrackKind.Audio)
            {
                return true;
            }
        }
        return false;
    }

    private static V4l2StatefulVideoDecoder
        CreateDecoder(
            in LinuxVideoDecoderDevice device,
            IsoBmffTrack track)
    {
        var decoder =
            new V4l2StatefulVideoDecoder(
                device.Path,
                track);
        try
        {
            decoder.Open();
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    private void PresentFrame(
        in V4l2DecodedFrame frame,
        TimeSpan duration)
    {
        var descriptor =
            new MediaGpuFrameDescriptor(
                frame.Sequence,
                frame.PresentationTime,
                duration,
                frame.Width,
                frame.Height,
                frame.PixelFormat ==
                    V4l2DecodedPixelFormat.Nv12
                    ? MediaVideoPixelFormat.Nv12
                    : frame.PixelFormat ==
                      V4l2DecodedPixelFormat.P010
                        ? MediaVideoPixelFormat.P010
                    : frame.PixelFormat ==
                      V4l2DecodedPixelFormat.Bgra8
                        ? MediaVideoPixelFormat.Bgra8
                        : MediaVideoPixelFormat.Rgba8,
                MediaTransferMode.NativeZeroCopy,
                frame.PixelFormat is
                    V4l2DecodedPixelFormat.Nv12 or
                    V4l2DecodedPixelFormat.P010
                    ? new MediaColorInfo(
                        MediaColorPrimaries.Bt709,
                        MediaTransferFunction.Bt709,
                        MediaMatrixCoefficients.Bt709,
                        FullRange: false)
                    : new MediaColorInfo(
                        MediaColorPrimaries.Bt709,
                        MediaTransferFunction.Srgb,
                        MediaMatrixCoefficients.Identity,
                        FullRange: true));
        IMediaGpuFrame? mediaFrame = null;
        try
        {
            if (frame.TryCreateExternalDescriptor(
                    out ProGpuExternalTextureDescriptor
                        external))
            {
                mediaFrame =
                    new ExternalMediaGpuFrame(
                        in descriptor,
                        in external,
                        frame.Owner);
            }
            else if (frame
                .TryCreatePlanarExternalDescriptors(
                    out ProGpuExternalTextureDescriptor
                        luma,
                    out ProGpuExternalTextureDescriptor
                        chroma))
            {
                mediaFrame =
                    new ExternalPlanarMediaGpuFrame(
                        in descriptor,
                        in luma,
                        in chroma,
                        frame.Owner);
            }
            else
            {
                throw new NotSupportedException(
                    "The decoded V4L2 frame cannot be represented as a ProGPU external texture.");
            }

            _sink.Present(mediaFrame);
            mediaFrame = null;
        }
        catch
        {
            Interlocked.Increment(
                ref _droppedFrames);
            throw;
        }
        finally
        {
            mediaFrame?.Dispose();
        }
    }

    private static int FindResumeSample(
        IsoBmffTrack track,
        TimeSpan position)
    {
        long target =
            ToMediaTime(
                position,
                track.Timescale);
        int selected = 0;
        for (int index = 0;
             index < track.Samples.Length;
             index++)
        {
            IsoBmffSample sample =
                track.Samples[index];
            if (sample.PresentationTime >
                target)
            {
                break;
            }
            if (sample.IsSync)
            {
                selected = index;
            }
        }
        return selected;
    }

    private static TimeSpan PresentationTime(
        IsoBmffTrack track,
        int sampleIndex) =>
        FromMediaTime(
            track.Samples[sampleIndex]
                .PresentationTime,
            track.Timescale);

    private static TimeSpan FromMediaTime(
        long value,
        uint timescale)
    {
        if (timescale == 0)
        {
            return TimeSpan.Zero;
        }
        return TimeSpan.FromTicks(
            checked(
                (long)Math.Round(
                    value *
                    (double)TimeSpan
                        .TicksPerSecond /
                    timescale)));
    }

    private static long ToMediaTime(
        TimeSpan value,
        uint timescale) =>
        checked(
            (long)Math.Round(
                value.Ticks *
                (double)timescale /
                TimeSpan.TicksPerSecond));

    private MediaPlaybackSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    private TimeSpan CurrentPosition()
    {
        PipeWirePcmOutput? audio;
        bool playing;
        TimeSpan duration;
        TimeSpan fallback;
        lock (_gate)
        {
            playing = _playRequested;
            audio = _audioOutput;
            duration =
                _snapshot.NaturalDuration;
            if (!playing)
            {
                return _anchorPosition;
            }
            TimeSpan elapsed =
                Stopwatch.GetElapsedTime(
                    _anchorTimestamp);
            fallback =
                _anchorPosition +
                TimeSpan.FromTicks(
                    checked(
                        (long)(elapsed.Ticks *
                               _rate)));
        }

        TimeSpan position =
            audio is not null &&
            audio.TryGetClock(
                out TimeSpan audioPosition,
                out _)
                ? audioPosition
                : fallback;
        if (duration > TimeSpan.Zero &&
            position > duration)
        {
            position = duration;
        }
        lock (_gate)
        {
            if (_playRequested)
            {
                _anchorPosition = position;
                _anchorTimestamp =
                    Stopwatch.GetTimestamp();
            }
        }
        return position;
    }

    private bool IsPlaying()
    {
        lock (_gate)
        {
            return _playRequested;
        }
    }

    private double CurrentRate()
    {
        lock (_gate)
        {
            return _rate;
        }
    }

    private PipeWireAudioRole
        CurrentPipeWireRole()
    {
        lock (_gate)
        {
            return _configuration.AudioCategory switch
            {
                MediaAudioCategory
                    .Communications or
                MediaAudioCategory.GameChat or
                MediaAudioCategory.Speech =>
                    PipeWireAudioRole
                        .Communication,
                MediaAudioCategory.Alerts =>
                    PipeWireAudioRole
                        .Notification,
                MediaAudioCategory.GameEffects or
                MediaAudioCategory.GameMedia =>
                    PipeWireAudioRole.Game,
                MediaAudioCategory.Movie =>
                    PipeWireAudioRole.Movie,
                _ => PipeWireAudioRole.Music
            };
        }
    }

    private TimeSpan? TakeSeekRequest()
    {
        lock (_gate)
        {
            TimeSpan? request =
                _seekRequest;
            _seekRequest = null;
            return request;
        }
    }

    private void RequestWorkerSeek(
        TimeSpan position)
    {
        lock (_gate)
        {
            _anchorPosition = position;
            _anchorTimestamp =
                Stopwatch.GetTimestamp();
            _seekRequest = position;
            _endedRaised = false;
        }
    }

    private bool ShouldLoop()
    {
        lock (_gate)
        {
            return _looping;
        }
    }

    private void RaiseEnded(TimeSpan duration)
    {
        bool raise;
        MediaPlaybackSnapshot snapshot;
        lock (_gate)
        {
            raise = !_endedRaised;
            _endedRaised = true;
            _playRequested = false;
            _anchorPosition = duration;
            _anchorTimestamp =
                Stopwatch.GetTimestamp();
            _snapshot = _snapshot with
            {
                State =
                    MediaEnginePlaybackState.Paused,
                Position = duration
            };
            snapshot = _snapshot;
        }
        if (raise)
        {
            PipeWirePcmOutput? audio;
            lock (_gate)
            {
                audio = _audioOutput;
            }
            audio?.SetActive(false);
            _sink.Update(in snapshot);
            _sink.Ended();
        }
    }

    private void UpdateSnapshotPosition(
        TimeSpan position)
    {
        MediaPlaybackSnapshot snapshot;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Position = position,
                PlaybackRate = _rate
            };
            snapshot = _snapshot;
        }
        _sink.Update(in snapshot);
    }

    private void UpdateNaturalVideoSize(
        uint width,
        uint height)
    {
        MediaPlaybackSnapshot snapshot;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                NaturalVideoWidth = width,
                NaturalVideoHeight = height
            };
            snapshot = _snapshot;
        }
        _sink.Update(in snapshot);
    }

    private void UpdateAnchorLocked()
    {
        if (_playRequested)
        {
            TimeSpan elapsed =
                Stopwatch.GetElapsedTime(
                    _anchorTimestamp);
            _anchorPosition +=
                TimeSpan.FromTicks(
                    checked(
                        (long)(elapsed.Ticks *
                               _rate)));
            if (_snapshot.NaturalDuration >
                    TimeSpan.Zero &&
                _anchorPosition >
                    _snapshot.NaturalDuration)
            {
                _anchorPosition =
                    _snapshot.NaturalDuration;
            }
        }
        _anchorTimestamp =
            Stopwatch.GetTimestamp();
    }

    private void PublishDiagnostics(
        string? fallbackReason)
    {
        PipeWirePcmOutput? audio;
        lock (_gate)
        {
            audio = _audioOutput;
        }
        TimeSpan audioLatency =
            audio is not null &&
            audio.TryGetClock(
                out _,
                out TimeSpan measuredLatency)
                ? measuredLatency
                : audio?.QueuedDuration ??
                  TimeSpan.Zero;
        _sink.UpdateDiagnostics(
            new MediaProviderDiagnostics(
                HardwareDecoded: true,
                TransferMode:
                    MediaTransferMode
                        .NativeZeroCopy,
                DroppedFrames:
                    Interlocked.Read(
                        ref _droppedFrames),
                VideoQueueDepth: 1,
                AudioQueueDepth:
                    audio is null
                        ? 0
                        : audio.QueuedFrames,
                AudioLatency:
                    audioLatency,
                LastFallbackReason:
                    fallbackReason));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(
                ref _disposed) != 0,
            this);
    }
}
