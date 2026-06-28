namespace ProGPU.Wpf.Interop;

public enum PortablePixelDataFormat
{
    Pbgra32,
    Bgra32,
    Bgr32,
    Bgr101010,
    Bgr24,
    Rgb24,
    BlackWhite,
    Gray2,
    Gray4,
    Gray8,
    Gray16,
    Bgr555,
    Bgr565,
    Rgb48,
    Rgba64,
    Prgba64,
    Cmyk32,
    Gray32Float,
    Rgb128Float,
    Rgba128Float,
    Prgba128Float,
    Indexed1,
    Indexed2,
    Indexed4,
    Indexed8
}

public readonly record struct PortablePbgra32Color(byte B, byte G, byte R, byte A);

public sealed class PortableBitmapSourcePixels
{
    public PortableBitmapSourcePixels(
        int width,
        int height,
        double dpiX,
        double dpiY,
        int stride,
        PortablePixelDataFormat format,
        byte[] pixels,
        PortablePbgra32Color[]? palette = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        Width = width;
        Height = height;
        DpiX = dpiX;
        DpiY = dpiY;
        Stride = stride;
        Format = format;
        Pixels = pixels;
        Palette = palette ?? Array.Empty<PortablePbgra32Color>();
    }

    public int Width { get; }

    public int Height { get; }

    public double DpiX { get; }

    public double DpiY { get; }

    public int Stride { get; }

    public PortablePixelDataFormat Format { get; }

    public byte[] Pixels { get; }

    public PortablePbgra32Color[] Palette { get; }
}

public interface IPortableBitmapSourcePixelsSource
{
    bool TryGetPortableBitmapSourcePixels(out PortableBitmapSourcePixels pixels);
}
