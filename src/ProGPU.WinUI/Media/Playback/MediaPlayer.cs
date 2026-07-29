using ProGPU.Media.Diagnostics;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Windows.Media.Playback;

public enum MediaPlaybackState
{
    None = 0,
    Opening = 1,
    Buffering = 2,
    Playing = 3,
    Paused = 4
}

public enum MediaPlayerError
{
    Unknown = 0,
    Aborted = 1,
    NetworkError = 2,
    DecodingError = 3,
    SourceNotSupported = 4
}

public enum MediaPlayerAudioCategory
{
    Other = 0,
    Communications = 3,
    Alerts = 4,
    SoundEffects = 5,
    GameEffects = 6,
    GameMedia = 7,
    GameChat = 8,
    Speech = 9,
    Movie = 10,
    Media = 11
}

public enum MediaPlayerAudioDeviceType
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

public enum StereoscopicVideoRenderMode
{
    Mono = 0,
    Stereo = 1
}

[Obsolete("Use MediaPlaybackState instead.")]
public enum MediaPlayerState
{
    Closed = 0,
    Opening = 1,
    Buffering = 2,
    Playing = 3,
    Paused = 4,
    Stopped = 5
}

public sealed class MediaPlayerRateChangedEventArgs : EventArgs
{
    internal MediaPlayerRateChangedEventArgs(double newRate)
    {
        NewRate = newRate;
    }

    public double NewRate { get; }
}

public sealed class MediaPlayerFailedEventArgs : EventArgs
{
    internal MediaPlayerFailedEventArgs(
        MediaPlayerError error,
        string errorMessage,
        Exception? extendedErrorCode)
    {
        Error = error;
        ErrorMessage = errorMessage;
        ExtendedErrorCode = extendedErrorCode;
    }

    public MediaPlayerError Error { get; }
    public string ErrorMessage { get; }
    public Exception? ExtendedErrorCode { get; }
}

public sealed class MediaPlaybackSessionBufferingStartedEventArgs :
    EventArgs
{
    internal MediaPlaybackSessionBufferingStartedEventArgs(
        bool isPlaybackInterruption)
    {
        IsPlaybackInterruption = isPlaybackInterruption;
    }

    public bool IsPlaybackInterruption { get; }
}

public sealed class MediaPlaybackSession
{
    private readonly MediaPlayer _mediaPlayer;
    private MediaPlaybackSnapshot _snapshot;
    private bool _isMirroring;
    private Rect _normalizedSourceRect =
        new(0d, 0d, 1d, 1d);
    private MediaRotation _playbackRotation;
    private StereoscopicVideoPackingMode
        _stereoscopicVideoPackingMode;
    private TimeSpan _playedRangeEnd;

    internal MediaPlaybackSession(
        MediaPlayer mediaPlayer,
        MediaPlaybackSnapshot snapshot)
    {
        _mediaPlayer = mediaPlayer;
        _snapshot = snapshot;
        SphericalVideoProjection =
            new MediaPlaybackSphericalVideoProjection(this);
    }

    public MediaPlayer MediaPlayer => _mediaPlayer;
    public MediaPlaybackState PlaybackState =>
        MapState(_snapshot.State);
    public TimeSpan Position
    {
        get => _snapshot.Position;
        set => _mediaPlayer.Seek(value);
    }
    public TimeSpan NaturalDuration =>
        _snapshot.NaturalDuration;
    public uint NaturalVideoWidth =>
        _snapshot.NaturalVideoWidth;
    public uint NaturalVideoHeight =>
        _snapshot.NaturalVideoHeight;
    public double BufferingProgress =>
        _snapshot.BufferingProgress;
    public double DownloadProgress =>
        _snapshot.DownloadProgress;
    public bool CanPause => _snapshot.Capabilities.CanPause;
    public bool CanSeek => _snapshot.Capabilities.CanSeek;
    public bool IsProtected => false;
    public bool IsMirroring
    {
        get => _isMirroring;
        set
        {
            if (_isMirroring == value)
            {
                return;
            }
            _isMirroring = value;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public Rect NormalizedSourceRect
    {
        get => _normalizedSourceRect;
        set
        {
            Rect normalized = NormalizeSourceRect(value);
            if (_normalizedSourceRect == normalized)
            {
                return;
            }
            _normalizedSourceRect = normalized;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public MediaRotation PlaybackRotation
    {
        get => _playbackRotation;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_playbackRotation == value)
            {
                return;
            }
            _playbackRotation = value;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public MediaPlaybackSphericalVideoProjection
        SphericalVideoProjection { get; }
    public StereoscopicVideoPackingMode
        StereoscopicVideoPackingMode
    {
        get => _stereoscopicVideoPackingMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_stereoscopicVideoPackingMode == value)
            {
                return;
            }
            _stereoscopicVideoPackingMode = value;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public double PlaybackRate
    {
        get => _snapshot.PlaybackRate;
        set => _mediaPlayer.SetPlaybackRate(value);
    }

    public IReadOnlyList<MediaTimeRange> GetBufferedRanges()
    {
        TimeSpan duration = _snapshot.NaturalDuration;
        if (duration <= TimeSpan.Zero ||
            _snapshot.DownloadProgress <= 0d)
        {
            return Array.Empty<MediaTimeRange>();
        }

        return
        [
            new MediaTimeRange
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromTicks(
                    checked((long)(
                        duration.Ticks *
                        _snapshot.DownloadProgress)))
            }
        ];
    }

    public IReadOnlyList<MediaTimeRange> GetPlayedRanges() =>
        _playedRangeEnd <= TimeSpan.Zero
            ? Array.Empty<MediaTimeRange>()
            :
            [
                new MediaTimeRange
                {
                    Start = TimeSpan.Zero,
                    End = _playedRangeEnd
                }
            ];

    public IReadOnlyList<MediaTimeRange> GetSeekableRanges() =>
        !_snapshot.Capabilities.CanSeek ||
        _snapshot.NaturalDuration <= TimeSpan.Zero
            ? Array.Empty<MediaTimeRange>()
            :
            [
                new MediaTimeRange
                {
                    Start = TimeSpan.Zero,
                    End = _snapshot.NaturalDuration
                }
            ];

    public bool IsSupportedPlaybackRateRange(
        double rate1,
        double rate2)
    {
        if (!double.IsFinite(rate1) ||
            !double.IsFinite(rate2) ||
            rate1 <= 0d ||
            rate2 < rate1)
        {
            return false;
        }

        return _snapshot.Capabilities.SupportsRate &&
               rate1 >= 0.5d &&
               rate2 <= 2d;
    }

    public MediaPlaybackSessionOutputDegradationPolicyState
        GetOutputDegradationPolicyState() =>
        new(MediaPlaybackSessionVideoConstrictionReason.None);

    public event TypedEventHandler<MediaPlaybackSession, object>?
        PlaybackStateChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        PositionChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        NaturalDurationChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        NaturalVideoSizeChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        BufferingProgressChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        DownloadProgressChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        PlaybackRateChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        SeekCompleted;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        BufferingStarted;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        BufferingEnded;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        BufferedRangesChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        PlayedRangesChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        SeekableRangesChanged;
    public event TypedEventHandler<MediaPlaybackSession, object>?
        SupportedPlaybackRatesChanged;

    internal event EventHandler? PresentationChanged;

    internal void AcceptChange(
        MediaPlaybackChangedEventArgs args)
    {
        MediaPlaybackState oldState = PlaybackState;
        MediaPlaybackSnapshot oldSnapshot = _snapshot;
        _snapshot = args.Snapshot;
        if ((args.Change & MediaPlaybackChange.Source) != 0)
        {
            _playedRangeEnd = TimeSpan.Zero;
        }
        else if (_snapshot.Position > _playedRangeEnd)
        {
            _playedRangeEnd = _snapshot.Position;
        }

        if ((args.Change & MediaPlaybackChange.State) != 0)
        {
            MediaPlaybackState newState = PlaybackState;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            if (oldState != MediaPlaybackState.Buffering &&
                newState == MediaPlaybackState.Buffering)
            {
                BufferingStarted?.Invoke(
                    this,
                    new MediaPlaybackSessionBufferingStartedEventArgs(
                        oldState == MediaPlaybackState.Playing));
            }
            else if (oldState == MediaPlaybackState.Buffering &&
                     newState != MediaPlaybackState.Buffering)
            {
                BufferingEnded?.Invoke(this, EventArgs.Empty);
            }
        }
        if ((args.Change & MediaPlaybackChange.Position) != 0)
        {
            PositionChanged?.Invoke(this, EventArgs.Empty);
            PlayedRangesChanged?.Invoke(this, EventArgs.Empty);
        }
        if ((args.Change & MediaPlaybackChange.Duration) != 0)
        {
            NaturalDurationChanged?.Invoke(
                this,
                EventArgs.Empty);
            SeekableRangesChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
        if ((args.Change &
             MediaPlaybackChange.NaturalVideoSize) != 0)
        {
            NaturalVideoSizeChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
        if ((args.Change & MediaPlaybackChange.Buffering) != 0)
        {
            BufferingProgressChanged?.Invoke(
                this,
                EventArgs.Empty);
            BufferedRangesChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
        if ((args.Change & MediaPlaybackChange.Download) != 0)
        {
            DownloadProgressChanged?.Invoke(
                this,
                EventArgs.Empty);
            BufferedRangesChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
        if ((args.Change & MediaPlaybackChange.PlaybackRate) != 0)
        {
            PlaybackRateChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
        if ((args.Change & MediaPlaybackChange.Capabilities) != 0)
        {
            if (oldSnapshot.Capabilities.CanSeek !=
                _snapshot.Capabilities.CanSeek)
            {
                SeekableRangesChanged?.Invoke(
                    this,
                    EventArgs.Empty);
            }
            if (oldSnapshot.Capabilities.SupportsRate !=
                _snapshot.Capabilities.SupportsRate)
            {
                SupportedPlaybackRatesChanged?.Invoke(
                    this,
                    EventArgs.Empty);
            }
        }
    }

    internal void RaiseSeekCompleted() =>
        SeekCompleted?.Invoke(this, EventArgs.Empty);

    internal void NotifyPresentationChanged() =>
        PresentationChanged?.Invoke(this, EventArgs.Empty);

    private static Rect NormalizeSourceRect(Rect value)
    {
        if (!double.IsFinite(value.X) ||
            !double.IsFinite(value.Y) ||
            !double.IsFinite(value.Width) ||
            !double.IsFinite(value.Height) ||
            value.Width <= 0d ||
            value.Height <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        double left = Math.Clamp(value.X, 0d, 1d);
        double top = Math.Clamp(value.Y, 0d, 1d);
        double right = Math.Clamp(value.X + value.Width, left, 1d);
        double bottom = Math.Clamp(value.Y + value.Height, top, 1d);
        if (right <= left || bottom <= top)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return new Rect(left, top, right - left, bottom - top);
    }

    private static MediaPlaybackState MapState(
        MediaEnginePlaybackState value) =>
        value switch
        {
            MediaEnginePlaybackState.Opening =>
                MediaPlaybackState.Opening,
            MediaEnginePlaybackState.Buffering =>
                MediaPlaybackState.Buffering,
            MediaEnginePlaybackState.Playing =>
                MediaPlaybackState.Playing,
            MediaEnginePlaybackState.Paused =>
                MediaPlaybackState.Paused,
            _ => MediaPlaybackState.None
        };
}

public sealed class MediaPlayer : IDisposable
{
    private readonly SynchronizationContext? _ownerContext =
        SynchronizationContext.Current;
    private readonly int _ownerThreadId =
        Environment.CurrentManagedThreadId;
    private readonly MediaPlaybackEngine _engine;
    private IMediaPlaybackSource? _source;
    private IProGpuMediaPlaybackSource? _typedSource;
    private MediaSourceDescriptor? _ownedSourceDescriptor;
    private bool _isVideoFrameServerEnabled;
    private int _disposed;

    public MediaPlayer()
        : this(null, null)
    {
    }

    internal MediaPlayer(
        MediaProviderRegistry? providers,
        MediaEffectRegistry? effects)
    {
        _engine = new MediaPlaybackEngine(providers, effects);
        PlaybackSession = new MediaPlaybackSession(
            this,
            _engine.Snapshot);
        CommandManager = new MediaPlaybackCommandManager(this);
        _engine.Changed += OnEngineChanged;
        _engine.Opened += OnEngineOpened;
        _engine.Ended += OnEngineEnded;
        _engine.SeekCompleted += OnEngineSeekCompleted;
        _engine.Failed += OnEngineFailed;
        _engine.VideoSurface.FrameAvailable +=
            OnFrameAvailable;
    }

    public IMediaPlaybackSource? Source
    {
        get => _source;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_source, value))
            {
                return;
            }

            if (_typedSource is not null)
            {
                _typedSource.SourceInvalidated -=
                    OnSourceInvalidated;
            }
            if (_source is MediaPlaybackList previousList)
            {
                previousList.DetachPlayer(this);
            }

            _source = value;
            _typedSource = value as IProGpuMediaPlaybackSource;
            if (_source is MediaPlaybackList currentList)
            {
                currentList.AttachPlayer(this);
            }
            if (_typedSource is not null)
            {
                _typedSource.SourceInvalidated +=
                    OnSourceInvalidated;
            }

            RefreshEngineSource();
            CommandManager.Refresh();
            SourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public MediaPlaybackSession PlaybackSession { get; }
    public MediaPlaybackCommandManager CommandManager { get; }
    public bool AutoPlay
    {
        get => _engine.AutoPlay;
        set => _engine.AutoPlay = value;
    }
    public bool IsLoopingEnabled
    {
        get => _engine.IsLoopingEnabled;
        set => _engine.IsLoopingEnabled = value;
    }
    public bool IsMuted
    {
        get => _engine.IsMuted;
        set
        {
            bool oldValue = _engine.IsMuted;
            _engine.IsMuted = value;
            if (oldValue != value)
            {
                IsMutedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    public double Volume
    {
        get => _engine.Volume;
        set
        {
            double oldValue = _engine.Volume;
            _engine.Volume = value;
            if (!oldValue.Equals(_engine.Volume))
            {
                VolumeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    public double AudioBalance
    {
        get => _engine.AudioBalance;
        set => _engine.AudioBalance = value;
    }
    public MediaPlayerAudioCategory AudioCategory
    {
        get => (MediaPlayerAudioCategory)
            _engine.Configuration.AudioCategory;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _engine.Configuration = _engine.Configuration with
            {
                AudioCategory = (MediaAudioCategory)value
            };
        }
    }
    public MediaPlayerAudioDeviceType AudioDeviceType
    {
        get => (MediaPlayerAudioDeviceType)
            _engine.Configuration.AudioDeviceRole;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _engine.Configuration = _engine.Configuration with
            {
                AudioDeviceRole = (MediaAudioDeviceRole)value
            };
        }
    }
    public bool RealTimePlayback
    {
        get => _engine.Configuration.RealTimePlayback;
        set => _engine.Configuration =
            _engine.Configuration with
            {
                RealTimePlayback = value
            };
    }
    public StereoscopicVideoRenderMode
        StereoscopicVideoRenderMode
    {
        get => (StereoscopicVideoRenderMode)
            _engine.Configuration.StereoscopicRenderMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _engine.Configuration = _engine.Configuration with
            {
                StereoscopicRenderMode =
                    (MediaStereoscopicRenderMode)value
            };
        }
    }
    public bool IsVideoFrameServerEnabled
    {
        get => _isVideoFrameServerEnabled;
        set => _isVideoFrameServerEnabled = value;
    }
    public bool CanPause => PlaybackSession.CanPause;
    public bool CanSeek => PlaybackSession.CanSeek;
    public bool IsProtected => PlaybackSession.IsProtected;
    [Obsolete("Use PlaybackSession.BufferingProgress.")]
    public double BufferingProgress =>
        PlaybackSession.BufferingProgress;
    [Obsolete("Use PlaybackSession.NaturalDuration.")]
    public TimeSpan NaturalDuration =>
        PlaybackSession.NaturalDuration;
    [Obsolete("Use PlaybackSession.PlaybackState.")]
    public MediaPlayerState CurrentState =>
        MapLegacyState(PlaybackSession.PlaybackState);
    public double StereoBalance
    {
        get => AudioBalance;
        set => AudioBalance = value;
    }

    [Obsolete("Use PlaybackSession.Position.")]
    public TimeSpan Position
    {
        get => PlaybackSession.Position;
        set => PlaybackSession.Position = value;
    }

    [Obsolete("Use PlaybackSession.PlaybackRate.")]
    public double PlaybackRate
    {
        get => PlaybackSession.PlaybackRate;
        set => PlaybackSession.PlaybackRate = value;
    }

    public event TypedEventHandler<MediaPlayer, object>?
        MediaOpened;
    public event TypedEventHandler<MediaPlayer, object>?
        MediaEnded;
    public event TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>?
        MediaFailed;
    public event TypedEventHandler<MediaPlayer, object>?
        SourceChanged;
    public event TypedEventHandler<MediaPlayer, object>?
        VolumeChanged;
    public event TypedEventHandler<MediaPlayer, object>?
        IsMutedChanged;
    public event TypedEventHandler<MediaPlayer, object>?
        VideoFrameAvailable;
    public event TypedEventHandler<MediaPlayer, object>?
        NaturalVideoDimensionChanged;
    public event TypedEventHandler<MediaPlayer, object>?
        SeekCompleted;
    [Obsolete("Use PlaybackSession.BufferingStarted.")]
    public event TypedEventHandler<MediaPlayer, object>?
        BufferingStarted;
    [Obsolete("Use PlaybackSession.BufferingEnded.")]
    public event TypedEventHandler<MediaPlayer, object>?
        BufferingEnded;
    [Obsolete("Use PlaybackSession.PlaybackStateChanged.")]
    public event TypedEventHandler<MediaPlayer, object>?
        CurrentStateChanged;
    [Obsolete("Use PlaybackSession.PlaybackRateChanged.")]
    public event TypedEventHandler<
        MediaPlayer,
        MediaPlayerRateChangedEventArgs>?
        MediaPlayerRateChanged;
    public event TypedEventHandler<MediaPlayer, object>?
        SubtitleFrameChanged;

    internal event EventHandler? ProGpuFrameAvailable;
    internal MediaGpuSurface ProGpuVideoSurface =>
        _engine.VideoSurface;
    internal MediaPlaybackDiagnosticsSnapshot ProGpuDiagnostics =>
        _engine.Diagnostics;

    public void Play()
    {
        ThrowIfDisposed();
        _engine.Play();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _engine.Pause();
    }

    public void StepForwardOneFrame()
    {
        ThrowIfDisposed();
        _engine.StepForwardOneFrame();
    }

    public void StepBackwardOneFrame()
    {
        ThrowIfDisposed();
        _engine.StepBackwardOneFrame();
    }

    [Obsolete("Set Source to MediaSource.CreateFromUri(uri).")]
    public void SetUriSource(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Source = MediaSource.CreateFromUri(value);
    }

    public void AddAudioEffect(
        string activatableClassId,
        bool effectOptional,
        IPropertySet configuration) =>
        AddEffect(
            activatableClassId,
            MediaEffectKind.Audio,
            effectOptional,
            configuration);

    public void AddVideoEffect(
        string activatableClassId,
        bool effectOptional,
        IPropertySet configuration) =>
        AddEffect(
            activatableClassId,
            MediaEffectKind.Video,
            effectOptional,
            configuration);

    public void RemoveAllEffects()
    {
        ThrowIfDisposed();
        _engine.RemoveAllEffects();
    }

    public void Close() => Dispose();

    internal void Seek(TimeSpan value)
    {
        ThrowIfDisposed();
        _engine.Seek(value);
    }

    internal void SetPlaybackRate(double value)
    {
        ThrowIfDisposed();
        _engine.SetPlaybackRate(value);
    }

    private void AddEffect(
        string activatableClassId,
        MediaEffectKind kind,
        bool optional,
        IPropertySet configuration)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(configuration);
        _engine.AddEffect(
            activatableClassId,
            kind,
            optional,
            new Dictionary<string, object?>(
                configuration,
                StringComparer.Ordinal));
    }

    private void OnSourceInvalidated(
        object? sender,
        EventArgs args)
    {
        RefreshEngineSource();
        CommandManager.Refresh();
    }

    private void RefreshEngineSource()
    {
        MediaSourceDescriptor? previousOwned =
            _ownedSourceDescriptor;
        MediaSourceDescriptor? descriptor;
        MediaPlaybackRange playbackRange =
            MediaPlaybackRange.All;
        try
        {
            descriptor = _typedSource?.ResolveDescriptor();
            if (_typedSource is not null)
            {
                playbackRange =
                    _typedSource.ResolvePlaybackRange();
            }
        }
        catch (InvalidOperationException)
        {
            descriptor = null;
        }

        if (_source is not null && _typedSource is null)
        {
            descriptor = MediaSourceDescriptor.FromCustom(_source);
            _ownedSourceDescriptor = descriptor;
        }
        else
        {
            _ownedSourceDescriptor = null;
        }

        _ = _engine.SetSourceAsync(
            descriptor,
            playbackRange);
        previousOwned?.Dispose();
    }

    private void OnEngineChanged(
        object? sender,
        MediaPlaybackChangedEventArgs args) =>
        Dispatch(() =>
        {
            MediaPlaybackState previousState =
                PlaybackSession.PlaybackState;
            PlaybackSession.AcceptChange(args);
            if ((args.Change & MediaPlaybackChange.State) != 0)
            {
                MediaPlaybackState currentState =
                    PlaybackSession.PlaybackState;
                (_source as MediaPlaybackList)?
                    .SetPlayerPlaybackActive(
                        this,
                        currentState is
                            MediaPlaybackState.Playing or
                            MediaPlaybackState.Buffering);
                CurrentStateChanged?.Invoke(
                    this,
                    EventArgs.Empty);
                if (previousState !=
                        MediaPlaybackState.Buffering &&
                    currentState ==
                        MediaPlaybackState.Buffering)
                {
                    BufferingStarted?.Invoke(
                        this,
                        EventArgs.Empty);
                }
                else if (previousState ==
                             MediaPlaybackState.Buffering &&
                         currentState !=
                             MediaPlaybackState.Buffering)
                {
                    BufferingEnded?.Invoke(
                        this,
                        EventArgs.Empty);
                }
            }
            CommandManager.Refresh();
            if ((args.Change &
                 MediaPlaybackChange.PlaybackRate) != 0)
            {
                MediaPlayerRateChanged?.Invoke(
                    this,
                    new MediaPlayerRateChangedEventArgs(
                        args.Snapshot.PlaybackRate));
            }
            if ((args.Change &
                 MediaPlaybackChange.NaturalVideoSize) != 0)
            {
                NaturalVideoDimensionChanged?.Invoke(
                    this,
                    EventArgs.Empty);
            }
        });

    private void OnEngineOpened(
        object? sender,
        EventArgs args) =>
        Dispatch(() =>
        {
            (_source as MediaPlaybackList)?.RaiseItemOpened();
            MediaOpened?.Invoke(this, EventArgs.Empty);
        });

    private void OnEngineEnded(
        object? sender,
        EventArgs args) =>
        Dispatch(() =>
        {
            if (_source is MediaPlaybackList list &&
                list.MoveNextAfterEnd())
            {
                Play();
                return;
            }
            MediaEnded?.Invoke(this, EventArgs.Empty);
        });

    private void OnEngineSeekCompleted(
        object? sender,
        EventArgs args) =>
        Dispatch(() =>
        {
            PlaybackSession.RaiseSeekCompleted();
            SeekCompleted?.Invoke(this, EventArgs.Empty);
        });

    private void OnEngineFailed(
        object? sender,
        MediaPlaybackFailureEventArgs args)
    {
        var error = args.Failure switch
        {
            MediaPlaybackFailure.Aborted =>
                MediaPlayerError.Aborted,
            MediaPlaybackFailure.Network =>
                MediaPlayerError.NetworkError,
            MediaPlaybackFailure.Decode =>
                MediaPlayerError.DecodingError,
            MediaPlaybackFailure.SourceNotSupported or
            MediaPlaybackFailure.ProviderUnavailable =>
                MediaPlayerError.SourceNotSupported,
            _ => MediaPlayerError.Unknown
        };
        var projected = new MediaPlayerFailedEventArgs(
            error,
            args.Message,
            args.Exception);
        Dispatch(() =>
        {
            if (_source is MediaPlaybackList list)
            {
                list.RaiseItemFailed(
                    new MediaPlaybackItemError(
                        MapItemError(error),
                        args.Exception));
            }
            MediaFailed?.Invoke(this, projected);
        });
    }

    private void OnFrameAvailable(
        object? sender,
        EventArgs args) =>
        Dispatch(() =>
        {
            ProGpuFrameAvailable?.Invoke(this, EventArgs.Empty);
            if (_isVideoFrameServerEnabled)
            {
                VideoFrameAvailable?.Invoke(
                    this,
                    EventArgs.Empty);
            }
        });

    private void Dispatch(Action action)
    {
        SynchronizationContext? context = _ownerContext;
        if (context is not null)
        {
            if (ReferenceEquals(
                    SynchronizationContext.Current,
                    context))
            {
                action();
            }
            else
            {
                context.Post(
                    static state =>
                        ((Action)state!).Invoke(),
                    action);
            }
            return;
        }

        if (Environment.CurrentManagedThreadId ==
            _ownerThreadId)
        {
            action();
            return;
        }

        Action<Action>? dispatcher =
            Microsoft.UI.Xaml.Input.InputSystem
                .DispatcherQueue;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher(action);
    }

    private static MediaPlaybackItemErrorCode MapItemError(
        MediaPlayerError error) =>
        error switch
        {
            MediaPlayerError.Aborted =>
                MediaPlaybackItemErrorCode.Aborted,
            MediaPlayerError.NetworkError =>
                MediaPlaybackItemErrorCode.NetworkError,
            MediaPlayerError.DecodingError =>
                MediaPlaybackItemErrorCode.DecodeError,
            MediaPlayerError.SourceNotSupported =>
                MediaPlaybackItemErrorCode
                    .SourceNotSupportedError,
            _ => MediaPlaybackItemErrorCode.None
        };

#pragma warning disable CS0618
    private static MediaPlayerState MapLegacyState(
        MediaPlaybackState state) =>
        state switch
        {
            MediaPlaybackState.Opening =>
                MediaPlayerState.Opening,
            MediaPlaybackState.Buffering =>
                MediaPlayerState.Buffering,
            MediaPlaybackState.Playing =>
                MediaPlayerState.Playing,
            MediaPlaybackState.Paused =>
                MediaPlayerState.Paused,
            _ => MediaPlayerState.Closed
        };
#pragma warning restore CS0618

    internal void RaiseSubtitleFrameChanged() =>
        SubtitleFrameChanged?.Invoke(this, EventArgs.Empty);

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

        if (_typedSource is not null)
        {
            _typedSource.SourceInvalidated -=
                OnSourceInvalidated;
        }
        if (_source is MediaPlaybackList list)
        {
            list.DetachPlayer(this);
        }
        _engine.Changed -= OnEngineChanged;
        _engine.Opened -= OnEngineOpened;
        _engine.Ended -= OnEngineEnded;
        _engine.SeekCompleted -= OnEngineSeekCompleted;
        _engine.Failed -= OnEngineFailed;
        _engine.VideoSurface.FrameAvailable -=
            OnFrameAvailable;
        _engine.Dispose();
        _ownedSourceDescriptor?.Dispose();
        _ownedSourceDescriptor = null;
        _source = null;
        _typedSource = null;
    }
}
