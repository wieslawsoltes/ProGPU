using ProGPU.Vector;

namespace Microsoft.Graphics.Canvas.Geometry;

public enum CanvasCapStyle
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3
}

public enum CanvasDashStyle
{
    Solid = 0,
    Dash = 1,
    Dot = 2,
    DashDot = 3,
    DashDotDot = 4
}

public enum CanvasLineJoin
{
    Miter = 0,
    Bevel = 1,
    Round = 2,
    MiterOrBevel = 3
}

public enum CanvasStrokeTransformBehavior
{
    Normal = 0,
    Fixed = 1,
    Hairline = 2
}

public sealed class CanvasStrokeStyle : IDisposable
{
    private static readonly double[] DashPattern = [2d, 2d];
    private static readonly double[] DotPattern = [0d, 2d];
    private static readonly double[] DashDotPattern = [2d, 2d, 0d, 2d];
    private static readonly double[] DashDotDotPattern =
        [2d, 2d, 0d, 2d, 0d, 2d];

    private CanvasCapStyle _startCap = CanvasCapStyle.Flat;
    private CanvasCapStyle _endCap = CanvasCapStyle.Flat;
    private CanvasCapStyle _dashCap = CanvasCapStyle.Square;
    private CanvasLineJoin _lineJoin = CanvasLineJoin.Miter;
    private float _miterLimit = 10f;
    private CanvasDashStyle _dashStyle = CanvasDashStyle.Solid;
    private float _dashOffset;
    private float[] _customDashStyle = [];
    private CanvasStrokeTransformBehavior _transformBehavior =
        CanvasStrokeTransformBehavior.Normal;
    private Brush? _cachedBrush;
    private Pen? _cachedPen;
    private int _cachedWidthBits;
    private int _version;
    private int _cachedVersion = -1;
    private bool _isDisposed;

    public CanvasCapStyle StartCap
    {
        get => Get(_startCap);
        set => SetEnum(ref _startCap, value, nameof(value));
    }

    public CanvasCapStyle EndCap
    {
        get => Get(_endCap);
        set => SetEnum(ref _endCap, value, nameof(value));
    }

    public CanvasCapStyle DashCap
    {
        get => Get(_dashCap);
        set => SetEnum(ref _dashCap, value, nameof(value));
    }

    public CanvasLineJoin LineJoin
    {
        get => Get(_lineJoin);
        set => SetEnum(ref _lineJoin, value, nameof(value));
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
            if (!float.IsFinite(value) || value < 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_miterLimit != value)
            {
                _miterLimit = value;
                Invalidate();
            }
        }
    }

    public CanvasDashStyle DashStyle
    {
        get => Get(_dashStyle);
        set => SetEnum(ref _dashStyle, value, nameof(value));
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
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_dashOffset != value)
            {
                _dashOffset = value;
                Invalidate();
            }
        }
    }

    public float[] CustomDashStyle
    {
        get
        {
            ThrowIfDisposed();
            return (float[])_customDashStyle.Clone();
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            var copy = new float[value.Length];
            for (int index = 0; index < value.Length; index++)
            {
                float interval = value[index];
                if (!float.IsFinite(interval) || interval < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                copy[index] = interval;
            }
            _customDashStyle = copy;
            Invalidate();
        }
    }

    public CanvasStrokeTransformBehavior TransformBehavior
    {
        get => Get(_transformBehavior);
        set => SetEnum(ref _transformBehavior, value, nameof(value));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        _cachedBrush = null;
        _cachedPen = null;
        GC.SuppressFinalize(this);
    }

    internal Pen GetOrCreatePen(Brush brush, float strokeWidth)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(brush);
        if (!float.IsFinite(strokeWidth) || strokeWidth <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(strokeWidth));
        }

        int widthBits = BitConverter.SingleToInt32Bits(strokeWidth);
        if (_cachedPen is not null &&
            ReferenceEquals(_cachedBrush, brush) &&
            _cachedWidthBits == widthBits &&
            _cachedVersion == _version)
        {
            return _cachedPen;
        }

        PenLineJoin join = _lineJoin switch
        {
            CanvasLineJoin.Miter => PenLineJoin.Miter,
            CanvasLineJoin.Bevel => PenLineJoin.Bevel,
            CanvasLineJoin.Round => PenLineJoin.Round,
            CanvasLineJoin.MiterOrBevel => throw new NotSupportedException(
                "CanvasLineJoin.MiterOrBevel requires a distinct retained join semantic and does not silently degrade to miter or bevel."),
            _ => throw new ArgumentOutOfRangeException(nameof(LineJoin))
        };
        PenStrokeTransformMode transformMode = _transformBehavior switch
        {
            CanvasStrokeTransformBehavior.Normal =>
                PenStrokeTransformMode.Normal,
            CanvasStrokeTransformBehavior.Fixed =>
                PenStrokeTransformMode.Fixed,
            CanvasStrokeTransformBehavior.Hairline =>
                PenStrokeTransformMode.Fixed,
            _ => throw new ArgumentOutOfRangeException(nameof(TransformBehavior))
        };
        float thickness = _transformBehavior ==
            CanvasStrokeTransformBehavior.Hairline
            ? Pen.HairlineThickness
            : strokeWidth;

        _cachedPen = new Pen(
            brush,
            thickness,
            join,
            _miterLimit,
            MapCap(_startCap),
            MapCap(_endCap),
            MapCap(_dashCap),
            ResolveDashArray(),
            _dashOffset,
            transformMode);
        _cachedBrush = brush;
        _cachedWidthBits = widthBits;
        _cachedVersion = _version;
        return _cachedPen;
    }

    private double[]? ResolveDashArray()
    {
        if (_customDashStyle.Length > 0)
        {
            var values = new double[_customDashStyle.Length];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = _customDashStyle[index];
            }
            return values;
        }

        return _dashStyle switch
        {
            CanvasDashStyle.Solid => null,
            CanvasDashStyle.Dash => DashPattern,
            CanvasDashStyle.Dot => DotPattern,
            CanvasDashStyle.DashDot => DashDotPattern,
            CanvasDashStyle.DashDotDot => DashDotDotPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(DashStyle))
        };
    }

    private static PenLineCap MapCap(CanvasCapStyle cap) => cap switch
    {
        CanvasCapStyle.Flat => PenLineCap.Flat,
        CanvasCapStyle.Square => PenLineCap.Square,
        CanvasCapStyle.Round => PenLineCap.Round,
        CanvasCapStyle.Triangle => PenLineCap.Triangle,
        _ => throw new ArgumentOutOfRangeException(nameof(cap))
    };

    private T Get<T>(T value)
    {
        ThrowIfDisposed();
        return value;
    }

    private void SetEnum<T>(ref T field, T value, string parameterName)
        where T : struct, Enum
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Invalidate();
        }
    }

    private void Invalidate()
    {
        _version++;
        _cachedBrush = null;
        _cachedPen = null;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CanvasStrokeStyle));
        }
    }
}
