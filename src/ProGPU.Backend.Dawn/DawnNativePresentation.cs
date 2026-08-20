using ProGPU.Backend;
using System.Runtime.InteropServices;
using SW = Silk.NET.WebGPU;
using W = WebGpuSharp;
using WebGpuSharp.FFI;

namespace ProGPU.Backend.Dawn;

public sealed unsafe partial class DawnGpuContext
{
    /// <summary>
    /// Creates a Dawn device selected against the supplied native presentation
    /// surface.
    /// </summary>
    /// <remarks>
    /// Startup performs one adapter request, one device request, and one
    /// surface-capability query. It allocates no full-frame pixel storage and
    /// keeps presentation on D3D12 (Win32) or Vulkan (Xlib/Wayland).
    /// </remarks>
    public static DawnGpuContext CreateNativePresentation(
        DawnNativeWindowSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        W.InstanceFeatureName timedWaitAny =
            W.InstanceFeatureName.TimedWaitAny;
        var instanceDescriptor = new InstanceDescriptorFFI
        {
            RequiredFeatureCount = 1,
            RequiredFeatures = &timedWaitAny
        };
        InstanceHandle instance =
            WebGPU_FFI.CreateInstance(&instanceDescriptor);
        if (instance == InstanceHandle.Null)
        {
            throw new InvalidOperationException(
                "Could not create a Dawn instance.");
        }

        SurfaceHandle compatibilitySurface = SurfaceHandle.Null;
        IDisposable? compatibilitySurfaceLease = null;
        AdapterHandle adapter = AdapterHandle.Null;
        DeviceHandle device = DeviceHandle.Null;
        QueueHandle queue = QueueHandle.Null;
        WgpuContext? context = null;
        try
        {
            compatibilitySurface = source.CreateSurface(
                instance,
                out compatibilitySurfaceLease);
            if (compatibilitySurface == SurfaceHandle.Null)
            {
                throw new NotSupportedException(
                    "Dawn could not create the native window surface.");
            }

            adapter = RequestPresentationAdapter(
                instance,
                source.BackendType,
                compatibilitySurface,
                source.BackendName);
            W.AdapterInfo? adapterInfo = adapter.GetInfo();
            NativeSurfaceCapabilities capabilities =
                QuerySurfaceCapabilities(
                    compatibilitySurface,
                    adapter);
            W.TextureFormat format =
                SelectSurfaceFormat(capabilities.Formats);

            Span<W.FeatureName> requiredFeatures =
                stackalloc W.FeatureName[5];
            int featureCount = 0;
            if (format == W.TextureFormat.BGRA8Unorm &&
                adapter.HasFeature(W.FeatureName.BGRA8UnormStorage))
            {
                requiredFeatures[featureCount++] =
                    W.FeatureName.BGRA8UnormStorage;
            }
            if (source.Kind == DawnNativeWindowKind.Win32 &&
                adapter.HasFeature(
                    DawnSharedTextureMemoryFeatures
                        .SharedTextureMemoryDXGISharedHandle))
            {
                requiredFeatures[featureCount++] =
                    DawnSharedTextureMemoryFeatures
                        .SharedTextureMemoryDXGISharedHandle;
            }
            if (source.Kind == DawnNativeWindowKind.Win32 &&
                adapter.HasFeature(
                    DawnSharedTextureMemoryFeatures
                        .SharedFenceDXGISharedHandle))
            {
                requiredFeatures[featureCount++] =
                    DawnSharedTextureMemoryFeatures
                        .SharedFenceDXGISharedHandle;
            }
            if (source.Kind == DawnNativeWindowKind.Android &&
                adapter.HasFeature(
                    DawnSharedTextureMemoryFeatures
                        .SharedTextureMemoryAHardwareBuffer))
            {
                requiredFeatures[featureCount++] =
                    DawnSharedTextureMemoryFeatures
                        .SharedTextureMemoryAHardwareBuffer;
            }
            if (source.Kind == DawnNativeWindowKind.Android &&
                adapter.HasFeature(
                    DawnSharedTextureMemoryFeatures
                        .SharedFenceSyncFD))
            {
                requiredFeatures[featureCount++] =
                    DawnSharedTextureMemoryFeatures
                        .SharedFenceSyncFD;
            }
            if ((source.Kind is
                     DawnNativeWindowKind.Xlib or
                     DawnNativeWindowKind.Wayland) &&
                adapter.HasFeature(
                    DawnSharedTextureMemoryFeatures
                        .SharedTextureMemoryDmaBuf))
            {
                requiredFeatures[featureCount++] =
                    DawnSharedTextureMemoryFeatures
                        .SharedTextureMemoryDmaBuf;
            }
            if ((source.Kind is
                     DawnNativeWindowKind.Xlib or
                     DawnNativeWindowKind.Wayland) &&
                adapter.HasFeature(
                    DawnSharedTextureMemoryFeatures
                        .SharedFenceSyncFD))
            {
                requiredFeatures[featureCount++] =
                    DawnSharedTextureMemoryFeatures
                        .SharedFenceSyncFD;
            }
            if (source.Kind == DawnNativeWindowKind.MetalLayer)
            {
                if (!adapter.HasFeature(
                        DawnSharedTextureMemoryFeatures
                            .SharedTextureMemoryIOSurface) ||
                    !adapter.HasFeature(
                        DawnSharedTextureMemoryFeatures
                            .SharedFenceMTLSharedEvent))
                {
                    throw new NotSupportedException(
                        "The Dawn Metal adapter does not expose IOSurface shared memory and MTLSharedEvent synchronization.");
                }
                requiredFeatures[featureCount++] =
                    DawnSharedTextureMemoryFeatures
                        .SharedTextureMemoryIOSurface;
                requiredFeatures[featureCount++] =
                    DawnSharedTextureMemoryFeatures
                        .SharedFenceMTLSharedEvent;
            }
            bool supportsTextureFormatsTier1 =
                adapter.HasFeature(
                    W.FeatureName.TextureFormatsTier1);
            if (supportsTextureFormatsTier1)
            {
                requiredFeatures[featureCount++] =
                    W.FeatureName.TextureFormatsTier1;
            }

            device = RequestDevice(
                instance,
                adapter,
                requiredFeatures[..featureCount]);
            queue = device.GetQueue();
            if (queue == QueueHandle.Null)
            {
                throw new InvalidOperationException(
                    "Dawn did not return a default queue.");
            }

            var limits = new W.Limits();
            if (device.GetLimits(&limits) != W.Status.Success)
            {
                throw new InvalidOperationException(
                    "Could not query Dawn device limits.");
            }

            var lifetime =
                new NativeLifetime(
                    instance,
                    adapter,
                    device,
                    queue);
            context = new WgpuContext();
            context.InitializeExternalNativeDevice(
                new DawnWebGpuApi(),
                lifetime,
                (SW.Device*)device.GetAddress(),
                (SW.Queue*)queue.GetAddress(),
                ToSilkFormat(format),
                maxSampledTexturesPerShaderStage:
                    limits.MaxSampledTexturesPerShaderStage,
                maxSamplersPerShaderStage:
                    limits.MaxSamplersPerShaderStage,
                maxBindGroups: limits.MaxBindGroups,
                supportsTextureFormatsTier1:
                    supportsTextureFormatsTier1,
                adapterBackendType:
                    ToSilkBackendType(
                        adapterInfo?.BackendType ??
                        source.BackendType),
                adapterName:
                    adapterInfo?.Description ??
                    adapterInfo?.Device ??
                    source.BackendName,
                adapterType:
                    ToSilkAdapterType(
                        adapterInfo?.AdapterType ??
                        W.AdapterType.Unknown),
                adapterDriverDescription:
                    DescribeAdapterDriver(adapterInfo),
                adapterVendorId:
                    adapterInfo?.VendorID ?? 0,
                adapterDeviceId:
                    adapterInfo?.DeviceID ?? 0,
                requiredCompatibleSurface: true);

            InstanceHandle ownedInstance = instance;
            AdapterHandle ownedAdapter = adapter;
            instance = InstanceHandle.Null;
            adapter = AdapterHandle.Null;
            device = DeviceHandle.Null;
            queue = QueueHandle.Null;
            return new DawnGpuContext(
                context,
                ownedInstance,
                ownedAdapter,
                new DeviceHandle((nuint)context.Device),
                new QueueHandle((nuint)context.Queue));
        }
        catch
        {
            context?.Dispose();
            if (compatibilitySurface != SurfaceHandle.Null)
            {
                compatibilitySurface.Release();
                compatibilitySurface = SurfaceHandle.Null;
            }
            if (queue != QueueHandle.Null)
            {
                queue.Release();
            }
            if (device != DeviceHandle.Null)
            {
                device.Destroy();
                device.Release();
            }
            if (adapter != AdapterHandle.Null)
            {
                adapter.Release();
            }
            if (instance != InstanceHandle.Null)
            {
                instance.Release();
            }
            throw;
        }
        finally
        {
            if (compatibilitySurface != SurfaceHandle.Null)
            {
                compatibilitySurface.Release();
                compatibilitySurface = SurfaceHandle.Null;
            }
            compatibilitySurfaceLease?.Dispose();
        }
    }

    public DawnNativePresentationSurface CreatePresentationSurface(
        DawnNativeWindowSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Context.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(DawnGpuContext));
        }
        if (ToSilkBackendType(source.BackendType) !=
            Context.AdapterBackendType)
        {
            throw new InvalidOperationException(
                "The native surface backend does not match the Dawn adapter.");
        }

        SurfaceHandle surface = source.CreateSurface(
            Instance,
            out IDisposable surfaceLifetimeLease);
        try
        {
            if (surface == SurfaceHandle.Null)
            {
                throw new NotSupportedException(
                    "Dawn could not create the native presentation surface.");
            }
            NativeSurfaceCapabilities capabilities =
                QuerySurfaceCapabilities(surface, Adapter);
            W.TextureFormat format =
                ToWebGpuSharpFormat(Context.SwapChainFormat);
            if (!capabilities.Formats.Contains(format))
            {
                throw new NotSupportedException(
                    $"The native surface does not support the Dawn device format {format}.");
            }
            W.CompositeAlphaMode alphaMode =
                SelectAlphaMode(
                    capabilities.AlphaModes,
                    source.BackendType);
            return new DawnNativePresentationSurface(
                this,
                surface,
                format,
                alphaMode,
                surfaceLifetimeLease);
        }
        catch
        {
            if (surface != SurfaceHandle.Null)
            {
                surface.Release();
            }
            surfaceLifetimeLease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attaches a Dawn-owned swapchain surface to the ordinary ProGPU context,
    /// keeping presentation and external-memory import on the same device.
    /// </summary>
    public void AttachNativePresentation(
        DawnNativeWindowSource source,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Context.Surface != null)
        {
            throw new InvalidOperationException(
                "A native presentation surface is already attached.");
        }
        if (Context.Api is not DawnWebGpuApi api)
        {
            throw new InvalidOperationException(
                "The context does not use the exact-ABI Dawn API.");
        }

        DawnNativePresentationSurface presentation =
            CreatePresentationSurface(source);
        bool attachedToApi = false;
        try
        {
            SW.Surface* surface =
                api.AttachPresentationSurface(presentation);
            attachedToApi = true;
            Context.AttachExternalNativePresentationSurface(
                surface,
                presentation.Format,
                width,
                height);
        }
        catch
        {
            if (attachedToApi)
            {
                api.SurfaceRelease(presentation.SilkSurface);
            }
            else
            {
                presentation.Dispose();
            }
            throw;
        }
    }

    private static AdapterHandle RequestPresentationAdapter(
        InstanceHandle instance,
        W.BackendType backendType,
        SurfaceHandle compatibleSurface,
        string backendName)
    {
        var state = new AdapterRequest();
        GCHandle stateHandle = GCHandle.Alloc(state);
        try
        {
            var options = new RequestAdapterOptionsFFI
            {
                BackendType = backendType,
                PowerPreference = W.PowerPreference.HighPerformance,
                CompatibleSurface = compatibleSurface
            };
            var callback = new RequestAdapterCallbackInfoFFI
            {
                Mode = W.CallbackMode.WaitAnyOnly,
                Callback = &CompleteAdapterRequest,
                Userdata1 =
                    (void*)GCHandle.ToIntPtr(stateHandle)
            };
            W.Future future =
                instance.RequestAdapter(&options, callback);
            Wait(instance, future, $"request a {backendName} adapter");
        }
        finally
        {
            stateHandle.Free();
        }

        if (state.Status != W.RequestAdapterStatus.Success ||
            state.Adapter == AdapterHandle.Null)
        {
            throw new InvalidOperationException(
                $"Dawn failed to request a {backendName} adapter: " +
                $"{state.Status}. {state.Message}");
        }
        return state.Adapter;
    }

    internal static NativeSurfaceCapabilities QuerySurfaceCapabilities(
        SurfaceHandle surface,
        AdapterHandle adapter)
    {
        var capabilities = new SurfaceCapabilitiesFFI();
        try
        {
            W.Status status =
                surface.GetCapabilities(
                    adapter,
                    ref capabilities);
            if (status != W.Status.Success)
            {
                throw new NotSupportedException(
                    "Dawn could not query native surface capabilities.");
            }

            var formats = new W.TextureFormat[
                checked((int)capabilities.FormatCount)];
            var alphaModes = new W.CompositeAlphaMode[
                checked((int)capabilities.AlphaModeCount)];
            for (int index = 0; index < formats.Length; index++)
            {
                formats[index] = capabilities.Formats[index];
            }
            for (int index = 0; index < alphaModes.Length; index++)
            {
                alphaModes[index] = capabilities.AlphaModes[index];
            }
            if ((capabilities.Usages &
                 W.TextureUsage.RenderAttachment) == 0)
            {
                throw new NotSupportedException(
                    "The Dawn surface cannot be used as a render attachment.");
            }
            return new NativeSurfaceCapabilities(
                formats,
                alphaModes);
        }
        finally
        {
            WebGPU_FFI.SurfaceCapabilitiesFreeMembers(
                capabilities);
        }
    }

    private static W.TextureFormat SelectSurfaceFormat(
        IReadOnlyList<W.TextureFormat> formats)
    {
        if (formats.Contains(W.TextureFormat.BGRA8Unorm))
        {
            return W.TextureFormat.BGRA8Unorm;
        }
        if (formats.Contains(W.TextureFormat.RGBA8Unorm))
        {
            return W.TextureFormat.RGBA8Unorm;
        }
        throw new NotSupportedException(
            "The Dawn surface exposes neither BGRA8Unorm nor RGBA8Unorm.");
    }

    internal static W.CompositeAlphaMode SelectAlphaMode(
        IReadOnlyList<W.CompositeAlphaMode> modes,
        W.BackendType backendType)
    {
        if (backendType == W.BackendType.Vulkan)
        {
            if (modes.Contains(W.CompositeAlphaMode.Opaque))
            {
                return W.CompositeAlphaMode.Opaque;
            }
            throw new NotSupportedException(
                "The Dawn Vulkan surface does not expose the required opaque alpha mode.");
        }
        if (modes.Contains(W.CompositeAlphaMode.Premultiplied))
        {
            return W.CompositeAlphaMode.Premultiplied;
        }
        if (modes.Contains(W.CompositeAlphaMode.Opaque))
        {
            return W.CompositeAlphaMode.Opaque;
        }
        if (modes.Contains(W.CompositeAlphaMode.Inherit))
        {
            return W.CompositeAlphaMode.Inherit;
        }
        if (modes.Count > 0)
        {
            return modes[0];
        }
        throw new NotSupportedException(
            "The Dawn surface exposes no composite alpha mode.");
    }

    internal static SW.TextureFormat ToSilkFormat(
        W.TextureFormat format) =>
        format switch
        {
            W.TextureFormat.BGRA8Unorm =>
                SW.TextureFormat.Bgra8Unorm,
            W.TextureFormat.RGBA8Unorm =>
                SW.TextureFormat.Rgba8Unorm,
            _ => throw new NotSupportedException(
                $"Unsupported Dawn presentation format {format}.")
        };

    internal static W.TextureFormat ToWebGpuSharpFormat(
        SW.TextureFormat format) =>
        format switch
        {
            SW.TextureFormat.Bgra8Unorm =>
                W.TextureFormat.BGRA8Unorm,
            SW.TextureFormat.Rgba8Unorm =>
                W.TextureFormat.RGBA8Unorm,
            _ => throw new NotSupportedException(
                $"Unsupported ProGPU presentation format {format}.")
        };

    private static SW.BackendType ToSilkBackendType(
        W.BackendType backendType) =>
        backendType switch
        {
            W.BackendType.D3D12 => SW.BackendType.D3D12,
            W.BackendType.Vulkan => SW.BackendType.Vulkan,
            W.BackendType.Metal => SW.BackendType.Metal,
            _ => throw new NotSupportedException(
                $"Unsupported Dawn presentation backend {backendType}.")
        };

    private static SW.AdapterType ToSilkAdapterType(
        W.AdapterType adapterType) =>
        adapterType switch
        {
            W.AdapterType.DiscreteGPU => SW.AdapterType.DiscreteGpu,
            W.AdapterType.IntegratedGPU => SW.AdapterType.IntegratedGpu,
            W.AdapterType.CPU => SW.AdapterType.Cpu,
            _ => SW.AdapterType.Unknown
        };

    private static string DescribeAdapterDriver(
        W.AdapterInfo? adapterInfo)
    {
        if (adapterInfo is null)
        {
            return string.Empty;
        }

        string vendor = adapterInfo.Vendor ?? string.Empty;
        string architecture = adapterInfo.Architecture ?? string.Empty;
        return string.IsNullOrWhiteSpace(vendor)
            ? architecture
            : string.IsNullOrWhiteSpace(architecture)
                ? vendor
                : $"{vendor} / {architecture}";
    }

    internal readonly record struct NativeSurfaceCapabilities(
        W.TextureFormat[] Formats,
        W.CompositeAlphaMode[] AlphaModes);
}

/// <summary>
/// Owns one configured Dawn window-system surface.
/// </summary>
/// <remarks>
/// Resize reconfiguration is O(1). Acquisition returns the compositor's
/// swapchain texture directly, so frame work performs no CPU readback and no
/// full-frame GPU copy.
/// </remarks>
public sealed unsafe class DawnNativePresentationSurface : IDisposable
{
    private const string PresentationDiagnosticsVariable =
        "PROGPU_DAWN_PRESENTATION_DIAGNOSTICS";
    private static readonly bool s_tracePresentation =
        IsPresentationDiagnosticsEnabled();
    private readonly DawnGpuContext _owner;
    private SurfaceHandle _surface;
    private readonly W.TextureFormat _format;
    private readonly W.CompositeAlphaMode _alphaMode;
    private IDisposable? _surfaceLifetimeLease;
    private uint _width;
    private uint _height;
    private bool _configured;
    private bool _reconfigureAfterPresent;
    private int _diagnosticAcquisitionCount;
    private int _diagnosticConfigurationCount;

    internal DawnNativePresentationSurface(
        DawnGpuContext owner,
        SurfaceHandle surface,
        W.TextureFormat format,
        W.CompositeAlphaMode alphaMode,
        IDisposable surfaceLifetimeLease)
    {
        _owner = owner;
        _surface = surface;
        _format = format;
        _alphaMode = alphaMode;
        _surfaceLifetimeLease = surfaceLifetimeLease;
    }

    public SW.TextureFormat Format =>
        DawnGpuContext.ToSilkFormat(_format);

    public bool UsesPremultipliedAlpha =>
        _alphaMode == W.CompositeAlphaMode.Premultiplied;

    internal SW.Surface* SilkSurface =>
        (SW.Surface*)_surface.GetAddress();

    public DawnNativePresentationFrame Acquire(
        uint width,
        uint height)
    {
        ObjectDisposedException.ThrowIf(
            _surface == SurfaceHandle.Null,
            this);
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Presentation dimensions must be nonzero.");
        }

        lock (_owner.Context.RenderLock)
        {
            ConfigureIfNeeded(width, height);
            SurfaceTextureFFI acquired = default;
            _surface.GetCurrentTexture(ref acquired);
            TraceAcquisition("owned", acquired);
            if (acquired.Status is
                W.SurfaceGetCurrentTextureStatus.Outdated or
                W.SurfaceGetCurrentTextureStatus.Lost)
            {
                if (acquired.Texture != TextureHandle.Null)
                {
                    acquired.Texture.Release();
                }
                Configure(width, height);
                acquired = default;
                _surface.GetCurrentTexture(ref acquired);
            }

            if (acquired.Status is not
                (W.SurfaceGetCurrentTextureStatus.SuccessOptimal or
                 W.SurfaceGetCurrentTextureStatus.SuccessSuboptimal) ||
                acquired.Texture == TextureHandle.Null)
            {
                if (acquired.Texture != TextureHandle.Null)
                {
                    acquired.Texture.Release();
                }
                throw new InvalidOperationException(
                    $"Dawn could not acquire the presentation texture: {acquired.Status}.");
            }

            _reconfigureAfterPresent =
                acquired.Status ==
                W.SurfaceGetCurrentTextureStatus.SuccessSuboptimal;
            try
            {
                var texture = GpuTexture.WrapOwnedExternal(
                    _owner.Context,
                    (SW.Texture*)acquired.Texture.GetAddress(),
                    width,
                    height,
                    Format,
                    SW.TextureUsage.RenderAttachment,
                    "Dawn native presentation texture",
                    UsesPremultipliedAlpha
                        ? GpuTextureAlphaMode.Premultiplied
                        : GpuTextureAlphaMode.Straight);
                acquired.Texture = TextureHandle.Null;
                return new DawnNativePresentationFrame(
                    this,
                    texture);
            }
            finally
            {
                if (acquired.Texture != TextureHandle.Null)
                {
                    acquired.Texture.Release();
                }
            }
        }
    }

    internal void Present()
    {
        lock (_owner.Context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(
                _surface == SurfaceHandle.Null,
                this);
            _surface.Present();
            if (_reconfigureAfterPresent)
            {
                _configured = false;
                _reconfigureAfterPresent = false;
            }
        }
    }

    internal void ConfigureExternal(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Presentation dimensions must be nonzero.");
        }
        lock (_owner.Context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(
                _surface == SurfaceHandle.Null,
                this);
            ConfigureIfNeeded(width, height);
        }
    }

    internal void GetCurrentTexture(SW.SurfaceTexture* target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_owner.Context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(
                _surface == SurfaceHandle.Null,
                this);
            SurfaceTextureFFI acquired = default;
            _surface.GetCurrentTexture(ref acquired);
            TraceAcquisition("external", acquired);
            bool acquiredTexture =
                acquired.Texture != TextureHandle.Null;
            *target = new SW.SurfaceTexture
            {
                Texture = (SW.Texture*)acquired.Texture.GetAddress(),
                Suboptimal =
                    acquired.Status ==
                    W.SurfaceGetCurrentTextureStatus.SuccessSuboptimal,
                Status = acquiredTexture
                    ? ToSilkStatus(acquired.Status)
                    : SW.SurfaceGetCurrentTextureStatus.Lost
            };
            if (target->Status !=
                SW.SurfaceGetCurrentTextureStatus.Success &&
                acquired.Texture != TextureHandle.Null)
            {
                acquired.Texture.Release();
                target->Texture = null;
            }
            _reconfigureAfterPresent =
                acquired.Status ==
                W.SurfaceGetCurrentTextureStatus.SuccessSuboptimal;
        }
    }

    internal void UnconfigureExternal()
    {
        lock (_owner.Context.RenderLock)
        {
            if (_surface == SurfaceHandle.Null || !_configured)
            {
                return;
            }
            _surface.Unconfigure();
            _configured = false;
            _reconfigureAfterPresent = false;
        }
    }

    private static SW.SurfaceGetCurrentTextureStatus ToSilkStatus(
        W.SurfaceGetCurrentTextureStatus status) =>
        status switch
        {
            W.SurfaceGetCurrentTextureStatus.SuccessOptimal or
            W.SurfaceGetCurrentTextureStatus.SuccessSuboptimal =>
                SW.SurfaceGetCurrentTextureStatus.Success,
            W.SurfaceGetCurrentTextureStatus.Timeout =>
                SW.SurfaceGetCurrentTextureStatus.Timeout,
            W.SurfaceGetCurrentTextureStatus.Outdated =>
                SW.SurfaceGetCurrentTextureStatus.Outdated,
            W.SurfaceGetCurrentTextureStatus.Lost =>
                SW.SurfaceGetCurrentTextureStatus.Lost,
            _ => SW.SurfaceGetCurrentTextureStatus.Lost
        };

    private void ConfigureIfNeeded(uint width, uint height)
    {
        if (!_configured ||
            _width != width ||
            _height != height)
        {
            Configure(width, height);
        }
    }

    private void Configure(uint width, uint height)
    {
        var configuration = new SurfaceConfigurationFFI
        {
            Device = _owner.Device,
            Format = _format,
            Usage = W.TextureUsage.RenderAttachment,
            Width = width,
            Height = height,
            PresentMode = W.PresentMode.Fifo,
            AlphaMode = _alphaMode
        };
        _surface.Configure(configuration);
        if (s_tracePresentation &&
            _diagnosticConfigurationCount++ < 32)
        {
            Console.WriteLine(
                $"[Dawn Presentation] configure " +
                $"size={width}x{height} " +
                $"format={_format} mode={configuration.PresentMode} " +
                $"alpha={_alphaMode}.");
        }
        _width = width;
        _height = height;
        _configured = true;
    }

    private void TraceAcquisition(
        string path,
        SurfaceTextureFFI acquired)
    {
        if (!s_tracePresentation ||
            _diagnosticAcquisitionCount++ >= 32)
        {
            return;
        }

        Console.WriteLine(
            $"[Dawn Presentation] acquire path={path} " +
            $"status={acquired.Status} " +
            $"texture={(acquired.Texture == TextureHandle.Null ? "null" : "valid")} " +
            $"configured={_configured} size={_width}x{_height}.");
    }

    private static bool IsPresentationDiagnosticsEnabled()
    {
        string? value = Environment.GetEnvironmentVariable(
            PresentationDiagnosticsVariable);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_surface == SurfaceHandle.Null)
        {
            return;
        }
        IDisposable? surfaceLifetimeLease;
        lock (_owner.Context.RenderLock)
        {
            if (_configured)
            {
                _surface.Unconfigure();
            }
            _surface.Release();
            _surface = SurfaceHandle.Null;
            _configured = false;
            surfaceLifetimeLease = _surfaceLifetimeLease;
            _surfaceLifetimeLease = null;
        }
        surfaceLifetimeLease?.Dispose();
    }
}

public sealed class DawnNativePresentationFrame : IDisposable
{
    private DawnNativePresentationSurface? _surface;

    internal DawnNativePresentationFrame(
        DawnNativePresentationSurface surface,
        GpuTexture texture)
    {
        _surface = surface;
        Texture = texture;
    }

    public GpuTexture Texture { get; }

    public void Complete(bool renderSucceeded)
    {
        DawnNativePresentationSurface? surface =
            Interlocked.Exchange(ref _surface, null);
        if (surface is not null && renderSucceeded)
        {
            surface.Present();
        }
    }

    public void Dispose()
    {
        _surface = null;
        Texture.Dispose();
    }
}
