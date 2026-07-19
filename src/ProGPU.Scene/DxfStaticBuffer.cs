using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Scene;

public unsafe class DxfStaticBuffer : IDisposable
{
    internal static event Action<DxfStaticBuffer>? Disposed;

    private readonly WgpuContext _context;

    internal WgpuContext Context => _context;
    
    public GpuBuffer? VertexBuffer { get; private set; }
    public GpuBuffer? IndexBuffer { get; private set; }
    public uint IndexCount { get; private set; }
    public VectorVertex[] VectorVertices { get; }
    
    public GpuBuffer? TextVertexBuffer { get; private set; }
    public uint TextIndexCount { get; private set; }
    public GlyphInstance[] TextVertices { get; }

    public GpuBuffer? RetainedGlyphRecordBuffer { get; private set; }
    public GpuBuffer? RetainedGlyphSegmentBuffer { get; private set; }
    public GpuBuffer? RetainedGlyphInstanceBuffer { get; private set; }
    public uint RetainedGlyphRecordCount { get; }
    public uint RetainedGlyphSegmentCount { get; }
    public uint RetainedGlyphInstanceCount { get; }
    
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
    public BindGroup* RetainedGlyphBindGroup { get; private set; }
    
    // The viewport Uniform buffer for this static buffer's custom MVP matrix
    public GpuBuffer? UniformBuffer { get; private set; }
    
    public Compositor.CompositorDrawCall[] DrawCalls { get; }
    
    public Compositor.StaticTextRecord[] TextRecords { get; set; } = Array.Empty<Compositor.StaticTextRecord>();
    
    private int _disposeState;
    private int _activeRenderCount;
    private Matrix4x4 _explicitModelToScreen = Matrix4x4.Identity;

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    internal readonly struct RenderLease : IDisposable
    {
        private readonly DxfStaticBuffer? _owner;

        internal RenderLease(DxfStaticBuffer owner) => _owner = owner;

        internal bool IsAcquired => _owner != null;

        public void Dispose() => _owner?.ReleaseRenderLease();
    }
    
    public DxfStaticBuffer(
        WgpuContext context,
        VectorVertex[] vertices,
        uint[] indices,
        GlyphInstance[] textVertices,
        GpuPathRecord[] retainedGlyphRecords,
        GpuPathSegment[] retainedGlyphSegments,
        RetainedGlyphInstance[] retainedGlyphInstances,
        GpuBrush[] brushes,
        GpuGradientStop[] gradientStops,
        Compositor.CompositorDrawCall[] drawCalls)
    {
        _context = context;
        VectorVertices = vertices;
        TextVertices = textVertices;
        RetainedGlyphRecordCount = (uint)retainedGlyphRecords.Length;
        RetainedGlyphSegmentCount = (uint)retainedGlyphSegments.Length;
        RetainedGlyphInstanceCount = (uint)retainedGlyphInstances.Length;
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

        if (retainedGlyphInstances.Length > 0)
        {
            RetainedGlyphRecordBuffer = new GpuBuffer(
                context,
                checked((uint)retainedGlyphRecords.Length * (uint)Marshal.SizeOf<GpuPathRecord>()),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Static DXF Retained Glyph Records");
            RetainedGlyphRecordBuffer.Write((ReadOnlySpan<GpuPathRecord>)retainedGlyphRecords);

            RetainedGlyphSegmentBuffer = new GpuBuffer(
                context,
                checked((uint)retainedGlyphSegments.Length * (uint)Marshal.SizeOf<GpuPathSegment>()),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Static DXF Retained Glyph Segments");
            RetainedGlyphSegmentBuffer.Write((ReadOnlySpan<GpuPathSegment>)retainedGlyphSegments);

            RetainedGlyphInstanceBuffer = new GpuBuffer(
                context,
                checked((uint)retainedGlyphInstances.Length * (uint)Marshal.SizeOf<RetainedGlyphInstance>()),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Static DXF Retained Glyph Instances");
            RetainedGlyphInstanceBuffer.Write((ReadOnlySpan<RetainedGlyphInstance>)retainedGlyphInstances);
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
    
    public void InitializeBindGroups(
        BindGroupLayout* layout,
        BindGroupLayout* layoutOffscreen,
        BindGroupLayout* textLayout,
        BindGroupLayout* textLayoutOffscreen,
        BindGroupLayout* retainedGlyphLayout)
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
        UniformBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = layoutOffscreen,
            EntryCount = 3,
            Entries = vectorEntries
        };
        UniformBindGroupOffscreen = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescVectorOffscreen);

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
        TextUniformBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescText);

        var uDescTextOffscreen = new BindGroupDescriptor
        {
            Layout = textLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntryText
        };
        TextUniformBindGroupOffscreen = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescTextOffscreen);

        if (RetainedGlyphRecordBuffer != null &&
            RetainedGlyphSegmentBuffer != null &&
            RetainedGlyphInstanceBuffer != null)
        {
            var retainedEntries = stackalloc BindGroupEntry[4];
            retainedEntries[0] = uBufferEntryText;
            retainedEntries[1] = new BindGroupEntry
            {
                Binding = 1,
                Buffer = RetainedGlyphRecordBuffer.BufferPtr,
                Offset = 0,
                Size = RetainedGlyphRecordBuffer.Size
            };
            retainedEntries[2] = new BindGroupEntry
            {
                Binding = 2,
                Buffer = RetainedGlyphSegmentBuffer.BufferPtr,
                Offset = 0,
                Size = RetainedGlyphSegmentBuffer.Size
            };
            retainedEntries[3] = new BindGroupEntry
            {
                Binding = 3,
                Buffer = RetainedGlyphInstanceBuffer.BufferPtr,
                Offset = 0,
                Size = RetainedGlyphInstanceBuffer.Size
            };
            var retainedDescriptor = new BindGroupDescriptor
            {
                Layout = retainedGlyphLayout,
                EntryCount = 4,
                Entries = retainedEntries
            };
            RetainedGlyphBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &retainedDescriptor);
        }
    }
    
    public void UpdateViewport(Matrix4x4 projection, float zoom, Vector2 pan, Vector2 center, Vector2 screenCenter)
    {
        // Static DXF vertices are recorded in control-local screen coordinates at
        // zoom 1. Retain only the camera transform here. The compositor composes
        // visual placement and the target projection immediately before drawing.
        _explicitModelToScreen = new Matrix4x4(
            zoom, 0, 0, 0,
            0, zoom, 0, 0,
            0, 0, 1, 0,
            -screenCenter.X * zoom + screenCenter.X + pan.X, -screenCenter.Y * zoom + screenCenter.Y + pan.Y, 0, 1
        );
        _hasExplicitViewport = true;
    }

    internal void PrepareViewport(
        Matrix4x4 projection,
        Matrix4x4 placementTransform,
        Vector2 canvasSize,
        float dpiScale)
    {
        var modelToScreen = _hasExplicitViewport
            ? _explicitModelToScreen * placementTransform
            : placementTransform;
        WriteViewportUniforms(projection, modelToScreen, canvasSize, dpiScale);
    }

    internal RenderLease AcquireRenderLease()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return default;
        }

        Interlocked.Increment(ref _activeRenderCount);
        if (Volatile.Read(ref _disposeState) == 0)
        {
            return new RenderLease(this);
        }

        ReleaseRenderLease();
        return default;
    }

    private void ReleaseRenderLease()
    {
        if (Interlocked.Decrement(ref _activeRenderCount) == 0 &&
            Volatile.Read(ref _disposeState) == 1)
        {
            ReleaseResources();
        }
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

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0) return;

        try
        {
            Disposed?.Invoke(this);
        }
        finally
        {
            if (Volatile.Read(ref _activeRenderCount) == 0)
            {
                ReleaseResources();
            }

            GC.SuppressFinalize(this);
        }
    }

    private void ReleaseResources()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 2, 1) != 1) return;

        lock (_context.RenderLock)
        {
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
            TextVertexBuffer?.Dispose();
            _textVertexBufferBack?.Dispose();
            RetainedGlyphRecordBuffer?.Dispose();
            RetainedGlyphSegmentBuffer?.Dispose();
            RetainedGlyphInstanceBuffer?.Dispose();
            BrushesBuffer?.Dispose();
            GradientStopsBuffer?.Dispose();
            UniformBuffer?.Dispose();
            
            var extensionStateEnumerator = _extensionStates.Values.GetEnumerator();
            while (extensionStateEnumerator.MoveNext())
            {
                var state = extensionStateEnumerator.Current;
                if (state is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _extensionStates.Clear();
            
            if (!_context.IsDisposed)
            {
                if (UniformBindGroup != null) _context.Api.BindGroupRelease(UniformBindGroup);
                if (UniformBindGroupOffscreen != null) _context.Api.BindGroupRelease(UniformBindGroupOffscreen);
                if (TextUniformBindGroup != null) _context.Api.BindGroupRelease(TextUniformBindGroup);
                if (TextUniformBindGroupOffscreen != null) _context.Api.BindGroupRelease(TextUniformBindGroupOffscreen);
                if (RetainedGlyphBindGroup != null) _context.Api.BindGroupRelease(RetainedGlyphBindGroup);
            }
        }
    }
}
