namespace ProGPU.Wpf.Interop;

/// <summary>
/// Explicit source and device inputs for a BitmapCacheBrush shader's raw cache
/// texture. This is not the receiving window's DPI or the brush's paint mapping.
/// </summary>
/// <remarks>
/// Scales retain the source's original float values. Limits must come from the
/// actual live rendering device. SourceRevision identifies a coherent owned
/// source-policy snapshot; the host separately retains source/device identities
/// and lifetimes. No field is inferred or replaced with a default. Consumers
/// validate the complete policy before publishing a new capture generation.
/// These inputs do not themselves prove device availability or compute the
/// selected cache's raster dimensions, transform or source ownership.
/// </remarks>
public readonly record struct PortableBitmapCacheRasterPolicy(
    float PrimaryDpiScaleX,
    float PrimaryDpiScaleY,
    uint MaximumTextureWidth,
    uint MaximumTextureHeight,
    ulong SourceRevision)
{
    public bool IsValid =>
        float.IsFinite(PrimaryDpiScaleX) && PrimaryDpiScaleX > 0 &&
        float.IsFinite(PrimaryDpiScaleY) && PrimaryDpiScaleY > 0 &&
        MaximumTextureWidth != 0 && MaximumTextureHeight != 0 &&
        SourceRevision != 0;
}
