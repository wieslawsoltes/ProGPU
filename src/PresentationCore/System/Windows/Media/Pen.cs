namespace System.Windows.Media;

public enum PenLineJoin
{
    Miter = 0,
    Bevel = 1,
    Round = 2
}

public enum PenLineCap
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3
}

public class DashStyle
{
    private double[] _dashes = global::System.Array.Empty<double>();

    public DashStyle()
        : this(global::System.Array.Empty<double>(), 0)
    {
    }

    public DashStyle(double[] dashes, double offset)
    {
        Dashes = dashes;
        Offset = offset;
    }

    public double[] Dashes
    {
        get => (double[])_dashes.Clone();
        set => _dashes = value is null ? global::System.Array.Empty<double>() : (double[])value.Clone();
    }

    public double Offset { get; set; }
}

public class Pen
{
    public Brush? Brush { get; set; }
    public double Thickness { get; set; }
    public PenLineJoin LineJoin { get; set; } = PenLineJoin.Miter;
    public double MiterLimit { get; set; } = 10.0;
    public PenLineCap StartLineCap { get; set; } = PenLineCap.Flat;
    public PenLineCap EndLineCap { get; set; } = PenLineCap.Flat;
    public PenLineCap DashCap { get; set; } = PenLineCap.Flat;
    public DashStyle? DashStyle { get; set; }

    public Pen() { }

    public Pen(Brush? brush, double thickness)
    {
        Brush = brush;
        Thickness = thickness;
    }

    public ProGPU.Vector.Pen? ToNative()
    {
        return ToNative(1f);
    }

    public ProGPU.Vector.Pen? ToNative(float thicknessScale)
    {
        if (Brush == null) return null;
        return CreateNativePen(Brush.ToNative(), thicknessScale);
    }

    public ProGPU.Vector.Pen? ToNative(Rect targetBounds)
    {
        return ToNative(targetBounds, 1f);
    }

    public ProGPU.Vector.Pen? ToNative(Rect targetBounds, float thicknessScale)
    {
        if (Brush == null) return null;
        return CreateNativePen(Brush.ToNative(targetBounds), thicknessScale);
    }

    private ProGPU.Vector.Pen CreateNativePen(ProGPU.Vector.Brush brush, float thicknessScale)
    {
        return new ProGPU.Vector.Pen(
            brush,
            GetScaledThickness(thicknessScale),
            ToNativeLineJoin(LineJoin),
            (float)global::System.Math.Max(1.0, MiterLimit),
            ToNativeLineCap(StartLineCap),
            ToNativeLineCap(EndLineCap),
            ToNativeLineCap(DashCap),
            GetScaledDashArray(thicknessScale),
            DashStyle?.Offset ?? 0.0);
    }

    private float GetScaledThickness(float thicknessScale)
    {
        if (!float.IsFinite(thicknessScale) || thicknessScale <= 0f)
        {
            thicknessScale = 1f;
        }

        return (float)Thickness * thicknessScale;
    }

    private double[]? GetScaledDashArray(float thicknessScale)
    {
        if (DashStyle?.Dashes is not { Length: > 0 } dashes)
        {
            return null;
        }

        var dashScale = Thickness * thicknessScale;
        if (!double.IsFinite(dashScale) || dashScale < 0.0)
        {
            dashScale = 0.0;
        }

        var scaledDashes = new double[dashes.Length];
        for (var i = 0; i < dashes.Length; i++)
        {
            var dash = dashes[i];
            if (!double.IsFinite(dash) || dash < 0.0)
            {
                return null;
            }

            scaledDashes[i] = dash * dashScale;
        }

        return scaledDashes;
    }

    private static ProGPU.Vector.PenLineJoin ToNativeLineJoin(PenLineJoin lineJoin)
    {
        return lineJoin switch
        {
            PenLineJoin.Bevel => ProGPU.Vector.PenLineJoin.Bevel,
            PenLineJoin.Round => ProGPU.Vector.PenLineJoin.Round,
            _ => ProGPU.Vector.PenLineJoin.Miter
        };
    }

    private static ProGPU.Vector.PenLineCap ToNativeLineCap(PenLineCap lineCap)
    {
        return lineCap switch
        {
            PenLineCap.Square => ProGPU.Vector.PenLineCap.Square,
            PenLineCap.Round => ProGPU.Vector.PenLineCap.Round,
            PenLineCap.Triangle => ProGPU.Vector.PenLineCap.Triangle,
            _ => ProGPU.Vector.PenLineCap.Flat
        };
    }
}
