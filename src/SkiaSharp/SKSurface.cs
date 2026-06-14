using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace SkiaSharp;

public class SKSurface : IDisposable
{
    private readonly WgpuContext _context;
    private readonly DrawingContext _drawingContext;
    private readonly GpuTexture? _gpuTexture;
    private readonly IntPtr _pixels;
    private readonly int _rowBytes;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _ownsTexture;
    private readonly SKColorType _colorType;

    private static readonly Dictionary<WgpuContext, Compositor> _compositorCache = new();

    public SKCanvas Canvas { get; }

    private static Compositor GetCompositorForContext(WgpuContext context)
    {
        lock (_compositorCache)
        {
            if (!_compositorCache.TryGetValue(context, out var compositor))
            {
                compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
                _compositorCache[context] = compositor;
            }
            return compositor;
        }
    }

    private SKSurface(WgpuContext context, int width, int height, GpuTexture? texture, bool ownsTexture, IntPtr pixels, int rowBytes, SKColorType colorType)
    {
        _context = context;
        _width = width;
        _height = height;
        _gpuTexture = texture;
        _ownsTexture = ownsTexture;
        _pixels = pixels;
        _rowBytes = rowBytes;
        _colorType = colorType;

        _drawingContext = new DrawingContext();
        Canvas = new SKCanvas(_drawingContext, width, height);

        if (_pixels != IntPtr.Zero && _gpuTexture != null)
        {
            int actualRowBytes = _rowBytes > 0 ? _rowBytes : _width * 4;
            byte[] temp = new byte[_width * _height * 4];
            unsafe
            {
                byte* src = (byte*)_pixels;
                fixed (byte* dst = temp)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        byte* srcRow = src + y * actualRowBytes;
                        byte* dstRow = dst + y * _width * 4;
                        
                        if (_colorType == SKColorType.Bgra8888)
                        {
                            for (int x = 0; x < _width; x++)
                            {
                                int srcIdx = x * 4;
                                int dstIdx = x * 4;
                                dstRow[dstIdx] = srcRow[srcIdx + 2];     // R (from BGRA B)
                                dstRow[dstIdx + 1] = srcRow[srcIdx + 1]; // G (from BGRA G)
                                dstRow[dstIdx + 2] = srcRow[srcIdx];     // B (from BGRA R)
                                dstRow[dstIdx + 3] = srcRow[srcIdx + 3]; // A (from BGRA A)
                            }
                        }
                        else
                        {
                            System.Buffer.MemoryCopy(srcRow, dstRow, _width * 4, _width * 4);
                        }
                    }
                }
            }
            _gpuTexture.WritePixels<byte>(temp);
        }
    }

    public static SKSurface Create(SKImageInfo info)
    {
        return Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

    public static SKSurface Create(SKImageInfo info, SKSurfaceProperties properties)
    {
        var ctx = SKContextHelper.GetContext();
        var texture = new GpuTexture(
            ctx,
            (uint)info.Width,
            (uint)info.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface Backing Texture"
        );
        return new SKSurface(ctx, info.Width, info.Height, texture, true, IntPtr.Zero, 0, info.ColorType);
    }

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, int rowBytes)
    {
        return Create(info, pixels, rowBytes, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, int rowBytes, SKSurfaceProperties properties)
    {
        var ctx = SKContextHelper.GetContext();
        var texture = new GpuTexture(
            ctx,
            (uint)info.Width,
            (uint)info.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface CPU-backed Backing Texture"
        );
        return new SKSurface(ctx, info.Width, info.Height, texture, true, pixels, rowBytes, info.ColorType);
    }

    public static SKSurface Create(GRContext grContext, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType)
    {
        return Create(grContext, renderTarget, origin, colorType, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

    public static SKSurface Create(GRContext grContext, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType, SKSurfaceProperties properties)
    {
        var texture = new GpuTexture(
            grContext.Context,
            (uint)renderTarget.Width,
            (uint)renderTarget.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface GPU-backed Texture"
        );
        return new SKSurface(grContext.Context, renderTarget.Width, renderTarget.Height, texture, true, IntPtr.Zero, 0, colorType);
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
        var texture = new GpuTexture(
            grContext.Context,
            (uint)imageInfo.Width,
            (uint)imageInfo.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface Offscreen Texture"
        );
        return new SKSurface(grContext.Context, imageInfo.Width, imageInfo.Height, texture, true, IntPtr.Zero, 0, imageInfo.ColorType);
    }

    public void Flush()
    {
        if (_gpuTexture == null) return;

        // Skip compiling if no commands have been recorded
        if (_drawingContext.Commands.Count == 0) return;

        var visual = new DrawingVisual();
        visual.Size = new Vector2(_width, _height);
        visual.Context.Append(_drawingContext);

        var compositor = GetCompositorForContext(_context);
        bool loadExisting = _pixels != IntPtr.Zero;
        compositor.RenderOffscreen(visual, (uint)_width, (uint)_height, _gpuTexture, 0f, 1f, null, loadExisting);

        // If CPU-backed surface, read pixels back and copy to memory pointer
        if (_pixels != IntPtr.Zero)
        {
            byte[] readBackBytes = _gpuTexture.ReadPixels();
            int actualRowBytes = _rowBytes > 0 ? _rowBytes : _width * 4;
            
            unsafe
            {
                fixed (byte* src = readBackBytes)
                {
                    byte* dst = (byte*)_pixels;
                    for (int y = 0; y < _height; y++)
                    {
                        byte* srcRow = src + y * _width * 4;
                        byte* dstRow = dst + y * actualRowBytes;
                        
                        if (_colorType == SKColorType.Bgra8888)
                        {
                            for (int x = 0; x < _width; x++)
                            {
                                int srcIdx = x * 4;
                                int dstIdx = x * 4;
                                dstRow[dstIdx] = srcRow[srcIdx + 2];     // B (source R)
                                dstRow[dstIdx + 1] = srcRow[srcIdx + 1]; // G (source G)
                                dstRow[dstIdx + 2] = srcRow[srcIdx];     // R (source B)
                                dstRow[dstIdx + 3] = srcRow[srcIdx + 3]; // A (source A)
                            }
                        }
                        else
                        {
                            System.Buffer.MemoryCopy(srcRow, dstRow, _width * 4, _width * 4);
                        }
                    }
                }
            }
        }

        // Clear recorded commands so we don't redraw them next flush
        _drawingContext.Commands.Clear();
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
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "SKSurface Snapshot Texture"
        );

        var wgpu = _context.Wgpu;
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

        return SKImage.FromTexture(snapshotTexture);
    }

    public void Dispose()
    {
        Flush();
        Canvas.Dispose();
        if (_ownsTexture)
        {
            _gpuTexture?.Dispose();
        }
    }
}
