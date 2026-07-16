extern alias ProGpu;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Numerics;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Backend;
using SilkInput = Silk.NET.Input;
using GpuBuffer = Silk.NET.WebGPU.Buffer;
using Microsoft.UI.Input;

// Alias ProGpu types using extern alias ProGpu reference
using ProGpuFrameworkElement = ProGpu::Microsoft.UI.Xaml.FrameworkElement;
using ProGpuWindowInputState = ProGpu::Microsoft.UI.Xaml.Input.WindowInputState;
using ProGpuInputSystem = ProGpu::Microsoft.UI.Xaml.Input.InputSystem;
using ProGpuThemeManager = ProGpu::Microsoft.UI.Xaml.ThemeManager;
using ProGpuCompositor = ProGPU.Scene.Compositor;
using ProGpuRect = ProGPU.Scene.Rect;

namespace ProGPU.Uno;

public unsafe class ProGpuHostControl : ContentControl
{
    // Native WGPU-native extension import for device polling
    [DllImport("wgpu_native", EntryPoint = "wgpuDevicePoll")]
    private static extern bool wgpuDevicePoll(Device* device, bool wait, void* wrappedSubmissionIndex);

    // Dependency properties
    public static readonly DependencyProperty WinuiRootProperty =
        DependencyProperty.Register(
            nameof(WinuiRoot), 
            typeof(ProGpuFrameworkElement), 
            typeof(ProGpuHostControl), 
            new PropertyMetadata(null, OnWinuiRootChanged)
        );

    public ProGpuFrameworkElement? WinuiRoot
    {
        get => (ProGpuFrameworkElement?)GetValue(WinuiRootProperty);
        set => SetValue(WinuiRootProperty, value);
    }

    public WgpuContext? WgpuContext => _wgpuContext;
    public ProGpuCompositor? Compositor => _compositor;

    private static void OnWinuiRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ProGpuHostControl)d;
        if (control._winuiInputState != null)
        {
            control._winuiInputState.Root = (ProGpuFrameworkElement?)e.NewValue;
        }
        control.QueueInvalidate();
    }

    // Child image control to render the WriteableBitmap natively in Uno
    private readonly Image _displayImage = new();

    // Core ProGPU context
    private WgpuContext? _wgpuContext;
    private ProGpuCompositor? _compositor;
    private ProGpuWindowInputState? _winuiInputState;

    // Staging resources
    private GpuTexture? _offscreenTexture;
    private GpuBuffer* _stagingBuffer;
    private uint _stagingBufferSize;
    private uint _bytesPerRow;
    private WriteableBitmap? _writeableBitmap;
    private bool _isMappingPending;
    private PfnBufferMapCallback _bufferMapCallback;
    private byte[]? _rowBuffer;

    // State tracking
    private bool _isInitialized;
    private double _lastDpiScale = 1.0;
    private uint _renderWidth;
    private uint _renderHeight;
    private bool _isRenderLoopActive;

    public ProGpuHostControl()
    {
        _bufferMapCallback = PfnBufferMapCallback.From(OnBufferMapped);
        
        // Embed the child Image control which works on all Uno targets
        Content = _displayImage;
        _displayImage.Stretch = Stretch.Fill;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnBufferMapped(BufferMapAsyncStatus status, void* userData)
    {
        _isMappingPending = false;
    }

    private void OnThemeChanged()
    {
        WinuiRoot?.NotifyThemeChanged();
        QueueInvalidate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeGraphics();
        StartRenderLoop();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRenderLoop();
        CleanupGraphics();
    }

    private void InitializeGraphics()
    {
        if (_isInitialized) return;

        // 1. Initialize Headless/Offscreen WebGPU Context
        _wgpuContext = new WgpuContext();
        _wgpuContext.Initialize(null);

        // 2. Initialize Compositor targeting BGRA8Unorm texture formats
        _compositor = new ProGpuCompositor(_wgpuContext, TextureFormat.Bgra8Unorm);

        // 3. Initialize WinUI Input state for events routing
        _winuiInputState = new ProGpuWindowInputState
        {
            Root = WinuiRoot
        };

        // 4. Setup initial drawing bounds
        double dpi = XamlRoot?.RasterizationScale ?? 1.0;
        _lastDpiScale = dpi;
        _renderWidth = (uint)Math.Max(1, ActualWidth * dpi);
        _renderHeight = (uint)Math.Max(1, ActualHeight * dpi);

        ResizeResources(_renderWidth, _renderHeight);

        ProGpuThemeManager.ThemeChanged += OnThemeChanged;

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
                "Uno Host Offscreen Target",
                alphaMode: GpuTextureAlphaMode.Premultiplied
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

        // 3. Setup Staging Buffer
        if (_stagingBuffer == null || _stagingBufferSize < requiredBufferSize)
        {
            if (_stagingBuffer != null)
            {
                _wgpuContext.Api.BufferDestroy(_stagingBuffer);
                _wgpuContext.Api.BufferRelease(_stagingBuffer);
            }
            
            var bufferDesc = new BufferDescriptor
            {
                Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
                Size = requiredBufferSize,
                MappedAtCreation = false
            };
            _stagingBuffer = _wgpuContext.Api.DeviceCreateBuffer(_wgpuContext.Device, &bufferDesc);
            _stagingBufferSize = requiredBufferSize;
        }

        // 4. Reallocate WriteableBitmap and attach to child XAML image source
        _writeableBitmap = new WriteableBitmap((int)width, (int)height);
        _displayImage.Source = _writeableBitmap;

        // 5. Pre-allocate or resize row buffer to avoid GC allocations in the rendering hot path
        uint rowBytes = width * bytesPerPixel;
        if (_rowBuffer == null || _rowBuffer.Length < rowBytes)
        {
            _rowBuffer = new byte[rowBytes];
        }
    }

    private void CleanupGraphics()
    {
        ProGpuThemeManager.ThemeChanged -= OnThemeChanged;
        if (_stagingBuffer != null && _wgpuContext != null)
        {
            _wgpuContext.Api.BufferDestroy(_stagingBuffer);
            _wgpuContext.Api.BufferRelease(_stagingBuffer);
            _stagingBuffer = null;
        }

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

            if (ProGpuInputSystem.Current == _winuiInputState)
            {
                ProGpuInputSystem.Current = null!;
            }
            _winuiInputState = null;
        }

        _writeableBitmap = null;
        _displayImage.Source = null;

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

        var constraint = new Vector2((float)availableSize.Width, (float)availableSize.Height);
        WinuiRoot.Measure(constraint);

        base.MeasureOverride(availableSize);

        return new Size(WinuiRoot.DesiredSize.X, WinuiRoot.DesiredSize.Y);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (WinuiRoot == null) return base.ArrangeOverride(finalSize);

        WinuiRoot.Arrange(new ProGpuRect(0, 0, (float)finalSize.Width, (float)finalSize.Height));

        double dpi = XamlRoot?.RasterizationScale ?? 1.0;
        _lastDpiScale = dpi;
        _renderWidth = (uint)Math.Max(1, finalSize.Width * dpi);
        _renderHeight = (uint)Math.Max(1, finalSize.Height * dpi);

        // Bind child XAML image bounds directly to logical final size to bypass scaling blowout bugs
        _displayImage.Width = finalSize.Width;
        _displayImage.Height = finalSize.Height;

        if (_isInitialized)
        {
            ResizeResources(_renderWidth, _renderHeight);
        }

        base.ArrangeOverride(finalSize);

        return finalSize;
    }

    // --- Input Routing & Focus Management ---

    private void SyncInputState(Point pos)
    {
        if (_winuiInputState == null) return;
        
        _winuiInputState.Root = WinuiRoot;
        _winuiInputState.LastMousePos = new Vector2((float)pos.X, (float)pos.Y);
        ProGpuInputSystem.Current = _winuiInputState;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        SyncInputState(point.Position);
        
        ProGpuInputSystem.InjectMouseMove(new Vector2((float)point.Position.X, (float)point.Position.Y));
        QueueInvalidate();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        SyncInputState(point.Position);
        
        ProGpuInputSystem.InjectMouseMove(new Vector2(-1f, -1f));
        QueueInvalidate();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        SyncInputState(point.Position);
        
        var props = point.Properties;
        var button = SilkInput.MouseButton.Left;
        if (props.IsLeftButtonPressed) button = SilkInput.MouseButton.Left;
        else if (props.IsRightButtonPressed) button = SilkInput.MouseButton.Right;
        else if (props.IsMiddleButtonPressed) button = SilkInput.MouseButton.Middle;
        
        ProGpuInputSystem.InjectMouseDown(button);
        Focus(FocusState.Pointer);
        
        QueueInvalidate();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        SyncInputState(point.Position);
        
        var props = point.Properties;
        var button = SilkInput.MouseButton.Left;
        var updateKind = props.PointerUpdateKind;
        if (updateKind == PointerUpdateKind.LeftButtonReleased) button = SilkInput.MouseButton.Left;
        else if (updateKind == PointerUpdateKind.RightButtonReleased) button = SilkInput.MouseButton.Right;
        else if (updateKind == PointerUpdateKind.MiddleButtonReleased) button = SilkInput.MouseButton.Middle;
        
        ProGpuInputSystem.InjectMouseUp(button);
        
        QueueInvalidate();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        SyncInputState(point.Position);
        
        var props = point.Properties;
        ProGpuInputSystem.InjectMouseScroll(new Vector2(0f, (float)props.MouseWheelDelta / 120f));
        
        QueueInvalidate();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (_winuiInputState != null)
        {
            _winuiInputState.Root = WinuiRoot;
            ProGpuInputSystem.Current = _winuiInputState;
        }

        var key = UnoInputBridge.TranslateKey(e.Key);
        ProGpuInputSystem.InjectKeyDown(key);
        
        QueueInvalidate();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        if (_winuiInputState != null)
        {
            _winuiInputState.Root = WinuiRoot;
            ProGpuInputSystem.Current = _winuiInputState;
        }

        var key = UnoInputBridge.TranslateKey(e.Key);
        ProGpuInputSystem.InjectKeyUp(key);
        
        QueueInvalidate();
        e.Handled = true;
    }

    protected override void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        if (_winuiInputState != null)
        {
            _winuiInputState.Root = WinuiRoot;
            ProGpuInputSystem.Current = _winuiInputState;
        }

        ProGpuInputSystem.InjectKeyChar(e.Character);
        
        QueueInvalidate();
        e.Handled = true;
    }

    // --- Render Loop & Pathways ---

    private void StartRenderLoop()
    {
        if (_isRenderLoopActive) return;
        _isRenderLoopActive = true;
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    private void StopRenderLoop()
    {
        if (!_isRenderLoopActive) return;
        CompositionTarget.Rendering -= OnRenderingFrame;
        _isRenderLoopActive = false;
    }

    private void OnRenderingFrame(object? sender, object e)
    {
        RenderFrame();
    }

    public void QueueInvalidate()
    {
        // Driven automatically by CompositionTarget.Rendering
    }

    private void RenderFrame()
    {
        if (!_isInitialized || WinuiRoot == null || _wgpuContext == null || _compositor == null) return;

        // 1. Force layout and animations updates recursively on WinUI Controls
        WinuiRoot.UpdateAnimations(0.016f);
        WinuiRoot.Measure(new Vector2((float)ActualWidth, (float)ActualHeight));
        WinuiRoot.Arrange(new ProGpuRect(0, 0, (float)ActualWidth, (float)ActualHeight));

        // 2. Direct WebGPU rendering to offscreen target texture
        if (_offscreenTexture != null && _stagingBuffer != null)
        {
            _compositor.RenderOffscreen(
                WinuiRoot, 
                (uint)Math.Max(1, ActualWidth), 
                (uint)Math.Max(1, ActualHeight), 
                _offscreenTexture, 
                0.0f, 
                (float)_lastDpiScale
            );

            // 3. Fast copy GPU texture to CPU staging buffer
            uint bytesPerPixel = 4;
            uint bufferSize = _bytesPerRow * _renderHeight;
            
            var encoderDesc = new CommandEncoderDescriptor();
            var encoder = _wgpuContext.Api.DeviceCreateCommandEncoder(_wgpuContext.Device, &encoderDesc);
            
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
            
            _wgpuContext.Api.CommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &copySize);
            
            var cmdBufferDesc = new CommandBufferDescriptor();
            var cmdBuffer = _wgpuContext.Api.CommandEncoderFinish(encoder, &cmdBufferDesc);
            
            _wgpuContext.Api.QueueSubmit(_wgpuContext.Queue, 1, &cmdBuffer);
            _wgpuContext.Api.CommandBufferRelease(cmdBuffer);
            _wgpuContext.Api.CommandEncoderRelease(encoder);

            // 4. Map staging buffer synchronously
            _isMappingPending = true;
            _wgpuContext.Api.BufferMapAsync(_stagingBuffer, MapMode.Read, 0, (nuint)bufferSize, _bufferMapCallback, null);
            
            // Poll device until mapping completes
            while (_isMappingPending)
            {
                wgpuDevicePoll(_wgpuContext.Device, false, null);
                Thread.Sleep(1);
            }

            // 5. Direct fast memory copy to Uno's WriteableBitmap stream
            void* mappedPtr = _wgpuContext.Api.BufferGetConstMappedRange(_stagingBuffer, 0, (nuint)bufferSize);
            if (mappedPtr != null && _writeableBitmap != null)
            {
                byte* srcBytes = (byte*)mappedPtr;
                uint rowBytes = _renderWidth * bytesPerPixel;
                
                if (_rowBuffer == null || _rowBuffer.Length < rowBytes)
                {
                    _rowBuffer = new byte[rowBytes];
                }

                using (var stream = _writeableBitmap.PixelBuffer.AsStream())
                {
                    stream.Position = 0;
                    for (uint y = 0; y < _renderHeight; y++)
                    {
                        long srcOffset = y * _bytesPerRow;
                        Marshal.Copy((nint)(srcBytes + srcOffset), _rowBuffer, 0, (int)rowBytes);
                        stream.Write(_rowBuffer, 0, (int)rowBytes);
                    }
                }

                _wgpuContext.Api.BufferUnmap(_stagingBuffer);

                // Invalidate WriteableBitmap so Uno repaints the Image control
                _writeableBitmap.Invalidate();
            }
        }
    }
}
