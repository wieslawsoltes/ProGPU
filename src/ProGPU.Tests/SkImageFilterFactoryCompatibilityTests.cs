using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Compute;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkImageFilterFactoryCompatibilityTests
{
    [Fact]
    public void ImageFilterUsesSkObjectLifetime()
    {
        using var filter = SKImageFilter.CreateBlur(1f, 2f);
        Assert.IsAssignableFrom<SKObject>(filter);
        Assert.NotEqual(IntPtr.Zero, filter.Handle);

        filter.Dispose();

        Assert.Equal(IntPtr.Zero, filter.Handle);
    }

    [Fact]
    public void BlurOverloadsDefaultToDecalAndPreserveGraphState()
    {
        using var input = SKImageFilter.CreateOffset(2f, 3f);
        var crop = new SKRect(4f, 5f, 20f, 30f);
        using var defaultBlur = SKImageFilter.CreateBlur(1f, 2f);
        using var inputBlur = SKImageFilter.CreateBlur(1f, 2f, input, crop);
        using var tiledBlur = SKImageFilter.CreateBlur(
            3f,
            4f,
            SKShaderTileMode.Mirror,
            input,
            crop);

        Assert.Equal(SKShaderTileMode.Decal, Assert.IsType<SKImageFilter.BlurData>(defaultBlur.Parameters).TileMode);
        Assert.Same(input, inputBlur.Input);
        Assert.Equal(crop, inputBlur.CropRect);
        Assert.Equal(SKShaderTileMode.Mirror, Assert.IsType<SKImageFilter.BlurData>(tiledBlur.Parameters).TileMode);
    }

    [Fact]
    public void EmptyAndCropFactoriesRetainTransparentAndInputGraphState()
    {
        var crop = new SKRect(3f, 4f, 20f, 30f);
        using var input = SKImageFilter.CreateBlur(1f, 2f);
        using var empty = SKImageFilter.CreateEmpty();
        using var cropped = SKImageFilter.CreateCrop(
            crop,
            SKShaderTileMode.Decal,
            input);

        Assert.Equal(SKImageFilter.FilterKind.Shader, empty.Kind);
        Assert.Null(
            Assert.IsType<SKImageFilter.ShaderData>(empty.Parameters)
                .Shader);
        Assert.Equal(SKImageFilter.FilterKind.Offset, cropped.Kind);
        Assert.Same(input, cropped.Input);
        Assert.Equal(crop, cropped.CropRect);
    }

    [Fact]
    public void ArithmeticAndBlenderFactoriesRetainNullableSourceFallbacks()
    {
        var crop = new SKRect(1f, 2f, 30f, 40f);
        using var arithmetic = SKImageFilter.CreateArithmetic(
            .1f,
            .2f,
            .3f,
            .4f,
            true,
            background: null,
            foreground: null,
            crop);
        using var blender = SKBlender.CreateArithmetic(.5f, .6f, .7f, .8f, false);
        using var blended = SKImageFilter.CreateBlendMode(blender!, null, null, crop);

        var arithmeticData = Assert.IsType<SKImageFilter.ArithmeticData>(arithmetic.Parameters);
        Assert.Null(arithmeticData.Background);
        Assert.Null(arithmeticData.Foreground);
        Assert.Equal(crop, arithmetic.CropRect);

        var blendData = Assert.IsType<SKImageFilter.BlendModeData>(blended.Parameters);
        Assert.Same(blender, blendData.Blender);
        Assert.Null(blendData.Mode);
        Assert.Null(blendData.Background);
        Assert.Null(blendData.Foreground);
    }

    [Fact]
    public void MatrixConvolutionSpanRequiresExactKernelAndCopiesInput()
    {
        var kernel = new[] { 1f, 2f, 3f, 4f };
        using var filter = SKImageFilter.CreateMatrixConvolution(
            new SKSizeI(2, 2),
            kernel.AsSpan(),
            0.5f,
            0.25f,
            new SKPointI(1, 1),
            SKShaderTileMode.Clamp,
            true);

        kernel[0] = 99f;
        var data = Assert.IsType<SKImageFilter.MatrixConvolutionData>(filter.Parameters);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, data.Kernel);

        var error = Assert.Throws<ArgumentException>(() =>
            SKImageFilter.CreateMatrixConvolution(
                new SKSizeI(2, 2),
                new[] { 1f, 2f, 3f }.AsSpan(),
                1f,
                0f,
                SKPointI.Empty,
                SKShaderTileMode.Decal,
                false));
        Assert.Equal("kernel", error.ParamName);
    }

    [Fact]
    public void MergeOverloadsCopyFiltersAndRetainNullSourceEntries()
    {
        using var first = SKImageFilter.CreateOffset(1f, 2f);
        using var second = SKImageFilter.CreateBlur(3f, 4f);
        var filters = new[] { first, second };
        using var spanMerge = SKImageFilter.CreateMerge(filters.AsSpan());
        using var pairMerge = SKImageFilter.CreateMerge(first: null, second);

        filters[0] = second;
        var copied = Assert.IsType<SKImageFilter?[]>(spanMerge.Parameters);
        Assert.Same(first, copied[0]);
        Assert.Same(second, copied[1]);

        var pair = Assert.IsType<SKImageFilter?[]>(pairMerge.Parameters);
        Assert.Null(pair[0]);
        Assert.Same(second, pair[1]);
    }

    [Fact]
    public void ShaderAndPictureFactoriesPreserveSourceGeneratingState()
    {
        using var emptyShader = SKImageFilter.CreateShader(shader: null);
        Assert.Null(Assert.IsType<SKImageFilter.ShaderData>(emptyShader.Parameters).Shader);

        using var recorder = new SKPictureRecorder();
        _ = recorder.BeginRecording(new SKRect(3f, 4f, 20f, 30f));
        using var picture = recorder.EndRecording();
        using var pictureFilter = SKImageFilter.CreatePicture(picture);
        var pictureData = Assert.IsType<SKImageFilter.PictureData>(pictureFilter.Parameters);
        Assert.Same(picture, pictureData.Picture);
        Assert.Equal(picture.CullRect, pictureData.TargetRect);
    }

    [Fact]
    public void FactoriesMatchNativeNullValidation()
    {
        Assert.Equal(
            "cf",
            Assert.Throws<ArgumentNullException>(
                () => SKImageFilter.CreateColorFilter(null!)).ParamName);
        Assert.Equal(
            "displacement",
            Assert.Throws<ArgumentNullException>(
                () => SKImageFilter.CreateDisplacementMapEffect(
                    SKColorChannel.R,
                    SKColorChannel.G,
                    1f,
                    null!)).ParamName);
        Assert.Equal(
            "image",
            Assert.Throws<ArgumentNullException>(
                () => SKImageFilter.CreateImage(null!)).ParamName);
        Assert.Equal(
            "picture",
            Assert.Throws<ArgumentNullException>(
                () => SKImageFilter.CreatePicture(null!)).ParamName);
        Assert.Equal(
            "blender",
            Assert.Throws<ArgumentNullException>(
                () => SKImageFilter.CreateBlendMode(null!, null)).ParamName);
        Assert.Equal(
            "input",
            Assert.Throws<ArgumentNullException>(
                () => SKImageFilter.CreateTile(SKRect.Empty, SKRect.Empty)).ParamName);
    }

    [Fact]
    public void ExistingGpuGraphFamiliesExposeExactCropOverloads()
    {
        var crop = new SKRect(1f, 2f, 30f, 40f);
        using var input = SKImageFilter.CreateOffset(1f, 2f);
        using var colorFilter = SKColorFilter.CreateBlendMode(SKColors.Red, SKBlendMode.Src);
        using var displacement = SKImageFilter.CreateShader(null);
        using var a = SKImageFilter.CreateColorFilter(colorFilter, input, crop);
        using var b = SKImageFilter.CreateDilate(1f, 2f, input, crop);
        using var c = SKImageFilter.CreateErode(1f, 2f, input, crop);
        using var d = SKImageFilter.CreateDisplacementMapEffect(
            SKColorChannel.R,
            SKColorChannel.G,
            3f,
            displacement,
            input,
            crop);
        using var e = SKImageFilter.CreateDropShadow(1f, 2f, 3f, 4f, SKColors.Black, input, crop);
        using var f = SKImageFilter.CreateDropShadowOnly(1f, 2f, 3f, 4f, SKColors.Black, input, crop);
        using var g = SKImageFilter.CreateDistantLitDiffuse(new SKPoint3(1f, 2f, 3f), SKColors.White, 1f, 2f, input, crop);
        using var h = SKImageFilter.CreateDistantLitSpecular(new SKPoint3(1f, 2f, 3f), SKColors.White, 1f, 2f, 3f, input, crop);
        using var i = SKImageFilter.CreatePointLitDiffuse(new SKPoint3(1f, 2f, 3f), SKColors.White, 1f, 2f, input, crop);
        using var j = SKImageFilter.CreatePointLitSpecular(new SKPoint3(1f, 2f, 3f), SKColors.White, 1f, 2f, 3f, input, crop);
        using var k = SKImageFilter.CreateSpotLitDiffuse(new SKPoint3(1f, 2f, 3f), new SKPoint3(4f, 5f, 6f), 1f, 45f, SKColors.White, 2f, 3f, input, crop);
        using var l = SKImageFilter.CreateSpotLitSpecular(new SKPoint3(1f, 2f, 3f), new SKPoint3(4f, 5f, 6f), 1f, 45f, SKColors.White, 2f, 3f, 4f, input, crop);
        using var m = SKImageFilter.CreateOffset(1f, 2f, input, crop);

        foreach (var filter in new[] { a, b, c, d, e, f, g, h, i, j, k, l, m })
        {
            Assert.Equal(crop, filter.CropRect);
        }
    }

    [Fact]
    public void ComposeRequiresBothChildrenAndRetainsEvaluationOrder()
    {
        using var outer = SKImageFilter.CreateBlur(1f, 2f);
        using var inner = SKImageFilter.CreateOffset(3f, 4f);
        using var compose = SKImageFilter.CreateCompose(outer, inner);

        var data = Assert.IsType<SKImageFilter.ComposeData>(compose.Parameters);
        Assert.Same(outer, data.Outer);
        Assert.Same(inner, data.Inner);
        Assert.Equal(
            "outer",
            Assert.Throws<ArgumentNullException>(
                () => SKImageFilter.CreateCompose(null!, inner)).ParamName);
        Assert.Equal(
            "inner",
            Assert.Throws<ArgumentNullException>(
                () => SKImageFilter.CreateCompose(outer, null!)).ParamName);
    }

    [Fact]
    public void MatrixFactoriesRetainTransformSamplingAndSourceFallback()
    {
        var matrix = SKMatrix.CreateScaleTranslation(2f, 3f, 4f, 5f);
        var sampling = new SKSamplingOptions(SKCubicResampler.CatmullRom);
        using var input = SKImageFilter.CreateBlur(1f, 1f);
        using var filter = SKImageFilter.CreateMatrix(in matrix, sampling, input);

        var data = Assert.IsType<SKImageFilter.MatrixTransformData>(filter.Parameters);
        Assert.Equal(matrix, data.Matrix);
        Assert.Equal(sampling, data.Sampling);
        Assert.Same(input, filter.Input);
    }

    [Fact]
    public void MatrixFilterConjugatesLocalTransformIntoLayerSpace()
    {
        var local = SKMatrix.CreateTranslation(3f, 4f);
        var layer = Matrix4x4.CreateScale(2f, 5f, 1f);

        Assert.True(SKCanvas.TryCreateImageFilterDeviceTransform(local, layer, out var device));
        Assert.Equal(6f, device.M41, 4);
        Assert.Equal(20f, device.M42, 4);
        Assert.Equal(1f, device.M11, 4);
        Assert.Equal(1f, device.M22, 4);

        Assert.False(SKCanvas.TryCreateImageFilterDeviceTransform(
            local,
            Matrix4x4.CreateScale(0f, 1f, 1f),
            out _));
    }

    [Fact]
    public void MagnifierMatchesNativeValidationAndIdentityFactories()
    {
        var lens = new SKRect(2f, 3f, 20f, 30f);
        var crop = new SKRect(4f, 5f, 18f, 24f);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var input = SKImageFilter.CreateOffset(1f, 2f);

        Assert.Null(SKImageFilter.CreateMagnifier(SKRect.Empty, 2f, 1f, sampling));
        Assert.Null(SKImageFilter.CreateMagnifier(lens, 0f, 1f, sampling));
        Assert.Null(SKImageFilter.CreateMagnifier(lens, float.NaN, 1f, sampling));
        Assert.Null(SKImageFilter.CreateMagnifier(lens, 2f, -1f, sampling));
        Assert.Null(SKImageFilter.CreateMagnifier(lens, 1f, 1f, sampling));
        Assert.Same(input, SKImageFilter.CreateMagnifier(lens, 1f, 1f, sampling, input));

        using var croppedIdentity = SKImageFilter.CreateMagnifier(
            lens,
            1f,
            1f,
            sampling,
            input,
            crop);
        using var magnifier = SKImageFilter.CreateMagnifier(
            lens,
            3f,
            2f,
            sampling,
            input,
            crop);

        Assert.Equal(crop, croppedIdentity.CropRect);
        var data = Assert.IsType<SKImageFilter.MagnifierData>(magnifier.Parameters);
        Assert.Equal(lens, data.LensBounds);
        Assert.Equal(3f, data.ZoomAmount);
        Assert.Equal(2f, data.Inset);
        Assert.Equal(sampling, data.Sampling);
        Assert.Same(input, magnifier.Input);
        Assert.Equal(crop, magnifier.CropRect);
    }

    [Fact]
    public void MagnifierUniformsMatchWgslLayoutAndNormalizeInvalidValues()
    {
        var parameters = new ComputeAccelerator.MagnifierParams(
            new Vector4(1f, 2f, 3f, 4f),
            new Vector4(5f, 6f, 7f, 8f),
            new Vector4(9f, 10f, 11f, 12f),
            new Vector2(13f, 14f),
            samplingMode: 1u,
            new Vector2(1f / 3f, 1f / 3f));

        Assert.Equal(80, Marshal.SizeOf<ComputeAccelerator.MagnifierParams>());
        Assert.Equal(new Vector4(5f, 6f, 7f, 8f), parameters.OutputBounds);
        Assert.Equal(new Vector2(13f, 14f), parameters.InverseInset);
        Assert.Equal(1u, parameters.SamplingMode);

        var invalid = new ComputeAccelerator.MagnifierParams(
            new Vector4(float.NaN),
            new Vector4(float.PositiveInfinity),
            new Vector4(float.NegativeInfinity),
            new Vector2(float.NaN, -1f),
            samplingMode: 99u,
            new Vector2(float.NaN));
        Assert.Equal(Vector4.Zero, invalid.LensBounds);
        Assert.Equal(Vector4.Zero, invalid.OutputBounds);
        Assert.Equal(Vector4.Zero, invalid.ZoomTransform);
        Assert.Equal(Vector2.Zero, invalid.InverseInset);
        Assert.Equal(2u, invalid.SamplingMode);
        Assert.Equal(Vector2.Zero, invalid.Cubic);
    }

    [Fact]
    public void MagnifierZoomTransformFitsVisibleSourceInsideCroppedInput()
    {
        var lens = new SKRect(0f, 0f, 100f, 100f);
        var available = new SKRect(40f, 40f, 100f, 100f);

        var transform = SKCanvas.CreateMagnifierZoomTransform(
            lens,
            lens,
            available,
            zoomAmount: 2f);

        Assert.Equal(new Vector4(40f, 40f, 0.5f, 0.5f), transform);
        Assert.InRange(transform.X + transform.Z * lens.Left, available.Left, available.Right);
        Assert.InRange(transform.Y + transform.W * lens.Top, available.Top, available.Bottom);
        Assert.InRange(transform.X + transform.Z * lens.Right, available.Left, available.Right);
        Assert.InRange(transform.Y + transform.W * lens.Bottom, available.Top, available.Bottom);
    }
}
