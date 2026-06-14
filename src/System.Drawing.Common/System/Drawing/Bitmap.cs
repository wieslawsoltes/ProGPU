using ProGPU.Backend;
using ProGPU.Scene;
using System;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;

namespace System.Drawing;

internal class GraphicsVisual : Visual
{
    private readonly DrawingContext _recordedContext;

    public GraphicsVisual(DrawingContext recordedContext)
    {
        _recordedContext = recordedContext;
    }

    public override void OnRender(DrawingContext context)
    {
        context.Append(_recordedContext);
    }
}

public class Bitmap : Image
{
    private readonly GpuTexture _texture;
    private readonly DrawingContext _recordedContext = new();
    private bool _isDisposed;

    public GpuTexture GpuTexture => _texture;
    public DrawingContext RecordedContext => _recordedContext;

    public override int Width => (int)_texture.Width;
    public override int Height => (int)_texture.Height;

    public Bitmap(int width, int height)
    {
        _texture = new GpuTexture(
            GpuProvider.Context,
            (uint)width,
            (uint)height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "GDI Bitmap Backing Texture"
        );
    }

    public Bitmap(string filename)
    {
        using var fs = System.IO.File.OpenRead(filename);
        using var skData = SkiaSharp.SKData.Create(fs);
        using var tempBitmap = SkiaSharp.SKBitmap.Decode(skData);

        _texture = new GpuTexture(
            GpuProvider.Context,
            (uint)tempBitmap.Width,
            (uint)tempBitmap.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "GDI Bitmap Backing Texture from file"
        );

        unsafe
        {
            var pixelsSpan = new ReadOnlySpan<byte>((void*)tempBitmap.GetPixels(), tempBitmap.Width * tempBitmap.Height * 4);
            _texture.WritePixels(pixelsSpan);
        }
    }

    public void Flush()
    {
        if (_isDisposed) return;
        if (_recordedContext.Commands.Count == 0) return;

        var visual = new GraphicsVisual(_recordedContext);
        GpuProvider.Compositor.RenderOffscreen(
            visual,
            (uint)Width,
            (uint)Height,
            _texture,
            padding: 0f,
            dpiScale: 1f
        );

        _recordedContext.Commands.Clear();
    }

    public Color GetPixel(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x));

        Flush();
        byte[] pixels = _texture.ReadPixels();
        int offset = (y * Width + x) * 4;
        return Color.FromArgb(pixels[offset + 3], pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    public void SetPixel(int x, int y, Color color)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x));

        Flush();
        byte[] rgba = new byte[] { color.R, color.G, color.B, color.A };
        _texture.WritePixelsSubRect(rgba.AsSpan(), (uint)x, (uint)y, 1, 1);
    }

    public void Save(string filename)
    {
        Flush();
        byte[] pixels = _texture.ReadPixels();
        PngEncoder.SavePng(filename, pixels, (uint)Width, (uint)Height);
    }

    private byte[]? _lockedBytes;
    private GCHandle _lockedHandle;
    private Rectangle _lockedRect;

    public BitmapData LockBits(Rectangle rect, ImageLockMode flags, PixelFormat format)
    {
        Flush();
        byte[] fullPixels = _texture.ReadPixels();
        
        int subWidth = rect.Width;
        int subHeight = rect.Height;
        _lockedBytes = new byte[subWidth * subHeight * 4];
        _lockedRect = rect;
        
        unsafe
        {
            fixed (byte* src = fullPixels)
            fixed (byte* dst = _lockedBytes)
            {
                for (int y = 0; y < subHeight; y++)
                {
                    int srcY = rect.Y + y;
                    byte* srcRow = src + (srcY * Width + rect.X) * 4;
                    byte* dstRow = dst + y * subWidth * 4;
                    
                    for (int x = 0; x < subWidth; x++)
                    {
                        int idx = x * 4;
                        dstRow[idx] = srcRow[idx + 2];     // B (source R)
                        dstRow[idx + 1] = srcRow[idx + 1]; // G (source G)
                        dstRow[idx + 2] = srcRow[idx];     // R (source B)
                        dstRow[idx + 3] = srcRow[idx + 3]; // A (source A)
                    }
                }
            }
        }
        
        _lockedHandle = GCHandle.Alloc(_lockedBytes, GCHandleType.Pinned);

        return new BitmapData
        {
            Width = subWidth,
            Height = subHeight,
            Stride = subWidth * 4,
            PixelFormat = PixelFormat.Format32bppArgb,
            Scan0 = _lockedHandle.AddrOfPinnedObject()
        };
    }

    public void UnlockBits(BitmapData bitmapData)
    {
        if (_lockedBytes != null)
        {
            if (_lockedHandle.IsAllocated)
            {
                _lockedHandle.Free();
            }

            unsafe
            {
                fixed (byte* pBytes = _lockedBytes)
                {
                    int totalPixels = _lockedRect.Width * _lockedRect.Height;
                    for (int i = 0; i < totalPixels; i++)
                    {
                        int idx = i * 4;
                        byte b = pBytes[idx];
                        pBytes[idx] = pBytes[idx + 2];     // R
                        pBytes[idx + 2] = b;               // B
                    }
                }
            }

            _texture.WritePixelsSubRect(new ReadOnlySpan<byte>(_lockedBytes), (uint)_lockedRect.X, (uint)_lockedRect.Y, (uint)_lockedRect.Width, (uint)_lockedRect.Height);
            _lockedBytes = null;
        }
    }

    public override void Dispose()
    {
        if (_isDisposed) return;
        Flush();
        _texture.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~Bitmap()
    {
        Dispose();
    }
}
