using System.Numerics;
using System.Reflection;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkShaderCompatibilityTests
{
    [Fact]
    public void ShaderExposesSkia148FactorySurfaceAndObjectBase()
    {
        Assert.True(typeof(SKObject).IsAssignableFrom(typeof(SKShader)));

        AssertMethod(nameof(SKShader.WithColorFilter), typeof(SKColorFilter));
        AssertMethod(nameof(SKShader.WithLocalMatrix), typeof(SKMatrix));
        AssertMethod(nameof(SKShader.CreateEmpty));
        AssertMethod(nameof(SKShader.CreateBitmap), typeof(SKBitmap));
        AssertMethod(
            nameof(SKShader.CreateBitmap),
            typeof(SKBitmap),
            typeof(SKShaderTileMode),
            typeof(SKShaderTileMode),
            typeof(SKMatrix));
        AssertMethod(
            nameof(SKShader.CreateBlend),
            typeof(SKBlendMode),
            typeof(SKShader),
            typeof(SKShader));
        AssertMethod(
            nameof(SKShader.CreateBlend),
            typeof(SKBlender),
            typeof(SKShader),
            typeof(SKShader));
        AssertMethod(
            nameof(SKShader.CreateCompose),
            typeof(SKShader),
            typeof(SKShader),
            typeof(SKBlendMode));
        AssertMethod(
            nameof(SKShader.CreateColorFilter),
            typeof(SKShader),
            typeof(SKColorFilter));
        AssertMethod(
            nameof(SKShader.CreateLocalMatrix),
            typeof(SKShader),
            typeof(SKMatrix));

        var filterQuality = typeof(SKShader).Assembly.GetType(
            "SkiaSharp.SKFilterQuality",
            throwOnError: true)!;
        var legacyImage = GetMethod(
            nameof(SKShader.CreateImage),
            typeof(SKImage),
            typeof(SKShaderTileMode),
            typeof(SKShaderTileMode),
            filterQuality,
            typeof(SKMatrix));
        var obsolete = legacyImage.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsolete);
        Assert.True(obsolete.IsError);
    }

    [Fact]
    public void ColorFilterAndLocalMatrixWrappersAreImmutableAndOrdered()
    {
        using var source = SKShader.CreateColor(SKColors.Blue);
        using var red = SKColorFilter.CreateBlendMode(SKColors.Red, SKBlendMode.Src);
        using var green = SKColorFilter.CreateBlendMode(SKColors.Green, SKBlendMode.Src);
        using var redShader = source.WithColorFilter(red);
        using var greenShader = redShader.WithColorFilter(green);

        Assert.NotSame(source, redShader);
        Assert.NotSame(redShader, greenShader);
        Assert.Equal(ToVector(SKColors.Blue), Assert.IsType<SolidColorBrush>(source.ToBrush()).Color);
        Assert.Equal(ToVector(SKColors.Red), Assert.IsType<SolidColorBrush>(redShader.ToBrush()).Color);
        Assert.Equal(ToVector(SKColors.Green), Assert.IsType<SolidColorBrush>(greenShader.ToBrush()).Color);

        var baseMatrix = SKMatrix.CreateTranslation(8f, 12f);
        var outerMatrix = SKMatrix.CreateScale(2f, 3f);
        using var gradient = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(100f, 0f),
            [SKColors.Black, SKColors.White],
            null,
            SKShaderTileMode.Clamp,
            baseMatrix);
        using var transformed = gradient.WithLocalMatrix(outerMatrix);

        var originalBrush = Assert.IsType<LinearGradientBrush>(gradient.ToBrush());
        var transformedBrush = Assert.IsType<LinearGradientBrush>(transformed.ToBrush());
        Assert.True(Matrix4x4.Invert(baseMatrix.ToMatrix4x4(), out var inverseBase));
        Assert.True(Matrix4x4.Invert(outerMatrix.ToMatrix4x4(), out var inverseOuter));
        AssertMatrixEqual(inverseBase, originalBrush.CoordinateTransform);
        AssertMatrixEqual(inverseOuter * inverseBase, transformedBrush.CoordinateTransform);
    }

    [Fact]
    public void GradientFactoriesSnapshotInputsAndReturnIndependentBrushStops()
    {
        var colors = new[] { SKColors.Red, SKColors.Blue };
        var positions = new[] { 0.2f, 0.8f };
        using var shader = SKShader.CreateLinearGradient(
            SKPoint.Empty,
            new SKPoint(10f, 0f),
            colors,
            positions,
            SKShaderTileMode.Clamp);

        colors[0] = SKColors.Green;
        positions[0] = 0.6f;
        var first = Assert.IsType<LinearGradientBrush>(shader.ToBrush());
        Assert.Equal(ToVector(SKColors.Red), first.Stops[0].Color);
        Assert.Equal(0.2f, first.Stops[0].Offset);

        first.Stops[0] = new GradientStop(Vector4.Zero, 1f);
        var second = Assert.IsType<LinearGradientBrush>(shader.ToBrush());
        Assert.Equal(ToVector(SKColors.Red), second.Stops[0].Color);
        Assert.Equal(0.2f, second.Stops[0].Offset);

        using var singular = SKShader.CreateLinearGradient(
            SKPoint.Empty,
            new SKPoint(10f, 0f),
            [SKColors.Red, SKColors.Blue],
            null,
            SKShaderTileMode.Clamp,
            SKMatrix.CreateScale(0f, 0f));
        Assert.Equal(Vector4.Zero, Assert.IsType<SolidColorBrush>(singular.ToBrush()).Color);
    }

    [Fact]
    public void ReusedGradientInputsDetectMutationWithoutChangingExistingShaders()
    {
        var colors = new[]
        {
            new SKColorF(1f, 0f, 0f, 1f),
            new SKColorF(0f, 1f, 0f, 1f),
            new SKColorF(0f, 0f, 1f, 1f),
        };
        var positions = new[] { 0f, 0.4f, 1f };
        using var colorSpace = SKColorSpace.CreateSrgb();
        using var before = SKShader.CreateLinearGradient(
            SKPoint.Empty,
            new SKPoint(16f, 0f),
            colors,
            colorSpace,
            positions,
            SKShaderTileMode.Clamp,
            SKMatrix.CreateTranslation(3f, 5f));
        using var shared = SKShader.CreateRadialGradient(
            new SKPoint(8f, 8f),
            8f,
            colors,
            colorSpace,
            positions,
            SKShaderTileMode.Clamp,
            SKMatrix.CreateTranslation(3f, 5f));

        colors[0] = new SKColorF(0.25f, 0.5f, 0.75f, 1f);
        positions[0] = 0.2f;
        using var after = SKShader.CreateSweepGradient(
            new SKPoint(8f, 8f),
            colors,
            colorSpace,
            positions,
            SKShaderTileMode.Clamp,
            0f,
            360f,
            SKMatrix.CreateTranslation(3f, 5f));

        var beforeStops = Assert.IsType<LinearGradientBrush>(before.ToBrush()).Stops;
        var sharedStops = Assert.IsType<RadialGradientBrush>(shared.ToBrush()).Stops;
        var afterStops = Assert.IsType<SweepGradientBrush>(after.ToBrush()).Stops;
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), beforeStops[0].Color);
        Assert.Equal(0f, beforeStops[0].Offset);
        Assert.Equal(beforeStops, sharedStops);
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1f), afterStops[0].Color);
        Assert.Equal(0.2f, afterStops[0].Offset);
    }

    [Fact]
    public void GradientStopOverflowOwnsAllInputValues()
    {
        var colors = new[]
        {
            SKColors.Red,
            SKColors.Green,
            SKColors.Blue,
            SKColors.White,
        };
        var positions = new[] { 0f, 0.25f, 0.75f, 1f };
        using var shader = SKShader.CreateLinearGradient(
            SKPoint.Empty,
            new SKPoint(20f, 0f),
            colors,
            positions,
            SKShaderTileMode.Clamp);

        colors[3] = SKColors.Black;
        positions[3] = 0.9f;

        var first = Assert.IsType<LinearGradientBrush>(shader.ToBrush());
        Assert.Equal(4, first.Stops.Length);
        Assert.Equal(ToVector(SKColors.White), first.Stops[3].Color);
        Assert.Equal(1f, first.Stops[3].Offset);
        first.Stops[3] = default;
        var second = Assert.IsType<LinearGradientBrush>(shader.ToBrush());
        Assert.Equal(ToVector(SKColors.White), second.Stops[3].Color);
        Assert.Equal(1f, second.Stops[3].Offset);
    }

    [Fact]
    public void FloatGradientFactoriesRetainColorSpaceTileModeAndLocalMatrix()
    {
        var colors = new[]
        {
            new SKColorF(1.25f, -0.25f, 0.5f, 1f),
            new SKColorF(0f, 0.75f, 1f, 0.5f),
        };
        var positions = new[] { 0.15f, 0.85f };
        var localMatrix = SKMatrix.CreateTranslation(7f, 11f);
        using var colorspace = SKColorSpace.CreateSrgbLinear();
        using var linear = SKShader.CreateLinearGradient(
            SKPoint.Empty,
            new SKPoint(40f, 0f),
            colors,
            colorspace,
            positions,
            SKShaderTileMode.Repeat,
            localMatrix);
        using var radial = SKShader.CreateRadialGradient(
            new SKPoint(20f, 20f),
            20f,
            colors,
            colorspace,
            positions,
            SKShaderTileMode.Mirror,
            localMatrix);
        using var conical = SKShader.CreateTwoPointConicalGradient(
            new SKPoint(4f, 5f),
            2f,
            new SKPoint(30f, 35f),
            18f,
            colors,
            colorspace,
            positions,
            SKShaderTileMode.Decal,
            localMatrix);
        using var sweep = SKShader.CreateSweepGradient(
            new SKPoint(20f, 20f),
            colors,
            colorspace,
            positions,
            SKShaderTileMode.Clamp,
            -45f,
            405f,
            localMatrix);

        colors[0] = new SKColorF(0f, 1f, 0f, 1f);
        positions[0] = 0.65f;
        Assert.True(Matrix4x4.Invert(localMatrix.ToMatrix4x4(), out var inverse));

        AssertFloatGradient(
            Assert.IsType<LinearGradientBrush>(linear.ToBrush()),
            GradientSpreadMethod.Repeat,
            inverse);
        AssertFloatGradient(
            Assert.IsType<RadialGradientBrush>(radial.ToBrush()),
            GradientSpreadMethod.Reflect,
            inverse);
        AssertFloatGradient(
            Assert.IsType<TwoPointConicalGradientBrush>(conical.ToBrush()),
            GradientSpreadMethod.Decal,
            inverse);
        AssertFloatGradient(
            Assert.IsType<SweepGradientBrush>(sweep.ToBrush()),
            GradientSpreadMethod.Pad,
            inverse);
    }

    [Fact]
    public void ReusedGradientInputsAndTransformsStayBelowNativeWrapperAllocation()
    {
        var colors = new[]
        {
            new SKColorF(1f, 0f, 0f, 1f),
            new SKColorF(0f, 1f, 0f, 1f),
            new SKColorF(0f, 0f, 1f, 1f),
        };
        var positions = new[] { 0f, 0.375f, 1f };
        using var colorSpace = SKColorSpace.CreateSrgb();

        static nint CreateFamily(
            SKColorF[] colors,
            float[] positions,
            SKColorSpace colorSpace,
            SKMatrix localMatrix)
        {
            using var linear = SKShader.CreateLinearGradient(
                SKPoint.Empty,
                new SKPoint(64f, 32f),
                colors,
                colorSpace,
                positions,
                SKShaderTileMode.Mirror,
                localMatrix);
            using var radial = SKShader.CreateRadialGradient(
                new SKPoint(32f, 32f),
                24f,
                colors,
                colorSpace,
                positions,
                SKShaderTileMode.Repeat,
                localMatrix);
            using var sweep = SKShader.CreateSweepGradient(
                new SKPoint(32f, 32f),
                colors,
                colorSpace,
                positions,
                SKShaderTileMode.Clamp,
                -45f,
                315f,
                localMatrix);
            using var conical = SKShader.CreateTwoPointConicalGradient(
                new SKPoint(8f, 8f),
                4f,
                new SKPoint(48f, 40f),
                28f,
                colors,
                colorSpace,
                positions,
                SKShaderTileMode.Decal,
                localMatrix);
            return linear.Handle | radial.Handle | sweep.Handle | conical.Handle;
        }

        for (var index = 0; index < 16; index++)
        {
            _ = CreateFamily(
                colors,
                positions,
                colorSpace,
                SKMatrix.CreateTranslation(index & 3, index & 7));
        }

        const int iterations = 1_000;
        nint checksum = 0;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            checksum |= CreateFamily(
                colors,
                positions,
                colorSpace,
                SKMatrix.CreateTranslation(index & 3, index & 7));
        }

        var allocatedPerFamily =
            (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
        Assert.NotEqual(0, checksum);
        Assert.InRange(allocatedPerFamily, 0, 400);
    }

    [Fact]
    public void ShaderWrappersRetainDisposedSources()
    {
        var source = SKShader.CreateColor(SKColors.Cyan);
        using var wrapped = source.WithLocalMatrix(SKMatrix.CreateTranslation(3f, 4f));

        source.Dispose();

        Assert.Equal(IntPtr.Zero, source.Handle);
        Assert.Equal(ToVector(SKColors.Cyan), Assert.IsType<SolidColorBrush>(wrapped.ToBrush()).Color);
    }

    [Fact]
    public void BlendFactoriesRetainModeArithmeticAndChildren()
    {
        var destination = SKShader.CreateColor(SKColors.Blue);
        var source = SKShader.CreateColor(SKColors.Red);
        using var multiply = SKShader.CreateBlend(SKBlendMode.Multiply, destination, source);
        using var arithmeticBlender = SKBlender.CreateArithmetic(0.1f, 0.2f, 0.3f, 0.4f, enforcePMColor: true);
        Assert.NotNull(arithmeticBlender);
        using var arithmetic = SKShader.CreateBlend(arithmeticBlender!, destination, source);

        destination.Dispose();
        source.Dispose();

        Assert.Equal(SKBlendMode.Multiply, multiply.Composed!.BlendMode);
        Assert.Null(multiply.Composed.Arithmetic);
        var coefficients = arithmetic.Composed!.Arithmetic;
        Assert.NotNull(coefficients);
        Assert.Equal(0.1f, coefficients.Value.K1);
        Assert.Equal(0.2f, coefficients.Value.K2);
        Assert.Equal(0.3f, coefficients.Value.K3);
        Assert.Equal(0.4f, coefficients.Value.K4);
        Assert.True(coefficients.Value.EnforcePremul);
        Assert.Equal(ToVector(SKColors.Blue), Assert.IsType<SolidColorBrush>(multiply.Composed.Destination.ToBrush()).Color);
        Assert.Equal(ToVector(SKColors.Red), Assert.IsType<SolidColorBrush>(multiply.Composed.Source.ToBrush()).Color);
    }

    [Fact]
    public void SpecialShaderClassificationAndCoverageStayInShaderSpace()
    {
        using var color = SKShader.CreateColor(SKColors.Red);
        using var local = color.WithLocalMatrix(SKMatrix.CreateTranslation(3f, 4f));
        using var filter = SKColorFilter.CreateLumaColor();
        using var filtered = local.WithColorFilter(filter);
        using var composed = SKShader.CreateBlend(SKBlendMode.Screen, color, local);

        var hasSpecial = typeof(SKCanvas).GetMethod(
            "HasSpecialShader",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.False(Assert.IsType<bool>(hasSpecial.Invoke(null, [color])));
        Assert.False(Assert.IsType<bool>(hasSpecial.Invoke(null, [local])));
        Assert.True(Assert.IsType<bool>(hasSpecial.Invoke(null, [filtered])));
        Assert.True(Assert.IsType<bool>(hasSpecial.Invoke(null, [composed])));

        using var canvas = new SKCanvas(new ProGPU.Scene.DrawingContext(), 120f, 80f);
        var matrix = SKMatrix.Concat(
            SKMatrix.CreateTranslation(10f, 12f),
            SKMatrix.CreateScale(2f, 4f));
        canvas.SetMatrix(matrix);
        var getCoverage = typeof(SKCanvas).GetMethod(
            "GetShaderLayerCoverageBounds",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var actual = Assert.IsType<SKRect>(getCoverage.Invoke(canvas, null));
        Assert.True(matrix.TryInvert(out var inverse));
        Assert.Equal(inverse.MapRect(new SKRect(0f, 0f, 120f, 80f)), actual);
    }

    [Fact]
    public void SweepAndPerlinOverloadsRetainCompleteState()
    {
        var localMatrix = SKMatrix.CreateTranslation(5f, 7f);
        using var sweep = SKShader.CreateSweepGradient(
            new SKPoint(20f, 30f),
            [SKColors.Red, SKColors.Blue],
            [0.25f, 0.75f],
            SKShaderTileMode.Mirror,
            -30f,
            810f,
            localMatrix);
        var brush = Assert.IsType<SweepGradientBrush>(sweep.ToBrush());
        Assert.Equal(new Vector2(20f, 30f), brush.Center);
        Assert.Equal(-30f, brush.StartAngle);
        Assert.Equal(810f, brush.EndAngle);
        Assert.Equal(GradientSpreadMethod.Reflect, brush.SpreadMethod);
        Assert.Equal(0.25f, brush.Stops[0].Offset);
        Assert.Equal(0.75f, brush.Stops[1].Offset);

        using var clampDegenerate = SKShader.CreateSweepGradient(
            SKPoint.Empty,
            [SKColors.Red, SKColors.Blue],
            null,
            SKShaderTileMode.Clamp,
            45f,
            45f);
        var clampBrush = Assert.IsType<SweepGradientBrush>(clampDegenerate.ToBrush());
        Assert.Equal(45f, clampBrush.StartAngle);
        Assert.Equal(45f, clampBrush.EndAngle);

        using var repeatDegenerate = SKShader.CreateSweepGradient(
            SKPoint.Empty,
            [SKColors.Red, SKColors.Blue],
            null,
            SKShaderTileMode.Repeat,
            45f,
            45f);
        Assert.Equal(
            new Vector4(0.5f, 0f, 0.5f, 1f),
            Assert.IsType<SolidColorBrush>(repeatDegenerate.ToBrush()).Color);

        using var decalDegenerate = SKShader.CreateSweepGradient(
            SKPoint.Empty,
            [SKColors.Red, SKColors.Blue],
            null,
            SKShaderTileMode.Decal,
            45f,
            45f);
        Assert.Equal(Vector4.Zero, Assert.IsType<SolidColorBrush>(decalDegenerate.ToBrush()).Color);

        using var perlin = SKShader.CreatePerlinNoiseTurbulence(
            0.1f,
            0.2f,
            4,
            9f,
            new SKSizeI(64, 32));
        Assert.Equal(new SKPointI(64, 32), perlin.PerlinNoise!.TileSize);

        using var empty = SKShader.CreateEmpty();
        Assert.Equal(Vector4.Zero, Assert.IsType<SolidColorBrush>(empty.ToBrush()).Color);
    }

    private static void AssertMethod(string name, params Type[] parameters) =>
        Assert.NotNull(GetMethod(name, parameters));

    private static MethodInfo GetMethod(string name, params Type[] parameters) =>
        typeof(SKShader).GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly,
            binder: null,
            types: parameters,
            modifiers: null)!;

    private static Vector4 ToVector(SKColor color) => new(
        color.R / 255f,
        color.G / 255f,
        color.B / 255f,
        color.A / 255f);

    private static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.Equal(expected.M11, actual.M11, 5);
        Assert.Equal(expected.M12, actual.M12, 5);
        Assert.Equal(expected.M21, actual.M21, 5);
        Assert.Equal(expected.M22, actual.M22, 5);
        Assert.Equal(expected.M41, actual.M41, 5);
        Assert.Equal(expected.M42, actual.M42, 5);
    }

    private static void AssertFloatGradient(
        Brush brush,
        GradientSpreadMethod expectedSpread,
        Matrix4x4 expectedTransform)
    {
        var (interpolation, spread, transform, stops) = brush switch
        {
            LinearGradientBrush value =>
                (value.ColorInterpolationMode, value.SpreadMethod, value.CoordinateTransform, value.Stops),
            RadialGradientBrush value =>
                (value.ColorInterpolationMode, value.SpreadMethod, value.CoordinateTransform, value.Stops),
            TwoPointConicalGradientBrush value =>
                (value.ColorInterpolationMode, value.SpreadMethod, value.CoordinateTransform, value.Stops),
            SweepGradientBrush value =>
                (value.ColorInterpolationMode, value.SpreadMethod, value.CoordinateTransform, value.Stops),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected gradient brush type {brush.GetType()}.")
        };

        Assert.Equal(GradientColorInterpolationMode.ScRgbLinearInterpolation, interpolation);
        Assert.Equal(expectedSpread, spread);
        AssertMatrixEqual(expectedTransform, transform);
        Assert.Equal(new Vector4(1f, 0f, 0.5f, 1f), stops[0].Color);
        Assert.Equal(0.15f, stops[0].Offset);
        Assert.Equal(new Vector4(0f, 0.75f, 1f, 0.5f), stops[1].Color);
        Assert.Equal(0.85f, stops[1].Offset);
    }
}
