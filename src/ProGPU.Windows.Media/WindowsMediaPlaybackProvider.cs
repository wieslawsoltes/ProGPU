using System.Collections.Concurrent;
using ProGPU.Backend;
using ProGPU.Media.Audio;
using ProGPU.Media.Diagnostics;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;
using Silk.NET.WebGPU;

namespace ProGPU.Windows.Media;

/// <summary>
/// Registers the dependency-free Media Foundation provider. Registration is
/// explicit so applications can replace it with a higher-priority provider.
/// </summary>
public static class WindowsMedia
{
    public static IDisposable Register(
        MediaProviderRegistry? registry = null,
        int priority = 100)
    {
        IDisposable playback =
            (registry ?? MediaProviderRegistry.Default).Register(
                new WindowsMediaPlaybackProviderFactory(priority));
        IDisposable preciseExport =
            MediaCompositionExportRegistry.Default.Register(
                new WindowsMediaFoundationCompositionExportProvider(
                    priority));
        IDisposable thumbnails =
            MediaCompositionThumbnailRegistry.Default.Register(
                new WindowsMediaFoundationCompositionThumbnailProvider(
                    priority));
        IDisposable fastExport =
            MediaCompositionExportRegistry.Default.Register(
                new IsoBmffFastMediaCompositionExportProvider(
                    LowerPriority(priority)));
        return new WindowsMediaRegistrations(
            playback,
            preciseExport,
            thumbnails,
            fastExport);
    }

    private static int LowerPriority(int priority) =>
        priority == int.MinValue
            ? int.MinValue
            : priority - 1;

    private sealed class WindowsMediaRegistrations :
        IDisposable
    {
        private IDisposable? _playback;
        private IDisposable? _preciseExport;
        private IDisposable? _thumbnails;
        private IDisposable? _fastExport;

        public WindowsMediaRegistrations(
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

public sealed class WindowsMediaPlaybackProviderFactory :
    IMediaPlaybackProviderFactory
{
    public WindowsMediaPlaybackProviderFactory(int priority = 100)
    {
        Priority = priority;
    }

    public string Id => "progpu.windows.mediafoundation";
    public int Priority { get; }

    public bool CanOpen(MediaSourceDescriptor source) =>
        OperatingSystem.IsWindows() &&
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
                "The Media Foundation provider accepts absolute URI media sources on Windows.");
        }

        return ValueTask.FromResult<IMediaPlaybackProvider>(
            new WindowsMediaPlaybackProvider(source.Uri!, sink));
    }
}

/// <summary>
/// Uses IMFMediaEngine in frame-server mode for native hardware decode and
/// audio presentation. Video stays on the GPU: Media Foundation blits into a
/// pooled shared D3D11 texture which Dawn imports through a DXGI HANDLE.
/// </summary>
internal sealed class WindowsMediaPlaybackProvider :
    IMediaPlaybackProvider,
    IMediaPlaybackConfigurationProvider,
    IMediaPlaybackTrackProvider,
    IMediaPlaybackTimedMetadataProvider
{
    private const int EventError = 5;
    private const int EventLoadedMetadata = 10;
    private const int EventSeeked = 17;
    private const int EventEnded = 19;
    private const int EventFormatChange = 1000;
    private const int EventStreamRenderingError = 1014;
    private const int FlagMetadata = 1 << 0;
    private const int FlagSeeked = 1 << 1;
    private const int FlagEnded = 1 << 2;
    private const int FlagFormatChange = 1 << 3;
    private const int FlagError = 1 << 4;
    private const int TimedTextEventReset = -1;
    private const int FramePoolSize = 3;
    private static readonly TimeSpan s_workerJoinTimeout =
        TimeSpan.FromSeconds(5);
    private const string TransferDiagnostic =
        "Media Foundation frame-server mode performs one GPU-local blit into a shared D3D11 texture. No CPU readback or upload occurs; direct decoder-allocation zero-copy requires the future Source Reader lane.";

    private readonly Uri _uri;
    private readonly IMediaPlaybackSink _sink;
    private readonly ConcurrentQueue<Action<nint>> _commands = new();
    private readonly ConcurrentQueue<TimedTextCueEvent>
        _timedTextCueEvents = new();
    private readonly ConcurrentQueue<TimedTextErrorEvent>
        _timedTextErrors = new();
    private readonly Dictionary<uint, TimedTextTrackCueState>
        _timedTextCueStates = [];
    private readonly object _audioEffectGate = new();
    private readonly List<AudioGraphEffectBinding>
        _audioEffects = [];
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly TaskCompletionSource _opened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MediaPlaybackConfiguration _configuration =
        MediaPlaybackConfiguration.Default;
    private Thread? _worker;
    private MediaPlaybackSnapshot _snapshot =
        MediaPlaybackSnapshot.Empty;
    private uint[] _audioStreamNativeIndices = [];
    private uint[] _videoStreamNativeIndices = [];
    private uint[] _timedTextTrackNativeIds = [];
    private bool[] _timedTextTrackSelectable = [];
    private double _volume = 1d;
    private double _balance;
    private double _rate = 1d;
    private bool _muted;
    private bool _looping;
    private long _sequence;
    private long _droppedFrames;
    private nuint _nativeError;
    private int _nativeEvents;
    private int _timedTextTracksDirty;
    private int _started;
    private int _stopDisposed;
    private int _disposed;

    internal WindowsMediaPlaybackProvider(
        Uri uri,
        IMediaPlaybackSink sink)
    {
        _uri = uri;
        _sink = sink;
    }

    public string Id => "progpu.windows.mediafoundation";

    public async ValueTask OpenAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Media Foundation playback is available only on Windows.");
        }
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "A Media Foundation provider can be opened only once.");
        }

        var thread = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "ProGPU Media Foundation"
        };
        _worker = thread;
        thread.Start();

        using CancellationTokenRegistration registration =
            cancellationToken.Register(
                static state =>
                    ((WindowsMediaPlaybackProvider)state!).CancelOpen(),
                this);
        await _opened.Task.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void Play() =>
        Enqueue(WindowsMediaNative.Play);

    public void Pause() =>
        Enqueue(WindowsMediaNative.Pause);

    public void Seek(TimeSpan position)
    {
        double seconds = Math.Max(0d, position.TotalSeconds);
        Enqueue(engine =>
            WindowsMediaNative.SetCurrentTime(engine, seconds));
    }

    public void SetPlaybackRate(double value)
    {
        ThrowIfDisposed();
        _rate = value;
        Enqueue(engine =>
            WindowsMediaNative.SetPlaybackRate(engine, value));
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
        Enqueue(ApplyAudioGraph);
    }

    public void SetLooping(bool enabled)
    {
        ThrowIfDisposed();
        _looping = enabled;
        Enqueue(engine =>
            WindowsMediaNative.SetLoop(engine, enabled));
    }

    public bool StepForwardOneFrame()
    {
        if (Volatile.Read(ref _started) == 0 ||
            Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        Enqueue(static engine =>
            WindowsMediaNative.FrameStep(
                engine,
                forward: true));
        return true;
    }

    public bool StepBackwardOneFrame()
    {
        if (Volatile.Read(ref _started) == 0 ||
            Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        Enqueue(static engine =>
            WindowsMediaNative.FrameStep(
                engine,
                forward: false));
        return true;
    }

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

        uint[] nativeIndices = kind ==
            MediaPlaybackTrackKind.Audio
                ? Volatile.Read(
                    ref _audioStreamNativeIndices)
                : Volatile.Read(
                    ref _videoStreamNativeIndices);
        if (index < -1 || index >= nativeIndices.Length)
        {
            return false;
        }

        Enqueue(engine =>
        {
            WindowsMediaNative.SetExclusiveStreamSelection(
                engine,
                nativeIndices,
                index < 0
                    ? uint.MaxValue
                    : nativeIndices[index]);
            PublishTracks(
                engine,
                _snapshot.NaturalVideoWidth,
                _snapshot.NaturalVideoHeight);
        });
        return true;
    }

    public bool TrySetTimedMetadataPresentationMode(
        int index,
        MediaPlaybackTimedMetadataPresentationMode mode)
    {
        if (mode ==
            MediaPlaybackTimedMetadataPresentationMode
                .PlatformPresented)
        {
            return false;
        }

        uint[] nativeIds = Volatile.Read(
            ref _timedTextTrackNativeIds);
        bool[] selectable = Volatile.Read(
            ref _timedTextTrackSelectable);
        if ((uint)index >= (uint)nativeIds.Length ||
            selectable.Length != nativeIds.Length ||
            (!selectable[index] &&
             mode !=
                MediaPlaybackTimedMetadataPresentationMode
                    .Disabled))
        {
            return false;
        }

        uint trackId = nativeIds[index];
        bool selected =
            mode !=
            MediaPlaybackTimedMetadataPresentationMode
                .Disabled;
        Enqueue(engine =>
        {
            if (!WindowsMediaNative.TryGetTimedTextService(
                    engine,
                    out nint timedText))
            {
                return;
            }

            try
            {
                WindowsMediaNative.SelectTimedTextTrack(
                    timedText,
                    trackId,
                    selected);
            }
            finally
            {
                WindowsMediaNative.Release(timedText);
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
                    "Media Foundation accepts typed gain and stereo-balance IMediaAudioGraphEffect nodes in the built-in lane. Arbitrary PCM effects require an IMFTransform or WASAPI processing provider.");
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
        Enqueue(ApplyAudioGraph);
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
        Enqueue(ApplyAudioGraph);
    }

    public void ApplyConfiguration(
        in MediaPlaybackConfiguration configuration)
    {
        ThrowIfDisposed();
        _configuration = configuration;
        if (Volatile.Read(ref _started) != 0)
        {
            PublishDiagnostics(
                "MediaAudioCategory is applied when IMFMediaEngine is created; changing it on an open source takes effect on the next source.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
        Thread? worker = Interlocked.Exchange(ref _worker, null);
        if (worker is not null &&
            worker != Thread.CurrentThread &&
            worker.IsAlive)
        {
            _ = worker.Join(s_workerJoinTimeout);
        }
        _opened.TrySetCanceled();
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
        if (worker is null || !worker.IsAlive)
        {
            DisposeStopSignal();
        }
    }

    private void WorkerMain()
    {
        nint attributes = 0;
        nint d3dDevice = 0;
        nint d3dContext = 0;
        nint dxgiManager = 0;
        nint factory = 0;
        nint engine = 0;
        nint timedText = 0;
        MediaEngineNotification? notification = null;
        MediaEngineTimedTextNotification?
            timedTextNotification = null;
        SharedTexturePool? pool = null;
        bool comInitialized = false;
        bool mediaFoundationStarted = false;
        try
        {
            WindowsMediaNative.InitializeCom();
            comInitialized = true;
            WindowsMediaNative.StartupMediaFoundation();
            mediaFoundationStarted = true;
            notification =
                new MediaEngineNotification(OnNativeEvent);
            d3dDevice =
                WindowsMediaNative.CreateD3D11Device(out d3dContext);
            dxgiManager =
                WindowsMediaNative.CreateDxgiDeviceManager(d3dDevice);
            attributes = WindowsMediaNative.CreateAttributes(5);
            WindowsMediaNative.SetAttributeUnknown(
                attributes,
                in WindowsMediaNative.MediaEngineCallback,
                notification.NativePointer);
            WindowsMediaNative.SetAttributeUnknown(
                attributes,
                in WindowsMediaNative.MediaEngineDxgiManager,
                dxgiManager);
            WindowsMediaNative.SetAttributeUInt32(
                attributes,
                in WindowsMediaNative.MediaEngineVideoOutputFormat,
                WindowsMediaNative.DxgiFormatB8G8R8A8Unorm);
            WindowsMediaNative.SetAttributeUInt32(
                attributes,
                in WindowsMediaNative.MediaEngineAudioCategory,
                unchecked((uint)_configuration.AudioCategory));
            WindowsMediaNative.SetAttributeUInt32(
                attributes,
                in WindowsMediaNative.MediaEngineAudioEndpointRole,
                unchecked((uint)_configuration.AudioDeviceRole));
            factory = WindowsMediaNative.CreateMediaEngineFactory();
            engine =
                WindowsMediaNative.CreateMediaEngine(
                    factory,
                    attributes,
                    _configuration.RealTimePlayback);
            if (WindowsMediaNative.TryGetTimedTextService(
                    engine,
                    out timedText))
            {
                timedTextNotification =
                    new MediaEngineTimedTextNotification(
                        OnTimedTextTrackChanged,
                        OnTimedTextError,
                        OnTimedTextCue,
                        OnTimedTextReset);
                WindowsMediaNative
                    .RegisterTimedTextNotifications(
                        timedText,
                        timedTextNotification.NativePointer);
            }
            ApplyInitialSettings(engine);
            WindowsMediaNative.SetSource(engine, _uri.AbsoluteUri);
            WindowsMediaNative.Load(engine);

            WaitForMetadata(engine);
            DisableInitialTimedTextTracks(timedText);
            bool hasVideo = WindowsMediaNative.HasVideo(engine);
            bool hasAudio = WindowsMediaNative.HasAudio(engine);
            uint width = 0;
            uint height = 0;
            if (hasVideo)
            {
                WindowsMediaNative.GetNativeVideoSize(
                    engine,
                    out width,
                    out height);
                if (width != 0 && height != 0)
                {
                    pool = new SharedTexturePool(
                        d3dDevice,
                        width,
                        height,
                        FramePoolSize);
                }
            }

            double durationSeconds =
                WindowsMediaNative.GetDuration(engine);
            _snapshot = new MediaPlaybackSnapshot(
                MediaEnginePlaybackState.Paused,
                TimeSpan.Zero,
                ToTimeSpan(durationSeconds),
                width,
                height,
                BufferingProgress: 1d,
                DownloadProgress: 0d,
                PlaybackRate: _rate,
                new MediaProviderCapabilities(
                    CanPause: true,
                    CanSeek:
                        double.IsFinite(durationSeconds) &&
                        durationSeconds > 0d,
                    SupportsRate: true,
                    SupportsFrameStepping: true,
                    HardwareDecoded: true,
                    HasAudio: hasAudio,
                    HasVideo: hasVideo));
            PublishTracks(
                engine,
                width,
                height,
                timedText);
            _sink.Opened(in _snapshot);
            PublishDiagnostics(TransferDiagnostic);
            _opened.TrySetResult();

            RunPlaybackLoop(
                engine,
                timedText,
                d3dDevice,
                ref pool);
        }
        catch (OperationCanceledException)
        {
            _opened.TrySetCanceled();
        }
        catch (Exception exception)
        {
            bool wasOpened = _opened.Task.IsCompletedSuccessfully;
            if (!wasOpened)
            {
                _opened.TrySetException(exception);
            }
            if (wasOpened &&
                Volatile.Read(ref _disposed) == 0)
            {
                _sink.Failed(
                    MediaPlaybackFailure.Decode,
                    exception.Message,
                    exception);
            }
        }
        finally
        {
            pool?.Dispose();
            WindowsMediaNative.ClearTimedTextNotifications(
                timedText);
            timedTextNotification?.Dispose();
            WindowsMediaNative.Release(timedText);
            WindowsMediaNative.ShutdownMediaEngine(engine);
            WindowsMediaNative.Release(engine);
            WindowsMediaNative.Release(factory);
            WindowsMediaNative.Release(attributes);
            WindowsMediaNative.Release(dxgiManager);
            WindowsMediaNative.Release(d3dContext);
            WindowsMediaNative.Release(d3dDevice);
            notification?.Dispose();
            if (mediaFoundationStarted)
            {
                WindowsMediaNative.ShutdownMediaFoundation();
            }
            if (comInitialized)
            {
                WindowsMediaNative.UninitializeCom();
            }
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeStopSignal();
            }
        }
    }

    private void WaitForMetadata(nint engine)
    {
        SharedTexturePool? unusedPool = null;
        while (WindowsMediaNative.GetReadyState(engine) < 1)
        {
            ThrowIfStopping();
            ProcessCommands(engine);
            ProcessNativeEvents(engine, ref unusedPool);
            _stop.Wait(5);
        }
    }

    private static void DisableInitialTimedTextTracks(
        nint timedText)
    {
        if (timedText == 0)
        {
            return;
        }

        WindowsMediaNative.MediaEngineTimedTextTrackInfo[] tracks =
            WindowsMediaNative.GetTimedTextTracks(timedText);
        for (int index = 0; index < tracks.Length; index++)
        {
            WindowsMediaNative.MediaEngineTimedTextTrackInfo track =
                tracks[index];
            if (track.Active &&
                track.Kind is 1 or 2)
            {
                WindowsMediaNative.SelectTimedTextTrack(
                    timedText,
                    track.NativeId,
                    selected: false);
            }
        }
    }

    private void PublishTracks(
            nint engine,
            uint width,
            uint height,
            nint timedText = 0)
    {
        WindowsMediaNative.MediaEngineStreamInfo[] streams =
            WindowsMediaNative.GetStreams(engine);
        var audio =
            new List<MediaPlaybackTrackDescriptor>();
        var video =
            new List<MediaPlaybackTrackDescriptor>();
        var audioNative = new List<uint>();
        var videoNative = new List<uint>();
        var timedMetadata =
            new List<MediaPlaybackTrackDescriptor>();
        var timedMetadataNative = new List<uint>();
        var timedMetadataSelectable = new List<bool>();
        int selectedAudio = -1;
        int selectedVideo = -1;

        for (int streamIndex = 0;
             streamIndex < streams.Length;
             streamIndex++)
        {
            WindowsMediaNative.MediaEngineStreamInfo stream =
                streams[streamIndex];
            bool isAudio =
                stream.MajorType ==
                WindowsMediaNative.MediaTypeAudio;
            List<MediaPlaybackTrackDescriptor> destination =
                isAudio ? audio : video;
            List<uint> nativeDestination =
                isAudio ? audioNative : videoNative;
            int localIndex = destination.Count;
            if (stream.Selected)
            {
                if (isAudio && selectedAudio < 0)
                {
                    selectedAudio = localIndex;
                }
                else if (!isAudio && selectedVideo < 0)
                {
                    selectedVideo = localIndex;
                }
            }

            destination.Add(
                new MediaPlaybackTrackDescriptor(
                    $"mediafoundation:{stream.NativeIndex}",
                    isAudio
                        ? MediaPlaybackTrackKind.Audio
                        : MediaPlaybackTrackKind.Video,
                    isAudio
                        ? string.IsNullOrWhiteSpace(stream.Name)
                            ? $"Audio {localIndex + 1}"
                            : stream.Name
                        : string.IsNullOrWhiteSpace(stream.Name)
                            ? $"Video {localIndex + 1}"
                            : stream.Name,
                    stream.Name,
                    stream.Language,
                    new MediaPlaybackTrackEncoding(
                        stream.Subtype == Guid.Empty
                            ? string.Empty
                            : stream.Subtype.ToString("D"),
                        Width:
                            !isAudio && stream.Selected
                                ? width
                                : 0,
                        Height:
                            !isAudio && stream.Selected
                                ? height
                                : 0),
                    MediaPlaybackTrackSupport.Supported));
            nativeDestination.Add(stream.NativeIndex);
        }

        bool releaseTimedText = false;
        if (timedText == 0 &&
            WindowsMediaNative.TryGetTimedTextService(
                engine,
                out timedText))
        {
            releaseTimedText = true;
        }
        try
        {
            if (timedText != 0)
            {
                WindowsMediaNative.MediaEngineTimedTextTrackInfo[]
                    timedTracks =
                        WindowsMediaNative.GetTimedTextTracks(
                            timedText);
                for (int index = 0;
                     index < timedTracks.Length;
                     index++)
                {
                    WindowsMediaNative.MediaEngineTimedTextTrackInfo
                        track = timedTracks[index];
                    MediaPlaybackTimedMetadataKind kind =
                        ToTimedMetadataKind(track.Kind);
                    bool selectable =
                        track.Kind is 1 or 2;
                    string label =
                        string.IsNullOrWhiteSpace(track.Label)
                            ? $"Timed metadata {index + 1}"
                            : track.Label;
                    timedMetadata.Add(
                        new MediaPlaybackTrackDescriptor(
                            GetTimedTextProviderTrackId(
                                track.NativeId),
                            MediaPlaybackTrackKind
                                .TimedMetadata,
                            label,
                            track.Label,
                            track.Language,
                            new MediaPlaybackTrackEncoding(
                                track.DataFormat == Guid.Empty
                                    ? string.Empty
                                    : track.DataFormat.ToString(
                                        "D")),
                            selectable
                                ? MediaPlaybackTrackSupport
                                    .Supported
                                : MediaPlaybackTrackSupport
                                    .Unsupported,
                            kind,
                            track.DispatchType));
                    timedMetadataNative.Add(track.NativeId);
                    timedMetadataSelectable.Add(selectable);
                }
            }
        }
        finally
        {
            if (releaseTimedText)
            {
                WindowsMediaNative.Release(timedText);
            }
        }

        uint[] audioIndices = audioNative.ToArray();
        uint[] videoIndices = videoNative.ToArray();
        uint[] timedMetadataIds =
            timedMetadataNative.ToArray();
        bool[] selectableTimedMetadata =
            timedMetadataSelectable.ToArray();
        Volatile.Write(
            ref _audioStreamNativeIndices,
            audioIndices);
        Volatile.Write(
            ref _videoStreamNativeIndices,
            videoIndices);
        Volatile.Write(
            ref _timedTextTrackNativeIds,
            timedMetadataIds);
        Volatile.Write(
            ref _timedTextTrackSelectable,
            selectableTimedMetadata);
        _sink.UpdateTracks(
            new MediaPlaybackTracksSnapshot(
                audio,
                selectedAudio,
                video,
                selectedVideo,
                timedMetadata));
    }

    private void RunPlaybackLoop(
        nint engine,
        nint timedText,
        nint d3dDevice,
        ref SharedTexturePool? pool)
    {
        long nextSnapshot =
            Environment.TickCount64;
        while (!_stop.IsSet)
        {
            ProcessCommands(engine);
            ProcessNativeEvents(engine, ref pool, d3dDevice);
            ProcessTimedTextEvents(
                engine,
                timedText);
            PresentVideoFrame(engine, pool);

            long now = Environment.TickCount64;
            if (now >= nextSnapshot)
            {
                UpdateSnapshot(engine);
                nextSnapshot = now + 16;
            }
            _stop.Wait(2);
        }
    }

    private void ProcessCommands(nint engine)
    {
        while (_commands.TryDequeue(out Action<nint>? command))
        {
            command(engine);
        }
    }

    private void ProcessTimedTextEvents(
        nint engine,
        nint timedText)
    {
        if (Interlocked.Exchange(
                ref _timedTextTracksDirty,
                0) != 0)
        {
            PublishTracks(
                engine,
                _snapshot.NaturalVideoWidth,
                _snapshot.NaturalVideoHeight,
                timedText);
        }

        while (_timedTextErrors.TryDequeue(
                   out TimedTextErrorEvent error))
        {
            PublishDiagnostics(
                $"Media Foundation timed-text track {error.TrackId} " +
                $"reported error {error.ErrorCode} " +
                $"(0x{unchecked((uint)error.ExtendedErrorCode):x8}).");
        }

        while (_timedTextCueEvents.TryDequeue(
                   out TimedTextCueEvent cueEvent))
        {
            if (cueEvent.Event == TimedTextEventReset)
            {
                foreach (TimedTextTrackCueState resetState in
                         _timedTextCueStates.Values)
                {
                    _sink.UpdateTimedMetadataCues(
                        resetState.Reset());
                }
                _timedTextCueStates.Clear();
                continue;
            }
            if (cueEvent.Event is not (0 or 1))
            {
                continue;
            }
            if (cueEvent.Cue is not
                WindowsMediaNative.MediaEngineTimedTextCueInfo
                    cue)
            {
                continue;
            }
            if (cue.Kind is not (1 or 2))
            {
                continue;
            }

            if (!_timedTextCueStates.TryGetValue(
                    cue.TrackId,
                    out TimedTextTrackCueState? state))
            {
                state = new TimedTextTrackCueState(
                    GetTimedTextProviderTrackId(
                        cue.TrackId));
                _timedTextCueStates.Add(
                    cue.TrackId,
                    state);
            }

            _sink.UpdateTimedMetadataCues(
                state.Upsert(
                    new MediaPlaybackTimedMetadataCueDescriptor(
                        string.Concat(
                            GetTimedTextProviderTrackId(
                                cue.TrackId),
                            ":cue:",
                            cue.NativeId.ToString(
                                System.Globalization
                                    .CultureInfo
                                    .InvariantCulture)),
                        ToTimeSpan(cue.StartTime),
                        ToTimeSpan(cue.Duration),
                        cue.Text)));
        }
    }

    private void OnTimedTextTrackChanged(uint trackId)
    {
        _ = trackId;
        Interlocked.Exchange(
            ref _timedTextTracksDirty,
            1);
    }

    private void OnTimedTextError(
        int errorCode,
        int extendedErrorCode,
        uint sourceTrackId) =>
        _timedTextErrors.Enqueue(
            new TimedTextErrorEvent(
                errorCode,
                extendedErrorCode,
                sourceTrackId));

    private void OnTimedTextCue(
        int cueEvent,
        double currentTime,
        nint cue)
    {
        _ = currentTime;
        try
        {
            WindowsMediaNative.MediaEngineTimedTextCueInfo?
                cueInfo = cue == 0
                    ? null
                    : WindowsMediaNative.ReadTimedTextCue(
                        cue);
            _timedTextCueEvents.Enqueue(
                new TimedTextCueEvent(
                    cueEvent,
                    cueInfo));
        }
        catch (Exception exception)
        {
            _timedTextErrors.Enqueue(
                new TimedTextErrorEvent(
                    ErrorCode: 4,
                    exception.HResult,
                    TrackId: 0));
        }
    }

    private void OnTimedTextReset() =>
        _timedTextCueEvents.Enqueue(
            new TimedTextCueEvent(
                TimedTextEventReset,
                Cue: null));

    private void ProcessNativeEvents(
        nint engine,
        ref SharedTexturePool? pool,
        nint d3dDevice = 0)
    {
        int events = Interlocked.Exchange(ref _nativeEvents, 0);
        if ((events & FlagError) != 0)
        {
            throw new InvalidOperationException(
                $"Media Foundation reported native error 0x{Volatile.Read(ref _nativeError):x}.");
        }
        if ((events & FlagSeeked) != 0)
        {
            _sink.SeekCompleted(
                ToTimeSpan(
                    WindowsMediaNative.GetCurrentTime(engine)));
        }
        if ((events & FlagEnded) != 0 && !_looping)
        {
            _sink.Ended();
        }
        if ((events & FlagFormatChange) != 0 &&
            d3dDevice != 0 &&
            WindowsMediaNative.HasVideo(engine))
        {
            WindowsMediaNative.GetNativeVideoSize(
                engine,
                out uint width,
                out uint height);
            if (width != 0 &&
                height != 0 &&
                (pool is null ||
                 pool.Width != width ||
                 pool.Height != height))
            {
                var replacement = new SharedTexturePool(
                    d3dDevice,
                    width,
                    height,
                    FramePoolSize);
                SharedTexturePool? previous = pool;
                pool = replacement;
                previous?.Dispose();
                _snapshot = _snapshot with
                {
                    NaturalVideoWidth = width,
                    NaturalVideoHeight = height
                };
                PublishTracks(
                    engine,
                    width,
                    height);
            }
        }
    }

    private void PresentVideoFrame(
        nint engine,
        SharedTexturePool? pool)
    {
        if (pool is null ||
            !WindowsMediaNative.TryGetVideoTick(
                engine,
                out long presentationTime))
        {
            return;
        }
        if (!pool.TryRent(out SharedTextureSlotLease? lease) ||
            lease is null)
        {
            Interlocked.Increment(ref _droppedFrames);
            PublishDiagnostics(TransferDiagnostic);
            return;
        }

        try
        {
            if (!WindowsMediaNative.TryAcquireKeyedMutex(
                    lease.KeyedMutex,
                    timeoutMilliseconds: 0))
            {
                Interlocked.Increment(ref _droppedFrames);
                lease.Dispose();
                return;
            }
            try
            {
                WindowsMediaNative.TransferVideoFrame(
                    engine,
                    lease.Texture,
                    pool.Width,
                    pool.Height);
            }
            finally
            {
                WindowsMediaNative.ReleaseKeyedMutex(
                    lease.KeyedMutex);
            }

            var descriptor = new MediaGpuFrameDescriptor(
                Interlocked.Increment(ref _sequence),
                TimeSpan.FromTicks(presentationTime),
                TimeSpan.Zero,
                pool.Width,
                pool.Height,
                MediaVideoPixelFormat.Bgra8,
                MediaTransferMode.GpuCopy,
                new MediaColorInfo(
                    MediaColorPrimaries.Bt709,
                    MediaTransferFunction.Srgb,
                    MediaMatrixCoefficients.Identity,
                    FullRange: true));
            var externalDescriptor =
                new ProGpuExternalTextureDescriptor(
                    ProGpuExternalTextureHandleKind.DxgiSharedHandle,
                    lease.SharedHandle,
                    pool.Width,
                    pool.Height,
                    TextureFormat.Bgra8Unorm,
                    TextureUsage.TextureBinding,
                    GpuTextureAlphaMode.Straight,
                    IsInitialized: true)
                {
                    UsesKeyedMutex = true
                };
            _sink.Present(new ExternalMediaGpuFrame(
                in descriptor,
                in externalDescriptor,
                lease));
            lease = null;
        }
        catch
        {
            Interlocked.Increment(ref _droppedFrames);
            throw;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private void UpdateSnapshot(nint engine)
    {
        ushort ready = WindowsMediaNative.GetReadyState(engine);
        bool paused = WindowsMediaNative.IsPaused(engine);
        bool ended = WindowsMediaNative.IsEnded(engine);
        MediaEnginePlaybackState state =
            paused || ended
                ? MediaEnginePlaybackState.Paused
                : ready < 3
                    ? MediaEnginePlaybackState.Buffering
                    : MediaEnginePlaybackState.Playing;
        _snapshot = _snapshot with
        {
            State = state,
            Position = ToTimeSpan(
                WindowsMediaNative.GetCurrentTime(engine)),
            BufferingProgress =
                ready >= 3
                    ? 1d
                    : Math.Clamp(ready / 3d, 0d, 1d),
            PlaybackRate =
                WindowsMediaNative.GetPlaybackRate(engine)
        };
        _sink.Update(in _snapshot);
    }

    private void ApplyInitialSettings(nint engine)
    {
        WindowsMediaNative.SetPlaybackRate(engine, _rate);
        ApplyAudioGraph(engine);
        WindowsMediaNative.SetLoop(engine, _looping);
    }

    private void ApplyAudioGraph(nint engine)
    {
        MediaAudioStereoLevels levels =
            GetCombinedAudioLevels();
        double effectiveVolume =
            Math.Clamp(
                _volume *
                    Math.Min(1d, levels.Peak),
                0d,
                1d);
        WindowsMediaNative.SetVolume(
            engine,
            effectiveVolume);
        WindowsMediaNative.SetMuted(
            engine,
            _muted);
        WindowsMediaNative.SetBalance(
            engine,
            levels.Balance);
        if (levels.Peak > 1f)
        {
            PublishDiagnostics(
                "The Media Engine volume stage is bounded to unity; gain above 1.0 requires the registered IMFTransform processing lane.");
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
        Enqueue(ApplyAudioGraph);

    private void OnNativeEvent(
        uint eventCode,
        nuint parameter1,
        uint parameter2)
    {
        _ = parameter2;
        int flag = eventCode switch
        {
            EventLoadedMetadata => FlagMetadata,
            EventSeeked => FlagSeeked,
            EventEnded => FlagEnded,
            EventFormatChange => FlagFormatChange,
            EventError or EventStreamRenderingError => FlagError,
            _ => 0
        };
        if (flag == FlagError)
        {
            Volatile.Write(ref _nativeError, parameter1);
        }
        if (flag != 0)
        {
            Interlocked.Or(ref _nativeEvents, flag);
        }
    }

    private void Enqueue(Action<nint> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();
        _commands.Enqueue(command);
    }

    private void CancelOpen()
    {
        _stop.Set();
        _opened.TrySetCanceled();
    }

    private void ThrowIfStopping()
    {
        if (_stop.IsSet)
        {
            throw new OperationCanceledException();
        }
    }

    private void PublishDiagnostics(string? fallbackReason)
    {
        _sink.UpdateDiagnostics(
            new MediaProviderDiagnostics(
                HardwareDecoded: true,
                TransferMode: MediaTransferMode.GpuCopy,
                DroppedFrames:
                    Interlocked.Read(ref _droppedFrames),
                VideoQueueDepth: FramePoolSize,
                AudioQueueDepth: 0,
                AudioLatency: TimeSpan.Zero,
                LastFallbackReason: fallbackReason));
    }

    private static TimeSpan ToTimeSpan(double seconds) =>
        double.IsFinite(seconds) &&
        seconds > 0d &&
        seconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    private static string GetTimedTextProviderTrackId(
        uint trackId) =>
        string.Concat(
            "mediafoundation-timed:",
            trackId.ToString(
                System.Globalization.CultureInfo
                    .InvariantCulture));

    private static MediaPlaybackTimedMetadataKind
        ToTimedMetadataKind(int kind) =>
        kind switch
        {
            1 => MediaPlaybackTimedMetadataKind.Subtitle,
            2 => MediaPlaybackTimedMetadataKind.Caption,
            3 => MediaPlaybackTimedMetadataKind.Data,
            _ => MediaPlaybackTimedMetadataKind.Custom
        };

    private void DisposeStopSignal()
    {
        if (Interlocked.Exchange(ref _stopDisposed, 1) == 0)
        {
            _stop.Dispose();
        }
    }

    private readonly record struct TimedTextCueEvent(
        int Event,
        WindowsMediaNative.MediaEngineTimedTextCueInfo? Cue);

    private readonly record struct TimedTextErrorEvent(
        int ErrorCode,
        int ExtendedErrorCode,
        uint TrackId);

    private sealed class TimedTextTrackCueState
    {
        private readonly string _providerTrackId;
        private readonly List<
            MediaPlaybackTimedMetadataCueDescriptor> _cues = [];
        private readonly Dictionary<string, int> _indices =
            new(StringComparer.Ordinal);

        internal TimedTextTrackCueState(
            string providerTrackId)
        {
            _providerTrackId = providerTrackId;
        }

        internal MediaPlaybackTimedMetadataCueSnapshot Upsert(
            in MediaPlaybackTimedMetadataCueDescriptor cue)
        {
            if (_indices.TryGetValue(
                    cue.CueId,
                    out int index))
            {
                _cues[index] = cue;
            }
            else
            {
                _indices.Add(cue.CueId, _cues.Count);
                _cues.Add(cue);
            }

            return new MediaPlaybackTimedMetadataCueSnapshot(
                _providerTrackId,
                _cues);
        }

        internal MediaPlaybackTimedMetadataCueSnapshot Reset()
        {
            _cues.Clear();
            _indices.Clear();
            return new MediaPlaybackTimedMetadataCueSnapshot(
                _providerTrackId,
                cues: null);
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

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}

/// <summary>
/// Fixed-size D3D11 texture ring. Disposal is deferred until every frame owner
/// has ended Dawn access, so shared HANDLEs and keyed mutexes cannot be reused
/// or destroyed while a WebGPU submission still references them.
/// </summary>
internal sealed class SharedTexturePool : IDisposable
{
    private readonly object _gate = new();
    private readonly SharedTextureSlot[] _slots;
    private int _active;
    private bool _disposeRequested;
    private bool _resourcesReleased;

    internal SharedTexturePool(
        nint d3dDevice,
        uint width,
        uint height,
        int capacity)
    {
        Width = width;
        Height = height;
        _slots = new SharedTextureSlot[capacity];
        try
        {
            for (int index = 0; index < capacity; index++)
            {
                nint texture =
                    WindowsMediaNative.CreateSharedVideoTexture(
                        d3dDevice,
                        width,
                        height,
                        out nint sharedHandle,
                        out nint keyedMutex);
                _slots[index] = new SharedTextureSlot(
                    texture,
                    sharedHandle,
                    keyedMutex);
            }
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    internal uint Width { get; }
    internal uint Height { get; }

    internal bool TryRent(out SharedTextureSlotLease? lease)
    {
        lock (_gate)
        {
            if (_disposeRequested)
            {
                lease = null;
                return false;
            }
            for (int index = 0; index < _slots.Length; index++)
            {
                SharedTextureSlot? slot = _slots[index];
                if (slot is not null && !slot.Busy)
                {
                    slot.Busy = true;
                    _active++;
                    lease =
                        new SharedTextureSlotLease(this, slot);
                    return true;
                }
            }
        }
        lease = null;
        return false;
    }

    internal void Return(SharedTextureSlot slot)
    {
        bool release;
        lock (_gate)
        {
            if (!slot.Busy)
            {
                return;
            }
            slot.Busy = false;
            _active--;
            release = _disposeRequested && _active == 0;
        }
        if (release)
        {
            ReleaseResources();
        }
    }

    public void Dispose()
    {
        bool release;
        lock (_gate)
        {
            _disposeRequested = true;
            release = _active == 0;
        }
        if (release)
        {
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        lock (_gate)
        {
            if (_resourcesReleased)
            {
                return;
            }
            _resourcesReleased = true;
            for (int index = 0; index < _slots.Length; index++)
            {
                SharedTextureSlot? slot = _slots[index];
                if (slot is null)
                {
                    continue;
                }
                WindowsMediaNative.Release(slot.KeyedMutex);
                WindowsMediaNative.CloseSharedHandle(
                    slot.SharedHandle);
                WindowsMediaNative.Release(slot.Texture);
            }
        }
    }
}

internal sealed class SharedTextureSlot
{
    internal SharedTextureSlot(
        nint texture,
        nint sharedHandle,
        nint keyedMutex)
    {
        Texture = texture;
        SharedHandle = sharedHandle;
        KeyedMutex = keyedMutex;
    }

    internal nint Texture { get; }
    internal nint SharedHandle { get; }
    internal nint KeyedMutex { get; }
    internal bool Busy { get; set; }
}

internal sealed class SharedTextureSlotLease : IDisposable
{
    private SharedTexturePool? _pool;
    private readonly SharedTextureSlot _slot;

    internal SharedTextureSlotLease(
        SharedTexturePool pool,
        SharedTextureSlot slot)
    {
        _pool = pool;
        _slot = slot;
    }

    internal nint Texture => _slot.Texture;
    internal nint SharedHandle => _slot.SharedHandle;
    internal nint KeyedMutex => _slot.KeyedMutex;

    public void Dispose()
    {
        SharedTexturePool? pool =
            Interlocked.Exchange(ref _pool, null);
        pool?.Return(_slot);
    }
}
