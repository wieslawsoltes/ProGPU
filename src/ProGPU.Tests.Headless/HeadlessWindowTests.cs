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
        
        context.Wgpu.DeviceSetUncapturedErrorCallback(context.Device, PfnErrorCallback.From((type, msg, _) =>
        {
            hasError = true;
            errorDetails += (msg != null ? Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)msg) : null) ?? "Unknown error";
            errorDetails += "\n";
        }), null);
        
        using var engine = new ProGPU.Compute.WavefrontVectorEngine(context);
        
        // Restore default callback
        context.Wgpu.DeviceSetUncapturedErrorCallback(context.Device, PfnErrorCallback.From((type, msg, _) =>
        {
            string errorMsg = (msg != null ? Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)msg) : null) ?? "Unknown error";
            Console.WriteLine($"[WebGPU Error] Type: {type}, Message: {errorMsg}");
        }), null);
        
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
        context.Wgpu.DeviceSetUncapturedErrorCallback(context.Device, PfnErrorCallback.From((type, msg, _) =>
        {
            hasError = true;
            if (errorCount++ < 10)
            {
                errorDetails += (msg != null ? Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)msg) : null) ?? "Unknown error";
                errorDetails += "\n";
            }
        }), null);
        
        using var engine = new ProGPU.Compute.WavefrontVectorEngine(context);
        using var destination = new GpuTexture(context, 256, 256, TextureFormat.Rgba8Unorm, 
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
        
        // Restore default callback
        context.Wgpu.DeviceSetUncapturedErrorCallback(context.Device, PfnErrorCallback.From((type, msg, _) =>
        {
            string errorMsg = (msg != null ? Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)msg) : null) ?? "Unknown error";
            Console.WriteLine($"[WebGPU Error] Type: {type}, Message: {errorMsg}");
        }), null);
        
        Assert.False(hasError, $"WebGPU validation errors occurred:\n{errorDetails}");
    }

    [Fact]
    public void Test_Wavefront_Render_Path()
    {
        uint width = 256;
        uint height = 256;

        var window = HeadlessWindow.Shared;
        window.Resize(width, height);
        
        var prevEngine = window.Compositor.VectorEngine;
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
            window.Compositor.VectorEngine = prevEngine;
            window.Content = null;
        }
    }

    [Fact]
    public void Test_Wavefront_Render_Text()
    {
        uint width = 256;
        uint height = 256;

        var window = HeadlessWindow.Shared;
        window.Resize(width, height);

        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath)) fontPath = "Arial.ttf";
        Assert.True(File.Exists(fontPath), "Arial font file must exist for text rendering tests");
        var font = new ProGPU.Text.TtfFont(fontPath);

        var prevEngine = window.Compositor.VectorEngine;
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
            window.Compositor.VectorEngine = prevEngine;
            window.Content = null;
        }
    }
}

