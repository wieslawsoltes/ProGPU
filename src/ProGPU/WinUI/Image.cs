using System;
using System.IO;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.WinUI;

public enum Stretch
{
    None,
    Fill,
    Uniform,
    UniformToFill
}

public class Image : FrameworkElement
{
    private object? _source;
    private GpuTexture? _loadedTexture;
    private Stretch _stretch = Stretch.Uniform;

    public object? Source
    {
        get => _source;
        set
        {
            if (_source != value)
            {
                _source = value;
                
                if (_loadedTexture != null)
                {
                    _loadedTexture.Dispose();
                    _loadedTexture = null;
                }

                if (_source is string path)
                {
                    try
                    {
                        _loadedTexture = LoadBmp(path);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Image] Failed to load BMP: {ex.Message}");
                    }
                }

                Invalidate();
            }
        }
    }

    public Stretch Stretch
    {
        get => _stretch;
        set
        {
            if (_stretch != value)
            {
                _stretch = value;
                Invalidate();
            }
        }
    }

    private GpuTexture? ActiveTexture => _source is GpuTexture tex ? tex : _loadedTexture;

    private Vector2 NaturalSize
    {
        get
        {
            var tex = ActiveTexture;
            return tex != null ? new Vector2(tex.Width, tex.Height) : Vector2.Zero;
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        Vector2 naturalSize = NaturalSize;
        if (naturalSize == Vector2.Zero)
        {
            return Vector2.Zero;
        }

        switch (Stretch)
        {
            case Stretch.None:
                return naturalSize;

            case Stretch.Fill:
                {
                    float w = float.IsInfinity(availableSize.X) ? naturalSize.X : availableSize.X;
                    float h = float.IsInfinity(availableSize.Y) ? naturalSize.Y : availableSize.Y;
                    return new Vector2(w, h);
                }

            case Stretch.Uniform:
                {
                    float scaleX = float.IsInfinity(availableSize.X) ? 1.0f : availableSize.X / naturalSize.X;
                    float scaleY = float.IsInfinity(availableSize.Y) ? 1.0f : availableSize.Y / naturalSize.Y;
                    float scale = Math.Min(scaleX, scaleY);

                    if (float.IsInfinity(availableSize.X) && float.IsInfinity(availableSize.Y))
                    {
                        return naturalSize;
                    }

                    return naturalSize * scale;
                }

            case Stretch.UniformToFill:
                {
                    float w = float.IsInfinity(availableSize.X) ? naturalSize.X : availableSize.X;
                    float h = float.IsInfinity(availableSize.Y) ? naturalSize.Y : availableSize.Y;
                    return new Vector2(w, h);
                }

            default:
                return naturalSize;
        }
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        base.ArrangeOverride(arrangeRect);
    }

    public override void OnRender(DrawingContext context)
    {
        var texture = ActiveTexture;
        if (texture == null)
        {
            return;
        }

        Vector2 naturalSize = new Vector2(texture.Width, texture.Height);
        Vector2 controlSize = Size;

        if (controlSize.X <= 0f || controlSize.Y <= 0f)
        {
            return;
        }

        Rect destRect = new Rect(0, 0, controlSize.X, controlSize.Y);

        switch (Stretch)
        {
            case Stretch.None:
                {
                    float x = (controlSize.X - naturalSize.X) / 2f;
                    float y = (controlSize.Y - naturalSize.Y) / 2f;
                    destRect = new Rect(x, y, naturalSize.X, naturalSize.Y);
                    break;
                }

            case Stretch.Fill:
                {
                    destRect = new Rect(0, 0, controlSize.X, controlSize.Y);
                    break;
                }

            case Stretch.Uniform:
                {
                    float scaleX = controlSize.X / naturalSize.X;
                    float scaleY = controlSize.Y / naturalSize.Y;
                    float scale = Math.Min(scaleX, scaleY);

                    float w = naturalSize.X * scale;
                    float h = naturalSize.Y * scale;
                    float x = (controlSize.X - w) / 2f;
                    float y = (controlSize.Y - h) / 2f;

                    destRect = new Rect(x, y, w, h);
                    break;
                }

            case Stretch.UniformToFill:
                {
                    float scaleX = controlSize.X / naturalSize.X;
                    float scaleY = controlSize.Y / naturalSize.Y;
                    float scale = Math.Max(scaleX, scaleY);

                    float w = naturalSize.X * scale;
                    float h = naturalSize.Y * scale;
                    float x = (controlSize.X - w) / 2f;
                    float y = (controlSize.Y - h) / 2f;

                    destRect = new Rect(x, y, w, h);
                    break;
                }
        }

        bool clip = Stretch == Stretch.UniformToFill;
        if (clip)
        {
            context.PushClip(new Rect(Vector2.Zero, Size));
        }

        context.DrawTexture(texture, destRect);

        if (clip)
        {
            context.PopClip();
        }
    }

    private static GpuTexture LoadBmp(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // 1. Read File Header (14 bytes)
        ushort magic = reader.ReadUInt16();
        if (magic != 0x4D42) // 'BM'
        {
            throw new InvalidDataException("Not a valid BMP file (magic number mismatch).");
        }

        reader.ReadUInt32(); // File size
        reader.ReadUInt16(); // Reserved 1
        reader.ReadUInt16(); // Reserved 2
        uint offBits = reader.ReadUInt32(); // Pixel data offset

        // 2. Read Info Header (offset 14, size 40 bytes)
        uint headerSize = reader.ReadUInt32();
        if (headerSize < 40)
        {
            throw new InvalidDataException("Unsupported BMP header size.");
        }

        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        ushort planes = reader.ReadUInt16();
        ushort bitCount = reader.ReadUInt16();
        uint compression = reader.ReadUInt32();

        if (compression != 0 && compression != 3) // BI_RGB (0) or BI_BITFIELDS (3)
        {
            throw new InvalidDataException($"Unsupported compression mode: {compression}. Only uncompressed BMP is supported.");
        }

        if (bitCount != 24 && bitCount != 32)
        {
            throw new InvalidDataException($"Unsupported bit depth: {bitCount}. Only 24-bit and 32-bit BMP are supported.");
        }

        stream.Position = offBits;

        bool bottomUp = height > 0;
        int absHeight = Math.Abs(height);
        int absWidth = Math.Abs(width);

        byte[] rgbaPixels = new byte[absWidth * absHeight * 4];

        int bytesPerPixel = bitCount / 8;
        int rowSize = ((absWidth * bitCount + 31) / 32) * 4;
        byte[] rowBuffer = new byte[rowSize];

        for (int y = 0; y < absHeight; y++)
        {
            reader.Read(rowBuffer, 0, rowSize);

            int targetY = bottomUp ? (absHeight - 1 - y) : y;
            int targetRowOffset = targetY * absWidth * 4;

            for (int x = 0; x < absWidth; x++)
            {
                int srcOffset = x * bytesPerPixel;
                int destOffset = targetRowOffset + x * 4;

                if (bitCount == 24)
                {
                    byte b = rowBuffer[srcOffset + 0];
                    byte g = rowBuffer[srcOffset + 1];
                    byte r = rowBuffer[srcOffset + 2];
                    
                    rgbaPixels[destOffset + 0] = r;
                    rgbaPixels[destOffset + 1] = g;
                    rgbaPixels[destOffset + 2] = b;
                    rgbaPixels[destOffset + 3] = 255;
                }
                else if (bitCount == 32)
                {
                    byte b = rowBuffer[srcOffset + 0];
                    byte g = rowBuffer[srcOffset + 1];
                    byte r = rowBuffer[srcOffset + 2];
                    byte a = rowBuffer[srcOffset + 3];

                    rgbaPixels[destOffset + 0] = r;
                    rgbaPixels[destOffset + 1] = g;
                    rgbaPixels[destOffset + 2] = b;
                    rgbaPixels[destOffset + 3] = a;
                }
            }
        }

        var context = WgpuContext.Current;
        if (context == null)
        {
            throw new InvalidOperationException("WgpuContext.Current is not initialized. Cannot create GpuTexture.");
        }

        var texture = new GpuTexture(
            context,
            (uint)absWidth,
            (uint)absHeight,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Path.GetFileName(path)
        );

        texture.WritePixels<byte>(rgbaPixels);
        return texture;
    }

    ~Image()
    {
        _loadedTexture?.Dispose();
    }
}
