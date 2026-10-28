using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Text;

public unsafe partial class GlyphAtlas
{
    // BinaryGlyphRasterizer.wgsl: 256 original segments, two roots per segment.
    internal const uint MaximumBinaryGlyphSegments = 256;
    private WgpuBindGroupLayoutLease? _binaryBindGroupLayoutLease;
    private WgpuPipelineLayoutLease? _binaryPipelineLayoutLease;
    private ComputePipeline* _binaryHorizontalPipeline;
    private ComputePipeline* _binaryVerticalPipeline;
    private GpuBuffer? _binaryHorizontalRingBuffer;
    private BindGroup* _binaryRingBindGroup;

    private void ValidateBinaryRasterAdmission(in GpuGlyphRecord record)
    {
        if (record.SegmentCount > MaximumBinaryGlyphSegments)
            throw new NotSupportedException("Binary glyph scan conversion exceeds the bounded crossing inventory.");
        // The ordered shared-memory scan has no equivalent fragment entry
        // point. Never silently substitute the old AA/CPU rasterizer.
        if (_rasterizationPath != GpuComputeExecutionPath.NativeCompute)
            throw new NotSupportedException("Binary glyph scan conversion requires the native-compute execution path.");
    }

    private void EnsureBinaryPipeline()
    {
        if (_binaryHorizontalPipeline != null && _binaryVerticalPipeline != null) return;
        if (_binaryBindGroupLayoutLease is null)
        {
            var entries = stackalloc BindGroupLayoutEntry[5];
            for (uint i = 0; i < 5; i++)
                entries[i] = new BindGroupLayoutEntry
                {
                    Binding = i, Visibility = ShaderStage.Compute,
                    Buffer = new BufferBindingLayout
                    {
                        Type = i == 0 ? BufferBindingType.Uniform :
                            i < 3 ? BufferBindingType.ReadOnlyStorage : BufferBindingType.Storage,
                        HasDynamicOffset = i == 0,
                        MinBindingSize = i == 0 ? (ulong)Marshal.SizeOf<GlyphUniforms>() :
                            i == 1 ? (ulong)Marshal.SizeOf<GpuGlyphRecord>() :
                            i == 2 ? (ulong)Marshal.SizeOf<GpuSegment>() : 4UL
                    }
                };
            var descriptor = new BindGroupLayoutDescriptor { EntryCount = 5, Entries = entries };
            _binaryBindGroupLayoutLease = _context.AcquireSharedBindGroupLayout(
                new WgpuDeviceResourceKey("ProGPU.Text.GlyphAtlas", "BinaryRasterizationBindings"), &descriptor);
        }
        if (_binaryPipelineLayoutLease is null)
        {
            var layout = _binaryBindGroupLayoutLease.Handle;
            var descriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &layout };
            _binaryPipelineLayoutLease = _context.AcquireSharedPipelineLayout(
                new WgpuDeviceResourceKey("ProGPU.Text.GlyphAtlas", "BinaryRasterizationPipeline"), &descriptor);
        }
        var shader = _pipelineCache.GetOrCreateShader("BinaryGlyphRasterizer",
            Shaders.BinaryGlyphRasterizerShader, "BinaryGlyphRasterizerShader");
        _binaryHorizontalPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "BinaryGlyphRasterizer.Horizontal", shader, "cs_binary_horizontal", _binaryPipelineLayoutLease.Handle);
        _binaryVerticalPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "BinaryGlyphRasterizer.Vertical", shader, "cs_binary_vertical", _binaryPipelineLayoutLease.Handle);
    }

    private BindGroup* GetOrCreateBinaryRingBindGroup()
    {
        if (_binaryRingBindGroup != null) return _binaryRingBindGroup;
        EnsureBinaryPipeline();
        _binaryHorizontalRingBuffer ??= new GpuBuffer(_context, ComputeCoverageRingBuffer.Size,
            BufferUsage.Storage, "Binary Glyph Horizontal Coverage Ring Buffer");
        var entries = stackalloc BindGroupEntry[5];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformRingBuffer.BufferPtr,
            Size = (uint)Marshal.SizeOf<GlyphUniforms>() };
        entries[1] = new BindGroupEntry { Binding = 1, Buffer = _recordsBuffer.BufferPtr, Size = _recordsBuffer.Size };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = _segmentsBuffer.BufferPtr, Size = _segmentsBuffer.Size };
        entries[3] = new BindGroupEntry { Binding = 3, Buffer = ComputeCoverageRingBuffer.BufferPtr,
            Size = ComputeCoverageRingBuffer.Size };
        entries[4] = new BindGroupEntry { Binding = 4, Buffer = _binaryHorizontalRingBuffer.BufferPtr,
            Size = _binaryHorizontalRingBuffer.Size };
        var descriptor = new BindGroupDescriptor
        {
            Layout = _binaryBindGroupLayoutLease!.Handle, EntryCount = 5, Entries = entries
        };
        _binaryRingBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
        if (_binaryRingBindGroup == null)
            throw new InvalidOperationException("Failed to create the binary glyph rasterization ring bind group.");
        RasterBindGroupCreationCount++;
        return _binaryRingBindGroup;
    }

    private void DispatchBinaryGlyph(ComputePassEncoder* pass, uint width, uint height)
    {
        // Dispatch order supplies the device-wide memory dependency. The second
        // pass reads only the completed horizontal buffer, never its own output.
        _context.Api.ComputePassEncoderSetPipeline(pass, _binaryHorizontalPipeline);
        _context.Api.ComputePassEncoderDispatchWorkgroups(pass, 1, height, 1);
        _context.Api.ComputePassEncoderSetPipeline(pass, _binaryVerticalPipeline);
        _context.Api.ComputePassEncoderDispatchWorkgroups(pass, 1, width, 1);
    }
}
