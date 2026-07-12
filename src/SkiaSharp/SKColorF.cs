using System;

namespace SkiaSharp;

public readonly partial struct SKColorF
{
    private const float Epsilon = 0.001f;

    public float Hue
    {
        get
        {
            ToHsv(out var hue, out _, out _);
            return hue;
        }
    }

    public SKColorF WithRed(float red) => new(red, _green, _blue, _alpha);

    public SKColorF WithGreen(float green) => new(_red, green, _blue, _alpha);

    public SKColorF WithBlue(float blue) => new(_red, _green, blue, _alpha);

    public SKColorF WithAlpha(float alpha) => new(_red, _green, _blue, alpha);

    public SKColorF Clamp() => new(
        ClampComponent(_red),
        ClampComponent(_green),
        ClampComponent(_blue),
        ClampComponent(_alpha));

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
            var second = l < 0.5f ? l * (1f + s) : l + s - s * l;
            var first = 2f * l - second;
            red = HueToRgb(first, second, h + 1f / 3f);
            green = HueToRgb(first, second, h);
            blue = HueToRgb(first, second, h - 1f / 3f);
        }

        return new SKColorF(red, green, blue, a);
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
            var first = v * (1f - s);
            var second = v * (1f - s * (h - sector));
            var third = v * (1f - s * (1f - (h - sector)));
            switch (sector)
            {
                case 0:
                    red = v;
                    green = third;
                    blue = first;
                    break;
                case 1:
                    red = second;
                    green = v;
                    blue = first;
                    break;
                case 2:
                    red = first;
                    green = v;
                    blue = third;
                    break;
                case 3:
                    red = first;
                    green = second;
                    blue = v;
                    break;
                case 4:
                    red = third;
                    green = first;
                    blue = v;
                    break;
                default:
                    red = v;
                    green = first;
                    blue = second;
                    break;
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
            h = ResolveHue(maximum, delta);
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
            h = ResolveHue(maximum, delta);
        }

        h *= 360f;
        s *= 100f;
        v *= 100f;
    }

    public override string ToString() => ((SKColor)this).ToString();

    public static implicit operator SKColorF(SKColor color) => new(
        color.Red / 255f,
        color.Green / 255f,
        color.Blue / 255f,
        color.Alpha / 255f);

    public static explicit operator SKColor(SKColorF color) => new(
        ToByte(color._red),
        ToByte(color._green),
        ToByte(color._blue),
        ToByte(color._alpha));

    public bool Equals(SKColorF other) =>
        _red == other._red &&
        _green == other._green &&
        _blue == other._blue &&
        _alpha == other._alpha;

    public override bool Equals(object? obj) => obj is SKColorF other && Equals(other);

    public static bool operator ==(SKColorF left, SKColorF right) => left.Equals(right);

    public static bool operator !=(SKColorF left, SKColorF right) => !left.Equals(right);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_red);
        hash.Add(_green);
        hash.Add(_blue);
        hash.Add(_alpha);
        return hash.ToHashCode();
    }

    private float ResolveHue(float maximum, float delta)
    {
        var redDistance = ((maximum - _red) / 6f + delta / 2f) / delta;
        var greenDistance = ((maximum - _green) / 6f + delta / 2f) / delta;
        var blueDistance = ((maximum - _blue) / 6f + delta / 2f) / delta;
        float hue;
        if (Math.Abs(_red - maximum) < Epsilon)
        {
            hue = blueDistance - greenDistance;
        }
        else if (Math.Abs(_green - maximum) < Epsilon)
        {
            hue = 1f / 3f + redDistance - blueDistance;
        }
        else
        {
            hue = 2f / 3f + greenDistance - redDistance;
        }

        if (hue < 0f)
        {
            hue += 1f;
        }

        if (hue > 1f)
        {
            hue -= 1f;
        }

        return hue;
    }

    private static float HueToRgb(float first, float second, float hue)
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
            return first + (second - first) * 6f * hue;
        }

        if (2f * hue < 1f)
        {
            return second;
        }

        if (3f * hue < 2f)
        {
            return first + (second - first) * (2f / 3f - hue) * 6f;
        }

        return first;
    }

    private static float ClampComponent(float value)
    {
        if (value > 1f)
        {
            return 1f;
        }

        if (value < 0f)
        {
            return 0f;
        }

        return value;
    }

    private static byte ToByte(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0;
        }

        if (value >= 1f)
        {
            return byte.MaxValue;
        }

        return (byte)(value * 255f + 0.5f);
    }
}
