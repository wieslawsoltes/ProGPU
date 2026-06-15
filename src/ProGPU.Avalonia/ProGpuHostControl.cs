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
using WinuiRect = ProGPU.Scene.Rect;
using WinuiCompositor = ProGPU.Scene.Compositor;
using AvaloniaDrawingContext = Avalonia.Media.DrawingContext;
using GpuBuffer = Silk.NET.WebGPU.Buffer;
using AvaloniaVector = Avalonia.Vector;

namespace ProGPU.Avalonia;

public class ProGpuHostControl : Control
{
    [DllImport("wgpu_native", EntryPoint = "wgpuDevicePoll")]
    private static extern unsafe bool wgpuDevicePoll(Device* device, bool wait, void* wrappedSubmissionIndex);

    private class SwapchainImage : IDisposable
    {
        public IntPtr SharedHandle = IntPtr.Zero;
        public ICompositionImportedGpuImage? ImportedImage;
        public GpuTexture? WgpuTexture;
        public IntPtr StagingBuffer = IntPtr.Zero;
        public uint StagingBufferSize;
        public uint BytesPerRow;
        public bool IsReady = true;

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
                _context.Wgpu.BufferDestroy((GpuBuffer*)StagingBuffer);
                _context.Wgpu.BufferRelease((GpuBuffer*)StagingBuffer);
                StagingBuffer = IntPtr.Zero;
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

    public bool EnableZeroCopy { get; set; } = true;

    public WgpuContext? WgpuContext => _wgpuContext;
    public WinuiCompositor? Compositor => _compositor;

    // Core ProGPU context
    private WgpuContext? _wgpuContext;
    private WinuiCompositor? _compositor;
    private WindowInputState? _winuiInputState;
    private SharedContextLease? _contextLease;

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
            _wgpuContext!.Wgpu.BufferMapAsync((GpuBuffer*)buffer, mode, 0, size, s_mapCallback, userData);
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
                        wgpuDevicePoll(_wgpuContext.Device, false, null);
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
            WgpuContext? context = null;
            var active = WgpuContext.ActiveContexts;
            for (int i = 0; i < active.Count; i++)
            {
                if (!active[i].IsDisposed)
                {
                    context = active[i];
                    break;
                }
            }

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

    private void OnThemeChanged()
    {
        WinuiRoot?.NotifyThemeChanged();
        QueueRenderUpdate();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        
        double dpi = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        _lastDpiScale = dpi;
        _renderWidth = (uint)Math.Max(1, e.NewSize.Width * dpi);
        _renderHeight = (uint)Math.Max(1, e.NewSize.Height * dpi);

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
        double dpi = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        _lastDpiScale = dpi;
        _renderWidth = (uint)Math.Max(1, Bounds.Width * dpi);
        _renderHeight = (uint)Math.Max(1, Bounds.Height * dpi);

        // 5. Setup Composition Custom Visual
        SetupCompositionSurface();

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

    private async Task SetupCompositionSurfaceAsync(global::Avalonia.Rendering.Composition.Compositor compositor)
    {
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

        if (EnableZeroCopy && useSharedTexture && interop != null)
        {
            _isZeroCopySupported = true;
            _gpuInterop = interop;
            _gpuHandleType = handleType;

            _surfaceVisual = compositor.CreateSurfaceVisual();
            _drawingSurface = compositor.CreateDrawingSurface();
            _surfaceVisual.Surface = _drawingSurface;

            ElementComposition.SetElementChildVisual(this, _surfaceVisual);
            _surfaceVisual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);

            QueueRenderUpdate();
        }
        else
        {
            _isZeroCopySupported = false;
            _customVisualHandler = new ProGpuCustomVisualHandler(_wgpuContext, _compositor);
            _customVisual = compositor.CreateCustomVisual(_customVisualHandler);
            ElementComposition.SetElementChildVisual(this, _customVisual);

            _customVisual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);

            QueueRenderUpdate();
        }
    }

    private void CleanupGraphics()
    {
        if (!_isInitialized) return;

        StopPolling();

        ThemeManager.ThemeChanged -= OnThemeChanged;

        if (_customVisual != null)
        {
            _customVisual.SendHandlerMessage("DISPOSE");
            ElementComposition.SetElementChildVisual(this, null);
            _customVisual = null;
            _customVisualHandler = null;
        }

        if (_surfaceVisual != null)
        {
            ElementComposition.SetElementChildVisual(this, null);
            _surfaceVisual = null;
            _drawingSurface?.Dispose();
            _drawingSurface = null;
            
            ReleaseSharedResources();

        }

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

    private void ResizeSharedResources(uint width, uint height)
    {
        if (width == _lastSharedWidth && height == _lastSharedHeight && _swapchainImages != null)
            return;

        ReleaseSharedResources();

        _lastSharedWidth = width;
        _lastSharedHeight = height;

        _swapchainImages = new SwapchainImage[2];
        for (int i = 0; i < 2; i++)
        {
            var image = new SwapchainImage(_wgpuContext!);
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                image.SharedHandle = GpuSharingInterop.CreateMacSharedSurface(width, height);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                image.SharedHandle = GpuSharingInterop.CreateWindowsSharedTexture(width, height, out image.WinD3DDevice, out image.WinTexture2D);
            }

            if (image.SharedHandle != IntPtr.Zero && _gpuInterop != null)
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
            }

            // Allocate WebGPU representation of the shared texture
            image.WgpuTexture = new GpuTexture(
                _wgpuContext!,
                width,
                height,
                TextureFormat.Bgra8Unorm,
                TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.TextureBinding,
                $"Shared Zero-Copy Target {i}"
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
                image.StagingBuffer = (IntPtr)_wgpuContext!.Wgpu.DeviceCreateBuffer(_wgpuContext!.Device, &bufferDesc);
            }

            _swapchainImages[i] = image;
        }

        _currentWriteImageIndex = 0;
    }

    private void ReleaseSharedResources()
    {
        if (_swapchainImages != null)
        {
            foreach (var img in _swapchainImages)
            {
                img.Dispose();
            }
            _swapchainImages = null;
        }
    }

    private unsafe void CopyTextureToStagingBuffer(SwapchainImage image, uint renderWidth, uint renderHeight)
    {
        var encoderDesc = new CommandEncoderDescriptor();
        var encoder = _wgpuContext!.Wgpu.DeviceCreateCommandEncoder(_wgpuContext.Device, &encoderDesc);
        
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
        
        _wgpuContext.Wgpu.CommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &copySize);
        
        var cmdBufferDesc = new CommandBufferDescriptor();
        var cmdBuffer = _wgpuContext.Wgpu.CommandEncoderFinish(encoder, &cmdBufferDesc);
        
        _wgpuContext.Wgpu.QueueSubmit(_wgpuContext.Queue, 1, &cmdBuffer);
        _wgpuContext.Wgpu.CommandBufferRelease(cmdBuffer);
        _wgpuContext.Wgpu.CommandEncoderRelease(encoder);
    }

    private unsafe void CopyMappedToSharedTexture(SwapchainImage image, uint renderWidth, uint renderHeight)
    {
        void* mappedPtr = _wgpuContext!.Wgpu.BufferGetConstMappedRange((GpuBuffer*)image.StagingBuffer, 0, (nuint)image.StagingBufferSize);
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
                GpuSharingInterop.COMHelper.CallGetImmediateContext(image.WinD3DDevice, out IntPtr context);
                if (context != IntPtr.Zero)
                {
                    GpuSharingInterop.COMHelper.CallUpdateSubresource(
                        context,
                        image.WinTexture2D,
                        0,
                        IntPtr.Zero,
                        mappedPtr,
                        image.BytesPerRow,
                        0
                    );
                    GpuSharingInterop.COMHelper.CallRelease(context);
                }
            }

            _wgpuContext.Wgpu.BufferUnmap((GpuBuffer*)image.StagingBuffer);
        }
    }

    private async Task RenderFrameAsync()
    {
        if (!_isInitialized || WinuiRoot == null || _wgpuContext == null || _compositor == null) return;

        // 1. Force layout and animations updates recursively on WinUI Controls
        WinuiRoot.UpdateAnimations(0.016f); // Pass baseline delta time
        WinuiRoot.Measure(new Vector2((float)Bounds.Width, (float)Bounds.Height));
        WinuiRoot.Arrange(new WinuiRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));

        double dpi = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        uint renderWidth = (uint)Math.Max(1, Bounds.Width * dpi);
        uint renderHeight = (uint)Math.Max(1, Bounds.Height * dpi);

        if (_isZeroCopySupported && _gpuInterop != null && _drawingSurface != null)
        {
            ResizeSharedResources(renderWidth, renderHeight);

            if (_swapchainImages != null)
            {
                var image = _swapchainImages[_currentWriteImageIndex];
                if (image != null && image.ImportedImage != null && image.WgpuTexture != null)
                {
                    // Render directly to WebGPU offscreen target
                    float logicalWidth = (float)(renderWidth / dpi);
                    float logicalHeight = (float)(renderHeight / dpi);

                    _compositor.RenderOffscreen(
                        WinuiRoot,
                        (uint)Math.Max(1, logicalWidth),
                        (uint)Math.Max(1, logicalHeight),
                        image.WgpuTexture,
                        0.0f,
                        (float)dpi
                    );

                    // Copy GPU texture to staging buffer
                    CopyTextureToStagingBuffer(image, renderWidth, renderHeight);

                    // Asynchronously map buffer - non-blocking!
                    await MapBufferAsync(image.StagingBuffer, MapMode.Read, (nuint)image.StagingBufferSize);

                    // Copy staging buffer to shared texture and unmap
                    CopyMappedToSharedTexture(image, renderWidth, renderHeight);

                    // Asynchronously update drawing surface directly from imported GPU image
                    await _drawingSurface.UpdateAsync(image.ImportedImage);

                    // Swap the write buffer index
                    _currentWriteImageIndex = (_currentWriteImageIndex + 1) % 2;
                }
            }
        }
        else if (_customVisual != null)
        {
            // Send latest compiled tree and sizes to composition handler
            _customVisual.SendHandlerMessage(new RenderState
            {
                WinuiRoot = WinuiRoot,
                Width = renderWidth,
                Height = renderHeight,
                DpiScale = dpi,
                CornerRadius = CornerRadius
            });
        }
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
        SyncInputState(e.GetPosition(this));
        InputSystem.InjectMouseMove(new Vector2((float)e.GetPosition(this).X, (float)e.GetPosition(this).Y));
        
        QueueRenderUpdate();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        SyncInputState(e.GetPosition(this));
        
        var props = e.GetCurrentPoint(this).Properties;
        var button = SilkInput.MouseButton.Left;
        if (props.IsLeftButtonPressed) button = SilkInput.MouseButton.Left;
        else if (props.IsRightButtonPressed) button = SilkInput.MouseButton.Right;
        else if (props.IsMiddleButtonPressed) button = SilkInput.MouseButton.Middle;
        
        InputSystem.InjectMouseDown(button);
        Focus();

        QueueRenderUpdate();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        SyncInputState(e.GetPosition(this));
        
        var props = e.GetCurrentPoint(this).Properties;
        var button = SilkInput.MouseButton.Left;
        var updateKind = props.PointerUpdateKind;
        if (updateKind == PointerUpdateKind.LeftButtonReleased) button = SilkInput.MouseButton.Left;
        else if (updateKind == PointerUpdateKind.RightButtonReleased) button = SilkInput.MouseButton.Right;
        else if (updateKind == PointerUpdateKind.MiddleButtonReleased) button = SilkInput.MouseButton.Middle;
        
        InputSystem.InjectMouseUp(button);
        
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

    private async void QueueRenderUpdate()
    {
        if (!_isInitialized || WinuiRoot == null) return;

        if (!_isZeroCopySupported && _customVisual == null) return;
        if (_isZeroCopySupported && _drawingSurface == null) return;

        _renderRequested = true;

        if (_isRendering) return;
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
        }
    }

    private void UpdateClipGeometry()
    {
        var cornerRadius = CornerRadius;
        bool hasRoundedCorners = cornerRadius.TopLeft > 0 || cornerRadius.TopRight > 0 || 
                                 cornerRadius.BottomLeft > 0 || cornerRadius.BottomRight > 0;

        if (hasRoundedCorners && Bounds.Width > 0 && Bounds.Height > 0)
        {
            double radius = Math.Max(cornerRadius.TopLeft, Math.Max(cornerRadius.TopRight, 
                            Math.Max(cornerRadius.BottomLeft, cornerRadius.BottomRight)));
            
            Clip = new RectangleGeometry
            {
                Rect = new AvaloniaRect(0, 0, Bounds.Width, Bounds.Height),
                RadiusX = radius,
                RadiusY = radius
            };
        }
        else
        {
            Clip = null;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
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
        QueueRenderUpdate();
    }
}

public unsafe class ProGpuCustomVisualHandler : CompositionCustomVisualHandler, IDisposable
{
    [DllImport("wgpu_native", EntryPoint = "wgpuDevicePoll")]
    private static extern bool wgpuDevicePoll(Device* device, bool wait, void* wrappedSubmissionIndex);

    private readonly WgpuContext? _wgpuContext;
    private readonly WinuiCompositor? _compositor;
    private GpuTexture? _offscreenTexture;
    private GpuBuffer* _stagingBuffer;
    private uint _stagingBufferSize;
    private uint _bytesPerRow;
    private WriteableBitmap? _writeableBitmap;
    private bool _isMappingPending;
    private readonly PfnBufferMapCallback _bufferMapCallback;

    private readonly object _stateLock = new();
    private Microsoft.UI.Xaml.FrameworkElement? _winuiRoot;
    private uint _renderWidth;
    private uint _renderHeight;
    private double _dpiScale = 1.0;
    private bool _resourcesDirty;
    private CornerRadius _cornerRadius;

    public ProGpuCustomVisualHandler(WgpuContext? wgpuContext, WinuiCompositor? compositor)
    {
        _wgpuContext = wgpuContext;
        _compositor = compositor;
        _bufferMapCallback = PfnBufferMapCallback.From(OnBufferMapped);
    }

    private void OnBufferMapped(BufferMapAsyncStatus status, void* userData)
    {
        _isMappingPending = false;
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
                if (_renderWidth != state.Width || _renderHeight != state.Height || _dpiScale != state.DpiScale)
                {
                    _renderWidth = state.Width;
                    _renderHeight = state.Height;
                    _dpiScale = state.DpiScale;
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
                "Avalonia Host Offscreen Target"
            );
        }
        else
        {
            _offscreenTexture.Resize(width, height);
        }

        uint bytesPerPixel = 4;
        uint unalignedBytesPerRow = width * bytesPerPixel;
        _bytesPerRow = (unalignedBytesPerRow + 255) & ~255u;
        uint requiredBufferSize = _bytesPerRow * height;

        if (_stagingBuffer == null || _stagingBufferSize < requiredBufferSize)
        {
            if (_stagingBuffer != null)
            {
                _wgpuContext.Wgpu.BufferDestroy(_stagingBuffer);
                _wgpuContext.Wgpu.BufferRelease(_stagingBuffer);
            }
            
            var bufferDesc = new BufferDescriptor
            {
                Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
                Size = requiredBufferSize,
                MappedAtCreation = false
            };
            _stagingBuffer = _wgpuContext.Wgpu.DeviceCreateBuffer(_wgpuContext.Device, &bufferDesc);
            _stagingBufferSize = requiredBufferSize;
        }

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
        uint width;
        uint height;
        double dpiScale;
        bool resourcesDirty;
        CornerRadius cornerRadius;

        lock (_stateLock)
        {
            localRoot = _winuiRoot;
            width = _renderWidth;
            height = _renderHeight;
            dpiScale = _dpiScale;
            resourcesDirty = _resourcesDirty;
            _resourcesDirty = false;
            cornerRadius = _cornerRadius;
        }

        if (localRoot == null || width == 0 || height == 0 || _wgpuContext == null || _compositor == null)
        {
            return;
        }

        if (resourcesDirty)
        {
            ResizeResources(width, height);
        }

        // Direct WebGPU rendering to offscreen target texture
        if (_offscreenTexture != null && _stagingBuffer != null)
        {
            float logicalWidth = (float)(width / dpiScale);
            float logicalHeight = (float)(height / dpiScale);

            _compositor.RenderOffscreen(
                localRoot, 
                (uint)Math.Max(1, logicalWidth), 
                (uint)Math.Max(1, logicalHeight), 
                _offscreenTexture, 
                0.0f, 
                (float)dpiScale
            );

            uint bytesPerPixel = 4;
            uint bufferSize = _bytesPerRow * height;
            
            var encoderDesc = new CommandEncoderDescriptor();
            var encoder = _wgpuContext.Wgpu.DeviceCreateCommandEncoder(_wgpuContext.Device, &encoderDesc);
            
            var copySrc = new ImageCopyTexture
            {
                Texture = _offscreenTexture.TexturePtr,
                MipLevel = 0,
                Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
                Aspect = TextureAspect.All
            };
            
            var copyDst = new ImageCopyBuffer
            {
                Buffer = _stagingBuffer,
                Layout = new TextureDataLayout
                {
                    Offset = 0,
                    BytesPerRow = _bytesPerRow,
                    RowsPerImage = height
                }
            };
            
            var copySize = new Extent3D
            {
                Width = width,
                Height = height,
                DepthOrArrayLayers = 1
            };
            
            _wgpuContext.Wgpu.CommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &copySize);
            
            var cmdBufferDesc = new CommandBufferDescriptor();
            var cmdBuffer = _wgpuContext.Wgpu.CommandEncoderFinish(encoder, &cmdBufferDesc);
            
            _wgpuContext.Wgpu.QueueSubmit(_wgpuContext.Queue, 1, &cmdBuffer);
            _wgpuContext.Wgpu.CommandBufferRelease(cmdBuffer);
            _wgpuContext.Wgpu.CommandEncoderRelease(encoder);
            
            _isMappingPending = true;
            _wgpuContext.Wgpu.BufferMapAsync(_stagingBuffer, MapMode.Read, 0, (nuint)bufferSize, _bufferMapCallback, null);
            
            while (_isMappingPending)
            {
                wgpuDevicePoll(_wgpuContext.Device, false, null);
                Thread.Sleep(1);
            }
            
            void* mappedPtr = _wgpuContext.Wgpu.BufferGetConstMappedRange(_stagingBuffer, 0, (nuint)bufferSize);
            if (mappedPtr != null && _writeableBitmap != null)
            {
                using (var locked = _writeableBitmap.Lock())
                {
                    byte* srcBytes = (byte*)mappedPtr;
                    byte* dstBytes = (byte*)locked.Address;
                    uint rowBytes = width * bytesPerPixel;
                    
                    for (uint y = 0; y < height; y++)
                    {
                        byte* srcRow = srcBytes + (y * _bytesPerRow);
                        byte* dstRow = dstBytes + (y * (uint)locked.RowBytes);
                        System.Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
                    }
                }
                
                _wgpuContext.Wgpu.BufferUnmap(_stagingBuffer);
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
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _winuiRoot = null;
        }

        if (_stagingBuffer != null && _wgpuContext != null)
        {
            _wgpuContext.Wgpu.BufferDestroy(_stagingBuffer);
            _wgpuContext.Wgpu.BufferRelease(_stagingBuffer);
            _stagingBuffer = null;
        }

        _writeableBitmap?.Dispose();
        _writeableBitmap = null;

        _offscreenTexture?.Dispose();
        _offscreenTexture = null;

    }
}

public struct RenderState
{
    public Microsoft.UI.Xaml.FrameworkElement? WinuiRoot;
    public uint Width;
    public uint Height;
    public double DpiScale;
    public CornerRadius CornerRadius;
}
