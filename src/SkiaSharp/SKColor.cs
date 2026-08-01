using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SkiaSharp;

public readonly partial struct SKColor
{
    public float Hue
    {
        get
        {
            ToHsv(out var hue, out _, out _);
            return hue;
        }
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

    public override string ToString() => $"#{Alpha:x2}{Red:x2}{Green:x2}{Blue:x2}";

    public bool Equals(SKColor obj) => _color == obj._color;

    public override bool Equals(object? other) => other is SKColor color && Equals(color);

    public static bool operator ==(SKColor left, SKColor right) => left.Equals(right);

    public static bool operator !=(SKColor left, SKColor right) => !left.Equals(right);

    public override int GetHashCode() => _color.GetHashCode();

    public static implicit operator SKColor(uint color) => new(color);

    public static explicit operator uint(SKColor color) => color._color;

    public static SKColor Parse(string hexString)
        => Parse(hexString.AsSpan());

    public static SKColor Parse(ReadOnlySpan<char> hexString)
    {
        if (!TryParse(hexString, out var result))
        {
            throw new ArgumentException("Invalid hexadecimal color string.", nameof(hexString));
        }

        return result;
    }

    public static bool TryParse(string hexString, out SKColor color)
        => TryParse(hexString.AsSpan(), out color);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryParse(ReadOnlySpan<char> hexString, out SKColor color)
    {
        ReadOnlySpan<char> value;
        if (hexString.Length is 4 or 5 or 7 or 9 && hexString[0] == '#')
        {
            value = hexString[1..];
        }
        else if (hexString.Length is 3 or 4 or 6 or 8 &&
                 HexNibble(hexString[0]) >= 0 &&
                 HexNibble(hexString[^1]) >= 0)
        {
            value = hexString;
        }
        else
        {
            value = hexString.Trim().TrimStart('#');
        }

        if (value.IsEmpty)
        {
            color = Empty;
            return false;
        }
        var length = value.Length;
        if (length is 3 or 4)
        {
            var offset = length - 3;
            var alpha = length == 4 ? HexNibble(value[0]) : 15;
            var red = HexNibble(value[offset]);
            var green = HexNibble(value[offset + 1]);
            var blue = HexNibble(value[offset + 2]);
            if ((alpha | red | green | blue) < 0)
            {
                color = Empty;
                return false;
            }

            color = new SKColor(
                (byte)(red * 17),
                (byte)(green * 17),
                (byte)(blue * 17),
                (byte)(alpha * 17));
            return true;
        }

        if (length is 6 or 8 &&
            uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            color = packed;
            if (length == 6)
            {
                color = color.WithAlpha(byte.MaxValue);
            }

            return true;
        }

        color = Empty;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexNibble(char value)
    {
        var digit = value - '0';
        if ((uint)digit <= 9u)
        {
            return digit;
        }

        var letter = (value | (char)0x20) - 'a';
        return (uint)letter <= 5u ? letter + 10 : -1;
    }
}
