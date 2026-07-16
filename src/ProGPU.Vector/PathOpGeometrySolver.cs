using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
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

        private sealed class PathOpMapState
        {
            public readonly System.Threading.ManualResetEventSlim Signal = new(false);
            public BufferMapAsyncStatus Status = BufferMapAsyncStatus.ValidationError;
            public GCHandle Handle;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnBufferMapped(BufferMapAsyncStatus status, void* userData)
        {
            var handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is PathOpMapState state)
            {
                state.Status = status;
                state.Signal.Set();
            }

            if (handle.IsAllocated)
            {
                handle.Free();
            }
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
            if (TryCreateImmediateResult(pathA, pathB, op, out var result))
            {
                return result;
            }

            var work = CreateGpuWork(pathA, pathB, op, result);
            if (work == null)
            {
                return result;
            }

            using (work)
            {
                var status = work.MapSynchronously();
                work.CompleteReadback(status);
                return result;
            }
        }

        public static Task<PathGeometry> CombineAsync(PathGeometry pathA, PathGeometry pathB, int op)
        {
            try
            {
                if (TryCreateImmediateResult(pathA, pathB, op, out var result))
                {
                    return Task.FromResult(result);
                }

                var work = CreateGpuWork(pathA, pathB, op, result);
                if (work == null)
                {
                    return Task.FromResult(result);
                }

                return work.MapAsync().ContinueWith(
                    static (task, state) =>
                    {
                        var pending = (PathOpGpuWork)state!;
                        try
                        {
                            pending.CompleteReadback(task.GetAwaiter().GetResult());
                            return pending.Result;
                        }
                        finally
                        {
                            pending.Dispose();
                        }
                    },
                    work,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                return Task.FromException<PathGeometry>(ex);
            }
        }

        private static bool TryCreateImmediateResult(PathGeometry pathA, PathGeometry pathB, int op, out PathGeometry result)
        {
            result = new PathGeometry { FillRule = GetOutputFillRule(pathA, pathB, op) };
            if (op == 0 && IsContainedAxisAlignedRectangle(pathA, pathB))
            {
                result.FillRule = FillRule.EvenOdd;
                CopyFigures(pathA.Figures, result.Figures);
                CopyFigures(pathB.Figures, result.Figures);
                return true;
            }

            bool emptyA = pathA.Figures.Count == 0 || IsEmptyFigures(pathA.Figures);
            bool emptyB = pathB.Figures.Count == 0 || IsEmptyFigures(pathB.Figures);
            if (emptyA && emptyB) return true;
            if (emptyA)
            {
                if (op == 2 || op == 3 || op == 4)
                {
                    result.FillRule = pathB.FillRule;
                    CopyFigures(pathB.Figures, result.Figures);
                }
                return true;
            }
            if (emptyB)
            {
                if (op == 0 || op == 2 || op == 3)
                {
                    result.FillRule = pathA.FillRule;
                    CopyFigures(pathA.Figures, result.Figures);
                }
                return true;
            }
            return false;
        }

        private static PathOpGpuWork? CreateGpuWork(PathGeometry pathA, PathGeometry pathB, int op, PathGeometry result)
        {
            var context = WgpuContext.Current;
            if (context == null && WgpuContext.TryGetFirstActiveContext(out var activeContext))
            {
                context = activeContext;
            }
            if (context == null)
            {
                throw new InvalidOperationException("No active WgpuContext found for GPU Path Operation solver.");
            }

            var (recsA, segsA) = CompilePath(pathA, out _, out _, out _, out _);
            var (recsB, segsB) = CompilePath(pathB, out _, out _, out _, out _);
            if (recsA.Length == 0 || segsA.Length == 0)
            {
                if (op == 2 || op == 3 || op == 4)
                {
                    result.FillRule = pathB.FillRule;
                    CopyFigures(pathB.Figures, result.Figures);
                }
                return null;
            }
            if (recsB.Length == 0 || segsB.Length == 0)
            {
                if (op == 0 || op == 2 || op == 3)
                {
                    result.FillRule = pathA.FillRule;
                    CopyFigures(pathA.Figures, result.Figures);
                }
                return null;
            }

            lock (context.RenderLock)
            {
                var work = new PathOpGpuWork(context, result);
                try
                {
                    work.Initialize(recsA, segsA, recsB, segsB, op);
                    return work;
                }
                catch
                {
                    work.DisposeCore();
                    throw;
                }
            }
        }

        private sealed class PathOpGpuWork : IDisposable
        {
            private readonly WgpuContext _context;
            private GpuBuffer? _recordsBufferA;
            private GpuBuffer? _segmentsBufferA;
            private GpuBuffer? _recordsBufferB;
            private GpuBuffer? _segmentsBufferB;
            private GpuBuffer? _destRecordBuffer;
            private GpuBuffer? _destSegmentsBuffer;
            private GpuBuffer? _uniformBuffer;
            private RenderPipelineCache? _cache;
            private BindGroupLayout* _bindGroupLayoutGeom;
            private BindGroup* _bgGeom;
            private BindGroupLayout* _bindGroupLayoutFinal;
            private BindGroup* _bgFinal;
            private CommandEncoder* _encoder;
            private CommandBuffer* _cmdBuffer;
            private Silk.NET.WebGPU.Buffer* _stagingBuffer;
            private bool _stagingBufferMapped;
            private bool _disposed;
            private uint _maxDestSegments;
            private uint _destSegmentsSize;

            public PathOpGpuWork(WgpuContext context, PathGeometry result)
            {
                _context = context;
                Result = result;
            }

            public PathGeometry Result { get; }

            public void Initialize(
                GpuPathRecord[] recsA,
                GpuPathSegment[] segsA,
                GpuPathRecord[] recsB,
                GpuPathSegment[] segsB,
                int op)
            {
                _recordsBufferA = CreateAndWrite(recsA, BufferUsage.Storage | BufferUsage.CopyDst, "Path A Records Buffer");
                _segmentsBufferA = CreateAndWrite(segsA, BufferUsage.Storage | BufferUsage.CopyDst, "Path A Segments Buffer");
                _recordsBufferB = CreateAndWrite(recsB, BufferUsage.Storage | BufferUsage.CopyDst, "Path B Records Buffer");
                _segmentsBufferB = CreateAndWrite(segsB, BufferUsage.Storage | BufferUsage.CopyDst, "Path B Segments Buffer");
                _maxDestSegments = ComputeMaxDestinationSegmentCount(segsA.Length, segsB.Length);
                _destRecordBuffer = new GpuBuffer(_context, (uint)Unsafe.SizeOf<GpuPathRecord>(), BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc, "Dest Path Record Buffer");
                _destSegmentsSize = checked((uint)(16 + _maxDestSegments * Unsafe.SizeOf<GpuPathSegment>()));
                _destSegmentsBuffer = new GpuBuffer(_context, _destSegmentsSize, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc, "Dest Path Segments Buffer");
                uint zero = 0;
                _context.Api.QueueWriteBuffer(_context.Queue, _destSegmentsBuffer.BufferPtr, 0, &zero, 4);

                _cache = new RenderPipelineCache(_context);
                var geometryModule = _cache.GetOrCreateShader("PathOpGeometry", Shaders.PathOpGeometryShader, "PathOpGeometryShader");
                var geometryPipeline = _cache.GetOrCreateComputePipeline("PathOpGeometry", geometryModule, "cs_main");
                var finalizerModule = _cache.GetOrCreateShader("PathOpRecordFinalizer", Shaders.PathOpRecordFinalizerShader, "PathOpRecordFinalizerShader");
                var finalizerPipeline = _cache.GetOrCreateComputePipeline("PathOpRecordFinalizer", finalizerModule, "cs_main");

                var uniforms = new PathOpDispatchUniforms { Op = (uint)op, MaxDestSegments = _maxDestSegments };
                _uniformBuffer = new GpuBuffer(_context, (uint)Unsafe.SizeOf<PathOpDispatchUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst, "Uniforms Buffer");
                _uniformBuffer.Write(new ReadOnlySpan<PathOpDispatchUniforms>(&uniforms, 1));

                _bindGroupLayoutGeom = _context.Api.ComputePipelineGetBindGroupLayout(geometryPipeline, 0);
                var entriesGeom = stackalloc BindGroupEntry[7];
                entriesGeom[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer.BufferPtr, Size = _uniformBuffer.Size };
                entriesGeom[1] = new BindGroupEntry { Binding = 1, Buffer = _recordsBufferA.BufferPtr, Size = _recordsBufferA.Size };
                entriesGeom[2] = new BindGroupEntry { Binding = 2, Buffer = _segmentsBufferA.BufferPtr, Size = _segmentsBufferA.Size };
                entriesGeom[3] = new BindGroupEntry { Binding = 3, Buffer = _recordsBufferB.BufferPtr, Size = _recordsBufferB.Size };
                entriesGeom[4] = new BindGroupEntry { Binding = 4, Buffer = _segmentsBufferB.BufferPtr, Size = _segmentsBufferB.Size };
                entriesGeom[5] = new BindGroupEntry { Binding = 5, Buffer = _destRecordBuffer.BufferPtr, Size = _destRecordBuffer.Size };
                entriesGeom[6] = new BindGroupEntry { Binding = 6, Buffer = _destSegmentsBuffer.BufferPtr, Size = _destSegmentsBuffer.Size };
                var bgDescGeom = new BindGroupDescriptor { Layout = _bindGroupLayoutGeom, EntryCount = 7, Entries = entriesGeom };
                _bgGeom = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDescGeom);

                _bindGroupLayoutFinal = _context.Api.ComputePipelineGetBindGroupLayout(finalizerPipeline, 0);
                var entriesFinal = stackalloc BindGroupEntry[5];
                entriesFinal[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer.BufferPtr, Size = _uniformBuffer.Size };
                entriesFinal[1] = new BindGroupEntry { Binding = 1, Buffer = _recordsBufferA.BufferPtr, Size = _recordsBufferA.Size };
                entriesFinal[2] = new BindGroupEntry { Binding = 2, Buffer = _recordsBufferB.BufferPtr, Size = _recordsBufferB.Size };
                entriesFinal[3] = new BindGroupEntry { Binding = 3, Buffer = _destRecordBuffer.BufferPtr, Size = _destRecordBuffer.Size };
                entriesFinal[4] = new BindGroupEntry { Binding = 4, Buffer = _destSegmentsBuffer.BufferPtr, Size = 16 };
                var bgDescFinal = new BindGroupDescriptor { Layout = _bindGroupLayoutFinal, EntryCount = 5, Entries = entriesFinal };
                _bgFinal = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDescFinal);

                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Path Op Solver Geometry Encoder") };
                _encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
                SilkMarshal.Free((nint)encoderDesc.Label);

                var passDesc = new ComputePassDescriptor();
                var pass = _context.Api.CommandEncoderBeginComputePass(_encoder, &passDesc);
                _context.Api.ComputePassEncoderSetPipeline(pass, geometryPipeline);
                _context.Api.ComputePassEncoderSetBindGroup(pass, 0, _bgGeom, 0, null);
                uint workgroups = (uint)((segsA.Length + segsB.Length + 63) / 64);
                _context.Api.ComputePassEncoderDispatchWorkgroups(pass, Math.Max(1u, workgroups), 1, 1);
                _context.Api.ComputePassEncoderEnd(pass);
                _context.Api.ComputePassEncoderRelease(pass);

                var passDescFinal = new ComputePassDescriptor();
                var passFinal = _context.Api.CommandEncoderBeginComputePass(_encoder, &passDescFinal);
                _context.Api.ComputePassEncoderSetPipeline(passFinal, finalizerPipeline);
                _context.Api.ComputePassEncoderSetBindGroup(passFinal, 0, _bgFinal, 0, null);
                _context.Api.ComputePassEncoderDispatchWorkgroups(passFinal, 1, 1, 1);
                _context.Api.ComputePassEncoderEnd(passFinal);
                _context.Api.ComputePassEncoderRelease(passFinal);

                var stagingBufferDesc = new BufferDescriptor { Size = _destSegmentsSize, Usage = BufferUsage.MapRead | BufferUsage.CopyDst };
                _stagingBuffer = _context.Api.DeviceCreateBuffer(_context.Device, &stagingBufferDesc);
                _context.Api.CommandEncoderCopyBufferToBuffer(_encoder, _destSegmentsBuffer.BufferPtr, 0, _stagingBuffer, 0, _destSegmentsSize);

                var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Path Op Solver Submit") };
                _cmdBuffer = _context.Api.CommandEncoderFinish(_encoder, &cmdDesc);
                SilkMarshal.Free((nint)cmdDesc.Label);
                var submittedCommandBuffer = _cmdBuffer;
                _context.Api.QueueSubmit(_context.Queue, 1, &submittedCommandBuffer);
                _context.Api.CommandBufferRelease(_cmdBuffer);
                _cmdBuffer = null;
                _context.Api.CommandEncoderRelease(_encoder);
                _encoder = null;
            }

            public Task<BufferMapAsyncStatus> MapAsync()
            {
                return _context.Api.BufferMapAsyncTask(_stagingBuffer, MapMode.Read, 0, _destSegmentsSize);
            }

            public BufferMapAsyncStatus MapSynchronously()
            {
                var mapState = new PathOpMapState();
                mapState.Handle = GCHandle.Alloc(mapState);
                try
                {
                    _context.Api.BufferMapAsync(
                        _stagingBuffer,
                        MapMode.Read,
                        0,
                        _destSegmentsSize,
                        new PfnBufferMapCallback(&OnBufferMapped),
                        (void*)GCHandle.ToIntPtr(mapState.Handle));
                }
                catch
                {
                    if (mapState.Handle.IsAllocated) mapState.Handle.Free();
                    throw;
                }

                var swTimeout = System.Diagnostics.Stopwatch.StartNew();
                while (!mapState.Signal.IsSet)
                {
                    _context.WaitIdle();
                    System.Threading.Thread.Sleep(1);
                    if (swTimeout.ElapsedMilliseconds > 5000)
                    {
                        throw new TimeoutException("WebGPU BufferMapAsync timed out after 5 seconds during path op solver readback.");
                    }
                }
                return mapState.Status;
            }

            public void CompleteReadback(BufferMapAsyncStatus status)
            {
                if (status != BufferMapAsyncStatus.Success)
                {
                    throw new InvalidOperationException($"Failed to map readback buffer. WebGPU Status: {status}");
                }

                lock (_context.RenderLock)
                {
                    _stagingBufferMapped = true;
                    void* mappedPtr = _context.Api.BufferGetConstMappedRange(_stagingBuffer, 0, _destSegmentsSize);
                    if (mappedPtr == null) return;
                    uint count = Math.Min(*(uint*)mappedPtr, _maxDestSegments);
                    var source = (GpuPathSegment*)((byte*)mappedPtr + 16);
                    var output = new GpuPathSegment[count];
                    for (uint index = 0; index < count; index++) output[index] = source[index];
                    Result.Figures.AddRange(ReconstructFigures(output));
                }
            }

            private GpuBuffer CreateAndWrite<T>(T[] values, BufferUsage usage, string label) where T : unmanaged
            {
                var buffer = new GpuBuffer(_context, checked((uint)(values.Length * Unsafe.SizeOf<T>())), usage, label);
                buffer.Write(new ReadOnlySpan<T>(values));
                return buffer;
            }

            public void Dispose()
            {
                lock (_context.RenderLock)
                {
                    DisposeCore();
                }
            }

            public void DisposeCore()
            {
                if (_disposed) return;
                _disposed = true;
                if (_stagingBuffer != null)
                {
                    if (_stagingBufferMapped) _context.Api.BufferUnmap(_stagingBuffer);
                    _context.Api.BufferDestroy(_stagingBuffer);
                    _context.Api.BufferRelease(_stagingBuffer);
                    _stagingBuffer = null;
                }
                if (_cmdBuffer != null) _context.Api.CommandBufferRelease(_cmdBuffer);
                if (_encoder != null) _context.Api.CommandEncoderRelease(_encoder);
                if (_bgGeom != null) _context.Api.BindGroupRelease(_bgGeom);
                if (_bindGroupLayoutGeom != null) _context.Api.BindGroupLayoutRelease(_bindGroupLayoutGeom);
                if (_bgFinal != null) _context.Api.BindGroupRelease(_bgFinal);
                if (_bindGroupLayoutFinal != null) _context.Api.BindGroupLayoutRelease(_bindGroupLayoutFinal);
                _recordsBufferA?.Dispose();
                _segmentsBufferA?.Dispose();
                _recordsBufferB?.Dispose();
                _segmentsBufferB?.Dispose();
                _destRecordBuffer?.Dispose();
                _destSegmentsBuffer?.Dispose();
                _uniformBuffer?.Dispose();
                _cache?.Dispose();
            }
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
