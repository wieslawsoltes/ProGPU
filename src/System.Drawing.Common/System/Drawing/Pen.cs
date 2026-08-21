using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace System.Drawing;

public class Pen : IDisposable, ICloneable
{
    private static readonly double[] s_dashPattern = { 3.0, 1.0 };
    private static readonly double[] s_dotPattern = { 1.0, 1.0 };
    private static readonly double[] s_dashDotPattern = { 3.0, 1.0, 1.0, 1.0 };
    private static readonly double[] s_dashDotDotPattern = { 3.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
    private static readonly double[] s_defaultCustomPattern = { 1.0 };
    private DashCap _dashCap;
    private LineCap _endCap;
    private LineJoin _lineJoin;
    private float _miterLimit = 10f;
    private LineCap _startCap;
    private float[]? _customDashPattern;

    public Brush Brush { get; set; }
    public float Width { get; set; }
    public System.Drawing.Drawing2D.DashStyle DashStyle { get; set; }
    public float DashOffset { get; set; }
    public PenAlignment Alignment { get; set; }

    public float[] DashPattern
    {
        get => _customDashPattern is null ? [1f] : (float[])_customDashPattern.Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0 || Array.Exists(value, element => !float.IsFinite(element) || element <= 0f))
            {
                throw new ArgumentException("Dash pattern entries must be finite and greater than zero.", nameof(value));
            }

            _customDashPattern = (float[])value.Clone();
            DashStyle = DashStyle.Custom;
        }
    }

    public DashCap DashCap
    {
        get => _dashCap;
        set
        {
            ValidateDashCap(value, nameof(value));
            _dashCap = value;
        }
    }

    public LineCap EndCap
    {
        get => _endCap;
        set
        {
            ValidateLineCap(value, nameof(value));
            _endCap = value;
        }
    }

    public LineJoin LineJoin
    {
        get => _lineJoin;
        set
        {
            if (value < LineJoin.Miter || value > LineJoin.MiterClipped)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(LineJoin));
            }

            _lineJoin = value;
        }
    }

    public float MiterLimit
    {
        get => _miterLimit;
        set => _miterLimit = value < 1f ? 1f : value;
    }

    public LineCap StartCap
    {
        get => _startCap;
        set
        {
            ValidateLineCap(value, nameof(value));
            _startCap = value;
        }
    }

    public Color Color
    {
        get => Brush is SolidBrush solidBrush ? solidBrush.Color : Color.Black;
        set => Brush = new SolidBrush(value);
    }

    public Pen(Color color) : this(color, 1.0f) {}

    public Pen(Color color, float width)
    {
        Brush = new SolidBrush(color);
        Width = width;
    }

    public Pen(Brush brush) : this(brush, 1.0f) {}

    public Pen(Brush brush, float width)
    {
        Brush = brush;
        Width = width;
    }

    public ProGPU.Vector.Pen ToProGpuPen()
    {
        return ToProGpuPen(Width);
    }

    internal ProGPU.Vector.Pen ToProGpuPen(float width)
    {
        var nativePen = new ProGPU.Vector.Pen(
            Brush.ToProGpuBrush(),
            width,
            lineJoin: ToProGpuLineJoin(_lineJoin),
            miterLimit: float.IsFinite(_miterLimit) ? _miterLimit : 1f,
            startLineCap: ToProGpuLineCap(_startCap),
            endLineCap: ToProGpuLineCap(_endCap),
            dashCap: ToProGpuDashCap(_dashCap),
            dashArray: GetDashArray(),
            dashOffset: DashOffset);
        nativePen.MiterLimit = _miterLimit;
        return nativePen;
    }

    public void SetLineCap(LineCap startCap, LineCap endCap, DashCap dashCap)
    {
        // GDI+ accepts arbitrary LineCap values through this method, while an
        // invalid DashCap is normalized to Flat.
        _startCap = startCap;
        _endCap = endCap;
        _dashCap = Enum.IsDefined(dashCap) ? dashCap : DashCap.Flat;
    }

    private double[]? GetDashArray()
    {
        if (DashStyle == DashStyle.Custom && _customDashPattern is not null)
        {
            return Array.ConvertAll(_customDashPattern, static value => (double)value);
        }

        return DashStyle switch
        {
            System.Drawing.Drawing2D.DashStyle.Dash => s_dashPattern,
            System.Drawing.Drawing2D.DashStyle.Dot => s_dotPattern,
            System.Drawing.Drawing2D.DashStyle.DashDot => s_dashDotPattern,
            System.Drawing.Drawing2D.DashStyle.DashDotDot => s_dashDotDotPattern,
            System.Drawing.Drawing2D.DashStyle.Custom => s_defaultCustomPattern,
            _ => null
        };
    }

    public object Clone()
    {
        var clone = new Pen(Brush is SolidBrush solid ? new SolidBrush(solid.Color) : Brush, Width)
        {
            Alignment = Alignment,
            DashCap = DashCap,
            DashOffset = DashOffset,
            DashStyle = DashStyle,
            EndCap = EndCap,
            LineJoin = LineJoin,
            MiterLimit = MiterLimit,
            StartCap = StartCap
        };
        clone._customDashPattern = _customDashPattern is null ? null : (float[])_customDashPattern.Clone();
        return clone;
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

    public void Dispose()
    {
        Brush?.Dispose();
    }
}
