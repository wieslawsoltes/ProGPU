using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Backend;
using SW = Silk.NET.WebGPU;
using W = WebGpuSharp;
using WebGpuSharp.FFI;

namespace ProGPU.Backend.Dawn;

/// <summary>
/// Owns one exact-ABI Dawn device and its ordinary ProGPU
/// <see cref="WgpuContext"/>.
/// </summary>
/// <remarks>
/// Creation is explicit and reflection-free. The compositor and shared-memory
/// importer expose the same Dawn device. Disposal waits for submitted work,
/// releases ProGPU resources, then releases Queue, Device, Adapter, and
/// Instance in dependency order.
/// </remarks>
public sealed unsafe partial class DawnGpuContext :
    IDisposable,
    IProGpuExternalTextureImporter
{
    private const string NativeLibraryName = "webgpu_dawn";
    private const string IosFrameworkLibrary =
        "@rpath/webgpu_dawn.framework/webgpu_dawn";
    private static readonly object NativeLibrarySync = new();
    private static nint s_iosNativeLibrary;
    private static bool s_iosResolversInstalled;

    /// <summary>
    /// Reports whether the exact WebGPUSharp/Dawn native ABI can be resolved
    /// for the current process without creating a GPU instance.
    /// </summary>
    public static bool IsNativeLibraryAvailable()
    {
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
        {
            return EnsureIosNativeLibrary();
        }

        if (!NativeLibrary.TryLoad(
                NativeLibraryName,
                out nint library))
        {
            return false;
        }
        NativeLibrary.Free(library);
        return true;
    }

    private static bool EnsureIosNativeLibrary()
    {
        lock (NativeLibrarySync)
        {
            if (s_iosNativeLibrary != 0)
            {
                return true;
            }

            if (!NativeLibrary.TryLoad(
                    IosFrameworkLibrary,
                    out s_iosNativeLibrary))
            {
                return false;
            }

            if (!s_iosResolversInstalled)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(WebGPU_FFI).Assembly,
                    ResolveIosDawnImport);
                NativeLibrary.SetDllImportResolver(
                    typeof(DawnGpuContext).Assembly,
                    ResolveIosDawnImport);
                s_iosResolversInstalled = true;
            }

            return true;
        }
    }

    private static nint ResolveIosDawnImport(
        string libraryName,
        System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath) =>
        string.Equals(
            libraryName,
            NativeLibraryName,
            StringComparison.Ordinal)
            ? s_iosNativeLibrary
            : 0;

    private sealed class AdapterRequest
    {
        internal W.RequestAdapterStatus Status;
        internal AdapterHandle Adapter;
        internal string Message = string.Empty;
    }

    private sealed class DeviceRequest
    {
        internal W.RequestDeviceStatus Status;
        internal DeviceHandle Device;
        internal string Message = string.Empty;
    }

    private sealed class QueueWait
    {
        internal W.QueueWorkDoneStatus Status;
        internal string Message = string.Empty;
    }

    private sealed class NativeLifetime(
        InstanceHandle instance,
        AdapterHandle adapter,
        DeviceHandle device,
        QueueHandle queue) : IWebGpuExternalDeviceLifetime
    {
        private bool _disposed;

        public void Poll(bool wait)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (wait)
            {
                WaitForQueue(instance, queue);
            }
            else
            {
                instance.ProcessEvents();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            queue.Release();
            device.Destroy();
            device.Release();
            adapter.Release();
            instance.Release();
            _disposed = true;
        }
    }

    private DawnGpuContext(
        WgpuContext context,
        InstanceHandle instance,
        AdapterHandle adapter,
        DeviceHandle device,
        QueueHandle queue)
    {
        Context = context;
        Instance = instance;
        Adapter = adapter;
        Device = device;
        Queue = queue;
        SharedTextureMemory =
            new DawnSharedTextureMemoryFeature(device);
        Context.SetExternalTextureImporter(this);
    }

    public WgpuContext Context { get; }
    public InstanceHandle Instance { get; }
    public AdapterHandle Adapter { get; }
    public DeviceHandle Device { get; }
    public QueueHandle Queue { get; }
    public DawnSharedTextureMemoryFeature SharedTextureMemory { get; }

    /// <summary>
    /// Forces this Dawn device into its native lost state for an isolated
    /// recovery qualification. This is destructive for the current device and
    /// must never be used as an ordinary shutdown mechanism.
    /// </summary>
    public void ForceDeviceLossForDiagnostics()
    {
        lock (Context.RenderLock)
        {
            if (Context.IsDisposed || Context.IsDeviceLost)
            {
                throw new InvalidOperationException(
                    "The Dawn device is already unavailable.");
            }

            fixed (byte* message =
                "ProGPU forced native device-loss qualification\0"u8)
            {
                DawnNativeDiagnostics.DeviceForceLoss(
                    Device,
                    W.DeviceLostReason.Unknown,
                    StringViewFFI.CreateNullTerminated(message));
            }
        }
    }

    public static DawnGpuContext CreateMetalPresentation()
    {
        if (!OperatingSystem.IsMacOS() &&
            !OperatingSystem.IsIOS())
        {
            throw new PlatformNotSupportedException(
                "Dawn Metal presentation requires an Apple platform.");
        }

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

        AdapterHandle adapter = AdapterHandle.Null;
        DeviceHandle device = DeviceHandle.Null;
        QueueHandle queue = QueueHandle.Null;
        WgpuContext? context = null;
        try
        {
            adapter = RequestMetalAdapter(instance);

            Span<W.FeatureName> requiredFeatures =
                stackalloc W.FeatureName[4];
            requiredFeatures[0] =
                DawnSharedTextureMemoryFeatures
                    .SharedTextureMemoryIOSurface;
            requiredFeatures[1] =
                DawnSharedTextureMemoryFeatures
                    .SharedFenceMTLSharedEvent;
            int featureCount = 2;
            for (int index = 0; index < featureCount; index++)
            {
                if (!adapter.HasFeature(requiredFeatures[index]))
                {
                    throw new NotSupportedException(
                        $"The Dawn Metal adapter does not expose {requiredFeatures[index]}.");
                }
            }
            if (adapter.HasFeature(W.FeatureName.BGRA8UnormStorage))
            {
                requiredFeatures[featureCount++] =
                    W.FeatureName.BGRA8UnormStorage;
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
                SW.TextureFormat.Bgra8Unorm,
                maxSampledTexturesPerShaderStage:
                    limits.MaxSampledTexturesPerShaderStage,
                maxSamplersPerShaderStage:
                    limits.MaxSamplersPerShaderStage,
                maxBindGroups: limits.MaxBindGroups,
                supportsTextureFormatsTier1:
                    supportsTextureFormatsTier1,
                adapterBackendType: SW.BackendType.Metal,
                adapterName: "Dawn Metal");

            InstanceHandle ownedInstance = instance;
            AdapterHandle ownedAdapter = adapter;
            // WgpuContext now owns the exact handles through lifetime.
            instance = InstanceHandle.Null;
            adapter = AdapterHandle.Null;
            device = DeviceHandle.Null;
            queue = QueueHandle.Null;
            return new DawnGpuContext(
                context,
                ownedInstance,
                ownedAdapter,
                new DeviceHandle(
                    (nuint)context.Device),
                new QueueHandle(
                    (nuint)context.Queue));
        }
        catch
        {
            context?.Dispose();
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
    }

    public void Dispose()
    {
        Context.Dispose();
    }

    public bool TryImportExternalTexture(
        WgpuContext targetContext,
        in ProGpuExternalTextureDescriptor descriptor,
        IDisposable nativeOwner,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(nativeOwner);
        if (!ReferenceEquals(targetContext, Context) ||
            descriptor.Handle == 0)
        {
            texture = null!;
            return false;
        }
        bool isIOSurface =
            descriptor.HandleKind ==
            ProGpuExternalTextureHandleKind.IOSurface;
        bool isDxgiHandle =
            descriptor.HandleKind ==
            ProGpuExternalTextureHandleKind.DxgiSharedHandle;
        bool isAHardwareBuffer =
            descriptor.HandleKind ==
            ProGpuExternalTextureHandleKind.AndroidHardwareBuffer;
        bool isDmaBuf =
            descriptor.HandleKind ==
            ProGpuExternalTextureHandleKind.DmaBuf;
        if ((!isIOSurface &&
             !isDxgiHandle &&
             !isAHardwareBuffer &&
             !isDmaBuf) ||
            (isIOSurface &&
             Context.AdapterBackendType != SW.BackendType.Metal) ||
            (isDxgiHandle &&
             Context.AdapterBackendType != SW.BackendType.D3D12) ||
            (isAHardwareBuffer &&
             Context.AdapterBackendType != SW.BackendType.Vulkan) ||
            (isDmaBuf &&
             Context.AdapterBackendType != SW.BackendType.Vulkan))
        {
            texture = null!;
            return false;
        }

        W.TextureFormat dawnFormat = descriptor.Format switch
        {
            SW.TextureFormat.Bgra8Unorm =>
                W.TextureFormat.BGRA8Unorm,
            SW.TextureFormat.Rgba8Unorm =>
                W.TextureFormat.RGBA8Unorm,
            SW.TextureFormat.R8Unorm =>
                W.TextureFormat.R8Unorm,
            SW.TextureFormat.RG8Unorm =>
                W.TextureFormat.RG8Unorm,
            var format when
                format ==
                    ProGpuTextureFormats.R16Unorm =>
                W.TextureFormat.R16Unorm,
            var format when
                format ==
                    ProGpuTextureFormats.RG16Unorm =>
                W.TextureFormat.RG16Unorm,
            _ => W.TextureFormat.Undefined
        };
        if (dawnFormat == W.TextureFormat.Undefined ||
            ProGpuTextureFormats
                    .RequiresTextureFormatsTier1(
                        descriptor.Format) &&
                !targetContext
                    .SupportsTextureFormatsTier1)
        {
            texture = null!;
            return false;
        }

        DawnSharedTextureMemory? sharedMemory = null;
        TextureHandle importedTexture = TextureHandle.Null;
        bool accessBegan = false;
        try
        {
            sharedMemory = isIOSurface
                ? SharedTextureMemory.ImportIOSurface(
                    descriptor.Handle)
                : isDxgiHandle
                    ? SharedTextureMemory.ImportDXGISharedHandle(
                        descriptor.Handle,
                        descriptor.UsesKeyedMutex)
                    : isAHardwareBuffer
                        ? SharedTextureMemory.ImportAHardwareBuffer(
                            descriptor.Handle)
                        : SharedTextureMemory.ImportDmaBuf(
                            descriptor.Width,
                            descriptor.Height,
                            descriptor.DmaBuf);
            DawnSharedTextureMemoryProperties properties =
                sharedMemory.GetProperties();
            if (properties.Size.Width != descriptor.Width ||
                properties.Size.Height != descriptor.Height ||
                properties.Format != dawnFormat ||
                (descriptor.Usage &
                 SW.TextureUsage.TextureBinding) == 0 ||
                (properties.Usage &
                 (W.TextureUsage)descriptor.Usage) !=
                (W.TextureUsage)descriptor.Usage)
            {
                sharedMemory.Dispose();
                texture = null!;
                return false;
            }

            importedTexture = sharedMemory.CreateTexture(
                (W.TextureUsage)descriptor.Usage,
                "ProGPU decoded media frame"u8);
            sharedMemory.BeginAccess(
                importedTexture,
                descriptor.IsInitialized);
            accessBegan = true;
            var owner = new ImportedSharedTextureOwner(
                sharedMemory,
                importedTexture,
                nativeOwner);
            sharedMemory = null;
            TextureHandle ownedTexture = importedTexture;
            importedTexture = TextureHandle.Null;
            texture = GpuTexture.WrapOwnedExternal(
                targetContext,
                (SW.Texture*)ownedTexture.GetAddress(),
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.Usage,
                isIOSurface
                    ? "Imported IOSurface media frame"
                    : isDxgiHandle
                        ? "Imported DXGI media frame"
                        : isAHardwareBuffer
                            ? "Imported AHardwareBuffer media frame"
                            : "Imported DMA-BUF media frame",
                descriptor.AlphaMode,
                owner);
            return true;
        }
        catch
        {
            if (accessBegan &&
                sharedMemory is not null &&
                importedTexture != TextureHandle.Null)
            {
                try
                {
                    sharedMemory.EndAccess(importedTexture);
                }
                catch
                {
                }
            }
            if (importedTexture != TextureHandle.Null)
            {
                importedTexture.Release();
            }
            sharedMemory?.Dispose();
            throw;
        }
    }

    private sealed class ImportedSharedTextureOwner : IDisposable
    {
        private DawnSharedTextureMemory? _sharedMemory;
        private IDisposable? _nativeOwner;
        private readonly TextureHandle _texture;

        public ImportedSharedTextureOwner(
            DawnSharedTextureMemory sharedMemory,
            TextureHandle texture,
            IDisposable nativeOwner)
        {
            _sharedMemory = sharedMemory;
            _texture = texture;
            _nativeOwner = nativeOwner;
        }

        public void Dispose()
        {
            DawnSharedTextureMemory? sharedMemory =
                Interlocked.Exchange(
                    ref _sharedMemory,
                    null);
            IDisposable? nativeOwner =
                Interlocked.Exchange(
                    ref _nativeOwner,
                    null);
            try
            {
                sharedMemory?.EndAccess(_texture);
            }
            finally
            {
                sharedMemory?.Dispose();
                nativeOwner?.Dispose();
            }
        }
    }

    private static AdapterHandle RequestMetalAdapter(
        InstanceHandle instance)
    {
        var state = new AdapterRequest();
        GCHandle stateHandle = GCHandle.Alloc(state);
        try
        {
            var options = new RequestAdapterOptionsFFI
            {
                BackendType = W.BackendType.Metal,
                PowerPreference = W.PowerPreference.HighPerformance
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
            Wait(instance, future, "request a Metal adapter");
        }
        finally
        {
            stateHandle.Free();
        }

        if (state.Status != W.RequestAdapterStatus.Success ||
            state.Adapter == AdapterHandle.Null)
        {
            throw new InvalidOperationException(
                $"Dawn failed to request a Metal adapter: {state.Status}. {state.Message}");
        }
        return state.Adapter;
    }

    private static DeviceHandle RequestDevice(
        InstanceHandle instance,
        AdapterHandle adapter,
        ReadOnlySpan<W.FeatureName> requiredFeatures)
    {
        var state = new DeviceRequest();
        GCHandle stateHandle = GCHandle.Alloc(state);
        fixed (W.FeatureName* features = requiredFeatures)
        fixed (byte* label =
            "ProGPU Dawn Primary Device\0"u8)
        {
            try
            {
                var descriptor = new DeviceDescriptorFFI
                {
                    Label =
                        StringViewFFI.CreateNullTerminated(label),
                    RequiredFeatureCount =
                        (nuint)requiredFeatures.Length,
                    RequiredFeatures = features,
                    DeviceLostCallbackInfo =
                        new DeviceLostCallbackInfoFFI
                        {
                            Mode =
                                W.CallbackMode.AllowSpontaneous,
                            Callback = &OnDeviceLost
                        },
                    UncapturedErrorCallbackInfo =
                        new UncapturedErrorCallbackInfoFFI
                        {
                            Callback = &OnUncapturedError
                        }
                };
                var callback = new RequestDeviceCallbackInfoFFI
                {
                    Mode = W.CallbackMode.WaitAnyOnly,
                    Callback = &CompleteDeviceRequest,
                    Userdata1 =
                        (void*)GCHandle.ToIntPtr(stateHandle)
                };
                W.Future future =
                    adapter.RequestDevice(&descriptor, callback);
                Wait(instance, future, "request a Dawn device");
            }
            finally
            {
                stateHandle.Free();
            }
        }

        if (state.Status != W.RequestDeviceStatus.Success ||
            state.Device == DeviceHandle.Null)
        {
            throw new InvalidOperationException(
                $"Dawn failed to request a device: {state.Status}. {state.Message}");
        }
        return state.Device;
    }

    private static void WaitForQueue(
        InstanceHandle instance,
        QueueHandle queue)
    {
        var state = new QueueWait();
        GCHandle stateHandle = GCHandle.Alloc(state);
        try
        {
            var callback = new QueueWorkDoneCallbackInfoFFI
            {
                Mode = W.CallbackMode.WaitAnyOnly,
                Callback = &CompleteQueueWait,
                Userdata1 =
                    (void*)GCHandle.ToIntPtr(stateHandle)
            };
            W.Future future =
                queue.OnSubmittedWorkDone(callback);
            Wait(instance, future, "wait for submitted Dawn work");
        }
        finally
        {
            stateHandle.Free();
        }

        if (state.Status != W.QueueWorkDoneStatus.Success)
        {
            throw new InvalidOperationException(
                $"Dawn queue wait failed: {state.Status}. {state.Message}");
        }
    }

    private static void Wait(
        InstanceHandle instance,
        W.Future future,
        string operation)
    {
        var wait = new W.FutureWaitInfo
        {
            Future = future
        };
        W.WaitStatus status =
            instance.WaitAny(1, &wait, ulong.MaxValue);
        if (status != W.WaitStatus.Success)
        {
            throw new InvalidOperationException(
                $"Dawn failed to {operation}: {status}.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompleteAdapterRequest(
        W.RequestAdapterStatus status,
        AdapterHandle adapter,
        StringViewFFI message,
        void* userData1,
        void* userData2)
    {
        var state =
            (AdapterRequest)
            GCHandle.FromIntPtr((nint)userData1).Target!;
        state.Status = status;
        state.Adapter = adapter;
        state.Message = Message(message);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompleteDeviceRequest(
        W.RequestDeviceStatus status,
        DeviceHandle device,
        StringViewFFI message,
        void* userData1,
        void* userData2)
    {
        var state =
            (DeviceRequest)
            GCHandle.FromIntPtr((nint)userData1).Target!;
        state.Status = status;
        state.Device = device;
        state.Message = Message(message);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompleteQueueWait(
        W.QueueWorkDoneStatus status,
        StringViewFFI message,
        void* userData1,
        void* userData2)
    {
        var state =
            (QueueWait)
            GCHandle.FromIntPtr((nint)userData1).Target!;
        state.Status = status;
        state.Message = Message(message);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedError(
        DeviceHandle* device,
        W.ErrorType type,
        StringViewFFI message,
        void* userData1,
        void* userData2)
    {
        string errorMessage = Message(message);
        Console.Error.WriteLine(
            $"[Dawn WebGPU Error] {type}: {errorMessage}");
        WgpuContext.RaiseWebGpuError(
            ErrorType(type),
            errorMessage);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLost(
        DeviceHandle* device,
        W.DeviceLostReason reason,
        StringViewFFI message,
        void* userData1,
        void* userData2)
    {
        if (reason == W.DeviceLostReason.Destroyed)
        {
            return;
        }
        string lossMessage = Message(message);
        Console.Error.WriteLine(
            $"[Dawn Device Lost] {reason}: {lossMessage}");
        WgpuContext.RaiseWebGpuDeviceLost(
            SW.DeviceLostReason.Unknown,
            lossMessage);
    }

    private static string Message(StringViewFFI message) =>
        message.Data == null
            ? string.Empty
            : Encoding.UTF8.GetString(message.AsSpan());

    private static SW.ErrorType ErrorType(
        W.ErrorType type) => type switch
        {
            W.ErrorType.NoError => SW.ErrorType.NoError,
            W.ErrorType.Validation => SW.ErrorType.Validation,
            W.ErrorType.OutOfMemory => SW.ErrorType.OutOfMemory,
            W.ErrorType.Internal => SW.ErrorType.Internal,
            W.ErrorType.Unknown => SW.ErrorType.Unknown,
            _ => SW.ErrorType.Unknown
        };
}

internal static unsafe partial class DawnNativeDiagnostics
{
    [LibraryImport(
        "webgpu_dawn",
        EntryPoint = "wgpuDeviceForceLoss")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DeviceForceLoss(
        DeviceHandle device,
        W.DeviceLostReason reason,
        StringViewFFI message);
}
