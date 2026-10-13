using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

// Original ProGPU wire records and outline cases; reference WGSL is the complete
// unchanged GlyphRasterizer.wgsl from 8bfe5b885ae9a8dcceccdccb19bb9ebeacd487fb.
internal static class OriginalGlyphCoverageProbe
{
    private const uint Sentinel = 0x5a17c0de;
    [StructLayout(LayoutKind.Sequential)]
    private struct Segment
    {
        public Vector2 P0, P1, P2, P3;
        public uint Kind, Pad0, Pad1, Pad2;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Record
    {
        public uint Start, Count;
        public float MinX, MinY, MaxX, MaxY;
        public uint Pad0, Pad1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Uniform
    {
        public float X, Y, Scale;
        public uint Index, Offset, RowWords, Width, Height;
        public float Phase, AtlasX, AtlasY, Pad;
    }

    public static unsafe int Run(string referenceSource, string candidateSource)
    {
        Segment Line(Vector2 a, Vector2 b) => new() { P0 = a, P1 = b };
        Segment[] rectangle = [Line(new(0, 0), new(16, 0)), Line(new(16, 0), new(16, 16)),
            Line(new(16, 16), new(0, 16)), Line(new(0, 16), new(0, 0))];
        Segment[] curved = [Line(new(-2, 0), new(13, 0)),
            new() { P0 = new(13, 0), P1 = new(18, 8), P2 = new(9, 15), Kind = 1 },
            new() { P0 = new(9, 15), P1 = new(2, 18), P2 = new(-7, 8), P3 = new(-2, 0), Kind = 2 }];
        Segment[] reversed = curved.Reverse().Select(s => s.Kind switch {
            0 => Line(s.P1, s.P0),
            1 => new Segment { P0=s.P2, P1=s.P1, P2=s.P0, Kind=1 },
            _ => new Segment { P0=s.P3, P1=s.P2, P2=s.P1, P3=s.P0, Kind=2 }
        }).ToArray();
        Segment[][] shapes = [rectangle, curved, reversed, [.. curved, .. curved],
            [.. curved, .. reversed],
            [.. rectangle, Line(new(5, 5), new(5, 11)), Line(new(5, 11), new(11, 11)),
             Line(new(11, 11), new(11, 5)), Line(new(11, 5), new(5, 5))],
            [Line(new(0, 0), new(16, 0)), Line(new(16, 0), new(0, 0))], []];
        List<Segment> segments = [];
        List<Record> records = [];
        List<Uniform> cases = [];
        uint words = 4;
        foreach (Segment[] shape in shapes)
        {
            records.Add(new Record { Start=(uint)segments.Count, Count=(uint)shape.Length,
                MinX=-7, MinY=0, MaxX=18, MaxY=18 });
            segments.AddRange(shape);
            foreach (uint width in new uint[] {0, 1, 2, 3, 4, 7, 16, 17})
            foreach (float scale in new[] {0.875f, 1f, 1.125f})
            foreach (float phase in new[] {0f, 0.3125f})
            {
                uint rowWords = (width+3)/4+3;
                cases.Add(new Uniform { X=-8, Y=-20, Scale=scale, Phase=phase,
                    Index=(uint)records.Count-1, Offset=words, RowWords=rowWords, Width=width, Height=23 });
                words += rowWords*23+4;
            }
        }
        Assert.Equal(48, Marshal.SizeOf<Segment>());
        Assert.Equal(32, Marshal.SizeOf<Record>());
        Assert.Equal(48, Marshal.SizeOf<Uniform>());
        Assert.Equal(384, cases.Count);
        using var context = new WgpuContext();
        context.Initialize(null);
        Console.WriteLine($"Glyph original differential: {context.AdapterBackendType}/{context.SelectedDx12ShaderCompiler}, {context.AdapterName}");
        using var cache = new RenderPipelineCache(context);
        uint[] baseline = Raster("baseline", referenceSource);
        uint[] actual = Raster("candidate", candidateSource);
        Assert.Equal(baseline, actual);
        Assert.Contains(actual, x => x != 0 && x != Sentinel);
        Assert.Contains(0u, actual);
        bool[] written = new bool[words];
        foreach (Uniform c in cases)
            for (uint y=0; y<c.Height; y++)
                for (uint x=0; x<(c.Width+3)/4; x++)
                {
                    uint index=c.Offset+y*c.RowWords+x;
                    written[index]=true;
                    if (x==c.Width/4 && c.Width%4!=0)
                        Assert.Equal(0u, actual[index] >> (int)(c.Width%4*8));
                }
        for (int i=0;i<written.Length;i++)
            if (!written[i]) Assert.Equal(Sentinel, actual[i]);
        Console.WriteLine($"PASS {cases.Count} original glyph cases, {words} exact words, padding/zero-width/tails/overdispatch");
        return cases.Count;

        uint[] Raster(string key, string source)
        {
            var shader=cache.GetOrCreateShader(key, source);
            var pipeline=cache.GetOrCreateComputePipeline(key, shader, "cs_main");
            var layout=context.Api.ComputePipelineGetBindGroupLayout(pipeline,0);
            using var recordBuffer=new GpuBuffer(context,(uint)(records.Count*32),BufferUsage.Storage|BufferUsage.CopyDst);
            using var segmentBuffer=new GpuBuffer(context,(uint)(segments.Count*48),BufferUsage.Storage|BufferUsage.CopyDst);
            using var output=new GpuBuffer(context,words*4,BufferUsage.Storage|BufferUsage.CopyDst|BufferUsage.CopySrc);
            recordBuffer.Write<Record>(CollectionsMarshal.AsSpan(records));
            segmentBuffer.Write<Segment>(CollectionsMarshal.AsSpan(segments));
            output.Write<uint>(Enumerable.Repeat(Sentinel,(int)words).ToArray());
            List<GpuBuffer> buffers=[];
            List<nint> groups=[];
            CommandEncoder* encoder=null;
            CommandBuffer* commands=null;
            try
            {
                var encoderDescriptor=new CommandEncoderDescriptor();
                encoder=context.Api.DeviceCreateCommandEncoder(context.Device,&encoderDescriptor);
                var entries=stackalloc BindGroupEntry[4];
                foreach (Uniform c in cases)
                {
                    var uniform=new GpuBuffer(context,48,BufferUsage.Uniform|BufferUsage.CopyDst);
                    buffers.Add(uniform);uniform.Write<Uniform>(new[]{c});
                    entries[0]=new(){Binding=0,Buffer=uniform.BufferPtr,Size=48};
                    entries[1]=new(){Binding=1,Buffer=recordBuffer.BufferPtr,Size=recordBuffer.Size};
                    entries[2]=new(){Binding=2,Buffer=segmentBuffer.BufferPtr,Size=segmentBuffer.Size};
                    entries[3]=new(){Binding=3,Buffer=output.BufferPtr,Size=output.Size};
                    var descriptor=new BindGroupDescriptor{Layout=layout,EntryCount=4,Entries=entries};
                    var group=context.Api.DeviceCreateBindGroup(context.Device,&descriptor);
                    Assert.True(group!=null);groups.Add((nint)group);
                    var passDescriptor=new ComputePassDescriptor();
                    var pass=context.Api.CommandEncoderBeginComputePass(encoder,&passDescriptor);
                    context.Api.ComputePassEncoderSetPipeline(pass,pipeline);
                    context.Api.ComputePassEncoderSetBindGroup(pass,0,group,0,null);
                    context.Api.ComputePassEncoderDispatchWorkgroups(pass,2,2,1);
                    context.Api.ComputePassEncoderEnd(pass);context.Api.ComputePassEncoderRelease(pass);
                }
                var commandDescriptor=new CommandBufferDescriptor();
                commands=context.Api.CommandEncoderFinish(encoder,&commandDescriptor);
                context.Submit(1,&commands);
                return MemoryMarshal.Cast<byte,uint>(output.ReadBytes()).ToArray();
            }
            finally
            {
                context.WaitIdle();
                if(commands!=null) context.Api.CommandBufferRelease(commands);
                if(encoder!=null) context.Api.CommandEncoderRelease(encoder);
                foreach(nint group in groups) context.Api.BindGroupRelease((BindGroup*)group);
                foreach(var buffer in buffers) buffer.Dispose();
                if(layout!=null) context.Api.BindGroupLayoutRelease(layout);
            }
        }
    }
}
