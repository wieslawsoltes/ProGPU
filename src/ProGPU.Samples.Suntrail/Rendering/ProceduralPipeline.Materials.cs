using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.GameEngine.Rendering;
using ProGPU.Scene;
using Silk.NET.WebGPU;

namespace ProGPU.Samples.Suntrail.Rendering;

public sealed unsafe partial class ProceduralPipeline
{
    // Desktop/browser retain explicit comparison mode; iOS uses the bounded material compiler.
    public bool EnableMaterialPages { get; set; } = OperatingSystem.IsIOS();
    public int MaterialAtlasExtent { get; init; } = 4992;
    public long MaterialBakeCount { get; private set; }
    public long MaterialResidentBytes => _materialAtlas?.ResidentBytes ?? 0;
    public int MaterialResidentPages => _materialPages?.Count ?? 0;
    public int MaterialVisiblePages { get; private set; }
    public int MaterialFallbackPages { get; private set; }
    public long MaterialEvictions => _materialPages?.Evictions ?? 0;
    private const int PageInstanceCapacity = 8192, BakeBudget = 32;
    private MaterialPageAtlas? _materialAtlas;
    private MaterialPageCache<MaterialKey>? _materialPages;
    private readonly MaterialPageInstance[] _pageInstances = new MaterialPageInstance[PageInstanceCapacity];
    private readonly MaterialPageInstance[] _previousPageInstances = new MaterialPageInstance[PageInstanceCapacity];
    private readonly PageRequest[] _pageRequests = new PageRequest[PageInstanceCapacity];
    private readonly MaterialPageInstance[] _bakes = new MaterialPageInstance[BakeBudget];
    private readonly MaterialPageHandle[] _bakeHandles = new MaterialPageHandle[BakeBudget];
    private int _pageCount, _previousPageCount = -1;
    private bool _pagesPrepared, _atlasInitialized;
    private GpuBuffer? _pageBuffer, _bakeBuffer, _bakeUniforms;
    private BindGroup* _materialSampleGroup, _materialBakeGroup;
    private BindGroupLayout* _materialSampleLayout;
    private PipelineLayout* _materialReplayLayout;
    private Sampler* _materialSampler;
    private readonly nint[] _pageOnscreen = new nint[Entries.Length + 1], _pageOffscreen = new nint[Entries.Length + 1];
    private nint _materialBakePipeline;
    private readonly record struct MaterialKey(Vector2 Size, Vector4 Parameters, float PixelDensity, int World, bool Dungeon, int X, int Y);
    private struct PageRequest { public MaterialKey Key; public bool Cacheable, Cached; }

    private static bool IsStaticMaterial(Artwork kind) => kind is Artwork.Sky or Artwork.Cliff or Artwork.Tree or
        Artwork.Bush or Artwork.Crate or Artwork.Lantern or Artwork.Ledge or Artwork.Thorns or Artwork.Cloud or
        Artwork.Mountain or Artwork.Ruin or Artwork.Mushroom or Artwork.Shadow or Artwork.Fern or
        Artwork.Crystal or Artwork.Palm or Artwork.Pine or Artwork.Spire or Artwork.Pipe;

    private static int MaterialPriority(Artwork kind) => kind switch
    {
        Artwork.Cliff or Artwork.Ledge => 0,
        Artwork.Tree or Artwork.Palm or Artwork.Pine or Artwork.Spire or Artwork.Crystal => 1,
        _ => 2
    };

    private static bool IsBackdrop(in ProceduralSprite sprite) => (Artwork)(int)sprite.Material.X is
        Artwork.Sky or Artwork.Cloud or Artwork.Mountain or Artwork.Ruin or Artwork.Water or Artwork.Cavern ||
        (sprite.Material.Z > .5f && (Artwork)(int)sprite.Material.X is Artwork.Tree or Artwork.Crystal or Artwork.Palm or Artwork.Pine or Artwork.Spire);

    private void PrepareMaterialPages(Compositor compositor, ProceduralBatch batch)
    {
        _pagesPrepared = false;
        if (!EnableMaterialPages || _transform != Matrix4x4.Identity) return;
        float dpi = MathF.Max(compositor.CurrentDpiScale, 2), scale = batch.Scene.W;
        if (!float.IsFinite(dpi) || dpi <= 0 || !float.IsFinite(scale) || scale <= 0) return;
        EnsureResources(compositor); EnsureMaterialResources();
        _materialPages!.BeginFrame(); _pageCount = 0;
        MaterialVisiblePages = MaterialFallbackPages = 0;
        foreach (var sprite in batch.Sprites)
        {
            var size = new Vector2(sprite.Bounds.Z, sprite.Bounds.W) / scale;
            var physicalSize = new Vector2(sprite.Bounds.Z, sprite.Bounds.W) * dpi;
            bool cacheable = IsStaticMaterial((Artwork)(int)sprite.Material.X) &&
                float.IsFinite(physicalSize.X) && float.IsFinite(physicalSize.Y) &&
                physicalSize.X > 0 && physicalSize.Y > 0 && physicalSize.X < 1_000_000 && physicalSize.Y < 1_000_000;
            if (!cacheable)
            {
                AddPageInstance(new(sprite.Bounds, sprite.Color, sprite.Material, new(0, 0, 1, 1), default, new(size, 0, 0)), default);
                continue;
            }
            // Screen culling selects page ranges directly; enormous terrain segments
            // never allocate an enormous texture or iterate over their invisible pages.
            float margin = (Artwork)(int)sprite.Material.X == Artwork.Tree ? sprite.Bounds.Z * .004f : 0;
            int minX = Math.Max(0, (int)MathF.Floor((-sprite.Bounds.X - margin) * dpi / MaterialPageAtlas.InteriorSize));
            int minY = Math.Max(0, (int)MathF.Floor(-sprite.Bounds.Y * dpi / MaterialPageAtlas.InteriorSize));
            int maxX = Math.Min((int)MathF.Ceiling(physicalSize.X / MaterialPageAtlas.InteriorSize),
                (int)MathF.Ceiling((batch.Size.X - sprite.Bounds.X + margin) * dpi / MaterialPageAtlas.InteriorSize));
            int maxY = Math.Min((int)MathF.Ceiling(physicalSize.Y / MaterialPageAtlas.InteriorSize),
                (int)MathF.Ceiling((batch.Size.Y - sprite.Bounds.Y) * dpi / MaterialPageAtlas.InteriorSize));
            for (int y = minY; y < maxY; y++) for (int x = minX; x < maxX; x++)
            {
                var origin = new Vector2(x, y) * MaterialPageAtlas.InteriorSize;
                var extent = Vector2.Min(new(MaterialPageAtlas.InteriorSize), physicalSize - origin);
                var screen = new Vector4(sprite.Bounds.X + origin.X / dpi - margin, sprite.Bounds.Y + origin.Y / dpi,
                    sprite.Bounds.X + (origin.X + extent.X) / dpi + margin, sprite.Bounds.Y + (origin.Y + extent.Y) / dpi);
                if (IsBackdrop(sprite) && FullyOccluded(batch, screen)) continue;
                var key = new MaterialKey(size, sprite.Material, dpi * scale, (int)batch.Scene.Y, batch.IsDungeon, x, y);
                bool resident = _materialPages.TryPin(key, out var handle);
                var region = new Vector4(origin / physicalSize, extent.X / physicalSize.X, extent.Y / physicalSize.Y);
                var atlas = resident ? _materialAtlas!.SampleRect(handle.Slot, extent) : default;
                AddPageInstance(new(sprite.Bounds, sprite.Color, sprite.Material, region, atlas, new(size, resident ? 1 : 0, 0)),
                    new() { Key = key, Cacheable = true, Cached = resident });
                MaterialVisiblePages++;
            }
        }
        // All resident requests are pinned before any miss can evict an old page.
        int bakeCount = 0;
        for (int priority = 0; priority < 3; priority++)
        for (int i = 0; i < _pageCount; i++)
        {
            ref var request = ref _pageRequests[i];
            if (!request.Cacheable || request.Cached || MaterialPriority((Artwork)(int)request.Key.Parameters.X) != priority) continue;
            if (bakeCount == BakeBudget || !_materialPages.TryReserve(request.Key, out var handle)) continue;
            var key = request.Key;
            var physicalSize = key.Size * key.PixelDensity;
            var origin = new Vector2(key.X, key.Y) * MaterialPageAtlas.InteriorSize;
            var region = new Vector4((origin - new Vector2(MaterialPageAtlas.Gutter)) / physicalSize,
                MaterialPageAtlas.Pitch / physicalSize.X, MaterialPageAtlas.Pitch / physicalSize.Y);
            _bakes[bakeCount] = new(_materialAtlas!.BakeRect(handle.Slot), Vector4.One, key.Parameters, region, default, new(key.Size, 0, 0));
            _bakeHandles[bakeCount++] = handle;
        }
        if (bakeCount > 0)
        {
            try
            {
                BakeMaterials(compositor, batch, bakeCount);
                for (int i = 0; i < bakeCount; i++) _materialPages.Commit(_bakeHandles[i]);
            }
            catch
            {
                for (int i = 0; i < bakeCount; i++) _materialPages.Cancel(_bakeHandles[i]);
                throw;
            }
        }
        for (int i = 0; i < _pageCount; i++)
        {
            ref var request = ref _pageRequests[i];
            if (!request.Cacheable || request.Cached) continue;
            if (_materialPages.TryPin(request.Key, out var handle))
            {
                var instance = _pageInstances[i];
                var physicalSize = request.Key.Size * request.Key.PixelDensity;
                var extent = Vector2.Min(new(MaterialPageAtlas.InteriorSize), physicalSize - new Vector2(request.Key.X, request.Key.Y) * MaterialPageAtlas.InteriorSize);
                _pageInstances[i] = instance with { AtlasRect = _materialAtlas!.SampleRect(handle.Slot, extent), SourceSize = instance.SourceSize with { Z = 1 } };
                request.Cached = true;
            }
            else MaterialFallbackPages++;
        }
        var current = _pageInstances.AsSpan(0, _pageCount);
        if (_previousPageCount != _pageCount || !MemoryMarshal.AsBytes(current).SequenceEqual(MemoryMarshal.AsBytes(_previousPageInstances.AsSpan(0, _pageCount))))
        {
            _pageBuffer!.Write<MaterialPageInstance>(current);
            UploadedBytes += _pageCount * 96;
            current.CopyTo(_previousPageInstances); _previousPageCount = _pageCount;
        }
        _pagesPrepared = true;
    }

    private static bool FullyOccluded(ProceduralBatch batch, Vector4 bounds)
    {
        for (int i = 0; i < batch.OccluderCount; i++)
        {
            var ground = batch.Occluders[i];
            if (bounds.X > ground.X + 2 && bounds.Y > ground.Y + 2 && bounds.Z < ground.Z - 2 && bounds.W < ground.W - 2) return true;
        }
        return false;
    }

    private void AddPageInstance(MaterialPageInstance instance, PageRequest request)
    {
        if (_pageCount == PageInstanceCapacity) throw new InvalidOperationException("Visible material page instance capacity exceeded.");
        _pageInstances[_pageCount] = instance; _pageRequests[_pageCount++] = request;
    }

    private void RenderMaterialPages(Compositor compositor, RenderPassEncoder* pass, bool offscreen, ProceduralBatch batch)
    {
        var api = _context!.Api;
        api.RenderPassEncoderSetVertexBuffer(pass, 0, _pageBuffer!.BufferPtr, 0, _pageBuffer.Size);
        api.RenderPassEncoderSetBindGroup(pass, 1, _materialSampleGroup, 0, null);
        for (int first = 0; first < _pageCount;)
        {
            int variant = PageVariant(first, batch);
            int end = first + 1;
            while (end < _pageCount && PageVariant(end, batch) == variant) end++;
            api.RenderPassEncoderSetPipeline(pass, GetPagePipeline(compositor, offscreen, variant));
            api.RenderPassEncoderDraw(pass, 6, (uint)(end - first), 0, (uint)first);
            Draws++; first = end;
        }
    }

    private int PageVariant(int index, ProceduralBatch batch)
    {
        if (_pageRequests[index].Cached) return Entries.Length;
        int variant = EnableSpecializedShaders ? Variant(_pageInstances[index].Parameters.X) : 0;
        return EnableWorldShaders ? 6 + (int)batch.Scene.Y * 6 + variant : variant;
    }

    private RenderPipeline* GetPagePipeline(Compositor compositor, bool offscreen, int variant, bool bake = false)
    {
        var pipelines = offscreen ? _pageOffscreen : _pageOnscreen;
        nint existing = bake ? _materialBakePipeline : pipelines[variant];
        if (existing != 0) return (RenderPipeline*)existing;
        var module = _cache!.GetOrCreateShader("Suntrail.Art.v1", Shader, "Suntrail procedural artwork");
        var attributes = stackalloc VertexAttribute[6];
        for (uint i = 0; i < 6; i++) attributes[i] = new() { ShaderLocation = i, Offset = i * 16, Format = VertexFormat.Float32x4 };
        Span<VertexBufferLayout> buffers = stackalloc VertexBufferLayout[1];
        buffers[0] = new() { ArrayStride = 96, StepMode = VertexStepMode.Instance, AttributeCount = 6, Attributes = attributes };
        var pipeline = _cache.GetOrCreateRenderPipeline(bake ? "Art.material.bake" : $"Art.page.{offscreen}.{variant}", module, buffers,
            vertexEntry: bake ? "vs_material_bake" : "vs_page",
            fragmentEntry: bake ? "fs_material_bake" : variant == Entries.Length ? "fs_material_cached" : Entries[variant],
            targetFormat: bake ? TextureFormat.Rgba16float : compositor.RenderFormat,
            sampleCount: bake || offscreen ? 1u : compositor.Options.PrimarySampleCount,
            enableBlend: !bake, pipelineLayout: bake ? _pipelineLayout : _materialReplayLayout,
            sourceAlphaMode: GpuTextureAlphaMode.Premultiplied);
        if (bake) _materialBakePipeline = (nint)pipeline; else pipelines[variant] = (nint)pipeline;
        return pipeline;
    }

    private void EnsureMaterialResources()
    {
        if (_materialAtlas is not null) return;
        var context = _context!; var api = context.Api;
        _materialAtlas = new(context, MaterialAtlasExtent); _materialPages = new(_materialAtlas.Capacity);
        _pageBuffer = new(context, PageInstanceCapacity * 96, BufferUsage.Vertex | BufferUsage.CopyDst, "Suntrail visible material instances");
        _bakeBuffer = new(context, BakeBudget * 96, BufferUsage.Vertex | BufferUsage.CopyDst, "Suntrail bounded material compiler jobs");
        _bakeUniforms = new(context, 288, BufferUsage.Uniform | BufferUsage.CopyDst, "Suntrail material compiler frame");
        var uniform = new BindGroupEntry { Binding = 0, Buffer = _bakeUniforms.BufferPtr, Size = 288 };
        var bakeGroup = new BindGroupDescriptor { Layout = _layout, EntryCount = 1, Entries = &uniform };
        _materialBakeGroup = api.DeviceCreateBindGroup(context.Device, &bakeGroup);
        var entries = stackalloc BindGroupLayoutEntry[2];
        entries[0] = new() { Binding = 0, Visibility = ShaderStage.Fragment, Texture = new() { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } };
        entries[1] = new() { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new() { Type = SamplerBindingType.Filtering } };
        var layout = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = entries };
        _materialSampleLayout = api.DeviceCreateBindGroupLayout(context.Device, &layout);
        var sampler = new SamplerDescriptor { AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge, MinFilter = FilterMode.Linear, MagFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Nearest, LodMaxClamp = 0, MaxAnisotropy = 1 };
        _materialSampler = api.DeviceCreateSampler(context.Device, &sampler);
        var bindings = stackalloc BindGroupEntry[2];
        bindings[0] = new() { Binding = 0, TextureView = _materialAtlas.Texture.ViewPtr };
        bindings[1] = new() { Binding = 1, Sampler = _materialSampler };
        var group = new BindGroupDescriptor { Layout = _materialSampleLayout, EntryCount = 2, Entries = bindings };
        _materialSampleGroup = api.DeviceCreateBindGroup(context.Device, &group);
        var layouts = stackalloc BindGroupLayout*[2]; layouts[0] = _layout; layouts[1] = _materialSampleLayout;
        var pipeline = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = layouts };
        _materialReplayLayout = api.DeviceCreatePipelineLayout(context.Device, &pipeline);
    }

    private void BakeMaterials(Compositor compositor, ProceduralBatch batch, int count)
    {
        var context = _context!; var api = context.Api;
        _bakeUniforms!.WriteSingle(new FrameUniforms { Transform = Matrix4x4.Identity, Scene = batch.Scene,
            Clip = new(0, 0, _materialAtlas!.Texture.Width, _materialAtlas.Texture.Height), Occlusion = new(0, batch.IsDungeon ? 1 : 0, 1, 1) });
        _bakeBuffer!.Write<MaterialPageInstance>(_bakes.AsSpan(0, count));
        UploadedBytes += 288 + count * 96;
        var pipeline = GetPagePipeline(compositor, true, 0, bake: true);
        var encoder = api.DeviceCreateCommandEncoder(context.Device, null);
        CommandBuffer* commands = null;
        try
        {
            var attachment = new RenderPassColorAttachment { View = _materialAtlas.Texture.ViewPtr,
                LoadOp = _atlasInitialized ? LoadOp.Load : LoadOp.Clear, StoreOp = StoreOp.Store, ClearValue = new(0, 0, 0, 0), DepthSlice = uint.MaxValue };
            var descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
            var pass = api.CommandEncoderBeginRenderPass(encoder, &descriptor);
            api.RenderPassEncoderSetPipeline(pass, pipeline);
            api.RenderPassEncoderSetBindGroup(pass, 0, _materialBakeGroup, 0, null);
            api.RenderPassEncoderSetVertexBuffer(pass, 0, _bakeBuffer.BufferPtr, 0, _bakeBuffer.Size);
            api.RenderPassEncoderDraw(pass, 6, (uint)count, 0, 0);
            api.RenderPassEncoderEnd(pass); api.RenderPassEncoderRelease(pass);
            commands = api.CommandEncoderFinish(encoder, null);
            context.Submit(1, &commands); _atlasInitialized = true; MaterialBakeCount += count;
        }
        finally
        {
            if (commands != null) api.CommandBufferRelease(commands);
            api.CommandEncoderRelease(encoder);
        }
    }

    private void DisposeMaterials()
    {
        if (_context is { IsDisposed: false } context)
        {
            if (_materialSampleGroup != null) context.QueueBindGroupDisposal((nint)_materialSampleGroup);
            if (_materialBakeGroup != null) context.QueueBindGroupDisposal((nint)_materialBakeGroup);
            if (_materialSampleLayout != null) context.QueueBindGroupLayoutDisposal((nint)_materialSampleLayout);
            if (_materialReplayLayout != null) context.QueuePipelineLayoutDisposal((nint)_materialReplayLayout);
            if (_materialSampler != null) context.QueueSamplerDisposal((nint)_materialSampler);
        }
        _materialAtlas?.Dispose(); _materialAtlas = null; _materialPages = null;
        _pageBuffer?.Dispose(); _bakeBuffer?.Dispose(); _bakeUniforms?.Dispose();
        _pageBuffer = _bakeBuffer = _bakeUniforms = null;
        _materialSampleGroup = _materialBakeGroup = null; _materialSampleLayout = null; _materialReplayLayout = null; _materialSampler = null;
        Array.Clear(_pageOnscreen); Array.Clear(_pageOffscreen); _materialBakePipeline = 0;
        _previousPageCount = -1; _pagesPrepared = _atlasInitialized = false;
    }
}
