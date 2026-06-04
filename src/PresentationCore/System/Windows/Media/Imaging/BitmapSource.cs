using ProGPU.Backend;

namespace System.Windows.Media.Imaging;

public abstract class BitmapSource : ImageSource
{
    public abstract int PixelWidth { get; }
    public abstract int PixelHeight { get; }
    public abstract GpuTexture GpuTexture { get; }
}
