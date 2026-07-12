using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;
using ProGPU.Transpiler;

namespace ProGPU.Scene.Extensions
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ShaderToyUniforms
    {
        public Vector3 Resolution;
        public float Time;
        public float TimeDelta;
        public int Frame;
        public float FrameRate;
        public float Pad0;
        public Vector4 Mouse;
        public Vector4 Date;
    }

    public class ShaderToyParams
    {
        public Rect Rect { get; set; }
        public string ShaderSource { get; set; } = string.Empty;
        public string ShaderKey { get; set; } = string.Empty;
        public string OldShaderKey { get; set; } = string.Empty;
        public bool IsFailed { get; set; }

        public Vector3 Resolution { get; set; }
        public float Time { get; set; }
        public float TimeDelta { get; set; }
        public float Frame { get; set; }
        public float FrameRate { get; set; }
        public Vector4 Mouse { get; set; }
        public Vector4 Date { get; set; }
    }

    public unsafe class ShaderToyExtensionPipeline : ICompositorExtension, IDisposable
    {
        private static readonly string VertexAndHeaderShader = ShaderResource.Load(typeof(ShaderToyExtensionPipeline), "ShaderToyHeader.wgsl");

        private static readonly string StraightFragmentWrapperShader = ShaderResource.Load(typeof(ShaderToyExtensionPipeline), "ShaderToyStraightWrapper.wgsl");

        private static readonly string PremultipliedFragmentWrapperShader = ShaderResource.Load(typeof(ShaderToyExtensionPipeline), "ShaderToyPremultipliedWrapper.wgsl");

        private struct ToyGpuResources
        {
            public GpuBuffer UniformBuffer;
            public nint BindGroupPtr; // BindGroup*
        }

        private readonly List<ToyGpuResources> _pool = new();
        private int _usedCount;

        private WgpuContext? _contextRef = null;
        private BindGroupLayout* _toyBindGroupLayout = null;
        private PipelineLayout* _onscreenPipelineLayout = null;
        private PipelineLayout* _offscreenPipelineLayout = null;

        private static bool BlendModeRequiresPremultipliedSource(GpuBlendMode blendMode)
        {
            return blendMode is GpuBlendMode.DstOver or GpuBlendMode.Multiply or GpuBlendMode.Screen;
        }

        private static GpuTextureAlphaMode GetPipelineSourceAlphaMode(GpuBlendMode blendMode)
        {
            return BlendModeRequiresPremultipliedSource(blendMode)
                ? GpuTextureAlphaMode.Premultiplied
                : GpuTextureAlphaMode.Straight;
        }

        private static string GetFragmentWrapperShader(GpuTextureAlphaMode pipelineSourceAlphaMode)
        {
            return pipelineSourceAlphaMode == GpuTextureAlphaMode.Premultiplied
                ? PremultipliedFragmentWrapperShader
                : StraightFragmentWrapperShader;
        }

        private static string GetStableShaderSourceKey(string shaderSource)
        {
            const ulong fnvOffsetBasis = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            var hash = fnvOffsetBasis;
            foreach (var c in shaderSource)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return hash.ToString("x16");
        }

        private static string GetShaderKey(string shaderKey, string shaderSourceKey, GpuTextureAlphaMode pipelineSourceAlphaMode)
        {
            return $"{shaderKey}_src_{shaderSourceKey}_{pipelineSourceAlphaMode}";
        }

        private static string GetLegacyShaderKey(string shaderKey, GpuTextureAlphaMode pipelineSourceAlphaMode)
        {
            return $"{shaderKey}_{pipelineSourceAlphaMode}";
        }

        private static string GetPipelineKey(string shaderKey, string shaderSourceKey, bool isOffscreen, GpuBlendMode blendMode, GpuTextureAlphaMode pipelineSourceAlphaMode)
        {
            var target = isOffscreen ? "offscreen" : "onscreen";
            return $"{shaderKey}_src_{shaderSourceKey}_{target}_{blendMode}_{pipelineSourceAlphaMode}";
        }

        private static string GetLegacyPipelineKey(string shaderKey, bool isOffscreen, GpuBlendMode blendMode, GpuTextureAlphaMode pipelineSourceAlphaMode)
        {
            var target = isOffscreen ? "offscreen" : "onscreen";
            return $"{shaderKey}_{target}_{blendMode}_{pipelineSourceAlphaMode}";
        }

        private void EnsureLayouts(Compositor compositor)
        {
            if (_toyBindGroupLayout != null) return;

            _contextRef = compositor.Context;
            var wgpu = _contextRef.Wgpu;
            var device = _contextRef.Device;

            var entry = new BindGroupLayoutEntry
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
            var layoutDesc = new BindGroupLayoutDescriptor
            {
                EntryCount = 1,
                Entries = &entry
            };
            _toyBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &layoutDesc);

            // Onscreen pipeline layout
            var onscreenLayouts = stackalloc BindGroupLayout*[3];
            onscreenLayouts[0] = compositor.VectorUniformBindGroupLayout;
            onscreenLayouts[1] = _toyBindGroupLayout;
            onscreenLayouts[2] = compositor.MaskBindGroupLayout;
            var onscreenDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 3,
                BindGroupLayouts = onscreenLayouts
            };
            _onscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &onscreenDesc);

            // Offscreen pipeline layout
            var offscreenLayouts = stackalloc BindGroupLayout*[3];
            offscreenLayouts[0] = compositor.VectorUniformBindGroupLayoutOffscreen;
            offscreenLayouts[1] = _toyBindGroupLayout;
            offscreenLayouts[2] = compositor.MaskBindGroupLayoutOffscreen;
            var offscreenDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 3,
                BindGroupLayouts = offscreenLayouts
            };
            _offscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &offscreenDesc);
        }

        public void Dispose()
        {
            if (_contextRef != null && !_contextRef.IsDisposed)
            {
                foreach (var res in _pool)
                {
                    QueueBindGroupRelease(_contextRef, res.BindGroupPtr);
                    res.UniformBuffer?.Dispose();
                }

                if (_toyBindGroupLayout != null)
                {
                    _contextRef.QueueBindGroupLayoutDisposal((IntPtr)_toyBindGroupLayout);
                    _toyBindGroupLayout = null;
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
        }

        private static void QueueBindGroupRelease(WgpuContext context, nint bindGroupPtr)
        {
            if (bindGroupPtr != 0 && !context.IsDisposed)
            {
                context.QueueBindGroupDisposal((IntPtr)bindGroupPtr);
            }
        }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            var p = cmd.DataParam as ShaderToyParams;
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
            // Optional: prune resources
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            if (dc.PointBufferCount <= 0 || dc.DataParam is not ShaderToyParams p) return;
            if (p.IsFailed || string.IsNullOrEmpty(p.ShaderKey)) return;

            EnsureLayouts(compositor);

            var wgpu = compositor.Context.Wgpu;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            // 1. Check for dynamic recompiles and release old pipeline
            if (!string.IsNullOrEmpty(p.OldShaderKey))
            {
                foreach (GpuBlendMode blendMode in Enum.GetValues<GpuBlendMode>())
                {
                    var oldPipelineSourceAlphaMode = GetPipelineSourceAlphaMode(blendMode);
                    compositor.PipelineCache.ReleaseRenderPipeline(GetLegacyPipelineKey(p.OldShaderKey, isOffscreen: false, blendMode, oldPipelineSourceAlphaMode));
                    compositor.PipelineCache.ReleaseRenderPipeline(GetLegacyPipelineKey(p.OldShaderKey, isOffscreen: true, blendMode, oldPipelineSourceAlphaMode));
                }

                compositor.PipelineCache.ReleaseRenderPipeline(p.OldShaderKey + "_offscreen");
                compositor.PipelineCache.ReleaseRenderPipeline(p.OldShaderKey + "_onscreen");
                compositor.PipelineCache.ReleaseShader(p.OldShaderKey);
                compositor.PipelineCache.ReleaseShader(GetLegacyShaderKey(p.OldShaderKey, GpuTextureAlphaMode.Straight));
                compositor.PipelineCache.ReleaseShader(GetLegacyShaderKey(p.OldShaderKey, GpuTextureAlphaMode.Premultiplied));
                p.OldShaderKey = string.Empty;
            }

            // 2. Fetch or Compile Shader module & Pipeline
            var pipelineSourceAlphaMode = GetPipelineSourceAlphaMode(dc.BlendMode);
            string shaderSourceKey = GetStableShaderSourceKey(p.ShaderSource);
            string shaderKey = GetShaderKey(p.ShaderKey, shaderSourceKey, pipelineSourceAlphaMode);
            string pipelineKey = GetPipelineKey(p.ShaderKey, shaderSourceKey, isOffscreen, dc.BlendMode, pipelineSourceAlphaMode);
            RenderPipeline* activePipeline = null;

            var cache = compositor.PipelineCache;
            if (cache.HasRenderPipeline(pipelineKey))
            {
                try
                {
                    activePipeline = cache.GetOrCreateRenderPipeline(pipelineKey, null);
                }
                catch
                {
                    // Ignore
                }
            }

            if (activePipeline == null)
            {
                try
                {
                    // Build full shader code
                    string userSource = p.ShaderSource;
                    if (ShaderToyTranspiler.IsGlsl(userSource))
                    {
                        try
                        {
                            userSource = ShaderToyTranspiler.Translate(userSource);
                            ProGpuSceneDiagnostics.WriteLine("[ShaderToy Transpiler] Auto-transpiled GLSL code to WGSL successfully.");
                        }
                        catch (Exception ex)
                        {
                            ProGpuSceneDiagnostics.WriteLine($"[ShaderToy Transpiler] Transpilation failed: {ex.Message}");
                        }
                    }
                    string fullShaderCode = VertexAndHeaderShader + "\n" + userSource + "\n" + GetFragmentWrapperShader(pipelineSourceAlphaMode);
                    var shaderModule = compositor.PipelineCache.GetOrCreateShader(shaderKey, fullShaderCode, $"ShaderToy_{shaderKey}");

                    string errors = "";
                    var verification = shaderModule == null
                        ? ShaderModuleVerificationStatus.Invalid
                        : compositor.Context.GetShaderModuleVerificationStatus(shaderModule, out errors);
                    if (verification == ShaderModuleVerificationStatus.Invalid)
                    {
                        ProGpuSceneDiagnostics.WriteLine($"[ShaderToy Render] Shader module creation failed:\n{errors}");
                        p.IsFailed = true;
                        if (!string.IsNullOrEmpty(errors))
                        {
                            WgpuContext.RaiseWebGpuError(ErrorType.Validation, errors);
                        }
                        compositor.PipelineCache.ReleaseShader(shaderKey);
                        return;
                    }

                    Span<VertexAttribute> attrs = stackalloc VertexAttribute[3];
                    attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // Position
                    attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }; // Color
                    attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // TexCoord

                    bool pipelineFailed = false;
                    Action<ErrorType, string> pipelineErrorHandler = (type, msg) => {
                        pipelineFailed = true;
                    };
                    WgpuContext.OnWebGpuError += pipelineErrorHandler;

                    try
                    {
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

                            activePipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                                pipelineKey,
                                shaderModule,
                                vertexBufferLayouts: layouts,
                                topology: PrimitiveTopology.TriangleList,
                                targetFormat: compositor.RenderFormat,
                                sampleCount: isOffscreen ? 1u : 4u,
                                pipelineLayout: isOffscreen ? _offscreenPipelineLayout : _onscreenPipelineLayout,
                                blendMode: dc.BlendMode,
                                sourceAlphaMode: pipelineSourceAlphaMode
                            );
                        }

                        // Force dispatching of pipeline creation validation errors before unhooking the error handler.
                        compositor.Context.WaitIdle();
                    }
                    finally
                    {
                        WgpuContext.OnWebGpuError -= pipelineErrorHandler;
                    }

                    if (pipelineFailed || activePipeline == null)
                    {
                        ProGpuSceneDiagnostics.WriteLine("[ShaderToy Render] Pipeline creation failed, aborting.");
                        p.IsFailed = true;
                        compositor.PipelineCache.ReleaseRenderPipeline(pipelineKey);
                        compositor.PipelineCache.ReleaseShader(shaderKey);
                        activePipeline = null;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    ProGpuSceneDiagnostics.WriteLine($"[ShaderToy Render] Error compiling shader or pipeline: {ex.Message}");
                    p.IsFailed = true;
                    return;
                }
            }

            if (activePipeline == null) return;

            // 3. Uniform buffer management
            if (_usedCount >= _pool.Count)
            {
                var buf = new GpuBuffer(compositor.Context, 64, BufferUsage.Uniform | BufferUsage.CopyDst, $"ShaderToy Uniforms {_pool.Count}");
                var bgl = _toyBindGroupLayout;

                var bgEntries = stackalloc BindGroupEntry[1];
                bgEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = buf.BufferPtr,
                    Offset = 0,
                    Size = 64
                };

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = bgl,
                    EntryCount = 1,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr($"ShaderToy Param BG {_pool.Count}")
                };

                var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                SilkMarshal.Free((nint)bgDesc.Label);

                _pool.Add(new ToyGpuResources { UniformBuffer = buf, BindGroupPtr = (nint)bg });
            }

            var gpuRes = _pool[_usedCount++];
            gpuRes.UniformBuffer.WriteSingle(new ShaderToyUniforms
            {
                Resolution = p.Resolution,
                Time = p.Time,
                TimeDelta = p.TimeDelta,
                Frame = (int)p.Frame,
                FrameRate = p.FrameRate,
                Pad0 = 0f,
                Mouse = p.Mouse,
                Date = p.Date
            });

            // 4. Set vertex & index buffers
            var vertexBuffer = compositor.VectorVertexBuffer.BufferPtr;
            wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, vertexBuffer, 0, compositor.VectorVertexBuffer.Size);
            wgpu.RenderPassEncoderSetIndexBuffer(pass, compositor.VectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, compositor.VectorIndexBuffer.Size);

            // Bind Groups:
            // @group(0): Projection matrices & camera (provided by Compositor)
            var group0 = isOffscreen ? compositor.VectorUniformBindGroupOffscreen : compositor.VectorUniformBindGroup;
            wgpu.RenderPassEncoderSetBindGroup(pass, 0, group0, 0, null);

            // @group(1): ShaderToy uniform inputs
            wgpu.RenderPassEncoderSetBindGroup(pass, 1, (BindGroup*)gpuRes.BindGroupPtr, 0, null);

            // @group(2): active opacity mask, or a dummy white mask when no mask is active
            wgpu.RenderPassEncoderSetBindGroup(pass, 2, compositor.GetMaskBindGroup(dc.MaskTexture, isOffscreen), 0, null);

            // Set pipeline & draw indexed
            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
        }
    }
}
