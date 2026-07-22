using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Vector;
using StbImageSharp;

namespace ProGPU.Text;

public struct GlyphInfo
{
    public uint X;
    public uint Y;
    public uint Width;
    public uint Height;
    public float BearX;
    public float BearY;
    public float RenderWidth;
    public float RenderHeight;
    public float Advance;
    public float RasterScale;
    public bool IsColorBitmap;
    
    // UV coordinates inside the atlas texture
    public Vector2 TexCoordMin;
    public Vector2 TexCoordMax;
    internal uint AtlasRegionWidth;
    internal uint AtlasRegionHeight;
}

public unsafe class GlyphAtlas : IDisposable
{
    private const uint DefaultUniformRingBufferSize = 64 * 1024;
    public const uint DefaultCoverageRingBufferSize = 256 * 1024;
    public const uint DefaultInitialAtlasSize = 512;
    public const uint DefaultInitialColorAtlasSize = 256;
    public const uint DefaultColorAtlasSize = 512;
    private const int InitialRecordCapacity = 64;
    private const int InitialSegmentCapacity = 1024;
    public const ulong DefaultGpuOutlineCacheBudgetBytes = 4UL * 1024UL * 1024UL;

    private readonly WgpuContext _context;
    private GpuTexture _atlasTexture;
    private GpuTexture _colorAtlasTexture;
    private uint _atlasSize;
    private uint _colorAtlasSize;
    private readonly uint _maxAtlasSize;
    private readonly uint _maxColorAtlasSize;

    // Packing state. Shelves are segregated by height so a tall glyph cannot waste
    // the full width of a row containing short punctuation and combining marks.
    private struct AtlasShelf
    {
        public uint Y;
        public uint Height;
        public uint NextX;
    }

    private readonly List<AtlasShelf> _shelves = new();
    private readonly List<AtlasShelf> _colorShelves = new();
    private uint _nextShelfY = 2;
    private uint _nextColorShelfY = 2;

    private readonly record struct GlyphKey(TtfFont Font, ushort GlyphIndex, float Size, byte SubpixelX);

    private struct CachedGlyph
    {
        public GlyphInfo Info;
        public ulong LastUsedFrame;
        public bool IsCapacityFallback;
    }

    private readonly Dictionary<GlyphKey, CachedGlyph> _glyphs = new();
    private sealed class FontGpuData
    {
        public Dictionary<ushort, uint> RecordSlots { get; } = new();
    }

    private readonly Dictionary<TtfFont, FontGpuData> _fontGpuData = new();
    private readonly List<GpuSegment> _glyphSegmentScratch = new(InitialSegmentCapacity);
    private GpuBuffer _recordsBuffer;
    private GpuBuffer _segmentsBuffer;
    private int _recordCount;
    private int _segmentCount;
    private int _recordCapacity = InitialRecordCapacity;
    private int _segmentCapacity = InitialSegmentCapacity;
    
    private readonly RenderPipelineCache _pipelineCache;
    private readonly ComputePipeline* _computePipeline;

    private CommandEncoder* _batchEncoder;
    private int _batchDepth;
    private readonly List<GpuBuffer> _batchBuffers = new();
    private readonly List<nint> _batchBindGroups = new();

    private readonly GpuBuffer _uniformRingBuffer;
    private readonly GpuBuffer _coverageRingBuffer;
    private uint _ringOffset;
    private uint _coverageRingOffset;
    private ulong _frameNumber;

    public void BeginBatch()
    {
        if (_isDisposed) return;
        _batchDepth++;
        if (_batchDepth > 1) return;

        TrimGpuOutlineCache();
        _frameNumber++;

        CreateBatchEncoder();
    }

    private void TrimGpuOutlineCache()
    {
        if (AllocatedGpuOutlineBytes <= DefaultGpuOutlineCacheBudgetBytes)
        {
            return;
        }

        _recordsBuffer.Dispose();
        _segmentsBuffer.Dispose();
        _fontGpuData.Clear();
        _recordCount = 0;
        _segmentCount = 0;
        _recordCapacity = InitialRecordCapacity;
        _segmentCapacity = InitialSegmentCapacity;
        (_recordsBuffer, _segmentsBuffer) = CreateOutlineBuffers(
            _recordCapacity,
            _segmentCapacity);
        GpuOutlineCacheResetCount++;
    }

    private void CreateBatchEncoder()
    {
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Batch Encoder") };
        _batchEncoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        if (_batchEncoder == null)
        {
            throw new InvalidOperationException("Failed to create the glyph rasterizer batch encoder.");
        }

        _ringOffset = 0;
        _coverageRingOffset = 0;
    }

    public void EndBatch()
    {
        if (_isDisposed) return;
        if (_batchDepth == 0) return;
        _batchDepth--;
        if (_batchDepth > 0) return;
        FlushBatchEncoder();
    }

    /// <summary>
    /// Submits glyph rasterization recorded by the active batch before a render pass
    /// that samples the atlas is submitted. The batch remains active so later glyphs
    /// in the same compositor frame continue to use the allocation-free ring path.
    /// </summary>
    public void FlushPendingBatchWork()
    {
        if (_isDisposed || _batchDepth == 0 || _batchEncoder == null || _ringOffset == 0)
        {
            return;
        }

        FlushBatchEncoder();
        CreateBatchEncoder();
    }

    private void FlushBatchEncoder()
    {
        if (_batchEncoder == null) return;

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Batch Command Buffer") };
        var cmdBuffer = _context.Api.CommandEncoderFinish(_batchEncoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Api.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(_batchEncoder);
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
            _context.Api.BindGroupRelease((BindGroup*)bg);
        }
        _batchBindGroups.Clear();
    }
    
    private bool _isDisposed;

    public GpuTexture AtlasTexture => _atlasTexture;

    public GpuTexture ColorAtlasTexture => _colorAtlasTexture;

    public uint AtlasSize => _atlasSize;

    public uint MaxAtlasSize => _maxAtlasSize;

    public uint ColorAtlasSize => _colorAtlasSize;

    public uint MaxColorAtlasSize => _maxColorAtlasSize;

    public ulong TextureRevision { get; private set; }

    public ulong PersistentTextureBytes =>
        (ulong)_atlasSize * _atlasSize +
        (ulong)_colorAtlasSize * _colorAtlasSize * 4UL;

    public ulong CoverageStagingBytes => _coverageRingBuffer.Size;

    public int CachedGlyphCount => _glyphs.Count;

    public int CompiledGpuGlyphCount
    {
        get => _recordCount;
    }

    public int AllocatedGpuGlyphRecordCapacity
    {
        get => _recordCapacity;
    }

    public ulong AllocatedGpuOutlineBytes
    {
        get => _recordsBuffer.Size + _segmentsBuffer.Size;
    }

    public ulong GpuOutlineCacheBudgetBytes => DefaultGpuOutlineCacheBudgetBytes;

    public ulong GpuOutlineCacheResetCount { get; private set; }

    public ulong Generation { get; private set; }

    public bool CapacityExceeded { get; private set; }

    public ulong EvictionCount { get; private set; }

    public ulong ClearCount { get; private set; }

    public bool IsAlmostFull =>
        (_atlasSize >= _maxAtlasSize && _nextShelfY > (_atlasSize * 0.85f)) ||
        (_colorAtlasSize >= _maxColorAtlasSize &&
         _nextColorShelfY > (_colorAtlasSize * 0.85f));

    public void Clear()
    {
        if (_isDisposed) return;
        
        ProGpuTextDiagnostics.WriteLine("[GlyphAtlas] Proactive Clear: Resetting packer and clearing cache.");
        _glyphs.Clear();
        _shelves.Clear();
        _colorShelves.Clear();
        _nextShelfY = 2;
        _nextColorShelfY = 2;

        _atlasTexture.ClearRenderTarget();
        _colorAtlasTexture.ClearRenderTarget();
        CapacityExceeded = false;
        ClearCount++;
        Generation++;
    }

    public GlyphAtlas(WgpuContext context, uint atlasSize = 2048)
        : this(
            context,
            atlasSize,
            Math.Min(atlasSize, DefaultColorAtlasSize),
            DefaultUniformRingBufferSize,
            DefaultCoverageRingBufferSize)
    {
    }

    internal GlyphAtlas(WgpuContext context, uint atlasSize, uint uniformRingBufferSize)
        : this(
            context,
            atlasSize,
            Math.Min(atlasSize, DefaultColorAtlasSize),
            uniformRingBufferSize,
            DefaultCoverageRingBufferSize)
    {
    }

    public GlyphAtlas(
        WgpuContext context,
        uint atlasSize,
        uint colorAtlasSize,
        uint coverageRingBufferSize)
        : this(
            context,
            atlasSize,
            colorAtlasSize,
            DefaultUniformRingBufferSize,
            coverageRingBufferSize)
    {
    }

    private GlyphAtlas(
        WgpuContext context,
        uint atlasSize,
        uint colorAtlasSize,
        uint uniformRingBufferSize,
        uint coverageRingBufferSize)
    {
        if (uniformRingBufferSize < 256 || uniformRingBufferSize % 256 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uniformRingBufferSize));
        }
        if (atlasSize <= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(atlasSize));
        }
        if (colorAtlasSize <= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(colorAtlasSize));
        }
        if (coverageRingBufferSize < 256 || coverageRingBufferSize % 256 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coverageRingBufferSize));
        }

        _context = context;
        _maxAtlasSize = atlasSize;
        _maxColorAtlasSize = colorAtlasSize;
        _atlasSize = Math.Min(atlasSize, DefaultInitialAtlasSize);
        _colorAtlasSize = Math.Min(colorAtlasSize, DefaultInitialColorAtlasSize);
        
        // Monochrome coverage is sampled from one byte per texel. Compute writes a
        // packed storage buffer and the GPU copies it into this filterable texture.
        _atlasTexture = new GpuTexture(
            _context, 
            _atlasSize, 
            _atlasSize, 
            TextureFormat.R8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst |
            TextureUsage.RenderAttachment,
            "Dynamic Glyph Coverage Atlas"
        );
        _colorAtlasTexture = new GpuTexture(
            _context,
            _colorAtlasSize,
            _colorAtlasSize,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst |
            TextureUsage.RenderAttachment,
            "Dynamic Color Glyph Atlas"
        );

        _atlasTexture.ClearRenderTarget();
        _colorAtlasTexture.ClearRenderTarget();

        // Compile and create the compute pipeline
        _pipelineCache = new RenderPipelineCache(_context);
        var shaderModule = _pipelineCache.GetOrCreateShader("GlyphRasterizer", Shaders.GlyphRasterizerShader, "GlyphRasterizerShader");
        _computePipeline = _pipelineCache.GetOrCreateComputePipeline("GlyphRasterizer", shaderModule, "cs_main");

        // Allocate a small bounded uniform ring once at startup to avoid
        // per-glyph CPU-to-GPU buffer allocation overhead.
        _uniformRingBuffer = new GpuBuffer(
            _context,
            uniformRingBufferSize,
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Glyph Atlas Uniform Ring Buffer");
        _coverageRingBuffer = new GpuBuffer(
            _context,
            coverageRingBufferSize,
            BufferUsage.Storage | BufferUsage.CopySrc,
            "Glyph Coverage Staging Ring Buffer");
        (_recordsBuffer, _segmentsBuffer) = CreateOutlineBuffers(
            _recordCapacity,
            _segmentCapacity);
        _ringOffset = 0;
        _coverageRingOffset = 0;
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

    public GlyphInfo GetOrCreateGlyphByIndex(
        TtfFont font,
        ushort glyphIdx,
        float size,
        byte subpixelX = 0,
        bool preferGlyphAtlas = false)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GlyphAtlas));
        
        var key = new GlyphKey(font, glyphIdx, size, subpixelX);
        if (_glyphs.TryGetValue(key, out var cached))
        {
            // Capacity sentinels are retryable on a later frame. A newly visible glyph
            // can then reuse a least-recently-used region instead of remaining on the
            // slower vector fallback forever.
            if (!cached.IsCapacityFallback ||
                !preferGlyphAtlas || cached.LastUsedFrame == _frameNumber)
            {
                if (cached.LastUsedFrame != _frameNumber)
                {
                    cached.LastUsedFrame = _frameNumber;
                    _glyphs[key] = cached;
                }
                return cached.Info;
            }

            _glyphs.Remove(key);
        }

        GlyphInfo info;
        {
            if (TryCreateColorBitmapGlyph(font, glyphIdx, size, preferGlyphAtlas, out info))
            {
                CacheGlyph(key, info);
                return info;
            }
            // Color vector glyphs are emitted as paths by the compositor.
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
                            else if (segment is CubicBezierSegment cubic)
                            {
                                ProcessPt(cubic.ControlPoint1);
                                ProcessPt(cubic.ControlPoint2);
                                ProcessPt(cubic.Point);
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

                            if (!TryAllocateAtlasRegion(
                                    gW,
                                    gH,
                                    preferGlyphAtlas,
                                    colorBitmap: false,
                                    out uint posX,
                                    out uint posY,
                                    out uint regionWidth,
                                    out uint regionHeight))
                            {
                                // Remember the bounded-atlas miss. Returning without caching
                                // made every subsequent frame retry the same failed allocation,
                                // emit another diagnostic, and rebuild the vector fallback.
                                // A zero-sized cached entry deliberately routes rendering to the
                                // compositor's retained vector fallback exactly once per key.
                                info = CreateEmptyGlyphInfo(font, glyphIdx, size);
                                CacheGlyph(key, info, isCapacityFallback: true);
                                return info;
                            }

                            // Keep the indexed record table stable, but expand and upload only
                            // outlines requested by visible glyphs. Compiling every outline in a
                            // large or variable font here creates severe first-use stalls.
                            if (!_fontGpuData.TryGetValue(font, out var gpuData))
                            {
                                gpuData = new FontGpuData();
                                _fontGpuData[font] = gpuData;
                            }
                            uint gpuGlyphSlot = EnsureGpuGlyph(font, glyphIdx, gpuData);
                            uint coverageBytesPerRow = GpuCoverageUpload.GetBytesPerRow(gW);
                            uint coverageBytes = checked(coverageBytesPerRow * gH);

                            // Write uniforms for the glyph
                             var uniforms = new GlyphUniforms
                             {
                                 XStart = xStart,
                                 YStart = yStart,
                                 Scale = scale,
                                 GlyphIndex = gpuGlyphSlot,
                                 Width = gW,
                                 Height = gH,
                                 SubpixelX = subpixelX * 0.25f
                             };

                            var bindGroupLayout = _context.Api.ComputePipelineGetBindGroupLayout(_computePipeline, 0);
                            uint alignedSize = (uint)((Marshal.SizeOf<GlyphUniforms>() + 255) & ~255);

                            if (_batchEncoder != null)
                            {
                                // Ring buffer slice allocation
                                if (coverageBytes > _coverageRingBuffer.Size)
                                {
                                    FlushBatchEncoder();
                                    RasterizeOversizedGlyph(
                                        uniforms,
                                        coverageBytes,
                                        coverageBytesPerRow,
                                        posX,
                                        posY,
                                        gW,
                                        gH,
                                        bindGroupLayout);
                                    CreateBatchEncoder();
                                    _context.Api.BindGroupLayoutRelease(bindGroupLayout);
                                    goto RasterizationComplete;
                                }
                                if (_ringOffset + alignedSize > _uniformRingBuffer.Size ||
                                    _coverageRingOffset + coverageBytes > _coverageRingBuffer.Size)
                                {
                                    FlushBatchEncoder();
                                    CreateBatchEncoder();
                                }

                                uniforms.OutputOffsetWords = _coverageRingOffset / 4;
                                uniforms.OutputRowWords = coverageBytesPerRow / 4;
                                _context.Api.QueueWriteBuffer(_context.Queue, _uniformRingBuffer.BufferPtr, _ringOffset, &uniforms, (uint)Marshal.SizeOf<GlyphUniforms>());

                                var entries = stackalloc BindGroupEntry[4];
                                entries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformRingBuffer.BufferPtr, Offset = _ringOffset, Size = (uint)Marshal.SizeOf<GlyphUniforms>() };
                                entries[1] = new BindGroupEntry { Binding = 1, Buffer = _recordsBuffer.BufferPtr, Offset = 0, Size = _recordsBuffer.Size };
                                entries[2] = new BindGroupEntry { Binding = 2, Buffer = _segmentsBuffer.BufferPtr, Offset = 0, Size = _segmentsBuffer.Size };
                                entries[3] = new BindGroupEntry { Binding = 3, Buffer = _coverageRingBuffer.BufferPtr, Offset = 0, Size = _coverageRingBuffer.Size };

                                var bgDesc = new BindGroupDescriptor
                                {
                                    Layout = bindGroupLayout,
                                    EntryCount = 4,
                                    Entries = entries
                                };
                                var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);

                                // Batch path: Record compute pass to batch encoder, defer resource cleanup to EndBatch
                                var passDesc = new ComputePassDescriptor();
                                var pass = _context.Api.CommandEncoderBeginComputePass(_batchEncoder, &passDesc);

                                _context.Api.ComputePassEncoderSetPipeline(pass, _computePipeline);
                                _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

                                uint workgroupsX = DivRoundUp(DivRoundUp(gW, 4), 16);
                                uint workgroupsY = DivRoundUp(gH, 16);
                                _context.Api.ComputePassEncoderDispatchWorkgroups(pass, workgroupsX, workgroupsY, 1);

                                _context.Api.ComputePassEncoderEnd(pass);
                                _context.Api.ComputePassEncoderRelease(pass);

                                GpuCoverageUpload.RecordCopy(
                                    _context,
                                    _batchEncoder,
                                    _coverageRingBuffer,
                                    _coverageRingOffset,
                                    coverageBytesPerRow,
                                    _atlasTexture,
                                    posX,
                                    posY,
                                    gW,
                                    gH);

                                _batchBindGroups.Add((nint)bg);
                                _context.Api.BindGroupLayoutRelease(bindGroupLayout);

                                _ringOffset += alignedSize;
                                _coverageRingOffset += coverageBytes;
                            }
                            else
                            {
                                // Immediate path: use a compact GPU coverage buffer, then copy its R8 bytes.
                                uniforms.OutputOffsetWords = 0;
                                uniforms.OutputRowWords = coverageBytesPerRow / 4;
                                var uniformsBuffer = new GpuBuffer(
                                    _context,
                                    (uint)Marshal.SizeOf<GlyphUniforms>(),
                                    BufferUsage.Uniform | BufferUsage.CopyDst,
                                    "Glyph Uniforms"
                                );
                                uniformsBuffer.WriteSingle(uniforms);
                                var coverageBuffer = new GpuBuffer(
                                    _context,
                                    coverageBytes,
                                    BufferUsage.Storage | BufferUsage.CopySrc,
                                    "Glyph Coverage Staging Buffer");

                                var entries = stackalloc BindGroupEntry[4];
                                entries[0] = new BindGroupEntry { Binding = 0, Buffer = uniformsBuffer.BufferPtr, Offset = 0, Size = uniformsBuffer.Size };
                                entries[1] = new BindGroupEntry { Binding = 1, Buffer = _recordsBuffer.BufferPtr, Offset = 0, Size = _recordsBuffer.Size };
                                entries[2] = new BindGroupEntry { Binding = 2, Buffer = _segmentsBuffer.BufferPtr, Offset = 0, Size = _segmentsBuffer.Size };
                                entries[3] = new BindGroupEntry { Binding = 3, Buffer = coverageBuffer.BufferPtr, Offset = 0, Size = coverageBuffer.Size };

                                var bgDesc = new BindGroupDescriptor
                                {
                                    Layout = bindGroupLayout,
                                    EntryCount = 4,
                                    Entries = entries
                                };
                                var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);

                                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Encoder") };
                                var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
                                SilkMarshal.Free((nint)encoderDesc.Label);

                                var passDesc = new ComputePassDescriptor();
                                var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);

                                _context.Api.ComputePassEncoderSetPipeline(pass, _computePipeline);
                                _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

                                uint workgroupsX = DivRoundUp(DivRoundUp(gW, 4), 16);
                                uint workgroupsY = DivRoundUp(gH, 16);
                                _context.Api.ComputePassEncoderDispatchWorkgroups(pass, workgroupsX, workgroupsY, 1);

                                _context.Api.ComputePassEncoderEnd(pass);
                                _context.Api.ComputePassEncoderRelease(pass);

                                GpuCoverageUpload.RecordCopy(
                                    _context,
                                    encoder,
                                    coverageBuffer,
                                    0,
                                    coverageBytesPerRow,
                                    _atlasTexture,
                                    posX,
                                    posY,
                                    gW,
                                    gH);

                                // Submit to queue
                                var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Command Buffer") };
                                var cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
                                SilkMarshal.Free((nint)cmdDesc.Label);

                                _context.Api.QueueSubmit(_context.Queue, 1, &cmdBuffer);

                                // Clean up temporary resources
                                _context.Api.CommandBufferRelease(cmdBuffer);
                                _context.Api.CommandEncoderRelease(encoder);
                                _context.Api.BindGroupRelease(bg);
                                _context.Api.BindGroupLayoutRelease(bindGroupLayout);
                                uniformsBuffer.Dispose();
                                coverageBuffer.Dispose();
                            }

                            RasterizationComplete:
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
                                TexCoordMax = uvMax,
                                AtlasRegionWidth = regionWidth,
                                AtlasRegionHeight = regionHeight
                            };
                        }
                    }
                }
            }
            CacheGlyph(key, info);
        }

        return info;
    }

    private void CacheGlyph(GlyphKey key, GlyphInfo info, bool isCapacityFallback = false)
    {
        _glyphs[key] = new CachedGlyph
        {
            Info = info,
            LastUsedFrame = _frameNumber,
            IsCapacityFallback = isCapacityFallback
        };
    }

    private void RasterizeOversizedGlyph(
        GlyphUniforms uniforms,
        uint coverageBytes,
        uint coverageBytesPerRow,
        uint atlasX,
        uint atlasY,
        uint width,
        uint height,
        BindGroupLayout* bindGroupLayout)
    {
        uniforms.OutputOffsetWords = 0;
        uniforms.OutputRowWords = coverageBytesPerRow / 4;
        using var uniformsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<GlyphUniforms>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Oversized Glyph Uniforms");
        using var coverageBuffer = new GpuBuffer(
            _context,
            coverageBytes,
            BufferUsage.Storage | BufferUsage.CopySrc,
            "Oversized Glyph Coverage Staging Buffer");
        uniformsBuffer.WriteSingle(uniforms);

        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = uniformsBuffer.BufferPtr, Offset = 0, Size = uniformsBuffer.Size };
        entries[1] = new BindGroupEntry { Binding = 1, Buffer = _recordsBuffer.BufferPtr, Offset = 0, Size = _recordsBuffer.Size };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = _segmentsBuffer.BufferPtr, Offset = 0, Size = _segmentsBuffer.Size };
        entries[3] = new BindGroupEntry { Binding = 3, Buffer = coverageBuffer.BufferPtr, Offset = 0, Size = coverageBuffer.Size };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = bindGroupLayout,
            EntryCount = 4,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);
        var encoderDescriptor = new CommandEncoderDescriptor();
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, _computePipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            DivRoundUp(DivRoundUp(width, 4), 16),
            DivRoundUp(height, 16),
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);
        GpuCoverageUpload.RecordCopy(
            _context,
            encoder,
            coverageBuffer,
            0,
            coverageBytesPerRow,
            _atlasTexture,
            atlasX,
            atlasY,
            width,
            height);
        var commandBufferDescriptor = new CommandBufferDescriptor();
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandBufferDescriptor);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);
        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
    }

    private bool TryCreateColorBitmapGlyph(
        TtfFont font,
        ushort glyphIndex,
        float size,
        bool preferGlyphAtlas,
        out GlyphInfo info)
    {
        info = default;
        if (!font.TryGetBitmapGlyph(glyphIndex, size, out var bitmap))
        {
            return false;
        }

        ImageResult decoded;
        try
        {
            decoded = ImageResult.FromMemory(
                bitmap.Data.ToArray(),
                ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }

        if (decoded.Width <= 0 ||
            decoded.Height <= 0 ||
            (long)decoded.Width * decoded.Height * 4L > decoded.Data.LongLength)
        {
            return false;
        }

        var width = checked((uint)decoded.Width);
        var height = checked((uint)decoded.Height);
        if (!TryAllocateAtlasRegion(
                width,
                height,
                preferGlyphAtlas,
                colorBitmap: true,
                out var x,
                out var y,
                out var regionWidth,
                out var regionHeight))
        {
            return false;
        }

        _colorAtlasTexture.WritePixelsSubRect(decoded.Data, x, y, width, height);
        var texelSize = 1f / _colorAtlasSize;
        var bitmapScale = bitmap.PixelsPerEm > 0 ? size / bitmap.PixelsPerEm : 1f;
        var bearX = bitmap.UsesHorizontalMetrics
            ? bitmap.BearingX
            : -(float)bitmap.OriginOffsetX;
        var bearY = bitmap.UsesHorizontalMetrics
            ? -bitmap.BearingY
            : bitmap.OriginOffsetY - (float)decoded.Height;
        var renderWidth = 0f;
        var renderHeight = 0f;
        var rasterScale = bitmapScale;
        if (!bitmap.UsesHorizontalMetrics &&
            font.UnitsPerEm > 0 &&
            font.TryGetGlyphBounds(glyphIndex, out var xMin, out var yMin, out var xMax, out var yMax) &&
            xMax > xMin &&
            yMax > yMin)
        {
            var outlineScale = size / font.UnitsPerEm;
            bearX = xMin * outlineScale - bitmap.OriginOffsetX * bitmapScale;
            bearY = -yMax * outlineScale + bitmap.OriginOffsetY * bitmapScale;
            renderWidth = (xMax - xMin) * outlineScale;
            renderHeight = (yMax - yMin) * outlineScale;
            rasterScale = 1f;
        }

        info = new GlyphInfo
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            BearX = bearX,
            BearY = bearY,
            RenderWidth = renderWidth,
            RenderHeight = renderHeight,
            Advance = font.GetAdvanceWidth(glyphIndex, size),
            RasterScale = rasterScale,
            IsColorBitmap = true,
            TexCoordMin = new Vector2(x * texelSize, y * texelSize),
            TexCoordMax = new Vector2((x + width) * texelSize, (y + height) * texelSize),
            AtlasRegionWidth = regionWidth,
            AtlasRegionHeight = regionHeight
        };
        return true;
    }

    private bool TryAllocateAtlasRegion(
        uint width,
        uint height,
        bool preferGlyphAtlas,
        bool colorBitmap,
        out uint x,
        out uint y,
        out uint regionWidth,
        out uint regionHeight)
    {
        x = 0;
        y = 0;
        regionWidth = 0;
        regionHeight = 0;
        uint atlasSize = colorBitmap ? _colorAtlasSize : _atlasSize;
        List<AtlasShelf> shelves = colorBitmap ? _colorShelves : _shelves;
        uint nextShelfY = colorBitmap ? _nextColorShelfY : _nextShelfY;
        if (atlasSize <= 4 || width == 0 || height == 0)
        {
            return false;
        }

        if (width > atlasSize - 4 || height > atlasSize - 4)
        {
            if (TryGrowAtlas(colorBitmap, width, height))
            {
                return TryAllocateAtlasRegion(
                    width,
                    height,
                    preferGlyphAtlas,
                    colorBitmap,
                    out x,
                    out y,
                    out regionWidth,
                    out regionHeight);
            }

            CapacityExceeded = true;
            ProGpuTextDiagnostics.WriteLine(
                $"[GlyphAtlas] Glyph {width}x{height} cannot fit in the {atlasSize}x{atlasSize} " +
                $"{(colorBitmap ? "color" : "coverage")} atlas; using vector fallback.");
            return false;
        }

        // Keep reusable size classes, but classify each axis independently. A square
        // based on max(width, height) wastes most of the atlas for narrow glyphs and
        // forces avoidable GPU rerasterization while scrolling through a font.
        uint allocationWidth = GetPreferredAllocationExtent(width, atlasSize);
        uint allocationHeight = GetPreferredAllocationExtent(height, atlasSize);
        for (int shelfIndex = 0; shelfIndex < shelves.Count; shelfIndex++)
        {
            AtlasShelf shelf = shelves[shelfIndex];
            if (shelf.Height != allocationHeight || shelf.NextX + allocationWidth + 2 > atlasSize)
            {
                continue;
            }

            x = shelf.NextX;
            y = shelf.Y;
            regionWidth = allocationWidth;
            regionHeight = allocationHeight;
            shelf.NextX += allocationWidth + 2;
            shelves[shelfIndex] = shelf;
            return true;
        }

        if (nextShelfY + allocationHeight + 2 > atlasSize)
        {
            if (TryGrowAtlas(colorBitmap, width, height))
            {
                return TryAllocateAtlasRegion(
                    width,
                    height,
                    preferGlyphAtlas,
                    colorBitmap,
                    out x,
                    out y,
                    out regionWidth,
                    out regionHeight);
            }

            if (preferGlyphAtlas && TryReuseLeastRecentlyUsedRegion(
                    allocationWidth,
                    allocationHeight,
                    colorBitmap,
                    out x,
                    out y,
                    out regionWidth,
                    out regionHeight))
            {
                CapacityExceeded = false;
                return true;
            }

            CapacityExceeded = true;
            ProGpuTextDiagnostics.WriteLine(
                "[GlyphAtlas] Atlas capacity exhausted; preserving existing UVs and using vector fallback for the new glyph.");
            return false;
        }

        x = 2;
        y = nextShelfY;
        regionWidth = allocationWidth;
        regionHeight = allocationHeight;
        shelves.Add(new AtlasShelf
        {
            Y = y,
            Height = allocationHeight,
            NextX = x + allocationWidth + 2
        });
        nextShelfY += allocationHeight + 2;
        if (colorBitmap)
        {
            _nextColorShelfY = nextShelfY;
        }
        else
        {
            _nextShelfY = nextShelfY;
        }
        return true;
    }

    private bool TryGrowAtlas(bool colorBitmap, uint requiredWidth, uint requiredHeight)
    {
        uint currentSize = colorBitmap ? _colorAtlasSize : _atlasSize;
        uint maximumSize = colorBitmap ? _maxColorAtlasSize : _maxAtlasSize;
        if (currentSize >= maximumSize)
        {
            return false;
        }

        uint requiredSize = checked(Math.Max(requiredWidth, requiredHeight) + 4);
        uint newSize = currentSize;
        do
        {
            newSize = Math.Min(maximumSize, checked(newSize * 2));
        }
        while (newSize < requiredSize && newSize < maximumSize);

        if (newSize < requiredSize)
        {
            return false;
        }

        bool resumeBatch = _batchEncoder != null;
        if (resumeBatch)
        {
            FlushBatchEncoder();
        }

        GpuTexture oldTexture = colorBitmap ? _colorAtlasTexture : _atlasTexture;
        var newTexture = new GpuTexture(
            _context,
            newSize,
            newSize,
            colorBitmap ? TextureFormat.Rgba8Unorm : TextureFormat.R8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst |
            TextureUsage.RenderAttachment,
            colorBitmap ? "Dynamic Color Glyph Atlas" : "Dynamic Glyph Coverage Atlas");
        newTexture.ClearRenderTarget();
        newTexture.CopyBaseLevelRegionFrom(oldTexture, currentSize, currentSize);

        if (colorBitmap)
        {
            _colorAtlasTexture = newTexture;
            _colorAtlasSize = newSize;
        }
        else
        {
            _atlasTexture = newTexture;
            _atlasSize = newSize;
        }

        oldTexture.Dispose();
        RefreshNormalizedTextureCoordinates(colorBitmap, newSize);
        TextureRevision++;
        CapacityExceeded = false;
        if (resumeBatch)
        {
            CreateBatchEncoder();
        }
        return true;
    }

    private void RefreshNormalizedTextureCoordinates(bool colorBitmap, uint atlasSize)
    {
        float texelSize = 1f / atlasSize;
        foreach (KeyValuePair<GlyphKey, CachedGlyph> pair in _glyphs)
        {
            ref CachedGlyph cached = ref CollectionsMarshal.GetValueRefOrNullRef(
                _glyphs,
                pair.Key);
            ref GlyphInfo info = ref cached.Info;
            if (info.IsColorBitmap != colorBitmap || info.Width == 0 || info.Height == 0)
            {
                continue;
            }

            info.TexCoordMin = new Vector2(info.X * texelSize, info.Y * texelSize);
            info.TexCoordMax = new Vector2(
                (info.X + info.Width) * texelSize,
                (info.Y + info.Height) * texelSize);
        }
    }

    private static uint GetPreferredAllocationExtent(uint extent, uint atlasSize)
    {
        // Eight-pixel classes retain cheap best-fit reuse without the near-2x loss
        // per axis caused by powers of two. Coverage still occupies its exact width
        // and height; this only controls reserved atlas residency.
        extent = Math.Max(8u, extent);
        uint alignedExtent = (extent + 7u) & ~7u;
        return Math.Min(alignedExtent, atlasSize - 4);
    }

    private bool TryReuseLeastRecentlyUsedRegion(
        uint width,
        uint height,
        bool colorBitmap,
        out uint x,
        out uint y,
        out uint regionWidth,
        out uint regionHeight)
    {
        x = 0;
        y = 0;
        regionWidth = 0;
        regionHeight = 0;

        GlyphKey candidateKey = default;
        CachedGlyph candidate = default;
        bool found = false;
        ulong bestWaste = ulong.MaxValue;

        foreach (var pair in _glyphs)
        {
            var entry = pair.Value;
            var info = entry.Info;
            uint entryWidth = info.AtlasRegionWidth > 0 ? info.AtlasRegionWidth : info.Width;
            uint entryHeight = info.AtlasRegionHeight > 0 ? info.AtlasRegionHeight : info.Height;
            if (entry.LastUsedFrame == _frameNumber ||
                info.IsColorBitmap != colorBitmap ||
                entryWidth < width || entryHeight < height ||
                entryWidth == 0 || entryHeight == 0)
            {
                continue;
            }

            ulong waste = (ulong)entryWidth * entryHeight - (ulong)width * height;
            if (!found || entry.LastUsedFrame < candidate.LastUsedFrame ||
                (entry.LastUsedFrame == candidate.LastUsedFrame && waste < bestWaste))
            {
                found = true;
                candidateKey = pair.Key;
                candidate = entry;
                bestWaste = waste;
            }
        }

        if (!found)
        {
            return false;
        }

        _glyphs.Remove(candidateKey);
        x = candidate.Info.X;
        y = candidate.Info.Y;
        regionWidth = candidate.Info.AtlasRegionWidth > 0
            ? candidate.Info.AtlasRegionWidth
            : candidate.Info.Width;
        regionHeight = candidate.Info.AtlasRegionHeight > 0
            ? candidate.Info.AtlasRegionHeight
            : candidate.Info.Height;
        EvictionCount++;
        Generation++;
        return true;
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

    private (GpuBuffer Records, GpuBuffer Segments) CreateOutlineBuffers(
        int recordCapacity,
        int segmentCapacity)
    {
        int recordSize = Marshal.SizeOf<GpuGlyphRecord>();
        int segmentSize = Marshal.SizeOf<GpuSegment>();
        return (
            new GpuBuffer(
                _context,
                checked((uint)(recordCapacity * recordSize)),
                BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc,
                "Incremental Glyph Records Buffer"),
            new GpuBuffer(
                _context,
                checked((uint)(segmentCapacity * segmentSize)),
                BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc,
                "Incremental Glyph Segments Buffer"));
    }

    private uint EnsureGpuGlyph(TtfFont font, ushort glyphIndex, FontGpuData data)
    {
        if (data.RecordSlots.TryGetValue(glyphIndex, out uint existingSlot))
        {
            return existingSlot;
        }

        _glyphSegmentScratch.Clear();
        GpuGlyphRecord record = font.AppendGpuOutlineData(glyphIndex, _glyphSegmentScratch);
        record.StartSegment = checked(record.StartSegment + (uint)_segmentCount);
        uint recordSlot = checked((uint)_recordCount);
        int requiredRecordCount = checked(_recordCount + 1);
        int requiredSegmentCount = checked(_segmentCount + _glyphSegmentScratch.Count);
        int recordSize = Marshal.SizeOf<GpuGlyphRecord>();
        int segmentSize = Marshal.SizeOf<GpuSegment>();

        if (requiredRecordCount > _recordCapacity)
        {
            int capacity = GrowCapacity(_recordCapacity, requiredRecordCount);

            var replacement = new GpuBuffer(
                _context,
                checked((uint)(capacity * recordSize)),
                BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc,
                "Incremental Glyph Records Buffer");
            CopyBufferContents(
                _recordsBuffer,
                replacement,
                checked((uint)(_recordCount * recordSize)));
            ReplaceBatchBuffer(_recordsBuffer);
            _recordsBuffer = replacement;
            _recordCapacity = capacity;
        }

        if (requiredSegmentCount > _segmentCapacity)
        {
            int capacity = GrowCapacity(_segmentCapacity, requiredSegmentCount);

            var replacement = new GpuBuffer(
                _context,
                checked((uint)(capacity * segmentSize)),
                BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc,
                "Incremental Glyph Segments Buffer");
            CopyBufferContents(
                _segmentsBuffer,
                replacement,
                checked((uint)(_segmentCount * segmentSize)));
            ReplaceBatchBuffer(_segmentsBuffer);
            _segmentsBuffer = replacement;
            _segmentCapacity = capacity;
        }

        _recordsBuffer.WriteSingle(
            record,
            checked(recordSlot * (uint)recordSize));
        if (_glyphSegmentScratch.Count > 0)
        {
            _segmentsBuffer.Write(
                CollectionsMarshal.AsSpan(_glyphSegmentScratch),
                checked((uint)(_segmentCount * segmentSize)));
        }

        _recordCount = requiredRecordCount;
        _segmentCount = requiredSegmentCount;
        data.RecordSlots.Add(glyphIndex, recordSlot);
        return recordSlot;
    }

    private static int GrowCapacity(int current, int required)
    {
        int capacity = current;
        while (capacity < required)
        {
            capacity = checked(capacity + Math.Max(1, capacity / 2));
        }

        return capacity;
    }

    private void CopyBufferContents(GpuBuffer source, GpuBuffer destination, uint size)
    {
        if (size == 0)
        {
            return;
        }

        var encoderDescriptor = new CommandEncoderDescriptor();
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        _context.Api.CommandEncoderCopyBufferToBuffer(
            encoder,
            source.BufferPtr,
            0,
            destination.BufferPtr,
            0,
            size);
        var commandBufferDescriptor = new CommandBufferDescriptor();
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandBufferDescriptor);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);
        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
    }

    private void ReplaceBatchBuffer(GpuBuffer previous)
    {
        if (_batchEncoder != null)
        {
            _batchBuffers.Add(previous);
        }
        else
        {
            previous.Dispose();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        if (_batchDepth > 0)
        {
            _batchDepth = 1;
            EndBatch();
        }

        _uniformRingBuffer.Dispose();
        _coverageRingBuffer.Dispose();

        _recordsBuffer.Dispose();
        _segmentsBuffer.Dispose();
        _fontGpuData.Clear();

        _pipelineCache.Dispose();
        _atlasTexture.Dispose();
        _colorAtlasTexture.Dispose();
        _glyphs.Clear();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
