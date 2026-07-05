using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Text;

public struct GlyphInfo
{
    public uint X;
    public uint Y;
    public uint Width;
    public uint Height;
    public float BearX;
    public float BearY;
    public float Advance;
    
    // UV coordinates inside the atlas texture
    public Vector2 TexCoordMin;
    public Vector2 TexCoordMax;
}

public unsafe class GlyphAtlas : IDisposable
{
    private readonly WgpuContext _context;
    private readonly GpuTexture _atlasTexture;
    private readonly uint _atlasSize;

    // Packing state (Shelf Packer)
    private uint _currentX = 2;
    private uint _currentY = 2;
    private uint _currentRowHeight = 0;

    private readonly Dictionary<(TtfFont font, ushort glyphIndex, float size, byte subpixelX), GlyphInfo> _glyphs = new();
    private readonly Dictionary<TtfFont, (GpuBuffer RecordsBuffer, GpuBuffer SegmentsBuffer)> _fontGpuData = new();
    
    private readonly RenderPipelineCache _pipelineCache;
    private readonly ComputePipeline* _computePipeline;

    private CommandEncoder* _batchEncoder;
    private readonly List<GpuBuffer> _batchBuffers = new();
    private readonly List<nint> _batchBindGroups = new();

    private readonly GpuBuffer _uniformRingBuffer;
    private uint _ringOffset;

    public void BeginBatch()
    {
        if (_isDisposed) return;
        if (_batchEncoder != null) return;
        
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Batch Encoder") };
        _batchEncoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);
        
        _ringOffset = 0; // Reset offset on each batch pass
    }

    public void EndBatch()
    {
        if (_isDisposed) return;
        if (_batchEncoder == null) return;

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Batch Command Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(_batchEncoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(_batchEncoder);
        _batchEncoder = null;

        int batchBufferCount = _batchBuffers.Count;
        for (int bufferIndex = 0; bufferIndex < batchBufferCount; bufferIndex++)
        {
            var buffer = _batchBuffers[bufferIndex];
            buffer.Dispose();
        }
        _batchBuffers.Clear();

        int batchBindGroupCount = _batchBindGroups.Count;
        for (int bindGroupIndex = 0; bindGroupIndex < batchBindGroupCount; bindGroupIndex++)
        {
            var bg = _batchBindGroups[bindGroupIndex];
            _context.Wgpu.BindGroupRelease((BindGroup*)bg);
        }
        _batchBindGroups.Clear();
    }
    
    private bool _isDisposed;

    public GpuTexture AtlasTexture => _atlasTexture;

    public bool IsAlmostFull => (_currentY + _currentRowHeight) > (_atlasSize * 0.85f);

    public void Clear()
    {
        if (_isDisposed) return;
        
        ProGpuTextDiagnostics.WriteLine("[GlyphAtlas] Proactive Clear: Resetting packer and clearing cache.");
        _glyphs.Clear();
        _currentX = 2;
        _currentY = 2;
        _currentRowHeight = 0;

        byte[] clearData = new byte[_atlasSize * _atlasSize * 4];
        _atlasTexture.WritePixels(new ReadOnlySpan<byte>(clearData));
    }

    public GlyphAtlas(WgpuContext context, uint atlasSize = 2048)
    {
        _context = context;
        _atlasSize = atlasSize;
        
        // Use Rgba8Unorm for dynamic alpha mapping (highly memory efficient and WebGPU Storage standard-compliant)
        // With TextureUsage.StorageBinding to allow Compute Shader writing directly to it
        _atlasTexture = new GpuTexture(
            _context, 
            _atlasSize, 
            _atlasSize, 
            TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.StorageBinding, 
            "Dynamic Glyph Atlas"
        );

        // Fill with zero initially to clear the atlas texture
        byte[] clearData = new byte[_atlasSize * _atlasSize * 4];
        _atlasTexture.WritePixels(new ReadOnlySpan<byte>(clearData));

        // Compile and create the compute pipeline
        _pipelineCache = new RenderPipelineCache(_context);
        var shaderModule = _pipelineCache.GetOrCreateShader("GlyphRasterizer", Shaders.GlyphRasterizerShader, "GlyphRasterizerShader");
        _computePipeline = _pipelineCache.GetOrCreateComputePipeline("GlyphRasterizer", shaderModule, "cs_main");

        // Allocate a 256KB uniform ring buffer once at startup to eliminate CPU-to-GPU memory allocation overhead
        _uniformRingBuffer = new GpuBuffer(_context, 256 * 1024, BufferUsage.Uniform | BufferUsage.CopyDst, "Glyph Atlas Uniform Ring Buffer");
        _ringOffset = 0;
    }

    private static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;

    public GlyphInfo GetOrCreateGlyph(TtfFont font, uint codePoint, float size, byte subpixelX = 0)
    {
        ushort glyphIdx = font.GetGlyphIndex(codePoint);
        if (IsWhitespaceCodePoint(codePoint))
        {
            return CreateEmptyGlyphInfo(font, glyphIdx, size);
        }

        return GetOrCreateGlyphByIndex(font, glyphIdx, size, subpixelX);
    }

    public GlyphInfo GetOrCreateGlyphByIndex(TtfFont font, ushort glyphIdx, float size, byte subpixelX = 0)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GlyphAtlas));
        
        var key = (font, glyphIdx, size, subpixelX);
        if (!_glyphs.TryGetValue(key, out var info))
        {
            // If it is a dynamic color emoji inside the font, we don't need to rasterize it into the monochrome atlas!
            // Instead, we just provide proper layout bounds, and the compositor will render it using color vector paths.
            if (font.HasColorLayers(glyphIdx))
            {
                info = new GlyphInfo
                {
                    X = 0,
                    Y = 0,
                    Width = (uint)size,
                    Height = (uint)size,
                    BearX = 0,
                    BearY = -size * 0.8f, // align nicely with font baseline
                    Advance = size,
                    TexCoordMin = Vector2.Zero,
                    TexCoordMax = Vector2.Zero
                };
            }
            else
            {
                var outline = font.GetGlyphOutline(glyphIdx);

                // Handle empty glyphs such as font-owned space outlines.
                if (outline == null)
                {
                    info = CreateEmptyGlyphInfo(font, glyphIdx, size);
                }
                else
                {
                    // Compute bounding box from raw segments and scale it
                    float scale = size / font.UnitsPerEm;
                    float minX = float.MaxValue, maxX = float.MinValue;
                    float minY = float.MaxValue, maxY = float.MinValue;

                    void ProcessPt(Vector2 pt)
                    {
                        float sx = pt.X * scale;
                        float sy = -pt.Y * scale;
                        minX = Math.Min(minX, sx);
                        maxX = Math.Max(maxX, sx);
                        minY = Math.Min(minY, sy);
                        maxY = Math.Max(maxY, sy);
                    }

                    bool hasPoints = false;
                    var outlineFigures = outline.Figures;
                    for (int figureIndex = 0; figureIndex < outlineFigures.Count; figureIndex++)
                    {
                        var figure = outlineFigures[figureIndex];
                        ProcessPt(figure.StartPoint);
                        hasPoints = true;
                        var figureSegments = figure.Segments;
                        for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
                        {
                            var segment = figureSegments[segmentIndex];
                            if (segment is LineSegment line)
                            {
                                ProcessPt(line.Point);
                            }
                            else if (segment is QuadraticBezierSegment quad)
                            {
                                ProcessPt(quad.ControlPoint);
                                ProcessPt(quad.Point);
                            }
                        }
                    }

                    if (!hasPoints)
                    {
                        float advance = font.GetAdvanceWidth(glyphIdx, size);
                        info = new GlyphInfo
                        {
                            X = 0, Y = 0, Width = 0, Height = 0,
                            BearX = 0, BearY = 0, Advance = advance,
                            TexCoordMin = Vector2.Zero, TexCoordMax = Vector2.Zero
                        };
                    }
                    else
                    {
                        // Add padding/margin of 4px on all sides of the glyph bounding box for perfect AA
                        int padding = 4;
                        int xStart = (int)Math.Floor(minX) - padding;
                        int xEnd = (int)Math.Ceiling(maxX) + padding;
                        int yStart = (int)Math.Floor(minY) - padding;
                        int yEnd = (int)Math.Ceiling(maxY) + padding;

                        int width = xEnd - xStart;
                        int height = yEnd - yStart;

                        if (width <= 0 || height <= 0)
                        {
                            float advance = font.GetAdvanceWidth(glyphIdx, size);
                            info = new GlyphInfo
                            {
                                X = 0, Y = 0, Width = 0, Height = 0,
                                BearX = 0, BearY = 0, Advance = advance,
                                TexCoordMin = Vector2.Zero, TexCoordMax = Vector2.Zero
                            };
                        }
                        else
                        {
                            // Shelf Packing placement
                            uint gW = (uint)width;
                            uint gH = (uint)height;

                            if (_currentX + gW + 2 > _atlasSize)
                            {
                                // Row is full, wrap to next shelf row
                                _currentX = 2;
                                _currentY += _currentRowHeight + 2;
                                _currentRowHeight = 0;
                            }

                            if (_currentY + gH + 2 > _atlasSize)
                            {
                                // Atlas is entirely out of space, reset packer
                                ProGpuTextDiagnostics.WriteLine("[GlyphAtlas] Warning: Texture Atlas is full! Clearing cache.");
                                _glyphs.Clear();
                                _currentX = 2;
                                _currentY = 2;
                                _currentRowHeight = 0;

                                byte[] clearData = new byte[_atlasSize * _atlasSize * 4];
                                _atlasTexture.WritePixels(new ReadOnlySpan<byte>(clearData));
                            }

                            uint posX = _currentX;
                            uint posY = _currentY;

                            // Advance packer
                            _currentX += gW + 2;
                            _currentRowHeight = Math.Max(_currentRowHeight, gH);

                            // Upload pre-compiled font segments and records once per font loading
                            if (!_fontGpuData.TryGetValue(font, out var gpuData))
                            {
                                var (records, segments) = font.CompileGpuOutlineData();

                                var recordsBuffer = new GpuBuffer(
                                    _context,
                                    (uint)(records.Length * Marshal.SizeOf<GpuGlyphRecord>()),
                                    BufferUsage.Storage | BufferUsage.CopyDst,
                                    $"Glyph Records Buffer"
                                );
                                recordsBuffer.Write(new ReadOnlySpan<GpuGlyphRecord>(records));

                                // Allocate at least 1 segment to prevent 0-sized buffers which crash WebGPU
                                uint segmentsSize = (uint)Math.Max(32, segments.Length * Marshal.SizeOf<GpuSegment>());
                                var segmentsBuffer = new GpuBuffer(
                                    _context,
                                    segmentsSize,
                                    BufferUsage.Storage | BufferUsage.CopyDst,
                                    $"Glyph Segments Buffer"
                                );
                                if (segments.Length > 0)
                                {
                                    segmentsBuffer.Write(new ReadOnlySpan<GpuSegment>(segments));
                                }

                                gpuData = (recordsBuffer, segmentsBuffer);
                                _fontGpuData[font] = gpuData;
                            }

                            // Write uniforms for the glyph
                             var uniforms = new GlyphUniforms
                             {
                                 XStart = xStart,
                                 YStart = yStart,
                                 Scale = scale,
                                 GlyphIndex = glyphIdx,
                                 AtlasX = posX,
                                 AtlasY = posY,
                                 Width = gW,
                                 Height = gH,
                                 SubpixelX = subpixelX * 0.25f
                             };

                            var bindGroupLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_computePipeline, 0);
                            uint alignedSize = (uint)((Marshal.SizeOf<GlyphUniforms>() + 255) & ~255);

                            if (_batchEncoder != null)
                            {
                                // Ring buffer slice allocation
                                if (_ringOffset + alignedSize > _uniformRingBuffer.Size)
                                {
                                    // Reset offset in the highly unlikely event we exceed 256KB inside a single batch
                                    _ringOffset = 0;
                                }

                                _context.Wgpu.QueueWriteBuffer(_context.Queue, _uniformRingBuffer.BufferPtr, _ringOffset, &uniforms, (uint)Marshal.SizeOf<GlyphUniforms>());

                                var entries = stackalloc BindGroupEntry[4];
                                entries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformRingBuffer.BufferPtr, Offset = _ringOffset, Size = (uint)Marshal.SizeOf<GlyphUniforms>() };
                                entries[1] = new BindGroupEntry { Binding = 1, Buffer = gpuData.RecordsBuffer.BufferPtr, Offset = 0, Size = gpuData.RecordsBuffer.Size };
                                entries[2] = new BindGroupEntry { Binding = 2, Buffer = gpuData.SegmentsBuffer.BufferPtr, Offset = 0, Size = gpuData.SegmentsBuffer.Size };
                                entries[3] = new BindGroupEntry { Binding = 3, TextureView = _atlasTexture.ViewPtr };

                                var bgDesc = new BindGroupDescriptor
                                {
                                    Layout = bindGroupLayout,
                                    EntryCount = 4,
                                    Entries = entries
                                };
                                var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);

                                // Batch path: Record compute pass to batch encoder, defer resource cleanup to EndBatch
                                var passDesc = new ComputePassDescriptor();
                                var pass = _context.Wgpu.CommandEncoderBeginComputePass(_batchEncoder, &passDesc);

                                _context.Wgpu.ComputePassEncoderSetPipeline(pass, _computePipeline);
                                _context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

                                uint workgroupsX = DivRoundUp(gW, 16);
                                uint workgroupsY = DivRoundUp(gH, 16);
                                _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, workgroupsX, workgroupsY, 1);

                                _context.Wgpu.ComputePassEncoderEnd(pass);
                                _context.Wgpu.ComputePassEncoderRelease(pass);

                                _batchBindGroups.Add((nint)bg);
                                _context.Wgpu.BindGroupLayoutRelease(bindGroupLayout);

                                _ringOffset += alignedSize;
                            }
                            else
                            {
                                // Immediate path: Allocate a temporary GPU buffer, write, and submit instantly
                                var uniformsBuffer = new GpuBuffer(
                                    _context,
                                    (uint)Marshal.SizeOf<GlyphUniforms>(),
                                    BufferUsage.Uniform | BufferUsage.CopyDst,
                                    "Glyph Uniforms"
                                );
                                uniformsBuffer.WriteSingle(uniforms);

                                var entries = stackalloc BindGroupEntry[4];
                                entries[0] = new BindGroupEntry { Binding = 0, Buffer = uniformsBuffer.BufferPtr, Offset = 0, Size = uniformsBuffer.Size };
                                entries[1] = new BindGroupEntry { Binding = 1, Buffer = gpuData.RecordsBuffer.BufferPtr, Offset = 0, Size = gpuData.RecordsBuffer.Size };
                                entries[2] = new BindGroupEntry { Binding = 2, Buffer = gpuData.SegmentsBuffer.BufferPtr, Offset = 0, Size = gpuData.SegmentsBuffer.Size };
                                entries[3] = new BindGroupEntry { Binding = 3, TextureView = _atlasTexture.ViewPtr };

                                var bgDesc = new BindGroupDescriptor
                                {
                                    Layout = bindGroupLayout,
                                    EntryCount = 4,
                                    Entries = entries
                                };
                                var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);

                                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Encoder") };
                                var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
                                SilkMarshal.Free((nint)encoderDesc.Label);

                                var passDesc = new ComputePassDescriptor();
                                var pass = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);

                                _context.Wgpu.ComputePassEncoderSetPipeline(pass, _computePipeline);
                                _context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

                                uint workgroupsX = DivRoundUp(gW, 16);
                                uint workgroupsY = DivRoundUp(gH, 16);
                                _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, workgroupsX, workgroupsY, 1);

                                _context.Wgpu.ComputePassEncoderEnd(pass);
                                _context.Wgpu.ComputePassEncoderRelease(pass);

                                // Submit to queue
                                var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Command Buffer") };
                                var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
                                SilkMarshal.Free((nint)cmdDesc.Label);

                                _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

                                // Clean up temporary resources
                                _context.Wgpu.CommandBufferRelease(cmdBuffer);
                                _context.Wgpu.CommandEncoderRelease(encoder);
                                _context.Wgpu.BindGroupRelease(bg);
                                _context.Wgpu.BindGroupLayoutRelease(bindGroupLayout);
                                uniformsBuffer.Dispose();
                            }

                            // Compute UV coordinates
                            float texelSize = 1.0f / _atlasSize;
                            var uvMin = new Vector2(posX * texelSize, posY * texelSize);
                            var uvMax = new Vector2((posX + gW) * texelSize, (posY + gH) * texelSize);
                            float advance = font.GetAdvanceWidth(glyphIdx, size);

                            info = new GlyphInfo
                            {
                                X = posX,
                                Y = posY,
                                Width = gW,
                                Height = gH,
                                BearX = xStart,
                                BearY = yStart,
                                Advance = advance,
                                TexCoordMin = uvMin,
                                TexCoordMax = uvMax
                            };
                        }
                    }
                }
            }
            _glyphs[key] = info;
        }

        return info;
    }

    private static bool IsWhitespaceCodePoint(uint codePoint)
    {
        return codePoint is ' ' or '\t' or '\n' or '\r';
    }

    private static GlyphInfo CreateEmptyGlyphInfo(TtfFont font, ushort glyphIdx, float size)
    {
        float advance = font.GetAdvanceWidth(glyphIdx, size);
        return new GlyphInfo
        {
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            BearX = 0,
            BearY = 0,
            Advance = advance,
            TexCoordMin = Vector2.Zero,
            TexCoordMax = Vector2.Zero
        };
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _uniformRingBuffer.Dispose();

        var fontGpuDataEnumerator = _fontGpuData.Values.GetEnumerator();
        while (fontGpuDataEnumerator.MoveNext())
        {
            var data = fontGpuDataEnumerator.Current;
            data.RecordsBuffer.Dispose();
            data.SegmentsBuffer.Dispose();
        }
        _fontGpuData.Clear();

        _pipelineCache.Dispose();
        _atlasTexture.Dispose();
        _glyphs.Clear();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
