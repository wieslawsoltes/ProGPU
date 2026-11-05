using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class BinaryGlyphVisitOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void ActualGpuVisitLoopPreservesOriginalPerLaneOrder(bool useDawn)
    {
        uint[] widths = [0, 1, 2, 127, 255, 256, 257, 511, 512, 513, 1023, 1024, 1025,
            2047, 2048, 2049, 4095, 4096, 4097, 16383, 16384];
        const int laneWords = 143;
        const int caseWords = 2 + 256 * laneWords;
        int caseCount = widths.Length * 2;
        var input = new uint[caseCount * caseWords];
        Array.Fill(input, uint.MaxValue);
        for (int i = 0; i < caseCount; i++)
        {
            input[i * caseWords] = widths[i / 2];
            input[i * caseWords + 1] = (uint)(i % 2);
        }
        BackendType backend = OperatingSystem.IsWindows() ? BackendType.D3D12 :
            OperatingSystem.IsMacOS() ? BackendType.Metal : BackendType.Vulkan;
        using var dawn = useDawn ? DawnGpuContext.CreateOffscreen(backend, forceFallbackAdapter: false) : null;
        using var stock = useDawn ? null : new WgpuContext();
        var context = dawn?.Context ?? stock!;
        if (stock is not null) stock.Initialize(null);
        using var cache = new RenderPipelineCache(context);
        using var buffer = new GpuBuffer(context, checked((uint)(input.Length * sizeof(uint))),
            BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        buffer.Write<uint>(input);
        string original = ShaderResource.Load(typeof(WgpuContext), "BinaryGlyphRasterizer.wgsl");
        string visitLoop = ExtractFunction(original, "render_scan_visits");
        string source = ShaderResource.Load(typeof(BinaryGlyphVisitOrderTests), "BinaryGlyphVisitTrace.wgsl") +
            "\n" + visitLoop;
        var shader = cache.GetOrCreateShader("BinaryGlyphVisitTrace", source);
        var pipeline = cache.GetOrCreateComputePipeline("BinaryGlyphVisitTrace", shader, "cs_trace");
        var layout = context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);
        var entry = new BindGroupEntry { Binding = 0, Buffer = buffer.BufferPtr, Size = buffer.Size };
        var descriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry };
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
            context.Api.ComputePassEncoderDispatchWorkgroups(pass, checked((uint)caseCount), 1, 1);
            context.Api.ComputePassEncoderEnd(pass);
            context.Api.ComputePassEncoderRelease(pass);
            var commandDescriptor = new CommandBufferDescriptor();
            commands = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
            context.Submit(1, &commands);
            var result = MemoryMarshal.Cast<byte, uint>(buffer.ReadBytes());
            for (int i = 0; i < caseCount; i++)
            {
                uint width = widths[i / 2];
                bool vertical = i % 2 != 0;
                Assert.Equal(width, result[i * caseWords]);
                Assert.Equal((uint)(i % 2), result[i * caseWords + 1]);
                for (uint lane = 0; lane < 256; lane++)
                {
                    // Independent original loops, not the production quotient/remainder
                    // formula or its combined phase/step selection.
                    var expected = new List<(uint Kind, uint Index)>();
                    if (!vertical)
                        for (uint x = lane; x < width; x += 256) expected.Add((0, x));
                    for (uint index = lane; index < 512; index += 256) expected.Add((1, index));
                    expected.Add((2, lane));
                    expected.Add((3, lane));
                    int offset = i * caseWords + 2 + checked((int)lane) * laneWords;
                    Assert.Equal((uint)expected.Count, result[offset]);
                    for (int visit = 0; visit < expected.Count; visit++)
                    {
                        Assert.Equal(expected[visit].Kind, result[offset + 1 + visit * 2]);
                        Assert.Equal(expected[visit].Index, result[offset + 2 + visit * 2]);
                    }
                    for (int untouched = 1 + expected.Count * 2; untouched < laneWords; untouched++)
                        Assert.Equal(uint.MaxValue, result[offset + untouched]);
                }
            }
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

    private static string ExtractFunction(string source, string name)
    {
        int start = source.IndexOf("fn " + name + "(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int body = source.IndexOf('{', start);
        int depth = 1;
        int end = body + 1;
        for (; end < source.Length && depth != 0; end++)
        {
            if (source[end] == '{') depth++;
            else if (source[end] == '}') depth--;
        }
        Assert.Equal(0, depth);
        return source[start..end];
    }
}
