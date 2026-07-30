using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Media;
using ProGPU.Media.Audio;
using ProGPU.Media.Diagnostics;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;
using Silk.NET.WebGPU;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ProGPU.Browser;

public static class BrowserMedia
{
    public static IDisposable Register(
        MediaProviderRegistry? registry = null,
        int priority = 100)
    {
        IDisposable playback =
            (registry ?? MediaProviderRegistry.Default).Register(
                new BrowserMediaPlaybackProviderFactory(priority));
        IDisposable export =
            MediaCompositionExportRegistry.Default.Register(
                new BrowserWebGpuMediaCompositionExportProvider(
                    priority));
        IDisposable thumbnails =
            MediaCompositionThumbnailRegistry.Default.Register(
                new BrowserWebGpuMediaCompositionThumbnailProvider(
                    priority));
        IDisposable fastExport =
            MediaCompositionExportRegistry.Default.Register(
                new BrowserFastMediaCompositionExportProvider(
                    priority == int.MinValue
                        ? int.MinValue
                        : priority - 1));
        return new BrowserMediaRegistrations(
            playback,
            export,
            thumbnails,
            fastExport);
    }

    private sealed class BrowserMediaRegistrations :
        IDisposable
    {
        private IDisposable? _playback;
        private IDisposable? _export;
        private IDisposable? _thumbnails;
        private IDisposable? _fastExport;

        public BrowserMediaRegistrations(
            IDisposable playback,
            IDisposable export,
            IDisposable thumbnails,
            IDisposable fastExport)
        {
            _playback = playback;
            _export = export;
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
                ref _export,
                null)?.Dispose();
            Interlocked.Exchange(
                ref _playback,
                null)?.Dispose();
        }
    }
}

public sealed class BrowserMediaPlaybackProviderFactory :
    IMediaPlaybackProviderFactory
{
    public BrowserMediaPlaybackProviderFactory(int priority = 100)
    {
        Priority = priority;
    }

    public string Id => "progpu.browser.html-media";
    public int Priority { get; }

    public bool CanOpen(MediaSourceDescriptor source) =>
        OperatingSystem.IsBrowser() &&
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
                "The browser provider accepts URI media sources only.");
        }

        return ValueTask.FromResult<IMediaPlaybackProvider>(
            new BrowserMediaPlaybackProvider(
                source.Uri!,
                sink));
    }
}

internal sealed partial class BrowserMediaPlaybackProvider :
    IMediaPlaybackProvider,
    IMediaPlaybackTimedMetadataProvider
{
    private static int s_nextId;
    private readonly object _gate = new();
    private readonly Uri _uri;
    private readonly IMediaPlaybackSink _sink;
    private readonly int _id;
    private readonly List<AudioEffectBinding>
        _audioEffects = [];
    private SharedGpuTextureSource? _textureSource;
    private WgpuContext? _textureContext;
    private uint _textureWidth;
    private uint _textureHeight;
    private long _lastCopiedSequence = -1;
    private long _sequence;
    private double _playbackRate = 1d;
    private double _volume = 1d;
    private double _balance;
    private double _pendingSeekSeconds;
    private bool _hasPendingSeek;
    private bool _muted;
    private bool _looping;
    private MediaPlaybackSnapshot _snapshot =
        MediaPlaybackSnapshot.Empty;
    private MediaPlaybackTracksSnapshot _tracks =
        MediaPlaybackTracksSnapshot.Empty;
    private int _opened;
    private int _nextAudioEffectId;
    private int _disposed;

    public BrowserMediaPlaybackProvider(
        Uri uri,
        IMediaPlaybackSink sink)
    {
        _uri = uri;
        _sink = sink;
        _id = Interlocked.Increment(ref s_nextId);
    }

    public string Id => "progpu.browser.html-media";

    public async ValueTask OpenAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        BrowserMediaCallbacks.Register(_id, this);
        try
        {
            string metadataJson = await CreateCoreAsync(
                    _id,
                    _uri.AbsoluteUri)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document =
                JsonDocument.Parse(metadataJson);
            JsonElement root = document.RootElement;
            uint width = checked((uint)root
                .GetProperty("width")
                .GetInt32());
            uint height = checked((uint)root
                .GetProperty("height")
                .GetInt32());
            double durationSeconds = root
                .GetProperty("duration")
                .GetDouble();
            var capabilities = new MediaProviderCapabilities(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: false,
                HardwareDecoded: false,
                HasAudio: true,
                HasVideo: width != 0 && height != 0);
            _snapshot = new MediaPlaybackSnapshot(
                MediaEnginePlaybackState.Paused,
                TimeSpan.Zero,
                Seconds(durationSeconds),
                width,
                height,
                BufferingProgress: 0d,
                DownloadProgress: 0d,
                PlaybackRate: 1d,
                capabilities);
            lock (_gate)
            {
                Volatile.Write(ref _opened, 1);
                SetRateCore(_id, _playbackRate);
                SetLoopingCore(_id, _looping);
                SetAudioCore(
                    _id,
                    _volume,
                    _balance,
                    _muted);
                for (int index = 0;
                     index < _audioEffects.Count;
                     index++)
                {
                    ConfigureAudioEffect(
                        _audioEffects[index]);
                }
                if (_hasPendingSeek)
                {
                    SeekCore(_id, _pendingSeekSeconds);
                    _hasPendingSeek = false;
                }
            }
            IReadOnlyList<
                MediaPlaybackTimedMetadataCueSnapshot>
                timedMetadataCues;
            _tracks = CreateTrackSnapshot(
                root,
                capabilities.HasAudio,
                capabilities.HasVideo,
                width,
                height,
                out timedMetadataCues);
            _sink.UpdateTracks(_tracks);
            PublishTimedMetadataCues(timedMetadataCues);
            _sink.Opened(in _snapshot);
            _sink.UpdateDiagnostics(
                new MediaProviderDiagnostics(
                    HardwareDecoded: false,
                    TransferMode: MediaTransferMode.GpuCopy,
                    DroppedFrames: 0,
                    VideoQueueDepth: 1,
                    AudioQueueDepth: 0,
                    AudioLatency: TimeSpan.Zero,
                    LastFallbackReason:
                        "Browser WebGPU imports video through copyExternalImageToTexture; GPUExternalTexture is not exposed by the portable wgpu texture contract."));
        }
        catch
        {
            Volatile.Write(ref _opened, 0);
            BrowserMediaCallbacks.Unregister(_id, this);
            DisposeCore(_id);
            throw;
        }
    }

    public void Play()
    {
        if (Volatile.Read(ref _opened) != 0)
        {
            PlayCore(_id);
        }
    }

    public void Pause()
    {
        if (Volatile.Read(ref _opened) != 0)
        {
            PauseCore(_id);
        }
    }

    public void Seek(TimeSpan position)
    {
        double seconds = Math.Max(0d, position.TotalSeconds);
        lock (_gate)
        {
            if (Volatile.Read(ref _opened) == 0)
            {
                _pendingSeekSeconds = seconds;
                _hasPendingSeek = true;
                return;
            }
            SeekCore(_id, seconds);
        }
    }

    public void SetPlaybackRate(double value)
    {
        lock (_gate)
        {
            _playbackRate = value;
            if (Volatile.Read(ref _opened) != 0)
            {
                SetRateCore(_id, value);
            }
        }
    }

    public void SetVolume(
        double volume,
        double balance,
        bool muted)
    {
        lock (_gate)
        {
            _volume = volume;
            _balance = balance;
            _muted = muted;
            if (Volatile.Read(ref _opened) != 0)
            {
                SetAudioCore(
                    _id,
                    volume,
                    balance,
                    muted);
            }
        }
    }

    public void SetLooping(bool enabled)
    {
        lock (_gate)
        {
            _looping = enabled;
            if (Volatile.Read(ref _opened) != 0)
            {
                SetLoopingCore(_id, enabled);
            }
        }
    }

    public bool StepForwardOneFrame() => false;
    public bool StepBackwardOneFrame() => false;

    public bool TrySetTimedMetadataPresentationMode(
        int index,
        MediaPlaybackTimedMetadataPresentationMode mode)
    {
        if ((uint)index >=
                (uint)_tracks.TimedMetadataTracks.Count ||
            !Enum.IsDefined(mode) ||
            Volatile.Read(ref _opened) == 0 ||
            Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        string browserMode = mode switch
        {
            MediaPlaybackTimedMetadataPresentationMode
                .Disabled => "disabled",
            MediaPlaybackTimedMetadataPresentationMode
                .PlatformPresented => "showing",
            _ => "hidden"
        };
        string metadataJson =
            SetTimedMetadataModeCore(
                _id,
                index,
                browserMode);
        ApplyBrowserTimedMetadata(metadataJson);
        return true;
    }

    public void AddEffect(IMediaEffect effect, bool optional)
    {
        ArgumentNullException.ThrowIfNull(effect);
        IMediaAudioGraphEffect? graphBindingEffect =
            null;
        IBrowserAudioWorkletEffect?
            workletBindingEffect = null;
        if (effect is IMediaAudioGraphEffect graphEffect)
        {
            MediaAudioGraphEffectState state;
            try
            {
                state = graphEffect.CaptureState();
            }
            catch when (optional)
            {
                return;
            }
            if (state.Kind is not (
                    MediaAudioGraphEffectKind.Gain or
                    MediaAudioGraphEffectKind
                        .StereoBalance))
            {
                if (!optional)
                {
                    throw new NotSupportedException(
                        $"Browser WebAudio does not support the audio graph effect kind '{state.Kind}'.");
                }
                return;
            }
            graphBindingEffect = graphEffect;
        }
        else if (effect is
                 IBrowserAudioWorkletEffect
                     workletEffect &&
                 effect.Kind ==
                     MediaEffectKind.Audio)
        {
            try
            {
                BrowserAudioWorkletEffectState
                    captured =
                        workletEffect.CaptureState();
                _ = new BrowserAudioWorkletEffectState(
                    captured.ModuleUri,
                    captured.ProcessorName,
                    captured.NodeOptionsJson);
            }
            catch when (optional)
            {
                return;
            }
            workletBindingEffect = workletEffect;
        }
        else
        {
            if (!optional)
            {
                throw new NotSupportedException(
                    "The browser provider accepts typed native Web Audio nodes or IBrowserAudioWorkletEffect modules. Arbitrary managed PCM callbacks cannot execute in the browser audio rendering realm.");
            }
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            var binding = graphBindingEffect is not null
                ? new AudioEffectBinding(
                    checked(++_nextAudioEffectId),
                    graphBindingEffect,
                    optional,
                    OnAudioEffectStateChanged)
                : new AudioEffectBinding(
                    checked(++_nextAudioEffectId),
                    workletBindingEffect!,
                    optional,
                    OnAudioEffectStateChanged);
            _audioEffects.Add(binding);
            if (Volatile.Read(ref _opened) != 0)
            {
                ConfigureAudioEffect(binding);
            }
        }
    }

    public void RemoveAllEffects()
    {
        AudioEffectBinding[] bindings;
        lock (_gate)
        {
            bindings = [.. _audioEffects];
            _audioEffects.Clear();
            if (Volatile.Read(ref _opened) != 0)
            {
                RemoveAllAudioEffectsCore(_id);
            }
        }
        for (int index = 0;
             index < bindings.Length;
             index++)
        {
            bindings[index].Dispose();
        }
    }

    internal void OnBrowserEvent(
        int kind,
        double positionSeconds,
        double durationSeconds,
        int width,
        int height,
        double progress,
        string message)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _opened) == 0)
        {
            return;
        }

        MediaPlaybackSnapshot current = _snapshot;
        var state = kind switch
        {
            2 => MediaEnginePlaybackState.Playing,
            3 => MediaEnginePlaybackState.Paused,
            4 => MediaEnginePlaybackState.Buffering,
            _ => current.State
        };
        _snapshot = current with
        {
            State = state,
            Position = Seconds(positionSeconds),
            NaturalDuration = Seconds(durationSeconds),
            NaturalVideoWidth = width > 0
                ? (uint)width
                : current.NaturalVideoWidth,
            NaturalVideoHeight = height > 0
                ? (uint)height
                : current.NaturalVideoHeight,
            BufferingProgress = kind == 4 ? 0d : 1d,
            DownloadProgress = progress
        };

        if (kind == 1 && width > 0 && height > 0)
        {
            long sequence = Interlocked.Increment(ref _sequence);
            _sink.Present(new BrowserMediaGpuFrame(
                this,
                new MediaGpuFrameDescriptor(
                    sequence,
                    Seconds(positionSeconds),
                    TimeSpan.Zero,
                    (uint)width,
                    (uint)height,
                    MediaVideoPixelFormat.Rgba8,
                    MediaTransferMode.GpuCopy,
                    new MediaColorInfo(
                        MediaColorPrimaries.Bt709,
                        MediaTransferFunction.Srgb,
                        MediaMatrixCoefficients.Identity,
                        FullRange: true))));
        }
        else if (kind == 5)
        {
            _sink.Ended();
        }
        else if (kind == 8)
        {
            _sink.Failed(
                MediaPlaybackFailure.Decode,
                string.IsNullOrWhiteSpace(message)
                    ? "Browser media playback failed."
                    : message);
            return;
        }
        else if (kind == 9)
        {
            _sink.SeekCompleted(
                Seconds(positionSeconds));
        }

        _sink.Update(in _snapshot);
    }

    internal void OnBrowserTimedMetadata(string metadataJson)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _opened) == 0)
        {
            return;
        }
        ApplyBrowserTimedMetadata(metadataJson);
    }

    internal unsafe bool TryGetTexture(
        in MediaGpuFrameDescriptor descriptor,
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            requiredContext.Api is not BrowserWebGpuApi browserApi)
        {
            texture = null!;
            return false;
        }

        lock (_gate)
        {
            if (_textureSource is null ||
                !ReferenceEquals(_textureContext, requiredContext) ||
                _textureWidth != descriptor.Width ||
                _textureHeight != descriptor.Height)
            {
                _textureSource?.Dispose();
                texture = new GpuTexture(
                    requiredContext,
                    descriptor.Width,
                    descriptor.Height,
                    TextureFormat.Rgba8Unorm,
                    TextureUsage.TextureBinding |
                    TextureUsage.CopyDst |
                    TextureUsage.RenderAttachment,
                    "Browser decoded media frame",
                    alphaMode: GpuTextureAlphaMode.Straight);
                _textureSource =
                    new SharedGpuTextureSource(texture);
                _textureContext = requiredContext;
                _textureWidth = descriptor.Width;
                _textureHeight = descriptor.Height;
                _lastCopiedSequence = -1;
            }
            else if (!_textureSource.TryGetGpuTexture(out texture))
            {
                return false;
            }

            if (_lastCopiedSequence != descriptor.Sequence)
            {
                if (!CopyFrameCore(
                        _id,
                        checked((int)descriptor.Width),
                        checked((int)descriptor.Height)))
                {
                    return false;
                }
                browserApi.CopyExternalMediaFrame(
                    _id,
                    texture.TexturePtr,
                    descriptor.Width,
                    descriptor.Height);
                texture.NotifyExternalContentChanged();
                _lastCopiedSequence = descriptor.Sequence;
            }
            return true;
        }
    }

    internal bool TryAcquireTexture(
        in MediaGpuFrameDescriptor descriptor,
        WgpuContext requiredContext,
        out IProGpuTextureLease lease)
    {
        lock (_gate)
        {
            if (!TryGetTexture(
                    in descriptor,
                    requiredContext,
                    out _) ||
                _textureSource is null)
            {
                lease = null!;
                return false;
            }
            return _textureSource.TryAcquireGpuTextureLease(
                out lease);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Volatile.Write(ref _opened, 0);
        BrowserMediaCallbacks.Unregister(_id, this);
        if (OperatingSystem.IsBrowser())
        {
            DisposeCore(_id);
        }
        lock (_gate)
        {
            for (int index = 0;
                 index < _audioEffects.Count;
                 index++)
            {
                _audioEffects[index].Dispose();
            }
            _audioEffects.Clear();
            _textureSource?.Dispose();
            _textureSource = null;
            _textureContext = null;
        }
    }

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && value > 0d
            ? TimeSpan.FromSeconds(value)
            : TimeSpan.Zero;

    private static MediaPlaybackTracksSnapshot
        CreateTrackSnapshot(
            JsonElement root,
            bool hasAudio,
            bool hasVideo,
            uint width,
            uint height,
            out IReadOnlyList<
                MediaPlaybackTimedMetadataCueSnapshot>
                timedMetadataCues)
    {
        MediaPlaybackTrackDescriptor[] audio = hasAudio
            ?
            [
                new MediaPlaybackTrackDescriptor(
                    "htmlmedia:audio:0",
                    MediaPlaybackTrackKind.Audio,
                    "Audio 1",
                    string.Empty,
                    string.Empty,
                    MediaPlaybackTrackEncoding.Empty,
                    MediaPlaybackTrackSupport.Unknown)
            ]
            : [];
        MediaPlaybackTrackDescriptor[] video = hasVideo
            ?
            [
                new MediaPlaybackTrackDescriptor(
                    "htmlmedia:video:0",
                    MediaPlaybackTrackKind.Video,
                    "Video 1",
                    string.Empty,
                    string.Empty,
                    new MediaPlaybackTrackEncoding(
                        string.Empty,
                        Width: width,
                        Height: height),
                    MediaPlaybackTrackSupport.Unknown)
            ]
            : [];
        ParseTimedMetadataTracks(
            root,
            out MediaPlaybackTrackDescriptor[] metadata,
            out MediaPlaybackTimedMetadataCueSnapshot[]
                cueSnapshots);
        timedMetadataCues = cueSnapshots;
        return new MediaPlaybackTracksSnapshot(
            audio,
            hasAudio ? 0 : -1,
            video,
            hasVideo ? 0 : -1,
            metadata);
    }

    private void ApplyBrowserTimedMetadata(string metadataJson)
    {
        using JsonDocument document =
            JsonDocument.Parse(metadataJson);
        ParseTimedMetadataTracks(
            document.RootElement,
            out MediaPlaybackTrackDescriptor[] metadata,
            out MediaPlaybackTimedMetadataCueSnapshot[] cues);
        _tracks = new MediaPlaybackTracksSnapshot(
            _tracks.AudioTracks,
            _tracks.SelectedAudioTrackIndex,
            _tracks.VideoTracks,
            _tracks.SelectedVideoTrackIndex,
            metadata);
        _sink.UpdateTracks(_tracks);
        PublishTimedMetadataCues(cues);
    }

    private void PublishTimedMetadataCues(
        IReadOnlyList<
            MediaPlaybackTimedMetadataCueSnapshot> snapshots)
    {
        for (int index = 0;
             index < snapshots.Count;
             index++)
        {
            _sink.UpdateTimedMetadataCues(snapshots[index]);
        }
    }

    private static void ParseTimedMetadataTracks(
        JsonElement root,
        out MediaPlaybackTrackDescriptor[] tracks,
        out MediaPlaybackTimedMetadataCueSnapshot[] cues)
    {
        if (!root.TryGetProperty(
                "textTracks",
                out JsonElement textTracks) ||
            textTracks.ValueKind != JsonValueKind.Array)
        {
            tracks = [];
            cues = [];
            return;
        }

        int count = textTracks.GetArrayLength();
        tracks = new MediaPlaybackTrackDescriptor[count];
        cues =
            new MediaPlaybackTimedMetadataCueSnapshot[count];
        int trackIndex = 0;
        foreach (JsonElement track in textTracks
                     .EnumerateArray())
        {
            string providerTrackId =
                GetString(track, "providerTrackId");
            string kind = GetString(track, "kind");
            string label = GetString(track, "label");
            string language = GetString(track, "language");
            tracks[trackIndex] =
                new MediaPlaybackTrackDescriptor(
                    providerTrackId,
                    MediaPlaybackTrackKind.TimedMetadata,
                    string.IsNullOrEmpty(label)
                        ? $"Text track {trackIndex + 1}"
                        : label,
                    label,
                    language,
                    new MediaPlaybackTrackEncoding(
                        "WebVTT"),
                    MediaPlaybackTrackSupport.Supported,
                    ToTimedMetadataKind(kind),
                    "text/vtt");

            JsonElement cueArray;
            if (!track.TryGetProperty(
                    "cues",
                    out cueArray) ||
                cueArray.ValueKind != JsonValueKind.Array)
            {
                cues[trackIndex] =
                    new MediaPlaybackTimedMetadataCueSnapshot(
                        providerTrackId,
                        []);
                trackIndex++;
                continue;
            }

            var descriptors =
                new MediaPlaybackTimedMetadataCueDescriptor[
                    cueArray.GetArrayLength()];
            int cueIndex = 0;
            foreach (JsonElement cue in cueArray
                         .EnumerateArray())
            {
                descriptors[cueIndex++] =
                    new
                        MediaPlaybackTimedMetadataCueDescriptor(
                            GetString(cue, "id"),
                            Seconds(
                                GetDouble(
                                    cue,
                                    "startTime")),
                            Seconds(
                                GetDouble(
                                    cue,
                                    "duration")),
                            GetString(cue, "text"));
            }
            cues[trackIndex] =
                new MediaPlaybackTimedMetadataCueSnapshot(
                    providerTrackId,
                    descriptors);
            trackIndex++;
        }
    }

    private static string GetString(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double GetDouble(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0d;

    private static MediaPlaybackTimedMetadataKind
        ToTimedMetadataKind(string kind) =>
        kind switch
        {
            "captions" =>
                MediaPlaybackTimedMetadataKind.Caption,
            "chapters" =>
                MediaPlaybackTimedMetadataKind.Chapter,
            "descriptions" =>
                MediaPlaybackTimedMetadataKind.Description,
            "metadata" =>
                MediaPlaybackTimedMetadataKind.Data,
            "subtitles" =>
                MediaPlaybackTimedMetadataKind.Subtitle,
            _ => MediaPlaybackTimedMetadataKind.Custom
        };

    private void OnAudioEffectStateChanged(
        AudioEffectBinding binding)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _opened) == 0 ||
                !_audioEffects.Contains(binding))
            {
                return;
            }
            ConfigureAudioEffect(binding);
        }
    }

    private void ConfigureAudioEffect(
        AudioEffectBinding binding)
    {
        if (binding.GraphEffect is { } graphEffect)
        {
            MediaAudioGraphEffectState state =
                graphEffect.CaptureState();
            ConfigureAudioEffectCore(
                _id,
                binding.Id,
                (int)state.Kind,
                state.Parameter0,
                state.Parameter1,
                state.Parameter2,
                state.Parameter3);
        }
        else
        {
            BrowserAudioWorkletEffectState state =
                binding.WorkletEffect!
                    .CaptureState();
            ConfigureAudioWorkletEffectCore(
                _id,
                binding.Id,
                state.ModuleUri,
                state.ProcessorName,
                state.NodeOptionsJson,
                binding.Optional);
        }
    }

    [JSImport("createBrowserMedia", "progpu-browser")]
    private static partial Task<string> CreateCoreAsync(
        int id,
        string uri);

    [JSImport("playBrowserMedia", "progpu-browser")]
    private static partial void PlayCore(int id);

    [JSImport("pauseBrowserMedia", "progpu-browser")]
    private static partial void PauseCore(int id);

    [JSImport("seekBrowserMedia", "progpu-browser")]
    private static partial void SeekCore(int id, double seconds);

    [JSImport("setBrowserMediaRate", "progpu-browser")]
    private static partial void SetRateCore(int id, double rate);

    [JSImport("setBrowserMediaLooping", "progpu-browser")]
    private static partial void SetLoopingCore(
        int id,
        bool looping);

    [JSImport("setBrowserMediaAudio", "progpu-browser")]
    private static partial void SetAudioCore(
        int id,
        double volume,
        double balance,
        bool muted);

    [JSImport(
        "setBrowserMediaTimedMetadataMode",
        "progpu-browser")]
    private static partial string
        SetTimedMetadataModeCore(
            int id,
            int index,
            string mode);

    [JSImport(
        "configureBrowserMediaAudioEffect",
        "progpu-browser")]
    private static partial void ConfigureAudioEffectCore(
        int id,
        int effectId,
        int kind,
        double parameter0,
        double parameter1,
        double parameter2,
        double parameter3);

    [JSImport(
        "configureBrowserMediaAudioWorkletEffect",
        "progpu-browser")]
    private static partial void
        ConfigureAudioWorkletEffectCore(
            int id,
            int effectId,
            string moduleUri,
            string processorName,
            string nodeOptionsJson,
            bool optional);

    [JSImport(
        "removeAllBrowserMediaAudioEffects",
        "progpu-browser")]
    private static partial void RemoveAllAudioEffectsCore(
        int id);

    [JSImport("copyBrowserMediaFrame", "progpu-browser")]
    private static partial bool CopyFrameCore(
        int id,
        int width,
        int height);

    [JSImport("disposeBrowserMedia", "progpu-browser")]
    private static partial void DisposeCore(int id);

    private sealed class AudioEffectBinding :
        IDisposable
    {
        private readonly Action _changed;

        public AudioEffectBinding(
            int id,
            IMediaAudioGraphEffect effect,
            bool optional,
            Action<AudioEffectBinding> changed)
        {
            Id = id;
            GraphEffect = effect;
            Optional = optional;
            _changed = () => changed(this);
            GraphEffect.StateChanged += _changed;
        }

        public AudioEffectBinding(
            int id,
            IBrowserAudioWorkletEffect effect,
            bool optional,
            Action<AudioEffectBinding> changed)
        {
            Id = id;
            WorkletEffect = effect;
            Optional = optional;
            _changed = () => changed(this);
            WorkletEffect.StateChanged += _changed;
        }

        public int Id { get; }

        public bool Optional { get; }

        public IMediaAudioGraphEffect? GraphEffect
        { get; }

        public IBrowserAudioWorkletEffect?
            WorkletEffect { get; }

        public void Dispose()
        {
            if (GraphEffect is not null)
            {
                GraphEffect.StateChanged -= _changed;
            }
            if (WorkletEffect is not null)
            {
                WorkletEffect.StateChanged -= _changed;
            }
        }
    }
}

public static partial class BrowserMediaCallbacks
{
    private static readonly ConcurrentDictionary<
        int,
        WeakReference<BrowserMediaPlaybackProvider>> s_providers =
        new();

    internal static void Register(
        int id,
        BrowserMediaPlaybackProvider provider) =>
        s_providers[id] =
            new WeakReference<BrowserMediaPlaybackProvider>(provider);

    internal static void Unregister(
        int id,
        BrowserMediaPlaybackProvider provider)
    {
        if (s_providers.TryGetValue(id, out var reference) &&
            reference.TryGetTarget(out var current) &&
            ReferenceEquals(current, provider))
        {
            s_providers.TryRemove(id, out _);
        }
    }

    [JSExport]
    public static void DispatchEvent(
        int id,
        int kind,
        double positionSeconds,
        double durationSeconds,
        int width,
        int height,
        double progress,
        string message)
    {
        if (s_providers.TryGetValue(id, out var reference) &&
            reference.TryGetTarget(out var provider))
        {
            provider.OnBrowserEvent(
                kind,
                positionSeconds,
                durationSeconds,
                width,
                height,
                progress,
                message);
        }
        else
        {
            s_providers.TryRemove(id, out _);
        }
    }

    [JSExport]
    public static void DispatchTimedMetadata(
        int id,
        string metadataJson)
    {
        if (s_providers.TryGetValue(id, out var reference) &&
            reference.TryGetTarget(out var provider))
        {
            provider.OnBrowserTimedMetadata(metadataJson);
        }
        else
        {
            s_providers.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// Real-browser playback lifecycle gate driven by the WinUI-aligned
/// <see cref="MediaPlayer"/> API. The query-driven browser harness invokes
/// this only from an explicit user gesture so audible-media and Web Audio
/// activation follow browser policy.
/// </summary>
public static partial class BrowserMediaPlaybackSmokeTest
{
    private const string AudioGainEffectId =
        "ProGPU.Browser.Smoke.PlaybackAudioGain";
    private const string AudioWorkletEffectId =
        "ProGPU.Browser.Smoke.PlaybackAudioWorklet";
    private const string AudioWorkletModuleUri =
        "./progpu-audio-worklet-smoke.js";
    private const string AudioWorkletProcessorName =
        "progpu-smoke-gain";
    private static readonly TimeSpan Timeout =
        TimeSpan.FromSeconds(30);

    [JSExport]
    public static async Task<int> RunAsync(
        string sourceUri)
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException(
                "The browser playback smoke test requires browser-wasm.");
        }
        if (!Uri.TryCreate(
                sourceUri,
                UriKind.Absolute,
                out Uri? sourceAddress))
        {
            throw new ArgumentException(
                "The browser playback smoke source must be an absolute URI.",
                nameof(sourceUri));
        }

        int initialElementCount =
            GetBrowserMediaElementCountCore();
        int initialAudioWorkletNodeCount =
            GetBrowserMediaAudioWorkletNodeCreationCountCore();
        var opened = CreateSignal();
        var playing = CreateSignal();
        var paused = CreateSignal();
        var firstFrame = CreateSignal();
        var initialProgress = CreateSignal();
        var seekCompleted = CreateSignal();
        var resumedProgress = CreateSignal();
        TimeSpan initialProgressTarget =
            TimeSpan.MaxValue;
        TimeSpan resumedProgressTarget =
            TimeSpan.MaxValue;

        using IDisposable providerRegistration =
            MediaProviderRegistry.Default.Register(
                new BrowserMediaPlaybackProviderFactory(
                    int.MaxValue));
        var gainFactory =
            new MediaAudioGainEffectFactory(
                AudioGainEffectId);
        using IDisposable effectRegistration =
            MediaEffectRegistry.Default.Register(
                gainFactory);
        using IDisposable workletRegistration =
            MediaEffectRegistry.Default.Register(
                new BrowserAudioWorkletEffectFactory(
                    AudioWorkletEffectId,
                    AudioWorkletModuleUri,
                    AudioWorkletProcessorName,
                    """
                    {
                      "processorOptions": {
                        "gain": 0.875
                      }
                    }
                    """));
        using MediaSource source =
            MediaSource.CreateFromUri(sourceAddress);
        using var player = new MediaPlayer
        {
            AutoPlay = false,
            AudioBalance = 0.25d,
            IsMuted = true,
            IsVideoFrameServerEnabled = true
        };

        player.AddAudioEffect(
            AudioGainEffectId,
            effectOptional: false,
            new PropertySet
            {
                [MediaAudioGainEffectFactory
                    .GainPropertyName] = 0.5f
            });
        player.AddAudioEffect(
            AudioWorkletEffectId,
            effectOptional: false,
            new PropertySet());

        void FailAll(Exception exception)
        {
            opened.TrySetException(exception);
            playing.TrySetException(exception);
            paused.TrySetException(exception);
            firstFrame.TrySetException(exception);
            initialProgress.TrySetException(exception);
            seekCompleted.TrySetException(exception);
            resumedProgress.TrySetException(exception);
        }

        player.MediaFailed += (_, args) =>
            FailAll(
                new InvalidOperationException(
                    $"Browser playback failed: {args.Error}: {args.ErrorMessage}",
                    args.ExtendedErrorCode));
        player.MediaOpened += (_, _) =>
            opened.TrySetResult(true);
        player.VideoFrameAvailable += (_, _) =>
            firstFrame.TrySetResult(true);
        player.SeekCompleted += (_, _) =>
            seekCompleted.TrySetResult(true);
        player.PlaybackSession.PlaybackStateChanged +=
            (_, _) =>
            {
                switch (player.PlaybackSession.PlaybackState)
                {
                    case MediaPlaybackState.Playing:
                        playing.TrySetResult(true);
                        break;
                    case MediaPlaybackState.Paused:
                        paused.TrySetResult(true);
                        break;
                }
            };
        player.PlaybackSession.PositionChanged +=
            (_, _) =>
            {
                TimeSpan position =
                    player.PlaybackSession.Position;
                if (position >= initialProgressTarget)
                {
                    initialProgress.TrySetResult(true);
                }
                if (position >= resumedProgressTarget)
                {
                    resumedProgress.TrySetResult(true);
                }
            };

        player.Source = source;
        await AwaitSignalAsync(
            opened.Task,
            "media open");
        await AwaitAudioWorkletNodeAsync(
            initialAudioWorkletNodeCount);

        TimeSpan duration =
            player.PlaybackSession.NaturalDuration;
        if (duration <= TimeSpan.FromSeconds(1) ||
            player.PlaybackSession.NaturalVideoWidth == 0 ||
            player.PlaybackSession.NaturalVideoHeight == 0)
        {
            throw new InvalidOperationException(
                "Browser playback opened without a usable duration and natural video size.");
        }
        if (GetBrowserMediaElementCountCore() !=
            initialElementCount + 1)
        {
            throw new InvalidOperationException(
                "Browser playback did not create exactly one owned DOM media element.");
        }

        initialProgressTarget =
            TimeSpan.FromTicks(
                Math.Min(
                    TimeSpan.FromMilliseconds(250).Ticks,
                    duration.Ticks / 10));
        player.Play();
        await AwaitSignalAsync(
            playing.Task,
            "playing state");
        await AwaitSignalAsync(
            firstFrame.Task,
            "decoded video frame");
        await AwaitSignalAsync(
            initialProgress.Task,
            "initial position progress");

        player.Pause();
        await AwaitSignalAsync(
            paused.Task,
            "paused state");

        TimeSpan seekTarget =
            TimeSpan.FromTicks(duration.Ticks * 2 / 5);
        resumedProgressTarget =
            TimeSpan.FromTicks(duration.Ticks / 2);
        player.PlaybackSession.Position =
            seekTarget;
        await AwaitSignalAsync(
            seekCompleted.Task,
            "seek completion");
        if (Math.Abs(
                (player.PlaybackSession.Position -
                 seekTarget).TotalSeconds) > 0.5d)
        {
            throw new InvalidOperationException(
                "Browser playback completed the seek outside the accepted half-second tolerance.");
        }

        player.Play();
        await AwaitSignalAsync(
            resumedProgress.Task,
            "post-seek replay progress");
        player.Pause();

        MediaGpuFrameDescriptor descriptor =
            player.GetProGpuSurface().CurrentDescriptor;
        MediaPlaybackDiagnosticsSnapshot diagnostics =
            player.GetProGpuDiagnostics();
        if (descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.TransferMode !=
                MediaTransferMode.GpuCopy ||
            diagnostics.ProviderId !=
                "progpu.browser.html-media" ||
            diagnostics.TransferMode !=
                MediaTransferMode.GpuCopy ||
            diagnostics.PresentedFrames == 0)
        {
            throw new InvalidOperationException(
                "Browser playback did not retain a decoded GPU-copy frame with matching provider diagnostics.");
        }

        player.RemoveAllEffects();
        player.Source = null;
        await Task.Yield();
        if (GetBrowserMediaElementCountCore() !=
            initialElementCount)
        {
            throw new InvalidOperationException(
                "Browser playback leaked its owned DOM media element after source disposal.");
        }
        return 0;
    }

    private static TaskCompletionSource<bool>
        CreateSignal() =>
        new(
            TaskCreationOptions
                .RunContinuationsAsynchronously);

    private static async Task AwaitSignalAsync(
        Task signal,
        string operation)
    {
        try
        {
            await signal.WaitAsync(Timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out waiting for browser {operation}.",
                exception);
        }
    }

    private static async Task AwaitAudioWorkletNodeAsync(
        int initialCount)
    {
        using var timeout =
            new CancellationTokenSource(Timeout);
        try
        {
            while (GetBrowserMediaAudioWorkletNodeCreationCountCore() <=
                   initialCount)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(25),
                    timeout.Token);
            }
        }
        catch (OperationCanceledException exception)
            when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Timed out waiting for browser AudioWorklet module loading and node creation.",
                exception);
        }
    }

    [JSImport(
        "getBrowserMediaElementCount",
        "progpu-browser")]
    private static partial int
        GetBrowserMediaElementCountCore();

    [JSImport(
        "getBrowserMediaAudioWorkletNodeCreationCount",
        "progpu-browser")]
    private static partial int
        GetBrowserMediaAudioWorkletNodeCreationCountCore();
}

internal sealed class BrowserMediaGpuFrame :
    IMediaGpuFrame,
    IProGpuContextTextureLeaseSource
{
    private BrowserMediaPlaybackProvider? _owner;

    public BrowserMediaGpuFrame(
        BrowserMediaPlaybackProvider owner,
        MediaGpuFrameDescriptor descriptor)
    {
        _owner = owner;
        Descriptor = descriptor;
    }

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

    public bool TryGetGpuTexture(
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        BrowserMediaPlaybackProvider? owner =
            Volatile.Read(ref _owner);
        ObjectDisposedException.ThrowIf(owner is null, this);
        return owner.TryGetTexture(
            Descriptor,
            requiredContext,
            out texture);
    }

    public bool TryAcquireGpuTextureLease(
        WgpuContext requiredContext,
        out IProGpuTextureLease lease)
    {
        BrowserMediaPlaybackProvider? owner =
            Volatile.Read(ref _owner);
        ObjectDisposedException.ThrowIf(owner is null, this);
        return owner.TryAcquireTexture(
            Descriptor,
            requiredContext,
            out lease);
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _owner, null);
}
