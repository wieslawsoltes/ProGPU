using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
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

public enum SKPointMode
{
    Points,
    Lines,
    Polygon,
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
    XOR = 3,
    ReverseDifference = 4,
    Replace = 5,
}

public partial struct SKPoint : IEquatable<SKPoint>
{
    private float _x;
    private float _y;

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

    public static readonly SKPoint Empty = new(0, 0);
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
        var lengthSquared = point._x * point._x + point._y * point._y;
        return new SKPointI(
            (int)(point._x - 2f * lengthSquared * normal._x),
            (int)(point._y - 2f * lengthSquared * normal._y));
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
    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_x);
        hash.Add(_y);
        return hash.ToHashCode();
    }
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

public partial struct SKSize : IEquatable<SKSize>
{
    private float _width;
    private float _height;

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

    public static readonly SKSize Empty = new(0f, 0f);

}

public partial struct SKSizeI : IEquatable<SKSizeI>
{
    private int _width;
    private int _height;

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

    public static readonly SKSizeI Empty = new(0, 0);
}

public partial struct SKRect : IEquatable<SKRect>
{
    private float _left;
    private float _top;
    private float _right;
    private float _bottom;

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

    public SKRect(float left, float top, float right, float bottom)
    {
        _left = left;
        _top = top;
        _right = right;
        _bottom = bottom;
    }

    public static readonly SKRect Empty = new(0, 0, 0, 0);
}

public partial struct SKRectI : IEquatable<SKRectI>
{
    private int _left;
    private int _top;
    private int _right;
    private int _bottom;

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

    public SKRectI(int left, int top, int right, int bottom)
    {
        _left = left;
        _top = top;
        _right = right;
        _bottom = bottom;
    }

    public static readonly SKRectI Empty = new(0, 0, 0, 0);

    public static SKRectI Create(int width, int height) => new(0, 0, width, height);
    public static SKRectI Create(int x, int y, int width, int height) =>
        new(x, y, x + width, y + height);
}

public readonly partial struct SKColor : IEquatable<SKColor>
{
    private readonly uint _color;

    public byte Alpha => (byte)((_color >> 24) & 0xff);
    public byte Red => (byte)((_color >> 16) & 0xff);
    public byte Green => (byte)((_color >> 8) & 0xff);
    public byte Blue => (byte)(_color & 0xff);

    internal byte A => Alpha;
    internal byte R => Red;
    internal byte G => Green;
    internal byte B => Blue;

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

    public static readonly SKColor Empty = new(0, 0, 0, 0);
}

[StructLayout(LayoutKind.Sequential, Size = 1)]
public struct SKColors
{
    public static SKColor AliceBlue = new(240, 248, 255, 255);
    public static SKColor AntiqueWhite = new(250, 235, 215, 255);
    public static SKColor Aqua = new(0, 255, 255, 255);
    public static SKColor Aquamarine = new(127, 255, 212, 255);
    public static SKColor Azure = new(240, 255, 255, 255);
    public static SKColor Beige = new(245, 245, 220, 255);
    public static SKColor Bisque = new(255, 228, 196, 255);
    public static SKColor Black = new(0, 0, 0, 255);
    public static SKColor BlanchedAlmond = new(255, 235, 205, 255);
    public static SKColor Blue = new(0, 0, 255, 255);
    public static SKColor BlueViolet = new(138, 43, 226, 255);
    public static SKColor Brown = new(165, 42, 42, 255);
    public static SKColor BurlyWood = new(222, 184, 135, 255);
    public static SKColor CadetBlue = new(95, 158, 160, 255);
    public static SKColor Chartreuse = new(127, 255, 0, 255);
    public static SKColor Chocolate = new(210, 105, 30, 255);
    public static SKColor Coral = new(255, 127, 80, 255);
    public static SKColor CornflowerBlue = new(100, 149, 237, 255);
    public static SKColor Cornsilk = new(255, 248, 220, 255);
    public static SKColor Crimson = new(220, 20, 60, 255);
    public static SKColor Cyan = new(0, 255, 255, 255);
    public static SKColor DarkBlue = new(0, 0, 139, 255);
    public static SKColor DarkCyan = new(0, 139, 139, 255);
    public static SKColor DarkGoldenrod = new(184, 134, 11, 255);
    public static SKColor DarkGray = new(169, 169, 169, 255);
    public static SKColor DarkGreen = new(0, 100, 0, 255);
    public static SKColor DarkKhaki = new(189, 183, 107, 255);
    public static SKColor DarkMagenta = new(139, 0, 139, 255);
    public static SKColor DarkOliveGreen = new(85, 107, 47, 255);
    public static SKColor DarkOrange = new(255, 140, 0, 255);
    public static SKColor DarkOrchid = new(153, 50, 204, 255);
    public static SKColor DarkRed = new(139, 0, 0, 255);
    public static SKColor DarkSalmon = new(233, 150, 122, 255);
    public static SKColor DarkSeaGreen = new(143, 188, 139, 255);
    public static SKColor DarkSlateBlue = new(72, 61, 139, 255);
    public static SKColor DarkSlateGray = new(47, 79, 79, 255);
    public static SKColor DarkTurquoise = new(0, 206, 209, 255);
    public static SKColor DarkViolet = new(148, 0, 211, 255);
    public static SKColor DeepPink = new(255, 20, 147, 255);
    public static SKColor DeepSkyBlue = new(0, 191, 255, 255);
    public static SKColor DimGray = new(105, 105, 105, 255);
    public static SKColor DodgerBlue = new(30, 144, 255, 255);
    public static SKColor Firebrick = new(178, 34, 34, 255);
    public static SKColor FloralWhite = new(255, 250, 240, 255);
    public static SKColor ForestGreen = new(34, 139, 34, 255);
    public static SKColor Fuchsia = new(255, 0, 255, 255);
    public static SKColor Gainsboro = new(220, 220, 220, 255);
    public static SKColor GhostWhite = new(248, 248, 255, 255);
    public static SKColor Gold = new(255, 215, 0, 255);
    public static SKColor Goldenrod = new(218, 165, 32, 255);
    public static SKColor Gray = new(128, 128, 128, 255);
    public static SKColor Green = new(0, 128, 0, 255);
    public static SKColor GreenYellow = new(173, 255, 47, 255);
    public static SKColor Honeydew = new(240, 255, 240, 255);
    public static SKColor HotPink = new(255, 105, 180, 255);
    public static SKColor IndianRed = new(205, 92, 92, 255);
    public static SKColor Indigo = new(75, 0, 130, 255);
    public static SKColor Ivory = new(255, 255, 240, 255);
    public static SKColor Khaki = new(240, 230, 140, 255);
    public static SKColor Lavender = new(230, 230, 250, 255);
    public static SKColor LavenderBlush = new(255, 240, 245, 255);
    public static SKColor LawnGreen = new(124, 252, 0, 255);
    public static SKColor LemonChiffon = new(255, 250, 205, 255);
    public static SKColor LightBlue = new(173, 216, 230, 255);
    public static SKColor LightCoral = new(240, 128, 128, 255);
    public static SKColor LightCyan = new(224, 255, 255, 255);
    public static SKColor LightGoldenrodYellow = new(250, 250, 210, 255);
    public static SKColor LightGray = new(211, 211, 211, 255);
    public static SKColor LightGreen = new(144, 238, 144, 255);
    public static SKColor LightPink = new(255, 182, 193, 255);
    public static SKColor LightSalmon = new(255, 160, 122, 255);
    public static SKColor LightSeaGreen = new(32, 178, 170, 255);
    public static SKColor LightSkyBlue = new(135, 206, 250, 255);
    public static SKColor LightSlateGray = new(119, 136, 153, 255);
    public static SKColor LightSteelBlue = new(176, 196, 222, 255);
    public static SKColor LightYellow = new(255, 255, 224, 255);
    public static SKColor Lime = new(0, 255, 0, 255);
    public static SKColor LimeGreen = new(50, 205, 50, 255);
    public static SKColor Linen = new(250, 240, 230, 255);
    public static SKColor Magenta = new(255, 0, 255, 255);
    public static SKColor Maroon = new(128, 0, 0, 255);
    public static SKColor MediumAquamarine = new(102, 205, 170, 255);
    public static SKColor MediumBlue = new(0, 0, 205, 255);
    public static SKColor MediumOrchid = new(186, 85, 211, 255);
    public static SKColor MediumPurple = new(147, 112, 219, 255);
    public static SKColor MediumSeaGreen = new(60, 179, 113, 255);
    public static SKColor MediumSlateBlue = new(123, 104, 238, 255);
    public static SKColor MediumSpringGreen = new(0, 250, 154, 255);
    public static SKColor MediumTurquoise = new(72, 209, 204, 255);
    public static SKColor MediumVioletRed = new(199, 21, 133, 255);
    public static SKColor MidnightBlue = new(25, 25, 112, 255);
    public static SKColor MintCream = new(245, 255, 250, 255);
    public static SKColor MistyRose = new(255, 228, 225, 255);
    public static SKColor Moccasin = new(255, 228, 181, 255);
    public static SKColor NavajoWhite = new(255, 222, 173, 255);
    public static SKColor Navy = new(0, 0, 128, 255);
    public static SKColor OldLace = new(253, 245, 230, 255);
    public static SKColor Olive = new(128, 128, 0, 255);
    public static SKColor OliveDrab = new(107, 142, 35, 255);
    public static SKColor Orange = new(255, 165, 0, 255);
    public static SKColor OrangeRed = new(255, 69, 0, 255);
    public static SKColor Orchid = new(218, 112, 214, 255);
    public static SKColor PaleGoldenrod = new(238, 232, 170, 255);
    public static SKColor PaleGreen = new(152, 251, 152, 255);
    public static SKColor PaleTurquoise = new(175, 238, 238, 255);
    public static SKColor PaleVioletRed = new(219, 112, 147, 255);
    public static SKColor PapayaWhip = new(255, 239, 213, 255);
    public static SKColor PeachPuff = new(255, 218, 185, 255);
    public static SKColor Peru = new(205, 133, 63, 255);
    public static SKColor Pink = new(255, 192, 203, 255);
    public static SKColor Plum = new(221, 160, 221, 255);
    public static SKColor PowderBlue = new(176, 224, 230, 255);
    public static SKColor Purple = new(128, 0, 128, 255);
    public static SKColor Red = new(255, 0, 0, 255);
    public static SKColor RosyBrown = new(188, 143, 143, 255);
    public static SKColor RoyalBlue = new(65, 105, 225, 255);
    public static SKColor SaddleBrown = new(139, 69, 19, 255);
    public static SKColor Salmon = new(250, 128, 114, 255);
    public static SKColor SandyBrown = new(244, 164, 96, 255);
    public static SKColor SeaGreen = new(46, 139, 87, 255);
    public static SKColor SeaShell = new(255, 245, 238, 255);
    public static SKColor Sienna = new(160, 82, 45, 255);
    public static SKColor Silver = new(192, 192, 192, 255);
    public static SKColor SkyBlue = new(135, 206, 235, 255);
    public static SKColor SlateBlue = new(106, 90, 205, 255);
    public static SKColor SlateGray = new(112, 128, 144, 255);
    public static SKColor Snow = new(255, 250, 250, 255);
    public static SKColor SpringGreen = new(0, 255, 127, 255);
    public static SKColor SteelBlue = new(70, 130, 180, 255);
    public static SKColor Tan = new(210, 180, 140, 255);
    public static SKColor Teal = new(0, 128, 128, 255);
    public static SKColor Thistle = new(216, 191, 216, 255);
    public static SKColor Tomato = new(255, 99, 71, 255);
    public static SKColor Transparent = new(255, 255, 255, 0);
    public static SKColor Turquoise = new(64, 224, 208, 255);
    public static SKColor Violet = new(238, 130, 238, 255);
    public static SKColor Wheat = new(245, 222, 179, 255);
    public static SKColor White = new(255, 255, 255, 255);
    public static SKColor WhiteSmoke = new(245, 245, 245, 255);
    public static SKColor Yellow = new(255, 255, 0, 255);
    public static SKColor YellowGreen = new(154, 205, 50, 255);

    public static SKColor Empty => new(0u);
}

public readonly partial struct SKColorF : IEquatable<SKColorF>
{
    private readonly float _red;
    private readonly float _green;
    private readonly float _blue;
    private readonly float _alpha;

    public float Red => _red;
    public float Green => _green;
    public float Blue => _blue;
    public float Alpha => _alpha;

    internal float R => _red;
    internal float G => _green;
    internal float B => _blue;
    internal float A => _alpha;

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

    public static readonly SKColorF Empty;
}

public partial struct SKMatrix : IEquatable<SKMatrix>
{
    private float _scaleX;
    private float _skewX;
    private float _transX;
    private float _skewY;
    private float _scaleY;
    private float _transY;
    private float _persp0;
    private float _persp1;
    private float _persp2;

    public float ScaleX
    {
        readonly get => _scaleX;
        set => _scaleX = value;
    }

    public float SkewX
    {
        readonly get => _skewX;
        set => _skewX = value;
    }

    public float TransX
    {
        readonly get => _transX;
        set => _transX = value;
    }

    public float SkewY
    {
        readonly get => _skewY;
        set => _skewY = value;
    }

    public float ScaleY
    {
        readonly get => _scaleY;
        set => _scaleY = value;
    }

    public float TransY
    {
        readonly get => _transY;
        set => _transY = value;
    }

    public float Persp0
    {
        readonly get => _persp0;
        set => _persp0 = value;
    }

    public float Persp1
    {
        readonly get => _persp1;
        set => _persp1 = value;
    }

    public float Persp2
    {
        readonly get => _persp2;
        set => _persp2 = value;
    }

    public static readonly SKMatrix Empty;

    public static readonly SKMatrix Identity = new()
    {
        ScaleX = 1f, ScaleY = 1f, Persp2 = 1f
    };

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
        _scaleX = scaleX;
        _skewX = skewX;
        _transX = transX;
        _skewY = skewY;
        _scaleY = scaleY;
        _transY = transY;
        _persp0 = persp0;
        _persp1 = persp1;
        _persp2 = persp2;
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
    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SCos);
        hash.Add(SSin);
        hash.Add(TX);
        hash.Add(TY);
        return hash.ToHashCode();
    }
}

public readonly struct SKCubicResampler : IEquatable<SKCubicResampler>
{
    private readonly float _b;
    private readonly float _c;

    public SKCubicResampler(float b, float c)
    {
        _b = b;
        _c = c;
    }

    public static readonly SKCubicResampler Mitchell = new(1f / 3f, 1f / 3f);
    public static readonly SKCubicResampler CatmullRom = new(0f, 0.5f);

    public float B => _b;

    public float C => _c;

    public bool Equals(SKCubicResampler obj) => _b == obj._b && _c == obj._c;

    public override bool Equals(object? obj) => obj is SKCubicResampler other && Equals(other);

    public static bool operator ==(SKCubicResampler left, SKCubicResampler right) => left.Equals(right);

    public static bool operator !=(SKCubicResampler left, SKCubicResampler right) => !left.Equals(right);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_b);
        hash.Add(_c);
        return hash.ToHashCode();
    }
}

public readonly struct SKSamplingOptions : IEquatable<SKSamplingOptions>
{
    public static readonly SKSamplingOptions Default;

    private readonly int _maxAniso;
    private readonly byte _useCubic;
    private readonly SKCubicResampler _cubic;
    private readonly SKFilterMode _filter;
    private readonly SKMipmapMode _mipmap;

    public bool IsAniso => MaxAniso != 0;

    public int MaxAniso => _maxAniso;

    public bool UseCubic => _useCubic > 0;

    public SKCubicResampler Cubic => _cubic;

    public SKFilterMode Filter => _filter;

    public SKMipmapMode Mipmap => _mipmap;

    public SKSamplingOptions(SKFilterMode filter, SKMipmapMode mipmap)
    {
        _maxAniso = 0;
        _useCubic = 0;
        _cubic = default;
        _filter = filter;
        _mipmap = mipmap;
    }

    public SKSamplingOptions(SKFilterMode filter)
    {
        _maxAniso = 0;
        _useCubic = 0;
        _cubic = default;
        _filter = filter;
        _mipmap = SKMipmapMode.None;
    }

    public SKSamplingOptions(SKCubicResampler resampler)
    {
        _maxAniso = 0;
        _useCubic = 1;
        _cubic = resampler;
        _filter = SKFilterMode.Nearest;
        _mipmap = SKMipmapMode.None;
    }

    public SKSamplingOptions(int maxAniso)
    {
        _maxAniso = Math.Max(1, maxAniso);
        _useCubic = 0;
        _cubic = default;
        _filter = SKFilterMode.Nearest;
        _mipmap = SKMipmapMode.None;
    }

    public bool Equals(SKSamplingOptions obj) =>
        _maxAniso == obj._maxAniso &&
        _useCubic == obj._useCubic &&
        _cubic == obj._cubic &&
        _filter == obj._filter &&
        _mipmap == obj._mipmap;

    public override bool Equals(object? obj) => obj is SKSamplingOptions other && Equals(other);

    public static bool operator ==(SKSamplingOptions left, SKSamplingOptions right) => left.Equals(right);

    public static bool operator !=(SKSamplingOptions left, SKSamplingOptions right) => !left.Equals(right);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_maxAniso);
        hash.Add(_useCubic);
        hash.Add(_cubic);
        hash.Add(_filter);
        hash.Add(_mipmap);
        return hash.ToHashCode();
    }
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

    internal static int GetBytesPerPixel(SKColorType colorType) =>
        colorType.GetBytesPerPixel();
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

    public bool ReadSByte(out sbyte buffer)
    {
        var success = ReadByte(out var raw);
        buffer = unchecked((sbyte)raw);
        return success;
    }

    public bool ReadInt16(out short buffer)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        var success = ReadExactly(bytes);
        buffer = success ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : (short)0;
        return success;
    }

    public bool ReadInt32(out int buffer)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        var success = ReadExactly(bytes);
        buffer = success ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : 0;
        return success;
    }

    public bool ReadByte(out byte buffer)
    {
        Span<byte> bytes = stackalloc byte[sizeof(byte)];
        var success = ReadExactly(bytes);
        buffer = success ? bytes[0] : (byte)0;
        return success;
    }

    public bool ReadUInt16(out ushort buffer)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        var success = ReadExactly(bytes);
        buffer = success ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : (ushort)0;
        return success;
    }

    public bool ReadUInt32(out uint buffer)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        var success = ReadExactly(bytes);
        buffer = success ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0u;
        return success;
    }

    public bool ReadBool(out bool buffer)
    {
        var success = ReadByte(out var raw);
        buffer = raw != 0;
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

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }
}

public class SKSurfaceProperties : SKObject
{
    private readonly SKSurfacePropsFlags _flags;

    public SKSurfacePropsFlags Flags => _flags;

    public SKPixelGeometry PixelGeometry { get; }

    public bool IsUseDeviceIndependentFonts =>
        (_flags & SKSurfacePropsFlags.UseDeviceIndependentFonts) != 0;

    public SKSurfaceProperties(SKPixelGeometry pixelGeometry)
        : this(0u, pixelGeometry)
    {
    }

    public SKSurfaceProperties(uint flags, SKPixelGeometry pixelGeometry)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _flags = (SKSurfacePropsFlags)flags;
        PixelGeometry = pixelGeometry;
    }

    public SKSurfaceProperties(SKSurfacePropsFlags flags, SKPixelGeometry pixelGeometry)
        : this((uint)flags, pixelGeometry)
    {
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
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
