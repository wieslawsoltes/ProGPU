using AVFoundation;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Apple.Media;

internal readonly record struct AppleExportAudioEffectSegment(
    TimeSpan Start,
    TimeSpan Duration,
    IReadOnlyList<MediaCompositionEffectDefinition>
        EffectDefinitions);

internal readonly record struct AppleExportAudioEffectTrack(
    AVMutableAudioMixInputParameters Parameters,
    IReadOnlyList<AppleExportAudioEffectSegment> Segments);

/// <summary>
/// Owns export-time typed effects and AVFoundation processing taps. All
/// activation, validation, array construction, and native tap creation occur
/// before export starts. The real-time callback observes immutable arrays and
/// processes the AVFoundation-owned float buffer in place.
/// </summary>
internal sealed class AppleExportAudioEffectGraph :
    IDisposable
{
    private readonly List<IMediaEffect> _effects = [];
    private readonly List<AppleAudioEffectTap> _taps = [];
    private int _disposed;

    public AppleExportAudioEffectGraph(
        MediaEffectRegistry registry,
        IReadOnlyList<AppleExportAudioEffectTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tracks);
        try
        {
            for (int trackIndex = 0;
                 trackIndex < tracks.Count;
                 trackIndex++)
            {
                AppleExportAudioEffectTrack track =
                    tracks[trackIndex];
                List<MediaAudioTimelineSegment> segments =
                    CreateSegments(
                        registry,
                        track.Segments);
                if (segments.Count == 0)
                {
                    continue;
                }

                var timeline =
                    new MediaAudioTimelineProcessor(segments);
                var tap = new AppleAudioEffectTap(
                    [timeline]);
                _taps.Add(tap);
                tap.AttachTo(track.Parameters);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool HasUnsupportedFormat
    {
        get
        {
            for (int index = 0;
                 index < _taps.Count;
                 index++)
            {
                if (_taps[index].HasUnsupportedFormat)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        for (int index = _taps.Count - 1;
             index >= 0;
             index--)
        {
            _taps[index].Dispose();
        }
        _taps.Clear();
        for (int index = _effects.Count - 1;
             index >= 0;
             index--)
        {
            _effects[index].Dispose();
        }
        _effects.Clear();
    }

    private List<MediaAudioTimelineSegment> CreateSegments(
        MediaEffectRegistry registry,
        IReadOnlyList<AppleExportAudioEffectSegment> source)
    {
        var result =
            new List<MediaAudioTimelineSegment>(
                source.Count);
        for (int segmentIndex = 0;
             segmentIndex < source.Count;
             segmentIndex++)
        {
            AppleExportAudioEffectSegment segment =
                source[segmentIndex];
            if (segment.EffectDefinitions.Count == 0)
            {
                continue;
            }

            var processors =
                new IMediaAudioProcessor[
                    segment.EffectDefinitions.Count];
            for (int effectIndex = 0;
                 effectIndex < processors.Length;
                 effectIndex++)
            {
                MediaCompositionEffectDefinition definition =
                    segment.EffectDefinitions[effectIndex];
                var descriptor = new MediaEffectDescriptor(
                    definition.ActivatableClassId,
                    MediaEffectKind.Audio,
                    definition.Properties);
                if (!registry.TryCreate(
                        descriptor,
                        out IMediaEffect? effect) ||
                    effect is not IMediaAudioEffect
                        audioEffect)
                {
                    effect?.Dispose();
                    throw new NotSupportedException(
                        $"The registered media effect " +
                        $"'{definition.ActivatableClassId}' " +
                        "is not an audio processor.");
                }
                _effects.Add(effect);
                processors[effectIndex] = audioEffect;
            }
            result.Add(
                new MediaAudioTimelineSegment(
                    segment.Start,
                    segment.Duration,
                    processors));
        }
        return result;
    }
}
