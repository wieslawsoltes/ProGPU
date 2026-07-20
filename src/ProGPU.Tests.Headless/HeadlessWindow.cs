using System;
using System.IO;
using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Scene;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Layout;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace ProGPU.Tests.Headless;

public unsafe class HeadlessWindow : IDisposable
{
    private static readonly TimeSpan ReadbackMapTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadbackAbortTimeout = TimeSpan.FromSeconds(5);

    private static HeadlessWindow? _shared;
    public static HeadlessWindow Shared => _shared ??= new HeadlessWindow(1280, 800);

    public static void ClearSharedContent()
    {
        if (_shared != null)
        {
            _shared.Content = null;
        }
    }

    public static void DisposeShared()
    {
        _shared?.Dispose();
        _shared = null;
    }

    [System.Runtime.InteropServices.DllImport("wgpu_native", EntryPoint = "wgpuDevicePoll")]
    private static extern bool wgpuDevicePoll(void* device, bool wait, void* wrappedSubmissionIndex);

    private readonly WgpuContext _context;
    private readonly Compositor _compositor;
    private FrameworkElement? _content;
    private uint _width;
    private uint _height;

    private GpuTexture? _offscreenTexture;
    private Buffer* _readbackBuffer;
    private uint _bufferSize;
    private readonly TextureFormat _renderFormat;
    private uint _bytesPerRow;

    public WgpuContext Context => _context;
    public Compositor Compositor => _compositor;

    public FrameworkElement? Content
    {
        get => _content;
        set
        {
            if (_content != null)
            {
                UnloadVisualTree(_content);
            }
            _content = value;
        }
    }

    private void UnloadVisualTree(Visual? visual)
    {
        if (visual == null) return;
        if (visual is FrameworkElement fe)
        {
            fe.FireUnloaded();
        }
        if (visual is ContainerVisual container)
        {
            var children = container.Children;
            for (int i = 0; i < children.Count; i++)
            {
                UnloadVisualTree(children[i]);
            }
        }
    }

    public uint Width => _width;
    public uint Height => _height;

    public HeadlessWindow(
        uint width = 1280,
        uint height = 800,
        CompositorOptions? compositorOptions = null,
        TextureFormat renderFormat = TextureFormat.Rgba8Unorm)
    {
        _width = width;
        _height = height;
        _renderFormat = renderFormat;

        // 1. Initialize Headless WebGPU Context
        _context = new WgpuContext();
        _context.Initialize(null);

        // 2. Preserve RGBA as the default test contract while allowing format-specific lanes.
        _compositor = new Compositor(
            _context,
            _renderFormat,
            compositorOptions ?? CompositorOptions.Default);

        // Setup Decoupled Hooks (similar to Window.cs)
        _compositor.PreRender += (w, h) => PopupService.MeasureAndArrangePopups(new Vector2(w, h));
        _compositor.GetExternalLayers = () => PopupService.ActivePopups;
        _compositor.GetTooltip = () => InputSystem.ActiveToolTip;
        _compositor.GetMousePosition = () => InputSystem.LastMousePosition;

        // 3. Create offscreen texture and mappable buffer resources
        UpdateResources();
    }

    private void UpdateResources()
    {
        var wgpu = _context.Wgpu;
        var device = _context.Device;

        if (_offscreenTexture == null || _offscreenTexture.Width != _width || _offscreenTexture.Height != _height)
        {
            _offscreenTexture?.Dispose();
            
            // Texture with RenderAttachment (resolve target) and CopySrc (source for reading)
            _offscreenTexture = new GpuTexture(
                _context,
                _width,
                _height,
                _renderFormat,
                TextureUsage.RenderAttachment | TextureUsage.CopySrc,
                "Headless Offscreen Target",
                alphaMode: GpuTextureAlphaMode.Premultiplied
            );

            // Dispose old readback buffer
            if (_readbackBuffer != null)
            {
                wgpu.BufferDestroy(_readbackBuffer);
                wgpu.BufferRelease(_readbackBuffer);
                _readbackBuffer = null;
            }

            // Align row pitch to 256 bytes per WebGPU requirements
            uint bytesPerPixel = 4;
            uint unalignedBytesPerRow = _width * bytesPerPixel;
            _bytesPerRow = (unalignedBytesPerRow + 255) & ~255u;
            _bufferSize = _bytesPerRow * _height;

            var labelPtr = SilkMarshal.StringToPtr("Headless Readback Buffer");
            var bufferDesc = new BufferDescriptor
            {
                Label = (byte*)labelPtr,
                Size = _bufferSize,
                Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
                MappedAtCreation = false
            };

            _readbackBuffer = wgpu.DeviceCreateBuffer(device, &bufferDesc);
            SilkMarshal.Free((nint)labelPtr);

            if (_readbackBuffer == null)
            {
                throw new InvalidOperationException("Failed to create WebGPU readback buffer.");
            }
        }
    }

    public void Resize(uint width, uint height)
    {
        _width = width;
        _height = height;
        UpdateResources();
    }

    public void Render(float deltaTime = 0.016f)
    {
        if (_content == null) return;

        // 1. Run animations and layout calculations (Measure and Arrange)
        _content.UpdateAnimations(deltaTime);
        
        _content.Measure(new Vector2(_width, _height));
        _content.Arrange(new Rect(0, 0, _width, _height));

        // 2. Render visual tree to the offscreen texture view
        _compositor.RenderScene(_content, _width, _height, _offscreenTexture!.ViewPtr);
    }

    public void RenderAtDpi(uint logicalWidth, uint logicalHeight, float dpiScale, float deltaTime = 0.016f)
    {
        if (_content == null) return;

        _content.UpdateAnimations(deltaTime);
        _content.Measure(new Vector2(logicalWidth, logicalHeight));
        _content.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
        _compositor.RenderScene(
            _content,
            logicalWidth,
            logicalHeight,
            _width,
            _height,
            dpiScale,
            _offscreenTexture!.ViewPtr);
    }

    public byte[] ReadPixels(TimeSpan? mapTimeout = null)
    {
        if (_offscreenTexture == null || _readbackBuffer == null)
        {
            throw new InvalidOperationException("Offscreen target or readback buffer is not initialized.");
        }

        var wgpu = _context.Wgpu;
        var device = _context.Device;
        var queue = _context.Queue;

        // 1. Create a command encoder for the copy operation
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Headless Readback Encoder") };
        var encoder = wgpu.DeviceCreateCommandEncoder(device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        // 2. Define source and destination copy descriptors
        var source = new ImageCopyTexture
        {
            Texture = _offscreenTexture.TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
            Aspect = TextureAspect.All
        };

        var destination = new ImageCopyBuffer
        {
            Buffer = _readbackBuffer,
            Layout = new TextureDataLayout
            {
                Offset = 0,
                BytesPerRow = _bytesPerRow,
                RowsPerImage = _height
            }
        };

        var copySize = new Extent3D
        {
            Width = _width,
            Height = _height,
            DepthOrArrayLayers = 1
        };

        wgpu.CommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &copySize);

        // 3. Submit copy command to Queue
        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Headless Readback Command Buffer") };
        var cmdBuffer = wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        wgpu.QueueSubmit(queue, 1, &cmdBuffer);

        wgpu.CommandBufferRelease(cmdBuffer);
        wgpu.CommandEncoderRelease(encoder);

        // 4. Map the buffer asynchronously to copy memory to CPU
        var mapSignal = new System.Threading.ManualResetEventSlim(false);
        BufferMapAsyncStatus mapStatus = BufferMapAsyncStatus.ValidationError;

        var onMapped = PfnBufferMapCallback.From((status, userData) =>
        {
            mapStatus = status;
            mapSignal.Set();
        });

        wgpu.BufferMapAsync(_readbackBuffer, MapMode.Read, 0, (nuint)_bufferSize, onMapped, null);

        // Poll the device to process events and fire the callback synchronously
        TimeSpan effectiveMapTimeout = mapTimeout ?? ReadbackMapTimeout;
        var swTimeout = System.Diagnostics.Stopwatch.StartNew();
        while (!mapSignal.IsSet)
        {
            wgpuDevicePoll(_context.Device, false, null);
            System.Threading.Thread.Sleep(1);
            if (swTimeout.Elapsed > effectiveMapTimeout)
            {
                // A timed-out map must be cancelled and observed before another test
                // submits work with this shared buffer. Otherwise wgpu-native can see
                // the late map complete and abort the process because the buffer is
                // still mapped during the next QueueSubmit.
                wgpu.BufferUnmap(_readbackBuffer);
                var abortTimeout = System.Diagnostics.Stopwatch.StartNew();
                while (!mapSignal.IsSet && abortTimeout.Elapsed <= ReadbackAbortTimeout)
                {
                    wgpuDevicePoll(_context.Device, false, null);
                    System.Threading.Thread.Sleep(1);
                }

                if (!mapSignal.IsSet)
                {
                    throw new TimeoutException(
                        $"WebGPU BufferMapAsync did not acknowledge cancellation within {ReadbackAbortTimeout.TotalSeconds:F0} seconds after a {effectiveMapTimeout.TotalSeconds:F0}-second headless readback timeout.");
                }

                throw new TimeoutException(
                    $"WebGPU BufferMapAsync timed out after {effectiveMapTimeout.TotalSeconds:F0} seconds during headless readback.");
            }
        }

        if (mapStatus != BufferMapAsyncStatus.Success)
        {
            throw new InvalidOperationException($"Failed to map readback buffer. WebGPU Status: {mapStatus}");
        }

        // 5. Read out the mapped pixels, stripping the row-alignment padding
        byte[] unpaddedPixels = new byte[_width * _height * 4];
        void* mappedPtr = wgpu.BufferGetConstMappedRange(_readbackBuffer, 0, (nuint)_bufferSize);
        if (mappedPtr != null)
        {
            byte* srcBytes = (byte*)mappedPtr;
            for (uint y = 0; y < _height; y++)
            {
                long srcOffset = y * _bytesPerRow;
                long dstOffset = y * _width * 4;
                System.Runtime.InteropServices.Marshal.Copy((nint)(srcBytes + srcOffset), unpaddedPixels, (int)dstOffset, (int)(_width * 4));
            }

            // Public headless readback remains RGBA regardless of the GPU target format.
            if (_renderFormat == TextureFormat.Bgra8Unorm)
            {
                for (int i = 0; i < unpaddedPixels.Length; i += 4)
                {
                    (unpaddedPixels[i], unpaddedPixels[i + 2]) =
                        (unpaddedPixels[i + 2], unpaddedPixels[i]);
                }
            }
        }

        // 6. Always unmap the buffer
        wgpu.BufferUnmap(_readbackBuffer);

        return unpaddedPixels;
    }

    public void SaveScreenshot(string filePath)
    {
        byte[] pixels = ReadPixels();
        PngEncoder.SavePng(filePath, pixels, _width, _height);
    }

    public void Dispose()
    {
        _offscreenTexture?.Dispose();
        _offscreenTexture = null;

        var wgpu = _context.Wgpu;
        if (_readbackBuffer != null)
        {
            wgpu.BufferDestroy(_readbackBuffer);
            wgpu.BufferRelease(_readbackBuffer);
            _readbackBuffer = null;
        }

        _compositor?.Dispose();
        _context?.Dispose();
        
        if (_shared == this)
        {
            _shared = null;
        }

        GC.SuppressFinalize(this);
    }

    ~HeadlessWindow()
    {
        // Do not call Dispose() or native WebGPU release APIs during finalization.
    }
}
