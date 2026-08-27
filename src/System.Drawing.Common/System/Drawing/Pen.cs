using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;

namespace System.Drawing;

public sealed class Pen : MarshalByRefObject, IDisposable, ICloneable
{
    private static readonly double[] s_dashPattern = { 3.0, 1.0 };
    private static readonly double[] s_dotPattern = { 1.0, 1.0 };
    private static readonly double[] s_dashDotPattern = { 3.0, 1.0, 1.0, 1.0 };
    private static readonly double[] s_dashDotDotPattern = { 3.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
    private static readonly double[] s_defaultCustomPattern = { 1.0 };

    private PenAlignment _alignment;
    private Brush _brush;
    private float[]? _customDashPattern;
    private DashCap _dashCap;
    private float _dashOffset;
    private DashStyle _dashStyle;
    private bool _disposed;
    private LineCap _endCap;
    private readonly bool _immutable;
    private LineJoin _lineJoin;
    private float _miterLimit = 10f;
    private LineCap _startCap;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private float _width;

    public Brush Brush
    {
        get
        {
            ThrowIfDisposed();
            return (Brush)_brush.Clone();
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            ArgumentNullException.ThrowIfNull(value);

            Brush replacement = (Brush)value.Clone();
            Brush previous = _brush;
            _brush = replacement;
            previous.Dispose();
        }
    }

    public float Width
    {
        get
        {
            ThrowIfDisposed();
            return _width;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            _width = value;
        }
    }

    public DashStyle DashStyle
    {
        get
        {
            ThrowIfDisposed();
            return _dashStyle;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            _dashStyle = value;
        }
    }

    public float DashOffset
    {
        get
        {
            ThrowIfDisposed();
            return _dashOffset;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            _dashOffset = value;
        }
    }

    public PenAlignment Alignment
    {
        get
        {
            ThrowIfDisposed();
            return _alignment;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            _alignment = value;
        }
    }

    public PenType PenType
    {
        get
        {
            ThrowIfDisposed();
            return _brush switch
            {
                HatchBrush => PenType.HatchFill,
                TextureBrush => PenType.TextureFill,
                LinearGradientBrush => PenType.LinearGradient,
                _ => PenType.SolidColor,
            };
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
            ThrowIfImmutable();
            ArgumentNullException.ThrowIfNull(value);

            if (!value.TryGetMatrixElements(out Matrix3x2 transform) ||
                !IsFinite(transform) ||
                !Matrix3x2.Invert(transform, out _))
            {
                throw new ArgumentException("Parameter is not valid.", nameof(value));
            }

            _transform = transform;
        }
    }

    public float[] DashPattern
    {
        get
        {
            ThrowIfDisposed();
            return _customDashPattern is null ? [1f] : (float[])_customDashPattern.Clone();
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0 || Array.Exists(value, element => !float.IsFinite(element) || element <= 0f))
            {
                throw new ArgumentException("Dash pattern entries must be finite and greater than zero.", nameof(value));
            }

            _customDashPattern = (float[])value.Clone();
            _dashStyle = DashStyle.Custom;
        }
    }

    public DashCap DashCap
    {
        get
        {
            ThrowIfDisposed();
            return _dashCap;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            ValidateDashCap(value, nameof(value));
            _dashCap = value;
        }
    }

    public LineCap EndCap
    {
        get
        {
            ThrowIfDisposed();
            return _endCap;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            ValidateLineCap(value, nameof(value));
            _endCap = value;
        }
    }

    public LineJoin LineJoin
    {
        get
        {
            ThrowIfDisposed();
            return _lineJoin;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            if (value < LineJoin.Miter || value > LineJoin.MiterClipped)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(LineJoin));
            }

            _lineJoin = value;
        }
    }

    public float MiterLimit
    {
        get
        {
            ThrowIfDisposed();
            return _miterLimit;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            _miterLimit = value < 1f ? 1f : value;
        }
    }

    public LineCap StartCap
    {
        get
        {
            ThrowIfDisposed();
            return _startCap;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            ValidateLineCap(value, nameof(value));
            _startCap = value;
        }
    }

    public Color Color
    {
        get
        {
            ThrowIfDisposed();
            return _brush is SolidBrush solidBrush ? solidBrush.Color : Color.Black;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();

            Brush previous = _brush;
            _brush = new SolidBrush(value);
            previous.Dispose();
        }
    }

    public Pen(Color color)
        : this(color, 1.0f)
    {
    }

    public Pen(Color color, float width)
        : this(new SolidBrush(color), width, immutable: false, cloneBrush: false)
    {
    }

    internal Pen(Color color, bool immutable)
        : this(new SolidBrush(color), 1.0f, immutable, cloneBrush: false)
    {
    }

    public Pen(Brush brush)
        : this(brush, 1.0f)
    {
    }

    public Pen(Brush brush, float width)
        : this(brush, width, immutable: false, cloneBrush: true)
    {
    }

    private Pen(Brush brush, float width, bool immutable, bool cloneBrush)
    {
        ArgumentNullException.ThrowIfNull(brush);
        _brush = cloneBrush ? (Brush)brush.Clone() : brush;
        _width = width;
        _immutable = immutable;
    }

    internal ProGPU.Vector.Pen ToProGpuPen()
    {
        ThrowIfDisposed();
        return ToProGpuPen(_width);
    }

    internal ProGPU.Vector.Pen ToProGpuPen(float width)
        => ToProGpuPen(width, Point.Empty);

    internal ProGPU.Vector.Pen ToProGpuPen(float width, Point renderingOrigin)
    {
        ThrowIfDisposed();
        ProGPU.Vector.Brush nativeBrush = Graphics.TransformBrush(_brush, renderingOrigin);
        var nativePen = new ProGPU.Vector.Pen(
            nativeBrush,
            width,
            lineJoin: ToProGpuLineJoin(_lineJoin),
            miterLimit: float.IsFinite(_miterLimit) ? _miterLimit : 1f,
            startLineCap: ToProGpuLineCap(_startCap),
            endLineCap: ToProGpuLineCap(_endCap),
            dashCap: ToProGpuDashCap(_dashCap),
            dashArray: GetDashArray(),
            dashOffset: _dashOffset);
        nativePen.MiterLimit = _miterLimit;
        return nativePen;
    }

    internal ProGPU.Vector.Brush ToProGpuBrush(Point renderingOrigin)
    {
        ThrowIfDisposed();
        return Graphics.TransformBrush(_brush, renderingOrigin);
    }

    internal bool HasTransformedTip
    {
        get
        {
            ThrowIfDisposed();
            return _transform.M11 != 1f ||
                _transform.M12 != 0f ||
                _transform.M21 != 0f ||
                _transform.M22 != 1f;
        }
    }

    internal bool TryGetTipTransform(out Matrix3x2 transform, out Matrix3x2 inverse)
    {
        ThrowIfDisposed();
        transform = new Matrix3x2(
            _transform.M11,
            _transform.M12,
            _transform.M21,
            _transform.M22,
            0f,
            0f);
        inverse = default;
        return IsFinite(transform) && Matrix3x2.Invert(transform, out inverse);
    }

    public void ResetTransform()
    {
        ThrowIfDisposed();
        ThrowIfImmutable();
        _transform = Matrix3x2.Identity;
    }

    public void MultiplyTransform(Matrix matrix)
        => MultiplyTransform(matrix, MatrixOrder.Prepend);

    public void MultiplyTransform(Matrix matrix, MatrixOrder order)
    {
        ThrowIfDisposed();
        ThrowIfImmutable();
        ArgumentNullException.ThrowIfNull(matrix);
        if (order is not MatrixOrder.Prepend and not MatrixOrder.Append)
        {
            return;
        }

        if (!matrix.TryGetMatrixElements(out Matrix3x2 operation))
        {
            return;
        }

        Matrix3x2 result = order == MatrixOrder.Prepend
            ? operation * _transform
            : _transform * operation;
        if (!IsFinite(operation) ||
            !Matrix3x2.Invert(operation, out _) ||
            !IsFinite(result) ||
            !Matrix3x2.Invert(result, out _))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(matrix));
        }

        _transform = result;
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

    public void SetLineCap(LineCap startCap, LineCap endCap, DashCap dashCap)
    {
        ThrowIfDisposed();
        ThrowIfImmutable();

        // GDI+ accepts arbitrary LineCap values through this method, while an
        // invalid DashCap is normalized to Flat.
        _startCap = startCap;
        _endCap = endCap;
        _dashCap = Enum.IsDefined(dashCap) ? dashCap : DashCap.Flat;
    }

    private double[]? GetDashArray()
    {
        if (_dashStyle == DashStyle.Custom && _customDashPattern is not null)
        {
            return Array.ConvertAll(_customDashPattern, static value => (double)value);
        }

        return _dashStyle switch
        {
            DashStyle.Dash => s_dashPattern,
            DashStyle.Dot => s_dotPattern,
            DashStyle.DashDot => s_dashDotPattern,
            DashStyle.DashDotDot => s_dashDotDotPattern,
            DashStyle.Custom => s_defaultCustomPattern,
            _ => null
        };
    }

    public object Clone()
    {
        ThrowIfDisposed();
        return new Pen(_brush, _width, immutable: false, cloneBrush: true)
        {
            _alignment = _alignment,
            _dashCap = _dashCap,
            _dashOffset = _dashOffset,
            _dashStyle = _dashStyle,
            _endCap = _endCap,
            _lineJoin = _lineJoin,
            _miterLimit = _miterLimit,
            _startCap = _startCap,
            _transform = _transform,
            _customDashPattern = _customDashPattern is null ? null : (float[])_customDashPattern.Clone()
        };
    }

    private static ProGPU.Vector.PenLineCap ToProGpuLineCap(LineCap lineCap)
    {
        return lineCap switch
        {
            LineCap.Square => ProGPU.Vector.PenLineCap.Square,
            LineCap.Round => ProGPU.Vector.PenLineCap.Round,
            LineCap.Triangle => ProGPU.Vector.PenLineCap.Triangle,
            _ => ProGPU.Vector.PenLineCap.Flat
        };
    }

    private static ProGPU.Vector.PenLineCap ToProGpuDashCap(DashCap dashCap)
    {
        return dashCap switch
        {
            DashCap.Round => ProGPU.Vector.PenLineCap.Round,
            DashCap.Triangle => ProGPU.Vector.PenLineCap.Triangle,
            _ => ProGPU.Vector.PenLineCap.Flat
        };
    }

    private static ProGPU.Vector.PenLineJoin ToProGpuLineJoin(LineJoin lineJoin)
    {
        return lineJoin switch
        {
            LineJoin.Bevel => ProGPU.Vector.PenLineJoin.Bevel,
            LineJoin.Round => ProGPU.Vector.PenLineJoin.Round,
            _ => ProGPU.Vector.PenLineJoin.Miter
        };
    }

    private static void ValidateDashCap(DashCap dashCap, string parameterName)
    {
        if (!Enum.IsDefined(dashCap))
        {
            throw new InvalidEnumArgumentException(parameterName, (int)dashCap, typeof(DashCap));
        }
    }

    private static void ValidateLineCap(LineCap lineCap, string parameterName)
    {
        if (!Enum.IsDefined(lineCap))
        {
            throw new InvalidEnumArgumentException(parameterName, (int)lineCap, typeof(LineCap));
        }
    }

    private void ApplyTransform(Matrix3x2 operation, MatrixOrder order)
    {
        ThrowIfDisposed();
        ThrowIfImmutable();
        if (order is not MatrixOrder.Prepend and not MatrixOrder.Append)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(order));
        }

        if (!IsFinite(operation))
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        Matrix3x2 result = order == MatrixOrder.Prepend
            ? operation * _transform
            : _transform * operation;
        if (!IsFinite(result))
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        _transform = result;
    }

    private static bool IsFinite(Matrix3x2 matrix)
        => float.IsFinite(matrix.M11) &&
            float.IsFinite(matrix.M12) &&
            float.IsFinite(matrix.M21) &&
            float.IsFinite(matrix.M22) &&
            float.IsFinite(matrix.M31) &&
            float.IsFinite(matrix.M32);

    public void Dispose()
    {
        ThrowIfImmutable();
        if (_disposed)
        {
            return;
        }

        _brush.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfImmutable()
    {
        if (_immutable)
        {
            throw new ArgumentException("Changes cannot be made to an immutable system pen.");
        }
    }
}
