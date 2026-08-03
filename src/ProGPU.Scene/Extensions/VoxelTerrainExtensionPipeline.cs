using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Vector;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Scene.Extensions;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuVoxelVertex
{
    public Vector3 Position;
    public Vector2 TextureCoordinate;
    public uint Material;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuVoxelUniforms
{
    public Matrix4x4 Projection;
    public Matrix4x4 View;
    public Vector4 CameraAndTime;
    public Vector4 SunDirectionAndIntensity;
    public Vector4 SkyColorAndFogStart;
    public Vector4 FogEndAndAmbient;
    public Vector4 SelectedBlock;
}

public sealed class VoxelTerrainCompilationPayload
{
    public List<VoxelChunkRenderEntry> Chunks { get; } = new();
    public GpuTexture? ColorTexture { get; set; }
    public GpuTexture? MsaaColorTexture { get; set; }
    public GpuTexture? DepthTexture { get; set; }
    public uint SampleCount { get; set; } = 1;
    public Vector3 CameraPosition { get; set; }
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.8f, -0.3f));
    public float SunIntensity { get; set; } = 0.85f;
    public Vector3 SkyColor { get; set; } = new(0.48f, 0.72f, 0.92f);
    public float FogStart { get; set; } = 48f;
    public float FogEnd { get; set; } = 92f;
    public float SkyAmbient { get; set; } = 0.62f;
    public float GroundAmbient { get; set; } = 0.38f;
    public float Time { get; set; }
    public Vector3 SelectedBlock { get; set; }
    public bool HasSelectedBlock { get; set; }
}

public sealed class VoxelChunkRenderEntry
{
    public object? Geometry { get; set; }
    public int GeometryVersion { get; set; }
    public GpuVoxelVertex[] Vertices { get; set; } = Array.Empty<GpuVoxelVertex>();
    public uint[] Indices { get; set; } = Array.Empty<uint>();
    public Vector3 Origin { get; set; }
}

/// <summary>
/// Indexed, versioned chunk renderer. Visible chunk geometry is packed into a shared arena
/// and submitted with one draw. Stable layouts reuse the arena without geometry uploads.
/// </summary>
public sealed unsafe class VoxelTerrainExtensionPipeline : ICompositorExtension, IDisposable
{
    private static readonly string ShaderCode =
        ShaderResource.Load(typeof(VoxelTerrainExtensionPipeline), "VoxelTerrain.wgsl");

    private readonly record struct ArenaSlice(
        object? Geometry,
        int Version,
        Vector3 Origin,
        int VertexCount,
        int IndexCount);

    private sealed class ViewportResource
    {
        public readonly GpuBuffer UniformsBuffer;
        public GpuBuffer? VertexArena;
        public GpuBuffer? IndexArena;
        public BindGroup* BindGroup;
        public nint BindGroupPipeline;
        public GpuVoxelVertex[] CpuVertices = Array.Empty<GpuVoxelVertex>();
        public uint[] CpuIndices = Array.Empty<uint>();
        public readonly List<ArenaSlice> ArenaSlices = new();
        public uint ArenaIndexCount;
        public GpuVoxelUniforms LastUniforms;
        public bool HasUniforms;

        public ViewportResource(WgpuContext context)
        {
            UniformsBuffer = new GpuBuffer(
                context,
                (uint)Marshal.SizeOf<GpuVoxelUniforms>(),
                BufferUsage.Uniform | BufferUsage.CopyDst,
                "Voxel uniforms");
        }

        public void EnsureGeometryCpuCapacity(int vertexCount, int indexCount)
        {
            if (CpuVertices.Length < vertexCount)
            {
                CpuVertices = new GpuVoxelVertex[NextArrayCapacity(vertexCount)];
            }
            if (CpuIndices.Length < indexCount)
            {
                CpuIndices = new uint[NextArrayCapacity(indexCount)];
            }
        }

        public void Dispose(WgpuContext context)
        {
            UniformsBuffer.Dispose();
            VertexArena?.Dispose();
            IndexArena?.Dispose();
            if (BindGroup != null)
            {
                context.Api.BindGroupRelease(BindGroup);
                BindGroup = null;
            }
        }

        private static int NextArrayCapacity(int required)
        {
            var capacity = 256;
            while (capacity < required)
            {
                capacity = checked(capacity * 2);
            }
            return capacity;
        }
    }

    private readonly List<ViewportResource> _viewportResources = new();
    private readonly List<nint> _pendingCommandBuffers = new();
    private WgpuContext? _context;
    private int _compileIndex;
    private RenderPipeline* _pipelineSingle;
    private RenderPipeline* _pipelineMsaa;

    public void BeginFrame(Compositor compositor)
    {
        _compileIndex = 0;
        ReleasePendingCommandBuffers(compositor.Context);
    }

    public void Compile(
        Compositor compositor,
        IRenderDataProvider? provider,
        Matrix4x4 transform,
        ref RenderCommand cmd)
    {
        if (cmd.DataParam is not VoxelTerrainCompilationPayload payload ||
            payload.Chunks.Count == 0 ||
            payload.ColorTexture is null ||
            payload.DepthTexture is null)
        {
            return;
        }

        _context = compositor.Context;
        var context = compositor.Context;
        var wgpu = context.Api;
        var sampleCount = payload.SampleCount is 1 or 4 ? payload.SampleCount : 1u;
        while (_viewportResources.Count <= _compileIndex)
        {
            _viewportResources.Add(new ViewportResource(context));
        }
        var resource = _viewportResources[_compileIndex];

        EnsureGeometryArena(context, resource, payload);

        var uniforms = new GpuVoxelUniforms
        {
            Projection = cmd.Transform,
            View = cmd.CameraView,
            CameraAndTime = new Vector4(payload.CameraPosition, payload.Time),
            SunDirectionAndIntensity = new Vector4(payload.SunDirection, payload.SunIntensity),
            SkyColorAndFogStart = new Vector4(payload.SkyColor, payload.FogStart),
            FogEndAndAmbient = new Vector4(payload.FogEnd, payload.SkyAmbient, payload.GroundAmbient, 0f),
            SelectedBlock = new Vector4(payload.SelectedBlock, payload.HasSelectedBlock ? 1f : 0f)
        };
        if (!resource.HasUniforms || !UniformsEqual(resource.LastUniforms, uniforms))
        {
            resource.UniformsBuffer.WriteSingle(uniforms);
            resource.LastUniforms = uniforms;
            resource.HasUniforms = true;
        }

        var pipeline = GetOrCreatePipeline(compositor, sampleCount);
        EnsureBindGroup(context, resource, pipeline);
        EncodeOffscreenPass(context, resource, pipeline, payload);

        _compileIndex++;
        cmd.PointBufferOffset = 0;
        cmd.PointBufferCount = 0;
    }

    public void EndFrame(Compositor compositor)
    {
        if (_pendingCommandBuffers.Count == 0)
        {
            return;
        }

        var buffers = stackalloc CommandBuffer*[_pendingCommandBuffers.Count];
        for (var index = 0; index < _pendingCommandBuffers.Count; index++)
        {
            buffers[index] = (CommandBuffer*)_pendingCommandBuffers[index];
        }

        compositor.Context.Submit(
            (uint)_pendingCommandBuffers.Count,
            buffers);
        ReleasePendingCommandBuffers(compositor.Context);
    }

    public void Render(
        Compositor compositor,
        void* renderPassEncoder,
        bool isOffscreen,
        in Compositor.CompositorDrawCall dc)
    {
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            foreach (var viewport in _viewportResources)
            {
                viewport.Dispose(_context);
            }
            ReleasePendingCommandBuffers(_context);
        }
        _viewportResources.Clear();
    }

    private RenderPipeline* GetOrCreatePipeline(Compositor compositor, uint sampleCount)
    {
        var cached = sampleCount == 1 ? _pipelineSingle : _pipelineMsaa;
        if (cached != null)
        {
            return cached;
        }

        var shader = compositor.PipelineCache.GetOrCreateShader(
            "VoxelTerrainShader_v2",
            ShaderCode,
            "Voxel terrain shader");
        Span<VertexAttribute> attributes = stackalloc VertexAttribute[3];
        attributes[0] = new VertexAttribute
        {
            Format = VertexFormat.Float32x3,
            Offset = 0,
            ShaderLocation = 0
        };
        attributes[1] = new VertexAttribute
        {
            Format = VertexFormat.Float32x2,
            Offset = 12,
            ShaderLocation = 1
        };
        attributes[2] = new VertexAttribute
        {
            Format = VertexFormat.Uint32,
            Offset = 20,
            ShaderLocation = 2
        };
        Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];
        fixed (VertexAttribute* attributesPointer = attributes)
        {
            layouts[0] = new VertexBufferLayout
            {
                ArrayStride = (uint)Unsafe.SizeOf<GpuVoxelVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 3,
                Attributes = attributesPointer
            };
            cached = compositor.PipelineCache.GetOrCreateRenderPipeline(
                $"VoxelTerrainPipeline_v2_{sampleCount}",
                shader,
                layouts,
                topology: PrimitiveTopology.TriangleList,
                targetFormat: TextureFormat.Rgba8Unorm,
                enableBlend: false,
                enableDepthStencil: true,
                depthFormat: TextureFormat.Depth24Plus,
                sampleCount: sampleCount,
                depthWriteEnabled: true,
                depthCompare: CompareFunction.LessEqual,
                cullMode: CullMode.Back);
        }

        if (sampleCount == 1) _pipelineSingle = cached;
        else _pipelineMsaa = cached;
        return cached;
    }

    private static void EnsureBindGroup(
        WgpuContext context,
        ViewportResource resource,
        RenderPipeline* pipeline)
    {
        if (resource.BindGroup != null && resource.BindGroupPipeline == (nint)pipeline)
        {
            return;
        }

        resource.BindGroupPipeline = (nint)pipeline;
        if (resource.BindGroup != null)
        {
            context.Api.BindGroupRelease(resource.BindGroup);
        }

        var entries = stackalloc BindGroupEntry[1];
        entries[0] = new BindGroupEntry
        {
            Binding = 0,
            Buffer = resource.UniformsBuffer.BufferPtr,
            Offset = 0,
            Size = resource.UniformsBuffer.Size
        };
        var layout = context.Api.RenderPipelineGetBindGroupLayout(pipeline, 0);
        var label = SilkMarshal.StringToPtr("Voxel terrain bind group");
        var descriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 1,
            Entries = entries,
            Label = (byte*)label
        };
        resource.BindGroup = context.Api.DeviceCreateBindGroup(context.Device, &descriptor);
        SilkMarshal.Free(label);
    }

    private static bool EnsureGeometryArena(
        WgpuContext context,
        ViewportResource resource,
        VoxelTerrainCompilationPayload payload)
    {
        var layoutMatches = resource.ArenaSlices.Count == payload.Chunks.Count;
        if (layoutMatches)
        {
            for (var index = 0; index < payload.Chunks.Count; index++)
            {
                var entry = payload.Chunks[index];
                var slice = resource.ArenaSlices[index];
                if (!ReferenceEquals(entry.Geometry, slice.Geometry) ||
                    entry.GeometryVersion != slice.Version ||
                    entry.Origin != slice.Origin ||
                    entry.Vertices.Length != slice.VertexCount ||
                    entry.Indices.Length != slice.IndexCount)
                {
                    layoutMatches = false;
                    break;
                }
            }
        }

        if (layoutMatches)
        {
            return false;
        }

        var totalVertices = 0;
        var totalIndices = 0;
        foreach (var entry in payload.Chunks)
        {
            totalVertices = checked(totalVertices + entry.Vertices.Length);
            totalIndices = checked(totalIndices + entry.Indices.Length);
        }
        resource.EnsureGeometryCpuCapacity(totalVertices, totalIndices);
        resource.ArenaSlices.Clear();

        var vertexOffset = 0;
        var indexOffset = 0;
        foreach (var entry in payload.Chunks)
        {
            resource.ArenaSlices.Add(new ArenaSlice(
                entry.Geometry,
                entry.GeometryVersion,
                entry.Origin,
                entry.Vertices.Length,
                entry.Indices.Length));
            if (entry.Geometry is null || entry.Vertices.Length == 0 || entry.Indices.Length == 0)
            {
                continue;
            }

            for (var index = 0; index < entry.Vertices.Length; index++)
            {
                var vertex = entry.Vertices[index];
                vertex.Position += entry.Origin;
                resource.CpuVertices[vertexOffset + index] = vertex;
            }
            for (var index = 0; index < entry.Indices.Length; index++)
            {
                resource.CpuIndices[indexOffset + index] =
                    checked(entry.Indices[index] + (uint)vertexOffset);
            }
            vertexOffset += entry.Vertices.Length;
            indexOffset += entry.Indices.Length;
        }
        resource.ArenaIndexCount = (uint)indexOffset;

        var vertexBytes = checked((uint)vertexOffset * (uint)Unsafe.SizeOf<GpuVoxelVertex>());
        var indexBytes = checked((uint)indexOffset * sizeof(uint));
        if (resource.VertexArena is null || resource.VertexArena.Size < vertexBytes)
        {
            resource.VertexArena?.Dispose();
            resource.VertexArena = new GpuBuffer(
                context,
                Math.Max(256u, NextPowerOfTwo(vertexBytes)),
                BufferUsage.Vertex | BufferUsage.CopyDst,
                "Voxel vertex arena");
        }
        if (resource.IndexArena is null || resource.IndexArena.Size < indexBytes)
        {
            resource.IndexArena?.Dispose();
            resource.IndexArena = new GpuBuffer(
                context,
                Math.Max(256u, NextPowerOfTwo(indexBytes)),
                BufferUsage.Index | BufferUsage.CopyDst,
                "Voxel index arena");
        }

        resource.VertexArena.Write(resource.CpuVertices.AsSpan(0, vertexOffset));
        resource.IndexArena.Write(resource.CpuIndices.AsSpan(0, indexOffset));
        return true;
    }

    private void EncodeOffscreenPass(
        WgpuContext context,
        ViewportResource resource,
        RenderPipeline* pipeline,
        VoxelTerrainCompilationPayload payload)
    {
        var label = SilkMarshal.StringToPtr("Voxel offscreen encoder");
        var encoderDescriptor = new CommandEncoderDescriptor { Label = (byte*)label };
        var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
        SilkMarshal.Free(label);

        var colorAttachment = new RenderPassColorAttachment
        {
            View = payload.MsaaColorTexture is not null
                ? payload.MsaaColorTexture.ViewPtr
                : payload.ColorTexture!.ViewPtr,
            ResolveTarget = payload.MsaaColorTexture is not null
                ? payload.ColorTexture!.ViewPtr
                : null,
            LoadOp = LoadOp.Clear,
            StoreOp = payload.MsaaColorTexture is not null ? StoreOp.Discard : StoreOp.Store,
            ClearValue = new Silk.NET.WebGPU.Color
            {
                R = payload.SkyColor.X,
                G = payload.SkyColor.Y,
                B = payload.SkyColor.Z,
                A = 1.0
            }
        };
        var depthAttachment = new RenderPassDepthStencilAttachment
        {
            View = payload.DepthTexture!.ViewPtr,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
            DepthClearValue = 1.0f,
            DepthReadOnly = false,
            StencilLoadOp = LoadOp.Undefined,
            StencilStoreOp = StoreOp.Undefined,
            StencilClearValue = 0,
            StencilReadOnly = false
        };
        var passDescriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = &depthAttachment
        };
        var pass = context.Api.CommandEncoderBeginRenderPass(encoder, &passDescriptor);
        context.Api.RenderPassEncoderSetPipeline(pass, pipeline);
        context.Api.RenderPassEncoderSetBindGroup(pass, 0, resource.BindGroup, 0, null);
        context.Api.RenderPassEncoderSetVertexBuffer(
            pass,
            0,
            resource.VertexArena!.BufferPtr,
            0,
            resource.VertexArena.Size);
        context.Api.RenderPassEncoderSetIndexBuffer(
            pass,
            resource.IndexArena!.BufferPtr,
            IndexFormat.Uint32,
            0,
            resource.IndexArena.Size);

        if (resource.ArenaIndexCount > 0)
        {
            context.Api.RenderPassEncoderDrawIndexed(
                pass,
                resource.ArenaIndexCount,
                1,
                0,
                0,
                0);
        }

        context.Api.RenderPassEncoderEnd(pass);
        context.Api.RenderPassEncoderRelease(pass);
        var commandLabel = SilkMarshal.StringToPtr("Voxel command buffer");
        var commandDescriptor = new CommandBufferDescriptor { Label = (byte*)commandLabel };
        var commandBuffer = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free(commandLabel);
        context.Api.CommandEncoderRelease(encoder);
        _pendingCommandBuffers.Add((nint)commandBuffer);
    }

    private void ReleasePendingCommandBuffers(WgpuContext context)
    {
        foreach (var handle in _pendingCommandBuffers)
        {
            context.Api.CommandBufferRelease((CommandBuffer*)handle);
        }
        _pendingCommandBuffers.Clear();
    }

    private static uint NextPowerOfTwo(uint value)
    {
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }

    private static bool UniformsEqual(in GpuVoxelUniforms left, in GpuVoxelUniforms right) =>
        left.Projection == right.Projection &&
        left.View == right.View &&
        left.CameraAndTime == right.CameraAndTime &&
        left.SunDirectionAndIntensity == right.SunDirectionAndIntensity &&
        left.SkyColorAndFogStart == right.SkyColorAndFogStart &&
        left.FogEndAndAmbient == right.FogEndAndAmbient &&
        left.SelectedBlock == right.SelectedBlock;
}
