using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using ProGPU.Backend;

namespace SkiaSharp;

public delegate void SKBitmapReleaseDelegate(IntPtr address, object context);

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
    RgbaF16 = 7,
    RgbaF32 = 8,
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
    UnknownHorizontal = 1,
    UnknownVertical = 2,
    RgbHorizontal = 3,
    RgbVertical = 4,
    BgrHorizontal = 5,
    BgrVertical = 6,
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

public struct SKPoint
{
    public float X;
    public float Y;

    public SKPoint(float x, float y)
    {
        X = x;
        Y = y;
    }

    public static readonly SKPoint Empty = new(0, 0);
    public override string ToString() => $"({X}, {Y})";
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

    public SKPointI(SKSizeI size)
    {
        _x = size.Width;
        _y = size.Height;
    }

    public SKPointI(int x, int y)
    {
        _x = x;
        _y = y;
    }

    public void Offset(SKPointI point)
    {
        _x += point.X;
        _y += point.Y;
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

    public static SKPointI Add(SKPointI point, SKSizeI size) => point + size;
    public static SKPointI Add(SKPointI point, SKPointI size) => point + size;
    public static SKPointI Subtract(SKPointI point, SKSizeI size) => point - size;
    public static SKPointI Subtract(SKPointI point, SKPointI size) => point - size;

    public static SKPointI operator +(SKPointI point, SKSizeI size) =>
        new(point.X + size.Width, point.Y + size.Height);

    public static SKPointI operator +(SKPointI point, SKPointI size) =>
        new(point.X + size.X, point.Y + size.Y);

    public static SKPointI operator -(SKPointI point, SKSizeI size) =>
        new(point.X - size.Width, point.Y - size.Height);

    public static SKPointI operator -(SKPointI point, SKPointI size) =>
        new(point.X - size.X, point.Y - size.Y);

    public static explicit operator SKSizeI(SKPointI point) => new(point.X, point.Y);
    public static implicit operator SKPoint(SKPointI point) => new(point.X, point.Y);
    public static implicit operator Vector2(SKPointI point) => new(point._x, point._y);

    public readonly bool Equals(SKPointI other) => _x == other._x && _y == other._y;
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

    public static SKPoint3 Add(SKPoint3 point, SKPoint3 size) => point + size;
    public static SKPoint3 Subtract(SKPoint3 point, SKPoint3 size) => point - size;

    public static SKPoint3 operator +(SKPoint3 point, SKPoint3 size) =>
        new(point.X + size.X, point.Y + size.Y, point.Z + size.Z);

    public static SKPoint3 operator -(SKPoint3 point, SKPoint3 size) =>
        new(point.X - size.X, point.Y - size.Y, point.Z - size.Z);

    public static implicit operator Vector3(SKPoint3 point) => new(point._x, point._y, point._z);
    public static implicit operator SKPoint3(Vector3 vector) => new(vector.X, vector.Y, vector.Z);

    public readonly bool Equals(SKPoint3 other) => _x == other._x && _y == other._y && _z == other._z;
    public override readonly bool Equals(object? obj) => obj is SKPoint3 other && Equals(other);
    public static bool operator ==(SKPoint3 left, SKPoint3 right) => left.Equals(right);
    public static bool operator !=(SKPoint3 left, SKPoint3 right) => !left.Equals(right);
    public override readonly int GetHashCode() => HashCode.Combine(_x, _y, _z);
}

public struct SKSize
{
    public float Width;
    public float Height;

    public SKSize(float width, float height)
    {
        Width = width;
        Height = height;
    }

    public static readonly SKSize Empty = new(0, 0);
}

public struct SKSizeI
{
    public int Width;
    public int Height;

    public SKSizeI(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public static readonly SKSizeI Empty = new(0, 0);
}

public struct SKRect
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;

    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public float MidX => Left + Width / 2f;
    public float MidY => Top + Height / 2f;
    public bool IsEmpty => Left >= Right || Top >= Bottom;

    public SKRect(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public static readonly SKRect Empty = new(0, 0, 0, 0);

    public static SKRect Create(float width, float height) => new(0f, 0f, width, height);
    public static SKRect Create(float x, float y, float width, float height) =>
        new(x, y, x + width, y + height);

    public void Union(SKRect rect)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        if (IsEmpty)
        {
            this = rect;
            return;
        }

        Left = Math.Min(Left, rect.Left);
        Top = Math.Min(Top, rect.Top);
        Right = Math.Max(Right, rect.Right);
        Bottom = Math.Max(Bottom, rect.Bottom);
    }

    public void Inflate(float amount)
    {
        Inflate(amount, amount);
    }

    public void Inflate(float x, float y)
    {
        Left -= x;
        Top -= y;
        Right += x;
        Bottom += y;
    }

    public void Offset(float x, float y)
    {
        Left += x;
        Top += y;
        Right += x;
        Bottom += y;
    }
}

public struct SKRectI
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public SKRectI(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public static readonly SKRectI Empty = new(0, 0, 0, 0);

    public static SKRectI Create(int width, int height) => new(0, 0, width, height);
    public static SKRectI Create(int x, int y, int width, int height) =>
        new(x, y, x + width, y + height);
}

public struct SKColor
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte A { get; }
    public byte Red => R;
    public byte Green => G;
    public byte Blue => B;
    public byte Alpha => A;

    public SKColor(byte r, byte g, byte b, byte a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public SKColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
        A = 255;
    }

    public SKColor(uint value)
    {
        A = (byte)((value >> 24) & 0xFF);
        R = (byte)((value >> 16) & 0xFF);
        G = (byte)((value >> 8) & 0xFF);
        B = (byte)(value & 0xFF);
    }

    public static readonly SKColor Empty = new(0, 0, 0, 0);

    public readonly SKColor WithRed(byte red) => new(red, G, B, A);
    public readonly SKColor WithGreen(byte green) => new(R, green, B, A);
    public readonly SKColor WithBlue(byte blue) => new(R, G, blue, A);
    public readonly SKColor WithAlpha(byte alpha) => new(R, G, B, alpha);

    public override readonly string ToString() => $"#{A:x2}{R:x2}{G:x2}{B:x2}";

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

    public static implicit operator SKColor(uint val)
    {
        byte a = (byte)((val >> 24) & 0xFF);
        byte r = (byte)((val >> 16) & 0xFF);
        byte g = (byte)((val >> 8) & 0xFF);
        byte b = (byte)(val & 0xFF);
        return new SKColor(r, g, b, a);
    }

    public static implicit operator uint(SKColor color)
    {
        return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
    }
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
    public static readonly SKColor Empty = new(0, 0, 0, 0);
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

public struct SKColorF
{
    public float R;
    public float G;
    public float B;
    public float A;

    public SKColorF(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
}

public struct SKColorSpaceTransferFn : IEquatable<SKColorSpaceTransferFn>
{
    public static readonly SKColorSpaceTransferFn Empty;
    public static SKColorSpaceTransferFn Linear => new(1f, 1f, 0f, 0f, 0f, 0f, 0f);
    public static SKColorSpaceTransferFn Srgb => new(
        2.4f,
        1f / 1.055f,
        0.055f / 1.055f,
        1f / 12.92f,
        0.04045f,
        0f,
        0f);

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

    public readonly float Transform(float value) =>
        value < D ? C * value + F : MathF.Pow(A * value + B, G) + E;

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
    private float[]? _values;

    public static readonly SKColorSpaceXyz Empty;
    public static readonly SKColorSpaceXyz Identity = new(new[]
    {
        1f, 0f, 0f,
        0f, 1f, 0f,
        0f, 0f, 1f,
    });
    public static SKColorSpaceXyz Srgb => new(new[]
    {
        0.4124564f, 0.3575761f, 0.1804375f,
        0.2126729f, 0.7151522f, 0.0721750f,
        0.0193339f, 0.1191920f, 0.9503041f,
    });

    public float[] Values
    {
        readonly get => _values is null ? new float[9] : (float[])_values.Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length != 9)
            {
                throw new ArgumentException("The matrix array must have a length of 9.", nameof(value));
            }

            _values = (float[])value.Clone();
        }
    }

    public SKColorSpaceXyz(float[] values)
    {
        _values = null;
        Values = values;
    }

    public readonly bool Equals(SKColorSpaceXyz other) => Values.AsSpan().SequenceEqual(other.Values);
    public override readonly bool Equals(object? obj) => obj is SKColorSpaceXyz other && Equals(other);
    public static bool operator ==(SKColorSpaceXyz left, SKColorSpaceXyz right) => left.Equals(right);
    public static bool operator !=(SKColorSpaceXyz left, SKColorSpaceXyz right) => !left.Equals(right);
    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

public struct SKMatrix
{
    public float ScaleX;
    public float SkewX;
    public float TransX;
    public float SkewY;
    public float ScaleY;
    public float TransY;
    public float Persp0;
    public float Persp1;
    public float Persp2;

    public static readonly SKMatrix Identity = new()
    {
        ScaleX = 1f, ScaleY = 1f, Persp2 = 1f
    };

    public readonly bool IsIdentity =>
        ScaleX == 1f && SkewX == 0f && TransX == 0f &&
        SkewY == 0f && ScaleY == 1f && TransY == 0f &&
        Persp0 == 0f && Persp1 == 0f && Persp2 == 1f;

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

    public Matrix4x4 ToMatrix4x4()
    {
        return new Matrix4x4(
            ScaleX, SkewY, 0f, 0f,
            SkewX, ScaleY, 0f, 0f,
            0f, 0f, 1f, 0f,
            TransX, TransY, 0f, 1f
        );
    }

    public static SKMatrix CreateIdentity() => Identity;

    public static SKMatrix CreateTranslation(float x, float y)
    {
        var matrix = Identity;
        matrix.TransX = x;
        matrix.TransY = y;
        return matrix;
    }

    public static SKMatrix CreateScale(float x, float y)
    {
        var matrix = Identity;
        matrix.ScaleX = x;
        matrix.ScaleY = y;
        return matrix;
    }

    public static SKMatrix CreateScale(float x, float y, float pivotX, float pivotY)
    {
        return FromMatrix4x4(
            Matrix4x4.CreateTranslation(-pivotX, -pivotY, 0f)
            * Matrix4x4.CreateScale(x, y, 1f)
            * Matrix4x4.CreateTranslation(pivotX, pivotY, 0f));
    }

    public static SKMatrix CreateRotationDegrees(float degrees)
    {
        return CreateRotationDegrees(degrees, 0f, 0f);
    }

    public static SKMatrix CreateRotationDegrees(float degrees, float pivotX, float pivotY)
    {
        var radians = degrees * MathF.PI / 180f;
        return FromMatrix4x4(
            Matrix4x4.CreateTranslation(-pivotX, -pivotY, 0f)
            * Matrix4x4.CreateRotationZ(radians)
            * Matrix4x4.CreateTranslation(pivotX, pivotY, 0f));
    }

    public SKMatrix PreConcat(SKMatrix matrix)
    {
        return FromMatrix4x4(matrix.ToMatrix4x4() * ToMatrix4x4());
    }

    public SKMatrix PostConcat(SKMatrix matrix)
    {
        return FromMatrix4x4(ToMatrix4x4() * matrix.ToMatrix4x4());
    }

    public static SKMatrix Concat(SKMatrix first, SKMatrix second)
    {
        return FromMatrix4x4(second.ToMatrix4x4() * first.ToMatrix4x4());
    }

    public static void Concat(ref SKMatrix result, SKMatrix first, SKMatrix second)
    {
        result = Concat(first, second);
    }

    internal static SKMatrix FromMatrix4x4(Matrix4x4 matrix)
    {
        return new SKMatrix
        {
            ScaleX = matrix.M11,
            SkewX = matrix.M21,
            TransX = matrix.M41,
            SkewY = matrix.M12,
            ScaleY = matrix.M22,
            TransY = matrix.M42,
            Persp0 = matrix.M14,
            Persp1 = matrix.M24,
            Persp2 = matrix.M44
        };
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
        Create(scale, degrees * MathF.PI / 180f, tx, ty, anchorX, anchorY);

    public static SKRotationScaleMatrix Create(
        float scale,
        float radians,
        float tx,
        float ty,
        float anchorX,
        float anchorY)
    {
        var sin = MathF.Sin(radians) * scale;
        var cos = MathF.Cos(radians) * scale;
        return new SKRotationScaleMatrix(
            cos,
            sin,
            tx - cos * anchorX + sin * anchorY,
            ty - sin * anchorX - cos * anchorY);
    }

    public readonly bool Equals(SKRotationScaleMatrix other) =>
        SCos == other.SCos && SSin == other.SSin && TX == other.TX && TY == other.TY;
    public override readonly bool Equals(object? obj) => obj is SKRotationScaleMatrix other && Equals(other);
    public static bool operator ==(SKRotationScaleMatrix left, SKRotationScaleMatrix right) => left.Equals(right);
    public static bool operator !=(SKRotationScaleMatrix left, SKRotationScaleMatrix right) => !left.Equals(right);
    public override readonly int GetHashCode() => HashCode.Combine(SCos, SSin, TX, TY);
}

public class SKMatrix44
{
    public float M00 { get; set; } = 1f;
    public float M01 { get; set; }
    public float M02 { get; set; }
    public float M03 { get; set; }
    public float M10 { get; set; }
    public float M11 { get; set; } = 1f;
    public float M12 { get; set; }
    public float M13 { get; set; }
    public float M20 { get; set; }
    public float M21 { get; set; }
    public float M22 { get; set; } = 1f;
    public float M23 { get; set; }
    public float M30 { get; set; }
    public float M31 { get; set; }
    public float M32 { get; set; }
    public float M33 { get; set; } = 1f;

    public Matrix4x4 ToMatrix4x4()
    {
        return new Matrix4x4(
            M00, M01, M02, M03,
            M10, M11, M12, M13,
            M20, M21, M22, M23,
            M30, M31, M32, M33);
    }

    internal static SKMatrix44 FromMatrix4x4(Matrix4x4 matrix)
    {
        return new SKMatrix44
        {
            M00 = matrix.M11,
            M01 = matrix.M12,
            M02 = matrix.M13,
            M03 = matrix.M14,
            M10 = matrix.M21,
            M11 = matrix.M22,
            M12 = matrix.M23,
            M13 = matrix.M24,
            M20 = matrix.M31,
            M21 = matrix.M32,
            M22 = matrix.M33,
            M23 = matrix.M34,
            M30 = matrix.M41,
            M31 = matrix.M42,
            M32 = matrix.M43,
            M33 = matrix.M44
        };
    }
}

public struct SKCubicResampler
{
    public float B;
    public float C;

    public SKCubicResampler(float b, float c)
    {
        B = b;
        C = c;
    }

    public static readonly SKCubicResampler Mitchell = new(1f / 3f, 1f / 3f);
    public static readonly SKCubicResampler CatmullRom = new(0f, 0.5f);
}

public struct SKSamplingOptions
{
    public static readonly SKSamplingOptions Default;
    public SKFilterMode FilterMode;
    public SKMipmapMode MipmapMode;
    public bool UseCubic;
    public SKCubicResampler CubicResampler;

    public SKSamplingOptions(SKFilterMode filterMode, SKMipmapMode mipmapMode)
    {
        FilterMode = filterMode;
        MipmapMode = mipmapMode;
        UseCubic = false;
        CubicResampler = default;
    }

    public SKSamplingOptions(SKCubicResampler cubicResampler)
    {
        FilterMode = default;
        MipmapMode = default;
        UseCubic = true;
        CubicResampler = cubicResampler;
    }
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

public struct SKImageInfo
{
    public int Width;
    public int Height;
    public SKColorType ColorType;
    public SKAlphaType AlphaType;
    public SKColorSpace? ColorSpace;

    public int BytesPerPixel => ColorType switch
    {
        SKColorType.Alpha8 => 1,
        SKColorType.Rgb565 or SKColorType.Argb4444 => 2,
        SKColorType.RgbaF16 => 8,
        SKColorType.RgbaF32 => 16,
        _ => 4
    };
    public int RowBytes => checked(Width * BytesPerPixel);
    public int BytesSize => RowBytes * Height;

    public SKImageInfo(int width, int height, SKColorType colorType = SKColorType.Rgba8888, SKAlphaType alphaType = SKAlphaType.Premul, SKColorSpace? colorSpace = null)
    {
        Width = width;
        Height = height;
        ColorType = colorType;
        AlphaType = alphaType;
        ColorSpace = colorSpace;
    }

    public static readonly SKColorType PlatformColorType = SKColorType.Rgba8888;
}

public abstract class SKStream : IDisposable
{
    public virtual void Dispose() { }
}

public class SKData : IDisposable
{
    public byte[] Bytes { get; }

    public SKData(byte[] bytes)
    {
        Bytes = bytes;
    }

    public static SKData CreateCopy(IntPtr address, int length)
    {
        byte[] buffer = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy(address, buffer, 0, length);
        return new SKData(buffer);
    }

    public static SKData Create(SKStream stream)
    {
        if (stream is SKManagedStream managed)
        {
            using (var ms = new MemoryStream())
            {
                managed.Stream.CopyTo(ms);
                return new SKData(ms.ToArray());
            }
        }
        return new SKData(Array.Empty<byte>());
    }

    public static SKData Create(Stream stream)
    {
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            return new SKData(ms.ToArray());
        }
    }

    public void SaveTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(Bytes, 0, Bytes.Length);
    }

    public byte[] ToArray() => (byte[])Bytes.Clone();

    public void Dispose() { }
}

public class SKCodec : IDisposable
{
    private readonly byte[] _data;
    internal byte[] EncodedBytes => _data;

    private SKCodec(byte[] data)
    {
        _data = data;
        var decoded = SKEncodedImageDecoder.Decode(data);
        Info = new SKImageInfo(
            decoded.Width,
            decoded.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            decoded.ColorSpace);
    }

    public SKImageInfo Info { get; }

    public SKSizeI GetScaledDimensions(float desiredScale)
    {
        if (!float.IsFinite(desiredScale) || desiredScale <= 0f)
        {
            return new SKSizeI(Info.Width, Info.Height);
        }

        return new SKSizeI(
            Math.Max(1, (int)MathF.Round(Info.Width * MathF.Min(desiredScale, 1f))),
            Math.Max(1, (int)MathF.Round(Info.Height * MathF.Min(desiredScale, 1f))));
    }

    public static SKCodec Create(SKData data)
    {
        return new SKCodec(data.Bytes);
    }

    public static SKCodec Create(SKStream stream)
    {
        using (var data = SKData.Create(stream))
        {
            return new SKCodec(data.Bytes);
        }
    }

    public static SKCodec Create(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return new SKCodec(ms.ToArray());
    }

    public void Dispose() { }
}

public class SKSurfaceProperties : IDisposable
{
    public SKPixelGeometry PixelGeometry { get; }

    public SKSurfaceProperties(SKPixelGeometry pixelGeometry)
    {
        PixelGeometry = pixelGeometry;
    }

    public void Dispose()
    {
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
