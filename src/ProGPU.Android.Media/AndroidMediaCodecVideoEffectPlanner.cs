using System.Globalization;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Android.Media;

/// <summary>
/// Pure capture of Android's portable affine and clamped-Gaussian media
/// effect plan. Native resources are not created during capability checks.
/// </summary>
internal static class AndroidMediaCodecVideoEffectPlanner
{
    internal static bool TryGetBuiltInEffects(
        IReadOnlyDictionary<string, string> userData,
        out float saturation,
        out float grayscale)
    {
        ArgumentNullException.ThrowIfNull(userData);
        saturation = 1f;
        grayscale = 0f;
        if (userData.TryGetValue(
                "progpu.saturation",
                out string? saturationText) &&
            (!float.TryParse(
                saturationText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out saturation) ||
             !float.IsFinite(saturation) ||
             saturation is < 0f or > 1f))
        {
            return false;
        }
        if (userData.TryGetValue(
                "progpu.grayscale",
                out string? grayscaleText) &&
            (!float.TryParse(
                grayscaleText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out grayscale) ||
             !float.IsFinite(grayscale) ||
             grayscale is < 0f or > 1f))
        {
            return false;
        }
        return true;
    }

    internal static bool TryGetVideoEffectPlan(
        MediaCompositionExportClip clip,
        MediaEffectRegistry effects,
        out MediaVideoEffectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(effects);
        plan = MediaVideoEffectPlan.Identity;
        if (!TryGetBuiltInEffects(
                clip.UserData,
                out float saturation,
                out float grayscale) ||
            !MediaCompositionVideoEffectResolver
                .TryCapturePlan(
                    effects,
                    clip.VideoEffectDefinitions,
                    out MediaVideoEffectPlan
                        declared))
        {
            return false;
        }

        MediaVideoColorTransform transform =
            MediaVideoColorEffectFactory
                .CreateTransform(
                    saturation: saturation,
                    grayscale: grayscale)
                .Then(declared.ColorTransform);
        plan = new MediaVideoEffectPlan(
            transform,
            declared.BlurStandardDeviation);
        return true;
    }
}
