using Windows.Foundation.Collections;
using Windows.Storage.Streams;

namespace Windows.Media.Core;

/// <summary>
/// Carries caller-defined binary timed metadata.
/// </summary>
public sealed class DataCue : IMediaCue
{
    private TimeSpan _duration;
    private TimeSpan _startTime;
    private string _id = string.Empty;

    public DataCue()
    {
        Properties = new PropertySet();
    }

    public IBuffer? Data { get; set; }

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

    public PropertySet Properties { get; }

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
}
