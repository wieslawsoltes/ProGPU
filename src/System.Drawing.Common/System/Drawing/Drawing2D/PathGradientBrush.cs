using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;

namespace System.Drawing.Drawing2D;

public sealed class PathGradientBrush : Brush
{
    private readonly PointF[] _boundaryPoints;
    private readonly RectangleF _rectangle;
    private Color _centerColor = Color.Black;
    private Color[] _surroundColors = [Color.White];
    private PointF _centerPoint;
    private PointF _focusScales;
    private Blend? _blend;
    private ColorBlend? _interpolationColors;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private WrapMode _wrapMode;
    private bool _disposed;

    public PathGradientBrush(params PointF[] points)
        : this(points, WrapMode.Clamp)
    {
    }

    public PathGradientBrush(params ReadOnlySpan<PointF> points)
        : this(WrapMode.Clamp, points)
    {
    }

    public PathGradientBrush(PointF[] points, WrapMode wrapMode)
        : this(
            wrapMode,
            (ReadOnlySpan<PointF>)(points ?? throw new ArgumentNullException(nameof(points))))
    {
    }

    public PathGradientBrush(WrapMode wrapMode, params ReadOnlySpan<PointF> points)
        : this(CopyAndValidate(points), wrapMode, validated: true)
    {
    }

    public PathGradientBrush(params Point[] points)
        : this(points, WrapMode.Clamp)
    {
    }

    public PathGradientBrush(params ReadOnlySpan<Point> points)
        : this(WrapMode.Clamp, points)
    {
    }

    public PathGradientBrush(Point[] points, WrapMode wrapMode)
        : this(
            wrapMode,
            (ReadOnlySpan<Point>)(points ?? throw new ArgumentNullException(nameof(points))))
    {
    }

    public PathGradientBrush(WrapMode wrapMode, params ReadOnlySpan<Point> points)
        : this(ConvertAndValidate(points), wrapMode, validated: true)
    {
    }

    public PathGradientBrush(GraphicsPath path)
        : this(ExtractBoundary(path), WrapMode.Clamp, validated: true)
    {
        _rectangle = path.GetBounds();
        _centerPoint = new PointF(
            _rectangle.Left + _rectangle.Width * 0.5f,
            _rectangle.Top + _rectangle.Height * 0.5f);
    }

    private PathGradientBrush(PointF[] points, WrapMode wrapMode, bool validated)
    {
        ValidateWrapMode(wrapMode);
        _boundaryPoints = points;
        _rectangle = GetBounds(points);
        _centerPoint = new PointF(
            _rectangle.Left + _rectangle.Width * 0.5f,
            _rectangle.Top + _rectangle.Height * 0.5f);
        _wrapMode = wrapMode;
    }

    public Color CenterColor
    {
        get
        {
            ThrowIfDisposed();
            return _centerColor;
        }
        set
        {
            ThrowIfDisposed();
            _centerColor = value;
        }
    }

    public Color[] SurroundColors
    {
        get
        {
            ThrowIfDisposed();
            return (Color[])_surroundColors.Clone();
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0 || value.Length > _boundaryPoints.Length)
            {
                throw new ArgumentException("Parameter is not valid.", nameof(value));
            }

            _surroundColors = AllColorsEqual(value)
                ? [value[0]]
                : (Color[])value.Clone();
        }
    }

    public PointF CenterPoint
    {
        get
        {
            ThrowIfDisposed();
            return _centerPoint;
        }
        set
        {
            ThrowIfDisposed();
            _centerPoint = value;
        }
    }

    public RectangleF Rectangle
    {
        get
        {
            ThrowIfDisposed();
            return _rectangle;
        }
    }

    public Blend Blend
    {
        get
        {
            ThrowIfDisposed();
            return (_blend ?? CreateDefaultBlend()).CloneBlend();
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            ValidateBlend(value);
            _blend = value.CloneBlend();
            _interpolationColors = null;
        }
    }

    public void SetSigmaBellShape(float focus)
        => SetSigmaBellShape(focus, 1f);

    public void SetSigmaBellShape(float focus, float scale)
    {
        ThrowIfDisposed();
        ValidateFocusAndScale(focus, scale);
        const int samplesPerSide = 256;
        int count = focus is 0f or 1f
            ? samplesPerSide
            : samplesPerSide * 2 - 1;
        var factors = new float[count];
        var positions = new float[count];
        if (focus == 0f)
        {
            for (int index = 0; index < samplesPerSide; index++)
            {
                float unit = index / (samplesPerSide - 1f);
                positions[index] = unit;
                float cosine = MathF.Cos(unit * MathF.PI * 0.5f);
                factors[index] = scale * cosine * cosine;
            }
        }
        else if (focus == 1f)
        {
            for (int index = 0; index < samplesPerSide; index++)
            {
                float unit = index / (samplesPerSide - 1f);
                positions[index] = unit;
                float sine = MathF.Sin(unit * MathF.PI * 0.5f);
                factors[index] = scale * sine * sine;
            }
        }
        else
        {
            for (int index = 0; index < samplesPerSide; index++)
            {
                float unit = index / (samplesPerSide - 1f);
                positions[index] = focus * unit;
                float sine = MathF.Sin(unit * MathF.PI * 0.5f);
                factors[index] = scale * sine * sine;
            }
            for (int index = 1; index < samplesPerSide; index++)
            {
                float unit = index / (samplesPerSide - 1f);
                int destination = samplesPerSide - 1 + index;
                positions[destination] = focus + (1f - focus) * unit;
                float cosine = MathF.Cos(unit * MathF.PI * 0.5f);
                factors[destination] = scale * cosine * cosine;
            }
        }

        Blend = new Blend(count) { Factors = factors, Positions = positions };
    }

    public void SetBlendTriangularShape(float focus)
        => SetBlendTriangularShape(focus, 1f);

    public void SetBlendTriangularShape(float focus, float scale)
    {
        ThrowIfDisposed();
        ValidateFocusAndScale(focus, scale);
        if (focus == 0f)
        {
            Blend = new Blend(2)
            {
                Factors = [scale, 0f],
                Positions = [0f, 1f]
            };
        }
        else if (focus == 1f)
        {
            Blend = new Blend(2)
            {
                Factors = [0f, scale],
                Positions = [0f, 1f]
            };
        }
        else
        {
            Blend = new Blend(3)
            {
                Factors = [0f, scale, 0f],
                Positions = [0f, focus, 1f]
            };
        }
    }

    public ColorBlend InterpolationColors
    {
        get
        {
            ThrowIfDisposed();
            return (_interpolationColors ?? new ColorBlend()).CloneBlend();
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            ValidateColorBlend(value);
            _interpolationColors = value.CloneBlend();
            _blend = null;
        }
    }

    public Matrix Transform
    {
        get
        {
            ThrowIfDisposed();
            return new Matrix(_transform);
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            Matrix3x2 candidate = value.MatrixElements;
            EnsureInvertible(candidate);
            _transform = candidate;
        }
    }

    public void ResetTransform()
    {
        ThrowIfDisposed();
        _transform = Matrix3x2.Identity;
    }

    public void MultiplyTransform(Matrix matrix)
        => MultiplyTransform(matrix, MatrixOrder.Prepend);

    public void MultiplyTransform(Matrix matrix, MatrixOrder order)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.TryGetMatrixElements(out Matrix3x2 elements))
        {
            ApplyTransform(elements, order);
        }
    }

    public void TranslateTransform(float dx, float dy)
        => TranslateTransform(dx, dy, MatrixOrder.Prepend);

    public void TranslateTransform(float dx, float dy, MatrixOrder order)
        => ApplyTransform(Matrix3x2.CreateTranslation(dx, dy), order);

    public void ScaleTransform(float sx, float sy)
        => ScaleTransform(sx, sy, MatrixOrder.Prepend);

    public void ScaleTransform(float sx, float sy, MatrixOrder order)
        => ApplyTransform(Matrix3x2.CreateScale(sx, sy), order);

    public void RotateTransform(float angle)
        => RotateTransform(angle, MatrixOrder.Prepend);

    public void RotateTransform(float angle, MatrixOrder order)
        => ApplyTransform(Matrix3x2.CreateRotation(angle * MathF.PI / 180f), order);

    public PointF FocusScales
    {
        get
        {
            ThrowIfDisposed();
            return _focusScales;
        }
        set
        {
            ThrowIfDisposed();
            _focusScales = value;
        }
    }

    public WrapMode WrapMode
    {
        get
        {
            ThrowIfDisposed();
            return _wrapMode;
        }
        set
        {
            ThrowIfDisposed();
            ValidateWrapMode(value);
            _wrapMode = value;
        }
    }

    public override object Clone()
    {
        ThrowIfDisposed();
        return new PathGradientBrush((PointF[])_boundaryPoints.Clone(), _wrapMode, validated: true)
        {
            _centerColor = _centerColor,
            _surroundColors = (Color[])_surroundColors.Clone(),
            _centerPoint = _centerPoint,
            _focusScales = _focusScales,
            _blend = _blend?.CloneBlend(),
            _interpolationColors = _interpolationColors?.CloneBlend(),
            _transform = _transform
        };
    }

    internal override ProGPU.Vector.Brush ToProGpuBrush()
    {
        ThrowIfDisposed();
        Vector2[] boundary = CreateRetainedBoundary();
        Vector4[] surround = CreateRetainedSurroundColors(boundary.Length);
        ProGPU.Vector.PathGradientBrush native;
        if (_interpolationColors is not null)
        {
            var stops = new ProGPU.Vector.GradientStop[_interpolationColors.Colors.Length];
            for (int index = 0; index < stops.Length; index++)
            {
                stops[index] = new ProGPU.Vector.GradientStop(
                    ToVector(_interpolationColors.Colors[index]),
                    _interpolationColors.Positions[index]);
            }
            native = new ProGPU.Vector.PathGradientBrush(
                boundary,
                surround,
                new Vector2(_centerPoint.X, _centerPoint.Y),
                ToVector(_centerColor),
                stops);
        }
        else
        {
            Blend renderBlend = _blend ?? CreateRenderingDefaultBlend();
            var stops = new ProGPU.Vector.PathGradientBlendStop[renderBlend.Factors.Length];
            float previousPosition = 0f;
            for (int index = 0; index < stops.Length; index++)
            {
                float position = float.IsFinite(renderBlend.Positions[index])
                    ? Math.Clamp(renderBlend.Positions[index], 0f, 1f)
                    : previousPosition;
                position = MathF.Max(previousPosition, position);
                stops[index] = new ProGPU.Vector.PathGradientBlendStop(
                    renderBlend.Factors[index],
                    position);
                previousPosition = position;
            }
            native = new ProGPU.Vector.PathGradientBrush(
                boundary,
                surround,
                new Vector2(_centerPoint.X, _centerPoint.Y),
                ToVector(_centerColor),
                stops);
        }

        native.FocusScales = new Vector2(_focusScales.X, _focusScales.Y);
        native.SpreadMethod = _wrapMode switch
        {
            WrapMode.Tile => ProGPU.Vector.GradientSpreadMethod.Repeat,
            WrapMode.TileFlipX or WrapMode.TileFlipY or WrapMode.TileFlipXY
                => ProGPU.Vector.GradientSpreadMethod.Reflect,
            _ => ProGPU.Vector.GradientSpreadMethod.Pad
        };
        if (Matrix3x2.Invert(_transform, out Matrix3x2 inverse))
        {
            native.CoordinateTransform = ToMatrix4x4(inverse);
        }
        return native;
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _surroundColors = [];
        _blend = null;
        _interpolationColors = null;
        _transform = default;
        base.Dispose(disposing);
    }

    private Vector2[] CreateRetainedBoundary()
    {
        int count = Math.Min(
            _boundaryPoints.Length,
            ProGPU.Vector.PathGradientBrush.MaximumBoundaryPoints);
        var result = new Vector2[count];
        for (int index = 0; index < count; index++)
        {
            int source = _boundaryPoints.Length <= count
                ? index
                : (int)((long)index * _boundaryPoints.Length / count);
            PointF point = _boundaryPoints[source];
            result[index] = new Vector2(point.X, point.Y);
        }
        return result;
    }

    private Vector4[] CreateRetainedSurroundColors(int count)
    {
        var result = new Vector4[count];
        if (_surroundColors.Length == 1)
        {
            Array.Fill(result, ToVector(_surroundColors[0]));
            return result;
        }

        for (int index = 0; index < count; index++)
        {
            float position = index * _surroundColors.Length / (float)count;
            int first = Math.Min((int)position, _surroundColors.Length - 1);
            int second = (first + 1) % _surroundColors.Length;
            result[index] = Vector4.Lerp(
                ToVector(_surroundColors[first]),
                ToVector(_surroundColors[second]),
                position - MathF.Floor(position));
        }
        return result;
    }

    private void ApplyTransform(Matrix3x2 operation, MatrixOrder order)
    {
        ThrowIfDisposed();
        ValidateMatrixOrder(order);
        Matrix3x2 candidate = order == MatrixOrder.Prepend
            ? operation * _transform
            : _transform * operation;
        EnsureInvertible(candidate);
        _transform = candidate;
    }

    private static PointF[] ExtractBoundary(GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.PointCount < 2)
        {
            throw new ExternalException("A generic error occurred in GDI+.");
        }

        using var flattened = (GraphicsPath)path.Clone();
        flattened.Flatten(null, 0.25f);
        PointF[] points = flattened.PathPoints;
        byte[] types = flattened.PathTypes;
        int bestStart = 0;
        int bestCount = 0;
        int currentStart = 0;
        for (int index = 1; index <= points.Length; index++)
        {
            bool boundary = index == points.Length ||
                (types[index] & (byte)PathPointType.PathTypeMask) ==
                    (byte)PathPointType.Start;
            if (!boundary)
            {
                continue;
            }
            int currentCount = index - currentStart;
            if (currentCount > bestCount)
            {
                bestStart = currentStart;
                bestCount = currentCount;
            }
            currentStart = index;
        }
        if (bestCount < 2)
        {
            throw new ExternalException("A generic error occurred in GDI+.");
        }
        return points.AsSpan(bestStart, bestCount).ToArray();
    }

    private static PointF[] CopyAndValidate(ReadOnlySpan<PointF> points)
    {
        if (points.Length < 2)
        {
            throw new ArgumentException(null, nameof(points));
        }
        return points.ToArray();
    }

    private static PointF[] ConvertAndValidate(ReadOnlySpan<Point> points)
    {
        if (points.Length < 2)
        {
            throw new ArgumentException(null, nameof(points));
        }
        var result = new PointF[points.Length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = points[index];
        }
        return result;
    }

    private static RectangleF GetBounds(ReadOnlySpan<PointF> points)
    {
        float left = points[0].X;
        float top = points[0].Y;
        float right = left;
        float bottom = top;
        for (int index = 1; index < points.Length; index++)
        {
            left = MathF.Min(left, points[index].X);
            top = MathF.Min(top, points[index].Y);
            right = MathF.Max(right, points[index].X);
            bottom = MathF.Max(bottom, points[index].Y);
        }
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static Blend CreateDefaultBlend()
        => new(1) { Factors = [1f], Positions = [0f] };

    private static Blend CreateRenderingDefaultBlend()
        => new(2) { Factors = [1f, 0f], Positions = [0f, 1f] };

    private static void ValidateBlend(Blend blend)
    {
        ArgumentNullException.ThrowIfNull(blend.Factors);
        ArgumentNullException.ThrowIfNull(blend.Positions);
        if (blend.Factors.Length == 0 ||
            blend.Factors.Length != blend.Positions.Length)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(blend));
        }
        if (blend.Factors.Length > 1)
        {
            if (blend.Positions[0] != 0f || blend.Positions[^1] != 1f)
            {
                throw new ArgumentException("Parameter is not valid.", nameof(blend));
            }
        }
        foreach (float factor in blend.Factors)
        {
            if (!float.IsFinite(factor) || factor is < 0f or > 1f)
            {
                throw new ArgumentException("Parameter is not valid.", nameof(blend));
            }
        }
    }

    private static void ValidateColorBlend(ColorBlend blend)
    {
        ArgumentNullException.ThrowIfNull(blend.Colors);
        ArgumentNullException.ThrowIfNull(blend.Positions);
        if (blend.Colors.Length < 2 || blend.Colors.Length != blend.Positions.Length)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(blend));
        }
        ValidatePositions(blend.Positions, nameof(blend));
    }

    private static void ValidatePositions(float[] positions, string parameterName)
    {
        if (positions[0] != 0f || positions[^1] != 1f)
        {
            throw new ArgumentException("Parameter is not valid.", parameterName);
        }
        float previous = -1f;
        foreach (float position in positions)
        {
            if (!float.IsFinite(position) || position is < 0f or > 1f ||
                position < previous)
            {
                throw new ArgumentException("Parameter is not valid.", parameterName);
            }
            previous = position;
        }
    }

    private static void ValidateFocusAndScale(float focus, float scale)
    {
        if (!float.IsFinite(focus) || focus is < 0f or > 1f ||
            !float.IsFinite(scale) || scale is < 0f or > 1f)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }

    private static void ValidateWrapMode(WrapMode value)
    {
        if (value < WrapMode.Tile || value > WrapMode.Clamp)
        {
            throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(WrapMode));
        }
    }

    private static void ValidateMatrixOrder(MatrixOrder order)
    {
        if (order is not MatrixOrder.Prepend and not MatrixOrder.Append)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(order));
        }
    }

    private static void EnsureInvertible(Matrix3x2 matrix)
    {
        if (!Matrix3x2.Invert(matrix, out _))
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }

    private static bool AllColorsEqual(ReadOnlySpan<Color> colors)
    {
        int first = colors[0].ToArgb();
        for (int index = 1; index < colors.Length; index++)
        {
            if (colors[index].ToArgb() != first)
            {
                return false;
            }
        }
        return true;
    }

    private static Vector4 ToVector(Color color)
        => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private static Matrix4x4 ToMatrix4x4(Matrix3x2 matrix)
        => new(
            matrix.M11, matrix.M12, 0f, 0f,
            matrix.M21, matrix.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            matrix.M31, matrix.M32, 0f, 1f);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
