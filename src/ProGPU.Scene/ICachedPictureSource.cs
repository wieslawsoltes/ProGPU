using System;

namespace ProGPU.Scene;

/// <summary>
/// A typed, rendering-thread source of owned picture recordings. Raise
/// Invalidated before the next render when content, bounds or raster policy
/// changes. Capture must return a stable snapshot without submitting GPU work.
/// </summary>
public interface ICachedPictureSource : IDisposable
{
    event EventHandler? Invalidated;
    CachedPictureSnapshot Capture();
}

/// <summary>
/// Transfers one picture lease to the receiver. Disposing releases that lease;
/// a CachedPicture retains its own independent lease before disposal.
/// </summary>
public readonly record struct CachedPictureSnapshot(
    GpuPicture Picture, Rect Bounds, float RenderScale = 1, bool EnableClearType = true) : IDisposable
{
    public void Dispose() => Picture?.Dispose();
}
