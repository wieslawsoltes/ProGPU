using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public class Line3DExtensionPipeline : ICompositorExtension
    {
        private static readonly string Line3DShaderCode = ShaderResource.Load(typeof(Line3DExtensionPipeline), "Line3D.wgsl");

        private unsafe RenderPipeline* _cachedPipeline;
        private unsafe RenderPipeline* _cachedPipelineOffscreen;

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            var pen = (cmd.DataParam as Pen) ?? cmd.Pen;
            if (pen == null) return;

            Vector3 p1, p2;
            if (provider != null && cmd.FloatBufferCount >= 6)
            {
                var floats = provider.GetFloats(cmd.FloatBufferOffset, 6);
                p1 = new Vector3(floats[0], floats[1], floats[2]);
                p2 = new Vector3(floats[3], floats[4], floats[5]);
            }
            else
            {
                p1 = cmd.Position3D1;
                p2 = cmd.Position3D2;
            }

            int startIndex = compositor.VectorIndices.Count;
            float penBrushIdx = compositor.RegisterBrush(pen.Brush);
            var penSolidColor = (pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            var p0_trans = Vector3.Transform(p1, transform);
            var p1_trans = Vector3.Transform(p2, transform);
            
            var p0_xy = new Vector2(p0_trans.X, p0_trans.Y);
            var p1_xy = new Vector2(p1_trans.X, p1_trans.Y);
            float thickness = pen.Thickness;

            int originalVertexCount = compositor.VectorVertices.Count;
            CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(p0_xy, penSolidColor, new Vector2(p0_trans.Z, 0f), penBrushIdx, p1_xy, 1f, thickness, 8f);
            vertexSpan[1] = new VectorVertex(p0_xy, penSolidColor, new Vector2(p0_trans.Z, 0f), penBrushIdx, p1_xy, -1f, thickness, 8f);
            vertexSpan[2] = new VectorVertex(p1_xy, penSolidColor, new Vector2(p1_trans.Z, 0f), penBrushIdx, p1_xy, 2f, thickness, 8f);
            vertexSpan[3] = new VectorVertex(p1_xy, penSolidColor, new Vector2(p1_trans.Z, 0f), penBrushIdx, p1_xy, -2f, thickness, 8f);

            int originalIndexCount = compositor.VectorIndices.Count;
            CollectionsMarshal.SetCount(compositor.VectorIndices, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(compositor.VectorIndices).Slice(originalIndexCount, 6);

            indexSpan[0] = (uint)originalVertexCount;
            indexSpan[1] = (uint)(originalVertexCount + 1);
            indexSpan[2] = (uint)(originalVertexCount + 2);
            indexSpan[3] = (uint)(originalVertexCount + 1);
            indexSpan[4] = (uint)(originalVertexCount + 3);
            indexSpan[5] = (uint)(originalVertexCount + 2);

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

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            if (dc.PointBufferCount <= 0) return;

            var wgpu = compositor.Context.Wgpu;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            var activePipeline = isOffscreen ? _cachedPipelineOffscreen : _cachedPipeline;
            if (activePipeline == null)
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("Line3DShader", Line3DShaderCode, "Line3D WGSL Shader");
                
                Span<VertexAttribute> attrs = stackalloc VertexAttribute[8];
                attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // Position
                attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }; // Color
                attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // TexCoord
                attrs[3] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 32, ShaderLocation = 3 };   // BrushIndex
                attrs[4] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 36, ShaderLocation = 4 }; // ShapeSize
                attrs[5] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 44, ShaderLocation = 5 };   // CornerRadius
                attrs[6] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 48, ShaderLocation = 6 };   // StrokeThickness
                attrs[7] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 52, ShaderLocation = 7 };   // ShapeType

                Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];
                fixed (VertexAttribute* attrsPtr = attrs)
                {
                    layouts[0] = new VertexBufferLayout
                    {
                        ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>(),
                        StepMode = VertexStepMode.Vertex,
                        AttributeCount = 8,
                        Attributes = attrsPtr
                    };

                    var pipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                        isOffscreen ? "Line3DPipeline_Offscreen" : "Line3DPipeline",
                        shaderModule,
                        layouts,
                        topology: PrimitiveTopology.TriangleList,
                        targetFormat: compositor.RenderFormat,
                        sampleCount: isOffscreen ? 1u : 4u
                    );

                    if (isOffscreen)
                    {
                        _cachedPipelineOffscreen = pipeline;
                        activePipeline = _cachedPipelineOffscreen;
                    }
                    else
                    {
                        _cachedPipeline = pipeline;
                        activePipeline = _cachedPipeline;
                    }
                }
            }

            var vertexBuffer = compositor.VectorVertexBuffer.BufferPtr;
            wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, vertexBuffer, 0, compositor.VectorVertexBuffer.Size);
            wgpu.RenderPassEncoderSetIndexBuffer(pass, compositor.VectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, compositor.VectorIndexBuffer.Size);

            var bindGroup = isOffscreen ? compositor.VectorUniformBindGroupOffscreen : compositor.VectorUniformBindGroup;
            wgpu.RenderPassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
        }
    }
}
