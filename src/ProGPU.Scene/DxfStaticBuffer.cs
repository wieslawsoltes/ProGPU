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
    
    public GpuBuffer? TextVertexBuffer { get; private set; }
    public GpuBuffer? TextIndexBuffer { get; private set; }
    public uint TextIndexCount { get; private set; }
    
    public GpuBuffer? BrushesBuffer { get; private set; }
    public GpuBuffer? HatchRecordsBuffer { get; private set; }
    public GpuBuffer? HatchSegmentsBuffer { get; private set; }
    public GpuBuffer? AcisRecordsBuffer { get; private set; }
    public GpuBuffer? AcisEdgesBuffer { get; private set; }
    
    // Bind groups for drawing the static buffer
    public BindGroup* UniformBindGroup { get; private set; }
    public BindGroup* UniformBindGroupOffscreen { get; private set; }
    
    public BindGroup* TextUniformBindGroup { get; private set; }
    public BindGroup* TextUniformBindGroupOffscreen { get; private set; }
    
    // The viewport Uniform buffer for this static buffer's custom MVP matrix
    public GpuBuffer? UniformBuffer { get; private set; }
    
    private bool _isDisposed;
    
    public DxfStaticBuffer(
        WgpuContext context,
        VectorVertex[] vertices,
        uint[] indices,
        VectorVertex[] textVertices,
        uint[] textIndices,
        GpuBrush[] brushes,
        GpuHatchRecord[] hatchRecords,
        GpuHatchSegment[] hatchSegments,
        GpuAcisRecord[] acisRecords,
        GpuAcisEdge[] acisEdges)
    {
        _context = context;
        
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
        
        // 4. Hatch Records
        uint hatchRecordsSize = (uint)Math.Max(1, hatchRecords.Length) * (uint)Marshal.SizeOf<GpuHatchRecord>();
        HatchRecordsBuffer = new GpuBuffer(context, hatchRecordsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static DXF Hatch Records Buffer");
        if (hatchRecords.Length > 0)
        {
            HatchRecordsBuffer.Write(new ReadOnlySpan<GpuHatchRecord>(hatchRecords));
        }
        else
        {
            var dummy = new GpuHatchRecord();
            HatchRecordsBuffer.WriteSingle(dummy);
        }
        
        // 5. Hatch Segments
        uint hatchSegmentsSize = (uint)Math.Max(1, hatchSegments.Length) * (uint)Marshal.SizeOf<GpuHatchSegment>();
        HatchSegmentsBuffer = new GpuBuffer(context, hatchSegmentsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static DXF Hatch Segments Buffer");
        if (hatchSegments.Length > 0)
        {
            HatchSegmentsBuffer.Write(new ReadOnlySpan<GpuHatchSegment>(hatchSegments));
        }
        else
        {
            var dummy = new GpuHatchSegment();
            HatchSegmentsBuffer.WriteSingle(dummy);
        }
        
        // 6. ACIS Records
        uint acisRecordsSize = (uint)Math.Max(1, acisRecords.Length) * (uint)Marshal.SizeOf<GpuAcisRecord>();
        AcisRecordsBuffer = new GpuBuffer(context, acisRecordsSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static DXF ACIS Records Buffer");
        if (acisRecords.Length > 0)
        {
            AcisRecordsBuffer.Write(new ReadOnlySpan<GpuAcisRecord>(acisRecords));
        }
        else
        {
            var dummy = new GpuAcisRecord();
            AcisRecordsBuffer.WriteSingle(dummy);
        }
        
        // 7. ACIS Edges
        uint acisEdgesSize = (uint)Math.Max(1, acisEdges.Length) * (uint)Marshal.SizeOf<GpuAcisEdge>();
        AcisEdgesBuffer = new GpuBuffer(context, acisEdgesSize, BufferUsage.Storage | BufferUsage.CopyDst, "Static DXF ACIS Edges Buffer");
        if (acisEdges.Length > 0)
        {
            AcisEdgesBuffer.Write(new ReadOnlySpan<GpuAcisEdge>(acisEdges));
        }
        else
        {
            var dummy = new GpuAcisEdge();
            AcisEdgesBuffer.WriteSingle(dummy);
        }
        
        // 8. Custom uniforms buffer (needs custom model-to-screen matrix)
        UniformBuffer = new GpuBuffer(context, (uint)Marshal.SizeOf<GpuUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst, "Static DXF Viewport Uniform Buffer");
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

        var hatchRecordsEntry = new BindGroupEntry
        {
            Binding = 2,
            Buffer = HatchRecordsBuffer!.BufferPtr,
            Offset = 0,
            Size = HatchRecordsBuffer.Size
        };

        var hatchSegmentsEntry = new BindGroupEntry
        {
            Binding = 3,
            Buffer = HatchSegmentsBuffer!.BufferPtr,
            Offset = 0,
            Size = HatchSegmentsBuffer.Size
        };

        var acisRecordsEntry = new BindGroupEntry
        {
            Binding = 4,
            Buffer = AcisRecordsBuffer!.BufferPtr,
            Offset = 0,
            Size = AcisRecordsBuffer.Size
        };

        var acisEdgesEntry = new BindGroupEntry
        {
            Binding = 5,
            Buffer = AcisEdgesBuffer!.BufferPtr,
            Offset = 0,
            Size = AcisEdgesBuffer.Size
        };

        var vectorEntries = stackalloc BindGroupEntry[6];
        vectorEntries[0] = uBufferEntryVector;
        vectorEntries[1] = brushesEntry;
        vectorEntries[2] = hatchRecordsEntry;
        vectorEntries[3] = hatchSegmentsEntry;
        vectorEntries[4] = acisRecordsEntry;
        vectorEntries[5] = acisEdgesEntry;

        var uDescVector = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 6,
            Entries = vectorEntries
        };
        UniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = layoutOffscreen,
            EntryCount = 6,
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
        
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
        TextVertexBuffer?.Dispose();
        TextIndexBuffer?.Dispose();
        BrushesBuffer?.Dispose();
        HatchRecordsBuffer?.Dispose();
        HatchSegmentsBuffer?.Dispose();
        AcisRecordsBuffer?.Dispose();
        AcisEdgesBuffer?.Dispose();
        UniformBuffer?.Dispose();
        
        if (UniformBindGroup != null) _context.Wgpu.BindGroupRelease(UniformBindGroup);
        if (UniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(UniformBindGroupOffscreen);
        if (TextUniformBindGroup != null) _context.Wgpu.BindGroupRelease(TextUniformBindGroup);
        if (TextUniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(TextUniformBindGroupOffscreen);
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
    
    ~DxfStaticBuffer()
    {
        Dispose();
    }
}
