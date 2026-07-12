using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public unsafe class ImageEffectExtensionPipeline : ICompositorExtension, IDisposable
    {
        private const string CrossContextTextureErrorPrefix =
            "Image effect texture belongs to a different WebGPU context";

        private static readonly string ShaderCode = ShaderResource.Load(typeof(ImageEffectExtensionPipeline), "ImageEffect.wgsl");

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct EffectUniforms
        {
            public Vector4 ColorMatrixRed;
            public Vector4 ColorMatrixGreen;
            public Vector4 ColorMatrixBlue;
            public Vector4 ColorMatrixAlpha;
            public Vector4 ColorMatrixOffset;
            public Vector4 Effects0;
            public Vector4 Effects1;
            public Vector4 Texture0;
            public Vector4 Flags0;
        }

        private struct EffectGpuResources
        {
            public GpuBuffer UniformBuffer;
            public nint BindGroupPtr; // BindGroup*
        }

        private readonly Dictionary<(bool IsOffscreen, GpuTextureAlphaMode PipelineSourceAlphaMode, GpuBlendMode BlendMode), nint> _cachedPipelines = new();
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

            Vector2 uv0, uv1, uv2, uv3;
            if (p.SourceRect.Width > 0f && p.SourceRect.Height > 0f)
            {
                float texW = p.Texture.Width;
                float texH = p.Texture.Height;
                float l = p.SourceRect.X / texW;
                float t = p.SourceRect.Y / texH;
                float right = (p.SourceRect.X + p.SourceRect.Width) / texW;
                float b = (p.SourceRect.Y + p.SourceRect.Height) / texH;

                uv0 = new Vector2(l, t);
                uv1 = new Vector2(right, t);
                uv2 = new Vector2(right, b);
                uv3 = new Vector2(l, b);
            }
            else
            {
                uv0 = new Vector2(0f, 0f);
                uv1 = new Vector2(1f, 0f);
                uv2 = new Vector2(1f, 1f);
                uv3 = new Vector2(0f, 1f);
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
            lock (_textureBindGroups)
            {
                Compositor.TextureCacheKey[]? keysToRemove = null;
                int keysToRemoveCount = 0;
                try
                {
                    var textureBindGroupEnumerator = _textureBindGroups.GetEnumerator();
                    while (textureBindGroupEnumerator.MoveNext())
                    {
                        var kvp = textureBindGroupEnumerator.Current;
                        if (frame - kvp.Value.LastUsedFrame > 120)
                        {
                            if (kvp.Value.BindGroupPtr != 0 && !compositor.Context.IsDisposed)
                            {
                                QueueBindGroupRelease(compositor.Context, kvp.Value.BindGroupPtr);
                            }
                            PooledRemovalBuffer.Add(ref keysToRemove, ref keysToRemoveCount, _textureBindGroups.Count, kvp.Key);
                        }
                    }

                    for (int i = 0; i < keysToRemoveCount; i++)
                    {
                        _textureBindGroups.Remove(keysToRemove![i]);
                    }
                }
                finally
                {
                    PooledRemovalBuffer.Return(keysToRemove, keysToRemoveCount);
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

            if (!ValidateTextureContext(compositor.Context, p.Texture, "source", out var textureContextError)
                || (p.MaskTexture != null && !ValidateTextureContext(compositor.Context, p.MaskTexture, "mask", out textureContextError))
                || (dc.MaskTexture != null && !ValidateTextureContext(compositor.Context, dc.MaskTexture, "active mask", out textureContextError)))
            {
                p.LastError = textureContextError;
                return;
            }

            if (p.LastError?.StartsWith(CrossContextTextureErrorPrefix, StringComparison.Ordinal) == true)
            {
                p.LastError = null;
            }

            var wgpu = compositor.Context.Wgpu;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            var sourceAlphaMode = p.Texture.AlphaMode;
            var pipelineSourceAlphaMode = GetPipelineSourceAlphaMode(sourceAlphaMode, dc.BlendMode);
            var pipelineCacheKey = (isOffscreen, pipelineSourceAlphaMode, dc.BlendMode);
            if (!_cachedPipelines.TryGetValue(pipelineCacheKey, out var activePipelinePtr))
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("ImageEffectShader", ShaderCode, "ImageEffect WGSL Shader");
                
                Span<VertexAttribute> attrs = stackalloc VertexAttribute[3];
                attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // Position
                attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }; // Color
                attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // TexCoord

                Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];
                fixed (VertexAttribute* attrsPtr = attrs)
                {
                    layouts[0] = new VertexBufferLayout
                    {
                        ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>(),
                        StepMode = VertexStepMode.Vertex,
                        AttributeCount = 3,
                        Attributes = attrsPtr
                    };

                    var pipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                        isOffscreen
                            ? $"ImageEffectPipeline_Offscreen_{pipelineSourceAlphaMode}_{dc.BlendMode}"
                            : $"ImageEffectPipeline_{pipelineSourceAlphaMode}_{dc.BlendMode}",
                        shaderModule,
                        layouts,
                        topology: PrimitiveTopology.TriangleList,
                        targetFormat: compositor.RenderFormat,
                        sampleCount: isOffscreen ? 1u : 4u,
                        pipelineLayout: isOffscreen ? _offscreenPipelineLayout : _onscreenPipelineLayout,
                        blendMode: dc.BlendMode,
                        sourceAlphaMode: pipelineSourceAlphaMode
                    );

                    activePipelinePtr = (nint)pipeline;
                    _cachedPipelines[pipelineCacheKey] = activePipelinePtr;
                }
            }

            var activePipeline = (RenderPipeline*)activePipelinePtr;

            // 1. Uniform parameters buffer management
            if (_usedCount >= _pool.Count)
            {
                var uniformSize = (uint)Unsafe.SizeOf<EffectUniforms>();
                var buf = new GpuBuffer(compositor.Context, uniformSize, BufferUsage.Uniform | BufferUsage.CopyDst, $"ImageEffect Uniforms {_pool.Count}");

                var bgEntries = stackalloc BindGroupEntry[1];
                bgEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = buf.BufferPtr,
                    Offset = 0,
                    Size = uniformSize
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
            var maskCanvasWidth = effectiveMaskTexture?.Width ?? compositor.CurrentCanvasPixelWidth;
            var maskCanvasHeight = effectiveMaskTexture?.Height ?? compositor.CurrentCanvasPixelHeight;
            var colorMatrix = p.ColorMatrix;
            gpuRes.UniformBuffer.WriteSingle(new EffectUniforms
            {
                ColorMatrixRed = colorMatrix?.Red ?? default,
                ColorMatrixGreen = colorMatrix?.Green ?? default,
                ColorMatrixBlue = colorMatrix?.Blue ?? default,
                ColorMatrixAlpha = colorMatrix?.Alpha ?? default,
                ColorMatrixOffset = colorMatrix?.Offset ?? default,
                Effects0 = new Vector4(p.Brightness, p.Contrast, p.Saturation, p.Grayscale),
                Effects1 = new Vector4(
                    p.Sepia,
                    p.Invert,
                    p.BlurSigma,
                    effectiveMaskTexture != null ? 1f : 0f),
                Texture0 = new Vector4(
                    MathF.Max(1f, maskCanvasWidth),
                    MathF.Max(1f, maskCanvasHeight),
                    sourceAlphaMode == GpuTextureAlphaMode.Premultiplied ? 1f : 0f,
                    pipelineSourceAlphaMode == GpuTextureAlphaMode.Premultiplied ? 1f : 0f),
                Flags0 = new Vector4(
                    0f,
                    0f,
                    colorMatrix.HasValue ? 1f : 0f,
                    p.LuminanceToAlpha ? 1f : 0f)
            });

            // 2. Texture & Sampler BindGroup (Group 2)
            var textureCacheKey = new Compositor.TextureCacheKey(
                p.Texture.Id,
                p.Texture.Generation,
                isOffscreen,
                p.SamplingMode);
            Compositor.CachedBindGroup? cachedBg;
            lock (_textureBindGroups)
            {
                if (!_textureBindGroups.TryGetValue(textureCacheKey, out cachedBg))
                {
                    var textureEntries = stackalloc BindGroupEntry[2];
                    textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = compositor.GetTextureSampler(p.SamplingMode) };
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

        private static bool ValidateTextureContext(
            WgpuContext targetContext,
            GpuTexture texture,
            string role,
            out string? error)
        {
            if (!ReferenceEquals(texture.Context, targetContext))
            {
                error = $"{CrossContextTextureErrorPrefix} for {role}. " +
                    "Create or copy the texture in the compositor target context before rendering the effect.";
                return false;
            }

            error = null;
            return true;
        }

        public void Dispose()
        {
            if (_contextRef != null && !_contextRef.IsDisposed)
            {
                for (int i = 0; i < _pool.Count; i++)
                {
                    var resource = _pool[i];
                    if (resource.BindGroupPtr != 0)
                    {
                        QueueBindGroupRelease(_contextRef, resource.BindGroupPtr);
                    }

                    resource.UniformBuffer.Dispose();
                }

                var textureBindGroupValueEnumerator = _textureBindGroups.Values.GetEnumerator();
                while (textureBindGroupValueEnumerator.MoveNext())
                {
                    var cached = textureBindGroupValueEnumerator.Current;
                    if (cached.BindGroupPtr != 0)
                    {
                        QueueBindGroupRelease(_contextRef, cached.BindGroupPtr);
                    }
                }

                if (_effectBindGroupLayout != null)
                {
                    _contextRef.QueueBindGroupLayoutDisposal((IntPtr)_effectBindGroupLayout);
                    _effectBindGroupLayout = null;
                }

                if (_textureBindGroupLayout != null)
                {
                    _contextRef.QueueBindGroupLayoutDisposal((IntPtr)_textureBindGroupLayout);
                    _textureBindGroupLayout = null;
                }

                if (_onscreenPipelineLayout != null)
                {
                    _contextRef.QueuePipelineLayoutDisposal((IntPtr)_onscreenPipelineLayout);
                    _onscreenPipelineLayout = null;
                }

                if (_offscreenPipelineLayout != null)
                {
                    _contextRef.QueuePipelineLayoutDisposal((IntPtr)_offscreenPipelineLayout);
                    _offscreenPipelineLayout = null;
                }
            }

            _pool.Clear();
            _textureBindGroups.Clear();
        }

        private static void QueueBindGroupRelease(WgpuContext context, nint bindGroupPtr)
        {
            if (bindGroupPtr != 0 && !context.IsDisposed)
            {
                context.QueueBindGroupDisposal((IntPtr)bindGroupPtr);
            }
        }
    }
}
