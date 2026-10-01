using System.Runtime.InteropServices;
using System.Threading;
using ProGPU.Backend;
using SW = Silk.NET.WebGPU;
using W = WebGpuSharp;
using WebGpuSharp.FFI;

namespace ProGPU.Backend.Dawn;

public sealed unsafe partial class DawnGpuContext
{
    /// <summary>
    /// Creates one owned Dawn device for offscreen rendering on the explicitly
    /// selected Metal, D3D12, or Vulkan backend.
    /// </summary>
    /// <remarks>
    /// No surface is created, and no presentation or shared-memory features are
    /// required. A requested fallback must actually select a CPU adapter;
    /// unsupported choices fail instead of selecting another backend/device.
    /// Selection occurs here, before the ordinary context receives the owned
    /// device. It never changes the selection policy of a borrowed device or
    /// either existing presentation factory. Targets and compositors must
    /// retire before this owner. Native queue completion/readback retains the
    /// existing Dawn context contract.
    /// </remarks>
    public static DawnGpuContext CreateOffscreen(
        SW.BackendType backendType,
        bool forceFallbackAdapter)
    {
        W.BackendType requestedBackend = backendType switch
        {
            SW.BackendType.Metal => W.BackendType.Metal,
            SW.BackendType.D3D12 => W.BackendType.D3D12,
            SW.BackendType.Vulkan => W.BackendType.Vulkan,
            _ => throw new ArgumentOutOfRangeException(
                nameof(backendType), backendType,
                "An explicit Metal, D3D12, or Vulkan backend is required.")
        };

        W.InstanceFeatureName timedWaitAny = W.InstanceFeatureName.TimedWaitAny;
        var descriptor = new InstanceDescriptorFFI
        {
            RequiredFeatureCount = 1,
            RequiredFeatures = &timedWaitAny
        };
        InstanceHandle instance = WebGPU_FFI.CreateInstance(&descriptor);
        if (instance == InstanceHandle.Null)
        {
            throw new InvalidOperationException("Could not create a Dawn instance.");
        }

        AdapterHandle adapter = AdapterHandle.Null;
        DeviceHandle device = DeviceHandle.Null;
        QueueHandle queue = QueueHandle.Null;
        GCHandle deviceLossStateHandle = default;
        WgpuContext? context = null;
        OffscreenDeviceLifetime? lifetime = null;
        try
        {
            adapter = RequestOffscreenAdapter(instance, requestedBackend, forceFallbackAdapter);
            string adapterName;
            string adapterDescription;
            SW.AdapterType adapterType;
            uint vendorId;
            uint deviceId;
            var information = new AdapterInfoFFI();
            try
            {
                // The cached WebGPUSharp 0.5.5 raw API returns Status; its
                // managed convenience overload does not check that status.
                if (adapter.GetInfo(&information) != W.Status.Success)
                {
                    throw new InvalidOperationException("Could not query Dawn adapter identity.");
                }
                if (information.BackendType != requestedBackend ||
                    (forceFallbackAdapter && information.AdapterType != W.AdapterType.CPU))
                {
                    throw new NotSupportedException(
                        "Dawn did not select the explicitly requested backend/fallback adapter.");
                }
                adapterType = information.AdapterType switch
                {
                    W.AdapterType.DiscreteGPU => SW.AdapterType.DiscreteGpu,
                    W.AdapterType.IntegratedGPU => SW.AdapterType.IntegratedGpu,
                    W.AdapterType.CPU => SW.AdapterType.Cpu,
                    W.AdapterType.Unknown => SW.AdapterType.Unknown,
                    _ => throw new NotSupportedException("Dawn returned an unknown adapter type.")
                };
                adapterName = Message(information.Device);
                adapterDescription = Message(information.Description);
                vendorId = information.VendorID;
                deviceId = information.DeviceID;
            }
            finally
            {
                WebGPU_FFI.AdapterInfoFreeMembers(information);
            }

            // Negotiate only actually supported ordinary device features, not
            // the presentation/shared-memory requirements of the other factories.
            Span<W.FeatureName> features = stackalloc W.FeatureName[2];
            int featureCount = 0;
            if (adapter.HasFeature(W.FeatureName.BGRA8UnormStorage))
            {
                features[featureCount++] = W.FeatureName.BGRA8UnormStorage;
            }
            bool supportsTextureFormatsTier1 = adapter.HasFeature(W.FeatureName.TextureFormatsTier1);
            if (supportsTextureFormatsTier1)
            {
                features[featureCount++] = W.FeatureName.TextureFormatsTier1;
            }
            device = RequestDevice(instance, adapter, features[..featureCount],
                out DeviceLossCallbackState deviceLossState, out deviceLossStateHandle);
            queue = device.GetQueue();
            if (queue == QueueHandle.Null)
            {
                throw new InvalidOperationException("Dawn did not return a default queue.");
            }
            var limits = new W.Limits();
            if (device.GetLimits(&limits) != W.Status.Success)
            {
                throw new InvalidOperationException("Could not query Dawn device limits.");
            }

            // Raw locals remain the sole releasing owner until BOTH managed
            // lifetime allocations succeed. Thereafter this idempotent wrapper
            // owns the whole chain, even if context initialization is partial.
            lifetime = new OffscreenDeviceLifetime(new NativeLifetime(
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

            context = new WgpuContext
            {
                ComputeLimits = new(limits.MaxStorageBufferBindingSize,
                    limits.MaxStorageBuffersPerShaderStage, limits.MaxComputeInvocationsPerWorkgroup,
                    limits.MaxComputeWorkgroupSizeX, limits.MaxComputeWorkgroupsPerDimension)
            };
            var api = new DawnWebGpuApi();
            // Allocate the public owner/importer before initialization publishes
            // this context. Its constructor does not execute/import GPU work.
            var result = new DawnGpuContext(context, ownedInstance, ownedAdapter, ownedDevice, ownedQueue);
            deviceLossState.Bind(context);
            if (context.IsDeviceLost)
            {
                throw new InvalidOperationException("The requested Dawn device was lost during creation.");
            }
            context.InitializeExternalNativeDevice(api, lifetime,
                (SW.Device*)ownedDevice.GetAddress(), (SW.Queue*)ownedQueue.GetAddress(),
                SW.TextureFormat.Rgba8Unorm,
                maxSampledTexturesPerShaderStage: limits.MaxSampledTexturesPerShaderStage,
                maxSamplersPerShaderStage: limits.MaxSamplersPerShaderStage,
                maxBindGroups: limits.MaxBindGroups,
                supportsTextureFormatsTier1: supportsTextureFormatsTier1,
                adapterBackendType: backendType,
                adapterName: adapterName,
                adapterType: adapterType,
                adapterDriverDescription: adapterDescription,
                adapterVendorId: vendorId,
                adapterDeviceId: deviceId,
                maxBufferSize: limits.MaxBufferSize);
            if (context.IsDeviceLost)
            {
                throw new InvalidOperationException("The requested Dawn device was lost during initialization.");
            }
            // Initialization owns lifetime now; no throwing setup remains.
            lifetime = null;
            context = null;
            return result;
        }
        catch
        {
            // Preserve the originating failure. A partially initialized context
            // may already release lifetime, or may throw before doing so; the
            // wrapper is an independently safe final releasing owner either way.
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

    private static AdapterHandle RequestOffscreenAdapter(
        InstanceHandle instance, W.BackendType backendType, bool forceFallbackAdapter)
    {
        var state = new AdapterRequest();
        GCHandle stateHandle = GCHandle.Alloc(state);
        try
        {
            var options = new RequestAdapterOptionsFFI
            {
                BackendType = backendType,
                ForceFallbackAdapter = forceFallbackAdapter,
                PowerPreference = W.PowerPreference.HighPerformance
            };
            var callback = new RequestAdapterCallbackInfoFFI
            {
                Mode = W.CallbackMode.WaitAnyOnly,
                Callback = &CompleteAdapterRequest,
                Userdata1 = (void*)GCHandle.ToIntPtr(stateHandle)
            };
            W.Future future = instance.RequestAdapter(&options, callback);
            Wait(instance, future, $"request an offscreen {backendType} adapter");
            if (state.Status != W.RequestAdapterStatus.Success || state.Adapter == AdapterHandle.Null)
            {
                var failure = new InvalidOperationException(
                    $"Dawn failed to request an offscreen adapter: {state.Status}. {state.Message}");
                DawnAdapterRequestFailureDiagnostics.Attach(failure, options.BackendType,
                    forceFallbackAdapter, options.FeatureLevel, options.PowerPreference);
                throw failure;
            }
            AdapterHandle result = state.Adapter;
            state.Adapter = AdapterHandle.Null;
            return result;
        }
        finally
        {
            try
            {
                if (state.Adapter != AdapterHandle.Null) state.Adapter.Release();
            }
            finally
            {
                stateHandle.Free();
            }
        }
    }

    // InitializeExternalNativeDevice installs its lifetime after several fields
    // and before further allocations. Its exception path therefore cannot tell
    // the factory whether ownership moved. This wrapper makes both possible
    // cleanup paths safe without changing that existing initialization API.
    private sealed class OffscreenDeviceLifetime(NativeLifetime lifetime) : IWebGpuExternalDeviceLifetime
    {
        private NativeLifetime? _lifetime = lifetime;

        public void Poll(bool wait)
        {
            NativeLifetime? active = Volatile.Read(ref _lifetime);
            ObjectDisposedException.ThrowIf(active is null, this);
            active.Poll(wait);
        }

        public void Dispose() => Interlocked.Exchange(ref _lifetime, null)?.Dispose();
    }
}
