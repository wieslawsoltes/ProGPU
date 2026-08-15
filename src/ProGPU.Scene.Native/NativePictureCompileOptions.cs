using System.Numerics;

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
public readonly record struct NativePictureCompileOptions
{
    public NativePictureCompileOptions(float dpiScale)
        : this(
            dpiScale,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero)
    {
    }

    public NativePictureCompileOptions(
        float dpiScale,
        Matrix4x4 projection3D,
        Matrix4x4 view3D,
        Vector3 cameraPosition3D)
    {
        DpiScale = dpiScale;
        Projection3D = projection3D;
        View3D = view3D;
        CameraPosition3D = cameraPosition3D;
    }

    public static NativePictureCompileOptions Default => new(1f);

    public float DpiScale { get; }

    public Matrix4x4 Projection3D { get; }

    public Matrix4x4 View3D { get; }

    public Vector3 CameraPosition3D { get; }

    internal bool IsValid =>
        float.IsFinite(DpiScale) && DpiScale > 0f &&
        IsFinite(Projection3D) && IsFinite(View3D) &&
        float.IsFinite(CameraPosition3D.X) &&
        float.IsFinite(CameraPosition3D.Y) &&
        float.IsFinite(CameraPosition3D.Z);

    private static bool IsFinite(in Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
