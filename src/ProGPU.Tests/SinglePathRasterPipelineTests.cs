using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class SinglePathRasterPipelineTests
{
    private const uint Sentinel = 0x5a17c0de;

    [Fact]
    public unsafe void SinglePathEntryMatchesEveryOrdinaryCoverageWordAndUntouchedSlot()
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
            {
                uint width = pathIndex % 3 == 0 ? 1u : pathIndex % 3 == 1 ? 7u : 17u;
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
        Assert.Equal(128, uniforms.Count);
        Assert.Contains(segments, segment => segment.SegmentType == 0);
        for (uint kind = 1; kind <= 5; kind++)
            Assert.Contains(segments, segment => segment.SegmentType == kind);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var cache = new RenderPipelineCache(context);
        var shader = cache.GetOrCreateShader("PathDifferential", Shaders.PathRasterizerShader);
        uint[] reference = Run("cs_main_ordinary");
        uint[] actual = Run("cs_main_single_path");
        Assert.Equal(reference, actual);
        Assert.Contains(actual, word => word != 0 && word != Sentinel);
        Assert.Contains(0u, actual);
        var nonzeroDouble = uniforms.First(item => item.PathIndex == 6 && item.SampleGrid == 8 && item.ScaleX == 1f);
        var evenOddDouble = uniforms.First(item => item.PathIndex == 14 && item.SampleGrid == 8 && item.ScaleX == 1f);
        Assert.Equal(255u, actual[nonzeroDouble.OutputOffsetWords]);
        Assert.Equal(0u, actual[evenOddDouble.OutputOffsetWords]);
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

        // The specialized entry does not admit Boolean/unknown operation bits.
        for (int index = 0; index < uniforms.Count; index++)
        {
            var item = uniforms[index];
            item.PathOpKind = index % 3 == 0 ? 2u : index % 3 == 1 ? 0x80000001u : uint.MaxValue;
            uniforms[index] = item;
        }
        Assert.All(Run("cs_main_single_path"), word => Assert.Equal(Sentinel, word));

        uint[] Run(string entryPoint)
        {
            int pipelinesBefore = cache.ComputePipelineCount;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var pipeline = cache.GetOrCreateComputePipeline(entryPoint, shader, entryPoint);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualAtlasRetainsSeparateLazyPipelinesAndMixedBatchCoverage(bool booleanFirst)
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var atlas = new PathAtlas(context, atlasSize: 128);
        var a = PrimitivePathGeometry.CreateRectangle(0, 0, 12, 12);
        var b = PrimitivePathGeometry.CreateEllipse(new Vector2(7, 7), 6, 5);
        var combined = PathGeometry.CombineDeferred(a, b, PathBooleanOperation.Intersect);
        atlas.RasterizePendingPaths();
        Assert.Equal(0, context.CachedDeviceComputePipelineCount);
        var first = atlas.GetOrCreatePath(booleanFirst ? combined : a, 1f);
        atlas.RasterizePendingPaths();
        Assert.Equal(1, context.CachedDeviceComputePipelineCount);
        byte[] before = atlas.AtlasTexture.ReadPixels();
        Assert.Contains(before, pixel => pixel > 200);
        var second = atlas.GetOrCreatePath(booleanFirst ? a : combined, 1.25f);
        var third = atlas.GetOrCreatePath(PrimitivePathGeometry.CreateRectangle(1, 1, 7, 7), 1.25f);
        atlas.RasterizePendingPaths();
        Assert.Equal(2, context.CachedDeviceComputePipelineCount);
        byte[] after = atlas.AtlasTexture.ReadPixels();
        foreach (var item in new[] { second, third })
            Assert.True(after[(item.Y + item.Height / 2) * 128 + item.X + item.Width / 2] > 200);
        for (uint y = 0; y < first.Height; y++)
            for (uint x = 0; x < first.Width; x++)
                Assert.Equal(before[(first.Y + y) * 128 + first.X + x], after[(first.Y + y) * 128 + first.X + x]);
        atlas.RasterizePendingPaths();
        Assert.Equal(2, context.CachedDeviceComputePipelineCount);
        atlas.Dispose();
        Assert.Equal(0, context.CachedDeviceComputePipelineCount);
        Assert.Equal(0, context.CachedDeviceShaderModuleCount);
    }

    private static IEnumerable<PathGeometry> Shapes()
    {
        yield return PrimitivePathGeometry.CreateRectangle(0, 0, 10, 8);
        yield return PrimitivePathGeometry.CreateEllipse(new Vector2(6, 5), 5, 4);
        foreach (PathSegment segment in new PathSegment[]
        {
            new QuadraticBezierSegment(new Vector2(5, 12), new Vector2(10, 0)),
            new CubicBezierSegment(new Vector2(0, 12), new Vector2(10, 12), new Vector2(10, 0)),
            new RationalQuadraticBezierSegment(new Vector2(5, 12), new Vector2(10, 0), 0.75f),
            new RationalCubicBezierSegment(new Vector2(0, 12), new Vector2(10, 12), new Vector2(10, 0), 0.5f, 1.5f)
        })
        {
            var path = new PathGeometry();
            var figure = new PathFigure(Vector2.Zero, isClosed: true);
            figure.Segments.Add(segment);
            path.Figures.Add(figure);
            yield return path;
        }
        var doubled = PrimitivePathGeometry.CreateRectangle(0, 0, 10, 8);
        doubled.Figures.Add(PrimitivePathGeometry.CreateRectangle(0, 0, 10, 8).Figures[0]);
        yield return doubled;
        var degenerate = new PathGeometry();
        var line = new PathFigure(new Vector2(0, 4), isClosed: true);
        line.Segments.Add(new LineSegment(new Vector2(10, 4)));
        degenerate.Figures.Add(line);
        yield return degenerate;
    }
}
