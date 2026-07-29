using Windows.Foundation;
using Windows.Media;

namespace Windows.Media.Playback;

public enum MediaCommandEnablingRule
{
    Auto = 0,
    Always = 1,
    Never = 2
}

internal sealed class MediaCommandDeferralState
{
    private readonly object _gate = new();
    private int _deferrals;
    private bool _sealed;
    private Action? _continuation;

    public Deferral GetDeferral()
    {
        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "A deferral cannot be requested after command dispatch completes.");
            }
            _deferrals++;
        }
        return new Deferral(Complete);
    }

    public void Seal(Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        bool run;
        lock (_gate)
        {
            _sealed = true;
            _continuation = continuation;
            run = _deferrals == 0;
            if (run)
            {
                _continuation = null;
            }
        }
        if (run)
        {
            continuation();
        }
    }

    private void Complete()
    {
        Action? continuation = null;
        lock (_gate)
        {
            if (_deferrals == 0)
            {
                return;
            }
            _deferrals--;
            if (_sealed && _deferrals == 0)
            {
                continuation = _continuation;
                _continuation = null;
            }
        }
        continuation?.Invoke();
    }
}

public sealed class MediaPlaybackCommandManagerCommandBehavior
{
    private readonly Func<bool> _autoEnabled;
    private MediaCommandEnablingRule _enablingRule;
    private bool _isEnabled;

    internal MediaPlaybackCommandManagerCommandBehavior(
        MediaPlaybackCommandManager commandManager,
        Func<bool> autoEnabled)
    {
        CommandManager = commandManager;
        _autoEnabled = autoEnabled;
        _isEnabled = autoEnabled();
    }

    public MediaPlaybackCommandManager CommandManager { get; }
    public MediaCommandEnablingRule EnablingRule
    {
        get => _enablingRule;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_enablingRule == value)
            {
                return;
            }
            _enablingRule = value;
            Refresh();
        }
    }
    public bool IsEnabled => _isEnabled;

    public event TypedEventHandler<
        MediaPlaybackCommandManagerCommandBehavior,
        object>?
        IsEnabledChanged;

    internal void Refresh()
    {
        bool enabled = _enablingRule switch
        {
            MediaCommandEnablingRule.Always => true,
            MediaCommandEnablingRule.Never => false,
            _ => _autoEnabled()
        };
        if (_isEnabled == enabled)
        {
            return;
        }
        _isEnabled = enabled;
        IsEnabledChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class MediaPlaybackCommandManagerPlayReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManagerPauseReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManagerNextReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManagerPreviousReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManagerFastForwardReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManagerRewindReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManagerPositionReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();

    internal MediaPlaybackCommandManagerPositionReceivedEventArgs(
        TimeSpan position)
    {
        Position = position;
    }

    public TimeSpan Position { get; }
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManagerRateReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();

    internal MediaPlaybackCommandManagerRateReceivedEventArgs(
        double playbackRate)
    {
        PlaybackRate = playbackRate;
    }

    public double PlaybackRate { get; }
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManagerShuffleReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();

    internal MediaPlaybackCommandManagerShuffleReceivedEventArgs(
        bool isShuffleRequested)
    {
        IsShuffleRequested = isShuffleRequested;
    }

    public bool IsShuffleRequested { get; }
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class
    MediaPlaybackCommandManagerAutoRepeatModeReceivedEventArgs
{
    private readonly MediaCommandDeferralState _deferrals = new();

    internal
        MediaPlaybackCommandManagerAutoRepeatModeReceivedEventArgs(
            MediaPlaybackAutoRepeatMode autoRepeatMode)
    {
        AutoRepeatMode = autoRepeatMode;
    }

    public MediaPlaybackAutoRepeatMode AutoRepeatMode { get; }
    public bool Handled { get; set; }
    public Deferral GetDeferral() => _deferrals.GetDeferral();
    internal void Seal(Action continuation) =>
        _deferrals.Seal(continuation);
}

public sealed class MediaPlaybackCommandManager
{
    private bool _isEnabled = true;

    internal MediaPlaybackCommandManager(MediaPlayer mediaPlayer)
    {
        MediaPlayer = mediaPlayer;
        AutoRepeatModeBehavior = Create(
            () => MediaPlayer.Source is MediaPlaybackList);
        FastForwardBehavior = Create(
            () => MediaPlayer.PlaybackSession
                .IsSupportedPlaybackRateRange(1d, 2d));
        NextBehavior = Create(CanMoveNext);
        PauseBehavior = Create(
            () => MediaPlayer.CanPause &&
                MediaPlayer.PlaybackSession.PlaybackState is
                    MediaPlaybackState.Playing or
                    MediaPlaybackState.Buffering);
        PlayBehavior = Create(
            () => MediaPlayer.Source is not null &&
                MediaPlayer.PlaybackSession.PlaybackState !=
                    MediaPlaybackState.Playing);
        PositionBehavior = Create(() => MediaPlayer.CanSeek);
        PreviousBehavior = Create(CanMovePrevious);
        RateBehavior = Create(
            () => MediaPlayer.PlaybackSession
                .IsSupportedPlaybackRateRange(0.5d, 2d));
        RewindBehavior = Create(() => MediaPlayer.CanSeek);
        ShuffleBehavior = Create(
            () => MediaPlayer.Source is MediaPlaybackList);
    }

    public MediaPlayer MediaPlayer { get; }
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }
            _isEnabled = value;
            Refresh();
        }
    }
    public MediaPlaybackCommandManagerCommandBehavior
        AutoRepeatModeBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        FastForwardBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        NextBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        PauseBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        PlayBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        PositionBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        PreviousBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        RateBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        RewindBehavior { get; }
    public MediaPlaybackCommandManagerCommandBehavior
        ShuffleBehavior { get; }

    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerAutoRepeatModeReceivedEventArgs>?
        AutoRepeatModeReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerFastForwardReceivedEventArgs>?
        FastForwardReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerNextReceivedEventArgs>?
        NextReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerPauseReceivedEventArgs>?
        PauseReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerPlayReceivedEventArgs>?
        PlayReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerPositionReceivedEventArgs>?
        PositionReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerPreviousReceivedEventArgs>?
        PreviousReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerRateReceivedEventArgs>?
        RateReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerRewindReceivedEventArgs>?
        RewindReceived;
    public event TypedEventHandler<
        MediaPlaybackCommandManager,
        MediaPlaybackCommandManagerShuffleReceivedEventArgs>?
        ShuffleReceived;

    internal void Refresh()
    {
        AutoRepeatModeBehavior.Refresh();
        FastForwardBehavior.Refresh();
        NextBehavior.Refresh();
        PauseBehavior.Refresh();
        PlayBehavior.Refresh();
        PositionBehavior.Refresh();
        PreviousBehavior.Refresh();
        RateBehavior.Refresh();
        RewindBehavior.Refresh();
        ShuffleBehavior.Refresh();
    }

    internal void ReceivePlay()
    {
        var args =
            new MediaPlaybackCommandManagerPlayReceivedEventArgs();
        PlayReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled && CanExecute(PlayBehavior))
            {
                MediaPlayer.Play();
            }
        });
    }

    internal void ReceivePause()
    {
        var args =
            new MediaPlaybackCommandManagerPauseReceivedEventArgs();
        PauseReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled && CanExecute(PauseBehavior))
            {
                MediaPlayer.Pause();
            }
        });
    }

    internal void ReceiveNext()
    {
        var args =
            new MediaPlaybackCommandManagerNextReceivedEventArgs();
        NextReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled && CanExecute(NextBehavior) &&
                MediaPlayer.Source is MediaPlaybackList list)
            {
                list.MoveNext();
            }
        });
    }

    internal void ReceivePrevious()
    {
        var args =
            new MediaPlaybackCommandManagerPreviousReceivedEventArgs();
        PreviousReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled &&
                CanExecute(PreviousBehavior) &&
                MediaPlayer.Source is MediaPlaybackList list)
            {
                list.MovePrevious();
            }
        });
    }

    internal void ReceivePosition(TimeSpan position)
    {
        var args =
            new MediaPlaybackCommandManagerPositionReceivedEventArgs(
                position);
        PositionReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled && CanExecute(PositionBehavior))
            {
                MediaPlayer.PlaybackSession.Position = position;
            }
        });
    }

    internal void ReceiveRate(double playbackRate)
    {
        var args =
            new MediaPlaybackCommandManagerRateReceivedEventArgs(
                playbackRate);
        RateReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled && CanExecute(RateBehavior))
            {
                MediaPlayer.PlaybackSession.PlaybackRate =
                    playbackRate;
            }
        });
    }

    internal void ReceiveShuffle(bool requested)
    {
        var args =
            new MediaPlaybackCommandManagerShuffleReceivedEventArgs(
                requested);
        ShuffleReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled && CanExecute(ShuffleBehavior) &&
                MediaPlayer.Source is MediaPlaybackList list)
            {
                list.ShuffleEnabled = requested;
            }
        });
    }

    internal void ReceiveAutoRepeatMode(
        MediaPlaybackAutoRepeatMode mode)
    {
        var args =
            new MediaPlaybackCommandManagerAutoRepeatModeReceivedEventArgs(
                mode);
        AutoRepeatModeReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (args.Handled ||
                !CanExecute(AutoRepeatModeBehavior))
            {
                return;
            }
            if (MediaPlayer.Source is MediaPlaybackList list)
            {
                list.AutoRepeatEnabled =
                    mode == MediaPlaybackAutoRepeatMode.List;
                MediaPlayer.IsLoopingEnabled =
                    mode == MediaPlaybackAutoRepeatMode.Track;
            }
        });
    }

    internal void ReceiveFastForward()
    {
        var args =
            new MediaPlaybackCommandManagerFastForwardReceivedEventArgs();
        FastForwardReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled &&
                CanExecute(FastForwardBehavior))
            {
                MediaPlayer.PlaybackSession.PlaybackRate = 2d;
            }
        });
    }

    internal void ReceiveRewind()
    {
        var args =
            new MediaPlaybackCommandManagerRewindReceivedEventArgs();
        RewindReceived?.Invoke(this, args);
        args.Seal(() =>
        {
            if (!args.Handled && CanExecute(RewindBehavior))
            {
                TimeSpan position =
                    MediaPlayer.PlaybackSession.Position -
                    TimeSpan.FromSeconds(10);
                MediaPlayer.PlaybackSession.Position =
                    position < TimeSpan.Zero
                        ? TimeSpan.Zero
                        : position;
            }
        });
    }

    private MediaPlaybackCommandManagerCommandBehavior Create(
        Func<bool> resolver) =>
        new(this, resolver);

    private bool CanExecute(
        MediaPlaybackCommandManagerCommandBehavior behavior) =>
        _isEnabled && behavior.IsEnabled;

    private bool CanMoveNext()
    {
        if (MediaPlayer.Source is not MediaPlaybackList list ||
            list.Items.Count == 0)
        {
            return false;
        }
        return list.AutoRepeatEnabled ||
            list.CurrentItemIndex == uint.MaxValue ||
            list.CurrentItemIndex + 1u <
                (uint)list.Items.Count;
    }

    private bool CanMovePrevious()
    {
        if (MediaPlayer.Source is not MediaPlaybackList list ||
            list.Items.Count == 0)
        {
            return false;
        }
        return list.AutoRepeatEnabled ||
            list.CurrentItemIndex == uint.MaxValue ||
            list.CurrentItemIndex > 0u;
    }
}
