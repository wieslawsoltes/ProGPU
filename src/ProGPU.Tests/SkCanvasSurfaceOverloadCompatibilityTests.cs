using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasSurfaceOverloadCompatibilityTests
{
    [Fact]
    public void CanvasSurfaceOverloadsValidateSurfaceBeforeSnapshot()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        Assert.Throws<ArgumentNullException>(() => canvas.DrawSurface(null!, 1f, 2f));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawSurface(null!, new SKPoint(1f, 2f)));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawSurface(null!, 1f, 2f, sampling));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawSurface(null!, new SKPoint(1f, 2f), sampling));
    }

    [Fact]
    public void SurfaceExposesNativeSamplingDrawOverloads()
    {
        var methods = typeof(SKSurface)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == nameof(SKSurface.Draw))
            .ToArray();

        Assert.Contains(methods, method => HasParameters(
            method,
            typeof(SKCanvas),
            typeof(float),
            typeof(float),
            typeof(SKSamplingOptions),
            typeof(SKPaint)));
        Assert.Contains(methods, method => HasParameters(
            method,
            typeof(SKCanvas),
            typeof(SKPoint),
            typeof(SKSamplingOptions),
            typeof(SKPaint)));
    }

    private static bool HasParameters(MethodInfo method, params Type[] types) =>
        method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(types);
}
