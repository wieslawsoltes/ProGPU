using System;
using System.Numerics;

namespace SkiaSharp;

public struct SKMatrix44 : IEquatable<SKMatrix44>
{
    internal const float DegreesToRadians = MathF.PI / 180f;

    private float _m00;
    private float _m01;
    private float _m02;
    private float _m03;
    private float _m10;
    private float _m11;
    private float _m12;
    private float _m13;
    private float _m20;
    private float _m21;
    private float _m22;
    private float _m23;
    private float _m30;
    private float _m31;
    private float _m32;
    private float _m33;

    public static readonly SKMatrix44 Empty;

    public static readonly SKMatrix44 Identity = Matrix4x4.Identity;

    public float M00
    {
        readonly get => _m00;
        set => _m00 = value;
    }

    public float M01
    {
        readonly get => _m01;
        set => _m01 = value;
    }

    public float M02
    {
        readonly get => _m02;
        set => _m02 = value;
    }

    public float M03
    {
        readonly get => _m03;
        set => _m03 = value;
    }

    public float M10
    {
        readonly get => _m10;
        set => _m10 = value;
    }

    public float M11
    {
        readonly get => _m11;
        set => _m11 = value;
    }

    public float M12
    {
        readonly get => _m12;
        set => _m12 = value;
    }

    public float M13
    {
        readonly get => _m13;
        set => _m13 = value;
    }

    public float M20
    {
        readonly get => _m20;
        set => _m20 = value;
    }

    public float M21
    {
        readonly get => _m21;
        set => _m21 = value;
    }

    public float M22
    {
        readonly get => _m22;
        set => _m22 = value;
    }

    public float M23
    {
        readonly get => _m23;
        set => _m23 = value;
    }

    public float M30
    {
        readonly get => _m30;
        set => _m30 = value;
    }

    public float M31
    {
        readonly get => _m31;
        set => _m31 = value;
    }

    public float M32
    {
        readonly get => _m32;
        set => _m32 = value;
    }

    public float M33
    {
        readonly get => _m33;
        set => _m33 = value;
    }

    public readonly bool IsInvertible => Matrix4x4.Invert(this, out _);

    public readonly SKMatrix Matrix => new(
        _m00,
        _m10,
        _m30,
        _m01,
        _m11,
        _m31,
        _m03,
        _m13,
        _m33);

    public float this[int row, int column]
    {
        readonly get => row switch
        {
            0 => column switch
            {
                0 => _m00,
                1 => _m01,
                2 => _m02,
                3 => _m03,
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            },
            1 => column switch
            {
                0 => _m10,
                1 => _m11,
                2 => _m12,
                3 => _m13,
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            },
            2 => column switch
            {
                0 => _m20,
                1 => _m21,
                2 => _m22,
                3 => _m23,
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            },
            3 => column switch
            {
                0 => _m30,
                1 => _m31,
                2 => _m32,
                3 => _m33,
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        set
        {
            switch (row)
            {
                case 0:
                    switch (column)
                    {
                        case 0: _m00 = value; break;
                        case 1: _m01 = value; break;
                        case 2: _m02 = value; break;
                        case 3: _m03 = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(column));
                    }
                    break;
                case 1:
                    switch (column)
                    {
                        case 0: _m10 = value; break;
                        case 1: _m11 = value; break;
                        case 2: _m12 = value; break;
                        case 3: _m13 = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(column));
                    }
                    break;
                case 2:
                    switch (column)
                    {
                        case 0: _m20 = value; break;
                        case 1: _m21 = value; break;
                        case 2: _m22 = value; break;
                        case 3: _m23 = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(column));
                    }
                    break;
                case 3:
                    switch (column)
                    {
                        case 0: _m30 = value; break;
                        case 1: _m31 = value; break;
                        case 2: _m32 = value; break;
                        case 3: _m33 = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(column));
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(row));
            }
        }
    }

    public SKMatrix44()
    {
        this = default;
    }

    public SKMatrix44(SKMatrix src)
    {
        this = src;
    }

    public SKMatrix44(SKMatrix44 src)
    {
        this = src;
    }

    public SKMatrix44(
        float m00,
        float m01,
        float m02,
        float m03,
        float m10,
        float m11,
        float m12,
        float m13,
        float m20,
        float m21,
        float m22,
        float m23,
        float m30,
        float m31,
        float m32,
        float m33)
    {
        _m00 = m00;
        _m01 = m01;
        _m02 = m02;
        _m03 = m03;
        _m10 = m10;
        _m11 = m11;
        _m12 = m12;
        _m13 = m13;
        _m20 = m20;
        _m21 = m21;
        _m22 = m22;
        _m23 = m23;
        _m30 = m30;
        _m31 = m31;
        _m32 = m32;
        _m33 = m33;
    }

    public static SKMatrix44 CreateIdentity() => Identity;

    public static SKMatrix44 CreateTranslation(float x, float y, float z) =>
        Matrix4x4.CreateTranslation(x, y, z);

    public static SKMatrix44 CreateScale(float x, float y, float z) => Matrix4x4.CreateScale(x, y, z);

    public static SKMatrix44 CreateScale(
        float x,
        float y,
        float z,
        float pivotX,
        float pivotY,
        float pivotZ) => Matrix4x4.CreateScale(x, y, z, new Vector3(pivotX, pivotY, pivotZ));

    public static SKMatrix44 CreateRotation(float x, float y, float z, float radians) =>
        Matrix4x4.CreateFromAxisAngle(new Vector3(x, y, z), radians);

    public static SKMatrix44 CreateRotationDegrees(float x, float y, float z, float degrees) =>
        Matrix4x4.CreateFromAxisAngle(new Vector3(x, y, z), degrees * DegreesToRadians);

    public static SKMatrix44 FromRowMajor(ReadOnlySpan<float> src)
    {
        if (src.Length != 16)
        {
            throw new ArgumentException("The source array must be 16 entries.", nameof(src));
        }

        return new SKMatrix44(
            src[0], src[1], src[2], src[3],
            src[4], src[5], src[6], src[7],
            src[8], src[9], src[10], src[11],
            src[12], src[13], src[14], src[15]);
    }

    public static SKMatrix44 FromColumnMajor(ReadOnlySpan<float> src)
    {
        if (src.Length != 16)
        {
            throw new ArgumentException("The source array must be 16 entries.", nameof(src));
        }

        return new SKMatrix44(
            src[0], src[4], src[8], src[12],
            src[1], src[5], src[9], src[13],
            src[2], src[6], src[10], src[14],
            src[3], src[7], src[11], src[15]);
    }

    public readonly float[] ToRowMajor()
    {
        var result = new float[16];
        ToRowMajor(result);
        return result;
    }

    public readonly float[] ToColumnMajor()
    {
        var result = new float[16];
        ToColumnMajor(result);
        return result;
    }

    public readonly void ToRowMajor(Span<float> dst)
    {
        if (dst.Length != 16)
        {
            throw new ArgumentException("The destination array must be 16 entries.", nameof(dst));
        }

        dst[0] = _m00;
        dst[1] = _m01;
        dst[2] = _m02;
        dst[3] = _m03;
        dst[4] = _m10;
        dst[5] = _m11;
        dst[6] = _m12;
        dst[7] = _m13;
        dst[8] = _m20;
        dst[9] = _m21;
        dst[10] = _m22;
        dst[11] = _m23;
        dst[12] = _m30;
        dst[13] = _m31;
        dst[14] = _m32;
        dst[15] = _m33;
    }

    public readonly void ToColumnMajor(Span<float> dst)
    {
        if (dst.Length != 16)
        {
            throw new ArgumentException("The destination array must be 16 entries.", nameof(dst));
        }

        dst[0] = _m00;
        dst[1] = _m10;
        dst[2] = _m20;
        dst[3] = _m30;
        dst[4] = _m01;
        dst[5] = _m11;
        dst[6] = _m21;
        dst[7] = _m31;
        dst[8] = _m02;
        dst[9] = _m12;
        dst[10] = _m22;
        dst[11] = _m32;
        dst[12] = _m03;
        dst[13] = _m13;
        dst[14] = _m23;
        dst[15] = _m33;
    }

    public readonly bool TryInvert(out SKMatrix44 inverse)
    {
        if (Matrix4x4.Invert(this, out var result))
        {
            inverse = result;
            return true;
        }

        inverse = Empty;
        return false;
    }

    public readonly SKMatrix44 Invert() =>
        Matrix4x4.Invert(this, out var result) ? result : Empty;

    public readonly SKMatrix44 Transpose() => Matrix4x4.Transpose(this);

    public readonly float Determinant() => ((Matrix4x4)this).GetDeterminant();

    public readonly SKPoint MapPoint(SKPoint point) => Vector2.Transform(point, this);

    public readonly SKPoint3 MapPoint(SKPoint3 point) => Vector3.Transform(point, this);

    public readonly SKPoint MapPoint(float x, float y) => MapPoint(new SKPoint(x, y));

    public readonly SKPoint3 MapPoint(float x, float y, float z) => MapPoint(new SKPoint3(x, y, z));

    internal readonly float[] MapScalars(float x, float y, float z, float w)
    {
        var result = Vector4.Transform(new Vector4(x, y, z, w), this);
        return [result.X, result.Y, result.Z, result.W];
    }

    internal readonly float[] MapScalars(ReadOnlySpan<float> srcVector4)
    {
        if (srcVector4.Length != 4)
        {
            throw new ArgumentException("The source vector array must be 4 entries.", nameof(srcVector4));
        }

        return MapScalars(srcVector4[0], srcVector4[1], srcVector4[2], srcVector4[3]);
    }

    internal readonly void MapScalars(ReadOnlySpan<float> srcVector4, Span<float> dstVector4)
    {
        if (srcVector4.Length != 4)
        {
            throw new ArgumentException("The source vector array must be 4 entries.", nameof(srcVector4));
        }

        if (dstVector4.Length != 4)
        {
            throw new ArgumentException("The destination vector array must be 4 entries.", nameof(dstVector4));
        }

        var result = Vector4.Transform(
            new Vector4(srcVector4[0], srcVector4[1], srcVector4[2], srcVector4[3]),
            this);
        dstVector4[0] = result.X;
        dstVector4[1] = result.Y;
        dstVector4[2] = result.Z;
        dstVector4[3] = result.W;
    }

    public static SKMatrix44 Concat(SKMatrix44 first, SKMatrix44 second) => first * second;

    public readonly SKMatrix44 PreConcat(SKMatrix44 matrix) => this * matrix;

    public readonly SKMatrix44 PostConcat(SKMatrix44 matrix) => matrix * this;

    public static void Concat(ref SKMatrix44 target, SKMatrix44 first, SKMatrix44 second) =>
        target = first * second;

    public static SKMatrix44 Negate(SKMatrix44 value) => -value;

    public static SKMatrix44 Add(SKMatrix44 value1, SKMatrix44 value2) => value1 + value2;

    public static SKMatrix44 Subtract(SKMatrix44 value1, SKMatrix44 value2) => value1 - value2;

    public static SKMatrix44 Multiply(SKMatrix44 value1, SKMatrix44 value2) => value1 * value2;

    public static SKMatrix44 Multiply(SKMatrix44 value1, float value2) => value1 * value2;

    public readonly bool Equals(SKMatrix44 obj) =>
        _m00 == obj._m00 &&
        _m01 == obj._m01 &&
        _m02 == obj._m02 &&
        _m03 == obj._m03 &&
        _m10 == obj._m10 &&
        _m11 == obj._m11 &&
        _m12 == obj._m12 &&
        _m13 == obj._m13 &&
        _m20 == obj._m20 &&
        _m21 == obj._m21 &&
        _m22 == obj._m22 &&
        _m23 == obj._m23 &&
        _m30 == obj._m30 &&
        _m31 == obj._m31 &&
        _m32 == obj._m32 &&
        _m33 == obj._m33;

    public override readonly bool Equals(object? obj) => obj is SKMatrix44 other && Equals(other);

    public static bool operator ==(SKMatrix44 left, SKMatrix44 right) => left.Equals(right);

    public static bool operator !=(SKMatrix44 left, SKMatrix44 right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_m00);
        hash.Add(_m01);
        hash.Add(_m02);
        hash.Add(_m03);
        hash.Add(_m10);
        hash.Add(_m11);
        hash.Add(_m12);
        hash.Add(_m13);
        hash.Add(_m20);
        hash.Add(_m21);
        hash.Add(_m22);
        hash.Add(_m23);
        hash.Add(_m30);
        hash.Add(_m31);
        hash.Add(_m32);
        hash.Add(_m33);
        return hash.ToHashCode();
    }

    public static SKMatrix44 operator -(SKMatrix44 value) => -(Matrix4x4)value;

    public static SKMatrix44 operator +(SKMatrix44 value1, SKMatrix44 value2) =>
        (Matrix4x4)value1 + (Matrix4x4)value2;

    public static SKMatrix44 operator -(SKMatrix44 value1, SKMatrix44 value2) =>
        (Matrix4x4)value1 - (Matrix4x4)value2;

    public static SKMatrix44 operator *(SKMatrix44 value1, SKMatrix44 value2) =>
        (Matrix4x4)value1 * (Matrix4x4)value2;

    public static SKMatrix44 operator *(SKMatrix44 value1, float value2) =>
        (Matrix4x4)value1 * value2;

    public static implicit operator SKMatrix44(SKMatrix matrix) => new(
        matrix.ScaleX,
        matrix.SkewY,
        0f,
        matrix.Persp0,
        matrix.SkewX,
        matrix.ScaleY,
        0f,
        matrix.Persp1,
        0f,
        0f,
        1f,
        0f,
        matrix.TransX,
        matrix.TransY,
        0f,
        matrix.Persp2);

    public static implicit operator Matrix4x4(SKMatrix44 matrix) => matrix.ToMatrix4x4();

    public static implicit operator SKMatrix44(Matrix4x4 matrix) => FromMatrix4x4(matrix);

    internal readonly Matrix4x4 ToMatrix4x4() => new(
        _m00, _m01, _m02, _m03,
        _m10, _m11, _m12, _m13,
        _m20, _m21, _m22, _m23,
        _m30, _m31, _m32, _m33);

    internal static SKMatrix44 FromMatrix4x4(Matrix4x4 matrix) => new(
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44);
}
