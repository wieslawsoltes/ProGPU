using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkValueSignatureCompatibilityTests
{
    [Fact]
    public void ValueOperationsExposeNativeParameterNames()
    {
        AssertParameterNames(GetEquals<SKCodecOptions>(), "obj");
        AssertParameterNames(GetEquals<SKCodecFrameInfo>(), "obj");
        AssertParameterNames(GetEquals<SKDocumentPdfMetadata>(), "obj");
        AssertParameterNames(GetEquals<SKDocumentXpsOptions>(), "obj");
        AssertParameterNames(GetEquals<SKJpegEncoderOptions>(), "obj");
        AssertParameterNames(GetEquals<SKPngEncoderOptions>(), "obj");
        AssertParameterNames(GetEquals<SKWebpEncoderOptions>(), "obj");
        AssertParameterNames(GetEquals<SKFontMetrics>(), "obj");
        AssertParameterNames(GetEquals<SKColorSpaceTransferFn>(), "obj");
        AssertParameterNames(GetEquals<SKColorSpaceXyz>(), "obj");
        AssertParameterNames(
            typeof(SKColorSpaceTransferFn).GetMethod(
                nameof(SKColorSpaceTransferFn.Transform),
                [typeof(float)]),
            "x");
        AssertParameterNames(
            typeof(SKColorSpaceXyz).GetMethod(
                nameof(SKColorSpaceXyz.Concat),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(SKColorSpaceXyz), typeof(SKColorSpaceXyz)]),
            "a",
            "b");
    }

    private static MethodInfo? GetEquals<T>() => typeof(T).GetMethod(nameof(Equals), [typeof(T)]);

    private static void AssertParameterNames(MethodBase? method, params string[] expected)
    {
        Assert.NotNull(method);
        Assert.Equal(expected, method!.GetParameters().Select(static parameter => parameter.Name));
    }
}
