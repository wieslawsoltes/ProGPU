using System;
using System.IO;
using System.Runtime.InteropServices;
#if !AVALONIA11
using Avalonia.Media.Imaging;
#endif
using Avalonia.Platform;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Avalonia.ProGpu;

/// <summary>
/// GPU-affined Avalonia drawing layer. One texture serves as render attachment
/// and sample source; CPU pixels exist only at an explicit snapshot/save
/// boundary.
/// </summary>
internal sealed class SurfaceRenderTarget :
    IProGpuBitmapSource,
    IDrawingContextLayerWithRenderContextAffinityImpl
{
    public struct CreateInfo
    {
        public int Width;
        public int Height;
        public Vector Dpi;
        public bool UseScaledDrawing;
        public bool DisableTextLcdRendering;
        public PixelFormat? Format;
        public WgpuContext? Context;
    }

    private readonly OffscreenTextureCache _commandCache = new();
    private readonly DrawingContextImpl _recordingContext;
    private bool _contextIssued;
    private bool _disposed;

    public SurfaceRenderTarget(CreateInfo createInfo)
    {
        if (createInfo.Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(createInfo.Width));
        if (createInfo.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(createInfo.Height));

        PixelSize = new PixelSize(createInfo.Width, createInfo.Height);
        Dpi = createInfo.Dpi;
        WgpuContext context =
            createInfo.Context ??
            GetOrCreateContext(PixelSize, Dpi);
        Texture = new GpuTexture(
            context,
            checked((uint)createInfo.Width),
            checked((uint)createInfo.Height),
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding |
            TextureUsage.RenderAttachment |
            TextureUsage.CopySrc |
            TextureUsage.CopyDst,
            "Avalonia drawing layer",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        _recordingContext = new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Size = PixelSize,
                Dpi = Dpi,
                ScaleDrawingToDpi = createInfo.UseScaledDrawing,
                DisableSubpixelTextRendering =
                    createInfo.DisableTextLcdRendering,
                PreserveRecordedCommandsOnDispose = true,
                CacheHolder = _commandCache,
                GpuRenderTarget = Texture,
                GpuRenderSynchronizationLock = this,
                GpuRenderStarting = OnGpuRenderStarting
            });
    }

    public GpuTexture? Texture { get; private set; }
    public PixelSize PixelSize { get; }
    public Vector Dpi { get; }
    public int Version { get; private set; } = 1;
    public bool IsCorrupted =>
        Texture is null ||
        Texture.IsDisposed ||
        Texture.Context.IsDisposed ||
        Texture.Context.IsDeviceLost;
    public bool CanBlit => true;
    public bool HasRenderContextAffinity =>
        Texture is { IsDisposed: false };
    public RenderTargetProperties Properties => default;

    public IDrawingContextImpl CreateDrawingContext()
    {
        ThrowIfDisposed();
        if (_contextIssued)
            _recordingContext.Reset();
        else
            _contextIssued = true;
        return _recordingContext;
    }

#if AVALONIA11
    public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing) =>
        CreateDrawingContext();
#endif

    public void EnsureGpuTexture()
    {
        ThrowIfDisposed();
        FlushPendingCommands();
    }

    public void Blit(IDrawingContextImpl context)
    {
        ThrowIfDisposed();
        FlushPendingCommands();
        if (context is not DrawingContextImpl destination ||
            Texture is not { IsDisposed: false } texture)
        {
            return;
        }

        destination.DrawingContext.PushBlendMode(GpuBlendMode.Src);
        try
        {
            destination.DrawingContext.DrawTexture(
                texture,
                new ProGPU.Scene.Rect(
                    0,
                    0,
                    PixelSize.Width,
                    PixelSize.Height),
                new ProGPU.Scene.Rect(
                    0,
                    0,
                    PixelSize.Width,
                    PixelSize.Height),
                DrawingContextImpl.ToProGpuMatrix(destination.Transform));
        }
        finally
        {
            destination.DrawingContext.PopBlendMode();
        }
    }

    public IBitmapImpl CreateNonAffinedSnapshot()
    {
        ThrowIfDisposed();
        FlushPendingCommands();
        return new ImmutableBitmap(
            PixelSize,
            Dpi,
            ReadPixels(),
            AlphaFormat.Premul,
            retainPixelsForContextMigration: true);
    }

    public void Save(string fileName, int? quality = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        using FileStream destination = File.Create(fileName);
#if AVALONIA11
        Save(destination, quality);
#else
        Save(destination, PngBitmapEncoderOptions.Default);
#endif
    }

    public void Save(
        Stream stream,
#if AVALONIA11
        int? quality = null)
#else
        BitmapEncoderOptions options)
#endif
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();
        FlushPendingCommands();
        using Image<Rgba32> image = Image.LoadPixelData(
            ReadPixels(),
            PixelSize.Width,
            PixelSize.Height);
#if AVALONIA11
        image.SaveAsPng(stream);
#else
        AvaloniaBitmapEncoding.Save(image, stream, options);
#endif
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _recordingContext.Dispose();
        _commandCache.Dispose();
        Texture?.Dispose();
        Texture = null;
    }

    private bool FlushPendingCommands()
    {
        if (_recordingContext.DrawingContext.Commands.Count == 0)
            return false;
        _recordingContext.Dispose();
        _recordingContext.DrawingContext.Clear();
        return true;
    }

    private Rgba32[] ReadPixels()
    {
        GpuTexture texture = Texture ??
            throw new ObjectDisposedException(nameof(SurfaceRenderTarget));
        byte[] bytes = texture.ReadPixels();
        var pixels = new Rgba32[checked(PixelSize.Width * PixelSize.Height)];
        MemoryMarshal.Cast<byte, Rgba32>(bytes).CopyTo(pixels);
        return pixels;
    }

    private void OnGpuRenderStarting()
    {
        Version++;
    }

    private static WgpuContext GetOrCreateContext(
        PixelSize size,
        Vector dpi)
    {
        if (WgpuContext.Current is { IsDisposed: false } current)
            return current;

        using var bootstrap = new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Size = size,
                Dpi = dpi
            });
        return WgpuContext.Current ??
            throw new InvalidOperationException(
                "ProGPU did not initialize a WebGPU context.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
