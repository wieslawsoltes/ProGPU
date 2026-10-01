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
    private readonly object _lifetimeGate = new();
    private int _lifetimeReferenceCount = 1;
    private bool _ownerLifetimeReleased;
    private bool _contextLifetimeDisposed;

    static DawnGpuContext() => DawnNativeProvider.EnsureResolvers();

    /// <summary>
    /// Selects one exact-ABI native Dawn provider by its absolute library path,
    /// before any WebGPUSharp or Dawn native import is used.
    /// </summary>
    /// <remarks>
    /// Selection is process-wide and immutable. Repeating the same path only
    /// verifies the existing selection; changing it or selecting after default
    /// native use is rejected. Configure before all direct FFI calls as well as
    /// context factories. Arbitrary prior FFI calls/cache bindings cannot be
    /// detected or retroactively rebound. A foreign import resolver is rejected,
    /// not replaced or assumed to select this provider. The supplied artifact
    /// must match the packaged WebGPUSharp ABI; this method does not infer ABI
    /// compatibility or create/reconfigure a GPU device.
    /// </remarks>
    public static void ConfigureNativeProviderLibrary(string absolutePath) =>
        DawnNativeProvider.ConfigureLibrary(absolutePath);

    /// <summary>
    /// Reports whether the exact WebGPUSharp/Dawn native ABI can be resolved
    /// for the current process without creating a GPU instance.
    /// </summary>
    /// <remarks>
    /// An unbound probe does not select or pin the default provider and leaves
    /// explicit configuration available. A configured provider probes only its
    /// selected file; availability is not runtime/ABI qualification.
    /// </remarks>
    public static bool IsNativeLibraryAvailable() => DawnNativeProvider.IsAvailable();

    private sealed class DeviceLossCallbackState
    {
        private readonly object _sync = new();
        private WgpuContext? _context;
        private bool _isLost;
        private string _message = string.Empty;

        internal void Bind(WgpuContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            bool report;
            string message;
            lock (_sync)
            {
                _context = context;
                report = _isLost;
                message = _message;
            }
            if (report)
            {
                context.ReportDeviceLost(
                    SW.DeviceLostReason.Unknown,
                    message);
            }
        }

        internal void Report(string message)
        {
            WgpuContext? context;
            lock (_sync)
            {
                if (_isLost)
                {
                    return;
                }
                _isLost = true;
                _message = message;
                context = _context;
            }
            context?.ReportDeviceLost(
                SW.DeviceLostReason.Unknown,
                message);
        }
    }

    private sealed class NativeLifetime(
        InstanceHandle instance,
        AdapterHandle adapter,
        DeviceHandle device,
        QueueHandle queue,
        GCHandle deviceLossStateHandle) : IWebGpuExternalDeviceLifetime
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
            if (deviceLossStateHandle.IsAllocated)
            {
                deviceLossStateHandle.Free();
            }
            _disposed = true;
        }
    }

    private sealed class ContextLifetimeLease(
        DawnGpuContext owner) : IDisposable
    {
        private DawnGpuContext? _owner = owner;

        public void Dispose()
        {
            DawnGpuContext? released =
                Interlocked.Exchange(ref _owner, null);
            released?.ReleaseLifetimeReference();
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

    internal IDisposable AcquireLifetimeLease()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(
                _ownerLifetimeReleased ||
                _contextLifetimeDisposed,
                this);
            checked
            {
                _lifetimeReferenceCount++;
            }
            return new ContextLifetimeLease(this);
        }
    }

    /// <summary>
    /// Returns the exact native WebGPU handles owned by this context as a
    /// package-neutral value record for typed native renderer integration.
    /// </summary>
    public DawnNativeDeviceHandles GetNativeDeviceHandles()
    {
        if (Context.IsDisposed || Context.IsDeviceLost)
        {
            throw new ObjectDisposedException(nameof(DawnGpuContext));
        }
        return new DawnNativeDeviceHandles(
            (nuint)Instance.GetAddress(),
            (nuint)Device.GetAddress(),
            (nuint)Queue.GetAddress());
    }

    // Borrow the process-pinned module that served this exact context's FFI
    // imports. No independently selected renderer module or native ownership
    // crosses this signed internal seam.
    internal nint GetNativeProviderModule()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_ownerLifetimeReleased || _contextLifetimeDisposed ||
                Context.IsDisposed || Context.IsDeviceLost, this);
            return DawnNativeProvider.GetModule();
        }
    }

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
        DeviceLossCallbackState? deviceLossState = null;
        GCHandle deviceLossStateHandle = default;
        WgpuContext? context = null;
        DawnDeviceLifetime? lifetime = null;
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
                requiredFeatures[..featureCount],
                out deviceLossState,
                out deviceLossStateHandle);
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

            lifetime = new DawnDeviceLifetime(new NativeLifetime(
                instance, adapter, device, queue, deviceLossStateHandle));
            InstanceHandle ownedInstance = instance;
            AdapterHandle ownedAdapter = adapter;
            DeviceHandle ownedDevice = device;
            QueueHandle ownedQueue = queue;
            instance = InstanceHandle.Null;
            adapter = AdapterHandle.Null;
            device = DeviceHandle.Null;
            queue = QueueHandle.Null;
            deviceLossStateHandle = default;
            context = new WgpuContext {
                ComputeLimits = DawnDeviceLimits.ToComputeLimits(limits)
            };
            var api = new DawnWebGpuApi();
            var result = new DawnGpuContext(context, ownedInstance, ownedAdapter, ownedDevice, ownedQueue);
            deviceLossState.Bind(context);
            if (context.IsDeviceLost)
                throw new InvalidOperationException("The requested Dawn device was lost during creation.");
            context.InitializeExternalNativeDevice(
                api,
                lifetime,
                (SW.Device*)ownedDevice.GetAddress(),
                (SW.Queue*)ownedQueue.GetAddress(),
                SW.TextureFormat.Bgra8Unorm,
                maxSampledTexturesPerShaderStage:
                    limits.MaxSampledTexturesPerShaderStage,
                maxSamplersPerShaderStage:
                    limits.MaxSamplersPerShaderStage,
                maxBindGroups: limits.MaxBindGroups,
                maxBufferSize: limits.MaxBufferSize,
                supportsTextureFormatsTier1:
                    supportsTextureFormatsTier1,
                adapterBackendType: SW.BackendType.Metal,
                adapterName: "Dawn Metal");
            if (context.IsDeviceLost)
                throw new InvalidOperationException("The requested Dawn device was lost during initialization.");
            lifetime = null;
            context = null;
            return result;
        }
        catch
        {
            try { context?.Dispose(); } catch { }
            try { lifetime?.Dispose(); } catch { }
            try { if (queue != QueueHandle.Null) queue.Release(); } catch { }
            try
            {
                if (device != DeviceHandle.Null)
                {
                    try { device.Destroy(); }
                    finally { device.Release(); }
                }
            }
            catch { }
            try { if (adapter != AdapterHandle.Null) adapter.Release(); } catch { }
            try { if (instance != InstanceHandle.Null) instance.Release(); } catch { }
            if (deviceLossStateHandle.IsAllocated) deviceLossStateHandle.Free();
            throw;
        }
    }

    public void Dispose()
    {
        bool disposeContext;
        lock (_lifetimeGate)
        {
            if (_ownerLifetimeReleased)
            {
                return;
            }
            _ownerLifetimeReleased = true;
            disposeContext = --_lifetimeReferenceCount == 0;
            if (disposeContext)
            {
                _contextLifetimeDisposed = true;
            }
        }
        if (disposeContext)
        {
            Context.Dispose();
        }
    }

    private void ReleaseLifetimeReference()
    {
        bool disposeContext;
        lock (_lifetimeGate)
        {
            if (_lifetimeReferenceCount <= 0)
            {
                return;
            }
            disposeContext = --_lifetimeReferenceCount == 0;
            if (disposeContext)
            {
                _contextLifetimeDisposed = true;
            }
        }
        if (disposeContext)
        {
            Context.Dispose();
        }
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
        using DawnMetalAutoreleasePool autoreleasePool =
            DawnMetalAutoreleasePool.Enter(isIOSurface);
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
                nativeOwner,
                useMetalAutoreleasePool: isIOSurface);
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
        private readonly bool _useMetalAutoreleasePool;

        public ImportedSharedTextureOwner(
            DawnSharedTextureMemory sharedMemory,
            TextureHandle texture,
            IDisposable nativeOwner,
            bool useMetalAutoreleasePool)
        {
            _sharedMemory = sharedMemory;
            _texture = texture;
            _nativeOwner = nativeOwner;
            _useMetalAutoreleasePool =
                useMetalAutoreleasePool;
        }

        public void Dispose()
        {
            using DawnMetalAutoreleasePool autoreleasePool =
                DawnMetalAutoreleasePool.Enter(
                    _useMetalAutoreleasePool);
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

    private static AdapterHandle RequestMetalAdapter(InstanceHandle instance)
    {
        var options = new RequestAdapterOptionsFFI
        {
            BackendType = W.BackendType.Metal,
            PowerPreference = W.PowerPreference.HighPerformance
        };
        return RequestOwnedAdapter(instance, options, "request a Metal adapter");
    }

    private static DeviceHandle RequestDevice(
        InstanceHandle instance,
        AdapterHandle adapter,
        ReadOnlySpan<W.FeatureName> requiredFeatures,
        out DeviceLossCallbackState deviceLossState,
        out GCHandle deviceLossStateHandle,
        DawnRequestAbandonmentProbe? cancellationProbe = null)
        => RequestOwnedDevice(instance, adapter, requiredFeatures,
            out deviceLossState, out deviceLossStateHandle, cancellationProbe);

    private static void WaitForQueue(
        InstanceHandle instance,
        QueueHandle queue,
        DawnQueueWaitAbandonmentProbe? abandonmentProbe = null)
    {
        var state = new DawnQueueCompletion();
        if (abandonmentProbe != null) abandonmentProbe.Completion = state;
        try
        {
            var callback = new QueueWorkDoneCallbackInfoFFI
            {
                // Completion only publishes private status. Permit native
                // progress/shutdown to retire an abandoned wait without another
                // WaitAny call on its future. No source handler is invoked here.
                Mode = W.CallbackMode.AllowSpontaneous,
                Callback = &CompleteQueueWait,
                Userdata1 = (void*)state.BeginNativeUse()
            };
            W.Future future;
            try { future = queue.OnSubmittedWorkDone(callback); }
            catch { state.CancelUnqueuedNativeUse(); throw; }
            if (abandonmentProbe != null) throw abandonmentProbe.Failure;
            Wait(instance, future, "wait for submitted Dawn work");
            state.RequireSuccess();
        }
        finally { state.EndManagedUse(); }
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
    private static void CompleteQueueWait(
        W.QueueWorkDoneStatus status,
        StringViewFFI message,
        void* userData1,
        void* userData2)
    {
        try
        {
            var state = (DawnQueueCompletion)GCHandle.FromIntPtr((nint)userData1).Target!;
            string text = string.Empty;
            Exception? decodeFailure = null;
            try { text = Message(message); }
            catch (Exception error) { decodeFailure = error; }
            state.Complete(status, text, decodeFailure);
        }
        catch { } // No managed fault may cross the native callback ABI.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedError(
        DeviceHandle* device,
        W.ErrorType type,
        StringViewFFI message,
        void* userData1,
        void* userData2)
    {
        try
        {
            string errorMessage = CallbackMessage(message);
            // Publish terminal state before invoking application diagnostics.
            // A throwing loss subscriber must not suppress the error callback.
            try
            {
                if (userData1 != null &&
                    IsTerminalDeviceFailure(type, errorMessage) &&
                    GCHandle.FromIntPtr((nint)userData1).Target is DeviceLossCallbackState state)
                    state.Report(errorMessage);
            }
            catch (Exception error) { ReportCallbackFailure(error); }
            try { WgpuContext.RaiseWebGpuError(ErrorType(type), errorMessage); }
            catch (Exception error) { ReportCallbackFailure(error); }
            WriteCallbackDiagnostic($"[Dawn WebGPU Error] {type}: {errorMessage}");
        }
        catch (Exception error) { ReportCallbackFailure(error); }
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
        try
        {
            string lossMessage = CallbackMessage(message);
            try
            {
                if (userData1 != null &&
                    GCHandle.FromIntPtr((nint)userData1).Target is DeviceLossCallbackState state)
                    state.Report(lossMessage);
            }
            catch (Exception error) { ReportCallbackFailure(error); }
            WriteCallbackDiagnostic($"[Dawn Device Lost] {reason}: {lossMessage}");
        }
        catch (Exception error) { ReportCallbackFailure(error); }
    }

    private static string CallbackMessage(StringViewFFI message)
    {
        try { return Message(message); }
        catch (Exception error)
        {
            ReportCallbackFailure(error);
            return "Dawn native callback message could not be decoded.";
        }
    }

    private static void WriteCallbackDiagnostic(string message)
    {
        // Console.Error is caller-replaceable and may itself throw. Diagnostics
        // must never unwind across an unmanaged callback or prevent retirement.
        try { Console.Error.WriteLine(message); }
        catch { }
    }

    private static void ReportCallbackFailure(Exception error)
    {
        try { WriteCallbackDiagnostic($"[Dawn callback failure] {error}"); }
        catch { }
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

    private static bool IsTerminalDeviceFailure(
        W.ErrorType type,
        string message) =>
        type is W.ErrorType.Internal or W.ErrorType.Unknown ||
        message.Contains(
            "device is lost",
            StringComparison.OrdinalIgnoreCase) ||
        message.Contains(
            "device lost",
            StringComparison.OrdinalIgnoreCase) ||
        message.Contains(
            "Creation of a resource failed for a reason other than running out of memory",
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Package-neutral borrowed Dawn handles. The owning
/// <see cref="DawnGpuContext"/> must outlive every consumer.
/// </summary>
public readonly record struct DawnNativeDeviceHandles(
    nuint Instance,
    nuint Device,
    nuint Queue);

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
