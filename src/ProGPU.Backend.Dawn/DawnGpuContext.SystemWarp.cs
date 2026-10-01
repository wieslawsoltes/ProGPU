using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Backend;
using SW = Silk.NET.WebGPU;
using W = WebGpuSharp;
using WebGpuSharp.FFI;

namespace ProGPU.Backend.Dawn;

/// <summary>The actual Microsoft WARP adapter LUID, verified against the selected Dawn adapter.</summary>
public readonly record struct DawnSystemWarpAdapterLuid(uint LowPart, int HighPart);

public sealed unsafe partial class DawnGpuContext
{
    public DawnSystemWarpAdapterLuid? SystemWarpAdapterLuid { get; private init; }

    /// <summary>Validates and pins the optional original-header Windows companion, without creating a device.</summary>
    /// <remarks>Startup-only hash/PE inspection and native loading; this does not qualify WARP rendering.</remarks>
    public static bool IsSystemWarpNativeLibraryAvailable(string? companionDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { DawnSystemWarpArtifact.EnsureAvailable(companionDirectory); return true; }
        catch { return false; }
    }

    /// <summary>Creates an explicit D3D12 offscreen device on Microsoft's actual EnumWarpAdapter LUID.</summary>
    /// <remarks>
    /// This optional Windows-only policy does not alter generic fallback, automatic
    /// selection, compiler choices or existing factories. It fails without a matching
    /// original provider/companion or D3D12/CPU/software/exact-LUID verification.
    /// Startup performs one adapter request and one device request. No hardware retry.
    /// </remarks>
    public static DawnGpuContext CreateSystemWarpOffscreen(string? companionDirectory = null)
    {
        DawnSystemWarpArtifact.EnsureAvailable(companionDirectory);
        W.InstanceFeatureName timedWaitAny = W.InstanceFeatureName.TimedWaitAny;
        var descriptor = new InstanceDescriptorFFI
        {
            RequiredFeatureCount = 1, RequiredFeatures = &timedWaitAny
        };
        InstanceHandle instance = WebGPU_FFI.CreateInstance(&descriptor);
        if (instance == InstanceHandle.Null)
            throw new InvalidOperationException("Could not create the original Dawn instance.");
        AdapterHandle adapter = AdapterHandle.Null;
        DeviceHandle device = DeviceHandle.Null;
        QueueHandle queue = QueueHandle.Null;
        GCHandle lossHandle = default;
        WgpuContext? context = null;
        NativeLifetime? transferredLifetime = null;
        try
        {
            adapter = RequestSystemWarpAdapter(instance, out DawnSystemWarpAdapterLuid luid);
            Span<byte> error = stackalloc byte[256];
            fixed (byte* message = error)
            {
                int status = DawnSystemWarpNative.VerifyAdapter(adapter.GetAddress(),
                    luid.LowPart, luid.HighPart, message, (uint)error.Length);
                ThrowSystemWarpFailure(status, error, "verify the selected original WARP adapter");
            }
            string name, driver;
            uint vendorId, deviceId;
            var info = new AdapterInfoFFI();
            try
            {
                if (adapter.GetInfo(&info) != W.Status.Success ||
                    info.BackendType != W.BackendType.D3D12 || info.AdapterType != W.AdapterType.CPU)
                    throw new NotSupportedException("The actual selected Dawn adapter is not D3D12/CPU.");
                name = Message(info.Device);
                driver = Message(info.Description);
                vendorId = info.VendorID;
                deviceId = info.DeviceID;
            }
            finally { WebGPU_FFI.AdapterInfoFreeMembers(info); }

            Span<W.FeatureName> features = stackalloc W.FeatureName[1];
            bool formatsTier1 = adapter.HasFeature(W.FeatureName.TextureFormatsTier1);
            int featureCount = 0;
            if (formatsTier1) features[featureCount++] = W.FeatureName.TextureFormatsTier1;
            device = RequestSystemWarpDevice(instance, adapter, features[..featureCount],
                out DeviceLossCallbackState lossState, out lossHandle);
            queue = device.GetQueue();
            if (queue == QueueHandle.Null)
                throw new InvalidOperationException("The original Dawn device returned no queue.");
            var limits = new W.Limits();
            if (device.GetLimits(&limits) != W.Status.Success)
                throw new InvalidOperationException("Could not query original Dawn device limits.");

            InstanceHandle ownedInstance = instance;
            AdapterHandle ownedAdapter = adapter;
            DeviceHandle ownedDevice = device;
            QueueHandle ownedQueue = queue;
            transferredLifetime = new NativeLifetime(instance, adapter, device, queue, lossHandle);
            instance = InstanceHandle.Null;
            adapter = AdapterHandle.Null;
            device = DeviceHandle.Null;
            queue = QueueHandle.Null;
            lossHandle = default;
            context = new WgpuContext
            {
                ComputeLimits = new(limits.MaxStorageBufferBindingSize,
                    limits.MaxStorageBuffersPerShaderStage, limits.MaxComputeInvocationsPerWorkgroup,
                    limits.MaxComputeWorkgroupSizeX, limits.MaxComputeWorkgroupsPerDimension)
            };
            context.InitializeExternalNativeDevice(new DawnWebGpuApi(), transferredLifetime,
                (SW.Device*)ownedDevice.GetAddress(), (SW.Queue*)ownedQueue.GetAddress(),
                SW.TextureFormat.Rgba8Unorm,
                maxSampledTexturesPerShaderStage: limits.MaxSampledTexturesPerShaderStage,
                maxSamplersPerShaderStage: limits.MaxSamplersPerShaderStage,
                maxBindGroups: limits.MaxBindGroups,
                maxBufferSize: limits.MaxBufferSize,
                supportsTextureFormatsTier1: formatsTier1,
                adapterBackendType: SW.BackendType.D3D12, adapterName: name,
                adapterType: SW.AdapterType.Cpu, adapterDriverDescription: driver,
                adapterVendorId: vendorId, adapterDeviceId: deviceId);
            lossState.Bind(context);
            return new DawnGpuContext(context, ownedInstance, ownedAdapter, ownedDevice, ownedQueue)
            {
                SystemWarpAdapterLuid = luid
            };
        }
        catch (Exception failure)
        {
            // Preserve setup failure while draining every acquired owner. The same
            // idempotent lifetime covers early and late external-context setup.
            void Cleanup(Action cleanup)
            {
                try { cleanup(); }
                catch (Exception error)
                {
                    try { failure.Data["DawnSystemWarpCleanupFailure"] = error; } catch { }
                }
            }
            if (context != null) Cleanup(context.Dispose);
            if (transferredLifetime != null) Cleanup(transferredLifetime.Dispose);
            if (queue != QueueHandle.Null) Cleanup(queue.Release);
            if (device != DeviceHandle.Null) { Cleanup(device.Destroy); Cleanup(device.Release); }
            if (adapter != AdapterHandle.Null) Cleanup(adapter.Release);
            if (instance != InstanceHandle.Null) Cleanup(instance.Release);
            // Instance shutdown cancels pending request/loss callbacks before
            // their userdata is released, including a failed device wait.
            if (lossHandle.IsAllocated) Cleanup(lossHandle.Free);
            throw;
        }
    }

    private static AdapterHandle RequestSystemWarpAdapter(
        InstanceHandle instance, out DawnSystemWarpAdapterLuid luid)
    {
        var state = new SystemWarpRequest<AdapterHandle>(static owned => owned.Release());
        Exception? failure = null;
        try
        {
            var callback = new RequestAdapterCallbackInfoFFI
            {
                Mode = W.CallbackMode.WaitAnyOnly, Callback = &CompleteSystemWarpAdapterRequest,
                Userdata1 = (void*)state.BeginNativeUse()
            };
            Span<byte> error = stackalloc byte[256];
            ulong futureId;
            uint low;
            int high;
            int status;
            try
            {
                fixed (byte* message = error)
                    status = DawnSystemWarpNative.RequestAdapter(instance.GetAddress(), &callback,
                        out futureId, out low, out high, message, (uint)error.Length);
            }
            catch { state.CancelUnqueuedNativeUse(); throw; }
            // The companion's negative result is strictly pre-request setup.
            // Once queued, only completion/cancellation releases native userdata.
            if (status < 0) state.CancelUnqueuedNativeUse();
            ThrowSystemWarpFailure(status, error, "request the original system WARP LUID");
            Wait(instance, new W.Future { Id = futureId }, "request the original system WARP LUID");
            AdapterHandle selected = state.TakeHandle((int)W.RequestAdapterStatus.Success,
                "request system WARP");
            luid = new(low, high);
            return selected;
        }
        catch (Exception error) { failure = error; throw; }
        finally { state.EndManagedUse(failure); }
    }

    private static DeviceHandle RequestSystemWarpDevice(InstanceHandle instance, AdapterHandle adapter,
        ReadOnlySpan<W.FeatureName> features, out DeviceLossCallbackState lossState, out GCHandle lossHandle)
    {
        lossState = new DeviceLossCallbackState();
        lossHandle = GCHandle.Alloc(lossState);
        var state = new SystemWarpRequest<DeviceHandle>(static owned =>
        {
            try { owned.Destroy(); } finally { owned.Release(); }
        });
        Exception? failure = null;
        try
        {
            fixed (W.FeatureName* required = features)
            fixed (byte* label = "ProGPU Dawn Primary Device\0"u8)
            {
                var descriptor = new DeviceDescriptorFFI
                {
                    Label = StringViewFFI.CreateNullTerminated(label),
                    RequiredFeatureCount = (nuint)features.Length, RequiredFeatures = required,
                    DeviceLostCallbackInfo = new DeviceLostCallbackInfoFFI
                    {
                        Mode = W.CallbackMode.AllowSpontaneous, Callback = &OnDeviceLost,
                        Userdata1 = (void*)GCHandle.ToIntPtr(lossHandle)
                    },
                    UncapturedErrorCallbackInfo = new UncapturedErrorCallbackInfoFFI
                    {
                        Callback = &OnUncapturedError, Userdata1 = (void*)GCHandle.ToIntPtr(lossHandle)
                    }
                };
                var callback = new RequestDeviceCallbackInfoFFI
                {
                    Mode = W.CallbackMode.WaitAnyOnly, Callback = &CompleteSystemWarpDeviceRequest,
                    Userdata1 = (void*)state.BeginNativeUse()
                };
                W.Future future;
                try { future = adapter.RequestDevice(&descriptor, callback); }
                catch { state.CancelUnqueuedNativeUse(); throw; }
                Wait(instance, future, "request a Dawn device");
            }
            return state.TakeHandle((int)W.RequestDeviceStatus.Success, "request a Dawn device");
        }
        catch (Exception error) { failure = error; throw; }
        finally { state.EndManagedUse(failure); }
    }

    // Explicit acquisition only. A failed WaitAny does NOT end the native use:
    // instance shutdown delivers the original CallbackCancelled before retirement.
    // The creating factory separately retains device-loss userdata until complete
    // instance shutdown, including cancellation of a failed device request.
    private sealed class SystemWarpRequest<T> where T : unmanaged
    {
        private readonly object _gate = new();
        private readonly Action<T> _release;
        private GCHandle _self;
        private int _uses = 1, _status;
        private bool _nativePending, _managedEnded, _completed;
        private T _handle;
        private string _message = string.Empty;
        private Exception? _completionFailure;

        internal SystemWarpRequest(Action<T> release)
        {
            _release = release;
            _self = GCHandle.Alloc(this);
        }
        internal nint BeginNativeUse()
        {
            lock (_gate)
            {
                if (_nativePending || _managedEnded) throw new InvalidOperationException("Request ownership already ended.");
                _nativePending = true;
                _uses++;
                return GCHandle.ToIntPtr(_self);
            }
        }
        internal void Complete(int status, T handle, StringViewFFI message)
        {
            try
            {
                lock (_gate)
                {
                    _status = status;
                    _handle = handle;
                    try { _message = Message(message); }
                    catch (Exception error) { _completionFailure = error; }
                    _completed = true;
                }
            }
            finally { EndNativeUse(); }
        }
        internal T TakeHandle(int success, string operation)
        {
            lock (_gate)
            {
                if (!_completed || _status != success || EqualityComparer<T>.Default.Equals(_handle, default))
                    throw new InvalidOperationException($"Dawn failed to {operation}: {_status}. {_message}", _completionFailure);
                if (_completionFailure != null) throw new InvalidOperationException("Could not read the original Dawn request result.", _completionFailure);
                T selected = _handle;
                _handle = default;
                return selected;
            }
        }
        internal void CancelUnqueuedNativeUse() => EndNativeUse();
        private void EndNativeUse()
        {
            bool retire;
            lock (_gate)
            {
                if (!_nativePending) return;
                _nativePending = false;
                retire = --_uses == 0;
            }
            if (retire) Retire();
        }
        internal void EndManagedUse(Exception? failure)
        {
            bool retire;
            lock (_gate)
            {
                if (_managedEnded) return;
                _managedEnded = true;
                retire = --_uses == 0;
            }
            if (!retire) return;
            try { Retire(); }
            catch (Exception cleanup) when (failure != null)
            {
                try { failure.Data["DawnSystemWarpRequestCleanupFailure"] = cleanup; } catch { }
            }
        }
        private void Retire()
        {
            try
            {
                if (!EqualityComparer<T>.Default.Equals(_handle, default)) _release(_handle);
            }
            finally { _self.Free(); }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompleteSystemWarpAdapterRequest(W.RequestAdapterStatus status,
        AdapterHandle adapter, StringViewFFI message, void* userdata1, void* userdata2)
    {
        // Original callback userdata is retained through this call. Native release
        // imports are void; no managed exception may escape this ABI boundary.
        try { ((SystemWarpRequest<AdapterHandle>)GCHandle.FromIntPtr((nint)userdata1).Target!).Complete((int)status, adapter, message); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompleteSystemWarpDeviceRequest(W.RequestDeviceStatus status,
        DeviceHandle device, StringViewFFI message, void* userdata1, void* userdata2)
    {
        try { ((SystemWarpRequest<DeviceHandle>)GCHandle.FromIntPtr((nint)userdata1).Target!).Complete((int)status, device, message); }
        catch { }
    }

    private static void ThrowSystemWarpFailure(int status, ReadOnlySpan<byte> error, string operation)
    {
        if (status >= 0) return;
        int end = error.IndexOf((byte)0);
        throw new NotSupportedException($"Dawn could not {operation}: " +
            Encoding.UTF8.GetString(end < 0 ? error : error[..end]));
    }
}

internal static unsafe partial class DawnSystemWarpNative
{
    internal const string LibraryName = "progpu_dawn_system_warp";
    [LibraryImport(LibraryName, EntryPoint = "progpu_dawn_system_warp_library_identity")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int LibraryIdentity(out nint companion, out nint provider);
    [LibraryImport(LibraryName, EntryPoint = "progpu_dawn_system_warp_request_adapter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RequestAdapter(nuint instance, RequestAdapterCallbackInfoFFI* callback,
        out ulong futureId, out uint low, out int high, byte* error, uint capacity);
    [LibraryImport(LibraryName, EntryPoint = "progpu_dawn_system_warp_verify_adapter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int VerifyAdapter(nuint adapter, uint low, int high, byte* error, uint capacity);
}
