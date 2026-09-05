using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;

namespace ProGPU.Samples.Suntrail.Rendering;

public sealed unsafe partial class ProceduralPipeline
{
    // One physical-resolution RGBA32Float image, capped at 96 MiB. No resampling,
    // reduced precision, mip generation, or history accumulation is involved.
    public bool EnableSkyCache { get; set; }
    public long SkyBakeCount { get; private set; }
    public long SkyResidentBytes => _skyTexture is null ? 0 : (long)_skyTexture.Width * _skyTexture.Height * 16;
    private GpuTexture? _skyTexture;
    private GpuBuffer? _skyUniforms, _skyInstance;
    private BindGroup* _skyBakeGroup, _skySampleGroup;
    private BindGroupLayout* _skySampleLayout;
    private PipelineLayout* _skyReplayLayout;
    private readonly nint[] _skyReplay = new nint[2];
    private SkyKey _skyKey;
    private bool _skyReady;
    private readonly record struct SkyKey(Vector2 Size, uint Width, uint Height, float World, bool Dungeon, Vector4 Color, bool WorldShaders);

    public bool TryPrepareDrawCall(Compositor compositor, bool isOffscreen,
        in Compositor.CompositorDrawCall drawCall, out Compositor.CompositorDrawCall preparedDrawCall)
    {
        preparedDrawCall = drawCall;
        _skyReady = false;
        _pagesPrepared = false;
        if (drawCall.DataParam is ProceduralBatch materialBatch) PrepareMaterialPages(compositor, materialBatch);
        if (_pagesPrepared) return false;
        if (!EnableSkyCache || drawCall.DataParam is not ProceduralBatch batch || batch.Count == 0 ||
            _transform != Matrix4x4.Identity) return false;
        var sky = batch.Sprites[0];
        if (sky.Material.X != (float)Artwork.Sky || sky.Bounds != new Vector4(0, 0, batch.Size.X, batch.Size.Y)) return false;
        float pixelWidth = batch.Size.X * compositor.CurrentDpiScale;
        float pixelHeight = batch.Size.Y * compositor.CurrentDpiScale;
        if (!float.IsFinite(pixelWidth) || !float.IsFinite(pixelHeight) || pixelWidth < 1 || pixelHeight < 1 ||
            pixelWidth > 4096 || pixelHeight > 4096 || pixelWidth * pixelHeight > 6_291_456) return false;
        uint width = (uint)MathF.Round(pixelWidth), height = (uint)MathF.Round(pixelHeight);
        // Fractional framebuffer extents cannot be represented by an exact texel replay.
        if (MathF.Abs(width - pixelWidth) > .001f || MathF.Abs(height - pixelHeight) > .001f) return false;
        EnsureResources(compositor);
        var key = new SkyKey(batch.Size, width, height, batch.Scene.Y, batch.IsDungeon, sky.Color, EnableWorldShaders);
        if (_skyTexture is null || key != _skyKey)
        {
            EnsureSkyResources(width, height);
            BakeSky(compositor, batch, sky);
            _skyKey = key;
        }
        _skyReady = true;
        return false;
    }

    private void EnsureSkyResources(uint width, uint height)
    {
        var context = _context!; var api = context.Api;
        if (_skyUniforms is null)
        {
            _skyUniforms = new(context, 288, BufferUsage.Uniform | BufferUsage.CopyDst, "Suntrail retained sky frame");
            _skyInstance = new(context, 48, BufferUsage.Vertex | BufferUsage.CopyDst, "Suntrail retained sky quad");
            var uniform = new BindGroupEntry { Binding = 0, Buffer = _skyUniforms.BufferPtr, Size = 288 };
            var group = new BindGroupDescriptor { Layout = _layout, EntryCount = 1, Entries = &uniform };
            _skyBakeGroup = api.DeviceCreateBindGroup(context.Device, &group);
            var texture = new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Fragment,
                Texture = new() { SampleType = TextureSampleType.UnfilterableFloat, ViewDimension = TextureViewDimension.Dimension2D } };
            var description = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &texture };
            _skySampleLayout = api.DeviceCreateBindGroupLayout(context.Device, &description);
            var layouts = stackalloc BindGroupLayout*[2]; layouts[0] = _layout; layouts[1] = _skySampleLayout;
            var pipeline = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = layouts };
            _skyReplayLayout = api.DeviceCreatePipelineLayout(context.Device, &pipeline);
        }
        if (_skyTexture is not null && _skyTexture.Width == width && _skyTexture.Height == height) return;
        if (_skySampleGroup != null) context.QueueBindGroupDisposal((nint)_skySampleGroup);
        _skySampleGroup = null; _skyTexture?.Dispose(); _skyTexture = null;
        _skyTexture = new(context, width, height, TextureFormat.Rgba32float,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding, "Suntrail full precision retained sky",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        var entry = new BindGroupEntry { Binding = 0, TextureView = _skyTexture.ViewPtr };
        var descriptor = new BindGroupDescriptor { Layout = _skySampleLayout, EntryCount = 1, Entries = &entry };
        _skySampleGroup = api.DeviceCreateBindGroup(context.Device, &descriptor);
    }

    private void BakeSky(Compositor compositor, ProceduralBatch batch, ProceduralSprite sky)
    {
        var context = _context!; var api = context.Api;
        _skyUniforms!.WriteSingle(new FrameUniforms { Transform = Matrix4x4.Identity,
            Scene = batch.Scene, Clip = new(0, 0, batch.Size.X, batch.Size.Y),
            Occlusion = new(0, batch.IsDungeon ? 1 : 0, 0, 0) });
        _skyInstance!.WriteSingle(sky);
        UploadedBytes += 336;
        var pipeline = CreateSkyPipeline(compositor, bake: true, isOffscreen: true, (int)batch.Scene.Y);
        var encoder = api.DeviceCreateCommandEncoder(context.Device, null);
        CommandBuffer* commands = null;
        try
        {
            var color = new RenderPassColorAttachment { View = _skyTexture!.ViewPtr, LoadOp = LoadOp.Clear, StoreOp = StoreOp.Store };
            var descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &color };
            var pass = api.CommandEncoderBeginRenderPass(encoder, &descriptor);
            api.RenderPassEncoderSetPipeline(pass, pipeline);
            api.RenderPassEncoderSetBindGroup(pass, 0, _skyBakeGroup, 0, null);
            api.RenderPassEncoderSetVertexBuffer(pass, 0, _skyInstance.BufferPtr, 0, 48);
            api.RenderPassEncoderDraw(pass, 6, 1, 0, 0);
            api.RenderPassEncoderEnd(pass); api.RenderPassEncoderRelease(pass);
            commands = api.CommandEncoderFinish(encoder, null);
            context.Submit(1, &commands);
            SkyBakeCount++;
        }
        finally
        {
            if (commands != null) api.CommandBufferRelease(commands);
            api.CommandEncoderRelease(encoder);
        }
    }

    private RenderPipeline* GetSkyReplayPipeline(Compositor compositor, bool isOffscreen)
    {
        int index = isOffscreen ? 1 : 0;
        if (_skyReplay[index] == 0) _skyReplay[index] = (nint)CreateSkyPipeline(compositor, false, isOffscreen);
        return (RenderPipeline*)_skyReplay[index];
    }

    private RenderPipeline* CreateSkyPipeline(Compositor compositor, bool bake, bool isOffscreen, int world = 0)
    {
        var module = _cache!.GetOrCreateShader("Suntrail.Art.v1", Shader, "Suntrail procedural artwork");
        var attributes = stackalloc VertexAttribute[3];
        for (uint i = 0; i < 3; i++) attributes[i] = new() { ShaderLocation = i, Offset = i * 16, Format = VertexFormat.Float32x4 };
        Span<VertexBufferLayout> buffers = stackalloc VertexBufferLayout[1];
        buffers[0] = new() { ArrayStride = 48, StepMode = VertexStepMode.Instance, AttributeCount = 3, Attributes = attributes };
        return _cache.GetOrCreateRenderPipeline(bake ? (EnableWorldShaders ? "Art.sky.bake.world" + world : "Art.sky.bake") : isOffscreen ? "Art.sky.replay.off" : "Art.sky.replay.on",
            module, buffers, fragmentEntry: bake ? (EnableWorldShaders ? Entries[7 + world * 6] : "fs_sky") : "fs_sky_cached",
            targetFormat: bake ? TextureFormat.Rgba32float : compositor.RenderFormat,
            enableBlend: !bake, sampleCount: bake || isOffscreen ? 1u : compositor.Options.PrimarySampleCount,
            pipelineLayout: bake ? _pipelineLayout : _skyReplayLayout, sourceAlphaMode: GpuTextureAlphaMode.Premultiplied);
    }

    private void DisposeSkyCache()
    {
        if (_context is { IsDisposed: false } context)
        {
            if (_skyBakeGroup != null) context.QueueBindGroupDisposal((nint)_skyBakeGroup);
            if (_skySampleGroup != null) context.QueueBindGroupDisposal((nint)_skySampleGroup);
            if (_skyReplayLayout != null) context.QueuePipelineLayoutDisposal((nint)_skyReplayLayout);
            if (_skySampleLayout != null) context.QueueBindGroupLayoutDisposal((nint)_skySampleLayout);
        }
        _skyTexture?.Dispose(); _skyTexture = null;
        _skyUniforms?.Dispose(); _skyInstance?.Dispose(); _skyUniforms = _skyInstance = null;
        _skyBakeGroup = _skySampleGroup = null; _skyReplayLayout = null; _skySampleLayout = null;
        Array.Clear(_skyReplay); _skyReady = false;
    }
}
