using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using System.Numerics;

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
internal sealed unsafe class
    LinuxWebGpuCompositionThumbnailRenderer :
        IDisposable
{
    private readonly WgpuContext _context;
    private readonly GpuTexture _target;
    private readonly GpuTexture _blurSource;
    private readonly GpuTexture _blurIntermediate;
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
        GpuTexture? target = null;
        GpuTexture? blurSource = null;
        GpuTexture? blurIntermediate = null;
        GpuTextureReadbackBuffer? readback = null;
        try
        {
            target =
                new GpuTexture(
                    context,
                    width,
                    height,
                    TextureFormat.Rgba8Unorm,
                    TextureUsage.RenderAttachment |
                    TextureUsage.CopySrc,
                    "Linux Media Composition Thumbnail RGBA");
            blurSource =
                new GpuTexture(
                    context,
                    width,
                    height,
                    TextureFormat.Rgba8Unorm,
                    TextureUsage.TextureBinding |
                    TextureUsage.RenderAttachment,
                    "Linux Media Thumbnail Gaussian Source");
            blurIntermediate =
                new GpuTexture(
                    context,
                    width,
                    height,
                    TextureFormat.Rgba8Unorm,
                    TextureUsage.TextureBinding |
                    TextureUsage.RenderAttachment,
                    "Linux Media Thumbnail Gaussian Intermediate");
            readback =
                new GpuTextureReadbackBuffer(
                    context);
            readback.EnsureCapacity(
                width,
                height,
                bytesPerPixel: 4);
            _target = target;
            _blurSource = blurSource;
            _blurIntermediate =
                blurIntermediate;
            _readback = readback;
        }
        catch
        {
            readback?.Dispose();
            blurIntermediate?.Dispose();
            blurSource?.Dispose();
            target?.Dispose();
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
        LinuxGpuVideoEffectPlan effectPlan)
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

            if (effectPlan.HasSpatialEffect)
            {
                GpuNv12Processor.ProcessToRgba(
                    luma,
                    chroma,
                    _blurSource,
                    GpuTextureColorTransform.Identity,
                    inFlightSlot: 0);
                GpuTextureGaussianBlur.Blur(
                    _blurSource,
                    _blurIntermediate,
                    _target.ViewPtr,
                    _target.Format,
                    effectPlan.BlurStandardDeviation,
                    effectPlan.ColorTransform);
            }
            else
            {
                GpuNv12Processor.ProcessToRgba(
                    luma,
                    chroma,
                    _target,
                    effectPlan.ColorTransform,
                    inFlightSlot: 0);
            }
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
        LinuxGpuVideoEffectPlan effectPlan)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Color color =
            ApplyEffects(
                argbColor,
                effectPlan.ColorTransform);
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
        _blurIntermediate.Dispose();
        _blurSource.Dispose();
        _target.Dispose();
        _context.CleanupPendingResources();
    }

    private static Color ApplyEffects(
        uint argbColor,
        GpuTextureColorTransform transform)
    {
        const double scale =
            1d / byte.MaxValue;
        float red =
            ((argbColor >> 16) & 0xff) *
            (float)scale;
        float green =
            ((argbColor >> 8) & 0xff) *
            (float)scale;
        float blue =
            (argbColor & 0xff) *
            (float)scale;
        Vector3 processed =
            transform.Transform(
                new Vector3(
                    red,
                    green,
                    blue));
        return new Color
        {
            R = Math.Clamp(
                processed.X,
                0f,
                1f),
            G = Math.Clamp(
                processed.Y,
                0f,
                1f),
            B = Math.Clamp(
                processed.Z,
                0f,
                1f),
            A = ((argbColor >> 24) & 0xff) *
                scale
        };
    }
}
