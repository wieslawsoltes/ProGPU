namespace System.Drawing;

public class Pen : IDisposable
{
    public Brush Brush { get; set; }
    public float Width { get; set; }

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
        return new ProGPU.Vector.Pen(Brush.ToProGpuBrush(), width);
    }

    public void Dispose()
    {
        Brush?.Dispose();
    }
}
