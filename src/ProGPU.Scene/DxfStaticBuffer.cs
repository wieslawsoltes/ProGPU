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
    public uint TextIndexCount { get; private set; }
    public GlyphInstance[] TextVertices { get; }
    
    private GpuBuffer? _textVertexBufferBack;
    
    public GpuBuffer? BrushesBuffer { get; private set; }
    public GpuBuffer? GradientStopsBuffer { get; private set; }
    
    private readonly Dictionary<int, object> _extensionStates = new();
    private bool _hasExplicitViewport;
    
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
        GlyphInstance[] textVertices,
        GpuBrush[] brushes,
        GpuGradientStop[] gradientStops,
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
        if (textVertices.Length > 0)
        {
            TextVertexBuffer = new GpuBuffer(context, (uint)textVertices.Length * (uint)Marshal.SizeOf<GlyphInstance>(), BufferUsage.Vertex | BufferUsage.CopyDst, "Static DXF Text Vertex Buffer");
            TextVertexBuffer.Write(new ReadOnlySpan<GlyphInstance>(textVertices));
            TextIndexCount = (uint)textVertices.Length;
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

        uint gradientStopsSize = (uint)Math.Max(1, gradientStops.Length) * (uint)Marshal.SizeOf<GpuGradientStop>();
        GradientStopsBuffer = new GpuBuffer(context, gradientStopsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static DXF Gradient Stops Buffer");
        if (gradientStops.Length > 0)
        {
            GradientStopsBuffer.Write(new ReadOnlySpan<GpuGradientStop>(gradientStops));
        }
        else
        {
            GradientStopsBuffer.WriteSingle(new GpuGradientStop());
        }
        
        // 4. Custom uniforms buffer (needs custom model-to-screen matrix)
        UniformBuffer = new GpuBuffer(context, (uint)Marshal.SizeOf<GpuUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst, "Static DXF Viewport Uniform Buffer");
    }
    
    public void UpdateTextBuffer(GlyphInstance[] textVertices)
    {
        UpdateTextBuffer((ReadOnlySpan<GlyphInstance>)textVertices);
    }

    public void UpdateTextBuffer(ReadOnlySpan<GlyphInstance> textVertices)
    {
        int textVertexCount = textVertices.Length;
        if (textVertexCount > 0)
        {
            uint requiredBytes = checked((uint)textVertexCount * (uint)Marshal.SizeOf<GlyphInstance>());
            if (_textVertexBufferBack == null || _textVertexBufferBack.Size < requiredBytes)
            {
                _textVertexBufferBack?.Dispose();
                _textVertexBufferBack = new GpuBuffer(_context, requiredBytes, BufferUsage.Vertex | BufferUsage.CopyDst, "Static DXF Text Vertex Back Buffer");
            }
            _textVertexBufferBack.Write(textVertices);
            
            // Swap front and back buffer references
            var tempVertexBuffer = TextVertexBuffer;
            TextVertexBuffer = _textVertexBufferBack;
            _textVertexBufferBack = tempVertexBuffer;

            TextIndexCount = (uint)textVertexCount;
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

        var gradientStopsEntry = new BindGroupEntry
        {
            Binding = 2,
            Buffer = GradientStopsBuffer!.BufferPtr,
            Offset = 0,
            Size = GradientStopsBuffer.Size
        };

        var vectorEntries = stackalloc BindGroupEntry[3];
        vectorEntries[0] = uBufferEntryVector;
        vectorEntries[1] = brushesEntry;
        vectorEntries[2] = gradientStopsEntry;

        var uDescVector = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = vectorEntries
        };
        UniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = layoutOffscreen,
            EntryCount = 3,
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

        WriteViewportUniforms(projection, modelToScreen, GetCanvasSize(projection), 1.0f);
        _hasExplicitViewport = true;
    }

    internal void UpdateDefaultViewport(Matrix4x4 projection, Vector2 canvasSize, float dpiScale)
    {
        if (_hasExplicitViewport)
        {
            return;
        }

        WriteViewportUniforms(projection, Matrix4x4.Identity, canvasSize, dpiScale);
    }

    private void WriteViewportUniforms(Matrix4x4 projection, Matrix4x4 modelToScreen, Vector2 canvasSize, float dpiScale)
    {
        if (UniformBuffer == null) return;

        var uniformsData = new GpuUniforms
        {
            Projection = projection,
            Mvp = modelToScreen,
            View = Matrix4x4.Identity,
            CanvasSize = canvasSize,
            DpiScale = dpiScale
        };

        UniformBuffer.WriteSingle(uniformsData);
    }

    private static Vector2 GetCanvasSize(Matrix4x4 projection)
    {
        var width = projection.M11 != 0f ? MathF.Abs(2.0f / projection.M11) : 0f;
        var height = projection.M22 != 0f ? MathF.Abs(2.0f / projection.M22) : 0f;
        return new Vector2(width, height);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        lock (_context.RenderLock)
        {
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
            TextVertexBuffer?.Dispose();
            _textVertexBufferBack?.Dispose();
            BrushesBuffer?.Dispose();
            GradientStopsBuffer?.Dispose();
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
