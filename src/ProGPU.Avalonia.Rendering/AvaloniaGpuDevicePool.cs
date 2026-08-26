using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Platform;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Text;
using Silk.NET.WebGPU;
using WgpuTexture = Silk.NET.WebGPU.Texture;

namespace Avalonia.ProGpu;

/// <summary>
/// Owns one compositor per WebGPU device/target-format pair and performs
/// bounded top-level submission. No GPU object is created by CPU-only
/// drawing-context construction when an existing target context is supplied.
/// </summary>
internal static unsafe class AvaloniaGpuDevicePool
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<DeviceFormatKey, Compositor>
        s_compositors = new();
    private static WgpuContext? s_standaloneContext;

    [ThreadStatic]
    private static OffscreenTextureCache? s_threadCache;

    static AvaloniaGpuDevicePool()
    {
        WgpuContext.Disposing += ReleaseDevice;
    }

    internal static OffscreenTextureCache ThreadCache =>
        s_threadCache ??= new OffscreenTextureCache();

    internal static CompositorOptions Options { get; } =
        CompositorOptions.Default with
        {
            InitialVertexCount = 1024,
            InitialIndexCount = 1536,
            InitialColorGlyphAtlasSize = 64,
            ColorGlyphAtlasSize = 1024,
            GlyphUniformStagingBytes = 16 * 1024,
            GlyphCoverageStagingBytes =
                GlyphAtlas.DefaultCoverageRingBufferSize,
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1,
            EnableIncrementalScenePages =
                !string.Equals(
                    Environment.GetEnvironmentVariable(
                        "PROGPU_AVALONIA_INCREMENTAL_SCENE_PAGES"),
                    "0",
                    StringComparison.Ordinal)
        };

    internal static WgpuContext ResolveOrCreate(
        IntPtr surfaceHandle,
        TextureFormat preferredFormat)
    {
        if (surfaceHandle != IntPtr.Zero &&
            WgpuContext.TryGetActiveContextForSurface(
                surfaceHandle,
                out WgpuContext? surfaceContext))
        {
            if (surfaceContext is
            {
                IsInitialized: true,
                IsDisposed: false,
                IsDeviceLost: false
            })
            {
                WgpuContext.Current = surfaceContext;
                return surfaceContext;
            }
            if (surfaceContext.IsDeviceLost)
            {
                throw new RenderTargetNotReadyException();
            }
        }

        if (WgpuContext.Current is
            {
                IsInitialized: true,
                IsDisposed: false,
                IsDeviceLost: false
            } current)
            return current;
        if (WgpuContext.TryGetFirstActiveContext(out WgpuContext? active) &&
            active is
            {
                IsInitialized: true,
                IsDisposed: false,
                IsDeviceLost: false
            })
        {
            WgpuContext.Current = active;
            return active;
        }

        lock (s_gate)
        {
            if (s_standaloneContext is null ||
                s_standaloneContext.IsDisposed ||
                s_standaloneContext.IsDeviceLost)
            {
                s_standaloneContext?.Dispose();
                s_standaloneContext = new WgpuContext();
                s_standaloneContext.Initialize(window: null);
            }
            WgpuContext.Current = s_standaloneContext;
            return s_standaloneContext;
        }
    }

    internal static WgpuContext GetOrCreateStandalone(
        TextureFormat preferredFormat) =>
        ResolveOrCreate(IntPtr.Zero, preferredFormat);

    internal static Compositor RenderToTexture(
        WgpuContext context,
        OffscreenTextureCache resources,
        DrawingContext commands,
        GpuTexture target,
        PixelSize size,
        Vector4 clearColor)
    {
        Compositor compositor = GetCompositor(context, target.Format);
        ProGPU.Scene.Visual root =
            resources.GetOrUpdateRecordedVisual(
                commands,
                new Vector2(size.Width, size.Height));
        compositor.RenderOffscreen(
            root,
            CreateHostFrame(target.Width, target.Height),
            target,
            padding: 0f,
            clearColor,
            loadExistingContents: false);
        target.NotifyExternalContentChanged();
        return compositor;
    }

    internal static Compositor RenderToSurface(
        WgpuContext context,
        OffscreenTextureCache resources,
        DrawingContext commands,
        IntPtr surfaceHandle,
        PixelSize size,
        Vector4 clearColor)
    {
        if (surfaceHandle == IntPtr.Zero)
            throw new ArgumentException(
                "A WebGPU surface handle is required.",
                nameof(surfaceHandle));

        context.ReconfigureIfNeeded(
            checked((uint)size.Width),
            checked((uint)size.Height));

        var surfaceTexture = new SurfaceTexture();
        TextureView* targetView = null;
        context.Wgpu.SurfaceGetCurrentTexture(
            (Surface*)surfaceHandle,
            &surfaceTexture);
        try
        {
            if (surfaceTexture.Status !=
                SurfaceGetCurrentTextureStatus.Success)
            {
                if (surfaceTexture.Status ==
                    SurfaceGetCurrentTextureStatus.DeviceLost)
                {
                    context.ReportDeviceLost(
                        DeviceLostReason.Unknown,
                        "The presentation surface reported device loss.");
                    throw new RenderTargetCorruptedException(
                        "The WebGPU presentation device is lost.");
                }
                if (surfaceTexture.Status ==
                    SurfaceGetCurrentTextureStatus.OutOfMemory)
                {
                    throw new OutOfMemoryException(
                        "The WebGPU presentation surface ran out of memory.");
                }
                if (surfaceTexture.Status is
                    SurfaceGetCurrentTextureStatus.Outdated or
                    SurfaceGetCurrentTextureStatus.Lost)
                {
                    context.InvalidateSurfaceConfiguration();
                    context.TryConfigureSwapChain(
                        checked((uint)size.Width),
                        checked((uint)size.Height),
                        refreshCapabilities: true);
                }
                throw new RenderTargetNotReadyException();
            }

            var viewDescriptor = new TextureViewDescriptor
            {
                Format = context.SwapChainFormat,
                Dimension = TextureViewDimension.Dimension2D,
                BaseMipLevel = 0,
                MipLevelCount = 1,
                BaseArrayLayer = 0,
                ArrayLayerCount = 1,
                Aspect = TextureAspect.All
            };
            targetView = context.Wgpu.TextureCreateView(
                surfaceTexture.Texture,
                &viewDescriptor);
            if (targetView == null)
                throw new InvalidOperationException(
                    "WebGPU could not create a presentation texture view.");

            Compositor compositor =
                GetCompositor(context, context.SwapChainFormat);
            Vector4 previousClear = compositor.ClearColor;
            compositor.ClearColor = clearColor;
            try
            {
                ProGPU.Scene.Visual root =
                    resources.GetOrUpdateRecordedVisual(
                        commands,
                        new Vector2(size.Width, size.Height));
                compositor.RenderScene(
                    root,
                    CreateHostFrame(
                        checked((uint)size.Width),
                        checked((uint)size.Height)),
                    targetView);
            }
            finally
            {
                compositor.ClearColor = previousClear;
            }

            context.Wgpu.SurfacePresent((Surface*)surfaceHandle);
            return compositor;
        }
        finally
        {
            if (targetView != null)
                context.Wgpu.TextureViewRelease(targetView);
            if (surfaceTexture.Texture != null)
                context.Wgpu.TextureRelease(surfaceTexture.Texture);
        }
    }

    internal static Compositor RenderToFramebuffer(
        WgpuContext context,
        OffscreenTextureCache resources,
        DrawingContext commands,
        ILockedFramebuffer framebuffer,
        Vector4 clearColor)
    {
        uint width = checked((uint)framebuffer.Size.Width);
        uint height = checked((uint)framebuffer.Size.Height);
        TextureFormat format =
            framebuffer.Format == PixelFormats.Rgba8888
                ? TextureFormat.Rgba8Unorm
                : TextureFormat.Bgra8Unorm;
        GpuTexture target = GetOffscreenTexture(
            resources,
            context,
            width,
            height,
            format);
        Compositor compositor = RenderToTexture(
            context,
            resources,
            commands,
            target,
            framebuffer.Size,
            clearColor);
        GpuTextureReadbackBuffer readback =
            resources.CachedReadbackBuffer ??=
                new GpuTextureReadbackBuffer(context);
        bool copied = readback.TryReadTextureRows(
            target,
            width,
            height,
            framebuffer.Address.ToPointer(),
            checked((uint)framebuffer.RowBytes));
        if (!copied)
            throw new InvalidOperationException(
                "The GPU framebuffer readback did not complete.");
        return compositor;
    }

    internal static GpuTexture GetOffscreenTexture(
        OffscreenTextureCache resources,
        WgpuContext context,
        uint width,
        uint height,
        TextureFormat format)
    {
        if (resources.CachedTexture is
            {
                IsDisposed: false
            } cached &&
            cached.Context.SharesDeviceWith(context) &&
            cached.Width == width &&
            cached.Height == height &&
            cached.Format == format)
        {
            return cached;
        }

        resources.Invalidate(context);
        resources.CachedWidth = width;
        resources.CachedHeight = height;
        resources.CachedTexture = new GpuTexture(
            context,
            width,
            height,
            format,
            TextureUsage.RenderAttachment |
            TextureUsage.CopySrc |
            TextureUsage.TextureBinding,
            "Avalonia CPU framebuffer bridge");
        return resources.CachedTexture;
    }

    internal static Compositor GetCompositor(
        WgpuContext context,
        TextureFormat format)
    {
        var key = new DeviceFormatKey(context, format);
        lock (s_gate)
        {
            if (!s_compositors.TryGetValue(
                    key,
                    out Compositor? compositor))
            {
                compositor =
                    new Compositor(context, format, Options);
                s_compositors.Add(key, compositor);
            }
            return compositor;
        }
    }

    internal static void RenderRecordedCommands(
        DrawingContext commands,
        GpuTexture target,
        bool loadExistingContents)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(target);
        WgpuContext context = target.Context;
        lock (context.RenderLock)
        {
            using WgpuContext.CurrentContextScope scope =
                WgpuContext.PushCurrent(context);
            var root = new RecordedVisual(commands)
            {
                Size = new Vector2(target.Width, target.Height)
            };
            GetCompositor(context, target.Format).RenderOffscreen(
                root,
                CreateHostFrame(target.Width, target.Height),
                target,
                padding: 0f,
                Vector4.Zero,
                loadExistingContents);
            target.NotifyExternalContentChanged();
        }
    }

    internal static void Invalidate(WgpuContext context) =>
        ReleaseDevice(context);

    private static void ReleaseDevice(WgpuContext context)
    {
        List<DeviceFormatKey>? removedKeys = null;
        List<Compositor>? removed = null;
        lock (s_gate)
        {
            foreach (
                KeyValuePair<DeviceFormatKey, Compositor> entry
                in s_compositors)
            {
                if (!ReferenceEquals(entry.Key.Context, context))
                    continue;
                (removedKeys ??= new List<DeviceFormatKey>())
                    .Add(entry.Key);
                (removed ??= new List<Compositor>()).Add(entry.Value);
            }
            if (removedKeys is not null)
            {
                foreach (DeviceFormatKey key in removedKeys)
                    s_compositors.Remove(key);
            }
            if (ReferenceEquals(s_standaloneContext, context))
                s_standaloneContext = null;
        }

        if (removed is not null)
        {
            foreach (Compositor compositor in removed)
                compositor.Dispose();
        }
        s_threadCache?.Invalidate(context);
    }

    private static CompositorHostFrame CreateHostFrame(
        uint width,
        uint height) =>
        CompositorHostFrame.FromRenderTarget(width, height, 1f);

    private readonly record struct DeviceFormatKey(
        WgpuContext Context,
        TextureFormat Format);

    private sealed class RecordedVisual :
        ProGPU.Scene.Visual,
        IOwnedRenderCommandCache
    {
        private readonly DrawingContext _commands;

        internal RecordedVisual(DrawingContext commands)
        {
            _commands = commands;
        }

        public DrawingContext GetOrUpdateRenderCommandCache() =>
            _commands;
    }
}
