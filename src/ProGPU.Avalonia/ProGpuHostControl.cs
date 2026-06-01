using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Numerics;
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
    // Dependency properties
    public static readonly StyledProperty<FrameworkElement?> WinuiRootProperty =
        AvaloniaProperty.Register<ProGpuHostControl, FrameworkElement?>(nameof(WinuiRoot));

    public FrameworkElement? WinuiRoot
    {
        get => GetValue(WinuiRootProperty);
        set => SetValue(WinuiRootProperty, value);
    }

    public bool EnableZeroCopy { get; set; } = false;

    public WgpuContext? WgpuContext => _wgpuContext;
    public WinuiCompositor? Compositor => _compositor;

    // Core ProGPU context
    private WgpuContext? _wgpuContext;
    private WinuiCompositor? _compositor;
    private WindowInputState? _winuiInputState;

    // Custom Visual references
    private CompositionCustomVisual? _customVisual;
    private ProGpuCustomVisualHandler? _customVisualHandler;

    // Zero-Copy Shared Texture states
    private bool _isZeroCopySupported;
    private ICompositionGpuInterop? _gpuInterop;
    private string _gpuHandleType = "";
    private CompositionSurfaceVisual? _surfaceVisual;
    private CompositionDrawingSurface? _drawingSurface;
    private ICompositionImportedGpuImage? _importedGpuImage;
    private IntPtr _sharedHandle = IntPtr.Zero;
    private uint _lastSharedWidth;
    private uint _lastSharedHeight;
    private GpuTexture? _sharedWgpuTextureWrapper;

    // Windows specific shared D3D11 resource holders
    private IntPtr _winD3DDevice = IntPtr.Zero;
    private IntPtr _winTexture2D = IntPtr.Zero;

    // State tracking
    private bool _isInitialized;
    private double _lastDpiScale = 1.0;
    private uint _renderWidth;
    private uint _renderHeight;

    static ProGpuHostControl()
    {
        // Enable focus by default
        FocusableProperty.OverrideDefaultValue<ProGpuHostControl>(true);
        AffectsRender<ProGpuHostControl>(WinuiRootProperty);
    }

    public ProGpuHostControl()
    {
        ThemeManager.ThemeChanged += OnThemeChanged;
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

        // 1. Initialize Headless/Offscreen WebGPU Context
        _wgpuContext = new WgpuContext();
        _wgpuContext.Initialize(null); // No Silk window; direct offscreen render target

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

        _winuiInputState = null;
        _compositor = null;
        _wgpuContext = null;

        _isInitialized = false;
    }

    private void ResizeSharedResources(uint width, uint height)
    {
        if (width == _lastSharedWidth && height == _lastSharedHeight && _importedGpuImage != null)
            return;

        ReleaseSharedResources();

        _lastSharedWidth = width;
        _lastSharedHeight = height;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _sharedHandle = GpuSharingInterop.CreateMacSharedSurface(width, height);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _sharedHandle = GpuSharingInterop.CreateWindowsSharedTexture(width, height, out _winD3DDevice, out _winTexture2D);
        }

        if (_sharedHandle != IntPtr.Zero && _gpuInterop != null)
        {
            var props = new PlatformGraphicsExternalImageProperties
            {
                Width = (int)width,
                Height = (int)height,
                Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                TopLeftOrigin = true
            };
            
            var platformHandle = new PlatformHandle(_sharedHandle, _gpuHandleType);
            _importedGpuImage = _gpuInterop.ImportImage(platformHandle, props);
        }
    }

    private void ReleaseSharedResources()
    {
        if (_importedGpuImage != null)
        {
            _ = _importedGpuImage.DisposeAsync();
            _importedGpuImage = null;
        }

        if (_sharedHandle != IntPtr.Zero)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                GpuSharingInterop.ReleaseMacSharedSurface(_sharedHandle);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                GpuSharingInterop.ReleaseWindowsSharedTexture(_winD3DDevice, _winTexture2D);
                _winD3DDevice = IntPtr.Zero;
                _winTexture2D = IntPtr.Zero;
            }
            _sharedHandle = IntPtr.Zero;
        }

        if (_sharedWgpuTextureWrapper != null)
        {
            _sharedWgpuTextureWrapper.Dispose();
            _sharedWgpuTextureWrapper = null;
        }
    }

    private void RenderToSharedTexture(uint width, uint height, double dpiScale)
    {
        if (_wgpuContext == null || _compositor == null) return;

        float logicalWidth = (float)(width / dpiScale);
        float logicalHeight = (float)(height / dpiScale);

        if (_sharedWgpuTextureWrapper == null || _sharedWgpuTextureWrapper.Width != width || _sharedWgpuTextureWrapper.Height != height)
        {
            _sharedWgpuTextureWrapper?.Dispose();
            
            // Allocate our WebGPU representation of the shared texture
            _sharedWgpuTextureWrapper = new GpuTexture(
                _wgpuContext,
                width,
                height,
                TextureFormat.Bgra8Unorm,
                TextureUsage.RenderAttachment | TextureUsage.CopySrc,
                "Shared Zero-Copy Target"
            );
        }

        // Render directly into the shared texture target - ZERO CPU-copy overhead!
        _compositor.RenderOffscreen(
            WinuiRoot,
            (uint)Math.Max(1, logicalWidth),
            (uint)Math.Max(1, logicalHeight),
            _sharedWgpuTextureWrapper,
            0.0f,
            (float)dpiScale
        );
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

    private void QueueRenderUpdate()
    {
        if (!_isInitialized || WinuiRoot == null) return;

        if (!_isZeroCopySupported && _customVisual == null) return;
        if (_isZeroCopySupported && _drawingSurface == null) return;

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

            if (_importedGpuImage != null)
            {
                RenderToSharedTexture(renderWidth, renderHeight, dpi);
                
                // Asynchronously update drawing surface directly from imported GPU image
                _ = _drawingSurface.UpdateAsync(_importedGpuImage);
            }
        }
        else if (_customVisual != null)
        {
            // 2. Send latest compiled tree and sizes to composition handler
            _customVisual.SendHandlerMessage(Tuple.Create<Microsoft.UI.Xaml.FrameworkElement?, uint, uint, double>(
                WinuiRoot, renderWidth, renderHeight, dpi
            ));
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

        if (message is Tuple<Microsoft.UI.Xaml.FrameworkElement?, uint, uint, double> renderState)
        {
            lock (_stateLock)
            {
                _winuiRoot = renderState.Item1;
                if (_renderWidth != renderState.Item2 || _renderHeight != renderState.Item3 || _dpiScale != renderState.Item4)
                {
                    _renderWidth = renderState.Item2;
                    _renderHeight = renderState.Item3;
                    _dpiScale = renderState.Item4;
                    _resourcesDirty = true;
                }
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
                TextureUsage.RenderAttachment | TextureUsage.CopySrc, 
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

        lock (_stateLock)
        {
            localRoot = _winuiRoot;
            width = _renderWidth;
            height = _renderHeight;
            dpiScale = _dpiScale;
            resourcesDirty = _resourcesDirty;
            _resourcesDirty = false;
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
            drawingContext.DrawBitmap(_writeableBitmap, bounds);
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

        _compositor?.Dispose();
        _wgpuContext?.Dispose();
    }
}
