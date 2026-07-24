using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace SkiaSharp;

public class SKSurface : IDisposable, IGpuFramebufferPresenter
{
    private static readonly object s_compositorCacheScope = new();
    private readonly WgpuContext _context;
    private readonly DrawingContext _drawingContext;
    private readonly GpuTexture? _gpuTexture;
    private readonly IntPtr _pixels;
    private readonly int _rowBytes;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _ownsTexture;
    private readonly SKColorType _colorType;
    private readonly SKAlphaType _alphaType;
    private readonly SKColorSpace? _colorSpace;
    private readonly GRSurfaceOrigin _origin;
    private GpuTextureReadbackBuffer? _readbackBuffer;
    private byte[]? _readbackPixels;
    private bool _hasTextureContents;
    private bool _disposed;

    public SKCanvas Canvas { get; }

    private static Compositor GetCompositorForContext(WgpuContext context, TextureFormat renderFormat)
    {
        return SharedCompositorCache.GetOrCreate(context, renderFormat, s_compositorCacheScope);
    }

    private static void RemoveCachedCompositor(WgpuContext context)
    {
        SharedCompositorCache.Remove(context, s_compositorCacheScope);
    }

    private SKSurface(
        WgpuContext context,
        int width,
        int height,
        GpuTexture? texture,
        bool ownsTexture,
        IntPtr pixels,
        int rowBytes,
        SKColorType colorType,
        SKAlphaType alphaType,
        SKColorSpace? colorSpace = null,
        GRSurfaceOrigin origin = GRSurfaceOrigin.TopLeft,
        GRRecordingContext? recordingContext = null)
    {
        _context = context;
        _width = width;
        _height = height;
        _gpuTexture = texture;
        _ownsTexture = ownsTexture;
        _pixels = pixels;
        _rowBytes = pixels != IntPtr.Zero
            ? ResolveCpuSurfaceRowBytes(width, height, rowBytes, colorType, nameof(rowBytes))
            : rowBytes;
        _colorType = colorType;
        _alphaType = alphaType;
        _colorSpace = colorSpace;
        _origin = origin;

        _drawingContext = new DrawingContext();
        Canvas = new SKCanvas(_drawingContext, width, height, context, Flush);
        Canvas.AttachSurface(this);
        Canvas.AttachRecordingContext(recordingContext);
        _hasTextureContents = _gpuTexture != null && !_ownsTexture;

        if (_pixels != IntPtr.Zero && _gpuTexture != null)
        {
            byte[] temp = new byte[_width * _height * 4];
            unsafe
            {
                byte* src = (byte*)_pixels;
                fixed (byte* dst = temp)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        byte* srcRow = src + y * _rowBytes;
                        byte* dstRow = dst + y * _width * 4;
                        
                        for (int x = 0; x < _width; x++)
                        {
                            CopyPixelToRgbaPremultiplied(srcRow, dstRow, x, _colorType, _alphaType);
                        }
                    }
                }
            }
            _gpuTexture.WritePixels<byte>(temp);
            _hasTextureContents = true;
            GpuFramebufferPresentationRegistry.Register(_pixels, this);
        }
    }

    public static SKSurface Create(SKImageInfo info)
    {
        return Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

    public static SKSurface Create(SKImageInfo info, SKSurfaceProperties properties)
    {
        ValidateImageInfoDimensions(info, nameof(info));

        var ctx = SKContextHelper.GetContext();
        var texture = new GpuTexture(
            ctx,
            (uint)info.Width,
            (uint)info.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface Backing Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied
        );
        return new SKSurface(ctx, info.Width, info.Height, texture, true, IntPtr.Zero, 0, info.ColorType, info.AlphaType, info.ColorSpace);
    }

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, int rowBytes)
    {
        return Create(info, pixels, rowBytes, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, int rowBytes, SKSurfaceProperties properties)
    {
        ValidateImageInfoDimensions(info, nameof(info));

        int actualRowBytes = pixels != IntPtr.Zero
            ? ResolveCpuSurfaceRowBytes(info.Width, info.Height, rowBytes, info.ColorType, nameof(rowBytes))
            : rowBytes;
        var ctx = SKContextHelper.GetContext();
        var texture = new GpuTexture(
            ctx,
            (uint)info.Width,
            (uint)info.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface CPU-backed Backing Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied
        );
        return new SKSurface(ctx, info.Width, info.Height, texture, true, pixels, actualRowBytes, info.ColorType, info.AlphaType, info.ColorSpace);
    }

    public static SKSurface Create(GRContext grContext, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType)
    {
        return Create(grContext, renderTarget, origin, colorType, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

    public static SKSurface? Create(
        GRContext grContext,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType)
    {
        ArgumentNullException.ThrowIfNull(grContext);
        ArgumentNullException.ThrowIfNull(texture);
        var gpuTexture = texture.BackendTexture;
        if (gpuTexture == null)
        {
            return null;
        }

        if (!ReferenceEquals(gpuTexture.Context, grContext.Context))
        {
            throw new InvalidOperationException("The backend texture belongs to a different ProGPU context.");
        }

        if ((gpuTexture.Usage & TextureUsage.RenderAttachment) == 0)
        {
            throw new InvalidOperationException("The backend texture must include TextureUsage.RenderAttachment.");
        }

        if ((gpuTexture.Usage & TextureUsage.CopySrc) == 0)
        {
            throw new InvalidOperationException("The backend texture must include TextureUsage.CopySrc so SKSurface.Snapshot can copy from it.");
        }

        if (gpuTexture.SampleCount != 1)
        {
            throw new NotSupportedException("This WebGPU-backed Skia shim can only wrap single-sampled backend textures.");
        }

        return new SKSurface(
            grContext.Context,
            texture.Width,
            texture.Height,
            gpuTexture,
            false,
            IntPtr.Zero,
            0,
            colorType,
            SKAlphaType.Premul,
            null,
            origin,
            grContext);
    }

    public static SKSurface? Create(
        GRContext grContext,
        GRBackendTexture texture,
        SKColorType colorType) =>
        Create(grContext, texture, GRSurfaceOrigin.TopLeft, colorType);

    public static SKSurface? Create(
        GRContext grContext,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKSurfaceProperties properties) =>
        Create(grContext, texture, origin, colorType);

    public static SKSurface Create(GRContext grContext, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType, SKSurfaceProperties properties)
    {
        ArgumentNullException.ThrowIfNull(grContext);
        ArgumentNullException.ThrowIfNull(renderTarget);

        var texture = renderTarget.BackendTexture
            ?? throw new NotSupportedException("This WebGPU-backed Skia shim can only wrap ProGPU GpuTexture render targets. GL, Vulkan, and Metal backend handles cannot be rendered through this context.");

        if (!ReferenceEquals(texture.Context, grContext.Context))
        {
            throw new InvalidOperationException("The backend render target texture belongs to a different ProGPU context.");
        }

        if ((texture.Usage & TextureUsage.RenderAttachment) == 0)
        {
            throw new InvalidOperationException("The backend render target texture must include TextureUsage.RenderAttachment.");
        }

        if ((texture.Usage & TextureUsage.CopySrc) == 0)
        {
            throw new InvalidOperationException("The backend render target texture must include TextureUsage.CopySrc so SKSurface.Snapshot can copy from it.");
        }

        if (renderTarget.SampleCount != 1 || texture.SampleCount != 1)
        {
            throw new NotSupportedException("This WebGPU-backed Skia shim can only wrap single-sampled backend render targets.");
        }

        return new SKSurface(
            grContext.Context,
            renderTarget.Width,
            renderTarget.Height,
            texture,
            false,
            IntPtr.Zero,
            0,
            colorType,
            SKAlphaType.Premul,
            null,
            origin,
            grContext);
    }

    public static SKSurface Create(GRContext grContext, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType, SKColorSpace colorSpace)
    {
        return Create(grContext, renderTarget, origin, colorType, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

    public static SKSurface Create(GRContext grContext, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType, SKColorSpace colorSpace, SKSurfaceProperties properties)
    {
        return Create(grContext, renderTarget, origin, colorType, properties);
    }

    public static SKSurface Create(GRContext grContext, bool useMips, SKImageInfo imageInfo, SKSurfaceProperties properties)
    {
        ArgumentNullException.ThrowIfNull(grContext);
        ValidateImageInfoDimensions(imageInfo, nameof(imageInfo));

        var texture = new GpuTexture(
            grContext.Context,
            (uint)imageInfo.Width,
            (uint)imageInfo.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface Offscreen Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied
        );
        return new SKSurface(
            grContext.Context,
            imageInfo.Width,
            imageInfo.Height,
            texture,
            true,
            IntPtr.Zero,
            0,
            imageInfo.ColorType,
            imageInfo.AlphaType,
            imageInfo.ColorSpace,
            recordingContext: grContext);
    }

    public void Flush()
    {
        FlushCore(copyToCpu: true);
    }

    private void FlushCore(bool copyToCpu)
    {
        if (_gpuTexture == null) return;

        // Skip compiling if no commands have been recorded
        if (_drawingContext.Commands.Count == 0) return;

        var cpuReadbackRegions = Canvas.TakeCpuReadbackRegions();

        var visual = new DrawingVisual();
        visual.Size = new Vector2(_width, _height);
        if (_origin == GRSurfaceOrigin.BottomLeft)
        {
            visual.Transform = Matrix4x4.CreateScale(1f, -1f, 1f) * Matrix4x4.CreateTranslation(0f, _height, 0f);
        }

        visual.Context.Append(_drawingContext);

        var compositor = GetCompositorForContext(_context, _gpuTexture.Format);
        try
        {
            try
            {
                compositor.RenderOffscreen(visual, (uint)_width, (uint)_height, _gpuTexture, 0f, 1f, null, _hasTextureContents);
            }
            finally
            {
                visual.Context.Clear();
            }

            _hasTextureContents = true;

            // If CPU-backed surface, read pixels back and copy to memory pointer
            if (copyToCpu && _pixels != IntPtr.Zero)
            {
                int readbackByteCount = checked(_width * _height * 4);
                if (_readbackPixels == null || _readbackPixels.Length != readbackByteCount)
                {
                    _readbackPixels = GC.AllocateUninitializedArray<byte>(readbackByteCount);
                }
                _readbackBuffer ??= new GpuTextureReadbackBuffer(_context);
                try
                {
                    _gpuTexture.ReadPixels(_readbackPixels, _readbackBuffer);
                }
                finally
                {
                    _context.CleanupPendingResources();
                }
                CopyReadbackToCpu(_readbackPixels, cpuReadbackRegions);
            }
        }
        finally
        {
            // Clear recorded commands and dispose command-retained source/save-layer textures.
            _drawingContext.Clear();
            Canvas.ReleaseLayerTexturesAfterFlush();
        }
    }

    void IGpuFramebufferPresenter.Present(WgpuContext context, IntPtr surfaceHandle)
    {
        if (_disposed || _gpuTexture is null || _gpuTexture.IsDisposed)
        {
            return;
        }

        if (!ReferenceEquals(context, _context))
        {
            throw new InvalidOperationException(
                "The framebuffer presentation surface belongs to a different ProGPU context.");
        }

        using var currentScope = WgpuContext.PushCurrent(context);
        FlushCore(copyToCpu: false);
        if (_hasTextureContents)
        {
            GpuTextureSurfacePresenter.Present(_gpuTexture, surfaceHandle);
        }
    }

    internal bool TryGetLayerBackdropTexture(out GpuTexture texture)
    {
        if (_hasTextureContents && _gpuTexture is { IsDisposed: false } backingTexture)
        {
            texture = backingTexture;
            return true;
        }

        texture = null!;
        return false;
    }

    private unsafe void CopyReadbackToCpu(byte[] readBackBytes, SKRect[]? regions)
    {
        fixed (byte* src = readBackBytes)
        {
            byte* dst = (byte*)_pixels;

            if (regions == null)
            {
                CopyReadbackRegion(src, dst, 0, 0, _width, _height);
                return;
            }

            foreach (var region in regions)
            {
                var left = Math.Clamp((int)MathF.Floor(region.Left), 0, _width);
                var top = Math.Clamp((int)MathF.Floor(region.Top), 0, _height);
                var right = Math.Clamp((int)MathF.Ceiling(region.Right), left, _width);
                var bottom = Math.Clamp((int)MathF.Ceiling(region.Bottom), top, _height);
                CopyReadbackRegion(src, dst, left, top, right, bottom);
            }
        }
    }

    private unsafe void CopyReadbackRegion(
        byte* source,
        byte* destination,
        int left,
        int top,
        int right,
        int bottom)
    {
        for (var y = top; y < bottom; y++)
        {
            var sourceRow = source + y * _width * 4;
            var destinationRow = destination + y * _rowBytes;
            for (var x = left; x < right; x++)
            {
                CopyRgbaTexturePixelToSurface(
                    sourceRow,
                    destinationRow,
                    x,
                    _colorType,
                    _alphaType,
                    _gpuTexture!.AlphaMode);
            }
        }
    }

    private static int ResolveCpuSurfaceRowBytes(
        int width,
        int height,
        int rowBytes,
        SKColorType colorType,
        string parameterName)
    {
        int minimumRowBytes = checked(width * GetBytesPerPixel(colorType));
        int actualRowBytes = rowBytes > 0 ? rowBytes : minimumRowBytes;
        if (height > 0 && actualRowBytes < minimumRowBytes)
        {
            throw new ArgumentException("Row bytes must be large enough for one surface row.", parameterName);
        }

        return actualRowBytes;
    }

    private static int GetBytesPerPixel(SKColorType colorType)
    {
        return SKImageInfo.GetBytesPerPixel(colorType);
    }

    private static void ValidateImageInfoDimensions(SKImageInfo info, string parameterName)
    {
        if (info.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, info.Width, "SKImageInfo width must be positive.");
        }

        if (info.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, info.Height, "SKImageInfo height must be positive.");
        }
    }

    public unsafe SKImage Snapshot()
    {
        if (_gpuTexture == null)
        {
            throw new InvalidOperationException("No backing texture for snapshot.");
        }

        // Flush first to make sure current commands are rendered
        Flush();

        var snapshotTexture = new GpuTexture(
            _context,
            (uint)_width,
            (uint)_height,
            _gpuTexture.Format,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "SKSurface Snapshot Texture",
            alphaMode: _gpuTexture.AlphaMode
        );

        var wgpu = _context.Api;
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Surface Snapshot Encoder") };
        var encoder = wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var srcCopy = new ImageCopyTexture
        {
            Texture = _gpuTexture.TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
            Aspect = TextureAspect.All
        };

        var dstCopy = new ImageCopyTexture
        {
            Texture = snapshotTexture.TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
            Aspect = TextureAspect.All
        };

        var copySize = new Extent3D
        {
            Width = (uint)_width,
            Height = (uint)_height,
            DepthOrArrayLayers = 1
        };

        wgpu.CommandEncoderCopyTextureToTexture(encoder, &srcCopy, &dstCopy, &copySize);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Surface Snapshot Command Buffer") };
        var cmdBuffer = wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);
        wgpu.CommandBufferRelease(cmdBuffer);
        wgpu.CommandEncoderRelease(encoder);

        return SKImage.FromOwnedTexture(
            snapshotTexture,
            new SKImageInfo(_width, _height, _colorType, _alphaType, _colorSpace));
    }

    public void Draw(SKCanvas canvas, float x, float y, SKPaint? paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        using var image = Snapshot();
        var sampling = paint?.GetLegacyFilterQualitySampling() ?? SKSamplingOptions.Default;
        canvas.DrawImage(image, x, y, sampling, paint);
    }

    public void Draw(
        SKCanvas canvas,
        float x,
        float y,
        SKSamplingOptions sampling,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        using var image = Snapshot();
        canvas.DrawImage(image, x, y, sampling, paint);
    }

    public void Draw(
        SKCanvas canvas,
        SKPoint point,
        SKSamplingOptions sampling,
        SKPaint? paint = null) =>
        Draw(canvas, point.X, point.Y, sampling, paint);

    private static unsafe void CopyPixelToRgbaPremultiplied(byte* sourceRow, byte* destinationRow, int x, SKColorType colorType, SKAlphaType alphaType)
    {
        int destinationOffset = x * 4;
        byte red;
        byte green;
        byte blue;
        byte alpha;

        if (colorType == SKColorType.Rgb565)
        {
            int sourceOffset = x * 2;
            ushort pixel = (ushort)(sourceRow[sourceOffset] | (sourceRow[sourceOffset + 1] << 8));
            red = (byte)(((pixel >> 11) & 0x1f) * 255 / 31);
            green = (byte)(((pixel >> 5) & 0x3f) * 255 / 63);
            blue = (byte)((pixel & 0x1f) * 255 / 31);
            alpha = 255;
        }
        else if (colorType == SKColorType.Bgra8888)
        {
            int sourceOffset = x * 4;
            blue = sourceRow[sourceOffset];
            green = sourceRow[sourceOffset + 1];
            red = sourceRow[sourceOffset + 2];
            alpha = sourceRow[sourceOffset + 3];
        }
        else
        {
            int sourceOffset = x * 4;
            red = sourceRow[sourceOffset];
            green = sourceRow[sourceOffset + 1];
            blue = sourceRow[sourceOffset + 2];
            alpha = sourceRow[sourceOffset + 3];
        }

        if (alphaType == SKAlphaType.Opaque)
        {
            alpha = 255;
        }
        else if (alphaType == SKAlphaType.Unpremul)
        {
            red = PremultiplyChannel(red, alpha);
            green = PremultiplyChannel(green, alpha);
            blue = PremultiplyChannel(blue, alpha);
        }

        destinationRow[destinationOffset] = red;
        destinationRow[destinationOffset + 1] = green;
        destinationRow[destinationOffset + 2] = blue;
        destinationRow[destinationOffset + 3] = alpha;
    }

    private static unsafe void CopyRgbaTexturePixelToSurface(byte* sourceRow, byte* destinationRow, int x, SKColorType colorType, SKAlphaType alphaType, GpuTextureAlphaMode sourceAlphaMode)
    {
        int sourceOffset = x * 4;
        byte red = sourceRow[sourceOffset];
        byte green = sourceRow[sourceOffset + 1];
        byte blue = sourceRow[sourceOffset + 2];
        byte alpha = sourceRow[sourceOffset + 3];

        if (sourceAlphaMode == GpuTextureAlphaMode.Premultiplied &&
            (alphaType == SKAlphaType.Unpremul || alphaType == SKAlphaType.Opaque))
        {
            red = UnpremultiplyChannel(red, alpha);
            green = UnpremultiplyChannel(green, alpha);
            blue = UnpremultiplyChannel(blue, alpha);
        }
        else if (sourceAlphaMode == GpuTextureAlphaMode.Straight && alphaType == SKAlphaType.Premul)
        {
            red = PremultiplyChannel(red, alpha);
            green = PremultiplyChannel(green, alpha);
            blue = PremultiplyChannel(blue, alpha);
        }

        if (alphaType == SKAlphaType.Opaque)
        {
            alpha = 255;
        }

        if (colorType == SKColorType.Rgb565)
        {
            int destinationOffset = x * 2;
            ushort pixel = (ushort)(
                ((red * 31 + 127) / 255 << 11) |
                ((green * 63 + 127) / 255 << 5) |
                ((blue * 31 + 127) / 255));
            destinationRow[destinationOffset] = (byte)pixel;
            destinationRow[destinationOffset + 1] = (byte)(pixel >> 8);
        }
        else if (colorType == SKColorType.Bgra8888)
        {
            int destinationOffset = x * 4;
            destinationRow[destinationOffset] = blue;
            destinationRow[destinationOffset + 1] = green;
            destinationRow[destinationOffset + 2] = red;
            destinationRow[destinationOffset + 3] = alpha;
        }
        else
        {
            int destinationOffset = x * 4;
            destinationRow[destinationOffset] = red;
            destinationRow[destinationOffset + 1] = green;
            destinationRow[destinationOffset + 2] = blue;
            destinationRow[destinationOffset + 3] = alpha;
        }
    }

    private static byte PremultiplyChannel(byte value, byte alpha)
    {
        return (byte)((value * alpha + 127) / 255);
    }

    private static byte UnpremultiplyChannel(byte value, byte alpha)
    {
        if (alpha == 0)
        {
            return 0;
        }

        return (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pixels != IntPtr.Zero)
        {
            GpuFramebufferPresentationRegistry.Unregister(_pixels, this);
        }

        try
        {
            FlushCore(copyToCpu: true);
        }
        finally
        {
            try
            {
                Canvas.DetachSurface(this);
                Canvas.Dispose();
            }
            finally
            {
                try
                {
                    if (_ownsTexture)
                    {
                        _gpuTexture?.Dispose();
                    }
                }
                finally
                {
                    _readbackBuffer?.Dispose();
                    _readbackBuffer = null;
                    _readbackPixels = null;
                    if (!_context.IsDisposed)
                    {
                        _context.CleanupPendingResources();
                    }
                }
            }
        }
    }
}
