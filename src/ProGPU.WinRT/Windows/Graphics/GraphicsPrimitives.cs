namespace Windows.Graphics;

public struct PointInt32 : IEquatable<PointInt32>
{
    public int X;
    public int Y;

    public PointInt32(int x, int y)
    {
        X = x;
        Y = y;
    }

    public readonly bool Equals(PointInt32 other) =>
        X == other.X && Y == other.Y;

    public override readonly bool Equals(object? obj) =>
        obj is PointInt32 other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(X, Y);

    public static bool operator ==(PointInt32 left, PointInt32 right) =>
        left.Equals(right);

    public static bool operator !=(PointInt32 left, PointInt32 right) =>
        !left.Equals(right);
}

public struct SizeInt32 : IEquatable<SizeInt32>
{
    public int Width;
    public int Height;

    public SizeInt32(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public readonly bool Equals(SizeInt32 other) =>
        Width == other.Width && Height == other.Height;

    public override readonly bool Equals(object? obj) =>
        obj is SizeInt32 other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(Width, Height);

    public static bool operator ==(SizeInt32 left, SizeInt32 right) =>
        left.Equals(right);

    public static bool operator !=(SizeInt32 left, SizeInt32 right) =>
        !left.Equals(right);
}

public struct RectInt32 : IEquatable<RectInt32>
{
    public int X;
    public int Y;
    public int Width;
    public int Height;

    public RectInt32(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public readonly bool Equals(RectInt32 other) =>
        X == other.X &&
        Y == other.Y &&
        Width == other.Width &&
        Height == other.Height;

    public override readonly bool Equals(object? obj) =>
        obj is RectInt32 other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(X, Y, Width, Height);

    public static bool operator ==(RectInt32 left, RectInt32 right) =>
        left.Equals(right);

    public static bool operator !=(RectInt32 left, RectInt32 right) =>
        !left.Equals(right);
}
