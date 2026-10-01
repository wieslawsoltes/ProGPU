using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;

if (!OperatingSystem.IsWindows() || args.Length != 1 ||
    !string.Equals(RuntimeInformation.ProcessArchitecture.ToString(), args[0], StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Run on the requested native Windows architecture.");

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
