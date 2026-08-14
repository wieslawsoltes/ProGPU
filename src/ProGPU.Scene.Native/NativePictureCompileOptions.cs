namespace ProGPU.Scene.Native;

/// <summary>
/// Defines target-sensitive inputs used while lowering an immutable managed
/// picture to the native retained scene ABI.
/// </summary>
/// <remarks>
/// Text atlas records contain physical raster sizes. A compiled picture is
/// therefore valid for the target DPI recorded here and must be rebuilt when
/// that DPI changes, matching the managed compiled-scene invalidation contract.
/// </remarks>
public readonly record struct NativePictureCompileOptions(float DpiScale)
{
    public static NativePictureCompileOptions Default => new(1f);

    internal bool IsValid => float.IsFinite(DpiScale) && DpiScale > 0f;
}
