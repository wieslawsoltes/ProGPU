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
    public uint CurveCount;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
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

    private ComputePipeline* _flattenPipeline;
    private ComputePipeline* _binShapesPipeline;
    private ComputePipeline* _renderPipeline;

    // Global BVH Cache
    private readonly List<GpuBvhNode> _bvhNodes = new();
    private readonly List<GpuBezierCurve> _rawCurves = new();
    private uint _totalLineSegmentsCount = 0;
    
    // Cache maps to offsets inside _bvhNodes and _rawCurves
    private readonly Dictionary<PathCacheKey, (uint BvhOffset, uint CurveOffset, Vector2 Min, Vector2 Max)> _pathCache = new();
    private readonly Dictionary<(TtfFont Font, ushort GlyphId), (uint BvhOffset, uint CurveOffset, Vector2 Min, Vector2 Max)> _glyphCache = new();

    // GPU Buffers (Reallocated/resized when frame sizes grow)
    private GpuBuffer? _bvhBuffer;
    private GpuBuffer? _rawCurvesBuffer;
    private GpuBuffer? _linesBuffer;
    private GpuBuffer? _instancesBuffer;
    private GpuBuffer? _gridCellsBuffer;
    private GpuBuffer? _gridIndicesBuffer;
    private GpuBuffer? _uniformsBuffer;

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
        _flattenPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontFlatten", shaderModule, "flatten_curves");
        _binShapesPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontBin", shaderModule, "bin_shapes");
        _renderPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontRender", shaderModule, "wavefront_render");
    }

    private (uint BvhOffset, uint CurveOffset, Vector2 Min, Vector2 Max) GetOrAddPathGeometry(PathGeometry path)
    {
        int contentHash = PathAtlas.ComputeHash(path);
        var key = new PathCacheKey(contentHash, 1f);

        if (_pathCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var curves = BvhBuilder.GetPathCurves(path);
        var nodes = BvhBuilder.BuildBvh(curves, out var orderedCurves, out var totalLines);

        uint bvhOffset = (uint)_bvhNodes.Count;
        uint curveOffset = (uint)_rawCurves.Count;
        uint lineOffset = _totalLineSegmentsCount;

        // Bounding box
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in orderedCurves)
        {
            var (cMin, cMax) = BvhBuilder.GetCurveBounds(c);
            minX = Math.Min(minX, cMin.X);
            minY = Math.Min(minY, cMin.Y);
            maxX = Math.Max(maxX, cMax.X);
            maxY = Math.Max(maxY, cMax.Y);
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

        for (int i = 0; i < orderedCurves.Count; i++)
        {
            var curve = orderedCurves[i];
            curve.LineOffset += lineOffset;
            _rawCurves.Add(curve);
        }

        _totalLineSegmentsCount += totalLines;

        var boundsMin = new Vector2(minX, minY);
        var boundsMax = new Vector2(maxX, maxY);

        var result = (bvhOffset, curveOffset, boundsMin, boundsMax);
        _pathCache[key] = result;
        return result;
    }

    private (uint BvhOffset, uint CurveOffset, Vector2 Min, Vector2 Max) GetOrAddGlyphGeometry(TtfFont font, ushort glyphId)
    {
        var key = (font, glyphId);
        if (_glyphCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var curves = BvhBuilder.GetGlyphCurves(font, glyphId);
        var nodes = BvhBuilder.BuildBvh(curves, out var orderedCurves, out var totalLines);

        uint bvhOffset = (uint)_bvhNodes.Count;
        uint curveOffset = (uint)_rawCurves.Count;
        uint lineOffset = _totalLineSegmentsCount;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in orderedCurves)
        {
            var (cMin, cMax) = BvhBuilder.GetCurveBounds(c);
            minX = Math.Min(minX, cMin.X);
            minY = Math.Min(minY, cMin.Y);
            maxX = Math.Max(maxX, cMax.X);
            maxY = Math.Max(maxY, cMax.Y);
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

        for (int i = 0; i < orderedCurves.Count; i++)
        {
            var curve = orderedCurves[i];
            curve.LineOffset += lineOffset;
            _rawCurves.Add(curve);
        }

        _totalLineSegmentsCount += totalLines;

        var boundsMin = new Vector2(minX, minY);
        var boundsMax = new Vector2(maxX, maxY);

        var result = (bvhOffset, curveOffset, boundsMin, boundsMax);
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
        UpdateGpuBuffers(width, height, gridStride, cellCount, maxGridIndices, destination.Format);

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
        _rawCurvesBuffer!.Write(CollectionsMarshal.AsSpan(_rawCurves));
        _instancesBuffer!.Write(CollectionsMarshal.AsSpan(_frameInstances));

        // Write Uniforms
        var uniforms = new GpuWavefrontUniforms
        {
            ScreenWidth = width,
            ScreenHeight = height,
            GridStride = gridStride,
            InstanceCount = (uint)_frameInstances.Count,
            MaxQueueSize = 0, // Unused
            CurrentFrameIndex = _frameNumber,
            FontWeightOffset = fontWeightOffset,
            DpiScale = dpiScale,
            CurveCount = (uint)_rawCurves.Count
        };
        _uniformsBuffer!.WriteSingle(uniforms);

        // 3. Bind Groups

        // 3.0 Bind Group for Flatten Curves Pipeline
        var flattenEntries = stackalloc BindGroupEntry[3];
        flattenEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        flattenEntries[1] = new BindGroupEntry { Binding = 12, Buffer = _rawCurvesBuffer!.BufferPtr, Offset = 0, Size = _rawCurvesBuffer.Size };
        flattenEntries[2] = new BindGroupEntry { Binding = 9, Buffer = _linesBuffer!.BufferPtr, Offset = 0, Size = _linesBuffer.Size };
        var flattenLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_flattenPipeline, 0);
        var flattenBgDesc = new BindGroupDescriptor
        {
            Layout = flattenLayout,
            EntryCount = 3,
            Entries = flattenEntries
        };
        var flattenBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &flattenBgDesc);
        _context.Wgpu.BindGroupLayoutRelease(flattenLayout);
        
        // 3.1 Bind Group for Bin Shapes Pipeline
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

        // 3.2 Bind Group for Render Pipeline
        var renderEntries = stackalloc BindGroupEntry[8];
        renderEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformsBuffer.BufferPtr, Offset = 0, Size = _uniformsBuffer.Size };
        renderEntries[1] = new BindGroupEntry { Binding = 3, Buffer = _bvhBuffer.BufferPtr, Offset = 0, Size = _bvhBuffer.Size };
        renderEntries[2] = new BindGroupEntry { Binding = 4, Buffer = _instancesBuffer.BufferPtr, Offset = 0, Size = _instancesBuffer.Size };
        renderEntries[3] = new BindGroupEntry { Binding = 5, Buffer = _gridCellsBuffer.BufferPtr, Offset = 0, Size = _gridCellsBuffer.Size };
        renderEntries[4] = new BindGroupEntry { Binding = 6, Buffer = _gridIndicesBuffer.BufferPtr, Offset = 0, Size = _gridIndicesBuffer.Size };
        renderEntries[5] = new BindGroupEntry { Binding = 8, TextureView = _bgCopyTexture.ViewPtr };
        renderEntries[6] = new BindGroupEntry { Binding = 9, Buffer = _linesBuffer.BufferPtr, Offset = 0, Size = _linesBuffer.Size };
        renderEntries[7] = new BindGroupEntry { Binding = 11, TextureView = destination.ViewPtr };
        var renderLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_renderPipeline, 0);
        var renderBgDesc = new BindGroupDescriptor
        {
            Layout = renderLayout,
            EntryCount = 8,
            Entries = renderEntries
        };
        var renderBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &renderBgDesc);
        _context.Wgpu.BindGroupLayoutRelease(renderLayout);

        // 4. Dispatch Compute Passes
        var passDesc = new ComputePassDescriptor();

        // Pass 0: Flatten curves on the GPU
        var passFlatten = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(passFlatten, _flattenPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(passFlatten, 0, flattenBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(passFlatten, ((uint)_rawCurves.Count + 63) / 64, 1, 1);
        _context.Wgpu.ComputePassEncoderEnd(passFlatten);
        _context.Wgpu.ComputePassEncoderRelease(passFlatten);

        // Pass 1: Bin shapes on the GPU
        var passBin = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(passBin, _binShapesPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(passBin, 0, binShapesBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(passBin, (gridCols + 15) / 16, (gridRows + 15) / 16, 1);
        _context.Wgpu.ComputePassEncoderEnd(passBin);
        _context.Wgpu.ComputePassEncoderRelease(passBin);

        // Pass 2: Render directly to destination
        var passRender = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Wgpu.ComputePassEncoderSetPipeline(passRender, _renderPipeline);
        _context.Wgpu.ComputePassEncoderSetBindGroup(passRender, 0, renderBindGroup, 0, null);
        _context.Wgpu.ComputePassEncoderDispatchWorkgroups(passRender, (width + 15) / 16, (height + 15) / 16, 1);
        _context.Wgpu.ComputePassEncoderEnd(passRender);
        _context.Wgpu.ComputePassEncoderRelease(passRender);

        // Cleanup local Bind Groups
        _context.Wgpu.BindGroupRelease(flattenBindGroup);
        _context.Wgpu.BindGroupRelease(binShapesBindGroup);
        _context.Wgpu.BindGroupRelease(renderBindGroup);
    }

    private void UpdateGpuBuffers(uint width, uint height, uint gridStride, uint cellCount, uint indexCount, TextureFormat destFormat)
    {
        _bvhBuffer?.Dispose();
        _bvhBuffer = new GpuBuffer(_context, (uint)(_bvhNodes.Count * sizeof(GpuBvhNode)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront BVH Nodes Buffer");

        _rawCurvesBuffer?.Dispose();
        uint curvesBufferSize = Math.Max(1u, (uint)_rawCurves.Count) * (uint)sizeof(GpuBezierCurve);
        _rawCurvesBuffer = new GpuBuffer(_context, curvesBufferSize, BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Raw Curves Buffer");

        _linesBuffer?.Dispose();
        uint linesBufferSize = Math.Max(1u, _totalLineSegmentsCount) * (uint)sizeof(GpuLineSegment);
        _linesBuffer = new GpuBuffer(_context, linesBufferSize, BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, "Wavefront Lines Buffer");

        _instancesBuffer?.Dispose();
        _instancesBuffer = new GpuBuffer(_context, (uint)(_frameInstances.Count * sizeof(GpuShapeInstance)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Instances Buffer");

        _gridCellsBuffer?.Dispose();
        _gridCellsBuffer = new GpuBuffer(_context, (uint)(cellCount * sizeof(GpuGridCell)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Grid Cells Buffer");

        _gridIndicesBuffer?.Dispose();
        _gridIndicesBuffer = new GpuBuffer(_context, (uint)(indexCount * sizeof(uint)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Grid Indices Buffer");

        if (_uniformsBuffer == null)
        {
            _uniformsBuffer = new GpuBuffer(_context, (uint)sizeof(GpuWavefrontUniforms), BufferUsage.Uniform | BufferUsage.CopyDst, "Wavefront Uniforms Buffer");
        }

        if (_bgCopyTexture == null || _bgCopyTexture.Width != width || _bgCopyTexture.Height != height || _bgCopyTexture.Format != destFormat)
        {
            _bgCopyTexture?.Dispose();
            _bgCopyTexture = new GpuTexture(_context, width, height, destFormat,
                TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst,
                "Wavefront Background Copy Texture");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _bvhBuffer?.Dispose();
        _rawCurvesBuffer?.Dispose();
        _linesBuffer?.Dispose();
        _instancesBuffer?.Dispose();
        _gridCellsBuffer?.Dispose();
        _gridIndicesBuffer?.Dispose();
        _uniformsBuffer?.Dispose();
        _bgCopyTexture?.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~WavefrontVectorEngine()
    {
        Dispose();
    }
}
