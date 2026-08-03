using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;

namespace SkiaSharp;

public partial class SKSurface : SKObject, IGpuFramebufferPresenter
{
    private sealed class SurfaceRenderVisual : Visual, IOwnedRenderCommandCache
    {
        private readonly DrawingContext _commands;

        public SurfaceRenderVisual(DrawingContext commands)
        {
            _commands = commands;
        }

        public bool HasRenderCommands => _commands.Commands.Count != 0;

        public DrawingContext GetOrUpdateRenderCommandCache() => _commands;
    }

    private static readonly object s_compositorCacheScope = new();
    private readonly WgpuContext? _context;
    private readonly DrawingContext _drawingContext;
    private readonly SurfaceRenderVisual _renderVisual;
    private GpuTexture? _gpuTexture;
    private IntPtr _pixels;
    private int _rowBytes;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _ownsTexture;
    private readonly SKColorType _colorType;
    private readonly SKAlphaType _alphaType;
    private SKColorSpace? _colorSpace;
    private readonly GRSurfaceOrigin _origin;
    private readonly GRRecordingContext? _recordingContext;
    private SKSurfaceProperties _surfaceProperties;
    private SKSurfaceReleaseDelegate? _releaseProc;
    private object? _releaseContext;
    private readonly bool _isNullSurface;
    private GpuTextureReadbackBuffer? _readbackBuffer;
    private byte[]? _readbackPixels;
    private bool _hasTextureContents;
    private bool _surfaceOwnsCurrentTexture;
    private bool _ownsCpuPixels;
    private int _releaseInvoked;
    private SKImage? _immutableSnapshotGeneration;
    private readonly Action<int> _invalidateSnapshotGeneration;

    public SKCanvas Canvas { get; }

    public GRRecordingContext Context => _recordingContext!;

    public SKSurfaceProperties SurfaceProperties => _surfaceProperties;

    private static Compositor GetCompositorForContext(WgpuContext context, TextureFormat renderFormat)
    {
        return SharedCompositorCache.GetOrCreate(context, renderFormat, s_compositorCacheScope);
    }

    private static void RemoveCachedCompositor(WgpuContext context)
    {
        SharedCompositorCache.Remove(context, s_compositorCacheScope);
    }

    private SKSurface(
        WgpuContext? context,
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
        GRRecordingContext? recordingContext = null,
        SKSurfaceProperties? props = null,
        SKSurfaceReleaseDelegate? releaseProc = null,
        object? releaseContext = null,
        bool isNullSurface = false)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _context = context;
        _width = width;
        _height = height;
        _gpuTexture = texture;
        _ownsTexture = ownsTexture;
        _surfaceOwnsCurrentTexture = ownsTexture;
        _pixels = pixels;
        _rowBytes = pixels != IntPtr.Zero
            ? ResolveCpuSurfaceRowBytes(width, height, rowBytes, colorType, nameof(rowBytes))
            : rowBytes;
        _colorType = colorType;
        _alphaType = alphaType;
        _colorSpace = colorSpace;
        _origin = origin;
        _recordingContext = recordingContext;
        _surfaceProperties = new SKSurfaceProperties(
            props?.Flags ?? SKSurfacePropsFlags.None,
            props?.PixelGeometry ?? SKPixelGeometry.RgbHorizontal);
        _releaseProc = releaseProc;
        _releaseContext = releaseContext;
        _isNullSurface = isNullSurface;

        _drawingContext = new DrawingContext();
        _renderVisual = new SurfaceRenderVisual(_drawingContext)
        {
            Size = new Vector2(width, height)
        };
        if (_origin == GRSurfaceOrigin.BottomLeft)
        {
            _renderVisual.Transform =
                Matrix4x4.CreateScale(1f, -1f, 1f) *
                Matrix4x4.CreateTranslation(0f, height, 0f);
        }
        _invalidateSnapshotGeneration = OnSurfaceCommandAdded;
        _drawingContext.SubscribeCommandAdded(_invalidateSnapshotGeneration);
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
        return Create(info, CreateDefaultProperties());
    }

    public static SKSurface Create(SKImageInfo info, SKSurfaceProperties props)
    {
        ValidateImageInfoDimensions(info, nameof(info));
        ArgumentNullException.ThrowIfNull(props);

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
        return new SKSurface(ctx, info.Width, info.Height, texture, true, IntPtr.Zero, 0, info.ColorType, info.AlphaType, info.ColorSpace, props: props);
    }

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, int rowBytes)
    {
        return Create(info, pixels, rowBytes, CreateDefaultProperties());
    }

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, int rowBytes, SKSurfaceProperties props)
    {
        ValidateImageInfoDimensions(info, nameof(info));
        ArgumentNullException.ThrowIfNull(props);

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
        return new SKSurface(ctx, info.Width, info.Height, texture, true, pixels, actualRowBytes, info.ColorType, info.AlphaType, info.ColorSpace, props: props);
    }

    public static SKSurface Create(GRContext context, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType)
    {
        return Create(context, renderTarget, origin, colorType, CreateDefaultProperties());
    }

    public static SKSurface? Create(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(texture);
        var gpuTexture = texture.BackendTexture;
        if (gpuTexture == null)
        {
            return null;
        }

        if (!ReferenceEquals(gpuTexture.Context, context.Context))
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
            context.Context,
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
            context);
    }

    public static SKSurface? Create(
        GRContext context,
        GRBackendTexture texture,
        SKColorType colorType) =>
        Create(context, texture, GRSurfaceOrigin.TopLeft, colorType);

    public static SKSurface? Create(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKSurfaceProperties props)
    {
        ArgumentNullException.ThrowIfNull(props);
        var surface = Create(context, texture, origin, colorType);
        if (surface == null)
        {
            return null;
        }

        surface._surfaceProperties.Dispose();
        surface._surfaceProperties = new SKSurfaceProperties(props.Flags, props.PixelGeometry);
        return surface;
    }

    public static SKSurface Create(GRContext context, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType, SKSurfaceProperties props)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(renderTarget);
        ArgumentNullException.ThrowIfNull(props);

        var texture = renderTarget.BackendTexture
            ?? throw new NotSupportedException("This WebGPU-backed Skia shim can only wrap ProGPU GpuTexture render targets. GL, Vulkan, and Metal backend handles cannot be rendered through this context.");

        if (!ReferenceEquals(texture.Context, context.Context))
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
            context.Context,
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
            context,
            props: props);
    }

    public static SKSurface Create(GRContext context, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType, SKColorSpace colorspace)
    {
        ArgumentNullException.ThrowIfNull(colorspace);
        return Create(context, renderTarget, origin, colorType, colorspace, CreateDefaultProperties());
    }

    public static SKSurface Create(GRContext context, GRBackendRenderTarget renderTarget, GRSurfaceOrigin origin, SKColorType colorType, SKColorSpace colorspace, SKSurfaceProperties props)
    {
        ArgumentNullException.ThrowIfNull(colorspace);
        var surface = Create(context, renderTarget, origin, colorType, props);
        surface._colorSpace = colorspace;
        return surface;
    }

    public static SKSurface Create(GRContext context, bool budgeted, SKImageInfo info, SKSurfaceProperties props)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(props);
        ValidateImageInfoDimensions(info, nameof(info));

        var texture = new GpuTexture(
            context.Context,
            (uint)info.Width,
            (uint)info.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface Offscreen Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied
        );
        return new SKSurface(
            context.Context,
            info.Width,
            info.Height,
            texture,
            true,
            IntPtr.Zero,
            0,
            info.ColorType,
            info.AlphaType,
            info.ColorSpace,
            recordingContext: context,
            props: props);
    }

    public void Flush()
    {
        FlushCore(copyToCpu: true);
    }

    private void FlushCore(bool copyToCpu)
    {
        if (_gpuTexture == null)
        {
            _drawingContext.Clear();
            Canvas.ReleaseLayerTexturesAfterFlush();
            return;
        }

        // Skip compiling if no commands have been recorded
        if (_drawingContext.Commands.Count == 0) return;

        var cpuReadbackRegions = Canvas.TakeCpuReadbackRegions();

        var compositor = GetCompositorForContext(_context!, _gpuTexture.Format);
        try
        {
            compositor.RenderOffscreen(
                _renderVisual,
                (uint)_width,
                (uint)_height,
                _gpuTexture,
                0f,
                1f,
                null,
                _hasTextureContents);

            _hasTextureContents = true;

            // If CPU-backed surface, read pixels back and copy to memory pointer
            if (copyToCpu && _pixels != IntPtr.Zero)
            {
                CopyReadbackToCpu(
                    ReadBackingTexturePixels(),
                    cpuReadbackRegions);
            }
        }
        finally
        {
            // Clear recorded commands and dispose command-retained source/save-layer textures.
            _drawingContext.Clear();
            Canvas.ReleaseLayerTexturesAfterFlush();
        }
    }

    private byte[] ReadBackingTexturePixels()
    {
        var readbackByteCount = checked(_width * _height * 4);
        if (_readbackPixels is null ||
            _readbackPixels.Length != readbackByteCount)
        {
            _readbackPixels =
                GC.AllocateUninitializedArray<byte>(readbackByteCount);
        }

        _readbackBuffer ??= new GpuTextureReadbackBuffer(_context!);
        try
        {
            _gpuTexture!.ReadPixels(
                _readbackPixels,
                _readbackBuffer);
        }
        finally
        {
            _context!.CleanupPendingResources();
        }

        return _readbackPixels;
    }

    private SKImageInfo CreateBackingTextureReadbackInfo()
    {
        var colorType = _gpuTexture!.Format is
            TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb
                ? SKColorType.Bgra8888
                : SKColorType.Rgba8888;
        var alphaType = _gpuTexture.AlphaMode ==
            GpuTextureAlphaMode.Straight
                ? SKAlphaType.Unpremul
                : SKAlphaType.Premul;
        return new SKImageInfo(
            _width,
            _height,
            colorType,
            alphaType,
            _colorSpace);
    }

    void IGpuFramebufferPresenter.Present(WgpuContext context, IntPtr surfaceHandle)
    {
        if (IsDisposed || _gpuTexture is null || _gpuTexture.IsDisposed)
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

    public SKImage Snapshot() => Snapshot(new SKRectI(0, 0, _width, _height));

    public SKImage Snapshot(SKRectI bounds)
    {
        if (_gpuTexture == null)
        {
            throw new InvalidOperationException("No backing texture for snapshot.");
        }

        if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > _width || bounds.Bottom > _height ||
            bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Snapshot bounds must be non-empty and contained by the surface.");
        }

        // Flush first to make sure current commands are rendered
        Flush();

        if (_immutableSnapshotGeneration is null || !_ownsTexture)
        {
            // A CPU-backed surface has already completed the required GPU
            // readback during Flush. Transfer that immutable generation to the
            // snapshot so ReadPixels and cross-context materialization do not
            // map the same frame a second time. The surface allocates its next
            // readback generation only after a subsequent drawing mutation.
            var portableRgbaPixels = _pixels != IntPtr.Zero
                ? _readbackPixels
                : null;
            SKImage generation;
            if (_ownsTexture)
            {
                // The immutable generation temporarily owns the surface backing.
                // A subsequent write either reclaims it in O(1) when no snapshot
                // lease remains, or performs one copy-on-write when a consumer
                // still retains the old pixels.
                generation = SKImage.FromOwnedTexture(
                    _gpuTexture,
                    new SKImageInfo(_width, _height, _colorType, _alphaType, _colorSpace),
                    portableRgbaPixels);
                _surfaceOwnsCurrentTexture = false;
            }
            else
            {
                var snapshotTexture = new GpuTexture(
                    _context!,
                    (uint)_width,
                    (uint)_height,
                    _gpuTexture.Format,
                    TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
                    "SKSurface Immutable Snapshot Generation",
                    alphaMode: _gpuTexture.AlphaMode);
                try
                {
                    snapshotTexture.CopyBaseLevelRegionFrom(
                        _gpuTexture,
                        0,
                        0,
                        0,
                        0,
                        (uint)_width,
                        (uint)_height);
                    generation = SKImage.FromOwnedTexture(
                        snapshotTexture,
                        new SKImageInfo(_width, _height, _colorType, _alphaType, _colorSpace),
                        portableRgbaPixels);
                }
                catch
                {
                    snapshotTexture.Dispose();
                    throw;
                }
            }

            if (portableRgbaPixels is not null)
            {
                _readbackPixels = null;
            }
            if (!_ownsTexture)
            {
                var view = generation.CreateSharedTextureView(bounds);
                generation.Dispose();
                return view;
            }

            _immutableSnapshotGeneration = generation;
        }

        return _immutableSnapshotGeneration.CreateSharedTextureView(bounds);
    }

    private void OnSurfaceCommandAdded(int commandIndex)
    {
        _renderVisual.Invalidate();
        var immutableGeneration = _immutableSnapshotGeneration;
        if (immutableGeneration is null)
        {
            return;
        }

        if (_ownsTexture &&
            immutableGeneration.TryRelinquishSoleTextureOwnership())
        {
            _surfaceOwnsCurrentTexture = true;
            immutableGeneration.Dispose();
            _immutableSnapshotGeneration = null;
            return;
        }

        if (_ownsTexture)
        {
            var previousTexture = _gpuTexture!;
            var writableTexture = new GpuTexture(
                _context!,
                (uint)_width,
                (uint)_height,
                previousTexture.Format,
                TextureUsage.RenderAttachment | TextureUsage.CopySrc |
                TextureUsage.CopyDst | TextureUsage.TextureBinding,
                "SKSurface Copy-on-write Backing Texture",
                alphaMode: previousTexture.AlphaMode);
            try
            {
                writableTexture.CopyBaseLevelRegionFrom(
                    previousTexture,
                    0,
                    0,
                    0,
                    0,
                    (uint)_width,
                    (uint)_height);
            }
            catch
            {
                writableTexture.Dispose();
                throw;
            }

            _gpuTexture = writableTexture;
            _surfaceOwnsCurrentTexture = true;
        }

        immutableGeneration.Dispose();
        _immutableSnapshotGeneration = null;
    }

    public void Draw(SKCanvas canvas, float x, float y, SKPaint? paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (_isNullSurface)
        {
            return;
        }

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
        if (_isNullSurface)
        {
            return;
        }

        using var image = Snapshot();
        canvas.DrawImage(image, x, y, sampling, paint);
    }

    public void Draw(
        SKCanvas canvas,
        SKPoint p,
        SKSamplingOptions sampling,
        SKPaint? paint = null) =>
        Draw(canvas, p.X, p.Y, sampling, paint);

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

    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    protected override void DisposeManaged()
    {
        _drawingContext.UnsubscribeCommandAdded(_invalidateSnapshotGeneration);
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
                    if (_surfaceOwnsCurrentTexture)
                    {
                        _gpuTexture?.Dispose();
                    }

                    _immutableSnapshotGeneration?.Dispose();
                    _immutableSnapshotGeneration = null;
                }
                finally
                {
                    _readbackBuffer?.Dispose();
                    _readbackBuffer = null;
                    _readbackPixels = null;
                    if (_context is { IsDisposed: false })
                    {
                        _context.CleanupPendingResources();
                        // Surface disposal is a lifecycle boundary. Poll once
                        // after dropping its final native references so already
                        // completed command/resource generations are retired
                        // without turning target teardown into a blocking queue
                        // drain.
                        _context.PollDevice(wait: false);
                    }

                    _surfaceProperties.Dispose();
                    if (_releaseProc != null && Interlocked.Exchange(ref _releaseInvoked, 1) == 0)
                    {
                        _releaseProc(_pixels, _releaseContext!);
                    }

                    if (_ownsCpuPixels && _pixels != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_pixels);
                        _pixels = IntPtr.Zero;
                        _ownsCpuPixels = false;
                    }
                }
            }
        }
    }
}
