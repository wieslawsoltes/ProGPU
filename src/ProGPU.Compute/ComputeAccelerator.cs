using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;

namespace ProGPU.Compute;

public unsafe class ComputeAccelerator : IDisposable
{
    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _cache;

    private ComputePipeline* _blurHorizPipeline;
    private ComputePipeline* _blurVertPipeline;
    private ComputePipeline* _shadowPipeline;
    private ComputePipeline* _shadowBlurHorizPipeline;
    private ComputePipeline* _shadowBlurVertPipeline;


    private bool _isDisposed;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct ShadowParams
    {
        [FieldOffset(0)] public Vector2 Offset;
        [FieldOffset(16)] public Vector4 Color;
        [FieldOffset(32)] public float BlurRadius;
        [FieldOffset(36)] private float _padding;
        [FieldOffset(40)] private float _pad0;
        [FieldOffset(44)] private float _pad1;
        [FieldOffset(48)] private float _pad2;
        [FieldOffset(52)] private float _pad3;
        [FieldOffset(56)] private float _pad4;
        [FieldOffset(60)] private float _pad5;

        public ShadowParams(Vector2 offset, Vector4 color, float blurRadius)
        {
            Offset = offset;
            Color = color;
            BlurRadius = blurRadius;
            _padding = 0f;
            _pad0 = 0f;
            _pad1 = 0f;
            _pad2 = 0f;
            _pad3 = 0f;
            _pad4 = 0f;
            _pad5 = 0f;
        }
    }



    public ComputeAccelerator(WgpuContext context)
    {
        _context = context;
        _cache = new RenderPipelineCache(_context);

        InitializePipelines();
    }

    private void InitializePipelines()
    {
        var shBlurH = _cache.GetOrCreateShader("BlurH", ComputeShaders.GaussianBlurHorizontal, "BlurHShader");
        _blurHorizPipeline = _cache.GetOrCreateComputePipeline("BlurH", shBlurH);

        var shBlurV = _cache.GetOrCreateShader("BlurV", ComputeShaders.GaussianBlurVertical, "BlurVShader");
        _blurVertPipeline = _cache.GetOrCreateComputePipeline("BlurV", shBlurV);

        var shShadow = _cache.GetOrCreateShader("Shadow", ComputeShaders.DropShadow, "ShadowShader");
        _shadowPipeline = _cache.GetOrCreateComputePipeline("Shadow", shShadow);

        var shShadowBlurH = _cache.GetOrCreateShader("ShadowBlurH", ComputeShaders.ShadowBlurHorizontal, "ShadowBlurHShader");
        _shadowBlurHorizPipeline = _cache.GetOrCreateComputePipeline("ShadowBlurH", shShadowBlurH);

        var shShadowBlurV = _cache.GetOrCreateShader("ShadowBlurV", ComputeShaders.ShadowBlurVertical, "ShadowBlurVShader");
        _shadowBlurVertPipeline = _cache.GetOrCreateComputePipeline("ShadowBlurV", shShadowBlurV);
    }

    private static void TrackBindGroupForRelease(Span<nint> bindGroupsToRelease, ref int count, BindGroup* bindGroup)
    {
        bindGroupsToRelease[count++] = (nint)bindGroup;
    }

    private void ReleaseBindGroups(ReadOnlySpan<nint> bindGroupsToRelease)
    {
        for (int i = 0; i < bindGroupsToRelease.Length; i++)
        {
            _context.Wgpu.BindGroupRelease((BindGroup*)bindGroupsToRelease[i]);
        }
    }

    private void RunBlurPass(
        CommandEncoder* encoder,
        ComputePipeline* pipeline,
        BindGroupLayout* layout,
        GpuTexture input,
        GpuTexture output,
        uint width,
        uint height,
        Span<nint> bindGroupsToRelease,
        ref int bindGroupToReleaseCount)
    {
        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = input.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = output.ViewPtr };

        var bgDesc = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 2,
            Entries = entries
        };
        var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
        TrackBindGroupForRelease(bindGroupsToRelease, ref bindGroupToReleaseCount, bg);

        var passDesc = new ComputePassDescriptor();
        var pass = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(pass, pipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

        uint workgroupX = (width + 15) / 16;
        uint workgroupY = (height + 15) / 16;
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, workgroupX, workgroupY, 1);

        _context.Wgpu.ComputePassEncoderEnd(pass);
        _context.Wgpu.ComputePassEncoderRelease(pass);
    }

    public void ApplyGaussianBlur(GpuTexture source, GpuTexture temp, GpuTexture destination, float radius)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        float snappedRadius = MathF.Round(radius * 2f) / 2f;

        uint width = source.Width;
        uint height = source.Height;

        // Ensure temp and destination are resized to match source
        temp.Resize(width, height);
        destination.Resize(width, height);

        // Clamp iterations based on blur radius to yield high quality result
        int iterations = Math.Clamp((int)Math.Round(snappedRadius / 2.5f), 1, 8);

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Blur Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var blurHLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_blurHorizPipeline, 0);
        var blurVLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_blurVertPipeline, 0);

        Span<nint> bindGroupsToRelease = stackalloc nint[iterations * 2];
        var bindGroupToReleaseCount = 0;

        for (int i = 0; i < iterations; i++)
        {
            var hInput = (i == 0) ? source : destination;
            RunBlurPass(encoder, _blurHorizPipeline, blurHLayout, hInput, temp, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);
            RunBlurPass(encoder, _blurVertPipeline, blurVLayout, temp, destination, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);
        }

        // Submit commands to queue
        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Blur Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        // Release resources
        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        ReleaseBindGroups(bindGroupsToRelease[..bindGroupToReleaseCount]);

        _context.Wgpu.BindGroupLayoutRelease(blurHLayout);
        _context.Wgpu.BindGroupLayoutRelease(blurVLayout);
    }

    private void RunShadowPass(
        CommandEncoder* encoder,
        ComputePipeline* pipeline,
        BindGroupLayout* layout,
        GpuTexture input,
        GpuTexture output,
        GpuBuffer paramsBuffer,
        uint width,
        uint height,
        Span<nint> bindGroupsToRelease,
        ref int bindGroupToReleaseCount)
    {
        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = input.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = output.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = paramsBuffer.BufferPtr, Offset = 0, Size = paramsBuffer.Size };

        var bgDesc = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = entries
        };
        var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
        TrackBindGroupForRelease(bindGroupsToRelease, ref bindGroupToReleaseCount, bg);

        var passDesc = new ComputePassDescriptor();
        var pass = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(pass, pipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

        uint workgroupX = (width + 15) / 16;
        uint workgroupY = (height + 15) / 16;
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, workgroupX, workgroupY, 1);

        _context.Wgpu.ComputePassEncoderEnd(pass);
        _context.Wgpu.ComputePassEncoderRelease(pass);
    }

    private void RunSharpDropShadow(GpuTexture source, GpuTexture destination, Vector2 offset, Vector4 shadowColor, float blurRadius)
    {
        var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ShadowParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Shadow Params Buffer"
        );
        paramsBuffer.WriteSingle(new ShadowParams(offset, shadowColor, blurRadius));

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var shadowLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_shadowPipeline, 0);

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = destination.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = paramsBuffer.BufferPtr, Offset = 0, Size = paramsBuffer.Size };

        var bgDesc = new BindGroupDescriptor
        {
            Layout = shadowLayout,
            EntryCount = 3,
            Entries = entries
        };
        var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);

        var passDesc = new ComputePassDescriptor();
        var pass = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);

        _context.Wgpu.ComputePassEncoderSetPipeline(pass, _shadowPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

        uint workgroupX = (source.Width + 15) / 16;
        uint workgroupY = (source.Height + 15) / 16;
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, workgroupX, workgroupY, 1);

        _context.Wgpu.ComputePassEncoderEnd(pass);
        _context.Wgpu.ComputePassEncoderRelease(pass);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);
        _context.Wgpu.BindGroupRelease(bg);
        _context.Wgpu.BindGroupLayoutRelease(shadowLayout);
        paramsBuffer.Dispose();
    }

    public void ApplyDropShadow(GpuTexture source, GpuTexture temp, GpuTexture destination, Vector2 offset, Vector4 shadowColor, float blurRadius)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        float snappedBlurRadius = MathF.Round(blurRadius * 2f) / 2f;

        uint width = source.Width;
        uint height = source.Height;

        temp.Resize(width, height);
        destination.Resize(width, height);

        if (snappedBlurRadius <= 0.01f)
        {
            RunSharpDropShadow(source, destination, offset, shadowColor, snappedBlurRadius);
            return;
        }

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ShadowParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Shadow Params Buffer"
        );
        paramsBuffer.WriteSingle(new ShadowParams(offset, shadowColor, snappedBlurRadius));

        var shadowHLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_shadowBlurHorizPipeline, 0);
        var shadowVLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_shadowBlurVertPipeline, 0);

        Span<nint> bindGroupsToRelease = stackalloc nint[2];
        var bindGroupToReleaseCount = 0;

        RunShadowPass(encoder, _shadowBlurHorizPipeline, shadowHLayout, source, temp, paramsBuffer, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);
        RunShadowPass(encoder, _shadowBlurVertPipeline, shadowVLayout, temp, destination, paramsBuffer, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        ReleaseBindGroups(bindGroupsToRelease[..bindGroupToReleaseCount]);

        _context.Wgpu.BindGroupLayoutRelease(shadowHLayout);
        _context.Wgpu.BindGroupLayoutRelease(shadowVLayout);
        paramsBuffer.Dispose();
    }



    public void Dispose()
    {
        if (_isDisposed) return;

        _cache.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
