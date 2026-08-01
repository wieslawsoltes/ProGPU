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
        AssertParameterNames(GetEquals<SKColorF>(), "obj");
        AssertParameterNames(GetEquals<SKCubicResampler>(), "obj");
        AssertParameterNames(GetEquals<SKRotationScaleMatrix>(), "obj");
        AssertParameterNames(GetEquals<SKSamplingOptions>(), "obj");
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
        AssertStreamReadParameter<sbyte>(nameof(SKStream.ReadSByte));
        AssertStreamReadParameter<short>(nameof(SKStream.ReadInt16));
        AssertStreamReadParameter<int>(nameof(SKStream.ReadInt32));
        AssertStreamReadParameter<byte>(nameof(SKStream.ReadByte));
        AssertStreamReadParameter<ushort>(nameof(SKStream.ReadUInt16));
        AssertStreamReadParameter<uint>(nameof(SKStream.ReadUInt32));
        AssertStreamReadParameter<bool>(nameof(SKStream.ReadBool));
    }

    private static MethodInfo? GetEquals<T>() => typeof(T).GetMethod(nameof(Equals), [typeof(T)]);

    private static void AssertStreamReadParameter<T>(string name) =>
        AssertParameterNames(typeof(SKStream).GetMethod(name, [typeof(T).MakeByRefType()]), "buffer");

    private static void AssertParameterNames(MethodBase? method, params string[] expected)
    {
        Assert.NotNull(method);
        Assert.Equal(expected, method!.GetParameters().Select(static parameter => parameter.Name));
    }
}
