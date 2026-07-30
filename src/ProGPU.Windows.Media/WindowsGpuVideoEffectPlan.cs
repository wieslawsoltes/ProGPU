using ProGPU.Backend;

namespace ProGPU.Windows.Media;

/// <summary>
/// Immutable Windows execution form of the portable composition effect plan.
/// </summary>
internal readonly record struct
    WindowsGpuVideoEffectPlan
{
    private readonly bool _isInitialized;

    internal WindowsGpuVideoEffectPlan(
        GpuTextureColorTransform colorTransform,
        float blurStandardDeviation)
    {
        if (!float.IsFinite(
                blurStandardDeviation) ||
            blurStandardDeviation < 0f ||
            blurStandardDeviation >
                GpuTextureGaussianBlur
                    .MaximumStandardDeviation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blurStandardDeviation));
        }

        ColorTransform = colorTransform;
        BlurStandardDeviation =
            blurStandardDeviation;
        _isInitialized = true;
    }

    internal static WindowsGpuVideoEffectPlan Identity =>
        new(
            GpuTextureColorTransform.Identity,
            0f);

    internal GpuTextureColorTransform ColorTransform =>
        _isInitialized
            ? field
            : GpuTextureColorTransform.Identity;

    internal float BlurStandardDeviation { get; }

    internal bool HasSpatialEffect =>
        BlurStandardDeviation > 0f;

    internal bool IsIdentity =>
        ColorTransform ==
            GpuTextureColorTransform.Identity &&
        !HasSpatialEffect;
}
