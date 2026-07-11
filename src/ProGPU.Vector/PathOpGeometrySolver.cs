using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;

namespace ProGPU.Vector
{
    public static unsafe class PathOpGeometrySolver
    {
        public const uint MaxOutputSegmentsPerInputSegment = 15;
        public const uint MinimumOutputSegments = 64;

        [StructLayout(LayoutKind.Sequential)]
        private struct PathOpDispatchUniforms
        {
            public uint Op;
            public uint MaxDestSegments;
            public uint Pad1;
            public uint Pad2;
        }

        public static uint ComputeMaxDestinationSegmentCount(int segmentCountA, int segmentCountB)
        {
            if (segmentCountA < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCountA));
            }

            if (segmentCountB < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCountB));
            }

            ulong inputSegmentCount = (ulong)segmentCountA + (ulong)segmentCountB;
            ulong requiredSegmentCount = inputSegmentCount * MaxOutputSegmentsPerInputSegment;
            if (requiredSegmentCount < MinimumOutputSegments)
            {
                return MinimumOutputSegments;
            }

            if (requiredSegmentCount > uint.MaxValue)
            {
                throw new InvalidOperationException("Path operation output segment count exceeds the supported WebGPU buffer size.");
            }

            return (uint)requiredSegmentCount;
        }

        public static PathGeometry Combine(PathGeometry pathA, PathGeometry pathB, int op)
        {
            var result = new PathGeometry { FillRule = GetOutputFillRule(pathA, pathB, op) };

            if (op == 0 && IsContainedAxisAlignedRectangle(pathA, pathB))
            {
                result.FillRule = FillRule.EvenOdd;
                CopyFigures(pathA.Figures, result.Figures);
                CopyFigures(pathB.Figures, result.Figures);
                return result;
            }
            // CPU Fast Path for Empty Geometries
            bool emptyA = pathA.Figures.Count == 0 || IsEmptyFigures(pathA.Figures);
            bool emptyB = pathB.Figures.Count == 0 || IsEmptyFigures(pathB.Figures);

            if (emptyA && emptyB) return result;

            if (emptyA)
            {
                if (op == 2 || op == 3) // Union (2) or XOR (3)
                {
                    result.FillRule = pathB.FillRule;
                    CopyFigures(pathB.Figures, result.Figures);
                }
                else if (op == 4) // Reverse Difference (4)
                {
                    result.FillRule = pathB.FillRule;
                    CopyFigures(pathB.Figures, result.Figures);
                }
                return result;
            }

            if (emptyB)
            {
                if (op == 0 || op == 2 || op == 3) // Difference (0), Union (2) or XOR (3)
                {
                    result.FillRule = pathA.FillRule;
                    CopyFigures(pathA.Figures, result.Figures);
                }
                return result;
            }

            // GPU Solver Path
            var context = WgpuContext.Current;
            if (context == null && WgpuContext.TryGetFirstActiveContext(out var activeContext))
            {
                context = activeContext;
            }

            if (context == null)
            {
                throw new InvalidOperationException("No active WgpuContext found for GPU Path Operation solver.");
            }

            lock (context.RenderLock)
            {
                // Compile path A and path B
                var (recsA, segsA) = CompilePath(pathA, out _, out _, out _, out _);
                var (recsB, segsB) = CompilePath(pathB, out _, out _, out _, out _);

                if (recsA.Length == 0 || segsA.Length == 0)
                {
                    if (op == 2 || op == 3 || op == 4)
                    {
                        result.FillRule = pathB.FillRule;
                        CopyFigures(pathB.Figures, result.Figures);
                    }
                    return result;
                }
                if (recsB.Length == 0 || segsB.Length == 0)
                {
                    if (op == 0 || op == 2 || op == 3)
                    {
                        result.FillRule = pathA.FillRule;
                        CopyFigures(pathA.Figures, result.Figures);
                    }
                    return result;
                }

                GpuBuffer? recordsBufferA = null;
                GpuBuffer? segmentsBufferA = null;
                GpuBuffer? recordsBufferB = null;
                GpuBuffer? segmentsBufferB = null;
                GpuBuffer? destRecordBuffer = null;
                GpuBuffer? destSegmentsBuffer = null;
                GpuBuffer? uniformBuffer = null;
                RenderPipelineCache? cache = null;
                BindGroupLayout* bindGroupLayoutGeom = null;
                BindGroup* bgGeom = null;
                BindGroupLayout* bindGroupLayoutFinal = null;
                BindGroup* bgFinal = null;
                CommandEncoder* encoder = null;
                CommandBuffer* cmdBuffer = null;
                Silk.NET.WebGPU.Buffer* stagingBuffer = null;
                bool stagingBufferMapped = false;

                try
                {
                    // Setup buffers
                    recordsBufferA = new GpuBuffer(context, (uint)(recsA.Length * Marshal.SizeOf<GpuPathRecord>()), BufferUsage.Storage | BufferUsage.CopyDst, "Path A Records Buffer");
                    recordsBufferA.Write(new ReadOnlySpan<GpuPathRecord>(recsA));

                    segmentsBufferA = new GpuBuffer(context, (uint)(segsA.Length * Marshal.SizeOf<GpuPathSegment>()), BufferUsage.Storage | BufferUsage.CopyDst, "Path A Segments Buffer");
                    segmentsBufferA.Write(new ReadOnlySpan<GpuPathSegment>(segsA));

                    recordsBufferB = new GpuBuffer(context, (uint)(recsB.Length * Marshal.SizeOf<GpuPathRecord>()), BufferUsage.Storage | BufferUsage.CopyDst, "Path B Records Buffer");
                    recordsBufferB.Write(new ReadOnlySpan<GpuPathRecord>(recsB));

                    segmentsBufferB = new GpuBuffer(context, (uint)(segsB.Length * Marshal.SizeOf<GpuPathSegment>()), BufferUsage.Storage | BufferUsage.CopyDst, "Path B Segments Buffer");
                    segmentsBufferB.Write(new ReadOnlySpan<GpuPathSegment>(segsB));

                    uint maxDestSegments = ComputeMaxDestinationSegmentCount(segsA.Length, segsB.Length);

                    destRecordBuffer = new GpuBuffer(context, (uint)Marshal.SizeOf<GpuPathRecord>(), BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc, "Dest Path Record Buffer");

                    uint destSegmentsSize = (uint)(16 + maxDestSegments * Marshal.SizeOf<GpuPathSegment>());
                    destSegmentsBuffer = new GpuBuffer(context, destSegmentsSize, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc, "Dest Path Segments Buffer");
                    uint zero = 0;
                    context.Wgpu.QueueWriteBuffer(context.Queue, destSegmentsBuffer.BufferPtr, 0, &zero, 4);

                    // Compile shaders using pipeline cache
                    cache = new RenderPipelineCache(context);
                    var geometryModule = cache.GetOrCreateShader("PathOpGeometry", Shaders.PathOpGeometryShader, "PathOpGeometryShader");
                    var geometryPipeline = cache.GetOrCreateComputePipeline("PathOpGeometry", geometryModule, "cs_main");
                    var finalizerModule = cache.GetOrCreateShader("PathOpRecordFinalizer", Shaders.PathOpRecordFinalizerShader, "PathOpRecordFinalizerShader");
                    var finalizerPipeline = cache.GetOrCreateComputePipeline("PathOpRecordFinalizer", finalizerModule, "cs_main");

                    // Write uniform buffer
                    var uniforms = new PathOpDispatchUniforms
                    {
                        Op = (uint)op,
                        MaxDestSegments = maxDestSegments
                    };
                    uniformBuffer = new GpuBuffer(context, (uint)Marshal.SizeOf<PathOpDispatchUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst, "Uniforms Buffer");
                    uniformBuffer.Write(new ReadOnlySpan<PathOpDispatchUniforms>(&uniforms, 1));

                    // Bind groups
                    bindGroupLayoutGeom = context.Wgpu.ComputePipelineGetBindGroupLayout(geometryPipeline, 0);
                    var entriesGeom = stackalloc BindGroupEntry[7];
                    entriesGeom[0] = new BindGroupEntry { Binding = 0, Buffer = uniformBuffer.BufferPtr, Offset = 0, Size = uniformBuffer.Size };
                    entriesGeom[1] = new BindGroupEntry { Binding = 1, Buffer = recordsBufferA.BufferPtr, Offset = 0, Size = recordsBufferA.Size };
                    entriesGeom[2] = new BindGroupEntry { Binding = 2, Buffer = segmentsBufferA.BufferPtr, Offset = 0, Size = segmentsBufferA.Size };
                    entriesGeom[3] = new BindGroupEntry { Binding = 3, Buffer = recordsBufferB.BufferPtr, Offset = 0, Size = recordsBufferB.Size };
                    entriesGeom[4] = new BindGroupEntry { Binding = 4, Buffer = segmentsBufferB.BufferPtr, Offset = 0, Size = segmentsBufferB.Size };
                    entriesGeom[5] = new BindGroupEntry { Binding = 5, Buffer = destRecordBuffer.BufferPtr, Offset = 0, Size = destRecordBuffer.Size };
                    entriesGeom[6] = new BindGroupEntry { Binding = 6, Buffer = destSegmentsBuffer.BufferPtr, Offset = 0, Size = destSegmentsBuffer.Size };

                    var bgDescGeom = new BindGroupDescriptor
                    {
                        Layout = bindGroupLayoutGeom,
                        EntryCount = 7,
                        Entries = entriesGeom
                    };
                    bgGeom = context.Wgpu.DeviceCreateBindGroup(context.Device, &bgDescGeom);

                    bindGroupLayoutFinal = context.Wgpu.ComputePipelineGetBindGroupLayout(finalizerPipeline, 0);
                    var entriesFinal = stackalloc BindGroupEntry[5];
                    entriesFinal[0] = new BindGroupEntry { Binding = 0, Buffer = uniformBuffer.BufferPtr, Offset = 0, Size = uniformBuffer.Size };
                    entriesFinal[1] = new BindGroupEntry { Binding = 1, Buffer = recordsBufferA.BufferPtr, Offset = 0, Size = recordsBufferA.Size };
                    entriesFinal[2] = new BindGroupEntry { Binding = 2, Buffer = recordsBufferB.BufferPtr, Offset = 0, Size = recordsBufferB.Size };
                    entriesFinal[3] = new BindGroupEntry { Binding = 3, Buffer = destRecordBuffer.BufferPtr, Offset = 0, Size = destRecordBuffer.Size };
                    entriesFinal[4] = new BindGroupEntry { Binding = 4, Buffer = destSegmentsBuffer.BufferPtr, Offset = 0, Size = 16 };

                    var bgDescFinal = new BindGroupDescriptor
                    {
                        Layout = bindGroupLayoutFinal,
                        EntryCount = 5,
                        Entries = entriesFinal
                    };
                    bgFinal = context.Wgpu.DeviceCreateBindGroup(context.Device, &bgDescFinal);

                    // Encoder
                    var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Path Op Solver Geometry Encoder") };
                    encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
                    SilkMarshal.Free((nint)encoderDesc.Label);

                    // Pass 1
                    var passDesc = new ComputePassDescriptor();
                    var pass = context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
                    context.Wgpu.ComputePassEncoderSetPipeline(pass, geometryPipeline);
                    context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bgGeom, 0, null);
                    uint workgroups = (uint)((segsA.Length + segsB.Length + 63) / 64);
                    if (workgroups == 0) workgroups = 1;
                    context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, workgroups, 1, 1);
                    context.Wgpu.ComputePassEncoderEnd(pass);
                    context.Wgpu.ComputePassEncoderRelease(pass);

                    // Pass 2
                    var passDescFinal = new ComputePassDescriptor();
                    var passFinal = context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDescFinal);
                    context.Wgpu.ComputePassEncoderSetPipeline(passFinal, finalizerPipeline);
                    context.Wgpu.ComputePassEncoderSetBindGroup(passFinal, 0, bgFinal, 0, null);
                    context.Wgpu.ComputePassEncoderDispatchWorkgroups(passFinal, 1, 1, 1);
                    context.Wgpu.ComputePassEncoderEnd(passFinal);
                    context.Wgpu.ComputePassEncoderRelease(passFinal);

                    // Create staging buffer to read segments back to CPU
                    var stagingBufferDesc = new BufferDescriptor
                    {
                        Size = destSegmentsSize,
                        Usage = BufferUsage.MapRead | BufferUsage.CopyDst
                    };
                    stagingBuffer = context.Wgpu.DeviceCreateBuffer(context.Device, &stagingBufferDesc);

                    // Copy output segments to staging buffer
                    context.Wgpu.CommandEncoderCopyBufferToBuffer(encoder, destSegmentsBuffer.BufferPtr, 0, stagingBuffer, 0, destSegmentsSize);

                    // Submit
                    var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Path Op Solver Submit") };
                    cmdBuffer = context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
                    SilkMarshal.Free((nint)cmdDesc.Label);

                    context.Wgpu.QueueSubmit(context.Queue, 1, &cmdBuffer);
                    context.Wgpu.CommandBufferRelease(cmdBuffer);
                    cmdBuffer = null;
                    context.Wgpu.CommandEncoderRelease(encoder);
                    encoder = null;

                    // Map staging buffer to read back count and segments
                    var mapSignal = new System.Threading.ManualResetEventSlim(false);
                    BufferMapAsyncStatus mapStatus = BufferMapAsyncStatus.ValidationError;

                    var onMapped = PfnBufferMapCallback.From((status, userData) =>
                    {
                        mapStatus = status;
                        mapSignal.Set();
                    });

                    context.Wgpu.BufferMapAsync(stagingBuffer, MapMode.Read, 0, destSegmentsSize, onMapped, null);

                    // Poll
                    var swTimeout = System.Diagnostics.Stopwatch.StartNew();
                    while (!mapSignal.IsSet)
                    {
                        context.WaitIdle();
                        System.Threading.Thread.Sleep(1);
                        if (swTimeout.ElapsedMilliseconds > 5000)
                        {
                            throw new TimeoutException("WebGPU BufferMapAsync timed out after 5 seconds during path op solver readback.");
                        }
                    }

                    if (mapStatus != BufferMapAsyncStatus.Success)
                    {
                        throw new InvalidOperationException($"Failed to map readback buffer. WebGPU Status: {mapStatus}");
                    }

                    stagingBufferMapped = true;
                    void* mappedPtr = context.Wgpu.BufferGetConstMappedRange(stagingBuffer, 0, destSegmentsSize);
                    if (mappedPtr != null)
                    {
                        uint count = *(uint*)mappedPtr;
                        if (count > maxDestSegments) count = maxDestSegments;

                        GpuPathSegment* segs = (GpuPathSegment*)((byte*)mappedPtr + 16);
                        var outputSegs = new GpuPathSegment[count];
                        for (uint i = 0; i < count; i++)
                        {
                            outputSegs[i] = segs[i];
                        }

                        // Reconstruct path figures
                        var figures = ReconstructFigures(outputSegs);
                        result.Figures.AddRange(figures);
                    }
                }
                finally
                {
                    if (stagingBuffer != null)
                    {
                        if (stagingBufferMapped)
                        {
                            context.Wgpu.BufferUnmap(stagingBuffer);
                        }

                        context.Wgpu.BufferDestroy(stagingBuffer);
                        context.Wgpu.BufferRelease(stagingBuffer);
                    }

                    if (cmdBuffer != null)
                    {
                        context.Wgpu.CommandBufferRelease(cmdBuffer);
                    }

                    if (encoder != null)
                    {
                        context.Wgpu.CommandEncoderRelease(encoder);
                    }

                    if (bgGeom != null)
                    {
                        context.Wgpu.BindGroupRelease(bgGeom);
                    }

                    if (bindGroupLayoutGeom != null)
                    {
                        context.Wgpu.BindGroupLayoutRelease(bindGroupLayoutGeom);
                    }

                    if (bgFinal != null)
                    {
                        context.Wgpu.BindGroupRelease(bgFinal);
                    }

                    if (bindGroupLayoutFinal != null)
                    {
                        context.Wgpu.BindGroupLayoutRelease(bindGroupLayoutFinal);
                    }

                    recordsBufferA?.Dispose();
                    segmentsBufferA?.Dispose();
                    recordsBufferB?.Dispose();
                    segmentsBufferB?.Dispose();
                    destRecordBuffer?.Dispose();
                    destSegmentsBuffer?.Dispose();
                    uniformBuffer?.Dispose();
                    cache?.Dispose();
                }
            }

            return result;
        }

        private static FillRule GetOutputFillRule(PathGeometry pathA, PathGeometry pathB, int op)
        {
            if (pathA.FillRule == FillRule.EvenOdd || pathB.FillRule == FillRule.EvenOdd)
            {
                return FillRule.EvenOdd;
            }

            return op == 4 ? pathB.FillRule : pathA.FillRule;
        }

        private static bool IsContainedAxisAlignedRectangle(PathGeometry outer, PathGeometry inner)
        {
            if (!TryGetAxisAlignedRectangleBounds(outer, out var outerMin, out var outerMax) ||
                !TryGetAxisAlignedRectangleBounds(inner, out var innerMin, out var innerMax))
            {
                return false;
            }

            const float epsilon = 0.0001f;
            return innerMin.X >= outerMin.X - epsilon &&
                   innerMin.Y >= outerMin.Y - epsilon &&
                   innerMax.X <= outerMax.X + epsilon &&
                   innerMax.Y <= outerMax.Y + epsilon;
        }

        private static bool TryGetAxisAlignedRectangleBounds(
            PathGeometry path,
            out Vector2 min,
            out Vector2 max)
        {
            min = default;
            max = default;
            if (path.IsCombined || path.Figures.Count != 1)
            {
                return false;
            }

            var figure = path.Figures[0];
            if (!figure.IsFilled || !figure.IsClosed || figure.Segments.Count < 4)
            {
                return false;
            }

            var points = new List<Vector2>(figure.Segments.Count + 1) { figure.StartPoint };
            var current = figure.StartPoint;
            for (var i = 0; i < figure.Segments.Count; i++)
            {
                Vector2 next;
                if (figure.Segments[i] is LineSegment line)
                {
                    next = line.Point;
                }
                else if (figure.Segments[i] is ArcSegment arc &&
                         (MathF.Abs(arc.Size.X) <= 0.0001f || MathF.Abs(arc.Size.Y) <= 0.0001f))
                {
                    next = arc.Point;
                }
                else
                {
                    return false;
                }

                var delta = next - current;
                if (MathF.Abs(delta.X) > 0.0001f && MathF.Abs(delta.Y) > 0.0001f)
                {
                    return false;
                }

                points.Add(next);
                current = next;
            }

            if (!path.TryGetBounds(out min, out max) || max.X <= min.X || max.Y <= min.Y)
            {
                return false;
            }

            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var liesOnBoundary = MathF.Abs(point.X - min.X) <= 0.0001f ||
                                     MathF.Abs(point.X - max.X) <= 0.0001f ||
                                     MathF.Abs(point.Y - min.Y) <= 0.0001f ||
                                     MathF.Abs(point.Y - max.Y) <= 0.0001f;
                if (!liesOnBoundary)
                {
                    return false;
                }
            }

            double twiceArea = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var first = points[i];
                var second = points[(i + 1) % points.Count];
                twiceArea += (double)first.X * second.Y - (double)second.X * first.Y;
            }

            var rectangleArea = (double)(max.X - min.X) * (max.Y - min.Y);
            var polygonArea = Math.Abs(twiceArea) * 0.5;
            return Math.Abs(polygonArea - rectangleArea) <= Math.Max(0.0001, rectangleArea * 0.0001);
        }

        private static bool IsEmptyFigures(List<PathFigure> figures)
        {
            for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
            {
                var figure = figures[figureIndex];
                if (figure.Segments.Count > 0) return false;
            }
            return true;
        }

        private static void CopyFigures(List<PathFigure> src, List<PathFigure> dest)
        {
            for (int figureIndex = 0; figureIndex < src.Count; figureIndex++)
            {
                var figure = src[figureIndex];
                var newFigure = new PathFigure(figure.StartPoint, figure.IsClosed) { IsFilled = figure.IsFilled };
                var figureSegments = figure.Segments;
                for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
                {
                    var seg = figureSegments[segmentIndex];
                    if (seg is LineSegment line) newFigure.Segments.Add(new LineSegment(line.Point, line.IsSmoothJoin, line.IsStroked));
                    else if (seg is QuadraticBezierSegment quad) newFigure.Segments.Add(new QuadraticBezierSegment(quad.ControlPoint, quad.Point, quad.IsSmoothJoin, quad.IsStroked));
                    else if (seg is CubicBezierSegment cubic) newFigure.Segments.Add(new CubicBezierSegment(cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point, cubic.IsSmoothJoin, cubic.IsStroked));
                    else if (seg is ArcSegment arc) newFigure.Segments.Add(new ArcSegment(arc.Point, arc.Size, arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection, arc.IsSmoothJoin, arc.IsStroked));
                }
                dest.Add(newFigure);
            }
        }

        public static List<PathFigure> ReconstructFigures(GpuPathSegment[] segments)
        {
            var figures = new List<PathFigure>();
            if (segments.Length == 0) return figures;

            var unused = new List<GpuPathSegment>(segments);

            while (unused.Count > 0)
            {
                var currentSeg = unused[0];
                unused.RemoveAt(0);

                var figure = new PathFigure(currentSeg.P0, isClosed: false);
                Vector2 currentPt = currentSeg.P0;
                AddSegmentToFigure(figure, currentSeg, ref currentPt);

                bool foundNext = true;
                while (foundNext && unused.Count > 0)
                {
                    foundNext = false;
                    for (int i = 0; i < unused.Count; i++)
                    {
                        var nextSeg = unused[i];
                        if (Vector2.DistanceSquared(nextSeg.P0, currentPt) < 0.25f)
                        {
                            AddSegmentToFigure(figure, nextSeg, ref currentPt);
                            unused.RemoveAt(i);
                            foundNext = true;
                            break;
                        }

                        if (Vector2.DistanceSquared(GetSegmentEndPoint(nextSeg), currentPt) < 0.25f)
                        {
                            nextSeg = ReverseSegment(nextSeg);
                            AddSegmentToFigure(figure, nextSeg, ref currentPt);
                            unused.RemoveAt(i);
                            foundNext = true;
                            break;
                        }
                    }

                    if (Vector2.DistanceSquared(currentPt, figure.StartPoint) < 0.25f)
                    {
                        figure.IsClosed = true;
                        break;
                    }
                }

                figures.Add(figure);
            }

            return figures;
        }

        private static Vector2 GetSegmentEndPoint(GpuPathSegment segment)
        {
            return segment.SegmentType switch
            {
                1 => segment.P2,
                2 => segment.P3,
                _ => segment.P1
            };
        }

        private static GpuPathSegment ReverseSegment(GpuPathSegment segment)
        {
            var result = segment;
            if (segment.SegmentType == 0)
            {
                result.P0 = segment.P1;
                result.P1 = segment.P0;
            }
            else if (segment.SegmentType == 1)
            {
                result.P0 = segment.P2;
                result.P2 = segment.P0;
            }
            else if (segment.SegmentType == 2)
            {
                result.P0 = segment.P3;
                result.P1 = segment.P2;
                result.P2 = segment.P1;
                result.P3 = segment.P0;
            }
            else if (segment.SegmentType == 3)
            {
                var theta1 = BitConverter.UInt32BitsToSingle(segment.Pad0);
                var deltaTheta = BitConverter.UInt32BitsToSingle(segment.Pad1);
                result.P0 = segment.P1;
                result.P1 = segment.P0;
                result.Pad0 = BitConverter.SingleToUInt32Bits(theta1 + deltaTheta);
                result.Pad1 = BitConverter.SingleToUInt32Bits(-deltaTheta);
            }

            return result;
        }

        private static void AddSegmentToFigure(PathFigure figure, GpuPathSegment seg, ref Vector2 currentPt)
        {
            if (seg.SegmentType == 0)
            {
                figure.Segments.Add(new LineSegment(seg.P1));
                currentPt = seg.P1;
            }
            else if (seg.SegmentType == 1)
            {
                figure.Segments.Add(new QuadraticBezierSegment(seg.P1, seg.P2));
                currentPt = seg.P2;
            }
            else if (seg.SegmentType == 2)
            {
                figure.Segments.Add(new CubicBezierSegment(seg.P1, seg.P2, seg.P3));
                currentPt = seg.P3;
            }
            else if (seg.SegmentType == 3)
            {
                float theta1 = BitConverter.UInt32BitsToSingle(seg.Pad0);
                float deltaTheta = BitConverter.UInt32BitsToSingle(seg.Pad1);
                float phi = BitConverter.UInt32BitsToSingle(seg.Pad2);
                float rotationAngle = phi * 180.0f / MathF.PI;
                float rx = seg.P3.X;
                float ry = seg.P3.Y;
                bool isLargeArc = MathF.Abs(deltaTheta) > MathF.PI;
                SweepDirection sweepDir = deltaTheta > 0.0f ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
                figure.Segments.Add(new ArcSegment(seg.P1, new Vector2(rx, ry), rotationAngle, isLargeArc, sweepDir));
                currentPt = seg.P1;
            }
        }

        public static (GpuPathRecord[] Records, GpuPathSegment[] Segments) CompilePath(PathGeometry path, out float localMinX, out float localMinY, out float localMaxX, out float localMaxY)
        {
            var figures = path.Figures;
            var segments = new List<GpuPathSegment>(EstimateSegmentCapacity(figures));
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

            for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
            {
                var figure = figures[figureIndex];
                var figureSegments = figure.Segments;
                if (figureSegments.Count == 0) continue;

                Vector2 currentPoint = figure.StartPoint;
                UpdateBounds(currentPoint);

                for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
                {
                    var segment = figureSegments[segmentIndex];
                    if (segment is LineSegment line)
                    {
                        segments.Add(new GpuPathSegment
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
                        segments.Add(new GpuPathSegment
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
                        segments.Add(new GpuPathSegment
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
                    else if (segment is ArcSegment arc)
                    {
                        if (!ArcSegmentGeometry.TryGetArcCenter(
                            currentPoint, arc.Point, arc.Size, arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection,
                            out Vector2 center, out float theta1, out float deltaTheta, out float rx, out float ry
                        ))
                        {
                            if (currentPoint != arc.Point)
                            {
                                segments.Add(new GpuPathSegment
                                {
                                    P0 = currentPoint,
                                    P1 = arc.Point,
                                    SegmentType = 0
                                });
                            }

                            UpdateBounds(arc.Point);
                            currentPoint = arc.Point;
                            continue;
                        }
                        
                        segments.Add(new GpuPathSegment
                        {
                            P0 = currentPoint,
                            P1 = arc.Point,
                            P2 = center,
                            P3 = new Vector2(rx, ry),
                            SegmentType = 3,
                            Pad0 = BitConverter.SingleToUInt32Bits(theta1),
                            Pad1 = BitConverter.SingleToUInt32Bits(deltaTheta),
                            Pad2 = BitConverter.SingleToUInt32Bits(arc.RotationAngle * MathF.PI / 180.0f)
                        });
                        
                        if (ArcSegmentGeometry.TryGetArcBounds(currentPoint, arc, out Vector2 min, out Vector2 max))
                        {
                            UpdateBounds(min);
                            UpdateBounds(max);
                        }
                        else
                        {
                            UpdateBounds(currentPoint);
                            UpdateBounds(arc.Point);
                        }
                        
                        currentPoint = arc.Point;
                    }
                }

                if (figure.IsClosed && currentPoint != figure.StartPoint)
                {
                    segments.Add(new GpuPathSegment
                    {
                        P0 = currentPoint,
                        P1 = figure.StartPoint,
                        SegmentType = 0
                    });
                    UpdateBounds(figure.StartPoint);
                }
            }

            if (segments.Count == 0)
            {
                localMinX = localMinY = localMaxX = localMaxY = 0f;
                return (Array.Empty<GpuPathRecord>(), Array.Empty<GpuPathSegment>());
            }

            localMinX = minX;
            localMinY = minY;
            localMaxX = maxX;
            localMaxY = maxY;

            var records = new GpuPathRecord[1];
            records[0] = new GpuPathRecord
            {
                StartSegment = 0,
                SegmentCount = (uint)segments.Count,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                FillRule = (uint)path.FillRule
            };

            return (records, CopySegments(segments));
        }

        private static int EstimateSegmentCapacity(List<PathFigure> figures)
        {
            int capacity = 0;
            for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
            {
                var figure = figures[figureIndex];
                int segmentCount = figure.Segments.Count;
                if (segmentCount == 0)
                {
                    continue;
                }

                capacity += segmentCount;
                if (figure.IsClosed)
                {
                    capacity++;
                }
            }

            return capacity;
        }

        private static GpuPathSegment[] CopySegments(List<GpuPathSegment> segments)
        {
            if (segments.Count == 0)
            {
                return Array.Empty<GpuPathSegment>();
            }

            var result = new GpuPathSegment[segments.Count];
            for (int segmentIndex = 0; segmentIndex < result.Length; segmentIndex++)
            {
                result[segmentIndex] = segments[segmentIndex];
            }

            return result;
        }

    }
}
