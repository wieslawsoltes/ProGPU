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
        => CreateSystemWarpOffscreenCore(companionDirectory, null);

    private static DawnGpuContext CreateSystemWarpOffscreenCore(
        string? companionDirectory, SystemWarpCancellationProbe? cancellationProbe)
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
            adapter = RequestSystemWarpAdapter(instance, out DawnSystemWarpAdapterLuid luid, cancellationProbe);
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
                out DeviceLossCallbackState lossState, out lossHandle, cancellationProbe);
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
            // Request failures already drain their completed native event. If
            // draining failed, the pending request retained its loss userdata
            // and cleared this handle instead of claiming shutdown canceled it.
            if (lossHandle.IsAllocated) Cleanup(lossHandle.Free);
            throw;
        }
    }

    private static AdapterHandle RequestSystemWarpAdapter(
        InstanceHandle instance, out DawnSystemWarpAdapterLuid luid,
        SystemWarpCancellationProbe? cancellationProbe)
    {
        var state = new SystemWarpRequest<AdapterHandle>(static owned => owned.Release(), cancellationProbe?.Adapter);
        Exception? failure = null;
        W.Future queued = default;
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
            queued = new W.Future { Id = futureId };
            cancellationProbe?.BeforeWait(deviceRequest: false);
            Wait(instance, queued, "request the original system WARP LUID");
            AdapterHandle selected = state.TakeHandle((int)W.RequestAdapterStatus.Success,
                "request system WARP");
            luid = new(low, high);
            return selected;
        }
        catch (Exception error)
        {
            failure = error;
            DrainAbandonedSystemWarpRequest(instance, queued, state, error);
            throw;
        }
        finally { state.EndManagedUse(failure); }
    }

    private static DeviceHandle RequestSystemWarpDevice(InstanceHandle instance, AdapterHandle adapter,
        ReadOnlySpan<W.FeatureName> features, out DeviceLossCallbackState lossState, out GCHandle lossHandle,
        SystemWarpCancellationProbe? cancellationProbe)
    {
        lossState = new DeviceLossCallbackState();
        lossHandle = GCHandle.Alloc(lossState);
        var state = new SystemWarpRequest<DeviceHandle>(static owned =>
        {
            try { owned.Destroy(); } finally { owned.Release(); }
        }, cancellationProbe?.Device);
        Exception? failure = null;
        W.Future queued = default;
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
                queued = future;
                cancellationProbe?.BeforeWait(deviceRequest: true);
                Wait(instance, future, "request a Dawn device");
            }
            return state.TakeHandle((int)W.RequestDeviceStatus.Success, "request a Dawn device");
        }
        catch (Exception error)
        {
            failure = error;
            DrainAbandonedSystemWarpRequest(instance, queued, state, error);
            if (state.IsNativePending)
            {
                // A failed drain is not permission to free callback userdata.
                // The managed use still prevents retirement while ownership is
                // transferred, even if native completion races this catch.
                GCHandle retainedLoss = lossHandle;
                state.RetainUntilRetirement(retainedLoss.Free);
                lossHandle = default;
            }
            throw;
        }
        finally { state.EndManagedUse(failure); }
    }

    private static void DrainAbandonedSystemWarpRequest<T>(InstanceHandle instance,
        W.Future future, SystemWarpRequest<T> state, Exception failure) where T : unmanaged
    {
        if (!state.IsNativePending) return;
        try
        {
            if (future.Id == 0) throw new InvalidOperationException("The pending Dawn request has no drainable future.");
            // The pinned native adapter/device requests create Completed events.
            // Their owned results may retain the instance, so Release alone is
            // not cancellation. Deliver the actual callback with a zero-timeout
            // wait, then let the request owner retire any unpublished result.
            var wait = new W.FutureWaitInfo { Future = future };
            W.WaitStatus status = instance.WaitAny(1, &wait, 0);
            if (status != W.WaitStatus.Success || state.IsNativePending)
                throw new InvalidOperationException($"Could not retire abandoned Dawn request: {status}.");
        }
        catch (Exception cleanup)
        {
            try { failure.Data["DawnSystemWarpRequestDrainFailure"] = cleanup; } catch { }
        }
    }

    // A failed wait never ends native ownership. Drain actual completion first;
    // if draining fails, retain all pending callback userdata until completion.
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
        private readonly SystemWarpRequestReceipt? _receipt;
        private Action? _retirementCleanup;

        internal SystemWarpRequest(Action<T> release, SystemWarpRequestReceipt? receipt)
        {
            _release = release;
            _receipt = receipt;
            _self = GCHandle.Alloc(this);
        }
        internal bool IsNativePending { get { lock (_gate) return _nativePending; } }
        internal void RetainUntilRetirement(Action cleanup)
        {
            lock (_gate)
            {
                if (_managedEnded) throw new InvalidOperationException("Managed request ownership already ended.");
                _retirementCleanup = cleanup;
            }
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
                    _receipt?.Complete(status, _completionFailure);
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
                if (!EqualityComparer<T>.Default.Equals(_handle, default))
                {
                    _release(_handle);
                    _receipt?.ReleaseResult();
                }
            }
            catch (Exception error)
            {
                _receipt?.Fail(error);
                throw;
            }
            finally
            {
                try { _retirementCleanup?.Invoke(); }
                finally
                {
                    _self.Free();
                    _receipt?.Retire();
                }
            }
        }
    }

    // Friend-only conformance entry point. Inject failure only after a real
    // WaitAnyOnly request is queued; the normal factory never supplies a probe.
    // Native completion must drain and retire the unpublished result/GCHandle.
    internal static void VerifySystemWarpRequestCancellationForDiagnostics(bool deviceRequest)
    {
        var probe = new SystemWarpCancellationProbe(deviceRequest);
        try
        {
            using DawnGpuContext unexpected = CreateSystemWarpOffscreenCore(null, probe);
            throw new InvalidOperationException("The request cancellation injection was not reached.");
        }
        catch (Exception error) when (ReferenceEquals(error, probe.Failure))
        {
            if (error.Data.Count != 0)
                throw new InvalidOperationException("Native request shutdown reported a cleanup failure.", error);
        }
        if (deviceRequest)
        {
            probe.Adapter.Verify((int)W.RequestAdapterStatus.Success, releasedResults: 0);
            probe.Device.Verify((int)W.RequestDeviceStatus.Success, releasedResults: 1);
        }
        else probe.Adapter.Verify((int)W.RequestAdapterStatus.Success, releasedResults: 1);
    }

    private sealed class SystemWarpCancellationProbe(bool cancelDeviceRequest)
    {
        internal readonly Exception Failure = new InvalidOperationException("Abandon queued system WARP request before its wait.");
        internal readonly SystemWarpRequestReceipt Adapter = new();
        internal readonly SystemWarpRequestReceipt Device = new();
        internal void BeforeWait(bool deviceRequest)
        {
            if (deviceRequest == cancelDeviceRequest) throw Failure;
        }
    }

    private sealed class SystemWarpRequestReceipt
    {
        private int _callbacks, _retirements, _releasedResults, _status;
        private Exception? _failure;
        internal void Complete(int status, Exception? failure)
        {
            if (failure != null) Fail(failure);
            Volatile.Write(ref _status, status);
            Interlocked.Increment(ref _callbacks);
        }
        internal void Fail(Exception error) => Interlocked.CompareExchange(ref _failure, error, null);
        internal void Retire() => Interlocked.Increment(ref _retirements);
        internal void ReleaseResult() => Interlocked.Increment(ref _releasedResults);
        internal void Verify(int expectedStatus, int releasedResults)
        {
            if (Volatile.Read(ref _callbacks) != 1 || Volatile.Read(ref _retirements) != 1 ||
                Volatile.Read(ref _status) != expectedStatus || Volatile.Read(ref _failure) != null ||
                Volatile.Read(ref _releasedResults) != releasedResults)
                throw new InvalidOperationException($"Original request retirement mismatch: callbacks={_callbacks}, retirements={_retirements}, status={_status} (expected {expectedStatus}), releasedResults={_releasedResults} (expected {releasedResults}).", _failure);
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
