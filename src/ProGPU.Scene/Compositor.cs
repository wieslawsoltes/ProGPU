using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Compute;

namespace ProGPU.Scene;

[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct GpuBrush
{
    [FieldOffset(0)] public uint Type;             // 0 = Solid, 1 = Linear, 2 = Radial
    [FieldOffset(4)] public float Opacity;
    [FieldOffset(8)] public Vector2 StartPoint;
    [FieldOffset(16)] public Vector2 EndPoint;
    [FieldOffset(24)] public Vector2 Center;
    [FieldOffset(32)] public float Radius;
    [FieldOffset(36)] public uint StopCount;
    [FieldOffset(40)] public uint Pad;
    
    [FieldOffset(48)] public Vector4 Color0;
    [FieldOffset(64)] public Vector4 Color1;
    [FieldOffset(80)] public Vector4 Color2;
    [FieldOffset(96)] public Vector4 Color3;
    [FieldOffset(112)] public Vector4 Offsets;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuUniforms
{
    public Matrix4x4 Projection;
    public GpuBrush Brush0; public GpuBrush Brush1; public GpuBrush Brush2; public GpuBrush Brush3;
    public GpuBrush Brush4; public GpuBrush Brush5; public GpuBrush Brush6; public GpuBrush Brush7;
    public GpuBrush Brush8; public GpuBrush Brush9; public GpuBrush Brush10; public GpuBrush Brush11;
    public GpuBrush Brush12; public GpuBrush Brush13; public GpuBrush Brush14; public GpuBrush Brush15;
    public GpuBrush Brush16; public GpuBrush Brush17; public GpuBrush Brush18; public GpuBrush Brush19;
    public GpuBrush Brush20; public GpuBrush Brush21; public GpuBrush Brush22; public GpuBrush Brush23;
    public GpuBrush Brush24; public GpuBrush Brush25; public GpuBrush Brush26; public GpuBrush Brush27;
    public GpuBrush Brush28; public GpuBrush Brush29; public GpuBrush Brush30; public GpuBrush Brush31;
    public GpuBrush Brush32; public GpuBrush Brush33; public GpuBrush Brush34; public GpuBrush Brush35;
    public GpuBrush Brush36; public GpuBrush Brush37; public GpuBrush Brush38; public GpuBrush Brush39;
    public GpuBrush Brush40; public GpuBrush Brush41; public GpuBrush Brush42; public GpuBrush Brush43;
    public GpuBrush Brush44; public GpuBrush Brush45; public GpuBrush Brush46; public GpuBrush Brush47;
    public GpuBrush Brush48; public GpuBrush Brush49; public GpuBrush Brush50; public GpuBrush Brush51;
    public GpuBrush Brush52; public GpuBrush Brush53; public GpuBrush Brush54; public GpuBrush Brush55;
    public GpuBrush Brush56; public GpuBrush Brush57; public GpuBrush Brush58; public GpuBrush Brush59;
    public GpuBrush Brush60; public GpuBrush Brush61; public GpuBrush Brush62; public GpuBrush Brush63;
}

public unsafe class Compositor : IDisposable
{
    // Decoupled hooks to remove hard dependency on UI layer
    public event Action<uint, uint>? PreRender;
    public Func<System.Collections.Generic.IReadOnlyList<Visual>>? GetExternalLayers { get; set; }
    public Func<Visual?>? GetTooltip { get; set; }
    public Func<Vector2>? GetMousePosition { get; set; }
    public Action<DrawingContext, uint, uint>? RenderDiagnostics { get; set; }
    public Vector4 ClearColor { get; set; } = new Vector4(0.08f, 0.08f, 0.12f, 1.0f);

    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _pipelineCache;
    private readonly GlyphAtlas _atlas;
    private readonly PathAtlas _pathAtlas;
    private BindGroupLayout* _pathAtlasBindGroupLayout;
    private BindGroup* _pathAtlasBindGroup;
    private BindGroupLayout* _pathAtlasBindGroupLayoutOffscreen;
    private BindGroup* _pathAtlasBindGroupOffscreen;

    // MSAA color target resources
    private Texture* _msaaTexture;
    private TextureView* _msaaTextureView;
    private uint _msaaWidth;
    private uint _msaaHeight;

    // Uniform buffer (Projection Matrix)
    private readonly GpuBuffer _uniformBuffer;
    private BindGroup* _vectorUniformBindGroup;
    private BindGroup* _textUniformBindGroup;
    private BindGroup* _textureUniformBindGroup;
    private BindGroupLayout* _vectorUniformBindGroupLayout;
    private BindGroupLayout* _textUniformBindGroupLayout;
    private BindGroupLayout* _textureUniformBindGroupLayout;

    private BindGroup* _vectorUniformBindGroupOffscreen;
    private BindGroup* _textUniformBindGroupOffscreen;
    private BindGroup* _textureUniformBindGroupOffscreen;
    private BindGroupLayout* _vectorUniformBindGroupLayoutOffscreen;
    private BindGroupLayout* _textUniformBindGroupLayoutOffscreen;
    private BindGroupLayout* _textureUniformBindGroupLayoutOffscreen;

    // Sampler & Texture Bind Group for Typography
    private Sampler* _atlasSampler;
    private BindGroup* _atlasBindGroup;
    private BindGroupLayout* _atlasBindGroupLayout;
    private BindGroup* _atlasBindGroupOffscreen;
    private BindGroupLayout* _atlasBindGroupLayoutOffscreen;

    // Render Pipelines
    private RenderPipeline* _vectorPipeline;
    private RenderPipeline* _textPipeline;
    private RenderPipeline* _texturePipeline;
    private RenderPipeline* _vectorPipelineOffscreen;
    private RenderPipeline* _textPipelineOffscreen;
    private RenderPipeline* _texturePipelineOffscreen;
    private BindGroupLayout* _textureBindGroupLayout;
    private BindGroupLayout* _textureBindGroupLayoutOffscreen;

    // Batch buffers (Dynamic GPU vertex & index buffers)
    private GpuBuffer _vectorVertexBuffer;
    private GpuBuffer _vectorIndexBuffer;
    private GpuBuffer _textVertexBuffer;
    private GpuBuffer _textIndexBuffer;
    private GpuBuffer _textureVertexBuffer;
    private GpuBuffer _textureIndexBuffer;

    public enum DrawCallType
    {
        Vector,
        Texture,
        Text
    }

    public struct CompositorDrawCall
    {
        public DrawCallType Type;
        public uint IndexStart;
        public uint IndexCount;
        public GpuTexture? Texture;
    }

    private readonly List<VectorVertex> _vectorVerticesList = new();
    private readonly List<uint> _vectorIndicesList = new();
    private readonly List<VectorVertex> _textVerticesList = new();
    private readonly List<uint> _textIndicesList = new();
    private readonly List<VectorVertex> _textureVerticesList = new();
    private readonly List<uint> _textureIndicesList = new();
    private readonly List<CompositorDrawCall> _drawCalls = new();
    private readonly Dictionary<nint, nint> _textureBindGroups = new();
    private readonly List<GpuBrush> _activeBrushes = new();
    private readonly Dictionary<(string Text, TtfFont Font, float Size, TextAlignment Align), TextLayout> _layoutCache = new();

    private readonly ComputeAccelerator _compute;
    private readonly Dictionary<Visual, (GpuTexture Source, GpuTexture Temp, GpuTexture Destination)> _effectTextures = new();
    private readonly HashSet<Visual> _elementsRenderingEffects = new();

    private bool _isDisposed;

    private readonly Stack<Rect> _clipStack = new();
    private Rect? _activeClipRect;

    public static float DefaultTextGamma = 1.43f;
    public static float DefaultTextContrast = 1.15f;

    public int VectorVertexCount => _vectorVerticesList.Count;
    public int VectorIndexCount => _vectorIndicesList.Count;
    public int TextVertexCount => _textVerticesList.Count;
    public int TextIndexCount => _textIndicesList.Count;
    public int TextureVertexCount => _textureVerticesList.Count;
    public int TextureIndexCount => _textureIndicesList.Count;
    public int TextureDrawCallCount
    {
        get
        {
            int count = 0;
            foreach (var dc in _drawCalls)
            {
                if (dc.Type == DrawCallType.Texture) count++;
            }
            return count;
        }
    }

    public GlyphAtlas Atlas => _atlas;
    public TextureFormat RenderFormat { get; private set; }

    public Compositor(WgpuContext context, TextureFormat? renderFormat = null)
    {
        _context = context;
        RenderFormat = renderFormat ?? _context.SwapChainFormat;
        _pipelineCache = new RenderPipelineCache(_context);
        _compute = new ComputeAccelerator(_context);
        
        // 1. Initialize Glyph Atlas (1024x1024)
        _atlas = new GlyphAtlas(_context, 1024);
        _pathAtlas = new PathAtlas(_context, 2048);

        // 2. Uniform Buffer allocation (Projection Matrix & 64 Brushes - 8256 bytes)
        _uniformBuffer = new GpuBuffer(
            _context, 
            8256, 
            BufferUsage.Uniform | BufferUsage.CopyDst, 
            "Compositor Uniform Projection & Brushes Buffer"
        );

        // 3. Dynamic mesh buffer setup (Vertex format: VectorVertex)
        uint initialVertexCount = 100000;
        uint initialIndexCount = 150000;
        uint vertexStride = (uint)Marshal.SizeOf<VectorVertex>();

        _vectorVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Vector Vertex Buffer");
        _vectorIndexBuffer = new GpuBuffer(_context, initialIndexCount * 4, BufferUsage.Index | BufferUsage.CopyDst, "Vector Index Buffer");

        _textVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Text Vertex Buffer");
        _textIndexBuffer = new GpuBuffer(_context, initialIndexCount * 4, BufferUsage.Index | BufferUsage.CopyDst, "Text Index Buffer");

        _textureVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Texture Vertex Buffer");
        _textureIndexBuffer = new GpuBuffer(_context, initialIndexCount * 4, BufferUsage.Index | BufferUsage.CopyDst, "Texture Index Buffer");

        InitializePipelinesAndBindGroups();
    }

    private void InitializePipelinesAndBindGroups()
    {
        // 4. Create WebGPU Sampler for font glyph textures (sharp linear bilinear interpolation)
        var samplerDesc = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Linear,
            LodMaxClamp = 1f,
            LodMinClamp = 0f,
            MaxAnisotropy = 1
        };
        _atlasSampler = _context.Wgpu.DeviceCreateSampler(_context.Device, &samplerDesc);

        // 5. Compile WGSL shaders
        var vecShaderModule = _pipelineCache.GetOrCreateShader("Vector", Shaders.VectorShader, "VectorShader");
        var textShaderModule = _pipelineCache.GetOrCreateShader("Text", Shaders.TextShader, "TextShader");
        var texShaderModule = _pipelineCache.GetOrCreateShader("Texture", Shaders.TextureShader, "TextureShader");

        var vertexAttribs = new VertexAttribute[]
        {
            new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }, // Position
            new() { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }, // Color
            new() { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }, // TexCoord
            new() { Format = VertexFormat.Float32, Offset = 32, ShaderLocation = 3 }, // BrushIndex
            new() { Format = VertexFormat.Float32x2, Offset = 36, ShaderLocation = 4 }, // ShapeSize
            new() { Format = VertexFormat.Float32, Offset = 44, ShaderLocation = 5 }, // CornerRadius
            new() { Format = VertexFormat.Float32, Offset = 48, ShaderLocation = 6 }, // StrokeThickness
            new() { Format = VertexFormat.Float32, Offset = 52, ShaderLocation = 7 } // ShapeType
        };

        fixed (VertexAttribute* attribsPtr = vertexAttribs)
        {
            var layoutDesc = new VertexBufferLayout
            {
                ArrayStride = (uint)Marshal.SizeOf<VectorVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 8,
                Attributes = attribsPtr
            };

            // Compile primary graphics pipelines with 4x MSAA
            _vectorPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Vector", 
                vecShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 4
            );

            _textPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Text", 
                textShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 4
            );

            _texturePipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Texture", 
                texShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 4
            );

            _vectorPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "Vector_Offscreen", 
                vecShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 1
            );

            _textPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "Text_Offscreen", 
                textShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 1
            );

            _texturePipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "Texture_Offscreen", 
                texShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 1
            );
        }

        _textureBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipeline, 1);
        _textureBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipelineOffscreen, 1);

        // 7. Uniform bind groups structure configuration
        _vectorUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipeline, 0);
        _textUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipeline, 0);
        _textureUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipeline, 0);
        
        _vectorUniformBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipelineOffscreen, 0);
        _textUniformBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipelineOffscreen, 0);
        _textureUniformBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipelineOffscreen, 0);

        var uBufferEntry = new BindGroupEntry
        {
            Binding = 0,
            Buffer = _uniformBuffer.BufferPtr,
            Offset = 0,
            Size = 8256
        };

        var uDescVector = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _vectorUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _vectorUniformBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVectorOffscreen);

        var uDescText = new BindGroupDescriptor
        {
            Layout = _textUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescText);

        var uDescTextOffscreen = new BindGroupDescriptor
        {
            Layout = _textUniformBindGroupLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textUniformBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescTextOffscreen);

        var uDescTexture = new BindGroupDescriptor
        {
            Layout = _textureUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textureUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescTexture);

        var uDescTextureOffscreen = new BindGroupDescriptor
        {
            Layout = _textureUniformBindGroupLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textureUniformBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescTextureOffscreen);

        // 8. Atlas bind group structure configuration
        _atlasBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipeline, 1);
        _atlasBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipelineOffscreen, 1);

        var samplerEntry = new BindGroupEntry
        {
            Binding = 0,
            Sampler = _atlasSampler
        };

        var viewEntry = new BindGroupEntry
        {
            Binding = 1,
            TextureView = _atlas.AtlasTexture.ViewPtr
        };

        var atlasEntries = stackalloc BindGroupEntry[2];
        atlasEntries[0] = samplerEntry;
        atlasEntries[1] = viewEntry;

        var atlasDesc = new BindGroupDescriptor
        {
            Layout = _atlasBindGroupLayout,
            EntryCount = 2,
            Entries = atlasEntries
        };
        _atlasBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &atlasDesc);

        var atlasDescOffscreen = new BindGroupDescriptor
        {
            Layout = _atlasBindGroupLayoutOffscreen,
            EntryCount = 2,
            Entries = atlasEntries
        };
        _atlasBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &atlasDescOffscreen);

        // Initialize Path Atlas bind group
        _pathAtlasBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipeline, 1);
        _pathAtlasBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipelineOffscreen, 1);

        var pathViewEntry = new BindGroupEntry
        {
            Binding = 1,
            TextureView = _pathAtlas.AtlasTexture.ViewPtr
        };
        var pathAtlasEntries = stackalloc BindGroupEntry[2];
        pathAtlasEntries[0] = samplerEntry;
        pathAtlasEntries[1] = pathViewEntry;

        var pathAtlasDesc = new BindGroupDescriptor
        {
            Layout = _pathAtlasBindGroupLayout,
            EntryCount = 2,
            Entries = pathAtlasEntries
        };
        _pathAtlasBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &pathAtlasDesc);

        var pathAtlasDescOffscreen = new BindGroupDescriptor
        {
            Layout = _pathAtlasBindGroupLayoutOffscreen,
            EntryCount = 2,
            Entries = pathAtlasEntries
        };
        _pathAtlasBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &pathAtlasDescOffscreen);
    }

    public void RenderScene(Visual root, uint width, uint height, TextureView* targetView)
    {
        if (_isDisposed) return;
        _pathAtlas.CleanupFrame();

        // Invoke pre-render actions (e.g. measure/arrange popups in UI framework)
        PreRender?.Invoke(width, height);

        // 1. Calculate orthographic projection matrix for modern 2D rendering
        // Maps X in [0, width] to [-1, 1], and Y in [0, height] to [1, -1]
        var projection = new Matrix4x4(
            2.0f / width, 0f, 0f, 0f,
            0f, -2.0f / height, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1.0f, 1.0f, 0f, 1.0f
        );

        // 2. Clear CPU collection batch lists and active brushes
        _activeBrushes.Clear();
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textIndicesList.Clear();
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _drawCalls.Clear();

        if (_layoutCache.Count > 1000)
        {
            _layoutCache.Clear();
        }

        _clipStack.Clear();
        _activeClipRect = null;

        // 3. Compile Layer 0: Root Visual Scene
        uint vecStart = (uint)_vectorIndicesList.Count;
        uint textStart = (uint)_textIndicesList.Count;
        CompileVisualTree(root, Matrix4x4.Identity);
        
        uint vecCount = (uint)_vectorIndicesList.Count - vecStart;
        if (vecCount > 0)
        {
            _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Vector, IndexStart = vecStart, IndexCount = vecCount });
        }
        uint textCount = (uint)_textIndicesList.Count - textStart;
        if (textCount > 0)
        {
            _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Text, IndexStart = textStart, IndexCount = textCount });
        }

        // 4. Compile Layer 1: Active Popups / External Layers (in proper Z-order)
        var externalLayers = GetExternalLayers?.Invoke();
        if (externalLayers != null)
        {
            for (int i = 0; i < externalLayers.Count; i++)
            {
                var layer = externalLayers[i];
                uint vecStartPopup = (uint)_vectorIndicesList.Count;
                uint textStartPopup = (uint)_textIndicesList.Count;
                
                CompileVisualTree(layer, Matrix4x4.Identity);
                
                uint vecCountPopup = (uint)_vectorIndicesList.Count - vecStartPopup;
                if (vecCountPopup > 0)
                {
                    _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Vector, IndexStart = vecStartPopup, IndexCount = vecCountPopup });
                }
                uint textCountPopup = (uint)_textIndicesList.Count - textStartPopup;
                if (textCountPopup > 0)
                {
                    _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Text, IndexStart = textStartPopup, IndexCount = textCountPopup });
                }
            }
        }

        // 5. Compile Layer 2: Tooltips
        var activeToolTip = GetTooltip?.Invoke();
        if (activeToolTip != null)
        {
            uint vecStartTip = (uint)_vectorIndicesList.Count;
            uint textStartTip = (uint)_textIndicesList.Count;
            
            CompileVisualTree(activeToolTip, Matrix4x4.Identity);
            
            uint vecCountTip = (uint)_vectorIndicesList.Count - vecStartTip;
            if (vecCountTip > 0)
            {
                _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Vector, IndexStart = vecStartTip, IndexCount = vecCountTip });
            }
            uint textCountTip = (uint)_textIndicesList.Count - textStartTip;
            if (textCountTip > 0)
            {
                _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Text, IndexStart = textStartTip, IndexCount = textCountTip });
            }
        }

        // 6. Compile Layer 3: Adorner / DevTools bounds highlights
        if (RenderDiagnostics != null)
        {
            uint vecStartAdorner = (uint)_vectorIndicesList.Count;
            uint textStartAdorner = (uint)_textIndicesList.Count;

            var diagContext = new DrawingContext();
            RenderDiagnostics(diagContext, width, height);
            foreach (var cmd in diagContext.Commands)
            {
                switch (cmd.Type)
                {
                    case RenderCommandType.DrawRect:
                        CompileRectCommand(cmd, Matrix4x4.Identity);
                        break;
                    case RenderCommandType.DrawPath:
                        CompilePathCommand(cmd, Matrix4x4.Identity);
                        break;
                    case RenderCommandType.DrawText:
                        CompileTextCommand(cmd, null, Matrix4x4.Identity);
                        break;
                }
            }

            uint vecCountAdorner = (uint)_vectorIndicesList.Count - vecStartAdorner;
            if (vecCountAdorner > 0)
            {
                _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Vector, IndexStart = vecStartAdorner, IndexCount = vecCountAdorner });
            }
            uint textCountAdorner = (uint)_textIndicesList.Count - textStartAdorner;
            if (textCountAdorner > 0)
            {
                _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Text, IndexStart = textStartAdorner, IndexCount = textCountAdorner });
            }
        }

        // Upload CPU batches to dynamic GPU buffers
        if (_vectorVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorVertexBuffer, (uint)_vectorVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _vectorVertexBuffer.Write(CollectionsMarshal.AsSpan(_vectorVerticesList));
        }
        if (_vectorIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorIndexBuffer, (uint)_vectorIndicesList.Count * 4, BufferUsage.Index);
            _vectorIndexBuffer.Write(CollectionsMarshal.AsSpan(_vectorIndicesList));
        }

        if (_textVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textVertexBuffer, (uint)_textVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textVertexBuffer.Write(CollectionsMarshal.AsSpan(_textVerticesList));
        }
        if (_textIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textIndexBuffer, (uint)_textIndicesList.Count * 4, BufferUsage.Index);
            _textIndexBuffer.Write(CollectionsMarshal.AsSpan(_textIndicesList));
        }

        if (_textureVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textureVertexBuffer, (uint)_textureVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textureVertexBuffer.Write(CollectionsMarshal.AsSpan(_textureVerticesList));
        }
        if (_textureIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textureIndexBuffer, (uint)_textureIndicesList.Count * 4, BufferUsage.Index);
            _textureIndexBuffer.Write(CollectionsMarshal.AsSpan(_textureIndicesList));
        }

        // Upload unified projection matrix and compiled brushes to GpuUniforms
        var uniforms = new GpuUniforms();
        uniforms.Projection = projection;
        GpuBrush* pBrushes = &uniforms.Brush0;
        for (int i = 0; i < Math.Min(64, _activeBrushes.Count); i++)
        {
            pBrushes[i] = _activeBrushes[i];
        }
        _uniformBuffer.WriteSingle(uniforms);

        // Recreate MSAA resources if needed (handles initialization and window resizing)
        if (_msaaTexture == null || _msaaWidth != width || _msaaHeight != height)
        {
            ReleaseMsaaResources();
            CreateMsaaResources(width, height);
        }

        // 5. WebGPU Command Encoder and Render Pass Execution
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compositor Command Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var bgColor = ClearColor;
        var colorAttachment = new RenderPassColorAttachment
        {
            View = _msaaTextureView,
            ResolveTarget = targetView,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = bgColor.X, G = bgColor.Y, B = bgColor.Z, A = bgColor.W }
        };

        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = null
        };

        var pass = _context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);

        DrawCallType? currentType = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        foreach (var dc in _drawCalls)
        {
            if (dc.Type == DrawCallType.Vector)
            {
                if (currentType != DrawCallType.Vector)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, _vectorPipeline);
                    fixed (BindGroup** pGrp = &_vectorUniformBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                    }
                    var buffer = _vectorVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                    currentType = DrawCallType.Vector;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                if (currentType != DrawCallType.Text)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, _textPipeline);
                    fixed (BindGroup** pGrp = &_textUniformBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pAtlas = &_atlasBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                    }
                    var buffer = _textVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textIndexBuffer.Size);
                    currentType = DrawCallType.Text;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Texture && dc.Texture != null)
            {
                _context.Wgpu.RenderPassEncoderSetPipeline(pass, _texturePipeline);
                fixed (BindGroup** pGrp = &_textureUniformBindGroup)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                }
                var buffer = _textureVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                currentType = DrawCallType.Texture;

                var viewPtr = dc.Texture.ViewPtr;
                nint viewKey = (nint)viewPtr;

                if (!_textureBindGroups.TryGetValue(viewKey, out var bgPtrVal))
                {
                    textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
                    textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = viewPtr };

                    var bgDesc = new BindGroupDescriptor { Layout = _textureBindGroupLayout, EntryCount = 2, Entries = textureEntries };
                    var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
                    if (bg == null)
                    {
                        System.Console.WriteLine($"[Compositor Error] Failed to create BindGroup for TextureView {(nint)viewPtr}");
                    }
                    bgPtrVal = (nint)bg;
                    _textureBindGroups[viewKey] = bgPtrVal;
                }

                var bindGroup = (BindGroup*)bgPtrVal;
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
        }

        _context.Wgpu.RenderPassEncoderEnd(pass);
        _context.Wgpu.RenderPassEncoderRelease(pass);

        // Submit to queue
        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compositor Command Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        // Release and clear dynamic texture bind groups to avoid leaking or using stale view pointers on next frame
        foreach (var bgVal in _textureBindGroups.Values)
        {
            if (bgVal != 0)
            {
                _context.Wgpu.BindGroupRelease((BindGroup*)bgVal);
            }
        }
        _textureBindGroups.Clear();
    }

    private void PushClipRect(Rect localClip, Matrix4x4 transform)
    {
        var vTopLeft = Vector2.Transform(new Vector2(localClip.X, localClip.Y), transform);
        var vBottomRight = Vector2.Transform(new Vector2(localClip.X + localClip.Width, localClip.Y + localClip.Height), transform);
        
        float x1 = Math.Min(vTopLeft.X, vBottomRight.X);
        float y1 = Math.Min(vTopLeft.Y, vBottomRight.Y);
        float x2 = Math.Max(vTopLeft.X, vBottomRight.X);
        float y2 = Math.Max(vTopLeft.Y, vBottomRight.Y);
        
        var screenClip = new Rect(x1, y1, x2 - x1, y2 - y1);

        if (_activeClipRect.HasValue)
        {
            float cx1 = Math.Max(_activeClipRect.Value.X, screenClip.X);
            float cy1 = Math.Max(_activeClipRect.Value.Y, screenClip.Y);
            float cx2 = Math.Min(_activeClipRect.Value.X + _activeClipRect.Value.Width, screenClip.X + screenClip.Width);
            float cy2 = Math.Min(_activeClipRect.Value.Y + _activeClipRect.Value.Height, screenClip.Y + screenClip.Height);
            _activeClipRect = new Rect(cx1, cy1, Math.Max(0f, cx2 - cx1), Math.Max(0f, cy2 - cy1));
        }
        else
        {
            _activeClipRect = screenClip;
        }
        _clipStack.Push(_activeClipRect.Value);
    }

    private void PopClipRect()
    {
        if (_clipStack.Count > 0)
        {
            _clipStack.Pop();
            _activeClipRect = _clipStack.Count > 0 ? _clipStack.Peek() : null;
        }
    }

    private void CompileVisualTree(Visual node, Matrix4x4 parentTransform)
    {
        if (node.Effect != null && !_elementsRenderingEffects.Contains(node))
        {
            ApplyAndDrawEffect(node, parentTransform);
            return;
        }

        // 1. Calculate global transform
        var localTransform = node.GetLocalTransform();
        var globalTransform = localTransform * parentTransform;

        bool pushedClip = false;
        if (node.ClipBounds.HasValue)
        {
            PushClipRect(node.ClipBounds.Value, globalTransform);
            pushedClip = true;
        }

        // 2. Playback recorded commands
        var ctx = new DrawingContext();
        node.OnRender(ctx);

        foreach (var cmd in ctx.Commands)
        {
            switch (cmd.Type)
            {
                case RenderCommandType.DrawRect:
                    CompileRectCommand(cmd, globalTransform);
                    break;
                case RenderCommandType.DrawPath:
                    CompilePathCommand(cmd, globalTransform);
                    break;
                case RenderCommandType.DrawText:
                    CompileTextCommand(cmd, node as ITextLayoutProvider, globalTransform);
                    break;
                case RenderCommandType.DrawTexture:
                    CompileTextureCommand(cmd, globalTransform);
                    break;
                case RenderCommandType.PushClip:
                    PushClipRect(cmd.Rect, globalTransform);
                    break;
                case RenderCommandType.PopClip:
                    PopClipRect();
                    break;
                case RenderCommandType.DrawLine:
                    CompileLineCommand(cmd, globalTransform);
                    break;
                case RenderCommandType.DrawEllipse:
                    CompileEllipseCommand(cmd, globalTransform);
                    break;
                case RenderCommandType.DrawCircle:
                    CompileCircleCommand(cmd, globalTransform);
                    break;
                case RenderCommandType.DrawRoundedRect:
                    CompileRoundedRectCommand(cmd, globalTransform);
                    break;
                case RenderCommandType.DrawBezier:
                    CompileBezierCommand(cmd, globalTransform);
                    break;
                case RenderCommandType.DrawCubicBezier:
                    CompileCubicBezierCommand(cmd, globalTransform);
                    break;
            }
        }

        // 3. Process children recursively
        if (node is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                CompileVisualTree(child, globalTransform);
            }
        }

        if (pushedClip)
        {
            PopClipRect();
        }
    }

    private void CompileRectCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        int startIndex = _vectorVerticesList.Count;
        var r = cmd.Rect;
        float wHalf = r.Width / 2f;
        float hHalf = r.Height / 2f;
        var shapeSize = new Vector2(r.Width, r.Height);

        if (cmd.Brush != null)
        {
            float pad = 1.5f;
            var f0_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y - pad), transform);
            var f1_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y - pad), transform);
            var f2_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad), transform);
            var f3_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y + r.Height + pad), transform);

            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(r.X + wHalf, r.Y + hHalf, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-wHalf - pad, -hHalf - pad), bIdx, shapeSize, 0f, 0f, 0f);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(wHalf + pad, -hHalf - pad), bIdx, shapeSize, 0f, 0f, 0f);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(wHalf + pad, hHalf + pad), bIdx, shapeSize, 0f, 0f, 0f);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-wHalf - pad, hHalf + pad), bIdx, shapeSize, 0f, 0f, 0f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (cmd.Pen != null)
        {
            float pad = cmd.Pen.Thickness / 2f + 1.5f;
            var p0_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y - pad), transform);
            var p1_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y - pad), transform);
            var p2_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad), transform);
            var p3_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y + r.Height + pad), transform);

            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solidPen) ? solidPen.Color : new Vector4(r.X + wHalf, r.Y + hHalf, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-wHalf - pad, -hHalf - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(wHalf + pad, -hHalf - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(wHalf + pad, hHalf + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-wHalf - pad, hHalf + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }


    private void CompilePathCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Path == null) return;
        int startIndex = _vectorVerticesList.Count;

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var brush = cmd.Brush as SolidColorBrush;
            var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

            var info = _pathAtlas.GetOrCreatePath(cmd.Path);
            if (info.Width > 0 && info.Height > 0)
            {
                var v0 = Vector2.Transform(new Vector2(info.MinX, info.MinY), transform);
                var v1 = Vector2.Transform(new Vector2(info.MinX + info.Width, info.MinY), transform);
                var v2 = Vector2.Transform(new Vector2(info.MinX + info.Width, info.MinY + info.Height), transform);
                var v3 = Vector2.Transform(new Vector2(info.MinX, info.MinY + info.Height), transform);

                var uv0 = new Vector2(info.TexCoordMin.X, info.TexCoordMin.Y);
                var uv1 = new Vector2(info.TexCoordMax.X, info.TexCoordMin.Y);
                var uv2 = new Vector2(info.TexCoordMax.X, info.TexCoordMax.Y);
                var uv3 = new Vector2(info.TexCoordMin.X, info.TexCoordMax.Y);

                var cp0 = new Vector2(info.MinX, info.MinY);
                var cp1 = new Vector2(info.MinX + info.Width, info.MinY);
                var cp2 = new Vector2(info.MinX + info.Width, info.MinY + info.Height);
                var cp3 = new Vector2(info.MinX, info.MinY + info.Height);

                if (_activeClipRect.HasValue)
                {
                    float rx1 = v0.X;
                    float ry1 = v0.Y;
                    float rx2 = v2.X;
                    float ry2 = v2.Y;

                    float cx1 = Math.Max(rx1, _activeClipRect.Value.X);
                    float cy1 = Math.Max(ry1, _activeClipRect.Value.Y);
                    float cx2 = Math.Min(rx2, _activeClipRect.Value.X + _activeClipRect.Value.Width);
                    float cy2 = Math.Min(ry2, _activeClipRect.Value.Y + _activeClipRect.Value.Height);

                    if (cx2 > cx1 && cy2 > cy1)
                    {
                        float dx = rx2 - rx1;
                        float dy = ry2 - ry1;

                        uv0 = new Vector2(
                            info.TexCoordMin.X + (cx1 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy1 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv1 = new Vector2(
                            info.TexCoordMin.X + (cx2 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy1 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv2 = new Vector2(
                            info.TexCoordMin.X + (cx2 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy2 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv3 = new Vector2(
                            info.TexCoordMin.X + (cx1 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy2 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );

                        float cp0_x = dx > 0.0001f ? info.MinX + (cx1 - rx1) / dx * info.Width : info.MinX;
                        float cp0_y = dy > 0.0001f ? info.MinY + (cy1 - ry1) / dy * info.Height : info.MinY;
                        float cp2_x = dx > 0.0001f ? info.MinX + (cx2 - rx1) / dx * info.Width : info.MinX + info.Width;
                        float cp2_y = dy > 0.0001f ? info.MinY + (cy2 - ry1) / dy * info.Height : info.MinY + info.Height;

                        var cp0_clip = new Vector2(cp0_x, cp0_y);
                        var cp1_clip = new Vector2(cp2_x, cp0_y);
                        var cp2_clip = new Vector2(cp2_x, cp2_y);
                        var cp3_clip = new Vector2(cp0_x, cp2_y);

                        v0 = new Vector2(cx1, cy1);
                        v1 = new Vector2(cx2, cy1);
                        v2 = new Vector2(cx2, cy2);
                        v3 = new Vector2(cx1, cy2);

                        uint idxStart = (uint)_vectorVerticesList.Count;

                        int originalVertexCount = _vectorVerticesList.Count;
                        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
                        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

                        vertexSpan[0] = new VectorVertex(v0, color, uv0, bIdx, shapeSize: cp0_clip, shapeType: 4f);
                        vertexSpan[1] = new VectorVertex(v1, color, uv1, bIdx, shapeSize: cp1_clip, shapeType: 4f);
                        vertexSpan[2] = new VectorVertex(v2, color, uv2, bIdx, shapeSize: cp2_clip, shapeType: 4f);
                        vertexSpan[3] = new VectorVertex(v3, color, uv3, bIdx, shapeSize: cp3_clip, shapeType: 4f);

                        int originalIndexCount = _vectorIndicesList.Count;
                        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
                        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

                        indexSpan[0] = idxStart;
                        indexSpan[1] = idxStart + 1;
                        indexSpan[2] = idxStart + 2;
                        indexSpan[3] = idxStart;
                        indexSpan[4] = idxStart + 2;
                        indexSpan[5] = idxStart + 3;
                    }
                }
                else
                {
                    uint idxStart = (uint)_vectorVerticesList.Count;

                    int originalVertexCount = _vectorVerticesList.Count;
                    CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
                    var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

                    vertexSpan[0] = new VectorVertex(v0, color, uv0, bIdx, shapeSize: cp0, shapeType: 4f);
                    vertexSpan[1] = new VectorVertex(v1, color, uv1, bIdx, shapeSize: cp1, shapeType: 4f);
                    vertexSpan[2] = new VectorVertex(v2, color, uv2, bIdx, shapeSize: cp2, shapeType: 4f);
                    vertexSpan[3] = new VectorVertex(v3, color, uv3, bIdx, shapeSize: cp3, shapeType: 4f);

                    int originalIndexCount = _vectorIndicesList.Count;
                    CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
                    var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

                    indexSpan[0] = idxStart;
                    indexSpan[1] = idxStart + 1;
                    indexSpan[2] = idxStart + 2;
                    indexSpan[3] = idxStart;
                    indexSpan[4] = idxStart + 2;
                    indexSpan[5] = idxStart + 3;
                }
            }
        }

        if (cmd.Pen != null)
        {
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
            float thickness = cmd.Pen.Thickness;

            int maxVertices = 0;
            int maxIndices = 0;
            foreach (var figure in cmd.Path.Figures)
            {
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment)
                    {
                        maxVertices += 4;
                        maxIndices += 6;
                    }
                    else if (segment is QuadraticBezierSegment)
                    {
                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        maxVertices += 2 * (N + 1);
                        maxIndices += 6 * N;
                    }
                    else if (segment is CubicBezierSegment)
                    {
                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        maxVertices += 2 * (N + 1);
                        maxIndices += 6 * N;
                    }
                }
                if (figure.IsClosed)
                {
                    maxVertices += 4;
                    maxIndices += 6;
                }
            }

            int vertexStart = _vectorVerticesList.Count;
            int indexStart = _vectorIndicesList.Count;

            CollectionsMarshal.SetCount(_vectorVerticesList, vertexStart + maxVertices);
            CollectionsMarshal.SetCount(_vectorIndicesList, indexStart + maxIndices);

            var verticesSpan = CollectionsMarshal.AsSpan(_vectorVerticesList);
            var indicesSpan = CollectionsMarshal.AsSpan(_vectorIndicesList);

            int currentVertexCount = vertexStart;
            int currentIndexCount = indexStart;

            foreach (var figure in cmd.Path.Figures)
            {
                var currentPoint = figure.StartPoint;

                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        var p0_trans = Vector2.Transform(currentPoint, transform);
                        var p1_trans = Vector2.Transform(line.Point, transform);

                        uint idxStart = (uint)currentVertexCount;

                        verticesSpan[currentVertexCount++] = new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f);
                        verticesSpan[currentVertexCount++] = new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f);
                        verticesSpan[currentVertexCount++] = new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f);
                        verticesSpan[currentVertexCount++] = new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f);

                        indicesSpan[currentIndexCount++] = idxStart;
                        indicesSpan[currentIndexCount++] = idxStart + 1;
                        indicesSpan[currentIndexCount++] = idxStart + 2;

                        indicesSpan[currentIndexCount++] = idxStart + 1;
                        indicesSpan[currentIndexCount++] = idxStart + 3;
                        indicesSpan[currentIndexCount++] = idxStart + 2;

                        currentPoint = line.Point;
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        var p0_trans = Vector2.Transform(currentPoint, transform);
                        var p1_trans = Vector2.Transform(quad.ControlPoint, transform);
                        var p2_trans = Vector2.Transform(quad.Point, transform);

                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        uint idxStart = (uint)currentVertexCount;

                        var baseVertex = new VectorVertex(p0_trans, Vector4.Zero, p1_trans, penBrushIdx, p2_trans, idxStart, thickness, 5f);
                        int vertexToAdd = 2 * (N + 1);
                        verticesSpan.Slice(currentVertexCount, vertexToAdd).Fill(baseVertex);
                        currentVertexCount += vertexToAdd;

                        for (int i = 0; i < N; i++)
                        {
                            uint currentLeft = (uint)(idxStart + 2 * i);
                            uint currentRight = (uint)(idxStart + 2 * i + 1);
                            uint nextLeft = (uint)(idxStart + 2 * i + 2);
                            uint nextRight = (uint)(idxStart + 2 * i + 3);

                            indicesSpan[currentIndexCount++] = currentLeft;
                            indicesSpan[currentIndexCount++] = currentRight;
                            indicesSpan[currentIndexCount++] = nextLeft;

                            indicesSpan[currentIndexCount++] = currentRight;
                            indicesSpan[currentIndexCount++] = nextRight;
                            indicesSpan[currentIndexCount++] = nextLeft;
                        }

                        currentPoint = quad.Point;
                    }
                    else if (segment is CubicBezierSegment cubic)
                    {
                        var p0_trans = Vector2.Transform(currentPoint, transform);
                        var p1_trans = Vector2.Transform(cubic.ControlPoint1, transform);
                        var p2_trans = Vector2.Transform(cubic.ControlPoint2, transform);
                        var p3_trans = Vector2.Transform(cubic.Point, transform);

                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        uint idxStart = (uint)currentVertexCount;

                        var baseVertex = new VectorVertex(p0_trans, new Vector4(p3_trans.X, p3_trans.Y, 0f, 0f), p1_trans, penBrushIdx, p2_trans, idxStart, thickness, 6f);
                        int vertexToAdd = 2 * (N + 1);
                        verticesSpan.Slice(currentVertexCount, vertexToAdd).Fill(baseVertex);
                        currentVertexCount += vertexToAdd;

                        for (int i = 0; i < N; i++)
                        {
                            uint currentLeft = (uint)(idxStart + 2 * i);
                            uint currentRight = (uint)(idxStart + 2 * i + 1);
                            uint nextLeft = (uint)(idxStart + 2 * i + 2);
                            uint nextRight = (uint)(idxStart + 2 * i + 3);

                            indicesSpan[currentIndexCount++] = currentLeft;
                            indicesSpan[currentIndexCount++] = currentRight;
                            indicesSpan[currentIndexCount++] = nextLeft;

                            indicesSpan[currentIndexCount++] = currentRight;
                            indicesSpan[currentIndexCount++] = nextRight;
                            indicesSpan[currentIndexCount++] = nextLeft;
                        }

                        currentPoint = cubic.Point;
                    }
                }

                if (figure.IsClosed && currentPoint != figure.StartPoint)
                {
                    var p0_trans = Vector2.Transform(currentPoint, transform);
                    var p1_trans = Vector2.Transform(figure.StartPoint, transform);

                    uint idxStart = (uint)currentVertexCount;

                    verticesSpan[currentVertexCount++] = new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f);
                    verticesSpan[currentVertexCount++] = new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f);
                    verticesSpan[currentVertexCount++] = new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f);
                    verticesSpan[currentVertexCount++] = new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f);

                    indicesSpan[currentIndexCount++] = idxStart;
                    indicesSpan[currentIndexCount++] = idxStart + 1;
                    indicesSpan[currentIndexCount++] = idxStart + 2;

                    indicesSpan[currentIndexCount++] = idxStart + 1;
                    indicesSpan[currentIndexCount++] = idxStart + 3;
                    indicesSpan[currentIndexCount++] = idxStart + 2;
                }
            }

            CollectionsMarshal.SetCount(_vectorVerticesList, currentVertexCount);
            CollectionsMarshal.SetCount(_vectorIndicesList, currentIndexCount);
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileLineCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;
        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

        var p0_pos = Vector2.Transform(cmd.Position, transform);
        var p1_pos = Vector2.Transform(cmd.Position2, transform);
        float thickness = cmd.Pen.Thickness;

        uint idxStart = (uint)startIndex;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, 3f);
        vertexSpan[1] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, 3f);
        vertexSpan[2] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, 3f);
        vertexSpan[3] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, 3f);

        int originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;
        indexSpan[3] = idxStart + 1;
        indexSpan[4] = idxStart + 3;
        indexSpan[5] = idxStart + 2;

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileBezierCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;
        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        float thickness = cmd.Pen.Thickness;

        var p0_trans = Vector2.Transform(cmd.Position, transform);
        var p1_trans = Vector2.Transform(cmd.Position2, transform);
        var p2_trans = Vector2.Transform(cmd.Position3, transform);

        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
        uint idxStart = (uint)startIndex;

        var baseVertex = new VectorVertex(p0_trans, Vector4.Zero, p1_trans, penBrushIdx, p2_trans, idxStart, thickness, 5f);
        
        int originalVertexCount = _vectorVerticesList.Count;
        int vertexToAdd = 2 * (N + 1);
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + vertexToAdd);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, vertexToAdd);
        vertexSpan.Fill(baseVertex);

        int originalIndexCount = _vectorIndicesList.Count;
        int indicesToAdd = 6 * N;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + indicesToAdd);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, indicesToAdd);

        for (int i = 0; i < N; i++)
        {
            uint currentLeft = (uint)(idxStart + 2 * i);
            uint currentRight = (uint)(idxStart + 2 * i + 1);
            uint nextLeft = (uint)(idxStart + 2 * i + 2);
            uint nextRight = (uint)(idxStart + 2 * i + 3);

            int baseIdx = 6 * i;
            indexSpan[baseIdx] = currentLeft;
            indexSpan[baseIdx + 1] = currentRight;
            indexSpan[baseIdx + 2] = nextLeft;
            indexSpan[baseIdx + 3] = currentRight;
            indexSpan[baseIdx + 4] = nextRight;
            indexSpan[baseIdx + 5] = nextLeft;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileCubicBezierCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;
        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        float thickness = cmd.Pen.Thickness;

        var p0_trans = Vector2.Transform(cmd.Position, transform);
        var p1_trans = Vector2.Transform(cmd.Position2, transform);
        var p2_trans = Vector2.Transform(cmd.Position3, transform);
        var p3_trans = Vector2.Transform(cmd.Position4, transform);

        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
        uint idxStart = (uint)startIndex;

        var baseVertex = new VectorVertex(p0_trans, new Vector4(p3_trans.X, p3_trans.Y, 0f, 0f), p1_trans, penBrushIdx, p2_trans, idxStart, thickness, 6f);
        
        int originalVertexCount = _vectorVerticesList.Count;
        int vertexToAdd = 2 * (N + 1);
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + vertexToAdd);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, vertexToAdd);
        vertexSpan.Fill(baseVertex);

        int originalIndexCount = _vectorIndicesList.Count;
        int indicesToAdd = 6 * N;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + indicesToAdd);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, indicesToAdd);

        for (int i = 0; i < N; i++)
        {
            uint currentLeft = (uint)(idxStart + 2 * i);
            uint currentRight = (uint)(idxStart + 2 * i + 1);
            uint nextLeft = (uint)(idxStart + 2 * i + 2);
            uint nextRight = (uint)(idxStart + 2 * i + 3);

            int baseIdx = 6 * i;
            indexSpan[baseIdx] = currentLeft;
            indexSpan[baseIdx + 1] = currentRight;
            indexSpan[baseIdx + 2] = nextLeft;
            indexSpan[baseIdx + 3] = currentRight;
            indexSpan[baseIdx + 4] = nextRight;
            indexSpan[baseIdx + 5] = nextLeft;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileEllipseCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        int startIndex = _vectorVerticesList.Count;
        var center = cmd.Position2;
        var rx = cmd.RadiusX;
        var ry = cmd.RadiusY;
        var shapeSize = new Vector2(2f * rx, 2f * ry);

        if (cmd.Brush != null)
        {
            float pad = 1.5f;
            var f0_pos = Vector2.Transform(new Vector2(center.X - rx - pad, center.Y - ry - pad), transform);
            var f1_pos = Vector2.Transform(new Vector2(center.X + rx + pad, center.Y - ry - pad), transform);
            var f2_pos = Vector2.Transform(new Vector2(center.X + rx + pad, center.Y + ry + pad), transform);
            var f3_pos = Vector2.Transform(new Vector2(center.X - rx - pad, center.Y + ry + pad), transform);

            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(center.X, center.Y, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-rx - pad, -ry - pad), bIdx, shapeSize, 0f, 0f, 1f);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(rx + pad, -ry - pad), bIdx, shapeSize, 0f, 0f, 1f);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(rx + pad, ry + pad), bIdx, shapeSize, 0f, 0f, 1f);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-rx - pad, ry + pad), bIdx, shapeSize, 0f, 0f, 1f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (cmd.Pen != null)
        {
            float pad = cmd.Pen.Thickness / 2f + 1.5f;
            var p0_pos = Vector2.Transform(new Vector2(center.X - rx - pad, center.Y - ry - pad), transform);
            var p1_pos = Vector2.Transform(new Vector2(center.X + rx + pad, center.Y - ry - pad), transform);
            var p2_pos = Vector2.Transform(new Vector2(center.X + rx + pad, center.Y + ry + pad), transform);
            var p3_pos = Vector2.Transform(new Vector2(center.X - rx - pad, center.Y + ry + pad), transform);

            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solidPen) ? solidPen.Color : new Vector4(center.X, center.Y, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-rx - pad, -ry - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(rx + pad, -ry - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(rx + pad, ry + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-rx - pad, ry + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileCircleCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        cmd.RadiusY = cmd.RadiusX;
        CompileEllipseCommand(cmd, transform);
    }

    private void CompileRoundedRectCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        int startIndex = _vectorVerticesList.Count;
        var r = cmd.Rect;
        var radius = Math.Min(cmd.RadiusX, Math.Min(r.Width / 2f, r.Height / 2f));

        if (radius <= 0f)
        {
            CompileRectCommand(cmd, transform);
            return;
        }

        float wHalf = r.Width / 2f;
        float hHalf = r.Height / 2f;
        var shapeSize = new Vector2(r.Width, r.Height);

        if (cmd.Brush != null)
        {
            float pad = 1.5f;
            var f0_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y - pad), transform);
            var f1_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y - pad), transform);
            var f2_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad), transform);
            var f3_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y + r.Height + pad), transform);

            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(r.X + wHalf, r.Y + hHalf, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-wHalf - pad, -hHalf - pad), bIdx, shapeSize, radius, 0f, 2f);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(wHalf + pad, -hHalf - pad), bIdx, shapeSize, radius, 0f, 2f);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(wHalf + pad, hHalf + pad), bIdx, shapeSize, radius, 0f, 2f);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-wHalf - pad, hHalf + pad), bIdx, shapeSize, radius, 0f, 2f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (cmd.Pen != null)
        {
            float pad = cmd.Pen.Thickness / 2f + 1.5f;
            var p0_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y - pad), transform);
            var p1_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y - pad), transform);
            var p2_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad), transform);
            var p3_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y + r.Height + pad), transform);

            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solidPen) ? solidPen.Color : new Vector4(r.X + wHalf, r.Y + hHalf, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-wHalf - pad, -hHalf - pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(wHalf + pad, -hHalf - pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(wHalf + pad, hHalf + pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-wHalf - pad, hHalf + pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private float RegisterBrush(Brush? brush)
    {
        if (brush == null) return 0f;
        
        GpuBrush gpuBrush = new GpuBrush();
        gpuBrush.Opacity = brush.Opacity;

        if (brush is SolidColorBrush solid)
        {
            gpuBrush.Type = 0;
            gpuBrush.Color0 = solid.Color;
        }
        else if (brush is LinearGradientBrush linear)
        {
            gpuBrush.Type = 1;
            gpuBrush.StartPoint = linear.StartPoint;
            gpuBrush.EndPoint = linear.EndPoint;
            if (linear.Stops != null)
            {
                gpuBrush.StopCount = (uint)Math.Min(4, linear.Stops.Length);
                if (gpuBrush.StopCount > 0) gpuBrush.Color0 = linear.Stops[0].Color;
                if (gpuBrush.StopCount > 1) gpuBrush.Color1 = linear.Stops[1].Color;
                if (gpuBrush.StopCount > 2) gpuBrush.Color2 = linear.Stops[2].Color;
                if (gpuBrush.StopCount > 3) gpuBrush.Color3 = linear.Stops[3].Color;

                float o0 = gpuBrush.StopCount > 0 ? linear.Stops[0].Offset : 0f;
                float o1 = gpuBrush.StopCount > 1 ? linear.Stops[1].Offset : 1f;
                float o2 = gpuBrush.StopCount > 2 ? linear.Stops[2].Offset : 1f;
                float o3 = gpuBrush.StopCount > 3 ? linear.Stops[3].Offset : 1f;
                gpuBrush.Offsets = new Vector4(o0, o1, o2, o3);
            }
        }
        else if (brush is RadialGradientBrush radial)
        {
            gpuBrush.Type = 2;
            gpuBrush.Center = radial.Center;
            gpuBrush.Radius = radial.Radius;
            if (radial.Stops != null)
            {
                gpuBrush.StopCount = (uint)Math.Min(4, radial.Stops.Length);
                if (gpuBrush.StopCount > 0) gpuBrush.Color0 = radial.Stops[0].Color;
                if (gpuBrush.StopCount > 1) gpuBrush.Color1 = radial.Stops[1].Color;
                if (gpuBrush.StopCount > 2) gpuBrush.Color2 = radial.Stops[2].Color;
                if (gpuBrush.StopCount > 3) gpuBrush.Color3 = radial.Stops[3].Color;

                float o0 = gpuBrush.StopCount > 0 ? radial.Stops[0].Offset : 0f;
                float o1 = gpuBrush.StopCount > 1 ? radial.Stops[1].Offset : 1f;
                float o2 = gpuBrush.StopCount > 2 ? radial.Stops[2].Offset : 1f;
                float o3 = gpuBrush.StopCount > 3 ? radial.Stops[3].Offset : 1f;
                gpuBrush.Offsets = new Vector4(o0, o1, o2, o3);
            }
        }

        for (int i = 0; i < _activeBrushes.Count; i++)
        {
            if (BrushesEqual(_activeBrushes[i], gpuBrush))
            {
                return (float)i;
            }
        }

        if (_activeBrushes.Count < 64)
        {
            _activeBrushes.Add(gpuBrush);
            return (float)(_activeBrushes.Count - 1);
        }

        return 0f;
    }

    private bool BrushesEqual(GpuBrush a, GpuBrush b)
    {
        return a.Type == b.Type &&
               a.Opacity == b.Opacity &&
               a.StartPoint == b.StartPoint &&
               a.EndPoint == b.EndPoint &&
               a.Center == b.Center &&
               a.Radius == b.Radius &&
               a.StopCount == b.StopCount &&
               a.Color0 == b.Color0 &&
               a.Color1 == b.Color1 &&
               a.Color2 == b.Color2 &&
               a.Color3 == b.Color3 &&
               a.Offsets == b.Offsets;
    }

    private void CompileTextCommand(RenderCommand cmd, ITextLayoutProvider? textNode, Matrix4x4 transform)
    {
        var font = cmd.Font ?? textNode?.Font;
        if (font == null || cmd.Text == null) return;

        TextLayout? layout;
        if (textNode != null)
        {
            layout = textNode.GetOrUpdateLayout(_atlas);
        }
        else
        {
            var key = (cmd.Text, font, cmd.FontSize, TextAlignment.Left);
            if (!_layoutCache.TryGetValue(key, out layout))
            {
                layout = new TextLayout(cmd.Text, font, cmd.FontSize, 10000f, TextAlignment.Left, null);
                _layoutCache[key] = layout;
            }
        }

        if (layout == null) return;

        float bIdx = RegisterBrush(cmd.Brush);
        var brush = cmd.Brush as SolidColorBrush;
        var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

        int maxGlyphs = layout.Glyphs.Count;
        int maxPassCount = cmd.IsBold ? 2 : 1;
        int maxVertices = maxGlyphs * maxPassCount * 4;
        int maxIndices = maxGlyphs * maxPassCount * 6;

        int vertexStart = _textVerticesList.Count;
        int indexStart = _textIndicesList.Count;

        CollectionsMarshal.SetCount(_textVerticesList, vertexStart + maxVertices);
        CollectionsMarshal.SetCount(_textIndicesList, indexStart + maxIndices);

        var textVerticesSpan = CollectionsMarshal.AsSpan(_textVerticesList);
        var textIndicesSpan = CollectionsMarshal.AsSpan(_textIndicesList);

        int currentVertexCount = vertexStart;
        int currentIndexCount = indexStart;

        foreach (var runGlyph in layout.Glyphs)
        {
            ushort glyphIdx = font.GetGlyphIndex(runGlyph.CodePoint);
            var colorLayers = font.GetColorLayers(glyphIdx);

            if (colorLayers != null && colorLayers.Count > 0)
            {
                foreach (var layer in colorLayers)
                {
                    var layerOutline = font.GetGlyphOutline(layer.GlyphId);
                    if (layerOutline == null) continue;

                    float emScale = cmd.FontSize / font.UnitsPerEm;
                    var transformedOutline = new PathGeometry();
                    float x0 = runGlyph.Position.X + cmd.Position.X;
                    float y0 = runGlyph.Position.Y + cmd.Position.Y;

                    foreach (var fig in layerOutline.Figures)
                    {
                        Vector2 startPt = new Vector2(x0 + fig.StartPoint.X * emScale, y0 - fig.StartPoint.Y * emScale);
                        var newFig = new PathFigure(startPt) { IsClosed = fig.IsClosed, IsFilled = fig.IsFilled };
                        foreach (var seg in fig.Segments)
                        {
                            if (seg is LineSegment ls)
                            {
                                newFig.Segments.Add(new LineSegment(new Vector2(x0 + ls.Point.X * emScale, y0 - ls.Point.Y * emScale)));
                            }
                            else if (seg is QuadraticBezierSegment qbs)
                            {
                                newFig.Segments.Add(new QuadraticBezierSegment(
                                    new Vector2(x0 + qbs.ControlPoint.X * emScale, y0 - qbs.ControlPoint.Y * emScale),
                                    new Vector2(x0 + qbs.Point.X * emScale, y0 - qbs.Point.Y * emScale)
                                ));
                            }
                            else if (seg is CubicBezierSegment cbs)
                            {
                                newFig.Segments.Add(new CubicBezierSegment(
                                    new Vector2(x0 + cbs.ControlPoint1.X * emScale, y0 - cbs.ControlPoint1.Y * emScale),
                                    new Vector2(x0 + cbs.ControlPoint2.X * emScale, y0 - cbs.ControlPoint2.Y * emScale),
                                    new Vector2(x0 + cbs.Point.X * emScale, y0 - cbs.Point.Y * emScale)
                                ));
                            }
                        }
                        transformedOutline.Figures.Add(newFig);
                    }

                    var pathCmd = new RenderCommand
                    {
                        Type = RenderCommandType.DrawPath,
                        Path = transformedOutline,
                        Brush = new SolidColorBrush(layer.Color)
                    };
                    CompilePathCommand(pathCmd, transform);
                }
                continue;
            }

            var info = _atlas.GetOrCreateGlyph(font, runGlyph.CodePoint, cmd.FontSize);
            if (info.Width == 0 || info.Height == 0) continue;

            int passCount = cmd.IsBold ? 2 : 1;
            float boldOffset = cmd.FontSize * 0.035f;

            for (int pass = 0; pass < passCount; pass++)
            {
                float xOffset = pass * boldOffset;
                
                // Retrieve baseline coordinate from layout position by subtracting dummy Bear offset
                float baseCursorX = runGlyph.Position.X - runGlyph.Glyph.BearX;
                float baseCursorY = runGlyph.Position.Y - runGlyph.Glyph.BearY;

                float x0 = baseCursorX + info.BearX + cmd.Position.X + xOffset;
                float y0 = baseCursorY + info.BearY + cmd.Position.Y;
                float x1 = x0 + info.Width;
                float y1 = y0 + info.Height;

                float skewFactor = cmd.IsItalic ? 0.22f : 0f;
                float yBase = cmd.Position.Y + cmd.FontSize * 0.8f; // Baseline anchor

                float sx0 = x0 - (y0 - yBase) * skewFactor;
                float sx1 = x1 - (y0 - yBase) * skewFactor;
                float sx2 = x1 - (y1 - yBase) * skewFactor;
                float sx3 = x0 - (y1 - yBase) * skewFactor;

                // Transform vertices on CPU
                var v0 = Vector2.Transform(new Vector2(sx0, y0), transform);
                var v1 = Vector2.Transform(new Vector2(sx1, y0), transform);
                var v2 = Vector2.Transform(new Vector2(sx2, y1), transform);
                var v3 = Vector2.Transform(new Vector2(sx3, y1), transform);

                uint idxStart = (uint)currentVertexCount;

                // Set dynamic UV texture mappings
                var uv0 = new Vector2(info.TexCoordMin.X, info.TexCoordMin.Y);
                var uv1 = new Vector2(info.TexCoordMax.X, info.TexCoordMin.Y);
                var uv2 = new Vector2(info.TexCoordMax.X, info.TexCoordMax.Y);
                var uv3 = new Vector2(info.TexCoordMin.X, info.TexCoordMax.Y);

                if (_activeClipRect.HasValue)
                {
                    float rx1 = v0.X;
                    float ry1 = v0.Y;
                    float rx2 = v2.X;
                    float ry2 = v2.Y;

                    float cx1 = Math.Max(rx1, _activeClipRect.Value.X);
                    float cy1 = Math.Max(ry1, _activeClipRect.Value.Y);
                    float cx2 = Math.Min(rx2, _activeClipRect.Value.X + _activeClipRect.Value.Width);
                    float cy2 = Math.Min(ry2, _activeClipRect.Value.Y + _activeClipRect.Value.Height);

                    if (cx2 <= cx1 || cy2 <= cy1) continue; // Completely clipped!

                    float dx = rx2 - rx1;
                    float dy = ry2 - ry1;

                    uv0 = new Vector2(
                        info.TexCoordMin.X + (cx1 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                        info.TexCoordMin.Y + (cy1 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                    );
                    uv1 = new Vector2(
                        info.TexCoordMin.X + (cx2 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                        info.TexCoordMin.Y + (cy1 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                    );
                    uv2 = new Vector2(
                        info.TexCoordMin.X + (cx2 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                        info.TexCoordMin.Y + (cy2 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                    );
                    uv3 = new Vector2(
                        info.TexCoordMin.X + (cx1 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                        info.TexCoordMin.Y + (cy2 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                    );

                    v0 = new Vector2(cx1, cy1);
                    v1 = new Vector2(cx2, cy1);
                    v2 = new Vector2(cx2, cy2);
                    v3 = new Vector2(cx1, cy2);
                }

                textVerticesSpan[currentVertexCount++] = new VectorVertex(v0, color, uv0, bIdx, cornerRadius: DefaultTextGamma, strokeThickness: DefaultTextContrast);
                textVerticesSpan[currentVertexCount++] = new VectorVertex(v1, color, uv1, bIdx, cornerRadius: DefaultTextGamma, strokeThickness: DefaultTextContrast);
                textVerticesSpan[currentVertexCount++] = new VectorVertex(v2, color, uv2, bIdx, cornerRadius: DefaultTextGamma, strokeThickness: DefaultTextContrast);
                textVerticesSpan[currentVertexCount++] = new VectorVertex(v3, color, uv3, bIdx, cornerRadius: DefaultTextGamma, strokeThickness: DefaultTextContrast);

                // Quads Triangle Indices
                textIndicesSpan[currentIndexCount++] = idxStart;
                textIndicesSpan[currentIndexCount++] = idxStart + 1;
                textIndicesSpan[currentIndexCount++] = idxStart + 2;

                textIndicesSpan[currentIndexCount++] = idxStart;
                textIndicesSpan[currentIndexCount++] = idxStart + 2;
                textIndicesSpan[currentIndexCount++] = idxStart + 3;
            }
        }

        CollectionsMarshal.SetCount(_textVerticesList, currentVertexCount);
        CollectionsMarshal.SetCount(_textIndicesList, currentIndexCount);
    }

    private void CompileTextureCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Texture == null) return;
        var r = cmd.Rect;
        var color = new Vector4(1f, 1f, 1f, 1f);

        var v0 = Vector2.Transform(new Vector2(r.X, r.Y), transform);
        var v1 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y), transform);
        var v2 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), transform);
        var v3 = Vector2.Transform(new Vector2(r.X, r.Y + r.Height), transform);

        uint idxStart = (uint)_textureVerticesList.Count;

        var uv0 = new Vector2(0f, 0f);
        var uv1 = new Vector2(1f, 0f);
        var uv2 = new Vector2(1f, 1f);
        var uv3 = new Vector2(0f, 1f);

        if (_activeClipRect.HasValue)
        {
            float rx1 = v0.X;
            float ry1 = v0.Y;
            float rx2 = v2.X;
            float ry2 = v2.Y;

            float cx1 = Math.Max(rx1, _activeClipRect.Value.X);
            float cy1 = Math.Max(ry1, _activeClipRect.Value.Y);
            float cx2 = Math.Min(rx2, _activeClipRect.Value.X + _activeClipRect.Value.Width);
            float cy2 = Math.Min(ry2, _activeClipRect.Value.Y + _activeClipRect.Value.Height);

            if (cx2 <= cx1 || cy2 <= cy1) return; // Completely clipped!

            float dx = rx2 - rx1;
            float dy = ry2 - ry1;

            uv0 = new Vector2((cx1 - rx1) / dx, (cy1 - ry1) / dy);
            uv1 = new Vector2((cx2 - rx1) / dx, (cy1 - ry1) / dy);
            uv2 = new Vector2((cx2 - rx1) / dx, (cy2 - ry1) / dy);
            uv3 = new Vector2((cx1 - rx1) / dx, (cy2 - ry1) / dy);

            v0 = new Vector2(cx1, cy1);
            v1 = new Vector2(cx2, cy1);
            v2 = new Vector2(cx2, cy2);
            v3 = new Vector2(cx1, cy2);
        }

        int originalVertexCount = _textureVerticesList.Count;
        CollectionsMarshal.SetCount(_textureVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_textureVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(v0, color, uv0);
        vertexSpan[1] = new VectorVertex(v1, color, uv1);
        vertexSpan[2] = new VectorVertex(v2, color, uv2);
        vertexSpan[3] = new VectorVertex(v3, color, uv3);

        int originalIndexCount = _textureIndicesList.Count;
        CollectionsMarshal.SetCount(_textureIndicesList, originalIndexCount + 6);
        var indexSpan = CollectionsMarshal.AsSpan(_textureIndicesList).Slice(originalIndexCount, 6);

        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;
        indexSpan[3] = idxStart;
        indexSpan[4] = idxStart + 2;
        indexSpan[5] = idxStart + 3;

        _drawCalls.Add(new CompositorDrawCall
        {
            Type = DrawCallType.Texture,
            IndexStart = (uint)(_textureIndicesList.Count - 6),
            IndexCount = 6,
            Texture = cmd.Texture
        });
    }

    private void EnsureBufferSize(ref GpuBuffer buffer, uint requiredSize, BufferUsage usage)
    {
        if (buffer.Size >= requiredSize) return;

        uint newSize = Math.Max(buffer.Size * 2, requiredSize);
        buffer.Dispose();
        
        string lbl = usage == BufferUsage.Vertex ? "Vector/Text Resize Vertex Buffer" : "Vector/Text Resize Index Buffer";
        buffer = new GpuBuffer(_context, newSize, usage | BufferUsage.CopyDst, lbl);
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        ReleaseMsaaResources();

        _uniformBuffer.Dispose();
        _vectorVertexBuffer.Dispose();
        _vectorIndexBuffer.Dispose();
        _textVertexBuffer.Dispose();
        _textIndexBuffer.Dispose();
        _textureVertexBuffer.Dispose();
        _textureIndexBuffer.Dispose();
        
        _atlas.Dispose();
        _pathAtlas.Dispose();
        if (_pathAtlasBindGroup != null) _context.Wgpu.BindGroupRelease(_pathAtlasBindGroup);
        if (_pathAtlasBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_pathAtlasBindGroupLayout);
        if (_pathAtlasBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_pathAtlasBindGroupOffscreen);
        if (_pathAtlasBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_pathAtlasBindGroupLayoutOffscreen);
        _pipelineCache.Dispose();
        _compute.Dispose();
        foreach (var tuple in _effectTextures.Values)
        {
            tuple.Source.Dispose();
            tuple.Temp.Dispose();
            tuple.Destination.Dispose();
        }
        _effectTextures.Clear();

        if (_atlasSampler != null) _context.Wgpu.SamplerRelease(_atlasSampler);

        if (_vectorUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_vectorUniformBindGroup);
        if (_vectorUniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_vectorUniformBindGroupOffscreen);
        if (_textUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_textUniformBindGroup);
        if (_textUniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_textUniformBindGroupOffscreen);
        if (_textureUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_textureUniformBindGroup);
        if (_textureUniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_textureUniformBindGroupOffscreen);

        if (_vectorUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_vectorUniformBindGroupLayout);
        if (_vectorUniformBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_vectorUniformBindGroupLayoutOffscreen);
        if (_textUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textUniformBindGroupLayout);
        if (_textUniformBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_textUniformBindGroupLayoutOffscreen);
        if (_textureUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textureUniformBindGroupLayout);
        if (_textureUniformBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_textureUniformBindGroupLayoutOffscreen);

        if (_atlasBindGroup != null) _context.Wgpu.BindGroupRelease(_atlasBindGroup);
        if (_atlasBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_atlasBindGroupOffscreen);
        if (_atlasBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_atlasBindGroupLayout);
        if (_atlasBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_atlasBindGroupLayoutOffscreen);

        if (_texturePipeline != null) _context.Wgpu.RenderPipelineRelease(_texturePipeline);
        if (_textureBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textureBindGroupLayout);
        if (_textureBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_textureBindGroupLayoutOffscreen);

        foreach (var bgVal in _textureBindGroups.Values)
        {
            if (bgVal != 0) _context.Wgpu.BindGroupRelease((BindGroup*)bgVal);
        }
        _textureBindGroups.Clear();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void CreateMsaaResources(uint width, uint height)
    {
        _msaaWidth = width > 0 ? width : 1;
        _msaaHeight = height > 0 ? height : 1;

        var labelPtr = SilkMarshal.StringToPtr("MSAA Color Texture");

        var desc = new TextureDescriptor
        {
            Label = (byte*)labelPtr,
            Usage = TextureUsage.RenderAttachment,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = _msaaWidth, Height = _msaaHeight, DepthOrArrayLayers = 1 },
            Format = RenderFormat,
            MipLevelCount = 1,
            SampleCount = 4,
            ViewFormatCount = 0,
            ViewFormats = null
        };

        _msaaTexture = _context.Wgpu.DeviceCreateTexture(_context.Device, &desc);
        SilkMarshal.Free(labelPtr);

        if (_msaaTexture == null)
        {
            throw new InvalidOperationException($"Failed to allocate MSAA Texture {_msaaWidth}x{_msaaHeight}.");
        }

        var viewDesc = new TextureViewDescriptor
        {
            Format = RenderFormat,
            Dimension = TextureViewDimension.Dimension2D,
            BaseMipLevel = 0,
            MipLevelCount = 1,
            BaseArrayLayer = 0,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All
        };

        _msaaTextureView = _context.Wgpu.TextureCreateView(_msaaTexture, &viewDesc);
        if (_msaaTextureView == null)
        {
            throw new InvalidOperationException($"Failed to create TextureView for MSAA Texture {_msaaWidth}x{_msaaHeight}.");
        }
    }

    private void ReleaseMsaaResources()
    {
        if (_msaaTextureView != null)
        {
            _context.Wgpu.TextureViewRelease(_msaaTextureView);
            _msaaTextureView = null;
        }

        if (_msaaTexture != null)
        {
            _context.Wgpu.TextureDestroy(_msaaTexture);
            _context.Wgpu.TextureRelease(_msaaTexture);
            _msaaTexture = null;
        }
    }

    private Vector2 ClampToClip(Vector2 p)
    {
        if (!_activeClipRect.HasValue) return p;
        var r = _activeClipRect.Value;
        float x = Math.Max(r.X, Math.Min(r.X + r.Width, p.X));
        float y = Math.Max(r.Y, Math.Min(r.Y + r.Height, p.Y));
        return new Vector2(x, y);
    }

    ~Compositor()
    {
        Dispose();
    }

    // Helper methods for real-time drop shadows and Gaussian/backdrop blurs
    private void ApplyAndDrawEffect(Visual fe, Matrix4x4 parentTransform)
    {
        if (fe.Size.X <= 0f || fe.Size.Y <= 0f) return;
        uint w = (uint)fe.Size.X;
        uint h = (uint)fe.Size.Y;

        if (!_effectTextures.TryGetValue(fe, out var textures))
        {
            var source = new GpuTexture(_context, w, h, RenderFormat, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, "Effect Source");
            var temp = new GpuTexture(_context, w, h, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.StorageBinding, "Effect Temp");
            var destination = new GpuTexture(_context, w, h, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.StorageBinding, "Effect Destination");
            
            textures = (source, temp, destination);
            _effectTextures[fe] = textures;
        }
        else
        {
            textures.Source.Resize(w, h);
            textures.Temp.Resize(w, h);
            textures.Destination.Resize(w, h);
        }

        _elementsRenderingEffects.Add(fe);
        try
        {
            // 1. Render the subtree of fe offscreen into textures.Source
            RenderOffscreen(fe, w, h, textures.Source);
        }
        finally
        {
            _elementsRenderingEffects.Remove(fe);
        }

        // 2. Apply compute shader accelerator filter
        if (fe.Effect is BlurEffect blur)
        {
            _compute.ApplyGaussianBlur(textures.Source, textures.Temp, textures.Destination, blur.BlurRadius);
            
            // Draw the blurred result back onto the main screen
            var controlRect = new Rect(fe.Offset, fe.Size);
            DrawTextureOnMain(textures.Destination, controlRect, parentTransform);
        }
        else if (fe.Effect is DropShadowEffect shadow)
        {
            _compute.ApplyDropShadow(textures.Source, textures.Destination, shadow.Offset, shadow.Color, shadow.BlurRadius);
            
            // Draw blurred shadow first (at offset)
            var shadowRect = new Rect(fe.Offset + shadow.Offset, fe.Size);
            DrawTextureOnMain(textures.Destination, shadowRect, parentTransform);
            
            // Draw original source on top
            var controlRect = new Rect(fe.Offset, fe.Size);
            DrawTextureOnMain(textures.Source, controlRect, parentTransform);
        }
    }

    private void DrawTextureOnMain(GpuTexture texture, Rect localRect, Matrix4x4 parentTransform)
    {
        var cmd = new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = texture,
            Rect = localRect
        };
        CompileTextureCommand(cmd, parentTransform);
    }

    public void RenderOffscreen(Visual node, uint width, uint height, GpuTexture targetTexture)
    {
        // 1. Calculate orthographic projection matrix for offscreen
        var projection = new Matrix4x4(
            2.0f / width, 0f, 0f, 0f,
            0f, -2.0f / height, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1.0f, 1.0f, 0f, 1.0f
        );

        // 2. Save and clear lists
        var savedVectorVertices = _vectorVerticesList.ToArray();
        var savedVectorIndices = _vectorIndicesList.ToArray();
        var savedTextVertices = _textVerticesList.ToArray();
        var savedTextIndices = _textIndicesList.ToArray();
        var savedTextureVertices = _textureVerticesList.ToArray();
        var savedTextureIndices = _textureIndicesList.ToArray();
        var savedDrawCalls = _drawCalls.ToArray();
        var savedActiveBrushes = _activeBrushes.ToArray();
        var savedClipStack = _clipStack.ToArray();
        var savedActiveClipRect = _activeClipRect;

        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textIndicesList.Clear();
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _drawCalls.Clear();
        _activeBrushes.Clear();
        _clipStack.Clear();
        _activeClipRect = null;

        // Save offset and temporarily set to Zero to render at origin of offscreen texture
        var oldOffset = node.Offset;
        node.Offset = Vector2.Zero;

        CompileVisualTree(node, Matrix4x4.Identity);

        node.Offset = oldOffset;

        // Compile draw calls for offscreen node
        uint vecStart = 0;
        uint vecCount = (uint)_vectorIndicesList.Count;
        if (vecCount > 0)
        {
            _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Vector, IndexStart = vecStart, IndexCount = vecCount });
        }
        uint textStart = 0;
        uint textCount = (uint)_textIndicesList.Count;
        if (textCount > 0)
        {
            _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Text, IndexStart = textStart, IndexCount = textCount });
        }

        // Upload CPU batches to dynamic GPU buffers
        if (_vectorVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorVertexBuffer, (uint)_vectorVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _vectorVertexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vectorVerticesList));
        }
        if (_vectorIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorIndexBuffer, (uint)_vectorIndicesList.Count * 4, BufferUsage.Index);
            _vectorIndexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vectorIndicesList));
        }

        if (_textVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textVertexBuffer, (uint)_textVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textVertexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textVerticesList));
        }
        if (_textIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textIndexBuffer, (uint)_textIndicesList.Count * 4, BufferUsage.Index);
            _textIndexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textIndicesList));
        }

        if (_textureVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textureVertexBuffer, (uint)_textureVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textureVertexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textureVerticesList));
        }
        if (_textureIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textureIndexBuffer, (uint)_textureIndicesList.Count * 4, BufferUsage.Index);
            _textureIndexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textureIndicesList));
        }

        var uniforms = new GpuUniforms();
        uniforms.Projection = projection;
        GpuBrush* pBrushes = &uniforms.Brush0;
        for (int i = 0; i < Math.Min(64, _activeBrushes.Count); i++)
        {
            pBrushes[i] = _activeBrushes[i];
        }
        _uniformBuffer.WriteSingle(uniforms);

        // Render target view for offscreen GpuTexture
        var targetView = targetTexture.ViewPtr;

        // Render pass for offscreen (1x MSAA)
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Offscreen Compositor Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        // Clear with transparent color
        var colorAttachment = new RenderPassColorAttachment
        {
            View = targetView,
            ResolveTarget = null,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 0, G = 0, B = 0, A = 0 }
        };

        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = null
        };

        var pass = _context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);

        DrawCallType? currentType = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        foreach (var dc in _drawCalls)
        {
            if (dc.Type == DrawCallType.Vector)
            {
                if (currentType != DrawCallType.Vector)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, _vectorPipelineOffscreen);
                    fixed (BindGroup** pGrp = &_vectorUniformBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                    }
                    var buffer = _vectorVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                    currentType = DrawCallType.Vector;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                if (currentType != DrawCallType.Text)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, _textPipelineOffscreen);
                    fixed (BindGroup** pGrp = &_textUniformBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pAtlas = &_atlasBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                    }
                    var buffer = _textVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textIndexBuffer.Size);
                    currentType = DrawCallType.Text;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Texture && dc.Texture != null)
            {
                _context.Wgpu.RenderPassEncoderSetPipeline(pass, _texturePipelineOffscreen);
                fixed (BindGroup** pGrp = &_textureUniformBindGroupOffscreen)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                }
                var buffer = _textureVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                currentType = DrawCallType.Texture;

                var viewPtr = dc.Texture.ViewPtr;
                nint viewKey = (nint)viewPtr;

                if (!_textureBindGroups.TryGetValue(viewKey, out var bgPtrVal))
                {
                    textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
                    textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = viewPtr };

                    var bgDesc = new BindGroupDescriptor { Layout = _textureBindGroupLayoutOffscreen, EntryCount = 2, Entries = textureEntries };
                    var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
                    bgPtrVal = (nint)bg;
                    _textureBindGroups[viewKey] = bgPtrVal;
                }

                var bindGroup = (BindGroup*)bgPtrVal;
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
        }

        _context.Wgpu.RenderPassEncoderEnd(pass);
        _context.Wgpu.RenderPassEncoderRelease(pass);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Offscreen Compositor Command Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        foreach (var bgVal in _textureBindGroups.Values)
        {
            if (bgVal != 0) _context.Wgpu.BindGroupRelease((BindGroup*)bgVal);
        }
        _textureBindGroups.Clear();

        // Restore main lists and state
        _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(savedVectorVertices);
        _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(savedVectorIndices);
        _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
        _textIndicesList.Clear(); _textIndicesList.AddRange(savedTextIndices);
        _textureVerticesList.Clear(); _textureVerticesList.AddRange(savedTextureVertices);
        _textureIndicesList.Clear(); _textureIndicesList.AddRange(savedTextureIndices);
        _drawCalls.Clear(); _drawCalls.AddRange(savedDrawCalls);
        _activeBrushes.Clear(); _activeBrushes.AddRange(savedActiveBrushes);
        _clipStack.Clear();
        foreach (var clip in savedClipStack) _clipStack.Push(clip);
        _activeClipRect = savedActiveClipRect;
    }
}
