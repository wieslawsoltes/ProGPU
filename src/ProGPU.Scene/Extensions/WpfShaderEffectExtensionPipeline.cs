using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Backend;
using ProGPU.Vector;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Scene.Extensions;

public sealed unsafe class WpfShaderEffectExtensionPipeline : ICompositorExtension, IDisposable
{
    private static readonly string VertexAndHeaderShader = CreateVertexAndHeaderShader();

    private const string VertexAndHeaderShaderPrefix = @"
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct WpfEffectUniforms {
    constants: array<vec4<f32>, 32>,
    bounds: vec4<f32>,
    textureSize: vec4<f32>,
    metadata: vec4<f32>,
};

@group(1) @binding(0) var<uniform> effect: WpfEffectUniforms;

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}

fn wpf_constant(index: u32) -> vec4<f32> {
    return effect.constants[index];
}
";

    private static string CreateVertexAndHeaderShader()
    {
        var builder = new StringBuilder(VertexAndHeaderShaderPrefix);

        for (var i = 0; i < WpfShaderEffectParams.MaxSamplerRegisterCount; i++)
        {
            builder.Append("@group(2) @binding(")
                .Append(i * 2)
                .Append(") var sourceSampler")
                .Append(i)
                .AppendLine(": sampler;");
            builder.Append("@group(2) @binding(")
                .Append(i * 2 + 1)
                .Append(") var sourceTexture")
                .Append(i)
                .AppendLine(": texture_2d<f32>;");
        }

        builder.AppendLine();
        builder.AppendLine("fn wpf_sample_register(index: u32, uv: vec2<f32>) -> vec4<f32> {");
        for (var i = 0; i < WpfShaderEffectParams.MaxSamplerRegisterCount; i++)
        {
            builder.Append("    if (index == ")
                .Append(i)
                .Append("u) { return textureSample(sourceTexture")
                .Append(i)
                .Append(", sourceSampler")
                .Append(i)
                .AppendLine(", uv); }");
        }

        builder.AppendLine("    return vec4<f32>(0.0);");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("fn wpf_source_register() -> u32 {");
        builder.AppendLine("    return u32(round(effect.metadata.x));");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("fn wpf_sample_source(uv: vec2<f32>) -> vec4<f32> {");
        builder.AppendLine("    return wpf_sample_register(wpf_source_register(), uv);");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private const string FragmentWrapperShader = @"
@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let inputColor = wpf_sample_source(input.texCoord);
    let shaded = wpf_effect_main(input.texCoord, inputColor);
    return clamp(shaded, vec4<f32>(0.0), vec4<f32>(1.0)) * input.color;
}
";

    private struct EffectGpuResources
    {
        public GpuBuffer UniformBuffer;
        public nint BindGroupPtr;
    }

    private readonly List<EffectGpuResources> _pool = new();
    private readonly Dictionary<string, Compositor.CachedBindGroup> _textureBindGroups = new();
    private int _usedCount;
    private WgpuContext? _contextRef;
    private GpuTexture? _fallbackTexture;
    private BindGroupLayout* _effectBindGroupLayout;
    private BindGroupLayout* _sourceBindGroupLayout;
    private PipelineLayout* _onscreenPipelineLayout;
    private PipelineLayout* _offscreenPipelineLayout;

    private void EnsureLayouts(Compositor compositor)
    {
        if (_effectBindGroupLayout != null)
        {
            return;
        }

        _contextRef = compositor.Context;
        var context = _contextRef;
        var wgpu = context.Wgpu;
        var device = context.Device;

        var effectEntry = new BindGroupLayoutEntry
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
        var effectLayoutDesc = new BindGroupLayoutDescriptor
        {
            EntryCount = 1,
            Entries = &effectEntry
        };
        _effectBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &effectLayoutDesc);

        const int sourceEntryCount = WpfShaderEffectParams.MaxSamplerRegisterCount * 2;
        var sourceEntries = stackalloc BindGroupLayoutEntry[sourceEntryCount];
        for (var i = 0; i < WpfShaderEffectParams.MaxSamplerRegisterCount; i++)
        {
            sourceEntries[i * 2] = new BindGroupLayoutEntry
            {
                Binding = (uint)(i * 2),
                Visibility = ShaderStage.Fragment,
                Sampler = new SamplerBindingLayout
                {
                    Type = SamplerBindingType.Filtering
                }
            };
            sourceEntries[i * 2 + 1] = new BindGroupLayoutEntry
            {
                Binding = (uint)(i * 2 + 1),
                Visibility = ShaderStage.Fragment,
                Texture = new TextureBindingLayout
                {
                    SampleType = TextureSampleType.Float,
                    ViewDimension = TextureViewDimension.Dimension2D,
                    Multisampled = false
                }
            };
        }

        var sourceLayoutDesc = new BindGroupLayoutDescriptor
        {
            EntryCount = sourceEntryCount,
            Entries = sourceEntries
        };
        _sourceBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &sourceLayoutDesc);

        _fallbackTexture = new GpuTexture(
            context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Transparent Fallback");
        _fallbackTexture.WritePixels(new byte[] { 0, 0, 0, 0 });

        var onscreenLayouts = stackalloc BindGroupLayout*[3];
        onscreenLayouts[0] = compositor.VectorUniformBindGroupLayout;
        onscreenLayouts[1] = _effectBindGroupLayout;
        onscreenLayouts[2] = _sourceBindGroupLayout;
        var onscreenDesc = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = onscreenLayouts
        };
        _onscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &onscreenDesc);

        var offscreenLayouts = stackalloc BindGroupLayout*[3];
        offscreenLayouts[0] = compositor.VectorUniformBindGroupLayoutOffscreen;
        offscreenLayouts[1] = _effectBindGroupLayout;
        offscreenLayouts[2] = _sourceBindGroupLayout;
        var offscreenDesc = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = offscreenLayouts
        };
        _offscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &offscreenDesc);
    }

    public void Compile(
        Compositor compositor,
        IRenderDataProvider? provider,
        Matrix4x4 transform,
        ref RenderCommand cmd)
    {
        if (cmd.DataParam is not WpfShaderEffectParams p || !p.HasAnyTexture())
        {
            return;
        }

        var r = p.Rect;
        var color = new Vector4(1f, 1f, 1f, compositor.ActiveOpacity);

        var v0 = Vector2.Transform(new Vector2(r.X, r.Y), transform);
        var v1 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y), transform);
        var v2 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), transform);
        var v3 = Vector2.Transform(new Vector2(r.X, r.Y + r.Height), transform);

        int startIndex = compositor.VectorIndices.Count;

        int originalVertexCount = compositor.VectorVertices.Count;
        CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(v0, color, new Vector2(0f, 0f));
        vertexSpan[1] = new VectorVertex(v1, color, new Vector2(1f, 0f));
        vertexSpan[2] = new VectorVertex(v2, color, new Vector2(1f, 1f));
        vertexSpan[3] = new VectorVertex(v3, color, new Vector2(0f, 1f));

        int originalIndexCount = compositor.VectorIndices.Count;
        CollectionsMarshal.SetCount(compositor.VectorIndices, originalIndexCount + 6);
        var indexSpan = CollectionsMarshal.AsSpan(compositor.VectorIndices).Slice(originalIndexCount, 6);

        uint idxStart = (uint)originalVertexCount;
        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;
        indexSpan[3] = idxStart;
        indexSpan[4] = idxStart + 2;
        indexSpan[5] = idxStart + 3;

        if (compositor.ActiveClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(compositor.VectorVertices);
            for (int i = originalVertexCount; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = compositor.ClampToClip(v.Position);
                vertices[i] = v;
            }
        }

        cmd.PointBufferOffset = startIndex;
        cmd.PointBufferCount = compositor.VectorIndices.Count - startIndex;
    }

    public void BeginFrame(Compositor compositor)
    {
        _usedCount = 0;
        _contextRef ??= compositor.Context;
    }

    public void EndFrame(Compositor compositor)
    {
        ulong frame = compositor.FrameNumber;
        List<string>? keysToRemove = null;

        lock (_textureBindGroups)
        {
            foreach (var kvp in _textureBindGroups)
            {
                if (frame - kvp.Value.LastUsedFrame > 120)
                {
                    if (kvp.Value.BindGroupPtr != 0 && !compositor.Context.IsDisposed)
                    {
                        compositor.Context.Wgpu.BindGroupRelease((BindGroup*)kvp.Value.BindGroupPtr);
                    }

                    keysToRemove ??= new List<string>();
                    keysToRemove.Add(kvp.Key);
                }
            }

            if (keysToRemove != null)
            {
                foreach (var key in keysToRemove)
                {
                    _textureBindGroups.Remove(key);
                }
            }
        }
    }

    public void Render(
        Compositor compositor,
        void* renderPassEncoder,
        bool isOffscreen,
        in Compositor.CompositorDrawCall dc)
    {
        if (dc.PointBufferCount <= 0 || dc.DataParam is not WpfShaderEffectParams p || !p.HasAnyTexture() || p.IsFailed)
        {
            return;
        }

        EnsureLayouts(compositor);

        var shaderKey = p.GetStableShaderKey();
        var pipelineKey = isOffscreen
            ? shaderKey + "_wpf_effect_offscreen"
            : shaderKey + "_wpf_effect_onscreen";

        var cache = compositor.PipelineCache;
        RenderPipeline* activePipeline = null;
        if (cache.HasRenderPipeline(pipelineKey))
        {
            try
            {
                activePipeline = cache.GetOrCreateRenderPipeline(pipelineKey, null);
            }
            catch
            {
                activePipeline = null;
            }
        }

        if (activePipeline == null)
        {
            activePipeline = CreatePipeline(compositor, p, shaderKey, pipelineKey, isOffscreen);
            if (activePipeline == null)
            {
                return;
            }
        }

        var wgpu = compositor.Context.Wgpu;
        var device = compositor.Context.Device;
        var pass = (RenderPassEncoder*)renderPassEncoder;

        if (_usedCount >= _pool.Count)
        {
            var buffer = new GpuBuffer(
                compositor.Context,
                WpfShaderEffectParams.UniformByteCount,
                BufferUsage.Uniform | BufferUsage.CopyDst,
                $"WPF Shader Effect Uniforms {_pool.Count}");

            var bgEntries = stackalloc BindGroupEntry[1];
            bgEntries[0] = new BindGroupEntry
            {
                Binding = 0,
                Buffer = buffer.BufferPtr,
                Offset = 0,
                Size = buffer.Size
            };

            var bgDesc = new BindGroupDescriptor
            {
                Layout = _effectBindGroupLayout,
                EntryCount = 1,
                Entries = bgEntries,
                Label = (byte*)SilkMarshal.StringToPtr($"WPF Shader Effect Param BG {_pool.Count}")
            };

            var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
            SilkMarshal.Free((nint)bgDesc.Label);

            _pool.Add(new EffectGpuResources { UniformBuffer = buffer, BindGroupPtr = (nint)bg });
        }

        var gpuRes = _pool[_usedCount++];
        Span<float> uniformFloats = stackalloc float[WpfShaderEffectParams.UniformFloatCount];
        if (!p.TryGetPrimaryTexture(out var primaryTexture))
        {
            return;
        }

        p.CopyUniformFloats(uniformFloats, primaryTexture.Width, primaryTexture.Height);
        gpuRes.UniformBuffer.Write<float>(uniformFloats);

        var textureBindGroup = GetTextureBindGroup(compositor, activePipeline, p, isOffscreen);
        if (textureBindGroup == null)
        {
            return;
        }

        wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, compositor.VectorVertexBuffer.BufferPtr, 0, compositor.VectorVertexBuffer.Size);
        wgpu.RenderPassEncoderSetIndexBuffer(pass, compositor.VectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, compositor.VectorIndexBuffer.Size);

        var group0 = isOffscreen ? compositor.VectorUniformBindGroupOffscreen : compositor.VectorUniformBindGroup;
        wgpu.RenderPassEncoderSetBindGroup(pass, 0, group0, 0, null);
        wgpu.RenderPassEncoderSetBindGroup(pass, 1, (BindGroup*)gpuRes.BindGroupPtr, 0, null);
        wgpu.RenderPassEncoderSetBindGroup(pass, 2, textureBindGroup, 0, null);
        wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
        wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
    }

    public void Dispose()
    {
        if (_contextRef == null || _contextRef.IsDisposed)
        {
            _pool.Clear();
            _textureBindGroups.Clear();
            _fallbackTexture = null;
            return;
        }

        var wgpu = _contextRef.Wgpu;
        foreach (var resource in _pool)
        {
            if (resource.BindGroupPtr != 0)
            {
                wgpu.BindGroupRelease((BindGroup*)resource.BindGroupPtr);
            }

            resource.UniformBuffer.Dispose();
        }

        if (_effectBindGroupLayout != null)
        {
            wgpu.BindGroupLayoutRelease(_effectBindGroupLayout);
            _effectBindGroupLayout = null;
        }

        if (_sourceBindGroupLayout != null)
        {
            wgpu.BindGroupLayoutRelease(_sourceBindGroupLayout);
            _sourceBindGroupLayout = null;
        }

        if (_onscreenPipelineLayout != null)
        {
            wgpu.PipelineLayoutRelease(_onscreenPipelineLayout);
            _onscreenPipelineLayout = null;
        }

        if (_offscreenPipelineLayout != null)
        {
            wgpu.PipelineLayoutRelease(_offscreenPipelineLayout);
            _offscreenPipelineLayout = null;
        }

        foreach (var cached in _textureBindGroups.Values)
        {
            if (cached.BindGroupPtr != 0)
            {
                wgpu.BindGroupRelease((BindGroup*)cached.BindGroupPtr);
            }
        }

        _pool.Clear();
        _textureBindGroups.Clear();
        _fallbackTexture?.Dispose();
        _fallbackTexture = null;
    }

    private RenderPipeline* CreatePipeline(
        Compositor compositor,
        WpfShaderEffectParams parameters,
        string shaderKey,
        string pipelineKey,
        bool isOffscreen)
    {
        try
        {
            var fullShaderCode = VertexAndHeaderShader
                + "\n"
                + parameters.GetShaderSourceOrDefault()
                + "\n"
                + FragmentWrapperShader;
            var shaderModule = compositor.PipelineCache.GetOrCreateShader(
                shaderKey,
                fullShaderCode,
                $"WPFShaderEffect_{shaderKey}");

            if (!compositor.Context.VerifyShaderModule(shaderModule, out var errors))
            {
                parameters.IsFailed = true;
                parameters.LastError = errors;
                compositor.PipelineCache.ReleaseShader(shaderKey);
                return null;
            }

            var layouts = new VertexBufferLayout[]
            {
                new()
                {
                    ArrayStride = (uint)Marshal.SizeOf<VectorVertex>(),
                    StepMode = VertexStepMode.Vertex,
                    AttributeCount = 3,
                    Attributes = (VertexAttribute*)Marshal.AllocHGlobal(Marshal.SizeOf<VertexAttribute>() * 3)
                }
            };

            var attrs = layouts[0].Attributes;
            attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
            attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 };
            attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 };

            try
            {
                return compositor.PipelineCache.GetOrCreateRenderPipeline(
                    pipelineKey,
                    shaderModule,
                    vertexBufferLayouts: layouts,
                    topology: PrimitiveTopology.TriangleList,
                    targetFormat: compositor.RenderFormat,
                    sampleCount: isOffscreen ? 1u : 4u,
                    pipelineLayout: isOffscreen ? _offscreenPipelineLayout : _onscreenPipelineLayout);
            }
            finally
            {
                Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes);
            }
        }
        catch (Exception ex)
        {
            parameters.IsFailed = true;
            parameters.LastError = ex.Message;
            compositor.PipelineCache.ReleaseRenderPipeline(pipelineKey);
            compositor.PipelineCache.ReleaseShader(shaderKey);
            return null;
        }
    }

    private BindGroup* GetTextureBindGroup(
        Compositor compositor,
        RenderPipeline* pipeline,
        WpfShaderEffectParams parameters,
        bool isOffscreen)
    {
        if (_fallbackTexture == null)
        {
            return null;
        }

        var key = BuildTextureBindGroupKey(parameters, isOffscreen);

        lock (_textureBindGroups)
        {
            if (_textureBindGroups.TryGetValue(key, out var cached))
            {
                cached.LastUsedFrame = compositor.FrameNumber;
                return (BindGroup*)cached.BindGroupPtr;
            }

            var wgpu = compositor.Context.Wgpu;
            const int entryCount = WpfShaderEffectParams.MaxSamplerRegisterCount * 2;
            var textureEntries = stackalloc BindGroupEntry[entryCount];
            for (var i = 0; i < WpfShaderEffectParams.MaxSamplerRegisterCount; i++)
            {
                var texture = _fallbackTexture;
                var samplingMode = TextureSamplingMode.Linear;
                if (parameters.TryGetSampler(i, out var registeredTexture, out var registeredSamplingMode))
                {
                    texture = registeredTexture;
                    samplingMode = registeredSamplingMode;
                }

                textureEntries[i * 2] = new BindGroupEntry
                {
                    Binding = (uint)(i * 2),
                    Sampler = compositor.GetTextureSampler(samplingMode)
                };
                textureEntries[i * 2 + 1] = new BindGroupEntry
                {
                    Binding = (uint)(i * 2 + 1),
                    TextureView = texture.ViewPtr
                };
            }

            var bgDesc = new BindGroupDescriptor
            {
                Layout = _sourceBindGroupLayout,
                EntryCount = entryCount,
                Entries = textureEntries,
                Label = (byte*)SilkMarshal.StringToPtr("WPF Shader Effect Texture BG")
            };

            var bg = wgpu.DeviceCreateBindGroup(compositor.Context.Device, &bgDesc);
            SilkMarshal.Free((nint)bgDesc.Label);

            _textureBindGroups[key] = new Compositor.CachedBindGroup((nint)bg, compositor.FrameNumber);
            return bg;
        }
    }

    private static string BuildTextureBindGroupKey(WpfShaderEffectParams parameters, bool isOffscreen)
    {
        var builder = new StringBuilder(isOffscreen ? "off" : "on");
        for (var i = 0; i < WpfShaderEffectParams.MaxSamplerRegisterCount; i++)
        {
            if (parameters.TryGetSampler(i, out var texture, out var samplingMode))
            {
                builder.Append('|')
                    .Append(i)
                    .Append(':')
                    .Append(texture.Id)
                    .Append(':')
                    .Append(texture.Generation)
                    .Append(':')
                    .Append((int)samplingMode);
            }
            else
            {
                builder.Append('|')
                    .Append(i)
                    .Append(":0:0:0");
            }
        }

        return builder.ToString();
    }
}
