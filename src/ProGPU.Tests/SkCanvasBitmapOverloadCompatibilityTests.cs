using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasBitmapOverloadCompatibilityTests
{
    [Fact]
    public void BitmapOverloadsValidateBitmapBeforeImageConversion()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));
        var source = new SKRect(0f, 0f, 1f, 1f);
        var destination = new SKRect(2f, 3f, 7f, 9f);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

#pragma warning disable CS0618
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBitmap(null!, new SKPoint(1f, 2f)));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBitmap(null!, 1f, 2f));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBitmap(null!, destination));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBitmap(null!, source, destination));
#pragma warning restore CS0618
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBitmap(null!, new SKPoint(1f, 2f), sampling));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBitmap(null!, 1f, 2f, sampling));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBitmap(null!, destination, sampling));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBitmap(null!, source, destination, sampling));
    }

    [Fact]
    public void LegacyBitmapOverloadsCarryNativeObsoleteContract()
    {
        var methods = typeof(SKCanvas).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var legacy = methods.Where(method =>
            method.Name == nameof(SKCanvas.DrawBitmap) &&
            method.GetParameters().All(parameter => parameter.ParameterType != typeof(SKSamplingOptions)));
        var sampling = methods.Where(method =>
            method.Name == nameof(SKCanvas.DrawBitmap) &&
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(SKSamplingOptions)));

        Assert.Equal(4, legacy.Count());
        Assert.All(legacy, method => Assert.NotNull(method.GetCustomAttribute<ObsoleteAttribute>()));
        Assert.Equal(4, sampling.Count());
        Assert.All(sampling, method => Assert.Null(method.GetCustomAttribute<ObsoleteAttribute>()));
    }
}
