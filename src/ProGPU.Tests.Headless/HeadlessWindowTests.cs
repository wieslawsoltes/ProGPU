using System;
using System.IO;
using System.Numerics;
using Xunit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using ProGPU.Backend;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class HeadlessWindowTests : IDisposable
{
    public void Dispose()
    {
    }

    [Fact]
    public void Test_HeadlessWindow_Initialization_And_Render()
    {
        uint width = 256;
        uint height = 256;

        var window = HeadlessWindow.Shared;
        window.Resize(width, height);
        Assert.NotNull(window.Context);
        Assert.NotNull(window.Compositor);
        Assert.Equal(width, window.Width);
        Assert.Equal(height, window.Height);

        // Create a solid red Border
        var border = new Border
        {
            Background = new SolidColorBrush(0xFF0000FF), // RGBA: Red = 255, Green = 0, Blue = 0, Alpha = 255
            Width = width,
            Height = height
        };

        window.Content = border;

        // Render the scene
        window.Render();

        // Retrieve pixels
        byte[] pixels = window.ReadPixels();
        Assert.NotNull(pixels);
        Assert.Equal((int)(width * height * 4), pixels.Length);

        // Check if there are colored pixels matching the red border.
        // The default clear color is dark (0.08f, 0.08f, 0.12f, 1.0f).
        // Since we draw a solid red border over the entire window, the pixels should be red (R=255, G=0, B=0, A=255).
        // Let's assert on a pixel in the center of the rendered image.
        int centerX = (int)width / 2;
        int centerY = (int)height / 2;
        int centerIndex = (centerY * (int)width + centerX) * 4;

        byte r = pixels[centerIndex + 0];
        byte g = pixels[centerIndex + 1];
        byte b = pixels[centerIndex + 2];
        byte a = pixels[centerIndex + 3];

        // Let's print out for diagnostic visibility
        Console.WriteLine($"Center pixel color: R={r}, G={g}, B={b}, A={a}");

        Assert.Equal(255, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
        Assert.Equal(255, a);

        // Cleanup
        window.Content = null;
    }

    [Fact]
    public void Test_HeadlessWindow_Screenshot_Export()
    {
        uint width = 512;
        uint height = 512;

        var window = HeadlessWindow.Shared;
        window.Resize(width, height);

        var border = new Border
        {
            Background = new SolidColorBrush(0x00FF00FF), // Green = 255, Alpha = 255
            Width = width,
            Height = height
        };

        window.Content = border;
        window.Render();

        string tempPath = Path.Combine(Path.GetTempPath(), "ProGPU_headless_test_screenshot.png");
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        window.SaveScreenshot(tempPath);

        Assert.True(File.Exists(tempPath), "Screenshot PNG should be successfully created.");
        Assert.True(new FileInfo(tempPath).Length > 0, "PNG file should not be empty.");

        // Clean up the temporary screenshot file
        File.Delete(tempPath);

        // Cleanup
        window.Content = null;
    }

    [Fact]
    public void ResizingDefersMsaaResourcesUntilSubmittedWorkIsIdle()
    {
        using var window = new HeadlessWindow(32, 32);
        window.Content = new Border
        {
            Background = new SolidColorBrush(0x336699FF)
        };

        window.Render();
        for (uint size = 33; size <= 64; size++)
        {
            window.Resize(size, size + 1);
            window.Render();
        }

        lock (window.Context.DisposalLock)
        {
            Assert.NotEmpty(window.Context.PendingTextureViews);
            Assert.NotEmpty(window.Context.PendingTextures);
        }

        window.Context.CleanupPendingResources();
    }

    [Fact]
    public unsafe void Test_Compile_WavefrontPipelines()
    {
        var window = HeadlessWindow.Shared;
        var context = window.Context;

        bool hasError = false;
        string errorDetails = "";

        void OnWebGpuError(ErrorType type, string message)
        {
            hasError = true;
            errorDetails += $"{type}: {message}\n";
        }

        WgpuContext.OnWebGpuError += OnWebGpuError;
        try
        {
            using var engine = new ProGPU.Compute.WavefrontVectorEngine(context);
        }
        finally
        {
            WgpuContext.OnWebGpuError -= OnWebGpuError;
        }

        Assert.False(hasError, $"WebGPU validation errors occurred:\n{errorDetails}");
    }

    [Fact]
    public unsafe void Test_Run_WavefrontEngine()
    {
        var window = HeadlessWindow.Shared;
        var context = window.Context;
        
        bool hasError = false;
        string errorDetails = "";

        int errorCount = 0;
        void OnWebGpuError(ErrorType type, string message)
        {
            hasError = true;
            if (errorCount++ < 10)
            {
                errorDetails += $"{type}: {message}\n";
            }
        }

        WgpuContext.OnWebGpuError += OnWebGpuError;
        try
        {
            using var engine = new ProGPU.Compute.WavefrontVectorEngine(context);
            using var destination = new GpuTexture(context, 256, 256, TextureFormat.Bgra8Unorm,
                TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopySrc | TextureUsage.CopyDst,
                "TestDestinationTexture");

            engine.BeginFrame();

            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(10f, 10f), isClosed: true);
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(100f, 10f)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(100f, 100f)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(10f, 100f)));
            path.Figures.Add(figure);

            var brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
            engine.DrawPath(path, Matrix4x4.Identity, brush);

            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("TestEncoder") };
            var encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)encoderDesc.Label);

            engine.EndFrame(encoder, destination, 1.0f);

            var cmdDesc = new CommandBufferDescriptor { Label = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("TestCommandBuffer") };
            var cmdBuffer = context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)cmdDesc.Label);

            context.Wgpu.QueueSubmit(context.Queue, 1, &cmdBuffer);

            context.Wgpu.CommandBufferRelease(cmdBuffer);
            context.Wgpu.CommandEncoderRelease(encoder);
        }
        finally
        {
            WgpuContext.OnWebGpuError -= OnWebGpuError;
        }

        Assert.False(hasError, $"WebGPU validation errors occurred:\n{errorDetails}");
    }

    [Fact]
    public void Test_Wavefront_Render_Path()
    {
        uint width = 256;
        uint height = 256;

        using var window = new HeadlessWindow(
            width,
            height,
            renderFormat: TextureFormat.Bgra8Unorm);
        
        window.Compositor.VectorEngine = ProGPU.Scene.Compositor.VectorRenderingEngine.Wavefront;

        try
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(10f, 10f), isClosed: true);
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(240f, 10f)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(240f, 240f)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(10f, 240f)));
            path.Figures.Add(figure);

            var pathIcon = new PathIcon
            {
                Data = path,
                Foreground = new SolidColorBrush(0xFF0000FF), // Red (RGBA: Red=255, Green=0, Blue=0, Alpha=255)
                Width = width,
                Height = height
            };

            window.Content = pathIcon;
            window.Render();

            byte[] pixels = window.ReadPixels();
            Assert.NotNull(pixels);

            int centerIdx = (128 * 256 + 128) * 4;
            byte r = pixels[centerIdx + 0];
            byte g = pixels[centerIdx + 1];
            byte b = pixels[centerIdx + 2];
            byte a = pixels[centerIdx + 3];

            Console.WriteLine($"[Diagnostic] Wavefront path center pixel: R={r}, G={g}, B={b}, A={a}");
            
            Assert.Equal(255, r);
            Assert.Equal(0, g);
            Assert.Equal(0, b);
            Assert.Equal(255, a);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void Test_Wavefront_Render_Text()
    {
        uint width = 256;
        uint height = 256;

        using var window = new HeadlessWindow(
            width,
            height,
            renderFormat: TextureFormat.Bgra8Unorm);

        string? fontPath = TryFindTextRenderingTestFont();
        Assert.False(string.IsNullOrWhiteSpace(fontPath), "A renderable system font must exist for text rendering tests");
        var font = new ProGPU.Text.TtfFont(fontPath!);

        window.Compositor.VectorEngine = ProGPU.Scene.Compositor.VectorRenderingEngine.Wavefront;

        try
        {
            var textBlock = new TextBlock
            {
                Text = "Hello",
                Font = font,
                FontSize = 64f,
                Foreground = new SolidColorBrush(0xFF0000FF), // Red
                Width = width,
                Height = height
            };

            window.Content = textBlock;
            window.Render();

            byte[] pixels = window.ReadPixels();
            Assert.NotNull(pixels);

            bool foundRed = false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 0] > 200 && pixels[i + 1] < 50 && pixels[i + 2] < 50 && pixels[i + 3] == 255)
                {
                    foundRed = true;
                    break;
                }
            }

            Assert.True(foundRed, "We should find red pixels drawn from the text rendering");
        }
        finally
        {
            window.Content = null;
        }
    }

    private static string? TryFindTextRenderingTestFont()
    {
        string[] candidates =
        {
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/System/Library/Fonts/Supplemental/Helvetica.ttf",
            "/Library/Fonts/Arial.ttf",
            "C:\\Windows\\Fonts\\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "Arial.ttf"
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return ProGPU.Text.FontApi.GetSystemFonts()
            .FirstOrDefault(font => File.Exists(font.FilePath))
            ?.FilePath;
    }

    [System.Runtime.InteropServices.DllImport("wgpu_native", EntryPoint = "wgpuDevicePoll")]
    private static unsafe extern bool wgpuDevicePoll(void* device, bool wait, void* wrappedSubmissionIndex);

    [Fact]
    public unsafe void Test_Wavefront_Render_Path_HighDpi()
    {
        uint logicalWidth = 256;
        uint logicalHeight = 256;
        uint physicalWidth = 512;
        uint physicalHeight = 512;

        using var window = new HeadlessWindow(
            logicalWidth,
            logicalHeight,
            renderFormat: TextureFormat.Bgra8Unorm);
        var context = window.Context;

        var mockWindow = System.Reflection.DispatchProxy.Create<Silk.NET.Windowing.IWindow, WindowProxy>();
        var proxyInstance = (WindowProxy)(object)mockWindow;
        proxyInstance.Size = new Silk.NET.Maths.Vector2D<int>((int)logicalWidth, (int)logicalHeight);
        proxyInstance.FramebufferSize = new Silk.NET.Maths.Vector2D<int>((int)physicalWidth, (int)physicalHeight);

        var contextWindowField = typeof(WgpuContext).GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var originalWindow = contextWindowField!.GetValue(context);
        contextWindowField.SetValue(context, mockWindow);

        window.Compositor.VectorEngine = ProGPU.Scene.Compositor.VectorRenderingEngine.Wavefront;

        GpuTexture? physicalOffscreenTexture = null;
        Silk.NET.WebGPU.Buffer* readbackBuffer = null;

        try
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(10f, 10f), isClosed: true);
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(240f, 10f)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(240f, 240f)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(10f, 240f)));
            path.Figures.Add(figure);

            var pathIcon = new PathIcon
            {
                Data = path,
                Foreground = new SolidColorBrush(0xFF0000FF), // Red
                Width = logicalWidth,
                Height = logicalHeight
            };

            pathIcon.Measure(new Vector2(logicalWidth, logicalHeight));
            pathIcon.Arrange(new ProGPU.Scene.Rect(0, 0, logicalWidth, logicalHeight));

            physicalOffscreenTexture = new GpuTexture(
                context,
                physicalWidth,
                physicalHeight,
                TextureFormat.Bgra8Unorm,
                TextureUsage.RenderAttachment | TextureUsage.CopySrc,
                "HighDpi Test Offscreen Target"
            );

            window.Compositor.RenderScene(pathIcon, logicalWidth, logicalHeight, physicalOffscreenTexture.ViewPtr);

            var wgpu = context.Wgpu;
            uint bytesPerPixel = 4;
            uint unalignedBytesPerRow = physicalWidth * bytesPerPixel;
            uint bytesPerRow = (unalignedBytesPerRow + 255) & ~255u;
            uint bufferSize = bytesPerRow * physicalHeight;

            var labelPtr = Silk.NET.Core.Native.SilkMarshal.StringToPtr("HighDpi Readback Buffer");
            var bufferDesc = new BufferDescriptor
            {
                Label = (byte*)labelPtr,
                Size = bufferSize,
                Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
                MappedAtCreation = false
            };
            readbackBuffer = wgpu.DeviceCreateBuffer(context.Device, &bufferDesc);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)labelPtr);

            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("HighDpi Readback Encoder") };
            var encoder = wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)encoderDesc.Label);

            var source = new ImageCopyTexture
            {
                Texture = physicalOffscreenTexture.TexturePtr,
                MipLevel = 0,
                Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
                Aspect = TextureAspect.All
            };
            var destination = new ImageCopyBuffer
            {
                Buffer = readbackBuffer,
                Layout = new TextureDataLayout { Offset = 0, BytesPerRow = bytesPerRow, RowsPerImage = physicalHeight }
            };
            var copySize = new Extent3D { Width = physicalWidth, Height = physicalHeight, DepthOrArrayLayers = 1 };
            wgpu.CommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &copySize);

            var cmdDesc = new CommandBufferDescriptor { Label = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("HighDpi Readback Command Buffer") };
            var cmdBuffer = wgpu.CommandEncoderFinish(encoder, &cmdDesc);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)cmdDesc.Label);

            wgpu.QueueSubmit(context.Queue, 1, &cmdBuffer);
            wgpu.CommandBufferRelease(cmdBuffer);
            wgpu.CommandEncoderRelease(encoder);

            var mapSignal = new System.Threading.ManualResetEventSlim(false);
            BufferMapAsyncStatus mapStatus = BufferMapAsyncStatus.ValidationError;
            var onMapped = PfnBufferMapCallback.From((status, userData) =>
            {
                mapStatus = status;
                mapSignal.Set();
            });
            wgpu.BufferMapAsync(readbackBuffer, MapMode.Read, 0, (nuint)bufferSize, onMapped, null);

            var swTimeout = System.Diagnostics.Stopwatch.StartNew();
            while (!mapSignal.IsSet)
            {
                wgpuDevicePoll(context.Device, false, null);
                System.Threading.Thread.Sleep(1);
                if (swTimeout.ElapsedMilliseconds > 5000)
                {
                    throw new TimeoutException("HighDpi BufferMapAsync timed out.");
                }
            }

            Assert.Equal(BufferMapAsyncStatus.Success, mapStatus);

            byte[] unpaddedPixels = new byte[physicalWidth * physicalHeight * 4];
            void* mappedPtr = wgpu.BufferGetConstMappedRange(readbackBuffer, 0, (nuint)bufferSize);
            byte* srcBytes = (byte*)mappedPtr;
            for (uint y = 0; y < physicalHeight; y++)
            {
                uint srcOffset = y * bytesPerRow;
                uint dstOffset = y * physicalWidth * 4;
                System.Runtime.InteropServices.Marshal.Copy((nint)(srcBytes + srcOffset), unpaddedPixels, (int)dstOffset, (int)(physicalWidth * 4));
            }
            wgpu.BufferUnmap(readbackBuffer);

            for (int i = 0; i < unpaddedPixels.Length; i += 4)
            {
                byte b = unpaddedPixels[i + 0];
                byte r = unpaddedPixels[i + 2];
                unpaddedPixels[i + 0] = r;
                unpaddedPixels[i + 2] = b;
            }

            int centerIdx = (256 * 512 + 256) * 4;
            byte redVal = unpaddedPixels[centerIdx + 0];
            byte greenVal = unpaddedPixels[centerIdx + 1];
            byte blueVal = unpaddedPixels[centerIdx + 2];
            byte alphaVal = unpaddedPixels[centerIdx + 3];

            Console.WriteLine($"[Diagnostic] Wavefront high-DPI path center pixel (512x512): R={redVal}, G={greenVal}, B={blueVal}, A={alphaVal}");

            Assert.Equal(255, redVal);
            Assert.Equal(0, greenVal);
            Assert.Equal(0, blueVal);
            Assert.Equal(255, alphaVal);
        }
        finally
        {
            contextWindowField.SetValue(context, originalWindow);
            physicalOffscreenTexture?.Dispose();
            if (readbackBuffer != null)
            {
                context.Wgpu.BufferDestroy(readbackBuffer);
                context.Wgpu.BufferRelease(readbackBuffer);
            }
        }
    }
}

public class WindowProxy : System.Reflection.DispatchProxy
{
    public Silk.NET.Maths.Vector2D<int> Size { get; set; }
    public Silk.NET.Maths.Vector2D<int> FramebufferSize { get; set; }

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null) return null;
        if (targetMethod.Name == "get_Size")
        {
            return Size;
        }
        if (targetMethod.Name == "get_FramebufferSize")
        {
            return FramebufferSize;
        }
        if (targetMethod.Name == "set_VSync")
        {
            return null;
        }
        if (targetMethod.ReturnType.IsValueType)
        {
            return Activator.CreateInstance(targetMethod.ReturnType);
        }
        return null;
    }
}
