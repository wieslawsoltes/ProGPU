namespace Microsoft.UI.Xaml;

public enum DurationType
{
    Automatic,
    TimeSpan,
    Forever
}

/// <summary>A timeline duration with the WinUI Automatic and Forever sentinels.</summary>
public readonly struct Duration : IEquatable<Duration>
{
    private Duration(DurationType type, TimeSpan timeSpan)
    {
        Type = type;
        TimeSpan = timeSpan;
    }

    public Duration(TimeSpan timeSpan) : this(DurationType.TimeSpan, timeSpan)
    {
    }

    public static Duration Automatic { get; } = new(DurationType.Automatic, default);
    public static Duration Forever { get; } = new(DurationType.Forever, default);
    public TimeSpan TimeSpan { get; }
    public DurationType Type { get; }
    public bool HasTimeSpan => Type == DurationType.TimeSpan;

    public static implicit operator Duration(TimeSpan value) => new(value);
    public bool Equals(Duration other) => Type == other.Type && TimeSpan.Equals(other.TimeSpan);
    public override bool Equals(object? obj) => obj is Duration other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Type, TimeSpan);
}
