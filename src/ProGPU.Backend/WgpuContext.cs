using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;
using WgpuAdapter = Silk.NET.WebGPU.Adapter;

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
    BrowserWebGpu,
    DawnNative
}

public unsafe class WgpuContext : IDisposable
{
    public const ulong DefaultMaxBufferSize = 256UL * 1024UL * 1024UL;
    // Device polling can materialize internal Metal signal/transition command
    // buffers even when the renderer has no callbacks to service. Keep progress
    // bounded without injecting that work into every retained frame.
    private const int QueuePollSubmissionInterval = 8;
    private const int DefaultMaximumDeferredQueueSubmissions = 64;
    private SharedDeviceLifetime? _sharedDeviceLifetime;
    private IWebGpuExternalDeviceLifetime? _externalDeviceLifetime;
    private WgpuDeviceResourceDomain? _deviceResourceDomain;
    public WebGPU Wgpu { get; private set; } = null!;
    public IWebGpuApi Api { get; private set; } = null!;
    public WgpuBackendKind BackendKind { get; private set; } = WgpuBackendKind.SilkNative;
    public Instance* Instance { get; private set; } = null;
    public WgpuAdapter* Adapter { get; private set; } = null;
    public Device* Device { get; private set; } = null;
    public Queue* Queue { get; private set; } = null;
    public Surface* Surface { get; private set; } = null;
    public TextureFormat SwapChainFormat { get; private set; } = TextureFormat.Bgra8Unorm;
    public uint MaxSampledTexturesPerShaderStage { get; private set; } = 16;
    public uint MaxSamplersPerShaderStage { get; private set; } = 16;
    public uint MaxBindGroups { get; private set; } = 4;
    public ulong MaxBufferSize { get; private set; } = DefaultMaxBufferSize;
    public bool SupportsReadOnlyAndReadWriteStorageTextures { get; private set; }
    public bool SupportsTextureFormatsTier1 { get; private set; }
    public BackendType AdapterBackendType { get; private set; } = BackendType.Undefined;
    public string AdapterName { get; private set; } = string.Empty;
    public WgpuAdapterSelectionDiagnostics AdapterSelectionDiagnostics { get; private set; } =
        WgpuAdapterSelectionDiagnostics.Unknown;
    public IProGpuExternalTextureImporter?
        ExternalTextureImporter { get; private set; }
    internal WgpuDeviceResourceDomain DeviceResourceDomain =>
        _deviceResourceDomain ??
        throw new InvalidOperationException(
            "The WebGPU device resource domain is not initialized.");
    public int CachedDeviceShaderModuleCount =>
        _deviceResourceDomain?.ShaderModuleCount ?? 0;
    public int CachedDeviceBindGroupLayoutCount =>
        _deviceResourceDomain?.BindGroupLayoutCount ?? 0;
    public int CachedDevicePipelineLayoutCount =>
        _deviceResourceDomain?.PipelineLayoutCount ?? 0;
    public int CachedDeviceRenderPipelineCount =>
        _deviceResourceDomain?.RenderPipelineCount ?? 0;
    public int CachedDeviceComputePipelineCount =>
        _deviceResourceDomain?.ComputePipelineCount ?? 0;
    public ulong SurfaceConfigurationCount { get; private set; }
    public double SurfaceConfigurationTimeMs { get; private set; }
    public double MaximumSurfaceConfigurationTimeMs { get; private set; }
    public uint DesiredMaximumFrameLatency { get; set; } =
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? 1u : 2u;

    public static event Action<ErrorType, string>? OnWebGpuError;
    public static event Action<DeviceLostReason, string>? OnWebGpuDeviceLost;
    private static long s_deviceLossGeneration;
    private readonly long _deviceLossGeneration =
        Volatile.Read(ref s_deviceLossGeneration);

    public static void RaiseWebGpuError(ErrorType type, string message)
    {
        OnWebGpuError?.Invoke(type, message);
    }

    /// <summary>
    /// Reports an unusable WebGPU device to every typed host sharing ProGPU's
    /// process-wide renderer. Device destruction is an expected ownership
    /// transition and is intentionally not reported as a loss.
    /// </summary>
    public static void RaiseWebGpuDeviceLost(
        DeviceLostReason reason,
        string message)
    {
        if (reason == DeviceLostReason.Destroyed)
        {
            return;
        }

        Interlocked.Increment(ref s_deviceLossGeneration);
        OnWebGpuDeviceLost?.Invoke(reason, message);
    }

    /// <summary>
    /// Gets whether a device-loss notification occurred after this context
    /// was created. The value is lock-free and safe to query from Avalonia's
    /// UI and render threads.
    /// </summary>
    public bool IsDeviceLost =>
        _deviceLossGeneration !=
        Volatile.Read(ref s_deviceLossGeneration);

    private PfnErrorCallback _errorCallback;
    private nint _devicePollAddress;
    private nint _generateReportAddress;

    private static readonly object s_silkNativeRenderLock = new();

    /// <summary>
    /// Serializes command recording, queue submission, resource destruction,
    /// and device polling within the active WebGPU synchronization domain.
    /// </summary>
    /// <remarks>
    /// Current wgpu-native instances share an internal process-wide resource
    /// lock graph, so independently created Silk-native devices use one lock.
    /// Browser and externally owned native devices receive independent locks;
    /// shared-device initialization replaces the field with the owner's lock.
    /// The field remains public for binary compatibility.
    /// </remarks>
    public object RenderLock = s_silkNativeRenderLock;
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
    public readonly List<IDisposable> PendingExternalTextureOwners =
        new();
    private readonly HashSet<IntPtr> _pendingSnapshotSeen = new();
    private long _queueSubmissionCount;
    private long _drainedQueueSubmissionCount;
    private long _polledQueueSubmissionCount;
    private long _textureContentVersion;
    private int _maximumDeferredQueueSubmissions =
        DefaultMaximumDeferredQueueSubmissions;

    /// <summary>
    /// Gets or sets the maximum number of queue submissions that may remain
    /// deferred before resource cleanup forces a blocking device drain.
    /// </summary>
    /// <remarks>
    /// Completed work is retired by non-blocking device polling. This bound is
    /// a safety valve for hosts that can submit work faster than the GPU can
    /// retire it; hosts with an explicit frame-latency policy may select a
    /// larger bounded window to avoid unnecessary CPU/GPU serialization.
    /// </remarks>
    public int MaximumDeferredQueueSubmissions
    {
        get => Volatile.Read(
            ref _maximumDeferredQueueSubmissions);
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "The deferred queue submission bound must be positive.");
            }

            Volatile.Write(
                ref _maximumDeferredQueueSubmissions,
                value);
        }
    }

    /// <summary>
    /// Gets a context-wide version that advances whenever an owned texture's
    /// content or native view identity changes.
    /// </summary>
    /// <remarks>
    /// Retained hosts can capture this value after rendering and require an
    /// exact match before reusing an already populated output target. Changes
    /// to unrelated textures may conservatively invalidate that reuse.
    /// </remarks>
    public long TextureContentVersion =>
        Volatile.Read(ref _textureContentVersion);

    internal void NotifyTextureContentChanged() =>
        Interlocked.Increment(ref _textureContentVersion);

    public void Submit(
        nuint commandCount,
        CommandBuffer** commandBuffers)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (commandCount == 0)
        {
            return;
        }
        if (commandBuffers == null)
        {
            throw new ArgumentNullException(nameof(commandBuffers));
        }

        lock (RenderLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            Api.QueueSubmit(Queue, commandCount, commandBuffers);
            Interlocked.Increment(ref _queueSubmissionCount);
        }
    }

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

    public void QueueExternalTextureOwnerDisposal(
        IDisposable owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (DisposalLock)
        {
            PendingExternalTextureOwners.Add(owner);
        }
    }

    public void SetExternalTextureImporter(
        IProGpuExternalTextureImporter? importer)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ExternalTextureImporter = importer;
    }

    public bool TryImportExternalTexture(
        in ProGpuExternalTextureDescriptor descriptor,
        IDisposable nativeOwner,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(nativeOwner);
        IProGpuExternalTextureImporter? importer =
            ExternalTextureImporter;
        if (importer is null)
        {
            texture = null!;
            return false;
        }
        return importer.TryImportExternalTexture(
            this,
            in descriptor,
            nativeOwner,
            out texture);
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
            IDisposable[]? externalTextureOwners = null;

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

                    if (PendingExternalTextureOwners.Count > 0)
                    {
                        externalTextureOwners =
                            PendingExternalTextureOwners.ToArray();
                        PendingExternalTextureOwners.Clear();
                    }
                }

                // WebGPU command buffers retain ordinary referenced resources
                // until submitted work completes, so dropping the application's
                // native handle references must not serialize every frame on the
                // device queue. A periodic non-blocking poll retires completed
                // work, and a fixed submission bound forces a full drain so
                // native command/resource residency cannot grow with frame
                // count. Imported external storage is different: its
                // platform owner sits outside WebGPU's reference graph and must
                // remain alive until all previously submitted use has completed.
                var submittedQueueWork =
                    Volatile.Read(ref _queueSubmissionCount);
                var drainedQueueWork =
                    Volatile.Read(ref _drainedQueueSubmissionCount);
                var deferredQueueSubmissions =
                    submittedQueueWork - drainedQueueWork;
                if (externalTextureOwners is not null ||
                    deferredQueueSubmissions >=
                    MaximumDeferredQueueSubmissions)
                {
                    WaitIdle();
                    Volatile.Write(
                        ref _drainedQueueSubmissionCount,
                        submittedQueueWork);
                    Volatile.Write(
                        ref _polledQueueSubmissionCount,
                        submittedQueueWork);
                }
                else if (submittedQueueWork -
                         Volatile.Read(ref _polledQueueSubmissionCount) >=
                         QueuePollSubmissionInterval)
                {
                    PollDevice(wait: false);
                    Volatile.Write(
                        ref _polledQueueSubmissionCount,
                        submittedQueueWork);
                }

                // Release dependants before their immutable ABI objects. This
                // mirrors device-domain disposal and keeps owner-first popup
                // teardown deterministic even when all resources become
                // unreferenced in the same cleanup batch.
                ReleaseBindGroups(bindGroups.Span);
                ReleaseRenderPipelines(renderPipes.Span);
                ReleaseComputePipelines(computePipes.Span);
                ReleasePipelineLayouts(pipeLayouts.Span);
                ReleaseBindGroupLayouts(layouts.Span);
                ReleaseTextureViews(views.Span);
                DisposeExternalTextureOwners(
                    externalTextureOwners);
                ReleaseTextures(textures.Span);
                ReleaseBuffers(buffers.Span);
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

    private static void DisposeExternalTextureOwners(
        IDisposable[]? owners)
    {
        if (owners is null)
        {
            return;
        }
        for (int index = 0; index < owners.Length; index++)
        {
            try
            {
                owners[index].Dispose();
            }
            catch
            {
                // Resource cleanup must continue for the remaining native
                // handles. Platform providers report operational failures
                // before ownership reaches this deferred release boundary.
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
        !IsDeviceLost &&
        Api != null &&
        Device != null &&
        Queue != null &&
        BackendKind switch
        {
            WgpuBackendKind.BrowserWebGpu => Surface != null,
            WgpuBackendKind.DawnNative =>
                _externalDeviceLifetime is not null,
            _ => Wgpu != null && Instance != null && Adapter != null
        };
    private uint _lastWidth = 1;
    private uint _lastHeight = 1;
    private bool _isSurfaceConfigured;
    private bool _hasSurfaceConfigurationCapabilities;
    private TextureFormat _cachedSurfaceFormat;
    private CompositeAlphaMode _cachedCompositeAlphaMode;
    private PresentMode _cachedVsyncPresentMode;
    private PresentMode _cachedUncappedPresentMode;
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

    public static unsafe bool TryGetActiveContextForSurface(
        IntPtr surfaceHandle,
        [NotNullWhen(true)] out WgpuContext? context)
    {
        if (surfaceHandle != IntPtr.Zero)
        {
            lock (_activeContexts)
            {
                for (var i = 0; i < _activeContexts.Count; i++)
                {
                    var active = _activeContexts[i];
                    if (active.IsInitialized &&
                        (IntPtr)active.Surface == surfaceHandle)
                    {
                        context = active;
                        return true;
                    }
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
        InitializeNative(window, null, null, 0, 0);
    }

    /// <summary>
    /// Initializes WebGPU directly against an Apple <c>CAMetalLayer</c>.
    /// The layer is borrowed for the lifetime of the context and remains owned by UIKit.
    /// </summary>
    public void InitializeMetalLayer(nint metalLayer, uint framebufferWidth, uint framebufferHeight)
    {
        if (metalLayer == 0)
            throw new ArgumentException("A valid CAMetalLayer handle is required.", nameof(metalLayer));

        InitializeNative(
            window: null,
            metalLayer: (void*)metalLayer,
            androidNativeWindow: null,
            framebufferWidth: Math.Max(1u, framebufferWidth),
            framebufferHeight: Math.Max(1u, framebufferHeight));
    }

    /// <summary>
    /// Initializes WebGPU directly against an Android <c>ANativeWindow</c>.
    /// The caller retains ownership and must keep the native window acquired until this
    /// context has been disposed.
    /// </summary>
    public void InitializeAndroidNativeWindow(nint nativeWindow, uint framebufferWidth, uint framebufferHeight)
    {
        if (nativeWindow == 0)
            throw new ArgumentException("A valid ANativeWindow pointer is required.", nameof(nativeWindow));

        InitializeNative(
            window: null,
            metalLayer: null,
            androidNativeWindow: (void*)nativeWindow,
            framebufferWidth: Math.Max(1u, framebufferWidth),
            framebufferHeight: Math.Max(1u, framebufferHeight));
    }

    private void InitializeNative(
        IWindow? window,
        void* metalLayer,
        void* androidNativeWindow,
        uint framebufferWidth,
        uint framebufferHeight)
    {
        lock (RenderLock)
        {
            InitializeNativeCore(
                window,
                metalLayer,
                androidNativeWindow,
                framebufferWidth,
                framebufferHeight);
        }
    }

    private void InitializeNativeCore(
        IWindow? window,
        void* metalLayer,
        void* androidNativeWindow,
        uint framebufferWidth,
        uint framebufferHeight)
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
        Wgpu = CreateNativeWebGpuApi();
        Api = new SilkWebGpuApi(Wgpu, RenderLock);
        
        // 1. Create WebGPU Instance (isolated per context)
        SafeLog("[WGPUCONTEXT] Creating WebGPU Instance\n");
        var instanceExtras = CreateNativeInstanceExtras();
        var instanceDesc = new InstanceDescriptor
        {
            NextInChain = instanceExtras.Chain.SType == 0 ? null : &instanceExtras.Chain
        };
        Instance = Wgpu.CreateInstance(&instanceDesc);
        if (Instance == null)
        {
            throw new InvalidOperationException("Failed to create WebGPU Instance.");
        }

        // 2. Create a native presentation surface when requested.
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
        else if (metalLayer != null)
        {
            SafeLog("[WGPUCONTEXT] Creating WebGPU Surface from CAMetalLayer\n");
            var metalDescriptor = new SurfaceDescriptorFromMetalLayer
            {
                Chain = new ChainedStruct
                {
                    SType = SType.SurfaceDescriptorFromMetalLayer
                },
                Layer = metalLayer
            };
            var surfaceDescriptor = new SurfaceDescriptor
            {
                NextInChain = &metalDescriptor.Chain
            };
            Surface = Wgpu.InstanceCreateSurface(Instance, &surfaceDescriptor);
            if (Surface == null)
            {
                throw new InvalidOperationException("Failed to create a WebGPU Surface from CAMetalLayer.");
            }
        }
        else if (androidNativeWindow != null)
        {
            SafeLog("[WGPUCONTEXT] Creating WebGPU Surface from ANativeWindow\n");
            Surface = CreateAndroidSurface(androidNativeWindow);
            if (Surface == null)
            {
                throw new InvalidOperationException("Failed to create a WebGPU Surface from ANativeWindow.");
            }
        }

        // 3. Request Adapter (synchronously)
        SafeLog("[WGPUCONTEXT] Requesting Adapter\n");
        using var adapterSignal = new ManualResetEventSlim(false);
        var adapterState = new AdapterRequestState(adapterSignal);
        var adapterStateHandle = GCHandle.Alloc(adapterState);

        var requestAdapterOptions = new RequestAdapterOptions
        {
            CompatibleSurface = Surface,
            PowerPreference = PowerPreference.HighPerformance
        };

        try
        {
            var onAdapterReceived = new PfnRequestAdapterCallback(&OnAdapterRequested);
            Wgpu.InstanceRequestAdapter(
                Instance,
                &requestAdapterOptions,
                onAdapterReceived,
                (void*)GCHandle.ToIntPtr(adapterStateHandle));
            adapterSignal.Wait();
        }
        finally
        {
            adapterStateHandle.Free();
        }
        
        SafeLog($"[WGPUCONTEXT] RequestAdapter finished, adapter={adapterState.Result:X}\n");
        if (adapterState.Result == 0)
        {
            throw new InvalidOperationException($"Failed to obtain WebGPU Adapter. {adapterState.Error}");
        }
        Adapter = (WgpuAdapter*)adapterState.Result;

        var adapterProperties = new AdapterProperties();
        Wgpu.AdapterGetProperties(Adapter, &adapterProperties);
        SetAdapterSelectionDiagnostics(new WgpuAdapterSelectionDiagnostics(
            adapterProperties.Name == null ? string.Empty : ReadNativeMessage(adapterProperties.Name),
            adapterProperties.BackendType,
            adapterProperties.AdapterType,
            adapterProperties.DriverDescription == null
                ? string.Empty
                : ReadNativeMessage(adapterProperties.DriverDescription),
            adapterProperties.VendorID,
            adapterProperties.DeviceID,
            Surface != null,
            Surface != null
                ? WgpuAdapterSelectionReason.HighPerformanceSurfaceCompatible
                : WgpuAdapterSelectionReason.HighPerformance));
        string adapterDiagnostic =
            $"[WGPUCONTEXT] Adapter '{AdapterName}', backend={AdapterBackendType}, " +
            $"type={AdapterSelectionDiagnostics.AdapterType}, " +
            $"driver='{AdapterSelectionDiagnostics.DriverDescription}', " +
            $"vendor=0x{AdapterSelectionDiagnostics.VendorId:X4}, " +
            $"device=0x{AdapterSelectionDiagnostics.DeviceId:X4}, " +
            $"surfaceCompatible={AdapterSelectionDiagnostics.RequiredCompatibleSurface}, " +
            $"reason={AdapterSelectionDiagnostics.SelectionReason}";
        SafeLog(adapterDiagnostic + "\n");
        ProGpuBackendDiagnostics.WriteLine(adapterDiagnostic);
        if (OperatingSystem.IsAndroid() && AdapterBackendType != BackendType.Vulkan)
        {
            ReleaseAdapterInitializationResources();
            throw new InvalidOperationException(
                $"Android requires the direct Vulkan WebGPU backend, but wgpu-native selected {AdapterBackendType}.");
        }
        if (OperatingSystem.IsIOS() && AdapterBackendType != BackendType.Metal)
        {
            ReleaseAdapterInitializationResources();
            throw new InvalidOperationException(
                $"iOS requires the direct Metal WebGPU backend, but wgpu-native selected {AdapterBackendType}.");
        }

        // 4. Request Device (synchronously)
        SafeLog("[WGPUCONTEXT] Requesting Device\n");
        using var deviceSignal = new ManualResetEventSlim(false);
        var deviceState = new DeviceRequestState(deviceSignal);
        var deviceStateHandle = GCHandle.Alloc(deviceState);

        var adapterLimits = new SupportedLimits();
        Wgpu.AdapterGetLimits(Adapter, &adapterLimits);
        var requiredLimits = CreateRequiredLimits(adapterLimits);
        var requiredFeatures = stackalloc FeatureName[1];
        uint requiredFeatureCount = 0;
        if (Wgpu.AdapterHasFeature(Adapter, FeatureName.Bgra8UnormStorage))
        {
            requiredFeatures[requiredFeatureCount++] = FeatureName.Bgra8UnormStorage;
        }

        var deviceDesc = new DeviceDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("ProGPU Primary Device"),
            RequiredLimits = &requiredLimits,
            RequiredFeatureCount = requiredFeatureCount,
            RequiredFeatures = requiredFeatureCount == 0 ? null : requiredFeatures,
            DeviceLostCallback = new PfnDeviceLostCallback(&OnDeviceLost)
        };

        try
        {
            var onDeviceReceived = new PfnRequestDeviceCallback(&OnDeviceRequested);
            Wgpu.AdapterRequestDevice(
                Adapter,
                &deviceDesc,
                onDeviceReceived,
                (void*)GCHandle.ToIntPtr(deviceStateHandle));
            deviceSignal.Wait();
        }
        finally
        {
            deviceStateHandle.Free();
        }

        // Free labeled string
        SilkMarshal.Free((nint)deviceDesc.Label);

        SafeLog($"[WGPUCONTEXT] RequestDevice finished, device={deviceState.Result:X}\n");
        if (deviceState.Result == 0)
        {
            throw new InvalidOperationException($"Failed to obtain WebGPU Device. {deviceState.Error}");
        }
        Device = (Device*)deviceState.Result;

        var deviceLimits = new SupportedLimits();
        Wgpu.DeviceGetLimits(Device, &deviceLimits);
        MaxSampledTexturesPerShaderStage = Math.Max(16, deviceLimits.Limits.MaxSampledTexturesPerShaderStage);
        MaxSamplersPerShaderStage = Math.Max(16, deviceLimits.Limits.MaxSamplersPerShaderStage);
        MaxBindGroups = Math.Max(4, deviceLimits.Limits.MaxBindGroups);
        if (deviceLimits.Limits.MaxBufferSize != 0)
        {
            MaxBufferSize = deviceLimits.Limits.MaxBufferSize;
        }
        SupportsReadOnlyAndReadWriteStorageTextures = IsReadWriteStorageTextureSupportEnabled();

        // 5. Retrieve Default Queue
        SafeLog("[WGPUCONTEXT] Getting Default Queue\n");
        Queue = Wgpu.DeviceGetQueue(Device);
        _deviceResourceDomain = new WgpuDeviceResourceDomain(Api, Device);
        _sharedDeviceLifetime = new SharedDeviceLifetime(
            Wgpu,
            Instance,
            Adapter,
            Device,
            Queue,
            _deviceResourceDomain);

        // 6. Hook up validation error callback
        _errorCallback = new PfnErrorCallback(&OnUncapturedError);
        Wgpu.DeviceSetUncapturedErrorCallback(Device, _errorCallback, null);

        // 7. Configure Surface if window exists
        if (Surface != null)
        {
            SafeLog("[WGPUCONTEXT] Configuring SwapChain\n");
            uint width = window != null ? (uint)Math.Max(1, window.FramebufferSize.X) : framebufferWidth;
            uint height = window != null ? (uint)Math.Max(1, window.FramebufferSize.Y) : framebufferHeight;
            ConfigureSwapChain(width, height);
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

    /// <summary>
    /// Replaces a temporarily lost Android presentation surface without rebuilding the
    /// adapter, device, queue, pipelines, atlases, or retained compositor resources.
    /// </summary>
    public void AttachAndroidNativeWindow(nint nativeWindow, uint framebufferWidth, uint framebufferHeight)
    {
        if (nativeWindow == 0)
            throw new ArgumentException("A valid ANativeWindow pointer is required.", nameof(nativeWindow));

        lock (RenderLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (Instance == null || Adapter == null || Device == null)
                throw new InvalidOperationException("The WebGPU device must be initialized before attaching a replacement surface.");

            ReleasePresentationSurfaceCore(waitForDevice: true);
            Surface = CreateAndroidSurface((void*)nativeWindow);
            if (Surface == null)
                throw new InvalidOperationException("Failed to create a replacement WebGPU Surface from ANativeWindow.");

            if (!TryConfigureSwapChain(Math.Max(1u, framebufferWidth), Math.Max(1u, framebufferHeight)))
                throw new InvalidOperationException("The replacement Android WebGPU surface did not expose usable capabilities.");
        }
    }

    /// <summary>
    /// Releases only the Android presentation surface. The device and all reusable GPU
    /// resources remain alive for a later <see cref="AttachAndroidNativeWindow"/> call.
    /// </summary>
    public void DetachAndroidNativeWindow()
    {
        DetachExternalNativePresentationSurface();
    }

    /// <summary>
    /// Releases an exact-ABI native presentation surface while preserving the
    /// external device and all reusable GPU resources.
    /// </summary>
    public void DetachExternalNativePresentationSurface()
    {
        lock (RenderLock)
        {
            if (_isDisposed) return;
            ReleasePresentationSurfaceCore(waitForDevice: true);
        }
    }

    private Surface* CreateAndroidSurface(void* nativeWindow)
    {
        var androidDescriptor = new SurfaceDescriptorFromAndroidNativeWindow
        {
            Chain = new ChainedStruct
            {
                SType = SType.SurfaceDescriptorFromAndroidNativeWindow
            },
            Window = nativeWindow
        };
        var surfaceDescriptor = new SurfaceDescriptor
        {
            NextInChain = &androidDescriptor.Chain
        };
        return Wgpu.InstanceCreateSurface(Instance, &surfaceDescriptor);
    }

    private void ReleaseAdapterInitializationResources()
    {
        ReleasePresentationSurfaceCore(waitForDevice: false);
        if (Adapter != null)
        {
            Wgpu.AdapterRelease(Adapter);
            Adapter = null;
        }
        if (Instance != null)
        {
            Wgpu.InstanceRelease(Instance);
            Instance = null;
        }
    }

    private void ReleasePresentationSurfaceCore(bool waitForDevice)
    {
        if (Surface == null) return;
        if (waitForDevice && Device != null) WaitIdle();
        // Externally owned browser surfaces are opaque command-stream handles and
        // have no native WebGPU instance to unconfigure. Exact-ABI native backends
        // own their configuration and must tear it down through the same ABI.
        if (Api is IWebGpuExternalSurfaceApi externalSurface &&
            _isSurfaceConfigured)
        {
            externalSurface.UnconfigureExternalSurface(Surface);
        }
        else if (BackendKind == WgpuBackendKind.SilkNative &&
                 _isSurfaceConfigured)
        {
            Wgpu.SurfaceUnconfigure(Surface);
        }
        Api.SurfaceRelease(Surface);
        Surface = null;
        _isSurfaceConfigured = false;
        _hasSurfaceConfigurationCapabilities = false;
    }

    private static NativeInstanceExtras CreateNativeInstanceExtras()
    {
        uint backends = OperatingSystem.IsAndroid()
            ? NativeInstanceExtras.VulkanBackend
            : OperatingSystem.IsIOS()
                ? NativeInstanceExtras.MetalBackend
                : 0u;
        return backends == 0u
            ? default
            : new NativeInstanceExtras
            {
                Chain = new ChainedStruct { SType = (SType)NativeInstanceExtras.STypeValue },
                Backends = backends
            };
    }

    // wgpu-native 0.19 extension ABI from its public wgpu.h. Silk exposes the
    // standard WebGPU descriptor chain but intentionally does not generate native-only
    // extensions, so this private sequential representation keeps that boundary explicit.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInstanceExtras
    {
        public const uint STypeValue = 0x00030006;
        public const uint VulkanBackend = 1u << 0;
        public const uint MetalBackend = 1u << 2;

        public ChainedStruct Chain;
        public uint Backends;
        public uint Flags;
        public int Dx12ShaderCompiler;
        public int Gles3MinorVersion;
        public byte* DxilPath;
        public byte* DxcPath;
    }

    // wgpu-native 0.19 surface extension ABI. An Apple frame latency of one
    // avoids an additional full-size CAMetalLayer drawable. Other platforms
    // keep wgpu's default of two frames in flight.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSurfaceConfigurationExtras
    {
        public const uint STypeValue = 0x0003000A;

        public ChainedStruct Chain;
        public uint DesiredMaximumFrameLatency;
    }

    private static WebGPU CreateNativeWebGpuApi()
    {
        if (OperatingSystem.IsIOS())
        {
            return new WebGPU(new LamdaNativeContext(ResolveAppleStaticWebGpuSymbol));
        }

        if (OperatingSystem.IsAndroid())
        {
            return new WebGPU(new LamdaNativeContext(ResolveAndroidWebGpuSymbol));
        }

        return WebGPU.GetApi();
    }

    private static readonly object s_androidWebGpuLibraryLock = new();
    private static nint s_androidWebGpuLibrary;

    private static nint ResolveAndroidWebGpuSymbol(string symbol)
    {
        if (s_androidWebGpuLibrary == 0)
        {
            lock (s_androidWebGpuLibraryLock)
            {
                if (s_androidWebGpuLibrary == 0 &&
                    !NativeLibrary.TryLoad("libwgpu_native.so", out s_androidWebGpuLibrary))
                {
                    throw new DllNotFoundException(
                        "Unable to load libwgpu_native.so. Package the matching Android ABI native library with the application.");
                }
            }
        }

        return NativeLibrary.TryGetExport(s_androidWebGpuLibrary, symbol, out nint address)
            ? address
            : 0;
    }

    private static nint ResolveAppleStaticWebGpuSymbol(string symbol)
    {
        nint program = NativeLibrary.GetMainProgramHandle();
        return NativeLibrary.TryGetExport(
            program,
            symbol,
            out nint address)
                ? address
                : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAdapterRequested(
        RequestAdapterStatus status,
        WgpuAdapter* adapter,
        byte* message,
        void* userData)
    {
        var state = (AdapterRequestState)GCHandle.FromIntPtr((nint)userData).Target!;
        if (status == RequestAdapterStatus.Success)
        {
            state.Result = (nint)adapter;
        }
        else
        {
            state.Error = ReadNativeMessage(message);
            Console.WriteLine($"[WebGPU] RequestAdapter failed: {state.Error}");
        }

        state.Signal.Set();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceRequested(
        RequestDeviceStatus status,
        Device* device,
        byte* message,
        void* userData)
    {
        var state = (DeviceRequestState)GCHandle.FromIntPtr((nint)userData).Target!;
        if (status == RequestDeviceStatus.Success)
        {
            state.Result = (nint)device;
        }
        else
        {
            state.Error = ReadNativeMessage(message);
            Console.WriteLine($"[WebGPU] RequestDevice failed: {state.Error}");
        }

        state.Signal.Set();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedError(ErrorType type, byte* message, void* _)
    {
        string errorMessage = ReadNativeMessage(message);
        Console.WriteLine($"[WebGPU Error] Type: {type}, Message: {errorMessage}");
        OnWebGpuError?.Invoke(type, errorMessage);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLost(DeviceLostReason reason, byte* message, void* _)
    {
        if (reason == DeviceLostReason.Destroyed)
        {
            return;
        }

        string errorMessage = ReadNativeMessage(message);
        Console.Error.WriteLine($"[WebGPU Device Lost] Reason: {reason}, Message: {errorMessage}");
        RaiseWebGpuDeviceLost(reason, errorMessage);
    }

    private static string ReadNativeMessage(byte* message) =>
        (message != null ? SilkMarshal.PtrToString((nint)message) : null) ?? "Unknown error";

    private sealed class AdapterRequestState(ManualResetEventSlim signal)
    {
        public ManualResetEventSlim Signal { get; } = signal;
        public nint Result { get; set; }
        public string? Error { get; set; }
    }

    private sealed class DeviceRequestState(ManualResetEventSlim signal)
    {
        public ManualResetEventSlim Signal { get; } = signal;
        public nint Result { get; set; }
        public string? Error { get; set; }
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
        ulong maxBufferSize = DefaultMaxBufferSize)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (Api != null || Device != null || _isDisposed)
            throw new InvalidOperationException("The WebGPU context is already initialized or disposed.");
        if (device == null || queue == null || surface == null)
            throw new ArgumentException("External WebGPU device, queue, and surface handles are required.");

        Api = api;
        BackendKind = WgpuBackendKind.BrowserWebGpu;
        RenderLock = new object();
        Device = device;
        Queue = queue;
        Surface = surface;
        SwapChainFormat = swapChainFormat;
        MaxSampledTexturesPerShaderStage = Math.Max(16, maxSampledTexturesPerShaderStage);
        MaxSamplersPerShaderStage = Math.Max(16, maxSamplersPerShaderStage);
        MaxBindGroups = Math.Max(4, maxBindGroups);
        MaxBufferSize = NormalizeMaxBufferSize(maxBufferSize);
        SupportsReadOnlyAndReadWriteStorageTextures = supportsReadOnlyAndReadWriteStorageTextures;
        _deviceResourceDomain = new WgpuDeviceResourceDomain(Api, Device);
        _isSurfaceConfigured = true;
        _lastWidth = 1;
        _lastHeight = 1;
        SetAdapterSelectionDiagnostics(new WgpuAdapterSelectionDiagnostics(
            string.Empty,
            BackendType.Undefined,
            AdapterType.Unknown,
            string.Empty,
            0,
            0,
            true,
            WgpuAdapterSelectionReason.ExternalBrowserHost));

        lock (_activeContexts)
        {
            if (!_activeContexts.Contains(this)) _activeContexts.Add(this);
        }
        Current = this;
    }

    /// <summary>
    /// Initializes an offscreen context from an exact-ABI native backend.
    /// The opaque handles retain the existing typed renderer contract, while
    /// <paramref name="api"/> owns descriptor translation and
    /// <paramref name="lifetime"/> owns the native instance/device chain.
    /// </summary>
    /// <remarks>
    /// This entry point intentionally has no presentation surface. Imported
    /// external-memory textures are created by the backend on this same
    /// device and supplied as render targets. Initialization is O(1) and does
    /// not probe symbols, use reflection, or copy texture data.
    /// </remarks>
    public void InitializeExternalNativeDevice(
        IWebGpuApi api,
        IWebGpuExternalDeviceLifetime lifetime,
        Device* device,
        Queue* queue,
        TextureFormat preferredRenderTargetFormat,
        uint maxSampledTexturesPerShaderStage = 16,
        uint maxSamplersPerShaderStage = 16,
        uint maxBindGroups = 4,
        bool supportsReadOnlyAndReadWriteStorageTextures = false,
        bool supportsTextureFormatsTier1 = false,
        BackendType adapterBackendType = BackendType.Undefined,
        string? adapterName = null,
        AdapterType adapterType = AdapterType.Unknown,
        string? adapterDriverDescription = null,
        uint adapterVendorId = 0,
        uint adapterDeviceId = 0,
        ulong maxBufferSize = DefaultMaxBufferSize)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (Api != null || Device != null || _isDisposed)
        {
            throw new InvalidOperationException(
                "The WebGPU context is already initialized or disposed.");
        }
        if (device == null || queue == null)
        {
            throw new ArgumentException(
                "External native WebGPU device and queue handles are required.");
        }

        Api = api;
        BackendKind = WgpuBackendKind.DawnNative;
        RenderLock = new object();
        Device = device;
        Queue = queue;
        SwapChainFormat = preferredRenderTargetFormat;
        MaxSampledTexturesPerShaderStage =
            Math.Max(16, maxSampledTexturesPerShaderStage);
        MaxSamplersPerShaderStage =
            Math.Max(16, maxSamplersPerShaderStage);
        MaxBindGroups = Math.Max(4, maxBindGroups);
        MaxBufferSize = NormalizeMaxBufferSize(maxBufferSize);
        SupportsReadOnlyAndReadWriteStorageTextures =
            supportsReadOnlyAndReadWriteStorageTextures;
        SupportsTextureFormatsTier1 =
            supportsTextureFormatsTier1;
        SetAdapterSelectionDiagnostics(new WgpuAdapterSelectionDiagnostics(
            adapterName ?? string.Empty,
            adapterBackendType,
            adapterType,
            adapterDriverDescription ?? string.Empty,
            adapterVendorId,
            adapterDeviceId,
            false,
            WgpuAdapterSelectionReason.ExternalNativeHost));
        _externalDeviceLifetime = lifetime;
        _deviceResourceDomain = new WgpuDeviceResourceDomain(Api, Device);

        lock (_activeContexts)
        {
            if (!_activeContexts.Contains(this))
            {
                _activeContexts.Add(this);
            }
        }
        Current = this;
    }

    /// <summary>
    /// Attaches a presentation surface owned by the exact-ABI external
    /// backend to an already initialized native device.
    /// </summary>
    public void AttachExternalNativePresentationSurface(
        Surface* surface,
        TextureFormat format,
        uint width,
        uint height)
    {
        if (BackendKind != WgpuBackendKind.DawnNative ||
            Api is not IWebGpuExternalSurfaceApi)
        {
            throw new InvalidOperationException(
                "The active external backend does not expose native presentation.");
        }
        if (Surface != null || surface == null)
        {
            throw new InvalidOperationException(
                "A presentation surface is already attached or invalid.");
        }

        Surface = surface;
        SwapChainFormat = format;
        if (!TryConfigureSwapChain(
                Math.Max(1u, width),
                Math.Max(1u, height)))
        {
            Surface = null;
            throw new InvalidOperationException(
                "The external native surface could not be configured.");
        }
    }

    /// <summary>
    /// Creates an additional presentation surface while reusing an initialized context's
    /// instance, adapter, device, and queue. The shared device remains alive until every surface
    /// context has been disposed, regardless of owner disposal order.
    /// Surface creation and configuration are O(1). Immutable device-domain caches can be
    /// shared, while mutable atlases, retained scenes, and render-target state remain local
    /// to each presentation context.
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
        MaxBufferSize = deviceOwner.MaxBufferSize;
        SupportsReadOnlyAndReadWriteStorageTextures = deviceOwner.SupportsReadOnlyAndReadWriteStorageTextures;
        SupportsTextureFormatsTier1 =
            deviceOwner.SupportsTextureFormatsTier1;
        SetAdapterSelectionDiagnostics(deviceOwner.AdapterSelectionDiagnostics);
        _deviceResourceDomain = deviceOwner._deviceResourceDomain;
        _sharedDeviceLifetime = sharedDeviceLifetime;
        RenderLock = deviceOwner.RenderLock;

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

    private void SetAdapterSelectionDiagnostics(
        WgpuAdapterSelectionDiagnostics diagnostics)
    {
        AdapterSelectionDiagnostics = diagnostics;
        AdapterBackendType = diagnostics.BackendType;
        AdapterName = diagnostics.Name;
    }

    /// <summary>
    /// Returns whether both initialized contexts use the same typed WebGPU
    /// device-resource ownership domain.
    /// </summary>
    public bool SharesDeviceWith(WgpuContext? other)
        => other is not null &&
           _deviceResourceDomain is not null &&
           ReferenceEquals(
               _deviceResourceDomain,
               other._deviceResourceDomain);

    public WgpuBindGroupLayoutLease AcquireSharedBindGroupLayout(
        WgpuDeviceResourceKey key,
        BindGroupLayoutDescriptor* descriptor)
    {
        WgpuDeviceResourceDomain domain = DeviceResourceDomain;
        BindGroupLayout* layout =
            domain.AcquireBindGroupLayout(key, descriptor);
        return new WgpuBindGroupLayoutLease(
            this,
            domain,
            key,
            layout);
    }

    public WgpuPipelineLayoutLease AcquireSharedPipelineLayout(
        WgpuDeviceResourceKey key,
        PipelineLayoutDescriptor* descriptor)
    {
        WgpuDeviceResourceDomain domain = DeviceResourceDomain;
        PipelineLayout* layout =
            domain.AcquirePipelineLayout(key, descriptor);
        return new WgpuPipelineLayoutLease(
            this,
            domain,
            key,
            layout);
    }

    public void ConfigureSwapChain(uint width, uint height)
    {
        _ = TryConfigureSwapChain(width, height);
    }

    public bool TryConfigureSwapChain(uint width, uint height, bool refreshCapabilities = false)
    {
        if (Surface != null &&
            Api is IWebGpuExternalSurfaceApi externalSurface)
        {
            uint configuredWidth = Math.Max(1u, width);
            uint configuredHeight = Math.Max(1u, height);
            externalSurface.ConfigureExternalSurface(
                Surface,
                configuredWidth,
                configuredHeight);
            _lastWidth = configuredWidth;
            _lastHeight = configuredHeight;
            _isSurfaceConfigured = true;
            _hasSurfaceConfigurationCapabilities = true;
            SurfaceConfigurationCount++;
            return true;
        }
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

        long configurationStart = Stopwatch.GetTimestamp();

        // Synchronize GLFW window VSync state with WebGPU context VSync state dynamically
        if (_window != null)
        {
            _window.VSync = _vsync;
        }

        // Surface capabilities are stable for the lifetime of a native surface. Keep
        // the selected immutable values across size-only reconfiguration; querying and
        // freeing the native capability arrays on every resize can block the event loop.
        if (refreshCapabilities || !_hasSurfaceConfigurationCapabilities)
        {
            var capabilities = new SurfaceCapabilities();
            Wgpu.SurfaceGetCapabilities(Surface, Adapter, &capabilities);
            try
            {
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
                    _hasSurfaceConfigurationCapabilities = false;
                    return false;
                }

                _cachedSurfaceFormat = ChooseSurfaceFormat(formats);
                _cachedCompositeAlphaMode = ChooseCompositeAlphaMode(
                    _window?.TransparentFramebuffer == true,
                    alphaModes);
                _cachedVsyncPresentMode = ChoosePresentMode(true, presentModes);
                _cachedUncappedPresentMode = ChoosePresentMode(false, presentModes);
                _hasSurfaceConfigurationCapabilities = true;
            }
            finally
            {
                Wgpu.SurfaceCapabilitiesFreeMembers(capabilities);
            }
        }

        TextureFormat swapChainFormat = _cachedSurfaceFormat;
        CompositeAlphaMode alphaMode = _cachedCompositeAlphaMode;
        PresentMode presentMode = _vsync ? _cachedVsyncPresentMode : _cachedUncappedPresentMode;

        ProGpuBackendDiagnostics.WriteLine($"[WebGPU Context] Configuring SwapChain: {width}x{height}, VSync: {_vsync}, Selected Mode: {presentMode}");

        // Configure only the latest physical size selected by the normal render tick.
        var surfaceExtras = new NativeSurfaceConfigurationExtras
        {
            Chain = new ChainedStruct
            {
                SType = (SType)NativeSurfaceConfigurationExtras.STypeValue
            },
            DesiredMaximumFrameLatency = Math.Max(1u, DesiredMaximumFrameLatency)
        };
        var config = new SurfaceConfiguration
        {
            NextInChain = &surfaceExtras.Chain,
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
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(configurationStart).TotalMilliseconds;
        SurfaceConfigurationCount++;
        SurfaceConfigurationTimeMs += elapsedMilliseconds;
        MaximumSurfaceConfigurationTimeMs = Math.Max(MaximumSurfaceConfigurationTimeMs, elapsedMilliseconds);
        return true;
    }

    public static bool CanConfigureSurface(
        ReadOnlySpan<TextureFormat> formats,
        ReadOnlySpan<CompositeAlphaMode> alphaModes,
        ReadOnlySpan<PresentMode> presentModes)
    {
        return !formats.IsEmpty && !alphaModes.IsEmpty && !presentModes.IsEmpty;
    }

    /// <summary>
    /// Chooses an encoded-color presentation target. ProGPU theme, vector, and text
    /// colors are stored as sRGB channel values and the compositor writes those values
    /// directly. A non-sRGB attachment preserves the established desktop and Metal
    /// output; selecting an *Srgb format would encode the channels a second time.
    /// </summary>
    public static TextureFormat ChooseSurfaceFormat(ReadOnlySpan<TextureFormat> formats)
    {
        if (formats.IsEmpty)
            throw new ArgumentException("At least one surface format is required.", nameof(formats));

        ReadOnlySpan<TextureFormat> preferred =
            [TextureFormat.Bgra8Unorm, TextureFormat.Rgba8Unorm];
        for (int preferredIndex = 0; preferredIndex < preferred.Length; preferredIndex++)
        {
            for (int availableIndex = 0; availableIndex < formats.Length; availableIndex++)
            {
                if (formats[availableIndex] == preferred[preferredIndex])
                    return preferred[preferredIndex];
            }
        }

        return formats[0];
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

    private static ulong NormalizeMaxBufferSize(ulong maxBufferSize) =>
        maxBufferSize == 0 ? DefaultMaxBufferSize : maxBufferSize;

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

    public void PollDevice(bool wait)
    {
        lock (RenderLock)
        {
            if (BackendKind == WgpuBackendKind.SilkNative &&
                Device != null &&
                !_isDisposed)
            {
                if (_devicePollAddress == 0)
                {
                    _devicePollAddress =
                        Wgpu.Context.GetProcAddress(
                            "wgpuDevicePoll");
                }

                var poll =
                    (delegate* unmanaged[Cdecl]<
                        Device*,
                        uint,
                        void*,
                        uint>)_devicePollAddress;
                _ = poll(
                    Device,
                    wait ? 1u : 0u,
                    null);
                if (wait)
                {
                    MarkSubmittedWorkDrained();
                }
            }
            else if (BackendKind ==
                         WgpuBackendKind.DawnNative &&
                     Device != null &&
                     !_isDisposed)
            {
                IWebGpuExternalDeviceLifetime? lifetime =
                    _externalDeviceLifetime;
                if (lifetime is not null)
                {
                    lifetime.Poll(wait);
                    if (wait)
                    {
                        MarkSubmittedWorkDrained();
                    }
                }
            }
        }
    }

    private void MarkSubmittedWorkDrained()
    {
        long submittedQueueWork =
            Volatile.Read(ref _queueSubmissionCount);
        Volatile.Write(
            ref _drainedQueueSubmissionCount,
            submittedQueueWork);
        Volatile.Write(
            ref _polledQueueSubmissionCount,
            submittedQueueWork);
    }

    public bool TryCaptureNativeResourceSnapshot(out WgpuNativeResourceSnapshot snapshot)
    {
        snapshot = default;
        if (BackendKind != WgpuBackendKind.SilkNative ||
            Instance == null ||
            _isDisposed)
        {
            return false;
        }

        lock (RenderLock)
        {
            if (_isDisposed || Instance == null)
            {
                return false;
            }

            if (_generateReportAddress == 0)
            {
                _generateReportAddress = Wgpu.Context.GetProcAddress("wgpuGenerateReport");
            }

            if (_generateReportAddress == 0)
            {
                return false;
            }

            WgpuGlobalReportNative report = default;
            var generateReport =
                (delegate* unmanaged[Cdecl]<Instance*, WgpuGlobalReportNative*, void>)
                _generateReportAddress;
            generateReport(Instance, &report);

            WgpuHubReportNative hub = AdapterBackendType switch
            {
                BackendType.Metal => report.Metal,
                BackendType.Vulkan => report.Vulkan,
                BackendType.D3D12 => report.Dx12,
                BackendType.OpenGL => report.Gl,
                _ => default
            };

            snapshot = new WgpuNativeResourceSnapshot(
                WgpuRegistrySnapshot.FromNative(hub.CommandBuffers),
                WgpuRegistrySnapshot.FromNative(hub.Buffers),
                WgpuRegistrySnapshot.FromNative(hub.Textures),
                WgpuRegistrySnapshot.FromNative(hub.TextureViews),
                WgpuRegistrySnapshot.FromNative(hub.BindGroups),
                WgpuRegistrySnapshot.FromNative(hub.BindGroupLayouts),
                WgpuRegistrySnapshot.FromNative(hub.ShaderModules),
                WgpuRegistrySnapshot.FromNative(hub.RenderPipelines),
                WgpuRegistrySnapshot.FromNative(hub.ComputePipelines),
                MacMetalMemory.TryGetCurrentAllocatedBytes(out ulong metalBytes)
                    ? metalBytes
                    : 0);
            return true;
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

            CleanupPendingResources();

            if (!IsDeviceLost)
            {
                WaitIdle();
            }

            if (Current == this)
            {
                Current = null;
            }
            
            lock (_activeContexts)
            {
                _activeContexts.Remove(this);
            }
            
            ReleasePresentationSurfaceCore(waitForDevice: false);

            WgpuDeviceResourceDomain? deviceResourceDomain =
                _deviceResourceDomain;
            SharedDeviceLifetime? sharedDeviceLifetime =
                _sharedDeviceLifetime;
            sharedDeviceLifetime?.Release();
            if (sharedDeviceLifetime is null)
            {
                deviceResourceDomain?.Dispose();
                _externalDeviceLifetime?.Dispose();
            }
            _sharedDeviceLifetime = null;
            _externalDeviceLifetime = null;
            ExternalTextureImporter = null;
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
        _deviceResourceDomain = null;
    }

    private sealed class SharedDeviceLifetime
    {
        private readonly object _sync = new();
        private WebGPU? _wgpu;
        private Instance* _instance;
        private WgpuAdapter* _adapter;
        private Device* _device;
        private Queue* _queue;
        private WgpuDeviceResourceDomain? _deviceResourceDomain;
        private int _referenceCount = 1;

        public SharedDeviceLifetime(
            WebGPU wgpu,
            Instance* instance,
            WgpuAdapter* adapter,
            Device* device,
            Queue* queue,
            WgpuDeviceResourceDomain deviceResourceDomain)
        {
            _wgpu = wgpu;
            _instance = instance;
            _adapter = adapter;
            _device = device;
            _queue = queue;
            _deviceResourceDomain = deviceResourceDomain;
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
            WgpuAdapter* adapter;
            Device* device;
            Queue* queue;
            WgpuDeviceResourceDomain? deviceResourceDomain;
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
                deviceResourceDomain = _deviceResourceDomain;
                _wgpu = null;
                _instance = null;
                _adapter = null;
                _device = null;
                _queue = null;
                _deviceResourceDomain = null;
            }

            if (wgpu == null)
            {
                return;
            }

            deviceResourceDomain?.Dispose();
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
