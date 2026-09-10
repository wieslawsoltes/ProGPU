using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Drawing.Drawing2D;

public enum MatrixOrder
{
    Prepend = 0,
    Append = 1
}

public sealed class Matrix : MarshalByRefObject, IDisposable
{
    // GDI+ considers the small rounding error produced by composing orthogonal
    // rotations to be identity. The next upstream boundary fixture (0.0009f)
    // must remain non-identity.
    private const float IdentityTolerance = 0.00011f;

    private Matrix3x2 _matrix;
    private bool _disposed;
    private bool _rotateAtIdentityQuirk;

    public Matrix()
        : this(Matrix3x2.Identity)
    {
    }

    public Matrix(float m11, float m12, float m21, float m22, float dx, float dy)
        : this(new Matrix3x2(m11, m12, m21, m22, dx, dy))
    {
    }

    public Matrix(Matrix3x2 matrix)
    {
        _matrix = matrix;
    }

    public Matrix(Rectangle rect, params Point[] plgpts)
        : this(
            new RectangleF(rect.X, rect.Y, rect.Width, rect.Height),
            ConvertPoints(plgpts))
    {
    }

    public Matrix(RectangleF rect, params PointF[] plgpts)
    {
        ArgumentNullException.ThrowIfNull(plgpts);
        if (plgpts.Length != 3)
        {
            throw InvalidParameter();
        }

        if (rect.Width == 0f || rect.Height == 0f)
        {
            throw new ExternalException("A generic error occurred in GDI+.");
        }

        PointF upperLeft = plgpts[0];
        PointF upperRight = plgpts[1];
        PointF lowerLeft = plgpts[2];

        float m11 = (upperRight.X - upperLeft.X) / rect.Width;
        float m12 = (upperRight.Y - upperLeft.Y) / rect.Width;
        float m21 = (lowerLeft.X - upperLeft.X) / rect.Height;
        float m22 = (lowerLeft.Y - upperLeft.Y) / rect.Height;
        float dx = upperLeft.X - (rect.X * m11) - (rect.Y * m21);
        float dy = upperLeft.Y - (rect.X * m12) - (rect.Y * m22);
        _matrix = new Matrix3x2(m11, m12, m21, m22, dx, dy);
    }

    // ProGPU's typed bridge predates MatrixElements. Keep this alias so existing
    // renderer code can consume the same value without reflection or field probes.
    public Matrix3x2 Value
    {
        get
        {
            ThrowIfDisposed();
            return _matrix;
        }
    }

    public Matrix3x2 MatrixElements
    {
        get
        {
            ThrowIfDisposed();
            return _matrix;
        }
        set
        {
            ThrowIfDisposed();
            _matrix = value;
            _rotateAtIdentityQuirk = false;
        }
    }

    internal bool TryGetMatrixElements(out Matrix3x2 matrix)
    {
        matrix = _matrix;
        return !_disposed;
    }

    public float[] Elements
    {
        get
        {
            ThrowIfDisposed();
            return [_matrix.M11, _matrix.M12, _matrix.M21, _matrix.M22, _matrix.M31, _matrix.M32];
        }
    }

    public float OffsetX
    {
        get
        {
            ThrowIfDisposed();
            return _matrix.M31;
        }
    }

    public float OffsetY
    {
        get
        {
            ThrowIfDisposed();
            return _matrix.M32;
        }
    }

    public bool IsIdentity
    {
        get
        {
            ThrowIfDisposed();
            return !_rotateAtIdentityQuirk
                && IsApproximately(_matrix.M11, 1f)
                && IsApproximately(_matrix.M12, 0f)
                && IsApproximately(_matrix.M21, 0f)
                && IsApproximately(_matrix.M22, 1f)
                && IsApproximately(_matrix.M31, 0f)
                && IsApproximately(_matrix.M32, 0f);
        }
    }

    public bool IsInvertible
    {
        get
        {
            ThrowIfDisposed();
            return IsFinite(_matrix) && Matrix3x2.Invert(_matrix, out _);
        }
    }

    public void Translate(float offsetX, float offsetY)
        => Translate(offsetX, offsetY, MatrixOrder.Prepend);

    public void Translate(float offsetX, float offsetY, MatrixOrder order)
        => Apply(Matrix3x2.CreateTranslation(offsetX, offsetY), order);

    public void Scale(float scaleX, float scaleY)
        => Scale(scaleX, scaleY, MatrixOrder.Prepend);

    public void Scale(float scaleX, float scaleY, MatrixOrder order)
        => Apply(Matrix3x2.CreateScale(scaleX, scaleY), order);

    public void Rotate(float angle)
        => Rotate(angle, MatrixOrder.Prepend);

    public void Rotate(float angle, MatrixOrder order)
        => Apply(Matrix3x2.CreateRotation(DegreesToRadians(angle)), order);

    public void RotateAt(float angle, PointF point)
        => RotateAt(angle, point, MatrixOrder.Prepend);

    public void RotateAt(float angle, PointF point, MatrixOrder order)
    {
        Matrix3x2 rotation =
            Matrix3x2.CreateTranslation(-point.X, -point.Y)
            * Matrix3x2.CreateRotation(DegreesToRadians(angle))
            * Matrix3x2.CreateTranslation(point.X, point.Y);
        Apply(rotation, order);
        // Native GDI+ reports a RotateAt-composed matrix as non-identity even
        // when its exported elements round back to identity (for example,
        // Rotate(90) followed by RotateAt(270, PointF.Empty)).
        _rotateAtIdentityQuirk = true;
    }

    public void Shear(float shearX, float shearY)
        => Shear(shearX, shearY, MatrixOrder.Prepend);

    public void Shear(float shearX, float shearY, MatrixOrder order)
        => Apply(new Matrix3x2(1f, shearY, shearX, 1f, 0f, 0f), order);

    public void Multiply(Matrix matrix)
        => Multiply(matrix, MatrixOrder.Prepend);

    public void Multiply(Matrix matrix, MatrixOrder order)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ThrowIfDisposed();
        matrix.ThrowIfDisposed();
        ValidateMatrixOrder(order);
        if (ReferenceEquals(this, matrix))
        {
            throw new InvalidOperationException("A matrix cannot be multiplied by itself.");
        }

        Apply(matrix.Value, order);
    }

    public void Invert()
    {
        ThrowIfDisposed();
        if (!IsFinite(_matrix) || !Matrix3x2.Invert(_matrix, out Matrix3x2 result))
        {
            throw InvalidParameter();
        }

        _matrix = result;
    }

    public void Reset()
    {
        ThrowIfDisposed();
        _matrix = Matrix3x2.Identity;
        _rotateAtIdentityQuirk = false;
    }

    public void TransformPoints(params PointF[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPointsCore(pts.AsSpan(), includeTranslation: true);
    }

    public void TransformPoints(scoped ReadOnlySpan<PointF> pts)
        => TransformPointsCore(AsWritableSpan(pts), includeTranslation: true);

    public void TransformPoints(params Point[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPointsCore(pts.AsSpan(), includeTranslation: true);
    }

    public void TransformPoints(scoped ReadOnlySpan<Point> pts)
        => TransformPointsCore(AsWritableSpan(pts), includeTranslation: true);

    public void TransformVectors(params PointF[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPointsCore(pts.AsSpan(), includeTranslation: false);
    }

    public void TransformVectors(scoped ReadOnlySpan<PointF> pts)
        => TransformPointsCore(AsWritableSpan(pts), includeTranslation: false);

    public void TransformVectors(params Point[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPointsCore(pts.AsSpan(), includeTranslation: false);
    }

    public void TransformVectors(scoped ReadOnlySpan<Point> pts)
        => TransformPointsCore(AsWritableSpan(pts), includeTranslation: false);

    public void VectorTransformPoints(params Point[] pts)
        => TransformVectors(pts);

    public void VectorTransformPoints(scoped ReadOnlySpan<Point> pts)
        => TransformVectors(pts);

    public Matrix Clone()
    {
        ThrowIfDisposed();
        return new Matrix(_matrix)
        {
            _rotateAtIdentityQuirk = _rotateAtIdentityQuirk
        };
    }

    public override bool Equals(object? obj)
    {
        ThrowIfDisposed();
        if (obj is not Matrix other)
        {
            return false;
        }

        other.ThrowIfDisposed();
        return _matrix.Equals(other._matrix);
    }

    // Native GDI+ matrices retain object-identity hashing even though Equals
    // compares elements. Preserve that observable desktop behavior.
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public void Dispose()
    {
        _disposed = true;
        _matrix = default;
        GC.SuppressFinalize(this);
    }

    private static PointF[] ConvertPoints(Point[] plgpts)
    {
        ArgumentNullException.ThrowIfNull(plgpts);
        var result = new PointF[plgpts.Length];
        for (int index = 0; index < plgpts.Length; index++)
        {
            result[index] = plgpts[index];
        }

        return result;
    }

    private void Apply(Matrix3x2 operation, MatrixOrder order)
    {
        ThrowIfDisposed();
        ValidateMatrixOrder(order);
        _matrix = order == MatrixOrder.Prepend
            ? MultiplyWithGdiPlusOverflow(operation, _matrix)
            : MultiplyWithGdiPlusOverflow(_matrix, operation);
    }

    private void TransformPointsCore(Span<PointF> points, bool includeTranslation)
    {
        ThrowIfDisposed();
        if (points.IsEmpty)
        {
            throw InvalidParameter();
        }

        Matrix3x2 transform = includeTranslation
            ? _matrix
            : new Matrix3x2(_matrix.M11, _matrix.M12, _matrix.M21, _matrix.M22, 0f, 0f);

        for (int index = 0; index < points.Length; index++)
        {
            Vector2 transformed = Vector2.Transform(new Vector2(points[index].X, points[index].Y), transform);
            points[index] = new PointF(transformed.X, transformed.Y);
        }
    }

    private void TransformPointsCore(Span<Point> points, bool includeTranslation)
    {
        ThrowIfDisposed();
        if (points.IsEmpty)
        {
            throw InvalidParameter();
        }

        Matrix3x2 transform = includeTranslation
            ? _matrix
            : new Matrix3x2(_matrix.M11, _matrix.M12, _matrix.M21, _matrix.M22, 0f, 0f);

        for (int index = 0; index < points.Length; index++)
        {
            Vector2 transformed = Vector2.Transform(new Vector2(points[index].X, points[index].Y), transform);
            points[index] = Point.Round(new PointF(transformed.X, transformed.Y));
        }
    }

    private static Span<T> AsWritableSpan<T>(ReadOnlySpan<T> source)
    {
        ref T first = ref Unsafe.AsRef(in MemoryMarshal.GetReference(source));
        return MemoryMarshal.CreateSpan(ref first, source.Length);
    }

    private static float DegreesToRadians(float angle) => angle * (MathF.PI / 180f);

    private static bool IsApproximately(float value, float expected)
        => float.IsFinite(value) && MathF.Abs(value - expected) <= IdentityTolerance;

    private static bool IsFinite(Matrix3x2 matrix)
        => float.IsFinite(matrix.M11)
            && float.IsFinite(matrix.M12)
            && float.IsFinite(matrix.M21)
            && float.IsFinite(matrix.M22)
            && float.IsFinite(matrix.M31)
            && float.IsFinite(matrix.M32);

    internal static Matrix3x2 MultiplyWithGdiPlusOverflow(Matrix3x2 left, Matrix3x2 right)
    {
        Matrix3x2 result = left * right;
        if (!IsFinite(left) || !IsFinite(right) || IsFinite(result))
        {
            return result;
        }

        // Matrix3x2 performs single-precision arithmetic and therefore exposes
        // IEEE infinity on finite overflow. GDI+ instead saturates those results.
        // Recompute only overflowing elements in double precision so ordinary
        // operations retain their existing single-precision rounding.
        return new Matrix3x2(
            ClampOverflow(result.M11, ((double)left.M11 * right.M11) + ((double)left.M12 * right.M21)),
            ClampOverflow(result.M12, ((double)left.M11 * right.M12) + ((double)left.M12 * right.M22)),
            ClampOverflow(result.M21, ((double)left.M21 * right.M11) + ((double)left.M22 * right.M21)),
            ClampOverflow(result.M22, ((double)left.M21 * right.M12) + ((double)left.M22 * right.M22)),
            ClampOverflow(result.M31, ((double)left.M31 * right.M11) + ((double)left.M32 * right.M21) + right.M31),
            ClampOverflow(result.M32, ((double)left.M31 * right.M12) + ((double)left.M32 * right.M22) + right.M32));
    }

    private static float ClampOverflow(float singlePrecisionResult, double exactResult)
    {
        if (float.IsFinite(singlePrecisionResult))
        {
            return singlePrecisionResult;
        }

        return exactResult > float.MaxValue
            ? float.MaxValue
            : exactResult < float.MinValue
                ? float.MinValue
                : (float)exactResult;
    }

    private static void ValidateMatrixOrder(MatrixOrder order)
    {
        if (order is not MatrixOrder.Prepend and not MatrixOrder.Append)
        {
            throw InvalidParameter();
        }
    }

    private static ArgumentException InvalidParameter() => new("Parameter is not valid.");

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw InvalidParameter();
        }
    }
}
