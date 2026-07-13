using System.Reflection;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPictureApiCompatibilityTests
{
    [Fact]
    public void PictureExposesNativeShaderSignatures()
    {
        var overloads = typeof(SKPicture)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static method => method.Name == nameof(SKPicture.ToShader))
            .ToArray();

        Assert.Equal(7, overloads.Length);
        AssertParameterNames(GetToShader(), []);
        AssertParameterNames(
            GetToShader(typeof(SKShaderTileMode), typeof(SKShaderTileMode)),
            "tmx",
            "tmy");
        AssertParameterNames(
            GetToShader(typeof(SKShaderTileMode), typeof(SKShaderTileMode), typeof(SKFilterMode)),
            "tmx",
            "tmy",
            "filterMode");
        AssertParameterNames(
            GetToShader(typeof(SKShaderTileMode), typeof(SKShaderTileMode), typeof(SKRect)),
            "tmx",
            "tmy",
            "tile");
        AssertParameterNames(
            GetToShader(
                typeof(SKShaderTileMode),
                typeof(SKShaderTileMode),
                typeof(SKFilterMode),
                typeof(SKRect)),
            "tmx",
            "tmy",
            "filterMode",
            "tile");
        AssertParameterNames(
            GetToShader(
                typeof(SKShaderTileMode),
                typeof(SKShaderTileMode),
                typeof(SKMatrix),
                typeof(SKRect)),
            "tmx",
            "tmy",
            "localMatrix",
            "tile");
        AssertParameterNames(
            GetToShader(
                typeof(SKShaderTileMode),
                typeof(SKShaderTileMode),
                typeof(SKFilterMode),
                typeof(SKMatrix),
                typeof(SKRect)),
            "tmx",
            "tmy",
            "filterMode",
            "localMatrix",
            "tile");
    }

    [Fact]
    public void PictureReportsIdentityAndNestedOperationCounts()
    {
        using var inner = RecordPicture(
            static canvas =>
            {
                using var paint = new SKPaint();
                canvas.DrawRect(new SKRect(2f, 3f, 10f, 12f), paint);
            });
        using var outer = RecordPicture(
            canvas =>
            {
                canvas.DrawPicture(inner);
                using var paint = new SKPaint();
                canvas.DrawCircle(16f, 16f, 4f, paint);
            });

        Assert.NotEqual(0u, inner.UniqueId);
        Assert.NotEqual(0u, outer.UniqueId);
        Assert.NotEqual(inner.UniqueId, outer.UniqueId);
        Assert.Equal(outer.Picture.Commands.Length, outer.ApproximateOperationCount);
        Assert.Equal(
            outer.Picture.Commands.Length + inner.Picture.Commands.Length,
            outer.GetApproximateOperationCount(includeNested: true));
        Assert.True(inner.ApproximateBytesUsed > 0);
        Assert.True(outer.ApproximateBytesUsed > 0);
    }

    [Fact]
    public void PictureShaderPreservesTileMatrixAndFilterMode()
    {
        using var picture = RecordPicture(
            static canvas =>
            {
                using var paint = new SKPaint { Color = SKColors.Red };
                canvas.DrawRect(new SKRect(0f, 0f, 32f, 32f), paint);
            });
        var tile = new SKRect(2f, 4f, 18f, 20f);
        var matrix = SKMatrix.CreateTranslation(5f, 7f);

        using var defaultShader = picture.ToShader();
        using var nearestShader = picture.ToShader(
            SKShaderTileMode.Repeat,
            SKShaderTileMode.Mirror,
            SKFilterMode.Nearest,
            matrix,
            tile);
        using var linearShader = picture.ToShader(
            SKShaderTileMode.Decal,
            SKShaderTileMode.Clamp,
            SKFilterMode.Linear,
            matrix,
            tile);

        AssertPictureShader(
            defaultShader,
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            SKFilterMode.Nearest,
            SKMatrix.Identity,
            picture.CullRect);
        AssertPictureShader(
            nearestShader,
            SKShaderTileMode.Repeat,
            SKShaderTileMode.Mirror,
            SKFilterMode.Nearest,
            matrix,
            tile);
        AssertPictureShader(
            linearShader,
            SKShaderTileMode.Decal,
            SKShaderTileMode.Clamp,
            SKFilterMode.Linear,
            matrix,
            tile);
    }

    [Theory]
    [InlineData(SKFilterMode.Nearest, TextureSamplingMode.Nearest)]
    [InlineData(SKFilterMode.Linear, TextureSamplingMode.Linear)]
    public void PictureShaderSelectsRequestedGpuSampling(
        SKFilterMode filterMode,
        TextureSamplingMode expectedSampling)
    {
        using var picture = RecordPicture(
            static canvas =>
            {
                using var paint = new SKPaint { Color = SKColors.Blue };
                canvas.DrawRect(new SKRect(0f, 0f, 32f, 32f), paint);
            });
        using var shader = picture.ToShader(
            SKShaderTileMode.Decal,
            SKShaderTileMode.Decal,
            filterMode,
            SKMatrix.Identity,
            picture.CullRect);
        using var paint = new SKPaint { Shader = shader };
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 32f, 32f);

        try
        {
            canvas.DrawRect(picture.CullRect, paint);

            var drawTexture = Assert.Single(
                context.Commands,
                static command => command.Type == RenderCommandType.DrawTexture);
            Assert.Equal(expectedSampling, drawTexture.TextureSamplingMode);
        }
        finally
        {
            context.Clear();
        }
    }

    private static SKPicture RecordPicture(Action<SKCanvas> record)
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 32f, 32f));
        record(canvas);
        return recorder.EndRecording();
    }

    private static MethodInfo? GetToShader(params Type[] parameterTypes) =>
        typeof(SKPicture).GetMethod(nameof(SKPicture.ToShader), parameterTypes);

    private static void AssertParameterNames(MethodBase? method, params string[] expected)
    {
        Assert.NotNull(method);
        Assert.Equal(expected, method!.GetParameters().Select(static parameter => parameter.Name));
    }

    private static void AssertPictureShader(
        SKShader shader,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKFilterMode filterMode,
        SKMatrix localMatrix,
        SKRect tileRect)
    {
        var picture = Assert.IsType<SKShader.PictureShaderData>(shader.Picture);
        Assert.Equal(tileModeX, picture.TileModeX);
        Assert.Equal(tileModeY, picture.TileModeY);
        Assert.Equal(filterMode, picture.FilterMode);
        Assert.Equal(localMatrix, picture.LocalMatrix);
        Assert.Equal(tileRect, picture.TileRect);
    }
}
