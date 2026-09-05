using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;

namespace ProGPU.Samples.Suntrail.Rendering;

/// <summary>
/// Sample-local instanced compositor extension: one 48-byte record per visible sprite,
/// one upload per changed generation and one GPU draw. No per-sprite native calls.
/// The same canonical WGSL is consumed by desktop, iOS and browser WebGPU devices.
/// </summary>
public sealed unsafe class ProceduralPipeline : ICompositorExtension, IDisposable
{
    private static readonly string Shader = LoadShader();
    private static string LoadShader()
    {
        string canonical = ShaderResource.Load(typeof(ProceduralPipeline), "Suntrail.wgsl");
        // The pinned native Naga predates WGSL diagnostic directives. Its validator
        // already permits this flat per-instance branch. Only remove the directive;
        // every target executes exactly the same shader instructions and fwidth AA.
        return OperatingSystem.IsBrowser() ? canonical : canonical.Replace("diagnostic(off, derivative_uniformity);", "", StringComparison.Ordinal);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms { public Matrix4x4 Transform; public Vector4 Scene, Clip, Light0, Light1, Light2;
        public Vector4 Occlusion, Ground0, Ground1, Ground2, Ground3, Ground4, Ground5, Ground6, Ground7;
    }
    private WgpuContext? _context;
    private RenderPipelineCache? _cache;
    private GpuBuffer? _instances, _uniforms;
    private BindGroupLayout* _layout;
    private PipelineLayout* _pipelineLayout;
    private BindGroup* _group;
    private RenderPipeline* _onscreen;
    private RenderPipeline* _offscreen;
    private ProceduralBatch? _lastBatch;
    private uint _lastGeneration;
    private Matrix4x4 _transform = Matrix4x4.Identity;
    private Vector4 _clip;
    private bool _uniformsDirty = true;
    public long UploadedBytes { get; private set; }
    public long Draws { get; private set; }

    public void Compile(Compositor compositor, IRenderDataProvider? provider, Matrix4x4 transform, ref RenderCommand cmd)
    {
        if (cmd.DataParam is not ProceduralBatch batch) throw new ArgumentException("Expected a procedural batch.");
        if (_transform != transform) { _transform = transform; _uniformsDirty = true; }
        var bounds = new Rect(0, 0, batch.Size.X, batch.Size.Y);
        var clip = new Vector4(bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height);
        if (_clip != clip) { _clip = clip; _uniformsDirty = true; }
        cmd.PointBufferOffset = 0; cmd.PointBufferCount = batch.Count;
    }

    public void Render(Compositor compositor, void* renderPassEncoder, bool isOffscreen, in Compositor.CompositorDrawCall dc)
    {
        if (dc.DataParam is not ProceduralBatch batch || batch.Count == 0) return;
        EnsureResources(compositor);
        var api = compositor.Context.Api;
        if (!ReferenceEquals(_lastBatch, batch) || _lastGeneration != batch.Generation)
        {
            _instances!.Write(batch.Sprites);
            UploadedBytes += batch.Count * 48;
            _lastBatch = batch; _lastGeneration = batch.Generation; _uniformsDirty = true;
        }
        if (_uniformsDirty)
        {
            _uniforms!.WriteSingle(new FrameUniforms { Transform = _transform, Scene = batch.Scene, Clip = _clip, Light0 = batch.Light0, Light1 = batch.Light1, Light2 = batch.Light2,
                Occlusion = new(batch.OccluderCount, 0, 0, 0),
                Ground0 = batch.Occluders[0], Ground1 = batch.Occluders[1], Ground2 = batch.Occluders[2], Ground3 = batch.Occluders[3],
                Ground4 = batch.Occluders[4], Ground5 = batch.Occluders[5], Ground6 = batch.Occluders[6], Ground7 = batch.Occluders[7]
            });
            UploadedBytes += 288; _uniformsDirty = false;
        }
        var pipeline = isOffscreen ? _offscreen : _onscreen;
        if (pipeline == null)
        {
            var module = _cache!.GetOrCreateShader("Suntrail.Art.v1", Shader, "Suntrail procedural artwork");
            Span<VertexAttribute> attributes = stackalloc VertexAttribute[3];
            for (uint i = 0; i < 3; i++) attributes[(int)i] = new() { ShaderLocation = i, Offset = i * 16, Format = VertexFormat.Float32x4 };
            fixed (VertexAttribute* ptr = attributes)
            {
                Span<VertexBufferLayout> buffers = stackalloc VertexBufferLayout[1];
                buffers[0] = new() { ArrayStride = 48, StepMode = VertexStepMode.Instance, AttributeCount = 3, Attributes = ptr };
                pipeline = _cache!.GetOrCreateRenderPipeline(
                    isOffscreen ? "Suntrail.Art.offscreen" : "Suntrail.Art.onscreen", module, buffers,
                    topology: PrimitiveTopology.TriangleList, targetFormat: compositor.RenderFormat,
                    sampleCount: isOffscreen ? 1u : compositor.Options.PrimarySampleCount,
                    pipelineLayout: _pipelineLayout, sourceAlphaMode: GpuTextureAlphaMode.Premultiplied);
            }
            if (isOffscreen) _offscreen = pipeline; else _onscreen = pipeline;
        }
        var pass = (RenderPassEncoder*)renderPassEncoder;
        api.RenderPassEncoderSetPipeline(pass, pipeline);
        api.RenderPassEncoderSetBindGroup(pass, 0, _group, 0, null);
        api.RenderPassEncoderSetVertexBuffer(pass, 0, _instances!.BufferPtr, 0, _instances.Size);
        api.RenderPassEncoderDraw(pass, 6, (uint)batch.Count, 0, 0);
        Draws++;
    }

    private void EnsureResources(Compositor compositor)
    {
        if (_context is not null && !ReferenceEquals(_context, compositor.Context))
            throw new InvalidOperationException("Procedural pipelines are owned by one compositor device.");
        if (_instances is not null) return;
        _context = compositor.Context;
        _cache = new RenderPipelineCache(_context);
        var api = _context.Api;
        _instances = new(_context, ProceduralBatch.Capacity * 48, BufferUsage.Vertex | BufferUsage.CopyDst, "Suntrail bounded sprite instances");
        _uniforms = new(_context, 288, BufferUsage.Uniform | BufferUsage.CopyDst, "Suntrail frame");
        var entry = new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Vertex | ShaderStage.Fragment, Buffer = new() { Type = BufferBindingType.Uniform, MinBindingSize = 288 } };
        var description = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &entry };
        _layout = api.DeviceCreateBindGroupLayout(_context.Device, &description);
        var layouts = stackalloc BindGroupLayout*[1];
        layouts[0] = _layout;
        var pipelineDescription = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = layouts };
        _pipelineLayout = api.DeviceCreatePipelineLayout(_context.Device, &pipelineDescription);
        var binding = new BindGroupEntry { Binding = 0, Buffer = _uniforms.BufferPtr, Size = 288 };
        var groupDescription = new BindGroupDescriptor { Layout = _layout, EntryCount = 1, Entries = &binding };
        _group = api.DeviceCreateBindGroup(_context.Device, &groupDescription);
    }

    public void Dispose()
    {
        if (_context is { IsDisposed: false } context)
        {
            if (_group != null) context.QueueBindGroupDisposal((nint)_group);
            if (_pipelineLayout != null) context.QueuePipelineLayoutDisposal((nint)_pipelineLayout);
            if (_layout != null) context.QueueBindGroupLayoutDisposal((nint)_layout);
        }
        _cache?.Dispose(); _cache = null;
        _instances?.Dispose(); _uniforms?.Dispose(); _instances = _uniforms = null;
        _group = null; _layout = null; _pipelineLayout = null; _onscreen = _offscreen = null;
        _context = null; _lastBatch = null; _uniformsDirty = true;
    }
}
