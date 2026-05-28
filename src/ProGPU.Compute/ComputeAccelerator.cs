using System;
using System.Collections.Generic;
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
    private ComputePipeline* _liquidGlassPipeline;

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

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct LiquidGlassParams
    {
        [FieldOffset(0)] public Vector4 GlassColor;
        [FieldOffset(16)] public Vector4 FluidColor;
        [FieldOffset(32)] public float Progress;
        [FieldOffset(36)] public float Time;
        [FieldOffset(40)] public float Refraction;
        [FieldOffset(44)] public float Shininess;
        [FieldOffset(48)] public float Width;
        [FieldOffset(52)] public float Height;
        [FieldOffset(56)] private float _pad0;
        [FieldOffset(60)] private float _pad1;

        public LiquidGlassParams(Vector4 glassColor, Vector4 fluidColor, float progress, float time, float refraction, float shininess, float width, float height)
        {
            GlassColor = glassColor;
            FluidColor = fluidColor;
            Progress = progress;
            Time = time;
            Refraction = refraction;
            Shininess = shininess;
            Width = width;
            Height = height;
            _pad0 = 0f;
            _pad1 = 0f;
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

        var shLiquidGlass = _cache.GetOrCreateShader("LiquidGlass", ComputeShaders.LiquidGlass, "LiquidGlassShader");
        _liquidGlassPipeline = _cache.GetOrCreateComputePipeline("LiquidGlass", shLiquidGlass);
    }

    private void RunBlurPass(CommandEncoder* encoder, ComputePipeline* pipeline, BindGroupLayout* layout, GpuTexture input, GpuTexture output, uint width, uint height, List<nint> bindGroupsToRelease)
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
        bindGroupsToRelease.Add((nint)bg);

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

        uint width = source.Width;
        uint height = source.Height;

        // Ensure temp and destination are resized to match source
        temp.Resize(width, height);
        destination.Resize(width, height);

        // Clamp iterations based on blur radius to yield high quality result
        int iterations = Math.Clamp((int)Math.Round(radius / 2.5f), 1, 8);

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Blur Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var blurHLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_blurHorizPipeline, 0);
        var blurVLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_blurVertPipeline, 0);

        var bindGroupsToRelease = new List<nint>();

        for (int i = 0; i < iterations; i++)
        {
            var hInput = (i == 0) ? source : destination;
            RunBlurPass(encoder, _blurHorizPipeline, blurHLayout, hInput, temp, width, height, bindGroupsToRelease);
            RunBlurPass(encoder, _blurVertPipeline, blurVLayout, temp, destination, width, height, bindGroupsToRelease);
        }

        // Submit commands to queue
        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Blur Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        // Release resources
        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        foreach (var bgPtr in bindGroupsToRelease)
        {
            _context.Wgpu.BindGroupRelease((BindGroup*)bgPtr);
        }

        _context.Wgpu.BindGroupLayoutRelease(blurHLayout);
        _context.Wgpu.BindGroupLayoutRelease(blurVLayout);
    }

    private void RunShadowHPass(CommandEncoder* encoder, ComputePipeline* pipeline, BindGroupLayout* layout, GpuTexture input, GpuTexture output, GpuBuffer paramsBuffer, uint width, uint height, List<nint> bindGroupsToRelease)
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
        bindGroupsToRelease.Add((nint)bg);

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

        uint width = source.Width;
        uint height = source.Height;

        temp.Resize(width, height);
        destination.Resize(width, height);

        if (blurRadius <= 0.01f)
        {
            RunSharpDropShadow(source, destination, offset, shadowColor, blurRadius);
            return;
        }

        int iterations = Math.Clamp((int)Math.Round(blurRadius / 2.5f), 1, 8);

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ShadowParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Shadow Params Buffer"
        );
        paramsBuffer.WriteSingle(new ShadowParams(offset, shadowColor, blurRadius));

        var shadowHLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_shadowBlurHorizPipeline, 0);
        var blurHLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_blurHorizPipeline, 0);
        var blurVLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_blurVertPipeline, 0);

        var bindGroupsToRelease = new List<nint>();

        for (int i = 0; i < iterations; i++)
        {
            if (i == 0)
            {
                RunShadowHPass(encoder, _shadowBlurHorizPipeline, shadowHLayout, source, temp, paramsBuffer, width, height, bindGroupsToRelease);
            }
            else
            {
                RunBlurPass(encoder, _blurHorizPipeline, blurHLayout, destination, temp, width, height, bindGroupsToRelease);
            }

            RunBlurPass(encoder, _blurVertPipeline, blurVLayout, temp, destination, width, height, bindGroupsToRelease);
        }

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        foreach (var bgPtr in bindGroupsToRelease)
        {
            _context.Wgpu.BindGroupRelease((BindGroup*)bgPtr);
        }

        _context.Wgpu.BindGroupLayoutRelease(shadowHLayout);
        _context.Wgpu.BindGroupLayoutRelease(blurHLayout);
        _context.Wgpu.BindGroupLayoutRelease(blurVLayout);
        paramsBuffer.Dispose();
    }

    public void ApplyLiquidGlass(GpuTexture source, GpuTexture temp, GpuTexture destination, Vector4 glassColor, Vector4 fluidColor, float progress, float time, float refraction, float shininess)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        uint width = source.Width;
        uint height = source.Height;

        temp.Resize(width, height);
        destination.Resize(width, height);

        var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<LiquidGlassParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Liquid Glass Params Buffer"
        );
        paramsBuffer.WriteSingle(new LiquidGlassParams(glassColor, fluidColor, progress, time, refraction, shininess, (float)width, (float)height));

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Liquid Glass Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var layout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_liquidGlassPipeline, 0);

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = destination.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = paramsBuffer.BufferPtr, Offset = 0, Size = paramsBuffer.Size };

        var bgDesc = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = entries
        };
        var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);

        var passDesc = new ComputePassDescriptor();
        var pass = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);

        _context.Wgpu.ComputePassEncoderSetPipeline(pass, _liquidGlassPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

        uint workgroupX = (width + 15) / 16;
        uint workgroupY = (height + 15) / 16;
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, workgroupX, workgroupY, 1);

        _context.Wgpu.ComputePassEncoderEnd(pass);
        _context.Wgpu.ComputePassEncoderRelease(pass);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Liquid Glass Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);
        _context.Wgpu.BindGroupRelease(bg);
        _context.Wgpu.BindGroupLayoutRelease(layout);
        paramsBuffer.Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _cache.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~ComputeAccelerator()
    {
        Dispose();
    }
}
