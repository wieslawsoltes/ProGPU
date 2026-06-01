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

// Resolve ambiguous namespaces between Avalonia, Silk.NET, and ProGPU
using AvaloniaRect = Avalonia.Rect;
using WinuiRect = ProGPU.Scene.Rect;
using WinuiCompositor = ProGPU.Scene.Compositor;
using AvaloniaDrawingContext = Avalonia.Media.DrawingContext;
using GpuBuffer = Silk.NET.WebGPU.Buffer;
using AvaloniaVector = Avalonia.Vector;

namespace ProGPU.Avalonia;

public unsafe class ProGpuHostControl : Control
{
    // Native WGPU-native extension import for device polling
    [DllImport("wgpu_native", EntryPoint = "wgpuDevicePoll")]
    private static extern bool wgpuDevicePoll(Device* device, bool wait, void* wrappedSubmissionIndex);

    // Dependency properties
    public static readonly StyledProperty<FrameworkElement?> WinuiRootProperty =
        AvaloniaProperty.Register<ProGpuHostControl, FrameworkElement?>(nameof(WinuiRoot));

    public FrameworkElement? WinuiRoot
    {
        get => GetValue(WinuiRootProperty);
        set => SetValue(WinuiRootProperty, value);
    }

    public WgpuContext? WgpuContext => _wgpuContext;
    public WinuiCompositor? Compositor => _compositor;

    // Core ProGPU context
    private WgpuContext? _wgpuContext;
    private WinuiCompositor? _compositor;
    private WindowInputState? _winuiInputState;

    // GPU-to-GPU and Staging resources
    private GpuTexture? _offscreenTexture;
    private GpuBuffer* _stagingBuffer;
    private uint _stagingBufferSize;
    private uint _bytesPerRow;
    private WriteableBitmap? _writeableBitmap;
    private bool _isMappingPending;

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
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        
        double dpi = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        _lastDpiScale = dpi;
        _renderWidth = (uint)Math.Max(1, e.NewSize.Width * dpi);
        _renderHeight = (uint)Math.Max(1, e.NewSize.Height * dpi);

        if (_isInitialized)
        {
            ResizeResources(_renderWidth, _renderHeight);
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

        ResizeResources(_renderWidth, _renderHeight);

        _isInitialized = true;
    }

    private void ResizeResources(uint width, uint height)
    {
        if (_wgpuContext == null) return;

        // 1. Resize/Allocate Offscreen GPU Render Target
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

        // 2. Align row pitch to 256 bytes per WebGPU specifications
        uint bytesPerPixel = 4;
        uint unalignedBytesPerRow = width * bytesPerPixel;
        _bytesPerRow = (unalignedBytesPerRow + 255) & ~255u;
        uint requiredBufferSize = _bytesPerRow * height;

        // 3. Setup CPU Staging Buffers
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

        // 4. Reallocate WriteableBitmap at constant 96 DPI to bypass platform double-scaling bugs
        _writeableBitmap?.Dispose();
        _writeableBitmap = new WriteableBitmap(
            new PixelSize((int)width, (int)height), 
            new AvaloniaVector(96, 96), 
            PixelFormat.Bgra8888, 
            AlphaFormat.Premul
        );
    }

    private void CleanupGraphics()
    {
        if (_stagingBuffer != null)
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
        _compositor = null;

        _wgpuContext?.Dispose();
        _wgpuContext = null;

        _isInitialized = false;
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
        
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
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

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
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
        
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        SyncInputState(e.GetPosition(this));
        InputSystem.InjectMouseScroll(new Vector2((float)e.Delta.X, (float)e.Delta.Y));
        
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
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

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
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

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
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

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        e.Handled = true;
    }

    // --- Render Loop Pipelines ---

    public override void Render(AvaloniaDrawingContext context)
    {
        if (!_isInitialized || WinuiRoot == null || _wgpuContext == null || _compositor == null)
        {
            base.Render(context);
            return;
        }

        // 1. Force layout and animations updates recursively on WinUI Controls
        WinuiRoot.UpdateAnimations(0.016f); // Pass baseline delta time
        WinuiRoot.Measure(new Vector2((float)Bounds.Width, (float)Bounds.Height));
        WinuiRoot.Arrange(new WinuiRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));

        // 2. Direct WebGPU rendering to offscreen target texture
        if (_offscreenTexture != null && _stagingBuffer != null)
        {
            _compositor.RenderOffscreen(
                WinuiRoot, 
                (uint)Math.Max(1, Bounds.Width), 
                (uint)Math.Max(1, Bounds.Height), 
                _offscreenTexture, 
                0.0f, // No blur padding
                (float)_lastDpiScale
            );

            // 3. Fast copy GPU texture to CPU staging buffer
            uint bytesPerPixel = 4;
            uint bufferSize = _bytesPerRow * _renderHeight;
            
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
                    RowsPerImage = _renderHeight
                }
            };
            
            var copySize = new Extent3D
            {
                Width = _renderWidth,
                Height = _renderHeight,
                DepthOrArrayLayers = 1
            };
            
            _wgpuContext.Wgpu.CommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &copySize);
            
            var cmdBufferDesc = new CommandBufferDescriptor();
            var cmdBuffer = _wgpuContext.Wgpu.CommandEncoderFinish(encoder, &cmdBufferDesc);
            
            _wgpuContext.Wgpu.QueueSubmit(_wgpuContext.Queue, 1, &cmdBuffer);
            _wgpuContext.Wgpu.CommandBufferRelease(cmdBuffer);
            _wgpuContext.Wgpu.CommandEncoderRelease(encoder);
            
            // 4. Map staging buffer synchronously to read pixels
            _isMappingPending = true;
            var onMapCallback = PfnBufferMapCallback.From((status, userData) =>
            {
                _isMappingPending = false;
            });
            
            _wgpuContext.Wgpu.BufferMapAsync(_stagingBuffer, MapMode.Read, 0, (nuint)bufferSize, onMapCallback, null);
            
            // Poll device until mapping completes
            while (_isMappingPending)
            {
                wgpuDevicePoll(_wgpuContext.Device, false, null);
                Thread.Sleep(1);
            }
            
            // 5. Directly blit row pixels into the Avalonia WriteableBitmap buffer
            void* mappedPtr = _wgpuContext.Wgpu.BufferGetConstMappedRange(_stagingBuffer, 0, (nuint)bufferSize);
            if (mappedPtr != null && _writeableBitmap != null)
            {
                using (var locked = _writeableBitmap.Lock())
                {
                    byte* srcBytes = (byte*)mappedPtr;
                    byte* dstBytes = (byte*)locked.Address;
                    uint rowBytes = _renderWidth * bytesPerPixel;
                    
                    for (uint y = 0; y < _renderHeight; y++)
                    {
                        byte* srcRow = srcBytes + (y * _bytesPerRow);
                        byte* dstRow = dstBytes + (y * (uint)locked.RowBytes);
                        System.Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
                    }
                }
                
                _wgpuContext.Wgpu.BufferUnmap(_stagingBuffer);
            }
        }

        // 6. Draw the bitmap cleanly scaling down into bounds to maintain Retina crispness
        if (_writeableBitmap != null)
        {
            context.DrawImage(_writeableBitmap, new AvaloniaRect(0, 0, Bounds.Width, Bounds.Height));
        }
    }
}
