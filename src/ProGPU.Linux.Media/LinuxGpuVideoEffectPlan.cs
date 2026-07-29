using ProGPU.Backend;

namespace ProGPU.Linux.Media;

/// <summary>
/// Immutable Linux execution form of the portable video-effect plan.
/// </summary>
internal readonly record struct LinuxGpuVideoEffectPlan
{
    private readonly GpuTextureColorTransform
        _colorTransform;
    private readonly bool _isInitialized;

    internal LinuxGpuVideoEffectPlan(
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
        _colorTransform = colorTransform;
        BlurStandardDeviation =
            blurStandardDeviation;
        _isInitialized = true;
    }

    internal static LinuxGpuVideoEffectPlan Identity =>
        new(
            GpuTextureColorTransform.Identity,
            0f);

    internal GpuTextureColorTransform ColorTransform =>
        _isInitialized
            ? _colorTransform
            : GpuTextureColorTransform.Identity;

    internal float BlurStandardDeviation { get; }

    internal bool HasSpatialEffect =>
        BlurStandardDeviation > 0f;

    internal bool IsIdentity =>
        ColorTransform ==
            GpuTextureColorTransform.Identity &&
        !HasSpatialEffect;
}
