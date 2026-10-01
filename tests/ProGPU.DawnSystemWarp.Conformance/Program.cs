using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;

if (!OperatingSystem.IsWindows() || args.Length is < 1 or > 2 ||
    !string.Equals(RuntimeInformation.ProcessArchitecture.ToString(), args[0], StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Run on the requested native Windows architecture.");

if (args.Length == 2 && args[1] == "--queue-abandonment")
{
    DawnGpuContext.VerifyDawnQueueWaitAbandonmentForDiagnostics();
    Console.WriteLine("Dawn queue wait abandonment retired actual native callback userdata exactly once.");
    return;
}

if (args.Length == 2 && args[1] == "--adapter-abandonment")
{
    DawnGpuContext.VerifyDawnAdapterRequestAbandonmentForDiagnostics();
    Console.WriteLine("Dawn ordinary adapter request abandonment retired its native result and userdata exactly once.");
    return;
}

if (args.Length == 2 && args[1] == "--request-cancellation")
{
    DawnGpuContext.VerifySystemWarpRequestCancellationForDiagnostics(deviceRequest: false);
    DawnGpuContext.VerifySystemWarpRequestCancellationForDiagnostics(deviceRequest: true);
    Console.WriteLine("Dawn system WARP native adapter and device request cancellation retired exactly once.");
    return;
}

if (args.Length == 2 && args[1] == "--device-loss")
{
    ExerciseDeviceLoss();
    Console.WriteLine("Dawn system WARP native device loss and independent replacement readback passed.");
    return;
}

if (args.Length == 2 && args[1] == "--callback-fault")
{
    TextWriter originalError = Console.Error;
    var throwingWriter = new ThrowingDiagnosticWriter();
    Console.SetError(throwingWriter);
    try { ExerciseDeviceLoss(throwFromCallback: true); }
    finally { Console.SetError(originalError); }
    if (throwingWriter.Attempts == 0)
        throw new InvalidOperationException("The throwing native diagnostic writer was not exercised.");
    Console.WriteLine("Dawn system WARP throwing loss subscriber and diagnostic writer preserved native retirement.");
    return;
}

if (args.Length == 2)
{
    if (args[1] != "--foreign-resolver") throw new ArgumentException("Unknown isolated control.");
    int resolverInvocations = 0;
    NativeLibrary.SetDllImportResolver(typeof(WebGpuSharp.FFI.WebGPU_FFI).Assembly,
        (_, _, _) => { resolverInvocations++; return 0; });
    try
    {
        using DawnGpuContext rejected = DawnGpuContext.CreateSystemWarpOffscreen();
        throw new InvalidOperationException("A foreign resolver was admitted.");
    }
    catch (TypeInitializationException error) when (
        error.InnerException is InvalidOperationException ownership &&
        ownership.Message.Contains("ownership of both native import resolvers", StringComparison.Ordinal))
    {
        if (resolverInvocations != 0)
            throw new InvalidOperationException("Foreign imports executed before provider rejection.");
        Console.WriteLine("Dawn system WARP foreign resolver rejected before native imports.");
        return;
    }
}

// Exercise fresh device ownership twice and real color replacement on each
// device. Every pixel and untouched caller tail is independently specified;
// adapter creation or queue submission alone cannot satisfy this control.
DawnSystemWarpAdapterLuid? originalLuid = null;
for (int lifetime = 0; lifetime < 2; lifetime++)
{
    using DawnGpuContext dawn = DawnGpuContext.CreateSystemWarpOffscreen();
    if (dawn.SystemWarpAdapterLuid is not { } luid ||
        dawn.Context.AdapterBackendType != BackendType.D3D12 ||
        dawn.Context.AdapterSelectionDiagnostics.AdapterType != AdapterType.Cpu)
        throw new InvalidOperationException("The factory did not retain verified system WARP identity.");
    if (originalLuid.HasValue && originalLuid.Value != luid)
        throw new InvalidOperationException("The system WARP identity changed between independent devices.");
    originalLuid = luid;
    using var texture = new GpuTexture(dawn.Context, 7, 3, TextureFormat.Rgba8Unorm,
        TextureUsage.RenderAttachment | TextureUsage.CopySrc);
    for (int frame = 0; frame < 2; frame++)
    {
        Clear(dawn.Context, texture, frame == 0
            ? new Color { R = 1, G = 0, B = 1, A = 1 }
            : new Color { R = 0, G = 1, B = 0, A = 1 });
        byte[] pixels = new byte[7 * 3 * 4 + 17];
        Array.Fill(pixels, (byte)0xA5);
        texture.ReadPixels(pixels);
        for (int pixel = 0; pixel < 21; pixel++)
        {
            int offset = pixel * 4;
            byte redBlue = frame == 0 ? (byte)255 : (byte)0;
            byte green = frame == 0 ? (byte)0 : (byte)255;
            if (pixels[offset] != redBlue || pixels[offset + 1] != green ||
                pixels[offset + 2] != redBlue || pixels[offset + 3] != 255)
                throw new InvalidOperationException($"WARP readback mismatch: lifetime={lifetime}, frame={frame}, pixel={pixel}.");
        }
        for (int index = 84; index < pixels.Length; index++)
            if (pixels[index] != 0xA5) throw new InvalidOperationException("Readback overwrote the caller tail.");
        if (dawn.Context.IsDeviceLost) throw new InvalidOperationException("WARP device lost during readback.");
    }
    Console.WriteLine($"Verified WARP lifetime {lifetime}: {luid.LowPart:X8}:{luid.HighPart:X8}; {dawn.Context.AdapterName}");
}
Console.WriteLine("Dawn system WARP conformance passed: 2 device lifetimes, 4 full RGBA readbacks, 0 skipped.");

static void ExerciseDeviceLoss(bool throwFromCallback = false)
{
    // Reuse the original shared-memory probe's real Dawn loss diagnostic and
    // bounded nonblocking event drain; no managed synthetic loss notification.
    using DawnGpuContext lost = DawnGpuContext.CreateSystemWarpOffscreen();
    int nativeLoss = 0;
    void OnLoss(DeviceLostReason reason, string message)
    {
        if (reason == DeviceLostReason.Unknown &&
            message.Contains("ProGPU forced native device-loss qualification", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref nativeLoss);
            if (throwFromCallback) throw new InvalidOperationException("Injected application loss subscriber failure.");
        }
    }
    WgpuContext.OnWebGpuDeviceLost += OnLoss;
    try
    {
        lost.ForceDeviceLossForDiagnostics();
        for (int attempt = 0; attempt < 100 &&
            (Volatile.Read(ref nativeLoss) == 0 || !lost.Context.IsDeviceLost); attempt++)
        {
            lost.Context.PollDevice(wait: false);
            Thread.Sleep(1);
        }
        if (Volatile.Read(ref nativeLoss) != 1 || !lost.Context.IsDeviceLost)
            throw new InvalidOperationException("The real native loss callback was not published exactly once.");
    }
    finally { WgpuContext.OnWebGpuDeviceLost -= OnLoss; }

    bool rejected = false;
    try { _ = lost.GetNativeDeviceHandles(); }
    catch (ObjectDisposedException) { rejected = true; }
    if (!rejected) throw new InvalidOperationException("Lost-device handles were published to a new consumer.");

    // The lost context remains live here: its loss cannot poison an independent
    // replacement's state, and both original owners must still retire safely.
    using DawnGpuContext replacement = DawnGpuContext.CreateSystemWarpOffscreen();
    if (replacement.Context.IsDeviceLost || replacement.SystemWarpAdapterLuid != lost.SystemWarpAdapterLuid)
        throw new InvalidOperationException("The independent replacement did not retain a healthy system WARP device.");
    using var target = new GpuTexture(replacement.Context, 1, 1, TextureFormat.Rgba8Unorm,
        TextureUsage.RenderAttachment | TextureUsage.CopySrc);
    Clear(replacement.Context, target, new Color { R = 0, G = 0, B = 1, A = 1 });
    if (!target.ReadPixels().AsSpan().SequenceEqual(new byte[] { 0, 0, 255, 255 }))
        throw new InvalidOperationException("The replacement device did not complete its independent blue readback.");
}

static unsafe void Clear(WgpuContext context, GpuTexture target, Color color)
{
    var api = context.Api;
    CommandEncoder* encoder = api.DeviceCreateCommandEncoder(context.Device, null);
    if (encoder == null) throw new InvalidOperationException("No WARP command encoder.");
    try
    {
        var attachment = new RenderPassColorAttachment
        {
            View = target.ViewPtr, LoadOp = LoadOp.Clear, StoreOp = StoreOp.Store, ClearValue = color
        };
        var descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
        RenderPassEncoder* pass = api.CommandEncoderBeginRenderPass(encoder, &descriptor);
        if (pass == null) throw new InvalidOperationException("No WARP render pass.");
        try { api.RenderPassEncoderEnd(pass); }
        finally { api.RenderPassEncoderRelease(pass); }
        CommandBuffer* commands = api.CommandEncoderFinish(encoder, null);
        if (commands == null) throw new InvalidOperationException("No WARP command buffer.");
        try { context.Submit(1, &commands); }
        finally { api.CommandBufferRelease(commands); }
    }
    finally { api.CommandEncoderRelease(encoder); }
}

sealed class ThrowingDiagnosticWriter : TextWriter
{
    private int _attempts;
    public int Attempts => Volatile.Read(ref _attempts);
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void WriteLine(string? value)
    {
        Interlocked.Increment(ref _attempts);
        throw new IOException("Injected application diagnostic writer failure.");
    }
}
