namespace ProGPU.Media.Editing;

using ProGPU.Media.Effects;

/// <summary>
/// Activates ordered WinUI-shaped video-effect definitions through the typed
/// registry and folds compatible nodes into one portable GPU effect plan.
/// </summary>
public static class MediaCompositionVideoEffectResolver
{
    /// <summary>
    /// Captures the portable GPU effect plan. Affine nodes retain their
    /// declared order. Clamped Gaussian nodes combine by variance, so E
    /// definitions require O(E) time and O(1) working storage.
    /// </summary>
    public static bool TryCapturePlan(
        MediaEffectRegistry registry,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        out MediaVideoEffectPlan plan) =>
        TryCapture(
            registry,
            definitions,
            allowSpatialEffects: true,
            out plan);

    /// <summary>
    /// Returns false when a definition is unregistered or cannot execute as
    /// an affine GPU color node. Work is O(E) for E definitions with O(1)
    /// working storage; no reflection or assembly scanning is performed.
    /// </summary>
    public static bool TryCaptureColorTransform(
        MediaEffectRegistry registry,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        out MediaVideoColorTransform transform)
    {
        bool captured = TryCapture(
            registry,
            definitions,
            allowSpatialEffects: false,
            out MediaVideoEffectPlan plan);
        transform = plan.ColorTransform;
        return captured;
    }

    private static bool TryCapture(
        MediaEffectRegistry registry,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        bool allowSpatialEffects,
        out MediaVideoEffectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);

        MediaVideoColorTransform transform =
            MediaVideoColorTransform.Identity;
        double blurVariance = 0d;
        plan = MediaVideoEffectPlan.Identity;
        for (int index = 0;
             index < definitions.Count;
             index++)
        {
            MediaCompositionEffectDefinition definition =
                definitions[index];
            var descriptor = new MediaEffectDescriptor(
                definition.ActivatableClassId,
                MediaEffectKind.Video,
                definition.Properties);
            IMediaEffect? effect = null;
            try
            {
                if (!registry.TryCreate(
                        descriptor,
                        out effect) ||
                    effect is not IMediaVideoGraphEffect graph)
                {
                    return false;
                }

                MediaVideoGraphEffectState state =
                    graph.CaptureState();
                switch (state.Kind)
                {
                    case MediaVideoGraphEffectKind
                        .ColorTransform:
                        transform = transform.Then(
                            state.ColorTransform);
                        break;
                    case MediaVideoGraphEffectKind
                        .GaussianBlur
                        when allowSpatialEffects:
                        blurVariance +=
                            (double)
                                state
                                    .BlurStandardDeviation *
                            state.BlurStandardDeviation;
                        double maximum =
                            MediaVideoGaussianBlurEffectFactory
                                .MaximumStandardDeviation;
                        if (blurVariance >
                            maximum * maximum)
                        {
                            plan =
                                MediaVideoEffectPlan
                                    .Identity;
                            return false;
                        }
                        break;
                    default:
                        plan =
                            MediaVideoEffectPlan.Identity;
                        return false;
                }
            }
            catch (Exception)
            {
                plan = MediaVideoEffectPlan.Identity;
                return false;
            }
            finally
            {
                effect?.Dispose();
            }
        }

        plan = new MediaVideoEffectPlan(
            transform,
            (float)Math.Sqrt(blurVariance));
        return true;
    }
}
