using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;

static partial class ClipDiagnostics
{
    // Test-only readback of the actual retained resources, after the original
    // bitmap readback. Never rerasterize a substitute path or modify ownership.
    static partial void CapturePortable(string name, string output)
    {
        if (name != "Union") return;
        Type provider = typeof(Graphics).Assembly.GetType("System.Drawing.GpuProvider", throwOnError: true)!;
        var compositor = (Compositor)provider.GetProperty("Compositor")!.GetValue(null)!;
        var context = (WgpuContext)provider.GetProperty("Context")!.GetValue(null)!;
        var masks = (IDictionary)typeof(Compositor).GetField("_maskTextureBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compositor)!;
        var paths = (IDictionary)typeof(PathAtlas).GetField("_paths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compositor.PathAtlas)!;
        var maskEvidence = new List<object>();
        var pathEvidence = new List<object>();
        int index = 0;
        foreach (DictionaryEntry entry in masks)
        {
            var texture = (GpuTexture)entry.Key;
            if (texture.IsDisposed) throw new InvalidOperationException("Diagnostic mask was retired.");
            string file = output + $".Union.mask-{index++}.r8";
            byte[] pixels = ReadMask(context, texture);
            if (pixels.Length != checked((int)(texture.Width * texture.Height)))
                throw new InvalidOperationException("Expected the actual R8 coverage mask.");
            File.WriteAllBytes(file, pixels);
            maskEvidence.Add(new { File = Path.GetFileName(file), texture.Width, texture.Height, Bounds = entry.Value });
        }
        byte[] atlas = compositor.PathAtlas.AtlasTexture.ReadPixels();
        uint atlasWidth = compositor.PathAtlas.AtlasTexture.Width;
        index = 0;
        foreach (DictionaryEntry entry in paths)
        {
            var path = (PathAtlas.PathInfo)entry.Value!;
            string file = output + $".Union.path-{index++}.r8";
            byte[] pixels = new byte[checked((int)(path.Width * path.Height))];
            for (uint y = 0; y < path.Height; y++)
                atlas.AsSpan(checked((int)((path.Y + y) * atlasWidth + path.X)), checked((int)path.Width))
                    .CopyTo(pixels.AsSpan(checked((int)(y * path.Width))));
            File.WriteAllBytes(file, pixels);
            pathEvidence.Add(new { File = Path.GetFileName(file), path.X, path.Y, path.Width, path.Height,
                path.MinX, path.MinY, path.UnscaledMinX, path.UnscaledMinY, path.UnscaledMaxX, path.UnscaledMaxY });
        }
        if (maskEvidence.Count == 0 || pathEvidence.Count == 0)
            throw new InvalidOperationException("Union diagnostic must inspect its actual atlas and mask.");
        File.WriteAllText(output + ".Union.coverage.json", JsonSerializer.Serialize(new
        {
            Provider = context.BackendKind.ToString(), Backend = context.AdapterBackendType.ToString(),
            Adapter = context.AdapterName, AtlasWidth = atlasWidth, Masks = maskEvidence, Paths = pathEvidence
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static unsafe byte[] ReadMask(WgpuContext context, GpuTexture texture)
    {
        // Production masks intentionally lack CopySrc. Inspect their existing
        // TextureBinding through exact integer loads; no sampler, re-render,
        // production usage change or additional coverage quantization.
        if (texture.Format != TextureFormat.R8Unorm)
            throw new InvalidOperationException("Expected an R8 mask.");
        using var cache = new RenderPipelineCache(context);
        var shader = cache.GetOrCreateShader("ClipMaskReadback", """
            @group(0) @binding(0) var mask: texture_2d<f32>;
            @group(0) @binding(1) var<storage, read_write> result: array<u32>;
            @compute @workgroup_size(8, 8)
            fn main(@builtin(global_invocation_id) id: vec3<u32>) {
                let size = textureDimensions(mask);
                if (any(id.xy >= size)) { return; }
                result[id.y * size.x + id.x] = u32(round(textureLoad(mask, vec2<i32>(id.xy), 0).r * 255.0));
            }
            """);
        var pipeline = cache.GetOrCreateComputePipeline("ClipMaskReadback", shader, "main");
        using var output = new GpuBuffer(context, checked(texture.Width * texture.Height * 4), BufferUsage.Storage | BufferUsage.CopySrc);
        var layout = context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);
        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new() { Binding = 0, TextureView = texture.ViewPtr };
        entries[1] = new() { Binding = 1, Buffer = output.BufferPtr, Size = output.Size };
        var descriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 2, Entries = entries };
        var group = context.Api.DeviceCreateBindGroup(context.Device, &descriptor);
        CommandEncoder* encoder = null;
        CommandBuffer* commands = null;
        try
        {
            if (group == null) throw new InvalidOperationException("Mask readback bind group creation failed.");
            var encoderDescriptor = new CommandEncoderDescriptor();
            encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
            var passDescriptor = new ComputePassDescriptor();
            var pass = context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
            context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
            context.Api.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
            context.Api.ComputePassEncoderDispatchWorkgroups(pass, (texture.Width + 7) / 8, (texture.Height + 7) / 8, 1);
            context.Api.ComputePassEncoderEnd(pass);
            context.Api.ComputePassEncoderRelease(pass);
            var commandDescriptor = new CommandBufferDescriptor();
            commands = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
            context.Submit(1, &commands);
            var words = MemoryMarshal.Cast<byte, uint>(output.ReadBytes());
            byte[] pixels = new byte[words.Length];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = checked((byte)words[index]);
            return pixels;
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
