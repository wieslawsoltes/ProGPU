using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Text;

public unsafe partial class GlyphAtlas
{
    // Original ProGPU GlyphAtlas GPU coverage dispatch, shared unchanged by
    // design-font and retained physical-geometry callers. No font decoding,
    // phase selection or execution-policy substitution occurs in this helper.
    private void RasterizeGpuGlyph(GlyphUniforms uniforms,
        uint posX, uint posY, uint gW, uint gH)
    {
        uint coverageBytesPerRow = GpuCoverageUpload.GetBytesPerRow(gW);
        uint coverageBytes = checked(coverageBytesPerRow * gH);

        uint alignedSize = (uint)((Marshal.SizeOf<GlyphUniforms>() + 255) & ~255);

        if (_batchEncoder != null)
        {
            // Ring buffer slice allocation
            if (_rasterizationPath ==
                    GpuComputeExecutionPath.NativeCompute &&
                coverageBytes > ComputeCoverageRingBuffer.Size)
            {
                FlushBatchEncoder();
                RasterizeOversizedGlyph(
                    uniforms,
                    coverageBytes,
                    coverageBytesPerRow,
                    posX,
                    posY,
                    gW,
                    gH);
                CreateBatchEncoder();
                return;
            }
            if (_ringOffset + alignedSize >
                    _uniformRingBuffer.Size ||
                (_rasterizationPath ==
                    GpuComputeExecutionPath.NativeCompute &&
                 (ulong)GpuCoverageUpload.AlignCopyOffset(_coverageRingOffset) + coverageBytes >
                    ComputeCoverageRingBuffer.Size))
            {
                FlushBatchEncoder();
                CreateBatchEncoder();
            }

            if (_rasterizationPath ==
                GpuComputeExecutionPath.NativeCompute)
            {
                // Padding is between slices, not in their row pitch or glyph bounds.
                // Account for it above before deciding whether the ring must flush.
                _coverageRingOffset = GpuCoverageUpload.AlignCopyOffset(_coverageRingOffset);
                uniforms.OutputOffsetWords =
                    _coverageRingOffset / 4;
                uniforms.OutputRowWords =
                    coverageBytesPerRow / 4;
            }
            MemoryMarshal.Write(
                _uniformRingUpload.AsSpan(
                    checked((int)_ringOffset),
                    Marshal.SizeOf<GlyphUniforms>()),
                in uniforms);

            uint dynamicUniformOffset = _ringOffset;
            if (_rasterizationPath ==
                GpuComputeExecutionPath.RasterShader)
            {
                var pass = GetOrCreateBatchRasterPass();
                var bg = GetOrCreateRasterRingBindGroup();
                _context.Api.RenderPassEncoderSetBindGroup(
                    pass,
                    0,
                    bg,
                    1,
                    &dynamicUniformOffset);
                _context.Api.RenderPassEncoderSetViewport(
                    pass, posX, posY, gW, gH, 0f, 1f);
                _context.Api.RenderPassEncoderSetScissorRect(
                    pass, posX, posY, gW, gH);
                _context.Api.RenderPassEncoderDraw(
                    pass, 3, 1, 0, 0);
            }
            else
            {
                var pass = GetOrCreateBatchComputePass();
                var bg = GetOrCreateRingBindGroup();
                _context.Api.ComputePassEncoderSetBindGroup(
                    pass,
                    0,
                    bg,
                    1,
                    &dynamicUniformOffset);
                uint workgroupsX = DivRoundUp(
                    DivRoundUp(gW, 4), 16);
                uint workgroupsY = DivRoundUp(gH, 16);
                _context.Api.ComputePassEncoderDispatchWorkgroups(
                    pass, workgroupsX, workgroupsY, 1);
                _batchCoverageCopies.Add(
                    new PendingCoverageCopy(
                        _coverageRingOffset,
                        coverageBytesPerRow,
                        posX,
                        posY,
                        gW,
                        gH));
                _coverageRingOffset += coverageBytes;
            }

            _ringOffset += alignedSize;
        }
        else
        {
            if (_rasterizationPath ==
                GpuComputeExecutionPath.RasterShader)
            {
                RasterizeGlyphWithRasterShader(
                    uniforms, posX, posY, gW, gH);
                return;
            }
            RasterizeImmediateComputeGlyph(uniforms, coverageBytes, coverageBytesPerRow,
                posX, posY, gW, gH, oversized: false);
        }
    }

    private void RasterizeImmediateComputeGlyph(GlyphUniforms uniforms,
        uint coverageBytes, uint coverageBytesPerRow, uint atlasX, uint atlasY,
        uint width, uint height, bool oversized)
    {
        EnsureComputePipeline();
        uniforms.OutputOffsetWords = 0;
        uniforms.OutputRowWords = coverageBytesPerRow / 4;
        using var uniformsBuffer = new GpuBuffer(_context,
            (uint)Marshal.SizeOf<GlyphUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst,
            oversized ? "Oversized Glyph Uniforms" : "Glyph Uniforms");
        using var coverageBuffer = new GpuBuffer(_context, coverageBytes,
            BufferUsage.Storage | BufferUsage.CopySrc,
            oversized ? "Oversized Glyph Coverage Staging Buffer" : "Glyph Coverage Staging Buffer");
        uniformsBuffer.WriteSingle(uniforms);
        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = uniformsBuffer.BufferPtr, Offset = 0, Size = uniformsBuffer.Size };
        entries[1] = new BindGroupEntry { Binding = 1, Buffer = _recordsBuffer.BufferPtr, Offset = 0, Size = _recordsBuffer.Size };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = _segmentsBuffer.BufferPtr, Offset = 0, Size = _segmentsBuffer.Size };
        entries[3] = new BindGroupEntry { Binding = 3, Buffer = coverageBuffer.BufferPtr, Offset = 0, Size = coverageBuffer.Size };
        BindGroup* bindGroup = null;
        CommandEncoder* encoder = null;
        ComputePassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var descriptor = new BindGroupDescriptor
            {
                Layout = _computeBindGroupLayout, EntryCount = 4, Entries = entries
            };
            bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
            if (bindGroup == null) throw new InvalidOperationException("Failed to create the immediate glyph compute bind group.");
            RasterBindGroupCreationCount++;
            encoder = CreateCommandEncoder("Glyph Rasterizer Encoder\0"u8);
            if (encoder == null) throw new InvalidOperationException("Failed to create the immediate glyph compute encoder.");
            var passDescriptor = new ComputePassDescriptor();
            pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
            if (pass == null) throw new InvalidOperationException("Failed to begin the immediate glyph compute pass.");
            _context.Api.ComputePassEncoderSetPipeline(pass, _computePipeline);
            uint dynamicOffset = 0;
            _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 1, &dynamicOffset);
            _context.Api.ComputePassEncoderDispatchWorkgroups(pass,
                DivRoundUp(DivRoundUp(width, 4), 16), DivRoundUp(height, 16), 1);
            _context.Api.ComputePassEncoderEnd(pass);
            _context.Api.ComputePassEncoderRelease(pass);
            pass = null;
            GpuCoverageUpload.RecordCopy(_context, encoder, coverageBuffer, 0,
                coverageBytesPerRow, _atlasTexture, atlasX, atlasY, width, height);
            commandBuffer = FinishCommandEncoder(encoder, "Glyph Rasterizer Command Buffer\0"u8);
            if (commandBuffer == null) throw new InvalidOperationException("Failed to finish the immediate glyph compute encoder.");
            _context.Submit(1, &commandBuffer);
        }
        finally
        {
            if (pass != null) _context.Api.ComputePassEncoderRelease(pass);
            if (commandBuffer != null) _context.Api.CommandBufferRelease(commandBuffer);
            if (encoder != null) _context.Api.CommandEncoderRelease(encoder);
            if (bindGroup != null) _context.Api.BindGroupRelease(bindGroup);
        }
    }
}
