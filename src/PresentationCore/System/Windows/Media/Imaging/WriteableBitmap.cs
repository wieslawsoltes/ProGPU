using ProGPU.Backend;
using ProGPU.Scene;
using System;

namespace System.Windows
{
    public struct Int32Rect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public Int32Rect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}

namespace System.Windows.Media.Imaging
{
    public struct PixelFormat
    {
    }

    public static class PixelFormats
    {
        public static PixelFormat Bgr32 => new PixelFormat();
        public static PixelFormat Pbgra32 => new PixelFormat();
    }

    public class BitmapPalette
    {
    }

    public class WriteableBitmap : BitmapSource
    {
        private readonly GpuTexture _texture;

        public override int PixelWidth => (int)_texture.Width;
        public override int PixelHeight => (int)_texture.Height;
        public override GpuTexture GpuTexture => _texture;

        public WriteableBitmap(int pixelWidth, int pixelHeight, double dpiX, double dpiY, PixelFormat pixelFormat, BitmapPalette? palette)
        {
            _texture = new GpuTexture(
                GpuProvider.Context,
                (uint)pixelWidth,
                (uint)pixelHeight,
                Silk.NET.WebGPU.TextureFormat.Bgra8Unorm,
                Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.TextureBinding,
                "WPF WriteableBitmap Backing Texture"
            );
        }

        public void WritePixels(Int32Rect sourceRect, IntPtr buffer, int bufferSize, int stride)
        {
            int width = sourceRect.Width;
            int height = sourceRect.Height;
            int bytesPerPixel = 4;
            int packedStride = width * bytesPerPixel;
            
            unsafe
            {
                if (stride > packedStride)
                {
                    byte[] temp = new byte[width * height * bytesPerPixel];
                    byte* src = (byte*)buffer;
                    fixed (byte* dst = temp)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            byte* srcRow = src + y * stride;
                            byte* dstRow = dst + y * packedStride;
                            System.Buffer.MemoryCopy(srcRow, dstRow, packedStride, packedStride);
                        }
                    }
                    _texture.WritePixelsSubRect(
                        temp.AsSpan(),
                        (uint)sourceRect.X,
                        (uint)sourceRect.Y,
                        (uint)width,
                        (uint)height
                    );
                }
                else
                {
                    var span = new ReadOnlySpan<byte>((void*)buffer, bufferSize);
                    _texture.WritePixelsSubRect(
                        span,
                        (uint)sourceRect.X,
                        (uint)sourceRect.Y,
                        (uint)width,
                        (uint)height
                    );
                }
            }
        }
    }
}
