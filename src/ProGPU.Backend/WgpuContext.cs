using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

public enum ShaderModuleVerificationStatus
{
    Verified,
    Unavailable,
    Invalid
}

public enum WgpuBackendKind
{
    SilkNative,
    BrowserWebGpu
}

public unsafe class WgpuContext : IDisposable
{
    private SharedDeviceLifetime? _sharedDeviceLifetime;
    private GpuFrameCompletionTracker? _frameCompletionTracker;
    private GpuTimestampRing? _gpuTimestampRing;
    public WebGPU Wgpu { get; private set; } = null!;
    public IWebGpuApi Api { get; private set; } = null!;
    public WgpuBackendKind BackendKind { get; private set; } = WgpuBackendKind.SilkNative;
    public Instance* Instance { get; private set; } = null;
    public Adapter* Adapter { get; private set; } = null;
    public Device* Device { get; private set; } = null;
    public Queue* Queue { get; private set; } = null;
    public Surface* Surface { get; private set; } = null;
    public TextureFormat SwapChainFormat { get; private set; } = TextureFormat.Bgra8Unorm;
    public uint MaxSampledTexturesPerShaderStage { get; private set; } = 16;
    public uint MaxSamplersPerShaderStage { get; private set; } = 16;
    public uint MaxBindGroups { get; private set; } = 4;
    public bool SupportsReadOnlyAndReadWriteStorageTextures { get; private set; }
    public bool SupportsTimestampQueries { get; private set; }

    /// <summary>
    /// Enables queue-completion callbacks for compositor frame submissions. This is intentionally
    /// opt-in because a callback per frame is diagnostic work and is not part of normal rendering.
    /// </summary>
    public bool EnableFrameCompletionTracking
    {
        get => Volatile.Read(ref _frameCompletionTracker) != null;
        set
        {
            if (value)
            {
                if (Volatile.Read(ref _frameCompletionTracker) == null)
                {
                    var tracker = new GpuFrameCompletionTracker();
                    if (Interlocked.CompareExchange(ref _frameCompletionTracker, tracker, null) != null)
                    {
                        tracker.Dispose();
                    }
                }
            }
            else
            {
                Interlocked.Exchange(ref _frameCompletionTracker, null)?.Dispose();
            }
        }
    }

    public GpuFrameCompletionMetrics FrameCompletionMetrics =>
        Volatile.Read(ref _frameCompletionTracker)?.Metrics ?? default;

    /// <summary>
    /// Enables non-blocking timestamp pairs around the main compositor command buffer. The
    /// setting remains false when the adapter did not expose the timestamp-query feature.
    /// </summary>
    public bool EnableGpuTimestampTracking
    {
        get => _gpuTimestampRing != null;
        set
        {
            if (value && SupportsTimestampQueries)
            {
                _gpuTimestampRing ??= new GpuTimestampRing(this);
            }
            else if (!value)
            {
                Interlocked.Exchange(ref _gpuTimestampRing, null)?.Dispose();
            }
        }
    }

    public GpuTimestampMetrics GpuTimestampMetrics => _gpuTimestampRing?.Metrics ?? default;

    public bool BeginFrameGpuTimestamp(CommandEncoder* encoder) =>
        _gpuTimestampRing?.BeginFrame(encoder) == true;

    public void EndFrameGpuTimestamp(CommandEncoder* encoder) =>
        _gpuTimestampRing?.EndFrame(encoder);

    /// <summary>
    /// Records the main compositor submission. Auxiliary uploads and offscreen utility submits do
    /// not count as completed frames.
    /// </summary>
    public void NotifyFrameSubmitted()
    {
        _gpuTimestampRing?.NotifySubmitted();
        Volatile.Read(ref _frameCompletionTracker)?.RecordSubmission(Api, Queue);
    }

    public static event Action<ErrorType, string>? OnWebGpuError;

    public static void RaiseWebGpuError(ErrorType type, string message)
    {
        OnWebGpuError?.Invoke(type, message);
    }

    private PfnErrorCallback _errorCallback;

    public readonly object RenderLock = new();
    public readonly object DisposalLock = new();
    public readonly List<IntPtr> PendingBuffers = new();
    public readonly List<IntPtr> PendingTextures = new();
    public readonly List<IntPtr> PendingTextureViews = new();
    public readonly List<IntPtr> PendingBindGroups = new();
    public readonly List<IntPtr> PendingBindGroupLayouts = new();
    public readonly List<IntPtr> PendingPipelineLayouts = new();
    public readonly List<IntPtr> PendingRenderPipelines = new();
    public readonly List<IntPtr> PendingComputePipelines = new();
    public readonly List<IntPtr> PendingSamplers = new();
    public readonly List<IntPtr> PendingShaderModules = new();
    private readonly HashSet<IntPtr> _pendingSnapshotSeen = new();

    public void QueueBufferDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingBuffers.Add(ptr);
        }
    }

    public void QueueTextureDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingTextures.Add(ptr);
        }
    }

    public void QueueTextureViewDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingTextureViews.Add(ptr);
        }
    }

    public void QueueBindGroupDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingBindGroups.Add(ptr);
        }
    }

    public void QueueBindGroupLayoutDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingBindGroupLayouts.Add(ptr);
        }
    }

    public void QueuePipelineLayoutDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingPipelineLayouts.Add(ptr);
        }
    }

    public void QueueRenderPipelineDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingRenderPipelines.Add(ptr);
        }
    }

    public void QueueComputePipelineDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingComputePipelines.Add(ptr);
        }
    }

    public void QueueSamplerDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingSamplers.Add(ptr);
        }
    }

    public void QueueShaderModuleDisposal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        lock (DisposalLock)
        {
            PendingShaderModules.Add(ptr);
        }
    }

    public void CleanupPendingResources()
    {
        if (_isDisposed) return;

        lock (RenderLock)
        {
            if (_isDisposed) return;

            PooledResourcePointerSnapshot buffers = default;
            PooledResourcePointerSnapshot textures = default;
            PooledResourcePointerSnapshot views = default;
            PooledResourcePointerSnapshot bindGroups = default;
            PooledResourcePointerSnapshot layouts = default;
            PooledResourcePointerSnapshot pipeLayouts = default;
            PooledResourcePointerSnapshot renderPipes = default;
            PooledResourcePointerSnapshot computePipes = default;
            PooledResourcePointerSnapshot samplers = default;
            PooledResourcePointerSnapshot shaders = default;

            try
            {
                lock (DisposalLock)
                {
                    buffers = SnapshotPendingResourcePointers(PendingBuffers);
                    PendingBuffers.Clear();

                    textures = SnapshotPendingResourcePointers(PendingTextures);
                    PendingTextures.Clear();

                    views = SnapshotPendingResourcePointers(PendingTextureViews);
                    PendingTextureViews.Clear();

                    bindGroups = SnapshotPendingResourcePointers(PendingBindGroups);
                    PendingBindGroups.Clear();

                    layouts = SnapshotPendingResourcePointers(PendingBindGroupLayouts);
                    PendingBindGroupLayouts.Clear();

                    pipeLayouts = SnapshotPendingResourcePointers(PendingPipelineLayouts);
                    PendingPipelineLayouts.Clear();

                    renderPipes = SnapshotPendingResourcePointers(PendingRenderPipelines);
                    PendingRenderPipelines.Clear();

                    computePipes = SnapshotPendingResourcePointers(PendingComputePipelines);
                    PendingComputePipelines.Clear();

                    samplers = SnapshotPendingResourcePointers(PendingSamplers);
                    PendingSamplers.Clear();

                    shaders = SnapshotPendingResourcePointers(PendingShaderModules);
                    PendingShaderModules.Clear();
                }

                if (views.Length > 0 || textures.Length > 0 || buffers.Length > 0 || bindGroups.Length > 0 ||
                    layouts.Length > 0 || pipeLayouts.Length > 0 || renderPipes.Length > 0 ||
                    computePipes.Length > 0 || samplers.Length > 0 || shaders.Length > 0)
                {
                    WaitIdle();
                }

                ReleaseBindGroups(bindGroups.Span);
                ReleaseTextureViews(views.Span);
                ReleaseTextures(textures.Span);
                ReleaseBuffers(buffers.Span);
                ReleaseBindGroupLayouts(layouts.Span);
                ReleasePipelineLayouts(pipeLayouts.Span);
                ReleaseRenderPipelines(renderPipes.Span);
                ReleaseComputePipelines(computePipes.Span);
                ReleaseSamplers(samplers.Span);
                ReleaseShaderModules(shaders.Span);
            }
            finally
            {
                buffers.Dispose();
                textures.Dispose();
                views.Dispose();
                bindGroups.Dispose();
                layouts.Dispose();
                pipeLayouts.Dispose();
                renderPipes.Dispose();
                computePipes.Dispose();
                samplers.Dispose();
                shaders.Dispose();
            }
        }
    }

    private PooledResourcePointerSnapshot SnapshotPendingResourcePointers(List<IntPtr> pending)
    {
        var pendingCount = pending.Count;
        if (pendingCount == 0)
        {
            return default;
        }

        var snapshot = ArrayPool<IntPtr>.Shared.Rent(pendingCount);
        var count = 0;
        _pendingSnapshotSeen.Clear();
        for (var pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
        {
            var ptr = pending[pendingIndex];
            if (ptr != IntPtr.Zero && _pendingSnapshotSeen.Add(ptr))
            {
                snapshot[count++] = ptr;
            }
        }

        _pendingSnapshotSeen.Clear();
        if (count == 0)
        {
            ArrayPool<IntPtr>.Shared.Return(snapshot);
            return default;
        }

        return new PooledResourcePointerSnapshot(snapshot, count);
    }

    private void ReleaseBindGroups(ReadOnlySpan<IntPtr> bindGroups)
    {
        for (var index = 0; index < bindGroups.Length; index++)
        {
            Api.BindGroupRelease((BindGroup*)bindGroups[index]);
        }
    }

    private void ReleaseTextureViews(ReadOnlySpan<IntPtr> views)
    {
        for (var index = 0; index < views.Length; index++)
        {
            Api.TextureViewRelease((TextureView*)views[index]);
        }
    }

    private void ReleaseTextures(ReadOnlySpan<IntPtr> textures)
    {
        for (var index = 0; index < textures.Length; index++)
        {
            // Release ownership without destroying; bind groups/views may still keep
            // the texture alive until the backend has drained all references.
            Api.TextureRelease((Texture*)textures[index]);
        }
    }

    private void ReleaseBuffers(ReadOnlySpan<IntPtr> buffers)
    {
        for (var index = 0; index < buffers.Length; index++)
        {
            var buffer = (Silk.NET.WebGPU.Buffer*)buffers[index];
            Api.BufferDestroy(buffer);
            Api.BufferRelease(buffer);
        }
    }

    private void ReleaseBindGroupLayouts(ReadOnlySpan<IntPtr> layouts)
    {
        for (var index = 0; index < layouts.Length; index++)
        {
            Api.BindGroupLayoutRelease((BindGroupLayout*)layouts[index]);
        }
    }

    private void ReleasePipelineLayouts(ReadOnlySpan<IntPtr> pipeLayouts)
    {
        for (var index = 0; index < pipeLayouts.Length; index++)
        {
            Api.PipelineLayoutRelease((PipelineLayout*)pipeLayouts[index]);
        }
    }

    private void ReleaseRenderPipelines(ReadOnlySpan<IntPtr> renderPipelines)
    {
        for (var index = 0; index < renderPipelines.Length; index++)
        {
            Api.RenderPipelineRelease((RenderPipeline*)renderPipelines[index]);
        }
    }

    private void ReleaseComputePipelines(ReadOnlySpan<IntPtr> computePipelines)
    {
        for (var index = 0; index < computePipelines.Length; index++)
        {
            Api.ComputePipelineRelease((ComputePipeline*)computePipelines[index]);
        }
    }

    private void ReleaseSamplers(ReadOnlySpan<IntPtr> samplers)
    {
        for (var index = 0; index < samplers.Length; index++)
        {
            Api.SamplerRelease((Sampler*)samplers[index]);
        }
    }

    private void ReleaseShaderModules(ReadOnlySpan<IntPtr> shaders)
    {
        for (var index = 0; index < shaders.Length; index++)
        {
            Api.ShaderModuleRelease((ShaderModule*)shaders[index]);
        }
    }

    private readonly struct PooledResourcePointerSnapshot(IntPtr[]? buffer, int length) : IDisposable
    {
        public int Length => length;

        public ReadOnlySpan<IntPtr> Span => buffer is null
            ? ReadOnlySpan<IntPtr>.Empty
            : buffer.AsSpan(0, length);

        public void Dispose()
        {
            if (buffer is not null)
            {
                ArrayPool<IntPtr>.Shared.Return(buffer);
            }
        }
    }
    
    private bool _isDisposed;
    public bool IsDisposed => _isDisposed;
    public bool IsInitialized =>
        !_isDisposed &&
        Api != null &&
        Device != null &&
        Queue != null &&
        (BackendKind == WgpuBackendKind.BrowserWebGpu
            ? Surface != null
            : Wgpu != null && Instance != null && Adapter != null);
    private uint _lastWidth = 1;
    private uint _lastHeight = 1;
    private bool _isSurfaceConfigured;
    private bool _vsync = false;

    public bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync != value)
            {
                _vsync = value;
                if (Surface != null)
                {
                    ConfigureSwapChain(_lastWidth, _lastHeight);
                }
            }
        }
    }

    private static readonly List<WgpuContext> _activeContexts = new();

    public static event Action<WgpuContext>? Disposing;

    public static IReadOnlyList<WgpuContext> ActiveContexts
    {
        get
        {
            lock (_activeContexts)
            {
                return _activeContexts.ToArray();
            }
        }
    }

    public static bool TryGetFirstActiveContext([NotNullWhen(true)] out WgpuContext? context)
    {
        lock (_activeContexts)
        {
            for (var i = 0; i < _activeContexts.Count; i++)
            {
                var active = _activeContexts[i];
                if (active.IsInitialized)
                {
                    context = active;
                    return true;
                }
            }
        }

        context = null;
        return false;
    }

    [ThreadStatic]
    private static WgpuContext? _current;

    public static WgpuContext? Current
    {
        get => _current;
        set => _current = value;
    }

    public static CurrentContextScope PushCurrent(WgpuContext? context)
    {
        return new CurrentContextScope(context);
    }

    public readonly struct CurrentContextScope : IDisposable
    {
        private readonly WgpuContext? _previous;

        internal CurrentContextScope(WgpuContext? context)
        {
            _previous = Current;
            Current = context;
        }

        public void Dispose()
        {
            Current = _previous;
        }
    }

    private IWindow? _window;
    public IWindow? Window => _window;



    public void Initialize(IWindow? window)
    {
        string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProGPU_test_run.log");
        void SafeLog(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(logPath, msg);
            }
            catch
            {
                // Ignore log failures
            }
        }

        SafeLog($"[WGPUCONTEXT] Initialize started, window exists={window != null}\n");
        _window = window;
        Wgpu = WebGPU.GetApi();
        Api = new SilkWebGpuApi(Wgpu);
        
        // 1. Create WebGPU Instance (isolated per context)
        SafeLog("[WGPUCONTEXT] Creating WebGPU Instance\n");
        var instanceDesc = new InstanceDescriptor();
        Instance = Wgpu.CreateInstance(&instanceDesc);
        if (Instance == null)
        {
            throw new InvalidOperationException("Failed to create WebGPU Instance.");
        }

        // 2. Create Surface if window is provided
        if (window != null)
        {
            if (!CanCreateNativeSurface(window))
            {
                throw new InvalidOperationException("Cannot create a WebGPU surface before the native window source is loaded.");
            }

            SafeLog("[WGPUCONTEXT] Creating WebGPU Surface from window\n");
            Surface = window.CreateWebGPUSurface(Wgpu, Instance);
            SafeLog($"[WGPUCONTEXT] CreateWebGPUSurface returned Surface={(nint)Surface:X}\n");
            if (Surface == null)
            {
                throw new InvalidOperationException("Failed to create WebGPU Surface from window.");
            }
        }

        // 3. Request Adapter (synchronously)
        SafeLog("[WGPUCONTEXT] Requesting Adapter\n");
        var adapterSignal = new ManualResetEventSlim(false);
        Adapter* requestedAdapter = null;

        var requestAdapterOptions = new RequestAdapterOptions
        {
            CompatibleSurface = Surface,
            PowerPreference = PowerPreference.HighPerformance
        };

        var onAdapterReceived = PfnRequestAdapterCallback.From((status, adapter, message, userData) =>
        {
            if (status == RequestAdapterStatus.Success)
            {
                requestedAdapter = adapter;
            }
            else
            {
                string msg = (message != null ? SilkMarshal.PtrToString((nint)message) : null) ?? "Unknown error";
                Console.WriteLine($"[WebGPU] RequestAdapter failed: {msg}");
            }
            adapterSignal.Set();
        });

        Wgpu.InstanceRequestAdapter(Instance, &requestAdapterOptions, onAdapterReceived, null);
        adapterSignal.Wait();
        
        SafeLog($"[WGPUCONTEXT] RequestAdapter finished, adapter={(nint)requestedAdapter:X}\n");
        if (requestedAdapter == null)
        {
            throw new InvalidOperationException("Failed to obtain WebGPU Adapter.");
        }
        Adapter = requestedAdapter;

        // 4. Request Device (synchronously)
        SafeLog("[WGPUCONTEXT] Requesting Device\n");
        var deviceSignal = new ManualResetEventSlim(false);
        Device* requestedDevice = null;

        var adapterLimits = new SupportedLimits();
        Wgpu.AdapterGetLimits(Adapter, &adapterLimits);
        var requiredLimits = CreateRequiredLimits(adapterLimits);
        var requiredFeatures = stackalloc FeatureName[2];
        int requiredFeatureCount = 0;
        requiredFeatures[requiredFeatureCount++] = FeatureName.Bgra8UnormStorage;
        SupportsTimestampQueries = Wgpu.AdapterHasFeature(Adapter, FeatureName.TimestampQuery);
        if (SupportsTimestampQueries)
        {
            requiredFeatures[requiredFeatureCount++] = FeatureName.TimestampQuery;
        }

        var deviceDesc = new DeviceDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("ProGPU Primary Device"),
            RequiredLimits = &requiredLimits,
            RequiredFeatureCount = (nuint)requiredFeatureCount,
            RequiredFeatures = requiredFeatures
        };

        var onDeviceReceived = PfnRequestDeviceCallback.From((status, device, message, userData) =>
        {
            if (status == RequestDeviceStatus.Success)
            {
                requestedDevice = device;
            }
            else
            {
                string msg = (message != null ? SilkMarshal.PtrToString((nint)message) : null) ?? "Unknown error";
                Console.WriteLine($"[WebGPU] RequestDevice failed: {msg}");
            }
            deviceSignal.Set();
        });

        Wgpu.AdapterRequestDevice(Adapter, &deviceDesc, onDeviceReceived, null);
        deviceSignal.Wait();

        // Free labeled string
        SilkMarshal.Free((nint)deviceDesc.Label);

        SafeLog($"[WGPUCONTEXT] RequestDevice finished, device={(nint)requestedDevice:X}\n");
        if (requestedDevice == null)
        {
            throw new InvalidOperationException("Failed to obtain WebGPU Device.");
        }
        Device = requestedDevice;

        var deviceLimits = new SupportedLimits();
        Wgpu.DeviceGetLimits(Device, &deviceLimits);
        MaxSampledTexturesPerShaderStage = Math.Max(16, deviceLimits.Limits.MaxSampledTexturesPerShaderStage);
        MaxSamplersPerShaderStage = Math.Max(16, deviceLimits.Limits.MaxSamplersPerShaderStage);
        MaxBindGroups = Math.Max(4, deviceLimits.Limits.MaxBindGroups);
        SupportsReadOnlyAndReadWriteStorageTextures = IsReadWriteStorageTextureSupportEnabled();

        // 5. Retrieve Default Queue
        SafeLog("[WGPUCONTEXT] Getting Default Queue\n");
        Queue = Wgpu.DeviceGetQueue(Device);
        _sharedDeviceLifetime = new SharedDeviceLifetime(Wgpu, Instance, Adapter, Device, Queue);

        // 6. Hook up validation error callback
        _errorCallback = PfnErrorCallback.From((type, msg, _) =>
        {
            string errorMsg = (msg != null ? SilkMarshal.PtrToString((nint)msg) : null) ?? "Unknown error";
            Console.WriteLine($"[WebGPU Error] Type: {type}, Message: {errorMsg}");
            OnWebGpuError?.Invoke(type, errorMsg);
        });
        Wgpu.DeviceSetUncapturedErrorCallback(Device, _errorCallback, null);

        // 7. Configure Surface if window exists
        if (window != null && Surface != null)
        {
            SafeLog("[WGPUCONTEXT] Configuring SwapChain\n");
            ConfigureSwapChain((uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y);
            SafeLog("[WGPUCONTEXT] Configuring SwapChain finished\n");
        }

        lock (_activeContexts)
        {
            if (!_activeContexts.Contains(this))
            {
                _activeContexts.Add(this);
            }
        }

        Current = this;
    }

    public Task InitializeAsync(IWindow? window, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Initialize(window);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Initializes a context owned by an external WebGPU host such as navigator.gpu.
    /// Handles are opaque backend tokens represented through the existing Silk pointer types.
    /// </summary>
    public void InitializeExternal(
        IWebGpuApi api,
        Device* device,
        Queue* queue,
        Surface* surface,
        TextureFormat swapChainFormat,
        uint maxSampledTexturesPerShaderStage = 16,
        uint maxSamplersPerShaderStage = 16,
        uint maxBindGroups = 4,
        bool supportsReadOnlyAndReadWriteStorageTextures = false,
        bool supportsTimestampQueries = false)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (Api != null || Device != null || _isDisposed)
            throw new InvalidOperationException("The WebGPU context is already initialized or disposed.");
        if (device == null || queue == null || surface == null)
            throw new ArgumentException("External WebGPU device, queue, and surface handles are required.");

        Api = api;
        BackendKind = WgpuBackendKind.BrowserWebGpu;
        Device = device;
        Queue = queue;
        Surface = surface;
        SwapChainFormat = swapChainFormat;
        MaxSampledTexturesPerShaderStage = Math.Max(16, maxSampledTexturesPerShaderStage);
        MaxSamplersPerShaderStage = Math.Max(16, maxSamplersPerShaderStage);
        MaxBindGroups = Math.Max(4, maxBindGroups);
        SupportsReadOnlyAndReadWriteStorageTextures = supportsReadOnlyAndReadWriteStorageTextures;
        SupportsTimestampQueries = supportsTimestampQueries;
        _isSurfaceConfigured = true;
        _lastWidth = 1;
        _lastHeight = 1;

        lock (_activeContexts)
        {
            if (!_activeContexts.Contains(this)) _activeContexts.Add(this);
        }
        Current = this;
    }

    /// <summary>
    /// Creates an additional presentation surface while reusing an initialized context's
    /// instance, adapter, device, and queue. The shared device remains alive until every surface
    /// context has been disposed, regardless of owner disposal order.
    /// Surface creation and configuration are O(1); GPU pipelines, atlases, and device heaps stay
    /// shared instead of being duplicated for transient popup or tool windows.
    /// </summary>
    public void InitializeSharedDevice(IWindow window, WgpuContext deviceOwner)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(deviceOwner);
        if (_window != null || Instance != null || Surface != null || Device != null)
        {
            throw new InvalidOperationException("The WebGPU context is already initialized.");
        }

        if (deviceOwner._isDisposed ||
            deviceOwner.Instance == null ||
            deviceOwner.Adapter == null ||
            deviceOwner.Device == null ||
            deviceOwner.Queue == null)
        {
            throw new InvalidOperationException("The shared WebGPU device owner is not initialized.");
        }

        if (!CanCreateNativeSurface(window))
        {
            throw new InvalidOperationException("Cannot create a WebGPU surface before the native window source is loaded.");
        }

        SharedDeviceLifetime sharedDeviceLifetime = deviceOwner._sharedDeviceLifetime?.Acquire()
            ?? throw new InvalidOperationException("The shared WebGPU device lifetime is unavailable.");

        _window = window;
        Wgpu = deviceOwner.Wgpu;
        Api = deviceOwner.Api;
        Instance = deviceOwner.Instance;
        Adapter = deviceOwner.Adapter;
        Device = deviceOwner.Device;
        Queue = deviceOwner.Queue;
        MaxSampledTexturesPerShaderStage = deviceOwner.MaxSampledTexturesPerShaderStage;
        MaxSamplersPerShaderStage = deviceOwner.MaxSamplersPerShaderStage;
        MaxBindGroups = deviceOwner.MaxBindGroups;
        SupportsReadOnlyAndReadWriteStorageTextures = deviceOwner.SupportsReadOnlyAndReadWriteStorageTextures;
        SupportsTimestampQueries = deviceOwner.SupportsTimestampQueries;
        _sharedDeviceLifetime = sharedDeviceLifetime;

        Surface = window.CreateWebGPUSurface(Wgpu, Instance);
        if (Surface == null)
        {
            _sharedDeviceLifetime.Release();
            _sharedDeviceLifetime = null;
            ClearSharedDeviceReferences();
            throw new InvalidOperationException("Failed to create the shared-device WebGPU surface.");
        }

        ConfigureSwapChain((uint)Math.Max(1, window.FramebufferSize.X), (uint)Math.Max(1, window.FramebufferSize.Y));
        lock (_activeContexts)
        {
            if (!_activeContexts.Contains(this))
            {
                _activeContexts.Add(this);
            }
        }

        Current = this;
    }

    private static bool CanCreateNativeSurface(IWindow window)
    {
        if (window is not IView view || view.Handle == IntPtr.Zero)
        {
            return false;
        }

        return window is INativeWindowSource { Native: not null };
    }

    public void ConfigureSwapChain(uint width, uint height)
    {
        _ = TryConfigureSwapChain(width, height);
    }

    public bool TryConfigureSwapChain(uint width, uint height)
    {
        if (BackendKind == WgpuBackendKind.BrowserWebGpu)
        {
            _lastWidth = Math.Max(1, width);
            _lastHeight = Math.Max(1, height);
            _isSurfaceConfigured = true;
            return true;
        }
        if (Surface == null || Device == null)
        {
            return false;
        }

        // Synchronize GLFW window VSync state with WebGPU context VSync state dynamically
        if (_window != null)
        {
            _window.VSync = _vsync;
        }

        // 7a. Query supported formats
        var capabilities = new SurfaceCapabilities();
        Wgpu.SurfaceGetCapabilities(Surface, Adapter, &capabilities);
        
        ReadOnlySpan<TextureFormat> formats = capabilities.FormatCount > 0 && capabilities.Formats != null
            ? new ReadOnlySpan<TextureFormat>(capabilities.Formats, checked((int)capabilities.FormatCount))
            : ReadOnlySpan<TextureFormat>.Empty;
        ReadOnlySpan<CompositeAlphaMode> alphaModes = capabilities.AlphaModeCount > 0 && capabilities.AlphaModes != null
            ? new ReadOnlySpan<CompositeAlphaMode>(capabilities.AlphaModes, checked((int)capabilities.AlphaModeCount))
            : ReadOnlySpan<CompositeAlphaMode>.Empty;
        ReadOnlySpan<PresentMode> presentModes = capabilities.PresentModeCount > 0 && capabilities.PresentModes != null
            ? new ReadOnlySpan<PresentMode>(capabilities.PresentModes, checked((int)capabilities.PresentModeCount))
            : ReadOnlySpan<PresentMode>.Empty;

        if (!CanConfigureSurface(formats, alphaModes, presentModes))
        {
            ProGpuBackendDiagnostics.WriteLine(
                $"[WebGPU Context] Deferring SwapChain configuration for {width}x{height}: " +
                $"formats={formats.Length}, alphaModes={alphaModes.Length}, presentModes={presentModes.Length}.");
            Wgpu.SurfaceCapabilitiesFreeMembers(capabilities);
            return false;
        }

        TextureFormat swapChainFormat = formats[0];
        for (int i = 0; i < formats.Length; i++)
        {
            if (formats[i] == TextureFormat.Bgra8Unorm)
            {
                swapChainFormat = TextureFormat.Bgra8Unorm;
                break;
            }
        }

        var alphaMode = ChooseCompositeAlphaMode(
            _window?.TransparentFramebuffer == true,
            alphaModes);
        PresentMode presentMode = ChoosePresentMode(_vsync, presentModes);

        ProGpuBackendDiagnostics.WriteLine($"[WebGPU Context] Configuring SwapChain: {width}x{height}, VSync: {_vsync}, Selected Mode: {presentMode}");

        Wgpu.SurfaceCapabilitiesFreeMembers(capabilities);

        // 7b. Surface Configuration
        var config = new SurfaceConfiguration
        {
            Device = Device,
            Format = swapChainFormat,
            Usage = TextureUsage.RenderAttachment,
            AlphaMode = alphaMode,
            PresentMode = presentMode,
            Width = width > 0 ? width : 1,
            Height = height > 0 ? height : 1
        };

        Wgpu.SurfaceConfigure(Surface, &config);
        SwapChainFormat = swapChainFormat;
        _lastWidth = config.Width;
        _lastHeight = config.Height;
        _isSurfaceConfigured = true;
        return true;
    }

    public static bool CanConfigureSurface(
        ReadOnlySpan<TextureFormat> formats,
        ReadOnlySpan<CompositeAlphaMode> alphaModes,
        ReadOnlySpan<PresentMode> presentModes)
    {
        return !formats.IsEmpty && !alphaModes.IsEmpty && !presentModes.IsEmpty;
    }

    public static CompositeAlphaMode ChooseCompositeAlphaMode(
        bool transparentFramebuffer,
        ReadOnlySpan<CompositeAlphaMode> alphaModes)
    {
        if (alphaModes.IsEmpty)
        {
            return transparentFramebuffer
                ? CompositeAlphaMode.Premultiplied
                : CompositeAlphaMode.Opaque;
        }

        if (transparentFramebuffer)
        {
            if (alphaModes.Contains(CompositeAlphaMode.Premultiplied))
            {
                return CompositeAlphaMode.Premultiplied;
            }
            if (alphaModes.Contains(CompositeAlphaMode.Unpremultiplied))
            {
                return CompositeAlphaMode.Unpremultiplied;
            }
            if (alphaModes.Contains(CompositeAlphaMode.Inherit))
            {
                return CompositeAlphaMode.Inherit;
            }
        }
        else if (alphaModes.Contains(CompositeAlphaMode.Opaque))
        {
            return CompositeAlphaMode.Opaque;
        }

        return alphaModes[0];
    }

    public static PresentMode ChoosePresentMode(bool vsync, ReadOnlySpan<PresentMode> presentModes)
    {
        if (presentModes.IsEmpty)
        {
            return PresentMode.Fifo;
        }

        if (!vsync)
        {
            for (int i = 0; i < presentModes.Length; i++)
            {
                if (presentModes[i] == PresentMode.Immediate)
                {
                    return PresentMode.Immediate;
                }
            }
        }

        for (int i = 0; i < presentModes.Length; i++)
        {
            if (presentModes[i] == PresentMode.Fifo)
            {
                return PresentMode.Fifo;
            }
        }

        return presentModes[0];
    }

    public bool CanBindWpfShaderEffectMask(int activeSamplerRegisterCount)
    {
        return CanBindWpfShaderEffectMask(
            activeSamplerRegisterCount,
            MaxSampledTexturesPerShaderStage,
            MaxSamplersPerShaderStage,
            MaxBindGroups);
    }

    public static bool CanBindWpfShaderEffectMask(
        int activeSamplerRegisterCount,
        uint maxSampledTexturesPerShaderStage,
        uint maxSamplersPerShaderStage,
        uint maxBindGroups)
    {
        if (activeSamplerRegisterCount < 0)
        {
            return false;
        }

        var requiredTextureAndSamplerCount = checked((uint)activeSamplerRegisterCount + 1u);
        return maxBindGroups >= 4
            && maxSampledTexturesPerShaderStage >= requiredTextureAndSamplerCount
            && maxSamplersPerShaderStage >= requiredTextureAndSamplerCount;
    }

    private static RequiredLimits CreateRequiredLimits(SupportedLimits adapterLimits)
    {
        var requiredLimits = new RequiredLimits
        {
            Limits = adapterLimits.Limits
        };

        if (requiredLimits.Limits.MaxSampledTexturesPerShaderStage < 16)
        {
            requiredLimits.Limits.MaxSampledTexturesPerShaderStage = 16;
        }

        if (requiredLimits.Limits.MaxSamplersPerShaderStage < 16)
        {
            requiredLimits.Limits.MaxSamplersPerShaderStage = 16;
        }

        if (requiredLimits.Limits.MaxBindGroups < 4)
        {
            requiredLimits.Limits.MaxBindGroups = 4;
        }

        return requiredLimits;
    }

    private static bool IsReadWriteStorageTextureSupportEnabled()
    {
        // wgpuInstanceHasWGSLLanguageFeature aborts in the current wgpu-native build, so keep this explicit.
        var value = Environment.GetEnvironmentVariable("PROGPU_ENABLE_READWRITE_STORAGE_TEXTURES");
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void ReconfigureIfNeeded(uint width, uint height)
    {
        _ = TryReconfigureIfNeeded(width, height);
    }

    public bool TryReconfigureIfNeeded(uint width, uint height)
    {
        if (width != _lastWidth || height != _lastHeight)
        {
            return TryConfigureSwapChain(width, height);
        }

        return _isSurfaceConfigured;
    }

    [DllImport("wgpu_native", EntryPoint = "wgpuDevicePoll")]
    private static extern unsafe bool wgpuDevicePoll(Device* device, bool wait, void* wrappedSubmissionIndex);

    public void PollDevice(bool wait)
    {
        if (BackendKind == WgpuBackendKind.SilkNative && Device != null && !_isDisposed)
        {
            wgpuDevicePoll(Device, wait, null);
        }
    }

    public void WaitIdle()
    {
        PollDevice(wait: true);
    }

    public ShaderModuleVerificationStatus GetShaderModuleVerificationStatus(ShaderModule* module, out string errors)
    {
        errors = string.Empty;
        if (module == null || Device == null || _isDisposed)
        {
            errors = "Cannot verify a shader module without an active WebGPU device.";
            return ShaderModuleVerificationStatus.Invalid;
        }

        // wgpu-native currently aborts the process from wgpuShaderModuleGetCompilationInfo.
        // Keep verification process-safe and report that preflight diagnostics are
        // unavailable instead of claiming unchecked user shader modules are verified.
        // Pipeline creation/device error callbacks remain
        // responsible for detailed diagnostics until a safe native diagnostics API exists.
        errors = "WebGPU shader module verification is unavailable for this backend; render pipeline creation will validate the module.";
        return ShaderModuleVerificationStatus.Unavailable;
    }

    public bool VerifyShaderModule(ShaderModule* module, out string errors)
    {
        return GetShaderModuleVerificationStatus(module, out errors) == ShaderModuleVerificationStatus.Verified;
    }

    public Task<(ShaderModuleVerificationStatus Status, string Errors)> VerifyShaderModuleAsync(
        ShaderModule* module,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = GetShaderModuleVerificationStatus(module, out var errors);
        return Task.FromResult((status, errors));
    }

    public Task WaitIdleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WaitIdle();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        lock (RenderLock)
        {
            if (_isDisposed) return;

            Disposing?.Invoke(this);

            WaitIdle();

            Interlocked.Exchange(ref _gpuTimestampRing, null)?.Dispose();
            Interlocked.Exchange(ref _frameCompletionTracker, null)?.Dispose();

            CleanupPendingResources();

            if (Current == this)
            {
                Current = null;
            }
            
            lock (_activeContexts)
            {
                _activeContexts.Remove(this);
            }
            
            if (Surface != null)
            {
                Api.SurfaceRelease(Surface);
                Surface = null;
            }

            _sharedDeviceLifetime?.Release();
            _sharedDeviceLifetime = null;
            ClearSharedDeviceReferences();
            
            _isDisposed = true;
        }
        
        GC.SuppressFinalize(this);
    }

    private void ClearSharedDeviceReferences()
    {
        Queue = null;
        Device = null;
        Adapter = null;
        Instance = null;
    }

    private sealed class SharedDeviceLifetime
    {
        private readonly object _sync = new();
        private WebGPU? _wgpu;
        private Instance* _instance;
        private Adapter* _adapter;
        private Device* _device;
        private Queue* _queue;
        private int _referenceCount = 1;

        public SharedDeviceLifetime(
            WebGPU wgpu,
            Instance* instance,
            Adapter* adapter,
            Device* device,
            Queue* queue)
        {
            _wgpu = wgpu;
            _instance = instance;
            _adapter = adapter;
            _device = device;
            _queue = queue;
        }

        public SharedDeviceLifetime Acquire()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_referenceCount == 0, this);
                _referenceCount++;
                return this;
            }
        }

        public void Release()
        {
            WebGPU? wgpu;
            Instance* instance;
            Adapter* adapter;
            Device* device;
            Queue* queue;
            lock (_sync)
            {
                if (_referenceCount == 0 || --_referenceCount != 0)
                {
                    return;
                }

                wgpu = _wgpu;
                instance = _instance;
                adapter = _adapter;
                device = _device;
                queue = _queue;
                _wgpu = null;
                _instance = null;
                _adapter = null;
                _device = null;
                _queue = null;
            }

            if (wgpu == null)
            {
                return;
            }

            if (queue != null)
            {
                wgpu.QueueRelease(queue);
            }
            if (device != null)
            {
                wgpu.DeviceDestroy(device);
                wgpu.DeviceRelease(device);
            }
            if (adapter != null)
            {
                wgpu.AdapterRelease(adapter);
            }
            if (instance != null)
            {
                wgpu.InstanceRelease(instance);
            }
        }
    }

    ~WgpuContext()
    {
        // Do not call Dispose() or native WebGPU release APIs during finalization.
        // During process exit or AssemblyLoadContext unload, the native wgpu_native library 
        // may already be unloaded, causing native entry point calls to crash with a segfault (139).
    }
}
