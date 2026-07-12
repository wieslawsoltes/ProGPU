using System;
using System.Globalization;

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

    public bool Equals(SKColor other) => _color == other._color;

    public override bool Equals(object? obj) => obj is SKColor other && Equals(other);

    public static bool operator ==(SKColor left, SKColor right) => left.Equals(right);

    public static bool operator !=(SKColor left, SKColor right) => !left.Equals(right);

    public override int GetHashCode() => _color.GetHashCode();

    public static implicit operator SKColor(uint color) => new(color);

    public static explicit operator uint(SKColor color) => color._color;

    public static SKColor Parse(string hexString)
    {
        if (!TryParse(hexString, out var result))
        {
            throw new ArgumentException("Invalid hexadecimal color string.", nameof(hexString));
        }

        return result;
    }

    public static bool TryParse(string hexString, out SKColor color)
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

            if (!byte.TryParse(value.Slice(length - 3, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
                !byte.TryParse(value.Slice(length - 2, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
                !byte.TryParse(value.Slice(length - 1, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
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
}
