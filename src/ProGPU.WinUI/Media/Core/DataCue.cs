using ProGPU.Media.Playback;
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
    private MediaPlaybackTimedMetadataCueData?
        _providerData;
    private IBuffer? _providerBuffer;

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

    internal bool ApplyProviderState(
        in MediaPlaybackTimedMetadataCueDescriptor
            descriptor)
    {
        MediaPlaybackTimedMetadataCueData data =
            descriptor.Data ??
            throw new ArgumentException(
                "A provider DataCue requires binary data.",
                nameof(descriptor));
        TimeSpan startTime = descriptor.StartTime;
        TimeSpan duration = descriptor.Duration;
        bool timingChanged =
            _startTime != startTime ||
            _duration != duration;
        bool changed = timingChanged;
        _startTime = startTime;
        _duration = duration;

        if (!ReferenceEquals(_providerData, data))
        {
            ReadOnlySpan<byte> source = data.Bytes;
            var buffer =
                new Windows.Storage.Streams.Buffer(
                    checked((uint)source.Length))
                {
                    Length = checked((uint)source.Length)
                };
            source.CopyTo(buffer.Memory.Span);
            _providerData = data;
            _providerBuffer = buffer;
            changed = true;
        }
        if (!ReferenceEquals(Data, _providerBuffer))
        {
            Data = _providerBuffer;
            changed = true;
        }
        if (timingChanged)
        {
            TimingChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }
}
