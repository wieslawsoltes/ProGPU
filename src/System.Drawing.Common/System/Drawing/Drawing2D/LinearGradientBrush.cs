using System.ComponentModel;
using System.Numerics;

namespace System.Drawing.Drawing2D;

public enum LinearGradientMode
{
    Horizontal = 0,
    Vertical = 1,
    ForwardDiagonal = 2,
    BackwardDiagonal = 3
}

public sealed class LinearGradientBrush : Brush, ICloneable
{
    private Vector2 _startPoint;
    private Vector2 _endPoint;
    private Color _color1;
    private Color _color2;
    private RectangleF _rectangle;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private Blend? _blend;
    private ColorBlend? _interpolationColors;
    private WrapMode _wrapMode = WrapMode.Tile;
    private bool _gammaCorrection;
    private bool _isAngleScaleable;
    private bool _disposed;

    public LinearGradientBrush(PointF point1, PointF point2, Color color1, Color color2)
    {
        if (point1 == point2)
        {
            throw new ArgumentException("The gradient points must be distinct.", nameof(point2));
        }

        _startPoint = new Vector2(point1.X, point1.Y);
        _endPoint = new Vector2(point2.X, point2.Y);
        _color1 = color1;
        _color2 = color2;
        UpdateRectangleFromPoints();
    }

    public LinearGradientBrush(Point point1, Point point2, Color color1, Color color2)
        : this((PointF)point1, (PointF)point2, color1, color2)
    {
    }

    public LinearGradientBrush(
        RectangleF rect,
        Color color1,
        Color color2,
        LinearGradientMode linearGradientMode)
    {
        ValidateRectangle(rect);
        if (linearGradientMode < LinearGradientMode.Horizontal
            || linearGradientMode > LinearGradientMode.BackwardDiagonal)
        {
            throw new InvalidEnumArgumentException(
                nameof(linearGradientMode),
                (int)linearGradientMode,
                typeof(LinearGradientMode));
        }

        _rectangle = rect;
        _color1 = color1;
        _color2 = color2;
        float centerX = rect.Left + (rect.Width / 2f);
        float centerY = rect.Top + (rect.Height / 2f);
        switch (linearGradientMode)
        {
            case LinearGradientMode.Horizontal:
                _startPoint = new Vector2(rect.Left, centerY);
                _endPoint = new Vector2(rect.Right, centerY);
                break;
            case LinearGradientMode.Vertical:
                _startPoint = new Vector2(centerX, rect.Top);
                _endPoint = new Vector2(centerX, rect.Bottom);
                break;
            case LinearGradientMode.ForwardDiagonal:
                _startPoint = new Vector2(rect.Left, rect.Top);
                _endPoint = new Vector2(rect.Right, rect.Bottom);
                break;
            default:
                _startPoint = new Vector2(rect.Right, rect.Top);
                _endPoint = new Vector2(rect.Left, rect.Bottom);
                break;
        }
    }

    public LinearGradientBrush(
        Rectangle rect,
        Color color1,
        Color color2,
        LinearGradientMode linearGradientMode)
        : this((RectangleF)rect, color1, color2, linearGradientMode)
    {
    }

    public LinearGradientBrush(RectangleF rect, Color color1, Color color2, float angle)
        : this(rect, color1, color2, angle, isAngleScaleable: false)
    {
    }

    public LinearGradientBrush(Rectangle rect, Color color1, Color color2, float angle)
        : this((RectangleF)rect, color1, color2, angle, isAngleScaleable: false)
    {
    }

    public LinearGradientBrush(
        RectangleF rect,
        Color color1,
        Color color2,
        float angle,
        bool isAngleScaleable)
    {
        ValidateRectangle(rect);
        if (!float.IsFinite(angle))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(angle));
        }

        _rectangle = rect;
        _color1 = color1;
        _color2 = color2;
        _isAngleScaleable = isAngleScaleable;

        float radians = angle * (MathF.PI / 180f);
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);
        var direction = isAngleScaleable
            ? Vector2.Normalize(new Vector2(rect.Height * cosine, rect.Width * sine))
            : new Vector2(cosine, sine);
        var center = new Vector2(rect.Left + (rect.Width / 2f), rect.Top + (rect.Height / 2f));
        float extent = (MathF.Abs(rect.Width * direction.X) + MathF.Abs(rect.Height * direction.Y)) / 2f;
        _startPoint = center - (direction * extent);
        _endPoint = center + (direction * extent);
    }

    public LinearGradientBrush(
        Rectangle rect,
        Color color1,
        Color color2,
        float angle,
        bool isAngleScaleable)
        : this((RectangleF)rect, color1, color2, angle, isAngleScaleable)
    {
    }

    // Typed ProGPU extensions retained for existing renderer integrations.
    public Vector2 StartPoint
    {
        get
        {
            ThrowIfDisposed();
            return _startPoint;
        }
        set
        {
            ThrowIfDisposed();
            _startPoint = value;
            UpdateRectangleFromPoints();
        }
    }

    public Vector2 EndPoint
    {
        get
        {
            ThrowIfDisposed();
            return _endPoint;
        }
        set
        {
            ThrowIfDisposed();
            _endPoint = value;
            UpdateRectangleFromPoints();
        }
    }

    public Color Color1
    {
        get
        {
            ThrowIfDisposed();
            return _color1;
        }
        set
        {
            ThrowIfDisposed();
            _color1 = value;
        }
    }

    public Color Color2
    {
        get
        {
            ThrowIfDisposed();
            return _color2;
        }
        set
        {
            ThrowIfDisposed();
            _color2 = value;
        }
    }

    public Color[] LinearColors
    {
        get
        {
            ThrowIfDisposed();
            return [_color1, _color2];
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length != 2)
            {
                throw new ArgumentException("LinearColors must contain exactly two colors.", nameof(value));
            }

            _color1 = value[0];
            _color2 = value[1];
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

    public bool GammaCorrection
    {
        get
        {
            ThrowIfDisposed();
            return _gammaCorrection;
        }
        set
        {
            ThrowIfDisposed();
            _gammaCorrection = value;
        }
    }

    public Blend? Blend
    {
        get
        {
            ThrowIfDisposed();
            return _blend?.CloneBlend();
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

    public ColorBlend InterpolationColors
    {
        get
        {
            ThrowIfDisposed();
            return (_interpolationColors ?? CreateEndpointColorBlend()).CloneBlend();
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
            if (value < WrapMode.Tile || value > WrapMode.Clamp)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(WrapMode));
            }

            _wrapMode = value;
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
            _transform = value.MatrixElements;
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
        ApplyTransform(matrix.MatrixElements, order);
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
        => ApplyTransform(Matrix3x2.CreateRotation(angle * (MathF.PI / 180f)), order);

    public void SetBlendTriangularShape(float focus)
        => SetBlendTriangularShape(focus, 1f);

    public void SetBlendTriangularShape(float focus, float scale)
    {
        ValidateFocusAndScale(focus, scale);
        float[] positions;
        float[] factors;
        if (focus == 0f)
        {
            positions = [0f, 1f];
            factors = [scale, 0f];
        }
        else if (focus == 1f)
        {
            positions = [0f, 1f];
            factors = [0f, scale];
        }
        else
        {
            positions = [0f, focus, 1f];
            factors = [0f, scale, 0f];
        }

        Blend = new Blend(positions.Length)
        {
            Factors = factors,
            Positions = positions
        };
    }

    public void SetSigmaBellShape(float focus)
        => SetSigmaBellShape(focus, 1f);

    public void SetSigmaBellShape(float focus, float scale)
    {
        ValidateFocusAndScale(focus, scale);
        const int sampleIntervals = 32;
        var positions = new SortedSet<float>();
        for (int index = 0; index <= sampleIntervals; index++)
        {
            positions.Add(index / (float)sampleIntervals);
        }
        positions.Add(focus);

        float[] sampledPositions = positions.ToArray();
        var factors = new float[sampledPositions.Length];
        for (int index = 0; index < sampledPositions.Length; index++)
        {
            float position = sampledPositions[index];
            float normalized = position <= focus
                ? focus == 0f ? 1f : position / focus
                : focus == 1f ? 1f : (1f - position) / (1f - focus);
            float sine = MathF.Sin(normalized * (MathF.PI / 2f));
            factors[index] = scale * sine * sine;
        }

        Blend = new Blend(sampledPositions.Length)
        {
            Factors = factors,
            Positions = sampledPositions
        };
    }

    public override object Clone()
    {
        ThrowIfDisposed();
        var clone = new LinearGradientBrush(
            new PointF(_startPoint.X, _startPoint.Y),
            new PointF(_endPoint.X, _endPoint.Y),
            _color1,
            _color2)
        {
            _rectangle = _rectangle,
            _transform = _transform,
            _blend = _blend?.CloneBlend(),
            _interpolationColors = _interpolationColors?.CloneBlend(),
            _wrapMode = _wrapMode,
            _gammaCorrection = _gammaCorrection,
            _isAngleScaleable = _isAngleScaleable
        };
        return clone;
    }

    internal override ProGPU.Vector.Brush ToProGpuBrush()
    {
        ThrowIfDisposed();
        ProGPU.Vector.GradientStop[] stops = CreateNativeStops();
        var nativeBrush = new ProGPU.Vector.LinearGradientBrush(_startPoint, _endPoint, stops)
        {
            SpreadMethod = _wrapMode switch
            {
                WrapMode.Tile => ProGPU.Vector.GradientSpreadMethod.Repeat,
                WrapMode.TileFlipX or WrapMode.TileFlipY or WrapMode.TileFlipXY
                    => ProGPU.Vector.GradientSpreadMethod.Reflect,
                _ => ProGPU.Vector.GradientSpreadMethod.Pad
            },
            ColorInterpolationMode = _gammaCorrection
                ? ProGPU.Vector.GradientColorInterpolationMode.ScRgbLinearInterpolation
                : ProGPU.Vector.GradientColorInterpolationMode.SRgbLinearInterpolation
        };

        if (Matrix3x2.Invert(_transform, out Matrix3x2 coordinateTransform))
        {
            nativeBrush.CoordinateTransform = ToMatrix4x4(coordinateTransform);
        }

        return nativeBrush;
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _transform = default;
        _blend = null;
        _interpolationColors = null;
        base.Dispose(disposing);
    }

    private ProGPU.Vector.GradientStop[] CreateNativeStops()
    {
        if (_interpolationColors is not null)
        {
            var stops = new ProGPU.Vector.GradientStop[_interpolationColors.Colors.Length];
            for (int index = 0; index < stops.Length; index++)
            {
                stops[index] = new ProGPU.Vector.GradientStop(
                    ToVector(_interpolationColors.Colors[index]),
                    _interpolationColors.Positions[index]);
            }

            return stops;
        }

        if (_blend is not null)
        {
            Vector4 start = ToVector(_color1);
            Vector4 end = ToVector(_color2);
            var stops = new ProGPU.Vector.GradientStop[_blend.Factors.Length];
            for (int index = 0; index < stops.Length; index++)
            {
                stops[index] = new ProGPU.Vector.GradientStop(
                    Vector4.Lerp(start, end, _blend.Factors[index]),
                    _blend.Positions[index]);
            }

            return stops;
        }

        return
        [
            new ProGPU.Vector.GradientStop(ToVector(_color1), 0f),
            new ProGPU.Vector.GradientStop(ToVector(_color2), 1f)
        ];
    }

    private ColorBlend CreateEndpointColorBlend()
        => new(2)
        {
            Colors = [_color1, _color2],
            Positions = [0f, 1f]
        };

    private void ApplyTransform(Matrix3x2 operation, MatrixOrder order)
    {
        ThrowIfDisposed();
        ValidateMatrixOrder(order);
        _transform = order == MatrixOrder.Prepend
            ? operation * _transform
            : _transform * operation;
    }

    private static void ValidateBlend(Blend blend)
    {
        ArgumentNullException.ThrowIfNull(blend.Factors);
        ArgumentNullException.ThrowIfNull(blend.Positions);
        if (blend.Factors.Length < 2 || blend.Factors.Length != blend.Positions.Length)
        {
            throw new ArgumentException("Blend factors and positions must have the same length of at least two.", nameof(blend));
        }

        ValidatePositions(blend.Positions, nameof(blend));
        foreach (float factor in blend.Factors)
        {
            if (!float.IsFinite(factor) || factor < 0f || factor > 1f)
            {
                throw new ArgumentException("Blend factors must be between zero and one.", nameof(blend));
            }
        }
    }

    private static void ValidateColorBlend(ColorBlend blend)
    {
        ArgumentNullException.ThrowIfNull(blend.Colors);
        ArgumentNullException.ThrowIfNull(blend.Positions);
        if (blend.Colors.Length < 2 || blend.Colors.Length != blend.Positions.Length)
        {
            throw new ArgumentException("Colors and positions must have the same length of at least two.", nameof(blend));
        }

        ValidatePositions(blend.Positions, nameof(blend));
    }

    private static void ValidatePositions(float[] positions, string parameterName)
    {
        if (positions[0] != 0f || positions[^1] != 1f)
        {
            throw new ArgumentException("Gradient positions must begin at zero and end at one.", parameterName);
        }

        float previous = -1f;
        foreach (float position in positions)
        {
            if (!float.IsFinite(position) || position < 0f || position > 1f || position < previous)
            {
                throw new ArgumentException("Gradient positions must be ordered between zero and one.", parameterName);
            }
            previous = position;
        }
    }

    private static void ValidateFocusAndScale(float focus, float scale)
    {
        if (!float.IsFinite(focus) || focus < 0f || focus > 1f)
        {
            throw new ArgumentException("Focus must be between zero and one.", nameof(focus));
        }
        if (!float.IsFinite(scale) || scale < 0f || scale > 1f)
        {
            throw new ArgumentException("Scale must be between zero and one.", nameof(scale));
        }
    }

    private static void ValidateRectangle(RectangleF rectangle)
    {
        if (rectangle.Width <= 0f || rectangle.Height <= 0f)
        {
            throw new ArgumentException("Rectangle dimensions must be positive.", nameof(rectangle));
        }
    }

    private static void ValidateMatrixOrder(MatrixOrder order)
    {
        if (order is not MatrixOrder.Prepend and not MatrixOrder.Append)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(order));
        }
    }

    private void UpdateRectangleFromPoints()
    {
        _rectangle = RectangleF.FromLTRB(
            MathF.Min(_startPoint.X, _endPoint.X),
            MathF.Min(_startPoint.Y, _endPoint.Y),
            MathF.Max(_startPoint.X, _endPoint.X),
            MathF.Max(_startPoint.Y, _endPoint.Y));
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
