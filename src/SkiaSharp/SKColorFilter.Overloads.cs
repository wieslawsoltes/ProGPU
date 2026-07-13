using System;
using System.Numerics;

namespace SkiaSharp;

public partial class SKColorFilter
{
    private const float FloatMachineEpsilon = 1.1920929e-7f;
    public const int ColorMatrixSize = 20;
    public const int TableMaxLength = 256;

    internal enum ColorFilterKind
    {
        Blend,
        Table,
        ColorMatrix,
        Luma,
        Compose,
        Lerp,
        HslaColorMatrix,
        HighContrast,
    }

    private static readonly Lazy<SKColorFilter> s_srgbToLinear = new(
        static () => CreateProtectedGammaFilter(SrgbToLinear));
    private static readonly Lazy<SKColorFilter> s_linearToSrgb = new(
        static () => CreateProtectedGammaFilter(LinearToSrgb));

    private readonly ColorFilterKind _kind;
    private readonly SKColorFilter? _outer;
    private readonly SKColorFilter? _inner;
    private readonly float _weight;
    private readonly SKHighContrastConfig _highContrast;

    private SKColorFilter(float[] colorMatrix, ColorFilterKind kind)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = kind;
        _colorMatrix = colorMatrix;
    }

    private SKColorFilter(SKColorFilter outer, SKColorFilter inner)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = ColorFilterKind.Compose;
        _outer = outer;
        _inner = inner;
    }

    private SKColorFilter(float weight, SKColorFilter filter0, SKColorFilter filter1)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = ColorFilterKind.Lerp;
        _weight = weight;
        _outer = filter0;
        _inner = filter1;
    }

    private SKColorFilter(SKHighContrastConfig config)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = ColorFilterKind.HighContrast;
        _highContrast = config;
    }

    internal bool TryGetCompose(out SKColorFilter outer, out SKColorFilter inner)
    {
        outer = _outer!;
        inner = _inner!;
        return _kind == ColorFilterKind.Compose;
    }

    internal bool TryGetLerp(out float weight, out SKColorFilter filter0, out SKColorFilter filter1)
    {
        weight = _weight;
        filter0 = _outer!;
        filter1 = _inner!;
        return _kind == ColorFilterKind.Lerp;
    }

    internal bool TryGetHslaColorMatrix(out ReadOnlyMemory<float> matrix)
    {
        if (_kind == ColorFilterKind.HslaColorMatrix && _colorMatrix != null)
        {
            matrix = _colorMatrix;
            return true;
        }

        matrix = default;
        return false;
    }

    internal bool TryGetHighContrast(out SKHighContrastConfig config)
    {
        config = _highContrast;
        return _kind == ColorFilterKind.HighContrast;
    }

    public static SKColorFilter CreateSrgbToLinearGamma() => s_srgbToLinear.Value;

    public static SKColorFilter CreateLinearToSrgbGamma() => s_linearToSrgb.Value;

    public static SKColorFilter CreateLighting(SKColor mul, SKColor add)
    {
        if (add.R == 0 && add.G == 0 && add.B == 0)
        {
            return CreateBlendMode(
                new SKColor(mul.R, mul.G, mul.B, 255),
                SKBlendMode.Modulate);
        }

        return CreateColorMatrix(new float[]
        {
            mul.R / 255f, 0f, 0f, 0f, add.R / 255f,
            0f, mul.G / 255f, 0f, 0f, add.G / 255f,
            0f, 0f, mul.B / 255f, 0f, add.B / 255f,
            0f, 0f, 0f, 1f, 0f,
        });
    }

    public static SKColorFilter CreateCompose(SKColorFilter outer, SKColorFilter inner)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        return new SKColorFilter(outer, inner);
    }

    public static SKColorFilter CreateLerp(
        float weight,
        SKColorFilter filter0,
        SKColorFilter filter1)
    {
        ArgumentNullException.ThrowIfNull(filter0);
        ArgumentNullException.ThrowIfNull(filter1);
        if (float.IsNaN(weight))
        {
            return null!;
        }

        if (ReferenceEquals(filter0, filter1) || weight <= 0f)
        {
            return filter0;
        }

        if (weight >= 1f)
        {
            return filter1;
        }

        return new SKColorFilter(weight, filter0, filter1);
    }

    public static SKColorFilter CreateColorMatrix(ReadOnlySpan<float> matrix)
    {
        ValidateColorMatrixLength(matrix.Length, nameof(matrix));
        if (!AllFinite(matrix))
        {
            return null!;
        }

        return new SKColorFilter(matrix.ToArray());
    }

    public static SKColorFilter CreateHslaColorMatrix(ReadOnlySpan<float> matrix)
    {
        ValidateColorMatrixLength(matrix.Length, nameof(matrix));
        if (!AllFinite(matrix))
        {
            return null!;
        }

        return new SKColorFilter(matrix.ToArray(), ColorFilterKind.HslaColorMatrix);
    }

    public static SKColorFilter CreateTable(byte[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return CreateTable(table.AsSpan());
    }

    public static SKColorFilter CreateTable(ReadOnlySpan<byte> table)
    {
        ValidateTableLength(table.Length, "table", "Table");
        var snapshot = table.ToArray();
        return new SKColorFilter(snapshot, snapshot, snapshot, snapshot);
    }

    public static SKColorFilter CreateTable(
        ReadOnlySpan<byte> tableA,
        ReadOnlySpan<byte> tableR,
        ReadOnlySpan<byte> tableG,
        ReadOnlySpan<byte> tableB)
    {
        ValidateTableLength(tableA.Length, nameof(tableA), "Table A");
        ValidateTableLength(tableR.Length, nameof(tableR), "Table R");
        ValidateTableLength(tableG.Length, nameof(tableG), "Table G");
        ValidateTableLength(tableB.Length, nameof(tableB), "Table B");
        return new SKColorFilter(
            tableA.ToArray(),
            tableR.ToArray(),
            tableG.ToArray(),
            tableB.ToArray());
    }

    public static SKColorFilter CreateHighContrast(SKHighContrastConfig config) =>
        config.IsValid ? new SKColorFilter(config) : null!;

    public static SKColorFilter CreateHighContrast(
        bool grayscale,
        SKHighContrastConfigInvertStyle invertStyle,
        float contrast) =>
        CreateHighContrast(new SKHighContrastConfig(grayscale, invertStyle, contrast));

    private SKColor ApplyRetainedFilter(SKColor destination)
    {
        switch (_kind)
        {
            case ColorFilterKind.Compose:
                return _outer!.Apply(_inner!.Apply(destination));
            case ColorFilterKind.Lerp:
            {
                var color0 = ToPremultiplied(_outer!.Apply(destination));
                var color1 = ToPremultiplied(_inner!.Apply(destination));
                return FromPremultiplied(Vector4.Lerp(color0, color1, _weight));
            }
            case ColorFilterKind.HslaColorMatrix:
                return ApplyHslaColorMatrix(destination, _colorMatrix!);
            case ColorFilterKind.HighContrast:
                return ApplyHighContrast(destination, _highContrast);
            default:
                return destination;
        }
    }

    private static SKColor ApplyHslaColorMatrix(SKColor color, float[] matrix)
    {
        var source = new Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);
        var hsl = RgbToHsl(new Vector3(source.X, source.Y, source.Z));
        var transformed = ApplyRawColorMatrix(new Vector4(hsl, source.W), matrix);
        var rgb = HslToRgb(new Vector3(transformed.X, transformed.Y, transformed.Z));
        return ToColor(new Vector4(Vector3.Clamp(rgb, Vector3.Zero, Vector3.One), Clamp01(transformed.W)));
    }

    private static SKColor ApplyHighContrast(SKColor color, SKHighContrastConfig config)
    {
        var alpha = color.A / 255f;
        var rgb = new Vector3(
            SrgbToLinear(color.R / 255f),
            SrgbToLinear(color.G / 255f),
            SrgbToLinear(color.B / 255f));

        if (config.Grayscale)
        {
            var luminance = Vector3.Dot(rgb, new Vector3(0.2126f, 0.7152f, 0.0722f));
            rgb = new Vector3(luminance);
        }

        switch (config.InvertStyle)
        {
            case SKHighContrastConfigInvertStyle.InvertBrightness:
                rgb = Vector3.One - rgb;
                break;
            case SKHighContrastConfigInvertStyle.InvertLightness:
            {
                var hsl = RgbToHsl(rgb);
                hsl.Z = 1f - hsl.Z;
                rgb = HslToRgb(hsl);
                break;
            }
        }

        var contrast = Math.Clamp(
            config.Contrast,
            -1f + FloatMachineEpsilon,
            1f - FloatMachineEpsilon);
        var contrastScale = (1f + contrast) / (1f - contrast);
        rgb = Vector3.Clamp(
            new Vector3(0.5f) + ((rgb - new Vector3(0.5f)) * contrastScale),
            Vector3.Zero,
            Vector3.One);
        rgb = new Vector3(
            LinearToSrgb(rgb.X),
            LinearToSrgb(rgb.Y),
            LinearToSrgb(rgb.Z));
        return ToColor(new Vector4(rgb, alpha));
    }

    private static Vector4 ApplyRawColorMatrix(Vector4 source, float[] matrix) =>
        new(
            Vector4.Dot(source, new Vector4(matrix[0], matrix[1], matrix[2], matrix[3])) + matrix[4],
            Vector4.Dot(source, new Vector4(matrix[5], matrix[6], matrix[7], matrix[8])) + matrix[9],
            Vector4.Dot(source, new Vector4(matrix[10], matrix[11], matrix[12], matrix[13])) + matrix[14],
            Vector4.Dot(source, new Vector4(matrix[15], matrix[16], matrix[17], matrix[18])) + matrix[19]);

    private static Vector3 RgbToHsl(Vector3 color)
    {
        var maximum = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        var minimum = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        var delta = maximum - minimum;
        var lightness = (maximum + minimum) * 0.5f;
        if (delta <= 0.000001f)
        {
            return new Vector3(0f, 0f, lightness);
        }

        var saturation = delta / MathF.Max(0.000001f, 1f - MathF.Abs((2f * lightness) - 1f));
        float hue;
        if (maximum == color.X)
        {
            hue = ((color.Y - color.Z) / delta) + (color.Y < color.Z ? 6f : 0f);
        }
        else if (maximum == color.Y)
        {
            hue = ((color.Z - color.X) / delta) + 2f;
        }
        else
        {
            hue = ((color.X - color.Y) / delta) + 4f;
        }

        return new Vector3(hue / 6f, saturation, lightness);
    }

    private static Vector3 HslToRgb(Vector3 hsl)
    {
        if (MathF.Abs(hsl.Y) <= 0.000001f)
        {
            return new Vector3(hsl.Z);
        }

        var q = hsl.Z < 0.5f
            ? hsl.Z * (1f + hsl.Y)
            : hsl.Z + hsl.Y - (hsl.Z * hsl.Y);
        var p = (2f * hsl.Z) - q;
        return new Vector3(
            HueToRgb(p, q, hsl.X + (1f / 3f)),
            HueToRgb(p, q, hsl.X),
            HueToRgb(p, q, hsl.X - (1f / 3f)));
    }

    private static float HueToRgb(float p, float q, float hue)
    {
        hue -= MathF.Floor(hue);
        if (hue < 1f / 6f)
        {
            return p + ((q - p) * 6f * hue);
        }
        if (hue < 0.5f)
        {
            return q;
        }
        if (hue < 2f / 3f)
        {
            return p + ((q - p) * ((2f / 3f) - hue) * 6f);
        }
        return p;
    }

    private static SKColorFilter CreateProtectedGammaFilter(Func<float, float> transfer)
    {
        var alpha = new byte[TableMaxLength];
        var table = new byte[TableMaxLength];
        for (var index = 0; index < TableMaxLength; index++)
        {
            alpha[index] = (byte)index;
            table[index] = ToByte(transfer(index / 255f));
        }

        var filter = new SKColorFilter(alpha, table, table, table);
        filter.PreventPublicDisposal();
        return filter;
    }

    private static float SrgbToLinear(float value) =>
        value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private static float LinearToSrgb(float value) =>
        value <= 0.0031308f
            ? value * 12.92f
            : (1.055f * MathF.Pow(value, 1f / 2.4f)) - 0.055f;

    private static SKColor ToColor(Vector4 color) => new(
        ToByte(color.X),
        ToByte(color.Y),
        ToByte(color.Z),
        ToByte(color.W));

    private static bool IsNoOpBlendColorFilter(byte alpha, SKBlendMode mode) =>
        mode == SKBlendMode.Dst ||
        (alpha == 0 && mode is
            SKBlendMode.SrcOver or
            SKBlendMode.DstOver or
            SKBlendMode.DstOut or
            SKBlendMode.SrcATop or
            SKBlendMode.Xor or
            SKBlendMode.Darken) ||
        (alpha == 255 && mode == SKBlendMode.DstIn);

    private static void ValidateColorMatrixLength(int length, string parameterName)
    {
        if (length != ColorMatrixSize)
        {
            throw new ArgumentException($"Matrix must have a length of {ColorMatrixSize}.", parameterName);
        }
    }

    private static void ValidateTableLength(int length, string parameterName, string label)
    {
        if (length != TableMaxLength)
        {
            throw new ArgumentException($"{label} must have a length of {TableMaxLength}.", parameterName);
        }
    }

    private static bool AllFinite(ReadOnlySpan<float> values)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }
        }
        return true;
    }
}
