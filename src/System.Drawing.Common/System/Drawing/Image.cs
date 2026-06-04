using System.Drawing.Imaging;

namespace System.Drawing;

public abstract class Image : IDisposable
{
    public abstract int Width { get; }
    public abstract int Height { get; }
    public Size Size => new Size(Width, Height);
    public virtual PixelFormat PixelFormat => PixelFormat.Format32bppArgb;
    public ImageFormat RawFormat { get; protected set; } = ImageFormat.Png;

    public abstract void Dispose();
}
