namespace ProGPU.Media.Editing;

using ProGPU.Media.Effects;

/// <summary>
/// Activates ordered WinUI-shaped video-effect definitions through the typed
/// registry and folds compatible affine color nodes into one GPU transform.
/// </summary>
public static class MediaCompositionVideoEffectResolver
{
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
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);

        transform = MediaVideoColorTransform.Identity;
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
                if (state.Kind !=
                    MediaVideoGraphEffectKind
                        .ColorTransform)
                {
                    return false;
                }

                transform = transform.Then(
                    state.ColorTransform);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                effect?.Dispose();
            }
        }
        return true;
    }
}
