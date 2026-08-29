namespace Windows.Graphics.Imaging;

/// <summary>WinRT-compatible unsigned bitmap dimensions.</summary>
public struct BitmapSize : IEquatable<BitmapSize>
{
    public uint Width { get; set; }

    public uint Height { get; set; }

    public readonly bool Equals(BitmapSize other) =>
        Width == other.Width && Height == other.Height;

    public override readonly bool Equals(object? obj) =>
        obj is BitmapSize other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(Width, Height);

    public static bool operator ==(BitmapSize left, BitmapSize right) =>
        left.Equals(right);

    public static bool operator !=(BitmapSize left, BitmapSize right) =>
        !left.Equals(right);
}
