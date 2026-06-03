using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Scene;

public unsafe class DxfStaticBuffer : IDisposable
{
    private readonly WgpuContext _context;
    
    public GpuBuffer? VertexBuffer { get; private set; }
    public GpuBuffer? IndexBuffer { get; private set; }
    public uint IndexCount { get; private set; }
    public VectorVertex[] VectorVertices { get; }
    
    public GpuBuffer? TextVertexBuffer { get; private set; }
    public GpuBuffer? TextIndexBuffer { get; private set; }
    public uint TextIndexCount { get; private set; }
    public VectorVertex[] TextVertices { get; }
    
    private GpuBuffer? _textVertexBufferBack;
    private GpuBuffer? _textIndexBufferBack;
    private uint _textIndexCountBack;
    
    public GpuBuffer? BrushesBuffer { get; private set; }
    
    private readonly Dictionary<int, object> _extensionStates = new();
    
    public void SetExtensionState(int extensionId, object state) => _extensionStates[extensionId] = state;
    public object? GetExtensionState(int extensionId) => _extensionStates.TryGetValue(extensionId, out var state) ? state : null;
    
    // Bind groups for drawing the static buffer
    public BindGroup* UniformBindGroup { get; private set; }
    public BindGroup* UniformBindGroupOffscreen { get; private set; }
    
    public BindGroup* TextUniformBindGroup { get; private set; }
    public BindGroup* TextUniformBindGroupOffscreen { get; private set; }
    
    // The viewport Uniform buffer for this static buffer's custom MVP matrix
    public GpuBuffer? UniformBuffer { get; private set; }
    
    public Compositor.CompositorDrawCall[] DrawCalls { get; }
    
    public Compositor.StaticTextRecord[] TextRecords { get; set; } = Array.Empty<Compositor.StaticTextRecord>();
    
    private bool _isDisposed;
    
    public DxfStaticBuffer(
        WgpuContext context,
        VectorVertex[] vertices,
        uint[] indices,
        VectorVertex[] textVertices,
        uint[] textIndices,
        GpuBrush[] brushes,
        Compositor.CompositorDrawCall[] drawCalls)
    {
        _context = context;
        VectorVertices = vertices;
        TextVertices = textVertices;
        DrawCalls = drawCalls;
        
        // 1. Create and upload Vector Buffers
        if (vertices.Length > 0 && indices.Length > 0)
        {
            VertexBuffer = new GpuBuffer(context, (uint)vertices.Length * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex | BufferUsage.CopyDst, "Static DXF Vector Vertex Buffer");
            VertexBuffer.Write(new ReadOnlySpan<VectorVertex>(vertices));
            
            IndexBuffer = new GpuBuffer(context, (uint)indices.Length * 4, BufferUsage.Index | BufferUsage.CopyDst, "Static DXF Vector Index Buffer");
            IndexBuffer.Write(new ReadOnlySpan<uint>(indices));
            IndexCount = (uint)indices.Length;
        }
        
        // 2. Create and upload Text Buffers
        if (textVertices.Length > 0 && textIndices.Length > 0)
        {
            TextVertexBuffer = new GpuBuffer(context, (uint)textVertices.Length * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex | BufferUsage.CopyDst, "Static DXF Text Vertex Buffer");
            TextVertexBuffer.Write(new ReadOnlySpan<VectorVertex>(textVertices));
            
            TextIndexBuffer = new GpuBuffer(context, (uint)textIndices.Length * 4, BufferUsage.Index | BufferUsage.CopyDst, "Static DXF Text Index Buffer");
            TextIndexBuffer.Write(new ReadOnlySpan<uint>(textIndices));
            TextIndexCount = (uint)textIndices.Length;
        }
        
        // 3. Brushes buffer
        uint brushesSize = (uint)Math.Max(1, brushes.Length) * (uint)Marshal.SizeOf<GpuBrush>();
        BrushesBuffer = new GpuBuffer(context, brushesSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static DXF Brushes Buffer");
        if (brushes.Length > 0)
        {
            BrushesBuffer.Write(new ReadOnlySpan<GpuBrush>(brushes));
        }
        else
        {
            var dummy = new GpuBrush();
            BrushesBuffer.WriteSingle(dummy);
        }
        
        // 4. Custom uniforms buffer (needs custom model-to-screen matrix)
        UniformBuffer = new GpuBuffer(context, (uint)Marshal.SizeOf<GpuUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst, "Static DXF Viewport Uniform Buffer");
    }
    
    public void UpdateTextBuffer(VectorVertex[] textVertices, uint[] textIndices)
    {
        if (textVertices.Length > 0 && textIndices.Length > 0)
        {
            if (_textVertexBufferBack == null || _textVertexBufferBack.Size < textVertices.Length * Marshal.SizeOf<VectorVertex>())
            {
                _textVertexBufferBack?.Dispose();
                _textVertexBufferBack = new GpuBuffer(_context, (uint)textVertices.Length * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex | BufferUsage.CopyDst, "Static DXF Text Vertex Back Buffer");
            }
            _textVertexBufferBack.Write(new ReadOnlySpan<VectorVertex>(textVertices));
            
            if (_textIndexBufferBack == null || _textIndexBufferBack.Size < textIndices.Length * 4)
            {
                _textIndexBufferBack?.Dispose();
                _textIndexBufferBack = new GpuBuffer(_context, (uint)textIndices.Length * 4, BufferUsage.Index | BufferUsage.CopyDst, "Static DXF Text Index Back Buffer");
            }
            _textIndexBufferBack.Write(new ReadOnlySpan<uint>(textIndices));
            _textIndexCountBack = (uint)textIndices.Length;

            // Swap front and back buffer references
            var tempVertexBuffer = TextVertexBuffer;
            TextVertexBuffer = _textVertexBufferBack;
            _textVertexBufferBack = tempVertexBuffer;

            var tempIndexBuffer = TextIndexBuffer;
            TextIndexBuffer = _textIndexBufferBack;
            _textIndexBufferBack = tempIndexBuffer;

            var tempIndexCount = TextIndexCount;
            TextIndexCount = _textIndexCountBack;
            _textIndexCountBack = tempIndexCount;
        }
        else
        {
            TextIndexCount = 0;
        }
        
        for (int i = 0; i < DrawCalls.Length; i++)
        {
            if (DrawCalls[i].Type == Compositor.DrawCallType.Text)
            {
                var dc = DrawCalls[i];
                dc.IndexCount = TextIndexCount;
                DrawCalls[i] = dc;
            }
        }
    }
    
    public void InitializeBindGroups(BindGroupLayout* layout, BindGroupLayout* layoutOffscreen, BindGroupLayout* textLayout, BindGroupLayout* textLayoutOffscreen)
    {
        if (UniformBuffer == null) return;
        
        // Vector bindings
        var uBufferEntryVector = new BindGroupEntry
        {
            Binding = 0,
            Buffer = UniformBuffer.BufferPtr,
            Offset = 0,
            Size = UniformBuffer.Size
        };

        var brushesEntry = new BindGroupEntry
        {
            Binding = 1,
            Buffer = BrushesBuffer!.BufferPtr,
            Offset = 0,
            Size = BrushesBuffer.Size
        };

        var vectorEntries = stackalloc BindGroupEntry[2];
        vectorEntries[0] = uBufferEntryVector;
        vectorEntries[1] = brushesEntry;

        var uDescVector = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 2,
            Entries = vectorEntries
        };
        UniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = layoutOffscreen,
            EntryCount = 2,
            Entries = vectorEntries
        };
        UniformBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVectorOffscreen);

        // Text bindings
        var uBufferEntryText = new BindGroupEntry
        {
            Binding = 0,
            Buffer = UniformBuffer.BufferPtr,
            Offset = 0,
            Size = UniformBuffer.Size
        };

        var uDescText = new BindGroupDescriptor
        {
            Layout = textLayout,
            EntryCount = 1,
            Entries = &uBufferEntryText
        };
        TextUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescText);

        var uDescTextOffscreen = new BindGroupDescriptor
        {
            Layout = textLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntryText
        };
        TextUniformBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescTextOffscreen);
    }
    
    public void UpdateViewport(Matrix4x4 projection, float zoom, Vector2 pan, Vector2 center, Vector2 screenCenter)
    {
        if (UniformBuffer == null) return;
        
        var modelToScreen = new Matrix4x4(
            zoom, 0, 0, 0,
            0, zoom, 0, 0,
            0, 0, 1, 0,
            -screenCenter.X * zoom + screenCenter.X + pan.X, -screenCenter.Y * zoom + screenCenter.Y + pan.Y, 0, 1
        );
        
        var uniformsData = new GpuUniforms
        {
            Projection = projection,
            Mvp = modelToScreen,
            View = Matrix4x4.Identity
        };
        
        UniformBuffer.WriteSingle(uniformsData);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        lock (_context.RenderLock)
        {
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
            TextVertexBuffer?.Dispose();
            TextIndexBuffer?.Dispose();
            _textVertexBufferBack?.Dispose();
            _textIndexBufferBack?.Dispose();
            BrushesBuffer?.Dispose();
            UniformBuffer?.Dispose();
            
            foreach (var state in _extensionStates.Values)
            {
                if (state is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _extensionStates.Clear();
            
            if (!_context.IsDisposed)
            {
                if (UniformBindGroup != null) _context.Wgpu.BindGroupRelease(UniformBindGroup);
                if (UniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(UniformBindGroupOffscreen);
                if (TextUniformBindGroup != null) _context.Wgpu.BindGroupRelease(TextUniformBindGroup);
                if (TextUniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(TextUniformBindGroupOffscreen);
            }
        }
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
