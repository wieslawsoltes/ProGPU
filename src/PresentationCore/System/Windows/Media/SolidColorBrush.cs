using System.Numerics;

namespace System.Windows.Media;

public class SolidColorBrush : Brush
{
    public Color Color { get; set; }

    public SolidColorBrush() { }

    public SolidColorBrush(Color color)
    {
        Color = color;
    }

    public override ProGPU.Vector.Brush ToNative()
    {
        return new ProGPU.Vector.SolidColorBrush(new Vector4(Color.R / 255f, Color.G / 255f, Color.B / 255f, Color.A / 255f));
    }
}
