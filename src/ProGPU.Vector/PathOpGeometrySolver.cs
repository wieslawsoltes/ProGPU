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
        public static PathGeometry Combine(PathGeometry pathA, PathGeometry pathB, int op)
        {
            var result = new PathGeometry();

            // CPU Fast Path for Empty Geometries
            bool emptyA = pathA.Figures.Count == 0 || IsEmptyFigures(pathA.Figures);
            bool emptyB = pathB.Figures.Count == 0 || IsEmptyFigures(pathB.Figures);

            if (emptyA && emptyB) return result;

            if (emptyA)
            {
                if (op == 2 || op == 3) // Union (2) or XOR (3)
                {
                    CopyFigures(pathB.Figures, result.Figures);
                }
                else if (op == 4) // Reverse Difference (4)
                {
                    CopyFigures(pathB.Figures, result.Figures);
                }
                return result;
            }

            if (emptyB)
            {
                if (op == 0 || op == 2 || op == 3) // Difference (0), Union (2) or XOR (3)
                {
                    CopyFigures(pathA.Figures, result.Figures);
                }
                return result;
            }

            // GPU Solver Path
            var context = WgpuContext.Current;
            if (context == null)
            {
                var active = WgpuContext.ActiveContexts;
                if (active.Count > 0)
                {
                    context = active[0];
                }
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
                    if (op == 2 || op == 3 || op == 4) CopyFigures(pathB.Figures, result.Figures);
                    return result;
                }
                if (recsB.Length == 0 || segsB.Length == 0)
                {
                    if (op == 0 || op == 2 || op == 3) CopyFigures(pathA.Figures, result.Figures);
                    return result;
                }

                // Setup buffers
                var recordsBufferA = new GpuBuffer(context, (uint)(recsA.Length * Marshal.SizeOf<GpuPathRecord>()), BufferUsage.Storage | BufferUsage.CopyDst, "Path A Records Buffer");
                recordsBufferA.Write(new ReadOnlySpan<GpuPathRecord>(recsA));

                var segmentsBufferA = new GpuBuffer(context, (uint)(segsA.Length * Marshal.SizeOf<GpuPathSegment>()), BufferUsage.Storage | BufferUsage.CopyDst, "Path A Segments Buffer");
                segmentsBufferA.Write(new ReadOnlySpan<GpuPathSegment>(segsA));

                var recordsBufferB = new GpuBuffer(context, (uint)(recsB.Length * Marshal.SizeOf<GpuPathRecord>()), BufferUsage.Storage | BufferUsage.CopyDst, "Path B Records Buffer");
                recordsBufferB.Write(new ReadOnlySpan<GpuPathRecord>(recsB));

                var segmentsBufferB = new GpuBuffer(context, (uint)(segsB.Length * Marshal.SizeOf<GpuPathSegment>()), BufferUsage.Storage | BufferUsage.CopyDst, "Path B Segments Buffer");
                segmentsBufferB.Write(new ReadOnlySpan<GpuPathSegment>(segsB));

                uint maxDestSegments = (uint)((segsA.Length + segsB.Length) * 2 + 16);
                if (maxDestSegments < 64) maxDestSegments = 64;

                var destRecordBuffer = new GpuBuffer(context, (uint)Marshal.SizeOf<GpuPathRecord>(), BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc, "Dest Path Record Buffer");
                
                uint destSegmentsSize = (uint)(16 + maxDestSegments * Marshal.SizeOf<GpuPathSegment>());
                var destSegmentsBuffer = new GpuBuffer(context, destSegmentsSize, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc, "Dest Path Segments Buffer");
                uint zero = 0;
                context.Wgpu.QueueWriteBuffer(context.Queue, destSegmentsBuffer.BufferPtr, 0, &zero, 4);

                // Compile shaders using pipeline cache
                var cache = new RenderPipelineCache(context);
                var geometryModule = cache.GetOrCreateShader("PathOpGeometry", Shaders.PathOpGeometryShader, "PathOpGeometryShader");
                var geometryPipeline = cache.GetOrCreateComputePipeline("PathOpGeometry", geometryModule, "cs_main");
                var finalizerModule = cache.GetOrCreateShader("PathOpRecordFinalizer", Shaders.PathOpRecordFinalizerShader, "PathOpRecordFinalizerShader");
                var finalizerPipeline = cache.GetOrCreateComputePipeline("PathOpRecordFinalizer", finalizerModule, "cs_main");

                // Write uniform buffer
                var uniforms = new PathOpUniforms { Op = (uint)op };
                var uniformBuffer = new GpuBuffer(context, (uint)Marshal.SizeOf<PathOpUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst, "Uniforms Buffer");
                uniformBuffer.Write(new ReadOnlySpan<PathOpUniforms>(&uniforms, 1));

                // Bind groups
                var bindGroupLayoutGeom = context.Wgpu.ComputePipelineGetBindGroupLayout(geometryPipeline, 0);
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
                var bgGeom = context.Wgpu.DeviceCreateBindGroup(context.Device, &bgDescGeom);

                var bindGroupLayoutFinal = context.Wgpu.ComputePipelineGetBindGroupLayout(finalizerPipeline, 0);
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
                var bgFinal = context.Wgpu.DeviceCreateBindGroup(context.Device, &bgDescFinal);

                // Encoder
                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Path Op Solver Geometry Encoder") };
                var encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
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
                var stagingBuffer = context.Wgpu.DeviceCreateBuffer(context.Device, &stagingBufferDesc);

                // Copy output segments to staging buffer
                context.Wgpu.CommandEncoderCopyBufferToBuffer(encoder, destSegmentsBuffer.BufferPtr, 0, stagingBuffer, 0, destSegmentsSize);

                // Submit
                var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Path Op Solver Submit") };
                var cmdBuffer = context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
                SilkMarshal.Free((nint)cmdDesc.Label);

                context.Wgpu.QueueSubmit(context.Queue, 1, &cmdBuffer);
                context.Wgpu.CommandBufferRelease(cmdBuffer);
                context.Wgpu.CommandEncoderRelease(encoder);

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
                        context.Wgpu.BufferDestroy(stagingBuffer);
                        context.Wgpu.BufferRelease(stagingBuffer);
                        throw new TimeoutException("WebGPU BufferMapAsync timed out after 5 seconds during path op solver readback.");
                    }
                }

                if (mapStatus != BufferMapAsyncStatus.Success)
                {
                    context.Wgpu.BufferDestroy(stagingBuffer);
                    context.Wgpu.BufferRelease(stagingBuffer);
                    throw new InvalidOperationException($"Failed to map readback buffer. WebGPU Status: {mapStatus}");
                }

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

                    // Unmap and release staging buffer
                    context.Wgpu.BufferUnmap(stagingBuffer);
                    context.Wgpu.BufferDestroy(stagingBuffer);
                    context.Wgpu.BufferRelease(stagingBuffer);

                    // Reconstruct path figures
                    var figures = ReconstructFigures(outputSegs);
                    result.Figures.AddRange(figures);
                }
                else
                {
                    context.Wgpu.BufferUnmap(stagingBuffer);
                    context.Wgpu.BufferDestroy(stagingBuffer);
                    context.Wgpu.BufferRelease(stagingBuffer);
                }

                // Cleanup temporary GPU buffers
                recordsBufferA.Dispose();
                segmentsBufferA.Dispose();
                recordsBufferB.Dispose();
                segmentsBufferB.Dispose();
                destRecordBuffer.Dispose();
                destSegmentsBuffer.Dispose();
                uniformBuffer.Dispose();

                context.Wgpu.BindGroupRelease(bgGeom);
                context.Wgpu.BindGroupLayoutRelease(bindGroupLayoutGeom);
                context.Wgpu.BindGroupRelease(bgFinal);
                context.Wgpu.BindGroupLayoutRelease(bindGroupLayoutFinal);

                cache.Dispose();
            }

            return result;
        }

        private static bool IsEmptyFigures(List<PathFigure> figures)
        {
            foreach (var figure in figures)
            {
                if (figure.Segments.Count > 0) return false;
            }
            return true;
        }

        private static void CopyFigures(List<PathFigure> src, List<PathFigure> dest)
        {
            foreach (var figure in src)
            {
                var newFigure = new PathFigure(figure.StartPoint, figure.IsClosed) { IsFilled = figure.IsFilled };
                foreach (var seg in figure.Segments)
                {
                    if (seg is LineSegment line) newFigure.Segments.Add(new LineSegment(line.Point));
                    else if (seg is QuadraticBezierSegment quad) newFigure.Segments.Add(new QuadraticBezierSegment(quad.ControlPoint, quad.Point));
                    else if (seg is CubicBezierSegment cubic) newFigure.Segments.Add(new CubicBezierSegment(cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point));
                    else if (seg is ArcSegment arc) newFigure.Segments.Add(new ArcSegment(arc.Point, arc.Size, arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection));
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
            var segments = new List<GpuPathSegment>();
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

            foreach (var figure in path.Figures)
            {
                if (figure.Segments.Count == 0) continue;

                Vector2 currentPoint = figure.StartPoint;
                UpdateBounds(currentPoint);

                foreach (var segment in figure.Segments)
                {
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
                        CalculateArcCenter(
                            currentPoint, arc.Point, arc.Size, arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection,
                            out Vector2 center, out float theta1, out float deltaTheta, out float rx, out float ry
                        );
                        
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
                        
                        UpdateBounds(currentPoint);
                        UpdateBounds(arc.Point);
                        float phi = arc.RotationAngle * MathF.PI / 180.0f;
                        float cosPhi = MathF.Cos(phi);
                        float sinPhi = MathF.Sin(phi);
                        for (int step = 1; step < 8; step++)
                        {
                            float t = (float)step / 8.0f;
                            float theta = theta1 + t * deltaTheta;
                            float cosT = MathF.Cos(theta);
                            float sinT = MathF.Sin(theta);
                            var p = new Vector2(
                                rx * cosT * cosPhi - ry * sinT * sinPhi + center.X,
                                rx * cosT * sinPhi + ry * sinT * cosPhi + center.Y
                            );
                            UpdateBounds(p);
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
                MaxY = maxY
            };

            return (records, segments.ToArray());
        }

        private static void CalculateArcCenter(
            Vector2 start, Vector2 end, Vector2 radii, float rotationAngleDegrees, bool isLargeArc, SweepDirection sweepDirection,
            out Vector2 center, out float theta1, out float deltaTheta, out float rx, out float ry)
        {
            rx = MathF.Abs(radii.X);
            ry = MathF.Abs(radii.Y);
            
            float phi = rotationAngleDegrees * MathF.PI / 180.0f;
            float cosPhi = MathF.Cos(phi);
            float sinPhi = MathF.Sin(phi);
            
            float dx = (start.X - end.X) * 0.5f;
            float dy = (start.Y - end.Y) * 0.5f;
            float x1p = cosPhi * dx + sinPhi * dy;
            float y1p = -sinPhi * dx + cosPhi * dy;
            
            float prx = rx * rx;
            float pry = ry * ry;
            float px1p = x1p * x1p;
            float py1p = y1p * y1p;
            
            float radiiCheck = px1p / prx + py1p / pry;
            if (radiiCheck > 1.0f)
            {
                float sq = MathF.Sqrt(radiiCheck);
                rx *= sq;
                ry *= sq;
                prx = rx * rx;
                pry = ry * ry;
            }
            
            float sign = (isLargeArc == (sweepDirection == SweepDirection.Clockwise)) ? -1.0f : 1.0f;
            float sqTerm = (prx * pry - prx * py1p - pry * px1p) / (prx * py1p + pry * px1p);
            if (sqTerm < 0.0f) sqTerm = 0.0f;
            float coef = sign * MathF.Sqrt(sqTerm);
            float cxp = coef * ((rx * y1p) / ry);
            float cyp = coef * -((ry * x1p) / rx);
            
            center = new Vector2(
                cosPhi * cxp - sinPhi * cyp + (start.X + end.X) * 0.5f,
                sinPhi * cxp + cosPhi * cyp + (start.Y + end.Y) * 0.5f
            );
            
            float ux = (x1p - cxp) / rx;
            float uy = (y1p - cyp) / ry;
            float vx = (-x1p - cxp) / rx;
            float vy = (-y1p - cyp) / ry;
            
            theta1 = MathF.Atan2(uy, ux);
            float theta2 = MathF.Atan2(vy, vx);
            
            deltaTheta = theta2 - theta1;
            if (sweepDirection == SweepDirection.Clockwise)
            {
                if (deltaTheta < 0) deltaTheta += 2.0f * MathF.PI;
            }
            else
            {
                if (deltaTheta > 0) deltaTheta -= 2.0f * MathF.PI;
            }
        }
    }
}
