using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace Avalonia.ProGpu;

/// <summary>
/// Immutable Avalonia bitmap backed by one lazily created ProGPU texture.
/// Encoded and decoded CPU representations are released after an ordinary
/// upload; portable snapshots retain exactly one decoded representation.
/// </summary>
internal sealed class ImmutableBitmap :
    IProGpuBitmapSource,
    IPortableProGpuBitmapSource
{
    private readonly object _gate = new();
    private readonly bool _retainPortablePixels;
    private readonly GpuTextureAlphaMode _alphaMode;
    private byte[]? _encoded;
    private Rgba32[]? _pendingPixels;
    private bool _disposed;

    public ImmutableBitmap(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _encoded = ReadEncoded(stream);
        PixelSize = AvaloniaEncodedImage.Identify(_encoded);
        Dpi = new Vector(96, 96);
        _alphaMode = GpuTextureAlphaMode.Straight;
    }

    public ImmutableBitmap(
        ImmutableBitmap source,
        PixelSize destinationSize,
        BitmapInterpolationMode interpolationMode)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSize(destinationSize);
        using Image<Rgba32> image = Image.LoadPixelData(
            source.GetPixelsForCpu(),
            source.PixelSize.Width,
            source.PixelSize.Height);
        image.Mutate(operation => operation.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(
                destinationSize.Width,
                destinationSize.Height),
            Sampler = SelectSampler(interpolationMode),
            Mode = ResizeMode.Stretch
        }));
        _pendingPixels = new Rgba32[
            checked(destinationSize.Width * destinationSize.Height)];
        image.CopyPixelDataTo(_pendingPixels);
        PixelSize = destinationSize;
        Dpi = source.Dpi;
        _alphaMode = source._alphaMode;
        EnsureGpuTexture();
    }

    public ImmutableBitmap(
        Stream stream,
        int decodeSize,
        bool horizontal,
        BitmapInterpolationMode interpolationMode)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (decodeSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(decodeSize));

        byte[] encoded = ReadEncoded(stream);
        Rgba32[] sourcePixels = AvaloniaEncodedImage.Decode(encoded);
        PixelSize sourceSize = AvaloniaEncodedImage.Identify(encoded);
        using Image<Rgba32> image = Image.LoadPixelData(
            sourcePixels,
            sourceSize.Width,
            sourceSize.Height);
        int width;
        int height;
        if (horizontal)
        {
            width = decodeSize;
            height = Math.Max(
                1,
                checked((int)Math.Round(
                    image.Height * (double)decodeSize / image.Width)));
        }
        else
        {
            height = decodeSize;
            width = Math.Max(
                1,
                checked((int)Math.Round(
                    image.Width * (double)decodeSize / image.Height)));
        }

        image.Mutate(operation => operation.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(width, height),
            Sampler = SelectSampler(interpolationMode),
            Mode = ResizeMode.Stretch
        }));
        _pendingPixels = new Rgba32[checked(width * height)];
        image.CopyPixelDataTo(_pendingPixels);
        PixelSize = new PixelSize(width, height);
        Dpi = new Vector(96, 96);
        _alphaMode = GpuTextureAlphaMode.Straight;
        EnsureGpuTexture();
    }

    public ImmutableBitmap(
        PixelSize size,
        Vector dpi,
        int stride,
        PixelFormat format,
        AlphaFormat alphaFormat,
        IntPtr data)
    {
        ValidateSize(size);
        PixelSize = size;
        Dpi = dpi;
        _alphaMode = ToTextureAlphaMode(alphaFormat);
        _pendingPixels =
            AvaloniaPixelTransfer.CopyToRgba(size, stride, format, data);
        EnsureGpuTexture();
    }

    internal ImmutableBitmap(
        PixelSize size,
        Vector dpi,
        Rgba32[] pixels,
        AlphaFormat alphaFormat,
        bool retainPixelsForContextMigration)
    {
        ValidateSize(size);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(size.Width * size.Height))
        {
            throw new ArgumentException(
                "Pixel storage must match the bitmap dimensions.",
                nameof(pixels));
        }

        PixelSize = size;
        Dpi = dpi;
        _alphaMode = ToTextureAlphaMode(alphaFormat);
        _pendingPixels = pixels;
        _retainPortablePixels = retainPixelsForContextMigration;
        if (!retainPixelsForContextMigration)
            EnsureGpuTexture();
    }

    public GpuTexture? Texture { get; private set; }
    public PixelSize PixelSize { get; }
    public Vector Dpi { get; }
    public int Version => 1;
    internal bool HasRetainedDecodedPixels => _pendingPixels is not null;

    public void EnsureGpuTexture()
    {
        WgpuContext? context = WgpuContext.Current;
        if (context is not null)
            GetTexture(context);
    }

    public GpuTexture? GetTexture(WgpuContext requiredContext)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (Texture is { IsDisposed: false } existing &&
                existing.Context.SharesDeviceWith(requiredContext))
            {
                return existing;
            }

            Rgba32[] pixels = _pendingPixels ??
                (Texture is { IsDisposed: false } previous
                    ? ReadTexture(previous)
                    : Decode(_encoded));
            Texture?.Dispose();
            Texture = new GpuTexture(
                requiredContext,
                checked((uint)PixelSize.Width),
                checked((uint)PixelSize.Height),
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.CopyDst |
                TextureUsage.CopySrc,
                "Avalonia immutable bitmap",
                alphaMode: _alphaMode);
            Texture.WritePixels(pixels);

            _encoded = null;
            if (_retainPortablePixels)
                _pendingPixels = pixels;
            else
                _pendingPixels = null;
            return Texture;
        }
    }

    public void Save(string fileName, int? quality = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        using FileStream destination = File.Create(fileName);
        Save(destination, quality);
    }

    public void Save(Stream stream, int? quality = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_encoded is { Length: > 0 } encoded)
            {
                stream.Write(encoded);
                return;
            }

            Rgba32[] pixels = _pendingPixels ??
                (Texture is { IsDisposed: false } texture
                    ? ReadTexture(texture)
                    : throw new InvalidOperationException(
                        "The bitmap has no readable representation."));
            using Image<Rgba32> image = Image.LoadPixelData(
                pixels,
                PixelSize.Width,
                PixelSize.Height);
            image.SaveAsPng(stream);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Texture?.Dispose();
            Texture = null;
            _encoded = null;
            _pendingPixels = null;
        }
    }

    private Rgba32[] GetPixelsForCpu()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pendingPixels is not null)
                return _pendingPixels;
            if (_encoded is not null)
                return Decode(_encoded);
            if (Texture is { IsDisposed: false } texture)
                return ReadTexture(texture);
            throw new InvalidOperationException(
                "The bitmap has no readable representation.");
        }
    }

    private static byte[] ReadEncoded(Stream stream)
    {
        using var storage = new MemoryStream();
        stream.CopyTo(storage);
        return storage.ToArray();
    }

    private static Rgba32[] Decode(byte[]? encoded)
    {
        if (encoded is null)
        {
            throw new InvalidOperationException(
                "The encoded bitmap payload has already been released.");
        }

        return AvaloniaEncodedImage.Decode(encoded);
    }

    private static Rgba32[] ReadTexture(GpuTexture texture)
    {
        byte[] bytes = texture.ReadPixels();
        var pixels = new Rgba32[checked((int)(texture.Width * texture.Height))];
        MemoryMarshal.Cast<byte, Rgba32>(bytes).CopyTo(pixels);
        return pixels;
    }

    private static IResampler SelectSampler(
        BitmapInterpolationMode interpolationMode)
    {
        return interpolationMode switch
        {
            BitmapInterpolationMode.Unspecified =>
                KnownResamplers.Bicubic,
            BitmapInterpolationMode.LowQuality =>
                KnownResamplers.NearestNeighbor,
            BitmapInterpolationMode.MediumQuality =>
                KnownResamplers.Triangle,
            _ => KnownResamplers.Lanczos3
        };
    }

    private static GpuTextureAlphaMode ToTextureAlphaMode(
        AlphaFormat alphaFormat)
    {
        return alphaFormat == AlphaFormat.Premul
            ? GpuTextureAlphaMode.Premultiplied
            : GpuTextureAlphaMode.Straight;
    }

    private static void ValidateSize(PixelSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
