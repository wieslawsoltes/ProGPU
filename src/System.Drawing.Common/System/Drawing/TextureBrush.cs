namespace System.Drawing;

public class TextureBrush : Brush
{
    public Image Image { get; }

    public TextureBrush(Image image)
    {
        Image = image;
    }

    public override ProGPU.Vector.Brush ToProGpuBrush()
    {
        return new ProGPU.Vector.SolidColorBrush(new System.Numerics.Vector4(0f, 0f, 0f, 1f));
    }
}
