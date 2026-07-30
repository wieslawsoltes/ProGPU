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
/// RGBA target, one aligned readback buffer, two transient imported plane
/// views, and at most two lazy RGBA snapshots for the scheduler's
/// previous/current candidates. Overlay state adds one lazy color source, one
/// retained compositor, and samples the bounded URI textures owned by the
/// shared overlay runtime. Managed pixel storage is one tightly packed RGBA
/// result. The final WebGPU map means this type does not provide a zero-copy
/// encoded result.
/// </remarks>
internal sealed unsafe class
    LinuxWebGpuCompositionThumbnailRenderer :
        IDisposable
{
    private const int SnapshotSlotCount = 2;
    private readonly WgpuContext _context;
    private readonly GpuTexture _target;
    private readonly GpuTexture _blurSource;
    private readonly GpuTexture _blurIntermediate;
    private GpuTexture? _overlaySource;
    private GpuTextureLayerCompositor?
        _layerCompositor;
    private readonly GpuTexture?[] _snapshots =
        new GpuTexture?[SnapshotSlotCount];
    private readonly bool[] _snapshotInUse =
        new bool[SnapshotSlotCount];
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
                    TextureUsage.CopySrc |
                    TextureUsage.CopyDst,
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

    internal WgpuContext Context => _context;

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
        LinuxGpuVideoEffectPlan effectPlan) =>
        RenderFrameCore(
            in frame,
            effectPlan,
            overlays: null,
            compositionTicks: 0);

    internal byte[] RenderFrame(
        in V4l2DecodedFrame frame,
        LinuxGpuVideoEffectPlan effectPlan,
        LinuxMediaOverlayRuntime overlays,
        long compositionTicks)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        return RenderFrameCore(
            in frame,
            effectPlan,
            overlays,
            compositionTicks);
    }

    private byte[] RenderFrameCore(
        in V4l2DecodedFrame frame,
        LinuxGpuVideoEffectPlan effectPlan,
        LinuxMediaOverlayRuntime? overlays,
        long compositionTicks)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        RenderDecodedFrame(
            in frame,
            effectPlan,
            _target);
        if (overlays is not null)
        {
            CompositeOverlays(
                overlays,
                compositionTicks);
        }
        return ReadTarget();
    }

    /// <summary>
    /// Consumes one decoded-frame lease and retains its processed RGBA result
    /// in one of two bounded previous/current candidate slots.
    /// </summary>
    internal int CaptureFrame(
        in V4l2DecodedFrame frame,
        LinuxGpuVideoEffectPlan effectPlan)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        int slot;
        try
        {
            slot = AcquireSnapshotSlot();
        }
        catch
        {
            frame.Owner.Dispose();
            throw;
        }
        try
        {
            RenderDecodedFrame(
                in frame,
                effectPlan,
                _snapshots[slot]!);
            return slot;
        }
        catch
        {
            _snapshotInUse[slot] = false;
            throw;
        }
    }

    internal byte[] RenderSnapshot(
        int slot,
        LinuxMediaOverlayRuntime overlays,
        long compositionTicks)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        GpuTexture snapshot =
            GetActiveSnapshot(slot);
        _target.CopyBaseLevelFrom(snapshot);
        CompositeOverlays(
            overlays,
            compositionTicks);
        return ReadTarget();
    }

    internal void ReleaseSnapshot(int slot)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        _ = GetActiveSnapshot(slot);
        _snapshotInUse[slot] = false;
    }

    internal byte[] RenderColor(
        uint argbColor,
        LinuxGpuVideoEffectPlan effectPlan) =>
        RenderColorCore(
            argbColor,
            effectPlan,
            overlays: null,
            compositionTicks: 0);

    internal byte[] RenderColor(
        uint argbColor,
        LinuxGpuVideoEffectPlan effectPlan,
        LinuxMediaOverlayRuntime overlays,
        long compositionTicks)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        return RenderColorCore(
            argbColor,
            effectPlan,
            overlays,
            compositionTicks);
    }

    private byte[] RenderColorCore(
        uint argbColor,
        LinuxGpuVideoEffectPlan effectPlan,
        LinuxMediaOverlayRuntime? overlays,
        long compositionTicks)
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
        if (overlays is not null)
        {
            CompositeOverlays(
                overlays,
                compositionTicks);
        }
        return ReadTarget();
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
        _layerCompositor?.Dispose();
        _overlaySource?.Dispose();
        _layerCompositor = null;
        _overlaySource = null;
        for (int index = 0;
             index < _snapshots.Length;
             index++)
        {
            _snapshots[index]?.Dispose();
            _snapshots[index] = null;
            _snapshotInUse[index] = false;
        }
        _blurIntermediate.Dispose();
        _blurSource.Dispose();
        _target.Dispose();
        _context.CleanupPendingResources();
    }

    private void RenderDecodedFrame(
        in V4l2DecodedFrame frame,
        LinuxGpuVideoEffectPlan effectPlan,
        GpuTexture destination)
    {
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
        var owner =
            new SharedOwnerRoot(frame.Owner);
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
                    destination.ViewPtr,
                    destination.Format,
                    effectPlan.BlurStandardDeviation,
                    effectPlan.ColorTransform);
            }
            else
            {
                GpuNv12Processor.ProcessToRgba(
                    luma,
                    chroma,
                    destination,
                    effectPlan.ColorTransform,
                    inFlightSlot: 0);
            }
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

    private int AcquireSnapshotSlot()
    {
        for (int index = 0;
             index < _snapshots.Length;
             index++)
        {
            if (_snapshotInUse[index])
            {
                continue;
            }
            _snapshots[index] ??=
                new GpuTexture(
                    _context,
                    _width,
                    _height,
                    TextureFormat.Rgba8Unorm,
                    TextureUsage.TextureBinding |
                    TextureUsage.RenderAttachment |
                    TextureUsage.CopySrc,
                    $"Linux Media Thumbnail Candidate {index}",
                    alphaMode:
                        GpuTextureAlphaMode.Straight);
            _snapshotInUse[index] = true;
            return index;
        }
        throw new InvalidOperationException(
            "Linux thumbnail scheduling exceeded the bounded previous/current GPU candidate set.");
    }

    private GpuTexture GetActiveSnapshot(int slot)
    {
        if ((uint)slot >=
                (uint)_snapshots.Length ||
            !_snapshotInUse[slot] ||
            _snapshots[slot] is not
                GpuTexture snapshot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot));
        }
        return snapshot;
    }

    private byte[] ReadTarget()
    {
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

    private void CompositeOverlays(
        LinuxMediaOverlayRuntime overlays,
        long compositionTicks)
    {
        ReadOnlySpan<LinuxMediaOverlayPlan>
            plans = overlays.Plans;
        for (int index = 0;
             index < plans.Length;
             index++)
        {
            LinuxMediaOverlayPlan plan =
                plans[index];
            if (!plan.IsActive(compositionTicks))
            {
                continue;
            }

            GpuTexture source;
            GpuTextureColorTransform transform;
            if (plan.IsUri)
            {
                if (!overlays.TryGetUriTexture(
                        index,
                        out GpuTexture? uriSource) ||
                    uriSource is null)
                {
                    continue;
                }
                source = uriSource;
                transform =
                    GpuTextureColorTransform
                        .Identity;
            }
            else
            {
                if (plan.ArgbColor is not uint color)
                {
                    throw new InvalidDataException(
                        "The overlay plan has no renderable source.");
                }
                EnsureColorOverlaySource();
                source = _overlaySource!;
                GpuTextureClearer.Clear(
                    source,
                    ApplyEffects(
                        color,
                        GpuTextureColorTransform
                            .Identity));
                transform =
                    plan.EffectPlan
                        .ColorTransform;
            }
            EnsureOverlayCompositor();
            _layerCompositor!.Composite(
                source,
                _target.ViewPtr,
                plan.Placement,
                transform);
        }
    }

    private void EnsureOverlayCompositor()
    {
        _layerCompositor ??=
            new GpuTextureLayerCompositor(
                _context,
                TextureFormat.Rgba8Unorm);
    }

    private void EnsureColorOverlaySource()
    {
        _overlaySource ??=
            new GpuTexture(
                _context,
                _width,
                _height,
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.RenderAttachment,
                "Linux Media Thumbnail Color Overlay",
                alphaMode:
                    GpuTextureAlphaMode.Straight);
    }
}
