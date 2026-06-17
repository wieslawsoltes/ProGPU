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

public class Pen
{
    public Brush? Brush { get; set; }
    public double Thickness { get; set; }
    public PenLineJoin LineJoin { get; set; } = PenLineJoin.Miter;
    public double MiterLimit { get; set; } = 10.0;
    public PenLineCap StartLineCap { get; set; } = PenLineCap.Flat;
    public PenLineCap EndLineCap { get; set; } = PenLineCap.Flat;
    public PenLineCap DashCap { get; set; } = PenLineCap.Flat;

    public Pen() { }

    public Pen(Brush? brush, double thickness)
    {
        Brush = brush;
        Thickness = thickness;
    }

    public ProGPU.Vector.Pen? ToNative()
    {
        if (Brush == null) return null;
        return CreateNativePen(Brush.ToNative());
    }

    public ProGPU.Vector.Pen? ToNative(Rect targetBounds)
    {
        if (Brush == null) return null;
        return CreateNativePen(Brush.ToNative(targetBounds));
    }

    private ProGPU.Vector.Pen CreateNativePen(ProGPU.Vector.Brush brush)
    {
        return new ProGPU.Vector.Pen(
            brush,
            (float)Thickness,
            ToNativeLineJoin(LineJoin),
            (float)global::System.Math.Max(1.0, MiterLimit),
            ToNativeLineCap(StartLineCap),
            ToNativeLineCap(EndLineCap),
            ToNativeLineCap(DashCap));
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
