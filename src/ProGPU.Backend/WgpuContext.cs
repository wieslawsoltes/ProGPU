using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

public unsafe class WgpuContext : IDisposable
{
    public WebGPU Wgpu { get; private set; } = null!;
    public Instance* Instance { get; private set; } = null;
    public Adapter* Adapter { get; private set; } = null;
    public Device* Device { get; private set; } = null;
    public Queue* Queue { get; private set; } = null;
    public Surface* Surface { get; private set; } = null;
    public TextureFormat SwapChainFormat { get; private set; } = TextureFormat.Bgra8Unorm;

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

            IntPtr[] buffers;
            IntPtr[] textures;
            IntPtr[] views;
            IntPtr[] bindGroups;
            IntPtr[] layouts;
            IntPtr[] pipeLayouts;
            IntPtr[] renderPipes;
            IntPtr[] computePipes;
            IntPtr[] samplers;
            IntPtr[] shaders;

            lock (DisposalLock)
            {
                buffers = PendingBuffers.ToArray();
                PendingBuffers.Clear();

                textures = PendingTextures.ToArray();
                PendingTextures.Clear();

                views = PendingTextureViews.ToArray();
                PendingTextureViews.Clear();

                bindGroups = PendingBindGroups.ToArray();
                PendingBindGroups.Clear();

                layouts = PendingBindGroupLayouts.ToArray();
                PendingBindGroupLayouts.Clear();

                pipeLayouts = PendingPipelineLayouts.ToArray();
                PendingPipelineLayouts.Clear();

                renderPipes = PendingRenderPipelines.ToArray();
                PendingRenderPipelines.Clear();

                computePipes = PendingComputePipelines.ToArray();
                PendingComputePipelines.Clear();

                samplers = PendingSamplers.ToArray();
                PendingSamplers.Clear();

                shaders = PendingShaderModules.ToArray();
                PendingShaderModules.Clear();
            }

            if (views.Length > 0 || textures.Length > 0 || buffers.Length > 0 || bindGroups.Length > 0 || 
                layouts.Length > 0 || pipeLayouts.Length > 0 || renderPipes.Length > 0 || 
                computePipes.Length > 0 || samplers.Length > 0 || shaders.Length > 0)
            {
                WaitIdle();
            }

            foreach (var view in views)
            {
                Wgpu.TextureViewRelease((TextureView*)view);
            }

            foreach (var tex in textures)
            {
                Wgpu.TextureDestroy((Texture*)tex);
                Wgpu.TextureRelease((Texture*)tex);
            }

            foreach (var buf in buffers)
            {
                Wgpu.BufferDestroy((Silk.NET.WebGPU.Buffer*)buf);
                Wgpu.BufferRelease((Silk.NET.WebGPU.Buffer*)buf);
            }

            foreach (var bg in bindGroups)
            {
                Wgpu.BindGroupRelease((BindGroup*)bg);
            }

            foreach (var layout in layouts)
            {
                Wgpu.BindGroupLayoutRelease((BindGroupLayout*)layout);
            }

            foreach (var pipeLayout in pipeLayouts)
            {
                Wgpu.PipelineLayoutRelease((PipelineLayout*)pipeLayout);
            }

            foreach (var rp in renderPipes)
            {
                Wgpu.RenderPipelineRelease((RenderPipeline*)rp);
            }

            foreach (var cp in computePipes)
            {
                Wgpu.ComputePipelineRelease((ComputePipeline*)cp);
            }

            foreach (var sampler in samplers)
            {
                Wgpu.SamplerRelease((Sampler*)sampler);
            }

            foreach (var shader in shaders)
            {
                Wgpu.ShaderModuleRelease((ShaderModule*)shader);
            }
        }
    }
    
    private bool _isDisposed;
    public bool IsDisposed => _isDisposed;
    private uint _lastWidth = 1;
    private uint _lastHeight = 1;
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

    [ThreadStatic]
    private static WgpuContext? _current;

    public static WgpuContext? Current
    {
        get => _current;
        set => _current = value;
    }

    private IWindow? _window;
    public IWindow? Window => _window;

    private static readonly object s_instanceLock = new();
    private static Instance* s_globalInstance = null;

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
        lock (_activeContexts)
        {
            if (!_activeContexts.Contains(this))
            {
                _activeContexts.Add(this);
            }
        }
        Current = this;
        _window = window;
        Wgpu = WebGPU.GetApi();
        
        // 1. Create WebGPU Instance (shared statically)
        SafeLog("[WGPUCONTEXT] Getting WebGPU Instance\n");
        lock (s_instanceLock)
        {
            if (s_globalInstance == null)
            {
                var instanceDesc = new InstanceDescriptor();
                s_globalInstance = Wgpu.CreateInstance(&instanceDesc);
                if (s_globalInstance == null)
                {
                    throw new InvalidOperationException("Failed to create WebGPU Instance.");
                }
            }
            Instance = s_globalInstance;
        }

        // 2. Create Surface if window is provided
        if (window != null)
        {
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

        var deviceDesc = new DeviceDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("ProGPU Primary Device")
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

        // 5. Retrieve Default Queue
        SafeLog("[WGPUCONTEXT] Getting Default Queue\n");
        Queue = Wgpu.DeviceGetQueue(Device);

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
    }

    public void ConfigureSwapChain(uint width, uint height)
    {
        if (Surface == null || Device == null) return;

        _lastWidth = width;
        _lastHeight = height;

        // Synchronize GLFW window VSync state with WebGPU context VSync state dynamically
        if (_window != null)
        {
            _window.VSync = _vsync;
        }

        // 7a. Query supported formats
        var capabilities = new SurfaceCapabilities();
        Wgpu.SurfaceGetCapabilities(Surface, Adapter, &capabilities);
        
        SwapChainFormat = TextureFormat.Bgra8Unorm; // Default fallback
        if (capabilities.FormatCount > 0 && capabilities.Formats != null)
        {
            // Prefer Bgra8Unorm or Rgba8Unorm
            SwapChainFormat = capabilities.Formats[0];
            for (uint i = 0; i < capabilities.FormatCount; i++)
            {
                if (capabilities.Formats[i] == TextureFormat.Bgra8Unorm)
                {
                    SwapChainFormat = TextureFormat.Bgra8Unorm;
                    break;
                }
            }
        }

        // Get standard composite alpha mode
        var alphaMode = CompositeAlphaMode.Opaque;
        if (capabilities.AlphaModeCount > 0 && capabilities.AlphaModes != null)
        {
            alphaMode = capabilities.AlphaModes[0];
        }

        PresentMode presentMode = PresentMode.Fifo;
        if (!_vsync)
        {
            // Prefer Immediate mode directly for truly uncapped framerates (tearing allowed),
            // which bypasses driver and OS compositor presentation queuing / mailbox locks.
            bool foundImmediate = false;
            if (capabilities.PresentModeCount > 0 && capabilities.PresentModes != null)
            {
                for (uint i = 0; i < capabilities.PresentModeCount; i++)
                {
                    if (capabilities.PresentModes[i] == PresentMode.Immediate)
                    {
                        presentMode = PresentMode.Immediate;
                        foundImmediate = true;
                        break;
                    }
                }
            }

            if (!foundImmediate)
            {
                // Force Immediate when VSync is off to guarantee uncapped framerates
                presentMode = PresentMode.Immediate;
            }
        }

        Console.WriteLine($"[WebGPU Context] Configuring SwapChain: {width}x{height}, VSync: {_vsync}, Selected Mode: {presentMode}");

        Wgpu.SurfaceCapabilitiesFreeMembers(capabilities);

        // 7b. Surface Configuration
        var config = new SurfaceConfiguration
        {
            Device = Device,
            Format = SwapChainFormat,
            Usage = TextureUsage.RenderAttachment,
            AlphaMode = alphaMode,
            PresentMode = presentMode,
            Width = width > 0 ? width : 1,
            Height = height > 0 ? height : 1
        };

        Wgpu.SurfaceConfigure(Surface, &config);
    }

    public void ReconfigureIfNeeded(uint width, uint height)
    {
        if (width != _lastWidth || height != _lastHeight)
        {
            ConfigureSwapChain(width, height);
        }
    }

    [DllImport("wgpu_native", EntryPoint = "wgpuDevicePoll")]
    private static extern unsafe bool wgpuDevicePoll(Device* device, bool wait, void* wrappedSubmissionIndex);

    public void WaitIdle()
    {
        if (Device != null && !_isDisposed)
        {
            wgpuDevicePoll(Device, true, null);
        }
    }

    public bool VerifyShaderModule(ShaderModule* module, out string errors)
    {
        errors = "";
        if (module == null || Device == null || _isDisposed) return false;

        try
        {
            System.IO.File.AppendAllText(
                "/Users/wieslawsoltes/GitHub/ProGPU/debug.txt",
                $"[VerifyShaderModule] Skipping compilation info check because it is unimplemented in wgpu-native.\n"
            );
        }
        catch {}

        return true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        lock (RenderLock)
        {
            if (_isDisposed) return;

            Disposing?.Invoke(this);

            CleanupPendingResources();

            WaitIdle();

            if (Current == this)
            {
                Current = null;
            }
            
            lock (_activeContexts)
            {
                _activeContexts.Remove(this);
            }
            
            if (Device != null)
            {
                Wgpu.DeviceRelease(Device);
                Device = null;
            }
            if (Adapter != null)
            {
                Wgpu.AdapterRelease(Adapter);
                Adapter = null;
            }
            if (Surface != null)
            {
                Wgpu.SurfaceRelease(Surface);
                Surface = null;
            }
            // Keep shared s_globalInstance alive; do not release it here.
            Instance = null;
            
            _isDisposed = true;
        }
        
        GC.SuppressFinalize(this);
    }

    ~WgpuContext()
    {
        // Do not call Dispose() or native WebGPU release APIs during finalization.
        // During process exit or AssemblyLoadContext unload, the native wgpu_native library 
        // may already be unloaded, causing native entry point calls to crash with a segfault (139).
    }
}
