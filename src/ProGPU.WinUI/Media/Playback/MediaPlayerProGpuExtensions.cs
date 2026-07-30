using ProGPU.Media.Diagnostics;
using ProGPU.Media.Playback;
using Windows.Media;
using Windows.Media.Playback;

namespace ProGPU.Media;

public enum ProGpuMediaPlaybackCommandKind
{
    Play,
    Pause,
    Next,
    Previous,
    FastForward,
    Rewind,
    Position,
    PlaybackRate,
    Shuffle,
    AutoRepeatMode
}

public readonly record struct ProGpuMediaPlaybackCommand(
    ProGpuMediaPlaybackCommandKind Kind,
    TimeSpan Position = default,
    double PlaybackRate = 1d,
    bool IsShuffleRequested = false,
    MediaPlaybackAutoRepeatMode AutoRepeatMode =
        MediaPlaybackAutoRepeatMode.None);

public static class MediaPlayerProGpuExtensions
{
    public static MediaGpuSurface GetProGpuSurface(
        this Windows.Media.Playback.MediaPlayer mediaPlayer)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);
        return mediaPlayer.ProGpuVideoSurface;
    }

    public static MediaPlaybackDiagnosticsSnapshot
        GetProGpuDiagnostics(
            this Windows.Media.Playback.MediaPlayer mediaPlayer)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);
        return mediaPlayer.ProGpuDiagnostics;
    }

    /// <summary>
    /// Typed input seam for platform adapters that receive media commands
    /// from native system transport controls. Dispatch raises the official
    /// WinUI command-manager event and then performs its default action unless
    /// the handler marks the request handled.
    /// </summary>
    public static bool TryDispatchProGpuCommand(
        this MediaPlayer mediaPlayer,
        in ProGpuMediaPlaybackCommand command)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);
        MediaPlaybackCommandManager manager =
            mediaPlayer.CommandManager;
        if (!manager.IsEnabled)
        {
            return false;
        }

        switch (command.Kind)
        {
            case ProGpuMediaPlaybackCommandKind.Play:
                if (!manager.PlayBehavior.IsEnabled)
                {
                    return false;
                }
                manager.ReceivePlay();
                return true;
            case ProGpuMediaPlaybackCommandKind.Pause:
                if (!manager.PauseBehavior.IsEnabled)
                {
                    return false;
                }
                manager.ReceivePause();
                return true;
            case ProGpuMediaPlaybackCommandKind.Next:
                if (!manager.NextBehavior.IsEnabled)
                {
                    return false;
                }
                manager.ReceiveNext();
                return true;
            case ProGpuMediaPlaybackCommandKind.Previous:
                if (!manager.PreviousBehavior.IsEnabled)
                {
                    return false;
                }
                manager.ReceivePrevious();
                return true;
            case ProGpuMediaPlaybackCommandKind.FastForward:
                if (!manager.FastForwardBehavior.IsEnabled)
                {
                    return false;
                }
                manager.ReceiveFastForward();
                return true;
            case ProGpuMediaPlaybackCommandKind.Rewind:
                if (!manager.RewindBehavior.IsEnabled)
                {
                    return false;
                }
                manager.ReceiveRewind();
                return true;
            case ProGpuMediaPlaybackCommandKind.Position:
                if (!manager.PositionBehavior.IsEnabled)
                {
                    return false;
                }
                manager.ReceivePosition(command.Position);
                return true;
            case ProGpuMediaPlaybackCommandKind.PlaybackRate:
                if (!manager.RateBehavior.IsEnabled ||
                    !double.IsFinite(command.PlaybackRate) ||
                    command.PlaybackRate <= 0d)
                {
                    return false;
                }
                manager.ReceiveRate(command.PlaybackRate);
                return true;
            case ProGpuMediaPlaybackCommandKind.Shuffle:
                if (!manager.ShuffleBehavior.IsEnabled)
                {
                    return false;
                }
                manager.ReceiveShuffle(
                    command.IsShuffleRequested);
                return true;
            case ProGpuMediaPlaybackCommandKind.AutoRepeatMode:
                if (!manager.AutoRepeatModeBehavior.IsEnabled ||
                    !Enum.IsDefined(command.AutoRepeatMode))
                {
                    return false;
                }
                manager.ReceiveAutoRepeatMode(
                    command.AutoRepeatMode);
                return true;
            default:
                return false;
        }
    }
}
