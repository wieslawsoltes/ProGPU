namespace ProGPU.Media.Audio;

/// <summary>
/// Exact integer conversions between PCM frame positions and microsecond
/// media timestamps.
/// </summary>
/// <remarks>
/// Every operation is O(1), allocation-free, and uses integer arithmetic.
/// Boundary selection rounds toward the first frame at or after the requested
/// time so adjacent half-open trim intervals do not duplicate a frame.
/// </remarks>
internal static class MediaPcmTimelineMath
{
    private const long MicrosecondsPerSecond =
        1_000_000L;

    internal static int GetBoundaryFrameOffset(
        long deltaMicroseconds,
        uint sampleRate,
        int maximumFrames)
    {
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }
        if (maximumFrames < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFrames));
        }
        if (deltaMicroseconds <= 0)
        {
            return 0;
        }

        long maximumDuration =
            checked(
                ((long)maximumFrames *
                 MicrosecondsPerSecond +
                 sampleRate -
                 1) /
                sampleRate);
        if (deltaMicroseconds >= maximumDuration)
        {
            return maximumFrames;
        }

        return checked(
            (int)(
                (deltaMicroseconds *
                 sampleRate +
                 MicrosecondsPerSecond -
                 1) /
                MicrosecondsPerSecond));
    }

    internal static long GetFrameTimestampMicroseconds(
        long frame,
        uint sampleRate)
    {
        if (frame < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame));
        }
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }

        return checked(
            frame /
                sampleRate *
                MicrosecondsPerSecond +
            frame %
                sampleRate *
                MicrosecondsPerSecond /
                sampleRate);
    }

    internal static long GetDurationFrameCountCeiling(
        TimeSpan duration,
        uint sampleRate)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration));
        }
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }

        const long ticksPerSecond =
            TimeSpan.TicksPerSecond;
        long ticks =
            duration.Ticks;
        return checked(
            ticks /
                ticksPerSecond *
                sampleRate +
            (ticks %
                 ticksPerSecond *
                 sampleRate +
             ticksPerSecond -
             1) /
                ticksPerSecond);
    }
}
