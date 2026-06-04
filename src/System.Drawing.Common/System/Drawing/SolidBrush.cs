using System.Numerics;

namespace System.Drawing;

public class SolidBrush : Brush
{
    public Color Color { get; set; }

    public SolidBrush(Color color)
    {
        Color = color;
    }

    public override ProGPU.Vector.Brush ToProGpuBrush()
    {
        return new ProGPU.Vector.SolidColorBrush(new Vector4(Color.R / 255f, Color.G / 255f, Color.B / 255f, Color.A / 255f));
    }
}
