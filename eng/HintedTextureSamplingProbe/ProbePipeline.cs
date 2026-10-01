using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace HintedTextureSamplingProbe;

internal sealed unsafe class ProbePipeline : IDisposable
{
    private readonly WgpuContext _context;
    private readonly List<nint> _layouts = new();
    private PipelineLayout* _pipelineLayout;
    internal string Name { get; }
    internal RenderPipeline* Handle { get; }
    internal List<nint> Groups { get; } = new();

    internal ProbePipeline(WgpuContext context, RenderPipelineCache cache, string name, string source,
        bool paint, bool diagnostic, GpuBuffer uniforms, GpuBuffer styles, GpuBuffer brushes,
        GpuBuffer stops, GpuBuffer paints, GpuTexture atlas, GpuTexture colorAtlas,
        GpuTexture texture, Sampler* sampler)
    {
        _context = context; Name = name;
        var shader = cache.GetOrCreateShader(name, source);
        VertexFormat[] formats = [VertexFormat.Float32x2, VertexFormat.Float32x2, VertexFormat.Float32x2,
            VertexFormat.Float32x4, VertexFormat.Float32x4, VertexFormat.Float32x4, VertexFormat.Float32x4,
            VertexFormat.Float32, VertexFormat.Uint32];
        ulong[] offsets = [0, 8, 16, 24, 40, 56, 72, 88, 92];
        VertexAttribute* attributes = stackalloc VertexAttribute[9];
        for (int i = 0; i < 9; i++) attributes[i] = new() { Format = formats[i], Offset = offsets[i], ShaderLocation = (uint)i };
        VertexBufferLayout vertices = new() { ArrayStride = 96, StepMode = VertexStepMode.Instance,
            AttributeCount = paint ? 9u : 8u, Attributes = attributes };
        try
        {
            // Match the product's explicit shared Vertex|Fragment buffer slots;
            // do not make an auto-layout compiler mapping part of this probe.
            var globalLayout = new BindGroupLayoutEntry[paint ? 4 : 2];
            for (int i = 0; i < globalLayout.Length; i++) globalLayout[i] = new()
            {
                Binding = (uint)i, Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new BufferBindingLayout { Type = i == 0 ? BufferBindingType.Uniform : BufferBindingType.ReadOnlyStorage }
            };
            AddLayout(globalLayout);
            AddLayout([SamplerLayout(0), TextureLayout(1), TextureLayout(2)]);
            if (paint) { AddLayout([]); AddLayout([SamplerLayout(0), TextureLayout(1)]); }
            var layoutPointers = stackalloc BindGroupLayout*[_layouts.Count];
            for (int i = 0; i < _layouts.Count; i++) layoutPointers[i] = (BindGroupLayout*)_layouts[i];
            PipelineLayoutDescriptor pipelineLayoutDescription = new() { BindGroupLayoutCount = (nuint)_layouts.Count, BindGroupLayouts = layoutPointers };
            _pipelineLayout = context.Api.DeviceCreatePipelineLayout(context.Device, &pipelineLayoutDescription);
            if (_pipelineLayout == null) throw new InvalidOperationException("Pipeline layout creation failed: " + Name);
            Handle = cache.GetOrCreateRenderPipeline(name, shader, new ReadOnlySpan<VertexBufferLayout>(&vertices, 1),
                fragmentEntry: "fs_main_unmasked", targetFormat: diagnostic ? TextureFormat.Rgba32float : TextureFormat.Rgba8Unorm,
                enableBlend: !diagnostic, enableDepthStencil: false, pipelineLayout: _pipelineLayout);
            BindGroupEntry[] globals = paint
                ? [BoundBuffer(0, uniforms), BoundBuffer(1, brushes), BoundBuffer(2, stops), BoundBuffer(3, paints)]
                : [BoundBuffer(0, uniforms), BoundBuffer(1, styles)];
            AddGroup(0, globals);
            AddGroup(1, [new() { Binding = 0, Sampler = sampler }, new() { Binding = 1, TextureView = atlas.ViewPtr }, new() { Binding = 2, TextureView = colorAtlas.ViewPtr }]);
            if (paint)
            {
                AddGroup(2, []);
                AddGroup(3, [new() { Binding = 0, Sampler = sampler }, new() { Binding = 1, TextureView = texture.ViewPtr }]);
            }
        }
        catch { Dispose(); throw; }
    }

    private static BindGroupEntry BoundBuffer(uint index, GpuBuffer buffer) =>
        new() { Binding = index, Buffer = buffer.BufferPtr, Size = buffer.Size };

    private static BindGroupLayoutEntry SamplerLayout(uint binding) => new()
    { Binding = binding, Visibility = ShaderStage.Fragment, Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering } };
    private static BindGroupLayoutEntry TextureLayout(uint binding) => new()
    { Binding = binding, Visibility = ShaderStage.Fragment, Texture = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } };

    private void AddLayout(ReadOnlySpan<BindGroupLayoutEntry> entries)
    {
        fixed (BindGroupLayoutEntry* pointer = entries)
        {
            BindGroupLayoutDescriptor description = new() { EntryCount = (nuint)entries.Length, Entries = pointer };
            var layout = _context.Api.DeviceCreateBindGroupLayout(_context.Device, &description);
            if (layout == null) throw new InvalidOperationException("Layout creation failed: " + Name);
            _layouts.Add((nint)layout);
        }
    }

    private void AddGroup(uint index, ReadOnlySpan<BindGroupEntry> entries)
    {
        fixed (BindGroupEntry* pointer = entries)
        {
            BindGroupDescriptor description = new() { Layout = (BindGroupLayout*)_layouts[(int)index], EntryCount = (nuint)entries.Length, Entries = pointer };
            var group = _context.Api.DeviceCreateBindGroup(_context.Device, &description);
            if (group == null) throw new InvalidOperationException("Bind-group creation failed: " + Name);
            Groups.Add((nint)group);
        }
    }

    public void Dispose()
    {
        foreach (var group in Groups) _context.QueueBindGroupDisposal(group);
        Groups.Clear();
        if (_pipelineLayout != null) _context.QueuePipelineLayoutDisposal((nint)_pipelineLayout);
        _pipelineLayout = null;
        foreach (var layout in _layouts) _context.QueueBindGroupLayoutDisposal(layout);
        _layouts.Clear();
    }
}
