using System;

#nullable disable

namespace SkiaSharp;

/// <summary>
/// Describes RGB chromaticity coordinates and their reference white point.
/// </summary>
public struct SKColorSpacePrimaries : IEquatable<SKColorSpacePrimaries>
{
    private static readonly SKColorSpaceXyz s_srgbToXyzD50 = new(
        0.43602818f, 0.38510093f, 0.14309105f,
        0.22247864f, 0.7168975f, 0.060624108f,
        0.013926373f, 0.097092114f, 0.7141915f);

    private static readonly SKColorSpaceXyz s_displayP3ToXyzD50 = new(
        0.51510215f, 0.29196474f, 0.1571531f,
        0.24118185f, 0.6922364f, 0.06658185f,
        -0.0010494092f, 0.041881755f, 0.7843777f);

    private float _rx;
    private float _ry;
    private float _gx;
    private float _gy;
    private float _bx;
    private float _by;
    private float _wx;
    private float _wy;

    public static readonly SKColorSpacePrimaries Empty;

    public float RX
    {
        readonly get => _rx;
        set => _rx = value;
    }

    public float RY
    {
        readonly get => _ry;
        set => _ry = value;
    }

    public float GX
    {
        readonly get => _gx;
        set => _gx = value;
    }

    public float GY
    {
        readonly get => _gy;
        set => _gy = value;
    }

    public float BX
    {
        readonly get => _bx;
        set => _bx = value;
    }

    public float BY
    {
        readonly get => _by;
        set => _by = value;
    }

    public float WX
    {
        readonly get => _wx;
        set => _wx = value;
    }

    public float WY
    {
        readonly get => _wy;
        set => _wy = value;
    }

    public readonly float[] Values =>
    [
        _rx, _ry,
        _gx, _gy,
        _bx, _by,
        _wx, _wy,
    ];

    public SKColorSpacePrimaries(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != 8)
        {
            throw new ArgumentException(
                "The values must have exactly 8 items, one for each of [RX, RY, GX, GY, BX, BY, WX, WY].",
                nameof(values));
        }

        _rx = values[0];
        _ry = values[1];
        _gx = values[2];
        _gy = values[3];
        _bx = values[4];
        _by = values[5];
        _wx = values[6];
        _wy = values[7];
    }

    public SKColorSpacePrimaries(
        float rx,
        float ry,
        float gx,
        float gy,
        float bx,
        float by,
        float wx,
        float wy)
    {
        _rx = rx;
        _ry = ry;
        _gx = gx;
        _gy = gy;
        _bx = bx;
        _by = by;
        _wx = wx;
        _wy = wy;
    }

    public readonly SKColorSpaceXyz ToColorSpaceXyz() =>
        ToColorSpaceXyz(out var toXyzD50) ? toXyzD50 : SKColorSpaceXyz.Empty;

    public readonly bool ToColorSpaceXyz(out SKColorSpaceXyz toXyzD50)
    {
        if (_wx == 0.3127f && _wy == 0.3290f)
        {
            if (_rx == 0.64f && _ry == 0.33f &&
                _gx == 0.30f && _gy == 0.60f &&
                _bx == 0.15f && _by == 0.06f)
            {
                toXyzD50 = s_srgbToXyzD50;
                return true;
            }

            if (_rx == 0.68f && _ry == 0.32f &&
                _gx == 0.265f && _gy == 0.69f &&
                _bx == 0.15f && _by == 0.06f)
            {
                toXyzD50 = s_displayP3ToXyzD50;
                return true;
            }
        }

        toXyzD50 = SKColorSpaceXyz.Empty;
        if (!IsUnitCoordinate(_rx) ||
            !IsUnitCoordinate(_ry) ||
            !IsUnitCoordinate(_gx) ||
            !IsUnitCoordinate(_gy) ||
            !IsUnitCoordinate(_bx) ||
            !IsUnitCoordinate(_by) ||
            !IsUnitCoordinate(_wx) ||
            !IsUnitCoordinate(_wy))
        {
            return false;
        }

        // Work in homogeneous xy chromaticity coordinates. Scaling the three
        // primary columns to their white point is one 3x3 solve; this avoids
        // divisions by a primary's y coordinate and remains well-defined for
        // boundary primaries.
        var rz = 1f - _rx - _ry;
        var gz = 1f - _gx - _gy;
        var bz = 1f - _bx - _by;
        var wz = 1f - _wx - _wy;
        var determinant =
            _rx * (_gy * bz - _by * gz) -
            _gx * (_ry * bz - _by * rz) +
            _bx * (_ry * gz - _gy * rz);
        if (determinant == 0f || !float.IsFinite(determinant))
            return false;

        var inverseDeterminant = 1f / determinant;
        var sr =
            ((_gy * bz - _by * gz) * _wx +
             (_bx * gz - _gx * bz) * _wy +
             (_gx * _by - _bx * _gy) * wz) * inverseDeterminant;
        var sg =
            ((_by * rz - _ry * bz) * _wx +
             (_rx * bz - _bx * rz) * _wy +
             (_bx * _ry - _rx * _by) * wz) * inverseDeterminant;
        var sb =
            ((_ry * gz - _gy * rz) * _wx +
             (_gx * rz - _rx * gz) * _wy +
             (_rx * _gy - _gx * _ry) * wz) * inverseDeterminant;

        var reciprocalWhiteY = 1f / _wy;
        var m00 = _rx * sr * reciprocalWhiteY;
        var m01 = _gx * sg * reciprocalWhiteY;
        var m02 = _bx * sb * reciprocalWhiteY;
        var m10 = _ry * sr * reciprocalWhiteY;
        var m11 = _gy * sg * reciprocalWhiteY;
        var m12 = _by * sb * reciprocalWhiteY;
        var m20 = rz * sr * reciprocalWhiteY;
        var m21 = gz * sg * reciprocalWhiteY;
        var m22 = bz * sb * reciprocalWhiteY;

        // ICC profiles use a D50 profile-connection space. Apply the standard
        // Bradford cone-response transform from the caller's white point to
        // the ICC D50 tristimulus white (0.96422, 1.0, 0.82521).
        var sourceX = _wx * reciprocalWhiteY;
        var sourceZ = wz * reciprocalWhiteY;
        var sourceL = 0.8951f * sourceX + 0.2664f - 0.1614f * sourceZ;
        var sourceM = -0.7502f * sourceX + 1.7135f + 0.0367f * sourceZ;
        var sourceS = 0.0389f * sourceX - 0.0685f + 1.0296f * sourceZ;
        var destinationL = 0.8951f * 0.96422f + 0.2664f - 0.1614f * 0.82521f;
        var destinationM = -0.7502f * 0.96422f + 1.7135f + 0.0367f * 0.82521f;
        var destinationS = 0.0389f * 0.96422f - 0.0685f + 1.0296f * 0.82521f;
        var scaleL = destinationL / sourceL;
        var scaleM = destinationM / sourceM;
        var scaleS = destinationS / sourceS;

        const float i00 = 0.986992905467f;
        const float i01 = -0.147054256421f;
        const float i02 = 0.159962651664f;
        const float i10 = 0.432305269723f;
        const float i11 = 0.518360271537f;
        const float i12 = 0.049291228213f;
        const float i20 = -0.008528664575f;
        const float i21 = 0.040042821654f;
        const float i22 = 0.968486695788f;

        var a00 = i00 * scaleL * 0.8951f + i01 * scaleM * -0.7502f + i02 * scaleS * 0.0389f;
        var a01 = i00 * scaleL * 0.2664f + i01 * scaleM * 1.7135f + i02 * scaleS * -0.0685f;
        var a02 = i00 * scaleL * -0.1614f + i01 * scaleM * 0.0367f + i02 * scaleS * 1.0296f;
        var a10 = i10 * scaleL * 0.8951f + i11 * scaleM * -0.7502f + i12 * scaleS * 0.0389f;
        var a11 = i10 * scaleL * 0.2664f + i11 * scaleM * 1.7135f + i12 * scaleS * -0.0685f;
        var a12 = i10 * scaleL * -0.1614f + i11 * scaleM * 0.0367f + i12 * scaleS * 1.0296f;
        var a20 = i20 * scaleL * 0.8951f + i21 * scaleM * -0.7502f + i22 * scaleS * 0.0389f;
        var a21 = i20 * scaleL * 0.2664f + i21 * scaleM * 1.7135f + i22 * scaleS * -0.0685f;
        var a22 = i20 * scaleL * -0.1614f + i21 * scaleM * 0.0367f + i22 * scaleS * 1.0296f;

        toXyzD50 = new SKColorSpaceXyz(
            a00 * m00 + a01 * m10 + a02 * m20,
            a00 * m01 + a01 * m11 + a02 * m21,
            a00 * m02 + a01 * m12 + a02 * m22,
            a10 * m00 + a11 * m10 + a12 * m20,
            a10 * m01 + a11 * m11 + a12 * m21,
            a10 * m02 + a11 * m12 + a12 * m22,
            a20 * m00 + a21 * m10 + a22 * m20,
            a20 * m01 + a21 * m11 + a22 * m21,
            a20 * m02 + a21 * m12 + a22 * m22);
        return true;
    }

    public readonly bool Equals(SKColorSpacePrimaries obj) =>
        _rx == obj._rx &&
        _ry == obj._ry &&
        _gx == obj._gx &&
        _gy == obj._gy &&
        _bx == obj._bx &&
        _by == obj._by &&
        _wx == obj._wx &&
        _wy == obj._wy;

    public override readonly bool Equals(object obj) =>
        obj is SKColorSpacePrimaries other && Equals(other);

    public static bool operator ==(SKColorSpacePrimaries left, SKColorSpacePrimaries right) =>
        left.Equals(right);

    public static bool operator !=(SKColorSpacePrimaries left, SKColorSpacePrimaries right) =>
        !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_rx);
        hash.Add(_ry);
        hash.Add(_gx);
        hash.Add(_gy);
        hash.Add(_bx);
        hash.Add(_by);
        hash.Add(_wx);
        hash.Add(_wy);
        return hash.ToHashCode();
    }

    private static bool IsUnitCoordinate(float value) => value >= 0f && value <= 1f;
}
