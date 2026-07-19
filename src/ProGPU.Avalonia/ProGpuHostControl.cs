using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Numerics;
using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Backend;
using ProGPU.Scene;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SilkInput = Silk.NET.Input;
using Avalonia.Rendering.Composition;

// Resolve ambiguous namespaces between Avalonia, Silk.NET, and ProGPU
using AvaloniaRect = Avalonia.Rect;
using AvaloniaCompositor = Avalonia.Rendering.Composition.Compositor;
using WinuiRect = ProGPU.Scene.Rect;
using WinuiCompositor = ProGPU.Scene.Compositor;
using AvaloniaDrawingContext = Avalonia.Media.DrawingContext;
using GpuBuffer = Silk.NET.WebGPU.Buffer;
using AvaloniaVector = Avalonia.Vector;
using PointerDeviceType = Windows.Devices.Input.PointerDeviceType;

namespace ProGPU.Avalonia;

public enum ProGpuAvaloniaPresentationMode
{
    None,
    ZeroCopySharedTexture,
    CustomVisualReadback
}

public readonly record struct ProGpuAvaloniaHostFrameState(
    CompositorHostFrame HostFrame,
    ProGpuAvaloniaPresentationMode PresentationMode,
    ulong PresentedFrameCount,
    ulong ZeroCopyPresentedFrameCount,
    ulong ReadbackPresentedFrameCount,
    bool IsZeroCopyActive,
    bool IsCustomVisualFallbackActive,
    string GpuHandleType)
{
    public static ProGpuAvaloniaHostFrameState Empty { get; } = new(
        default,
        ProGpuAvaloniaPresentationMode.None,
        0,
        0,
        0,
        false,
        false,
        string.Empty);

    public bool HasPresentedFrame => PresentedFrameCount > 0 && HostFrame.IsValid;
}

public class ProGpuHostControl : Control
{
    private class SwapchainImage : IDisposable
    {
        public IntPtr SharedHandle = IntPtr.Zero;
        public ICompositionImportedGpuImage? ImportedImage;
        public GpuTexture? WgpuTexture;
        public IntPtr StagingBuffer = IntPtr.Zero;
        public uint StagingBufferSize;
        public uint BytesPerRow;
        public bool IsReady = true;
        public bool IsStagingBufferMapActive;

        // Windows specific
        public IntPtr WinD3DDevice = IntPtr.Zero;
        public IntPtr WinTexture2D = IntPtr.Zero;

        private readonly WgpuContext _context;

        public SwapchainImage(WgpuContext context)
        {
            _context = context;
        }

        public unsafe void Dispose()
        {
            if (ImportedImage != null)
            {
                _ = ImportedImage.DisposeAsync();
                ImportedImage = null;
            }

            if (SharedHandle != IntPtr.Zero)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    GpuSharingInterop.ReleaseMacSharedSurface(SharedHandle);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    GpuSharingInterop.ReleaseWindowsSharedTexture(WinD3DDevice, WinTexture2D);
                }
                SharedHandle = IntPtr.Zero;
                WinD3DDevice = IntPtr.Zero;
                WinTexture2D = IntPtr.Zero;
            }

            if (WgpuTexture != null)
            {
                WgpuTexture.Dispose();
                WgpuTexture = null;
            }

            if (StagingBuffer != IntPtr.Zero)
            {
                if (!_context.IsDisposed)
                {
                    if (IsStagingBufferMapActive)
                    {
                        _context.Api.BufferUnmap((GpuBuffer*)StagingBuffer);
                        IsStagingBufferMapActive = false;
                    }

                    _context.QueueBufferDisposal(StagingBuffer);
                }

                StagingBuffer = IntPtr.Zero;
                StagingBufferSize = 0;
                BytesPerRow = 0;
                IsStagingBufferMapActive = false;
            }
        }
    }

    // Dependency properties
    public static readonly StyledProperty<FrameworkElement?> WinuiRootProperty =
        AvaloniaProperty.Register<ProGpuHostControl, FrameworkElement?>(nameof(WinuiRoot));

    public FrameworkElement? WinuiRoot
    {
        get => GetValue(WinuiRootProperty);
        set => SetValue(WinuiRootProperty, value);
    }

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<ProGpuHostControl, CornerRadius>(nameof(CornerRadius));

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool EnableZeroCopy { get; set; }

    public WgpuContext? WgpuContext => _wgpuContext;
    public WinuiCompositor? Compositor => _compositor;

    public ProGpuAvaloniaHostFrameState LastPresentedFrameState
    {
        get
        {
            lock (_frameStateLock)
            {
                return _lastPresentedFrameState;
            }
        }
    }

    // Core ProGPU context
    private WgpuContext? _wgpuContext;
    private WinuiCompositor? _compositor;
    private WindowInputState? _winuiInputState;
    private SharedContextLease? _contextLease;
    private readonly object _frameStateLock = new();
    private ProGpuAvaloniaHostFrameState _lastPresentedFrameState = ProGpuAvaloniaHostFrameState.Empty;
    private ulong _presentedFrameCount;
    private ulong _zeroCopyPresentedFrameCount;
    private ulong _readbackPresentedFrameCount;

    private sealed class SharedContextState
    {
        public int ReferenceCount;
        public bool OwnsContext;
    }

    private sealed class SharedContextLease : IDisposable
    {
        private bool _isDisposed;

        public WgpuContext Context { get; }

        public SharedContextLease(WgpuContext context)
        {
            Context = context;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            ReleaseSharedContext(Context);
        }
    }

    private static readonly object s_contextLeaseLock = new();
    private static readonly Dictionary<WgpuContext, SharedContextState> s_contextLeases = new();

    // Custom Visual references
    private CompositionCustomVisual? _customVisual;
    private ProGpuCustomVisualHandler? _customVisualHandler;

    // Zero-Copy Shared Texture states
    private bool _isZeroCopySupported;
    private ICompositionGpuInterop? _gpuInterop;
    private string _gpuHandleType = "";
    private AvaloniaCompositor? _compositionCompositor;
    private CompositionSurfaceVisual? _surfaceVisual;
    private CompositionDrawingSurface? _drawingSurface;
    private uint _lastSharedWidth;
    private uint _lastSharedHeight;
    private SwapchainImage[]? _swapchainImages;
    private int _currentWriteImageIndex = 0;

    // Background Device Polling Thread and Mapping
    private Thread? _pollingThread;
    private CancellationTokenSource? _pollingCts;
    private static readonly PfnBufferMapCallback s_mapCallback;
    private bool _isRendering = false;
    private bool _renderRequested = false;
    private bool _renderDispatchQueued = false;

    private static unsafe void OnMapCallback(BufferMapAsyncStatus status, void* userData)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)userData);
        if (handle.IsAllocated)
        {
            var tcs = (TaskCompletionSource<bool>)handle.Target!;
            handle.Free();
            if (status == BufferMapAsyncStatus.Success)
            {
                tcs.TrySetResult(true);
            }
            else
            {
                tcs.TrySetException(new Exception($"Buffer map failed: {status}"));
            }
        }
    }

    private Task MapBufferAsync(IntPtr buffer, MapMode mode, nuint size)
    {
        unsafe
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handle = GCHandle.Alloc(tcs);
            var userData = (void*)GCHandle.ToIntPtr(handle);
            _wgpuContext!.Api.BufferMapAsync((GpuBuffer*)buffer, mode, 0, size, s_mapCallback, userData);
            return tcs.Task;
        }
    }

    private void StartPolling()
    {
        if (_pollingThread != null) return;
        _pollingCts = new CancellationTokenSource();
        var token = _pollingCts.Token;

        _pollingThread = new Thread(() =>
        {
            unsafe
            {
                while (!token.IsCancellationRequested)
                {
                    if (_wgpuContext != null && _wgpuContext.Device != null)
                    {
                        _wgpuContext.PollDevice(wait: false);
                    }
                    Thread.Sleep(2);
                }
            }
        })
        {
            IsBackground = true,
            Name = "ProGpuDevicePolling"
        };
        _pollingThread.Start();
    }

    private void StopPolling()
    {
        _pollingCts?.Cancel();
        _pollingThread?.Join(500);
        _pollingThread = null;
        _pollingCts = null;
    }

    // State tracking
    private bool _isInitialized;
    private double _lastDpiScale = 1.0;
    private uint _renderWidth;
    private uint _renderHeight;

    static unsafe ProGpuHostControl()
    {
        // Enable focus by default
        FocusableProperty.OverrideDefaultValue<ProGpuHostControl>(true);
        AffectsRender<ProGpuHostControl>(WinuiRootProperty);
        AffectsRender<ProGpuHostControl>(CornerRadiusProperty);
        s_mapCallback = PfnBufferMapCallback.From(OnMapCallback);
    }

    public ProGpuHostControl()
    {
    }

    private static SharedContextLease AcquireSharedContext()
    {
        lock (s_contextLeaseLock)
        {
            WgpuContext.TryGetFirstActiveContext(out var context);

            var ownsContext = false;
            if (context == null)
            {
                context = new WgpuContext();
                context.Initialize(null);
                ownsContext = true;
            }

            if (!s_contextLeases.TryGetValue(context, out var state))
            {
                state = new SharedContextState
                {
                    ReferenceCount = 0,
                    OwnsContext = ownsContext
                };
                s_contextLeases[context] = state;
            }

            state.ReferenceCount++;
            WgpuContext.Current = context;
            return new SharedContextLease(context);
        }
    }

    private static void ReleaseSharedContext(WgpuContext context)
    {
        bool disposeContext = false;
        lock (s_contextLeaseLock)
        {
            if (s_contextLeases.TryGetValue(context, out var state))
            {
                state.ReferenceCount--;
                if (state.ReferenceCount <= 0)
                {
                    s_contextLeases.Remove(context);
                    disposeContext = state.OwnsContext;
                }
            }
        }

        if (disposeContext)
        {
            context.Dispose();
        }
    }

    private CompositorHostFrame CreateHostFrame()
    {
        return CreateHostFrame(Bounds.Size);
    }

    private CompositorHostFrame CreateHostFrame(Size logicalSize)
    {
        double dpi = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        return CompositorHostFrame.FromLogicalSize(logicalSize.Width, logicalSize.Height, dpi);
    }

    private void StoreFrameMetrics(CompositorHostFrame frame)
    {
        _lastDpiScale = frame.DpiScale;
        _renderWidth = frame.RenderTargetWidth;
        _renderHeight = frame.RenderTargetHeight;
    }

    private void RecordPresentedFrame(CompositorHostFrame frame, ProGpuAvaloniaPresentationMode mode)
    {
        if (!frame.IsValid || mode == ProGpuAvaloniaPresentationMode.None)
        {
            return;
        }

        lock (_frameStateLock)
        {
            _presentedFrameCount++;
            if (mode == ProGpuAvaloniaPresentationMode.ZeroCopySharedTexture)
            {
                _zeroCopyPresentedFrameCount++;
            }
            else if (mode == ProGpuAvaloniaPresentationMode.CustomVisualReadback)
            {
                _readbackPresentedFrameCount++;
            }

            _lastPresentedFrameState = new ProGpuAvaloniaHostFrameState(
                frame,
                mode,
                _presentedFrameCount,
                _zeroCopyPresentedFrameCount,
                _readbackPresentedFrameCount,
                _isZeroCopySupported,
                _customVisual != null,
                _gpuHandleType);
        }
    }

    private void RecordReadbackPresentedFrame(CompositorHostFrame frame)
    {
        RecordPresentedFrame(frame, ProGpuAvaloniaPresentationMode.CustomVisualReadback);
    }

    private void OnThemeChanged()
    {
        WinuiRoot?.NotifyThemeChanged();
        QueueRenderUpdate();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;

        StoreFrameMetrics(CreateHostFrame(e.NewSize));

        if (_customVisual != null)
        {
            _customVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        }
        else if (_surfaceVisual != null)
        {
            _surfaceVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        }

        UpdateClipGeometry();

        if (_isInitialized)
        {
            QueueRenderUpdate();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InitializeGraphics();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CleanupGraphics();
        base.OnDetachedFromVisualTree(e);
    }

    private void InitializeGraphics()
    {
        if (_isInitialized) return;

        // 1. Initialize or reuse Headless/Offscreen WebGPU Context
        _contextLease = AcquireSharedContext();
        _wgpuContext = _contextLease.Context;
        StartPolling();

        // 2. Initialize Compositor targeting BGRA8Unorm texture formats
        _compositor = new WinuiCompositor(_wgpuContext, TextureFormat.Bgra8Unorm);

        // 3. Initialize WinUI Input state for events routing
        _winuiInputState = new WindowInputState
        {
            Root = WinuiRoot
        };
 
        // 4. Setup initial drawing bounds
        StoreFrameMetrics(CreateHostFrame());

        // 5. Setup Composition Custom Visual
        SetupCompositionSurface();
        Dispatcher.UIThread.Post(QueueRenderUpdate, DispatcherPriority.Render);

        ThemeManager.ThemeChanged += OnThemeChanged;

        _isInitialized = true;
    }

    private void SetupCompositionSurface()
    {
        var visual = ElementComposition.GetElementVisual(this);
        if (visual?.Compositor is { } compositor)
        {
            _ = SetupCompositionSurfaceAsync(compositor);
        }
    }

    private async Task SetupCompositionSurfaceAsync(AvaloniaCompositor compositor)
    {
        var expectedContext = _wgpuContext;
        var expectedCompositor = _compositor;
        _compositionCompositor = compositor;

        ICompositionGpuInterop? interop = null;
        try
        {
            interop = await compositor.TryGetCompositionGpuInterop();
        }
        catch
        {
            // Fallback gracefully
        }

        bool useSharedTexture = false;
        string handleType = "";

        if (interop != null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                interop.SupportedImageHandleTypes.Contains("IOSurfaceRef"))
            {
                useSharedTexture = true;
                handleType = "IOSurfaceRef";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                     interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle))
            {
                useSharedTexture = true;
                handleType = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle;
            }
        }

        if (!IsCompositionSurfaceSetupCurrent(compositor, expectedContext, expectedCompositor))
        {
            return;
        }

        if (EnableZeroCopy && useSharedTexture && interop != null)
        {
            UseZeroCopyCompositionSurface(compositor, interop, handleType);
        }
        else
        {
            UseCustomVisualFallback(compositor);
        }
    }

    private bool IsCompositionSurfaceSetupCurrent(
        AvaloniaCompositor compositionCompositor,
        WgpuContext? context,
        WinuiCompositor? compositor)
    {
        return context != null &&
               compositor != null &&
               !context.IsDisposed &&
               ReferenceEquals(_compositionCompositor, compositionCompositor) &&
               ReferenceEquals(_wgpuContext, context) &&
               ReferenceEquals(_compositor, compositor);
    }

    private void UseZeroCopyCompositionSurface(
        AvaloniaCompositor compositor,
        ICompositionGpuInterop interop,
        string handleType)
    {
        _isZeroCopySupported = true;
        _gpuInterop = interop;
        _gpuHandleType = handleType;

        DisposeCustomVisualFallback();

        _surfaceVisual = compositor.CreateSurfaceVisual();
        _drawingSurface = compositor.CreateDrawingSurface();
        _surfaceVisual.Surface = _drawingSurface;

        ElementComposition.SetElementChildVisual(this, _surfaceVisual);
        _surfaceVisual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);

        QueueRenderUpdate();
    }

    private void UseCustomVisualFallback(AvaloniaCompositor compositor)
    {
        _isZeroCopySupported = false;
        _gpuInterop = null;
        _gpuHandleType = "";

        ReleaseSharedResources();
        ResetSharedResourceSize();

        if (_surfaceVisual != null || _drawingSurface != null)
        {
            ElementComposition.SetElementChildVisual(this, null);
            _surfaceVisual = null;
            _drawingSurface?.Dispose();
            _drawingSurface = null;
        }

        if (_wgpuContext == null || _compositor == null || _wgpuContext.IsDisposed)
        {
            DisposeCustomVisualFallback();
            return;
        }

        if (_customVisualHandler != null && !_customVisualHandler.Matches(_wgpuContext, _compositor))
        {
            DisposeCustomVisualFallback();
        }

        _customVisualHandler ??= new ProGpuCustomVisualHandler(_wgpuContext, _compositor, RecordReadbackPresentedFrame);
        _customVisual ??= compositor.CreateCustomVisual(_customVisualHandler);
        ElementComposition.SetElementChildVisual(this, _customVisual);

        _customVisual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);

        QueueRenderUpdate();
    }

    private bool TryUseCustomVisualFallback()
    {
        var compositor = _compositionCompositor ?? ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
        {
            return false;
        }

        UseCustomVisualFallback(compositor);
        return _customVisual != null;
    }

    private void CleanupGraphics()
    {
        if (!_isInitialized) return;

        StopPolling();

        ThemeManager.ThemeChanged -= OnThemeChanged;

        DisposeCustomVisualFallback();

        if (_surfaceVisual != null)
        {
            ElementComposition.SetElementChildVisual(this, null);
            _surfaceVisual = null;
            _drawingSurface?.Dispose();
            _drawingSurface = null;

            ReleaseSharedResources();

        }

        _compositionCompositor = null;
        _isZeroCopySupported = false;
        _gpuInterop = null;
        _gpuHandleType = "";

        _compositor?.Dispose();
        _compositor = null;
        _contextLease?.Dispose();
        _contextLease = null;

        if (_winuiInputState != null)
        {
            _winuiInputState.Root = null;
            _winuiInputState.HoveredElement = null;
            _winuiInputState.FocusedElement = null;
            _winuiInputState.CapturedElement = null;
            _winuiInputState.HoveredElementForTimer = null;
            _winuiInputState.ActiveToolTip = null;
            _winuiInputState.HoverCancellation?.Cancel();
            _winuiInputState.HoverCancellation = null;

            if (InputSystem.Current == _winuiInputState)
            {
                InputSystem.Current = null!;
            }
            _winuiInputState = null;
        }

        _wgpuContext = null;

        _isInitialized = false;
    }

    private void DisposeCustomVisualFallback()
    {
        if (_customVisual != null)
        {
            _customVisual.SendHandlerMessage("DISPOSE");
            ElementComposition.SetElementChildVisual(this, null);
            _customVisual = null;
        }

        _customVisualHandler = null;
    }

    private bool ResizeSharedResources(uint width, uint height)
    {
        if (width == _lastSharedWidth && height == _lastSharedHeight && _swapchainImages != null)
            return HasUsableSharedResources();

        ReleaseSharedResources();
        ResetSharedResourceSize();

        _swapchainImages = new SwapchainImage[2];
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var image = new SwapchainImage(_wgpuContext!);
                _swapchainImages[i] = image;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    image.SharedHandle = GpuSharingInterop.CreateMacSharedSurface(width, height);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    image.SharedHandle = GpuSharingInterop.CreateWindowsSharedTexture(width, height, out image.WinD3DDevice, out image.WinTexture2D);
                }

                if (!TryImportSharedImage(image, width, height))
                {
                    ReleaseSharedResources();
                    ResetSharedResourceSize();
                    return false;
                }

                // Allocate WebGPU representation of the shared texture
                image.WgpuTexture = new GpuTexture(
                    _wgpuContext!,
                    width,
                    height,
                    TextureFormat.Bgra8Unorm,
                    TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.TextureBinding,
                    $"Shared Zero-Copy Target {i}",
                    alphaMode: GpuTextureAlphaMode.Premultiplied
                );

                // Setup staging buffer
                uint bytesPerPixel = 4;
                uint unalignedBytesPerRow = width * bytesPerPixel;
                image.BytesPerRow = (unalignedBytesPerRow + 255) & ~255u;
                uint requiredBufferSize = image.BytesPerRow * height;
                image.StagingBufferSize = requiredBufferSize;

                unsafe
                {
                    var bufferDesc = new BufferDescriptor
                    {
                        Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
                        Size = requiredBufferSize,
                        MappedAtCreation = false
                    };
                    image.StagingBuffer = (IntPtr)_wgpuContext!.Api.DeviceCreateBuffer(_wgpuContext!.Device, &bufferDesc);
                }
            }

            _lastSharedWidth = width;
            _lastSharedHeight = height;
            _currentWriteImageIndex = 0;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Falling back from zero-copy composition after shared image setup failed: {ex}");
            ReleaseSharedResources();
            ResetSharedResourceSize();
            return false;
        }
    }

    private bool TryImportSharedImage(SwapchainImage image, uint width, uint height)
    {
        if (image.SharedHandle == IntPtr.Zero || _gpuInterop == null)
        {
            return false;
        }

        try
        {
            var props = new PlatformGraphicsExternalImageProperties
            {
                Width = (int)width,
                Height = (int)height,
                Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                TopLeftOrigin = true
            };

            var platformHandle = new PlatformHandle(image.SharedHandle, _gpuHandleType);
            image.ImportedImage = _gpuInterop.ImportImage(platformHandle, props);
            return image.ImportedImage != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Composition shared image import failed: {ex}");
            image.ImportedImage = null;
            return false;
        }
    }

    private bool HasUsableSharedResources()
    {
        if (_swapchainImages == null)
        {
            return false;
        }

        foreach (var image in _swapchainImages)
        {
            if (image?.ImportedImage == null || image.WgpuTexture == null || image.StagingBuffer == IntPtr.Zero)
            {
                return false;
            }
        }

        return true;
    }

    private void ResetSharedResourceSize()
    {
        _lastSharedWidth = 0;
        _lastSharedHeight = 0;
        _currentWriteImageIndex = 0;
    }

    private void ReleaseSharedResources()
    {
        if (_swapchainImages != null)
        {
            foreach (var img in _swapchainImages)
            {
                img?.Dispose();
            }
            _swapchainImages = null;
        }
    }

    private unsafe void CopyTextureToStagingBuffer(SwapchainImage image, uint renderWidth, uint renderHeight)
    {
        var encoderDesc = new CommandEncoderDescriptor();
        var encoder = _wgpuContext!.Api.DeviceCreateCommandEncoder(_wgpuContext.Device, &encoderDesc);
        
        var copySrc = new ImageCopyTexture
        {
            Texture = image.WgpuTexture!.TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
            Aspect = TextureAspect.All
        };
        
        var copyDst = new ImageCopyBuffer
        {
            Buffer = (GpuBuffer*)image.StagingBuffer,
            Layout = new TextureDataLayout
            {
                Offset = 0,
                BytesPerRow = image.BytesPerRow,
                RowsPerImage = renderHeight
            }
        };
        
        var copySize = new Extent3D
        {
            Width = renderWidth,
            Height = renderHeight,
            DepthOrArrayLayers = 1
        };
        
        _wgpuContext.Api.CommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &copySize);
        
        var cmdBufferDesc = new CommandBufferDescriptor();
        var cmdBuffer = _wgpuContext.Api.CommandEncoderFinish(encoder, &cmdBufferDesc);
        
        _wgpuContext.Api.QueueSubmit(_wgpuContext.Queue, 1, &cmdBuffer);
        _wgpuContext.Api.CommandBufferRelease(cmdBuffer);
        _wgpuContext.Api.CommandEncoderRelease(encoder);
    }

    private unsafe void CopyMappedToSharedTexture(WgpuContext context, SwapchainImage image, uint renderWidth, uint renderHeight)
    {
        void* mappedPtr = context.Api.BufferGetConstMappedRange((GpuBuffer*)image.StagingBuffer, 0, (nuint)image.StagingBufferSize);
        try
        {
            if (mappedPtr != null)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    GpuSharingInterop.IOSurfaceLock(image.SharedHandle, 0, null);
                    void* destPtr = GpuSharingInterop.IOSurfaceGetBaseAddress(image.SharedHandle);
                    nuint surfaceBytesPerRow = GpuSharingInterop.IOSurfaceGetBytesPerRow(image.SharedHandle);

                    byte* srcBytes = (byte*)mappedPtr;
                    byte* destBytes = (byte*)destPtr;
                    uint rowBytes = renderWidth * 4;

                    for (uint y = 0; y < renderHeight; y++)
                    {
                        byte* srcRow = srcBytes + (y * image.BytesPerRow);
                        byte* destRow = destBytes + (y * (uint)surfaceBytesPerRow);
                        System.Buffer.MemoryCopy(srcRow, destRow, rowBytes, rowBytes);
                    }

                    GpuSharingInterop.IOSurfaceUnlock(image.SharedHandle, 0, null);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    GpuSharingInterop.COMHelper.CallGetImmediateContext(image.WinD3DDevice, out IntPtr d3dContext);
                    if (d3dContext != IntPtr.Zero)
                    {
                        GpuSharingInterop.COMHelper.CallUpdateSubresource(
                            d3dContext,
                            image.WinTexture2D,
                            0,
                            IntPtr.Zero,
                            mappedPtr,
                            image.BytesPerRow,
                            0
                        );
                        GpuSharingInterop.COMHelper.CallRelease(d3dContext);
                    }
                }
            }
        }
        finally
        {
            if (!context.IsDisposed && image.StagingBuffer != IntPtr.Zero && image.IsStagingBufferMapActive)
            {
                context.Api.BufferUnmap((GpuBuffer*)image.StagingBuffer);
                image.IsStagingBufferMapActive = false;
            }
        }
    }

    private unsafe void TryUnmapStagingBuffer(WgpuContext context, SwapchainImage image)
    {
        if (context.IsDisposed || image.StagingBuffer == IntPtr.Zero || !image.IsStagingBufferMapActive)
        {
            return;
        }

        context.Api.BufferUnmap((GpuBuffer*)image.StagingBuffer);
        image.IsStagingBufferMapActive = false;
    }

    private bool IsCurrentZeroCopyFrame(
        SwapchainImage[] swapchainImages,
        int imageIndex,
        SwapchainImage image,
        ICompositionImportedGpuImage importedImage,
        CompositionDrawingSurface drawingSurface,
        WgpuContext context)
    {
        return _isInitialized &&
            _isZeroCopySupported &&
            ReferenceEquals(_wgpuContext, context) &&
            !context.IsDisposed &&
            ReferenceEquals(_drawingSurface, drawingSurface) &&
            ReferenceEquals(_swapchainImages, swapchainImages) &&
            imageIndex >= 0 &&
            imageIndex < swapchainImages.Length &&
            _currentWriteImageIndex == imageIndex &&
            ReferenceEquals(swapchainImages[imageIndex], image) &&
            ReferenceEquals(image.ImportedImage, importedImage) &&
            image.WgpuTexture is { IsDisposed: false } &&
            image.StagingBuffer != IntPtr.Zero;
    }

    private async Task RenderFrameAsync()
    {
        if (!_isInitialized || WinuiRoot == null || _wgpuContext == null || _compositor == null) return;

        var hostFrame = CreateHostFrame();
        if (!hostFrame.IsValid)
        {
            return;
        }

        StoreFrameMetrics(hostFrame);

        // 1. Force layout and animations updates recursively on WinUI Controls
        WinuiRoot.UpdateAnimations(0.016f); // Pass baseline delta time
        WinuiRoot.Measure(hostFrame.LogicalSize);
        WinuiRoot.Arrange(new WinuiRect(0, 0, hostFrame.LogicalWidth, hostFrame.LogicalHeight));

        if (_isZeroCopySupported && _gpuInterop != null && _drawingSurface != null)
        {
            if (!ResizeSharedResources(hostFrame.RenderTargetWidth, hostFrame.RenderTargetHeight))
            {
                if (TryUseCustomVisualFallback())
                {
                    SendRenderStateToCustomVisual(hostFrame);
                }
                return;
            }

            if (_swapchainImages != null && _wgpuContext != null && _drawingSurface != null)
            {
                var swapchainImages = _swapchainImages;
                var imageIndex = _currentWriteImageIndex;
                if (imageIndex < 0 || imageIndex >= swapchainImages.Length)
                {
                    return;
                }

                var image = swapchainImages[imageIndex];
                var context = _wgpuContext;
                var drawingSurface = _drawingSurface;
                var importedImage = image?.ImportedImage;
                if (image != null && importedImage != null && image.WgpuTexture != null)
                {
                    // Render directly to WebGPU offscreen target
                    _compositor.RenderOffscreen(
                        WinuiRoot,
                        hostFrame,
                        image.WgpuTexture,
                        0.0f
                    );

                    // Copy GPU texture to staging buffer
                    CopyTextureToStagingBuffer(image, hostFrame.RenderTargetWidth, hostFrame.RenderTargetHeight);

                    // Asynchronously map buffer - non-blocking!
                    image.IsStagingBufferMapActive = true;
                    try
                    {
                        await MapBufferAsync(image.StagingBuffer, MapMode.Read, (nuint)image.StagingBufferSize);
                    }
                    catch
                    {
                        image.IsStagingBufferMapActive = false;
                        if (!IsCurrentZeroCopyFrame(swapchainImages, imageIndex, image, importedImage, drawingSurface, context))
                        {
                            return;
                        }

                        throw;
                    }

                    if (!IsCurrentZeroCopyFrame(swapchainImages, imageIndex, image, importedImage, drawingSurface, context))
                    {
                        TryUnmapStagingBuffer(context, image);
                        return;
                    }

                    // Copy staging buffer to shared texture and unmap
                    CopyMappedToSharedTexture(context, image, hostFrame.RenderTargetWidth, hostFrame.RenderTargetHeight);

                    if (!IsCurrentZeroCopyFrame(swapchainImages, imageIndex, image, importedImage, drawingSurface, context))
                    {
                        return;
                    }

                    // Asynchronously update drawing surface directly from imported GPU image
                    await drawingSurface.UpdateAsync(importedImage);

                    if (!IsCurrentZeroCopyFrame(swapchainImages, imageIndex, image, importedImage, drawingSurface, context))
                    {
                        return;
                    }

                    RecordPresentedFrame(hostFrame, ProGpuAvaloniaPresentationMode.ZeroCopySharedTexture);

                    // Swap the write buffer index
                    _currentWriteImageIndex = (_currentWriteImageIndex + 1) % 2;
                }
            }
        }
        else if (_customVisual != null)
        {
            SendRenderStateToCustomVisual(hostFrame);
        }
    }

    private void SendRenderStateToCustomVisual(CompositorHostFrame hostFrame)
    {
        if (_customVisual == null)
        {
            return;
        }

        // Send latest compiled tree and sizes to composition handler
        _customVisual.SendHandlerMessage(new RenderState
        {
            WinuiRoot = WinuiRoot,
            HostFrame = hostFrame,
            CornerRadius = CornerRadius
        });
    }

    // --- Sizing Negotiation Lifecycle ---

    protected override Size MeasureOverride(Size availableSize)
    {
        if (WinuiRoot == null) return base.MeasureOverride(availableSize);

        // Bridge constraints to WinUI sizing Vector2
        var constraint = new Vector2((float)availableSize.Width, (float)availableSize.Height);
        
        // Let the ProGPU WinUI control recursively measure
        WinuiRoot.Measure(constraint);

        return new Size(WinuiRoot.DesiredSize.X, WinuiRoot.DesiredSize.Y);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (WinuiRoot == null) return base.ArrangeOverride(finalSize);

        // Arrange ProGPU visual subtree at origin
        WinuiRoot.Arrange(new WinuiRect(0, 0, (float)finalSize.Width, (float)finalSize.Height));

        return finalSize;
    }

    // --- Input Routing & Focus Management ---

    private void SyncInputState(Point pos)
    {
        if (_winuiInputState == null) return;
        
        _winuiInputState.Root = WinuiRoot;
        _winuiInputState.LastMousePos = new Vector2((float)pos.X, (float)pos.Y);
        InputSystem.Current = _winuiInputState;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        SyncInputState(position);
        InputSystem.InjectPointer(CreatePointerInput(e, PointerInputKind.Moved));
        
        QueueRenderUpdate();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        SyncInputState(e.GetPosition(this));
        
        InputSystem.InjectPointer(CreatePointerInput(e, PointerInputKind.Pressed));
        Focus();

        QueueRenderUpdate();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        SyncInputState(e.GetPosition(this));
        
        InputSystem.InjectPointer(CreatePointerInput(e, PointerInputKind.Released));
        
        QueueRenderUpdate();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        SyncInputState(e.GetPosition(this));
        InputSystem.InjectMouseScroll(new Vector2((float)e.Delta.X, (float)e.Delta.Y));
        
        QueueRenderUpdate();
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        SyncInputState(e.GetPosition(this));
        base.OnPointerEntered(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        SyncInputState(e.GetPosition(this));
        InputSystem.InjectMouseMove(new Vector2(-1f, -1f));
        QueueRenderUpdate();
        base.OnPointerExited(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_winuiInputState != null)
        {
            InputSystem.Current = _winuiInputState;
            var deviceType = e.Pointer.Type switch
            {
                PointerType.Touch => PointerDeviceType.Touch,
                PointerType.Pen => PointerDeviceType.Pen,
                _ => PointerDeviceType.Mouse
            };
            InputSystem.InjectPointer(new PointerInputEvent(
                PointerInputKind.Canceled,
                unchecked((uint)Math.Max(1, e.Pointer.Id)),
                deviceType,
                _winuiInputState.LastMousePos,
                (ulong)(Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency),
                IsPrimary: true));
        }
        QueueRenderUpdate();
        base.OnPointerCaptureLost(e);
    }

    private PointerInputEvent CreatePointerInput(PointerEventArgs e, PointerInputKind kind)
    {
        var point = e.GetCurrentPoint(this);
        var position = point.Position;
        var properties = point.Properties;
        var deviceType = e.Pointer.Type switch
        {
            PointerType.Touch => PointerDeviceType.Touch,
            PointerType.Pen => PointerDeviceType.Pen,
            _ => PointerDeviceType.Mouse
        };
        var modifiers = VirtualKeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= VirtualKeyModifiers.Menu;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= VirtualKeyModifiers.Windows;
        var inContact = kind == PointerInputKind.Pressed ||
            (kind == PointerInputKind.Moved &&
             (deviceType != PointerDeviceType.Mouse || properties.IsLeftButtonPressed || properties.IsMiddleButtonPressed || properties.IsRightButtonPressed));
        return new PointerInputEvent(
            kind,
            unchecked((uint)Math.Max(1, e.Pointer.Id)),
            deviceType,
            new Vector2((float)position.X, (float)position.Y),
            e.Timestamp,
            IsPrimary: true,
            IsInContact: inContact,
            IsLeftButtonPressed: properties.IsLeftButtonPressed || (kind == PointerInputKind.Pressed && deviceType != PointerDeviceType.Mouse),
            IsMiddleButtonPressed: properties.IsMiddleButtonPressed,
            IsRightButtonPressed: properties.IsRightButtonPressed,
            Pressure: deviceType == PointerDeviceType.Mouse ? (properties.IsLeftButtonPressed ? 0.5f : 0f) : 1f,
            Modifiers: modifiers);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_winuiInputState != null)
        {
            _winuiInputState.Root = WinuiRoot;
            InputSystem.Current = _winuiInputState;
        }

        var key = AvaloniaInputBridge.TranslateKey(e.Key);
        InputSystem.InjectKeyDown(key);

        QueueRenderUpdate();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_winuiInputState != null)
        {
            _winuiInputState.Root = WinuiRoot;
            InputSystem.Current = _winuiInputState;
        }

        var key = AvaloniaInputBridge.TranslateKey(e.Key);
        InputSystem.InjectKeyUp(key);

        QueueRenderUpdate();
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (_winuiInputState != null)
        {
            _winuiInputState.Root = WinuiRoot;
            InputSystem.Current = _winuiInputState;
        }

        if (!string.IsNullOrEmpty(e.Text))
        {
            foreach (char c in e.Text)
            {
                InputSystem.InjectKeyChar(c);
            }
        }

        QueueRenderUpdate();
        e.Handled = true;
    }

    // --- Render Update Pipeline ---

    public void RequestRender()
    {
        QueueRenderUpdate();
    }

    private void QueueRenderUpdate()
    {
        if (!_isInitialized || WinuiRoot == null) return;

        _renderRequested = true;

        if (_renderDispatchQueued || _isRendering)
        {
            return;
        }

        _renderDispatchQueued = true;
        Dispatcher.UIThread.Post(ProcessQueuedRenderUpdate, DispatcherPriority.Render);
    }

    private async void ProcessQueuedRenderUpdate()
    {
        _renderDispatchQueued = false;

        if (!_isInitialized || WinuiRoot == null)
        {
            _renderRequested = false;
            return;
        }

        if (_isRendering)
        {
            return;
        }

        if (!_isZeroCopySupported && _customVisual == null)
        {
            if (!TryUseCustomVisualFallback())
            {
                _renderRequested = false;
                return;
            }
        }

        if (_isZeroCopySupported && _drawingSurface == null)
        {
            if (!TryUseCustomVisualFallback())
            {
                _renderRequested = false;
                return;
            }
        }

        _isRendering = true;

        try
        {
            while (_renderRequested)
            {
                _renderRequested = false;
                await RenderFrameAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during rendering: {ex}");
        }
        finally
        {
            _isRendering = false;
            if (_renderRequested && !_renderDispatchQueued)
            {
                _renderDispatchQueued = true;
                Dispatcher.UIThread.Post(ProcessQueuedRenderUpdate, DispatcherPriority.Render);
            }
        }
    }

    private void UpdateClipGeometry()
    {
        var cornerRadius = CornerRadius;
        bool hasRoundedCorners = cornerRadius.TopLeft > 0 || cornerRadius.TopRight > 0 || 
                                 cornerRadius.BottomLeft > 0 || cornerRadius.BottomRight > 0;

        if (hasRoundedCorners && Bounds.Width > 0 && Bounds.Height > 0)
        {
            Clip = CreateRoundedRectangleClipGeometry(
                new AvaloniaRect(0, 0, Bounds.Width, Bounds.Height),
                cornerRadius);
        }
        else
        {
            Clip = null;
        }
    }

    private static Geometry CreateRoundedRectangleClipGeometry(AvaloniaRect bounds, CornerRadius cornerRadius)
    {
        var width = Math.Max(0.0, bounds.Width);
        var height = Math.Max(0.0, bounds.Height);
        var topLeft = Math.Max(0.0, cornerRadius.TopLeft);
        var topRight = Math.Max(0.0, cornerRadius.TopRight);
        var bottomRight = Math.Max(0.0, cornerRadius.BottomRight);
        var bottomLeft = Math.Max(0.0, cornerRadius.BottomLeft);

        var scale = 1.0;
        ScaleToFit(topLeft + topRight, width, ref scale);
        ScaleToFit(bottomLeft + bottomRight, width, ref scale);
        ScaleToFit(topLeft + bottomLeft, height, ref scale);
        ScaleToFit(topRight + bottomRight, height, ref scale);

        topLeft *= scale;
        topRight *= scale;
        bottomRight *= scale;
        bottomLeft *= scale;

        var left = bounds.X;
        var top = bounds.Y;
        var right = bounds.Right;
        var bottom = bounds.Bottom;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(left + topLeft, top), isFilled: true);
            context.LineTo(new Point(right - topRight, top));
            AddCornerArc(context, topRight, new Point(right, top + topRight));
            context.LineTo(new Point(right, bottom - bottomRight));
            AddCornerArc(context, bottomRight, new Point(right - bottomRight, bottom));
            context.LineTo(new Point(left + bottomLeft, bottom));
            AddCornerArc(context, bottomLeft, new Point(left, bottom - bottomLeft));
            context.LineTo(new Point(left, top + topLeft));
            AddCornerArc(context, topLeft, new Point(left + topLeft, top));
            context.EndFigure(isClosed: true);
        }

        return geometry;
    }

    private static void ScaleToFit(double radiusSum, double sideLength, ref double scale)
    {
        if (radiusSum > sideLength && radiusSum > 0.0)
        {
            scale = Math.Min(scale, sideLength / radiusSum);
        }
    }

    private static void AddCornerArc(StreamGeometryContext context, double radius, Point endPoint)
    {
        if (radius > 0.0)
        {
            context.ArcTo(endPoint, new Size(radius, radius), 0.0, isLargeArc: false, SweepDirection.Clockwise);
        }
        else
        {
            context.LineTo(endPoint);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WinuiRootProperty)
        {
            if (_winuiInputState != null)
            {
                _winuiInputState.Root = WinuiRoot;
            }

            InvalidateMeasure();
            QueueRenderUpdate();
        }

        if (change.Property == CornerRadiusProperty)
        {
            UpdateClipGeometry();
            QueueRenderUpdate();
        }
    }

    public override void Render(AvaloniaDrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new AvaloniaRect(0, 0, Bounds.Width, Bounds.Height));
        base.Render(context);
    }
}

public unsafe class ProGpuCustomVisualHandler : CompositionCustomVisualHandler, IDisposable
{
    private readonly WgpuContext? _wgpuContext;
    private readonly WinuiCompositor? _compositor;
    private readonly Action<CompositorHostFrame>? _framePresented;
    private GpuTexture? _offscreenTexture;
    private GpuTextureReadbackBuffer? _readbackBuffer;
    private WriteableBitmap? _writeableBitmap;

    private readonly object _stateLock = new();
    private Microsoft.UI.Xaml.FrameworkElement? _winuiRoot;
    private CompositorHostFrame _hostFrame;
    private bool _resourcesDirty;
    private CornerRadius _cornerRadius;

    public ProGpuCustomVisualHandler(
        WgpuContext? wgpuContext,
        WinuiCompositor? compositor,
        Action<CompositorHostFrame>? framePresented = null)
    {
        _wgpuContext = wgpuContext;
        _compositor = compositor;
        _framePresented = framePresented;
    }

    internal bool Matches(WgpuContext context, WinuiCompositor compositor)
    {
        return ReferenceEquals(_wgpuContext, context) &&
               ReferenceEquals(_compositor, compositor);
    }

    public override void OnMessage(object message)
    {
        if (message is string cmd && cmd == "DISPOSE")
        {
            Dispose();
            return;
        }

        if (message is RenderState state)
        {
            lock (_stateLock)
            {
                _winuiRoot = state.WinuiRoot;
                if (_hostFrame != state.HostFrame)
                {
                    _hostFrame = state.HostFrame;
                    _resourcesDirty = true;
                }
                _cornerRadius = state.CornerRadius;
            }
            Invalidate();
        }
    }

    private void ResizeResources(uint width, uint height)
    {
        if (_wgpuContext == null) return;

        if (_offscreenTexture == null)
        {
            _offscreenTexture = new GpuTexture(
                _wgpuContext, 
                width, 
                height, 
                TextureFormat.Bgra8Unorm, 
                TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.TextureBinding, 
                "Avalonia Host Offscreen Target",
                alphaMode: GpuTextureAlphaMode.Premultiplied
            );
        }
        else
        {
            _offscreenTexture.Resize(width, height);
        }

        const uint bytesPerPixel = 4;

        _readbackBuffer ??= new GpuTextureReadbackBuffer(_wgpuContext);
        _readbackBuffer.EnsureCapacity(width, height, bytesPerPixel);

        _writeableBitmap?.Dispose();
        _writeableBitmap = new WriteableBitmap(
            new PixelSize((int)width, (int)height), 
            new AvaloniaVector(96, 96), 
            PixelFormat.Bgra8888, 
            AlphaFormat.Premul
        );
    }

    public override void OnRender(ImmediateDrawingContext drawingContext)
    {
        Microsoft.UI.Xaml.FrameworkElement? localRoot;
        CompositorHostFrame hostFrame;
        bool resourcesDirty;
        CornerRadius cornerRadius;

        lock (_stateLock)
        {
            localRoot = _winuiRoot;
            hostFrame = _hostFrame;
            resourcesDirty = _resourcesDirty;
            _resourcesDirty = false;
            cornerRadius = _cornerRadius;
        }

        if (localRoot == null || !hostFrame.IsValid || _wgpuContext == null || _compositor == null)
        {
            return;
        }

        if (resourcesDirty)
        {
            ResizeResources(hostFrame.RenderTargetWidth, hostFrame.RenderTargetHeight);
        }

        // Direct WebGPU rendering to offscreen target texture
        if (_offscreenTexture != null && _readbackBuffer != null)
        {
            _compositor.RenderOffscreen(
                localRoot,
                hostFrame,
                _offscreenTexture,
                0.0f
            );

            if (_writeableBitmap != null)
            {
                using (var locked = _writeableBitmap.Lock())
                {
                    _readbackBuffer.TryReadTextureRows(
                        _offscreenTexture,
                        hostFrame.RenderTargetWidth,
                        hostFrame.RenderTargetHeight,
                        (void*)locked.Address,
                        (uint)locked.RowBytes);
                }
            }
        }

        if (_writeableBitmap != null)
        {
            var bounds = GetRenderBounds();
            bool hasRoundedCorners = cornerRadius.TopLeft > 0 || cornerRadius.TopRight > 0 || 
                                     cornerRadius.BottomLeft > 0 || cornerRadius.BottomRight > 0;
            if (hasRoundedCorners)
            {
                using (drawingContext.PushClip(new RoundedRect(bounds, cornerRadius)))
                {
                    drawingContext.DrawBitmap(_writeableBitmap, bounds);
                }
            }
            else
            {
                drawingContext.DrawBitmap(_writeableBitmap, bounds);
            }

            _framePresented?.Invoke(hostFrame);
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _winuiRoot = null;
        }

        _readbackBuffer?.Dispose();
        _readbackBuffer = null;

        _writeableBitmap?.Dispose();
        _writeableBitmap = null;

        _offscreenTexture?.Dispose();
        _offscreenTexture = null;
    }
}

public struct RenderState
{
    public Microsoft.UI.Xaml.FrameworkElement? WinuiRoot;
    public CompositorHostFrame HostFrame;
    public CornerRadius CornerRadius;
}
