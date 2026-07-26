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
    public Vector4 WindAndDeformation;
    public Vector4 WeatherAndTimeOfDay;
    public Vector4 CameraForwardAndTanHalfFov;
    public Vector4 CameraRightAndAspect;
    public Vector4 CameraUpAndMaxSteps;
    public Vector4 VolumeOrigin;
    public Vector4 VolumeSize;
}

public enum VoxelRenderMode
{
    Rasterized,
    RayTraced
}

public sealed class VoxelMaterialEffectDefinition
{
    public VoxelMaterialEffectDefinition(string key, string source)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A stable voxel material effect key is required.", nameof(key));
        }
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Voxel material WGSL source is required.", nameof(source));
        }
        Key = key;
        Source = source;
        StableSourceHash = ComputeStableHash(source);
    }

    public string Key { get; }
    public string Source { get; }
    public bool IsFailed { get; internal set; }
    public string? LastError { get; internal set; }
    internal ulong StableSourceHash { get; }

    private static ulong ComputeStableHash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }
}

public static class VoxelMaterialEffects
{
    public static readonly VoxelMaterialEffectDefinition None = new(
        "none_v1",
        ShaderResource.Load(typeof(VoxelMaterialEffects), "VoxelMaterialNone.wgsl"));

    public static readonly VoxelMaterialEffectDefinition DynamicEnvironment = new(
        "dynamic_environment_v1",
        ShaderResource.Load(typeof(VoxelMaterialEffects), "VoxelMaterialDynamicEnvironment.wgsl"));
}

/// <summary>
/// Dense, immutable-for-a-version voxel occupancy used by the portable WGSL DDA renderer.
/// X is the fastest-moving axis, followed by Z and then Y.
/// </summary>
public sealed class VoxelRayTracingVolume
{
    public required uint[] Blocks { get; init; }
    public required int OriginX { get; init; }
    public required int OriginY { get; init; }
    public required int OriginZ { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Depth { get; init; }
    public required int ContentVersion { get; set; }

    public int CellCount => checked(Width * Height * Depth);

    public bool TrySetBlock(int worldX, int worldY, int worldZ, uint material)
    {
        var x = worldX - OriginX;
        var y = worldY - OriginY;
        var z = worldZ - OriginZ;
        if ((uint)x >= Width || (uint)y >= Height || (uint)z >= Depth)
        {
            return false;
        }

        Blocks[x + Width * (z + Depth * y)] = material;
        unchecked
        {
            ContentVersion++;
        }
        return true;
    }
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
    public VoxelRenderMode RenderMode { get; set; }
    public VoxelMaterialEffectDefinition MaterialEffect { get; set; } = VoxelMaterialEffects.None;
    public Vector2 WindDirection { get; set; } = Vector2.Normalize(new Vector2(0.8f, 0.3f));
    public float WindStrength { get; set; }
    public float DeformationStrength { get; set; }
    public float RainIntensity { get; set; }
    public float Wetness { get; set; }
    public float TimeOfDay { get; set; } = 0.35f;
    public Vector3 CameraForward { get; set; } = -Vector3.UnitZ;
    public float VerticalFieldOfView { get; set; } = 70f * MathF.PI / 180f;
    public float AspectRatio { get; set; } = 1f;
    public int RayTracingMaxSteps { get; set; } = 192;
    public VoxelRayTracingVolume? RayTracingVolume { get; set; }
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
        public GpuBuffer? RayTracingVolumeBuffer;
        public BindGroup* RayTracingBindGroup;
        public nint RayTracingBindGroupPipeline;
        public VoxelRayTracingVolume? UploadedRayTracingVolume;
        public int UploadedRayTracingVersion = int.MinValue;
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
            RayTracingVolumeBuffer?.Dispose();
            if (BindGroup != null)
            {
                context.Api.BindGroupRelease(BindGroup);
                BindGroup = null;
            }
            if (RayTracingBindGroup != null)
            {
                context.Api.BindGroupRelease(RayTracingBindGroup);
                RayTracingBindGroup = null;
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
    private readonly Dictionary<string, nint> _terrainPipelines = new();
    private RenderPipeline* _rayTracingPipeline;

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
            payload.ColorTexture is null ||
            (payload.RenderMode == VoxelRenderMode.Rasterized &&
             (payload.Chunks.Count == 0 || payload.DepthTexture is null)) ||
            (payload.RenderMode == VoxelRenderMode.RayTraced &&
             payload.RayTracingVolume is null))
        {
            return;
        }

        _context = compositor.Context;
        var context = compositor.Context;
        var wgpu = context.Api;
        var sampleCount = payload.RenderMode == VoxelRenderMode.RayTraced
            ? 1u
            : payload.SampleCount is 1 or 4 ? payload.SampleCount : 1u;
        while (_viewportResources.Count <= _compileIndex)
        {
            _viewportResources.Add(new ViewportResource(context));
        }
        var resource = _viewportResources[_compileIndex];

        if (payload.RenderMode == VoxelRenderMode.Rasterized)
        {
            EnsureGeometryArena(context, resource, payload);
        }

        var uniforms = new GpuVoxelUniforms
        {
            Projection = cmd.Transform,
            View = cmd.CameraView,
            CameraAndTime = new Vector4(payload.CameraPosition, payload.Time),
            SunDirectionAndIntensity = new Vector4(payload.SunDirection, payload.SunIntensity),
            SkyColorAndFogStart = new Vector4(payload.SkyColor, payload.FogStart),
            FogEndAndAmbient = new Vector4(payload.FogEnd, payload.SkyAmbient, payload.GroundAmbient, 0f),
            SelectedBlock = new Vector4(payload.SelectedBlock, payload.HasSelectedBlock ? 1f : 0f),
            WindAndDeformation = new Vector4(
                payload.WindDirection,
                payload.WindStrength,
                payload.DeformationStrength),
            WeatherAndTimeOfDay = new Vector4(
                payload.RainIntensity,
                payload.Wetness,
                payload.TimeOfDay,
                0f),
            CameraForwardAndTanHalfFov = new Vector4(
                Vector3.Normalize(payload.CameraForward),
                MathF.Tan(payload.VerticalFieldOfView * 0.5f)),
            CameraRightAndAspect = new Vector4(
                GetCameraRight(payload.CameraForward),
                payload.AspectRatio),
            CameraUpAndMaxSteps = new Vector4(
                GetCameraUp(payload.CameraForward),
                Math.Clamp(payload.RayTracingMaxSteps, 1, 512)),
            VolumeOrigin = payload.RayTracingVolume is { } volume
                ? new Vector4(volume.OriginX, volume.OriginY, volume.OriginZ, 0f)
                : Vector4.Zero,
            VolumeSize = payload.RayTracingVolume is { } sizeVolume
                ? new Vector4(sizeVolume.Width, sizeVolume.Height, sizeVolume.Depth, 0f)
                : Vector4.Zero
        };
        if (!resource.HasUniforms || !UniformsEqual(resource.LastUniforms, uniforms))
        {
            resource.UniformsBuffer.WriteSingle(uniforms);
            resource.LastUniforms = uniforms;
            resource.HasUniforms = true;
        }

        if (payload.RenderMode == VoxelRenderMode.RayTraced)
        {
            var pipeline = GetOrCreateRayTracingPipeline(compositor);
            EnsureRayTracingVolume(context, resource, payload.RayTracingVolume!);
            EnsureRayTracingBindGroup(context, resource, pipeline);
            EncodeRayTracingPass(context, resource, pipeline, payload);
        }
        else
        {
            var pipeline = GetOrCreatePipeline(compositor, sampleCount, payload.MaterialEffect);
            EnsureBindGroup(context, resource, pipeline);
            EncodeOffscreenPass(context, resource, pipeline, payload);
        }

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

        compositor.Context.Api.QueueSubmit(
            compositor.Context.Queue,
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

    private RenderPipeline* GetOrCreatePipeline(
        Compositor compositor,
        uint sampleCount,
        VoxelMaterialEffectDefinition materialEffect)
    {
        materialEffect ??= VoxelMaterialEffects.None;
        if (materialEffect.IsFailed && !ReferenceEquals(materialEffect, VoxelMaterialEffects.None))
        {
            return GetOrCreatePipeline(compositor, sampleCount, VoxelMaterialEffects.None);
        }

        var sourceHash = materialEffect.StableSourceHash;
        var cacheKey = $"{materialEffect.Key}_{sourceHash:x16}_{sampleCount}";
        if (_terrainPipelines.TryGetValue(cacheKey, out var cachedHandle))
        {
            return (RenderPipeline*)cachedHandle;
        }

        var fullShader = string.Concat(ShaderCode, "\n", materialEffect.Source);
        var shader = compositor.PipelineCache.GetOrCreateShader(
            $"VoxelTerrainShader_v3_{cacheKey}",
            fullShader,
            "Voxel terrain shader");
        var verification = compositor.Context.GetShaderModuleVerificationStatus(shader, out var errors);
        if (verification == ShaderModuleVerificationStatus.Invalid)
        {
            return FailMaterialEffect(compositor, sampleCount, materialEffect, cacheKey, errors);
        }

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
            var pipelineFailed = false;
            string? pipelineError = null;
            Action<ErrorType, string> pipelineErrorHandler = (_, message) =>
            {
                pipelineFailed = true;
                pipelineError = message;
            };
            WgpuContext.OnWebGpuError += pipelineErrorHandler;
            RenderPipeline* cached;
            try
            {
                cached = compositor.PipelineCache.GetOrCreateRenderPipeline(
                    $"VoxelTerrainPipeline_v3_{cacheKey}",
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
                compositor.Context.WaitIdle();
            }
            finally
            {
                WgpuContext.OnWebGpuError -= pipelineErrorHandler;
            }

            if (pipelineFailed || cached == null)
            {
                return FailMaterialEffect(
                    compositor,
                    sampleCount,
                    materialEffect,
                    cacheKey,
                    string.IsNullOrWhiteSpace(pipelineError)
                        ? "Voxel material render pipeline creation failed."
                        : pipelineError);
            }

            _terrainPipelines[cacheKey] = (nint)cached;
            return cached;
        }
    }

    private RenderPipeline* FailMaterialEffect(
        Compositor compositor,
        uint sampleCount,
        VoxelMaterialEffectDefinition materialEffect,
        string cacheKey,
        string error)
    {
        materialEffect.IsFailed = true;
        materialEffect.LastError = error;
        compositor.PipelineCache.ReleaseRenderPipeline($"VoxelTerrainPipeline_v3_{cacheKey}");
        compositor.PipelineCache.ReleaseShader($"VoxelTerrainShader_v3_{cacheKey}");
        if (ReferenceEquals(materialEffect, VoxelMaterialEffects.None))
        {
            throw new InvalidOperationException($"The built-in voxel material shader failed: {error}");
        }
        return GetOrCreatePipeline(compositor, sampleCount, VoxelMaterialEffects.None);
    }

    private RenderPipeline* GetOrCreateRayTracingPipeline(Compositor compositor)
    {
        if (_rayTracingPipeline != null)
        {
            return _rayTracingPipeline;
        }

        var source = ShaderResource.Load(
            typeof(VoxelTerrainExtensionPipeline),
            "VoxelRayTracing.wgsl");
        var shader = compositor.PipelineCache.GetOrCreateShader(
            "VoxelRayTracingShader_v1",
            source,
            "Voxel ray tracing shader");
        _rayTracingPipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
            "VoxelRayTracingPipeline_v1",
            shader,
            targetFormat: TextureFormat.Rgba8Unorm,
            topology: PrimitiveTopology.TriangleList,
            vertexBufferLayouts: null,
            enableBlend: false,
            enableDepthStencil: false,
            sampleCount: 1);
        return _rayTracingPipeline;
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

    private static void EnsureRayTracingVolume(
        WgpuContext context,
        ViewportResource resource,
        VoxelRayTracingVolume volume)
    {
        if (volume.Blocks.Length != volume.CellCount)
        {
            throw new InvalidOperationException("Voxel ray-tracing volume dimensions do not match its block array.");
        }

        var requiredBytes = checked((uint)Math.Max(1, volume.Blocks.Length) * sizeof(uint));
        if (resource.RayTracingVolumeBuffer is null ||
            resource.RayTracingVolumeBuffer.Size < requiredBytes)
        {
            resource.RayTracingVolumeBuffer?.Dispose();
            resource.RayTracingVolumeBuffer = new GpuBuffer(
                context,
                Math.Max(256u, NextPowerOfTwo(requiredBytes)),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Voxel ray-tracing volume");
            resource.UploadedRayTracingVolume = null;
            resource.RayTracingBindGroupPipeline = 0;
        }

        if (!ReferenceEquals(resource.UploadedRayTracingVolume, volume) ||
            resource.UploadedRayTracingVersion != volume.ContentVersion)
        {
            resource.RayTracingVolumeBuffer.Write(volume.Blocks);
            resource.UploadedRayTracingVolume = volume;
            resource.UploadedRayTracingVersion = volume.ContentVersion;
        }
    }

    private static void EnsureRayTracingBindGroup(
        WgpuContext context,
        ViewportResource resource,
        RenderPipeline* pipeline)
    {
        if (resource.RayTracingBindGroup != null &&
            resource.RayTracingBindGroupPipeline == (nint)pipeline)
        {
            return;
        }

        if (resource.RayTracingBindGroup != null)
        {
            context.Api.BindGroupRelease(resource.RayTracingBindGroup);
        }
        resource.RayTracingBindGroupPipeline = (nint)pipeline;

        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry
        {
            Binding = 0,
            Buffer = resource.UniformsBuffer.BufferPtr,
            Size = resource.UniformsBuffer.Size
        };
        entries[1] = new BindGroupEntry
        {
            Binding = 1,
            Buffer = resource.RayTracingVolumeBuffer!.BufferPtr,
            Size = resource.RayTracingVolumeBuffer.Size
        };
        var layout = context.Api.RenderPipelineGetBindGroupLayout(pipeline, 0);
        var label = SilkMarshal.StringToPtr("Voxel ray-tracing bind group");
        var descriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 2,
            Entries = entries,
            Label = (byte*)label
        };
        resource.RayTracingBindGroup = context.Api.DeviceCreateBindGroup(context.Device, &descriptor);
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

    private void EncodeRayTracingPass(
        WgpuContext context,
        ViewportResource resource,
        RenderPipeline* pipeline,
        VoxelTerrainCompilationPayload payload)
    {
        var label = SilkMarshal.StringToPtr("Voxel ray-tracing encoder");
        var encoderDescriptor = new CommandEncoderDescriptor { Label = (byte*)label };
        var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
        SilkMarshal.Free(label);

        var colorAttachment = new RenderPassColorAttachment
        {
            View = payload.ColorTexture!.ViewPtr,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Silk.NET.WebGPU.Color
            {
                R = payload.SkyColor.X,
                G = payload.SkyColor.Y,
                B = payload.SkyColor.Z,
                A = 1.0
            }
        };
        var passDescriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment
        };
        var pass = context.Api.CommandEncoderBeginRenderPass(encoder, &passDescriptor);
        context.Api.RenderPassEncoderSetPipeline(pass, pipeline);
        context.Api.RenderPassEncoderSetBindGroup(pass, 0, resource.RayTracingBindGroup, 0, null);
        context.Api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
        context.Api.RenderPassEncoderEnd(pass);
        context.Api.RenderPassEncoderRelease(pass);

        var commandLabel = SilkMarshal.StringToPtr("Voxel ray-tracing command buffer");
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
        left.SelectedBlock == right.SelectedBlock &&
        left.WindAndDeformation == right.WindAndDeformation &&
        left.WeatherAndTimeOfDay == right.WeatherAndTimeOfDay &&
        left.CameraForwardAndTanHalfFov == right.CameraForwardAndTanHalfFov &&
        left.CameraRightAndAspect == right.CameraRightAndAspect &&
        left.CameraUpAndMaxSteps == right.CameraUpAndMaxSteps &&
        left.VolumeOrigin == right.VolumeOrigin &&
        left.VolumeSize == right.VolumeSize;

    private static Vector3 GetCameraRight(Vector3 forward)
    {
        var normalizedForward = Vector3.Normalize(forward);
        var right = Vector3.Cross(normalizedForward, Vector3.UnitY);
        return right.LengthSquared() > 0.000001f
            ? Vector3.Normalize(right)
            : Vector3.UnitX;
    }

    private static Vector3 GetCameraUp(Vector3 forward)
    {
        var right = GetCameraRight(forward);
        return Vector3.Normalize(Vector3.Cross(right, Vector3.Normalize(forward)));
    }

}
