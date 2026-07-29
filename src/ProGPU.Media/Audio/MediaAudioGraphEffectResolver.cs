namespace ProGPU.Media.Audio;

using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

/// <summary>
/// Activates serialized composition audio-effect definitions through the
/// typed registry and snapshots graph nodes that can be represented as one
/// native linear-gain stage. This runs while an export graph is prepared,
/// never from a real-time audio callback.
/// </summary>
public static class MediaAudioGraphEffectResolver
{
    /// <summary>
    /// Multiplies an ordered list of typed gain nodes into one finite,
    /// nonnegative native amplitude value. Returns false for an unregistered
    /// definition or a graph-node kind that cannot be represented by gain.
    /// Work is O(E) for E definitions with O(1) working storage.
    /// </summary>
    public static bool TryCaptureCombinedGain(
        MediaEffectRegistry registry,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        out double gain)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);

        gain = 1d;
        for (int index = 0;
             index < definitions.Count;
             index++)
        {
            MediaCompositionEffectDefinition definition =
                definitions[index];
            var descriptor = new MediaEffectDescriptor(
                definition.ActivatableClassId,
                MediaEffectKind.Audio,
                definition.Properties);
            IMediaEffect? effect = null;
            try
            {
                if (!registry.TryCreate(
                        descriptor,
                        out effect) ||
                    effect is not IMediaAudioGraphEffect graph)
                {
                    return false;
                }

                MediaAudioGraphEffectState state =
                    graph.CaptureState();
                if (state.Kind !=
                    MediaAudioGraphEffectKind.Gain)
                {
                    return false;
                }

                gain *= state.Parameter0;
                if (!double.IsFinite(gain))
                {
                    return false;
                }
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
