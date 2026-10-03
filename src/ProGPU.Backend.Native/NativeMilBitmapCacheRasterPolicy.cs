namespace ProGPU.Backend.Native;

/// <summary>
/// Original primary-display float scales and actual owned-renderer limits for
/// one coherent cache-raster source revision. These are not receiving-window
/// DPI, precomputed output pixels or inferred/default texture limits.
/// </summary>
public readonly record struct NativeMilBitmapCacheRasterPolicy(
    float PrimaryDpiScaleX,
    float PrimaryDpiScaleY,
    uint MaximumTextureWidth,
    uint MaximumTextureHeight,
    ulong SourceRevision);
