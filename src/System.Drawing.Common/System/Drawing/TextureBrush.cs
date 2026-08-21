using System;

namespace System.Drawing;

public class TextureBrush : Brush
{
    private Drawing2D.Matrix _transform = new();

    public Image Image { get; }
    public Drawing2D.WrapMode WrapMode { get; }
    public Drawing2D.Matrix Transform
    {
        get => _transform.Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _transform.Dispose();
            _transform = value.Clone();
        }
    }

    public TextureBrush(Image image) : this(image, Drawing2D.WrapMode.Tile)
    {
    }

    public TextureBrush(Image image, Drawing2D.WrapMode wrapMode)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        WrapMode = wrapMode;
    }

    public override ProGPU.Vector.Brush ToProGpuBrush()
    {
        throw new NotSupportedException("TextureBrush cannot be converted to a vector brush; use a texture-aware Graphics fill path.");
    }

    public override void Dispose()
    {
        _transform.Dispose();
    }
}
