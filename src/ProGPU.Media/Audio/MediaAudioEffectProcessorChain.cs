using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Media.Audio;

/// <summary>
/// Owns an ordered set of explicitly registered typed audio effects and
/// processes caller-owned float PCM storage in place.
/// </summary>
/// <remarks>
/// Creation is O(E) time and storage for E definitions and occurs outside an
/// audio callback. Processing is O(E * F * C) for F frames and C channels,
/// uses caller-owned storage, and performs no allocation, locking, reflection,
/// assembly scanning, or effect activation. Effects must obey the
/// <see cref="IMediaAudioProcessor"/> real-time contract.
/// </remarks>
public sealed class MediaAudioEffectProcessorChain :
    IDisposable
{
    private IMediaAudioEffect[]? _effects;

    private MediaAudioEffectProcessorChain(
        IMediaAudioEffect[] effects)
    {
        _effects = effects;
    }

    public int Count =>
        Volatile.Read(ref _effects)?.Length ??
        0;

    /// <summary>
    /// Returns the serial sum of the activated effects' finite latency and
    /// tail declarations. Effects without the optional timing contract are
    /// block-local.
    /// </summary>
    public MediaAudioProcessorTiming GetTiming(
        in MediaAudioFormat format)
    {
        IMediaAudioEffect[] effects =
            Volatile.Read(ref _effects) ??
            throw new ObjectDisposedException(
                nameof(
                    MediaAudioEffectProcessorChain));
        return MediaAudioProcessorTiming.Sum(
            effects,
            in format);
    }

    /// <summary>
    /// Activates every definition through the supplied typed registry in
    /// declaration order. A failed or non-audio activation disposes all
    /// already-created effects and returns false.
    /// </summary>
    public static bool TryCreate(
        MediaEffectRegistry registry,
        IReadOnlyList<
            MediaCompositionEffectDefinition>
            definitions,
        out MediaAudioEffectProcessorChain? chain)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);

        var effects =
            new IMediaAudioEffect[
                definitions.Count];
        int created = 0;
        try
        {
            for (int index = 0;
                 index < definitions.Count;
                 index++)
            {
                MediaCompositionEffectDefinition
                    definition = definitions[index];
                var descriptor =
                    new MediaEffectDescriptor(
                        definition
                            .ActivatableClassId,
                        MediaEffectKind.Audio,
                        definition.Properties);
                IMediaEffect? effect = null;
                try
                {
                    if (!registry.TryCreate(
                            descriptor,
                            out effect) ||
                        effect is not
                            IMediaAudioEffect
                                audioEffect ||
                        effect.Kind !=
                            MediaEffectKind.Audio)
                    {
                        DisposeEffect(effect);
                        effect = null;
                        DisposeCreated(
                            effects,
                            created);
                        chain = null;
                        return false;
                    }

                    effects[index] = audioEffect;
                    effect = null;
                    created++;
                }
                finally
                {
                    DisposeEffect(effect);
                }
            }

            chain =
                new MediaAudioEffectProcessorChain(
                    effects);
            return true;
        }
        catch
        {
            DisposeCreated(
                effects,
                created);
            chain = null;
            return false;
        }
    }

    public void Process(
        Span<float> interleavedSamples,
        in MediaAudioProcessContext context)
    {
        IMediaAudioEffect[] effects =
            Volatile.Read(ref _effects) ??
            throw new ObjectDisposedException(
                nameof(
                    MediaAudioEffectProcessorChain));
        int requiredSamples = checked(
            context.FrameCount *
            context.Format.ChannelCount);
        if (context.FrameCount < 0 ||
            interleavedSamples.Length <
                requiredSamples)
        {
            throw new ArgumentException(
                "The callback buffer is smaller than the declared frame count.",
                nameof(interleavedSamples));
        }

        Span<float> samples =
            interleavedSamples[
                ..requiredSamples];
        for (int index = 0;
             index < effects.Length;
             index++)
        {
            effects[index].Process(
                samples,
                context);
        }
    }

    public void Dispose()
    {
        IMediaAudioEffect[]? effects =
            Interlocked.Exchange(
                ref _effects,
                null);
        if (effects is null)
        {
            return;
        }
        DisposeCreated(
            effects,
            effects.Length);
    }

    private static void DisposeCreated(
        IMediaAudioEffect[] effects,
        int count)
    {
        for (int index = count - 1;
             index >= 0;
             index--)
        {
            DisposeEffect(effects[index]);
            effects[index] = null!;
        }
    }

    private static void DisposeEffect(
        IMediaEffect? effect)
    {
        try
        {
            effect?.Dispose();
        }
        catch
        {
            // Cleanup cannot recover a failed effect activation and must
            // continue releasing the rest of the prepared chain.
        }
    }
}
