using System;

namespace SkiaSharp;

public partial struct SKSizeI
{
    public readonly bool IsEmpty => Equals(Empty);

    public SKSizeI(SKPointI pt)
    {
        _width = pt.X;
        _height = pt.Y;
    }

    public readonly SKPointI ToPointI() => new(_width, _height);

    public override readonly string ToString() => $"{{Width={_width}, Height={_height}}}";

    public static SKSizeI Add(SKSizeI sz1, SKSizeI sz2) => sz1 + sz2;

    public static SKSizeI Subtract(SKSizeI sz1, SKSizeI sz2) => sz1 - sz2;

    public static SKSizeI operator +(SKSizeI sz1, SKSizeI sz2) =>
        new(sz1._width + sz2._width, sz1._height + sz2._height);

    public static SKSizeI operator -(SKSizeI sz1, SKSizeI sz2) =>
        new(sz1._width - sz2._width, sz1._height - sz2._height);

    public static explicit operator SKPointI(SKSizeI size) =>
        new(size._width, size._height);

    public readonly bool Equals(SKSizeI obj) =>
        _width == obj._width && _height == obj._height;

    public override readonly bool Equals(object? obj) => obj is SKSizeI other && Equals(other);

    public static bool operator ==(SKSizeI left, SKSizeI right) => left.Equals(right);

    public static bool operator !=(SKSizeI left, SKSizeI right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_width);
        hash.Add(_height);
        return hash.ToHashCode();
    }
}
