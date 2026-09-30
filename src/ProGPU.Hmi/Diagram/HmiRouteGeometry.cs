namespace ProGPU.Hmi;

public readonly record struct HmiRouteBox(float Left, float Top, float Right, float Bottom)
{
    public bool Contains(HmiPoint point) => point.X > Left && point.X < Right && point.Y > Top && point.Y < Bottom;
    public HmiRouteBox Inflate(float amount) => new(Left - amount, Top - amount, Right + amount, Bottom + amount);
    internal bool IsValid => float.IsFinite(Left) && float.IsFinite(Top) && float.IsFinite(Right) && float.IsFinite(Bottom) &&
        Left < Right && Top < Bottom && Math.Abs(Left) <= 1_000_000 && Math.Abs(Top) <= 1_000_000 && Math.Abs(Right) <= 1_000_000 && Math.Abs(Bottom) <= 1_000_000;
}

public readonly record struct HmiRouteObstacle(string ElementId, HmiRouteBox Bounds);
public readonly record struct HmiRouteTerminal(string ElementId, HmiPoint Point, HmiPortDirection Direction, HmiRouteBox Bounds);
public enum HmiRouteStatus { Success, BlockedTerminal, NoRoute, CapacityExceeded, BlockedWaypoint, BlockedSegment }

/// <summary>An immutable computed route. It is never persisted as if it were topology.</summary>
public sealed class HmiRouteResult
{
    public HmiRouteStatus Status { get; }
    public IReadOnlyList<HmiPoint> Points { get; }
    public double Length { get; }
    public string Diagnostic { get; }
    public static HmiRouteResult Unavailable(HmiRouteStatus status, string diagnostic)
    {
        if (!Enum.IsDefined(status) || status == HmiRouteStatus.Success) throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        return new(status, [], diagnostic);
    }
    internal HmiRouteResult(HmiRouteStatus status, HmiPoint[] points, string diagnostic)
    {
        Status = status; Points = Array.AsReadOnly(points); Diagnostic = diagnostic;
        for (int i = 1; i < points.Length; i++) Length += Math.Abs((double)points[i].X - points[i - 1].X) + Math.Abs((double)points[i].Y - points[i - 1].Y);
    }
}
