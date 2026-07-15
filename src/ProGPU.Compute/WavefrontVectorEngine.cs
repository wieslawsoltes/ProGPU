using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Vector;
using ProGPU.Text;

namespace ProGPU.Compute;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct RayState
{
    public uint PixelCoordX;
    public uint PixelCoordY;
    public uint LeafNodeId;
    private uint _pad0;
    public Vector4 AccumulatedColor;
    public float AccumulatedAlpha;
    private float _pad1;
    private float _pad2;
    private float _pad3;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuShapeInstance
{
    public Matrix4x4 Transform;
    public Matrix4x4 InvTransform;
    public Vector2 MinBounds;
    public Vector2 MaxBounds;
    public uint BvhRootIdx;
    public uint ShapeId;
    public Vector4 Color;
    public uint IsText;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuGridCell
{
    public uint ShapeStartOffset;
    public uint ShapeCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuWavefrontUniforms
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint GridStride;
    public uint InstanceCount;
    public uint MaxQueueSize;
    public uint CurrentFrameIndex;
    public float FontWeightOffset;
    public uint Pad;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuSortParams
{
    public uint Stage;
    public uint Step;
}

public unsafe class WavefrontVectorEngine : IDisposable
{
    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _pipelineCache;

    private ComputePipeline* _initQueuePipeline;
    private ComputePipeline* _binShapesPipeline;
    private ComputePipeline* _clearMaskPipeline;
    private ComputePipeline* _traversePipeline;
    private ComputePipeline* _sortPipeline;
    private ComputePipeline* _intersectPipeline;

    // Global BVH Cache
    private readonly List<GpuBvhNode> _bvhNodes = new();
    private readonly List<GpuLineSegment> _lineSegments = new();
    
    // Cache maps to offsets inside _bvhNodes and _lineSegments
    private readonly Dictionary<PathCacheKey, (uint BvhOffset, uint LineOffset, Vector2 Min, Vector2 Max)> _pathCache = new();
    private readonly Dictionary<(TtfFont Font, ushort GlyphId), (uint BvhOffset, uint LineOffset, Vector2 Min, Vector2 Max)> _glyphCache = new();

    // GPU Buffers (Reallocated/resized when frame sizes grow)
    private GpuBuffer? _bvhBuffer;
    private GpuBuffer? _linesBuffer;
    private GpuBuffer? _instancesBuffer;
    private GpuBuffer? _gridCellsBuffer;
    private GpuBuffer? _gridIndicesBuffer;
    private GpuBuffer? _rayQueueBuffer;
    private GpuBuffer? _queueCounterBuffer;
    private GpuBuffer? _uniformsBuffer;
    private GpuBuffer? _sortParamsBuffer;

    private GpuTexture? _maskTexture;

    private readonly List<GpuShapeInstance> _frameInstances = new();
    private uint _frameNumber = 0;
    private bool _isDisposed;

    public WavefrontVectorEngine(WgpuContext context)
    {
        _context = context;
        _pipelineCache = new RenderPipelineCache(_context);

        InitializePipelines();
    }

    private void InitializePipelines()
    {
        var shaderModule = _pipelineCache.GetOrCreateShader("WavefrontShaders", WavefrontShaders.ShadersSource, "WavefrontShaders");
        _initQueuePipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontInit", shaderModule, "init_queue");
        _binShapesPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontBin", shaderModule, "bin_shapes");
        _clearMaskPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontClearMask", shaderModule, "clear_mask");
        _traversePipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontTraverse", shaderModule, "wavefront_traverse");
        _sortPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontSort", shaderModule, "wavefront_sort");
        _intersectPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontIntersect", shaderModule, "wavefront_intersect");
    }

    private (uint BvhOffset, uint LineOffset, Vector2 Min, Vector2 Max) GetOrAddPathGeometry(PathGeometry path)
    {
        int contentHash = PathAtlas.ComputeHash(path);
        var key = new PathCacheKey(contentHash, 1f);

        if (_pathCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var flattened = BvhBuilder.FlattenPath(path);
        var nodes = BvhBuilder.BuildBvh(flattened, out var orderedLines);

        uint bvhOffset = (uint)_bvhNodes.Count;
        uint lineOffset = (uint)_lineSegments.Count;

        // Bounding box
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var l in orderedLines)
        {
            minX = Math.Min(minX, Math.Min(l.Start.X, l.End.X));
            minY = Math.Min(minY, Math.Min(l.Start.Y, l.End.Y));
            maxX = Math.Max(maxX, Math.Max(l.Start.X, l.End.X));
            maxY = Math.Max(maxY, Math.Max(l.Start.Y, l.End.Y));
        }

        // Adjust left child / first line offsets relative to the global buffer
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.PrimitiveCount > 0)
            {
                node.LeftChildOrFirstLine += lineOffset;
            }
            else
            {
                node.LeftChildOrFirstLine += bvhOffset;
                node.RightChild += bvhOffset;
            }
            _bvhNodes.Add(node);
        }

        _lineSegments.AddRange(orderedLines);

        var boundsMin = new Vector2(minX, minY);
        var boundsMax = new Vector2(maxX, maxY);

        var result = (bvhOffset, lineOffset, boundsMin, boundsMax);
        _pathCache[key] = result;
        return result;
    }

    private (uint BvhOffset, uint LineOffset, Vector2 Min, Vector2 Max) GetOrAddGlyphGeometry(TtfFont font, ushort glyphId)
    {
        var key = (font, glyphId);
        if (_glyphCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var flattened = BvhBuilder.FlattenGlyph(font, glyphId);
        var nodes = BvhBuilder.BuildBvh(flattened, out var orderedLines);

        uint bvhOffset = (uint)_bvhNodes.Count;
        uint lineOffset = (uint)_lineSegments.Count;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var l in orderedLines)
        {
            minX = Math.Min(minX, Math.Min(l.Start.X, l.End.X));
            minY = Math.Min(minY, Math.Min(l.Start.Y, l.End.Y));
            maxX = Math.Max(maxX, Math.Max(l.Start.X, l.End.X));
            maxY = Math.Max(maxY, Math.Max(l.Start.Y, l.End.Y));
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.PrimitiveCount > 0)
            {
                node.LeftChildOrFirstLine += lineOffset;
            }
            else
            {
                node.LeftChildOrFirstLine += bvhOffset;
                node.RightChild += bvhOffset;
            }
            _bvhNodes.Add(node);
        }

        _lineSegments.AddRange(orderedLines);

        var boundsMin = new Vector2(minX, minY);
        var boundsMax = new Vector2(maxX, maxY);

        var result = (bvhOffset, lineOffset, boundsMin, boundsMax);
        _glyphCache[key] = result;
        return result;
    }

    public void BeginFrame()
    {
        _frameInstances.Clear();
        _frameNumber++;
    }

    public void DrawPath(PathGeometry path, in Matrix4x4 transform, Brush brush)
    {
        var geom = GetOrAddPathGeometry(path);
        
        Matrix4x4.Invert(transform, out var inv);

        var brushColor = (brush is SolidColorBrush solid) ? solid.Color : Vector4.One;

        _frameInstances.Add(new GpuShapeInstance
        {
            Transform = transform,
            InvTransform = inv,
            MinBounds = geom.Min,
            MaxBounds = geom.Max,
            BvhRootIdx = geom.BvhOffset,
            ShapeId = (uint)_frameInstances.Count,
            Color = brushColor,
            IsText = 0
        });
    }

    public void DrawText(string text, TtfFont font, float fontSize, in Matrix4x4 transform, Brush brush)
    {
        // Simple CPU shaping for text to build glyph instances
        var brushColor = (brush is SolidColorBrush solid) ? solid.Color : Vector4.One;

        float currentX = 0f;
        float fontScale = fontSize / font.UnitsPerEm;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            ushort glyphId = font.GetGlyphIndex(c);
            var geom = GetOrAddGlyphGeometry(font, glyphId);

            var glyphTransform = Matrix4x4.CreateScale(fontScale) *
                                 Matrix4x4.CreateTranslation(currentX, 0f, 0f) *
                                 transform;

            Matrix4x4.Invert(glyphTransform, out var inv);

            _frameInstances.Add(new GpuShapeInstance
            {
                Transform = glyphTransform,
                InvTransform = inv,
                MinBounds = geom.Min,
                MaxBounds = geom.Max,
                BvhRootIdx = geom.BvhOffset,
                ShapeId = (uint)_frameInstances.Count,
                Color = brushColor,
                IsText = 1
            });

            currentX += font.GetAdvanceWidth(glyphId, fontSize);
        }
    }

    public void EndFrame(CommandEncoder* encoder, GpuTexture destination, float fontWeightOffset = 0f)
    {
        uint width = destination.Width;
        uint height = destination.Height;

        if (_frameInstances.Count == 0) return;

        // 1. Build screen grid cells (16x16 pixels)
        uint gridCols = (width + 15) / 16;
        uint gridRows = (height + 15) / 16;
        uint gridStride = gridCols;
        uint cellCount = gridCols * gridRows;

        // Allocate index indices buffer with static capacity of cellCount * 64
        uint maxCellInstances = 64;
        uint maxGridIndices = cellCount * maxCellInstances;

        // 2. Allocate & upload GPU Buffers
        UpdateGpuBuffers(width, height, gridStride, cellCount, maxGridIndices);

        _bvhBuffer!.Write(CollectionsMarshal.AsSpan(_bvhNodes));
        _linesBuffer!.Write(CollectionsMarshal.AsSpan(_lineSegments));
        _instancesBuffer!.Write(CollectionsMarshal.AsSpan(_frameInstances));

        // Write Uniforms
        uint queueSize = 131072; // Power of two to simplify Bitonic Sort
        var uniforms = new GpuWavefrontUniforms
        {
            ScreenWidth = width,
            ScreenHeight = height,
            GridStride = gridStride,
            InstanceCount = (uint)_frameInstances.Count,
            MaxQueueSize = queueSize,
            CurrentFrameIndex = _frameNumber,
            FontWeightOffset = fontWeightOffset
        };
        _uniformsBuffer!.WriteSingle(uniforms);

        // 3. Bind Groups
        var entries = stackalloc BindGroupEntry[12];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        entries[1] = new BindGroupEntry { Binding = 1, Buffer = _rayQueueBuffer!.BufferPtr, Offset = 0, Size = _rayQueueBuffer.Size };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = _queueCounterBuffer!.BufferPtr, Offset = 0, Size = _queueCounterBuffer.Size };
        entries[3] = new BindGroupEntry { Binding = 3, Buffer = _bvhBuffer.BufferPtr, Offset = 0, Size = _bvhBuffer.Size };
        entries[4] = new BindGroupEntry { Binding = 4, Buffer = _instancesBuffer.BufferPtr, Offset = 0, Size = _instancesBuffer.Size };
        entries[5] = new BindGroupEntry { Binding = 5, Buffer = _gridCellsBuffer!.BufferPtr, Offset = 0, Size = _gridCellsBuffer.Size };
        entries[6] = new BindGroupEntry { Binding = 6, Buffer = _gridIndicesBuffer!.BufferPtr, Offset = 0, Size = _gridIndicesBuffer.Size };
        entries[7] = new BindGroupEntry { Binding = 7, TextureView = _maskTexture!.ViewPtr };
        entries[8] = new BindGroupEntry { Binding = 8, TextureView = destination.ViewPtr };
        entries[9] = new BindGroupEntry { Binding = 9, Buffer = _linesBuffer.BufferPtr, Offset = 0, Size = _linesBuffer.Size };
        entries[10] = new BindGroupEntry { Binding = 10, TextureView = _maskTexture.ViewPtr };
        entries[11] = new BindGroupEntry { Binding = 11, TextureView = destination.ViewPtr };

        var bgLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_traversePipeline, 0);
        var bgDesc = new BindGroupDescriptor
        {
            Layout = bgLayout,
            EntryCount = 12,
            Entries = entries
        };
        var mainBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
        _context.Wgpu.BindGroupLayoutRelease(bgLayout);

        // Bind Group for Sorting
        var sortEntries = stackalloc BindGroupEntry[3];
        sortEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _rayQueueBuffer.BufferPtr, Offset = 0, Size = _rayQueueBuffer.Size };
        sortEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        sortEntries[2] = new BindGroupEntry { Binding = 2, Buffer = _sortParamsBuffer!.BufferPtr, Offset = 0, Size = _sortParamsBuffer.Size };

        var sortBgLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_sortPipeline, 0);
        var sortBgDesc = new BindGroupDescriptor
        {
            Layout = sortBgLayout,
            EntryCount = 3,
            Entries = sortEntries
        };
        var sortBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &sortBgDesc);
        _context.Wgpu.BindGroupLayoutRelease(sortBgLayout);

        // 4. Dispatch Compute Passes
        var passDesc = new ComputePassDescriptor();

        // Pass 0.1: Clear active pixel mask on the GPU
        var passClear = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(passClear, _clearMaskPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(passClear, 0, mainBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(passClear, (width + 15) / 16, (height + 15) / 16, 1);
        _context.Wgpu.ComputePassEncoderEnd(passClear);
        _context.Wgpu.ComputePassEncoderRelease(passClear);

        // Pass 0.2: Bin shapes on the GPU
        var passBin = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(passBin, _binShapesPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(passBin, 0, mainBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(passBin, (gridCols + 15) / 16, (gridRows + 15) / 16, 1);
        _context.Wgpu.ComputePassEncoderEnd(passBin);
        _context.Wgpu.ComputePassEncoderRelease(passBin);

        // Pass 0: Init queue
        _context.Wgpu.CommandEncoderClearBuffer(encoder, _queueCounterBuffer.BufferPtr, 0, 4);
        var pass0 = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(pass0, _initQueuePipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass0, 0, mainBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass0, queueSize / 256, 1, 1);
        _context.Wgpu.ComputePassEncoderEnd(pass0);
        _context.Wgpu.ComputePassEncoderRelease(pass0);

        // Pass 1: Traversal
        var pass1 = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(pass1, _traversePipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass1, 0, mainBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass1, (width + 7) / 8, (height + 7) / 8, 1);
        _context.Wgpu.ComputePassEncoderEnd(pass1);
        _context.Wgpu.ComputePassEncoderRelease(pass1);

        // Pass 2: Bitonic Sort
        // Loop stages and steps
        for (uint stage = 2; stage <= queueSize; stage <<= 1)
        {
            for (uint step = stage >> 1; step > 0; step >>= 1)
            {
                var sortParams = new GpuSortParams { Stage = stage, Step = step };
                _context.Wgpu.QueueWriteBuffer(_context.Queue, _sortParamsBuffer.BufferPtr, 0, &sortParams, (nuint)sizeof(GpuSortParams));

                var sortPass = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
                _context.Wgpu.ComputePassEncoderSetPipeline(sortPass, _sortPipeline);
                _context.Wgpu.ComputePassEncoderSetBindGroup(sortPass, 0, sortBindGroup, 0, null);
                _context.Wgpu.ComputePassEncoderDispatchWorkgroups(sortPass, queueSize / 256, 1, 1);
                _context.Wgpu.ComputePassEncoderEnd(sortPass);
                _context.Wgpu.ComputePassEncoderRelease(sortPass);
            }
        }

        // Pass 3: Intersection & Blending
        var pass3 = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(pass3, _intersectPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass3, 0, mainBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass3, queueSize / 256, 1, 1);
        _context.Wgpu.ComputePassEncoderEnd(pass3);
        _context.Wgpu.ComputePassEncoderRelease(pass3);

        // Cleanup local Bind Groups
        _context.Wgpu.BindGroupRelease(mainBindGroup);
        _context.Wgpu.BindGroupRelease(sortBindGroup);
    }

    private void UpdateGpuBuffers(uint width, uint height, uint gridStride, uint cellCount, uint indexCount)
    {
        uint maxQueueSize = 131072;

        _bvhBuffer?.Dispose();
        _bvhBuffer = new GpuBuffer(_context, (uint)(_bvhNodes.Count * sizeof(GpuBvhNode)), BufferUsage.Storage, "Wavefront BVH Nodes Buffer");

        _linesBuffer?.Dispose();
        _linesBuffer = new GpuBuffer(_context, (uint)(_lineSegments.Count * sizeof(GpuLineSegment)), BufferUsage.Storage, "Wavefront Lines Buffer");

        _instancesBuffer?.Dispose();
        _instancesBuffer = new GpuBuffer(_context, (uint)(_frameInstances.Count * sizeof(GpuShapeInstance)), BufferUsage.Storage, "Wavefront Instances Buffer");

        _gridCellsBuffer?.Dispose();
        _gridCellsBuffer = new GpuBuffer(_context, (uint)(cellCount * sizeof(GpuGridCell)), BufferUsage.Storage, "Wavefront Grid Cells Buffer");

        _gridIndicesBuffer?.Dispose();
        _gridIndicesBuffer = new GpuBuffer(_context, (uint)(indexCount * sizeof(uint)), BufferUsage.Storage, "Wavefront Grid Indices Buffer");

        if (_rayQueueBuffer == null)
        {
            _rayQueueBuffer = new GpuBuffer(_context, (uint)(maxQueueSize * sizeof(RayState)), BufferUsage.Storage, "Wavefront Ray Queue Buffer");
            _queueCounterBuffer = new GpuBuffer(_context, 4, BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, "Wavefront Queue Counter Buffer");
            _uniformsBuffer = new GpuBuffer(_context, (uint)sizeof(GpuWavefrontUniforms), BufferUsage.Uniform, "Wavefront Uniforms Buffer");
            _sortParamsBuffer = new GpuBuffer(_context, (uint)sizeof(GpuSortParams), BufferUsage.Uniform | BufferUsage.CopyDst, "Wavefront Sort Params Buffer");
        }

        if (_maskTexture == null || _maskTexture.Width != width || _maskTexture.Height != height)
        {
            _maskTexture?.Dispose();
            _maskTexture = new GpuTexture(_context, width, height, TextureFormat.R32Uint,
                TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopyDst,
                "Wavefront Mask Texture");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _bvhBuffer?.Dispose();
        _linesBuffer?.Dispose();
        _instancesBuffer?.Dispose();
        _gridCellsBuffer?.Dispose();
        _gridIndicesBuffer?.Dispose();
        _rayQueueBuffer?.Dispose();
        _queueCounterBuffer?.Dispose();
        _uniformsBuffer?.Dispose();
        _sortParamsBuffer?.Dispose();

        _maskTexture?.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~WavefrontVectorEngine()
    {
        Dispose();
    }
}
