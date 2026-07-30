using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Android.Media;

/// <summary>
/// One decoded source interval on the Android composition-audio timeline.
/// </summary>
internal readonly record struct AndroidMediaCodecAudioPlan(
    Uri SourceUri,
    long SourceStartMicroseconds,
    long SourceEndMicroseconds,
    long DestinationStartFrame,
    long DestinationEndFrame,
    AndroidPcm16MixLevels Levels);

/// <summary>
/// Clean-room schedule capture for WinUI-compatible main, background, and
/// audio-enabled overlay composition audio.
/// </summary>
/// <remarks>
/// Capture is O(C + B + O) time and storage for C main clips, B background
/// tracks, and O audible URI overlays. A negative background delay advances
/// its source interval; a positive delay advances its destination interval.
/// Overlay delay is nonnegative. Every concurrent plan is clipped to the
/// duration of the sequential main composition before a native codec is
/// created.
/// </remarks>
internal static class AndroidMediaCodecAudioPlanner
{
    internal static bool TryCapture(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        out AndroidMediaCodecAudioPlan[] plans,
        out long compositionFrameCount)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effects);

        uint sampleRate =
            request.EncodingProfile.AudioSampleRate;
        var captured =
            new List<AndroidMediaCodecAudioPlan>(
                request.Clips.Count +
                request.BackgroundAudioTracks.Count);
        try
        {
            long timelineTicks = 0;
            for (int index = 0;
                 index < request.Clips.Count;
                 index++)
            {
                MediaCompositionExportClip clip =
                    request.Clips[index];
                long durationTicks =
                    GetTrimmedDurationTicks(
                        clip.OriginalDuration,
                        clip.TrimTimeFromStart,
                        clip.TrimTimeFromEnd);
                long destinationEndTicks =
                    checked(
                        timelineTicks +
                        durationTicks);
                if (!TryGetLevels(
                        clip.Volume,
                        clip.AudioEffectDefinitions,
                        effects,
                        out AndroidPcm16MixLevels levels))
                {
                    plans = [];
                    compositionFrameCount = 0;
                    return false;
                }

                if (clip.SourceUri is
                    { IsAbsoluteUri: true } source &&
                    levels is not
                    { Left: 0, Right: 0 })
                {
                    long sourceStart =
                        ToMicroseconds(
                            clip.TrimTimeFromStart);
                    long duration =
                        ToMicroseconds(
                            TimeSpan.FromTicks(
                                durationTicks));
                    long destinationStartFrame =
                        TicksToFramesCeiling(
                            timelineTicks,
                            sampleRate);
                    long destinationEndFrame =
                        TicksToFramesCeiling(
                            destinationEndTicks,
                            sampleRate);
                    captured.Add(
                        new AndroidMediaCodecAudioPlan(
                            source,
                            sourceStart,
                            checked(
                                sourceStart +
                                duration),
                            destinationStartFrame,
                            destinationEndFrame,
                            levels));
                }
                timelineTicks = destinationEndTicks;
            }

            compositionFrameCount =
                TicksToFramesCeiling(
                    timelineTicks,
                    sampleRate);
            if (compositionFrameCount <= 0)
            {
                plans = [];
                return false;
            }

            for (int index = 0;
                 index <
                    request.BackgroundAudioTracks.Count;
                 index++)
            {
                MediaCompositionExportAudioTrack track =
                    request.BackgroundAudioTracks[index];
                _ = GetTrimmedDurationTicks(
                    track.OriginalDuration,
                    track.TrimTimeFromStart,
                    track.TrimTimeFromEnd);
                if (!track.SourceUri.IsAbsoluteUri ||
                    !TryGetLevels(
                        track.Volume,
                        track.AudioEffectDefinitions,
                        effects,
                        out AndroidPcm16MixLevels levels))
                {
                    plans = [];
                    compositionFrameCount = 0;
                    return false;
                }

                long sourceSkipTicks =
                    track.Delay.Ticks < 0
                        ? checked(-track.Delay.Ticks)
                        : 0;
                long sourceStartTicks =
                    checked(
                        track.TrimTimeFromStart.Ticks +
                        sourceSkipTicks);
                long sourceEndTicks =
                    checked(
                        track.OriginalDuration.Ticks -
                        track.TrimTimeFromEnd.Ticks);
                long destinationStartTicks =
                    Math.Max(
                        0,
                        track.Delay.Ticks);
                if (sourceStartTicks >= sourceEndTicks)
                {
                    continue;
                }

                long destinationStartFrame =
                    TicksToFramesCeiling(
                        destinationStartTicks,
                        sampleRate);
                if (destinationStartFrame >=
                    compositionFrameCount)
                {
                    continue;
                }
                long availableFrames =
                    TicksToFramesFloor(
                        sourceEndTicks -
                            sourceStartTicks,
                        sampleRate);
                long destinationEndFrame =
                    Math.Min(
                        compositionFrameCount,
                        checked(
                            destinationStartFrame +
                            availableFrames));
                if (destinationStartFrame >=
                        destinationEndFrame ||
                    levels is
                    { Left: 0, Right: 0 })
                {
                    continue;
                }

                long includedFrameCount =
                    destinationEndFrame -
                    destinationStartFrame;
                long sourceStart =
                    ToMicroseconds(
                        TimeSpan.FromTicks(
                            sourceStartTicks));
                captured.Add(
                    new AndroidMediaCodecAudioPlan(
                        track.SourceUri,
                        sourceStart,
                        checked(
                            sourceStart +
                            FramesToMicrosecondsCeiling(
                                includedFrameCount,
                                sampleRate)),
                        destinationStartFrame,
                        destinationEndFrame,
                        levels));
            }

            for (int layerIndex = 0;
                 layerIndex <
                    request.OverlayLayers.Count;
                 layerIndex++)
            {
                MediaCompositionExportOverlayLayer layer =
                    request.OverlayLayers[layerIndex];
                for (int overlayIndex = 0;
                     overlayIndex <
                        layer.Overlays.Count;
                     overlayIndex++)
                {
                    MediaCompositionExportOverlay overlay =
                        layer.Overlays[overlayIndex];
                    MediaCompositionExportClip clip =
                        overlay.Clip;
                    if (!overlay.AudioEnabled ||
                        clip.SourceUri is not
                        { IsAbsoluteUri: true } source)
                    {
                        continue;
                    }

                    long durationTicks =
                        GetTrimmedDurationTicks(
                            clip.OriginalDuration,
                            clip.TrimTimeFromStart,
                            clip.TrimTimeFromEnd);
                    if (overlay.Delay < TimeSpan.Zero ||
                        !TryGetLevels(
                            clip.Volume,
                            clip.AudioEffectDefinitions,
                            effects,
                            out AndroidPcm16MixLevels
                                levels))
                    {
                        plans = [];
                        compositionFrameCount = 0;
                        return false;
                    }

                    long destinationStartFrame =
                        TicksToFramesCeiling(
                            overlay.Delay.Ticks,
                            sampleRate);
                    if (destinationStartFrame >=
                        compositionFrameCount)
                    {
                        continue;
                    }
                    long availableFrames =
                        TicksToFramesFloor(
                            durationTicks,
                            sampleRate);
                    long destinationEndFrame =
                        Math.Min(
                            compositionFrameCount,
                            checked(
                                destinationStartFrame +
                                availableFrames));
                    if (destinationStartFrame >=
                            destinationEndFrame ||
                        levels is
                        { Left: 0, Right: 0 })
                    {
                        continue;
                    }

                    long includedFrameCount =
                        destinationEndFrame -
                        destinationStartFrame;
                    long sourceStart =
                        ToMicroseconds(
                            clip.TrimTimeFromStart);
                    captured.Add(
                        new AndroidMediaCodecAudioPlan(
                            source,
                            sourceStart,
                            checked(
                                sourceStart +
                                FramesToMicrosecondsCeiling(
                                    includedFrameCount,
                                    sampleRate)),
                            destinationStartFrame,
                            destinationEndFrame,
                            levels));
                }
            }
        }
        catch (OverflowException)
        {
            plans = [];
            compositionFrameCount = 0;
            return false;
        }

        plans = captured.ToArray();
        Array.Sort(
            plans,
            static (left, right) =>
                left.DestinationStartFrame.CompareTo(
                    right.DestinationStartFrame));
        return true;
    }

    private static bool TryGetLevels(
        double volume,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        MediaEffectRegistry effects,
        out AndroidPcm16MixLevels levels)
    {
        if (!double.IsFinite(volume) ||
            volume is < 0d or > 1d ||
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
            MediaAudioStereoLevels effective =
                effectLevels.Scale(
                    checked((float)volume));
            return AndroidPcm16MixLevels.TryCreate(
                effective,
                out levels);
        }
        catch (Exception exception)
            when (exception is
                OverflowException or
                ArgumentOutOfRangeException)
        {
            levels = default;
            return false;
        }
    }

    private static long GetTrimmedDurationTicks(
        TimeSpan original,
        TimeSpan trimStart,
        TimeSpan trimEnd)
    {
        if (original <= TimeSpan.Zero ||
            trimStart < TimeSpan.Zero ||
            trimEnd < TimeSpan.Zero)
        {
            throw new OverflowException();
        }

        long duration =
            checked(
                original.Ticks -
                trimStart.Ticks -
                trimEnd.Ticks);
        if (duration <= 0)
        {
            throw new OverflowException();
        }
        return duration;
    }

    internal static long TicksToFramesFloor(
        long ticks,
        uint sampleRate)
    {
        if (ticks < 0 || sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticks));
        }
        return checked(
            (long)(
                (Int128)ticks *
                sampleRate /
                TimeSpan.TicksPerSecond));
    }

    internal static long TicksToFramesCeiling(
        long ticks,
        uint sampleRate)
    {
        if (ticks < 0 || sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticks));
        }
        Int128 numerator =
            (Int128)ticks * sampleRate;
        return checked(
            (long)(
                (numerator +
                 TimeSpan.TicksPerSecond -
                 1) /
                TimeSpan.TicksPerSecond));
    }

    internal static long MicrosecondsToFramesFloor(
        long microseconds,
        uint sampleRate)
    {
        if (microseconds < 0 || sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(microseconds));
        }
        return checked(
            (long)(
                (Int128)microseconds *
                sampleRate /
                1_000_000));
    }

    internal static long MicrosecondsToFramesCeiling(
        long microseconds,
        uint sampleRate)
    {
        if (microseconds < 0 || sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(microseconds));
        }
        Int128 numerator =
            (Int128)microseconds * sampleRate;
        return checked(
            (long)(
                (numerator +
                 999_999) /
                1_000_000));
    }

    private static long FramesToMicrosecondsCeiling(
        long frames,
        uint sampleRate)
    {
        if (frames < 0 || sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frames));
        }
        Int128 numerator =
            (Int128)frames * 1_000_000;
        return checked(
            (long)(
                (numerator +
                 sampleRate -
                 1) /
                sampleRate));
    }

    private static long ToMicroseconds(
        TimeSpan time) =>
        time.Ticks / 10;
}
