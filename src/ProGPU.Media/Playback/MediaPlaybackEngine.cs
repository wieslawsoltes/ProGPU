using ProGPU.Media.Diagnostics;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;

namespace ProGPU.Media.Playback;

/// <summary>
/// Framework-neutral playback coordinator. Providers own demux/decode/audio;
/// this type owns state, source generations, effect lifetime and the latest
/// GPU-frame surface.
/// </summary>
public sealed class MediaPlaybackEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly MediaProviderRegistry _providers;
    private readonly MediaEffectRegistry _effects;
    private readonly List<ActiveEffect> _activeEffects = [];
    private CancellationTokenSource? _openCancellation;
    private IMediaPlaybackProvider? _provider;
    private MediaSourceDescriptor? _source;
    private MediaPlaybackSnapshot _snapshot =
        MediaPlaybackSnapshot.Empty;
    private MediaPlaybackDiagnosticsSnapshot _diagnostics =
        MediaPlaybackDiagnosticsSnapshot.Empty;
    private long _sourceGeneration;
    private bool _playRequested;
    private bool _autoPlay;
    private bool _looping;
    private bool _muted;
    private double _volume = 1d;
    private double _balance;
    private double _playbackRate = 1d;
    private MediaPlaybackConfiguration _configuration =
        MediaPlaybackConfiguration.Default;
    private int _disposed;

    public MediaPlaybackEngine(
        MediaProviderRegistry? providers = null,
        MediaEffectRegistry? effects = null)
    {
        _providers = providers ?? MediaProviderRegistry.Default;
        _effects = effects ?? MediaEffectRegistry.Default;
        VideoSurface = new MediaGpuSurface();
    }

    public event EventHandler<MediaPlaybackChangedEventArgs>? Changed;
    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler? SeekCompleted;
    public event EventHandler<MediaPlaybackFailureEventArgs>? Failed;
    public event EventHandler? DiagnosticsChanged;

    public MediaGpuSurface VideoSurface { get; }

    public MediaSourceDescriptor? Source
    {
        get
        {
            lock (_gate)
            {
                return _source;
            }
        }
    }

    public MediaPlaybackSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public MediaPlaybackDiagnosticsSnapshot Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics;
            }
        }
    }

    public MediaPlaybackConfiguration Configuration
    {
        get
        {
            lock (_gate)
            {
                return _configuration;
            }
        }
        set
        {
            if (!Enum.IsDefined(value.AudioCategory) ||
                !Enum.IsDefined(value.AudioDeviceRole) ||
                !Enum.IsDefined(value.StereoscopicRenderMode))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            IMediaPlaybackConfigurationProvider? provider;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_configuration == value)
                {
                    return;
                }
                _configuration = value;
                provider = _provider as
                    IMediaPlaybackConfigurationProvider;
            }
            provider?.ApplyConfiguration(in value);
        }
    }

    public bool AutoPlay
    {
        get
        {
            lock (_gate)
            {
                return _autoPlay;
            }
        }
        set
        {
            bool start;
            lock (_gate)
            {
                ThrowIfDisposed();
                _autoPlay = value;
                start = value && _source is not null;
            }
            if (start)
            {
                Play();
            }
        }
    }

    public bool IsLoopingEnabled
    {
        get
        {
            lock (_gate)
            {
                return _looping;
            }
        }
        set
        {
            IMediaPlaybackProvider? provider;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_looping == value)
                {
                    return;
                }
                _looping = value;
                provider = _provider;
            }
            provider?.SetLooping(value);
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_gate)
            {
                return _muted;
            }
        }
        set
        {
            IMediaPlaybackProvider? provider;
            double volume;
            double balance;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_muted == value)
                {
                    return;
                }
                _muted = value;
                volume = _volume;
                balance = _balance;
                provider = _provider;
            }
            provider?.SetVolume(volume, balance, value);
        }
    }

    public double Volume
    {
        get
        {
            lock (_gate)
            {
                return _volume;
            }
        }
        set
        {
            double normalized = Math.Clamp(value, 0d, 1d);
            IMediaPlaybackProvider? provider;
            double balance;
            bool muted;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_volume.Equals(normalized))
                {
                    return;
                }
                _volume = normalized;
                balance = _balance;
                muted = _muted;
                provider = _provider;
            }
            provider?.SetVolume(normalized, balance, muted);
        }
    }

    public double AudioBalance
    {
        get
        {
            lock (_gate)
            {
                return _balance;
            }
        }
        set
        {
            double normalized = Math.Clamp(value, -1d, 1d);
            IMediaPlaybackProvider? provider;
            double volume;
            bool muted;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_balance.Equals(normalized))
                {
                    return;
                }
                _balance = normalized;
                volume = _volume;
                muted = _muted;
                provider = _provider;
            }
            provider?.SetVolume(volume, normalized, muted);
        }
    }

    public async ValueTask SetSourceAsync(
        MediaSourceDescriptor? source,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        IMediaPlaybackProvider? previousProvider;
        CancellationTokenSource? previousCancellation;
        long generation;
        MediaPlaybackChangedEventArgs change;
        lock (_gate)
        {
            generation = ++_sourceGeneration;
            previousProvider = _provider;
            _provider = null;
            previousCancellation = _openCancellation;
            _openCancellation = source is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            _source = source;
            _playRequested = source is not null && _autoPlay;
            _snapshot = source is null
                ? MediaPlaybackSnapshot.Empty with
                {
                    PlaybackRate = _playbackRate
                }
                : MediaPlaybackSnapshot.Empty with
                {
                    State = MediaEnginePlaybackState.Opening,
                    PlaybackRate = _playbackRate
                };
            _diagnostics = MediaPlaybackDiagnosticsSnapshot.Empty;
            change = new MediaPlaybackChangedEventArgs(
                MediaPlaybackChange.Source |
                MediaPlaybackChange.State,
                _snapshot);
        }

        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        previousProvider?.Dispose();
        VideoSurface.Clear();
        Changed?.Invoke(this, change);

        if (source is null)
        {
            return;
        }

        IMediaPlaybackProviderFactory? factory =
            _providers.Select(source);
        if (factory is null)
        {
            ReportFailure(
                generation,
                MediaPlaybackFailure.SourceNotSupported,
                "No registered media provider can open this source.",
                null);
            return;
        }

        CancellationToken token;
        lock (_gate)
        {
            if (generation != _sourceGeneration ||
                _openCancellation is null)
            {
                return;
            }
            token = _openCancellation.Token;
        }

        IMediaPlaybackProvider? provider = null;
        try
        {
            provider = await factory.CreateAsync(
                source,
                new ProviderSink(this, generation),
                token).ConfigureAwait(false);

            bool accepted;
            lock (_gate)
            {
                accepted = generation == _sourceGeneration &&
                    !token.IsCancellationRequested &&
                    Volatile.Read(ref _disposed) == 0;
                if (accepted)
                {
                    _provider = provider;
                    _diagnostics = _diagnostics with
                    {
                        ProviderId = provider.Id
                    };
                }
            }

            if (!accepted)
            {
                // Disposal may call provider code. Keep it outside the
                // coordinator lock to prevent callback reentrancy.
                provider.Dispose();
                return;
            }

            ApplyProviderSettings(provider);
            await provider.OpenAsync(token).ConfigureAwait(false);

            bool mustPlay;
            bool mustSynthesizeOpen;
            lock (_gate)
            {
                if (generation != _sourceGeneration ||
                    !ReferenceEquals(provider, _provider))
                {
                    return;
                }

                mustPlay = _playRequested;
                mustSynthesizeOpen =
                    _snapshot.State == MediaEnginePlaybackState.Opening;
            }

            if (mustSynthesizeOpen)
            {
                AcceptOpened(
                    generation,
                    Snapshot with
                    {
                        State = mustPlay
                            ? MediaEnginePlaybackState.Playing
                            : MediaEnginePlaybackState.Paused
                    });
            }
            if (mustPlay)
            {
                provider.Play();
            }
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            DetachProvider(generation, provider);
            provider?.Dispose();
        }
        catch (Exception exception)
        {
            DetachProvider(generation, provider);
            provider?.Dispose();
            ReportFailure(
                generation,
                MediaPlaybackFailure.Unknown,
                exception.Message,
                exception);
        }
    }

    public void Play()
    {
        IMediaPlaybackProvider? provider;
        bool restartFromEnd;
        MediaPlaybackChangedEventArgs? change = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_source is null)
            {
                return;
            }

            _playRequested = true;
            provider = _provider;
            restartFromEnd =
                provider is not null &&
                _snapshot.NaturalDuration > TimeSpan.Zero &&
                _snapshot.Position >= _snapshot.NaturalDuration;
            if (provider is not null &&
                (_snapshot.State !=
                    MediaEnginePlaybackState.Playing ||
                 restartFromEnd))
            {
                _snapshot = _snapshot with
                {
                    State = MediaEnginePlaybackState.Playing,
                    Position = restartFromEnd
                        ? TimeSpan.Zero
                        : _snapshot.Position
                };
                change = new MediaPlaybackChangedEventArgs(
                    MediaPlaybackChange.State |
                    (restartFromEnd
                        ? MediaPlaybackChange.Position
                        : MediaPlaybackChange.None),
                    _snapshot);
            }
        }

        if (restartFromEnd)
        {
            provider?.Seek(TimeSpan.Zero);
        }
        provider?.Play();
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
    }

    public void Pause()
    {
        IMediaPlaybackProvider? provider;
        MediaPlaybackChangedEventArgs? change = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            _playRequested = false;
            provider = _provider;
            if (provider is not null &&
                _snapshot.State is
                    MediaEnginePlaybackState.Playing or
                    MediaEnginePlaybackState.Buffering)
            {
                _snapshot = _snapshot with
                {
                    State = MediaEnginePlaybackState.Paused
                };
                change = new MediaPlaybackChangedEventArgs(
                    MediaPlaybackChange.State,
                    _snapshot);
            }
        }

        provider?.Pause();
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
    }

    public void Seek(TimeSpan position)
    {
        IMediaPlaybackProvider? provider;
        TimeSpan normalized;
        MediaPlaybackChangedEventArgs? change = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_snapshot.Capabilities.CanSeek)
            {
                return;
            }

            normalized = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position;
            if (_snapshot.NaturalDuration > TimeSpan.Zero &&
                normalized > _snapshot.NaturalDuration)
            {
                normalized = _snapshot.NaturalDuration;
            }

            provider = _provider;
            if (_snapshot.Position != normalized)
            {
                _snapshot = _snapshot with
                {
                    Position = normalized
                };
                change = new MediaPlaybackChangedEventArgs(
                    MediaPlaybackChange.Position,
                    _snapshot);
            }
        }

        provider?.Seek(normalized);
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
    }

    public void SetPlaybackRate(double value)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        IMediaPlaybackProvider? provider;
        MediaPlaybackChangedEventArgs? change = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            provider = _provider;
            if (_playbackRate.Equals(value))
            {
                return;
            }
            _playbackRate = value;
            _snapshot = _snapshot with { PlaybackRate = value };
            change = new MediaPlaybackChangedEventArgs(
                MediaPlaybackChange.PlaybackRate,
                _snapshot);
        }

        provider?.SetPlaybackRate(value);
        Changed?.Invoke(this, change);
    }

    public bool StepForwardOneFrame()
    {
        IMediaPlaybackProvider? provider;
        lock (_gate)
        {
            ThrowIfDisposed();
            provider = _provider;
        }
        return provider?.StepForwardOneFrame() == true;
    }

    public bool StepBackwardOneFrame()
    {
        IMediaPlaybackProvider? provider;
        lock (_gate)
        {
            ThrowIfDisposed();
            provider = _provider;
        }
        return provider?.StepBackwardOneFrame() == true;
    }

    public void AddEffect(
        string activatableClassId,
        MediaEffectKind kind,
        bool optional,
        IReadOnlyDictionary<string, object?> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            activatableClassId);
        ArgumentNullException.ThrowIfNull(properties);

        var descriptor = new MediaEffectDescriptor(
            activatableClassId,
            kind,
            properties);
        if (!_effects.TryCreate(descriptor, out IMediaEffect? effect) ||
            effect is null)
        {
            if (optional)
            {
                return;
            }
            throw new NotSupportedException(
                $"The media effect '{activatableClassId}' is not registered.");
        }

        var activeEffect =
            new ActiveEffect(effect, optional);
        IMediaPlaybackProvider? provider;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _activeEffects.Add(activeEffect);
                provider = _provider;
            }
        }
        catch
        {
            effect.Dispose();
            throw;
        }

        try
        {
            provider?.AddEffect(effect, optional);
        }
        catch
        {
            lock (_gate)
            {
                _activeEffects.Remove(activeEffect);
            }
            effect.Dispose();
            throw;
        }
    }

    public void RemoveAllEffects()
    {
        ActiveEffect[] effects;
        IMediaPlaybackProvider? provider;
        lock (_gate)
        {
            ThrowIfDisposed();
            effects = [.. _activeEffects];
            _activeEffects.Clear();
            provider = _provider;
        }

        provider?.RemoveAllEffects();
        for (int index = 0; index < effects.Length; index++)
        {
            effects[index].Effect.Dispose();
        }
    }

    private void ApplyProviderSettings(
        IMediaPlaybackProvider provider)
    {
        ActiveEffect[] effects;
        double volume;
        double balance;
        bool muted;
        bool looping;
        double rate;
        MediaPlaybackConfiguration configuration;
        lock (_gate)
        {
            volume = _volume;
            balance = _balance;
            muted = _muted;
            looping = _looping;
            rate = _playbackRate;
            configuration = _configuration;
            effects = [.. _activeEffects];
        }

        if (provider is IMediaPlaybackConfigurationProvider
            configurable)
        {
            configurable.ApplyConfiguration(in configuration);
        }
        provider.SetVolume(volume, balance, muted);
        provider.SetLooping(looping);
        provider.SetPlaybackRate(rate);
        for (int index = 0; index < effects.Length; index++)
        {
            provider.AddEffect(
                effects[index].Effect,
                effects[index].Optional);
        }
    }

    private void AcceptUpdate(
        long generation,
        in MediaPlaybackSnapshot value)
    {
        MediaPlaybackChangedEventArgs? change;
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            MediaPlaybackSnapshot normalized = value.Normalize();
            MediaPlaybackChange flags = GetChanges(
                _snapshot,
                normalized);
            if (flags == MediaPlaybackChange.None)
            {
                return;
            }

            _snapshot = normalized;
            _diagnostics = _diagnostics with
            {
                HardwareDecoded =
                    normalized.Capabilities.HardwareDecoded
            };
            change = new MediaPlaybackChangedEventArgs(
                flags,
                normalized);
        }
        Changed?.Invoke(this, change);
    }

    private void AcceptOpened(
        long generation,
        in MediaPlaybackSnapshot value)
    {
        MediaPlaybackChangedEventArgs change;
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            MediaPlaybackSnapshot normalized = value.Normalize();
            if (_playRequested)
            {
                normalized = normalized with
                {
                    State = MediaEnginePlaybackState.Playing
                };
            }
            else if (normalized.State ==
                     MediaEnginePlaybackState.Opening)
            {
                normalized = normalized with
                {
                    State = MediaEnginePlaybackState.Paused
                };
            }

            MediaPlaybackChange flags =
                GetChanges(_snapshot, normalized) |
                MediaPlaybackChange.State;
            _snapshot = normalized;
            _diagnostics = _diagnostics with
            {
                HardwareDecoded =
                    normalized.Capabilities.HardwareDecoded
            };
            change = new MediaPlaybackChangedEventArgs(
                flags,
                normalized);
        }
        Changed?.Invoke(this, change);
        Opened?.Invoke(this, EventArgs.Empty);
    }

    private void AcceptEnded(long generation)
    {
        IMediaPlaybackProvider? provider;
        bool looping;
        MediaPlaybackChangedEventArgs? change = null;
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            provider = _provider;
            looping = _looping;
            if (looping)
            {
                _playRequested = true;
                _snapshot = _snapshot with
                {
                    State = MediaEnginePlaybackState.Playing,
                    Position = TimeSpan.Zero
                };
                change = new MediaPlaybackChangedEventArgs(
                    MediaPlaybackChange.State |
                    MediaPlaybackChange.Position,
                    _snapshot);
            }
            else
            {
                _playRequested = false;
                _snapshot = _snapshot with
                {
                    State = MediaEnginePlaybackState.Paused,
                    Position = _snapshot.NaturalDuration
                };
                change = new MediaPlaybackChangedEventArgs(
                    MediaPlaybackChange.State |
                    MediaPlaybackChange.Position,
                    _snapshot);
            }
        }

        if (looping && provider is not null)
        {
            provider.Seek(TimeSpan.Zero);
            provider.Play();
            if (change is not null)
            {
                Changed?.Invoke(this, change);
            }
        }
        else if (change is not null)
        {
            Changed?.Invoke(this, change);
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AcceptSeekCompleted(
        long generation,
        TimeSpan position)
    {
        MediaPlaybackChangedEventArgs? change = null;
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            TimeSpan normalized = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position;
            if (_snapshot.NaturalDuration > TimeSpan.Zero &&
                normalized > _snapshot.NaturalDuration)
            {
                normalized = _snapshot.NaturalDuration;
            }
            if (_snapshot.Position != normalized)
            {
                _snapshot = _snapshot with
                {
                    Position = normalized
                };
                change = new MediaPlaybackChangedEventArgs(
                    MediaPlaybackChange.Position,
                    _snapshot);
            }
        }
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
        SeekCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ReportFailure(
        long generation,
        MediaPlaybackFailure failure,
        string message,
        Exception? exception)
    {
        MediaPlaybackChangedEventArgs? change;
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            _playRequested = false;
            _snapshot = _snapshot with
            {
                State = MediaEnginePlaybackState.None
            };
            _diagnostics = _diagnostics with
            {
                LastFallbackReason = message
            };
            change = new MediaPlaybackChangedEventArgs(
                MediaPlaybackChange.State,
                _snapshot);
        }
        Changed?.Invoke(this, change);
        Failed?.Invoke(
            this,
            new MediaPlaybackFailureEventArgs(
                failure,
                message,
                exception));
    }

    private void AcceptFrame(
        long generation,
        IMediaGpuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                frame.Dispose();
                return;
            }

            _diagnostics = _diagnostics with
            {
                TransferMode = frame.Descriptor.TransferMode,
                PresentedFrames =
                    _diagnostics.PresentedFrames + 1
            };
        }
        VideoSurface.Publish(frame);
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AcceptDiagnostics(
        long generation,
        in MediaProviderDiagnostics value)
    {
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            _diagnostics = _diagnostics with
            {
                HardwareDecoded = value.HardwareDecoded,
                TransferMode = value.TransferMode,
                DroppedFrames = Math.Max(0, value.DroppedFrames),
                VideoQueueDepth = Math.Max(0, value.VideoQueueDepth),
                AudioQueueDepth = Math.Max(0, value.AudioQueueDepth),
                AudioLatency = value.AudioLatency < TimeSpan.Zero
                    ? TimeSpan.Zero
                    : value.AudioLatency,
                LastFallbackReason = value.LastFallbackReason
            };
        }
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsCurrent(long generation) =>
        generation == _sourceGeneration &&
        Volatile.Read(ref _disposed) == 0;

    private void DetachProvider(
        long generation,
        IMediaPlaybackProvider? provider)
    {
        if (provider is null)
        {
            return;
        }

        lock (_gate)
        {
            if (generation == _sourceGeneration &&
                ReferenceEquals(_provider, provider))
            {
                _provider = null;
            }
        }
    }

    private static MediaPlaybackChange GetChanges(
        in MediaPlaybackSnapshot oldValue,
        in MediaPlaybackSnapshot newValue)
    {
        MediaPlaybackChange result = MediaPlaybackChange.None;
        if (oldValue.State != newValue.State)
        {
            result |= MediaPlaybackChange.State;
        }
        if (oldValue.Position != newValue.Position)
        {
            result |= MediaPlaybackChange.Position;
        }
        if (oldValue.NaturalDuration != newValue.NaturalDuration)
        {
            result |= MediaPlaybackChange.Duration;
        }
        if (oldValue.NaturalVideoWidth !=
                newValue.NaturalVideoWidth ||
            oldValue.NaturalVideoHeight !=
                newValue.NaturalVideoHeight)
        {
            result |= MediaPlaybackChange.NaturalVideoSize;
        }
        if (!oldValue.BufferingProgress.Equals(
                newValue.BufferingProgress))
        {
            result |= MediaPlaybackChange.Buffering;
        }
        if (!oldValue.DownloadProgress.Equals(
                newValue.DownloadProgress))
        {
            result |= MediaPlaybackChange.Download;
        }
        if (!oldValue.PlaybackRate.Equals(
                newValue.PlaybackRate))
        {
            result |= MediaPlaybackChange.PlaybackRate;
        }
        if (oldValue.Capabilities != newValue.Capabilities)
        {
            result |= MediaPlaybackChange.Capabilities;
        }
        return result;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? cancellation;
        IMediaPlaybackProvider? provider;
        ActiveEffect[] effects;
        lock (_gate)
        {
            ++_sourceGeneration;
            cancellation = _openCancellation;
            _openCancellation = null;
            provider = _provider;
            _provider = null;
            _source = null;
            effects = [.. _activeEffects];
            _activeEffects.Clear();
            _snapshot = MediaPlaybackSnapshot.Empty;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        provider?.Dispose();
        for (int index = 0; index < effects.Length; index++)
        {
            effects[index].Effect.Dispose();
        }
        VideoSurface.Dispose();
    }

    private readonly record struct ActiveEffect(
        IMediaEffect Effect,
        bool Optional);

    private sealed class ProviderSink : IMediaPlaybackSink
    {
        private readonly MediaPlaybackEngine _owner;
        private readonly long _generation;

        public ProviderSink(
            MediaPlaybackEngine owner,
            long generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public void Update(in MediaPlaybackSnapshot snapshot) =>
            _owner.AcceptUpdate(_generation, snapshot);

        public void Opened(in MediaPlaybackSnapshot snapshot) =>
            _owner.AcceptOpened(_generation, snapshot);

        public void Ended() =>
            _owner.AcceptEnded(_generation);

        public void SeekCompleted(TimeSpan position) =>
            _owner.AcceptSeekCompleted(_generation, position);

        public void Failed(
            MediaPlaybackFailure failure,
            string message,
            Exception? exception = null) =>
            _owner.ReportFailure(
                _generation,
                failure,
                message,
                exception);

        public void Present(IMediaGpuFrame frame) =>
            _owner.AcceptFrame(_generation, frame);

        public void UpdateDiagnostics(
            in MediaProviderDiagnostics diagnostics) =>
            _owner.AcceptDiagnostics(_generation, diagnostics);
    }
}
