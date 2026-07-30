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
    /// Activates an ordered list of typed gain and stereo-balance nodes and
    /// folds them into one finite pair of linear channel levels. Returns
    /// false for an unregistered definition or unsupported graph-node kind.
    /// Work is O(E) for E definitions with O(1) working storage.
    /// </summary>
    public static bool TryCaptureCombinedStereoLevels(
        MediaEffectRegistry registry,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        out MediaAudioStereoLevels levels)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);

        levels = MediaAudioStereoLevels.Identity;
        for (int index = 0;
             index < definitions.Count;
             index++)
        {
            if (!TryCaptureState(
                    registry,
                    definitions[index],
                    out MediaAudioGraphEffectState state) ||
                state.Kind is not (
                    MediaAudioGraphEffectKind.Gain or
                    MediaAudioGraphEffectKind.StereoBalance))
            {
                levels = default;
                return false;
            }

            try
            {
                levels = levels.Apply(state);
            }
            catch (ArgumentOutOfRangeException)
            {
                levels = default;
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Activates and snapshots an ordered list of built-in portable graph
    /// nodes for providers that retain the graph structure. Configuration
    /// allocates exactly one E-element result for E definitions; processing
    /// remains owned by the native provider.
    /// </summary>
    public static bool TryCaptureBuiltInGraph(
        MediaEffectRegistry registry,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        out MediaAudioGraphEffectState[] states)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);

        states = definitions.Count == 0
            ? []
            : new MediaAudioGraphEffectState[
                definitions.Count];
        for (int index = 0;
             index < definitions.Count;
             index++)
        {
            if (!TryCaptureState(
                    registry,
                    definitions[index],
                    out MediaAudioGraphEffectState state) ||
                state.Kind is not (
                    MediaAudioGraphEffectKind.Gain or
                    MediaAudioGraphEffectKind.StereoBalance))
            {
                states = [];
                return false;
            }
            states[index] = state;
        }
        return true;
    }

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
            if (!TryCaptureState(
                    registry,
                    definitions[index],
                    out MediaAudioGraphEffectState state) ||
                state.Kind !=
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
        return true;
    }

    private static bool TryCaptureState(
        MediaEffectRegistry registry,
        MediaCompositionEffectDefinition definition,
        out MediaAudioGraphEffectState state)
    {
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
                state = default;
                return false;
            }

            state = graph.CaptureState();
            return true;
        }
        catch (Exception)
        {
            state = default;
            return false;
        }
        finally
        {
            effect?.Dispose();
        }
    }
}
