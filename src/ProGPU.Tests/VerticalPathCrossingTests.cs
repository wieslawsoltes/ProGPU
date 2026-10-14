using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class VerticalPathCrossingTests
{
    private const uint Sentinel = 0x5a17c0de;

    [StructLayout(LayoutKind.Sequential)]
    private struct Crossing
    {
        public Vector4 Low, High;
        public Vector2 A, B;
        public float Y;
        public int Direction;
        public Vector2 Padding;
    }

    // This is only a raw invocation fixture. Both implementations consume the
    // complete independent production shader; no crossing math is reproduced.
    private const string Entry = """
        struct CrossingInput {
            low: vec4<f32>, high: vec4<f32>, a: vec2<f32>, b: vec2<f32>,
            y: f32, direction: i32, padding: vec2<f32>,
        };
        @group(0) @binding(5) var<storage, read> crossingInputs: array<CrossingInput>;
        @compute @workgroup_size(64)
        fn crossing_fixture(@builtin(global_invocation_id) id: vec3<u32>) {
            if (id.x >= arrayLength(&crossingInputs)) { return; }
            let input = crossingInputs[id.x];
            var result = WindingRow(vec4<i32>(0), vec4<i32>(0));
            add_line_crossing(&result, SampleRow(input.low, input.high),
                input.y, input.a, input.b, input.direction);
            for (var lane = 0u; lane < 4u; lane++) {
                coverageOutput[4u + id.x * 12u + lane] = bitcast<u32>(result.low[lane]);
                coverageOutput[8u + id.x * 12u + lane] = bitcast<u32>(result.high[lane]);
            }
        }
        """;

    [Fact]
    public unsafe void ExactVerticalCrossingsMatchOriginalSignedLanesAndUntouchedTails()
    {
        List<Crossing> cases = [];
        foreach (float x in new[] { -1024f, -1023.9375f, -0.125f, -0f, 0.1f, 8f, 1023.9375f, 1024f, 65536.125f })
        foreach (var (bottom, top, y) in new (float, float, float)[]
        {
            (-16, 16, 0), (-1.25f, 8.75f, 0.125f), (-0.13f, 0.87f, 0.27f),
            (-1023.9375f, 1023.9375f, 0.0625f), (-65536, 65536, 0.5f),
            (-float.MaxValue, float.MaxValue, 0),
            (-float.MaxValue, float.MaxValue, float.MaxValue * 0.5f),
        })
        foreach (bool reversed in new[] { false, true })
        foreach (bool nonvertical in new[] { false, true })
        {
            float secondX = nonvertical ? MathF.BitIncrement(x) : x;
            var a = new Vector2(x, bottom);
            var b = new Vector2(secondX, top);
            cases.Add(new Crossing
            {
                A = reversed ? b : a, B = reversed ? a : b, Y = y,
                Direction = reversed ? -1 : 1,
                Low = new(x - 1f, MathF.BitDecrement(x), x, MathF.BitIncrement(x)),
                High = new(x + 1f, x - 0.0625f, x + 0.0625f, x + 0.1f)
            });
        }
        Assert.Equal(64, Marshal.SizeOf<Crossing>());
        Assert.Equal(252, cases.Count);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var cache = new RenderPipelineCache(context);
        uint[] original = Run(RasterCrossingGlyphTests.ReadReference("Path.wgsl"), "OriginalVerticalCrossing");
        uint[] actual = Run(Shaders.PathRasterizerShader, "VerticalCrossing");
        Assert.Equal(original, actual);
        for (int index = 0; index < cases.Count; index++)
        {
            var item = cases[index];
            if (item.A.X == item.B.X && MathF.Abs(item.A.Y) < 100000 && MathF.Abs(item.B.Y) < 100000)
                for (int lane = 0; lane < 8; lane++)
                {
                    float x = lane < 4 ? item.Low[lane] : item.High[lane - 4];
                    uint expected = x < item.A.X ? unchecked((uint)item.Direction) : 0;
                    // WGSL may flush subnormal operands. Their original GPU
                    // behavior is still compared above, across every raw lane.
                    if (!float.IsSubnormal(x))
                        Assert.Equal(expected, actual[4 + index * 12 + lane]);
                }
            for (int lane = 8; lane < 12; lane++)
                Assert.Equal(Sentinel, actual[4 + index * 12 + lane]);
        }
        Assert.All(actual.Take(4), word => Assert.Equal(Sentinel, word));
        Assert.All(actual.TakeLast(4), word => Assert.Equal(Sentinel, word));

        uint[] Run(string source, string name)
        {
            var shader = cache.GetOrCreateShader(name, source + Entry);
            var pipeline = cache.GetOrCreateComputePipeline(name, shader, "crossing_fixture");
            using var input = new GpuBuffer(context, checked((uint)cases.Count * 64), BufferUsage.Storage | BufferUsage.CopyDst);
            uint words = checked((uint)cases.Count * 12 + 8);
            using var output = new GpuBuffer(context, words * 4, BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst);
            input.Write<Crossing>(CollectionsMarshal.AsSpan(cases));
            output.Write<uint>(Enumerable.Repeat(Sentinel, (int)words).ToArray());
            var layout = context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);
            var entries = stackalloc BindGroupEntry[2];
            entries[0] = new() { Binding = 3, Buffer = output.BufferPtr, Size = output.Size };
            entries[1] = new() { Binding = 5, Buffer = input.BufferPtr, Size = input.Size };
            var descriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 2, Entries = entries };
            var group = context.Api.DeviceCreateBindGroup(context.Device, &descriptor);
            var encoderDescriptor = new CommandEncoderDescriptor();
            var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
            CommandBuffer* commands = null;
            try
            {
                Assert.True(group != null && encoder != null);
                var passDescriptor = new ComputePassDescriptor();
                var pass = context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
                context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
                context.Api.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
                context.Api.ComputePassEncoderDispatchWorkgroups(pass, ((uint)cases.Count + 63) / 64 + 1, 1, 1);
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
}
