using System;

namespace System.Drawing.Imaging;

public enum PixelFormat
{
    Format32bppArgb = 2498570,
    Format24bppRgb = 137224,
    Format8bppIndexed = 198658,
    Format32bppRgb = 139273,
    Format32bppPArgb = 925707,
    Format16bppRgb565 = 135173
}

public enum ImageLockMode
{
    ReadOnly = 1,
    WriteOnly = 2,
    ReadWrite = 3,
    UserInputBuffer = 4
}

public sealed class BitmapData
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Stride { get; set; }
    public PixelFormat PixelFormat { get; set; }
    public IntPtr Scan0 { get; set; }
    public int Reserved { get; set; }
}

public sealed class ImageFormat
{
    public Guid Guid { get; }

    public ImageFormat(Guid guid)
    {
        Guid = guid;
    }

    public static ImageFormat Bmp { get; } = new ImageFormat(new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Jpeg { get; } = new ImageFormat(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Png { get; } = new ImageFormat(new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Gif { get; } = new ImageFormat(new Guid("b96b3cb0-0728-11d3-9d7b-0000f81ef32e"));
}
