using ProGPU.Media.Playback;

namespace Windows.Media.Core;

/// <summary>
/// Represents one line of text in a timed-text cue.
/// </summary>
public sealed class TimedTextLine
{
    private readonly List<TimedTextSubformat> _subformats =
        [];
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set => _text = value ?? string.Empty;
    }

    public IList<TimedTextSubformat> Subformats =>
        _subformats;

    internal bool ApplyProviderState(
        MediaPlaybackTimedTextLineDescriptor descriptor)
    {
        bool changed =
            !StringComparer.Ordinal.Equals(
                _text,
                descriptor.Text);
        _text = descriptor.Text;
        int sourceCount = descriptor.Subformats.Count;
        for (int index = 0; index < sourceCount; index++)
        {
            TimedTextSubformat subformat;
            if (index < _subformats.Count)
            {
                subformat = _subformats[index];
            }
            else
            {
                subformat = new TimedTextSubformat();
                _subformats.Add(subformat);
                changed = true;
            }
            MediaPlaybackTimedTextSubformatDescriptor
                source = descriptor.Subformats[index];
            changed |= subformat.ApplyProviderState(
                in source);
        }
        if (_subformats.Count > sourceCount)
        {
            _subformats.RemoveRange(
                sourceCount,
                _subformats.Count - sourceCount);
            changed = true;
        }
        return changed;
    }

    internal bool ApplyProviderText(string text)
    {
        bool changed =
            !StringComparer.Ordinal.Equals(_text, text) ||
            _subformats.Count != 0;
        _text = text;
        _subformats.Clear();
        return changed;
    }
}

/// <summary>
/// WinUI-aligned timed-text cue projected from native provider text tracks.
/// </summary>
public sealed class TimedTextCue : IMediaCue
{
    private readonly List<TimedTextLine> _lines = [];
    private TimedTextRegion _cueRegion = new();
    private TimedTextStyle _cueStyle = new();
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

    public TimedTextRegion CueRegion
    {
        get => _cueRegion;
        set => _cueRegion =
            value ?? new TimedTextRegion();
    }

    public TimedTextStyle CueStyle
    {
        get => _cueStyle;
        set => _cueStyle =
            value ?? new TimedTextStyle();
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

    internal MediaPlaybackTimedTextCueLayout
        ProviderLayout { get; private set; }

    internal bool ApplyProviderState(
        in MediaPlaybackTimedMetadataCueDescriptor
            descriptor)
    {
        TimeSpan startTime = descriptor.StartTime;
        TimeSpan duration = descriptor.Duration;
        bool timingChanged =
            _startTime != startTime ||
            _duration != duration;
        bool changed = timingChanged;
        _startTime = startTime;
        _duration = duration;

        MediaPlaybackTimedTextCuePresentation?
            presentation = descriptor.Presentation;
        if (presentation is not null &&
            presentation.Lines.Count != 0)
        {
            changed |= ApplyProviderLines(
                presentation.Lines);
        }
        else
        {
            changed |= ApplyProviderPlainText(
                descriptor.Text ?? string.Empty);
        }

        MediaPlaybackTimedTextStyle style =
            presentation?.Style ?? default;
        MediaPlaybackTimedTextCueLayout layout =
            presentation?.Layout ?? default;
        changed |= _cueStyle.ApplyProviderStyle(
            in style,
            layout.TextAlignment);
        changed |= _cueRegion.ApplyProviderLayout(
            in layout);
        if (ProviderLayout != layout)
        {
            ProviderLayout = layout;
            changed = true;
        }
        if (timingChanged)
        {
            TimingChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    private bool ApplyProviderLines(
        IReadOnlyList<
            MediaPlaybackTimedTextLineDescriptor> source)
    {
        bool changed = false;
        for (int index = 0; index < source.Count; index++)
        {
            TimedTextLine line;
            if (index < _lines.Count)
            {
                line = _lines[index];
            }
            else
            {
                line = new TimedTextLine();
                _lines.Add(line);
                changed = true;
            }
            changed |= line.ApplyProviderState(
                source[index]);
        }
        if (_lines.Count > source.Count)
        {
            _lines.RemoveRange(
                source.Count,
                _lines.Count - source.Count);
            changed = true;
        }
        return changed;
    }

    private bool ApplyProviderPlainText(string text)
    {
        int lineCount = 1;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                lineCount++;
                if (index + 1 < text.Length &&
                    text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (text[index] == '\n')
            {
                lineCount++;
            }
        }

        bool changed = false;
        int lineStart = 0;
        int lineIndex = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            bool atEnd = index == text.Length;
            bool atBreak =
                !atEnd &&
                (text[index] == '\r' ||
                 text[index] == '\n');
            if (!atEnd && !atBreak)
            {
                continue;
            }

            string lineText =
                text.Substring(
                    lineStart,
                    index - lineStart);
            TimedTextLine line;
            if (lineIndex < _lines.Count)
            {
                line = _lines[lineIndex];
            }
            else
            {
                line = new TimedTextLine();
                _lines.Add(line);
                changed = true;
            }
            changed |= line.ApplyProviderText(lineText);
            lineIndex++;
            if (atBreak &&
                text[index] == '\r' &&
                index + 1 < text.Length &&
                text[index + 1] == '\n')
            {
                index++;
            }
            lineStart = index + 1;
        }
        if (_lines.Count > lineCount)
        {
            _lines.RemoveRange(
                lineCount,
                _lines.Count - lineCount);
            changed = true;
        }
        return changed;
    }
}
