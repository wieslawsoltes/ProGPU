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
/// Mutable bitmap with one lazily allocated CPU framebuffer and one lazily
/// synchronized GPU texture. CPU and GPU freshness are explicit and never
/// represented by duplicate authoritative copies.
/// </summary>
internal unsafe class WriteableBitmapImpl :
    IWriteableBitmapImpl,
    IProGpuBitmapSource,
    IPortableProGpuBitmapSource
{
    private readonly object _gate = new();
    private readonly PixelFormat _format;
    private readonly AlphaFormat _alphaFormat;
    private readonly TextureUsage _textureUsage;
    private readonly string _textureLabel =
        "Avalonia writable bitmap";
    private IntPtr _cpuStorage;
    private int _stride;
    private bool _cpuCurrent;
    private bool _gpuCurrent;
    private bool _activeLock;
    private bool _disposed;

    public WriteableBitmapImpl(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using Image<Rgba32> image = Image.Load<Rgba32>(stream);
        PixelSize = new PixelSize(image.Width, image.Height);
        Dpi = new Vector(96, 96);
        _format = PixelFormats.Rgba8888;
        _alphaFormat = Avalonia.Platform.AlphaFormat.Unpremul;
        _textureUsage = DefaultTextureUsage;
        AllocateCpuStorage();
        var pixels = new Rgba32[checked(image.Width * image.Height)];
        image.CopyPixelDataTo(pixels);
        AvaloniaPixelTransfer.CopyFromRgba(
            pixels,
            PixelSize,
            _stride,
            _format,
            _cpuStorage);
        _cpuCurrent = true;
        EnsureGpuTexture();
    }

    public WriteableBitmapImpl(
        Stream stream,
        int decodeSize,
        bool horizontal,
        BitmapInterpolationMode interpolationMode)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (decodeSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(decodeSize));

        using Image<Rgba32> image = Image.Load<Rgba32>(stream);
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
            Mode = ResizeMode.Stretch,
            Sampler = SelectSampler(interpolationMode)
        }));
        PixelSize = new PixelSize(width, height);
        Dpi = new Vector(96, 96);
        _format = PixelFormats.Rgba8888;
        _alphaFormat = Avalonia.Platform.AlphaFormat.Unpremul;
        _textureUsage = DefaultTextureUsage;
        AllocateCpuStorage();
        var pixels = new Rgba32[checked(width * height)];
        image.CopyPixelDataTo(pixels);
        AvaloniaPixelTransfer.CopyFromRgba(
            pixels,
            PixelSize,
            _stride,
            _format,
            _cpuStorage);
        _cpuCurrent = true;
        EnsureGpuTexture();
    }

    public WriteableBitmapImpl(
        PixelSize size,
        Vector dpi,
        PixelFormat format,
        AlphaFormat alphaFormat)
        : this(size, dpi, format, alphaFormat, DefaultTextureUsage)
    {
        AllocateCpuStorage();
        new Span<byte>(
            (void*)_cpuStorage,
            checked(_stride * PixelSize.Height)).Clear();
        _cpuCurrent = true;
        EnsureGpuTexture();
    }

    protected unsafe WriteableBitmapImpl(
        PixelSize size,
        Vector dpi,
        PixelFormat format,
        AlphaFormat alphaFormat,
        TextureUsage textureUsage,
        string textureLabel = "Avalonia writable bitmap")
    {
        ValidateSize(size);
        ValidateFormat(format);
        PixelSize = size;
        Dpi = dpi;
        _format = format;
        _alphaFormat = alphaFormat;
        _textureUsage = textureUsage;
        _textureLabel = textureLabel;
    }

    private static TextureUsage DefaultTextureUsage =>
        TextureUsage.TextureBinding |
        TextureUsage.CopyDst |
        TextureUsage.CopySrc;

    public GpuTexture? Texture { get; protected set; }
    public PixelSize PixelSize { get; }
    public Vector Dpi { get; }
    public PixelFormat? Format => _format;
    public AlphaFormat? AlphaFormat => _alphaFormat;
    public int Version { get; private set; } = 1;
    internal bool HasCurrentCpuPixels => _cpuCurrent;
    internal bool HasAllocatedCpuPixels => _cpuStorage != IntPtr.Zero;
    protected object GpuRenderSynchronizationLock => _gate;

    public void EnsureGpuTexture()
    {
        WgpuContext? context =
            WgpuContext.Current is
            {
                IsDisposed: false,
                IsDeviceLost: false
            } current
                ? current
                : Texture?.Context;
        if (context is null)
            return;

        _ = GetTexture(context);
    }

    public GpuTexture? GetTexture(WgpuContext requiredContext)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        ObjectDisposedException.ThrowIf(
            requiredContext.IsDisposed,
            requiredContext);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (Texture is
                {
                    IsDisposed: false
                } existing &&
                existing.Context.SharesDeviceWith(requiredContext))
            {
                SynchronizeCpuPixelsToGpu();
                return existing;
            }

            if (Texture is
                {
                    IsDisposed: false
                } previous &&
                _gpuCurrent &&
                !_cpuCurrent)
            {
                EnsureCpuStorageCurrent();
            }

            CreateTexture(requiredContext);
            SynchronizeCpuPixelsToGpu();
            return Texture;
        }
    }

    protected void MarkGpuContentChanged()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _gpuCurrent = true;
            _cpuCurrent = false;
            Version++;
        }
    }

    public ILockedFramebuffer Lock()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_activeLock)
                throw new InvalidOperationException("The bitmap is already locked.");
            EnsureCpuStorageCurrent();
            _activeLock = true;
            return new BitmapFramebuffer(this);
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
        Rgba32[] pixels;
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureCpuStorageCurrent();
            pixels = AvaloniaPixelTransfer.CopyToRgba(
                PixelSize,
                _stride,
                _format,
                _cpuStorage);
        }

        using Image<Rgba32> image = Image.LoadPixelData(
            pixels,
            PixelSize.Width,
            PixelSize.Height);
        image.SaveAsPng(stream);
    }

    public virtual void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_activeLock)
                throw new InvalidOperationException(
                    "Cannot dispose a locked bitmap.");
            _disposed = true;
            Texture?.Dispose();
            Texture = null;
            if (_cpuStorage != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_cpuStorage);
                _cpuStorage = IntPtr.Zero;
            }
        }
    }

    private void CreateTexture(WgpuContext context)
    {
        Texture?.Dispose();
        Texture = new GpuTexture(
            context,
            checked((uint)PixelSize.Width),
            checked((uint)PixelSize.Height),
            TextureFormat.Rgba8Unorm,
            _textureUsage,
            _textureLabel,
            alphaMode:
                _alphaFormat == Avalonia.Platform.AlphaFormat.Premul
                ? GpuTextureAlphaMode.Premultiplied
                : GpuTextureAlphaMode.Straight);
        _gpuCurrent = false;
    }

    private void SynchronizeCpuPixelsToGpu()
    {
        if (_gpuCurrent)
            return;

        if (!_cpuCurrent)
        {
            // WebGPU texture creation guarantees zero-initialized texels.
            // A never-written render target is therefore transparently black
            // without allocating or uploading a CPU-sized zero buffer.
            _gpuCurrent = true;
            return;
        }

        Rgba32[] pixels = AvaloniaPixelTransfer.CopyToRgba(
            PixelSize,
            _stride,
            _format,
            _cpuStorage);
        Texture!.WritePixels(pixels);
        _gpuCurrent = true;
    }

    private void EnsureCpuStorageCurrent()
    {
        AllocateCpuStorage();
        if (_cpuCurrent)
            return;

        if (Texture is not { IsDisposed: false } texture || !_gpuCurrent)
        {
            new Span<byte>(
                (void*)_cpuStorage,
                checked(_stride * PixelSize.Height)).Clear();
            _cpuCurrent = true;
            return;
        }

        byte[] rgbaBytes = texture.ReadPixels();
        ReadOnlySpan<Rgba32> pixels =
            MemoryMarshal.Cast<byte, Rgba32>(rgbaBytes);
        AvaloniaPixelTransfer.CopyFromRgba(
            pixels,
            PixelSize,
            _stride,
            _format,
            _cpuStorage);
        _cpuCurrent = true;
    }

    private void AllocateCpuStorage()
    {
        if (_cpuStorage != IntPtr.Zero)
            return;
        int bytesPerPixel = _format == PixelFormats.Rgb565 ? 2 : 4;
        _stride = checked((PixelSize.Width * bytesPerPixel + 3) & ~3);
        _cpuStorage = Marshal.AllocHGlobal(
            checked(_stride * PixelSize.Height));
    }

    private void CompleteCpuWrite()
    {
        lock (_gate)
        {
            if (!_activeLock)
                return;
            _activeLock = false;
            _cpuCurrent = true;
            _gpuCurrent = false;
            Version++;
        }
    }

    private static IResampler SelectSampler(
        BitmapInterpolationMode interpolationMode)
    {
        return interpolationMode switch
        {
            BitmapInterpolationMode.LowQuality =>
                KnownResamplers.NearestNeighbor,
            BitmapInterpolationMode.MediumQuality =>
                KnownResamplers.Triangle,
            BitmapInterpolationMode.HighQuality =>
                KnownResamplers.Lanczos3,
            _ => KnownResamplers.Bicubic
        };
    }

    private static void ValidateSize(PixelSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
    }

    private static void ValidateFormat(PixelFormat format)
    {
        if (format != PixelFormats.Rgb565 &&
            format != PixelFormats.Rgba8888 &&
            format != PixelFormats.Bgra8888)
        {
            throw new NotSupportedException(
                $"Unsupported Avalonia pixel format {format}.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class BitmapFramebuffer : ILockedFramebuffer
    {
        private WriteableBitmapImpl? _owner;

        public BitmapFramebuffer(WriteableBitmapImpl owner)
        {
            _owner = owner;
        }

        private WriteableBitmapImpl Owner =>
            _owner ?? throw new ObjectDisposedException(nameof(BitmapFramebuffer));

        public IntPtr Address => Owner._cpuStorage;
        public PixelSize Size => Owner.PixelSize;
        public int RowBytes => Owner._stride;
        public Vector Dpi => Owner.Dpi;
        public PixelFormat Format => Owner._format;
        public AlphaFormat AlphaFormat => Owner._alphaFormat;

        public void Dispose()
        {
            WriteableBitmapImpl? owner = _owner;
            if (owner is null)
                return;
            _owner = null;
            owner.CompleteCpuWrite();
        }
    }
}
