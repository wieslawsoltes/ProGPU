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
    private Matrix3x2 _matrix;
    private bool _disposed;

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
        if (plgpts.Length != 3 || rect.Width == 0f || rect.Height == 0f)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(plgpts));
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
            return _matrix.IsIdentity;
        }
    }

    public bool IsInvertible
    {
        get
        {
            ThrowIfDisposed();
            return Matrix3x2.Invert(_matrix, out _);
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
        Apply(matrix.Value, order);
    }

    public void Invert()
    {
        ThrowIfDisposed();
        if (!Matrix3x2.Invert(_matrix, out Matrix3x2 result))
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        _matrix = result;
    }

    public void Reset()
    {
        ThrowIfDisposed();
        _matrix = Matrix3x2.Identity;
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
        return new Matrix(_matrix);
    }

    public override bool Equals(object? obj)
        => obj is Matrix other && !_disposed && !other._disposed && _matrix.Equals(other._matrix);

    public override int GetHashCode() => _disposed ? 0 : _matrix.GetHashCode();

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
            ? operation * _matrix
            : _matrix * operation;
    }

    private void TransformPointsCore(Span<PointF> points, bool includeTranslation)
    {
        ThrowIfDisposed();
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

    private static void ValidateMatrixOrder(MatrixOrder order)
    {
        if (order is not MatrixOrder.Prepend and not MatrixOrder.Append)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(order));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
