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
    private const string MaskSamplerLimitError =
        "WPF shader effects that use all 16 sampler registers cannot also bind an active mask on this WebGPU device.";

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

    private static string CreateVertexAndHeaderShader(ReadOnlySpan<int> activeSamplerRegisters, bool includeMask)
    {
        var builder = new StringBuilder(VertexAndHeaderShaderPrefix);

        for (var slot = 0; slot < activeSamplerRegisters.Length; slot++)
        {
            var register = activeSamplerRegisters[slot];
            builder.Append("@group(2) @binding(")
                .Append(slot * 2)
                .Append(") var sourceSampler")
                .Append(register)
                .AppendLine(": sampler;");
            builder.Append("@group(2) @binding(")
                .Append(slot * 2 + 1)
                .Append(") var sourceTexture")
                .Append(register)
                .AppendLine(": texture_2d<f32>;");
        }

        if (includeMask)
        {
            builder.AppendLine("@group(3) @binding(0) var activeMaskSampler: sampler;");
            builder.AppendLine("@group(3) @binding(1) var activeMaskTexture: texture_2d<f32>;");
        }

        builder.AppendLine();
        builder.AppendLine("fn wpf_sample_register(index: u32, uv: vec2<f32>) -> vec4<f32> {");
        for (var slot = 0; slot < activeSamplerRegisters.Length; slot++)
        {
            var register = activeSamplerRegisters[slot];
            builder.Append("    if (index == ")
                .Append(register)
                .Append("u) { return textureSample(sourceTexture")
                .Append(register)
                .Append(", sourceSampler")
                .Append(register)
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
        builder.AppendLine();
        builder.AppendLine("fn wpf_has_active_mask() -> bool {");
        builder.AppendLine(includeMask ? "    return effect.metadata.w > 0.5;" : "    return false;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("fn wpf_active_mask_alpha(screenPosition: vec4<f32>) -> f32 {");
        if (includeMask)
        {
            builder.AppendLine("    let canvasSize = max(effect.metadata.yz, vec2<f32>(1.0));");
            builder.AppendLine("    let screenUv = screenPosition.xy / canvasSize;");
            builder.AppendLine("    return textureSample(activeMaskTexture, activeMaskSampler, screenUv).r;");
        }
        else
        {
            builder.AppendLine("    return 1.0;");
        }
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string CreateFragmentWrapperShader(
        GpuTextureAlphaMode sourceAlphaMode,
        GpuTextureAlphaMode pipelineSourceAlphaMode)
    {
        if (pipelineSourceAlphaMode != GpuTextureAlphaMode.Premultiplied)
        {
            return StraightFragmentWrapperShader;
        }

        return sourceAlphaMode == GpuTextureAlphaMode.Premultiplied
            ? PremultipliedFragmentWrapperShader
            : StraightToPremultipliedFragmentWrapperShader;
    }

    private const string PremultipliedFragmentWrapperShader = @"
@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let inputColor = wpf_sample_source(input.texCoord);
    let shaded = wpf_effect_main(input.texCoord, inputColor);
    let shadedColor = clamp(shaded, vec4<f32>(0.0), vec4<f32>(1.0));
    var maskAlpha = 1.0;
    if (wpf_has_active_mask()) {
        maskAlpha = wpf_active_mask_alpha(input.position);
    }

    let coverage = input.color.a * maskAlpha;
    return vec4<f32>(shadedColor.rgb * input.color.rgb * coverage, shadedColor.a * coverage);
}
";

    private const string StraightFragmentWrapperShader = @"
@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let inputColor = wpf_sample_source(input.texCoord);
    let shaded = wpf_effect_main(input.texCoord, inputColor);
    let shadedColor = clamp(shaded, vec4<f32>(0.0), vec4<f32>(1.0));
    var maskAlpha = 1.0;
    if (wpf_has_active_mask()) {
        maskAlpha = wpf_active_mask_alpha(input.position);
    }

    let coverage = input.color.a * maskAlpha;
    return vec4<f32>(shadedColor.rgb * input.color.rgb, shadedColor.a * coverage);
}
";

    private const string StraightToPremultipliedFragmentWrapperShader = @"
@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let inputColor = wpf_sample_source(input.texCoord);
    let shaded = wpf_effect_main(input.texCoord, inputColor);
    let shadedColor = clamp(shaded, vec4<f32>(0.0), vec4<f32>(1.0));
    var maskAlpha = 1.0;
    if (wpf_has_active_mask()) {
        maskAlpha = wpf_active_mask_alpha(input.position);
    }

    let coverage = input.color.a * maskAlpha;
    return vec4<f32>(shadedColor.rgb * shadedColor.a * input.color.rgb * coverage, shadedColor.a * coverage);
}
";

    private struct EffectGpuResources
    {
        public GpuBuffer UniformBuffer;
        public nint BindGroupPtr;
    }

    private sealed class SourceLayoutResources
    {
        public required string LayoutKey { get; init; }
        public required int[] Registers { get; init; }
        public required bool IncludeMask { get; init; }
        public BindGroupLayout* SourceBindGroupLayout;
        public PipelineLayout* OnscreenPipelineLayout;
        public PipelineLayout* OffscreenPipelineLayout;
    }

    private readonly List<EffectGpuResources> _pool = new();
    private readonly Dictionary<string, Compositor.CachedBindGroup> _textureBindGroups = new();
    private readonly Dictionary<string, SourceLayoutResources> _sourceLayouts = new();
    private int _usedCount;
    private WgpuContext? _contextRef;
    private GpuTexture? _fallbackTexture;
    private BindGroupLayout* _effectBindGroupLayout;

    private static bool BlendModeRequiresPremultipliedSource(GpuBlendMode blendMode)
    {
        return blendMode is GpuBlendMode.DstOver or GpuBlendMode.Multiply or GpuBlendMode.Screen;
    }

    private static GpuTextureAlphaMode GetPipelineSourceAlphaMode(
        GpuTextureAlphaMode textureAlphaMode,
        GpuBlendMode blendMode)
    {
        return BlendModeRequiresPremultipliedSource(blendMode)
            ? GpuTextureAlphaMode.Premultiplied
            : textureAlphaMode;
    }

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

        _fallbackTexture = new GpuTexture(
            context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Transparent Fallback");
        _fallbackTexture.WritePixels(new byte[] { 0, 0, 0, 0 });
    }

    private SourceLayoutResources GetOrCreateSourceLayout(Compositor compositor, int[] activeRegisters)
    {
        var includeMask = compositor.Context.CanBindWpfShaderEffectMask(activeRegisters.Length);
        var layoutKey = BuildSourceLayoutKey(activeRegisters, includeMask);

        lock (_sourceLayouts)
        {
            if (_sourceLayouts.TryGetValue(layoutKey, out var cached))
            {
                return cached;
            }

            var context = compositor.Context;
            var wgpu = context.Wgpu;
            var device = context.Device;
            var entryCount = activeRegisters.Length * 2;
            var sourceEntries = stackalloc BindGroupLayoutEntry[entryCount];

            for (var slot = 0; slot < activeRegisters.Length; slot++)
            {
                sourceEntries[slot * 2] = new BindGroupLayoutEntry
                {
                    Binding = (uint)(slot * 2),
                    Visibility = ShaderStage.Fragment,
                    Sampler = new SamplerBindingLayout
                    {
                        Type = SamplerBindingType.Filtering
                    }
                };
                sourceEntries[slot * 2 + 1] = new BindGroupLayoutEntry
                {
                    Binding = (uint)(slot * 2 + 1),
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
                EntryCount = (uint)entryCount,
                Entries = sourceEntries
            };
            var sourceBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &sourceLayoutDesc);

            var onscreenLayoutCount = includeMask ? 4 : 3;
            var onscreenLayouts = stackalloc BindGroupLayout*[onscreenLayoutCount];
            onscreenLayouts[0] = compositor.VectorUniformBindGroupLayout;
            onscreenLayouts[1] = _effectBindGroupLayout;
            onscreenLayouts[2] = sourceBindGroupLayout;
            if (includeMask)
            {
                onscreenLayouts[3] = compositor.MaskBindGroupLayout;
            }

            var onscreenDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = (uint)onscreenLayoutCount,
                BindGroupLayouts = onscreenLayouts
            };
            var onscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &onscreenDesc);

            var offscreenLayoutCount = includeMask ? 4 : 3;
            var offscreenLayouts = stackalloc BindGroupLayout*[offscreenLayoutCount];
            offscreenLayouts[0] = compositor.VectorUniformBindGroupLayoutOffscreen;
            offscreenLayouts[1] = _effectBindGroupLayout;
            offscreenLayouts[2] = sourceBindGroupLayout;
            if (includeMask)
            {
                offscreenLayouts[3] = compositor.MaskBindGroupLayoutOffscreen;
            }

            var offscreenDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = (uint)offscreenLayoutCount,
                BindGroupLayouts = offscreenLayouts
            };
            var offscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &offscreenDesc);

            var resources = new SourceLayoutResources
            {
                LayoutKey = layoutKey,
                Registers = activeRegisters,
                IncludeMask = includeMask,
                SourceBindGroupLayout = sourceBindGroupLayout,
                OnscreenPipelineLayout = onscreenPipelineLayout,
                OffscreenPipelineLayout = offscreenPipelineLayout
            };
            _sourceLayouts[layoutKey] = resources;
            return resources;
        }
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

        var uv0 = new Vector2(0f, 0f);
        var uv1 = new Vector2(1f, 0f);
        var uv2 = new Vector2(1f, 1f);
        var uv3 = new Vector2(0f, 1f);

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

        int startIndex = compositor.VectorIndices.Count;
        int originalVertexCount = compositor.VectorVertices.Count;
        CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(v0, color, uv0);
        vertexSpan[1] = new VectorVertex(v1, color, uv1);
        vertexSpan[2] = new VectorVertex(v2, color, uv2);
        vertexSpan[3] = new VectorVertex(v3, color, uv3);

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
                        QueueBindGroupRelease(compositor.Context, kvp.Value.BindGroupPtr);
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

        var activeRegisters = CollectActiveSamplerRegisters(p);
        if (activeRegisters.Length == 0)
        {
            return;
        }

        if (!p.TryGetPrimaryTexture(out var primaryTexture))
        {
            return;
        }

        var sourceLayout = GetOrCreateSourceLayout(compositor, activeRegisters);
        if (dc.MaskTexture != null && !sourceLayout.IncludeMask)
        {
            p.LastError = MaskSamplerLimitError;
            return;
        }

        if (string.Equals(p.LastError, MaskSamplerLimitError, StringComparison.Ordinal))
        {
            p.LastError = null;
        }

        var sourceAlphaMode = primaryTexture.AlphaMode;
        var pipelineSourceAlphaMode = GetPipelineSourceAlphaMode(sourceAlphaMode, dc.BlendMode);
        var shaderKey = $"{p.GetStableShaderKey()}_{sourceLayout.LayoutKey}_{sourceAlphaMode}_{pipelineSourceAlphaMode}";
        var pipelineKey = isOffscreen
            ? $"{shaderKey}_wpf_effect_offscreen_{pipelineSourceAlphaMode}_{dc.BlendMode}"
            : $"{shaderKey}_wpf_effect_onscreen_{pipelineSourceAlphaMode}_{dc.BlendMode}";

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
            activePipeline = CreatePipeline(
                compositor,
                p,
                sourceLayout,
                shaderKey,
                pipelineKey,
                isOffscreen,
                dc.BlendMode,
                sourceAlphaMode,
                pipelineSourceAlphaMode);
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

        p.CopyUniformFloats(uniformFloats, primaryTexture.Width, primaryTexture.Height);
        uniformFloats[WpfShaderEffectParams.CanvasWidthMetadataIndex] = compositor.CurrentCanvasPixelWidth;
        uniformFloats[WpfShaderEffectParams.CanvasHeightMetadataIndex] = compositor.CurrentCanvasPixelHeight;
        uniformFloats[WpfShaderEffectParams.HasMaskMetadataIndex] = dc.MaskTexture != null && sourceLayout.IncludeMask ? 1f : 0f;
        gpuRes.UniformBuffer.Write<float>(uniformFloats);

        var textureBindGroup = GetTextureBindGroup(compositor, sourceLayout, p, isOffscreen);
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
        if (sourceLayout.IncludeMask)
        {
            wgpu.RenderPassEncoderSetBindGroup(pass, 3, compositor.GetMaskBindGroup(dc.MaskTexture, isOffscreen), 0, null);
        }

        wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
        wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
    }

    public void Dispose()
    {
        if (_contextRef == null || _contextRef.IsDisposed)
        {
            _pool.Clear();
            _textureBindGroups.Clear();
            _sourceLayouts.Clear();
            _fallbackTexture = null;
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

        if (_effectBindGroupLayout != null)
        {
            _contextRef.QueueBindGroupLayoutDisposal((IntPtr)_effectBindGroupLayout);
            _effectBindGroupLayout = null;
        }

        foreach (var layout in _sourceLayouts.Values)
        {
            if (layout.SourceBindGroupLayout != null)
            {
                _contextRef.QueueBindGroupLayoutDisposal((IntPtr)layout.SourceBindGroupLayout);
                layout.SourceBindGroupLayout = null;
            }

            if (layout.OnscreenPipelineLayout != null)
            {
                _contextRef.QueuePipelineLayoutDisposal((IntPtr)layout.OnscreenPipelineLayout);
                layout.OnscreenPipelineLayout = null;
            }

            if (layout.OffscreenPipelineLayout != null)
            {
                _contextRef.QueuePipelineLayoutDisposal((IntPtr)layout.OffscreenPipelineLayout);
                layout.OffscreenPipelineLayout = null;
            }
        }

        foreach (var cached in _textureBindGroups.Values)
        {
            if (cached.BindGroupPtr != 0)
            {
                QueueBindGroupRelease(_contextRef, cached.BindGroupPtr);
            }
        }

        _pool.Clear();
        _textureBindGroups.Clear();
        _sourceLayouts.Clear();
        _fallbackTexture?.Dispose();
        _fallbackTexture = null;
    }

    private static void QueueBindGroupRelease(WgpuContext context, nint bindGroupPtr)
    {
        if (bindGroupPtr != 0 && !context.IsDisposed)
        {
            context.QueueBindGroupDisposal((IntPtr)bindGroupPtr);
        }
    }

    private RenderPipeline* CreatePipeline(
        Compositor compositor,
        WpfShaderEffectParams parameters,
        SourceLayoutResources sourceLayout,
        string shaderKey,
        string pipelineKey,
        bool isOffscreen,
        GpuBlendMode blendMode,
        GpuTextureAlphaMode sourceAlphaMode,
        GpuTextureAlphaMode pipelineSourceAlphaMode)
    {
        try
        {
            var fullShaderCode = CreateVertexAndHeaderShader(sourceLayout.Registers, sourceLayout.IncludeMask)
                + "\n"
                + parameters.GetShaderSourceOrDefault()
                + "\n"
                + CreateFragmentWrapperShader(sourceAlphaMode, pipelineSourceAlphaMode);
            var shaderModule = compositor.PipelineCache.GetOrCreateShader(
                shaderKey,
                fullShaderCode,
                $"WPFShaderEffect_{shaderKey}");

            var verification = compositor.Context.GetShaderModuleVerificationStatus(shaderModule, out var errors);
            if (verification == ShaderModuleVerificationStatus.Invalid)
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

            var pipelineFailed = false;
            Action<ErrorType, string> pipelineErrorHandler = (_, msg) =>
            {
                pipelineFailed = true;
                parameters.LastError = msg;
            };
            WgpuContext.OnWebGpuError += pipelineErrorHandler;

            try
            {
                try
                {
                    var pipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                        pipelineKey,
                        shaderModule,
                        vertexBufferLayouts: layouts,
                        topology: PrimitiveTopology.TriangleList,
                        targetFormat: compositor.RenderFormat,
                        sampleCount: isOffscreen ? 1u : 4u,
                        pipelineLayout: isOffscreen ? sourceLayout.OffscreenPipelineLayout : sourceLayout.OnscreenPipelineLayout,
                        blendMode: blendMode,
                        sourceAlphaMode: pipelineSourceAlphaMode);

                    compositor.Context.WaitIdle();

                    if (pipelineFailed || pipeline == null)
                    {
                        parameters.IsFailed = true;
                        parameters.LastError = string.IsNullOrEmpty(parameters.LastError)
                            ? "WPF shader effect pipeline creation failed."
                            : parameters.LastError;
                        compositor.PipelineCache.ReleaseRenderPipeline(pipelineKey);
                        compositor.PipelineCache.ReleaseShader(shaderKey);
                        return null;
                    }

                    return pipeline;
                }
                finally
                {
                    Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes);
                }
            }
            finally
            {
                WgpuContext.OnWebGpuError -= pipelineErrorHandler;
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
        SourceLayoutResources sourceLayout,
        WpfShaderEffectParams parameters,
        bool isOffscreen)
    {
        if (_fallbackTexture == null)
        {
            return null;
        }

        var key = BuildTextureBindGroupKey(parameters, sourceLayout, isOffscreen);

        lock (_textureBindGroups)
        {
            if (_textureBindGroups.TryGetValue(key, out var cached))
            {
                cached.LastUsedFrame = compositor.FrameNumber;
                return (BindGroup*)cached.BindGroupPtr;
            }

            var wgpu = compositor.Context.Wgpu;
            var entryCount = sourceLayout.Registers.Length * 2;
            var textureEntries = stackalloc BindGroupEntry[entryCount];
            for (var slot = 0; slot < sourceLayout.Registers.Length; slot++)
            {
                var register = sourceLayout.Registers[slot];
                var texture = _fallbackTexture;
                var samplingMode = TextureSamplingMode.Linear;
                if (parameters.TryGetSampler(register, out var registeredTexture, out var registeredSamplingMode))
                {
                    texture = registeredTexture;
                    samplingMode = registeredSamplingMode;
                }

                textureEntries[slot * 2] = new BindGroupEntry
                {
                    Binding = (uint)(slot * 2),
                    Sampler = compositor.GetTextureSampler(samplingMode)
                };
                textureEntries[slot * 2 + 1] = new BindGroupEntry
                {
                    Binding = (uint)(slot * 2 + 1),
                    TextureView = texture.ViewPtr
                };
            }

            var bgDesc = new BindGroupDescriptor
            {
                Layout = sourceLayout.SourceBindGroupLayout,
                EntryCount = (nuint)entryCount,
                Entries = textureEntries,
                Label = (byte*)SilkMarshal.StringToPtr("WPF Shader Effect Texture BG")
            };

            var bg = wgpu.DeviceCreateBindGroup(compositor.Context.Device, &bgDesc);
            SilkMarshal.Free((nint)bgDesc.Label);

            _textureBindGroups[key] = new Compositor.CachedBindGroup((nint)bg, compositor.FrameNumber);
            return bg;
        }
    }

    private static string BuildTextureBindGroupKey(
        WpfShaderEffectParams parameters,
        SourceLayoutResources sourceLayout,
        bool isOffscreen)
    {
        var builder = new StringBuilder(isOffscreen ? "off" : "on");
        builder.Append('|').Append(sourceLayout.LayoutKey);
        foreach (var register in sourceLayout.Registers)
        {
            if (parameters.TryGetSampler(register, out var texture, out var samplingMode))
            {
                builder.Append('|')
                    .Append(register)
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
                    .Append(register)
                    .Append(":0:0:0");
            }
        }

        return builder.ToString();
    }

    private static int[] CollectActiveSamplerRegisters(WpfShaderEffectParams parameters)
    {
        Span<int> registers = stackalloc int[WpfShaderEffectParams.MaxSamplerRegisterCount];
        var count = 0;

        for (var register = 0; register < WpfShaderEffectParams.MaxSamplerRegisterCount; register++)
        {
            if (parameters.TryGetSampler(register, out _, out _))
            {
                registers[count++] = register;
            }
        }

        return registers[..count].ToArray();
    }

    private static string BuildSourceLayoutKey(ReadOnlySpan<int> activeRegisters, bool includeMask)
    {
        var builder = new StringBuilder(includeMask ? "m" : "n");
        foreach (var register in activeRegisters)
        {
            builder.Append('_').Append(register);
        }

        return builder.ToString();
    }
}
