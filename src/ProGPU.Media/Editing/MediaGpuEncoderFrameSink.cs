using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Media.Editing;

/// <summary>
/// Describes one native encoder target exposed as a WebGPU render attachment.
/// This ProGPU extensibility contract is intentionally separate from the
/// WinUI media-editing surface.
/// </summary>
public readonly struct MediaGpuEncoderFrameSinkCapabilities
{
    public MediaGpuEncoderFrameSinkCapabilities(
        string backendId,
        MediaCompositionExportVideoPath videoPath,
        TextureFormat textureFormat,
        bool hardwareEncoderSurface,
        bool supportsExplicitPresentationTime,
        bool supportsGpuEffects,
        int maximumFramesInFlight)
    {
        if (string.IsNullOrWhiteSpace(backendId))
        {
            throw new ArgumentException(
                "A stable encoder-sink backend identifier is required.",
                nameof(backendId));
        }
        if (videoPath is not (
                MediaCompositionExportVideoPath.NativeGpuSurface or
                MediaCompositionExportVideoPath.GpuCopy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(videoPath),
                "A GPU encoder sink must report NativeGpuSurface or GpuCopy.");
        }
        if (textureFormat == TextureFormat.Undefined)
        {
            throw new ArgumentOutOfRangeException(
                nameof(textureFormat));
        }
        if (maximumFramesInFlight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFramesInFlight));
        }

        BackendId = backendId;
        VideoPath = videoPath;
        TextureFormat = textureFormat;
        HardwareEncoderSurface = hardwareEncoderSurface;
        SupportsExplicitPresentationTime =
            supportsExplicitPresentationTime;
        SupportsGpuEffects = supportsGpuEffects;
        MaximumFramesInFlight = maximumFramesInFlight;
    }

    public string BackendId { get; }

    public MediaCompositionExportVideoPath VideoPath { get; }

    public TextureFormat TextureFormat { get; }

    public bool HardwareEncoderSurface { get; }

    /// <summary>
    /// True only when the sink can assign the requested composition timestamp
    /// to the native encoder input frame before it is submitted.
    /// </summary>
    public bool SupportsExplicitPresentationTime { get; }

    public bool SupportsGpuEffects { get; }

    /// <summary>
    /// Upper bound on acquired frames that may remain incomplete at once.
    /// </summary>
    public int MaximumFramesInFlight { get; }
}

/// <summary>
/// One WebGPU render target borrowed from a native hardware-encoder queue.
/// </summary>
/// <remarks>
/// The texture is sink-owned and must not be disposed by the renderer.
/// <see cref="Complete"/> must be called only after all command buffers that
/// reference the texture have been submitted. Disposing an incomplete frame
/// aborts it and returns its native slot without presenting stale pixels.
/// </remarks>
public interface IMediaGpuEncoderFrame : IDisposable
{
    GpuTexture Texture { get; }

    TimeSpan PresentationTime { get; }

    bool IsCompleted { get; }

    void Complete(bool renderSucceeded);
}

/// <summary>
/// Reusable terminal-state implementation for encoder-frame leases.
/// Providers implement only <see cref="CompleteCore"/> and may pool their
/// derived frame objects when the native queue permits reuse.
/// </summary>
public abstract class MediaGpuEncoderFrame :
    IMediaGpuEncoderFrame
{
    private int _completionState;

    protected MediaGpuEncoderFrame(
        GpuTexture texture,
        TimeSpan presentationTime)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(texture));
        }
        if (presentationTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationTime));
        }

        Texture = texture;
        PresentationTime = presentationTime;
    }

    public GpuTexture Texture { get; }

    public TimeSpan PresentationTime { get; }

    public bool IsCompleted =>
        Volatile.Read(ref _completionState) != 0;

    public void Complete(bool renderSucceeded)
    {
        if (Interlocked.CompareExchange(
                ref _completionState,
                1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "The encoder frame has already been completed or disposed.");
        }

        try
        {
            CompleteCore(renderSucceeded);
        }
        finally
        {
            Volatile.Write(ref _completionState, 2);
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(
                ref _completionState,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            CompleteCore(renderSucceeded: false);
        }
        finally
        {
            Volatile.Write(ref _completionState, 2);
        }
    }

    /// <summary>
    /// Presents or releases the native encoder slot. Called exactly once.
    /// </summary>
    protected abstract void CompleteCore(bool renderSucceeded);
}

/// <summary>
/// Non-blocking frame target for WebGPU composition into a native encoder.
/// </summary>
/// <remarks>
/// <see cref="TryAcquireFrame"/> is O(1) and must not wait for GPU or codec
/// completion. False is normal backpressure: the exporter should drain the
/// native encoder and retry. Implementations must reject disposal while a
/// frame is outstanding or abort those frames deterministically.
/// </remarks>
public interface IMediaGpuEncoderFrameSink : IDisposable
{
    WgpuContext Context { get; }

    uint Width { get; }

    uint Height { get; }

    MediaGpuEncoderFrameSinkCapabilities Capabilities { get; }

    bool TryAcquireFrame(
        TimeSpan presentationTime,
        out IMediaGpuEncoderFrame frame);

    /// <summary>
    /// Drains all frames already submitted to the native codec. It does not
    /// acquire or render another frame.
    /// </summary>
    ValueTask DrainAsync(CancellationToken cancellationToken);
}
