using System;

namespace SkiaSharp;

public partial struct SKSize
{
    public readonly bool IsEmpty => Equals(Empty);

    public SKSize(SKPoint pt)
    {
        _width = pt.X;
        _height = pt.Y;
    }

    public readonly SKPoint ToPoint() => new(_width, _height);

    public readonly SKSizeI ToSizeI()
    {
        checked
        {
            return new SKSizeI((int)_width, (int)_height);
        }
    }

    public override readonly string ToString() =>
        FormattableString.Invariant($"{{Width={_width}, Height={_height}}}");

    public static SKSize Add(SKSize sz1, SKSize sz2) => sz1 + sz2;

    public static SKSize Subtract(SKSize sz1, SKSize sz2) => sz1 - sz2;

    public static SKSize operator +(SKSize sz1, SKSize sz2) =>
        new(sz1._width + sz2._width, sz1._height + sz2._height);

    public static SKSize operator -(SKSize sz1, SKSize sz2) =>
        new(sz1._width - sz2._width, sz1._height - sz2._height);

    public static explicit operator SKPoint(SKSize size) => new(size._width, size._height);

    public static implicit operator SKSize(SKSizeI size) => new(size.Width, size.Height);

    public readonly bool Equals(SKSize obj) =>
        _width == obj._width && _height == obj._height;

    public override readonly bool Equals(object? obj) => obj is SKSize other && Equals(other);

    public static bool operator ==(SKSize left, SKSize right) => left.Equals(right);

    public static bool operator !=(SKSize left, SKSize right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_width);
        hash.Add(_height);
        return hash.ToHashCode();
    }
}
