using System.Numerics;
using ProGPU.Vector;

namespace SkiaSharp;

public partial class SKShader
{
    public static SKShader CreateEmpty() =>
        new(() => new SolidColorBrush(Vector4.Zero));

    public static SKShader CreateBitmap(SKBitmap src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
    }

    public static SKShader CreateBitmap(
        SKBitmap src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKMatrix localMatrix)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader(tmx, tmy, localMatrix);
    }

    public static SKShader CreateImage(SKImage src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return CreateRetainedImage(
            src.CreateOwnedCopy(),
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            SKMatrix.Identity,
            SKSamplingOptions.Default);
    }

    public static SKShader CreateImage(
        SKImage src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy) =>
        CreateImage(src, tmx, tmy, SKSamplingOptions.Default);

    public static SKShader CreateImage(
        SKImage src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKSamplingOptions sampling) =>
        CreateImage(src, tmx, tmy, sampling, SKMatrix.Identity);

#pragma warning disable CS0619
    [Obsolete("Use CreateImage(SKImage src, SKShaderTileMode tmx, SKShaderTileMode tmy, SKSamplingOptions sampling) instead.", true)]
    public static SKShader CreateImage(
        SKImage source,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKFilterQuality quality) =>
        CreateImage(source, tileModeX, tileModeY, SamplingFromQuality((int)quality));
#pragma warning restore CS0619

    public static SKShader CreateImage(
        SKImage src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKMatrix localMatrix) =>
        CreateImage(src, tmx, tmy, SKSamplingOptions.Default, localMatrix);

    public static SKShader CreateImage(
        SKImage src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKSamplingOptions sampling,
        SKMatrix localMatrix)
    {
        ArgumentNullException.ThrowIfNull(src);
        return CreateRetainedImage(src.CreateOwnedCopy(), tmx, tmy, localMatrix, sampling);
    }

#pragma warning disable CS0619
    [Obsolete("Use CreateImage(SKImage src, SKShaderTileMode tmx, SKShaderTileMode tmy, SKSamplingOptions sampling, SKMatrix localMatrix) instead.", true)]
    public static SKShader CreateImage(
        SKImage source,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKFilterQuality quality,
        SKMatrix localMatrix) =>
        CreateImage(source, tileModeX, tileModeY, SamplingFromQuality((int)quality), localMatrix);
#pragma warning restore CS0619

    public static SKShader CreatePicture(SKPicture src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader();
    }

    public static SKShader CreatePicture(
        SKPicture src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader(tmx, tmy);
    }

    public static SKShader CreatePicture(
        SKPicture src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKFilterMode filterMode)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader(tmx, tmy, filterMode);
    }

    public static SKShader CreatePicture(
        SKPicture src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKRect tile)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader(tmx, tmy, tile);
    }

    public static SKShader CreatePicture(
        SKPicture src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKFilterMode filterMode,
        SKRect tile)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader(tmx, tmy, filterMode, tile);
    }

    public static SKShader CreatePicture(
        SKPicture src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKFilterMode filterMode,
        SKMatrix localMatrix,
        SKRect tile)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader(tmx, tmy, filterMode, localMatrix, tile);
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColor[] colors,
        SKShaderTileMode mode) =>
        CreateLinearGradient(start, end, colors, null, mode);

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColorF[] colors,
        SKColorSpace colorspace,
        SKShaderTileMode mode) =>
        CreateLinearGradient(start, end, colors, colorspace, null, mode);

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColor[] colors,
        SKShaderTileMode mode) =>
        CreateRadialGradient(center, radius, colors, null, mode);

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColorF[] colors,
        SKColorSpace colorspace,
        SKShaderTileMode mode) =>
        CreateRadialGradient(center, radius, colors, colorspace, null, mode);

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColor[] colors,
        SKShaderTileMode mode) =>
        CreateTwoPointConicalGradient(start, startRadius, end, endRadius, colors, null, mode);

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColorF[] colors,
        SKColorSpace colorspace,
        SKShaderTileMode mode) =>
        CreateTwoPointConicalGradient(
            start,
            startRadius,
            end,
            endRadius,
            colors,
            colorspace,
            null,
            mode);

    public static SKShader CreateSweepGradient(SKPoint center, SKColor[] colors) =>
        CreateSweepGradient(center, colors, null, SKShaderTileMode.Clamp, 0f, 360f);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColor[] colors,
        float[]? colorPos) =>
        CreateSweepGradient(center, colors, colorPos, SKShaderTileMode.Clamp, 0f, 360f);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColor[] colors,
        SKShaderTileMode tileMode,
        float startAngle,
        float endAngle) =>
        CreateSweepGradient(center, colors, null, tileMode, startAngle, endAngle);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode tileMode,
        float startAngle,
        float endAngle) =>
        CreateSweepGradientCore(
            center,
            colors,
            colorPos,
            tileMode,
            startAngle,
            endAngle,
            SKMatrix.Identity,
            GradientColorInterpolationMode.SRgbLinearInterpolation);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode tileMode,
        float startAngle,
        float endAngle,
        SKMatrix localMatrix) =>
        CreateSweepGradientCore(
            center,
            colors,
            colorPos,
            tileMode,
            startAngle,
            endAngle,
            localMatrix,
            GradientColorInterpolationMode.SRgbLinearInterpolation);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColorF[] colors,
        SKColorSpace colorspace) =>
        CreateSweepGradient(center, colors, colorspace, null, SKShaderTileMode.Clamp, 0f, 360f);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos) =>
        CreateSweepGradient(center, colors, colorspace, colorPos, SKShaderTileMode.Clamp, 0f, 360f);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKMatrix localMatrix) =>
        CreateSweepGradient(
            center,
            colors,
            colorspace,
            colorPos,
            SKShaderTileMode.Clamp,
            0f,
            360f,
            localMatrix);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColorF[] colors,
        SKColorSpace colorspace,
        SKShaderTileMode tileMode,
        float startAngle,
        float endAngle) =>
        CreateSweepGradient(center, colors, colorspace, null, tileMode, startAngle, endAngle);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKShaderTileMode tileMode,
        float startAngle,
        float endAngle) =>
        CreateSweepGradient(
            center,
            colors,
            colorspace,
            colorPos,
            tileMode,
            startAngle,
            endAngle,
            SKMatrix.Identity);

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKShaderTileMode tileMode,
        float startAngle,
        float endAngle,
        SKMatrix localMatrix) =>
        CreateSweepGradientCore(
            center,
            colors,
            colorPos,
            tileMode,
            startAngle,
            endAngle,
            localMatrix,
            colorspace?.IsLinear == true
                ? GradientColorInterpolationMode.ScRgbLinearInterpolation
                : GradientColorInterpolationMode.SRgbLinearInterpolation);

    public static SKShader CreatePerlinNoiseFractalNoise(
        float baseFrequencyX,
        float baseFrequencyY,
        int numOctaves,
        float seed) =>
        CreatePerlinNoiseFractalNoise(
            baseFrequencyX,
            baseFrequencyY,
            numOctaves,
            seed,
            SKPointI.Empty);

    public static SKShader CreatePerlinNoiseFractalNoise(
        float baseFrequencyX,
        float baseFrequencyY,
        int numOctaves,
        float seed,
        SKSizeI tileSize) =>
        CreatePerlinNoiseFractalNoise(
            baseFrequencyX,
            baseFrequencyY,
            numOctaves,
            seed,
            new SKPointI(tileSize.Width, tileSize.Height));

    public static SKShader CreatePerlinNoiseTurbulence(
        float baseFrequencyX,
        float baseFrequencyY,
        int numOctaves,
        float seed) =>
        CreatePerlinNoiseTurbulence(
            baseFrequencyX,
            baseFrequencyY,
            numOctaves,
            seed,
            SKPointI.Empty);

    public static SKShader CreatePerlinNoiseTurbulence(
        float baseFrequencyX,
        float baseFrequencyY,
        int numOctaves,
        float seed,
        SKSizeI tileSize) =>
        CreatePerlinNoiseTurbulence(
            baseFrequencyX,
            baseFrequencyY,
            numOctaves,
            seed,
            new SKPointI(tileSize.Width, tileSize.Height));

    public static SKShader CreateCompose(
        SKShader shaderA,
        SKShader shaderB,
        SKBlendMode mode) =>
        CreateBlend(mode, shaderA, shaderB);

    public static SKShader CreateBlend(
        SKBlendMode mode,
        SKShader shaderA,
        SKShader shaderB)
    {
        ArgumentNullException.ThrowIfNull(shaderA);
        ArgumentNullException.ThrowIfNull(shaderB);
        return new SKShader(new ComposedShaderData(shaderA, shaderB, mode, null));
    }

    public static SKShader CreateBlend(
        SKBlender blender,
        SKShader shaderA,
        SKShader shaderB)
    {
        ArgumentNullException.ThrowIfNull(shaderA);
        ArgumentNullException.ThrowIfNull(shaderB);
        ArgumentNullException.ThrowIfNull(blender);

        if (blender.TryGetBlendMode(out var mode))
        {
            return new SKShader(new ComposedShaderData(shaderA, shaderB, mode, null));
        }

        if (blender.Arithmetic is { } arithmetic)
        {
            return new SKShader(new ComposedShaderData(shaderA, shaderB, null, arithmetic));
        }

        throw new NotSupportedException("The SKBlender implementation is not supported by shader composition.");
    }

    public static SKShader CreateColorFilter(SKShader shader, SKColorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(filter);
        return shader.WithColorFilter(filter);
    }

    public static SKShader CreateLocalMatrix(SKShader shader, SKMatrix localMatrix)
    {
        ArgumentNullException.ThrowIfNull(shader);
        return shader.WithLocalMatrix(localMatrix);
    }

    private static SKShader CreateSweepGradientCore(
        SKPoint center,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode tileMode,
        float startAngle,
        float endAngle,
        SKMatrix localMatrix,
        GradientColorInterpolationMode interpolationMode)
    {
        var stops = GetGradientStops(colors, colorPos);
        if (!float.IsFinite(startAngle) || !float.IsFinite(endAngle) || startAngle > endAngle)
        {
            return CreateEmpty();
        }

        if (startAngle == endAngle && tileMode == SKShaderTileMode.Decal)
        {
            return CreateEmpty();
        }

        if (!TryGetShaderCoordinateTransform(localMatrix, out var coordinateTransform))
        {
            return CreateEmpty();
        }

        var data = new SweepGradientShaderData(
            center,
            stops,
            MapTileMode(tileMode),
            interpolationMode,
            coordinateTransform,
            startAngle,
            endAngle);
        return startAngle == endAngle && tileMode is SKShaderTileMode.Repeat or SKShaderTileMode.Mirror
            ? new SKShader(() => new SolidColorBrush(data.AverageColor()))
            : data;
    }

    private static SKShader CreateSweepGradientCore(
        SKPoint center,
        SKColorF[] colors,
        float[]? colorPos,
        SKShaderTileMode tileMode,
        float startAngle,
        float endAngle,
        SKMatrix localMatrix,
        GradientColorInterpolationMode interpolationMode)
    {
        var stops = GetGradientStops(colors, colorPos);
        if (!float.IsFinite(startAngle) || !float.IsFinite(endAngle) || startAngle > endAngle)
        {
            return CreateEmpty();
        }

        if (startAngle == endAngle && tileMode == SKShaderTileMode.Decal)
        {
            return CreateEmpty();
        }

        if (!TryGetShaderCoordinateTransform(localMatrix, out var coordinateTransform))
        {
            return CreateEmpty();
        }

        var data = new SweepGradientShaderData(
            center,
            stops,
            MapTileMode(tileMode),
            interpolationMode,
            coordinateTransform,
            startAngle,
            endAngle);
        return startAngle == endAngle && tileMode is SKShaderTileMode.Repeat or SKShaderTileMode.Mirror
            ? new SKShader(() => new SolidColorBrush(data.AverageColor()))
            : data;
    }

    private static SKSamplingOptions SamplingFromQuality(int quality) => quality switch
    {
        0 => SKSamplingOptions.Default,
        1 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        2 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
        3 => new SKSamplingOptions(SKCubicResampler.Mitchell),
        _ => SKSamplingOptions.Default,
    };
}
