using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SkiaSharp;

public partial struct SKMatrix
{
    internal const float DegreesToRadians = MathF.PI / 180f;
    private const float PerspectiveNearPlane = 1f / 16384f;
    private const float DeterminantTolerance = 1f / 68719476736f;

    private readonly struct HomogeneousPoint
    {
        public HomogeneousPoint(float x, float y, float w)
        {
            X = x;
            Y = y;
            W = w;
        }

        public float X { get; }
        public float Y { get; }
        public float W { get; }
    }

    public readonly bool IsIdentity => Equals(Identity);

    public float[] Values
    {
        readonly get =>
        [
            _scaleX, _skewX, _transX,
            _skewY, _scaleY, _transY,
            _persp0, _persp1, _persp2
        ];
        set
        {
            ArgumentNullException.ThrowIfNull(value, "Values");
            if (value.Length != 9)
            {
                throw new ArgumentException("The matrix array must have a length of 9.", "Values");
            }

            SetValues(value);
        }
    }

    public readonly bool IsInvertible => TryInvert(out _);

    public SKMatrix(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != 9)
        {
            throw new ArgumentException("The matrix array must have a length of 9.", nameof(values));
        }

        _scaleX = values[0];
        _skewX = values[1];
        _transX = values[2];
        _skewY = values[3];
        _scaleY = values[4];
        _transY = values[5];
        _persp0 = values[6];
        _persp1 = values[7];
        _persp2 = values[8];
    }

    private void SetValues(float[] values)
    {
        _scaleX = values[0];
        _skewX = values[1];
        _transX = values[2];
        _skewY = values[3];
        _scaleY = values[4];
        _transY = values[5];
        _persp0 = values[6];
        _persp1 = values[7];
        _persp2 = values[8];
    }

    public readonly void GetValues(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != 9)
        {
            throw new ArgumentException("The matrix array must have a length of 9.", nameof(values));
        }

        values[0] = _scaleX;
        values[1] = _skewX;
        values[2] = _transX;
        values[3] = _skewY;
        values[4] = _scaleY;
        values[5] = _transY;
        values[6] = _persp0;
        values[7] = _persp1;
        values[8] = _persp2;
    }

    public static SKMatrix CreateIdentity() => Identity;

    public static SKMatrix CreateTranslation(float x, float y) =>
        x == 0f && y == 0f
            ? Identity
            : new SKMatrix(1f, 0f, x, 0f, 1f, y, 0f, 0f, 1f);

    public static SKMatrix CreateScale(float x, float y) =>
        x == 1f && y == 1f
            ? Identity
            : new SKMatrix(x, 0f, 0f, 0f, y, 0f, 0f, 0f, 1f);

    public static SKMatrix CreateScale(float x, float y, float pivotX, float pivotY) =>
        x == 1f && y == 1f
            ? Identity
            : new SKMatrix(
                x,
                0f,
                pivotX - x * pivotX,
                0f,
                y,
                pivotY - y * pivotY,
                0f,
                0f,
                1f);

    public static SKMatrix CreateRotation(float radians)
    {
        if (radians == 0f)
        {
            return Identity;
        }

        var matrix = Identity;
        SetSinCos(ref matrix, (float)Math.Sin(radians), (float)Math.Cos(radians));
        return matrix;
    }

    public static SKMatrix CreateRotation(float radians, float pivotX, float pivotY)
    {
        if (radians == 0f)
        {
            return Identity;
        }

        var matrix = Identity;
        SetSinCos(
            ref matrix,
            (float)Math.Sin(radians),
            (float)Math.Cos(radians),
            pivotX,
            pivotY);
        return matrix;
    }

    public static SKMatrix CreateRotationDegrees(float degrees) =>
        degrees == 0f ? Identity : CreateRotation(degrees * DegreesToRadians);

    public static SKMatrix CreateRotationDegrees(float degrees, float pivotX, float pivotY) =>
        degrees == 0f
            ? Identity
            : CreateRotation(degrees * DegreesToRadians, pivotX, pivotY);

    public static SKMatrix CreateSkew(float x, float y) =>
        x == 0f && y == 0f
            ? Identity
            : new SKMatrix(1f, x, 0f, y, 1f, 0f, 0f, 0f, 1f);

    public static SKMatrix CreateScaleTranslation(float sx, float sy, float tx, float ty) =>
        sx == 0f && sy == 0f && tx == 0f && ty == 0f
            ? Identity
            : new SKMatrix(sx, 0f, tx, 0f, sy, ty, 0f, 0f, 1f);

    public readonly bool TryInvert(out SKMatrix inverse)
    {
        if (IsIdentity)
        {
            inverse = Identity;
            return true;
        }

        inverse = Empty;

        if (!HasPerspective && _skewX == 0f && _skewY == 0f)
        {
            var inverseX = 1f / _scaleX;
            var inverseY = 1f / _scaleY;
            var inverseTranslationX = -_transX * inverseX;
            var inverseTranslationY = -_transY * inverseY;
            if (!float.IsFinite(inverseX) || !float.IsFinite(inverseY) ||
                !float.IsFinite(inverseTranslationX) || !float.IsFinite(inverseTranslationY))
            {
                return false;
            }

            inverse = new SKMatrix(
                inverseX, 0f, inverseTranslationX,
                0f, inverseY, inverseTranslationY,
                0f, 0f, 1f);
            return true;
        }

        var determinant = HasPerspective
            ? _scaleX * Cross(_scaleY, _persp2, _transY, _persp1) +
              _skewX * Cross(_transY, _persp0, _skewY, _persp2) +
              _transX * Cross(_skewY, _persp1, _scaleY, _persp0)
            : Cross(_scaleX, _scaleY, _skewX, _skewY);
        if (!double.IsFinite(determinant) || Math.Abs((float)determinant) <= DeterminantTolerance)
        {
            return false;
        }

        var inverseDeterminant = 1d / determinant;
        inverse = HasPerspective
            ? new SKMatrix(
                ScaleCross(_scaleY, _persp2, _transY, _persp1, inverseDeterminant),
                ScaleCross(_transX, _persp1, _skewX, _persp2, inverseDeterminant),
                ScaleCross(_skewX, _transY, _transX, _scaleY, inverseDeterminant),
                ScaleCross(_transY, _persp0, _skewY, _persp2, inverseDeterminant),
                ScaleCross(_scaleX, _persp2, _transX, _persp0, inverseDeterminant),
                ScaleCross(_transX, _skewY, _scaleX, _transY, inverseDeterminant),
                ScaleCross(_skewY, _persp1, _scaleY, _persp0, inverseDeterminant),
                ScaleCross(_skewX, _persp0, _scaleX, _persp1, inverseDeterminant),
                ScaleCross(_scaleX, _scaleY, _skewX, _skewY, inverseDeterminant))
            : new SKMatrix(
                (float)(_scaleY * inverseDeterminant),
                (float)(-_skewX * inverseDeterminant),
                ScaleCross(_skewX, _transY, _scaleY, _transX, inverseDeterminant),
                (float)(-_skewY * inverseDeterminant),
                (float)(_scaleX * inverseDeterminant),
                ScaleCross(_skewY, _transX, _scaleX, _transY, inverseDeterminant),
                0f,
                0f,
                1f);

        if (inverse.AreValuesFinite)
        {
            return true;
        }

        inverse = Empty;
        return false;
    }

    public readonly SKMatrix Invert() => TryInvert(out var inverse) ? inverse : Empty;

    public static SKMatrix Concat(SKMatrix first, SKMatrix second)
    {
        if (first.IsIdentity)
        {
            return second;
        }

        if (second.IsIdentity)
        {
            return first;
        }

        if (!first.HasPerspective && !second.HasPerspective)
        {
            return new SKMatrix(
                MultiplyAdd(first._scaleX, second._scaleX, first._skewX, second._skewY),
                MultiplyAdd(first._scaleX, second._skewX, first._skewX, second._scaleY),
                MultiplyAdd(first._scaleX, second._transX, first._skewX, second._transY) + first._transX,
                MultiplyAdd(first._skewY, second._scaleX, first._scaleY, second._skewY),
                MultiplyAdd(first._skewY, second._skewX, first._scaleY, second._scaleY),
                MultiplyAdd(first._skewY, second._transX, first._scaleY, second._transY) + first._transY,
                0f,
                0f,
                1f);
        }

        return new SKMatrix(
            RowColumn(first._scaleX, first._skewX, first._transX, second._scaleX, second._skewY, second._persp0),
            RowColumn(first._scaleX, first._skewX, first._transX, second._skewX, second._scaleY, second._persp1),
            RowColumn(first._scaleX, first._skewX, first._transX, second._transX, second._transY, second._persp2),
            RowColumn(first._skewY, first._scaleY, first._transY, second._scaleX, second._skewY, second._persp0),
            RowColumn(first._skewY, first._scaleY, first._transY, second._skewX, second._scaleY, second._persp1),
            RowColumn(first._skewY, first._scaleY, first._transY, second._transX, second._transY, second._persp2),
            RowColumn(first._persp0, first._persp1, first._persp2, second._scaleX, second._skewY, second._persp0),
            RowColumn(first._persp0, first._persp1, first._persp2, second._skewX, second._scaleY, second._persp1),
            RowColumn(first._persp0, first._persp1, first._persp2, second._transX, second._transY, second._persp2));
    }

    public readonly SKMatrix PreConcat(SKMatrix matrix) => Concat(this, matrix);

    public readonly SKMatrix PostConcat(SKMatrix matrix) => Concat(matrix, this);

    public static void Concat(ref SKMatrix target, SKMatrix first, SKMatrix second) =>
        target = Concat(first, second);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly SKRect MapRect(SKRect source)
    {
        if (!HasPerspective)
        {
            var leftScale = source.Left * _scaleX;
            var rightScale = source.Right * _scaleX;
            var topSkew = source.Top * _skewX;
            var bottomSkew = source.Bottom * _skewX;
            var leftSkew = source.Left * _skewY;
            var rightSkew = source.Right * _skewY;
            var topScale = source.Top * _scaleY;
            var bottomScale = source.Bottom * _scaleY;
            return new SKRect(
                MathF.Min(leftScale, rightScale) + MathF.Min(topSkew, bottomSkew) + _transX,
                MathF.Min(leftSkew, rightSkew) + MathF.Min(topScale, bottomScale) + _transY,
                MathF.Max(leftScale, rightScale) + MathF.Max(topSkew, bottomSkew) + _transX,
                MathF.Max(leftSkew, rightSkew) + MathF.Max(topScale, bottomScale) + _transY);
        }

        Span<SKPoint> corners =
        [
            new SKPoint(source.Left, source.Top),
            new SKPoint(source.Right, source.Top),
            new SKPoint(source.Right, source.Bottom),
            new SKPoint(source.Left, source.Bottom)
        ];

        return MapPerspectiveRect(corners);
    }

    public readonly SKPoint MapPoint(SKPoint point) => MapPoint(point.X, point.Y);

    public readonly SKPoint MapPoint(float x, float y)
    {
        var mappedX = x * _scaleX + y * _skewX + _transX;
        var mappedY = x * _skewY + y * _scaleY + _transY;
        if (!HasPerspective)
        {
            return new SKPoint(mappedX, mappedY);
        }

        var w = x * _persp0 + y * _persp1 + _persp2;
        var inverseW = w == 0f ? 0f : 1f / w;
        return new SKPoint(mappedX * inverseW, mappedY * inverseW);
    }

    public readonly void MapPoints(Span<SKPoint> result, ReadOnlySpan<SKPoint> points)
    {
        if (result.Length != points.Length)
        {
            throw new ArgumentException("Buffers must be the same size.");
        }

        for (var index = 0; index < points.Length; index++)
        {
            result[index] = MapPoint(points[index]);
        }
    }

    public readonly void MapPoints(SKPoint[] result, SKPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(points);
        MapPoints(result.AsSpan(), points.AsSpan());
    }

    public readonly SKPoint[] MapPoints(SKPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var result = new SKPoint[points.Length];
        MapPoints(result, points);
        return result;
    }

    public readonly SKPoint MapVector(SKPoint vector) => MapVector(vector.X, vector.Y);

    public readonly SKPoint MapVector(float x, float y)
    {
        if (!HasPerspective)
        {
            return new SKPoint(
                x * _scaleX + y * _skewX,
                x * _skewY + y * _scaleY);
        }

        return MapPoint(x, y) - MapPoint(0f, 0f);
    }

    public readonly void MapVectors(Span<SKPoint> result, ReadOnlySpan<SKPoint> vectors)
    {
        if (result.Length != vectors.Length)
        {
            throw new ArgumentException("Buffers must be the same size.");
        }

        for (var index = vectors.Length - 1; index >= 0; index--)
        {
            result[index] = MapVector(vectors[index]);
        }
    }

    public readonly void MapVectors(SKPoint[] result, SKPoint[] vectors)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(vectors);
        MapVectors(result.AsSpan(), vectors.AsSpan());
    }

    public readonly SKPoint[] MapVectors(SKPoint[] vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var result = new SKPoint[vectors.Length];
        MapVectors(result, vectors);
        return result;
    }

    public readonly float MapRadius(float radius)
    {
        var first = MapVector(radius, 0f);
        var second = MapVector(0f, radius);
        return MathF.Sqrt(first.Length * second.Length);
    }

    internal readonly Matrix4x4 ToMatrix4x4() => new(
        _scaleX, _skewY, 0f, _persp0,
        _skewX, _scaleY, 0f, _persp1,
        0f, 0f, 1f, 0f,
        _transX, _transY, 0f, _persp2);

    internal static SKMatrix FromMatrix4x4(Matrix4x4 matrix) => new(
        matrix.M11,
        matrix.M21,
        matrix.M41,
        matrix.M12,
        matrix.M22,
        matrix.M42,
        matrix.M14,
        matrix.M24,
        matrix.M44);

    public readonly bool Equals(SKMatrix obj) =>
        _scaleX == obj._scaleX &&
        _skewX == obj._skewX &&
        _transX == obj._transX &&
        _skewY == obj._skewY &&
        _scaleY == obj._scaleY &&
        _transY == obj._transY &&
        _persp0 == obj._persp0 &&
        _persp1 == obj._persp1 &&
        _persp2 == obj._persp2;

    public override readonly bool Equals(object? obj) => obj is SKMatrix other && Equals(other);

    public static bool operator ==(SKMatrix left, SKMatrix right) => left.Equals(right);

    public static bool operator !=(SKMatrix left, SKMatrix right) => !left.Equals(right);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_scaleX);
        hash.Add(_skewX);
        hash.Add(_transX);
        hash.Add(_skewY);
        hash.Add(_scaleY);
        hash.Add(_transY);
        hash.Add(_persp0);
        hash.Add(_persp1);
        hash.Add(_persp2);
        return hash.ToHashCode();
    }

    private readonly bool HasPerspective => _persp0 != 0f || _persp1 != 0f || _persp2 != 1f;

    private readonly bool AreValuesFinite =>
        float.IsFinite(_scaleX) &&
        float.IsFinite(_skewX) &&
        float.IsFinite(_transX) &&
        float.IsFinite(_skewY) &&
        float.IsFinite(_scaleY) &&
        float.IsFinite(_transY) &&
        float.IsFinite(_persp0) &&
        float.IsFinite(_persp1) &&
        float.IsFinite(_persp2);

    private readonly SKRect MapPerspectiveRect(ReadOnlySpan<SKPoint> corners)
    {
        Span<HomogeneousPoint> input = stackalloc HomogeneousPoint[8];
        for (var index = 0; index < corners.Length; index++)
        {
            var point = corners[index];
            input[index] = new HomogeneousPoint(
                point.X * _scaleX + point.Y * _skewX + _transX,
                point.X * _skewY + point.Y * _scaleY + _transY,
                point.X * _persp0 + point.Y * _persp1 + _persp2);
        }

        Span<HomogeneousPoint> clipped = stackalloc HomogeneousPoint[8];
        var count = ClipPerspectiveNearPlane(input[..corners.Length], clipped);
        if (count == 0)
        {
            return SKRect.Empty;
        }

        Span<SKPoint> projected = stackalloc SKPoint[8];
        for (var index = 0; index < count; index++)
        {
            var point = clipped[index];
            var inverseW = 1f / point.W;
            projected[index] = new SKPoint(point.X * inverseW, point.Y * inverseW);
        }

        return GetBounds(projected[..count]);
    }

    private static int ClipPerspectiveNearPlane(
        ReadOnlySpan<HomogeneousPoint> input,
        Span<HomogeneousPoint> output)
    {
        var count = 0;
        var previous = input[^1];
        var previousInside = previous.W >= PerspectiveNearPlane;
        foreach (var current in input)
        {
            var currentInside = current.W >= PerspectiveNearPlane;
            if (previousInside != currentInside)
            {
                var amount = (PerspectiveNearPlane - previous.W) / (current.W - previous.W);
                output[count++] = new HomogeneousPoint(
                    previous.X + (current.X - previous.X) * amount,
                    previous.Y + (current.Y - previous.Y) * amount,
                    PerspectiveNearPlane);
            }

            if (currentInside)
            {
                output[count++] = current;
            }

            previous = current;
            previousInside = currentInside;
        }

        return count;
    }

    private static SKRect GetBounds(ReadOnlySpan<SKPoint> points)
    {
        var left = points[0].X;
        var top = points[0].Y;
        var right = left;
        var bottom = top;
        for (var index = 1; index < points.Length; index++)
        {
            var point = points[index];
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return new SKRect(left, top, right, bottom);
    }

    private static void SetSinCos(ref SKMatrix matrix, float sin, float cos)
    {
        matrix = new SKMatrix(cos, -sin, 0f, sin, cos, 0f, 0f, 0f, 1f);
    }

    private static void SetSinCos(
        ref SKMatrix matrix,
        float sin,
        float cos,
        float pivotX,
        float pivotY)
    {
        var oneMinusCos = 1f - cos;
        matrix = new SKMatrix(
            cos,
            -sin,
            Dot(sin, pivotY, oneMinusCos, pivotX),
            sin,
            cos,
            Dot(-sin, pivotX, oneMinusCos, pivotY),
            0f,
            0f,
            1f);
    }

    private static float Dot(float a, float b, float c, float d) => a * b + c * d;

    private static double Cross(float a, float b, float c, float d) =>
        (double)a * b - (double)c * d;

    private static float ScaleCross(float a, float b, float c, float d, double scale) =>
        (float)(Cross(a, b, c, d) * scale);

    private static float MultiplyAdd(float a, float b, float c, float d) =>
        (float)((double)a * b + (double)c * d);

    private static float RowColumn(float a, float b, float c, float x, float y, float z) =>
        a * x + b * y + c * z;
}
