using System;
using System.Collections.Generic;
using Avalonia.Platform;
#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Metal;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition.Server;
using ProGPU.Backend.Dawn;
#endif
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace Avalonia.ProGpu;

/// <summary>
/// Owns the renderer-wide ProGPU services used by one Avalonia render
/// interface context.
/// </summary>
/// <remarks>
/// GPU initialization is demand-driven. Window targets and drawing layers
/// share the same selected WebGPU context, preventing an offscreen layer from
/// silently creating a second native device. Surface selection is O(S) for S
/// offered surfaces and uses O(1) temporary storage.
/// </remarks>
internal sealed class ProGpuBackendContext : IPlatformRenderInterfaceContext
{
    private static readonly IReadOnlyDictionary<Type, object> s_noFeatures =
        new Dictionary<Type, object>();

    private readonly object _gate = new();
    private readonly IPlatformGraphicsContext? _platformGraphics;
    private readonly bool _requireNativeCompositionScene;
#if !AVALONIA11
    private readonly bool _useDawnMetalPresentation;
    private readonly bool _requireDawnMetalPresentation;
    private readonly bool _useDawnNativePresentation;
    private readonly bool _requireDawnNativePresentation;
    private readonly IDisposable? _compositionServerBackendLifetime;
    private DawnGpuContext? _dawnContext;
    private bool _dawnMetalUnavailable;
#endif
    private bool _disposed;

    internal ProGpuBackendContext(
        IPlatformGraphicsContext? platformGraphics,
        bool requireNativeCompositionScene,
        bool useDawnMetalPresentation,
        bool requireDawnMetalPresentation,
        bool useDawnNativePresentation,
        bool requireDawnNativePresentation)
    {
        _platformGraphics = platformGraphics;
        _requireNativeCompositionScene =
            requireNativeCompositionScene;
#if !AVALONIA11
        _useDawnMetalPresentation =
            useDawnMetalPresentation ||
            requireDawnMetalPresentation;
        _requireDawnMetalPresentation =
            requireDawnMetalPresentation;
        _useDawnNativePresentation =
            useDawnNativePresentation ||
            requireDawnNativePresentation;
        _requireDawnNativePresentation =
            requireDawnNativePresentation;

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        var compositionServerBackend =
            new ProGpuCompositionServerBackend(
                requireNativeCompositionScene);
        _compositionServerBackendLifetime =
            compositionServerBackend;
        PublicFeatures = new Dictionary<Type, object>
        {
            [typeof(ICompositionServerBackend)] =
                compositionServerBackend,
            [typeof(IExternalObjectsRenderInterfaceContextFeature)] =
                new ProGpuExternalObjectsFeature(GetSelectedContext)
        };
#else
        _compositionServerBackendLifetime = null;
        PublicFeatures = new Dictionary<Type, object>
        {
            [typeof(IExternalObjectsRenderInterfaceContextFeature)] =
                new ProGpuExternalObjectsFeature(GetSelectedContext)
        };
#endif
#else
        _ = useDawnMetalPresentation;
        _ = requireDawnMetalPresentation;
        _ = useDawnNativePresentation;
        _ = requireDawnNativePresentation;
        PublicFeatures = s_noFeatures;
#endif
    }

    public bool IsLost
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || _platformGraphics?.IsLost == true)
                    return true;
#if !AVALONIA11
                return _dawnContext?.Context.IsDeviceLost == true;
#else
                return false;
#endif
            }
        }
    }

    public IReadOnlyDictionary<Type, object> PublicFeatures { get; }

    public object? TryGetFeature(Type featureType)
    {
        ArgumentNullException.ThrowIfNull(featureType);
        return PublicFeatures.TryGetValue(featureType, out object? feature)
            ? feature
            : null;
    }

#if AVALONIA11
    public IRenderTarget CreateRenderTarget(IEnumerable<object> surfaces)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(surfaces);

        foreach (object surface in surfaces)
        {
            if (surface is IFramebufferPlatformSurface framebuffer)
            {
                return new FramebufferRenderTarget(
                    framebuffer,
                    requireNativeCompositionScene:
                        _requireNativeCompositionScene);
            }
        }

        throw new NotSupportedException(
            "None of the supplied Avalonia surfaces expose a framebuffer.");
    }

    public IDrawingContextLayerImpl CreateOffscreenRenderTarget(
        PixelSize pixelSize,
        double scaling) =>
        CreateDrawingLayer(
            pixelSize,
            new Vector(scaling, scaling),
            enableTextAntialiasing: true);
#else
    public IRenderTarget CreateRenderTarget(
        IEnumerable<IPlatformRenderSurface> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        SurfaceCandidates candidates = CollectCandidates(surfaces);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ShouldAttemptMetal(candidates))
        {
            IRenderTarget? metalTarget =
                TryCreateMetalTarget(candidates);
            if (metalTarget is not null)
                return metalTarget;
        }

        if (ShouldAttemptNativeWindow(candidates))
        {
            IRenderTarget? nativeTarget =
                TryCreateNativeWindowTarget(candidates.NativeWindow!);
            if (nativeTarget is not null)
                return nativeTarget;
        }

        if (candidates.Framebuffer is not null)
        {
            return new FramebufferRenderTarget(
                candidates.Framebuffer,
                requireNativeCompositionScene:
                    _requireNativeCompositionScene);
        }

        throw new NotSupportedException(
            "None of the supplied Avalonia surfaces can be presented by ProGPU.");
    }

    public IDrawingContextLayerImpl CreateOffscreenRenderTarget(
        PixelSize pixelSize,
        Vector scaling,
        bool enableTextAntialiasing) =>
        CreateDrawingLayer(
            pixelSize,
            scaling,
            enableTextAntialiasing);

    public PixelSize? MaxOffscreenRenderTargetPixelSize => null;

    public bool IsReadyToCreateRenderTarget(
        IEnumerable<IPlatformRenderSurface> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        SurfaceCandidates candidates = CollectCandidates(surfaces);
        if (ShouldAttemptMetal(candidates))
            return candidates.Metal!.IsReady;
        if (ShouldAttemptNativeWindow(candidates))
            return candidates.NativeWindow!.IsReady;
        return candidates.Framebuffer?.IsReady == true;
    }
#endif

    public void Dispose()
    {
#if !AVALONIA11
        DawnGpuContext? dawn;
        IDisposable? composition;
#endif
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
#if !AVALONIA11
            dawn = _dawnContext;
            _dawnContext = null;
            composition = _compositionServerBackendLifetime;
#endif
        }

#if !AVALONIA11
        composition?.Dispose();
        dawn?.Context.Dispose();
#endif
    }

    private SurfaceRenderTarget CreateDrawingLayer(
        PixelSize pixelSize,
        Vector scaling,
        bool enableTextAntialiasing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WgpuContext? context = GetSelectedContext();
        PixelFormat? format = context is null
            ? null
            : context.SwapChainFormat == TextureFormat.Rgba8Unorm
                ? PixelFormats.Rgba8888
                : PixelFormats.Bgra8888;
        return new SurfaceRenderTarget(
            new SurfaceRenderTarget.CreateInfo
            {
                Width = pixelSize.Width,
                Height = pixelSize.Height,
                Dpi = new Vector(
                    scaling.X * 96.0,
                    scaling.Y * 96.0),
                UseScaledDrawing = false,
                DisableTextLcdRendering =
                    !enableTextAntialiasing,
                Format = format,
                Context = context
            });
    }

    private WgpuContext? GetSelectedContext()
    {
#if !AVALONIA11
        lock (_gate)
        {
            if (_dawnContext is not null)
                return _dawnContext.Context;
        }
#endif
        if (WgpuContext.Current is { IsDisposed: false } current)
            return current;
        return WgpuContext.TryGetFirstActiveContext(out WgpuContext? active)
            ? active
            : null;
    }

#if !AVALONIA11
    private bool ShouldAttemptMetal(SurfaceCandidates candidates) =>
        !_dawnMetalUnavailable &&
        _useDawnMetalPresentation &&
        _platformGraphics is IMetalDevice &&
        candidates.Metal is not null;

    private bool ShouldAttemptNativeWindow(
        SurfaceCandidates candidates) =>
        _useDawnNativePresentation &&
        candidates.NativeWindow is not null &&
        DawnNativeWindowSource.TryGetKind(
            candidates.NativeWindow.HandleDescriptor,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            out _);

    private IRenderTarget? TryCreateMetalTarget(
        SurfaceCandidates candidates)
    {
        try
        {
            DawnGpuContext context = GetOrCreateMetalContext();
            var target = new DawnMetalRenderTarget(
                candidates.Metal!,
                (IMetalDevice)_platformGraphics!,
                context,
                _requireNativeCompositionScene);
            if (candidates.Framebuffer is null ||
                _requireDawnMetalPresentation)
            {
                return target;
            }

            return new DawnFallbackRenderTarget(
                target,
                candidates.Framebuffer,
                _requireNativeCompositionScene,
                DisableDawnMetalPresentation);
        }
        catch (Exception exception)
            when (CanFallbackFromPresentationFailure(exception) &&
                  !_requireDawnMetalPresentation)
        {
            DisableDawnMetalPresentation();
            return null;
        }
    }

    private IRenderTarget? TryCreateNativeWindowTarget(
        INativePlatformHandleSurface surface)
    {
        DawnNativeWindowSource? source = null;
        try
        {
            if (!DawnNativeWindowSource.TryGetKind(
                    surface.HandleDescriptor,
                    OperatingSystem.IsWindows(),
                    OperatingSystem.IsLinux(),
                    out DawnNativeWindowKind kind))
            {
                return null;
            }

            source = kind switch
            {
                DawnNativeWindowKind.Win32 =>
                    DawnNativeWindowSource.CreateWin32(surface.Handle),
                DawnNativeWindowKind.Xlib =>
                    DawnNativeWindowSource.CreateXlib(surface.Handle),
                _ => throw new NotSupportedException(
                    $"Unsupported Dawn native surface kind {kind}.")
            };
            DawnGpuContext context =
                GetOrCreateNativeContext(source);
            var target = new DawnNativeWindowRenderTarget(
                surface,
                source,
                context,
                _requireNativeCompositionScene);
            source = null;
            return target;
        }
        catch (Exception exception)
            when (CanFallbackFromPresentationFailure(exception) &&
                  !_requireDawnNativePresentation)
        {
            return null;
        }
        finally
        {
            source?.Dispose();
        }
    }

    private DawnGpuContext GetOrCreateMetalContext()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _dawnContext ??=
                DawnGpuContext.CreateMetalPresentation();
        }
    }

    private DawnGpuContext GetOrCreateNativeContext(
        DawnNativeWindowSource source)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _dawnContext ??=
                DawnGpuContext.CreateNativePresentation(source);
        }
    }

    private void DisableDawnMetalPresentation()
    {
        lock (_gate)
            _dawnMetalUnavailable = true;
    }

    private static bool CanFallbackFromPresentationFailure(
        Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not AccessViolationException &&
        exception is not OperationCanceledException;

    private static SurfaceCandidates CollectCandidates(
        IEnumerable<IPlatformRenderSurface> surfaces)
    {
        IMetalPlatformSurface? metal = null;
        INativePlatformHandleSurface? nativeWindow = null;
        IFramebufferPlatformSurface? framebuffer = null;
        foreach (IPlatformRenderSurface surface in surfaces)
        {
            metal ??= surface as IMetalPlatformSurface;
            nativeWindow ??= surface as INativePlatformHandleSurface;
            framebuffer ??= surface as IFramebufferPlatformSurface;
        }

        return new SurfaceCandidates(
            metal,
            nativeWindow,
            framebuffer);
    }

    private readonly record struct SurfaceCandidates(
        IMetalPlatformSurface? Metal,
        INativePlatformHandleSurface? NativeWindow,
        IFramebufferPlatformSurface? Framebuffer);
#endif
}
