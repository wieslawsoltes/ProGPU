using ProGPU.Media.Effects;
using ProGPU.Media.Diagnostics;

namespace ProGPU.Media.Playback;

public enum MediaEnginePlaybackState
{
    None,
    Opening,
    Buffering,
    Playing,
    Paused
}

public enum MediaPlaybackFailure
{
    Unknown,
    Aborted,
    Network,
    Decode,
    SourceNotSupported,
    ProviderUnavailable,
    DeviceLost
}

/// <summary>
/// Provider-neutral virtual range over a media source. Providers continue to
/// report absolute source timestamps; <see cref="MediaPlaybackEngine"/>
/// projects them into this relative timeline and translates seeks back to the
/// source domain. Construction and projection are O(1) and allocation-free.
/// </summary>
public readonly record struct MediaPlaybackRange
{
    public MediaPlaybackRange(
        TimeSpan startTime,
        TimeSpan? durationLimit = null)
    {
        if (startTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startTime));
        }
        if (durationLimit < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationLimit));
        }

        StartTime = startTime;
        DurationLimit = durationLimit;
    }

    public static MediaPlaybackRange All { get; } =
        new(TimeSpan.Zero);

    public TimeSpan StartTime { get; }
    public TimeSpan? DurationLimit { get; }

    public bool IsIdentity =>
        StartTime == TimeSpan.Zero &&
        DurationLimit is null;
}

public readonly record struct MediaProviderCapabilities(
    bool CanPause,
    bool CanSeek,
    bool SupportsRate,
    bool SupportsFrameStepping,
    bool HardwareDecoded,
    bool HasAudio,
    bool HasVideo)
{
    public static MediaProviderCapabilities Empty { get; } = new(
        CanPause: false,
        CanSeek: false,
        SupportsRate: false,
        SupportsFrameStepping: false,
        HardwareDecoded: false,
        HasAudio: false,
        HasVideo: false);
}

public readonly record struct MediaPlaybackSnapshot(
    MediaEnginePlaybackState State,
    TimeSpan Position,
    TimeSpan NaturalDuration,
    uint NaturalVideoWidth,
    uint NaturalVideoHeight,
    double BufferingProgress,
    double DownloadProgress,
    double PlaybackRate,
    MediaProviderCapabilities Capabilities)
{
    public static MediaPlaybackSnapshot Empty { get; } = new(
        MediaEnginePlaybackState.None,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        0,
        0d,
        0d,
        1d,
        MediaProviderCapabilities.Empty);

    internal MediaPlaybackSnapshot Normalize()
    {
        TimeSpan duration = NaturalDuration < TimeSpan.Zero
            ? TimeSpan.Zero
            : NaturalDuration;
        TimeSpan position = Position < TimeSpan.Zero
            ? TimeSpan.Zero
            : Position;
        if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        double rate = double.IsFinite(PlaybackRate) &&
                      PlaybackRate > 0d
            ? PlaybackRate
            : 1d;

        return this with
        {
            Position = position,
            NaturalDuration = duration,
            BufferingProgress = Math.Clamp(BufferingProgress, 0d, 1d),
            DownloadProgress = Math.Clamp(DownloadProgress, 0d, 1d),
            PlaybackRate = rate
        };
    }
}

public sealed class MediaPlaybackFailureEventArgs : EventArgs
{
    public MediaPlaybackFailureEventArgs(
        MediaPlaybackFailure failure,
        string message,
        Exception? exception = null)
    {
        Failure = failure;
        Message = message ?? string.Empty;
        Exception = exception;
    }

    public MediaPlaybackFailure Failure { get; }
    public string Message { get; }
    public Exception? Exception { get; }
}

[Flags]
public enum MediaPlaybackChange
{
    None = 0,
    State = 1 << 0,
    Position = 1 << 1,
    Duration = 1 << 2,
    NaturalVideoSize = 1 << 3,
    Buffering = 1 << 4,
    Download = 1 << 5,
    PlaybackRate = 1 << 6,
    Capabilities = 1 << 7,
    Source = 1 << 8
}

public sealed class MediaPlaybackChangedEventArgs : EventArgs
{
    public MediaPlaybackChangedEventArgs(
        MediaPlaybackChange change,
        MediaPlaybackSnapshot snapshot)
    {
        Change = change;
        Snapshot = snapshot;
    }

    public MediaPlaybackChange Change { get; }
    public MediaPlaybackSnapshot Snapshot { get; }
}

public interface IMediaPlaybackProvider : IDisposable
{
    string Id { get; }

    ValueTask OpenAsync(CancellationToken cancellationToken);

    void Play();
    void Pause();
    void Seek(TimeSpan position);
    void SetPlaybackRate(double value);
    void SetVolume(double volume, double balance, bool muted);
    void SetLooping(bool enabled);
    bool StepForwardOneFrame();
    bool StepBackwardOneFrame();
    void AddEffect(IMediaEffect effect, bool optional);
    void RemoveAllEffects();
}

public interface IMediaPlaybackSink
{
    void Update(in MediaPlaybackSnapshot snapshot);
    void UpdateTracks(MediaPlaybackTracksSnapshot tracks);
    void Opened(in MediaPlaybackSnapshot snapshot);
    void Ended();
    void SeekCompleted(TimeSpan position);
    void Failed(
        MediaPlaybackFailure failure,
        string message,
        Exception? exception = null);
    void Present(IMediaGpuFrame frame);
    void UpdateDiagnostics(
        in MediaProviderDiagnostics diagnostics);
}

public interface IMediaPlaybackProviderFactory
{
    string Id { get; }
    int Priority { get; }

    bool CanOpen(MediaSourceDescriptor source);

    ValueTask<IMediaPlaybackProvider> CreateAsync(
        MediaSourceDescriptor source,
        IMediaPlaybackSink sink,
        CancellationToken cancellationToken);
}
