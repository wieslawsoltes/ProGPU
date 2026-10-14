using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class CubicPathRasterPipelineTests
{
    private const uint Sentinel = 0x5a17c0de;

    [Fact]
    public unsafe void CubicEntryMatchesOriginalCoverageAndRejectsEveryOtherCurve()
    {
        List<GpuPathRecord> records = [];
        List<GpuPathSegment> segments = [];
        List<PathUniforms> uniforms = [];
        uint outputWords = 4;
        foreach (FillRule fill in new[] { FillRule.Nonzero, FillRule.EvenOdd })
        foreach (PathGeometry path in Shapes())
        {
            path.FillRule = fill;
            var (compiledRecords, compiledSegments) = PathAtlas.CompileFillPath(path, out _, out _, out _, out _);
            var record = Assert.Single(compiledRecords);
            record.StartSegment += checked((uint)segments.Count);
            uint pathIndex = checked((uint)records.Count);
            records.Add(record);
            segments.AddRange(compiledSegments);
            foreach (uint grid in new uint[] { 1, 2, 4, 8 })
            foreach (bool scaled in new[] { false, true })
            foreach (uint width in new uint[] { 0, 1, 2, 3, 4, 7, 16, 17 })
            {
                uint height = pathIndex % 2 == 0 ? 9u : 13u;
                uint rowWords = (width + 3) / 4 + 3;
                uniforms.Add(new PathUniforms
                {
                    XStart = scaled ? -2.25f : 0f, YStart = scaled ? -1.75f : 0f,
                    ScaleX = scaled ? 1.25f : 1f, ScaleY = scaled ? 0.75f : 1f,
                    PathIndex = pathIndex, OutputOffsetWords = outputWords, OutputRowWords = rowWords,
                    Width = width, Height = height, SampleGrid = grid, PathOpKind = 0,
                    PathIndexB = uint.MaxValue // This entry must never read a second operand.
                });
                outputWords += rowWords * height + 4;
            }
        }
        Assert.Equal(48, Marshal.SizeOf<PathUniforms>());
        Assert.Equal(896, uniforms.Count);
        Assert.All(segments, segment => Assert.True(segment.SegmentType is 0 or 2));
        Assert.Contains(segments, segment => segment.SegmentType == 2);
        using var context = new WgpuContext();
        context.Initialize(null);
        Console.WriteLine($"Path coverage device: provider={context.BackendKind}, backend={context.AdapterBackendType}, adapter={context.AdapterName}");
        using var cache = new RenderPipelineCache(context);
        var shader = cache.GetOrCreateShader("PathDifferential", Shaders.PathRasterizerShader);
        var referenceShader = cache.GetOrCreateShader("OriginalPath", RasterCrossingGlyphTests.ReadReference("Path.wgsl"));
        uint[] reference = Run("cs_main_ordinary");
        uint[] actual = Run("cs_main_cubic_path");
        Assert.Equal(reference, actual);
        Assert.Contains(actual, word => word != 0 && word != Sentinel);
        Assert.Contains(0u, actual);
        bool[] written = new bool[outputWords];
        foreach (var item in uniforms)
            for (uint y = 0; y < item.Height; y++)
                for (uint x = 0; x < (item.Width + 3) / 4; x++)
                {
                    uint index = item.OutputOffsetWords + y * item.OutputRowWords + x;
                    written[index] = true;
                    if (x == item.Width / 4 && item.Width % 4 != 0)
                        Assert.Equal(0u, actual[index] >> checked((int)(item.Width % 4 * 8)));
                }
        for (int index = 0; index < written.Length; index++)
            if (!written[index]) Assert.Equal(Sentinel, actual[index]);

        // Every later unsupported curve must reject the complete raw record.
        // Original source/reference inputs stay independently retained.
        var savedSegments = segments.ToArray();
        foreach (uint kind in new uint[] { 1, 3, 4, 5, uint.MaxValue })
        {
            foreach (var record in records)
            {
                int index = checked((int)(record.StartSegment + record.SegmentCount - 1));
                var segment = segments[index];
                segment.SegmentType = kind;
                segments[index] = segment;
            }
            Assert.All(Run("cs_main_cubic_path"), word => Assert.Equal(Sentinel, word));
        }
        segments.Clear();
        segments.AddRange(savedSegments);

        var savedRecords = records.ToArray();
        foreach (var (start, count) in new (uint, uint)[]
        {
            ((uint)segments.Count + 1, 1),
            ((uint)segments.Count - 1, 2),
            (uint.MaxValue, 2)
        })
        {
            for (int index = 0; index < records.Count; index++)
            {
                var record = savedRecords[index];
                record.StartSegment = start;
                record.SegmentCount = count;
                records[index] = record;
            }
            Assert.All(Run("cs_main_cubic_path"), word => Assert.Equal(Sentinel, word));
        }
        records.Clear();
        records.AddRange(savedRecords);

        // The specialized entry does not admit Boolean/unknown operation bits.
        for (int index = 0; index < uniforms.Count; index++)
        {
            var item = uniforms[index];
            item.PathOpKind = index % 3 == 0 ? 2u : index % 3 == 1 ? 0x80000001u : uint.MaxValue;
            uniforms[index] = item;
        }
        Assert.All(Run("cs_main_cubic_path"), word => Assert.Equal(Sentinel, word));

        uint[] Run(string entryPoint)
        {
            int pipelinesBefore = cache.ComputePipelineCount;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var pipeline = cache.GetOrCreateComputePipeline(entryPoint,
                entryPoint == "cs_main_ordinary" ? referenceShader : shader, entryPoint);
            Console.WriteLine(FormattableString.Invariant(
                $"Path pipeline acquisition: entry={entryPoint}, newPipeline={cache.ComputePipelineCount > pipelinesBefore}, wallMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.000}"));
            using var uniformBuffer = new GpuBuffer(context, checked((uint)(uniforms.Count * 48)), BufferUsage.Storage | BufferUsage.CopyDst);
            using var recordBuffer = new GpuBuffer(context, checked((uint)(records.Count * Marshal.SizeOf<GpuPathRecord>())), BufferUsage.Storage | BufferUsage.CopyDst);
            using var segmentBuffer = new GpuBuffer(context, checked((uint)(segments.Count * Marshal.SizeOf<GpuPathSegment>())), BufferUsage.Storage | BufferUsage.CopyDst);
            using var output = new GpuBuffer(context, outputWords * 4, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
            uniformBuffer.Write<PathUniforms>(CollectionsMarshal.AsSpan(uniforms));
            recordBuffer.Write<GpuPathRecord>(CollectionsMarshal.AsSpan(records));
            segmentBuffer.Write<GpuPathSegment>(CollectionsMarshal.AsSpan(segments));
            output.Write<uint>(Enumerable.Repeat(Sentinel, checked((int)outputWords)).ToArray());
            var layout = context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);
            var entries = stackalloc BindGroupEntry[4];
            entries[0] = new() { Binding = 0, Buffer = uniformBuffer.BufferPtr, Size = uniformBuffer.Size };
            entries[1] = new() { Binding = 1, Buffer = recordBuffer.BufferPtr, Size = recordBuffer.Size };
            entries[2] = new() { Binding = 2, Buffer = segmentBuffer.BufferPtr, Size = segmentBuffer.Size };
            entries[3] = new() { Binding = 3, Buffer = output.BufferPtr, Size = output.Size };
            var descriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 4, Entries = entries };
            var group = context.Api.DeviceCreateBindGroup(context.Device, &descriptor);
            CommandEncoder* encoder = null;
            CommandBuffer* commands = null;
            try
            {
                Assert.True(group != null);
                var encoderDescriptor = new CommandEncoderDescriptor();
                encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
                var passDescriptor = new ComputePassDescriptor();
                var pass = context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
                context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
                context.Api.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
                // Deliberately overdispatch both pixel axes; tails must stay untouched.
                context.Api.ComputePassEncoderDispatchWorkgroups(pass, 2, 2, checked((uint)uniforms.Count));
                context.Api.ComputePassEncoderEnd(pass);
                context.Api.ComputePassEncoderRelease(pass);
                var commandDescriptor = new CommandBufferDescriptor();
                commands = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
                context.Submit(1, &commands);
                return MemoryMarshal.Cast<byte, uint>(output.ReadBytes()).ToArray();
            }
            finally
            {
                context.WaitIdle();
                if (commands != null) context.Api.CommandBufferRelease(commands);
                if (encoder != null) context.Api.CommandEncoderRelease(encoder);
                if (group != null) context.Api.BindGroupRelease(group);
                if (layout != null) context.Api.BindGroupLayoutRelease(layout);
            }
        }
    }

    [Fact]
    public void ActualAtlasKeepsCubicLinearMixedAndBooleanPipelinesLazy()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var atlas = new PathAtlas(context, atlasSize: 256);
        var cubic = Cubic(new(0, 0), new(0, 12), new(10, 12), new(10, 0));
        var line = PrimitivePathGeometry.CreateRectangle(0, 0, 10, 8);
        var quadratic = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero, isClosed: true);
        figure.Segments.Add(new QuadraticBezierSegment(new(5, 12), new(10, 0)));
        quadratic.Figures.Add(figure);
        atlas.RasterizePendingPaths();
        Assert.Equal(0, context.CachedDeviceComputePipelineCount);
        var first = atlas.GetOrCreatePath(cubic, 1f);
        atlas.RasterizePendingPaths();
        Assert.Equal(1, context.CachedDeviceComputePipelineCount);
        byte[] before = atlas.AtlasTexture.ReadPixels();
        Assert.Contains(before, value => value > 0);
        atlas.GetOrCreatePath(line, 1f);
        atlas.RasterizePendingPaths();
        Assert.Equal(2, context.CachedDeviceComputePipelineCount);
        // An unsupported later request prevents specialization of the whole batch.
        atlas.GetOrCreatePath(cubic, 1.25f);
        atlas.GetOrCreatePath(quadratic, 1.25f);
        atlas.RasterizePendingPaths();
        Assert.Equal(3, context.CachedDeviceComputePipelineCount);
        atlas.GetOrCreatePath(PathGeometry.CombineDeferred(
            cubic, line, PathBooleanOperation.Intersect), 1f);
        atlas.RasterizePendingPaths();
        Assert.Equal(4, context.CachedDeviceComputePipelineCount);
        byte[] after = atlas.AtlasTexture.ReadPixels();
        for (uint y = 0; y < first.Height; y++)
            for (uint x = 0; x < first.Width; x++)
                Assert.Equal(before[(first.Y + y) * 256 + first.X + x],
                    after[(first.Y + y) * 256 + first.X + x]);
        atlas.RasterizePendingPaths();
        Assert.Equal(4, context.CachedDeviceComputePipelineCount);
        atlas.Dispose();
        Assert.Equal(0, context.CachedDeviceComputePipelineCount);
    }

    private static PathGeometry Cubic(Vector2 start, Vector2 a, Vector2 b, Vector2 end)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(start, isClosed: true);
        figure.Segments.Add(new CubicBezierSegment(a, b, end));
        path.Figures.Add(figure);
        return path;
    }

    private static IEnumerable<PathGeometry> Shapes()
    {
        yield return PrimitivePathGeometry.CreateRectangle(0, 0, 10, 8);
        yield return Cubic(new(0, 0), new(0, 12), new(10, 12), new(10, 0));
        yield return Cubic(new(0, 0), new(14, -12), new(-4, 16), new(10, 0));
        yield return Cubic(new(10, 0), new(10, 12), new(0, 12), new(0, 0));
        yield return Cubic(new(0, 4), new(0, 4), new(10, 4), new(10, 4));
        yield return Cubic(new(-0.125f, 0.375f), new(8.625f, 16.125f),
            new(-3.25f, -8.75f), new(10.875f, 0.375f));
        var doubled = Cubic(new(0, 0), new(0, 12), new(10, 12), new(10, 0));
        doubled.Figures.Add(Cubic(new(0, 0), new(0, 12), new(10, 12), new(10, 0)).Figures[0]);
        yield return doubled;
    }
}

