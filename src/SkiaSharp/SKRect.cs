using System;

namespace SkiaSharp;

public partial struct SKRect
{
    public readonly float MidX => _left + Width / 2f;
    public readonly float MidY => _top + Height / 2f;
    public readonly bool IsEmpty => Equals(Empty);

    public SKSize Size
    {
        readonly get => new(Width, Height);
        set
        {
            _right = _left + value.Width;
            _bottom = _top + value.Height;
        }
    }

    public SKPoint Location
    {
        readonly get => new(_left, _top);
        set => this = Create(value, Size);
    }

    public readonly SKRect Standardized => new(
        Math.Min(_left, _right),
        Math.Min(_top, _bottom),
        Math.Max(_left, _right),
        Math.Max(_top, _bottom));

    public readonly SKRect AspectFit(SKSize size) => AspectResize(size, fit: true);

    public readonly SKRect AspectFill(SKSize size) => AspectResize(size, fit: false);

    private readonly SKRect AspectResize(SKSize size, bool fit)
    {
        if (size.Width == 0f || size.Height == 0f || Width == 0f || Height == 0f)
        {
            return Create(MidX, MidY, 0f, 0f);
        }

        var width = size.Width;
        var height = size.Height;
        var targetAspect = width / height;
        var rectAspect = Width / Height;
        if (fit ? rectAspect > targetAspect : rectAspect < targetAspect)
        {
            height = Height;
            width = height * targetAspect;
        }
        else
        {
            width = Width;
            height = width / targetAspect;
        }

        return Create(MidX - width / 2f, MidY - height / 2f, width, height);
    }

    public static SKRect Inflate(SKRect rect, float x, float y)
    {
        rect.Inflate(x, y);
        return rect;
    }

    public void Inflate(SKSize size) => Inflate(size.Width, size.Height);

    public void Inflate(float x, float y)
    {
        _left -= x;
        _top -= y;
        _right += x;
        _bottom += y;
    }

    public static SKRect Intersect(SKRect a, SKRect b) =>
        !a.IntersectsWithInclusive(b)
            ? Empty
            : new SKRect(
                Math.Max(a._left, b._left),
                Math.Max(a._top, b._top),
                Math.Min(a._right, b._right),
                Math.Min(a._bottom, b._bottom));

    public void Intersect(SKRect rect) => this = Intersect(this, rect);

    public static SKRect Union(SKRect a, SKRect b) => new(
        Math.Min(a._left, b._left),
        Math.Min(a._top, b._top),
        Math.Max(a._right, b._right),
        Math.Max(a._bottom, b._bottom));

    public void Union(SKRect rect) => this = Union(this, rect);

    public static implicit operator SKRect(SKRectI rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    public readonly bool Contains(float x, float y) =>
        x >= _left && x < _right && y >= _top && y < _bottom;

    public readonly bool Contains(SKPoint point) => Contains(point.X, point.Y);

    public readonly bool Contains(SKRect rect) =>
        _left <= rect._left &&
        _right >= rect._right &&
        _top <= rect._top &&
        _bottom >= rect._bottom;

    public readonly bool IntersectsWith(SKRect rect) =>
        _left < rect._right &&
        _right > rect._left &&
        _top < rect._bottom &&
        _bottom > rect._top;

    public readonly bool IntersectsWithInclusive(SKRect rect) =>
        _left <= rect._right &&
        _right >= rect._left &&
        _top <= rect._bottom &&
        _bottom >= rect._top;

    public void Offset(float x, float y)
    {
        _left += x;
        _top += y;
        _right += x;
        _bottom += y;
    }

    public void Offset(SKPoint position) => Offset(position.X, position.Y);

    public static SKRect Create(SKPoint location, SKSize size) =>
        Create(location.X, location.Y, size.Width, size.Height);

    public static SKRect Create(SKSize size) => Create(SKPoint.Empty, size);

    public static SKRect Create(float width, float height) =>
        new(SKPoint.Empty.X, SKPoint.Empty.Y, width, height);

    public static SKRect Create(float x, float y, float width, float height) =>
        new(x, y, x + width, y + height);

    public readonly bool Equals(SKRect other) =>
        _left == other._left &&
        _top == other._top &&
        _right == other._right &&
        _bottom == other._bottom;

    public override readonly bool Equals(object? obj) => obj is SKRect other && Equals(other);

    public static bool operator ==(SKRect left, SKRect right) => left.Equals(right);

    public static bool operator !=(SKRect left, SKRect right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_left);
        hash.Add(_top);
        hash.Add(_right);
        hash.Add(_bottom);
        return hash.ToHashCode();
    }

    public override readonly string ToString() =>
        $"{{Left={Left},Top={Top},Width={Width},Height={Height}}}";
}
