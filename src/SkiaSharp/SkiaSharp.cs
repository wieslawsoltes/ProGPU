using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using ProGPU.Backend;

namespace SkiaSharp;

public delegate void SKBitmapReleaseDelegate(IntPtr address, object context);
public delegate void SKGlyphPathDelegate(SKPath? path, SKMatrix matrix);

internal static class SKObjectHandle
{
    private static long s_next;

    public static IntPtr Create() => (nint)Interlocked.Increment(ref s_next);
}

public enum SKColorType
{
    Unknown = 0,
    Alpha8 = 1,
    Rgb565 = 2,
    Argb4444 = 3,
    Rgba8888 = 4,
    Rgb888x = 5,
    Bgra8888 = 6,
    Rgba1010102 = 7,
    Rgb101010x = 8,
    Gray8 = 9,
    RgbaF16 = 10,
    RgbaF16Clamped = 11,
    RgbaF32 = 12,
    Rg88 = 13,
    AlphaF16 = 14,
    RgF16 = 15,
    Alpha16 = 16,
    Rg1616 = 17,
    Rgba16161616 = 18,
    Bgra1010102 = 19,
    Bgr101010x = 20,
    Bgr101010xXR = 21,
    Srgba8888 = 22,
    R8Unorm = 23,
    Rgba10x6 = 24,
    Bgra10101010XR = 25,
    RgbF16F16F16x = 26,
    R16Unorm = 27,
    RF16 = 28,
}

public enum SKAlphaType
{
    Unknown = 0,
    Opaque = 1,
    Premul = 2,
    Unpremul = 3,
}

public enum SKBlendMode
{
    Clear = 0,
    Src = 1,
    Dst = 2,
    SrcOver = 3,
    DstOver = 4,
    SrcIn = 5,
    DstIn = 6,
    SrcOut = 7,
    DstOut = 8,
    SrcATop = 9,
    DstATop = 10,
    Xor = 11,
    Plus = 12,
    Modulate = 13,
    Screen = 14,
    Overlay = 15,
    Darken = 16,
    Lighten = 17,
    ColorDodge = 18,
    ColorBurn = 19,
    HardLight = 20,
    SoftLight = 21,
    Difference = 22,
    Exclusion = 23,
    Multiply = 24,
    Hue = 25,
    Saturation = 26,
    Color = 27,
    Luminosity = 28,
}

public enum SKClipOperation
{
    Difference = 0,
    Intersect = 1,
}

public enum SKFilterMode
{
    Nearest = 0,
    Linear = 1,
}

public enum SKMipmapMode
{
    None = 0,
    Nearest = 1,
    Linear = 2,
}

[Obsolete("Use SKSamplingOptions instead.", true)]
public enum SKFilterQuality
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

[Flags]
public enum SKBitmapAllocFlags
{
    None = 0,
    ZeroPixels = 1,
}

public enum SKShaderTileMode
{
    Clamp = 0,
    Repeat = 1,
    Mirror = 2,
    Decal = 3,
}

public enum SKTextAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

public enum SKTextEncoding
{
    Utf8 = 0,
    Utf16 = 1,
    Utf32 = 2,
    GlyphId = 3,
}

public enum SKColorChannel
{
    R = 0,
    G = 1,
    B = 2,
    A = 3,
}

public enum SKStrokeCap
{
    Butt = 0,
    Round = 1,
    Square = 2,
}

public enum SKStrokeJoin
{
    Miter = 0,
    Round = 1,
    Bevel = 2,
}

public enum SKFontStyleSlant
{
    Upright = 0,
    Italic = 1,
    Oblique = 2,
}

public enum SKFontHinting
{
    None = 0,
    Slight = 1,
    Normal = 2,
    Full = 3,
}

public enum SKFontEdging
{
    Alias = 0,
    Antialias = 1,
    SubpixelAntialias = 2,
}

public enum SKPathOp
{
    Difference = 0,
    Intersect = 1,
    Union = 2,
    Xor = 3,
    ReverseDifference = 4,
}

public enum SKPathFillType
{
    Winding = 0,
    EvenOdd = 1,
    InverseWinding = 2,
    InverseEvenOdd = 3,
}

public enum SKPathArcSize
{
    Small = 0,
    Large = 1,
}

public enum SKPathDirection
{
    Clockwise = 0,
    CounterClockwise = 1,
}

public enum SKPathAddMode
{
    Append = 0,
    Extend = 1,
}

public enum SKPixelGeometry
{
    Unknown = 0,
    RgbHorizontal = 1,
    BgrHorizontal = 2,
    RgbVertical = 3,
    BgrVertical = 4,
}

[Flags]
public enum SKSurfacePropsFlags
{
    None = 0,
    UseDeviceIndependentFonts = 1,
}

public enum SKEncodedImageFormat
{
    Bmp = 0,
    Gif = 1,
    Ico = 2,
    Jpeg = 3,
    Png = 4,
    Wbmp = 5,
    Webp = 6,
    Pkm = 7,
    Ktx = 8,
    Astc = 9,
    Dng = 10,
    Heif = 11,
    Avif = 12,
    Jpegxl = 13,
}

public enum SKImageCachingHint
{
    Allow = 0,
    Disallow = 1,
}

public enum SKRegionOperation
{
    Difference = 0,
    Intersect = 1,
    Union = 2,
    Xor = 3,
    ReverseDifference = 4,
    Replace = 5,
}

public struct SKPoint : IEquatable<SKPoint>
{
    private float _x;
    private float _y;

    public static readonly SKPoint Empty;

    public readonly bool IsEmpty => this == Empty;

    public readonly float Length => (float)Math.Sqrt(LengthSquared);

    public readonly float LengthSquared => _x * _x + _y * _y;

    public float X
    {
        readonly get => _x;
        set => _x = value;
    }

    public float Y
    {
        readonly get => _y;
        set => _y = value;
    }

    public SKPoint(float x, float y)
    {
        _x = x;
        _y = y;
    }

    public void Offset(SKPoint p) => Offset(p.X, p.Y);

    public void Offset(float dx, float dy)
    {
        _x += dx;
        _y += dy;
    }

    public static SKPoint Normalize(SKPoint point)
    {
        var inverseLength = 1.0 / Math.Sqrt(point.LengthSquared);
        return new SKPoint((float)(point.X * inverseLength), (float)(point.Y * inverseLength));
    }

    public static float Distance(SKPoint point, SKPoint other) =>
        (float)Math.Sqrt(DistanceSquared(point, other));

    public static float DistanceSquared(SKPoint point, SKPoint other)
    {
        var dx = point.X - other.X;
        var dy = point.Y - other.Y;
        return dx * dx + dy * dy;
    }

    public static SKPoint Reflect(SKPoint point, SKPoint normal)
    {
        var dot = point.LengthSquared;
        return new SKPoint(
            point.X - 2f * dot * normal.X,
            point.Y - 2f * dot * normal.Y);
    }

    public static SKPoint Add(SKPoint pt, SKPoint sz) =>
        new(pt.X + sz.X, pt.Y + sz.Y);

    public static SKPoint Add(SKPoint pt, SKPointI sz) =>
        new(pt.X + sz.X, pt.Y + sz.Y);

    public static SKPoint Add(SKPoint pt, SKSize sz) =>
        new(pt.X + sz.Width, pt.Y + sz.Height);

    public static SKPoint Add(SKPoint pt, SKSizeI sz) =>
        new(pt.X + sz.Width, pt.Y + sz.Height);

    public static SKPoint Subtract(SKPoint pt, SKPoint sz) =>
        new(pt.X - sz.X, pt.Y - sz.Y);

    public static SKPoint Subtract(SKPoint pt, SKPointI sz) =>
        new(pt.X - sz.X, pt.Y - sz.Y);

    public static SKPoint Subtract(SKPoint pt, SKSize sz) =>
        new(pt.X - sz.Width, pt.Y - sz.Height);

    public static SKPoint Subtract(SKPoint pt, SKSizeI sz) =>
        new(pt.X - sz.Width, pt.Y - sz.Height);

    public readonly bool Equals(SKPoint obj) => _x == obj._x && _y == obj._y;

    public override readonly bool Equals(object? obj) => obj is SKPoint other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_x, _y);

    public override readonly string ToString() => $"{{X={_x}, Y={_y}}}";

    public static SKPoint operator +(SKPoint pt, SKPoint sz) => Add(pt, sz);

    public static SKPoint operator +(SKPoint pt, SKPointI sz) => Add(pt, sz);

    public static SKPoint operator +(SKPoint pt, SKSize sz) => Add(pt, sz);

    public static SKPoint operator +(SKPoint pt, SKSizeI sz) => Add(pt, sz);

    public static SKPoint operator -(SKPoint pt, SKPoint sz) => Subtract(pt, sz);

    public static SKPoint operator -(SKPoint pt, SKPointI sz) => Subtract(pt, sz);

    public static SKPoint operator -(SKPoint pt, SKSize sz) => Subtract(pt, sz);

    public static SKPoint operator -(SKPoint pt, SKSizeI sz) => Subtract(pt, sz);

    public static implicit operator Vector2(SKPoint point) => new(point.X, point.Y);

    public static implicit operator SKPoint(Vector2 vector) => new(vector.X, vector.Y);

    public static bool operator ==(SKPoint left, SKPoint right) => left.Equals(right);

    public static bool operator !=(SKPoint left, SKPoint right) => !left.Equals(right);
}

public struct SKPointI : IEquatable<SKPointI>
{
    private int _x;
    private int _y;

    public static readonly SKPointI Empty;

    public readonly bool IsEmpty => this == Empty;
    public readonly int Length => (int)Math.Sqrt(_x * _x + _y * _y);
    public readonly int LengthSquared => _x * _x + _y * _y;

    public int X
    {
        readonly get => _x;
        set => _x = value;
    }

    public int Y
    {
        readonly get => _y;
        set => _y = value;
    }

    public SKPointI(SKSizeI sz)
    {
        _x = sz.Width;
        _y = sz.Height;
    }

    public SKPointI(int x, int y)
    {
        _x = x;
        _y = y;
    }

    public void Offset(SKPointI p)
    {
        _x += p.X;
        _y += p.Y;
    }

    public void Offset(int dx, int dy)
    {
        _x += dx;
        _y += dy;
    }

    public override readonly string ToString() => $"{{X={_x},Y={_y}}}";

    public static SKPointI Normalize(SKPointI point)
    {
        var lengthSquared = point._x * point._x + point._y * point._y;
        var inverseLength = 1d / Math.Sqrt(lengthSquared);
        return new SKPointI((int)(point._x * inverseLength), (int)(point._y * inverseLength));
    }

    public static float Distance(SKPointI point, SKPointI other)
    {
        var dx = point._x - other._x;
        var dy = point._y - other._y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    public static float DistanceSquared(SKPointI point, SKPointI other)
    {
        var dx = point._x - other._x;
        var dy = point._y - other._y;
        return dx * dx + dy * dy;
    }

    public static SKPointI Reflect(SKPointI point, SKPointI normal)
    {
        var dot = point._x * normal._x + point._y * normal._y;
        return new SKPointI(
            (int)(point._x - 2f * dot * normal._x),
            (int)(point._y - 2f * dot * normal._y));
    }

    public static SKPointI Ceiling(SKPoint value)
    {
        checked
        {
            return new SKPointI((int)Math.Ceiling(value.X), (int)Math.Ceiling(value.Y));
        }
    }

    public static SKPointI Round(SKPoint value)
    {
        checked
        {
            return new SKPointI((int)Math.Round(value.X), (int)Math.Round(value.Y));
        }
    }

    public static SKPointI Truncate(SKPoint value)
    {
        checked
        {
            return new SKPointI((int)value.X, (int)value.Y);
        }
    }

    public static SKPointI Add(SKPointI pt, SKSizeI sz) => pt + sz;
    public static SKPointI Add(SKPointI pt, SKPointI sz) => pt + sz;
    public static SKPointI Subtract(SKPointI pt, SKSizeI sz) => pt - sz;
    public static SKPointI Subtract(SKPointI pt, SKPointI sz) => pt - sz;

    public static SKPointI operator +(SKPointI pt, SKSizeI sz) =>
        new(pt.X + sz.Width, pt.Y + sz.Height);

    public static SKPointI operator +(SKPointI pt, SKPointI sz) =>
        new(pt.X + sz.X, pt.Y + sz.Y);

    public static SKPointI operator -(SKPointI pt, SKSizeI sz) =>
        new(pt.X - sz.Width, pt.Y - sz.Height);

    public static SKPointI operator -(SKPointI pt, SKPointI sz) =>
        new(pt.X - sz.X, pt.Y - sz.Y);

    public static explicit operator SKSizeI(SKPointI p) => new(p.X, p.Y);
    public static implicit operator SKPoint(SKPointI p) => new(p.X, p.Y);
    public static implicit operator Vector2(SKPointI point) => new(point._x, point._y);

    public readonly bool Equals(SKPointI obj) => _x == obj._x && _y == obj._y;
    public override readonly bool Equals(object? obj) => obj is SKPointI other && Equals(other);
    public static bool operator ==(SKPointI left, SKPointI right) => left.Equals(right);
    public static bool operator !=(SKPointI left, SKPointI right) => !left.Equals(right);
    public override readonly int GetHashCode() => HashCode.Combine(_x, _y);
}

public struct SKPoint3 : IEquatable<SKPoint3>
{
    private float _x;
    private float _y;
    private float _z;

    public static readonly SKPoint3 Empty;

    public readonly bool IsEmpty => this == Empty;

    public float X
    {
        readonly get => _x;
        set => _x = value;
    }

    public float Y
    {
        readonly get => _y;
        set => _y = value;
    }

    public float Z
    {
        readonly get => _z;
        set => _z = value;
    }

    public SKPoint3(float x, float y, float z)
    {
        _x = x;
        _y = y;
        _z = z;
    }

    public override readonly string ToString() => $"{{X={_x}, Y={_y}, Z={_z}}}";

    public static SKPoint3 Add(SKPoint3 pt, SKPoint3 sz) => pt + sz;
    public static SKPoint3 Subtract(SKPoint3 pt, SKPoint3 sz) => pt - sz;

    public static SKPoint3 operator +(SKPoint3 pt, SKPoint3 sz) =>
        new(pt.X + sz.X, pt.Y + sz.Y, pt.Z + sz.Z);

    public static SKPoint3 operator -(SKPoint3 pt, SKPoint3 sz) =>
        new(pt.X - sz.X, pt.Y - sz.Y, pt.Z - sz.Z);

    public static implicit operator Vector3(SKPoint3 point) => new(point._x, point._y, point._z);
    public static implicit operator SKPoint3(Vector3 vector) => new(vector.X, vector.Y, vector.Z);

    public readonly bool Equals(SKPoint3 obj) => _x == obj._x && _y == obj._y && _z == obj._z;
    public override readonly bool Equals(object? obj) => obj is SKPoint3 other && Equals(other);
    public static bool operator ==(SKPoint3 left, SKPoint3 right) => left.Equals(right);
    public static bool operator !=(SKPoint3 left, SKPoint3 right) => !left.Equals(right);
    public override readonly int GetHashCode() => HashCode.Combine(_x, _y, _z);
}

public struct SKSize : IEquatable<SKSize>
{
    private float _width;
    private float _height;

    public static readonly SKSize Empty;

    public readonly bool IsEmpty => this == Empty;

    public float Width
    {
        readonly get => _width;
        set => _width = value;
    }

    public float Height
    {
        readonly get => _height;
        set => _height = value;
    }

    public SKSize(float width, float height)
    {
        _width = width;
        _height = height;
    }

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

    public static SKSize Add(SKSize sz1, SKSize sz2) => sz1 + sz2;

    public static SKSize Subtract(SKSize sz1, SKSize sz2) => sz1 - sz2;

    public readonly bool Equals(SKSize obj) => _width == obj._width && _height == obj._height;

    public override readonly bool Equals(object? obj) => obj is SKSize other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_width, _height);

    public override readonly string ToString() => $"{{Width={_width}, Height={_height}}}";

    public static SKSize operator +(SKSize sz1, SKSize sz2) =>
        new(sz1.Width + sz2.Width, sz1.Height + sz2.Height);

    public static SKSize operator -(SKSize sz1, SKSize sz2) =>
        new(sz1.Width - sz2.Width, sz1.Height - sz2.Height);

    public static bool operator ==(SKSize left, SKSize right) => left.Equals(right);

    public static bool operator !=(SKSize left, SKSize right) => !left.Equals(right);

    public static explicit operator SKPoint(SKSize size) => size.ToPoint();

    public static implicit operator SKSize(SKSizeI size) => new(size.Width, size.Height);
}

public struct SKSizeI : IEquatable<SKSizeI>
{
    private int _width;
    private int _height;

    public static readonly SKSizeI Empty;

    public readonly bool IsEmpty => this == Empty;

    public int Width
    {
        readonly get => _width;
        set => _width = value;
    }

    public int Height
    {
        readonly get => _height;
        set => _height = value;
    }

    public SKSizeI(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public SKSizeI(SKPointI pt)
    {
        _width = pt.X;
        _height = pt.Y;
    }

    public readonly SKPointI ToPointI() => new(_width, _height);

    public static SKSizeI Add(SKSizeI sz1, SKSizeI sz2) => sz1 + sz2;

    public static SKSizeI Subtract(SKSizeI sz1, SKSizeI sz2) => sz1 - sz2;

    public readonly bool Equals(SKSizeI obj) => _width == obj._width && _height == obj._height;

    public override readonly bool Equals(object? obj) => obj is SKSizeI other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_width, _height);

    public override readonly string ToString() => $"{{Width={_width}, Height={_height}}}";

    public static SKSizeI operator +(SKSizeI sz1, SKSizeI sz2) =>
        new(sz1.Width + sz2.Width, sz1.Height + sz2.Height);

    public static SKSizeI operator -(SKSizeI sz1, SKSizeI sz2) =>
        new(sz1.Width - sz2.Width, sz1.Height - sz2.Height);

    public static bool operator ==(SKSizeI left, SKSizeI right) => left.Equals(right);

    public static bool operator !=(SKSizeI left, SKSizeI right) => !left.Equals(right);

    public static explicit operator SKPointI(SKSizeI size) => size.ToPointI();
}

public struct SKRect : IEquatable<SKRect>
{
    private float _left;
    private float _top;
    private float _right;
    private float _bottom;

    public static readonly SKRect Empty;

    public float Left
    {
        readonly get => _left;
        set => _left = value;
    }

    public float Top
    {
        readonly get => _top;
        set => _top = value;
    }

    public float Right
    {
        readonly get => _right;
        set => _right = value;
    }

    public float Bottom
    {
        readonly get => _bottom;
        set => _bottom = value;
    }

    public readonly float Width => _right - _left;

    public readonly float Height => _bottom - _top;

    public readonly float MidX => _left + Width * 0.5f;

    public readonly float MidY => _top + Height * 0.5f;

    public readonly bool IsEmpty => this == Empty;

    public SKPoint Location
    {
        readonly get => new(_left, _top);
        set
        {
            var width = Width;
            var height = Height;
            _left = value.X;
            _top = value.Y;
            _right = value.X + width;
            _bottom = value.Y + height;
        }
    }

    public SKSize Size
    {
        readonly get => new(Width, Height);
        set
        {
            _right = _left + value.Width;
            _bottom = _top + value.Height;
        }
    }

    public readonly SKRect Standardized => new(
        MathF.Min(_left, _right),
        MathF.Min(_top, _bottom),
        MathF.Max(_left, _right),
        MathF.Max(_top, _bottom));

    public SKRect(float left, float top, float right, float bottom)
    {
        _left = left;
        _top = top;
        _right = right;
        _bottom = bottom;
    }

    public static SKRect Create(float width, float height) => new(0f, 0f, width, height);

    public static SKRect Create(float x, float y, float width, float height) =>
        new(x, y, x + width, y + height);

    public static SKRect Create(SKSize size) => Create(size.Width, size.Height);

    public static SKRect Create(SKPoint location, SKSize size) =>
        Create(location.X, location.Y, size.Width, size.Height);

    public readonly bool Contains(SKPoint pt) => Contains(pt.X, pt.Y);

    public readonly bool Contains(float x, float y) =>
        _left <= x && x < _right && _top <= y && y < _bottom;

    public readonly bool Contains(SKRect rect) =>
        _left <= rect.Left && _top <= rect.Top &&
        _right >= rect.Right && _bottom >= rect.Bottom;

    public void Inflate(SKSize size) => Inflate(size.Width, size.Height);

    public void Inflate(float x, float y)
    {
        _left -= x;
        _top -= y;
        _right += x;
        _bottom += y;
    }

    public static SKRect Inflate(SKRect rect, float x, float y)
    {
        rect.Inflate(x, y);
        return rect;
    }

    public void Offset(SKPoint pos) => Offset(pos.X, pos.Y);

    public void Offset(float x, float y)
    {
        _left += x;
        _top += y;
        _right += x;
        _bottom += y;
    }

    public void Intersect(SKRect rect) => this = Intersect(this, rect);

    public static SKRect Intersect(SKRect a, SKRect b)
    {
        if (!a.IntersectsWithInclusive(b))
        {
            return Empty;
        }

        return new SKRect(
            MathF.Max(a.Left, b.Left),
            MathF.Max(a.Top, b.Top),
            MathF.Min(a.Right, b.Right),
            MathF.Min(a.Bottom, b.Bottom));
    }

    public readonly bool IntersectsWith(SKRect rect) =>
        _left < rect.Right && rect.Left < _right &&
        _top < rect.Bottom && rect.Top < _bottom;

    public readonly bool IntersectsWithInclusive(SKRect rect) =>
        _left <= rect.Right && rect.Left <= _right &&
        _top <= rect.Bottom && rect.Top <= _bottom;

    public void Union(SKRect rect)
    {
        if (IsEmpty)
        {
            this = rect;
        }
        else if (!rect.IsEmpty)
        {
            this = Union(this, rect);
        }
    }

    public static SKRect Union(SKRect a, SKRect b)
        => new(
            MathF.Min(a.Left, b.Left),
            MathF.Min(a.Top, b.Top),
            MathF.Max(a.Right, b.Right),
            MathF.Max(a.Bottom, b.Bottom));

    public readonly SKRect AspectFit(SKSize size) => AspectResize(size, fit: true);

    public readonly SKRect AspectFill(SKSize size) => AspectResize(size, fit: false);

    private readonly SKRect AspectResize(SKSize size, bool fit)
    {
        if (size.Width == 0f || size.Height == 0f || Width == 0f || Height == 0f)
        {
            return Create(MidX, MidY, 0f, 0f);
        }

        var aspectWidth = size.Width;
        var aspectHeight = size.Height;
        var imageAspect = aspectWidth / aspectHeight;
        var rectAspect = Width / Height;
        var resizeHeight = fit ? rectAspect > imageAspect : rectAspect < imageAspect;
        if (resizeHeight)
        {
            aspectHeight = Height;
            aspectWidth = aspectHeight * imageAspect;
        }
        else
        {
            aspectWidth = Width;
            aspectHeight = aspectWidth / imageAspect;
        }

        return Create(
            MidX - aspectWidth * 0.5f,
            MidY - aspectHeight * 0.5f,
            aspectWidth,
            aspectHeight);
    }

    public readonly bool Equals(SKRect obj) =>
        _left == obj._left && _top == obj._top &&
        _right == obj._right && _bottom == obj._bottom;

    public override readonly bool Equals(object? obj) => obj is SKRect other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_left, _top, _right, _bottom);

    public override readonly string ToString() =>
        $"{{Left={Left},Top={Top},Width={Width},Height={Height}}}";

    public static bool operator ==(SKRect left, SKRect right) => left.Equals(right);

    public static bool operator !=(SKRect left, SKRect right) => !left.Equals(right);

    public static implicit operator SKRect(SKRectI r) =>
        new(r.Left, r.Top, r.Right, r.Bottom);
}

public struct SKRectI : IEquatable<SKRectI>
{
    private int _left;
    private int _top;
    private int _right;
    private int _bottom;

    public static readonly SKRectI Empty;

    public int Left
    {
        readonly get => _left;
        set => _left = value;
    }

    public int Top
    {
        readonly get => _top;
        set => _top = value;
    }

    public int Right
    {
        readonly get => _right;
        set => _right = value;
    }

    public int Bottom
    {
        readonly get => _bottom;
        set => _bottom = value;
    }

    public readonly int Width => _right - _left;

    public readonly int Height => _bottom - _top;

    public readonly int MidX => _left + Width / 2;

    public readonly int MidY => _top + Height / 2;

    public readonly bool IsEmpty => this == Empty;

    public SKPointI Location
    {
        readonly get => new(_left, _top);
        set
        {
            var size = Size;
            this = Create(value, size);
        }
    }

    public SKSizeI Size
    {
        readonly get => new(Width, Height);
        set
        {
            _right = _left + value.Width;
            _bottom = _top + value.Height;
        }
    }

    public readonly SKRectI Standardized => new(
        Math.Min(_left, _right),
        Math.Min(_top, _bottom),
        Math.Max(_left, _right),
        Math.Max(_top, _bottom));

    public SKRectI(int left, int top, int right, int bottom)
    {
        _left = left;
        _top = top;
        _right = right;
        _bottom = bottom;
    }

    public readonly SKRectI AspectFit(SKSizeI size) => Floor(((SKRect)this).AspectFit(size));

    public readonly SKRectI AspectFill(SKSizeI size) => Floor(((SKRect)this).AspectFill(size));

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
            return new SKRectI((int)value.Left, (int)value.Top, (int)value.Right, (int)value.Bottom);
        }
    }

    public void Inflate(SKSizeI size) => Inflate(size.Width, size.Height);

    public void Inflate(int width, int height)
    {
        _left -= width;
        _top -= height;
        _right += width;
        _bottom += height;
    }

    public static SKRectI Inflate(SKRectI rect, int x, int y)
    {
        rect.Inflate(x, y);
        return rect;
    }

    public void Offset(SKPointI pos) => Offset(pos.X, pos.Y);

    public void Offset(int x, int y)
    {
        _left += x;
        _top += y;
        _right += x;
        _bottom += y;
    }

    public void Intersect(SKRectI rect) => this = Intersect(this, rect);

    public static SKRectI Intersect(SKRectI a, SKRectI b)
    {
        if (!a.IntersectsWithInclusive(b))
        {
            return Empty;
        }

        return new SKRectI(
            Math.Max(a.Left, b.Left),
            Math.Max(a.Top, b.Top),
            Math.Min(a.Right, b.Right),
            Math.Min(a.Bottom, b.Bottom));
    }

    public readonly bool IntersectsWith(SKRectI rect) =>
        _left < rect.Right && rect.Left < _right &&
        _top < rect.Bottom && rect.Top < _bottom;

    public readonly bool IntersectsWithInclusive(SKRectI rect) =>
        _left <= rect.Right && rect.Left <= _right &&
        _top <= rect.Bottom && rect.Top <= _bottom;

    public void Union(SKRectI rect) => this = Union(this, rect);

    public static SKRectI Union(SKRectI a, SKRectI b) => new(
        Math.Min(a.Left, b.Left),
        Math.Min(a.Top, b.Top),
        Math.Max(a.Right, b.Right),
        Math.Max(a.Bottom, b.Bottom));

    public readonly bool Contains(SKPointI pt) => Contains(pt.X, pt.Y);

    public readonly bool Contains(int x, int y) =>
        _left <= x && x < _right && _top <= y && y < _bottom;

    public readonly bool Contains(SKRectI rect) =>
        _left <= rect.Left && _top <= rect.Top &&
        _right >= rect.Right && _bottom >= rect.Bottom;

    public static SKRectI Create(int width, int height) => new(0, 0, width, height);

    public static SKRectI Create(int x, int y, int width, int height) =>
        new(x, y, x + width, y + height);

    public static SKRectI Create(SKSizeI size) => Create(size.Width, size.Height);

    public static SKRectI Create(SKPointI location, SKSizeI size) =>
        Create(location.X, location.Y, size.Width, size.Height);

    public readonly bool Equals(SKRectI obj) =>
        _left == obj._left && _top == obj._top &&
        _right == obj._right && _bottom == obj._bottom;

    public override readonly bool Equals(object? obj) => obj is SKRectI other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_left, _top, _right, _bottom);

    public override readonly string ToString() =>
        $"{{Left={Left},Top={Top},Width={Width},Height={Height}}}";

    public static bool operator ==(SKRectI left, SKRectI right) => left.Equals(right);

    public static bool operator !=(SKRectI left, SKRectI right) => !left.Equals(right);
}

public readonly struct SKColor : IEquatable<SKColor>
{
    private readonly uint _color;

    public static readonly SKColor Empty;

    public byte Alpha => (byte)((_color >> 24) & 0xff);

    public byte Red => (byte)((_color >> 16) & 0xff);

    public byte Green => (byte)((_color >> 8) & 0xff);

    public byte Blue => (byte)(_color & 0xff);

    internal byte A => Alpha;

    internal byte R => Red;

    internal byte G => Green;

    internal byte B => Blue;

    public float Hue
    {
        get
        {
            ToHsv(out var hue, out _, out _);
            return hue;
        }
    }

    public SKColor(byte red, byte green, byte blue, byte alpha)
    {
        _color = (uint)((alpha << 24) | (red << 16) | (green << 8) | blue);
    }

    public SKColor(byte red, byte green, byte blue)
    {
        _color = 0xff000000u | (uint)(red << 16) | (uint)(green << 8) | blue;
    }

    public SKColor(uint value)
    {
        _color = value;
    }

    public SKColor WithRed(byte red) => new(red, Green, Blue, Alpha);

    public SKColor WithGreen(byte green) => new(Red, green, Blue, Alpha);

    public SKColor WithBlue(byte blue) => new(Red, Green, blue, Alpha);

    public SKColor WithAlpha(byte alpha) => new(Red, Green, Blue, alpha);

    public static SKColor FromHsl(float h, float s, float l, byte a = byte.MaxValue)
    {
        var color = SKColorF.FromHsl(h, s, l);
        return new SKColor(
            (byte)(color.Red * 255f),
            (byte)(color.Green * 255f),
            (byte)(color.Blue * 255f),
            a);
    }

    public static SKColor FromHsv(float h, float s, float v, byte a = byte.MaxValue)
    {
        var color = SKColorF.FromHsv(h, s, v);
        return new SKColor(
            (byte)(color.Red * 255f),
            (byte)(color.Green * 255f),
            (byte)(color.Blue * 255f),
            a);
    }

    public void ToHsl(out float h, out float s, out float l) =>
        new SKColorF(Red / 255f, Green / 255f, Blue / 255f).ToHsl(out h, out s, out l);

    public void ToHsv(out float h, out float s, out float v) =>
        new SKColorF(Red / 255f, Green / 255f, Blue / 255f).ToHsv(out h, out s, out v);

    public bool Equals(SKColor obj) => _color == obj._color;

    public override bool Equals(object? other) => other is SKColor color && Equals(color);

    public override int GetHashCode() => _color.GetHashCode();

    public override string ToString() => $"#{Alpha:x2}{Red:x2}{Green:x2}{Blue:x2}";

    public static SKColor Parse(string hexString)
    {
        if (!TryParse(hexString, out var color))
        {
            throw new ArgumentException("Invalid hexadecimal color string.", nameof(hexString));
        }

        return color;
    }

    public static bool TryParse(string? hexString, out SKColor color)
    {
        if (string.IsNullOrWhiteSpace(hexString))
        {
            color = Empty;
            return false;
        }

        var value = hexString.AsSpan().Trim().TrimStart('#');
        var length = value.Length;
        if (length is 3 or 4)
        {
            byte alpha;
            if (length == 4)
            {
                if (!byte.TryParse(value[..1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out alpha))
                {
                    color = Empty;
                    return false;
                }

                alpha = (byte)((alpha << 4) | alpha);
            }
            else
            {
                alpha = byte.MaxValue;
            }

            if (!byte.TryParse(value.Slice(length - 3, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
                || !byte.TryParse(value.Slice(length - 2, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
                || !byte.TryParse(value.Slice(length - 1, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
            {
                color = Empty;
                return false;
            }

            color = new SKColor(
                (byte)((red << 4) | red),
                (byte)((green << 4) | green),
                (byte)((blue << 4) | blue),
                alpha);
            return true;
        }

        if (length is 6 or 8
            && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            color = new SKColor(packed);
            if (length == 6)
            {
                color = color.WithAlpha(byte.MaxValue);
            }

            return true;
        }

        color = Empty;
        return false;
    }

    public static bool operator ==(SKColor left, SKColor right) => left.Equals(right);

    public static bool operator !=(SKColor left, SKColor right) => !left.Equals(right);

    public static implicit operator SKColor(uint color) => new(color);

    public static explicit operator uint(SKColor color) => color._color;
}

public static class SKColors
{
    public static readonly SKColor AliceBlue = new(240, 248, 255, 255);
    public static readonly SKColor AntiqueWhite = new(250, 235, 215, 255);
    public static readonly SKColor Aqua = new(0, 255, 255, 255);
    public static readonly SKColor Aquamarine = new(127, 255, 212, 255);
    public static readonly SKColor Azure = new(240, 255, 255, 255);
    public static readonly SKColor Beige = new(245, 245, 220, 255);
    public static readonly SKColor Bisque = new(255, 228, 196, 255);
    public static readonly SKColor Black = new(0, 0, 0, 255);
    public static readonly SKColor BlanchedAlmond = new(255, 235, 205, 255);
    public static readonly SKColor Blue = new(0, 0, 255, 255);
    public static readonly SKColor BlueViolet = new(138, 43, 226, 255);
    public static readonly SKColor Brown = new(165, 42, 42, 255);
    public static readonly SKColor BurlyWood = new(222, 184, 135, 255);
    public static readonly SKColor CadetBlue = new(95, 158, 160, 255);
    public static readonly SKColor Chartreuse = new(127, 255, 0, 255);
    public static readonly SKColor Chocolate = new(210, 105, 30, 255);
    public static readonly SKColor Coral = new(255, 127, 80, 255);
    public static readonly SKColor CornflowerBlue = new(100, 149, 237, 255);
    public static readonly SKColor Cornsilk = new(255, 248, 220, 255);
    public static readonly SKColor Crimson = new(220, 20, 60, 255);
    public static readonly SKColor Cyan = new(0, 255, 255, 255);
    public static readonly SKColor DarkBlue = new(0, 0, 139, 255);
    public static readonly SKColor DarkCyan = new(0, 139, 139, 255);
    public static readonly SKColor DarkGoldenrod = new(184, 134, 11, 255);
    public static readonly SKColor DarkGray = new(169, 169, 169, 255);
    public static readonly SKColor DarkGreen = new(0, 100, 0, 255);
    public static readonly SKColor DarkKhaki = new(189, 183, 107, 255);
    public static readonly SKColor DarkMagenta = new(139, 0, 139, 255);
    public static readonly SKColor DarkOliveGreen = new(85, 107, 47, 255);
    public static readonly SKColor DarkOrange = new(255, 140, 0, 255);
    public static readonly SKColor DarkOrchid = new(153, 50, 204, 255);
    public static readonly SKColor DarkRed = new(139, 0, 0, 255);
    public static readonly SKColor DarkSalmon = new(233, 150, 122, 255);
    public static readonly SKColor DarkSeaGreen = new(143, 188, 139, 255);
    public static readonly SKColor DarkSlateBlue = new(72, 61, 139, 255);
    public static readonly SKColor DarkSlateGray = new(47, 79, 79, 255);
    public static readonly SKColor DarkTurquoise = new(0, 206, 209, 255);
    public static readonly SKColor DarkViolet = new(148, 0, 211, 255);
    public static readonly SKColor DeepPink = new(255, 20, 147, 255);
    public static readonly SKColor DeepSkyBlue = new(0, 191, 255, 255);
    public static readonly SKColor DimGray = new(105, 105, 105, 255);
    public static readonly SKColor DodgerBlue = new(30, 144, 255, 255);
    public static SKColor Empty => SKColor.Empty;
    public static readonly SKColor Firebrick = new(178, 34, 34, 255);
    public static readonly SKColor FloralWhite = new(255, 250, 240, 255);
    public static readonly SKColor ForestGreen = new(34, 139, 34, 255);
    public static readonly SKColor Fuchsia = new(255, 0, 255, 255);
    public static readonly SKColor Gainsboro = new(220, 220, 220, 255);
    public static readonly SKColor GhostWhite = new(248, 248, 255, 255);
    public static readonly SKColor Gold = new(255, 215, 0, 255);
    public static readonly SKColor Goldenrod = new(218, 165, 32, 255);
    public static readonly SKColor Gray = new(128, 128, 128, 255);
    public static readonly SKColor Green = new(0, 128, 0, 255);
    public static readonly SKColor GreenYellow = new(173, 255, 47, 255);
    public static readonly SKColor Honeydew = new(240, 255, 240, 255);
    public static readonly SKColor HotPink = new(255, 105, 180, 255);
    public static readonly SKColor IndianRed = new(205, 92, 92, 255);
    public static readonly SKColor Indigo = new(75, 0, 130, 255);
    public static readonly SKColor Ivory = new(255, 255, 240, 255);
    public static readonly SKColor Khaki = new(240, 230, 140, 255);
    public static readonly SKColor Lavender = new(230, 230, 250, 255);
    public static readonly SKColor LavenderBlush = new(255, 240, 245, 255);
    public static readonly SKColor LawnGreen = new(124, 252, 0, 255);
    public static readonly SKColor LemonChiffon = new(255, 250, 205, 255);
    public static readonly SKColor LightBlue = new(173, 216, 230, 255);
    public static readonly SKColor LightCoral = new(240, 128, 128, 255);
    public static readonly SKColor LightCyan = new(224, 255, 255, 255);
    public static readonly SKColor LightGoldenrodYellow = new(250, 250, 210, 255);
    public static readonly SKColor LightGray = new(211, 211, 211, 255);
    public static readonly SKColor LightGreen = new(144, 238, 144, 255);
    public static readonly SKColor LightPink = new(255, 182, 193, 255);
    public static readonly SKColor LightSalmon = new(255, 160, 122, 255);
    public static readonly SKColor LightSeaGreen = new(32, 178, 170, 255);
    public static readonly SKColor LightSkyBlue = new(135, 206, 250, 255);
    public static readonly SKColor LightSlateGray = new(119, 136, 153, 255);
    public static readonly SKColor LightSteelBlue = new(176, 196, 222, 255);
    public static readonly SKColor LightYellow = new(255, 255, 224, 255);
    public static readonly SKColor Lime = new(0, 255, 0, 255);
    public static readonly SKColor LimeGreen = new(50, 205, 50, 255);
    public static readonly SKColor Linen = new(250, 240, 230, 255);
    public static readonly SKColor Magenta = new(255, 0, 255, 255);
    public static readonly SKColor Maroon = new(128, 0, 0, 255);
    public static readonly SKColor MediumAquamarine = new(102, 205, 170, 255);
    public static readonly SKColor MediumBlue = new(0, 0, 205, 255);
    public static readonly SKColor MediumOrchid = new(186, 85, 211, 255);
    public static readonly SKColor MediumPurple = new(147, 112, 219, 255);
    public static readonly SKColor MediumSeaGreen = new(60, 179, 113, 255);
    public static readonly SKColor MediumSlateBlue = new(123, 104, 238, 255);
    public static readonly SKColor MediumSpringGreen = new(0, 250, 154, 255);
    public static readonly SKColor MediumTurquoise = new(72, 209, 204, 255);
    public static readonly SKColor MediumVioletRed = new(199, 21, 133, 255);
    public static readonly SKColor MidnightBlue = new(25, 25, 112, 255);
    public static readonly SKColor MintCream = new(245, 255, 250, 255);
    public static readonly SKColor MistyRose = new(255, 228, 225, 255);
    public static readonly SKColor Moccasin = new(255, 228, 181, 255);
    public static readonly SKColor NavajoWhite = new(255, 222, 173, 255);
    public static readonly SKColor Navy = new(0, 0, 128, 255);
    public static readonly SKColor OldLace = new(253, 245, 230, 255);
    public static readonly SKColor Olive = new(128, 128, 0, 255);
    public static readonly SKColor OliveDrab = new(107, 142, 35, 255);
    public static readonly SKColor Orange = new(255, 165, 0, 255);
    public static readonly SKColor OrangeRed = new(255, 69, 0, 255);
    public static readonly SKColor Orchid = new(218, 112, 214, 255);
    public static readonly SKColor PaleGoldenrod = new(238, 232, 170, 255);
    public static readonly SKColor PaleGreen = new(152, 251, 152, 255);
    public static readonly SKColor PaleTurquoise = new(175, 238, 238, 255);
    public static readonly SKColor PaleVioletRed = new(219, 112, 147, 255);
    public static readonly SKColor PapayaWhip = new(255, 239, 213, 255);
    public static readonly SKColor PeachPuff = new(255, 218, 185, 255);
    public static readonly SKColor Peru = new(205, 133, 63, 255);
    public static readonly SKColor Pink = new(255, 192, 203, 255);
    public static readonly SKColor Plum = new(221, 160, 221, 255);
    public static readonly SKColor PowderBlue = new(176, 224, 230, 255);
    public static readonly SKColor Purple = new(128, 0, 128, 255);
    public static readonly SKColor Red = new(255, 0, 0, 255);
    public static readonly SKColor RosyBrown = new(188, 143, 143, 255);
    public static readonly SKColor RoyalBlue = new(65, 105, 225, 255);
    public static readonly SKColor SaddleBrown = new(139, 69, 19, 255);
    public static readonly SKColor Salmon = new(250, 128, 114, 255);
    public static readonly SKColor SandyBrown = new(244, 164, 96, 255);
    public static readonly SKColor SeaGreen = new(46, 139, 87, 255);
    public static readonly SKColor SeaShell = new(255, 245, 238, 255);
    public static readonly SKColor Sienna = new(160, 82, 45, 255);
    public static readonly SKColor Silver = new(192, 192, 192, 255);
    public static readonly SKColor SkyBlue = new(135, 206, 235, 255);
    public static readonly SKColor SlateBlue = new(106, 90, 205, 255);
    public static readonly SKColor SlateGray = new(112, 128, 144, 255);
    public static readonly SKColor Snow = new(255, 250, 250, 255);
    public static readonly SKColor SpringGreen = new(0, 255, 127, 255);
    public static readonly SKColor SteelBlue = new(70, 130, 180, 255);
    public static readonly SKColor Tan = new(210, 180, 140, 255);
    public static readonly SKColor Teal = new(0, 128, 128, 255);
    public static readonly SKColor Thistle = new(216, 191, 216, 255);
    public static readonly SKColor Tomato = new(255, 99, 71, 255);
    public static readonly SKColor Transparent = new(255, 255, 255, 0);
    public static readonly SKColor Turquoise = new(64, 224, 208, 255);
    public static readonly SKColor Violet = new(238, 130, 238, 255);
    public static readonly SKColor Wheat = new(245, 222, 179, 255);
    public static readonly SKColor White = new(255, 255, 255, 255);
    public static readonly SKColor WhiteSmoke = new(245, 245, 245, 255);
    public static readonly SKColor Yellow = new(255, 255, 0, 255);
    public static readonly SKColor YellowGreen = new(154, 205, 50, 255);
}

public readonly struct SKColorF : IEquatable<SKColorF>
{
    private const float Epsilon = 0.001f;

    private readonly float _red;
    private readonly float _green;
    private readonly float _blue;
    private readonly float _alpha;

    public static readonly SKColorF Empty;

    public float Red => _red;

    public float Green => _green;

    public float Blue => _blue;

    public float Alpha => _alpha;

    internal float R => _red;

    internal float G => _green;

    internal float B => _blue;

    internal float A => _alpha;

    public float Hue
    {
        get
        {
            ToHsv(out var hue, out _, out _);
            return hue;
        }
    }

    public SKColorF(float red, float green, float blue)
    {
        _red = red;
        _green = green;
        _blue = blue;
        _alpha = 1f;
    }

    public SKColorF(float red, float green, float blue, float alpha)
    {
        _red = red;
        _green = green;
        _blue = blue;
        _alpha = alpha;
    }

    public SKColorF WithRed(float red) => new(red, _green, _blue, _alpha);

    public SKColorF WithGreen(float green) => new(_red, green, _blue, _alpha);

    public SKColorF WithBlue(float blue) => new(_red, _green, blue, _alpha);

    public SKColorF WithAlpha(float alpha) => new(_red, _green, _blue, alpha);

    public SKColorF Clamp() => new(
        Math.Clamp(_red, 0f, 1f),
        Math.Clamp(_green, 0f, 1f),
        Math.Clamp(_blue, 0f, 1f),
        Math.Clamp(_alpha, 0f, 1f));

    public static SKColorF FromHsl(float h, float s, float l, float a = 1f)
    {
        h /= 360f;
        s /= 100f;
        l /= 100f;

        var red = l;
        var green = l;
        var blue = l;
        if (Math.Abs(s) > Epsilon)
        {
            var value2 = l < 0.5f ? l * (1f + s) : l + s - s * l;
            var value1 = 2f * l - value2;
            red = HueToRgb(value1, value2, h + 1f / 3f);
            green = HueToRgb(value1, value2, h);
            blue = HueToRgb(value1, value2, h - 1f / 3f);
        }

        return new SKColorF(red, green, blue, a);
    }

    private static float HueToRgb(float value1, float value2, float hue)
    {
        if (hue < 0f)
        {
            hue += 1f;
        }

        if (hue > 1f)
        {
            hue -= 1f;
        }

        if (6f * hue < 1f)
        {
            return value1 + (value2 - value1) * 6f * hue;
        }

        if (2f * hue < 1f)
        {
            return value2;
        }

        if (3f * hue < 2f)
        {
            return value1 + (value2 - value1) * (2f / 3f - hue) * 6f;
        }

        return value1;
    }

    public static SKColorF FromHsv(float h, float s, float v, float a = 1f)
    {
        h /= 360f;
        s /= 100f;
        v /= 100f;

        var red = v;
        var green = v;
        var blue = v;
        if (Math.Abs(s) > Epsilon)
        {
            h *= 6f;
            if (Math.Abs(h - 6f) < Epsilon)
            {
                h = 0f;
            }

            var sector = (int)h;
            var value1 = v * (1f - s);
            var value2 = v * (1f - s * (h - sector));
            var value3 = v * (1f - s * (1f - (h - sector)));
            if (sector == 0)
            {
                red = v;
                green = value3;
                blue = value1;
            }
            else if (sector == 1)
            {
                red = value2;
                green = v;
                blue = value1;
            }
            else if (sector == 2)
            {
                red = value1;
                green = v;
                blue = value3;
            }
            else if (sector == 3)
            {
                red = value1;
                green = value2;
                blue = v;
            }
            else if (sector == 4)
            {
                red = value3;
                green = value1;
                blue = v;
            }
            else
            {
                red = v;
                green = value1;
                blue = value2;
            }
        }

        return new SKColorF(red, green, blue, a);
    }

    public void ToHsl(out float h, out float s, out float l)
    {
        var minimum = Math.Min(Math.Min(_red, _green), _blue);
        var maximum = Math.Max(Math.Max(_red, _green), _blue);
        var delta = maximum - minimum;

        h = 0f;
        s = 0f;
        l = (maximum + minimum) / 2f;
        if (Math.Abs(delta) > Epsilon)
        {
            s = l < 0.5f
                ? delta / (maximum + minimum)
                : delta / (2f - maximum - minimum);
            ResolveHue(_red, _green, _blue, maximum, delta, out h);
        }

        h *= 360f;
        s *= 100f;
        l *= 100f;
    }

    public void ToHsv(out float h, out float s, out float v)
    {
        var minimum = Math.Min(Math.Min(_red, _green), _blue);
        var maximum = Math.Max(Math.Max(_red, _green), _blue);
        var delta = maximum - minimum;

        h = 0f;
        s = 0f;
        v = maximum;
        if (Math.Abs(delta) > Epsilon)
        {
            s = delta / maximum;
            ResolveHue(_red, _green, _blue, maximum, delta, out h);
        }

        h *= 360f;
        s *= 100f;
        v *= 100f;
    }

    private static void ResolveHue(float red, float green, float blue, float maximum, float delta, out float hue)
    {
        var deltaRed = (((maximum - red) / 6f) + delta / 2f) / delta;
        var deltaGreen = (((maximum - green) / 6f) + delta / 2f) / delta;
        var deltaBlue = (((maximum - blue) / 6f) + delta / 2f) / delta;
        if (Math.Abs(red - maximum) < Epsilon)
        {
            hue = deltaBlue - deltaGreen;
        }
        else if (Math.Abs(green - maximum) < Epsilon)
        {
            hue = 1f / 3f + deltaRed - deltaBlue;
        }
        else
        {
            hue = 2f / 3f + deltaGreen - deltaRed;
        }

        if (hue < 0f)
        {
            hue += 1f;
        }

        if (hue > 1f)
        {
            hue -= 1f;
        }
    }

    public bool Equals(SKColorF obj) =>
        _red == obj._red && _green == obj._green &&
        _blue == obj._blue && _alpha == obj._alpha;

    public override bool Equals(object? obj) => obj is SKColorF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_red, _green, _blue, _alpha);

    public override string ToString() => ((SKColor)this).ToString();

    public static bool operator ==(SKColorF left, SKColorF right) => left.Equals(right);

    public static bool operator !=(SKColorF left, SKColorF right) => !left.Equals(right);

    public static implicit operator SKColorF(SKColor color)
    {
        const float scale = 1f / 255f;
        return new SKColorF(
            color.Red * scale,
            color.Green * scale,
            color.Blue * scale,
            color.Alpha * scale);
    }

    public static explicit operator SKColor(SKColorF color)
    {
        var clamped = color.Clamp();
        return new SKColor(
            ToByte(clamped.Red),
            ToByte(clamped.Green),
            ToByte(clamped.Blue),
            ToByte(clamped.Alpha));
    }

    private static byte ToByte(float value) => (byte)MathF.Round(value * 255f);
}

public struct SKColorSpaceTransferFn : IEquatable<SKColorSpaceTransferFn>
{
    public static readonly SKColorSpaceTransferFn Empty;

    public static SKColorSpaceTransferFn Srgb =>
        new(2.4f, 0.9478673f, 0.0521327f, 0.07739938f, 0.04045f, 0f, 0f);

    public static SKColorSpaceTransferFn TwoDotTwo => new(2.2f, 1f, 0f, 0f, 0f, 0f, 0f);

    public static SKColorSpaceTransferFn Linear => new(1f, 1f, 0f, 0f, 0f, 0f, 0f);

    public static SKColorSpaceTransferFn Rec2020 =>
        new(2.22222f, 0.909672f, 0.0903276f, 0.222222f, 0.0812429f, 0f, 0f);

    public static SKColorSpaceTransferFn Pq => new(-5f, 203f, 0f, 0f, 0f, 0f, 0f);

    public static SKColorSpaceTransferFn Hlg => new(-6f, 203f, 1000f, 1.2f, 0f, 0f, 0f);

    public float G { readonly get; set; }
    public float A { readonly get; set; }
    public float B { readonly get; set; }
    public float C { readonly get; set; }
    public float D { readonly get; set; }
    public float E { readonly get; set; }
    public float F { readonly get; set; }
    public readonly float[] Values => new[] { G, A, B, C, D, E, F };

    public SKColorSpaceTransferFn(float g, float a, float b, float c, float d, float e, float f)
    {
        G = g;
        A = a;
        B = b;
        C = c;
        D = d;
        E = e;
        F = f;
    }

    public SKColorSpaceTransferFn(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != 7)
        {
            throw new ArgumentException(
                "The values must have exactly 7 items, one for each of [G, A, B, C, D, E, F].",
                nameof(values));
        }

        G = values[0];
        A = values[1];
        B = values[2];
        C = values[3];
        D = values[4];
        E = values[5];
        F = values[6];
    }

    public readonly SKColorSpaceTransferFn Invert()
    {
        if (!IsSrgbLike())
        {
            return Empty;
        }

        var threshold = C * D + F;
        var nonlinearThreshold = PowApprox(A * D + B, G) + E;
        if (MathF.Abs(threshold - nonlinearThreshold) > 1f / 512f)
        {
            return Empty;
        }

        var inverse = new SKColorSpaceTransferFn
        {
            D = threshold,
        };
        if (threshold > 0f)
        {
            inverse.C = 1f / C;
            inverse.F = -F / C;
        }

        var scale = PowApprox(A, -G);
        inverse.G = 1f / G;
        inverse.A = scale;
        inverse.B = -scale * E;
        inverse.E = -B / A;
        if (inverse.A < 0f)
        {
            return Empty;
        }

        if (inverse.A * inverse.D + inverse.B < 0f)
        {
            inverse.B = -inverse.A * inverse.D;
        }

        if (!inverse.IsSrgbLike())
        {
            return Empty;
        }

        var one = Transform(1f);
        if (!float.IsFinite(one))
        {
            return Empty;
        }

        var sign = one < 0f ? -1f : 1f;
        one *= sign;
        if (one < inverse.D)
        {
            inverse.F = 1f - sign * inverse.C * one;
        }
        else
        {
            inverse.E = 1f - sign * PowApprox(inverse.A * one + inverse.B, inverse.G);
        }

        return inverse.IsSrgbLike() ? inverse : Empty;
    }

    public readonly float Transform(float value)
    {
        var sign = value < 0f ? -1f : 1f;
        var magnitude = value * sign;
        if (G == Pq.G)
        {
            const float c1 = 107f / 128f;
            const float c2 = 2413f / 128f;
            const float c3 = 2392f / 128f;
            const float m1 = 1305f / 8192f;
            const float m2 = 2523f / 32f;
            var power = PowApprox(magnitude, 1f / m2);
            return PowApprox((power - c1) / (c2 - c3 * power), 1f / m1);
        }

        if (G == Hlg.G)
        {
            const float hlgA = 0.17883277f;
            const float hlgB = 0.28466892f;
            const float hlgC = 0.55991073f;
            return sign * (magnitude <= 0.5f
                ? magnitude * magnitude / 3f
                : (ExpApprox((magnitude - hlgC) / hlgA) + hlgB) / 12f);
        }

        if (!IsSrgbLike())
        {
            return 0f;
        }

        return sign * (magnitude < D
            ? C * magnitude + F
            : PowApprox(A * magnitude + B, G) + E);
    }

    private readonly bool IsSrgbLike() =>
        float.IsFinite(G + A + B + C + D + E + F) &&
        A >= 0f && C >= 0f && D >= 0f && G >= 0f && A * D + B >= 0f;

    private static float PowApprox(float value, float power)
    {
        if (value <= 0f)
        {
            return 0f;
        }

        if (value == 1f)
        {
            return 1f;
        }

        return Exp2Approx(Log2Approx(value) * power);
    }

    private static float Log2Approx(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        var exponent = bits * (1f / (1 << 23));
        var mantissa = BitConverter.Int32BitsToSingle((bits & 0x007fffff) | 0x3f000000);
        return exponent - 124.225514990f -
               1.498030302f * mantissa -
               1.725879990f / (0.3520887068f + mantissa);
    }

    private static float Exp2Approx(float value)
    {
        if (value > 128f)
        {
            return float.PositiveInfinity;
        }

        if (value < -127f)
        {
            return 0f;
        }

        var fraction = value - MathF.Floor(value);
        var floatBits = (1 << 23) * (value + 121.274057500f -
            1.490129070f * fraction +
            27.728023300f / (4.84252568f - fraction));
        if (floatBits >= (float)int.MaxValue)
        {
            return float.PositiveInfinity;
        }

        if (floatBits < 0f)
        {
            return 0f;
        }

        return BitConverter.Int32BitsToSingle((int)floatBits);
    }

    private static float ExpApprox(float value) => Exp2Approx(1.4426950408889634f * value);

    public readonly bool Equals(SKColorSpaceTransferFn other) =>
        G == other.G && A == other.A && B == other.B && C == other.C &&
        D == other.D && E == other.E && F == other.F;
    public override readonly bool Equals(object? obj) => obj is SKColorSpaceTransferFn other && Equals(other);
    public static bool operator ==(SKColorSpaceTransferFn left, SKColorSpaceTransferFn right) => left.Equals(right);
    public static bool operator !=(SKColorSpaceTransferFn left, SKColorSpaceTransferFn right) => !left.Equals(right);
    public override readonly int GetHashCode() => HashCode.Combine(G, A, B, C, D, E, F);
}

public struct SKColorSpaceXyz : IEquatable<SKColorSpaceXyz>
{
    private float _m00;
    private float _m01;
    private float _m02;
    private float _m10;
    private float _m11;
    private float _m12;
    private float _m20;
    private float _m21;
    private float _m22;

    public static readonly SKColorSpaceXyz Empty;

    public static readonly SKColorSpaceXyz Identity = new(
        1f, 0f, 0f,
        0f, 1f, 0f,
        0f, 0f, 1f);

    public static SKColorSpaceXyz Srgb => new(
        0.43606567f, 0.3851471f, 0.1430664f,
        0.2224884f, 0.71687317f, 0.06060791f,
        0.013916016f, 0.097076416f, 0.71409607f);

    public static SKColorSpaceXyz AdobeRgb => new(
        0.6097412f, 0.20527649f, 0.14918518f,
        0.31111145f, 0.6256714f, 0.06321716f,
        0.019470215f, 0.06086731f, 0.7445679f);

    public static SKColorSpaceXyz DisplayP3 => new(
        0.515102f, 0.291965f, 0.157153f,
        0.241182f, 0.692236f, 0.0665819f,
        -0.00104941f, 0.0418818f, 0.784378f);

    public static SKColorSpaceXyz Rec2020 => new(
        0.673459f, 0.165661f, 0.1251f,
        0.279033f, 0.675338f, 0.0456288f,
        -0.00193139f, 0.0299794f, 0.797162f);

    public static SKColorSpaceXyz Xyz => Identity;

    public float[] Values
    {
        readonly get =>
        [
            _m00, _m01, _m02,
            _m10, _m11, _m12,
            _m20, _m21, _m22,
        ];
        set
        {
            if (value.Length != 9)
            {
                throw new ArgumentException("The matrix array must have a length of 9.", nameof(value));
            }

            _m00 = value[0];
            _m01 = value[1];
            _m02 = value[2];
            _m10 = value[3];
            _m11 = value[4];
            _m12 = value[5];
            _m20 = value[6];
            _m21 = value[7];
            _m22 = value[8];
        }
    }

    public readonly float this[int x, int y]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(x);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, 3);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, 3);
            return (x + y * 3) switch
            {
                0 => _m00,
                1 => _m01,
                2 => _m02,
                3 => _m10,
                4 => _m11,
                5 => _m12,
                6 => _m20,
                7 => _m21,
                8 => _m22,
                _ => throw new ArgumentOutOfRangeException("index"),
            };
        }
    }

    public SKColorSpaceXyz(float value)
        : this(value, value, value, value, value, value, value, value, value)
    {
    }

    public SKColorSpaceXyz(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != 9)
        {
            throw new ArgumentException("The matrix array must have a length of 9.", nameof(values));
        }

        _m00 = values[0];
        _m01 = values[1];
        _m02 = values[2];
        _m10 = values[3];
        _m11 = values[4];
        _m12 = values[5];
        _m20 = values[6];
        _m21 = values[7];
        _m22 = values[8];
    }

    public SKColorSpaceXyz(
        float m00, float m01, float m02,
        float m10, float m11, float m12,
        float m20, float m21, float m22)
    {
        _m00 = m00;
        _m01 = m01;
        _m02 = m02;
        _m10 = m10;
        _m11 = m11;
        _m12 = m12;
        _m20 = m20;
        _m21 = m21;
        _m22 = m22;
    }

    public readonly SKColorSpaceXyz Invert()
    {
        var a00 = (double)_m00;
        var a01 = (double)_m10;
        var a02 = (double)_m20;
        var a10 = (double)_m01;
        var a11 = (double)_m11;
        var a12 = (double)_m21;
        var a20 = (double)_m02;
        var a21 = (double)_m12;
        var a22 = (double)_m22;
        var b0 = a00 * a11 - a01 * a10;
        var b1 = a00 * a12 - a02 * a10;
        var b2 = a01 * a12 - a02 * a11;
        var b3 = a20;
        var b4 = a21;
        var b5 = a22;
        var determinant = b0 * b5 - b1 * b4 + b2 * b3;
        if (determinant == 0d)
        {
            return Empty;
        }

        var inverseDeterminant = 1d / determinant;
        if (!double.IsFinite(inverseDeterminant) ||
            inverseDeterminant > float.MaxValue || inverseDeterminant < -float.MaxValue)
        {
            return Empty;
        }

        b0 *= inverseDeterminant;
        b1 *= inverseDeterminant;
        b2 *= inverseDeterminant;
        b3 *= inverseDeterminant;
        b4 *= inverseDeterminant;
        b5 *= inverseDeterminant;
        var result = new SKColorSpaceXyz(
            (float)(a11 * b5 - a12 * b4),
            (float)(a12 * b3 - a10 * b5),
            (float)(a10 * b4 - a11 * b3),
            (float)(a02 * b4 - a01 * b5),
            (float)(a00 * b5 - a02 * b3),
            (float)(a01 * b3 - a00 * b4),
            (float)b2,
            (float)-b1,
            (float)b0);
        return result.IsFinite() ? result : Empty;
    }

    private readonly bool IsFinite() =>
        float.IsFinite(_m00) && float.IsFinite(_m01) && float.IsFinite(_m02) &&
        float.IsFinite(_m10) && float.IsFinite(_m11) && float.IsFinite(_m12) &&
        float.IsFinite(_m20) && float.IsFinite(_m21) && float.IsFinite(_m22);

    public static SKColorSpaceXyz Concat(SKColorSpaceXyz left, SKColorSpaceXyz right) => new(
        left._m00 * right._m00 + left._m01 * right._m10 + left._m02 * right._m20,
        left._m00 * right._m01 + left._m01 * right._m11 + left._m02 * right._m21,
        left._m00 * right._m02 + left._m01 * right._m12 + left._m02 * right._m22,
        left._m10 * right._m00 + left._m11 * right._m10 + left._m12 * right._m20,
        left._m10 * right._m01 + left._m11 * right._m11 + left._m12 * right._m21,
        left._m10 * right._m02 + left._m11 * right._m12 + left._m12 * right._m22,
        left._m20 * right._m00 + left._m21 * right._m10 + left._m22 * right._m20,
        left._m20 * right._m01 + left._m21 * right._m11 + left._m22 * right._m21,
        left._m20 * right._m02 + left._m21 * right._m12 + left._m22 * right._m22);

    public readonly bool Equals(SKColorSpaceXyz other) =>
        _m00 == other._m00 && _m01 == other._m01 && _m02 == other._m02 &&
        _m10 == other._m10 && _m11 == other._m11 && _m12 == other._m12 &&
        _m20 == other._m20 && _m21 == other._m21 && _m22 == other._m22;

    public override readonly bool Equals(object? obj) => obj is SKColorSpaceXyz other && Equals(other);

    public static bool operator ==(SKColorSpaceXyz left, SKColorSpaceXyz right) => left.Equals(right);

    public static bool operator !=(SKColorSpaceXyz left, SKColorSpaceXyz right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_m00);
        hash.Add(_m01);
        hash.Add(_m02);
        hash.Add(_m10);
        hash.Add(_m11);
        hash.Add(_m12);
        hash.Add(_m20);
        hash.Add(_m21);
        hash.Add(_m22);

        return hash.ToHashCode();
    }
}

public struct SKMatrix : IEquatable<SKMatrix>
{
    private const int ValueCount = 9;

    public static readonly SKMatrix Empty;
    public static readonly SKMatrix Identity = new()
    {
        ScaleX = 1f,
        ScaleY = 1f,
        Persp2 = 1f,
    };

    public float ScaleX { readonly get; set; }
    public float SkewX { readonly get; set; }
    public float TransX { readonly get; set; }
    public float SkewY { readonly get; set; }
    public float ScaleY { readonly get; set; }
    public float TransY { readonly get; set; }
    public float Persp0 { readonly get; set; }
    public float Persp1 { readonly get; set; }
    public float Persp2 { readonly get; set; }

    public readonly bool IsIdentity => Equals(Identity);

    public readonly bool IsInvertible => TryInvert(out _);

    public float[] Values
    {
        readonly get =>
        [
            ScaleX,
            SkewX,
            TransX,
            SkewY,
            ScaleY,
            TransY,
            Persp0,
            Persp1,
            Persp2,
        ];
        set
        {
            ValidateValues(value, nameof(Values));
            SetValues(value);
        }
    }

    public SKMatrix(float[] values)
    {
        ValidateValues(values, nameof(values));
        this = default;
        SetValues(values);
    }

    public SKMatrix(
        float scaleX,
        float skewX,
        float transX,
        float skewY,
        float scaleY,
        float transY,
        float persp0,
        float persp1,
        float persp2)
    {
        ScaleX = scaleX;
        SkewX = skewX;
        TransX = transX;
        SkewY = skewY;
        ScaleY = scaleY;
        TransY = transY;
        Persp0 = persp0;
        Persp1 = persp1;
        Persp2 = persp2;
    }

    public readonly void GetValues(float[] values)
    {
        ValidateValues(values, nameof(values));
        values[0] = ScaleX;
        values[1] = SkewX;
        values[2] = TransX;
        values[3] = SkewY;
        values[4] = ScaleY;
        values[5] = TransY;
        values[6] = Persp0;
        values[7] = Persp1;
        values[8] = Persp2;
    }

    public readonly Matrix4x4 ToMatrix4x4() => new(
        ScaleX, SkewY, 0f, Persp0,
        SkewX, ScaleY, 0f, Persp1,
        0f, 0f, 1f, 0f,
        TransX, TransY, 0f, Persp2);

    public static SKMatrix CreateIdentity() => Identity;

    public static SKMatrix CreateTranslation(float x, float y)
    {
        if (x == 0f && y == 0f)
        {
            return Identity;
        }

        return new SKMatrix(1f, 0f, x, 0f, 1f, y, 0f, 0f, 1f);
    }

    public static SKMatrix CreateScale(float x, float y)
    {
        if (x == 1f && y == 1f)
        {
            return Identity;
        }

        return new SKMatrix(x, 0f, 0f, 0f, y, 0f, 0f, 0f, 1f);
    }

    public static SKMatrix CreateScale(float x, float y, float pivotX, float pivotY)
    {
        if (x == 1f && y == 1f)
        {
            return Identity;
        }

        return new SKMatrix(
            x,
            0f,
            pivotX - x * pivotX,
            0f,
            y,
            pivotY - y * pivotY,
            0f,
            0f,
            1f);
    }

    public static SKMatrix CreateRotation(float radians) =>
        CreateRotation(radians, 0f, 0f);

    public static SKMatrix CreateRotation(float radians, float pivotX, float pivotY)
    {
        if (radians == 0f)
        {
            return Identity;
        }

        var sin = (float)Math.Sin(radians);
        var cos = (float)Math.Cos(radians);
        var oneMinusCos = 1f - cos;
        return new SKMatrix(
            cos,
            -sin,
            sin * pivotY + oneMinusCos * pivotX,
            sin,
            cos,
            -sin * pivotX + oneMinusCos * pivotY,
            0f,
            0f,
            1f);
    }

    public static SKMatrix CreateRotationDegrees(float degrees) =>
        CreateRotation(degrees * (MathF.PI / 180f));

    public static SKMatrix CreateRotationDegrees(float degrees, float pivotX, float pivotY) =>
        CreateRotation(degrees * (MathF.PI / 180f), pivotX, pivotY);

    public static SKMatrix CreateSkew(float x, float y)
    {
        if (x == 0f && y == 0f)
        {
            return Identity;
        }

        return new SKMatrix(1f, x, 0f, y, 1f, 0f, 0f, 0f, 1f);
    }

    public static SKMatrix CreateScaleTranslation(float sx, float sy, float tx, float ty)
    {
        if ((sx == 0f && sy == 0f && tx == 0f && ty == 0f) ||
            (sx == 1f && sy == 1f && tx == 0f && ty == 0f))
        {
            return Identity;
        }

        return new SKMatrix(sx, 0f, tx, 0f, sy, ty, 0f, 0f, 1f);
    }

    public readonly bool TryInvert(out SKMatrix inverse)
    {
        var a = (double)ScaleX;
        var b = (double)SkewX;
        var c = (double)TransX;
        var d = (double)SkewY;
        var e = (double)ScaleY;
        var f = (double)TransY;
        var g = (double)Persp0;
        var h = (double)Persp1;
        var i = (double)Persp2;
        var determinant = a * (e * i - f * h) -
            b * (d * i - f * g) +
            c * (d * h - e * g);
        if (determinant == 0d || !double.IsFinite(determinant))
        {
            inverse = Empty;
            return false;
        }

        var reciprocal = 1d / determinant;
        inverse = new SKMatrix(
            (float)((e * i - f * h) * reciprocal),
            (float)((c * h - b * i) * reciprocal),
            (float)((b * f - c * e) * reciprocal),
            (float)((f * g - d * i) * reciprocal),
            (float)((a * i - c * g) * reciprocal),
            (float)((c * d - a * f) * reciprocal),
            (float)((d * h - e * g) * reciprocal),
            (float)((b * g - a * h) * reciprocal),
            (float)((a * e - b * d) * reciprocal));
        if (!IsFinite(inverse))
        {
            inverse = Empty;
            return false;
        }

        return true;
    }

    public readonly SKMatrix Invert() => TryInvert(out var inverse) ? inverse : Empty;

    public readonly SKMatrix PreConcat(SKMatrix matrix) =>
        FromMatrix4x4(matrix.ToMatrix4x4() * ToMatrix4x4());

    public readonly SKMatrix PostConcat(SKMatrix matrix) =>
        FromMatrix4x4(ToMatrix4x4() * matrix.ToMatrix4x4());

    public static SKMatrix Concat(SKMatrix first, SKMatrix second) =>
        FromMatrix4x4(second.ToMatrix4x4() * first.ToMatrix4x4());

    public static void Concat(ref SKMatrix target, SKMatrix first, SKMatrix second) =>
        target = Concat(first, second);

    public readonly SKRect MapRect(SKRect source)
    {
        Span<SKPoint> points =
        [
            new SKPoint(source.Left, source.Top),
            new SKPoint(source.Right, source.Top),
            new SKPoint(source.Right, source.Bottom),
            new SKPoint(source.Left, source.Bottom),
        ];
        MapPoints(points, points);
        var left = points[0].X;
        var top = points[0].Y;
        var right = left;
        var bottom = top;
        for (var index = 1; index < points.Length; index++)
        {
            left = MathF.Min(left, points[index].X);
            top = MathF.Min(top, points[index].Y);
            right = MathF.Max(right, points[index].X);
            bottom = MathF.Max(bottom, points[index].Y);
        }

        return new SKRect(left, top, right, bottom);
    }

    public readonly SKPoint MapPoint(SKPoint point) => MapPoint(point.X, point.Y);

    public readonly SKPoint MapPoint(float x, float y)
    {
        var denominator = Persp0 * x + Persp1 * y + Persp2;
        if (denominator == 0f)
        {
            return SKPoint.Empty;
        }

        var mappedX = ScaleX * x + SkewX * y + TransX;
        var mappedY = SkewY * x + ScaleY * y + TransY;
        if (denominator != 1f)
        {
            var reciprocal = 1f / denominator;
            mappedX *= reciprocal;
            mappedY *= reciprocal;
        }

        return new SKPoint(mappedX, mappedY);
    }

    public readonly void MapPoints(Span<SKPoint> result, ReadOnlySpan<SKPoint> points)
    {
        if (result.Length != points.Length)
        {
            throw new ArgumentException("Buffers must be the same size.");
        }

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = MapPoint(points[index]);
        }
    }

    public readonly void MapPoints(SKPoint[] result, SKPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(points);
        MapPoints(result.AsSpan(), points.AsSpan());
    }

    public readonly SKPoint[] MapPoints(SKPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var result = new SKPoint[points.Length];
        MapPoints(result, points);
        return result;
    }

    public readonly SKPoint MapVector(SKPoint vector) => MapVector(vector.X, vector.Y);

    public readonly SKPoint MapVector(float x, float y)
    {
        var origin = MapPoint(0f, 0f);
        var point = MapPoint(x, y);
        return new SKPoint(point.X - origin.X, point.Y - origin.Y);
    }

    public readonly void MapVectors(Span<SKPoint> result, ReadOnlySpan<SKPoint> vectors)
    {
        if (result.Length != vectors.Length)
        {
            throw new ArgumentException("Buffers must be the same size.");
        }

        var origin = MapPoint(0f, 0f);
        for (var index = 0; index < result.Length; index++)
        {
            var point = MapPoint(vectors[index]);
            result[index] = new SKPoint(point.X - origin.X, point.Y - origin.Y);
        }
    }

    public readonly void MapVectors(SKPoint[] result, SKPoint[] vectors)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(vectors);
        MapVectors(result.AsSpan(), vectors.AsSpan());
    }

    public readonly SKPoint[] MapVectors(SKPoint[] vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var result = new SKPoint[vectors.Length];
        MapVectors(result, vectors);
        return result;
    }

    public readonly float MapRadius(float radius)
    {
        var x = MapVector(radius, 0f);
        var y = MapVector(0f, radius);
        var xLength = MathF.Sqrt(x.X * x.X + x.Y * x.Y);
        var yLength = MathF.Sqrt(y.X * y.X + y.Y * y.Y);
        return MathF.Sqrt(xLength * yLength);
    }

    public readonly bool Equals(SKMatrix obj) =>
        ScaleX == obj.ScaleX &&
        SkewX == obj.SkewX &&
        TransX == obj.TransX &&
        SkewY == obj.SkewY &&
        ScaleY == obj.ScaleY &&
        TransY == obj.TransY &&
        Persp0 == obj.Persp0 &&
        Persp1 == obj.Persp1 &&
        Persp2 == obj.Persp2;

    public override readonly bool Equals(object? obj) => obj is SKMatrix matrix && Equals(matrix);

    public static bool operator ==(SKMatrix left, SKMatrix right) => left.Equals(right);

    public static bool operator !=(SKMatrix left, SKMatrix right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ScaleX);
        hash.Add(SkewX);
        hash.Add(TransX);
        hash.Add(SkewY);
        hash.Add(ScaleY);
        hash.Add(TransY);
        hash.Add(Persp0);
        hash.Add(Persp1);
        hash.Add(Persp2);
        return hash.ToHashCode();
    }

    internal static SKMatrix FromMatrix4x4(Matrix4x4 matrix) => new(
        matrix.M11,
        matrix.M21,
        matrix.M41,
        matrix.M12,
        matrix.M22,
        matrix.M42,
        matrix.M14,
        matrix.M24,
        matrix.M44);

    private static void ValidateValues(float[]? values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Length != ValueCount)
        {
            throw new ArgumentException(
                $"The matrix array must have a length of {ValueCount}.",
                parameterName);
        }
    }

    private static bool IsFinite(SKMatrix matrix) =>
        float.IsFinite(matrix.ScaleX) &&
        float.IsFinite(matrix.SkewX) &&
        float.IsFinite(matrix.TransX) &&
        float.IsFinite(matrix.SkewY) &&
        float.IsFinite(matrix.ScaleY) &&
        float.IsFinite(matrix.TransY) &&
        float.IsFinite(matrix.Persp0) &&
        float.IsFinite(matrix.Persp1) &&
        float.IsFinite(matrix.Persp2);

    private void SetValues(float[] values)
    {
        ScaleX = values[0];
        SkewX = values[1];
        TransX = values[2];
        SkewY = values[3];
        ScaleY = values[4];
        TransY = values[5];
        Persp0 = values[6];
        Persp1 = values[7];
        Persp2 = values[8];
    }
}

public struct SKRotationScaleMatrix : IEquatable<SKRotationScaleMatrix>
{
    public static readonly SKRotationScaleMatrix Empty;
    public static readonly SKRotationScaleMatrix Identity = new(1f, 0f, 0f, 0f);

    public float SCos { readonly get; set; }
    public float SSin { readonly get; set; }
    public float TX { readonly get; set; }
    public float TY { readonly get; set; }

    public SKRotationScaleMatrix(float scos, float ssin, float tx, float ty)
    {
        SCos = scos;
        SSin = ssin;
        TX = tx;
        TY = ty;
    }

    public readonly SKMatrix ToMatrix() => new(
        SCos,
        -SSin,
        TX,
        SSin,
        SCos,
        TY,
        0f,
        0f,
        1f);

    public static SKRotationScaleMatrix CreateDegrees(
        float scale,
        float degrees,
        float tx,
        float ty,
        float anchorX,
        float anchorY) =>
        Create(scale, degrees * ((float)Math.PI / 180f), tx, ty, anchorX, anchorY);

    public static SKRotationScaleMatrix Create(
        float scale,
        float radians,
        float tx,
        float ty,
        float anchorX,
        float anchorY)
    {
        var sin = (float)Math.Sin(radians) * scale;
        var cos = (float)Math.Cos(radians) * scale;
        return new SKRotationScaleMatrix(
            cos,
            sin,
            tx - cos * anchorX + sin * anchorY,
            ty - sin * anchorX - cos * anchorY);
    }

    public static SKRotationScaleMatrix CreateIdentity() => new(1f, 0f, 0f, 0f);

    public static SKRotationScaleMatrix CreateTranslation(float x, float y) => new(1f, 0f, x, y);

    public static SKRotationScaleMatrix CreateScale(float s) => new(s, 0f, 0f, 0f);

    public static SKRotationScaleMatrix CreateRotation(float radians, float anchorX, float anchorY) =>
        Create(1f, radians, 0f, 0f, anchorX, anchorY);

    public static SKRotationScaleMatrix CreateRotationDegrees(float degrees, float anchorX, float anchorY) =>
        CreateDegrees(1f, degrees, 0f, 0f, anchorX, anchorY);

    public readonly bool Equals(SKRotationScaleMatrix obj) =>
        SCos == obj.SCos && SSin == obj.SSin && TX == obj.TX && TY == obj.TY;
    public override readonly bool Equals(object? obj) => obj is SKRotationScaleMatrix other && Equals(other);
    public static bool operator ==(SKRotationScaleMatrix left, SKRotationScaleMatrix right) => left.Equals(right);
    public static bool operator !=(SKRotationScaleMatrix left, SKRotationScaleMatrix right) => !left.Equals(right);
    public override readonly int GetHashCode() => HashCode.Combine(SCos, SSin, TX, TY);
}

public struct SKMatrix44 : IEquatable<SKMatrix44>
{
    public static readonly SKMatrix44 Empty;
    public static readonly SKMatrix44 Identity = Matrix4x4.Identity;

    private float _m00;
    private float _m01;
    private float _m02;
    private float _m03;
    private float _m10;
    private float _m11;
    private float _m12;
    private float _m13;
    private float _m20;
    private float _m21;
    private float _m22;
    private float _m23;
    private float _m30;
    private float _m31;
    private float _m32;
    private float _m33;

    public float M00 { readonly get => _m00; set => _m00 = value; }
    public float M01 { readonly get => _m01; set => _m01 = value; }
    public float M02 { readonly get => _m02; set => _m02 = value; }
    public float M03 { readonly get => _m03; set => _m03 = value; }
    public float M10 { readonly get => _m10; set => _m10 = value; }
    public float M11 { readonly get => _m11; set => _m11 = value; }
    public float M12 { readonly get => _m12; set => _m12 = value; }
    public float M13 { readonly get => _m13; set => _m13 = value; }
    public float M20 { readonly get => _m20; set => _m20 = value; }
    public float M21 { readonly get => _m21; set => _m21 = value; }
    public float M22 { readonly get => _m22; set => _m22 = value; }
    public float M23 { readonly get => _m23; set => _m23 = value; }
    public float M30 { readonly get => _m30; set => _m30 = value; }
    public float M31 { readonly get => _m31; set => _m31 = value; }
    public float M32 { readonly get => _m32; set => _m32 = value; }
    public float M33 { readonly get => _m33; set => _m33 = value; }

    public readonly bool IsInvertible => Matrix4x4.Invert(this, out _);

    public readonly SKMatrix Matrix => new(
        _m00,
        _m10,
        _m30,
        _m01,
        _m11,
        _m31,
        _m03,
        _m13,
        _m33);

    public float this[int row, int column]
    {
        readonly get => row switch
        {
            0 => column switch
            {
                0 => _m00,
                1 => _m01,
                2 => _m02,
                3 => _m03,
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            },
            1 => column switch
            {
                0 => _m10,
                1 => _m11,
                2 => _m12,
                3 => _m13,
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            },
            2 => column switch
            {
                0 => _m20,
                1 => _m21,
                2 => _m22,
                3 => _m23,
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            },
            3 => column switch
            {
                0 => _m30,
                1 => _m31,
                2 => _m32,
                3 => _m33,
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        set
        {
            switch (row)
            {
                case 0:
                    switch (column)
                    {
                        case 0: _m00 = value; break;
                        case 1: _m01 = value; break;
                        case 2: _m02 = value; break;
                        case 3: _m03 = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(column));
                    }
                    break;
                case 1:
                    switch (column)
                    {
                        case 0: _m10 = value; break;
                        case 1: _m11 = value; break;
                        case 2: _m12 = value; break;
                        case 3: _m13 = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(column));
                    }
                    break;
                case 2:
                    switch (column)
                    {
                        case 0: _m20 = value; break;
                        case 1: _m21 = value; break;
                        case 2: _m22 = value; break;
                        case 3: _m23 = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(column));
                    }
                    break;
                case 3:
                    switch (column)
                    {
                        case 0: _m30 = value; break;
                        case 1: _m31 = value; break;
                        case 2: _m32 = value; break;
                        case 3: _m33 = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(column));
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(row));
            }
        }
    }

    public SKMatrix44()
    {
        this = default;
    }

    public SKMatrix44(SKMatrix src)
    {
        this = src;
    }

    public SKMatrix44(SKMatrix44 src)
    {
        this = src;
    }

    public SKMatrix44(
        float m00,
        float m01,
        float m02,
        float m03,
        float m10,
        float m11,
        float m12,
        float m13,
        float m20,
        float m21,
        float m22,
        float m23,
        float m30,
        float m31,
        float m32,
        float m33)
    {
        _m00 = m00;
        _m01 = m01;
        _m02 = m02;
        _m03 = m03;
        _m10 = m10;
        _m11 = m11;
        _m12 = m12;
        _m13 = m13;
        _m20 = m20;
        _m21 = m21;
        _m22 = m22;
        _m23 = m23;
        _m30 = m30;
        _m31 = m31;
        _m32 = m32;
        _m33 = m33;
    }

    public static SKMatrix44 CreateIdentity() => Identity;

    public static SKMatrix44 CreateTranslation(float x, float y, float z) =>
        Matrix4x4.CreateTranslation(x, y, z);

    public static SKMatrix44 CreateScale(float x, float y, float z) =>
        Matrix4x4.CreateScale(x, y, z);

    public static SKMatrix44 CreateScale(
        float x,
        float y,
        float z,
        float pivotX,
        float pivotY,
        float pivotZ) =>
        Matrix4x4.CreateScale(x, y, z, new Vector3(pivotX, pivotY, pivotZ));

    public static SKMatrix44 CreateRotation(float x, float y, float z, float radians) =>
        Matrix4x4.CreateFromAxisAngle(new Vector3(x, y, z), radians);

    public static SKMatrix44 CreateRotationDegrees(float x, float y, float z, float degrees) =>
        CreateRotation(x, y, z, degrees * (MathF.PI / 180f));

    public static SKMatrix44 FromRowMajor(ReadOnlySpan<float> src)
    {
        ValidateMatrixSpan(src.Length, nameof(src), "source");
        return new SKMatrix44(
            src[0], src[1], src[2], src[3],
            src[4], src[5], src[6], src[7],
            src[8], src[9], src[10], src[11],
            src[12], src[13], src[14], src[15]);
    }

    public static SKMatrix44 FromColumnMajor(ReadOnlySpan<float> src)
    {
        ValidateMatrixSpan(src.Length, nameof(src), "source");
        return new SKMatrix44(
            src[0], src[4], src[8], src[12],
            src[1], src[5], src[9], src[13],
            src[2], src[6], src[10], src[14],
            src[3], src[7], src[11], src[15]);
    }

    public readonly float[] ToRowMajor()
    {
        var values = new float[16];
        ToRowMajor(values);
        return values;
    }

    public readonly float[] ToColumnMajor()
    {
        var values = new float[16];
        ToColumnMajor(values);
        return values;
    }

    public readonly void ToRowMajor(Span<float> dst)
    {
        ValidateMatrixSpan(dst.Length, nameof(dst), "destination");
        dst[0] = _m00;
        dst[1] = _m01;
        dst[2] = _m02;
        dst[3] = _m03;
        dst[4] = _m10;
        dst[5] = _m11;
        dst[6] = _m12;
        dst[7] = _m13;
        dst[8] = _m20;
        dst[9] = _m21;
        dst[10] = _m22;
        dst[11] = _m23;
        dst[12] = _m30;
        dst[13] = _m31;
        dst[14] = _m32;
        dst[15] = _m33;
    }

    public readonly void ToColumnMajor(Span<float> dst)
    {
        ValidateMatrixSpan(dst.Length, nameof(dst), "destination");
        dst[0] = _m00;
        dst[1] = _m10;
        dst[2] = _m20;
        dst[3] = _m30;
        dst[4] = _m01;
        dst[5] = _m11;
        dst[6] = _m21;
        dst[7] = _m31;
        dst[8] = _m02;
        dst[9] = _m12;
        dst[10] = _m22;
        dst[11] = _m32;
        dst[12] = _m03;
        dst[13] = _m13;
        dst[14] = _m23;
        dst[15] = _m33;
    }

    public readonly bool TryInvert(out SKMatrix44 inverse)
    {
        if (Matrix4x4.Invert(this, out var result))
        {
            inverse = result;
            return true;
        }

        inverse = Empty;
        return false;
    }

    public readonly SKMatrix44 Invert() =>
        Matrix4x4.Invert(this, out var inverse) ? inverse : Empty;

    public readonly SKMatrix44 Transpose() => Matrix4x4.Transpose(this);

    public readonly float Determinant() => ((Matrix4x4)this).GetDeterminant();

    public readonly SKPoint MapPoint(SKPoint point)
    {
        var mapped = Vector2.Transform(new Vector2(point.X, point.Y), this);
        return new SKPoint(mapped.X, mapped.Y);
    }

    public readonly SKPoint3 MapPoint(SKPoint3 point)
    {
        var mapped = Vector3.Transform(new Vector3(point.X, point.Y, point.Z), this);
        return new SKPoint3(mapped.X, mapped.Y, mapped.Z);
    }

    public readonly SKPoint MapPoint(float x, float y) => MapPoint(new SKPoint(x, y));

    public readonly SKPoint3 MapPoint(float x, float y, float z) => MapPoint(new SKPoint3(x, y, z));

    public static SKMatrix44 Concat(SKMatrix44 first, SKMatrix44 second) => first * second;

    public readonly SKMatrix44 PreConcat(SKMatrix44 matrix) => this * matrix;

    public readonly SKMatrix44 PostConcat(SKMatrix44 matrix) => matrix * this;

    public static void Concat(ref SKMatrix44 target, SKMatrix44 first, SKMatrix44 second) =>
        target = first * second;

    public static SKMatrix44 Negate(SKMatrix44 value) => -value;

    public static SKMatrix44 Add(SKMatrix44 value1, SKMatrix44 value2) => value1 + value2;

    public static SKMatrix44 Subtract(SKMatrix44 value1, SKMatrix44 value2) => value1 - value2;

    public static SKMatrix44 Multiply(SKMatrix44 value1, SKMatrix44 value2) => value1 * value2;

    public static SKMatrix44 Multiply(SKMatrix44 value1, float value2) => value1 * value2;

    public static SKMatrix44 operator -(SKMatrix44 value) => -(Matrix4x4)value;

    public static SKMatrix44 operator +(SKMatrix44 value1, SKMatrix44 value2) =>
        (Matrix4x4)value1 + (Matrix4x4)value2;

    public static SKMatrix44 operator -(SKMatrix44 value1, SKMatrix44 value2) =>
        (Matrix4x4)value1 - (Matrix4x4)value2;

    public static SKMatrix44 operator *(SKMatrix44 value1, SKMatrix44 value2) =>
        (Matrix4x4)value1 * (Matrix4x4)value2;

    public static SKMatrix44 operator *(SKMatrix44 value1, float value2) =>
        (Matrix4x4)value1 * value2;

    public static implicit operator SKMatrix44(SKMatrix matrix) => new(
        matrix.ScaleX,
        matrix.SkewY,
        0f,
        matrix.Persp0,
        matrix.SkewX,
        matrix.ScaleY,
        0f,
        matrix.Persp1,
        0f,
        0f,
        1f,
        0f,
        matrix.TransX,
        matrix.TransY,
        0f,
        matrix.Persp2);

    public static implicit operator Matrix4x4(SKMatrix44 matrix) => matrix.ToMatrix4x4();

    public static implicit operator SKMatrix44(Matrix4x4 matrix) => new(
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44);

    public readonly Matrix4x4 ToMatrix4x4() => new(
        _m00, _m01, _m02, _m03,
        _m10, _m11, _m12, _m13,
        _m20, _m21, _m22, _m23,
        _m30, _m31, _m32, _m33);

    internal static SKMatrix44 FromMatrix4x4(Matrix4x4 matrix) => matrix;

    public readonly bool Equals(SKMatrix44 obj) =>
        _m00 == obj._m00 && _m01 == obj._m01 && _m02 == obj._m02 && _m03 == obj._m03 &&
        _m10 == obj._m10 && _m11 == obj._m11 && _m12 == obj._m12 && _m13 == obj._m13 &&
        _m20 == obj._m20 && _m21 == obj._m21 && _m22 == obj._m22 && _m23 == obj._m23 &&
        _m30 == obj._m30 && _m31 == obj._m31 && _m32 == obj._m32 && _m33 == obj._m33;

    public override readonly bool Equals(object? obj) => obj is SKMatrix44 other && Equals(other);

    public static bool operator ==(SKMatrix44 left, SKMatrix44 right) => left.Equals(right);

    public static bool operator !=(SKMatrix44 left, SKMatrix44 right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_m00);
        hash.Add(_m01);
        hash.Add(_m02);
        hash.Add(_m03);
        hash.Add(_m10);
        hash.Add(_m11);
        hash.Add(_m12);
        hash.Add(_m13);
        hash.Add(_m20);
        hash.Add(_m21);
        hash.Add(_m22);
        hash.Add(_m23);
        hash.Add(_m30);
        hash.Add(_m31);
        hash.Add(_m32);
        hash.Add(_m33);
        return hash.ToHashCode();
    }

    private static void ValidateMatrixSpan(int length, string parameterName, string direction)
    {
        if (length != 16)
        {
            throw new ArgumentException($"The {direction} array must be 16 entries.", parameterName);
        }
    }
}

public readonly struct SKCubicResampler : IEquatable<SKCubicResampler>
{
    private readonly float _b;
    private readonly float _c;

    public static readonly SKCubicResampler Mitchell = new(1f / 3f, 1f / 3f);

    public static readonly SKCubicResampler CatmullRom = new(0f, 0.5f);

    public float B => _b;

    public float C => _c;

    public SKCubicResampler(float b, float c)
    {
        _b = b;
        _c = c;
    }

    public bool Equals(SKCubicResampler obj) => _b == obj._b && _c == obj._c;

    public override bool Equals(object? obj) => obj is SKCubicResampler other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_b, _c);

    public static bool operator ==(SKCubicResampler left, SKCubicResampler right) => left.Equals(right);

    public static bool operator !=(SKCubicResampler left, SKCubicResampler right) => !left.Equals(right);
}

public readonly struct SKSamplingOptions : IEquatable<SKSamplingOptions>
{
    public static readonly SKSamplingOptions Default;

    private readonly int _maxAniso;
    private readonly byte _useCubic;
    private readonly SKCubicResampler _cubic;
    private readonly SKFilterMode _filter;
    private readonly SKMipmapMode _mipmap;

    public int MaxAniso => _maxAniso;

    public bool UseCubic => _useCubic > 0;

    public SKCubicResampler Cubic => _cubic;

    public SKFilterMode Filter => _filter;

    public SKMipmapMode Mipmap => _mipmap;

    public bool IsAniso => MaxAniso != 0;

    internal SKFilterMode FilterMode => _filter;

    internal SKMipmapMode MipmapMode => _mipmap;

    internal SKCubicResampler CubicResampler => _cubic;

    public SKSamplingOptions(SKFilterMode filter, SKMipmapMode mipmap)
    {
        _maxAniso = default;
        _useCubic = default;
        _cubic = default;
        _filter = filter;
        _mipmap = mipmap;
    }

    public SKSamplingOptions(SKFilterMode filter)
        : this(filter, SKMipmapMode.None)
    {
    }

    public SKSamplingOptions(SKCubicResampler resampler)
    {
        _maxAniso = default;
        _useCubic = 1;
        _cubic = resampler;
        _filter = default;
        _mipmap = default;
    }

    public SKSamplingOptions(int maxAniso)
    {
        _maxAniso = Math.Max(1, maxAniso);
        _useCubic = default;
        _cubic = default;
        _filter = default;
        _mipmap = default;
    }

    public bool Equals(SKSamplingOptions obj) =>
        _maxAniso == obj._maxAniso && _useCubic == obj._useCubic &&
        _cubic == obj._cubic && _filter == obj._filter && _mipmap == obj._mipmap;

    public override bool Equals(object? obj) => obj is SKSamplingOptions other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_maxAniso, _useCubic, _cubic, _filter, _mipmap);

    public static bool operator ==(SKSamplingOptions left, SKSamplingOptions right) => left.Equals(right);

    public static bool operator !=(SKSamplingOptions left, SKSamplingOptions right) => !left.Equals(right);
}

public class SKColorSpace : IDisposable
{
    private SKColorSpace(SKColorSpaceTransferFn transferFunction, SKColorSpaceXyz xyz)
    {
        TransferFunction = transferFunction;
        Xyz = xyz;
    }

    public IntPtr Handle { get; } = SKObjectHandle.Create();
    public SKColorSpaceTransferFn TransferFunction { get; }
    public SKColorSpaceXyz Xyz { get; }
    public bool IsLinear => TransferFunction == SKColorSpaceTransferFn.Linear;

    public static SKColorSpace CreateSrgb() => CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.Srgb);
    public static SKColorSpace CreateSrgbLinear() => CreateRgb(SKColorSpaceTransferFn.Linear, SKColorSpaceXyz.Srgb);
    public static SKColorSpace CreateRgb(SKColorSpaceTransferFn transferFunction, SKColorSpaceXyz xyz) =>
        new(transferFunction, xyz);
    public void Dispose() { }
}

public struct SKImageInfo : IEquatable<SKImageInfo>
{
    public static readonly SKImageInfo Empty;
    public static readonly SKColorType PlatformColorType = SKColorType.Rgba8888;
    public static readonly int PlatformColorAlphaShift = 24;
    public static readonly int PlatformColorRedShift = 0;
    public static readonly int PlatformColorGreenShift = 8;
    public static readonly int PlatformColorBlueShift = 16;

    public int Width { readonly get; set; }
    public int Height { readonly get; set; }
    public SKColorType ColorType { readonly get; set; }
    public SKAlphaType AlphaType { readonly get; set; }
    public SKColorSpace? ColorSpace { readonly get; set; }

    public readonly int BytesPerPixel => GetBytesPerPixel(ColorType);

    internal static int GetBytesPerPixel(SKColorType colorType) => colorType switch
    {
        SKColorType.Unknown => 0,
        SKColorType.Alpha8 or
            SKColorType.Gray8 or
            SKColorType.R8Unorm => 1,
        SKColorType.Rgb565 or
            SKColorType.Argb4444 or
            SKColorType.Rg88 or
            SKColorType.AlphaF16 or
            SKColorType.Alpha16 or
            SKColorType.R16Unorm or
            SKColorType.RF16 => 2,
        SKColorType.Rgba8888 or
            SKColorType.Rgb888x or
            SKColorType.Bgra8888 or
            SKColorType.Rgba1010102 or
            SKColorType.Rgb101010x or
            SKColorType.RgF16 or
            SKColorType.Rg1616 or
            SKColorType.Bgra1010102 or
            SKColorType.Bgr101010x or
            SKColorType.Bgr101010xXR or
            SKColorType.Srgba8888 => 4,
        SKColorType.RgbaF16 or
            SKColorType.RgbaF16Clamped or
            SKColorType.Rgba16161616 or
            SKColorType.Rgba10x6 or
            SKColorType.Bgra10101010XR or
            SKColorType.RgbF16F16F16x => 8,
        SKColorType.RgbaF32 => 16,
        _ => 0,
    };
    public readonly int BitShiftPerPixel => BytesPerPixel switch
    {
        1 => 0,
        2 => 1,
        4 => 2,
        8 => 3,
        16 => 4,
        _ => 0,
    };
    public readonly int BitsPerPixel => BytesPerPixel * 8;
    public readonly int BytesSize => checked(Width * Height * BytesPerPixel);
    public readonly long BytesSize64 => (long)Width * Height * BytesPerPixel;
    public readonly int RowBytes => checked(Width * BytesPerPixel);
    public readonly long RowBytes64 => (long)Width * BytesPerPixel;
    public readonly bool IsEmpty => Width <= 0 || Height <= 0;
    public readonly bool IsOpaque => AlphaType == SKAlphaType.Opaque;
    public readonly SKSizeI Size => new(Width, Height);
    public readonly SKRectI Rect => SKRectI.Create(Width, Height);

    public SKImageInfo(int width, int height)
        : this(width, height, PlatformColorType, SKAlphaType.Premul, null)
    {
    }

    public SKImageInfo(int width, int height, SKColorType colorType)
        : this(width, height, colorType, SKAlphaType.Premul, null)
    {
    }

    public SKImageInfo(int width, int height, SKColorType colorType, SKAlphaType alphaType)
        : this(width, height, colorType, alphaType, null)
    {
    }

    public SKImageInfo(
        int width,
        int height,
        SKColorType colorType,
        SKAlphaType alphaType,
        SKColorSpace? colorspace)
    {
        Width = width;
        Height = height;
        ColorType = colorType;
        AlphaType = alphaType;
        ColorSpace = colorspace;
    }

    public readonly SKImageInfo WithSize(SKSizeI size) => WithSize(size.Width, size.Height);

    public readonly SKImageInfo WithSize(int width, int height)
    {
        var result = this;
        result.Width = width;
        result.Height = height;
        return result;
    }

    public readonly SKImageInfo WithColorType(SKColorType newColorType)
    {
        var result = this;
        result.ColorType = newColorType;
        return result;
    }

    public readonly SKImageInfo WithColorSpace(SKColorSpace? newColorSpace)
    {
        var result = this;
        result.ColorSpace = newColorSpace;
        return result;
    }

    public readonly SKImageInfo WithAlphaType(SKAlphaType newAlphaType)
    {
        var result = this;
        result.AlphaType = newAlphaType;
        return result;
    }

    public readonly bool Equals(SKImageInfo obj) =>
        ReferenceEquals(ColorSpace, obj.ColorSpace) &&
        Width == obj.Width &&
        Height == obj.Height &&
        ColorType == obj.ColorType &&
        AlphaType == obj.AlphaType;

    public override readonly bool Equals(object? obj) => obj is SKImageInfo other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(ColorSpace, Width, Height, ColorType, AlphaType);

    public static bool operator ==(SKImageInfo left, SKImageInfo right) => left.Equals(right);
    public static bool operator !=(SKImageInfo left, SKImageInfo right) => !left.Equals(right);
}

public abstract class SKStream : SKObject
{
    internal SKStream()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    internal SKStream(IntPtr handle, bool owns)
        : base(handle, owns)
    {
    }

    protected virtual Stream? BackingStream => null;
    protected virtual ReadOnlyMemory<byte>? BackingMemory => null;

    public bool IsAtEnd
    {
        get
        {
            ThrowIfDisposed();
            return this is SKAbstractManagedStream managed
                ? managed.OnIsAtEnd()
                : BackingStream is not { } stream ||
                  (stream.CanSeek && stream.Position >= stream.Length);
        }
    }

    public bool HasPosition
    {
        get
        {
            ThrowIfDisposed();
            return this is SKAbstractManagedStream managed
                ? managed.OnHasPosition()
                : BackingStream?.CanSeek == true;
        }
    }

    public int Position
    {
        get
        {
            ThrowIfDisposed();
            return this is SKAbstractManagedStream managed
                ? checked((int)managed.OnGetPosition())
                : BackingStream is { CanSeek: true } stream
                    ? checked((int)stream.Position)
                    : 0;
        }
        set => Seek(value);
    }

    public bool HasLength
    {
        get
        {
            ThrowIfDisposed();
            return this is SKAbstractManagedStream managed
                ? managed.OnHasLength()
                : BackingStream?.CanSeek == true;
        }
    }

    public int Length
    {
        get
        {
            ThrowIfDisposed();
            return this is SKAbstractManagedStream managed
                ? checked((int)managed.OnGetLength())
                : BackingStream is { CanSeek: true } stream
                    ? checked((int)stream.Length)
                    : 0;
        }
    }

    public sbyte ReadSByte() => ReadSByte(out var value) ? value : (sbyte)0;
    public short ReadInt16() => ReadInt16(out var value) ? value : (short)0;
    public int ReadInt32() => ReadInt32(out var value) ? value : 0;
    public byte ReadByte() => ReadByte(out var value) ? value : (byte)0;
    public ushort ReadUInt16() => ReadUInt16(out var value) ? value : (ushort)0;
    public uint ReadUInt32() => ReadUInt32(out var value) ? value : 0u;
    public bool ReadBool() => ReadBool(out var value) && value;

    public bool ReadSByte(out sbyte value)
    {
        var success = ReadByte(out var raw);
        value = unchecked((sbyte)raw);
        return success;
    }

    public bool ReadInt16(out short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        var success = ReadExactly(bytes);
        value = success ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : (short)0;
        return success;
    }

    public bool ReadInt32(out int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        var success = ReadExactly(bytes);
        value = success ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : 0;
        return success;
    }

    public bool ReadByte(out byte value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(byte)];
        var success = ReadExactly(bytes);
        value = success ? bytes[0] : (byte)0;
        return success;
    }

    public bool ReadUInt16(out ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        var success = ReadExactly(bytes);
        value = success ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : (ushort)0;
        return success;
    }

    public bool ReadUInt32(out uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        var success = ReadExactly(bytes);
        value = success ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0u;
        return success;
    }

    public bool ReadBool(out bool value)
    {
        var success = ReadByte(out var raw);
        value = raw != 0;
        return success;
    }

    public int Read(byte[] buffer, int size)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        return size > 0
            ? ReadCore(buffer.AsSpan(0, Math.Min(size, buffer.Length)))
            : 0;
    }

    public unsafe int Read(IntPtr buffer, int size)
    {
        ThrowIfDisposed();
        if (size <= 0)
        {
            return 0;
        }

        if (this is SKAbstractManagedStream managed)
        {
            return checked((int)managed.OnRead(buffer, (IntPtr)size));
        }

        return buffer != IntPtr.Zero && BackingStream is { } stream
            ? stream.Read(new Span<byte>(buffer.ToPointer(), size))
            : 0;
    }

    public int Peek(IntPtr buffer, int size)
    {
        ThrowIfDisposed();
        if (this is SKAbstractManagedStream managed)
        {
            return checked((int)managed.OnPeek(buffer, (IntPtr)size));
        }

        if (!HasPosition)
        {
            return 0;
        }

        var position = Position;
        var read = Read(buffer, size);
        Seek(position);
        return read;
    }

    public int Skip(int size)
    {
        ThrowIfDisposed();
        if (size <= 0)
        {
            return 0;
        }

        if (this is SKAbstractManagedStream managed)
        {
            return checked((int)managed.OnRead(IntPtr.Zero, (IntPtr)size));
        }

        if (BackingStream is not { } stream)
        {
            return 0;
        }

        if (stream.CanSeek)
        {
            var start = stream.Position;
            stream.Position = Math.Min(stream.Length, start + size);
            return checked((int)(stream.Position - start));
        }

        Span<byte> scratch = stackalloc byte[256];
        var skipped = 0;
        while (skipped < size)
        {
            var read = stream.Read(scratch[..Math.Min(scratch.Length, size - skipped)]);
            if (read == 0)
            {
                break;
            }

            skipped += read;
        }

        return skipped;
    }

    public bool Rewind()
    {
        ThrowIfDisposed();
        return this is SKAbstractManagedStream managed
            ? managed.OnRewind()
            : Seek(0);
    }

    public bool Seek(int position)
    {
        ThrowIfDisposed();
        if (this is SKAbstractManagedStream managed)
        {
            return managed.OnSeek((IntPtr)position);
        }

        if (position < 0 || BackingStream is not { CanSeek: true } stream || position > stream.Length)
        {
            return false;
        }

        stream.Position = position;
        return true;
    }

    [Obsolete("The native stream move offset is capped at a 32-bit int. Use Move(int) instead.")]
    public bool Move(long offset) => Move(checked((int)offset));

    public bool Move(int offset)
    {
        ThrowIfDisposed();
        if (this is SKAbstractManagedStream managed)
        {
            return managed.OnMove(offset);
        }

        var target = (long)Position + offset;
        return target >= 0 && target <= int.MaxValue && Seek((int)target);
    }

    public IntPtr GetMemoryBase()
    {
        ThrowIfDisposed();
        return this is SKStreamAsset asset
            ? asset.GetMemoryBaseCore()
            : IntPtr.Zero;
    }

    public SKData GetData()
    {
        ThrowIfDisposed();
        if (this is SKAbstractManagedStream)
        {
            var managedPosition = HasPosition ? Position : 0;
            if (HasPosition)
            {
                Rewind();
            }

            using var managedCopy = new MemoryStream();
            var buffer = new byte[81920];
            try
            {
                int read;
                while ((read = Read(buffer, buffer.Length)) > 0)
                {
                    managedCopy.Write(buffer, 0, read);
                }
            }
            finally
            {
                if (HasPosition)
                {
                    Seek(managedPosition);
                }
            }

            return new SKData(managedCopy.ToArray());
        }

        if (BackingMemory is { } memory)
        {
            return new SKData(memory.ToArray());
        }

        if (BackingStream is not { } stream)
        {
            return new SKData(Array.Empty<byte>());
        }

        var position = stream.CanSeek ? stream.Position : 0;
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        if (stream.CanSeek)
        {
            stream.Position = position;
        }

        return new SKData(copy.ToArray());
    }

    private bool ReadExactly(Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = ReadCore(destination[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private unsafe int ReadCore(Span<byte> destination)
    {
        ThrowIfDisposed();
        if (this is SKAbstractManagedStream managed)
        {
            fixed (byte* buffer = destination)
            {
                return checked((int)managed.OnRead((IntPtr)buffer, (IntPtr)destination.Length));
            }
        }

        return BackingStream?.Read(destination) ?? 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}

public abstract class SKStreamRewindable : SKStream
{
    internal SKStreamRewindable()
    {
    }
}

public abstract class SKStreamSeekable : SKStreamRewindable
{
    internal SKStreamSeekable()
    {
    }
}

public class SKCodec : SKObject
{
    private readonly byte[] _data;
    private readonly SKEncodedImageDecoder.DecodedImage _decoded;
    private readonly SKEncodedImageFormat _encodedFormat;
    private SKImageInfo _incrementalInfo;
    private IntPtr _incrementalPixels;
    private int _incrementalRowBytes;
    private SKCodecOptions _incrementalOptions;
    private bool _incrementalStarted;
    internal byte[] EncodedBytes => _data;
    internal SKEncodedImageDecoder.DecodedImage DecodedImage => _decoded;

    private SKCodec(byte[] data)
        : base(SKObjectHandle.Create(), owns: true)
    {
        if (!TryDetectEncodedFormat(data, out _encodedFormat) || !IsCpuDecodableFormat(_encodedFormat))
        {
            throw new NotSupportedException("The encoded image format is not supported.");
        }

        _data = data;
        _decoded = SKEncodedImageDecoder.Decode(data);
        Info = new SKImageInfo(
            _decoded.Width,
            _decoded.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            _decoded.ColorSpace);
    }

    public static int MinBufferedBytesNeeded => 32;
    public SKImageInfo Info { get; }
    public SKEncodedOrigin EncodedOrigin => SKEncodedOrigin.TopLeft;
    public SKEncodedImageFormat EncodedFormat => _encodedFormat;
    public byte[] Pixels
    {
        get
        {
            var result = GetPixels(out var pixels);
            if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            {
                throw new Exception(result.ToString());
            }

            return pixels;
        }
    }
    public int RepetitionCount => 0;
    public int FrameCount => 0;
    public SKCodecFrameInfo[] FrameInfo => Array.Empty<SKCodecFrameInfo>();
    public SKCodecScanlineOrder ScanlineOrder => SKCodecScanlineOrder.TopDown;
    public int NextScanline => -1;

    public SKSizeI GetScaledDimensions(float desiredScale)
    {
        if (desiredScale <= 0f)
        {
            return SKSizeI.Empty;
        }

        if (_encodedFormat != SKEncodedImageFormat.Jpeg || float.IsNaN(desiredScale) || desiredScale >= 1f)
        {
            return Info.Size;
        }

        var numerator = Math.Clamp((int)MathF.Floor(desiredScale * 8f + 0.5f), 1, 8);
        return GetJpegScaledDimensions(numerator);
    }

    public bool GetValidSubset(ref SKRectI desiredSubset) => false;

    public bool GetFrameInfo(int index, out SKCodecFrameInfo frameInfo)
    {
        frameInfo = default;
        return false;
    }

    public SKCodecResult GetPixels(out byte[] pixels) => GetPixels(Info, out pixels);

    public SKCodecResult GetPixels(SKImageInfo info, out byte[] pixels)
    {
        pixels = new byte[info.BytesSize];
        return GetPixels(info, pixels);
    }

    public unsafe SKCodecResult GetPixels(SKImageInfo info, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length < info.BytesSize)
        {
            return SKCodecResult.InvalidParameters;
        }

        fixed (byte* pointer = pixels)
        {
            return GetPixels(info, (IntPtr)pointer, info.RowBytes, SKCodecOptions.Default);
        }
    }

    public SKCodecResult GetPixels(SKImageInfo info, IntPtr pixels) =>
        GetPixels(info, pixels, info.RowBytes, SKCodecOptions.Default);

    public SKCodecResult GetPixels(SKImageInfo info, IntPtr pixels, SKCodecOptions options) =>
        GetPixels(info, pixels, info.RowBytes, options);

    public SKCodecResult GetPixels(
        SKImageInfo info,
        IntPtr pixels,
        int rowBytes,
        SKCodecOptions options)
    {
        if (pixels == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        var validation = ValidateDecodeTarget(info, rowBytes, options);
        if (validation != SKCodecResult.Success)
        {
            return validation;
        }

        if (info.ColorType == SKColorType.Rgba8888 &&
            info.AlphaType == SKAlphaType.Unpremul &&
            info.Size.Equals(Info.Size) &&
            ReferenceEquals(info.ColorSpace, Info.ColorSpace))
        {
            CopyRows(_decoded.Pixels, Info.RowBytes, pixels, rowBytes, info.RowBytes, info.Height);
            return SKCodecResult.Success;
        }

        using var bitmap = SKBitmap.Decode(this, info);
        if (bitmap is null)
        {
            return SKCodecResult.InvalidConversion;
        }

        CopyBitmapRows(bitmap, pixels, rowBytes, info.RowBytes, info.Height);
        return SKCodecResult.Success;
    }

    public SKCodecResult StartIncrementalDecode(SKImageInfo info, IntPtr pixels, int rowBytes) =>
        pixels == IntPtr.Zero
            ? SKCodecResult.InvalidParameters
            : StartIncrementalDecode(info, pixels, rowBytes, SKCodecOptions.Default);

    public SKCodecResult StartIncrementalDecode(
        SKImageInfo info,
        IntPtr pixels,
        int rowBytes,
        SKCodecOptions options)
    {
        if (pixels == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        _incrementalStarted = false;
        var validation = ValidateDecodeTarget(info, rowBytes, options);
        if (validation != SKCodecResult.Success)
        {
            return validation;
        }

        _incrementalInfo = info;
        _incrementalPixels = pixels;
        _incrementalRowBytes = rowBytes;
        _incrementalOptions = options;
        _incrementalStarted = true;
        return SKCodecResult.Success;
    }

    public SKCodecResult IncrementalDecode() => IncrementalDecode(out _);

    public SKCodecResult IncrementalDecode(out int rowsDecoded)
    {
        rowsDecoded = 0;
        if (!_incrementalStarted)
        {
            return SKCodecResult.InvalidParameters;
        }

        var result = GetPixels(
            _incrementalInfo,
            _incrementalPixels,
            _incrementalRowBytes,
            _incrementalOptions);
        _incrementalStarted = false;
        return result;
    }

    public SKCodecResult StartScanlineDecode(SKImageInfo info) => SKCodecResult.Unimplemented;

    public SKCodecResult StartScanlineDecode(SKImageInfo info, SKCodecOptions options) =>
        SKCodecResult.Unimplemented;

    public int GetScanlines(IntPtr dst, int countLines, int rowBytes)
    {
        if (dst == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(dst));
        }

        return 0;
    }

    public bool SkipScanlines(int countLines) => false;

    public int GetOutputScanline(int inputScanline) => inputScanline;

    public static SKCodec Create(SKData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return CreateCore(data.Bytes);
    }

    public static SKCodec Create(SKStream stream) => Create(stream, out _);

    public static SKCodec Create(SKStream stream, out SKCodecResult result)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is SKFileStream { IsValid: false })
        {
            throw new ArgumentException("File stream was not valid.", nameof(stream));
        }

        return CreateCore(ReadRemainingBytes(stream), out result);
    }

    public static SKCodec Create(Stream stream) => Create(stream, out _);

    public static SKCodec Create(Stream stream, out SKCodecResult result)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return CreateCore(ms.ToArray(), out result);
    }

    public static SKCodec Create(string filename) => Create(filename, out _);

    public static SKCodec Create(string filename, out SKCodecResult result)
    {
        using var stream = SKFileStream.OpenStream(filename);
        if (stream is null)
        {
            result = SKCodecResult.InternalError;
            return null!;
        }

        return Create(stream, out result);
    }

    protected override void Dispose(bool disposing)
    {
        _incrementalStarted = false;
        _incrementalPixels = IntPtr.Zero;
        base.Dispose(disposing);
    }

    private static SKCodec CreateCore(byte[] data) => CreateCore(data, out _);

    private static unsafe byte[] ReadRemainingBytes(SKStream stream)
    {
        if (stream.HasLength && stream.HasPosition)
        {
            var remaining = Math.Max(0, stream.Length - stream.Position);
            var data = GC.AllocateUninitializedArray<byte>(remaining);
            var read = 0;
            fixed (byte* pointer = data)
            {
                while (read < data.Length)
                {
                    var count = stream.Read((IntPtr)(pointer + read), data.Length - read);
                    if (count == 0)
                    {
                        break;
                    }

                    read += count;
                }
            }

            return read == data.Length ? data : data.AsSpan(0, read).ToArray();
        }

        using var copy = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8192];
        fixed (byte* pointer = buffer)
        {
            while (true)
            {
                var count = stream.Read((IntPtr)pointer, buffer.Length);
                if (count == 0)
                {
                    break;
                }

                copy.Write(buffer[..count]);
            }
        }

        return copy.ToArray();
    }

    private static SKCodec CreateCore(byte[] data, out SKCodecResult result)
    {
        try
        {
            var codec = new SKCodec(data);
            result = SKCodecResult.Success;
            return codec;
        }
        catch (Exception exception) when (IsInvalidEncodedImageException(exception))
        {
            result = TryDetectEncodedFormat(data, out var format) && IsCpuDecodableFormat(format)
                ? SKCodecResult.IncompleteInput
                : SKCodecResult.Unimplemented;
            return null!;
        }
    }

    private SKCodecResult ValidateDecodeTarget(SKImageInfo info, int rowBytes, SKCodecOptions options)
    {
        if (options.HasSubset)
        {
            return SKCodecResult.Unimplemented;
        }

        if (options.FrameIndex != 0 || options.PriorFrame < -1 || info.IsEmpty || rowBytes < info.RowBytes)
        {
            return SKCodecResult.InvalidParameters;
        }

        if (info.BytesPerPixel <= 0)
        {
            return SKCodecResult.InvalidConversion;
        }

        return IsSupportedDecodeSize(info.Size)
            ? SKCodecResult.Success
            : SKCodecResult.InvalidScale;
    }

    private bool IsSupportedDecodeSize(SKSizeI size)
    {
        if (size.Equals(Info.Size))
        {
            return true;
        }

        if (_encodedFormat != SKEncodedImageFormat.Jpeg)
        {
            return false;
        }

        for (var numerator = 1; numerator < 8; numerator++)
        {
            if (GetJpegScaledDimensions(numerator).Equals(size))
            {
                return true;
            }
        }

        return false;
    }

    private SKSizeI GetJpegScaledDimensions(int numerator) => new(
        (int)(((long)Info.Width * numerator + 7) / 8),
        (int)(((long)Info.Height * numerator + 7) / 8));

    private static unsafe void CopyBitmapRows(
        SKBitmap bitmap,
        IntPtr destination,
        int destinationRowBytes,
        int copyRowBytes,
        int height) =>
        CopyRows(
            bitmap.GetPixelSpan(),
            bitmap.RowBytes,
            destination,
            destinationRowBytes,
            copyRowBytes,
            height);

    private static unsafe void CopyRows(
        ReadOnlySpan<byte> source,
        int sourceRowBytes,
        IntPtr destination,
        int destinationRowBytes,
        int copyRowBytes,
        int height)
    {
        fixed (byte* sourcePointer = source)
        {
            var destinationPointer = (byte*)destination;
            for (var row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(
                    sourcePointer + row * sourceRowBytes,
                    destinationPointer + row * destinationRowBytes,
                    destinationRowBytes,
                    copyRowBytes);
            }
        }
    }

    private static bool TryDetectEncodedFormat(ReadOnlySpan<byte> data, out SKEncodedImageFormat format)
    {
        if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4e && data[3] == 0x47)
        {
            format = SKEncodedImageFormat.Png;
            return true;
        }

        if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xd8)
        {
            format = SKEncodedImageFormat.Jpeg;
            return true;
        }

        if (data.Length >= 3 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
        {
            format = SKEncodedImageFormat.Gif;
            return true;
        }

        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            format = SKEncodedImageFormat.Bmp;
            return true;
        }

        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[3] == 0 && data[2] is 1 or 2)
        {
            format = SKEncodedImageFormat.Ico;
            return true;
        }

        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            format = SKEncodedImageFormat.Webp;
            return true;
        }

        if (data.Length >= 4 && data[0] == (byte)'P' && data[1] == (byte)'K' && data[2] == (byte)'M' && data[3] == (byte)' ')
        {
            format = SKEncodedImageFormat.Pkm;
            return true;
        }

        if (data.Length >= 4 && data[0] == 0x13 && data[1] == 0xab && data[2] == 0xa1 && data[3] == 0x5c)
        {
            format = SKEncodedImageFormat.Astc;
            return true;
        }

        if (data.Length >= 12 && data[0] == 0xab && data[1] == 0x4b && data[2] == 0x54 && data[3] == 0x58)
        {
            format = SKEncodedImageFormat.Ktx;
            return true;
        }

        if (data.Length >= 12 &&
            data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
        {
            var brand = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
            if (brand is 0x61766966 or 0x61766973)
            {
                format = SKEncodedImageFormat.Avif;
                return true;
            }

            if (brand is 0x68656963 or 0x68656978 or 0x68657663 or 0x68657678 or 0x6d696631 or 0x6d736631)
            {
                format = SKEncodedImageFormat.Heif;
                return true;
            }
        }

        if (data.Length >= 2 && data[0] == 0xff && data[1] == 0x0a ||
            data.Length >= 12 &&
            data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 0x0c &&
            data[4] == (byte)'J' && data[5] == (byte)'X' && data[6] == (byte)'L' && data[7] == (byte)' ')
        {
            format = SKEncodedImageFormat.Jpegxl;
            return true;
        }

        if (data.Length >= 4 &&
            ((data[0] == (byte)'I' && data[1] == (byte)'I' && data[2] == 0x2a && data[3] == 0) ||
             (data[0] == (byte)'M' && data[1] == (byte)'M' && data[2] == 0 && data[3] == 0x2a)))
        {
            format = SKEncodedImageFormat.Dng;
            return true;
        }

        format = default;
        return false;
    }

    private static bool IsCpuDecodableFormat(SKEncodedImageFormat format) =>
        format is SKEncodedImageFormat.Bmp or
            SKEncodedImageFormat.Gif or
            SKEncodedImageFormat.Ico or
            SKEncodedImageFormat.Jpeg or
            SKEncodedImageFormat.Png;

    private static bool IsInvalidEncodedImageException(Exception exception) =>
        exception is InvalidOperationException or
            ArgumentException or
            FormatException or
            IndexOutOfRangeException or
            NotSupportedException;
}

public class SKSurfaceProperties : SKObject
{
    public SKSurfacePropsFlags Flags { get; }
    public SKPixelGeometry PixelGeometry { get; }
    public bool IsUseDeviceIndependentFonts => Flags.HasFlag(SKSurfacePropsFlags.UseDeviceIndependentFonts);

    public SKSurfaceProperties(SKPixelGeometry pixelGeometry)
        : this(SKSurfacePropsFlags.None, pixelGeometry)
    {
    }

    public SKSurfaceProperties(uint flags, SKPixelGeometry pixelGeometry)
        : this((SKSurfacePropsFlags)flags, pixelGeometry)
    {
    }

    public SKSurfaceProperties(SKSurfacePropsFlags flags, SKPixelGeometry pixelGeometry)
        : base(IntPtr.Zero, owns: true)
    {
        Flags = flags;
        PixelGeometry = pixelGeometry;
    }
}

internal static class SKContextHelper
{
    private static readonly object s_fallbackLock = new();
    private static WgpuContext? _fallbackContext;
    public static WgpuContext GetContext()
    {
        if (WgpuContext.Current is { IsInitialized: true } current)
            return current;

        if (WgpuContext.TryGetFirstActiveContext(out var ctx))
        {
            return ctx;
        }

        lock (s_fallbackLock)
        {
            if (_fallbackContext is not { IsInitialized: true })
            {
                var replacement = new WgpuContext();
                replacement.Initialize(null);
                _fallbackContext = replacement;
            }

            return _fallbackContext;
        }
    }
}
