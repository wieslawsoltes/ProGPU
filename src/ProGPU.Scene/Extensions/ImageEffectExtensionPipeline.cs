using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public unsafe class ImageEffectExtensionPipeline : ICompositorExtension, IDisposable
    {
        private const string ShaderCode = @"
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct EffectUniforms {
    brightness: f32,
    contrast: f32,
    saturation: f32,
    grayscale: f32,
    sepia: f32,
    invert: f32,
    blurSigma: f32,
    hasMask: f32,
    canvasWidth: f32,
    canvasHeight: f32,
    sourceIsPremultiplied: f32,
    _pad1: f32,
};

@group(1) @binding(0) var<uniform> effect: EffectUniforms;

@group(2) @binding(0) var texSampler: sampler;
@group(2) @binding(1) var texTexture: texture_2d<f32>;

@group(3) @binding(0) var maskSampler: sampler;
@group(3) @binding(1) var maskTexture: texture_2d<f32>;

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

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    var color = vec4<f32>(0.0);
    
    let sigma = effect.blurSigma;
    if (sigma > 0.01) {
        let texSize = vec2<f32>(textureDimensions(texTexture));
        let texel = vec2<f32>(1.0) / texSize;
        
        var totalWeight = 0.0;
        let radius = i32(clamp(sigma * 2.0, 1.0, 5.0));
        
        for (var dy = -radius; dy <= radius; dy = dy + 1) {
            for (var dx = -radius; dx <= radius; dx = dx + 1) {
                let offset = vec2<f32>(f32(dx), f32(dy)) * texel;
                let weight = exp(-f32(dx * dx + dy * dy) / (2.0 * sigma * sigma));
                color = color + textureSample(texTexture, texSampler, input.texCoord + offset) * weight;
                totalWeight = totalWeight + weight;
            }
        }
        color = color / totalWeight;
    } else {
        color = textureSample(texTexture, texSampler, input.texCoord);
    }
    
    // Apply brightness
    color.r = color.r + effect.brightness;
    color.g = color.g + effect.brightness;
    color.b = color.b + effect.brightness;
    
    // Apply contrast
    color.r = (color.r - 0.5) * effect.contrast + 0.5;
    color.g = (color.g - 0.5) * effect.contrast + 0.5;
    color.b = (color.b - 0.5) * effect.contrast + 0.5;
    
    // Apply saturation
    let luminance = dot(color.rgb, vec3<f32>(0.2126, 0.7152, 0.0722));
    color.r = mix(luminance, color.r, effect.saturation);
    color.g = mix(luminance, color.g, effect.saturation);
    color.b = mix(luminance, color.b, effect.saturation);
    
    // Apply grayscale
    let gray = vec3<f32>(luminance);
    color = vec4<f32>(mix(color.rgb, gray, effect.grayscale), color.a);
    
    // Apply sepia
    let sepiaColor = vec3<f32>(
        color.r * 0.393 + color.g * 0.769 + color.b * 0.189,
        color.r * 0.349 + color.g * 0.686 + color.b * 0.168,
        color.r * 0.272 + color.g * 0.534 + color.b * 0.131
    );
    color = vec4<f32>(mix(color.rgb, sepiaColor, effect.sepia), color.a);
    
    // Apply invert
    let inverted = vec3<f32>(1.0) - color.rgb;
    color = vec4<f32>(mix(color.rgb, inverted, effect.invert), color.a);
    
    color = clamp(color, vec4<f32>(0.0), vec4<f32>(1.0));
    
    var maskAlpha = 1.0;
    if (effect.hasMask > 0.5) {
        let screen_uv = input.position.xy / vec2<f32>(effect.canvasWidth, effect.canvasHeight);
        maskAlpha = textureSample(maskTexture, maskSampler, screen_uv).r;
    }

    let coverage = input.color.a * maskAlpha;
    if (effect.sourceIsPremultiplied > 0.5) {
        return vec4<f32>(color.rgb * input.color.rgb * coverage, color.a * coverage);
    }

    return vec4<f32>(color.rgb * input.color.rgb, color.a * coverage);
}
";

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct EffectUniforms
        {
            public float Brightness;
            public float Contrast;
            public float Saturation;
            public float Grayscale;
            public float Sepia;
            public float Invert;
            public float BlurSigma;
            public float HasMask;
            public float CanvasWidth;
            public float CanvasHeight;
            public float SourceIsPremultiplied;
            public float Pad1;
        }

        private struct EffectGpuResources
        {
            public GpuBuffer UniformBuffer;
            public nint BindGroupPtr; // BindGroup*
        }

        private readonly Dictionary<(bool IsOffscreen, GpuTextureAlphaMode SourceAlphaMode, GpuBlendMode BlendMode), nint> _cachedPipelines = new();
        private WgpuContext? _contextRef;
        private BindGroupLayout* _effectBindGroupLayout;
        private BindGroupLayout* _textureBindGroupLayout;
        private PipelineLayout* _onscreenPipelineLayout;
        private PipelineLayout* _offscreenPipelineLayout;

        // Dynamic pool to recycle uniform buffers and bind groups without frame allocation
        private readonly List<EffectGpuResources> _pool = new();
        private int _usedCount;

        // Texture bind groups cache
        private readonly Dictionary<Compositor.TextureCacheKey, Compositor.CachedBindGroup> _textureBindGroups = new();

        private void EnsureLayouts(Compositor compositor)
        {
            if (_effectBindGroupLayout != null)
            {
                return;
            }

            _contextRef = compositor.Context;
            var wgpu = _contextRef.Wgpu;
            var device = _contextRef.Device;

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

            var textureEntries = stackalloc BindGroupLayoutEntry[2];
            textureEntries[0] = new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Sampler = new SamplerBindingLayout
                {
                    Type = SamplerBindingType.Filtering
                }
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
            var textureLayoutDesc = new BindGroupLayoutDescriptor
            {
                EntryCount = 2,
                Entries = textureEntries
            };
            _textureBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &textureLayoutDesc);

            var onscreenLayouts = stackalloc BindGroupLayout*[4];
            onscreenLayouts[0] = compositor.VectorUniformBindGroupLayout;
            onscreenLayouts[1] = _effectBindGroupLayout;
            onscreenLayouts[2] = _textureBindGroupLayout;
            onscreenLayouts[3] = compositor.MaskBindGroupLayout;
            var onscreenDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 4,
                BindGroupLayouts = onscreenLayouts
            };
            _onscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &onscreenDesc);

            var offscreenLayouts = stackalloc BindGroupLayout*[4];
            offscreenLayouts[0] = compositor.VectorUniformBindGroupLayoutOffscreen;
            offscreenLayouts[1] = _effectBindGroupLayout;
            offscreenLayouts[2] = _textureBindGroupLayout;
            offscreenLayouts[3] = compositor.MaskBindGroupLayoutOffscreen;
            var offscreenDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 4,
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
            var p = cmd.DataParam as ImageEffectParams;
            if (p == null) return;

            var r = p.Rect;
            float opacity = compositor.ActiveOpacity;
            var color = new Vector4(1f, 1f, 1f, opacity);

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

            int indexCount = compositor.VectorIndices.Count - startIndex;
            cmd.PointBufferOffset = startIndex;
            cmd.PointBufferCount = indexCount;
        }

        public void BeginFrame(Compositor compositor)
        {
            _usedCount = 0;
        }

        public void EndFrame(Compositor compositor)
        {
            // Prune unused texture bind groups periodically
            ulong frame = compositor.FrameNumber;
            List<Compositor.TextureCacheKey>? keysToRemove = null;
            lock (_textureBindGroups)
            {
                foreach (var kvp in _textureBindGroups)
                {
                    if (frame - kvp.Value.LastUsedFrame > 120)
                    {
                        if (kvp.Value.BindGroupPtr != 0 && !compositor.Context.IsDisposed)
                        {
                            unsafe
                            {
                                compositor.Context.Wgpu.BindGroupRelease((BindGroup*)kvp.Value.BindGroupPtr);
                            }
                        }
                        keysToRemove ??= new List<Compositor.TextureCacheKey>();
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

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            if (dc.PointBufferCount <= 0 || dc.DataParam is not ImageEffectParams p) return;

            EnsureLayouts(compositor);

            var wgpu = compositor.Context.Wgpu;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            var sourceAlphaMode = p.Texture.AlphaMode;
            var pipelineCacheKey = (isOffscreen, sourceAlphaMode, dc.BlendMode);
            if (!_cachedPipelines.TryGetValue(pipelineCacheKey, out var activePipelinePtr))
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("ImageEffectShader", ShaderCode, "ImageEffect WGSL Shader");
                
                var layouts = new VertexBufferLayout[]
                {
                    new VertexBufferLayout
                    {
                        ArrayStride = (uint)Marshal.SizeOf<VectorVertex>(),
                        StepMode = VertexStepMode.Vertex,
                        AttributeCount = 3,
                        Attributes = (VertexAttribute*)Marshal.AllocHGlobal(Marshal.SizeOf<VertexAttribute>() * 3)
                    }
                };

                var attrs = layouts[0].Attributes;
                attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // Position
                attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }; // Color
                attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // TexCoord

                var pipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                    isOffscreen
                        ? $"ImageEffectPipeline_Offscreen_{sourceAlphaMode}_{dc.BlendMode}"
                        : $"ImageEffectPipeline_{sourceAlphaMode}_{dc.BlendMode}",
                    shaderModule,
                    vertexBufferLayouts: layouts,
                    topology: PrimitiveTopology.TriangleList,
                    targetFormat: compositor.RenderFormat,
                    sampleCount: isOffscreen ? 1u : 4u,
                    pipelineLayout: isOffscreen ? _offscreenPipelineLayout : _onscreenPipelineLayout,
                    blendMode: dc.BlendMode,
                    sourceAlphaMode: sourceAlphaMode
                );

                Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes);

                activePipelinePtr = (nint)pipeline;
                _cachedPipelines[pipelineCacheKey] = activePipelinePtr;
            }

            var activePipeline = (RenderPipeline*)activePipelinePtr;

            // 1. Uniform parameters buffer management
            if (_usedCount >= _pool.Count)
            {
                var buf = new GpuBuffer(compositor.Context, 48, BufferUsage.Uniform | BufferUsage.CopyDst, $"ImageEffect Uniforms {_pool.Count}");

                var bgEntries = stackalloc BindGroupEntry[1];
                bgEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = buf.BufferPtr,
                    Offset = 0,
                    Size = 48
                };

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = _effectBindGroupLayout,
                    EntryCount = 1,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr($"ImageEffect Param BG {_pool.Count}")
                };

                var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                SilkMarshal.Free((nint)bgDesc.Label);

                _pool.Add(new EffectGpuResources { UniformBuffer = buf, BindGroupPtr = (nint)bg });
            }

            var gpuRes = _pool[_usedCount++];
            var effectiveMaskTexture = p.MaskTexture ?? dc.MaskTexture;
            gpuRes.UniformBuffer.WriteSingle(new EffectUniforms
            {
                Brightness = p.Brightness,
                Contrast = p.Contrast,
                Saturation = p.Saturation,
                Grayscale = p.Grayscale,
                Sepia = p.Sepia,
                Invert = p.Invert,
                BlurSigma = p.BlurSigma,
                HasMask = effectiveMaskTexture != null ? 1f : 0f,
                CanvasWidth = compositor.CurrentWidth,
                CanvasHeight = compositor.CurrentHeight,
                SourceIsPremultiplied = sourceAlphaMode == GpuTextureAlphaMode.Premultiplied ? 1f : 0f
            });

            // 2. Texture & Sampler BindGroup (Group 2)
            var textureCacheKey = new Compositor.TextureCacheKey(
                p.Texture.Id,
                p.Texture.Generation,
                isOffscreen,
                TextureSamplingMode.Linear);
            Compositor.CachedBindGroup? cachedBg;
            lock (_textureBindGroups)
            {
                if (!_textureBindGroups.TryGetValue(textureCacheKey, out cachedBg))
                {
                    var textureEntries = stackalloc BindGroupEntry[2];
                    textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = compositor.GetTextureSampler(TextureSamplingMode.Linear) };
                    textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = p.Texture.ViewPtr };

                    var bgDesc = new BindGroupDescriptor
                    {
                        Layout = _textureBindGroupLayout,
                        EntryCount = 2,
                        Entries = textureEntries,
                        Label = (byte*)SilkMarshal.StringToPtr("ImageEffect Texture BG")
                    };

                    var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                    SilkMarshal.Free((nint)bgDesc.Label);
                    cachedBg = new Compositor.CachedBindGroup((nint)bg, compositor.FrameNumber);
                    _textureBindGroups[textureCacheKey] = cachedBg;
                }
                else
                {
                    cachedBg.LastUsedFrame = compositor.FrameNumber;
                }
            }

            // 3. Mask BindGroup (Group 3)
            var maskBg = compositor.GetMaskBindGroup(effectiveMaskTexture, isOffscreen);

            // 4. Set states & draw
            var vertexBuffer = compositor.VectorVertexBuffer.BufferPtr;
            wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, vertexBuffer, 0, compositor.VectorVertexBuffer.Size);
            wgpu.RenderPassEncoderSetIndexBuffer(pass, compositor.VectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, compositor.VectorIndexBuffer.Size);

            var group0 = isOffscreen ? compositor.VectorUniformBindGroupOffscreen : compositor.VectorUniformBindGroup;
            wgpu.RenderPassEncoderSetBindGroup(pass, 0, group0, 0, null);
            wgpu.RenderPassEncoderSetBindGroup(pass, 1, (BindGroup*)gpuRes.BindGroupPtr, 0, null);
            wgpu.RenderPassEncoderSetBindGroup(pass, 2, (BindGroup*)cachedBg.BindGroupPtr, 0, null);
            wgpu.RenderPassEncoderSetBindGroup(pass, 3, maskBg, 0, null);

            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
        }

        public void Dispose()
        {
            if (_contextRef != null && !_contextRef.IsDisposed)
            {
                var wgpu = _contextRef.Wgpu;

                foreach (var resource in _pool)
                {
                    if (resource.BindGroupPtr != 0)
                    {
                        wgpu.BindGroupRelease((BindGroup*)resource.BindGroupPtr);
                    }

                    resource.UniformBuffer.Dispose();
                }

                foreach (var cached in _textureBindGroups.Values)
                {
                    if (cached.BindGroupPtr != 0)
                    {
                        wgpu.BindGroupRelease((BindGroup*)cached.BindGroupPtr);
                    }
                }

                if (_effectBindGroupLayout != null)
                {
                    wgpu.BindGroupLayoutRelease(_effectBindGroupLayout);
                    _effectBindGroupLayout = null;
                }

                if (_textureBindGroupLayout != null)
                {
                    wgpu.BindGroupLayoutRelease(_textureBindGroupLayout);
                    _textureBindGroupLayout = null;
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
            }

            _pool.Clear();
            _textureBindGroups.Clear();
        }
    }
}
