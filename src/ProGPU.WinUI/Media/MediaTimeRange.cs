namespace Windows.Media;

/// <summary>
/// WinUI-compatible value describing a time range in media content.
/// </summary>
public struct MediaTimeRange : IEquatable<MediaTimeRange>
{
    public TimeSpan Start;
    public TimeSpan End;

    public readonly bool Equals(MediaTimeRange other) =>
        Start == other.Start && End == other.End;

    public override readonly bool Equals(object? obj) =>
        obj is MediaTimeRange other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(Start, End);

    public static bool operator ==(
        MediaTimeRange left,
        MediaTimeRange right) =>
        left.Equals(right);

    public static bool operator !=(
        MediaTimeRange left,
        MediaTimeRange right) =>
        !left.Equals(right);
}
