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
    private const uint DefaultUniformRingBufferSize = 256 * 1024;
    private const int InitialFontRecordCapacity = 32;
    private const int InitialFontSegmentCapacity = 256;

    private readonly WgpuContext _context;
    private readonly GpuTexture _atlasTexture;
    private readonly uint _atlasSize;

    // Packing state. Shelves are segregated by height so a tall glyph cannot waste
    // the full width of a row containing short punctuation and combining marks.
    private struct AtlasShelf
    {
        public uint Y;
        public uint Height;
        public uint NextX;
    }

    private readonly List<AtlasShelf> _shelves = new();
    private uint _nextShelfY = 2;

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
        public required GpuBuffer RecordsBuffer { get; set; }
        public required GpuBuffer SegmentsBuffer { get; set; }
        public Dictionary<ushort, uint> RecordSlots { get; } = new();
        public List<GpuGlyphRecord> Records { get; } = new(InitialFontRecordCapacity);
        public List<GpuSegment> Segments { get; } = new(InitialFontSegmentCapacity);
        public int RecordCapacity { get; set; } = InitialFontRecordCapacity;
        public int SegmentCapacity { get; set; } = InitialFontSegmentCapacity;
    }

    private readonly Dictionary<TtfFont, FontGpuData> _fontGpuData = new();
    
    private readonly RenderPipelineCache _pipelineCache;
    private readonly ComputePipeline* _computePipeline;

    private CommandEncoder* _batchEncoder;
    private int _batchDepth;
    private readonly List<GpuBuffer> _batchBuffers = new();
    private readonly List<nint> _batchBindGroups = new();

    private readonly GpuBuffer _uniformRingBuffer;
    private uint _ringOffset;
    private ulong _frameNumber;

    public void BeginBatch()
    {
        if (_isDisposed) return;
        _batchDepth++;
        if (_batchDepth > 1) return;

        _frameNumber++;

        CreateBatchEncoder();
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

    public uint AtlasSize => _atlasSize;

    public int CachedGlyphCount => _glyphs.Count;

    public int CompiledGpuGlyphCount
    {
        get
        {
            int count = 0;
            foreach (FontGpuData data in _fontGpuData.Values)
            {
                count += data.RecordSlots.Count;
            }
            return count;
        }
    }

    public int AllocatedGpuGlyphRecordCapacity
    {
        get
        {
            int count = 0;
            foreach (FontGpuData data in _fontGpuData.Values)
            {
                count += data.RecordCapacity;
            }
            return count;
        }
    }

    public ulong Generation { get; private set; }

    public bool CapacityExceeded { get; private set; }

    public ulong EvictionCount { get; private set; }

    public ulong ClearCount { get; private set; }

    public bool IsAlmostFull => _nextShelfY > (_atlasSize * 0.85f);

    public void Clear()
    {
        if (_isDisposed) return;
        
        ProGpuTextDiagnostics.WriteLine("[GlyphAtlas] Proactive Clear: Resetting packer and clearing cache.");
        _glyphs.Clear();
        _shelves.Clear();
        _nextShelfY = 2;

        _atlasTexture.ClearRenderTarget();
        CapacityExceeded = false;
        ClearCount++;
        Generation++;
    }

    public GlyphAtlas(WgpuContext context, uint atlasSize = 2048)
        : this(context, atlasSize, DefaultUniformRingBufferSize)
    {
    }

    internal GlyphAtlas(WgpuContext context, uint atlasSize, uint uniformRingBufferSize)
    {
        if (uniformRingBufferSize < 256 || uniformRingBufferSize % 256 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uniformRingBufferSize));
        }

        _context = context;
        _atlasSize = atlasSize;
        
        // Use Rgba8Unorm for dynamic alpha mapping (highly memory efficient and WebGPU Storage standard-compliant)
        // With TextureUsage.StorageBinding to allow Compute Shader writing directly to it
        _atlasTexture = new GpuTexture(
            _context, 
            _atlasSize, 
            _atlasSize, 
            TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst |
            TextureUsage.StorageBinding | TextureUsage.RenderAttachment,
            "Dynamic Glyph Atlas"
        );

        _atlasTexture.ClearRenderTarget();

        // Compile and create the compute pipeline
        _pipelineCache = new RenderPipelineCache(_context);
        var shaderModule = _pipelineCache.GetOrCreateShader("GlyphRasterizer", Shaders.GlyphRasterizerShader, "GlyphRasterizerShader");
        _computePipeline = _pipelineCache.GetOrCreateComputePipeline("GlyphRasterizer", shaderModule, "cs_main");

        // Allocate a 256KB uniform ring buffer once at startup to eliminate CPU-to-GPU memory allocation overhead
        _uniformRingBuffer = new GpuBuffer(
            _context,
            uniformRingBufferSize,
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Glyph Atlas Uniform Ring Buffer");
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
                                gpuData = CreateFontGpuData();
                                _fontGpuData[font] = gpuData;
                            }
                            uint gpuGlyphSlot = EnsureGpuGlyph(font, glyphIdx, gpuData);

                            // Write uniforms for the glyph
                             var uniforms = new GlyphUniforms
                             {
                                 XStart = xStart,
                                 YStart = yStart,
                                 Scale = scale,
                                 GlyphIndex = gpuGlyphSlot,
                                 AtlasX = posX,
                                 AtlasY = posY,
                                 Width = gW,
                                 Height = gH,
                                 SubpixelX = subpixelX * 0.25f
                             };

                            var bindGroupLayout = _context.Api.ComputePipelineGetBindGroupLayout(_computePipeline, 0);
                            uint alignedSize = (uint)((Marshal.SizeOf<GlyphUniforms>() + 255) & ~255);

                            if (_batchEncoder != null)
                            {
                                // Ring buffer slice allocation
                                if (_ringOffset + alignedSize > _uniformRingBuffer.Size)
                                {
                                    FlushBatchEncoder();
                                    CreateBatchEncoder();
                                }

                                _context.Api.QueueWriteBuffer(_context.Queue, _uniformRingBuffer.BufferPtr, _ringOffset, &uniforms, (uint)Marshal.SizeOf<GlyphUniforms>());

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
                                var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);

                                // Batch path: Record compute pass to batch encoder, defer resource cleanup to EndBatch
                                var passDesc = new ComputePassDescriptor();
                                var pass = _context.Api.CommandEncoderBeginComputePass(_batchEncoder, &passDesc);

                                _context.Api.ComputePassEncoderSetPipeline(pass, _computePipeline);
                                _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

                                uint workgroupsX = DivRoundUp(gW, 16);
                                uint workgroupsY = DivRoundUp(gH, 16);
                                _context.Api.ComputePassEncoderDispatchWorkgroups(pass, workgroupsX, workgroupsY, 1);

                                _context.Api.ComputePassEncoderEnd(pass);
                                _context.Api.ComputePassEncoderRelease(pass);

                                _batchBindGroups.Add((nint)bg);
                                _context.Api.BindGroupLayoutRelease(bindGroupLayout);

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
                                var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);

                                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Glyph Rasterizer Encoder") };
                                var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
                                SilkMarshal.Free((nint)encoderDesc.Label);

                                var passDesc = new ComputePassDescriptor();
                                var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);

                                _context.Api.ComputePassEncoderSetPipeline(pass, _computePipeline);
                                _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

                                uint workgroupsX = DivRoundUp(gW, 16);
                                uint workgroupsY = DivRoundUp(gH, 16);
                                _context.Api.ComputePassEncoderDispatchWorkgroups(pass, workgroupsX, workgroupsY, 1);

                                _context.Api.ComputePassEncoderEnd(pass);
                                _context.Api.ComputePassEncoderRelease(pass);

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
                out var x,
                out var y,
                out var regionWidth,
                out var regionHeight))
        {
            return false;
        }

        _atlasTexture.WritePixelsSubRect(decoded.Data, x, y, width, height);
        var texelSize = 1f / _atlasSize;
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
        out uint x,
        out uint y,
        out uint regionWidth,
        out uint regionHeight)
    {
        x = 0;
        y = 0;
        regionWidth = 0;
        regionHeight = 0;
        if (_atlasSize <= 4 || width == 0 || height == 0)
        {
            return false;
        }

        if (width > _atlasSize - 4 || height > _atlasSize - 4)
        {
            CapacityExceeded = true;
            ProGpuTextDiagnostics.WriteLine(
                $"[GlyphAtlas] Glyph {width}x{height} cannot fit in the {_atlasSize}x{_atlasSize} atlas; using vector fallback.");
            return false;
        }

        // Keep reusable size classes, but classify each axis independently. A square
        // based on max(width, height) wastes most of the atlas for narrow glyphs and
        // forces avoidable GPU rerasterization while scrolling through a font.
        uint allocationWidth = GetPreferredAllocationExtent(width);
        uint allocationHeight = GetPreferredAllocationExtent(height);
        for (int shelfIndex = 0; shelfIndex < _shelves.Count; shelfIndex++)
        {
            AtlasShelf shelf = _shelves[shelfIndex];
            if (shelf.Height != allocationHeight || shelf.NextX + allocationWidth + 2 > _atlasSize)
            {
                continue;
            }

            x = shelf.NextX;
            y = shelf.Y;
            regionWidth = allocationWidth;
            regionHeight = allocationHeight;
            shelf.NextX += allocationWidth + 2;
            _shelves[shelfIndex] = shelf;
            return true;
        }

        if (_nextShelfY + allocationHeight + 2 > _atlasSize)
        {
            if (preferGlyphAtlas && TryReuseLeastRecentlyUsedRegion(
                    allocationWidth,
                    allocationHeight,
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
        y = _nextShelfY;
        regionWidth = allocationWidth;
        regionHeight = allocationHeight;
        _shelves.Add(new AtlasShelf
        {
            Y = y,
            Height = allocationHeight,
            NextX = x + allocationWidth + 2
        });
        _nextShelfY += allocationHeight + 2;
        return true;
    }

    private uint GetPreferredAllocationExtent(uint extent)
    {
        // Eight-pixel classes retain cheap best-fit reuse without the near-2x loss
        // per axis caused by powers of two. Coverage still occupies its exact width
        // and height; this only controls reserved atlas residency.
        extent = Math.Max(8u, extent);
        uint alignedExtent = (extent + 7u) & ~7u;
        return Math.Min(alignedExtent, _atlasSize - 4);
    }

    private bool TryReuseLeastRecentlyUsedRegion(
        uint width,
        uint height,
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

    private FontGpuData CreateFontGpuData()
    {
        int recordSize = Marshal.SizeOf<GpuGlyphRecord>();
        int segmentSize = Marshal.SizeOf<GpuSegment>();
        uint recordsSize = checked((uint)(InitialFontRecordCapacity * recordSize));
        uint segmentsSize = checked((uint)(InitialFontSegmentCapacity * segmentSize));
        return new FontGpuData
        {
            RecordsBuffer = new GpuBuffer(
                _context,
                recordsSize,
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Incremental Glyph Records Buffer"),
            SegmentsBuffer = new GpuBuffer(
                _context,
                segmentsSize,
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Incremental Glyph Segments Buffer")
        };
    }

    private uint EnsureGpuGlyph(TtfFont font, ushort glyphIndex, FontGpuData data)
    {
        if (data.RecordSlots.TryGetValue(glyphIndex, out uint existingSlot))
        {
            return existingSlot;
        }

        int previousSegmentCount = data.Segments.Count;
        GpuGlyphRecord record;
        try
        {
            record = font.AppendGpuOutlineData(glyphIndex, data.Segments);
        }
        catch
        {
            if (data.Segments.Count > previousSegmentCount)
            {
                data.Segments.RemoveRange(
                    previousSegmentCount,
                    data.Segments.Count - previousSegmentCount);
            }
            throw;
        }

        uint recordSlot = checked((uint)data.Records.Count);
        data.Records.Add(record);
        int appendedSegmentCount = data.Segments.Count - previousSegmentCount;
        int recordSize = Marshal.SizeOf<GpuGlyphRecord>();
        int segmentSize = Marshal.SizeOf<GpuSegment>();
        try
        {
            if (data.Records.Count > data.RecordCapacity)
            {
                int capacity = data.RecordCapacity;
                while (capacity < data.Records.Count)
                {
                    capacity = checked(capacity * 2);
                }

                var replacement = new GpuBuffer(
                    _context,
                    checked((uint)(capacity * recordSize)),
                    BufferUsage.Storage | BufferUsage.CopyDst,
                    "Incremental Glyph Records Buffer");
                replacement.Write(CollectionsMarshal.AsSpan(data.Records));
                ReplaceBatchBuffer(data.RecordsBuffer);
                data.RecordsBuffer = replacement;
                data.RecordCapacity = capacity;
            }
            else
            {
                data.RecordsBuffer.WriteSingle(
                    record,
                    checked(recordSlot * (uint)recordSize));
            }

            if (data.Segments.Count > data.SegmentCapacity)
            {
                int capacity = data.SegmentCapacity;
                while (capacity < data.Segments.Count)
                {
                    capacity = checked(capacity * 2);
                }

                var replacement = new GpuBuffer(
                    _context,
                    checked((uint)(capacity * segmentSize)),
                    BufferUsage.Storage | BufferUsage.CopyDst,
                    "Incremental Glyph Segments Buffer");
                replacement.Write(CollectionsMarshal.AsSpan(data.Segments));
                ReplaceBatchBuffer(data.SegmentsBuffer);
                data.SegmentsBuffer = replacement;
                data.SegmentCapacity = capacity;
            }
            else if (appendedSegmentCount > 0)
            {
                data.SegmentsBuffer.Write(
                    CollectionsMarshal.AsSpan(data.Segments).Slice(
                        previousSegmentCount,
                        appendedSegmentCount),
                    checked((uint)(previousSegmentCount * segmentSize)));
            }

            data.RecordSlots.Add(glyphIndex, recordSlot);
            return recordSlot;
        }
        catch
        {
            data.Records.RemoveAt(data.Records.Count - 1);
            if (data.Segments.Count > previousSegmentCount)
            {
                data.Segments.RemoveRange(
                    previousSegmentCount,
                    data.Segments.Count - previousSegmentCount);
            }
            throw;
        }
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
