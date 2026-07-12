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
    public class HatchExtensionPipeline : ICompositorExtension
    {
        private static readonly string HatchShaderCode = ShaderResource.Load(typeof(HatchExtensionPipeline), "Hatch.wgsl");

        private unsafe RenderPipeline* _cachedPipeline;
        private unsafe RenderPipeline* _cachedPipelineOffscreen;
        private unsafe BindGroup* _cachedBindGroup;
        private unsafe BindGroup* _cachedBindGroupOffscreen;
        private int _cachedRecordGen = -1;

        private readonly List<GpuHatchRecord> _dynamicRecords = new();
        private readonly List<GpuHatchSegment> _dynamicSegments = new();
        private GpuBuffer? _dynamicRecordsBuffer;
        private GpuBuffer? _dynamicSegmentsBuffer;

        public void BeginFrame(Compositor compositor)
        {
            _dynamicRecords.Clear();
            _dynamicSegments.Clear();
        }

        private class HatchStaticBuilder
        {
            public readonly List<GpuHatchRecord> Records = new();
            public readonly List<GpuHatchSegment> Segments = new();
        }

        public void BeginStaticCompile(Compositor compositor, StaticCompilationContext context)
        {
            context.SetBuilder(3, new HatchStaticBuilder());
        }

        public void EndStaticCompile(Compositor compositor, StaticCompilationContext context, DxfStaticBuffer staticBuffer)
        {
            if (context.GetBuilder(3) is HatchStaticBuilder builder && builder.Records.Count > 0)
            {
                var state = new HatchStaticState(compositor.Context, builder.Records.ToArray(), builder.Segments.ToArray());
                staticBuffer.SetExtensionState(3, state);
            }
        }

        private class HatchStaticState : IDisposable
        {
            public GpuBuffer RecordsBuffer { get; }
            public GpuBuffer SegmentsBuffer { get; }

            public HatchStaticState(WgpuContext context, GpuHatchRecord[] records, GpuHatchSegment[] segments)
            {
                uint recordsSize = (uint)Math.Max(1, records.Length) * (uint)Marshal.SizeOf<GpuHatchRecord>();
                RecordsBuffer = new GpuBuffer(context, recordsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static Hatch Records Buffer");
                if (records.Length > 0) RecordsBuffer.Write(new ReadOnlySpan<GpuHatchRecord>(records));
                else RecordsBuffer.WriteSingle(new GpuHatchRecord());

                uint segmentsSize = (uint)Math.Max(1, segments.Length) * (uint)Marshal.SizeOf<GpuHatchSegment>();
                SegmentsBuffer = new GpuBuffer(context, segmentsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static Hatch Segments Buffer");
                if (segments.Length > 0) SegmentsBuffer.Write(new ReadOnlySpan<GpuHatchSegment>(segments));
                else SegmentsBuffer.WriteSingle(new GpuHatchSegment());
            }

            public void Dispose()
            {
                RecordsBuffer.Dispose();
                SegmentsBuffer.Dispose();
            }
        }

        private void EnsureDynamicBuffers(WgpuContext context)
        {
            uint reqRecordsSize = (uint)Math.Max(1, _dynamicRecords.Count) * (uint)Marshal.SizeOf<GpuHatchRecord>();
            if (_dynamicRecordsBuffer == null || _dynamicRecordsBuffer.Size < reqRecordsSize)
            {
                _dynamicRecordsBuffer?.Dispose();
                _dynamicRecordsBuffer = new GpuBuffer(context, reqRecordsSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic Hatch Records Buffer");
            }

            uint reqSegmentsSize = (uint)Math.Max(1, _dynamicSegments.Count) * (uint)Marshal.SizeOf<GpuHatchSegment>();
            if (_dynamicSegmentsBuffer == null || _dynamicSegmentsBuffer.Size < reqSegmentsSize)
            {
                _dynamicSegmentsBuffer?.Dispose();
                _dynamicSegmentsBuffer = new GpuBuffer(context, reqSegmentsSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic Hatch Segments Buffer");
            }
        }

        public void Dispose()
        {
            _dynamicRecordsBuffer?.Dispose();
            _dynamicSegmentsBuffer?.Dispose();
        }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            if (cmd.Path == null) return;
            
            List<GpuHatchSegment> segmentsList;
            List<GpuHatchRecord> recordsList;

            if (compositor.ActiveCompilationContext != null && compositor.ActiveCompilationContext.GetBuilder(3) is HatchStaticBuilder builder)
            {
                segmentsList = builder.Segments;
                recordsList = builder.Records;
            }
            else
            {
                segmentsList = _dynamicSegments;
                recordsList = _dynamicRecords;
            }

            int startIndex = compositor.VectorIndices.Count;

            if (cmd.Brush != null)
            {
                float bIdx = compositor.RegisterBrush(cmd.Brush);

                uint startSegment = (uint)segmentsList.Count;
                float minX = float.MaxValue;
                float minY = float.MaxValue;
                float maxX = float.MinValue;
                float maxY = float.MinValue;

                void UpdateBounds(Vector2 p)
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }

                var pathFigures = cmd.Path.Figures;
                for (int figureIndex = 0; figureIndex < pathFigures.Count; figureIndex++)
                {
                    var figure = pathFigures[figureIndex];
                    if (figure.Segments.Count == 0) continue;

                    Vector2 currentPoint = figure.StartPoint;
                    UpdateBounds(currentPoint);

                    var figureSegments = figure.Segments;
                    for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
                    {
                        var segment = figureSegments[segmentIndex];
                        if (segment is LineSegment line)
                        {
                            segmentsList.Add(new GpuHatchSegment
                            {
                                P0 = currentPoint,
                                P1 = line.Point,
                                SegmentType = 0
                            });
                            UpdateBounds(line.Point);
                            currentPoint = line.Point;
                        }
                        else if (segment is QuadraticBezierSegment quad)
                        {
                            segmentsList.Add(new GpuHatchSegment
                            {
                                P0 = currentPoint,
                                P1 = quad.ControlPoint,
                                P2 = quad.Point,
                                SegmentType = 1
                            });
                            UpdateBounds(quad.ControlPoint);
                            UpdateBounds(quad.Point);
                            currentPoint = quad.Point;
                        }
                        else if (segment is CubicBezierSegment cubic)
                        {
                            segmentsList.Add(new GpuHatchSegment
                            {
                                P0 = currentPoint,
                                P1 = cubic.ControlPoint1,
                                P2 = cubic.ControlPoint2,
                                P3 = cubic.Point,
                                SegmentType = 2
                            });
                            UpdateBounds(cubic.ControlPoint1);
                            UpdateBounds(cubic.ControlPoint2);
                            UpdateBounds(cubic.Point);
                            currentPoint = cubic.Point;
                        }
                    }

                    if (figure.IsClosed && currentPoint != figure.StartPoint)
                    {
                        segmentsList.Add(new GpuHatchSegment
                        {
                            P0 = currentPoint,
                            P1 = figure.StartPoint,
                            SegmentType = 0
                        });
                        UpdateBounds(figure.StartPoint);
                    }
                }

                uint segmentCount = (uint)segmentsList.Count - startSegment;
                if (segmentCount == 0) return;

                uint hatchRecordIndex = (uint)recordsList.Count;
                recordsList.Add(new GpuHatchRecord
                {
                    StartSegment = startSegment,
                    SegmentCount = segmentCount,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY
                });

                var v0 = Vector2.Transform(new Vector2(minX, minY), transform);
                var v1 = Vector2.Transform(new Vector2(maxX, minY), transform);
                var v2 = Vector2.Transform(new Vector2(maxX, maxY), transform);
                var v3 = Vector2.Transform(new Vector2(minX, maxY), transform);

                var c0 = new Vector4(minX, minY, hatchRecordIndex, 0f);
                var c1 = new Vector4(maxX, minY, hatchRecordIndex, 0f);
                var c2 = new Vector4(maxX, maxY, hatchRecordIndex, 0f);
                var c3 = new Vector4(minX, maxY, hatchRecordIndex, 0f);

                int originalVertexCount = compositor.VectorVertices.Count;
                CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + 4);
                var vertexSpan = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, 4);

                vertexSpan[0] = new VectorVertex(v0, c0, new Vector2(0f, 0f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
                vertexSpan[1] = new VectorVertex(v1, c1, new Vector2(1f, 0f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
                vertexSpan[2] = new VectorVertex(v2, c2, new Vector2(1f, 1f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
                vertexSpan[3] = new VectorVertex(v3, c3, new Vector2(0f, 1f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);

                int originalIndexCount = compositor.VectorIndices.Count;
                CollectionsMarshal.SetCount(compositor.VectorIndices, originalIndexCount + 6);
                var indexSpan = CollectionsMarshal.AsSpan(compositor.VectorIndices).Slice(originalIndexCount, 6);

                indexSpan[0] = (uint)originalVertexCount;
                indexSpan[1] = (uint)(originalVertexCount + 1);
                indexSpan[2] = (uint)(originalVertexCount + 2);
                indexSpan[3] = (uint)originalVertexCount;
                indexSpan[4] = (uint)(originalVertexCount + 2);
                indexSpan[5] = (uint)(originalVertexCount + 3);

                int indexCount = compositor.VectorIndices.Count - startIndex;
                cmd.PointBufferOffset = startIndex;
                cmd.PointBufferCount = indexCount;
            }
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            if (dc.PointBufferCount <= 0) return;

            var wgpu = compositor.Context.Wgpu;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            var activePipeline = isOffscreen ? _cachedPipelineOffscreen : _cachedPipeline;
            if (activePipeline == null)
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("HatchShader", HatchShaderCode, "Hatch WGSL Shader");
                
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
                        isOffscreen ? "HatchPipeline_Offscreen" : "HatchPipeline",
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

            GpuBuffer vertexBuffer;
            GpuBuffer indexBuffer;
            GpuBuffer uniformBuffer;
            GpuBuffer brushesBuf;
            GpuBuffer gradientStopsBuf;
            GpuBuffer hatchRecordsBuf;
            GpuBuffer hatchSegmentsBuf;

            if (dc.StaticBuffer is DxfStaticBuffer sb && sb.GetExtensionState(3) is HatchStaticState staticState)
            {
                vertexBuffer = sb.VertexBuffer!;
                indexBuffer = sb.IndexBuffer!;
                uniformBuffer = sb.UniformBuffer!;
                brushesBuf = sb.BrushesBuffer!;
                gradientStopsBuf = sb.GradientStopsBuffer!;
                hatchRecordsBuf = staticState.RecordsBuffer;
                hatchSegmentsBuf = staticState.SegmentsBuffer;
            }
            else
            {
                vertexBuffer = compositor.VectorVertexBuffer;
                indexBuffer = compositor.VectorIndexBuffer;
                uniformBuffer = compositor.VectorUniformBuffer;
                brushesBuf = compositor.BrushesStorageBuffer;
                gradientStopsBuf = compositor.GradientStopsStorageBuffer;

                EnsureDynamicBuffers(compositor.Context);

                if (_dynamicRecords.Count > 0)
                {
                    _dynamicRecordsBuffer!.Write(CollectionsMarshal.AsSpan(_dynamicRecords));
                }
                else
                {
                    var dummy = new GpuHatchRecord();
                    _dynamicRecordsBuffer!.WriteSingle(dummy);
                }

                if (_dynamicSegments.Count > 0)
                {
                    _dynamicSegmentsBuffer!.Write(CollectionsMarshal.AsSpan(_dynamicSegments));
                }
                else
                {
                    var dummy = new GpuHatchSegment();
                    _dynamicSegmentsBuffer!.WriteSingle(dummy);
                }

                hatchRecordsBuf = _dynamicRecordsBuffer!;
                hatchSegmentsBuf = _dynamicSegmentsBuffer!;
            }

            int currentGen = HashCode.Combine(hatchRecordsBuf.GetHashCode(), brushesBuf.GetHashCode(), gradientStopsBuf.GetHashCode());
            var activeBg = isOffscreen ? _cachedBindGroupOffscreen : _cachedBindGroup;
            if (activeBg == null || currentGen != _cachedRecordGen)
            {
                _cachedRecordGen = currentGen;

                var bgEntries = stackalloc BindGroupEntry[5];
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
                    Buffer = brushesBuf.BufferPtr,
                    Offset = 0,
                    Size = brushesBuf.Size
                };
                bgEntries[2] = new BindGroupEntry
                {
                    Binding = 2,
                    Buffer = hatchRecordsBuf.BufferPtr,
                    Offset = 0,
                    Size = hatchRecordsBuf.Size
                };
                bgEntries[3] = new BindGroupEntry
                {
                    Binding = 3,
                    Buffer = hatchSegmentsBuf.BufferPtr,
                    Offset = 0,
                    Size = hatchSegmentsBuf.Size
                };
                bgEntries[4] = new BindGroupEntry
                {
                    Binding = 4,
                    Buffer = gradientStopsBuf.BufferPtr,
                    Offset = 0,
                    Size = gradientStopsBuf.Size
                };

                var pipelineLayout = wgpu.RenderPipelineGetBindGroupLayout(activePipeline, 0);

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = pipelineLayout,
                    EntryCount = 5,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr("Hatch BindGroup")
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
