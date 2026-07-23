using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using StbImageSharp;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Immutable encoded image payload. Decoding is cached on first render and GPU upload
/// remains demand-driven, so constructing or measuring the source does not initialize WebGPU.
/// </summary>
public sealed class EncodedImageSource
{
    private readonly byte[] _data;
    private readonly object _sync = new();
    private byte[]? _rgba;
    private bool _decodeAttempted;

    public EncodedImageSource(ReadOnlySpan<byte> data, int suggestedWidth = 0, int suggestedHeight = 0)
    {
        _data = data.ToArray();
        SuggestedWidth = Math.Max(0, suggestedWidth);
        SuggestedHeight = Math.Max(0, suggestedHeight);
    }

    internal EncodedImageSource(byte[] ownedData, int suggestedWidth, int suggestedHeight)
    {
        _data = ownedData ?? throw new ArgumentNullException(nameof(ownedData));
        SuggestedWidth = Math.Max(0, suggestedWidth);
        SuggestedHeight = Math.Max(0, suggestedHeight);
    }

    public int SuggestedWidth { get; }
    public int SuggestedHeight { get; }

    internal bool TryGetRgba(out ReadOnlyMemory<byte> pixels, out int width, out int height)
    {
        lock (_sync)
        {
            if (!_decodeAttempted)
            {
                _decodeAttempted = true;
                try
                {
                    ImageResult decoded = ImageResult.FromMemory(_data, ColorComponents.RedGreenBlueAlpha);
                    _rgba = decoded.Data;
                    DecodedWidth = decoded.Width;
                    DecodedHeight = decoded.Height;
                }
                catch
                {
                    _rgba = null;
                }
            }
            pixels = _rgba;
            width = DecodedWidth;
            height = DecodedHeight;
            return _rgba is not null && width > 0 && height > 0;
        }
    }

    internal int DecodedWidth { get; private set; }
    internal int DecodedHeight { get; private set; }
}

public class Image : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(object),
            typeof(Image),
            new PropertyMetadata(null, OnSourceChanged)
            {
                AffectsMeasure = true,
                AffectsRender = true
            });

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            nameof(Stretch),
            typeof(Stretch),
            typeof(Image),
            new PropertyMetadata(Stretch.Uniform, OnStretchChanged)
            {
                AffectsMeasure = true,
                AffectsRender = true
            });

    private object? _source;
    private GpuTexture? _loadedTexture;
    private Stretch _stretch = Stretch.Uniform;

    private float _brightness = 0f;
    private float _contrast = 1f;
    private float _saturation = 1f;
    private float _grayscale = 0f;
    private float _sepia = 0f;
    private float _invert = 0f;
    private float _blurSigma = 0f;

    protected override bool ShouldInheritProperty(DependencyProperty property) =>
        property != FlowDirectionProperty && base.ShouldInheritProperty(property);

    public float Brightness
    {
        get => _brightness;
        set { if (_brightness != value) { _brightness = value; Invalidate(); } }
    }

    public float Contrast
    {
        get => _contrast;
        set { if (_contrast != value) { _contrast = value; Invalidate(); } }
    }

    public float Saturation
    {
        get => _saturation;
        set { if (_saturation != value) { _saturation = value; Invalidate(); } }
    }

    public float Grayscale
    {
        get => _grayscale;
        set { if (_grayscale != value) { _grayscale = value; Invalidate(); } }
    }

    public float Sepia
    {
        get => _sepia;
        set { if (_sepia != value) { _sepia = value; Invalidate(); } }
    }

    public float Invert
    {
        get => _invert;
        set { if (_invert != value) { _invert = value; Invalidate(); } }
    }

    public float BlurSigma
    {
        get => _blurSigma;
        set { if (_blurSigma != value) { _blurSigma = value; Invalidate(); } }
    }

    public object? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var image = (Image)dependencyObject;
        image._source = args.NewValue;

        if (image._loadedTexture != null)
        {
            image._loadedTexture.Dispose();
            image._loadedTexture = null;
        }

        if (image._source is string path)
        {
            try
            {
                image._loadedTexture = LoadBmp(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Image] Failed to load BMP: {ex.Message}");
            }
        }

        image.InvalidateMeasure();
        image.Invalidate();
    }

    public Stretch Stretch
    {
        get => (Stretch)(GetValue(StretchProperty) ?? Stretch.Uniform);
        set => SetValue(StretchProperty, value);
    }

    private static void OnStretchChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((Image)dependencyObject)._stretch = (Stretch)(args.NewValue ?? Stretch.Uniform);

    private GpuTexture? ActiveTexture => _source is GpuTexture tex ? tex : _loadedTexture;

    private Vector2 NaturalSize
    {
        get
        {
            var tex = ActiveTexture;
            if (tex != null) return new Vector2(tex.Width, tex.Height);
            return _source is EncodedImageSource encoded
                ? new Vector2(encoded.SuggestedWidth, encoded.SuggestedHeight)
                : Vector2.Zero;
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
        var texture = EnsureActiveTexture();
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

        Matrix4x4 flowTransform = FlowDirection == FlowDirection.RightToLeft
            ? Matrix4x4.CreateScale(-1f, 1f, 1f) * Matrix4x4.CreateTranslation(controlSize.X, 0f, 0f)
            : Matrix4x4.Identity;

        if (Brightness != 0f || Contrast != 1f || Saturation != 1f || Grayscale != 0f || Sepia != 0f || Invert != 0f || BlurSigma > 0.01f)
        {
            context.DrawImageWithEffect(
                texture,
                destRect,
                Brightness,
                Contrast,
                Saturation,
                Grayscale,
                Sepia,
                Invert,
                BlurSigma,
                transform: flowTransform);
        }
        else
        {
            if (FlowDirection == FlowDirection.RightToLeft)
            {
                context.DrawTexture(
                    texture,
                    destRect,
                    new Rect(0f, 0f, texture.Width, texture.Height),
                    flowTransform);
            }
            else
            {
                context.DrawTexture(texture, destRect);
            }
        }

        if (clip)
        {
            context.PopClip();
        }
    }

    private GpuTexture? EnsureActiveTexture()
    {
        if (ActiveTexture is { } active) return active;
        if (_source is not EncodedImageSource encoded ||
            !encoded.TryGetRgba(out ReadOnlyMemory<byte> pixels, out int width, out int height) ||
            WgpuContext.Current is not { } context)
        {
            return null;
        }

        _loadedTexture = new GpuTexture(
            context,
            (uint)width,
            (uint)height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Encoded Image",
            alphaMode: GpuTextureAlphaMode.Straight);
        _loadedTexture.WritePixels<byte>(pixels.Span);
        return _loadedTexture;
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
            Path.GetFileName(path),
            alphaMode: GpuTextureAlphaMode.Straight
        );

        texture.WritePixels<byte>(rgbaPixels);
        return texture;
    }

    ~Image()
    {
        _loadedTexture?.Dispose();
    }
}
