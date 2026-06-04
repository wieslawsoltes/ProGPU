namespace System.Windows.Media;

public class Pen
{
    public Brush? Brush { get; set; }
    public double Thickness { get; set; }

    public Pen() { }

    public Pen(Brush? brush, double thickness)
    {
        Brush = brush;
        Thickness = thickness;
    }

    public ProGPU.Vector.Pen? ToNative()
    {
        if (Brush == null) return null;
        return new ProGPU.Vector.Pen(Brush.ToNative(), (float)Thickness);
    }
}
