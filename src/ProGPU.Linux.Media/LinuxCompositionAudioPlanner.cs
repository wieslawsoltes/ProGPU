using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Linux.Media;

internal enum LinuxCompositionAudioSourceKind
{
    MainClip,
    BackgroundTrack,
    Overlay
}

/// <summary>
/// One half-open source interval scheduled on the composition PCM timeline.
/// Source ticks remain in TimeSpan units so a decoder can map them to its
/// native clock without an intermediate floating-point conversion.
/// </summary>
internal readonly record struct LinuxCompositionAudioSourcePlan(
    LinuxCompositionAudioSourceKind Kind,
    Uri SourceUri,
    uint SourceTrackIndex,
    long SourceStartTicks,
    long SourceEndTicks,
    long DestinationStartFrame,
    long DestinationEndFrame,
    LinuxPcm16MixLevels Levels);

/// <summary>
/// Captures the WinUI composition-audio model before native decoding starts.
/// </summary>
/// <remarks>
/// Capture is O(C + B + O + E) time and O(C + B + O) storage for C main
/// clips, B background tracks, O audible overlays, and E typed effects.
/// Frame endpoints use exact integer arithmetic. Negative background delay
/// consumes source time; positive delay advances destination time.
/// </remarks>
internal static class LinuxCompositionAudioPlanner
{
    internal static bool TryCapture(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        out LinuxCompositionAudioSourcePlan[] plans,
        out long compositionFrameCount)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effects);

        uint sampleRate =
            request.EncodingProfile.AudioSampleRate;
        uint channelCount =
            request.EncodingProfile.AudioChannelCount;
        if (sampleRate is < 8_000 or > 384_000 ||
            channelCount is not (1u or 2u) ||
            !TryMeasureComposition(
                request.Clips,
                out long compositionTicks))
        {
            plans = [];
            compositionFrameCount = 0;
            return false;
        }

        try
        {
            compositionFrameCount =
                TicksToFramesCeiling(
                    compositionTicks,
                    sampleRate);
            var captured =
                new List<LinuxCompositionAudioSourcePlan>(
                    checked(
                        request.Clips.Count +
                        request.BackgroundAudioTracks
                            .Count));
            bool valid =
                CaptureMainSequence(
                    request.Clips,
                    effects,
                    sampleRate,
                    captured) &&
                CaptureBackgroundTracks(
                    request.BackgroundAudioTracks,
                    compositionTicks,
                    effects,
                    sampleRate,
                    captured) &&
                CaptureAudibleOverlays(
                    request.OverlayLayers,
                    compositionTicks,
                    effects,
                    sampleRate,
                    captured);
            if (!valid)
            {
                plans = [];
                compositionFrameCount = 0;
                return false;
            }

            plans = captured.ToArray();
            return true;
        }
        catch (ArithmeticException)
        {
            plans = [];
            compositionFrameCount = 0;
            return false;
        }
    }

    private static bool TryMeasureComposition(
        IReadOnlyList<MediaCompositionExportClip> clips,
        out long durationTicks)
    {
        durationTicks = 0;
        if (clips.Count == 0)
        {
            return false;
        }
        try
        {
            for (int index = 0;
                 index < clips.Count;
                 index++)
            {
                if (!TryGetTrimmedInterval(
                        clips[index]
                            .OriginalDuration,
                        clips[index]
                            .TrimTimeFromStart,
                        clips[index]
                            .TrimTimeFromEnd,
                        out _,
                        out long sourceEnd))
                {
                    durationTicks = 0;
                    return false;
                }
                durationTicks = checked(
                    durationTicks +
                    sourceEnd -
                    clips[index]
                        .TrimTimeFromStart
                        .Ticks);
            }
            return durationTicks > 0;
        }
        catch (OverflowException)
        {
            durationTicks = 0;
            return false;
        }
    }

    private static bool CaptureMainSequence(
        IReadOnlyList<MediaCompositionExportClip> clips,
        MediaEffectRegistry effects,
        uint sampleRate,
        List<LinuxCompositionAudioSourcePlan>
            destination)
    {
        long cursor = 0;
        for (int index = 0;
             index < clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                clips[index];
            if (!TryGetTrimmedInterval(
                    clip.OriginalDuration,
                    clip.TrimTimeFromStart,
                    clip.TrimTimeFromEnd,
                    out long sourceStart,
                    out long sourceEnd) ||
                !TryResolveLevels(
                    clip.Volume,
                    clip.AudioEffectDefinitions,
                    effects,
                    out LinuxPcm16MixLevels levels))
            {
                return false;
            }
            long clipTicks =
                checked(
                    sourceEnd -
                    sourceStart);
            long nextCursor =
                checked(
                    cursor +
                    clipTicks);
            if (clip.SourceUri is
                { IsFile: true } source)
            {
                if (!HasKnownNoAudio(clip))
                {
                    AppendIfAudible(
                        destination,
                        LinuxCompositionAudioSourceKind
                            .MainClip,
                        source,
                        clip.SourceAudioTrackIndex,
                        sourceStart,
                        sourceEnd,
                        cursor,
                        nextCursor,
                        sampleRate,
                        levels);
                }
            }
            else if (clip.SourceUri is not null)
            {
                return false;
            }
            cursor = nextCursor;
        }
        return true;
    }

    private static bool CaptureBackgroundTracks(
        IReadOnlyList<MediaCompositionExportAudioTrack>
            tracks,
        long compositionTicks,
        MediaEffectRegistry effects,
        uint sampleRate,
        List<LinuxCompositionAudioSourcePlan>
            destination)
    {
        for (int index = 0;
             index < tracks.Count;
             index++)
        {
            MediaCompositionExportAudioTrack track =
                tracks[index];
            if (!track.SourceUri.IsFile ||
                !TryGetTrimmedInterval(
                    track.OriginalDuration,
                    track.TrimTimeFromStart,
                    track.TrimTimeFromEnd,
                    out long sourceStart,
                    out long sourceEnd) ||
                !TryResolveLevels(
                    track.Volume,
                    track.AudioEffectDefinitions,
                    effects,
                    out LinuxPcm16MixLevels levels))
            {
                return false;
            }

            long sourceAdvance =
                track.Delay.Ticks < 0
                    ? checked(-track.Delay.Ticks)
                    : 0;
            sourceStart = checked(
                sourceStart +
                sourceAdvance);
            long presentationStart =
                Math.Max(
                    0,
                    track.Delay.Ticks);
            long presentationEnd =
                Math.Min(
                    compositionTicks,
                    checked(
                        presentationStart +
                        sourceEnd -
                        sourceStart));
            if (sourceStart >= sourceEnd ||
                presentationStart >=
                    presentationEnd)
            {
                continue;
            }
            sourceEnd = checked(
                sourceStart +
                presentationEnd -
                presentationStart);
            AppendIfAudible(
                destination,
                LinuxCompositionAudioSourceKind
                    .BackgroundTrack,
                track.SourceUri,
                track.SourceAudioTrackIndex,
                sourceStart,
                sourceEnd,
                presentationStart,
                presentationEnd,
                sampleRate,
                levels);
        }
        return true;
    }

    private static bool CaptureAudibleOverlays(
        IReadOnlyList<MediaCompositionExportOverlayLayer>
            layers,
        long compositionTicks,
        MediaEffectRegistry effects,
        uint sampleRate,
        List<LinuxCompositionAudioSourcePlan>
            destination)
    {
        for (int layerIndex = 0;
             layerIndex < layers.Count;
             layerIndex++)
        {
            IReadOnlyList<MediaCompositionExportOverlay>
                overlays =
                    layers[layerIndex].Overlays;
            for (int overlayIndex = 0;
                 overlayIndex < overlays.Count;
                 overlayIndex++)
            {
                MediaCompositionExportOverlay overlay =
                    overlays[overlayIndex];
                if (!overlay.AudioEnabled)
                {
                    continue;
                }

                MediaCompositionExportClip clip =
                    overlay.Clip;
                if (overlay.Delay < TimeSpan.Zero ||
                    clip.SourceUri is not
                    { IsFile: true } source ||
                    !TryGetTrimmedInterval(
                        clip.OriginalDuration,
                        clip.TrimTimeFromStart,
                        clip.TrimTimeFromEnd,
                        out long sourceStart,
                        out long sourceEnd) ||
                    !TryResolveLevels(
                        clip.Volume,
                        clip.AudioEffectDefinitions,
                        effects,
                        out LinuxPcm16MixLevels levels))
                {
                    return false;
                }
                if (HasKnownNoAudio(clip))
                {
                    continue;
                }

                long presentationStart =
                    overlay.Delay.Ticks;
                long presentationEnd =
                    Math.Min(
                        compositionTicks,
                        checked(
                            presentationStart +
                            sourceEnd -
                            sourceStart));
                if (presentationStart >=
                    presentationEnd)
                {
                    continue;
                }
                sourceEnd = checked(
                    sourceStart +
                    presentationEnd -
                    presentationStart);
                AppendIfAudible(
                    destination,
                    LinuxCompositionAudioSourceKind
                        .Overlay,
                    source,
                    clip.SourceAudioTrackIndex,
                    sourceStart,
                    sourceEnd,
                    presentationStart,
                    presentationEnd,
                    sampleRate,
                    levels);
            }
        }
        return true;
    }

    private static bool HasKnownNoAudio(
        MediaCompositionExportClip clip) =>
        clip.SourceAudioSubtype is null &&
        clip.SourceVideoWidth != 0 &&
        clip.SourceVideoHeight != 0;

    private static void AppendIfAudible(
        List<LinuxCompositionAudioSourcePlan> plans,
        LinuxCompositionAudioSourceKind kind,
        Uri source,
        uint sourceTrackIndex,
        long sourceStartTicks,
        long sourceEndTicks,
        long destinationStartTicks,
        long destinationEndTicks,
        uint sampleRate,
        in LinuxPcm16MixLevels levels)
    {
        if (levels.IsSilent)
        {
            return;
        }
        long destinationStartFrame =
            TicksToFramesCeiling(
                destinationStartTicks,
                sampleRate);
        long destinationEndFrame =
            TicksToFramesCeiling(
                destinationEndTicks,
                sampleRate);
        if (destinationStartFrame >=
            destinationEndFrame)
        {
            return;
        }
        plans.Add(
            new LinuxCompositionAudioSourcePlan(
                kind,
                source,
                sourceTrackIndex,
                sourceStartTicks,
                sourceEndTicks,
                destinationStartFrame,
                destinationEndFrame,
                levels));
    }

    private static bool TryResolveLevels(
        double volume,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        MediaEffectRegistry effects,
        out LinuxPcm16MixLevels levels)
    {
        if (!double.IsFinite(volume) ||
            volume is < 0d or >
                MediaPcm16StereoProcessor
                    .MaximumLevel ||
            !MediaAudioGraphEffectResolver
                .TryCaptureCombinedStereoLevels(
                    effects,
                    definitions,
                    out MediaAudioStereoLevels
                        effectLevels))
        {
            levels = default;
            return false;
        }
        try
        {
            return LinuxPcm16MixLevels.TryCreate(
                effectLevels.Scale(
                    (float)volume),
                out levels);
        }
        catch (ArgumentOutOfRangeException)
        {
            levels = default;
            return false;
        }
    }

    private static bool TryGetTrimmedInterval(
        TimeSpan originalDuration,
        TimeSpan trimStart,
        TimeSpan trimEnd,
        out long sourceStartTicks,
        out long sourceEndTicks)
    {
        sourceStartTicks = 0;
        sourceEndTicks = 0;
        if (originalDuration <= TimeSpan.Zero ||
            trimStart < TimeSpan.Zero ||
            trimEnd < TimeSpan.Zero)
        {
            return false;
        }
        try
        {
            sourceStartTicks =
                trimStart.Ticks;
            sourceEndTicks = checked(
                originalDuration.Ticks -
                trimEnd.Ticks);
            return sourceStartTicks <
                sourceEndTicks;
        }
        catch (OverflowException)
        {
            sourceStartTicks = 0;
            sourceEndTicks = 0;
            return false;
        }
    }

    private static long TicksToFramesCeiling(
        long ticks,
        uint sampleRate)
    {
        if (ticks < 0 || sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticks));
        }
        return checked(
            ticks /
                TimeSpan.TicksPerSecond *
                sampleRate +
            (ticks %
                 TimeSpan.TicksPerSecond *
                 sampleRate +
             TimeSpan.TicksPerSecond -
             1) /
                TimeSpan.TicksPerSecond);
    }
}
