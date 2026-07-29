using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Media.Audio;
using ProGPU.Media.Diagnostics;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;
using Silk.NET.WebGPU;

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
        IDisposable fastExport =
            MediaCompositionExportRegistry.Default.Register(
                new BrowserFastMediaCompositionExportProvider(
                    priority == int.MinValue
                        ? int.MinValue
                        : priority - 1));
        return new BrowserMediaRegistrations(
            playback,
            export,
            fastExport);
    }

    private sealed class BrowserMediaRegistrations :
        IDisposable
    {
        private IDisposable? _playback;
        private IDisposable? _export;
        private IDisposable? _fastExport;

        public BrowserMediaRegistrations(
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
    IMediaPlaybackProvider
{
    private static int s_nextId;
    private readonly object _gate = new();
    private readonly Uri _uri;
    private readonly IMediaPlaybackSink _sink;
    private readonly int _id;
    private readonly List<AudioGraphEffectBinding>
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

    public void AddEffect(IMediaEffect effect, bool optional)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (effect is not IMediaAudioGraphEffect
            graphEffect)
        {
            if (!optional)
            {
                throw new NotSupportedException(
                    "The browser provider accepts typed IMediaAudioGraphEffect nodes. Arbitrary managed PCM callbacks cannot execute in an AudioWorklet graph.");
            }
            return;
        }

        MediaAudioGraphEffectState state =
            graphEffect.CaptureState();
        if (state.Kind !=
            MediaAudioGraphEffectKind.Gain)
        {
            if (!optional)
            {
                throw new NotSupportedException(
                    $"Browser WebAudio does not support the audio graph effect kind '{state.Kind}'.");
            }
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            var binding = new AudioGraphEffectBinding(
                checked(++_nextAudioEffectId),
                graphEffect,
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
        AudioGraphEffectBinding[] bindings;
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

    private void OnAudioEffectStateChanged(
        AudioGraphEffectBinding binding)
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
        AudioGraphEffectBinding binding)
    {
        MediaAudioGraphEffectState state =
            binding.Effect.CaptureState();
        ConfigureAudioEffectCore(
            _id,
            binding.Id,
            (int)state.Kind,
            state.Parameter0,
            state.Parameter1,
            state.Parameter2,
            state.Parameter3);
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

    private sealed class AudioGraphEffectBinding :
        IDisposable
    {
        private readonly Action _changed;

        public AudioGraphEffectBinding(
            int id,
            IMediaAudioGraphEffect effect,
            Action<AudioGraphEffectBinding> changed)
        {
            Id = id;
            Effect = effect;
            _changed = () => changed(this);
            Effect.StateChanged += _changed;
        }

        public int Id { get; }

        public IMediaAudioGraphEffect Effect { get; }

        public void Dispose() =>
            Effect.StateChanged -= _changed;
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
