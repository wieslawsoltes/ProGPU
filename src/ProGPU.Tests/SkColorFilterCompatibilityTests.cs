using System.Reflection;
using System.Runtime.InteropServices;
using ProGPU.Compute;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkColorFilterCompatibilityTests
{
    [Fact]
    public void ColorFilterExposesSkia148SurfaceAndObjectLifetime()
    {
        Assert.True(typeof(SKObject).IsAssignableFrom(typeof(SKColorFilter)));
        Assert.Equal(20, SKColorFilter.ColorMatrixSize);
        Assert.Equal(256, SKColorFilter.TableMaxLength);
        AssertMethod(nameof(SKColorFilter.CreateSrgbToLinearGamma));
        AssertMethod(nameof(SKColorFilter.CreateLinearToSrgbGamma));
        AssertMethod(nameof(SKColorFilter.CreateLighting), typeof(SKColor), typeof(SKColor));
        AssertMethod(nameof(SKColorFilter.CreateCompose), typeof(SKColorFilter), typeof(SKColorFilter));
        AssertMethod(nameof(SKColorFilter.CreateLerp), typeof(float), typeof(SKColorFilter), typeof(SKColorFilter));
        AssertMethod(nameof(SKColorFilter.CreateColorMatrix), typeof(ReadOnlySpan<float>));
        AssertMethod(nameof(SKColorFilter.CreateHslaColorMatrix), typeof(ReadOnlySpan<float>));
        AssertMethod(nameof(SKColorFilter.CreateTable), typeof(ReadOnlySpan<byte>));
        AssertMethod(
            nameof(SKColorFilter.CreateTable),
            typeof(ReadOnlySpan<byte>),
            typeof(ReadOnlySpan<byte>),
            typeof(ReadOnlySpan<byte>),
            typeof(ReadOnlySpan<byte>));
        AssertMethod(nameof(SKColorFilter.CreateHighContrast), typeof(SKHighContrastConfig));
        Assert.Equal(12, Marshal.SizeOf<SKHighContrastConfig>());

        using var filter = SKColorFilter.CreateLumaColor();
        Assert.NotEqual(IntPtr.Zero, filter.Handle);
        filter.Dispose();
        Assert.Equal(IntPtr.Zero, filter.Handle);
    }

    [Fact]
    public void MatrixAndTableFactoriesRequireExactFiniteSnapshots()
    {
        var matrix = IdentityMatrix();
        using var matrixFilter = SKColorFilter.CreateColorMatrix(matrix);
        matrix[0] = 0f;
        Assert.Equal(new SKColor(80, 120, 160, 200), matrixFilter.Apply(new SKColor(80, 120, 160, 200)));

        var identity = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        using var tableFilter = SKColorFilter.CreateTable(identity);
        identity[80] = 0;
        Assert.Equal(new SKColor(80, 120, 160, 200), tableFilter.Apply(new SKColor(80, 120, 160, 200)));

        Assert.Equal("matrix", Assert.Throws<ArgumentException>(
            () => SKColorFilter.CreateColorMatrix(new float[19])).ParamName);
        Assert.Equal("table", Assert.Throws<ArgumentException>(
            () => SKColorFilter.CreateTable(new byte[257])).ParamName);

        matrix = IdentityMatrix();
        matrix[3] = float.NaN;
        Assert.Null(SKColorFilter.CreateColorMatrix(matrix));
        Assert.Null(SKColorFilter.CreateHslaColorMatrix(matrix));
    }

    [Fact]
    public void BlendFactoriesMatchNativeCanonicalNoOpRules()
    {
        Assert.Null(SKColorFilter.CreateBlendMode(SKColors.Red, SKBlendMode.Dst));
        Assert.Null(SKColorFilter.CreateBlendMode(SKColors.Transparent, SKBlendMode.SrcOver));
        Assert.Null(SKColorFilter.CreateBlendMode(SKColors.Red, (SKBlendMode)int.MaxValue));

        using var clear = SKColorFilter.CreateBlendMode(SKColors.Red, SKBlendMode.Clear);
        Assert.NotNull(clear);
        Assert.Equal(SKColor.Empty, clear.Apply(SKColors.Blue));

        using var opaqueSourceOver = SKColorFilter.CreateBlendMode(SKColors.Red, SKBlendMode.SrcOver);
        Assert.NotNull(opaqueSourceOver);
        Assert.Equal(SKColors.Red, opaqueSourceOver.Apply(SKColors.Blue));
    }

    [Fact]
    public void GammaLightingAndLumaFiltersPreserveSkiaChannelSemantics()
    {
        var srgbToLinear = SKColorFilter.CreateSrgbToLinearGamma();
        var linearToSrgb = SKColorFilter.CreateLinearToSrgbGamma();
        Assert.Same(srgbToLinear, SKColorFilter.CreateSrgbToLinearGamma());
        Assert.Same(linearToSrgb, SKColorFilter.CreateLinearToSrgbGamma());
        Assert.Equal(new SKColor(55, 55, 55, 128), srgbToLinear.Apply(new SKColor(128, 128, 128, 128)));
        Assert.Equal(new SKColor(128, 128, 128, 128), linearToSrgb.Apply(new SKColor(55, 55, 55, 128)));

        var singletonHandle = srgbToLinear.Handle;
        srgbToLinear.Dispose();
        Assert.Equal(singletonHandle, srgbToLinear.Handle);

        using var lighting = SKColorFilter.CreateLighting(
            new SKColor(128, 64, 255, 1),
            new SKColor(10, 20, 30, 2));
        Assert.Equal(
            new SKColor(60, 70, 80, 128),
            lighting.Apply(new SKColor(100, 200, 50, 128)));

        using var luma = SKColorFilter.CreateLumaColor();
        Assert.Equal(new SKColor(0, 0, 0, 27), luma.Apply(new SKColor(255, 0, 0, 128)));
    }

    [Fact]
    public void ComposeAndLerpRetainChildrenAndUseNativeOrder()
    {
        var addRed = IdentityMatrix();
        addRed[4] = 0.2f;
        var swapRedBlue = new float[]
        {
            0f, 0f, 1f, 0f, 0f,
            0f, 1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
        };
        using var inner = SKColorFilter.CreateColorMatrix(addRed);
        using var outer = SKColorFilter.CreateColorMatrix(swapRedBlue);
        using var composed = SKColorFilter.CreateCompose(outer, inner);

        inner.Dispose();
        outer.Dispose();
        Assert.Equal(new SKColor(0, 0, 51, 255), composed.Apply(SKColors.Black));

        using var red = SKColorFilter.CreateBlendMode(SKColors.Red, SKBlendMode.Src);
        using var blue = SKColorFilter.CreateBlendMode(SKColors.Blue, SKBlendMode.Src);
        using var lerp = SKColorFilter.CreateLerp(0.25f, red, blue);
        Assert.Equal(new SKColor(191, 0, 64, 255), lerp.Apply(SKColors.Transparent));
        Assert.Same(red, SKColorFilter.CreateLerp(-1f, red, blue));
        Assert.Same(blue, SKColorFilter.CreateLerp(2f, red, blue));
        Assert.Null(SKColorFilter.CreateLerp(float.NaN, red, blue));
    }

    [Fact]
    public void HslaAndHighContrastFiltersMatchDocumentedColorDomains()
    {
        var hueRotation = IdentityMatrix();
        hueRotation[4] = 1f / 3f;
        using var hsla = SKColorFilter.CreateHslaColorMatrix(hueRotation);
        Assert.Equal(SKColors.Lime, hsla.Apply(SKColors.Red));

        var config = new SKHighContrastConfig(
            grayscale: false,
            SKHighContrastConfigInvertStyle.InvertBrightness,
            contrast: 0f);
        Assert.True(config.IsValid);
        Assert.Equal(config, new SKHighContrastConfig(false, SKHighContrastConfigInvertStyle.InvertBrightness, 0f));
        using var invert = SKColorFilter.CreateHighContrast(config);
        Assert.Equal(SKColors.White, invert.Apply(SKColors.Black));
        Assert.Equal(SKColors.Black, invert.Apply(SKColors.White));

        using var grayscale = SKColorFilter.CreateHighContrast(
            true,
            SKHighContrastConfigInvertStyle.NoInvert,
            0f);
        var gray = grayscale.Apply(new SKColor(255, 0, 0, 123));
        Assert.Equal(gray.R, gray.G);
        Assert.Equal(gray.G, gray.B);
        Assert.Equal((byte)123, gray.A);

        Assert.False(new SKHighContrastConfig(false, SKHighContrastConfigInvertStyle.NoInvert, 1.01f).IsValid);
        Assert.False(new SKHighContrastConfig(false, (SKHighContrastConfigInvertStyle)99, 0f).IsValid);
        Assert.Null(SKColorFilter.CreateHighContrast(false, SKHighContrastConfigInvertStyle.NoInvert, float.NaN));
    }

    [Fact]
    public void NonlinearGpuParametersHaveStableUniformLayoutAndLazyResource()
    {
        Assert.Equal(96, Marshal.SizeOf<ComputeAccelerator.NonlinearColorFilterParams>());
        var parameters = new ComputeAccelerator.NonlinearColorFilterParams(
            IdentityMatrix(),
            hsla: true,
            grayscale: false,
            invertStyle: 0u,
            contrast: 0f);
        Assert.Equal(0f, parameters.Configuration.X);
        Assert.Equal(1f, parameters.MatrixRed.X);
        Assert.Contains("fn apply_hsla_matrix", ComputeShaders.NonlinearColorFilter, StringComparison.Ordinal);
        Assert.Contains("fn apply_high_contrast", ComputeShaders.NonlinearColorFilter, StringComparison.Ordinal);
        Assert.Contains("// Time complexity:", ComputeShaders.NonlinearColorFilter, StringComparison.Ordinal);
    }

    private static float[] IdentityMatrix() =>
    [
        1f, 0f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    private static void AssertMethod(string name, params Type[] parameterTypes)
    {
        MethodInfo? method = typeof(SKColorFilter).GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        Assert.Equal(typeof(SKColorFilter), method.ReturnType);
    }
}
