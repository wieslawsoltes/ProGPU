using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Compute;

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
    public void GpuTimestampRing_ReportsPerPassMetricsWithoutBlockingRendering()
    {
        using var window = new HeadlessWindow(64, 64);
        var context = window.Context;
        if (!context.SupportsTimestampQueries)
        {
            return;
        }

        bool hasError = false;
        string errorDetails = string.Empty;
        void OnWebGpuError(ErrorType type, string message)
        {
            hasError = true;
            errorDetails += $"{type}: {message}\n";
        }

        WgpuContext.OnWebGpuError += OnWebGpuError;
        try
        {
            context.EnableGpuTimestampTracking = true;
            window.Content = new Border
            {
                Background = new SolidColorBrush(0x336699FF),
                Width = 64,
                Height = 64
            };

            for (int frame = 0; frame < 6; frame++)
            {
                window.Render();
            }

            GpuTimestampMetrics metrics = WaitForTimestampSample(context);
            Assert.True(metrics.SubmittedSamples >= metrics.CompletedSamples);
            Assert.True(metrics.CompletedSamples > 0);
            Assert.True(metrics.TotalFrameMilliseconds > 0d);
            Assert.True(metrics.MaximumFrameMilliseconds > 0d);
            Assert.True(metrics.PrimaryRender.CompletedSamples > 0);
            Assert.True(metrics.PrimaryRender.TotalMilliseconds >= 0d);
            AssertStageMetricsAreValid(metrics.GlyphAtlas, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.PathAtlas, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.ScenePreparation, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.MaskEffects, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.PrimaryRender, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.WavefrontCompute, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.FinalComposite, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.WavefrontGeometry, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.WavefrontBinning, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.WavefrontCompaction, metrics.CompletedSamples);
            AssertStageMetricsAreValid(metrics.WavefrontCoarseFine, metrics.CompletedSamples);

            context.ResetGpuTimestampMetrics();
            metrics = context.GpuTimestampMetrics;
            Assert.Equal(0, metrics.SubmittedSamples);
            Assert.Equal(0, metrics.CompletedSamples);
            Assert.Equal(0d, metrics.TotalFrameMilliseconds);
            Assert.Equal(0d, metrics.MaximumFrameMilliseconds);
            Assert.Equal(0, metrics.PrimaryRender.CompletedSamples);
            Assert.Equal(0d, metrics.PrimaryRender.MaximumMilliseconds);

            for (int frame = 0; frame < 4; frame++)
            {
                window.Render();
            }

            metrics = WaitForTimestampSample(context);
            Assert.True(metrics.CompletedSamples > 0);
            Assert.True(metrics.PrimaryRender.CompletedSamples > 0);
        }
        finally
        {
            context.EnableGpuTimestampTracking = false;
            WgpuContext.OnWebGpuError -= OnWebGpuError;
        }

        Assert.False(hasError, $"WebGPU validation errors occurred:\n{errorDetails}");
    }

    private static GpuTimestampMetrics WaitForTimestampSample(WgpuContext context)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            context.PollDevice(wait: false);
            GpuTimestampMetrics metrics = context.GpuTimestampMetrics;
            if (metrics.CompletedSamples > 0)
            {
                return metrics;
            }

            System.Threading.Thread.Yield();
        }

        return context.GpuTimestampMetrics;
    }

    private static void AssertStageMetricsAreValid(GpuTimestampStageMetrics metrics, long frameSamples)
    {
        Assert.InRange(metrics.CompletedSamples, 0, frameSamples);
        Assert.True(metrics.TotalMilliseconds >= 0d);
        Assert.True(metrics.LastMilliseconds >= 0d);
        Assert.True(metrics.AverageMilliseconds >= 0d);
        Assert.True(metrics.MaximumMilliseconds >= 0d);
        if (metrics.CompletedSamples == 0)
        {
            Assert.Equal(0d, metrics.TotalMilliseconds);
            Assert.Equal(0d, metrics.MaximumMilliseconds);
        }
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
    public unsafe void Test_WavefrontGeometryMissUploadsAndFlattensOnlyAppendedRanges()
    {
        var context = HeadlessWindow.Shared.Context;
        using var engine = new WavefrontVectorEngine(context);
        using var destination = new GpuTexture(
            context,
            128,
            128,
            TextureFormat.Bgra8Unorm,
            TextureUsage.TextureBinding | TextureUsage.StorageBinding,
            "Wavefront Incremental Geometry Destination");
        var brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));

        var firstPath = new PathGeometry();
        var firstFigure = new PathFigure(new Vector2(4f, 4f), isClosed: false);
        firstFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(40f, 4f)));
        firstFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(44f, 20f)));
        firstFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(28f, 40f)));
        firstFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(8f, 32f)));
        firstFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(2f, 18f)));
        firstFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(4f, 4f)));
        firstPath.Figures.Add(firstFigure);

        var secondPath = new PathGeometry();
        var secondFigure = new PathFigure(new Vector2(60f, 8f), isClosed: false);
        secondFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(96f, 8f)));
        secondFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(60f, 36f)));
        secondPath.Figures.Add(secondFigure);

        void SubmitFrame()
        {
            var encoderDescriptor = new CommandEncoderDescriptor();
            var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
            Assert.NotEqual(nint.Zero, (nint)encoder);
            engine.EndFrame(encoder, destination, 1f);
            var commandDescriptor = new CommandBufferDescriptor();
            var commandBuffer = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
            Assert.NotEqual(nint.Zero, (nint)commandBuffer);
            context.Api.QueueSubmit(context.Queue, 1, &commandBuffer);
            context.Api.CommandBufferRelease(commandBuffer);
            context.Api.CommandEncoderRelease(encoder);
        }

        engine.BeginFrame();
        engine.DrawPath(firstPath, Matrix4x4.Identity, brush);
        SubmitFrame();
        Assert.Equal(6u, engine.LastUploadedRawCurveCount);
        Assert.Equal(6u, engine.LastFlattenedCurveCount);

        engine.BeginFrame();
        engine.DrawPath(firstPath, Matrix4x4.Identity, brush);
        SubmitFrame();
        Assert.Equal(0u, engine.LastUploadedBvhNodeCount);
        Assert.Equal(0u, engine.LastUploadedRawCurveCount);
        Assert.Equal(0u, engine.LastFlattenedCurveCount);

        engine.BeginFrame();
        engine.DrawPath(firstPath, Matrix4x4.Identity, brush);
        engine.DrawPath(secondPath, Matrix4x4.Identity, brush);
        SubmitFrame();
        Assert.False(engine.LastGeometryArenaReplay);
        Assert.Equal(1u, engine.LastUploadedBvhNodeCount);
        Assert.Equal(2u, engine.LastUploadedRawCurveCount);
        Assert.Equal(2u, engine.LastFlattenedCurveCount);
    }

    [Fact]
    public unsafe void Test_GpuPrefixScan_MatchesCpuOracleAcrossHierarchy()
    {
        var context = HeadlessWindow.Shared.Context;
        using var scan = new GpuPrefixScan(context);
        int[] counts = [1, 511, 512, 513, 1_025, 262_145];

        foreach (int count in counts)
        {
            var input = new uint[count];
            for (int index = 0; index < count; index++)
            {
                input[index] = (uint)((index * 17 + 3) % 11);
            }
            if (count == 513)
            {
                input[0] = uint.MaxValue;
                input[1] = 2u;
            }

            var expected = new uint[count];
            GpuPrefixScan.ExclusiveScanCpu(input, expected);
            scan.WriteInput(input);

            var encoderDescriptor = new CommandEncoderDescriptor();
            var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
            Assert.NotEqual(nint.Zero, (nint)encoder);
            scan.RecordExclusiveScan(encoder, (uint)count);

            var commandDescriptor = new CommandBufferDescriptor();
            var commandBuffer = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
            Assert.NotEqual(nint.Zero, (nint)commandBuffer);
            context.Api.QueueSubmit(context.Queue, 1, &commandBuffer);
            context.Api.CommandBufferRelease(commandBuffer);
            context.Api.CommandEncoderRelease(encoder);

            byte[] bytes = scan.OutputBuffer.ReadBytes(0, checked((uint)count * sizeof(uint)));
            uint[] actual = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public unsafe void Test_GpuSceneVisibility_MatchesStableCpuCompaction()
    {
        const int drawCount = 1_025;
        var context = HeadlessWindow.Shared.Context;
        using var visibility = new GpuSceneVisibility(context);
        Matrix4x4[] transforms =
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(2_000f, 0f, 0f)
        ];
        var draws = new GpuSceneDrawMetadata[drawCount];
        for (int index = 0; index < draws.Length; index++)
        {
            float x = index % 80;
            bool clippedOut = index % 10 == 0;
            draws[index] = new GpuSceneDrawMetadata
            {
                Bounds = new Vector4(x, 10f, x + 4f, 14f),
                ClipBounds = clippedOut
                    ? new Vector4(200f, 200f, 210f, 210f)
                    : new Vector4(0f, 0f, 100f, 100f),
                TransformIndex = (uint)(index & 1),
                SourceIndex = (uint)(10_000 + index),
                MaterialKey = (uint)(index % 7),
                Flags = GpuSceneVisibility.HasClipFlag
            };
        }

        var expectedSources = new uint[drawCount];
        var expectedMaterials = new uint[drawCount];
        var viewport = new Vector4(0f, 0f, 100f, 100f);
        var rootTransform = Matrix4x4.CreateTranslation(2f, 3f, 0f);
        int expectedCount = GpuSceneVisibility.CompactVisibleCpu(
            draws,
            transforms,
            viewport,
            rootTransform,
            expectedSources,
            expectedMaterials);

        visibility.Upload(draws, transforms);
        var encoderDescriptor = new CommandEncoderDescriptor();
        var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
        Assert.NotEqual(nint.Zero, (nint)encoder);
        visibility.Record(
            encoder,
            drawCount,
            (uint)transforms.Length,
            viewport,
            rootTransform,
            vertexCount: 6,
            firstVertex: 2,
            firstInstance: 3);
        var commandDescriptor = new CommandBufferDescriptor();
        var commandBuffer = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        Assert.NotEqual(nint.Zero, (nint)commandBuffer);
        context.Api.QueueSubmit(context.Queue, 1, &commandBuffer);
        context.Api.CommandBufferRelease(commandBuffer);
        context.Api.CommandEncoderRelease(encoder);

        uint actualCount = MemoryMarshal.Read<uint>(visibility.VisibleCountBuffer.ReadBytes(0, sizeof(uint)));
        Assert.Equal((uint)expectedCount, actualCount);
        uint[] actualSources = MemoryMarshal.Cast<byte, uint>(
            visibility.VisibleSourceBuffer.ReadBytes(0, actualCount * sizeof(uint))).ToArray();
        uint[] actualMaterials = MemoryMarshal.Cast<byte, uint>(
            visibility.VisibleMaterialBuffer.ReadBytes(0, actualCount * sizeof(uint))).ToArray();
        Assert.Equal(expectedSources.AsSpan(0, expectedCount).ToArray(), actualSources);
        Assert.Equal(expectedMaterials.AsSpan(0, expectedCount).ToArray(), actualMaterials);

        var indirect = MemoryMarshal.Read<GpuDrawIndirectArgs>(
            visibility.IndirectBuffer.ReadBytes(0, (uint)Marshal.SizeOf<GpuDrawIndirectArgs>()));
        Assert.Equal(6u, indirect.VertexCount);
        Assert.Equal((uint)expectedCount, indirect.InstanceCount);
        Assert.Equal(2u, indirect.FirstVertex);
        Assert.Equal(3u, indirect.FirstInstance);
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

            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, window.Compositor.Metrics.WavefrontPathCount);
            Assert.Equal(0, window.Compositor.Metrics.WavefrontGeometryCacheMisses);

            byte[] replayPixels = window.ReadPixels();
            Assert.Equal(255, replayPixels[centerIdx + 0]);
            Assert.Equal(0, replayPixels[centerIdx + 1]);
            Assert.Equal(0, replayPixels[centerIdx + 2]);
            Assert.Equal(255, replayPixels[centerIdx + 3]);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void Test_Wavefront_RetainedTransformPatchesWithoutInstanceUpload()
    {
        using var window = new HeadlessWindow(
            64,
            32,
            renderFormat: TextureFormat.Bgra8Unorm);
        window.Compositor.VectorEngine = ProGPU.Scene.Compositor.VectorRenderingEngine.Wavefront;
        var visual = new RetainedWavefrontPathVisual();
        window.Content = visual;

        try
        {
            window.Render();
            Assert.False(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1u, window.Compositor.Metrics.WavefrontUploadedInstanceCount);
            Assert.Equal(1, window.Compositor.Metrics.WavefrontPathCount);
            byte[] initialPixels = window.ReadPixels();
            int initialPath = (6 * 64 + 6) * 4;
            Assert.Equal(255, initialPixels[initialPath]);
            Assert.Equal(0, initialPixels[initialPath + 1]);
            Assert.Equal(0, initialPixels[initialPath + 2]);

            visual.TransformHandle.Translation = new Vector2(36f, 0f);
            visual.InvalidateRetainedTransform();
            window.Render();

            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            Assert.Equal(1, visual.RenderCount);
            Assert.Equal(1, window.Compositor.Metrics.WavefrontPathCount);
            Assert.Equal(0u, window.Compositor.Metrics.WavefrontUploadedInstanceCount);
            Assert.Equal(1u, window.Compositor.Metrics.WavefrontUploadedTransformCount);
            Assert.True(window.Compositor.Metrics.WavefrontRetainedTransformCount >= 2u);

            byte[] pixels = window.ReadPixels();
            int background = (20 * 64) * 4;
            int oldPath = (6 * 64 + 6) * 4;
            int movedPath = (6 * 64 + 38) * 4;
            Assert.Equal(pixels[background], pixels[oldPath]);
            Assert.Equal(pixels[background + 1], pixels[oldPath + 1]);
            Assert.Equal(pixels[background + 2], pixels[oldPath + 2]);
            Assert.Equal(255, pixels[movedPath]);
            Assert.Equal(0, pixels[movedPath + 1]);
            Assert.Equal(0, pixels[movedPath + 2]);
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

    private sealed class RetainedWavefrontPathVisual : FrameworkElement
    {
        private readonly PathGeometry _path;
        private readonly SolidColorBrush _brush = new(new Vector4(1f, 0f, 0f, 1f));

        public RetainedWavefrontPathVisual()
        {
            Width = 64f;
            Height = 32f;
            TransformHandle.Translation = new Vector2(4f, 0f);
            RetainedTransform = TransformHandle;
            _path = new PathGeometry();
            var figure = new PathFigure(Vector2.Zero, isClosed: true);
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(8f, 0f)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(8f, 8f)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(0f, 8f)));
            _path.Figures.Add(figure);
        }

        public ProGPU.Scene.SceneTransformHandle TransformHandle { get; } = new();
        public int RenderCount { get; private set; }

        public override void OnRender(ProGPU.Scene.DrawingContext context)
        {
            RenderCount++;
            context.DrawPath(_brush, null, _path);
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
