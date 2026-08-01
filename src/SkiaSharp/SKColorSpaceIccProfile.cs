using System;
using System.Buffers.Binary;

namespace SkiaSharp;

public class SKColorSpaceIccProfile : SKObject
{
    private const uint AcspSignature = 0x61637370;
    private const uint RgbSignature = 0x52474220;
    private const uint XyzTagType = 0x58595a20;
    private const uint CurveTagType = 0x63757276;
    private const uint ParametricCurveTagType = 0x70617261;

    private SKData? _data;
    private readonly SKColorSpaceTransferFn _transferFunction;
    private readonly SKColorSpaceXyz _xyz;
    private readonly bool _hasTransferFunction;
    private readonly bool _hasXyz;

    public SKColorSpaceIccProfile()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    private SKColorSpaceIccProfile(
        SKData data,
        SKColorSpaceTransferFn transferFunction,
        SKColorSpaceXyz xyz,
        bool hasTransferFunction,
        bool hasXyz)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = data;
        _transferFunction = transferFunction;
        _xyz = xyz;
        _hasTransferFunction = hasTransferFunction;
        _hasXyz = hasXyz;
    }

    internal SKColorSpaceIccProfile(SKColorSpaceTransferFn transferFunction, SKColorSpaceXyz xyz)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _transferFunction = transferFunction;
        _xyz = xyz;
        _hasTransferFunction = true;
        _hasXyz = true;
    }

    public IntPtr Buffer => _data?.Data ?? IntPtr.Zero;

    public long Size => _data?.Size ?? 0;

    public SKColorSpaceXyz ToColorSpaceXyz() =>
        ToColorSpaceXyz(out var xyz) ? xyz : SKColorSpaceXyz.Empty;

    public bool ToColorSpaceXyz(out SKColorSpaceXyz toXyzD50)
    {
        toXyzD50 = _hasXyz ? _xyz : SKColorSpaceXyz.Empty;
        return _hasXyz;
    }

    public static SKColorSpaceIccProfile? Create(SKData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Create(data.Span);
    }

    public static SKColorSpaceIccProfile? Create(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Create(data.AsSpan());
    }

    public static unsafe SKColorSpaceIccProfile? Create(IntPtr data, long length)
    {
        if (length < 0 || length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return Create(ReadOnlySpan<byte>.Empty);
        }

        if (data == IntPtr.Zero)
        {
            throw new ArgumentException("A non-empty profile requires a valid address.", nameof(data));
        }

        return Create(new ReadOnlySpan<byte>((void*)data, checked((int)length)));
    }

    public static SKColorSpaceIccProfile? Create(ReadOnlySpan<byte> data)
    {
        if (!TryParse(data, out var transferFunction, out var xyz, out var hasTransferFunction, out var hasXyz))
        {
            return null;
        }

        return new SKColorSpaceIccProfile(
            SKData.CreateCopy(data),
            transferFunction,
            xyz,
            hasTransferFunction,
            hasXyz);
    }

    internal bool TryGetColorSpace(out SKColorSpaceTransferFn transferFunction, out SKColorSpaceXyz xyz)
    {
        transferFunction = _transferFunction;
        xyz = _xyz;
        return _hasTransferFunction && _hasXyz;
    }

    protected override void DisposeManaged()
    {
        _data?.Dispose();
        _data = null;
        base.DisposeManaged();
    }

    private static bool TryParse(
        ReadOnlySpan<byte> source,
        out SKColorSpaceTransferFn transferFunction,
        out SKColorSpaceXyz xyz,
        out bool hasTransferFunction,
        out bool hasXyz)
    {
        transferFunction = SKColorSpaceTransferFn.Empty;
        xyz = SKColorSpaceXyz.Empty;
        hasTransferFunction = false;
        hasXyz = false;
        if (source.Length < 132 || ReadUInt32(source, 36) != AcspSignature)
        {
            return false;
        }

        var declaredSize = ReadUInt32(source, 0);
        if (declaredSize < 132 || declaredSize > source.Length)
        {
            return false;
        }

        var profile = source[..checked((int)declaredSize)];
        var tagCount = ReadUInt32(profile, 128);
        if (tagCount > (uint)((profile.Length - 132) / 12))
        {
            return false;
        }

        if (ReadUInt32(profile, 16) != RgbSignature)
        {
            return true;
        }

        hasXyz = TryReadRgbMatrix(profile, tagCount, out xyz);
        hasTransferFunction = TryReadRgbTransferFunction(profile, tagCount, out transferFunction);
        return true;
    }

    private static bool TryReadRgbMatrix(ReadOnlySpan<byte> profile, uint tagCount, out SKColorSpaceXyz xyz)
    {
        xyz = SKColorSpaceXyz.Empty;
        if (!TryReadXyzTag(profile, tagCount, 0x7258595a, out var red) ||
            !TryReadXyzTag(profile, tagCount, 0x6758595a, out var green) ||
            !TryReadXyzTag(profile, tagCount, 0x6258595a, out var blue))
        {
            return false;
        }

        xyz = new SKColorSpaceXyz(
            red[0], green[0], blue[0],
            red[1], green[1], blue[1],
            red[2], green[2], blue[2]);
        return true;
    }

    private static bool TryReadRgbTransferFunction(
        ReadOnlySpan<byte> profile,
        uint tagCount,
        out SKColorSpaceTransferFn transferFunction)
    {
        transferFunction = SKColorSpaceTransferFn.Empty;
        if (!TryFindTag(profile, tagCount, 0x72545243, out var red) ||
            !TryFindTag(profile, tagCount, 0x67545243, out var green) ||
            !TryFindTag(profile, tagCount, 0x62545243, out var blue) ||
            !TryReadCurve(red, out var redTransfer) ||
            !TryReadCurve(green, out var greenTransfer) ||
            !TryReadCurve(blue, out var blueTransfer) ||
            !TransferIsClose(redTransfer, greenTransfer) ||
            !TransferIsClose(redTransfer, blueTransfer))
        {
            return false;
        }

        transferFunction = redTransfer;
        return true;
    }

    private static bool TryReadXyzTag(
        ReadOnlySpan<byte> profile,
        uint tagCount,
        uint signature,
        out float[] xyz)
    {
        xyz = Array.Empty<float>();
        if (!TryFindTag(profile, tagCount, signature, out var tag) ||
            tag.Length < 20 ||
            ReadUInt32(tag, 0) != XyzTagType)
        {
            return false;
        }

        xyz =
        [
            ReadS15Fixed16(tag, 8),
            ReadS15Fixed16(tag, 12),
            ReadS15Fixed16(tag, 16),
        ];
        return true;
    }

    private static bool TryReadCurve(ReadOnlySpan<byte> tag, out SKColorSpaceTransferFn transferFunction)
    {
        transferFunction = SKColorSpaceTransferFn.Empty;
        if (tag.Length < 12)
        {
            return false;
        }

        return ReadUInt32(tag, 0) switch
        {
            CurveTagType => TryReadCurveType(tag, out transferFunction),
            ParametricCurveTagType => TryReadParametricCurve(tag, out transferFunction),
            _ => false,
        };
    }

    private static bool TryReadCurveType(ReadOnlySpan<byte> tag, out SKColorSpaceTransferFn transferFunction)
    {
        transferFunction = SKColorSpaceTransferFn.Empty;
        var count = ReadUInt32(tag, 8);
        if (count == 0)
        {
            transferFunction = SKColorSpaceTransferFn.Linear;
            return true;
        }

        if (count == 1 && tag.Length >= 14)
        {
            transferFunction = new SKColorSpaceTransferFn(
                BinaryPrimitives.ReadUInt16BigEndian(tag[12..]) / 256f,
                1f, 0f, 0f, 0f, 0f, 0f);
            return true;
        }

        return count <= (uint)((tag.Length - 12) / 2) &&
               TryApproximateSampledSrgbCurve(tag[12..], checked((int)count), out transferFunction);
    }

    private static bool TryReadParametricCurve(ReadOnlySpan<byte> tag, out SKColorSpaceTransferFn transferFunction)
    {
        transferFunction = SKColorSpaceTransferFn.Empty;
        var function = BinaryPrimitives.ReadUInt16BigEndian(tag[8..]);
        var parameterCount = function switch
        {
            0 => 1,
            1 => 3,
            2 => 4,
            3 => 5,
            4 => 7,
            _ => 0,
        };
        if (parameterCount == 0 || tag.Length < 12 + parameterCount * 4)
        {
            return false;
        }

        Span<float> parameters = stackalloc float[7];
        for (var index = 0; index < parameterCount; index++)
        {
            parameters[index] = ReadS15Fixed16(tag, 12 + index * 4);
        }

        var g = parameters[0];
        if (function == 0)
        {
            transferFunction = new SKColorSpaceTransferFn(g, 1f, 0f, 0f, 0f, 0f, 0f);
            return transferFunction.IsValid;
        }

        var a = parameters[1];
        var b = parameters[2];
        if (a == 0f)
        {
            return false;
        }

        transferFunction = function switch
        {
            1 => new SKColorSpaceTransferFn(g, a, b, 0f, -b / a, 0f, 0f),
            2 => new SKColorSpaceTransferFn(g, a, b, 0f, -b / a, 0f, parameters[3]),
            3 => new SKColorSpaceTransferFn(g, a, b, parameters[3], parameters[4], 0f, 0f),
            4 => new SKColorSpaceTransferFn(g, a, b, parameters[3], parameters[4], parameters[5], parameters[6]),
            _ => SKColorSpaceTransferFn.Empty,
        };
        return transferFunction.IsValid;
    }

    private static bool TryApproximateSampledSrgbCurve(
        ReadOnlySpan<byte> samples,
        int count,
        out SKColorSpaceTransferFn transferFunction)
    {
        transferFunction = SKColorSpaceTransferFn.Empty;
        if (count < 2)
        {
            return false;
        }

        if (GetMaximumSampleError(samples, count, SKColorSpaceTransferFn.Srgb) < 1f / 512f)
        {
            transferFunction = SKColorSpaceTransferFn.Srgb;
            return true;
        }
        return false;
    }

    private static float GetMaximumSampleError(
        ReadOnlySpan<byte> samples,
        int count,
        SKColorSpaceTransferFn transferFunction)
    {
        var maximum = 0f;
        for (var index = 0; index < count; index++)
        {
            var encoded = index / (float)(count - 1);
            var expected = EvaluateTransferFunction(transferFunction, encoded);
            var actual = BinaryPrimitives.ReadUInt16BigEndian(samples[(index * 2)..]) / 65535f;
            maximum = MathF.Max(maximum, MathF.Abs(actual - expected));
        }

        return maximum;
    }

    private static float EvaluateTransferFunction(SKColorSpaceTransferFn value, float encoded)
    {
        if (encoded < value.D)
        {
            return value.C * encoded + value.F;
        }

        return MathF.Pow(value.A * encoded + value.B, value.G) + value.E;
    }

    private static bool TryFindTag(
        ReadOnlySpan<byte> profile,
        uint tagCount,
        uint signature,
        out ReadOnlySpan<byte> tag)
    {
        for (var index = 0; index < tagCount; index++)
        {
            var entry = 132 + index * 12;
            if (ReadUInt32(profile, entry) != signature)
            {
                continue;
            }

            var offset = ReadUInt32(profile, entry + 4);
            var size = ReadUInt32(profile, entry + 8);
            if (offset > profile.Length || size > profile.Length - offset)
            {
                tag = default;
                return false;
            }

            tag = profile.Slice(checked((int)offset), checked((int)size));
            return true;
        }

        tag = default;
        return false;
    }

    private static bool TransferIsClose(SKColorSpaceTransferFn left, SKColorSpaceTransferFn right) =>
        MathF.Abs(left.G - right.G) < 0.001f &&
        MathF.Abs(left.A - right.A) < 0.001f &&
        MathF.Abs(left.B - right.B) < 0.001f &&
        MathF.Abs(left.C - right.C) < 0.001f &&
        MathF.Abs(left.D - right.D) < 0.001f &&
        MathF.Abs(left.E - right.E) < 0.001f &&
        MathF.Abs(left.F - right.F) < 0.001f;

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static float ReadS15Fixed16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(data[offset..]) / 65536f;
}
