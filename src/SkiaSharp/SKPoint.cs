using System;
using System.Numerics;

namespace SkiaSharp;

public partial struct SKPoint
{
    public readonly bool IsEmpty => Equals(Empty);
    public readonly float Length => (float)Math.Sqrt(_x * _x + _y * _y);
    public readonly float LengthSquared => _x * _x + _y * _y;

    public void Offset(SKPoint p)
    {
        _x += p._x;
        _y += p._y;
    }

    public void Offset(float dx, float dy)
    {
        _x += dx;
        _y += dy;
    }

    public override readonly string ToString() => $"{{X={_x}, Y={_y}}}";

    public static SKPoint Normalize(SKPoint point)
    {
        var lengthSquared = point._x * point._x + point._y * point._y;
        var inverseLength = 1d / Math.Sqrt(lengthSquared);
        return new SKPoint(
            (float)(point._x * inverseLength),
            (float)(point._y * inverseLength));
    }

    public static float Distance(SKPoint point, SKPoint other)
    {
        var dx = point._x - other._x;
        var dy = point._y - other._y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    public static float DistanceSquared(SKPoint point, SKPoint other)
    {
        var dx = point._x - other._x;
        var dy = point._y - other._y;
        return dx * dx + dy * dy;
    }

    public static SKPoint Reflect(SKPoint point, SKPoint normal)
    {
        var lengthSquared = point._x * point._x + point._y * point._y;
        return new SKPoint(
            point._x - 2f * lengthSquared * normal._x,
            point._y - 2f * lengthSquared * normal._y);
    }

    public static SKPoint Add(SKPoint pt, SKSizeI sz) => pt + sz;

    public static SKPoint Add(SKPoint pt, SKSize sz) => pt + sz;

    public static SKPoint Add(SKPoint pt, SKPointI sz) => pt + sz;

    public static SKPoint Add(SKPoint pt, SKPoint sz) => pt + sz;

    public static SKPoint Subtract(SKPoint pt, SKSizeI sz) => pt - sz;

    public static SKPoint Subtract(SKPoint pt, SKSize sz) => pt - sz;

    public static SKPoint Subtract(SKPoint pt, SKPointI sz) => pt - sz;

    public static SKPoint Subtract(SKPoint pt, SKPoint sz) => pt - sz;

    public static SKPoint operator +(SKPoint pt, SKSizeI sz) =>
        new(pt._x + sz.Width, pt._y + sz.Height);

    public static SKPoint operator +(SKPoint pt, SKSize sz) =>
        new(pt._x + sz.Width, pt._y + sz.Height);

    public static SKPoint operator +(SKPoint pt, SKPointI sz) =>
        new(pt._x + sz.X, pt._y + sz.Y);

    public static SKPoint operator +(SKPoint pt, SKPoint sz) =>
        new(pt._x + sz._x, pt._y + sz._y);

    public static SKPoint operator -(SKPoint pt, SKSizeI sz) =>
        new(pt._x - sz.Width, pt._y - sz.Height);

    public static SKPoint operator -(SKPoint pt, SKSize sz) =>
        new(pt._x - sz.Width, pt._y - sz.Height);

    public static SKPoint operator -(SKPoint pt, SKPointI sz) =>
        new(pt._x - sz.X, pt._y - sz.Y);

    public static SKPoint operator -(SKPoint pt, SKPoint sz) =>
        new(pt._x - sz._x, pt._y - sz._y);

    public static implicit operator Vector2(SKPoint point) => new(point._x, point._y);

    public static implicit operator SKPoint(Vector2 vector) => new(vector.X, vector.Y);

    public readonly bool Equals(SKPoint obj) => _x == obj._x && _y == obj._y;

    public override readonly bool Equals(object? obj) => obj is SKPoint other && Equals(other);

    public static bool operator ==(SKPoint left, SKPoint right) => left.Equals(right);

    public static bool operator !=(SKPoint left, SKPoint right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_x);
        hash.Add(_y);
        return hash.ToHashCode();
    }
}
