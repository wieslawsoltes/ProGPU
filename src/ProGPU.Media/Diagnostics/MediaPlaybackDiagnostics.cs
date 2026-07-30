using ProGPU.Media.Playback;

namespace ProGPU.Media.Diagnostics;

public readonly record struct MediaPlaybackDiagnosticsSnapshot(
    string? ProviderId,
    bool HardwareDecoded,
    MediaTransferMode? TransferMode,
    long PresentedFrames,
    long DroppedFrames,
    int VideoQueueDepth,
    int AudioQueueDepth,
    TimeSpan AudioLatency,
    string? LastFallbackReason)
{
    public static MediaPlaybackDiagnosticsSnapshot Empty { get; } =
        new(
            ProviderId: null,
            HardwareDecoded: false,
            TransferMode: null,
            PresentedFrames: 0,
            DroppedFrames: 0,
            VideoQueueDepth: 0,
            AudioQueueDepth: 0,
            AudioLatency: TimeSpan.Zero,
            LastFallbackReason: null);
}

/// <summary>
/// Provider-owned diagnostic fields. Counters are absolute values so a
/// provider can publish a coherent snapshot without read/modify/write races.
/// </summary>
public readonly record struct MediaProviderDiagnostics(
    bool HardwareDecoded,
    MediaTransferMode? TransferMode,
    long DroppedFrames,
    int VideoQueueDepth,
    int AudioQueueDepth,
    TimeSpan AudioLatency,
    string? LastFallbackReason);
