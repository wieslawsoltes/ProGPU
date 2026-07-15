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
    public uint ShapeId;
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
    public uint StructPad0;
    public uint StructPad1;
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
    public float DpiScale;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuSortParams
{
    public uint Stage;
    public uint Step;
}

[StructLayout(LayoutKind.Sequential, Size = 256)]
public struct GpuSortParamsAligned
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
    private GpuTexture? _bgCopyTexture;

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

    public void DrawGlyph(TtfFont font, ushort glyphId, float fontSize, in Matrix4x4 transform, Brush brush)
    {
        var geom = GetOrAddGlyphGeometry(font, glyphId);
        
        float fontScale = fontSize / font.UnitsPerEm;

        var glyphTransform = Matrix4x4.CreateScale(fontScale, -fontScale, 1f) * transform;

        Matrix4x4.Invert(glyphTransform, out var inv);

        var brushColor = (brush is SolidColorBrush solid) ? solid.Color : Vector4.One;

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
    }

    public void EndFrame(CommandEncoder* encoder, GpuTexture destination, float dpiScale, float fontWeightOffset = 0f)
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

        // Copy cleared destination texture to background copy texture
        var copySrc = new ImageCopyTexture
        {
            Texture = destination.TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
            Aspect = TextureAspect.All
        };
        var copyDst = new ImageCopyTexture
        {
            Texture = _bgCopyTexture!.TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
            Aspect = TextureAspect.All
        };
        var copySize = new Extent3D
        {
            Width = width,
            Height = height,
            DepthOrArrayLayers = 1
        };
        _context.Wgpu.CommandEncoderCopyTextureToTexture(encoder, &copySrc, &copyDst, &copySize);

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
            FontWeightOffset = fontWeightOffset,
            DpiScale = dpiScale
        };
        _uniformsBuffer!.WriteSingle(uniforms);

        // 3. Bind Groups
        
        // 3.1 Bind Group for Clear Mask Pipeline
        var clearMaskEntries = stackalloc BindGroupEntry[2];
        clearMaskEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        clearMaskEntries[1] = new BindGroupEntry { Binding = 10, TextureView = _maskTexture!.ViewPtr };
        var clearMaskLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_clearMaskPipeline, 0);
        var clearMaskBgDesc = new BindGroupDescriptor
        {
            Layout = clearMaskLayout,
            EntryCount = 2,
            Entries = clearMaskEntries
        };
        var clearMaskBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &clearMaskBgDesc);
        _context.Wgpu.BindGroupLayoutRelease(clearMaskLayout);

        // 3.2 Bind Group for Bin Shapes Pipeline
        var binShapesEntries = stackalloc BindGroupEntry[4];
        binShapesEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        binShapesEntries[1] = new BindGroupEntry { Binding = 4, Buffer = _instancesBuffer!.BufferPtr, Offset = 0, Size = _instancesBuffer.Size };
        binShapesEntries[2] = new BindGroupEntry { Binding = 5, Buffer = _gridCellsBuffer!.BufferPtr, Offset = 0, Size = _gridCellsBuffer.Size };
        binShapesEntries[3] = new BindGroupEntry { Binding = 6, Buffer = _gridIndicesBuffer!.BufferPtr, Offset = 0, Size = _gridIndicesBuffer.Size };
        var binShapesLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_binShapesPipeline, 0);
        var binShapesBgDesc = new BindGroupDescriptor
        {
            Layout = binShapesLayout,
            EntryCount = 4,
            Entries = binShapesEntries
        };
        var binShapesBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &binShapesBgDesc);
        _context.Wgpu.BindGroupLayoutRelease(binShapesLayout);

        // 3.3 Bind Group for Init Queue Pipeline
        var initQueueEntries = stackalloc BindGroupEntry[2];
        initQueueEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        initQueueEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _rayQueueBuffer!.BufferPtr, Offset = 0, Size = _rayQueueBuffer.Size };
        var initQueueLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_initQueuePipeline, 0);
        var initQueueBgDesc = new BindGroupDescriptor
        {
            Layout = initQueueLayout,
            EntryCount = 2,
            Entries = initQueueEntries
        };
        var initQueueBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &initQueueBgDesc);
        _context.Wgpu.BindGroupLayoutRelease(initQueueLayout);

        // 3.4 Bind Group for Traverse Pipeline
        var traverseEntries = stackalloc BindGroupEntry[9];
        traverseEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        traverseEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _rayQueueBuffer!.BufferPtr, Offset = 0, Size = _rayQueueBuffer.Size };
        traverseEntries[2] = new BindGroupEntry { Binding = 2, Buffer = _queueCounterBuffer!.BufferPtr, Offset = 0, Size = _queueCounterBuffer.Size };
        traverseEntries[3] = new BindGroupEntry { Binding = 3, Buffer = _bvhBuffer!.BufferPtr, Offset = 0, Size = _bvhBuffer.Size };
        traverseEntries[4] = new BindGroupEntry { Binding = 4, Buffer = _instancesBuffer!.BufferPtr, Offset = 0, Size = _instancesBuffer.Size };
        traverseEntries[5] = new BindGroupEntry { Binding = 5, Buffer = _gridCellsBuffer!.BufferPtr, Offset = 0, Size = _gridCellsBuffer.Size };
        traverseEntries[6] = new BindGroupEntry { Binding = 6, Buffer = _gridIndicesBuffer!.BufferPtr, Offset = 0, Size = _gridIndicesBuffer.Size };
        traverseEntries[7] = new BindGroupEntry { Binding = 7, TextureView = _maskTexture!.ViewPtr };
        traverseEntries[8] = new BindGroupEntry { Binding = 8, TextureView = _bgCopyTexture!.ViewPtr };
        var traverseLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_traversePipeline, 0);
        var traverseBgDesc = new BindGroupDescriptor
        {
            Layout = traverseLayout,
            EntryCount = 9,
            Entries = traverseEntries
        };
        var traverseBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &traverseBgDesc);
        _context.Wgpu.BindGroupLayoutRelease(traverseLayout);

        // 3.5 Bind Group for Intersect Pipeline
        var intersectEntries = stackalloc BindGroupEntry[8];
        intersectEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        intersectEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _rayQueueBuffer.BufferPtr, Offset = 0, Size = _rayQueueBuffer.Size };
        intersectEntries[2] = new BindGroupEntry { Binding = 3, Buffer = _bvhBuffer.BufferPtr, Offset = 0, Size = _bvhBuffer.Size };
        intersectEntries[3] = new BindGroupEntry { Binding = 4, Buffer = _instancesBuffer!.BufferPtr, Offset = 0, Size = _instancesBuffer.Size };
        intersectEntries[4] = new BindGroupEntry { Binding = 8, TextureView = _bgCopyTexture!.ViewPtr };
        intersectEntries[5] = new BindGroupEntry { Binding = 9, Buffer = _linesBuffer!.BufferPtr, Offset = 0, Size = _linesBuffer.Size };
        intersectEntries[6] = new BindGroupEntry { Binding = 10, TextureView = _maskTexture.ViewPtr };
        intersectEntries[7] = new BindGroupEntry { Binding = 11, TextureView = destination.ViewPtr };
        var intersectLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_intersectPipeline, 0);
        var intersectBgDesc = new BindGroupDescriptor
        {
            Layout = intersectLayout,
            EntryCount = 8,
            Entries = intersectEntries
        };
        var intersectBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &intersectBgDesc);
        _context.Wgpu.BindGroupLayoutRelease(intersectLayout);

        // Write all sort parameters for all steps to _sortParamsBuffer once per frame
        var sortParamsArray = new GpuSortParamsAligned[153];
        int writeIdx = 0;
        for (uint stage = 2; stage <= queueSize; stage <<= 1)
        {
            for (uint step = stage >> 1; step > 0; step >>= 1)
            {
                sortParamsArray[writeIdx++] = new GpuSortParamsAligned { Stage = stage, Step = step };
            }
        }
        _sortParamsBuffer!.Write(new ReadOnlySpan<GpuSortParamsAligned>(sortParamsArray));

        // 4. Dispatch Compute Passes
        var passDesc = new ComputePassDescriptor();

        // Pass 0.1: Clear active pixel mask on the GPU
        var passClear = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(passClear, _clearMaskPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(passClear, 0, clearMaskBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(passClear, (width + 15) / 16, (height + 15) / 16, 1);
        _context.Wgpu.ComputePassEncoderEnd(passClear);
        _context.Wgpu.ComputePassEncoderRelease(passClear);

        // Pass 0.2: Bin shapes on the GPU
        var passBin = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(passBin, _binShapesPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(passBin, 0, binShapesBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(passBin, (gridCols + 15) / 16, (gridRows + 15) / 16, 1);
        _context.Wgpu.ComputePassEncoderEnd(passBin);
        _context.Wgpu.ComputePassEncoderRelease(passBin);

        // Pass 0: Init queue
        _context.Wgpu.CommandEncoderClearBuffer(encoder, _queueCounterBuffer.BufferPtr, 0, 4);
        var pass0 = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(pass0, _initQueuePipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass0, 0, initQueueBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass0, queueSize / 256, 1, 1);
        _context.Wgpu.ComputePassEncoderEnd(pass0);
        _context.Wgpu.ComputePassEncoderRelease(pass0);

        // Pass 1: Traversal
        var pass1 = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(pass1, _traversePipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass1, 0, traverseBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass1, (width + 7) / 8, (height + 7) / 8, 1);
        _context.Wgpu.ComputePassEncoderEnd(pass1);
        _context.Wgpu.ComputePassEncoderRelease(pass1);

        // Pass 2: Bitonic Sort
        // Loop stages and steps
        int stepIdx = 0;
        var sortEntries = stackalloc BindGroupEntry[3];
        sortEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        sortEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _rayQueueBuffer.BufferPtr, Offset = 0, Size = _rayQueueBuffer.Size };

        for (uint stage = 2; stage <= queueSize; stage <<= 1)
        {
            for (uint step = stage >> 1; step > 0; step >>= 1)
            {
                uint sortParamsOffset = (uint)(stepIdx * 256);
                stepIdx++;

                sortEntries[2] = new BindGroupEntry { Binding = 12, Buffer = _sortParamsBuffer.BufferPtr, Offset = sortParamsOffset, Size = (uint)sizeof(GpuSortParams) };

                var sortBgLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_sortPipeline, 0);
                var sortBgDesc = new BindGroupDescriptor
                {
                    Layout = sortBgLayout,
                    EntryCount = 3,
                    Entries = sortEntries
                };
                var sortBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &sortBgDesc);
                _context.Wgpu.BindGroupLayoutRelease(sortBgLayout);

                var sortPass = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
                _context.Wgpu.ComputePassEncoderSetPipeline(sortPass, _sortPipeline);
                _context.Wgpu.ComputePassEncoderSetBindGroup(sortPass, 0, sortBindGroup, 0, null);
                _context.Wgpu.ComputePassEncoderDispatchWorkgroups(sortPass, queueSize / 256, 1, 1);
                _context.Wgpu.ComputePassEncoderEnd(sortPass);
                _context.Wgpu.ComputePassEncoderRelease(sortPass);

                _context.Wgpu.BindGroupRelease(sortBindGroup);
            }
        }

        // Pass 3: Intersection & Blending
        var pass3 = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(pass3, _intersectPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(pass3, 0, intersectBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass3, queueSize / 256, 1, 1);
        _context.Wgpu.ComputePassEncoderEnd(pass3);
        _context.Wgpu.ComputePassEncoderRelease(pass3);

        // Cleanup local Bind Groups
        _context.Wgpu.BindGroupRelease(clearMaskBindGroup);
        _context.Wgpu.BindGroupRelease(binShapesBindGroup);
        _context.Wgpu.BindGroupRelease(initQueueBindGroup);
        _context.Wgpu.BindGroupRelease(traverseBindGroup);
        _context.Wgpu.BindGroupRelease(intersectBindGroup);
    }

    private void UpdateGpuBuffers(uint width, uint height, uint gridStride, uint cellCount, uint indexCount)
    {
        uint maxQueueSize = 131072;

        _bvhBuffer?.Dispose();
        _bvhBuffer = new GpuBuffer(_context, (uint)(_bvhNodes.Count * sizeof(GpuBvhNode)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront BVH Nodes Buffer");

        _linesBuffer?.Dispose();
        _linesBuffer = new GpuBuffer(_context, (uint)(_lineSegments.Count * sizeof(GpuLineSegment)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Lines Buffer");

        _instancesBuffer?.Dispose();
        _instancesBuffer = new GpuBuffer(_context, (uint)(_frameInstances.Count * sizeof(GpuShapeInstance)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Instances Buffer");

        _gridCellsBuffer?.Dispose();
        _gridCellsBuffer = new GpuBuffer(_context, (uint)(cellCount * sizeof(GpuGridCell)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Grid Cells Buffer");

        _gridIndicesBuffer?.Dispose();
        _gridIndicesBuffer = new GpuBuffer(_context, (uint)(indexCount * sizeof(uint)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Grid Indices Buffer");

        if (_rayQueueBuffer == null)
        {
            _rayQueueBuffer = new GpuBuffer(_context, (uint)(maxQueueSize * sizeof(RayState)), BufferUsage.Storage, "Wavefront Ray Queue Buffer");
            _queueCounterBuffer = new GpuBuffer(_context, 4, BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, "Wavefront Queue Counter Buffer");
            _uniformsBuffer = new GpuBuffer(_context, (uint)sizeof(GpuWavefrontUniforms), BufferUsage.Uniform | BufferUsage.CopyDst, "Wavefront Uniforms Buffer");
            _sortParamsBuffer = new GpuBuffer(_context, 153 * 256, BufferUsage.Uniform | BufferUsage.CopyDst, "Wavefront Sort Params Buffer");
        }

        if (_maskTexture == null || _maskTexture.Width != width || _maskTexture.Height != height)
        {
            _maskTexture?.Dispose();
            _maskTexture = new GpuTexture(_context, width, height, TextureFormat.R32Uint,
                TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopyDst,
                "Wavefront Mask Texture");
        }

        if (_bgCopyTexture == null || _bgCopyTexture.Width != width || _bgCopyTexture.Height != height)
        {
            _bgCopyTexture?.Dispose();
            _bgCopyTexture = new GpuTexture(_context, width, height, TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst,
                "Wavefront Background Copy Texture");
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
        _bgCopyTexture?.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~WavefrontVectorEngine()
    {
        Dispose();
    }
}
