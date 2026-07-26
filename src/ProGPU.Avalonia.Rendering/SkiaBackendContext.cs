using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Platform;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using Avalonia.Rendering.Composition.Server;
#endif
#if !AVALONIA11
using Avalonia.Metal;
using ProGPU.Backend.Dawn;
#endif
#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Platform.Surfaces;
#endif
using ProGPU.Backend;

namespace Avalonia.ProGpu
{
    internal class SkiaContext : IPlatformRenderInterfaceContext
    {
        private readonly bool _requireNativeCompositionScene;
        private readonly bool _useDawnMetalPresentation;
        private readonly bool _requireDawnMetalPresentation;
        private readonly bool _useDawnNativePresentation;
        private readonly bool _requireDawnNativePresentation;
#if !AVALONIA11
        private readonly IMetalDevice? _metalDevice;
        private DawnGpuContext? _dawnContext;
        private bool _dawnPresentationUnavailable;
#endif
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        private readonly ProGpuCompositionServerBackend
            _compositionServerBackend;
#endif
        private int _isLost;

        public SkiaContext(
            object? gpu,
            bool requireNativeCompositionScene = false,
            bool useDawnMetalPresentation = true,
            bool requireDawnMetalPresentation = false,
            bool useDawnNativePresentation = true,
            bool requireDawnNativePresentation = false)
        {
            _requireNativeCompositionScene = requireNativeCompositionScene;
            _useDawnMetalPresentation = useDawnMetalPresentation;
            _requireDawnMetalPresentation = requireDawnMetalPresentation;
            _useDawnNativePresentation = useDawnNativePresentation;
            _requireDawnNativePresentation =
                requireDawnNativePresentation;
#if !AVALONIA11
            _metalDevice = gpu as IMetalDevice;
#endif
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
            _compositionServerBackend =
                new ProGpuCompositionServerBackend(
                    requireNativeCompositionScene);
#endif
            WgpuContext.OnWebGpuDeviceLost += OnWebGpuDeviceLost;
            PublicFeatures = new Dictionary<Type, object>
            {
#if !AVALONIA11
                [typeof(IExternalObjectsRenderInterfaceContextFeature)] =
                    new ProGpuExternalObjectsFeature(),
#endif
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
                [typeof(ICompositionServerBackend)] =
                    _compositionServerBackend
#endif
            };
        }

        public void Dispose()
        {
            WgpuContext.OnWebGpuDeviceLost -= OnWebGpuDeviceLost;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
            _compositionServerBackend.Dispose();
#endif
#if !AVALONIA11
            _dawnContext?.Dispose();
            _dawnContext = null;
#endif
        }

        private void OnWebGpuDeviceLost(
            Silk.NET.WebGPU.DeviceLostReason reason,
            string message)
        {
            Interlocked.Exchange(ref _isLost, 1);
        }

        public IRenderTarget CreateRenderTarget(
#if AVALONIA11
            IEnumerable<object>
#else
            IEnumerable<IPlatformRenderSurface>
#endif
            surfaces)
        {
            if (surfaces is not IList)
                surfaces = surfaces.ToList();

#if !AVALONIA11
            if (_useDawnMetalPresentation &&
                _metalDevice is not null &&
                !_dawnPresentationUnavailable)
            {
                IFramebufferPlatformSurface? framebufferSurface =
                    surfaces.OfType<IFramebufferPlatformSurface>()
                        .FirstOrDefault();
                foreach (var surface in surfaces)
                {
                    if (surface is not IMetalPlatformSurface metalSurface)
                    {
                        continue;
                    }

                    try
                    {
                        _dawnContext ??=
                            DawnGpuContext.CreateMetalPresentation();
                        var dawnTarget = new DawnMetalRenderTarget(
                            metalSurface,
                            _metalDevice,
                            _dawnContext,
                            _requireNativeCompositionScene);
                        if (_requireDawnMetalPresentation ||
                            framebufferSurface is null)
                        {
                            return dawnTarget;
                        }

                        return new DawnFallbackRenderTarget(
                            dawnTarget,
                            framebufferSurface,
                            _requireNativeCompositionScene,
                            DisableDawnPresentation);
                    }
                    catch (Exception exception)
                        when (_requireDawnMetalPresentation)
                    {
                        Console.Error.WriteLine(
                            $"[ProGPU:Dawn] Strict Metal presentation initialization failed: {exception}");
                        throw;
                    }
                    catch when (!_requireDawnMetalPresentation)
                    {
                        _dawnContext?.Dispose();
                        _dawnContext = null;
                        break;
                    }
                }
            }

            if (_useDawnNativePresentation &&
                !_dawnPresentationUnavailable)
            {
                IFramebufferPlatformSurface? framebufferSurface =
                    surfaces.OfType<IFramebufferPlatformSurface>()
                        .FirstOrDefault();
                foreach (IPlatformRenderSurface surface in surfaces)
                {
                    if (surface is not
                        INativePlatformHandleSurface nativeSurface ||
                        !DawnNativeWindowSource.TryGetKind(
                            nativeSurface.HandleDescriptor,
                            OperatingSystem.IsWindows(),
                            OperatingSystem.IsLinux(),
                            out DawnNativeWindowKind kind))
                    {
                        continue;
                    }

                    DawnNativeWindowSource? source = null;
                    try
                    {
                        source = kind switch
                        {
                            DawnNativeWindowKind.Win32 =>
                                DawnNativeWindowSource.CreateWin32(
                                    nativeSurface.Handle),
                            DawnNativeWindowKind.Xlib =>
                                DawnNativeWindowSource.CreateXlib(
                                    nativeSurface.Handle),
                            _ => throw new NotSupportedException()
                        };
                        _dawnContext ??=
                            DawnGpuContext.CreateNativePresentation(
                                source);
                        var dawnTarget =
                            new DawnNativeWindowRenderTarget(
                                nativeSurface,
                                source,
                                _dawnContext,
                                _requireNativeCompositionScene);
                        source = null;
                        if (_requireDawnNativePresentation ||
                            framebufferSurface is null)
                        {
                            return dawnTarget;
                        }

                        return new DawnFallbackRenderTarget(
                            dawnTarget,
                            framebufferSurface,
                            _requireNativeCompositionScene,
                            DisableDawnPresentation);
                    }
                    catch (Exception exception)
                        when (_requireDawnNativePresentation)
                    {
                        source?.Dispose();
                        Console.Error.WriteLine(
                            $"[ProGPU:Dawn] Strict {kind} presentation initialization failed: {exception}");
                        throw;
                    }
                    catch when (!_requireDawnNativePresentation)
                    {
                        source?.Dispose();
                        _dawnContext?.Dispose();
                        _dawnContext = null;
                        break;
                    }
                }

                if (_requireDawnNativePresentation)
                {
                    var descriptors = string.Join(
                        ", ",
                        surfaces.Select(
                            static surface =>
                                surface is INativePlatformHandleSurface native
                                    ? native.HandleDescriptor
                                    : "unsupported-typed-surface"));
                    throw new NotSupportedException(
                        "Strict Dawn native presentation did not receive a supported " +
                        $"HWND or XID surface. Surfaces: {descriptors}.");
                }
            }
#endif

            foreach (var surface in surfaces)
            {
                if (surface is IFramebufferPlatformSurface framebufferSurface)
                    return new FramebufferRenderTarget(
                        framebufferSurface,
                        requireNativeCompositionScene:
                            _requireNativeCompositionScene);
            }

            throw new NotSupportedException(
                "Don't know how to create a ProGpu render target from any of the provided surfaces");
        }

#if !AVALONIA11
        private void DisableDawnPresentation()
        {
            _dawnPresentationUnavailable = true;
            _dawnContext?.Dispose();
            _dawnContext = null;
        }

        public bool IsReadyToCreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
        {
            if (surfaces is not IList)
                surfaces = surfaces.ToList();

            foreach (var surface in surfaces)
            {
#if !AVALONIA11
                if (_useDawnMetalPresentation &&
                    _metalDevice is not null &&
                    !_dawnPresentationUnavailable &&
                    surface is IMetalPlatformSurface)
                {
                    return surface.IsReady;
                }
                if (_useDawnNativePresentation &&
                    !_dawnPresentationUnavailable &&
                    surface is
                        INativePlatformHandleSurface nativeSurface &&
                    DawnNativeWindowSource.TryGetKind(
                        nativeSurface.HandleDescriptor,
                        OperatingSystem.IsWindows(),
                        OperatingSystem.IsLinux(),
                        out _))
                {
                    return surface.IsReady;
                }
#endif
                if (surface is IFramebufferPlatformSurface)
                {
                    return surface.IsReady;
                }
            }

            return false;
        }

        public PixelSize? MaxOffscreenRenderTargetPixelSize => new PixelSize(8192, 8192);
#endif

        public IDrawingContextLayerImpl CreateOffscreenRenderTarget(PixelSize pixelSize,
#if AVALONIA11
            double scaling)
#else
            Vector scaling,
            bool enableTextAntialiasing)
#endif
        {
            PixelFormat? preferredFormat = null;
            var currentContext = WgpuContext.Current;
            if (currentContext != null)
            {
                preferredFormat = currentContext.SwapChainFormat == Silk.NET.WebGPU.TextureFormat.Rgba8Unorm
                    ? PixelFormats.Rgba8888
                    : PixelFormats.Bgra8888;
            }

            var createInfo = new SurfaceRenderTarget.CreateInfo
            {
                Width = pixelSize.Width,
                Height = pixelSize.Height,
                Dpi =
#if AVALONIA11
                    new Vector(scaling * 96, scaling * 96),
#else
                    scaling * 96,
#endif
                Format = preferredFormat,
                DisableTextLcdRendering =
#if AVALONIA11
                    false
#else
                    !enableTextAntialiasing
#endif
            };

            return new SurfaceRenderTarget(createInfo);
        }

        public bool IsLost => Volatile.Read(ref _isLost) != 0;
        public IReadOnlyDictionary<Type, object> PublicFeatures { get; }

        public object? TryGetFeature(Type featureType) =>
            PublicFeatures.TryGetValue(featureType, out object? feature)
                ? feature
                : null;
    }
}
