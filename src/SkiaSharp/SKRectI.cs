using System;

namespace SkiaSharp;

public partial struct SKRectI
{
    public readonly int MidX => _left + Width / 2;
    public readonly int MidY => _top + Height / 2;
    public readonly bool IsEmpty => Equals(Empty);

    public SKSizeI Size
    {
        readonly get => new(Width, Height);
        set
        {
            _right = _left + value.Width;
            _bottom = _top + value.Height;
        }
    }

    public SKPointI Location
    {
        readonly get => new(_left, _top);
        set => this = Create(value, Size);
    }

    public readonly SKRectI Standardized => new(
        Math.Min(_left, _right),
        Math.Min(_top, _bottom),
        Math.Max(_left, _right),
        Math.Max(_top, _bottom));

    public readonly SKRectI AspectFit(SKSizeI size) => AspectResize(size, fit: true);

    public readonly SKRectI AspectFill(SKSizeI size) => AspectResize(size, fit: false);

    private readonly SKRectI AspectResize(SKSizeI size, bool fit)
    {
        var rectWidth = (float)Width;
        var rectHeight = (float)Height;
        var midpointX = _left + rectWidth / 2f;
        var midpointY = _top + rectHeight / 2f;
        if (size.Width == 0 || size.Height == 0 || rectWidth == 0f || rectHeight == 0f)
        {
            return Floor(SKRect.Create(midpointX, midpointY, 0f, 0f));
        }

        var width = (float)size.Width;
        var height = (float)size.Height;
        var targetAspect = width / height;
        var rectAspect = rectWidth / rectHeight;
        if (fit ? rectAspect > targetAspect : rectAspect < targetAspect)
        {
            height = rectHeight;
            width = height * targetAspect;
        }
        else
        {
            width = rectWidth;
            height = width / targetAspect;
        }

        return Floor(SKRect.Create(
            midpointX - width / 2f,
            midpointY - height / 2f,
            width,
            height));
    }

    public static SKRectI Ceiling(SKRect value) => Ceiling(value, outwards: false);

    public static SKRectI Ceiling(SKRect value, bool outwards)
    {
        checked
        {
            return new SKRectI(
                (int)(outwards && value.Width > 0f ? Math.Floor(value.Left) : Math.Ceiling(value.Left)),
                (int)(outwards && value.Height > 0f ? Math.Floor(value.Top) : Math.Ceiling(value.Top)),
                (int)(outwards && value.Width < 0f ? Math.Floor(value.Right) : Math.Ceiling(value.Right)),
                (int)(outwards && value.Height < 0f ? Math.Floor(value.Bottom) : Math.Ceiling(value.Bottom)));
        }
    }

    public static SKRectI Floor(SKRect value) => Floor(value, inwards: false);

    public static SKRectI Floor(SKRect value, bool inwards)
    {
        checked
        {
            return new SKRectI(
                (int)(inwards && value.Width > 0f ? Math.Ceiling(value.Left) : Math.Floor(value.Left)),
                (int)(inwards && value.Height > 0f ? Math.Ceiling(value.Top) : Math.Floor(value.Top)),
                (int)(inwards && value.Width < 0f ? Math.Ceiling(value.Right) : Math.Floor(value.Right)),
                (int)(inwards && value.Height < 0f ? Math.Ceiling(value.Bottom) : Math.Floor(value.Bottom)));
        }
    }

    public static SKRectI Round(SKRect value)
    {
        checked
        {
            return new SKRectI(
                (int)Math.Round(value.Left),
                (int)Math.Round(value.Top),
                (int)Math.Round(value.Right),
                (int)Math.Round(value.Bottom));
        }
    }

    public static SKRectI Truncate(SKRect value)
    {
        checked
        {
            return new SKRectI(
                (int)value.Left,
                (int)value.Top,
                (int)value.Right,
                (int)value.Bottom);
        }
    }

    public static SKRectI Inflate(SKRectI rect, int x, int y)
    {
        rect.Inflate(x, y);
        return rect;
    }

    public void Inflate(SKSizeI size) => Inflate(size.Width, size.Height);

    public void Inflate(int width, int height)
    {
        _left -= width;
        _top -= height;
        _right += width;
        _bottom += height;
    }

    public static SKRectI Intersect(SKRectI a, SKRectI b) =>
        !a.IntersectsWithInclusive(b)
            ? Empty
            : new SKRectI(
                Math.Max(a._left, b._left),
                Math.Max(a._top, b._top),
                Math.Min(a._right, b._right),
                Math.Min(a._bottom, b._bottom));

    public void Intersect(SKRectI rect) => this = Intersect(this, rect);

    public static SKRectI Union(SKRectI a, SKRectI b) => new(
        Math.Min(a._left, b._left),
        Math.Min(a._top, b._top),
        Math.Max(a._right, b._right),
        Math.Max(a._bottom, b._bottom));

    public void Union(SKRectI rect) => this = Union(this, rect);

    public readonly bool Contains(int x, int y) =>
        x >= _left && x < _right && y >= _top && y < _bottom;

    public readonly bool Contains(SKPointI pt) => Contains(pt.X, pt.Y);

    public readonly bool Contains(SKRectI rect) =>
        _left <= rect._left &&
        _right >= rect._right &&
        _top <= rect._top &&
        _bottom >= rect._bottom;

    public readonly bool IntersectsWith(SKRectI rect) =>
        _left < rect._right &&
        _right > rect._left &&
        _top < rect._bottom &&
        _bottom > rect._top;

    public readonly bool IntersectsWithInclusive(SKRectI rect) =>
        _left <= rect._right &&
        _right >= rect._left &&
        _top <= rect._bottom &&
        _bottom >= rect._top;

    public void Offset(int x, int y)
    {
        _left += x;
        _top += y;
        _right += x;
        _bottom += y;
    }

    public void Offset(SKPointI pos) => Offset(pos.X, pos.Y);

    public static SKRectI Create(SKSizeI size) => Create(SKPointI.Empty, size);

    public static SKRectI Create(SKPointI location, SKSizeI size) =>
        Create(location.X, location.Y, size.Width, size.Height);

    public readonly bool Equals(SKRectI obj) =>
        _left == obj._left &&
        _top == obj._top &&
        _right == obj._right &&
        _bottom == obj._bottom;

    public override readonly bool Equals(object? obj) => obj is SKRectI other && Equals(other);

    public static bool operator ==(SKRectI left, SKRectI right) => left.Equals(right);

    public static bool operator !=(SKRectI left, SKRectI right) => !left.Equals(right);

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
