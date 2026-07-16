using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public class AcisSolidExtensionPipeline : ICompositorExtension
    {
        private static readonly string AcisSolidShaderCode = ShaderResource.Load(typeof(AcisSolidExtensionPipeline), "AcisSolid.wgsl");

        private unsafe RenderPipeline* _cachedPipeline;
        private unsafe RenderPipeline* _cachedPipelineOffscreen;
        private unsafe BindGroup* _cachedBindGroup;
        private unsafe BindGroup* _cachedBindGroupOffscreen;
        private int _cachedRecordGen = -1;

        private readonly List<GpuAcisRecord> _dynamicRecords = new();
        private readonly List<GpuAcisEdge> _dynamicEdges = new();
        private GpuBuffer? _dynamicRecordsBuffer;
        private GpuBuffer? _dynamicEdgesBuffer;

        public void BeginFrame(Compositor compositor)
        {
            _dynamicRecords.Clear();
            _dynamicEdges.Clear();
        }

        private class AcisStaticBuilder
        {
            public readonly List<GpuAcisRecord> Records = new();
            public readonly List<GpuAcisEdge> Edges = new();
        }

        public void BeginStaticCompile(Compositor compositor, StaticCompilationContext context)
        {
            context.SetBuilder(1, new AcisStaticBuilder());
        }

        public void EndStaticCompile(Compositor compositor, StaticCompilationContext context, DxfStaticBuffer staticBuffer)
        {
            if (context.GetBuilder(1) is AcisStaticBuilder builder && builder.Records.Count > 0)
            {
                var state = new AcisStaticState(compositor.Context, builder.Records.ToArray(), builder.Edges.ToArray());
                staticBuffer.SetExtensionState(1, state);
            }
        }

        private class AcisStaticState : IDisposable
        {
            public GpuBuffer RecordsBuffer { get; }
            public GpuBuffer EdgesBuffer { get; }
#if DEBUG
            public GpuAcisRecord[] RecordsSnapshot { get; }
#endif

            public AcisStaticState(WgpuContext context, GpuAcisRecord[] records, GpuAcisEdge[] edges)
            {
#if DEBUG
                RecordsSnapshot = (GpuAcisRecord[])records.Clone();
#endif

                uint recordsSize = (uint)Math.Max(1, records.Length) * (uint)Marshal.SizeOf<GpuAcisRecord>();
                RecordsBuffer = new GpuBuffer(context, recordsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static ACIS Records Buffer");
                if (records.Length > 0) RecordsBuffer.Write(new ReadOnlySpan<GpuAcisRecord>(records));
                else RecordsBuffer.WriteSingle(new GpuAcisRecord());

                uint edgesSize = (uint)Math.Max(1, edges.Length) * (uint)Marshal.SizeOf<GpuAcisEdge>();
                EdgesBuffer = new GpuBuffer(context, edgesSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static ACIS Edges Buffer");
                if (edges.Length > 0) EdgesBuffer.Write(new ReadOnlySpan<GpuAcisEdge>(edges));
                else EdgesBuffer.WriteSingle(new GpuAcisEdge());
            }

            public void Dispose()
            {
                RecordsBuffer.Dispose();
                EdgesBuffer.Dispose();
            }
        }

        private void EnsureDynamicBuffers(WgpuContext context)
        {
            uint reqRecordsSize = (uint)Math.Max(1, _dynamicRecords.Count) * (uint)Marshal.SizeOf<GpuAcisRecord>();
            if (_dynamicRecordsBuffer == null || _dynamicRecordsBuffer.Size < reqRecordsSize)
            {
                _dynamicRecordsBuffer?.Dispose();
                _dynamicRecordsBuffer = new GpuBuffer(context, reqRecordsSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic ACIS Records Buffer");
            }

            uint reqEdgesSize = (uint)Math.Max(1, _dynamicEdges.Count) * (uint)Marshal.SizeOf<GpuAcisEdge>();
            if (_dynamicEdgesBuffer == null || _dynamicEdgesBuffer.Size < reqEdgesSize)
            {
                _dynamicEdgesBuffer?.Dispose();
                _dynamicEdgesBuffer = new GpuBuffer(context, reqEdgesSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic ACIS Edges Buffer");
            }
        }

        public void Dispose()
        {
            _dynamicRecordsBuffer?.Dispose();
            _dynamicEdgesBuffer?.Dispose();
        }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            if (cmd.Pen == null) return;

            ReadOnlySpan<Line3D> edgesSpan = provider != null
                ? provider.GetLines3D(cmd.Line3DBufferOffset, cmd.Line3DBufferCount)
                : cmd.Edges3D is { } edges
                    ? CollectionsMarshal.AsSpan(edges)
                    : ReadOnlySpan<Line3D>.Empty;

            if (edgesSpan.IsEmpty) return;

            float penBrushIdx = compositor.RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
            float thickness = cmd.Pen.Thickness;

            List<GpuAcisEdge> edgesList;
            List<GpuAcisRecord> recordsList;

            if (compositor.ActiveCompilationContext != null && compositor.ActiveCompilationContext.GetBuilder(1) is AcisStaticBuilder builder)
            {
                edgesList = builder.Edges;
                recordsList = builder.Records;
            }
            else
            {
                edgesList = _dynamicEdges;
                recordsList = _dynamicRecords;
            }

            uint startEdge = (uint)edgesList.Count;
            uint edgeCount = (uint)edgesSpan.Length;

            foreach (var edge in edgesSpan)
            {
                edgesList.Add(new GpuAcisEdge
                {
                    P0 = new Vector4(edge.Start, 0f),
                    P1 = new Vector4(edge.End, 0f)
                });
            }

            uint acisRecordIndex = (uint)recordsList.Count;
            recordsList.Add(new GpuAcisRecord
            {
                Transform = transform,
                Color = penSolidColor,
                StartEdge = startEdge,
                EdgeCount = edgeCount,
                PenThickness = thickness,
                Opacity = cmd.Pen.Brush.Opacity * compositor.ActiveOpacity
            });

            int startIndex = compositor.VectorIndices.Count;

            int originalVertexCount = compositor.VectorVertices.Count;
            CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + (int)edgeCount * 4);
            var vertexSpan = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, (int)edgeCount * 4);

            int originalIndexCount = compositor.VectorIndices.Count;
            CollectionsMarshal.SetCount(compositor.VectorIndices, originalIndexCount + (int)edgeCount * 6);
            var indexSpan = CollectionsMarshal.AsSpan(compositor.VectorIndices).Slice(originalIndexCount, (int)edgeCount * 6);

            for (int i = 0; i < (int)edgeCount; i++)
            {
                uint edgeIdx = startEdge + (uint)i;
                int vOffset = i * 4;
                int iOffset = i * 6;
                uint vStart = (uint)originalVertexCount + (uint)vOffset;

                vertexSpan[vOffset + 0] = new VectorVertex(new Vector2(edgeIdx, 0f), penSolidColor, new Vector2(0f, 0f), penBrushIdx, shapeSize: new Vector2(acisRecordIndex, 0f), shapeType: 10f);
                vertexSpan[vOffset + 1] = new VectorVertex(new Vector2(edgeIdx, 1f), penSolidColor, new Vector2(0f, 0f), penBrushIdx, shapeSize: new Vector2(acisRecordIndex, 0f), shapeType: 10f);
                vertexSpan[vOffset + 2] = new VectorVertex(new Vector2(edgeIdx, 2f), penSolidColor, new Vector2(0f, 0f), penBrushIdx, shapeSize: new Vector2(acisRecordIndex, 0f), shapeType: 10f);
                vertexSpan[vOffset + 3] = new VectorVertex(new Vector2(edgeIdx, 3f), penSolidColor, new Vector2(0f, 0f), penBrushIdx, shapeSize: new Vector2(acisRecordIndex, 0f), shapeType: 10f);

                indexSpan[iOffset + 0] = vStart;
                indexSpan[iOffset + 1] = vStart + 1;
                indexSpan[iOffset + 2] = vStart + 2;
                indexSpan[iOffset + 3] = vStart + 1;
                indexSpan[iOffset + 4] = vStart + 3;
                indexSpan[iOffset + 5] = vStart + 2;
            }

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

            var wgpu = compositor.Context.Api;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            var activePipeline = isOffscreen ? _cachedPipelineOffscreen : _cachedPipeline;
            if (activePipeline == null)
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("AcisSolidShader", AcisSolidShaderCode, "ACIS Solid WGSL Shader");
                
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
                        isOffscreen ? "AcisSolidPipeline_Offscreen" : "AcisSolidPipeline",
                        shaderModule,
                        layouts,
                        topology: PrimitiveTopology.TriangleList,
                        targetFormat: compositor.RenderFormat,
                        sampleCount: isOffscreen ? 1u : compositor.Options.PrimarySampleCount
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

            GpuBuffer vertexBuffer;
            GpuBuffer indexBuffer;
            GpuBuffer uniformBuffer;
            GpuBuffer acisRecordsBuf;
            GpuBuffer acisEdgesBuf;

            if (dc.StaticBuffer is DxfStaticBuffer sb && sb.GetExtensionState(1) is AcisStaticState staticState)
            {
                vertexBuffer = sb.VertexBuffer!;
                indexBuffer = sb.IndexBuffer!;
                uniformBuffer = sb.UniformBuffer!;
                acisRecordsBuf = staticState.RecordsBuffer;
                acisEdgesBuf = staticState.EdgesBuffer;
            }
            else
            {
                vertexBuffer = compositor.VectorVertexBuffer;
                indexBuffer = compositor.VectorIndexBuffer;
                uniformBuffer = compositor.VectorUniformBuffer;

                EnsureDynamicBuffers(compositor.Context);

                if (_dynamicRecords.Count > 0)
                {
                    _dynamicRecordsBuffer!.Write(CollectionsMarshal.AsSpan(_dynamicRecords));
                }
                else
                {
                    var dummy = new GpuAcisRecord();
                    _dynamicRecordsBuffer!.WriteSingle(dummy);
                }

                if (_dynamicEdges.Count > 0)
                {
                    _dynamicEdgesBuffer!.Write(CollectionsMarshal.AsSpan(_dynamicEdges));
                }
                else
                {
                    var dummy = new GpuAcisEdge();
                    _dynamicEdgesBuffer!.WriteSingle(dummy);
                }

                acisRecordsBuf = _dynamicRecordsBuffer!;
                acisEdgesBuf = _dynamicEdgesBuffer!;
            }

            int currentGen = acisRecordsBuf.GetHashCode();
            var activeBg = isOffscreen ? _cachedBindGroupOffscreen : _cachedBindGroup;
            if (activeBg == null || currentGen != _cachedRecordGen)
            {
                _cachedRecordGen = currentGen;

                var bgEntries = stackalloc BindGroupEntry[3];
                bgEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = uniformBuffer.BufferPtr,
                    Offset = 0,
                    Size = 192
                };
                bgEntries[1] = new BindGroupEntry
                {
                    Binding = 1,
                    Buffer = acisRecordsBuf.BufferPtr,
                    Offset = 0,
                    Size = acisRecordsBuf.Size
                };
                bgEntries[2] = new BindGroupEntry
                {
                    Binding = 2,
                    Buffer = acisEdgesBuf.BufferPtr,
                    Offset = 0,
                    Size = acisEdgesBuf.Size
                };

                var pipelineLayout = wgpu.RenderPipelineGetBindGroupLayout(activePipeline, 0);

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = pipelineLayout,
                    EntryCount = 3,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr("ACIS Solid BindGroup")
                };

                if (isOffscreen)
                {
                    if (_cachedBindGroupOffscreen != null) wgpu.BindGroupRelease(_cachedBindGroupOffscreen);
                    _cachedBindGroupOffscreen = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                    activeBg = _cachedBindGroupOffscreen;
                }
                else
                {
                    if (_cachedBindGroup != null) wgpu.BindGroupRelease(_cachedBindGroup);
                    _cachedBindGroup = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                    activeBg = _cachedBindGroup;
                }
                SilkMarshal.Free((nint)bgDesc.Label);
            }

            wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, vertexBuffer.BufferPtr, 0, vertexBuffer.Size);
            wgpu.RenderPassEncoderSetIndexBuffer(pass, indexBuffer.BufferPtr, IndexFormat.Uint32, 0, indexBuffer.Size);

            wgpu.RenderPassEncoderSetBindGroup(pass, 0, activeBg, 0, null);
            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
        }
    }
}
