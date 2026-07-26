#if !AVALONIA11
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Metal;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using W = WebGpuSharp;
using WF = WebGpuSharp.FFI;

namespace Avalonia.ProGpu;

/// <summary>
/// Presents ProGPU through Avalonia's native Metal windowing surface without
/// involving Silk.NET windowing or a CPU framebuffer.
/// </summary>
/// <remarks>
/// One Dawn shared-memory object and one WebGPU texture wrapper are retained
/// per CAMetalLayer swapchain IOSurface. Frame work is O(1), performs no
/// full-frame copy, and exchanges one wait/signal timeline pair between Dawn
/// and Avalonia's Metal command queue.
/// </remarks>
internal sealed class DawnMetalRenderTarget : IRenderTarget
{
    private const int MaximumRetainedDrawables = 4;

    private readonly IMetalDevice _metalDevice;
    private readonly IMetalExternalObjectsFeature _externalObjects;
    private readonly DawnGpuContext _dawnContext;
    private readonly OffscreenTextureCache _textureCache;
    private readonly Dictionary<nint, DrawableSlot> _slots = new();
    private IMetalPlatformSurfaceRenderTarget? _target;
    private long _frameId;

    public DawnMetalRenderTarget(
        IMetalPlatformSurface surface,
        IMetalDevice metalDevice,
        DawnGpuContext dawnContext,
        bool requireNativeCompositionScene)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(metalDevice);
        ArgumentNullException.ThrowIfNull(dawnContext);

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Dawn drawable import is currently implemented for Avalonia's macOS Metal surface.");
        }

        _externalObjects =
            metalDevice.TryGetFeature<IMetalExternalObjectsFeature>() ??
            throw new NotSupportedException(
                "Avalonia's Metal device does not expose timeline semaphore interop.");
        if (!_externalObjects.SupportedSemaphoreTypes.Contains(
                KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent))
        {
            throw new NotSupportedException(
                "Avalonia's Metal device does not accept MTLSharedEvent synchronization.");
        }

        _metalDevice = metalDevice;
        _dawnContext = dawnContext;
        _textureCache = new OffscreenTextureCache(
            requireNativeCompositionScene);
        _target = surface.CreateMetalRenderTarget(metalDevice);
    }

    public RenderTargetProperties Properties => new()
    {
        RetainsPreviousFrameContents = false,
        IsSuitableForDirectRendering = true
    };

    public PlatformRenderTargetState PlatformRenderTargetState =>
        _target?.State ?? PlatformRenderTargetState.Disposed;

    public IDrawingContextImpl CreateDrawingContext(
        IRenderTarget.RenderTargetSceneInfo sceneInfo,
        out RenderTargetDrawingContextProperties properties)
    {
        IMetalPlatformSurfaceRenderingSession? session = null;
        MetalFrameLease? frameLease = null;
        try
        {
            session = (_target ??
                throw new ObjectDisposedException(nameof(DawnMetalRenderTarget)))
                .BeginRendering();
            if (session.Size.Width <= 0 || session.Size.Height <= 0)
            {
                throw new RenderTargetNotReadyException();
            }

            MetalDrawableInfo drawable =
                MetalDrawableInterop.GetDrawableInfo(session.Texture);
            nint ioSurface = drawable.IOSurface;
            if (ioSurface == 0)
            {
                throw new NotSupportedException(
                    "The Avalonia CAMetalDrawable texture is not IOSurface-backed.");
            }

            DrawableSlot slot = GetOrCreateSlot(
                ioSurface,
                session.Size,
                drawable);
            slot.BeginAccess();
            frameLease = new MetalFrameLease(
                session,
                slot,
                _externalObjects,
                _dawnContext.SharedTextureMemory);
            session = null;

            properties = new RenderTargetDrawingContextProperties
            {
                PreviousFrameIsRetained = false
            };
            return new DrawingContextImpl(
                new DrawingContextImpl.CreateInfo
                {
                    Size = slot.Size,
                    Dpi = new Vector(
                        frameLease.Scaling * 96.0,
                        frameLease.Scaling * 96.0),
                    ScaleDrawingToDpi = false,
                    CacheHolder = _textureCache,
                    GpuRenderTarget = slot.Texture,
                    PresentationPath =
                        "DawnMetalIOSurface",
                    GpuRenderCompleted =
                        frameLease.CompleteGpuAccess
                },
                frameLease);
        }
        catch
        {
            frameLease?.Dispose();
            session?.Dispose();
            throw;
        }
    }

    private DrawableSlot GetOrCreateSlot(
        nint ioSurface,
        PixelSize size,
        MetalDrawableInfo drawable)
    {
        long frameId = checked(++_frameId);
        if (_slots.TryGetValue(ioSurface, out DrawableSlot? existing))
        {
            if (existing.Size == size)
            {
                existing.LastUsedFrame = frameId;
                return existing;
            }

            existing.Dispose();
            _slots.Remove(ioSurface);
        }

        var created = new DrawableSlot(
            ioSurface,
            size,
            _dawnContext,
            drawable);
        created.LastUsedFrame = frameId;
        _slots.Add(ioSurface, created);
        TrimDrawableCache(created);
        return created;
    }

    private void TrimDrawableCache(DrawableSlot current)
    {
        while (_slots.Count > MaximumRetainedDrawables)
        {
            DrawableSlot? oldest = null;
            foreach (DrawableSlot candidate in _slots.Values)
            {
                if (ReferenceEquals(candidate, current) ||
                    candidate.IsActive)
                {
                    continue;
                }
                if (oldest is null ||
                    candidate.LastUsedFrame < oldest.LastUsedFrame)
                {
                    oldest = candidate;
                }
            }

            if (oldest is null)
            {
                return;
            }

            _slots.Remove(oldest.IOSurface);
            oldest.Dispose();
        }
    }

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
        foreach (DrawableSlot slot in _slots.Values)
        {
            slot.Dispose();
        }
        _slots.Clear();
        _textureCache.Dispose();
    }

    private sealed unsafe class DrawableSlot : IDisposable
    {
        private readonly DawnGpuContext _dawnContext;
        private readonly WF.TextureHandle _dawnTexture;
        private readonly DawnMetalEndAccessResult _endAccess = new();
        private DawnSharedFence? _consumerFence;
        private IMetalSharedEvent? _platformEvent;
        private nint _sharedEvent;
        private ulong _consumerValue;
        private bool _initialized;
        private bool _disposed;

        internal DrawableSlot(
            nint ioSurface,
            PixelSize size,
            DawnGpuContext dawnContext,
            MetalDrawableInfo drawable)
        {
            IOSurface = ioSurface;
            Size = size;
            _dawnContext = dawnContext;
            SharedMemory =
                dawnContext.SharedTextureMemory.ImportIOSurface(ioSurface);
            DawnSharedTextureMemoryProperties properties =
                SharedMemory.GetProperties();
            if (properties.Size.Width != checked((uint)size.Width) ||
                properties.Size.Height != checked((uint)size.Height) ||
                properties.Format != W.TextureFormat.BGRA8Unorm)
            {
                SharedMemory.Dispose();
                throw new NotSupportedException(
                    $"Unsupported Avalonia Metal drawable: " +
                    $"{properties.Size.Width}x{properties.Size.Height} " +
                    $"{properties.Format}; MTL texture={drawable.TextureWidth}x" +
                    $"{drawable.TextureHeight} pixelFormat={drawable.TexturePixelFormat}, " +
                    $"IOSurface={drawable.SurfaceWidth}x{drawable.SurfaceHeight} " +
                    $"pixelFormat=0x{drawable.SurfacePixelFormat:x8}.");
            }

            const W.TextureUsage requiredUsage =
                W.TextureUsage.RenderAttachment |
                W.TextureUsage.TextureBinding |
                W.TextureUsage.CopySrc;
            if ((properties.Usage & requiredUsage) != requiredUsage)
            {
                SharedMemory.Dispose();
                throw new NotSupportedException(
                    $"The Avalonia drawable does not expose the required Dawn usage: " +
                    $"{properties.Usage}.");
            }

            _dawnTexture = SharedMemory.CreateTexture(
                requiredUsage,
                "Avalonia Dawn drawable"u8);
            try
            {
                Texture = GpuTexture.WrapOwnedExternal(
                    dawnContext.Context,
                    (Silk.NET.WebGPU.Texture*)_dawnTexture.GetAddress(),
                    checked((uint)size.Width),
                    checked((uint)size.Height),
                    TextureFormat.Bgra8Unorm,
                    TextureUsage.RenderAttachment |
                    TextureUsage.TextureBinding |
                    TextureUsage.CopySrc,
                    "Avalonia Dawn drawable",
                    GpuTextureAlphaMode.Premultiplied);
            }
            catch
            {
                _dawnTexture.Release();
                SharedMemory.Dispose();
                throw;
            }
        }

        internal nint IOSurface { get; }
        internal PixelSize Size { get; }
        internal DawnSharedTextureMemory SharedMemory { get; }
        internal GpuTexture Texture { get; }
        internal long LastUsedFrame { get; set; }
        internal bool IsActive { get; private set; }

        internal void BeginAccess()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsActive)
            {
                throw new InvalidOperationException(
                    "The Metal drawable is already being rendered.");
            }

            SharedMemory.BeginAccess(
                _dawnTexture,
                _initialized,
                _consumerFence,
                _consumerValue);
            IsActive = true;
        }

        internal void EndAccess(
            IMetalExternalObjectsFeature externalObjects,
            DawnSharedTextureMemoryFeature sharedTextureMemory,
            bool renderSucceeded)
        {
            if (!IsActive)
            {
                return;
            }

            try
            {
                SharedMemory.EndAccessAndExportMetalSharedEvent(
                    _dawnTexture,
                    _endAccess);
                _initialized = _endAccess.Initialized;
                _dawnContext.Context.PollDevice(wait: false);
                if (_endAccess.SharedEvent == 0)
                {
                    if (!renderSucceeded)
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        "Dawn did not export a Metal timeline event for a " +
                        "successfully rendered drawable. " +
                        $"Initialized={_endAccess.Initialized}.");
                }
                EnsureSharedEvent(
                    externalObjects,
                    sharedTextureMemory);
                externalObjects.SubmitWait(
                    _platformEvent!,
                    _endAccess.SignaledValue);
            }
            finally
            {
                IsActive = false;
            }
        }

        internal void SignalConsumer(
            IMetalExternalObjectsFeature externalObjects)
        {
            if (_platformEvent is null ||
                _endAccess.SharedEvent == 0)
            {
                return;
            }

            _consumerValue =
                checked(_endAccess.SignaledValue + 1);
            externalObjects.SubmitSignal(
                _platformEvent,
                _consumerValue);
        }

        private void EnsureSharedEvent(
            IMetalExternalObjectsFeature externalObjects,
            DawnSharedTextureMemoryFeature sharedTextureMemory)
        {
            nint sharedEvent = _endAccess.SharedEvent;
            if (_sharedEvent == sharedEvent &&
                _platformEvent is not null &&
                _consumerFence is not null)
            {
                return;
            }

            IMetalSharedEvent platformEvent =
                externalObjects.ImportSharedEvent(
                    new PlatformHandle(
                        sharedEvent,
                        KnownPlatformGraphicsExternalSemaphoreHandleTypes
                            .MetalSharedEvent));
            DawnSharedFence consumerFence =
                sharedTextureMemory.ImportMetalSharedEvent(
                    sharedEvent);
            _platformEvent?.Dispose();
            _consumerFence?.Dispose();
            _platformEvent = platformEvent;
            _consumerFence = consumerFence;
            _sharedEvent = sharedEvent;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            if (IsActive)
            {
                try
                {
                    SharedMemory.EndAccessAndExportMetalSharedEvent(
                        _dawnTexture,
                        _endAccess);
                }
                catch
                {
                    // Best-effort teardown after a failed frame.
                }
                IsActive = false;
            }

            _platformEvent?.Dispose();
            _consumerFence?.Dispose();
            _endAccess.Dispose();
            Texture.Dispose();
            SharedMemory.Dispose();
            _disposed = true;
        }
    }

    private sealed class MetalFrameLease : IDisposable
    {
        private IMetalPlatformSurfaceRenderingSession? _session;
        private DrawableSlot? _slot;
        private readonly IMetalExternalObjectsFeature _externalObjects;
        private readonly DawnSharedTextureMemoryFeature _sharedTextureMemory;
        private bool _gpuAccessCompleted;

        internal MetalFrameLease(
            IMetalPlatformSurfaceRenderingSession session,
            DrawableSlot slot,
            IMetalExternalObjectsFeature externalObjects,
            DawnSharedTextureMemoryFeature sharedTextureMemory)
        {
            _session = session;
            _slot = slot;
            _externalObjects = externalObjects;
            _sharedTextureMemory = sharedTextureMemory;
            Scaling = session.Scaling;
        }

        internal double Scaling { get; }

        internal void CompleteGpuAccess(bool renderSucceeded)
        {
            if (_gpuAccessCompleted)
            {
                return;
            }

            DrawableSlot slot = _slot ??
                throw new ObjectDisposedException(nameof(MetalFrameLease));
            slot.EndAccess(
                _externalObjects,
                _sharedTextureMemory,
                renderSucceeded);
            _gpuAccessCompleted = true;
        }

        public void Dispose()
        {
            IMetalPlatformSurfaceRenderingSession? session =
                Interlocked.Exchange(ref _session, null);
            DrawableSlot? slot =
                Interlocked.Exchange(ref _slot, null);
            if (session is null || slot is null)
            {
                return;
            }

            try
            {
                CompleteGpuAccessCore(slot);
                session.Dispose();
                session = null;
                slot.SignalConsumer(_externalObjects);
            }
            finally
            {
                session?.Dispose();
            }
        }

        private void CompleteGpuAccessCore(DrawableSlot slot)
        {
            if (_gpuAccessCompleted)
            {
                return;
            }

            slot.EndAccess(
                _externalObjects,
                _sharedTextureMemory,
                renderSucceeded: false);
            _gpuAccessCompleted = true;
        }
    }
}

internal readonly record struct MetalDrawableInfo(
    nint IOSurface,
    nuint TextureWidth,
    nuint TextureHeight,
    nuint TexturePixelFormat,
    nuint SurfaceWidth,
    nuint SurfaceHeight,
    uint SurfacePixelFormat);

internal static unsafe partial class MetalDrawableInterop
{
    private const string ObjectiveCLibrary =
        "/usr/lib/libobjc.A.dylib";
    private const string IOSurfaceLibrary =
        "/System/Library/Frameworks/IOSurface.framework/IOSurface";
    private static readonly nint s_ioSurfaceSelector =
        RegisterSelector("iosurface");
    private static readonly nint s_widthSelector =
        RegisterSelector("width");
    private static readonly nint s_heightSelector =
        RegisterSelector("height");
    private static readonly nint s_pixelFormatSelector =
        RegisterSelector("pixelFormat");

    internal static MetalDrawableInfo GetDrawableInfo(
        nint metalTexture)
    {
        if (metalTexture == 0)
        {
            throw new ArgumentException(
                "A valid MTLTexture is required.",
                nameof(metalTexture));
        }

        nint ioSurface = SendObject(
            metalTexture,
            s_ioSurfaceSelector);
        return new MetalDrawableInfo(
            ioSurface,
            (nuint)SendObject(metalTexture, s_widthSelector),
            (nuint)SendObject(metalTexture, s_heightSelector),
            (nuint)SendObject(metalTexture, s_pixelFormatSelector),
            ioSurface == 0 ? 0 : IOSurfaceGetWidth(ioSurface),
            ioSurface == 0 ? 0 : IOSurfaceGetHeight(ioSurface),
            ioSurface == 0 ? 0 : IOSurfaceGetPixelFormat(ioSurface));
    }

    private static nint RegisterSelector(string name)
    {
        nint selector = sel_registerName(name);
        if (selector == 0)
        {
            throw new InvalidOperationException(
                $"Objective-C selector '{name}' is unavailable.");
        }
        return selector;
    }

    [LibraryImport(ObjectiveCLibrary)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport(
        ObjectiveCLibrary,
        EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint SendObject(
        nint receiver,
        nint selector);

    [LibraryImport(IOSurfaceLibrary)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint IOSurfaceGetWidth(nint ioSurface);

    [LibraryImport(IOSurfaceLibrary)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint IOSurfaceGetHeight(nint ioSurface);

    [LibraryImport(IOSurfaceLibrary)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint IOSurfaceGetPixelFormat(nint ioSurface);
}
#endif
