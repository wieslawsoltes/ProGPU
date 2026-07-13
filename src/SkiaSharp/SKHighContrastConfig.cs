using System;

namespace SkiaSharp;

public enum SKHighContrastConfigInvertStyle
{
    NoInvert,
    InvertBrightness,
    InvertLightness,
}

public struct SKHighContrastConfig : IEquatable<SKHighContrastConfig>
{
    public static readonly SKHighContrastConfig Default = new(
        grayscale: false,
        SKHighContrastConfigInvertStyle.NoInvert,
        contrast: 0f);

    private byte _grayscale;
    private SKHighContrastConfigInvertStyle _invertStyle;
    private float _contrast;

    public SKHighContrastConfig(
        bool grayscale,
        SKHighContrastConfigInvertStyle invertStyle,
        float contrast)
    {
        _grayscale = grayscale ? (byte)1 : (byte)0;
        _invertStyle = invertStyle;
        _contrast = contrast;
    }

    public bool Grayscale
    {
        readonly get => _grayscale > 0;
        set => _grayscale = value ? (byte)1 : (byte)0;
    }

    public SKHighContrastConfigInvertStyle InvertStyle
    {
        readonly get => _invertStyle;
        set => _invertStyle = value;
    }

    public float Contrast
    {
        readonly get => _contrast;
        set => _contrast = value;
    }

    public readonly bool IsValid =>
        InvertStyle >= SKHighContrastConfigInvertStyle.NoInvert &&
        InvertStyle <= SKHighContrastConfigInvertStyle.InvertLightness &&
        Contrast >= -1f &&
        Contrast <= 1f;

    public readonly bool Equals(SKHighContrastConfig obj) =>
        _grayscale == obj._grayscale &&
        _invertStyle == obj._invertStyle &&
        _contrast == obj._contrast;

    public override readonly bool Equals(object? obj) =>
        obj is SKHighContrastConfig config && Equals(config);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_grayscale);
        hash.Add(_invertStyle);
        hash.Add(_contrast);
        return hash.ToHashCode();
    }

    public static bool operator ==(SKHighContrastConfig left, SKHighContrastConfig right) =>
        left.Equals(right);

    public static bool operator !=(SKHighContrastConfig left, SKHighContrastConfig right) =>
        !left.Equals(right);
}
