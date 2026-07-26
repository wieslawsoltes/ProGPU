using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Scene;
using SkiaSharp;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;

return GpuMemoryBaseline.Run(args);

internal static unsafe class GpuMemoryBaseline
{
    public static int Run(string[] args)
    {
        if (!TryParseOptions(args, out Options options, out string? error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        var windowOptions = WindowOptions.Default;
        windowOptions.API = GraphicsAPI.None;
        windowOptions.FramesPerSecond = options.FramesPerSecond;
        windowOptions.Size = new Vector2D<int>(options.Width, options.Height);
        windowOptions.Title = "ProGPU clear-only GPU memory baseline";
        windowOptions.TransparentFramebuffer = false;
        windowOptions.VSync = false;
        windowOptions.WindowBorder = WindowBorder.Fixed;

        using IWindow window = Window.Create(windowOptions);
        WgpuContext? context = null;
        RawMetalBaseline? rawMetal = null;
        Compositor? compositor = null;
        ContainerVisual? root = null;
        DrawingContext? filterDrawingContext = null;
        SKCanvas? filterCanvas = null;
        SKPaint? filterLayerPaint = null;
        SKPaint? filterFillPaint = null;
        var elapsed = new Stopwatch();
        long frames = 0;
        long lastReportedSecond = -1;
        int exitCode = 0;

        window.Load += () =>
        {
            try
            {
                if (options.StartupDelaySeconds > 0)
                {
                    Thread.Sleep(
                        TimeSpan.FromSeconds(options.StartupDelaySeconds));
                }

                if (options.Mode is
                    BaselineMode.RawMetal or
                    BaselineMode.RawMetalClear)
                {
                    Vector2D<int> framebuffer = window.FramebufferSize;
                    rawMetal = new RawMetalBaseline(
                        checked((uint)Math.Max(1, framebuffer.X)),
                        checked((uint)Math.Max(1, framebuffer.Y)),
                        createRenderTarget:
                            options.Mode == BaselineMode.RawMetalClear);
                }
                else if (options.Mode != BaselineMode.Window)
                {
                    context = new WgpuContext
                    {
                        DesiredMaximumFrameLatency = options.FrameLatency
                    };
                    context.Initialize(
                        options.Mode is BaselineMode.Device or BaselineMode.Filter
                            ? null
                            : window);
                }
                if (options.Mode == BaselineMode.Compositor)
                {
                    compositor = new Compositor(
                        context!,
                        context!.SwapChainFormat,
                        CompositorOptions.Default with
                        {
                            InitialVertexCount = 1024,
                            InitialIndexCount = 1536,
                            InitialColorGlyphAtlasSize = 64,
                            GlyphUniformStagingBytes = 16 * 1024,
                            EnableGpuHitTesting = false,
                            PrimarySampleCount = 1
                        });
                    root = new ContainerVisual();
                }
                else if (options.Mode == BaselineMode.Filter)
                {
                    filterDrawingContext = new DrawingContext();
                    filterCanvas = new SKCanvas(
                        filterDrawingContext,
                        options.Width,
                        options.Height,
                        context);
                    filterLayerPaint = new SKPaint
                    {
                        ImageFilter = SKImageFilter.CreateBlur(8f, 8f)
                    };
                    filterFillPaint = new SKPaint
                    {
                        Color = SKColors.CornflowerBlue
                    };
                }
                elapsed.Start();
                Console.WriteLine(
                    $"baseline pid={Environment.ProcessId} " +
                    $"mode={options.Mode.ToString().ToLowerInvariant()} " +
                    $"size={options.Width}x{options.Height} " +
                    $"latency={options.FrameLatency} fps={options.FramesPerSecond:R}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                exitCode = 1;
                window.Close();
            }
        };

        window.Render += _ =>
        {
            if (context == null)
            {
                if (rawMetal != null)
                {
                    rawMetal.SubmitEmptyCommandBuffer();
                    frames++;
                }
                CloseWhenDurationElapsed(window, options, elapsed);
                return;
            }

            Vector2D<int> framebuffer = window.FramebufferSize;
            if (options.Mode == BaselineMode.Device)
            {
                context.PollDevice(wait: false);
                CloseWhenDurationElapsed(window, options, elapsed);
                return;
            }
            if (options.Mode == BaselineMode.Filter)
            {
                TryRenderFilter(
                    context,
                    filterDrawingContext!,
                    filterCanvas!,
                    filterLayerPaint!,
                    filterFillPaint!,
                    options,
                    frames);
                frames++;
                long filterSecond = (long)elapsed.Elapsed.TotalSeconds;
                if (filterSecond != lastReportedSecond)
                {
                    lastReportedSecond = filterSecond;
                    Report(
                        context,
                        elapsed.Elapsed,
                        frames,
                        new Vector2D<int>(options.Width, options.Height));
                }
                CloseWhenDurationElapsed(window, options, elapsed);
                return;
            }

            if (framebuffer.X <= 0 ||
                framebuffer.Y <= 0 ||
                !context.TryReconfigureIfNeeded(
                    checked((uint)framebuffer.X),
                    checked((uint)framebuffer.Y)))
            {
                return;
            }

            bool rendered = options.Mode switch
            {
                BaselineMode.Surface => true,
                BaselineMode.Clear => TryRenderClear(context),
                BaselineMode.Compositor => TryRenderCompositor(
                    context,
                    compositor!,
                    root!,
                    options,
                    framebuffer),
                _ => false
            };
            if (!rendered)
            {
                return;
            }

            frames++;
            long currentSecond = (long)elapsed.Elapsed.TotalSeconds;
            if (currentSecond != lastReportedSecond)
            {
                lastReportedSecond = currentSecond;
                Report(context, elapsed.Elapsed, frames, framebuffer);
            }

            if (options.DurationSeconds > 0 &&
                elapsed.Elapsed.TotalSeconds >= options.DurationSeconds)
            {
                window.Close();
            }
        };

        window.Closing += () =>
        {
            if (context != null)
            {
                context.WaitIdle();
                Report(context, elapsed.Elapsed, frames, window.FramebufferSize);
                filterCanvas?.Dispose();
                filterCanvas = null;
                filterDrawingContext?.Clear();
                filterDrawingContext = null;
                filterLayerPaint?.Dispose();
                filterLayerPaint = null;
                filterFillPaint?.Dispose();
                filterFillPaint = null;
                compositor?.Dispose();
                compositor = null;
                root = null;
                context.Dispose();
                context = null;
            }
            rawMetal?.Dispose();
            rawMetal = null;
        };

        window.Run();
        return exitCode;
    }

    private static void TryRenderFilter(
        WgpuContext context,
        DrawingContext drawingContext,
        SKCanvas canvas,
        SKPaint layerPaint,
        SKPaint fillPaint,
        Options options,
        long frame)
    {
        drawingContext.Clear();
        canvas.Clear(SKColors.Transparent);
        int restoreCount = canvas.SaveLayer(layerPaint);
        float inset = 8f + frame % 17;
        canvas.DrawRect(
            new SKRect(
                inset,
                inset,
                options.Width - inset,
                options.Height - inset),
            fillPaint);
        canvas.RestoreToCount(restoreCount);
        drawingContext.Clear();
        context.PollDevice(wait: false);
    }

    private static void CloseWhenDurationElapsed(
        IWindow window,
        Options options,
        Stopwatch elapsed)
    {
        if (options.DurationSeconds > 0 &&
            elapsed.Elapsed.TotalSeconds >= options.DurationSeconds)
        {
            window.Close();
        }
    }

    private static bool TryRenderClear(WgpuContext context)
    {
        var surfaceTexture = new SurfaceTexture();
        context.Api.SurfaceGetCurrentTexture(context.Surface, &surfaceTexture);
        if (surfaceTexture.Status is
            SurfaceGetCurrentTextureStatus.Outdated or
            SurfaceGetCurrentTextureStatus.Lost)
        {
            return false;
        }
        if (surfaceTexture.Status != SurfaceGetCurrentTextureStatus.Success ||
            surfaceTexture.Texture == null)
        {
            throw new InvalidOperationException(
                $"WebGPU surface acquisition failed: {surfaceTexture.Status}.");
        }

        TextureView* view = null;
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            view = context.Api.TextureCreateView(surfaceTexture.Texture, null);
            var encoderDescriptor = new CommandEncoderDescriptor();
            encoder = context.Api.DeviceCreateCommandEncoder(
                context.Device,
                &encoderDescriptor);
            var colorAttachment = new RenderPassColorAttachment
            {
                View = view,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                DepthSlice = uint.MaxValue,
                ClearValue = new Color
                {
                    R = 0.02,
                    G = 0.025,
                    B = 0.04,
                    A = 1.0
                }
            };
            var passDescriptor = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment
            };
            pass = context.Api.CommandEncoderBeginRenderPass(
                encoder,
                &passDescriptor);
            context.Api.RenderPassEncoderEnd(pass);
            var commandBufferDescriptor = new CommandBufferDescriptor();
            commandBuffer = context.Api.CommandEncoderFinish(
                encoder,
                &commandBufferDescriptor);
            context.Api.QueueSubmit(context.Queue, 1, &commandBuffer);
            context.Api.SurfacePresent(context.Surface);
            context.PollDevice(wait: false);
            return true;
        }
        finally
        {
            if (pass != null)
                context.Api.RenderPassEncoderRelease(pass);
            if (commandBuffer != null)
                context.Api.CommandBufferRelease(commandBuffer);
            if (encoder != null)
                context.Api.CommandEncoderRelease(encoder);
            if (view != null)
                context.Api.TextureViewRelease(view);
            context.Api.TextureRelease(surfaceTexture.Texture);
        }
    }

    private static bool TryRenderCompositor(
        WgpuContext context,
        Compositor compositor,
        ContainerVisual root,
        Options options,
        Vector2D<int> framebuffer)
    {
        var surfaceTexture = new SurfaceTexture();
        context.Api.SurfaceGetCurrentTexture(context.Surface, &surfaceTexture);
        if (surfaceTexture.Status is
            SurfaceGetCurrentTextureStatus.Outdated or
            SurfaceGetCurrentTextureStatus.Lost)
        {
            return false;
        }
        if (surfaceTexture.Status != SurfaceGetCurrentTextureStatus.Success ||
            surfaceTexture.Texture == null)
        {
            throw new InvalidOperationException(
                $"WebGPU surface acquisition failed: {surfaceTexture.Status}.");
        }

        TextureView* view = null;
        try
        {
            view = context.Api.TextureCreateView(surfaceTexture.Texture, null);
            float dpiScale = Math.Max(
                framebuffer.X / (float)options.Width,
                framebuffer.Y / (float)options.Height);
            compositor.RenderScene(
                root,
                checked((uint)options.Width),
                checked((uint)options.Height),
                checked((uint)framebuffer.X),
                checked((uint)framebuffer.Y),
                dpiScale,
                view);
            context.Api.SurfacePresent(context.Surface);
            context.PollDevice(wait: false);
            return true;
        }
        finally
        {
            if (view != null)
                context.Api.TextureViewRelease(view);
            context.Api.TextureRelease(surfaceTexture.Texture);
        }
    }

    private static void Report(
        WgpuContext context,
        TimeSpan elapsed,
        long frames,
        Vector2D<int> framebuffer)
    {
        if (!context.TryCaptureNativeResourceSnapshot(out var resources))
        {
            return;
        }

        Console.WriteLine(
            $"sample seconds={elapsed.TotalSeconds:F3} frames={frames} " +
            $"framebuffer={framebuffer.X}x{framebuffer.Y} " +
            $"metalMiB={ToMiB(resources.MetalAllocatedBytes):F3} " +
            $"commandBuffers={resources.CommandBuffers.KeptFromUser} " +
            $"buffers={resources.Buffers.KeptFromUser} " +
            $"textures={resources.Textures.KeptFromUser} " +
            $"textureViews={resources.TextureViews.KeptFromUser} " +
            $"bindGroups={resources.BindGroups.KeptFromUser}");
    }

    private static double ToMiB(ulong bytes) =>
        bytes / (1024d * 1024d);

    private static bool TryParseOptions(
        string[] args,
        out Options options,
        out string? error)
    {
        int width = 1024;
        int height = 800;
        int durationSeconds = 0;
        int startupDelaySeconds = 0;
        double framesPerSecond = 60d;
        uint frameLatency = 1;
        BaselineMode mode = BaselineMode.Clear;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument == "--width" &&
                TryReadInt(args, ref index, out int parsedWidth) &&
                parsedWidth > 0)
            {
                width = parsedWidth;
            }
            else if (argument == "--height" &&
                     TryReadInt(args, ref index, out int parsedHeight) &&
                     parsedHeight > 0)
            {
                height = parsedHeight;
            }
            else if (argument == "--duration" &&
                     TryReadInt(args, ref index, out int parsedDuration) &&
                     parsedDuration >= 0)
            {
                durationSeconds = parsedDuration;
            }
            else if (argument == "--fps" &&
                     TryReadDouble(args, ref index, out double parsedFps) &&
                     parsedFps > 0d)
            {
                framesPerSecond = parsedFps;
            }
            else if (argument == "--startup-delay" &&
                     TryReadInt(args, ref index, out int parsedStartupDelay) &&
                     parsedStartupDelay is >= 0 and <= 30)
            {
                startupDelaySeconds = parsedStartupDelay;
            }
            else if (argument == "--latency" &&
                     TryReadUInt(args, ref index, out uint parsedLatency) &&
                     parsedLatency is 1 or 2 or 3)
            {
                frameLatency = parsedLatency;
            }
            else if (argument == "--mode" &&
                     index + 1 < args.Length &&
                     Enum.TryParse(
                         args[++index],
                         ignoreCase: true,
                         out BaselineMode parsedMode))
            {
                mode = parsedMode;
            }
            else if (argument is "--help" or "-h")
            {
                options = default;
                error = null;
                return false;
            }
            else
            {
                options = default;
                error = $"Unknown or invalid option: {argument}";
                return false;
            }
        }

        options = new Options(
            width,
            height,
            durationSeconds,
            startupDelaySeconds,
            framesPerSecond,
            frameLatency,
            mode);
        error = null;
        return true;
    }

    private static bool TryReadInt(
        string[] args,
        ref int index,
        out int value)
    {
        value = 0;
        return index + 1 < args.Length &&
            int.TryParse(
                args[++index],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static bool TryReadUInt(
        string[] args,
        ref int index,
        out uint value)
    {
        value = 0;
        return index + 1 < args.Length &&
            uint.TryParse(
                args[++index],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static bool TryReadDouble(
        string[] args,
        ref int index,
        out double value)
    {
        value = 0d;
        return index + 1 < args.Length &&
            double.TryParse(
                args[++index],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: ProGPU.GpuMemoryBaseline " +
            "[--width 1024] [--height 800] [--duration seconds] " +
            "[--startup-delay 0..30] [--fps 60] [--latency 1|2|3] " +
            "[--mode window|rawmetal|rawmetalclear|device|surface|clear|compositor|filter]");
    }

    private readonly record struct Options(
        int Width,
        int Height,
        int DurationSeconds,
        int StartupDelaySeconds,
        double FramesPerSecond,
        uint FrameLatency,
        BaselineMode Mode);

    private enum BaselineMode
    {
        Window,
        RawMetal,
        RawMetalClear,
        Device,
        Surface,
        Clear,
        Compositor,
        Filter
    }
}

internal sealed class RawMetalBaseline : IDisposable
{
    private const string MetalLibrary =
        "/System/Library/Frameworks/Metal.framework/Metal";
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    private readonly nint _device;
    private readonly nint _commandQueue;
    private readonly nint _autoreleasePoolClass;
    private readonly nint _allocSelector;
    private readonly nint _initSelector;
    private readonly nint _drainSelector;
    private readonly nint _retainSelector;
    private readonly nint _commandBufferSelector;
    private readonly nint _renderCommandEncoderSelector;
    private readonly nint _endEncodingSelector;
    private readonly nint _commitSelector;
    private readonly nint _waitUntilCompletedSelector;
    private readonly nint _releaseSelector;
    private nint _renderTarget;
    private nint _renderPassDescriptor;
    private bool _disposed;

    public RawMetalBaseline(
        uint width,
        uint height,
        bool createRenderTarget)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The raw Metal baseline requires macOS.");
        }

        _autoreleasePoolClass = objc_getClass("NSAutoreleasePool");
        _allocSelector = sel_registerName("alloc");
        _initSelector = sel_registerName("init");
        _drainSelector = sel_registerName("drain");
        _retainSelector = sel_registerName("retain");
        _releaseSelector = sel_registerName("release");
        _commandBufferSelector = sel_registerName("commandBuffer");
        _renderCommandEncoderSelector =
            sel_registerName("renderCommandEncoderWithDescriptor:");
        _endEncodingSelector = sel_registerName("endEncoding");
        _commitSelector = sel_registerName("commit");
        _waitUntilCompletedSelector = sel_registerName("waitUntilCompleted");

        nint pool = CreateAutoreleasePool();
        _device = MTLCreateSystemDefaultDevice();
        if (_device == 0)
        {
            throw new InvalidOperationException(
                "Metal did not expose a system-default device.");
        }
        Send(_device, _retainSelector);

        _commandQueue = Send(
            _device,
            sel_registerName("newCommandQueue"));
        if (_commandQueue == 0)
        {
            throw new InvalidOperationException(
                "Metal failed to create a command queue.");
        }

        if (createRenderTarget)
        {
            CreateRenderTarget(width, height);
        }
        SendVoid(pool, _drainSelector);
    }

    public void SubmitEmptyCommandBuffer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        nint pool = CreateAutoreleasePool();
        nint commandBuffer = Send(
            _commandQueue,
            _commandBufferSelector);
        if (commandBuffer == 0)
        {
            throw new InvalidOperationException(
                "Metal failed to create a command buffer.");
        }

        if (_renderPassDescriptor != 0)
        {
            nint encoder = SendObject(
                commandBuffer,
                _renderCommandEncoderSelector,
                _renderPassDescriptor);
            if (encoder == 0)
            {
                throw new InvalidOperationException(
                    "Metal failed to create a render command encoder.");
            }
            SendVoid(encoder, _endEncodingSelector);
        }

        SendVoid(commandBuffer, _commitSelector);
        SendVoid(commandBuffer, _waitUntilCompletedSelector);
        SendVoid(pool, _drainSelector);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_renderPassDescriptor != 0)
        {
            SendVoid(_renderPassDescriptor, _releaseSelector);
            _renderPassDescriptor = 0;
        }
        if (_renderTarget != 0)
        {
            SendVoid(_renderTarget, _releaseSelector);
            _renderTarget = 0;
        }
        SendVoid(_commandQueue, _releaseSelector);
        SendVoid(_device, _releaseSelector);
    }

    private nint CreateAutoreleasePool()
    {
        return Send(
            Send(_autoreleasePoolClass, _allocSelector),
            _initSelector);
    }

    private void CreateRenderTarget(uint width, uint height)
    {
        const ulong Bgra8Unorm = 80;
        const ulong RenderTargetUsage = 1UL << 2;
        const ulong LoadActionClear = 2;
        const ulong StoreActionStore = 1;

        nint textureDescriptorClass =
            objc_getClass("MTLTextureDescriptor");
        nint textureDescriptor = SendUInt64UInt64UInt64Byte(
            textureDescriptorClass,
            sel_registerName(
                "texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            Bgra8Unorm,
            width,
            height,
            0);
        SendVoidUInt64(
            textureDescriptor,
            sel_registerName("setUsage:"),
            RenderTargetUsage);
        _renderTarget = SendObject(
            _device,
            sel_registerName("newTextureWithDescriptor:"),
            textureDescriptor);
        if (_renderTarget == 0)
        {
            throw new InvalidOperationException(
                "Metal failed to create the raw baseline render target.");
        }

        nint renderPassDescriptorClass =
            objc_getClass("MTLRenderPassDescriptor");
        _renderPassDescriptor = Send(
            renderPassDescriptorClass,
            sel_registerName("renderPassDescriptor"));
        Send(_renderPassDescriptor, _retainSelector);
        nint colorAttachments = Send(
            _renderPassDescriptor,
            sel_registerName("colorAttachments"));
        nint colorAttachment = SendUInt64(
            colorAttachments,
            sel_registerName("objectAtIndexedSubscript:"),
            0);
        SendVoidObject(
            colorAttachment,
            sel_registerName("setTexture:"),
            _renderTarget);
        SendVoidUInt64(
            colorAttachment,
            sel_registerName("setLoadAction:"),
            LoadActionClear);
        SendVoidUInt64(
            colorAttachment,
            sel_registerName("setStoreAction:"),
            StoreActionStore);
        SendVoidClearColor(
            colorAttachment,
            sel_registerName("setClearColor:"),
            new MetalClearColor(0.02, 0.025, 0.04, 1.0));
    }

    [DllImport(MetalLibrary)]
    private static extern nint MTLCreateSystemDefaultDevice();

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(
        nint receiver,
        nint selector,
        nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendUInt64(
        nint receiver,
        nint selector,
        ulong value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendUInt64UInt64UInt64Byte(
        nint receiver,
        nint selector,
        ulong first,
        ulong second,
        ulong third,
        byte fourth);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidObject(
        nint receiver,
        nint selector,
        nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidUInt64(
        nint receiver,
        nint selector,
        ulong value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidClearColor(
        nint receiver,
        nint selector,
        MetalClearColor value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MetalClearColor(
        double Red,
        double Green,
        double Blue,
        double Alpha);
}
