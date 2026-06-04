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
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ShaderToyUniforms
    {
        public Vector3 Resolution;
        public float Time;
        public float TimeDelta;
        public float Frame;
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

        public Vector3 Resolution { get; set; }
        public float Time { get; set; }
        public float TimeDelta { get; set; }
        public float Frame { get; set; }
        public float FrameRate { get; set; }
        public Vector4 Mouse { get; set; }
        public Vector4 Date { get; set; }
    }

    public class ShaderToyExtensionPipeline : ICompositorExtension
    {
        private const string VertexAndHeaderShader = @"
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct ShaderToyUniforms {
    iResolution: vec3<f32>,
    iTime: f32,
    iTimeDelta: f32,
    iFrame: f32,
    iFrameRate: f32,
    _pad0: f32,
    iMouse: vec4<f32>,
    iDate: vec4<f32>,
};

@group(1) @binding(0) var<uniform> inputs: ShaderToyUniforms;

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
";

        private const string FragmentWrapperShader = @"
@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let fragCoord = vec2<f32>(input.texCoord.x * inputs.iResolution.x, (1.0 - input.texCoord.y) * inputs.iResolution.y);
    return mainImage(fragCoord);
}
";

        private struct ToyGpuResources
        {
            public GpuBuffer UniformBuffer;
            public nint BindGroupPtr; // BindGroup*
        }

        private readonly List<ToyGpuResources> _pool = new();
        private int _usedCount;

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
            if (string.IsNullOrEmpty(p.ShaderKey)) return;

            var wgpu = compositor.Context.Wgpu;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            // 1. Check for dynamic recompiles and release old pipeline
            if (!string.IsNullOrEmpty(p.OldShaderKey))
            {
                compositor.PipelineCache.ReleaseRenderPipeline(p.OldShaderKey + "_offscreen");
                compositor.PipelineCache.ReleaseRenderPipeline(p.OldShaderKey + "_onscreen");
                compositor.PipelineCache.ReleaseShader(p.OldShaderKey);
                p.OldShaderKey = string.Empty;
            }

            // 2. Fetch or Compile Shader module & Pipeline
            string pipelineKey = isOffscreen ? p.ShaderKey + "_offscreen" : p.ShaderKey + "_onscreen";
            RenderPipeline* activePipeline = null;

            try
            {
                var cache = compositor.PipelineCache;
                // Double check if pipeline layout exists
                activePipeline = cache.GetOrCreateRenderPipeline(
                    pipelineKey,
                    null // Will look it up if we compiled the shader module under ShaderKey first
                );
            }
            catch
            {
                // Not compiled yet or compilation failed
            }

            if (activePipeline == null)
            {
                try
                {
                    // Build full shader code
                    string fullShaderCode = VertexAndHeaderShader + "\n" + p.ShaderSource + "\n" + FragmentWrapperShader;
                    var shaderModule = compositor.PipelineCache.GetOrCreateShader(p.ShaderKey, fullShaderCode, "ShaderToy WGSL Module");

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

                    activePipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                        pipelineKey,
                        shaderModule,
                        vertexBufferLayouts: layouts,
                        topology: PrimitiveTopology.TriangleList,
                        targetFormat: isOffscreen ? TextureFormat.Rgba8Unorm : compositor.Context.SwapChainFormat,
                        sampleCount: isOffscreen ? 1u : 4u
                    );

                    Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ShaderToy Render] Error compiling shader or pipeline: {ex.Message}");
                    return;
                }
            }

            if (activePipeline == null) return;

            // 3. Uniform buffer management
            if (_usedCount >= _pool.Count)
            {
                var buf = new GpuBuffer(compositor.Context, 64, BufferUsage.Uniform | BufferUsage.CopyDst, $"ShaderToy Uniforms {_pool.Count}");
                var bgl = wgpu.RenderPipelineGetBindGroupLayout(activePipeline, 1);

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
                Frame = p.Frame,
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

            // Set pipeline & draw indexed
            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
        }
    }
}
