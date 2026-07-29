namespace ProGPU.Media.Playback;

/// <summary>
/// Framework-neutral audio routing category. Numeric values intentionally
/// match the corresponding WinUI media contract.
/// </summary>
public enum MediaAudioCategory
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

/// <summary>
/// Primary role used when a native provider selects an audio endpoint.
/// </summary>
public enum MediaAudioDeviceRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

public enum MediaStereoscopicRenderMode
{
    Mono = 0,
    Stereo = 1
}

/// <summary>
/// Portable provider configuration applied before a provider is opened and
/// whenever an active player changes one of these settings.
/// </summary>
public readonly record struct MediaPlaybackConfiguration(
    MediaAudioCategory AudioCategory,
    MediaAudioDeviceRole AudioDeviceRole,
    bool RealTimePlayback,
    MediaStereoscopicRenderMode StereoscopicRenderMode)
{
    public static MediaPlaybackConfiguration Default { get; } = new(
        MediaAudioCategory.Media,
        MediaAudioDeviceRole.Multimedia,
        RealTimePlayback: false,
        MediaStereoscopicRenderMode.Mono);
}

/// <summary>
/// Optional contract for providers that can consume native audio-routing,
/// latency, or stereoscopic presentation configuration.
/// </summary>
public interface IMediaPlaybackConfigurationProvider
{
    void ApplyConfiguration(
        in MediaPlaybackConfiguration configuration);
}
