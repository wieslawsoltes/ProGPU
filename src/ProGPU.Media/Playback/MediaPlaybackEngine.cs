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
    private MediaPlaybackTracksSnapshot _tracks =
        MediaPlaybackTracksSnapshot.Empty;
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
    private MediaPlaybackRange _playbackRange =
        MediaPlaybackRange.All;
    private bool _rangeBoundaryReached;
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
    public event EventHandler<MediaPlaybackTracksChangedEventArgs>?
        TracksChanged;
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

    public MediaPlaybackTracksSnapshot Tracks
    {
        get
        {
            lock (_gate)
            {
                return _tracks;
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
            bool nativeLooping;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_looping == value)
                {
                    return;
                }
                _looping = value;
                provider = _provider;
                nativeLooping =
                    value &&
                    _playbackRange.IsIdentity;
            }
            provider?.SetLooping(nativeLooping);
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

    public ValueTask SetSourceAsync(
        MediaSourceDescriptor? source,
        CancellationToken cancellationToken = default) =>
        SetSourceAsync(
            source,
            MediaPlaybackRange.All,
            cancellationToken);

    public async ValueTask SetSourceAsync(
        MediaSourceDescriptor? source,
        MediaPlaybackRange playbackRange,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        IMediaPlaybackProvider? previousProvider;
        CancellationTokenSource? previousCancellation;
        long generation;
        MediaPlaybackChangedEventArgs change;
        MediaPlaybackTracksChangedEventArgs tracksChange;
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
            _playbackRange = source is null
                ? MediaPlaybackRange.All
                : playbackRange;
            _rangeBoundaryReached = false;
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
            _tracks = MediaPlaybackTracksSnapshot.Empty;
            _diagnostics = MediaPlaybackDiagnosticsSnapshot.Empty;
            change = new MediaPlaybackChangedEventArgs(
                MediaPlaybackChange.Source |
                MediaPlaybackChange.State,
                _snapshot);
            tracksChange =
                new MediaPlaybackTracksChangedEventArgs(_tracks);
        }

        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        previousProvider?.Dispose();
        VideoSurface.Clear();
        Changed?.Invoke(this, change);
        TracksChanged?.Invoke(this, tracksChange);

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
            TimeSpan initialSeek;
            bool rangeCanSeek;
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
                initialSeek = _playbackRange.StartTime;
                rangeCanSeek =
                    initialSeek == TimeSpan.Zero ||
                    _snapshot.Capabilities.CanSeek;
            }

            if (!rangeCanSeek)
            {
                DetachProvider(generation, provider);
                provider.Dispose();
                ReportFailure(
                    generation,
                    MediaPlaybackFailure.SourceNotSupported,
                    "The selected provider cannot seek to the playback item's start time.",
                    null);
                return;
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
            if (initialSeek > TimeSpan.Zero)
            {
                provider.Seek(initialSeek);
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
        TimeSpan restartPosition;
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
            restartPosition = _playbackRange.StartTime;
            if (provider is not null &&
                (_snapshot.State !=
                    MediaEnginePlaybackState.Playing ||
                 restartFromEnd))
            {
                if (restartFromEnd)
                {
                    _rangeBoundaryReached = false;
                }
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
            provider?.Seek(restartPosition);
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
        TimeSpan providerPosition;
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
            providerPosition = AddSaturating(
                _playbackRange.StartTime,
                normalized);
            if (_snapshot.NaturalDuration <= TimeSpan.Zero ||
                normalized < _snapshot.NaturalDuration)
            {
                _rangeBoundaryReached = false;
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

        provider?.Seek(providerPosition);
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

    public void SelectTrack(
        MediaPlaybackTrackKind kind,
        int index)
    {
        IMediaPlaybackProvider? provider;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (kind is not (
                    MediaPlaybackTrackKind.Audio or
                    MediaPlaybackTrackKind.Video))
            {
                throw new NotSupportedException(
                    "Timed metadata tracks use presentation modes rather than a single selected index.");
            }

            IReadOnlyList<MediaPlaybackTrackDescriptor> tracks =
                _tracks.GetTracks(kind);
            if (index < -1 || index >= tracks.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index));
            }
            if (_tracks.GetSelectedIndex(kind) == index)
            {
                return;
            }
            provider = _provider;
        }

        if (provider is not IMediaPlaybackTrackProvider
            trackProvider ||
            !trackProvider.TrySelectTrack(kind, index))
        {
            throw new NotSupportedException(
                $"The active media provider cannot select {kind} track index {index}.");
        }
    }

    public void SetTimedMetadataPresentationMode(
        int index,
        MediaPlaybackTimedMetadataPresentationMode mode)
    {
        IMediaPlaybackProvider? provider;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(mode));
            }
            if ((uint)index >=
                (uint)_tracks.TimedMetadataTracks.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index));
            }
            provider = _provider;
        }

        if (provider is not IMediaPlaybackTimedMetadataProvider
            timedMetadataProvider ||
            !timedMetadataProvider
                .TrySetTimedMetadataPresentationMode(
                    index,
                    mode))
        {
            throw new NotSupportedException(
                $"The active media provider cannot set timed metadata track index {index} to {mode}.");
        }
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
        bool nativeLooping;
        double rate;
        MediaPlaybackConfiguration configuration;
        lock (_gate)
        {
            volume = _volume;
            balance = _balance;
            muted = _muted;
            looping = _looping;
            nativeLooping =
                looping &&
                _playbackRange.IsIdentity;
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
        provider.SetLooping(nativeLooping);
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
        IMediaPlaybackProvider? rangeEndProvider = null;
        bool reachedRangeEnd = false;
        bool pauseAtRangeEnd = false;
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            MediaPlaybackSnapshot providerSnapshot =
                value.Normalize();
            reachedRangeEnd =
                !_rangeBoundaryReached &&
                _playRequested &&
                HasReachedDurationLimit(providerSnapshot.Position);
            MediaPlaybackSnapshot normalized =
                ProjectProviderSnapshot(providerSnapshot);
            MediaPlaybackChange flags = GetChanges(
                _snapshot,
                normalized);
            if (reachedRangeEnd)
            {
                _rangeBoundaryReached = true;
                rangeEndProvider = _provider;
                pauseAtRangeEnd = !_looping;
            }
            if (flags == MediaPlaybackChange.None &&
                !reachedRangeEnd)
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
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
        if (reachedRangeEnd)
        {
            if (pauseAtRangeEnd)
            {
                rangeEndProvider?.Pause();
            }
            AcceptEnded(generation);
        }
    }

    private void AcceptTracks(
        long generation,
        MediaPlaybackTracksSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        MediaPlaybackTracksChangedEventArgs? change = null;
        lock (_gate)
        {
            if (!IsCurrent(generation) ||
                _tracks.ContentEquals(value))
            {
                return;
            }

            _tracks = value;
            change =
                new MediaPlaybackTracksChangedEventArgs(value);
        }
        TracksChanged?.Invoke(this, change);
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

            MediaPlaybackSnapshot normalized =
                ProjectProviderSnapshot(value.Normalize());
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
        TimeSpan restartPosition;
        MediaPlaybackChangedEventArgs? change = null;
        lock (_gate)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            provider = _provider;
            looping = _looping;
            restartPosition = _playbackRange.StartTime;
            if (_rangeBoundaryReached &&
                !_playRequested &&
                !looping &&
                _snapshot.State ==
                    MediaEnginePlaybackState.Paused &&
                _snapshot.Position >=
                    _snapshot.NaturalDuration)
            {
                return;
            }
            if (looping)
            {
                _playRequested = true;
                _rangeBoundaryReached = false;
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
                _rangeBoundaryReached = true;
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
            provider.Seek(restartPosition);
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

            TimeSpan normalized =
                ToRelativePosition(position);
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
            if (_snapshot.NaturalDuration <= TimeSpan.Zero ||
                normalized < _snapshot.NaturalDuration)
            {
                _rangeBoundaryReached = false;
            }
        }
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
        SeekCompleted?.Invoke(this, EventArgs.Empty);
    }

    private MediaPlaybackSnapshot ProjectProviderSnapshot(
        in MediaPlaybackSnapshot providerSnapshot)
    {
        TimeSpan sourceDuration =
            providerSnapshot.NaturalDuration;
        TimeSpan relativeDuration;
        if (sourceDuration > TimeSpan.Zero)
        {
            relativeDuration =
                sourceDuration <= _playbackRange.StartTime
                    ? TimeSpan.Zero
                    : sourceDuration -
                        _playbackRange.StartTime;
            if (_playbackRange.DurationLimit is
                    { } durationLimit &&
                relativeDuration > durationLimit)
            {
                relativeDuration = durationLimit;
            }
        }
        else
        {
            relativeDuration =
                _playbackRange.DurationLimit ??
                TimeSpan.Zero;
        }

        TimeSpan relativePosition =
            ToRelativePosition(providerSnapshot.Position);
        if (relativeDuration > TimeSpan.Zero &&
            relativePosition > relativeDuration)
        {
            relativePosition = relativeDuration;
        }

        return providerSnapshot with
        {
            Position = relativePosition,
            NaturalDuration = relativeDuration
        };
    }

    private TimeSpan ToRelativePosition(
        TimeSpan providerPosition)
    {
        if (providerPosition <=
            _playbackRange.StartTime)
        {
            return TimeSpan.Zero;
        }
        return providerPosition -
            _playbackRange.StartTime;
    }

    private bool HasReachedDurationLimit(
        TimeSpan providerPosition)
    {
        if (_playbackRange.DurationLimit is
            not { } durationLimit)
        {
            return false;
        }
        TimeSpan end = AddSaturating(
            _playbackRange.StartTime,
            durationLimit);
        return providerPosition >= end;
    }

    private static TimeSpan AddSaturating(
        TimeSpan left,
        TimeSpan right)
    {
        long remaining =
            TimeSpan.MaxValue.Ticks -
            left.Ticks;
        return right.Ticks >= remaining
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(
                left.Ticks + right.Ticks);
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
            _playbackRange = MediaPlaybackRange.All;
            _rangeBoundaryReached = false;
            effects = [.. _activeEffects];
            _activeEffects.Clear();
            _snapshot = MediaPlaybackSnapshot.Empty;
            _tracks = MediaPlaybackTracksSnapshot.Empty;
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

        public void UpdateTracks(
            MediaPlaybackTracksSnapshot tracks) =>
            _owner.AcceptTracks(_generation, tracks);

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
