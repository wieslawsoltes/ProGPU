using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public class CustomGridExtensionPipeline : ICompositorExtension
    {
        private static readonly string GridShaderCode = ShaderResource.Load(typeof(CustomGridExtensionPipeline), "CustomGrid.wgsl");

        private unsafe RenderPipeline* _cachedPipeline;
        private unsafe RenderPipeline* _cachedPipelineOffscreen;

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            // Allocate viewport quad vertices: Position from -1 to 1 in normalized device coords (NDC)
            int startIndex = compositor.VectorVertices.Count;
            float dummyBrush = 0f;
            var color = new Vector4(1f, 1f, 1f, 1f);

            int originalVertexCount = compositor.VectorVertices.Count;
            CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(new Vector2(-1f, -1f), color, new Vector2(0f, 0f), dummyBrush, default, 0f, 0f, 7f);
            vertexSpan[1] = new VectorVertex(new Vector2(1f, -1f), color, new Vector2(1f, 0f), dummyBrush, default, 0f, 0f, 7f);
            vertexSpan[2] = new VectorVertex(new Vector2(1f, 1f), color, new Vector2(1f, 1f), dummyBrush, default, 0f, 0f, 7f);
            vertexSpan[3] = new VectorVertex(new Vector2(-1f, 1f), color, new Vector2(0f, 1f), dummyBrush, default, 0f, 0f, 7f);

            int originalIndexCount = compositor.VectorIndices.Count;
            CollectionsMarshal.SetCount(compositor.VectorIndices, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(compositor.VectorIndices).Slice(originalIndexCount, 6);

            indexSpan[0] = (uint)startIndex;
            indexSpan[1] = (uint)(startIndex + 1);
            indexSpan[2] = (uint)(startIndex + 2);
            indexSpan[3] = (uint)startIndex;
            indexSpan[4] = (uint)(startIndex + 2);
            indexSpan[5] = (uint)(startIndex + 3);
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            var wgpu = compositor.Context.Wgpu;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            var activePipeline = isOffscreen ? _cachedPipelineOffscreen : _cachedPipeline;
            if (activePipeline == null)
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("CustomGridShader", GridShaderCode, "Custom Grid WGSL Shader");
                
                // Populate attributes matching VectorVertex structure
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
                        isOffscreen ? "CustomGridPipeline_Offscreen" : "CustomGridPipeline",
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

            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            wgpu.RenderPassEncoderDrawIndexed(pass, 6, 1, (uint)dc.PointBufferOffset, 0, 0);
        }
    }
}
