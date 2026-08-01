using SkiaSharp;
using System.Buffers.Binary;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkColorSpaceCompatibilityTests
{
    [Fact]
    public void XyzConstructorsValuesAndIndexerMatchNative()
    {
        Assert.Equal(Enumerable.Repeat(2f, 9), new SKColorSpaceXyz(2f).Values);
        var matrix = new SKColorSpaceXyz(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f }, matrix.Values);
        Assert.Equal(1f, matrix[0, 0]);
        Assert.Equal(3f, matrix[2, 0]);
        Assert.Equal(7f, matrix[0, 2]);
        Assert.Equal(9f, matrix[2, 2]);
    }

    [Fact]
    public void XyzNamedMatricesMatchNative()
    {
        Assert.Equal(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f }, SKColorSpaceXyz.Identity.Values);
        Assert.Equal(SKColorSpaceXyz.Identity, SKColorSpaceXyz.Xyz);
        Assert.Equal(0.43606567f, SKColorSpaceXyz.Srgb[0, 0]);
        Assert.Equal(0.6097412f, SKColorSpaceXyz.AdobeRgb[0, 0]);
        Assert.Equal(-0.00104941f, SKColorSpaceXyz.DisplayP3[0, 2]);
        Assert.Equal(0.797162f, SKColorSpaceXyz.Rec2020[2, 2]);
    }

    [Fact]
    public void XyzValuesAreCopiedAndMutableThroughSetter()
    {
        var source = new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f };
        var matrix = new SKColorSpaceXyz(source);
        source[0] = 99f;
        Assert.Equal(1f, matrix[0, 0]);
        var snapshot = matrix.Values;
        snapshot[0] = 88f;
        Assert.Equal(1f, matrix[0, 0]);
        matrix.Values = source;
        Assert.Equal(99f, matrix[0, 0]);
    }

    [Fact]
    public void XyzValidationMatchesNativeExceptions()
    {
        Assert.Equal("values", Assert.Throws<ArgumentNullException>(() => new SKColorSpaceXyz((float[])null!)).ParamName);
        Assert.Equal("values", Assert.Throws<ArgumentException>(() => new SKColorSpaceXyz(new float[8])).ParamName);
        Assert.Throws<NullReferenceException>(() => { var matrix = default(SKColorSpaceXyz); matrix.Values = null!; });
        var value = SKColorSpaceXyz.Identity;
        Assert.Equal("x", Assert.Throws<ArgumentOutOfRangeException>(() => _ = value[-1, 0]).ParamName);
        Assert.Equal("y", Assert.Throws<ArgumentOutOfRangeException>(() => _ = value[0, 3]).ParamName);
    }

    [Fact]
    public void XyzInversionMatchesNative()
    {
        Assert.Equal(
            new[] { -24f, 18f, 5f, 20f, -15f, -4f, -5f, 4f, 1f },
            new SKColorSpaceXyz(1f, 2f, 3f, 0f, 1f, 4f, 5f, 6f, 0f).Invert().Values);
        Assert.Equal(
            new[] { 0.5f, 0f, 0f, 0f, 0.25f, 0f, 0f, -0f, 0.2f },
            new SKColorSpaceXyz(2f, 0f, 0f, 0f, 4f, 0f, 0f, 0f, 5f).Invert().Values);
        Assert.Equal(
            SKColorSpaceXyz.Empty,
            new SKColorSpaceXyz(1f, 2f, 3f, 2f, 4f, 6f, 3f, 6f, 9f).Invert());
    }

    [Fact]
    public void XyzConcatOrderMatchesNative()
    {
        var left = new SKColorSpaceXyz(1f, 2f, 0f, 0f, 1f, 3f, 0f, 0f, 1f);
        var right = new SKColorSpaceXyz(2f, 0f, 0f, 0f, 4f, 0f, 0f, 0f, 5f);
        Assert.Equal(new[] { 2f, 8f, 0f, 0f, 4f, 15f, 0f, 0f, 5f }, SKColorSpaceXyz.Concat(left, right).Values);
        Assert.Equal(new[] { 2f, 4f, 0f, 0f, 4f, 12f, 0f, 0f, 5f }, SKColorSpaceXyz.Concat(right, left).Values);
    }

    [Fact]
    public void XyzEqualityAndHashUseEveryScalar()
    {
        var value = new SKColorSpaceXyz(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f);
        var equal = new SKColorSpaceXyz(value.Values);
        Assert.True(value == equal);
        Assert.False(value != equal);
        Assert.Equal(value.GetHashCode(), equal.GetHashCode());
        var changed = equal;
        changed.Values = new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 10f };
        Assert.NotEqual(value, changed);
    }

    [Fact]
    public void TransferConstructorsValuesAndPropertiesMatchNative()
    {
        var value = new SKColorSpaceTransferFn(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f });
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f }, value.Values);
        value.G = 7f;
        value.A = 6f;
        value.B = 5f;
        value.C = 4f;
        value.D = 3f;
        value.E = 2f;
        value.F = 1f;
        Assert.Equal(new[] { 7f, 6f, 5f, 4f, 3f, 2f, 1f }, value.Values);
    }

    [Fact]
    public void TransferNamedValuesMatchNative()
    {
        Assert.Equal(new[] { 2.4f, 0.9478673f, 0.0521327f, 0.07739938f, 0.04045f, 0f, 0f }, SKColorSpaceTransferFn.Srgb.Values);
        Assert.Equal(new[] { 2.2f, 1f, 0f, 0f, 0f, 0f, 0f }, SKColorSpaceTransferFn.TwoDotTwo.Values);
        Assert.Equal(new[] { 1f, 1f, 0f, 0f, 0f, 0f, 0f }, SKColorSpaceTransferFn.Linear.Values);
        Assert.Equal(new[] { 2.22222f, 0.909672f, 0.0903276f, 0.222222f, 0.0812429f, 0f, 0f }, SKColorSpaceTransferFn.Rec2020.Values);
        Assert.Equal(new[] { -5f, 203f, 0f, 0f, 0f, 0f, 0f }, SKColorSpaceTransferFn.Pq.Values);
        Assert.Equal(new[] { -6f, 203f, 1000f, 1.2f, 0f, 0f, 0f }, SKColorSpaceTransferFn.Hlg.Values);
    }

    [Fact]
    public void TransferEvaluationMatchesNativeFastMath()
    {
        Assert.Equal(0.21399307f, SKColorSpaceTransferFn.Srgb.Transform(0.5f));
        Assert.Equal(-0.21399307f, SKColorSpaceTransferFn.Srgb.Transform(-0.5f));
        Assert.Equal(0.5000076f, SKColorSpaceTransferFn.Linear.Transform(0.5f));
        Assert.Equal(0.2596531f, SKColorSpaceTransferFn.Rec2020.Transform(0.5f));
        Assert.Equal(0f, SKColorSpaceTransferFn.Srgb.Transform(float.NaN));
        Assert.Equal(float.PositiveInfinity, SKColorSpaceTransferFn.Srgb.Transform(float.PositiveInfinity));
    }

    [Fact]
    public void TransferHdrEvaluationMatchesNative()
    {
        Assert.Equal(0.00922668f, SKColorSpaceTransferFn.Pq.Transform(0.5f));
        Assert.Equal(1f, SKColorSpaceTransferFn.Pq.Transform(-1f));
        Assert.Equal(0.083333336f, SKColorSpaceTransferFn.Hlg.Transform(0.5f));
        Assert.Equal(-0.083333336f, SKColorSpaceTransferFn.Hlg.Transform(-0.5f));
        Assert.Equal(0.02372241f, SKColorSpaceTransferFn.Hlg.Transform(float.NaN));
    }

    [Fact]
    public void TransferInversionMatchesNativeCoefficients()
    {
        Assert.Equal(
            new[] { 0.41666666f, 1.1372833f, -0f, 12.92f, 0.003130805f, -0.054969788f, -0f },
            SKColorSpaceTransferFn.Srgb.Invert().Values);
        Assert.Equal(new[] { 0.45454544f, 1f, -0f, 0f, 0f, 0f, 0f }, SKColorSpaceTransferFn.TwoDotTwo.Invert().Values);
        Assert.Equal(new[] { 1f, 1f, -0f, 0f, 0f, 0f, 0f }, SKColorSpaceTransferFn.Linear.Invert().Values);
        Assert.Equal(
            new[] { 0.45000046f, 1.2343903f, -0f, 4.5000043f, 0.018053958f, -0.09931946f, -0f },
            SKColorSpaceTransferFn.Rec2020.Invert().Values);
    }

    [Fact]
    public void TransferInvalidInversionReturnsEmpty()
    {
        Assert.Equal(SKColorSpaceTransferFn.Empty, SKColorSpaceTransferFn.Empty.Invert());
        Assert.Equal(SKColorSpaceTransferFn.Empty, SKColorSpaceTransferFn.Pq.Invert());
        Assert.Equal(SKColorSpaceTransferFn.Empty, SKColorSpaceTransferFn.Hlg.Invert());
        Assert.Equal(
            SKColorSpaceTransferFn.Empty,
            new SKColorSpaceTransferFn(2f, 3f, 4f, 5f, 6f, 7f, 8f).Invert());
    }

    [Fact]
    public void TransferValidationEqualityAndHashMatchNative()
    {
        Assert.Equal(
            "values",
            Assert.Throws<ArgumentNullException>(() => new SKColorSpaceTransferFn((float[])null!)).ParamName);
        Assert.Equal(
            "values",
            Assert.Throws<ArgumentException>(() => new SKColorSpaceTransferFn(new float[6])).ParamName);
        var value = SKColorSpaceTransferFn.Srgb;
        var equal = new SKColorSpaceTransferFn(value.Values);
        Assert.True(value == equal);
        Assert.False(value != equal);
        Assert.Equal(value.GetHashCode(), equal.GetHashCode());
        equal.F = 1f;
        Assert.NotEqual(value, equal);
    }

    [Fact]
    public void ColorSpaceFactoriesReuseNativeSingletons()
    {
        using var srgb = SKColorSpace.CreateSrgb();
        using var linear = SKColorSpace.CreateSrgbLinear();
        Assert.Same(srgb, SKColorSpace.CreateSrgb());
        Assert.Same(linear, SKColorSpace.CreateSrgbLinear());
        Assert.True(srgb.IsSrgb);
        Assert.True(srgb.GammaIsCloseToSrgb);
        Assert.False(srgb.GammaIsLinear);
        Assert.True(srgb.IsNumericalTransferFunction);
        Assert.False(linear.IsSrgb);
        Assert.True(linear.GammaIsLinear);

        var handle = srgb.Handle;
        srgb.Dispose();
        Assert.Same(srgb, SKColorSpace.CreateSrgb());
        Assert.Equal(handle, srgb.Handle);
    }

    [Fact]
    public void ColorSpaceRgbCreationValidatesAndCanonicalizes()
    {
        Assert.Null(SKColorSpace.CreateRgb(
            new SKColorSpaceTransferFn(float.NaN, 1f, 0f, 0f, 0f, 0f, 0f),
            SKColorSpaceXyz.Srgb));
        Assert.Null(SKColorSpace.CreateRgb(
            SKColorSpaceTransferFn.Srgb,
            new SKColorSpaceXyz(float.NaN)));
        Assert.Same(
            SKColorSpace.CreateSrgb(),
            SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.Srgb));

        using var emptyTransfer = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Empty, SKColorSpaceXyz.Srgb);
        Assert.NotNull(emptyTransfer);
        Assert.True(emptyTransfer.IsNumericalTransferFunction);
        Assert.Equal(SKColorSpaceTransferFn.Empty, emptyTransfer.GetNumericalTransferFunction());

        using var twoDotTwo = SKColorSpace.CreateRgb(
            new SKColorSpaceTransferFn(2.2005f, 1.0005f, 0.0005f, 7f, 0f, 0.0005f, 8f),
            SKColorSpaceXyz.DisplayP3)!;
        Assert.Equal(SKColorSpaceTransferFn.TwoDotTwo, twoDotTwo.GetNumericalTransferFunction());

        using var linearExponent = SKColorSpace.CreateRgb(
            new SKColorSpaceTransferFn(1.0005f, 1.0005f, 0.0005f, 7f, 0f, 0.0005f, 8f),
            SKColorSpaceXyz.DisplayP3)!;
        Assert.Equal(SKColorSpaceTransferFn.Linear, linearExponent.GetNumericalTransferFunction());

        using var linearSegment = SKColorSpace.CreateRgb(
            new SKColorSpaceTransferFn(3f, 2f, 1f, 1.0005f, 1f, 4f, 0.0005f),
            SKColorSpaceXyz.DisplayP3)!;
        Assert.Equal(SKColorSpaceTransferFn.Linear, linearSegment.GetNumericalTransferFunction());

        var nearSrgb = SKColorSpaceXyz.Srgb;
        var nearValues = nearSrgb.Values;
        nearValues[0] += 0.005f;
        nearSrgb.Values = nearValues;
        using var preservedMatrix = SKColorSpace.CreateRgb(
            new SKColorSpaceTransferFn(2.4f, 1f, 0f, 0f, 0f, 0f, 0f),
            nearSrgb)!;
        Assert.Equal(nearSrgb, preservedMatrix.ToColorSpaceXyz());
    }

    [Fact]
    public void ColorSpaceQueriesAndGammaConversionsMatchNative()
    {
        using var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3)!;
        Assert.True(p3.GetNumericalTransferFunction(out var transferFunction));
        Assert.Equal(SKColorSpaceTransferFn.Srgb, transferFunction);
        Assert.True(p3.ToColorSpaceXyz(out var xyz));
        Assert.Equal(SKColorSpaceXyz.DisplayP3, xyz);
        Assert.Equal(xyz, p3.ToColorSpaceXyz());
        Assert.Same(p3, p3.ToSrgbGamma());

        using var linear = p3.ToLinearGamma();
        Assert.True(linear.GammaIsLinear);
        Assert.Same(linear, linear.ToLinearGamma());
        Assert.Equal(SKColorSpaceXyz.DisplayP3, linear.ToColorSpaceXyz());
    }

    [Fact]
    public void ColorSpaceHdrTransferFunctionsAreNotNumerical()
    {
        using var pq = SKColorSpace.CreateCicp(
            SKColorspacePrimariesCicp.Rec2020,
            SKColorspaceTransferFnCicp.Pq)!;
        Assert.False(pq.IsNumericalTransferFunction);
        Assert.False(pq.GetNumericalTransferFunction(out var pqTransfer));
        Assert.Equal(SKColorSpaceTransferFn.Pq, pqTransfer);
        Assert.Equal(SKColorSpaceTransferFn.Empty, pq.GetNumericalTransferFunction());
        Assert.Equal(SKColorSpaceXyz.Rec2020, pq.ToColorSpaceXyz());

        using var hlg = SKColorSpace.CreateCicp(
            SKColorspacePrimariesCicp.Rec2020,
            SKColorspaceTransferFnCicp.Hlg)!;
        Assert.False(hlg.GetNumericalTransferFunction(out var hlgTransfer));
        Assert.Equal(SKColorSpaceTransferFn.Hlg, hlgTransfer);
    }

    [Fact]
    public void CicpValuesAndFactoriesMatchNative()
    {
        Assert.Equal(22, (int)SKColorspacePrimariesCicp.ItuTH273Value22);
        Assert.Equal(18, (int)SKColorspaceTransferFnCicp.Hlg);
        Assert.Same(
            SKColorSpace.CreateSrgb(),
            SKColorSpace.CreateCicp(
                SKColorspacePrimariesCicp.Rec709,
                SKColorspaceTransferFnCicp.Iec6196621));
        Assert.Null(SKColorSpace.CreateCicp(
            SKColorspacePrimariesCicp.Unknown,
            SKColorspaceTransferFnCicp.Unknown));

        foreach (var primaries in Enum.GetValues<SKColorspacePrimariesCicp>().Where(value => value != 0))
        {
            using var colorSpace = SKColorSpace.CreateCicp(primaries, SKColorspaceTransferFnCicp.Linear);
            Assert.NotNull(colorSpace);
            Assert.True(colorSpace.ToColorSpaceXyz(out _));
        }
    }

    [Fact]
    public void ColorSpaceEqualityMatchesNativeValidation()
    {
        using var srgb = SKColorSpace.CreateSrgb();
        using var linear = SKColorSpace.CreateSrgbLinear();
        Assert.True(SKColorSpace.Equal(srgb, SKColorSpace.CreateSrgb()));
        Assert.False(SKColorSpace.Equal(srgb, linear));
        Assert.Equal("left", Assert.Throws<ArgumentNullException>(() => SKColorSpace.Equal(null!, srgb)).ParamName);
        Assert.Equal("right", Assert.Throws<ArgumentNullException>(() => SKColorSpace.Equal(srgb, null!)).ParamName);
    }

    [Fact]
    public void EmptyAndGeneratedProfilesMatchNative()
    {
        using var empty = new SKColorSpaceIccProfile();
        Assert.Equal(0, empty.Size);
        Assert.Equal(IntPtr.Zero, empty.Buffer);
        Assert.False(empty.ToColorSpaceXyz(out var emptyXyz));
        Assert.Equal(SKColorSpaceXyz.Empty, emptyXyz);

        using var generated = SKColorSpace.CreateSrgb().ToProfile();
        Assert.Equal(0, generated.Size);
        Assert.Equal(IntPtr.Zero, generated.Buffer);
        Assert.True(generated.ToColorSpaceXyz(out var generatedXyz));
        Assert.Equal(SKColorSpaceXyz.Srgb, generatedXyz);
    }

    [Fact]
    public void ParametricRgbIccProfileRoundTripsWithoutSourceOwnership()
    {
        var bytes = CreateRgbIcc(SKColorSpaceXyz.DisplayP3, SKColorSpaceTransferFn.Srgb, sampledCurve: false);
        using var profile = SKColorSpaceIccProfile.Create(bytes)!;
        Assert.NotNull(profile);
        Assert.Equal(bytes.Length, profile.Size);
        Assert.NotEqual(IntPtr.Zero, profile.Buffer);
        Assert.True(profile.ToColorSpaceXyz(out var xyz));
        Assert.Equal(QuantizeXyz(SKColorSpaceXyz.DisplayP3), xyz);

        bytes[36] = 0;
        using var colorSpace = SKColorSpace.CreateIcc(profile)!;
        Assert.NotNull(colorSpace);
        Assert.True(colorSpace.GammaIsCloseToSrgb);
        Assert.Equal(xyz, colorSpace.ToColorSpaceXyz());
    }

    [Fact]
    public void SampledSrgbIccProfileCanonicalizesToSrgb()
    {
        var bytes = CreateRgbIcc(SKColorSpaceXyz.Srgb, SKColorSpaceTransferFn.Srgb, sampledCurve: true);
        using var profile = SKColorSpaceIccProfile.Create(bytes)!;
        using var colorSpace = SKColorSpace.CreateIcc(profile)!;
        Assert.Same(SKColorSpace.CreateSrgb(), colorSpace);
        Assert.True(colorSpace.IsSrgb);
        Assert.Equal(SKColorSpaceTransferFn.Srgb, colorSpace.GetNumericalTransferFunction());

        var gammaBytes = CreateRgbIcc(
            SKColorSpaceXyz.Srgb,
            SKColorSpaceTransferFn.TwoDotTwo,
            sampledCurve: true);
        using var gammaProfile = SKColorSpaceIccProfile.Create(gammaBytes)!;
        Assert.NotNull(gammaProfile);
        Assert.Null(SKColorSpace.CreateIcc(gammaProfile));
    }

    [Fact]
    public unsafe void IccOverloadsMatchNative()
    {
        var bytes = CreateRgbIcc(SKColorSpaceXyz.Srgb, SKColorSpaceTransferFn.Srgb, sampledCurve: false);
        using var data = SKData.CreateCopy(bytes);
        using var fromData = SKColorSpace.CreateIcc(data)!;
        using var fromArray = SKColorSpace.CreateIcc(bytes)!;
        using var fromLength = SKColorSpace.CreateIcc(bytes, bytes.Length)!;
        using var fromSpan = SKColorSpace.CreateIcc(bytes.AsSpan())!;
        fixed (byte* pointer = bytes)
        {
            using var fromPointer = SKColorSpace.CreateIcc((IntPtr)pointer, bytes.Length)!;
            Assert.True(fromPointer.IsSrgb);
        }

        Assert.All(new[] { fromData, fromArray, fromLength, fromSpan }, value => Assert.True(value.IsSrgb));

        Assert.Equal("input", Assert.Throws<ArgumentNullException>(() => SKColorSpace.CreateIcc((byte[])null!)).ParamName);
        Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => SKColorSpace.CreateIcc(bytes, -1)).ParamName);
        Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => SKColorSpace.CreateIcc(bytes, bytes.Length + 1L)).ParamName);
    }

    [Fact]
    public void InvalidAndNonRgbIccProfilesMatchNative()
    {
        Assert.Null(SKColorSpaceIccProfile.Create(Array.Empty<byte>()));
        Assert.Null(SKColorSpaceIccProfile.Create(new byte[256]));
        Assert.Equal(
            "profile",
            Assert.Throws<ArgumentNullException>(() => SKColorSpace.CreateIcc(new byte[256])).ParamName);

        var cmyk = CreateRgbIcc(SKColorSpaceXyz.Srgb, SKColorSpaceTransferFn.Srgb, sampledCurve: false);
        "CMYK"u8.CopyTo(cmyk.AsSpan(16));
        using var profile = SKColorSpaceIccProfile.Create(cmyk)!;
        Assert.NotNull(profile);
        Assert.False(profile.ToColorSpaceXyz(out _));
        Assert.Null(SKColorSpace.CreateIcc(profile));
    }

    private static byte[] CreateRgbIcc(
        SKColorSpaceXyz xyz,
        SKColorSpaceTransferFn transferFunction,
        bool sampledCurve)
    {
        const int tagCount = 6;
        const int tableEnd = 132 + tagCount * 12;
        const int xyzTagSize = 20;
        var curveSize = sampledCurve ? 12 + 1024 * 2 : 40;
        var curveOffset = tableEnd + xyzTagSize * 3;
        var bytes = new byte[curveOffset + curveSize];
        WriteUInt32(bytes, 0, (uint)bytes.Length);
        "mntr"u8.CopyTo(bytes.AsSpan(12));
        "RGB "u8.CopyTo(bytes.AsSpan(16));
        "XYZ "u8.CopyTo(bytes.AsSpan(20));
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        WriteUInt32(bytes, 128, tagCount);

        var signatures = new[] { "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC" };
        for (var index = 0; index < signatures.Length; index++)
        {
            System.Text.Encoding.ASCII.GetBytes(signatures[index]).CopyTo(bytes, 132 + index * 12);
            var offset = index < 3 ? tableEnd + index * xyzTagSize : curveOffset;
            var size = index < 3 ? xyzTagSize : curveSize;
            WriteUInt32(bytes, 136 + index * 12, (uint)offset);
            WriteUInt32(bytes, 140 + index * 12, (uint)size);
        }

        for (var column = 0; column < 3; column++)
        {
            var offset = tableEnd + column * xyzTagSize;
            "XYZ "u8.CopyTo(bytes.AsSpan(offset));
            WriteS15Fixed16(bytes, offset + 8, xyz[column, 0]);
            WriteS15Fixed16(bytes, offset + 12, xyz[column, 1]);
            WriteS15Fixed16(bytes, offset + 16, xyz[column, 2]);
        }

        if (sampledCurve)
        {
            "curv"u8.CopyTo(bytes.AsSpan(curveOffset));
            WriteUInt32(bytes, curveOffset + 8, 1024);
            for (var index = 0; index < 1024; index++)
            {
                var encoded = index / 1023f;
                var linear = encoded < transferFunction.D
                    ? transferFunction.C * encoded + transferFunction.F
                    : MathF.Pow(transferFunction.A * encoded + transferFunction.B, transferFunction.G) + transferFunction.E;
                BinaryPrimitives.WriteUInt16BigEndian(
                    bytes.AsSpan(curveOffset + 12 + index * 2),
                    (ushort)Math.Clamp((int)MathF.Round(linear * 65535f), 0, ushort.MaxValue));
            }
        }
        else
        {
            "para"u8.CopyTo(bytes.AsSpan(curveOffset));
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(curveOffset + 8), 4);
            var values = transferFunction.Values;
            for (var index = 0; index < values.Length; index++)
            {
                WriteS15Fixed16(bytes, curveOffset + 12 + index * 4, values[index]);
            }
        }

        return bytes;
    }

    private static SKColorSpaceXyz QuantizeXyz(SKColorSpaceXyz value) => new(
        value.Values.Select(number => MathF.Round(number * 65536f) / 65536f).ToArray());

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);

    private static void WriteS15Fixed16(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), (int)MathF.Round(value * 65536f));
}
