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

    public BitmapData LockBits(Rectangle rect, ImageLockMode flags, PixelFormat format)
    {
        Flush();
        byte[] pixels = _texture.ReadPixels();
        
        _lockedBytes = pixels;
        _lockedHandle = GCHandle.Alloc(_lockedBytes, GCHandleType.Pinned);

        return new BitmapData
        {
            Width = Width,
            Height = Height,
            Stride = Width * 4,
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

            _texture.WritePixels(new ReadOnlySpan<byte>(_lockedBytes));
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
