namespace SkiaSharp;

public enum SKJpegEncoderDownsample
{
    Downsample420 = 0,
    Downsample422 = 1,
    Downsample444 = 2,
}

public enum SKJpegEncoderAlphaOption
{
    Ignore = 0,
    BlendOnBlack = 1,
}

public readonly struct SKJpegEncoderOptions : IEquatable<SKJpegEncoderOptions>
{
    public static readonly SKJpegEncoderOptions Default = new(
        100,
        SKJpegEncoderDownsample.Downsample420,
        SKJpegEncoderAlphaOption.Ignore);

    public int Quality { get; }
    public SKJpegEncoderDownsample Downsample { get; }
    public SKJpegEncoderAlphaOption AlphaOption { get; }

    public SKJpegEncoderOptions(int quality)
        : this(
            quality,
            SKJpegEncoderDownsample.Downsample420,
            SKJpegEncoderAlphaOption.Ignore)
    {
    }

    public SKJpegEncoderOptions(
        int quality,
        SKJpegEncoderDownsample downsample,
        SKJpegEncoderAlphaOption alphaOption)
    {
        Quality = quality;
        Downsample = downsample;
        AlphaOption = alphaOption;
    }

    public bool Equals(SKJpegEncoderOptions other) =>
        Quality == other.Quality &&
        Downsample == other.Downsample &&
        AlphaOption == other.AlphaOption;

    public override bool Equals(object? obj) => obj is SKJpegEncoderOptions other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Quality, Downsample, AlphaOption);
    public static bool operator ==(SKJpegEncoderOptions left, SKJpegEncoderOptions right) => left.Equals(right);
    public static bool operator !=(SKJpegEncoderOptions left, SKJpegEncoderOptions right) => !left.Equals(right);
}

[Flags]
public enum SKPngEncoderFilterFlags
{
    NoFilters = 0,
    None = 0x08,
    Sub = 0x10,
    Up = 0x20,
    Avg = 0x40,
    Paeth = 0x80,
    AllFilters = 0xf8,
}

public readonly struct SKPngEncoderOptions : IEquatable<SKPngEncoderOptions>
{
    public static readonly SKPngEncoderOptions Default = new(SKPngEncoderFilterFlags.AllFilters, 6);

    public SKPngEncoderFilterFlags FilterFlags { get; }
    public int ZLibLevel { get; }

    public SKPngEncoderOptions(SKPngEncoderFilterFlags filterFlags, int zLibLevel)
    {
        FilterFlags = filterFlags;
        ZLibLevel = zLibLevel;
    }

    public bool Equals(SKPngEncoderOptions other) =>
        FilterFlags == other.FilterFlags && ZLibLevel == other.ZLibLevel;

    public override bool Equals(object? obj) => obj is SKPngEncoderOptions other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(FilterFlags, ZLibLevel);
    public static bool operator ==(SKPngEncoderOptions left, SKPngEncoderOptions right) => left.Equals(right);
    public static bool operator !=(SKPngEncoderOptions left, SKPngEncoderOptions right) => !left.Equals(right);
}

public enum SKWebpEncoderCompression
{
    Lossy = 0,
    Lossless = 1,
}

public readonly struct SKWebpEncoderOptions : IEquatable<SKWebpEncoderOptions>
{
    public static readonly SKWebpEncoderOptions Default = new(SKWebpEncoderCompression.Lossy, 100f);

    public SKWebpEncoderCompression Compression { get; }
    public float Quality { get; }

    public SKWebpEncoderOptions(SKWebpEncoderCompression compression, float quality)
    {
        Compression = compression;
        Quality = quality;
    }

    public bool Equals(SKWebpEncoderOptions other) =>
        Compression == other.Compression && Quality == other.Quality;

    public override bool Equals(object? obj) => obj is SKWebpEncoderOptions other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Compression, Quality);
    public static bool operator ==(SKWebpEncoderOptions left, SKWebpEncoderOptions right) => left.Equals(right);
    public static bool operator !=(SKWebpEncoderOptions left, SKWebpEncoderOptions right) => !left.Equals(right);
}
