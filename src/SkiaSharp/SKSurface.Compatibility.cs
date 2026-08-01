using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace SkiaSharp;

public delegate void SKSurfaceReleaseDelegate(IntPtr address, object context);

public partial class SKSurface
{
    private static readonly SKSurfaceProperties s_defaultSurfaceProperties =
        new(SKPixelGeometry.RgbHorizontal);

    public SKPixmap PeekPixels()
    {
        var pixmap = new SKPixmap();
        if (PeekPixels(pixmap))
        {
            return pixmap;
        }

        pixmap.Dispose();
        return null!;
    }

    public bool PeekPixels(SKPixmap pixmap)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        if (_isNullSurface || _gpuTexture == null)
        {
            pixmap.Reset();
            return false;
        }

        Flush();
        EnsureCpuPixels();
        pixmap.Reset(
            new SKImageInfo(_width, _height, _colorType, _alphaType, _colorSpace),
            _pixels,
            _rowBytes);
        pixmap.SetPixelSource(this);
        return true;
    }

    public bool ReadPixels(
        SKImageInfo dstInfo,
        IntPtr dstPixels,
        int dstRowBytes,
        int srcX,
        int srcY)
    {
        if (_gpuTexture == null || dstPixels == IntPtr.Zero || dstInfo.IsEmpty)
        {
            return false;
        }

        Flush();
        using var image = new SKImage(_gpuTexture);
        return image.ReadPixels(dstInfo, dstPixels, dstRowBytes, srcX, srcY);
    }

    public void Flush(bool submit, bool synchronous = false)
    {
        FlushCore(copyToCpu: true);
        if (submit && synchronous && _context is { IsDisposed: false })
        {
            _context.WaitIdle();
        }
    }

    public static SKSurface Create(SKImageInfo info, int rowBytes) =>
        Create(info, IntPtr.Zero, rowBytes);

    public static SKSurface Create(SKImageInfo info, int rowBytes, SKSurfaceProperties props) =>
        Create(info, IntPtr.Zero, rowBytes, props);

    public static SKSurface Create(SKImageInfo info, IntPtr pixels) =>
        Create(info, pixels, info.RowBytes);

    public static SKSurface Create(SKImageInfo info, IntPtr pixels, SKSurfaceProperties props) =>
        Create(info, pixels, info.RowBytes, props);

    public static SKSurface Create(
        SKImageInfo info,
        IntPtr pixels,
        int rowBytes,
        SKSurfaceReleaseDelegate releaseProc,
        object context,
        SKSurfaceProperties props)
    {
        var surface = Create(info, pixels, rowBytes, props);
        surface._releaseProc = releaseProc;
        surface._releaseContext = context;
        return surface;
    }

    public static SKSurface Create(
        SKImageInfo info,
        IntPtr pixels,
        int rowBytes,
        SKSurfaceReleaseDelegate releaseProc,
        object context) =>
        Create(
            info,
            pixels,
            rowBytes,
            releaseProc,
            context,
            CreateDefaultProperties());

    public static SKSurface Create(SKPixmap pixmap)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        return Create(pixmap.Info, pixmap.GetPixels(), pixmap.RowBytes);
    }

    public static SKSurface Create(SKPixmap pixmap, SKSurfaceProperties props)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        return Create(pixmap.Info, pixmap.GetPixels(), pixmap.RowBytes, props);
    }

    public static SKSurface CreateNull(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        return new SKSurface(
            null,
            width,
            height,
            null,
            false,
            IntPtr.Zero,
            0,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            props: CreateDefaultProperties(),
            isNullSurface: true);
    }

    public static SKSurface Create(
        GRContext context,
        GRBackendRenderTarget renderTarget,
        SKColorType colorType) =>
        Create(context, renderTarget, GRSurfaceOrigin.TopLeft, colorType);

    public static SKSurface Create(
        GRContext context,
        GRBackendRenderTarget renderTarget,
        SKColorType colorType,
        SKSurfaceProperties props) =>
        Create(context, renderTarget, GRSurfaceOrigin.TopLeft, colorType, props);

    public static SKSurface? Create(
        GRContext context,
        GRBackendTexture texture,
        SKColorType colorType,
        SKSurfaceProperties props) =>
        Create(context, texture, GRSurfaceOrigin.TopLeft, colorType, props);

    public static SKSurface? Create(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType) =>
        Create(context, texture, origin, sampleCount, colorType, null, CreateDefaultProperties());

    public static SKSurface? Create(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType,
        SKColorSpace colorspace) =>
        Create(context, texture, origin, sampleCount, colorType, colorspace, CreateDefaultProperties());

    public static SKSurface? Create(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType,
        SKSurfaceProperties props) =>
        Create(context, texture, origin, sampleCount, colorType, null, props);

    public static SKSurface? Create(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType,
        SKColorSpace? colorspace,
        SKSurfaceProperties props)
    {
        ValidateSampleCount(sampleCount);
        var surface = Create(context, texture, origin, colorType, props);
        if (surface != null && colorspace != null)
        {
            surface._colorSpace = colorspace;
        }

        return surface;
    }

    public static SKSurface Create(GRContext context, bool budgeted, SKImageInfo info) =>
        Create(context, budgeted, info, CreateDefaultProperties());

    public static SKSurface Create(
        GRContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount) =>
        Create(context, budgeted, info, sampleCount, CreateDefaultProperties());

    public static SKSurface Create(
        GRContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount,
        SKSurfaceProperties props) =>
        Create(context, budgeted, info, sampleCount, GRSurfaceOrigin.TopLeft, props, false);

    public static SKSurface Create(
        GRContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount,
        GRSurfaceOrigin origin) =>
        Create(context, budgeted, info, sampleCount, origin, CreateDefaultProperties(), false);

    public static SKSurface Create(
        GRContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount,
        GRSurfaceOrigin origin,
        SKSurfaceProperties props,
        bool shouldCreateWithMips) =>
        CreateOffscreen(context, budgeted, info, sampleCount, origin, props, shouldCreateWithMips);

    public static SKSurface Create(
        GRRecordingContext context,
        GRBackendRenderTarget renderTarget,
        GRSurfaceOrigin origin,
        SKColorType colorType) =>
        CreateRenderTarget(context, renderTarget, origin, colorType, null, CreateDefaultProperties());

    public static SKSurface Create(
        GRRecordingContext context,
        GRBackendRenderTarget renderTarget,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKColorSpace colorspace) =>
        CreateRenderTarget(context, renderTarget, origin, colorType, colorspace, CreateDefaultProperties());

    public static SKSurface Create(
        GRRecordingContext context,
        GRBackendRenderTarget renderTarget,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKSurfaceProperties props) =>
        CreateRenderTarget(context, renderTarget, origin, colorType, null, props);

    public static SKSurface Create(
        GRRecordingContext context,
        GRBackendRenderTarget renderTarget,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKColorSpace? colorspace,
        SKSurfaceProperties props) =>
        CreateRenderTarget(context, renderTarget, origin, colorType, colorspace, props);

    public static SKSurface Create(
        GRRecordingContext context,
        GRBackendRenderTarget renderTarget,
        SKColorType colorType) =>
        CreateRenderTarget(context, renderTarget, GRSurfaceOrigin.TopLeft, colorType, null, CreateDefaultProperties());

    public static SKSurface Create(
        GRRecordingContext context,
        GRBackendRenderTarget renderTarget,
        SKColorType colorType,
        SKSurfaceProperties props) =>
        CreateRenderTarget(context, renderTarget, GRSurfaceOrigin.TopLeft, colorType, null, props);

    public static SKSurface? Create(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType) =>
        CreateTexture(context, texture, origin, 1, colorType, null, CreateDefaultProperties());

    public static SKSurface? Create(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKSurfaceProperties props) =>
        CreateTexture(context, texture, origin, 1, colorType, null, props);

    public static SKSurface? Create(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType) =>
        CreateTexture(context, texture, origin, sampleCount, colorType, null, CreateDefaultProperties());

    public static SKSurface? Create(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType,
        SKColorSpace colorspace) =>
        CreateTexture(context, texture, origin, sampleCount, colorType, colorspace, CreateDefaultProperties());

    public static SKSurface? Create(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType,
        SKSurfaceProperties props) =>
        CreateTexture(context, texture, origin, sampleCount, colorType, null, props);

    public static SKSurface? Create(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType,
        SKColorSpace? colorspace,
        SKSurfaceProperties props) =>
        CreateTexture(context, texture, origin, sampleCount, colorType, colorspace, props);

    public static SKSurface? Create(
        GRRecordingContext context,
        GRBackendTexture texture,
        SKColorType colorType) =>
        CreateTexture(context, texture, GRSurfaceOrigin.TopLeft, 1, colorType, null, CreateDefaultProperties());

    public static SKSurface? Create(
        GRRecordingContext context,
        GRBackendTexture texture,
        SKColorType colorType,
        SKSurfaceProperties props) =>
        CreateTexture(context, texture, GRSurfaceOrigin.TopLeft, 1, colorType, null, props);

    public static SKSurface Create(GRRecordingContext context, bool budgeted, SKImageInfo info) =>
        CreateOffscreen(context, budgeted, info, 1, GRSurfaceOrigin.TopLeft, CreateDefaultProperties(), false);

    public static SKSurface Create(
        GRRecordingContext context,
        bool budgeted,
        SKImageInfo info,
        SKSurfaceProperties props) =>
        CreateOffscreen(context, budgeted, info, 1, GRSurfaceOrigin.TopLeft, props, false);

    public static SKSurface Create(
        GRRecordingContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount) =>
        CreateOffscreen(context, budgeted, info, sampleCount, GRSurfaceOrigin.TopLeft, CreateDefaultProperties(), false);

    public static SKSurface Create(
        GRRecordingContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount,
        SKSurfaceProperties props) =>
        CreateOffscreen(context, budgeted, info, sampleCount, GRSurfaceOrigin.TopLeft, props, false);

    public static SKSurface Create(
        GRRecordingContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount,
        GRSurfaceOrigin origin) =>
        CreateOffscreen(context, budgeted, info, sampleCount, origin, CreateDefaultProperties(), false);

    public static SKSurface Create(
        GRRecordingContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount,
        GRSurfaceOrigin origin,
        SKSurfaceProperties props,
        bool shouldCreateWithMips) =>
        CreateOffscreen(context, budgeted, info, sampleCount, origin, props, shouldCreateWithMips);

    private static SKSurface CreateRenderTarget(
        GRRecordingContext context,
        GRBackendRenderTarget renderTarget,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKColorSpace? colorspace,
        SKSurfaceProperties props)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(renderTarget);
        ArgumentNullException.ThrowIfNull(props);
        var texture = renderTarget.BackendTexture
            ?? throw new NotSupportedException("Only ProGPU WebGPU render targets can be wrapped.");
        ValidateTexture(context, texture, requireTextureBinding: false);
        ValidateSampleCount(renderTarget.SampleCount);
        return new SKSurface(
            context.BackendContext,
            renderTarget.Width,
            renderTarget.Height,
            texture,
            false,
            IntPtr.Zero,
            0,
            colorType,
            SKAlphaType.Premul,
            colorspace,
            origin,
            context,
            props);
    }

    private static SKSurface? CreateTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        int sampleCount,
        SKColorType colorType,
        SKColorSpace? colorspace,
        SKSurfaceProperties props)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(props);
        ValidateSampleCount(sampleCount);
        var backendTexture = texture.BackendTexture;
        if (backendTexture == null)
        {
            return null;
        }

        ValidateTexture(context, backendTexture, requireTextureBinding: false);
        return new SKSurface(
            context.BackendContext,
            texture.Width,
            texture.Height,
            backendTexture,
            false,
            IntPtr.Zero,
            0,
            colorType,
            SKAlphaType.Premul,
            colorspace,
            origin,
            context,
            props);
    }

    private static SKSurface CreateOffscreen(
        GRRecordingContext context,
        bool budgeted,
        SKImageInfo info,
        int sampleCount,
        GRSurfaceOrigin origin,
        SKSurfaceProperties props,
        bool shouldCreateWithMips)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(props);
        ValidateImageInfoDimensions(info, nameof(info));
        ValidateSampleCount(sampleCount);
        var mipLevelCount = shouldCreateWithMips
            ? CalculateMipLevelCount((uint)info.Width, (uint)info.Height)
            : 1u;
        var texture = new GpuTexture(
            context.BackendContext,
            (uint)info.Width,
            (uint)info.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            budgeted ? "SKSurface Budgeted Offscreen Texture" : "SKSurface Unbudgeted Offscreen Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied,
            mipLevelCount: mipLevelCount);
        return new SKSurface(
            context.BackendContext,
            info.Width,
            info.Height,
            texture,
            true,
            IntPtr.Zero,
            0,
            info.ColorType,
            info.AlphaType,
            info.ColorSpace,
            origin,
            context,
            props);
    }

    private static void ValidateTexture(
        GRRecordingContext context,
        GpuTexture texture,
        bool requireTextureBinding)
    {
        if (!ReferenceEquals(texture.Context, context.BackendContext))
        {
            throw new InvalidOperationException("The backend texture belongs to a different ProGPU context.");
        }

        var required = TextureUsage.RenderAttachment | TextureUsage.CopySrc;
        if (requireTextureBinding)
        {
            required |= TextureUsage.TextureBinding;
        }

        if ((texture.Usage & required) != required)
        {
            throw new InvalidOperationException("The backend texture is missing required WebGPU usages.");
        }

        ValidateSampleCount((int)texture.SampleCount);
    }

    private static void ValidateSampleCount(int sampleCount)
    {
        if (sampleCount is not 0 and not 1)
        {
            throw new NotSupportedException("ProGPU WebGPU surfaces currently support a single sample.");
        }
    }

    private static uint CalculateMipLevelCount(uint width, uint height)
    {
        var levels = 1u;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1u, width >> 1);
            height = Math.Max(1u, height >> 1);
            levels++;
        }

        return levels;
    }

    private static SKSurfaceProperties CreateDefaultProperties() =>
        s_defaultSurfaceProperties;

    private void EnsureCpuPixels()
    {
        if (_pixels != IntPtr.Zero)
        {
            return;
        }

        _rowBytes = checked(_width * GetBytesPerPixel(_colorType));
        _pixels = Marshal.AllocHGlobal(checked(_rowBytes * _height));
        _ownsCpuPixels = true;
        using var image = new SKImage(_gpuTexture!);
        if (image.ReadPixels(
                new SKImageInfo(_width, _height, _colorType, _alphaType, _colorSpace),
                _pixels,
                _rowBytes))
        {
            return;
        }

        Marshal.FreeHGlobal(_pixels);
        _pixels = IntPtr.Zero;
        _rowBytes = 0;
        _ownsCpuPixels = false;
        throw new InvalidOperationException("The WebGPU surface could not be exposed as CPU pixels.");
    }
}
