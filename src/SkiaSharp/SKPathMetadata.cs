namespace SkiaSharp;

public enum SKPathConvexity
{
    Unknown,
    Convex,
    Concave,
}

[Flags]
public enum SKPathSegmentMask
{
    Line = 1,
    Quad = 2,
    Conic = 4,
    Cubic = 8,
}
