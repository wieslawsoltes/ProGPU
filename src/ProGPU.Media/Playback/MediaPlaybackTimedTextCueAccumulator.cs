namespace ProGPU.Media.Playback;

/// <summary>
/// Accumulates the active-text snapshots produced by native push providers
/// into stable, replayable timed-text cues. Updating or closing a native
/// snapshot is O(C + A + N) time and O(C) retained storage for C known cues,
/// A previously active cues, and N newly active strings.
/// </summary>
internal sealed class MediaPlaybackTimedTextCueAccumulator
{
    private readonly string _providerTrackId;
    private readonly List<
        MediaPlaybackTimedMetadataCueDescriptor> _cues = [];
    private readonly Dictionary<string, int> _cueIndices =
        new(StringComparer.Ordinal);
    private readonly List<int> _activeCueIndices = [];

    public MediaPlaybackTimedTextCueAccumulator(
        string providerTrackId)
    {
        if (string.IsNullOrWhiteSpace(providerTrackId))
        {
            throw new ArgumentException(
                "A provider track identifier is required.",
                nameof(providerTrackId));
        }

        _providerTrackId = providerTrackId;
    }

    public MediaPlaybackTimedMetadataCueSnapshot Update(
        TimeSpan itemTime,
        IReadOnlyList<string>? activeTexts,
        TimeSpan sourceDuration)
    {
        itemTime = NormalizeTime(itemTime);
        CloseActiveCues(itemTime);

        int count = activeTexts?.Count ?? 0;
        for (int index = 0; index < count; index++)
        {
            string cueId = string.Concat(
                _providerTrackId,
                ":",
                itemTime.Ticks.ToString(
                    System.Globalization.CultureInfo
                        .InvariantCulture),
                ":",
                index.ToString(
                    System.Globalization.CultureInfo
                        .InvariantCulture));
            TimeSpan duration =
                GetOpenDuration(itemTime, sourceDuration);
            var cue =
                new MediaPlaybackTimedMetadataCueDescriptor(
                    cueId,
                    itemTime,
                    duration,
                    activeTexts![index] ?? string.Empty);
            if (_cueIndices.TryGetValue(
                    cueId,
                    out int cueIndex))
            {
                _cues[cueIndex] = cue;
                _activeCueIndices.Add(cueIndex);
            }
            else
            {
                _cueIndices.Add(cueId, _cues.Count);
                _activeCueIndices.Add(_cues.Count);
                _cues.Add(cue);
            }
        }

        return Capture();
    }

    public MediaPlaybackTimedMetadataCueSnapshot Flush(
        TimeSpan itemTime)
    {
        CloseActiveCues(NormalizeTime(itemTime));
        return Capture();
    }

    private void CloseActiveCues(TimeSpan itemTime)
    {
        for (int index = 0;
             index < _activeCueIndices.Count;
             index++)
        {
            int cueIndex = _activeCueIndices[index];
            MediaPlaybackTimedMetadataCueDescriptor cue =
                _cues[cueIndex];
            TimeSpan duration = itemTime > cue.StartTime
                ? itemTime - cue.StartTime
                : TimeSpan.Zero;
            _cues[cueIndex] = cue with
            {
                Duration = duration
            };
        }
        _activeCueIndices.Clear();
    }

    private MediaPlaybackTimedMetadataCueSnapshot Capture() =>
        new(_providerTrackId, _cues);

    private static TimeSpan NormalizeTime(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TimeSpan GetOpenDuration(
        TimeSpan itemTime,
        TimeSpan sourceDuration)
    {
        if (sourceDuration > itemTime)
        {
            return sourceDuration - itemTime;
        }

        return TimeSpan.MaxValue - itemTime;
    }
}
