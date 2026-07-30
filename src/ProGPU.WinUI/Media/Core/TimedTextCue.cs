namespace Windows.Media.Core;

/// <summary>
/// Represents one line of text in a timed-text cue.
/// </summary>
public sealed class TimedTextLine
{
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set => _text = value ?? string.Empty;
    }
}

/// <summary>
/// WinUI-aligned timed-text cue projected from native provider text tracks.
/// </summary>
public sealed class TimedTextCue : IMediaCue
{
    private readonly List<TimedTextLine> _lines = [];
    private TimeSpan _duration;
    private string _id = string.Empty;
    private TimeSpan _startTime;

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (_duration == value)
            {
                return;
            }
            _duration = value;
            TimingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Id
    {
        get => _id;
        set => _id = value ?? string.Empty;
    }

    public IList<TimedTextLine> Lines => _lines;

    public TimeSpan StartTime
    {
        get => _startTime;
        set
        {
            if (_startTime == value)
            {
                return;
            }
            _startTime = value;
            TimingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal event EventHandler? TimingChanged;

    internal bool ApplyProviderState(
        TimeSpan startTime,
        TimeSpan duration,
        string text)
    {
        bool timingChanged =
            _startTime != startTime ||
            _duration != duration;
        bool changed = timingChanged;
        _startTime = startTime;
        _duration = duration;

        TimedTextLine line;
        if (_lines.Count == 0)
        {
            line = new TimedTextLine();
            _lines.Add(line);
            changed = true;
        }
        else
        {
            line = _lines[0];
        }
        string normalizedText = text ?? string.Empty;
        if (!StringComparer.Ordinal.Equals(
                line.Text,
                normalizedText))
        {
            line.Text = normalizedText;
            changed = true;
        }
        if (timingChanged)
        {
            TimingChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }
}
