using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;

namespace ProGPU.Linux.Media;

/// <summary>
/// Retained Linux NV12 DMA-BUF to RGBA thumbnail renderer. Decode surfaces
/// remain GPU-resident through import, scaling, color conversion, and effects;
/// one reusable WebGPU staging buffer performs the required final PNG-boundary
/// readback.
/// </summary>
/// <remarks>
/// Rendering and readback are O(P) for P output pixels. Native state is one
/// RGBA target, one aligned readback buffer, and two transient imported plane
/// views. Managed pixel storage is one tightly packed RGBA result. The final
/// WebGPU map means this type does not provide a zero-copy encoded result.
/// </remarks>
internal sealed class
    LinuxWebGpuCompositionThumbnailRenderer :
        IDisposable
{
    private readonly WgpuContext _context;
    private readonly GpuTexture _target;
    private readonly GpuTextureReadbackBuffer _readback;
    private readonly uint _width;
    private readonly uint _height;
    private int _disposed;

    private LinuxWebGpuCompositionThumbnailRenderer(
        WgpuContext context,
        uint width,
        uint height)
    {
        _context = context;
        _width = width;
        _height = height;
        GpuTexture target =
            new GpuTexture(
                context,
                width,
                height,
                TextureFormat.Rgba8Unorm,
                TextureUsage.RenderAttachment |
                TextureUsage.CopySrc,
                "Linux Media Composition Thumbnail RGBA");
        try
        {
            var readback =
                new GpuTextureReadbackBuffer(
                    context);
            try
            {
                readback.EnsureCapacity(
                    width,
                    height,
                    bytesPerPixel: 4);
                _target = target;
                _readback = readback;
            }
            catch
            {
                readback.Dispose();
                throw;
            }
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    internal static bool TryCreate(
        DawnGpuContext dawn,
        uint width,
        uint height,
        out LinuxWebGpuCompositionThumbnailRenderer
            renderer)
    {
        ArgumentNullException.ThrowIfNull(dawn);
        renderer = null!;
        if (!OperatingSystem.IsLinux() ||
            width == 0 ||
            height == 0 ||
            dawn.Context.AdapterBackendType !=
                BackendType.Vulkan ||
            !ReferenceEquals(
                dawn.Context.ExternalTextureImporter,
                dawn))
        {
            return false;
        }

        try
        {
            renderer =
                new LinuxWebGpuCompositionThumbnailRenderer(
                    dawn.Context,
                    width,
                    height);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Consumes ownership of the decoded frame and returns tightly packed
    /// RGBA pixels after the fused WebGPU pass.
    /// </summary>
    internal byte[] RenderFrame(
        in V4l2DecodedFrame frame,
        float saturation,
        float grayscale)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (frame.PixelFormat !=
                V4l2DecodedPixelFormat.Nv12 ||
            !frame.TryCreatePlanarExternalDescriptors(
                out ProGpuExternalTextureDescriptor
                    lumaDescriptor,
                out ProGpuExternalTextureDescriptor
                    chromaDescriptor))
        {
            frame.Owner.Dispose();
            throw new NotSupportedException(
                "Linux WebGPU thumbnails require NV12 DMA-BUF decoder output.");
        }

        GpuTexture? luma = null;
        GpuTexture? chroma = null;
        var owner = new SharedOwnerRoot(frame.Owner);
        SharedOwnerLease? lumaOwner =
            owner.CreateLease();
        SharedOwnerLease? chromaOwner =
            owner.CreateLease();
        try
        {
            if (!_context.TryImportExternalTexture(
                    in lumaDescriptor,
                    lumaOwner,
                    out luma))
            {
                throw new NotSupportedException(
                    "Dawn could not import the decoded NV12 luma DMA-BUF.");
            }
            lumaOwner = null;
            if (!_context.TryImportExternalTexture(
                    in chromaDescriptor,
                    chromaOwner,
                    out chroma))
            {
                throw new NotSupportedException(
                    "Dawn could not import the decoded NV12 chroma DMA-BUF.");
            }
            chromaOwner = null;

            GpuNv12Processor.ProcessToRgba(
                luma,
                chroma,
                _target,
                saturation,
                grayscale,
                inFlightSlot: 0);
            byte[] pixels =
                GC.AllocateUninitializedArray<byte>(
                    checked(
                        (int)_width *
                        (int)_height *
                        4));
            _target.ReadPixels(
                pixels,
                _readback);
            return pixels;
        }
        finally
        {
            luma?.Dispose();
            chroma?.Dispose();
            lumaOwner?.Dispose();
            chromaOwner?.Dispose();
            owner.Dispose();
        }
    }

    internal byte[] RenderColor(
        uint argbColor,
        float saturation,
        float grayscale)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Color color =
            ApplyEffects(
                argbColor,
                saturation,
                grayscale);
        GpuTextureClearer.Clear(
            _target,
            color);
        byte[] pixels =
            GC.AllocateUninitializedArray<byte>(
                checked(
                    (int)_width *
                    (int)_height *
                    4));
        _target.ReadPixels(
            pixels,
            _readback);
        return pixels;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }
        _readback.Dispose();
        _target.Dispose();
        _context.CleanupPendingResources();
    }

    private static Color ApplyEffects(
        uint argbColor,
        float saturation,
        float grayscale)
    {
        saturation = Math.Clamp(
            saturation,
            0f,
            1f);
        grayscale = Math.Clamp(
            grayscale,
            0f,
            1f);
        const double scale =
            1d / byte.MaxValue;
        double red =
            ((argbColor >> 16) & 0xff) *
            scale;
        double green =
            ((argbColor >> 8) & 0xff) *
            scale;
        double blue =
            (argbColor & 0xff) *
            scale;
        double luminance =
            red * 0.2126d +
            green * 0.7152d +
            blue * 0.0722d;
        red = luminance +
              (red - luminance) *
              saturation;
        green = luminance +
                (green - luminance) *
                saturation;
        blue = luminance +
               (blue - luminance) *
               saturation;
        luminance =
            red * 0.2126d +
            green * 0.7152d +
            blue * 0.0722d;
        red +=
            (luminance - red) *
            grayscale;
        green +=
            (luminance - green) *
            grayscale;
        blue +=
            (luminance - blue) *
            grayscale;
        return new Color
        {
            R = Math.Clamp(red, 0d, 1d),
            G = Math.Clamp(green, 0d, 1d),
            B = Math.Clamp(blue, 0d, 1d),
            A = ((argbColor >> 24) & 0xff) *
                scale
        };
    }
}
