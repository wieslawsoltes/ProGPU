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
    private readonly SKAlphaType _alphaType;
    private readonly GRSurfaceOrigin _origin;
    private bool _hasTextureContents;

    private static readonly Dictionary<WgpuContext, Dictionary<TextureFormat, Compositor>> _compositorCache = new();

    static SKSurface()
    {
        WgpuContext.Disposing += RemoveCachedCompositor;
    }

    public SKCanvas Canvas { get; }

    private static Compositor GetCompositorForContext(WgpuContext context, TextureFormat renderFormat)
    {
        lock (_compositorCache)
        {
            if (!_compositorCache.TryGetValue(context, out var formatCompositors))
            {
                formatCompositors = new Dictionary<TextureFormat, Compositor>();
                _compositorCache[context] = formatCompositors;
            }

            if (!formatCompositors.TryGetValue(renderFormat, out var compositor))
            {
                compositor = new Compositor(context, renderFormat);
                formatCompositors[renderFormat] = compositor;
            }

            return compositor;
        }
    }

    private static void RemoveCachedCompositor(WgpuContext context)
    {
        Dictionary<TextureFormat, Compositor>? formatCompositors = null;
        lock (_compositorCache)
        {
            if (_compositorCache.TryGetValue(context, out formatCompositors))
            {
                _compositorCache.Remove(context);
            }
        }

        if (formatCompositors != null)
        {
            foreach (var compositor in formatCompositors.Values)
            {
                compositor.Dispose();
            }
        }
    }

    private SKSurface(WgpuContext context, int width, int height, GpuTexture? texture, bool ownsTexture, IntPtr pixels, int rowBytes, SKColorType colorType, SKAlphaType alphaType, GRSurfaceOrigin origin = GRSurfaceOrigin.TopLeft)
    {
        _context = context;
        _width = width;
        _height = height;
        _gpuTexture = texture;
        _ownsTexture = ownsTexture;
        _pixels = pixels;
        _rowBytes = pixels != IntPtr.Zero ? ResolveCpuSurfaceRowBytes(width, height, rowBytes, nameof(rowBytes)) : rowBytes;
        _colorType = colorType;
        _alphaType = alphaType;
        _origin = origin;

        _drawingContext = new DrawingContext();
        Canvas = new SKCanvas(_drawingContext, width, height, context);
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
            "SKSurface Backing Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied
        );
        return new SKSurface(ctx, info.Width, info.Height, texture, true, IntPtr.Zero, 0, info.ColorType, info.AlphaType);
    }

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, int rowBytes)
    {
        return Create(info, pixels, rowBytes, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, int rowBytes, SKSurfaceProperties properties)
    {
        int actualRowBytes = pixels != IntPtr.Zero ? ResolveCpuSurfaceRowBytes(info.Width, info.Height, rowBytes, nameof(rowBytes)) : rowBytes;
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
        return new SKSurface(ctx, info.Width, info.Height, texture, true, pixels, actualRowBytes, info.ColorType, info.AlphaType);
    }

    public static SKSurface Create(GRContext grContext, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType)
    {
        return Create(grContext, renderTarget, origin, colorType, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
    }

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

        return new SKSurface(grContext.Context, renderTarget.Width, renderTarget.Height, texture, false, IntPtr.Zero, 0, colorType, SKAlphaType.Premul, origin);
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
            "SKSurface Offscreen Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied
        );
        return new SKSurface(grContext.Context, imageInfo.Width, imageInfo.Height, texture, true, IntPtr.Zero, 0, imageInfo.ColorType, imageInfo.AlphaType);
    }

    public void Flush()
    {
        if (_gpuTexture == null) return;

        // Skip compiling if no commands have been recorded
        if (_drawingContext.Commands.Count == 0) return;

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
            compositor.RenderOffscreen(visual, (uint)_width, (uint)_height, _gpuTexture, 0f, 1f, null, _hasTextureContents);
        }
        finally
        {
            visual.Context.Clear();
        }

        _hasTextureContents = true;

        // If CPU-backed surface, read pixels back and copy to memory pointer
        if (_pixels != IntPtr.Zero)
        {
            byte[] readBackBytes = _gpuTexture.ReadPixels();
            
            unsafe
            {
                fixed (byte* src = readBackBytes)
                {
                    byte* dst = (byte*)_pixels;
                    for (int y = 0; y < _height; y++)
                    {
                        byte* srcRow = src + y * _width * 4;
                        byte* dstRow = dst + y * _rowBytes;
                        
                        for (int x = 0; x < _width; x++)
                        {
                            CopyRgbaTexturePixelToSurface(srcRow, dstRow, x, _colorType, _alphaType, _gpuTexture.AlphaMode);
                        }
                    }
                }
            }
        }

        // Clear recorded commands and dispose command-retained source textures.
        _drawingContext.Clear();
        Canvas.ReleaseLayerTexturesAfterFlush();
    }

    private static int ResolveCpuSurfaceRowBytes(int width, int height, int rowBytes, string parameterName)
    {
        int minimumRowBytes = checked(width * 4);
        int actualRowBytes = rowBytes > 0 ? rowBytes : minimumRowBytes;
        if (height > 0 && actualRowBytes < minimumRowBytes)
        {
            throw new ArgumentException("Row bytes must be large enough for one surface row.", parameterName);
        }

        return actualRowBytes;
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

        return SKImage.FromOwnedTexture(snapshotTexture);
    }

    private static unsafe void CopyPixelToRgbaPremultiplied(byte* sourceRow, byte* destinationRow, int x, SKColorType colorType, SKAlphaType alphaType)
    {
        int offset = x * 4;
        byte red;
        byte green;
        byte blue;
        byte alpha;

        if (colorType == SKColorType.Bgra8888)
        {
            blue = sourceRow[offset];
            green = sourceRow[offset + 1];
            red = sourceRow[offset + 2];
            alpha = sourceRow[offset + 3];
        }
        else
        {
            red = sourceRow[offset];
            green = sourceRow[offset + 1];
            blue = sourceRow[offset + 2];
            alpha = sourceRow[offset + 3];
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

        destinationRow[offset] = red;
        destinationRow[offset + 1] = green;
        destinationRow[offset + 2] = blue;
        destinationRow[offset + 3] = alpha;
    }

    private static unsafe void CopyRgbaTexturePixelToSurface(byte* sourceRow, byte* destinationRow, int x, SKColorType colorType, SKAlphaType alphaType, GpuTextureAlphaMode sourceAlphaMode)
    {
        int offset = x * 4;
        byte red = sourceRow[offset];
        byte green = sourceRow[offset + 1];
        byte blue = sourceRow[offset + 2];
        byte alpha = sourceRow[offset + 3];

        if (alphaType == SKAlphaType.Opaque)
        {
            alpha = 255;
        }
        else if (sourceAlphaMode == GpuTextureAlphaMode.Premultiplied && alphaType == SKAlphaType.Unpremul)
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

        if (colorType == SKColorType.Bgra8888)
        {
            destinationRow[offset] = blue;
            destinationRow[offset + 1] = green;
            destinationRow[offset + 2] = red;
            destinationRow[offset + 3] = alpha;
        }
        else
        {
            destinationRow[offset] = red;
            destinationRow[offset + 1] = green;
            destinationRow[offset + 2] = blue;
            destinationRow[offset + 3] = alpha;
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
        Flush();
        Canvas.Dispose();
        if (_ownsTexture)
        {
            _gpuTexture?.Dispose();
        }
    }
}
