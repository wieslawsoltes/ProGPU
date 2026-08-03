using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Records a render-pass clear directly into a GPU texture.
/// </summary>
/// <remarks>
/// The operation is O(P) GPU attachment bandwidth for P target pixels and
/// O(1) CPU/GPU command storage. It performs no texture upload, mapping, or
/// readback.
/// </remarks>
public static unsafe class GpuTextureClearer
{
    public static void Clear(
        GpuTexture target,
        Color color)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuTexture));
        }
        if (!target.Usage.HasFlag(
                TextureUsage.RenderAttachment))
        {
            throw new InvalidOperationException(
                "GPU texture clear requires RenderAttachment usage.");
        }
        if (target.Dimension !=
                GpuTextureDimension.Dimension2D ||
            target.DepthOrArrayLayers != 1 ||
            target.SampleCount != 1)
        {
            throw new NotSupportedException(
                "GPU texture clear supports single-sample 2D textures with one layer.");
        }
        if (!double.IsFinite(color.R) ||
            !double.IsFinite(color.G) ||
            !double.IsFinite(color.B) ||
            !double.IsFinite(color.A))
        {
            throw new ArgumentOutOfRangeException(
                nameof(color),
                "GPU texture clear values must be finite.");
        }

        WgpuContext context = target.Context;
        lock (context.RenderLock)
        {
            if (context.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(WgpuContext));
            }

            ClearCore(
                target,
                color);
        }
    }

    private static void ClearCore(
        GpuTexture target,
        Color color)
    {
        WgpuContext context = target.Context;
        var wgpu = context.Api;
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDescriptor =
                new CommandEncoderDescriptor();
            encoder =
                wgpu.DeviceCreateCommandEncoder(
                    context.Device,
                    &encoderDescriptor);
            if (encoder == null)
            {
                throw new InvalidOperationException(
                    "Failed to create a command encoder for GPU texture clear.");
            }

            var colorAttachment =
                new RenderPassColorAttachment
                {
                    View = target.ViewPtr,
                    ResolveTarget = null,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = color
                };
            var passDescriptor =
                new RenderPassDescriptor
                {
                    ColorAttachmentCount = 1,
                    ColorAttachments = &colorAttachment
                };
            pass =
                wgpu.CommandEncoderBeginRenderPass(
                    encoder,
                    &passDescriptor);
            if (pass == null)
            {
                throw new InvalidOperationException(
                    "Failed to begin a render pass for GPU texture clear.");
            }

            wgpu.RenderPassEncoderEnd(pass);
            wgpu.RenderPassEncoderRelease(pass);
            pass = null;

            var commandBufferDescriptor =
                new CommandBufferDescriptor();
            commandBuffer =
                wgpu.CommandEncoderFinish(
                    encoder,
                    &commandBufferDescriptor);
            if (commandBuffer == null)
            {
                throw new InvalidOperationException(
                    "Failed to finish the GPU texture-clear command buffer.");
            }

            context.Submit(
                1,
                &commandBuffer);
        }
        finally
        {
            if (pass != null)
            {
                wgpu.RenderPassEncoderRelease(pass);
            }
            if (commandBuffer != null)
            {
                wgpu.CommandBufferRelease(
                    commandBuffer);
            }
            if (encoder != null)
            {
                wgpu.CommandEncoderRelease(encoder);
            }
        }
    }
}
