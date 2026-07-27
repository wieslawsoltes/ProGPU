using ProGPU.Avalonia;
using ProGPU.Backend.Dawn;
using ProGPU.Scene;
using ProGPU.Vector;
using System.Numerics;
using WebGpuSharp;
using WebGpuSharp.FFI;
using SW = Silk.NET.WebGPU;

bool forceDeviceLoss = args.Contains(
    "--force-device-loss",
    StringComparer.Ordinal);

if (!OperatingSystem.IsMacOS())
{
    Console.WriteLine("Dawn IOSurface probe is supported only on macOS.");
    return 0;
}

const uint width = 64;
const uint height = 64;
nint ioSurface = GpuSharingInterop.CreateMacSharedSurface(width, height);
if (ioSurface == 0)
{
    throw new InvalidOperationException("Could not allocate the probe IOSurface.");
}

try
{
    using DawnGpuContext dawnContext =
        DawnGpuContext.CreateMetalPresentation();
    DeviceHandle deviceHandle = dawnContext.Device;
    QueueHandle queueHandle = dawnContext.Queue;
    DawnSharedTextureMemoryFeature feature =
        dawnContext.SharedTextureMemory;
    var translatedApi =
        (DawnWebGpuApi)dawnContext.Context.Api;
    using DawnSharedTextureMemory memory =
        feature.ImportIOSurface(ioSurface);
    DawnSharedTextureMemoryProperties properties = memory.GetProperties();
    if (properties.Size.Width != width ||
        properties.Size.Height != height ||
        properties.Format != TextureFormat.BGRA8Unorm ||
        !properties.Usage.HasFlag(TextureUsage.RenderAttachment))
    {
        throw new InvalidOperationException(
            $"Unexpected shared texture properties: {properties}.");
    }

    using TextureHandle texture = memory.CreateTexture(
        TextureUsage.RenderAttachment |
        TextureUsage.TextureBinding |
        TextureUsage.CopySrc,
        "ProGPU Dawn IOSurface Texture"u8);

    using var compositor = new Compositor(
        dawnContext.Context,
        SW.TextureFormat.Bgra8Unorm,
        CompositorOptions.Default with
        {
            InitialVertexCount = 256,
            InitialIndexCount = 384,
            InitialColorGlyphAtlasSize = 64,
            GlyphUniformStagingBytes = 4 * 1024,
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1
        })
    {
        ClearColor = new Vector4(0.25f, 0.50f, 0.75f, 1f)
    };
    var root = new DrawingVisual
    {
        Size = new Vector2(width, height)
    };
    root.Context.DrawRectangle(
        new SolidColorBrush(
            new Vector4(1f, 0.125f, 0.25f, 1f)),
        pen: null,
        new Rect(16f, 16f, 32f, 32f));
    using DawnMetalEndAccessResult first = RenderCompositorClear(
        memory,
        texture,
        compositor,
        root,
        translatedApi,
        width,
        height);
    if (first.SharedEvent == 0 || first.SignaledValue == 0)
    {
        throw new InvalidOperationException(
            "Dawn did not export a usable Metal timeline fence.");
    }

    dawnContext.Context.WaitIdle();
    ValidateFirstPixel(
        ioSurface,
        expectedBlue: 191,
        expectedGreen: 128,
        expectedRed: 64,
        expectedAlpha: 255);
    ValidatePixel(
        ioSurface,
        x: 32,
        y: 32,
        expectedBlue: 64,
        expectedGreen: 32,
        expectedRed: 255,
        expectedAlpha: 255);

    using DawnSharedFence importedFence =
        feature.ImportMetalSharedEvent(first.SharedEvent);
    using DawnMetalEndAccessResult second = RenderClear(
        memory,
        texture,
        deviceHandle,
        queueHandle,
        translatedApi,
        initialized: true,
        importedFence,
        first.SignaledValue,
        new WebGpuSharp.Color(0.75, 0.25, 0.50, 1.0));
    if (second.SharedEvent == 0 ||
        second.SignaledValue <= first.SignaledValue)
    {
        throw new InvalidOperationException(
            "The second shared access did not advance the Metal timeline.");
    }

    dawnContext.Context.WaitIdle();
    ValidateFirstPixel(
        ioSurface,
        expectedBlue: 128,
        expectedGreen: 64,
        expectedRed: 191,
        expectedAlpha: 255);

    if (forceDeviceLoss)
    {
        bool observedNativeLoss = false;
        void OnDeviceLost(
            SW.DeviceLostReason reason,
            string message)
        {
            if (reason != SW.DeviceLostReason.Destroyed &&
                message.Contains(
                    "forced native device-loss",
                    StringComparison.Ordinal))
            {
                observedNativeLoss = true;
            }
        }

        ProGPU.Backend.WgpuContext.OnWebGpuDeviceLost +=
            OnDeviceLost;
        try
        {
            dawnContext.ForceDeviceLossForDiagnostics();
            for (int attempt = 0;
                 attempt < 100 &&
                 (!observedNativeLoss ||
                  !dawnContext.Context.IsDeviceLost);
                 attempt++)
            {
                dawnContext.Context.PollDevice(wait: false);
                Thread.Sleep(1);
            }

            if (!observedNativeLoss ||
                !dawnContext.Context.IsDeviceLost)
            {
                throw new InvalidOperationException(
                    "Dawn did not publish the forced native device-loss callback.");
            }
        }
        finally
        {
            ProGPU.Backend.WgpuContext.OnWebGpuDeviceLost -=
                OnDeviceLost;
        }

        using DawnGpuContext replacement =
            DawnGpuContext.CreateMetalPresentation();
        if (replacement.Context.IsDeviceLost ||
            !replacement.Context.IsInitialized)
        {
            throw new InvalidOperationException(
                "A replacement Dawn device did not start healthy.");
        }
    }

    Console.WriteLine(
        $"PASS Dawn IOSurface {width}x{height}, " +
        $"format={properties.Format}, usage={properties.Usage}, " +
        $"timeline={first.SignaledValue}->{second.SignaledValue}, " +
        $"forcedDeviceLoss={(forceDeviceLoss ? "pass" : "not-requested")}");
    return 0;
}
finally
{
    GpuSharingInterop.ReleaseMacSharedSurface(ioSurface);
}

static unsafe DawnMetalEndAccessResult RenderCompositorClear(
    DawnSharedTextureMemory memory,
    TextureHandle texture,
    Compositor compositor,
    DrawingVisual root,
    DawnWebGpuApi api,
    uint width,
    uint height)
{
    memory.BeginAccess(
        texture,
        initialized: false);
    var descriptor = new SW.TextureViewDescriptor
    {
        Format = SW.TextureFormat.Bgra8Unorm,
        Dimension = SW.TextureViewDimension.Dimension2D,
        MipLevelCount = 1,
        ArrayLayerCount = 1,
        Aspect = SW.TextureAspect.All
    };
    SW.TextureView* view =
        api.TextureCreateView(
            (SW.Texture*)texture.GetAddress(),
            &descriptor);
    try
    {
        compositor.RenderScene(root, width, height, view);
    }
    finally
    {
        api.TextureViewRelease(view);
    }
    return memory.EndAccessAndExportMetalSharedEvent(texture);
}

static unsafe DawnMetalEndAccessResult RenderClear(
    DawnSharedTextureMemory memory,
    TextureHandle texture,
    DeviceHandle device,
    QueueHandle queue,
    DawnWebGpuApi api,
    bool initialized,
    DawnSharedFence? waitFence,
    ulong waitValue,
    WebGpuSharp.Color color)
{
    memory.BeginAccess(texture, initialized, waitFence, waitValue);

    var silkDevice = (SW.Device*)device.GetAddress();
    var silkQueue = (SW.Queue*)queue.GetAddress();
    var silkTexture = (SW.Texture*)texture.GetAddress();
    SW.TextureView* view =
        api.TextureCreateView(silkTexture, null);
    var encoderDescriptor = new SW.CommandEncoderDescriptor();
    SW.CommandEncoder* encoder =
        api.DeviceCreateCommandEncoder(
            silkDevice,
            &encoderDescriptor);
    SW.RenderPassColorAttachment* attachments =
        stackalloc SW.RenderPassColorAttachment[1];
    attachments[0] = new SW.RenderPassColorAttachment
    {
        View = view,
        LoadOp = SW.LoadOp.Clear,
        StoreOp = SW.StoreOp.Store,
        ClearValue = new SW.Color
        {
            R = color.R,
            G = color.G,
            B = color.B,
            A = color.A
        }
    };
    var descriptor = new SW.RenderPassDescriptor
    {
        ColorAttachmentCount = 1,
        ColorAttachments = attachments
    };
    SW.RenderPassEncoder* pass =
        api.CommandEncoderBeginRenderPass(encoder, &descriptor);
    api.RenderPassEncoderEnd(pass);
    var commandBufferDescriptor =
        new SW.CommandBufferDescriptor();
    SW.CommandBuffer* commandBuffer =
        api.CommandEncoderFinish(
            encoder,
            &commandBufferDescriptor);
    api.QueueSubmit(silkQueue, 1, &commandBuffer);
    api.CommandBufferRelease(commandBuffer);
    api.RenderPassEncoderRelease(pass);
    api.CommandEncoderRelease(encoder);
    api.TextureViewRelease(view);

    return memory.EndAccessAndExportMetalSharedEvent(texture);
}

static unsafe void ValidateFirstPixel(
    nint ioSurface,
    byte expectedBlue,
    byte expectedGreen,
    byte expectedRed,
    byte expectedAlpha)
{
    ValidatePixel(
        ioSurface,
        x: 0,
        y: 0,
        expectedBlue,
        expectedGreen,
        expectedRed,
        expectedAlpha);
}

static unsafe void ValidatePixel(
    nint ioSurface,
    uint x,
    uint y,
    byte expectedBlue,
    byte expectedGreen,
    byte expectedRed,
    byte expectedAlpha)
{
    int lockStatus = GpuSharingInterop.IOSurfaceLock(
        ioSurface,
        1,
        null);
    if (lockStatus != 0)
    {
        throw new InvalidOperationException(
            $"IOSurfaceLock failed with status {lockStatus}.");
    }

    try
    {
        byte* pixels =
            (byte*)GpuSharingInterop.IOSurfaceGetBaseAddress(ioSurface);
        if (pixels == null)
        {
            throw new InvalidOperationException(
                "IOSurface has no CPU-visible base address.");
        }
        nuint bytesPerRow =
            GpuSharingInterop.IOSurfaceGetBytesPerRow(ioSurface);
        byte* pixel = pixels + y * bytesPerRow + x * 4;

        AssertNear(pixel[0], expectedBlue, "blue");
        AssertNear(pixel[1], expectedGreen, "green");
        AssertNear(pixel[2], expectedRed, "red");
        AssertNear(pixel[3], expectedAlpha, "alpha");
    }
    finally
    {
        GpuSharingInterop.IOSurfaceUnlock(ioSurface, 1, null);
    }
}

static void AssertNear(byte actual, byte expected, string channel)
{
    if (Math.Abs(actual - expected) > 1)
    {
        throw new InvalidOperationException(
            $"Unexpected {channel} channel: expected {expected}, actual {actual}.");
    }
}
