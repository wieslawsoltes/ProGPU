using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Vector;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Scene.Extensions;

public sealed unsafe class BackdropMaterialExtensionPipeline : ICompositorExtension, IDisposable
{
    private const string CrossContextTextureErrorPrefix =
        "Backdrop material texture belongs to a different WebGPU context";

    private static readonly string ShaderCode = ShaderResource.Load(typeof(BackdropMaterialExtensionPipeline), "BackdropMaterial.wgsl");

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MaterialUniforms
    {
        public Vector4 TintColor;
        public Vector4 LuminosityColor;
        public Vector4 FallbackColor;
        public Vector4 NoiseColor;
        public Vector4 Material0;
        public Vector4 Material1;
        public Vector4 Geometry0;
        public Vector4 RadiiX;
        public Vector4 RadiiY;
        public Vector4 Flags0;
        public Vector4 SourceUvRect;
    }

    private struct MaterialGpuResources
    {
        public GpuBuffer UniformBuffer;
        public nint BindGroupPtr;
    }

    private readonly Dictionary<(bool IsOffscreen, GpuBlendMode BlendMode), nint> _cachedPipelines = new();
    private readonly Dictionary<Compositor.TextureCacheKey, Compositor.CachedBindGroup> _textureBindGroups = new();
    private readonly List<MaterialGpuResources> _pool = new();
    private WgpuContext? _contextRef;
    private GpuTexture? _transparentTexture;
    private BindGroupLayout* _materialBindGroupLayout;
    private BindGroupLayout* _textureBindGroupLayout;
    private PipelineLayout* _onscreenPipelineLayout;
    private PipelineLayout* _offscreenPipelineLayout;
    private int _usedCount;

    public void Compile(
        Compositor compositor,
        IRenderDataProvider? provider,
        Matrix4x4 transform,
        ref RenderCommand cmd)
    {
        if (cmd.DataParam is not BackdropMaterialParams parameters || parameters.Rect.IsEmpty)
        {
            return;
        }

        var rect = parameters.Rect;
        var opacity = compositor.ActiveOpacity;
        var color = new Vector4(1f, 1f, 1f, opacity);
        var v0 = Vector2.Transform(new Vector2(rect.X, rect.Y), transform);
        var v1 = Vector2.Transform(new Vector2(rect.Right, rect.Y), transform);
        var v2 = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), transform);
        var v3 = Vector2.Transform(new Vector2(rect.X, rect.Bottom), transform);

        var uv0 = Vector2.Zero;
        var uv1 = Vector2.UnitX;
        var uv2 = Vector2.One;
        var uv3 = Vector2.UnitY;
        if (parameters.SourceTexture != null &&
            parameters.SourceRect.Width > 0f &&
            parameters.SourceRect.Height > 0f)
        {
            var textureWidth = MathF.Max(1f, parameters.SourceTexture.Width);
            var textureHeight = MathF.Max(1f, parameters.SourceTexture.Height);
            var left = parameters.SourceRect.X / textureWidth;
            var top = parameters.SourceRect.Y / textureHeight;
            var right = parameters.SourceRect.Right / textureWidth;
            var bottom = parameters.SourceRect.Bottom / textureHeight;
            uv0 = new Vector2(left, top);
            uv1 = new Vector2(right, top);
            uv2 = new Vector2(right, bottom);
            uv3 = new Vector2(left, bottom);
        }

        if (compositor.ActiveClipRect.HasValue &&
            !QuadClipper.TryClipAxisAlignedQuad(
                compositor.ActiveClipRect.Value,
                ref v0,
                ref v1,
                ref v2,
                ref v3,
                ref uv0,
                ref uv1,
                ref uv2,
                ref uv3))
        {
            cmd.PointBufferOffset = compositor.VectorIndices.Count;
            cmd.PointBufferCount = 0;
            return;
        }

        var originalVertexCount = compositor.VectorVertices.Count;
        CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + 4);
        var vertices = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, 4);
        vertices[0] = new VectorVertex(v0, color, uv0);
        vertices[1] = new VectorVertex(v1, color, uv1);
        vertices[2] = new VectorVertex(v2, color, uv2);
        vertices[3] = new VectorVertex(v3, color, uv3);

        var originalIndexCount = compositor.VectorIndices.Count;
        CollectionsMarshal.SetCount(compositor.VectorIndices, originalIndexCount + 6);
        var indices = CollectionsMarshal.AsSpan(compositor.VectorIndices).Slice(originalIndexCount, 6);
        var vertexStart = (uint)originalVertexCount;
        indices[0] = vertexStart;
        indices[1] = vertexStart + 1;
        indices[2] = vertexStart + 2;
        indices[3] = vertexStart;
        indices[4] = vertexStart + 2;
        indices[5] = vertexStart + 3;

        cmd.PointBufferOffset = originalIndexCount;
        cmd.PointBufferCount = 6;
    }

    public void BeginFrame(Compositor compositor)
    {
        _usedCount = 0;
    }

    public void EndFrame(Compositor compositor)
    {
        var frame = compositor.FrameNumber;
        lock (_textureBindGroups)
        {
            Compositor.TextureCacheKey[]? keysToRemove = null;
            var keysToRemoveCount = 0;
            try
            {
                foreach (var entry in _textureBindGroups)
                {
                    if (frame - entry.Value.LastUsedFrame <= 120)
                    {
                        continue;
                    }

                    if (entry.Value.BindGroupPtr != 0 && !compositor.Context.IsDisposed)
                    {
                        QueueBindGroupRelease(compositor.Context, entry.Value.BindGroupPtr);
                    }

                    PooledRemovalBuffer.Add(
                        ref keysToRemove,
                        ref keysToRemoveCount,
                        _textureBindGroups.Count,
                        entry.Key);
                }

                for (var index = 0; index < keysToRemoveCount; index++)
                {
                    _textureBindGroups.Remove(keysToRemove![index]);
                }
            }
            finally
            {
                PooledRemovalBuffer.Return(keysToRemove, keysToRemoveCount);
            }
        }
    }

    public void Render(
        Compositor compositor,
        void* renderPassEncoder,
        bool isOffscreen,
        in Compositor.CompositorDrawCall drawCall)
    {
        if (drawCall.PointBufferCount <= 0 ||
            drawCall.DataParam is not BackdropMaterialParams parameters)
        {
            return;
        }

        EnsureLayouts(compositor);
        var requestedTexture = parameters.SourceTexture;
        if (requestedTexture != null && !ReferenceEquals(requestedTexture.Context, compositor.Context))
        {
            parameters.LastError = $"{CrossContextTextureErrorPrefix}. " +
                "Create or copy the texture in the compositor target context before rendering the material.";
            return;
        }

        if (parameters.LastError?.StartsWith(CrossContextTextureErrorPrefix, StringComparison.Ordinal) == true)
        {
            parameters.LastError = null;
        }

        var sourceTexture = requestedTexture ?? _transparentTexture!;
        var hasSource = requestedTexture != null && parameters.Source != BackdropMaterialSource.None;
        var useFallback = parameters.UseFallback ||
            (parameters.Source == BackdropMaterialSource.Texture && requestedTexture == null);
        var effectiveMask = drawCall.MaskTexture;
        var maskWidth = effectiveMask?.Width ?? compositor.CurrentCanvasPixelWidth;
        var maskHeight = effectiveMask?.Height ?? compositor.CurrentCanvasPixelHeight;
        var sourceUvRect = GetSourceUvRect(parameters, sourceTexture);

        var gpuResources = GetGpuResources(compositor);
        gpuResources.UniformBuffer.WriteSingle(new MaterialUniforms
        {
            TintColor = ClampColor(parameters.TintColor),
            LuminosityColor = ClampColor(parameters.LuminosityColor),
            FallbackColor = ClampColor(parameters.FallbackColor),
            NoiseColor = ClampColor(parameters.NoiseColor),
            Material0 = new Vector4(
                Clamp01(parameters.TintOpacity),
                Clamp01(parameters.LuminosityOpacity),
                Clamp01(parameters.MaterialOpacity),
                Clamp01(parameters.NoiseOpacity)),
            Material1 = new Vector4(
                Clamp(parameters.BlurRadius, 0f, 96f),
                Clamp(parameters.Saturation, 0f, 4f),
                (float)parameters.Kind,
                useFallback ? 1f : 0f),
            Geometry0 = new Vector4(
                MathF.Max(0.0001f, MathF.Abs(parameters.Rect.Width)),
                MathF.Max(0.0001f, MathF.Abs(parameters.Rect.Height)),
                MathF.Max(1f, maskWidth),
                MathF.Max(1f, maskHeight)),
            RadiiX = ClampRadii(parameters.CornerRadiiX),
            RadiiY = ClampRadii(parameters.CornerRadiiY),
            Flags0 = new Vector4(
                hasSource ? 1f : 0f,
                effectiveMask != null ? 1f : 0f,
                sourceTexture.AlphaMode == GpuTextureAlphaMode.Premultiplied ? 1f : 0f,
                0f),
            SourceUvRect = sourceUvRect
        });

        var pipelineKey = (isOffscreen, drawCall.BlendMode);
        if (!_cachedPipelines.TryGetValue(pipelineKey, out var pipelinePointer))
        {
            pipelinePointer = (nint)CreatePipeline(compositor, isOffscreen, drawCall.BlendMode);
            _cachedPipelines.Add(pipelineKey, pipelinePointer);
        }

        var textureBindGroup = GetTextureBindGroup(compositor, sourceTexture, parameters.SamplingMode, isOffscreen);
        var maskBindGroup = compositor.GetMaskBindGroup(effectiveMask, isOffscreen);
        var pass = (RenderPassEncoder*)renderPassEncoder;
        var wgpu = compositor.Context.Api;

        wgpu.RenderPassEncoderSetPipeline(pass, (RenderPipeline*)pipelinePointer);
        wgpu.RenderPassEncoderSetVertexBuffer(
            pass,
            0,
            compositor.VectorVertexBuffer.BufferPtr,
            0,
            compositor.VectorVertexBuffer.Size);
        wgpu.RenderPassEncoderSetIndexBuffer(
            pass,
            compositor.VectorIndexBuffer.BufferPtr,
            IndexFormat.Uint32,
            0,
            compositor.VectorIndexBuffer.Size);
        wgpu.RenderPassEncoderSetBindGroup(
            pass,
            0,
            isOffscreen ? compositor.VectorUniformBindGroupOffscreen : compositor.VectorUniformBindGroup,
            0,
            null);
        wgpu.RenderPassEncoderSetBindGroup(pass, 1, (BindGroup*)gpuResources.BindGroupPtr, 0, null);
        wgpu.RenderPassEncoderSetBindGroup(pass, 2, (BindGroup*)textureBindGroup.BindGroupPtr, 0, null);
        wgpu.RenderPassEncoderSetBindGroup(pass, 3, maskBindGroup, 0, null);
        wgpu.RenderPassEncoderDrawIndexed(
            pass,
            (uint)drawCall.PointBufferCount,
            1,
            (uint)drawCall.PointBufferOffset,
            0,
            0);
    }

    private void EnsureLayouts(Compositor compositor)
    {
        if (_materialBindGroupLayout != null)
        {
            return;
        }

        _contextRef = compositor.Context;
        var wgpu = _contextRef.Api;
        var device = _contextRef.Device;

        var materialEntry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.Uniform,
                HasDynamicOffset = false,
                MinBindingSize = 0
            }
        };
        var materialLayoutDescriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 1,
            Entries = &materialEntry
        };
        _materialBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &materialLayoutDescriptor);

        var textureEntries = stackalloc BindGroupLayoutEntry[2];
        textureEntries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering }
        };
        textureEntries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType = TextureSampleType.Float,
                ViewDimension = TextureViewDimension.Dimension2D,
                Multisampled = false
            }
        };
        var textureLayoutDescriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 2,
            Entries = textureEntries
        };
        _textureBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &textureLayoutDescriptor);

        var onscreenLayouts = stackalloc BindGroupLayout*[4];
        onscreenLayouts[0] = compositor.VectorUniformBindGroupLayout;
        onscreenLayouts[1] = _materialBindGroupLayout;
        onscreenLayouts[2] = _textureBindGroupLayout;
        onscreenLayouts[3] = compositor.MaskBindGroupLayout;
        var onscreenDescriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 4,
            BindGroupLayouts = onscreenLayouts
        };
        _onscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &onscreenDescriptor);

        var offscreenLayouts = stackalloc BindGroupLayout*[4];
        offscreenLayouts[0] = compositor.VectorUniformBindGroupLayoutOffscreen;
        offscreenLayouts[1] = _materialBindGroupLayout;
        offscreenLayouts[2] = _textureBindGroupLayout;
        offscreenLayouts[3] = compositor.MaskBindGroupLayoutOffscreen;
        var offscreenDescriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 4,
            BindGroupLayouts = offscreenLayouts
        };
        _offscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &offscreenDescriptor);

        _transparentTexture = new GpuTexture(
            _contextRef,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Backdrop Material Transparent Source",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        _transparentTexture.WritePixels<byte>(new byte[] { 0, 0, 0, 0 });
    }

    private MaterialGpuResources GetGpuResources(Compositor compositor)
    {
        if (_usedCount >= _pool.Count)
        {
            var uniformSize = (uint)Unsafe.SizeOf<MaterialUniforms>();
            var buffer = new GpuBuffer(
                compositor.Context,
                uniformSize,
                BufferUsage.Uniform | BufferUsage.CopyDst,
                $"Backdrop Material Uniforms {_pool.Count}");
            var entry = new BindGroupEntry
            {
                Binding = 0,
                Buffer = buffer.BufferPtr,
                Offset = 0,
                Size = uniformSize
            };
            var descriptor = new BindGroupDescriptor
            {
                Layout = _materialBindGroupLayout,
                EntryCount = 1,
                Entries = &entry
            };
            var bindGroup = compositor.Context.Api.DeviceCreateBindGroup(compositor.Context.Device, &descriptor);
            _pool.Add(new MaterialGpuResources
            {
                UniformBuffer = buffer,
                BindGroupPtr = (nint)bindGroup
            });
        }

        return _pool[_usedCount++];
    }

    private RenderPipeline* CreatePipeline(
        Compositor compositor,
        bool isOffscreen,
        GpuBlendMode blendMode)
    {
        var shader = compositor.PipelineCache.GetOrCreateShader(
            "BackdropMaterialShader",
            ShaderCode,
            "Backdrop Material WGSL Shader");
        Span<VertexAttribute> attributes = stackalloc VertexAttribute[3];
        attributes[0] = new VertexAttribute
        {
            Format = VertexFormat.Float32x2,
            Offset = 0,
            ShaderLocation = 0
        };
        attributes[1] = new VertexAttribute
        {
            Format = VertexFormat.Float32x4,
            Offset = 8,
            ShaderLocation = 1
        };
        attributes[2] = new VertexAttribute
        {
            Format = VertexFormat.Float32x2,
            Offset = 24,
            ShaderLocation = 2
        };

        fixed (VertexAttribute* attributesPointer = attributes)
        {
            var layout = new VertexBufferLayout
            {
                ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 3,
                Attributes = attributesPointer
            };
            return compositor.PipelineCache.GetOrCreateRenderPipeline(
                isOffscreen
                    ? $"BackdropMaterialPipeline_Offscreen_{blendMode}"
                    : $"BackdropMaterialPipeline_{blendMode}",
                shader,
                new ReadOnlySpan<VertexBufferLayout>(&layout, 1),
                topology: PrimitiveTopology.TriangleList,
                targetFormat: compositor.RenderFormat,
                sampleCount: isOffscreen ? 1u : compositor.Options.PrimarySampleCount,
                pipelineLayout: isOffscreen ? _offscreenPipelineLayout : _onscreenPipelineLayout,
                blendMode: blendMode,
                sourceAlphaMode: GpuTextureAlphaMode.Premultiplied);
        }
    }

    private Compositor.CachedBindGroup GetTextureBindGroup(
        Compositor compositor,
        GpuTexture texture,
        TextureSamplingMode samplingMode,
        bool isOffscreen)
    {
        var key = new Compositor.TextureCacheKey(
            texture.Id,
            texture.Generation,
            isOffscreen,
            samplingMode,
            maxAnisotropy: 1);
        lock (_textureBindGroups)
        {
            if (_textureBindGroups.TryGetValue(key, out var cached))
            {
                cached.LastUsedFrame = compositor.FrameNumber;
                return cached;
            }

            var entries = stackalloc BindGroupEntry[2];
            entries[0] = new BindGroupEntry
            {
                Binding = 0,
                Sampler = compositor.GetTextureSampler(samplingMode)
            };
            entries[1] = new BindGroupEntry
            {
                Binding = 1,
                TextureView = texture.ViewPtr
            };
            var descriptor = new BindGroupDescriptor
            {
                Layout = _textureBindGroupLayout,
                EntryCount = 2,
                Entries = entries
            };
            var bindGroup = compositor.Context.Api.DeviceCreateBindGroup(compositor.Context.Device, &descriptor);
            cached = new Compositor.CachedBindGroup((nint)bindGroup, compositor.FrameNumber);
            _textureBindGroups.Add(key, cached);
            return cached;
        }
    }

    private static float Clamp01(float value)
    {
        return Clamp(value, 0f, 1f);
    }

    private static float Clamp(float value, float minimum, float maximum)
    {
        return float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
    }

    private static Vector4 ClampColor(Vector4 color)
    {
        return new Vector4(
            Clamp01(color.X),
            Clamp01(color.Y),
            Clamp01(color.Z),
            Clamp01(color.W));
    }

    private static Vector4 ClampRadii(Vector4 radii)
    {
        return new Vector4(
            MathF.Max(0f, float.IsFinite(radii.X) ? radii.X : 0f),
            MathF.Max(0f, float.IsFinite(radii.Y) ? radii.Y : 0f),
            MathF.Max(0f, float.IsFinite(radii.Z) ? radii.Z : 0f),
            MathF.Max(0f, float.IsFinite(radii.W) ? radii.W : 0f));
    }

    private static Vector4 GetSourceUvRect(BackdropMaterialParams parameters, GpuTexture sourceTexture)
    {
        if (parameters.SourceTexture == null ||
            parameters.SourceRect.Width <= 0f ||
            parameters.SourceRect.Height <= 0f)
        {
            return new Vector4(0f, 0f, 1f, 1f);
        }

        var width = MathF.Max(1f, sourceTexture.Width);
        var height = MathF.Max(1f, sourceTexture.Height);
        return new Vector4(
            parameters.SourceRect.X / width,
            parameters.SourceRect.Y / height,
            parameters.SourceRect.Right / width,
            parameters.SourceRect.Bottom / height);
    }

    public void Dispose()
    {
        if (_contextRef == null || _contextRef.IsDisposed)
        {
            _pool.Clear();
            _textureBindGroups.Clear();
            _transparentTexture = null;
            return;
        }

        foreach (var resource in _pool)
        {
            if (resource.BindGroupPtr != 0)
            {
                QueueBindGroupRelease(_contextRef, resource.BindGroupPtr);
            }
            resource.UniformBuffer.Dispose();
        }
        _pool.Clear();

        foreach (var cached in _textureBindGroups.Values)
        {
            if (cached.BindGroupPtr != 0)
            {
                QueueBindGroupRelease(_contextRef, cached.BindGroupPtr);
            }
        }
        _textureBindGroups.Clear();

        _transparentTexture?.Dispose();
        _transparentTexture = null;

        if (_materialBindGroupLayout != null)
        {
            _contextRef.QueueBindGroupLayoutDisposal((nint)_materialBindGroupLayout);
            _materialBindGroupLayout = null;
        }
        if (_textureBindGroupLayout != null)
        {
            _contextRef.QueueBindGroupLayoutDisposal((nint)_textureBindGroupLayout);
            _textureBindGroupLayout = null;
        }
        if (_onscreenPipelineLayout != null)
        {
            _contextRef.QueuePipelineLayoutDisposal((nint)_onscreenPipelineLayout);
            _onscreenPipelineLayout = null;
        }
        if (_offscreenPipelineLayout != null)
        {
            _contextRef.QueuePipelineLayoutDisposal((nint)_offscreenPipelineLayout);
            _offscreenPipelineLayout = null;
        }
    }

    private static void QueueBindGroupRelease(WgpuContext context, nint bindGroupPointer)
    {
        context.QueueBindGroupDisposal(bindGroupPointer);
    }
}
