namespace SkiaSharp;

#pragma warning disable CS0618, CS0619

public partial class SKImageFilter
{
    public static SKImageFilter CreateEmpty() =>
        CreateShader(null, dither: false);

    public static SKImageFilter CreateCrop(SKRect rect) =>
        CreateCrop(rect, SKShaderTileMode.Decal, null);

    public static SKImageFilter CreateCrop(
        SKRect rect,
        SKShaderTileMode tileMode) =>
        CreateCrop(rect, tileMode, null);

    public static SKImageFilter CreateCrop(
        SKRect rect,
        SKShaderTileMode tileMode,
        SKImageFilter? input)
    {
        _ = tileMode;
        return new SKImageFilter(
            FilterKind.Offset,
            new OffsetData(0f, 0f),
            input,
            rect);
    }

    public static SKImageFilter CreateArithmetic(
        float k1,
        float k2,
        float k3,
        float k4,
        bool enforcePMColor,
        SKImageFilter? background) =>
        CreateArithmetic(k1, k2, k3, k4, enforcePMColor, background, null, null);

    public static SKImageFilter CreateArithmetic(
        float k1,
        float k2,
        float k3,
        float k4,
        bool enforcePMColor,
        SKImageFilter? background,
        SKImageFilter? foreground) =>
        CreateArithmetic(k1, k2, k3, k4, enforcePMColor, background, foreground, null);

    public static SKImageFilter CreateArithmetic(
        float k1,
        float k2,
        float k3,
        float k4,
        bool enforcePMColor,
        SKImageFilter? background,
        SKImageFilter? foreground,
        SKRect cropRect) =>
        CreateArithmetic(
            k1,
            k2,
            k3,
            k4,
            enforcePMColor,
            background,
            foreground,
            (SKRect?)cropRect);

    public static SKImageFilter CreateBlendMode(
        SKBlendMode mode,
        SKImageFilter? background) =>
        CreateBlendMode(mode, background, null, null);

    public static SKImageFilter CreateBlendMode(
        SKBlendMode mode,
        SKImageFilter? background,
        SKImageFilter? foreground) =>
        CreateBlendMode(mode, background, foreground, null);

    public static SKImageFilter CreateBlendMode(
        SKBlendMode mode,
        SKImageFilter? background,
        SKImageFilter? foreground,
        SKRect cropRect) =>
        CreateBlendMode(mode, background, foreground, (SKRect?)cropRect);

    public static SKImageFilter CreateBlendMode(
        SKBlender blender,
        SKImageFilter? background) =>
        CreateBlendMode(blender, background, null, null);

    public static SKImageFilter CreateBlendMode(
        SKBlender blender,
        SKImageFilter? background,
        SKImageFilter? foreground) =>
        CreateBlendMode(blender, background, foreground, null);

    public static SKImageFilter CreateBlendMode(
        SKBlender blender,
        SKImageFilter? background,
        SKImageFilter? foreground,
        SKRect cropRect) =>
        CreateBlendMode(blender, background, foreground, (SKRect?)cropRect);

    private static SKImageFilter CreateBlendMode(
        SKBlender blender,
        SKImageFilter? background,
        SKImageFilter? foreground,
        SKRect? cropRect)
    {
        ArgumentNullException.ThrowIfNull(blender);
        return new SKImageFilter(
            FilterKind.BlendMode,
            new BlendModeData(null, blender, background, foreground),
            null,
            cropRect);
    }

    public static SKImageFilter CreateBlur(float sigmaX, float sigmaY) =>
        CreateBlur(sigmaX, sigmaY, (SKImageFilter?)null);

    public static SKImageFilter CreateBlur(
        float sigmaX,
        float sigmaY,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateBlur(sigmaX, sigmaY, SKShaderTileMode.Decal, input, (SKRect?)cropRect);

    public static SKImageFilter CreateBlur(
        float sigmaX,
        float sigmaY,
        SKShaderTileMode tileMode) =>
        CreateBlur(sigmaX, sigmaY, tileMode, null, null);

    public static SKImageFilter CreateBlur(
        float sigmaX,
        float sigmaY,
        SKShaderTileMode tileMode,
        SKImageFilter? input) =>
        CreateBlur(sigmaX, sigmaY, tileMode, input, null);

    public static SKImageFilter CreateBlur(
        float sigmaX,
        float sigmaY,
        SKShaderTileMode tileMode,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateBlur(sigmaX, sigmaY, tileMode, input, (SKRect?)cropRect);

    public static SKImageFilter CreateColorFilter(SKColorFilter cf) =>
        CreateColorFilter(cf, null, null);

    public static SKImageFilter CreateColorFilter(SKColorFilter cf, SKImageFilter? input) =>
        CreateColorFilter(cf, input, null);

    public static SKImageFilter CreateColorFilter(
        SKColorFilter cf,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateColorFilter(cf, input, (SKRect?)cropRect);

    public static SKImageFilter CreateCompose(SKImageFilter outer, SKImageFilter inner)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        return new SKImageFilter(
            FilterKind.Compose,
            new ComposeData(outer, inner),
            null,
            null);
    }

    public static SKImageFilter CreateDilate(float radiusX, float radiusY) =>
        CreateDilate(radiusX, radiusY, null, null);

    public static SKImageFilter CreateDilate(
        float radiusX,
        float radiusY,
        SKImageFilter? input) =>
        CreateDilate(radiusX, radiusY, input, null);

    public static SKImageFilter CreateDilate(
        float radiusX,
        float radiusY,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateDilate(radiusX, radiusY, input, (SKRect?)cropRect);

    public static SKImageFilter CreateDisplacementMapEffect(
        SKColorChannel xChannelSelector,
        SKColorChannel yChannelSelector,
        float scale,
        SKImageFilter displacement) =>
        CreateDisplacementMapEffect(
            xChannelSelector,
            yChannelSelector,
            scale,
            displacement,
            null,
            null);

    public static SKImageFilter CreateDisplacementMapEffect(
        SKColorChannel xChannelSelector,
        SKColorChannel yChannelSelector,
        float scale,
        SKImageFilter displacement,
        SKImageFilter? input) =>
        CreateDisplacementMapEffect(
            xChannelSelector,
            yChannelSelector,
            scale,
            displacement,
            input,
            null);

    public static SKImageFilter CreateDisplacementMapEffect(
        SKColorChannel xChannelSelector,
        SKColorChannel yChannelSelector,
        float scale,
        SKImageFilter displacement,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateDisplacementMapEffect(
            xChannelSelector,
            yChannelSelector,
            scale,
            displacement,
            input,
            (SKRect?)cropRect);

    public static SKImageFilter CreateDistantLitDiffuse(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float kd) =>
        CreateDistantLitDiffuse(direction, lightColor, surfaceScale, kd, null, null);

    public static SKImageFilter CreateDistantLitDiffuse(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input) =>
        CreateDistantLitDiffuse(direction, lightColor, surfaceScale, kd, input, null);

    public static SKImageFilter CreateDistantLitDiffuse(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateDistantLitDiffuse(
            direction,
            lightColor,
            surfaceScale,
            kd,
            input,
            (SKRect?)cropRect);

    public static SKImageFilter CreateDistantLitSpecular(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess) =>
        CreateDistantLitSpecular(
            direction,
            lightColor,
            surfaceScale,
            ks,
            shininess,
            null,
            null);

    public static SKImageFilter CreateDistantLitSpecular(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input) =>
        CreateDistantLitSpecular(
            direction,
            lightColor,
            surfaceScale,
            ks,
            shininess,
            input,
            null);

    public static SKImageFilter CreateDistantLitSpecular(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateDistantLitSpecular(
            direction,
            lightColor,
            surfaceScale,
            ks,
            shininess,
            input,
            (SKRect?)cropRect);

    public static SKImageFilter CreateDropShadow(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateDropShadow(dx, dy, sigmaX, sigmaY, color, input, (SKRect?)cropRect);

    public static SKImageFilter CreateDropShadowOnly(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateDropShadowOnly(dx, dy, sigmaX, sigmaY, color, input, (SKRect?)cropRect);

    public static SKImageFilter CreateErode(float radiusX, float radiusY) =>
        CreateErode(radiusX, radiusY, null, null);

    public static SKImageFilter CreateErode(
        float radiusX,
        float radiusY,
        SKImageFilter? input) =>
        CreateErode(radiusX, radiusY, input, null);

    public static SKImageFilter CreateErode(
        float radiusX,
        float radiusY,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateErode(radiusX, radiusY, input, (SKRect?)cropRect);

    public static SKImageFilter CreateImage(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return CreateImage(image, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    public static SKImageFilter CreateImage(SKImage image, SKSamplingOptions sampling)
    {
        ArgumentNullException.ThrowIfNull(image);
        return CreateImage(
            image,
            new SKRect(0f, 0f, image.Width, image.Height),
            new SKRect(0f, 0f, image.Width, image.Height),
            sampling);
    }

    [Obsolete("Use CreateImage(SKImage, SKRect, SKRect, SKSamplingOptions) instead.", true)]
    public static SKImageFilter CreateImage(
        SKImage image,
        SKRect src,
        SKRect dst,
        SKFilterQuality filterQuality) =>
        CreateImage(image, src, dst, ToSamplingOptions((int)filterQuality));

    public static SKImageFilter CreateMagnifier(
        SKRect lensBounds,
        float zoomAmount,
        float inset,
        SKSamplingOptions sampling) =>
        CreateMagnifierCore(lensBounds, zoomAmount, inset, sampling, null, null);

    public static SKImageFilter CreateMagnifier(
        SKRect lensBounds,
        float zoomAmount,
        float inset,
        SKSamplingOptions sampling,
        SKImageFilter? input) =>
        CreateMagnifierCore(lensBounds, zoomAmount, inset, sampling, input, null);

    public static SKImageFilter CreateMagnifier(
        SKRect lensBounds,
        float zoomAmount,
        float inset,
        SKSamplingOptions sampling,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateMagnifierCore(lensBounds, zoomAmount, inset, sampling, input, cropRect);

    private static SKImageFilter CreateMagnifierCore(
        SKRect lensBounds,
        float zoomAmount,
        float inset,
        SKSamplingOptions sampling,
        SKImageFilter? input,
        SKRect? cropRect)
    {
        if (!IsFiniteNonEmpty(lensBounds) ||
            !float.IsFinite(zoomAmount) ||
            zoomAmount <= 0f ||
            !float.IsFinite(inset) ||
            inset < 0f)
        {
            return null!;
        }

        if (zoomAmount <= 1f && cropRect == null)
        {
            return input!;
        }

        return new SKImageFilter(
            FilterKind.Magnifier,
            new MagnifierData(lensBounds, zoomAmount, inset, sampling),
            input,
            cropRect);
    }

    [Obsolete("Use SetMatrix(in SKMatrix) instead.", true)]
    public static SKImageFilter CreateMatrix(SKMatrix matrix) =>
        CreateMatrix(in matrix);

    [Obsolete("Use SetMatrix(in SKMatrix, SKSamplingOptions, SKImageFilter) instead.", true)]
    public static SKImageFilter CreateMatrix(
        SKMatrix matrix,
        SKFilterQuality quality,
        SKImageFilter? input) =>
        CreateMatrix(in matrix, ToSamplingOptions((int)quality), input);

    public static SKImageFilter CreateMatrix(in SKMatrix matrix) =>
        CreateMatrix(in matrix, SKSamplingOptions.Default, null);

    public static SKImageFilter CreateMatrix(
        in SKMatrix matrix,
        SKSamplingOptions sampling) =>
        CreateMatrix(in matrix, sampling, null);

    public static SKImageFilter CreateMatrix(
        in SKMatrix matrix,
        SKSamplingOptions sampling,
        SKImageFilter? input) =>
        new(
            FilterKind.MatrixTransform,
            new MatrixTransformData(matrix, sampling),
            input,
            null);

    public static SKImageFilter CreateMatrixConvolution(
        SKSizeI kernelSize,
        ReadOnlySpan<float> kernel,
        float gain,
        float bias,
        SKPointI kernelOffset,
        SKShaderTileMode tileMode,
        bool convolveAlpha) =>
        CreateMatrixConvolutionCore(
            kernelSize,
            kernel,
            gain,
            bias,
            kernelOffset,
            tileMode,
            convolveAlpha,
            null,
            null);

    public static SKImageFilter CreateMatrixConvolution(
        SKSizeI kernelSize,
        ReadOnlySpan<float> kernel,
        float gain,
        float bias,
        SKPointI kernelOffset,
        SKShaderTileMode tileMode,
        bool convolveAlpha,
        SKImageFilter? input) =>
        CreateMatrixConvolutionCore(
            kernelSize,
            kernel,
            gain,
            bias,
            kernelOffset,
            tileMode,
            convolveAlpha,
            input,
            null);

    public static SKImageFilter CreateMatrixConvolution(
        SKSizeI kernelSize,
        ReadOnlySpan<float> kernel,
        float gain,
        float bias,
        SKPointI kernelOffset,
        SKShaderTileMode tileMode,
        bool convolveAlpha,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateMatrixConvolutionCore(
            kernelSize,
            kernel,
            gain,
            bias,
            kernelOffset,
            tileMode,
            convolveAlpha,
            input,
            cropRect);

    public static SKImageFilter CreateMerge(SKImageFilter? first, SKImageFilter? second) =>
        CreateMergeCore(first, second, null);

    public static SKImageFilter CreateMerge(
        SKImageFilter? first,
        SKImageFilter? second,
        SKRect cropRect) =>
        CreateMergeCore(first, second, cropRect);

    public static SKImageFilter CreateMerge(ReadOnlySpan<SKImageFilter> filters) =>
        CreateMergeCore(filters, null);

    public static SKImageFilter CreateMerge(
        ReadOnlySpan<SKImageFilter> filters,
        SKRect cropRect) =>
        CreateMergeCore(filters, cropRect);

    public static unsafe SKImageFilter CreateMerge(
        ReadOnlySpan<SKImageFilter> filters,
        SKRect* cropRect) =>
        CreateMergeCore(filters, cropRect == null ? null : *cropRect);

    public static SKImageFilter CreateOffset(float radiusX, float radiusY) =>
        CreateOffset(radiusX, radiusY, null, null);

    public static SKImageFilter CreateOffset(
        float radiusX,
        float radiusY,
        SKImageFilter? input) =>
        CreateOffset(radiusX, radiusY, input, null);

    public static SKImageFilter CreateOffset(
        float radiusX,
        float radiusY,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateOffset(radiusX, radiusY, input, (SKRect?)cropRect);

    [Obsolete("Use CreateShader(SKShader) instead.", true)]
    public static SKImageFilter CreatePaint(SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        return CreateShader(paint.Shader, paint.IsDither);
    }

    [Obsolete("Use CreateShader(SKShader, bool, SKRect) instead.", true)]
    public static SKImageFilter CreatePaint(SKPaint paint, SKRect cropRect)
    {
        ArgumentNullException.ThrowIfNull(paint);
        return CreateShader(paint.Shader, paint.IsDither, cropRect);
    }

    public static SKImageFilter CreatePicture(SKPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return CreatePicture(picture, picture.CullRect);
    }

    public static SKImageFilter CreatePointLitDiffuse(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float kd) =>
        CreatePointLitDiffuse(location, lightColor, surfaceScale, kd, null, null);

    public static SKImageFilter CreatePointLitDiffuse(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input) =>
        CreatePointLitDiffuse(location, lightColor, surfaceScale, kd, input, null);

    public static SKImageFilter CreatePointLitDiffuse(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreatePointLitDiffuse(location, lightColor, surfaceScale, kd, input, (SKRect?)cropRect);

    public static SKImageFilter CreatePointLitSpecular(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess) =>
        CreatePointLitSpecular(location, lightColor, surfaceScale, ks, shininess, null, null);

    public static SKImageFilter CreatePointLitSpecular(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input) =>
        CreatePointLitSpecular(location, lightColor, surfaceScale, ks, shininess, input, null);

    public static SKImageFilter CreatePointLitSpecular(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreatePointLitSpecular(
            location,
            lightColor,
            surfaceScale,
            ks,
            shininess,
            input,
            (SKRect?)cropRect);

    public static SKImageFilter CreateShader(SKShader? shader) =>
        CreateShader(shader, dither: false, null);

    public static SKImageFilter CreateShader(SKShader? shader, bool dither) =>
        CreateShader(shader, dither, null);

    public static SKImageFilter CreateShader(
        SKShader? shader,
        bool dither,
        SKRect cropRect) =>
        CreateShader(shader, dither, (SKRect?)cropRect);

    public static SKImageFilter CreateSpotLitDiffuse(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float kd) =>
        CreateSpotLitDiffuse(
            location,
            target,
            specularExponent,
            cutoffAngle,
            lightColor,
            surfaceScale,
            kd,
            null,
            null);

    public static SKImageFilter CreateSpotLitDiffuse(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input) =>
        CreateSpotLitDiffuse(
            location,
            target,
            specularExponent,
            cutoffAngle,
            lightColor,
            surfaceScale,
            kd,
            input,
            null);

    public static SKImageFilter CreateSpotLitDiffuse(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateSpotLitDiffuse(
            location,
            target,
            specularExponent,
            cutoffAngle,
            lightColor,
            surfaceScale,
            kd,
            input,
            (SKRect?)cropRect);

    public static SKImageFilter CreateSpotLitSpecular(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess) =>
        CreateSpotLitSpecular(
            location,
            target,
            specularExponent,
            cutoffAngle,
            lightColor,
            surfaceScale,
            ks,
            shininess,
            null,
            null);

    public static SKImageFilter CreateSpotLitSpecular(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input) =>
        CreateSpotLitSpecular(
            location,
            target,
            specularExponent,
            cutoffAngle,
            lightColor,
            surfaceScale,
            ks,
            shininess,
            input,
            null);

    public static SKImageFilter CreateSpotLitSpecular(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input,
        SKRect cropRect) =>
        CreateSpotLitSpecular(
            location,
            target,
            specularExponent,
            cutoffAngle,
            lightColor,
            surfaceScale,
            ks,
            shininess,
            input,
            (SKRect?)cropRect);

    public static SKImageFilter CreateTile(SKRect src, SKRect dst) =>
        CreateTile(src, dst, null);

    private static SKSamplingOptions ToSamplingOptions(int quality) => quality switch
    {
        0 => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
        1 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        2 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
        3 => new SKSamplingOptions(SKCubicResampler.Mitchell),
        _ => throw new ArgumentOutOfRangeException(nameof(quality)),
    };

    private static bool IsFiniteNonEmpty(SKRect rect) =>
        float.IsFinite(rect.Left) &&
        float.IsFinite(rect.Top) &&
        float.IsFinite(rect.Right) &&
        float.IsFinite(rect.Bottom) &&
        rect.Right > rect.Left &&
        rect.Bottom > rect.Top;
}
