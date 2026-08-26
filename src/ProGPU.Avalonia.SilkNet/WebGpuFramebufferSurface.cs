using System;
using Avalonia.Platform;
#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Platform.Surfaces;
#endif
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace Avalonia.SilkNet;

/// <summary>
/// Exposes a WGPU surface through Avalonia's framebuffer contract while
/// preserving a CPU fallback for renderers that write pixels.
/// </summary>
internal sealed unsafe class SilkNetFramebufferSurface :
    IFramebufferPlatformSurface,
    IFramebufferRenderTarget
{
    private readonly WindowImpl _owner;
    private readonly SilkNetFramebufferAddressProvider _storage = new();
    private GpuTexture? _cpuUploadTexture;
    private bool _disposed;

    internal SilkNetFramebufferSurface(WindowImpl owner)
    {
        _owner = owner;
    }

    public IFramebufferRenderTarget CreateFramebufferRenderTarget()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return this;
    }

#if AVALONIA11
    public bool RetainsFrameContents => false;

    public ILockedFramebuffer Lock() =>
        Lock(out _);

    public ILockedFramebuffer Lock(
        out FramebufferLockProperties properties)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WgpuContext context = _owner.EnsureWebGpuContext();
        PixelSize size = _owner.FramebufferPixelSize;
        int rowBytes = checked(size.Width * 4);
        IntPtr address = _storage.GetAddress(
            checked(rowBytes * size.Height));
        properties = default;
        return new LockedFrame(
            this,
            context,
            address,
            size,
            rowBytes,
            _owner.RenderScaling);
    }
#else
    public ILockedFramebuffer Lock(
        IRenderTarget.RenderTargetSceneInfo sceneInfo,
        out FramebufferLockProperties properties)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WgpuContext context = _owner.EnsureWebGpuContext();
        PixelSize size = sceneInfo.Size.Width > 0 &&
                         sceneInfo.Size.Height > 0
            ? sceneInfo.Size
            : _owner.FramebufferPixelSize;
        int rowBytes = checked(size.Width * 4);
        IntPtr address = _storage.GetAddress(
            checked(rowBytes * size.Height));
        properties = default;
        return new LockedFrame(
            this,
            context,
            address,
            size,
            rowBytes,
            sceneInfo.Scaling);
    }
#endif

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cpuUploadTexture?.Dispose();
        _cpuUploadTexture = null;
        _storage.Dispose();
    }

    private void PresentCpuFrame(
        WgpuContext context,
        IntPtr address,
        PixelSize size,
        int rowBytes)
    {
        if (GpuFramebufferPresentationRegistry.TryPresent(
                address,
                (IntPtr)context.Surface))
        {
            return;
        }

        if (context.IsDisposed ||
            context.IsDeviceLost ||
            context.Surface is null)
        {
            return;
        }

        uint width = checked((uint)size.Width);
        uint height = checked((uint)size.Height);
        int byteCount = checked(rowBytes * size.Height);

        GpuTexture texture = _cpuUploadTexture ??=
            CreateUploadTexture(context, width, height);
        if (!ReferenceEquals(texture.Context, context) ||
            texture.Width != width ||
            texture.Height != height)
        {
            texture.Dispose();
            texture = _cpuUploadTexture =
                CreateUploadTexture(context, width, height);
        }

        texture.WritePixels(
            new ReadOnlySpan<byte>(
                (void*)address,
                byteCount));
        if (!context.TryReconfigureIfNeeded(width, height))
            return;

        var surfaceTexture = new SurfaceTexture();
        context.Api.SurfaceGetCurrentTexture(
            context.Surface,
            &surfaceTexture);
        if (surfaceTexture.Status !=
            SurfaceGetCurrentTextureStatus.Success)
        {
            if (surfaceTexture.Texture is not null)
                context.Api.TextureRelease(surfaceTexture.Texture);
            if (surfaceTexture.Status ==
                SurfaceGetCurrentTextureStatus.DeviceLost)
            {
                context.ReportDeviceLost(
                    DeviceLostReason.Unknown,
                    "The Avalonia Silk.NET presentation surface reported device loss.");
            }
            else if (surfaceTexture.Status ==
                SurfaceGetCurrentTextureStatus.OutOfMemory)
            {
                throw new OutOfMemoryException(
                    "The Avalonia Silk.NET presentation surface ran out of memory.");
            }
            else if (surfaceTexture.Status is
                SurfaceGetCurrentTextureStatus.Outdated or
                SurfaceGetCurrentTextureStatus.Lost)
            {
                context.InvalidateSurfaceConfiguration();
            }
            return;
        }

        TextureView* view = null;
        try
        {
            var descriptor = new TextureViewDescriptor
            {
                Format = context.SwapChainFormat,
                Dimension = TextureViewDimension.Dimension2D,
                MipLevelCount = 1,
                ArrayLayerCount = 1,
                Aspect = TextureAspect.All
            };
            view = context.Api.TextureCreateView(
                surfaceTexture.Texture,
                &descriptor);
            if (view is null)
                return;

            GpuTextureBlitter.Blit(
                texture,
                view,
                context.SwapChainFormat);
            context.Api.SurfacePresent(context.Surface);
        }
        finally
        {
            if (view is not null)
                context.Api.TextureViewRelease(view);
            if (surfaceTexture.Texture is not null)
                context.Api.TextureRelease(surfaceTexture.Texture);
        }
    }

    private static GpuTexture CreateUploadTexture(
        WgpuContext context,
        uint width,
        uint height) =>
        new(
            context,
            width,
            height,
            TextureFormat.Bgra8Unorm,
            TextureUsage.CopyDst |
            TextureUsage.TextureBinding,
            "Avalonia Silk.NET CPU presentation",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

    private sealed class LockedFrame :
        ILockedFramebuffer,
        IPlatformHandle,
        IGpuDirectPresentationFrame
    {
        private SilkNetFramebufferSurface? _owner;
        private readonly WgpuContext _context;
        private bool _gpuPresentationComplete;

        internal LockedFrame(
            SilkNetFramebufferSurface owner,
            WgpuContext context,
            IntPtr address,
            PixelSize size,
            int rowBytes,
            double scaling)
        {
            _owner = owner;
            _context = context;
            Address = address;
            Size = size;
            RowBytes = rowBytes;
            double effectiveScaling =
                scaling > 0 ? scaling : 1;
            Dpi = new Vector(
                effectiveScaling * 96,
                effectiveScaling * 96);
        }

        public IntPtr Address { get; }
        public PixelSize Size { get; }
        public int RowBytes { get; }
        public Vector Dpi { get; }
        public PixelFormat Format => PixelFormats.Bgra8888;
        public AlphaFormat AlphaFormat => AlphaFormat.Premul;
        public IntPtr Handle => (IntPtr)_context.Surface;
        public string HandleDescriptor => "WGPU_SURFACE";

        public void MarkGpuPresentationComplete() =>
            _gpuPresentationComplete = true;

        public void Dispose()
        {
            SilkNetFramebufferSurface? owner = _owner;
            _owner = null;
            if (_gpuPresentationComplete)
                return;
            owner?.PresentCpuFrame(
                _context,
                Address,
                Size,
                RowBytes);
        }
    }
}
