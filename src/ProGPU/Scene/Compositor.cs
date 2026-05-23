using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.WinUI;

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
    public float Time;
    private float _pad1;
    private float _pad2;
    private float _pad3;
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
    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _pipelineCache;
    private readonly GlyphAtlas _atlas;

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

    // Sampler & Texture Bind Group for Typography
    private Sampler* _atlasSampler;
    private BindGroup* _atlasBindGroup;
    private BindGroupLayout* _atlasBindGroupLayout;

    // Render Pipelines
    private RenderPipeline* _vectorPipeline;
    private RenderPipeline* _textPipeline;
    private RenderPipeline* _texturePipeline;
    private BindGroupLayout* _textureBindGroupLayout;

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

    private bool _isDisposed;

    private readonly Stack<Rect> _clipStack = new();
    private Rect? _activeClipRect;

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
    public float GlobalTime { get; set; }

    public Compositor(WgpuContext context, TextureFormat? renderFormat = null)
    {
        _context = context;
        RenderFormat = renderFormat ?? _context.SwapChainFormat;
        _pipelineCache = new RenderPipelineCache(_context);
        
        // 1. Initialize Glyph Atlas (1024x1024)
        _atlas = new GlyphAtlas(_context, 1024);

        // 2. Uniform Buffer allocation (Projection Matrix & 64 Brushes - 8272 bytes)
        _uniformBuffer = new GpuBuffer(
            _context, 
            8272, 
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

        // 6. Define Vertex Buffer Layout descriptors (format stride 56 bytes)
        var vertexAttribs = new VertexAttribute[]
        {
            new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }, // Position
            new() { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }, // Color
            new() { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }, // TexCoord
            new() { Format = VertexFormat.Float32, Offset = 32, ShaderLocation = 3 }, // BrushIndex
            new() { Format = VertexFormat.Float32x2, Offset = 36, ShaderLocation = 4 }, // ShapeSize
            new() { Format = VertexFormat.Float32, Offset = 44, ShaderLocation = 5 }, // CornerRadius
            new() { Format = VertexFormat.Float32, Offset = 48, ShaderLocation = 6 }, // StrokeThickness
            new() { Format = VertexFormat.Float32, Offset = 52, ShaderLocation = 7 }, // ShapeType
            new() { Format = VertexFormat.Float32x4, Offset = 56, ShaderLocation = 8 }, // AnimAmp
            new() { Format = VertexFormat.Float32x4, Offset = 72, ShaderLocation = 9 } // AnimFreqPhase
        };

        fixed (VertexAttribute* attribsPtr = vertexAttribs)
        {
            var layoutDesc = new VertexBufferLayout
            {
                ArrayStride = (uint)Marshal.SizeOf<VectorVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 10,
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
        }

        _textureBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipeline, 1);

        // 7. Uniform bind groups structure configuration
        _vectorUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipeline, 0);
        _textUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipeline, 0);
        _textureUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipeline, 0);
        
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

        var uDescText = new BindGroupDescriptor
        {
            Layout = _textUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescText);

        var uDescTexture = new BindGroupDescriptor
        {
            Layout = _textureUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textureUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescTexture);

        // 8. Atlas bind group structure configuration
        _atlasBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipeline, 1);

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
    }

    public void RenderScene(Visual root, uint width, uint height, TextureView* targetView)
    {
        if (_isDisposed) return;

        // Automatically measure and arrange active popups with window dimensions before rendering
        ProGPU.WinUI.PopupService.MeasureAndArrangePopups(new Vector2(width, height));

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

        // 4. Compile Layer 1: Active Popups (in proper Z-order)
        for (int i = 0; i < ProGPU.WinUI.PopupService.ActivePopups.Count; i++)
        {
            var popup = ProGPU.WinUI.PopupService.ActivePopups[i];
            uint vecStartPopup = (uint)_vectorIndicesList.Count;
            uint textStartPopup = (uint)_textIndicesList.Count;
            
            CompileVisualTree(popup, Matrix4x4.Identity);
            
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

        // 5. Compile Layer 2: Tooltips
        var activeToolTip = ProGPU.WinUI.InputSystem.ActiveToolTip;
        if (activeToolTip != null)
        {
            activeToolTip.Measure(new Vector2(width, height));
            
            var mousePos = ProGPU.WinUI.InputSystem.LastMousePosition;
            float tooltipX = mousePos.X + 12f;
            float tooltipY = mousePos.Y + 20f;
            
            if (tooltipX + activeToolTip.DesiredSize.X > width)
            {
                tooltipX = width - activeToolTip.DesiredSize.X - 8f;
            }
            if (tooltipX < 0f) tooltipX = 8f;
            
            if (tooltipY + activeToolTip.DesiredSize.Y > height)
            {
                tooltipY = mousePos.Y - activeToolTip.DesiredSize.Y - 8f;
            }
            if (tooltipY < 0f) tooltipY = 8f;
            
            activeToolTip.Offset = new Vector2(tooltipX, tooltipY);
            activeToolTip.Arrange(new Rect(activeToolTip.Offset, activeToolTip.DesiredSize));
            
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
        if (ProGPU.WinUI.DevToolsService.IsDevToolsActive)
        {
            uint vecStartAdorner = (uint)_vectorIndicesList.Count;
            uint textStartAdorner = (uint)_textIndicesList.Count;

            var diagContext = new DrawingContext();
            ProGPU.WinUI.AdornerLayer.Render(diagContext, width, height);
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
        uniforms.Time = GlobalTime;
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

        var bgColor = ThemeManager.GetColor("PageBackground");
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
                    CompileTextCommand(cmd, node as TextVisual, globalTransform);
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

        var v0_pos = Vector2.Transform(new Vector2(r.X, r.Y), transform);
        var v1_pos = Vector2.Transform(new Vector2(r.X + r.Width, r.Y), transform);
        var v2_pos = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), transform);
        var v3_pos = Vector2.Transform(new Vector2(r.X, r.Y + r.Height), transform);

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            _vectorVerticesList.Add(new VectorVertex(v0_pos, solidColor, new Vector2(-wHalf, -hHalf), bIdx, shapeSize, 0f, 0f, 0f));
            _vectorVerticesList.Add(new VectorVertex(v1_pos, solidColor, new Vector2(wHalf, -hHalf), bIdx, shapeSize, 0f, 0f, 0f));
            _vectorVerticesList.Add(new VectorVertex(v2_pos, solidColor, new Vector2(wHalf, hHalf), bIdx, shapeSize, 0f, 0f, 0f));
            _vectorVerticesList.Add(new VectorVertex(v3_pos, solidColor, new Vector2(-wHalf, hHalf), bIdx, shapeSize, 0f, 0f, 0f));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 1));
            _vectorIndicesList.Add((uint)(idxStart + 2));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 2));
            _vectorIndicesList.Add((uint)(idxStart + 3));
        }

        if (cmd.Pen != null)
        {
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solidPen) ? solidPen.Color : new Vector4(1f, 1f, 1f, 1f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            _vectorVerticesList.Add(new VectorVertex(v0_pos, penSolidColor, new Vector2(-wHalf, -hHalf), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f));
            _vectorVerticesList.Add(new VectorVertex(v1_pos, penSolidColor, new Vector2(wHalf, -hHalf), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f));
            _vectorVerticesList.Add(new VectorVertex(v2_pos, penSolidColor, new Vector2(wHalf, hHalf), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f));
            _vectorVerticesList.Add(new VectorVertex(v3_pos, penSolidColor, new Vector2(-wHalf, hHalf), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 1));
            _vectorIndicesList.Add((uint)(idxStart + 2));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 2));
            _vectorIndicesList.Add((uint)(idxStart + 3));
        }

        if (_activeClipRect.HasValue)
        {
            for (int i = startIndex; i < _vectorVerticesList.Count; i++)
            {
                var v = _vectorVerticesList[i];
                v.Position = ClampToClip(v.Position);
                _vectorVerticesList[i] = v;
            }
        }
    }

    private List<List<Vector2>> EvaluatePathFills(PathGeometry path)
    {
        var figures = new List<List<Vector2>>();
        foreach (var figure in path.Figures)
        {
            var points = new List<Vector2>();
            var currentPoint = figure.StartPoint;
            points.Add(currentPoint);

            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line)
                {
                    points.Add(line.Point);
                    currentPoint = line.Point;
                }
                else if (segment is QuadraticBezierSegment quad)
                {
                    int N = 16;
                    for (int i = 1; i <= N; i++)
                    {
                        float t = i / (float)N;
                        float oneMinusT = 1.0f - t;
                        var pt = oneMinusT * oneMinusT * currentPoint + 2.0f * oneMinusT * t * quad.ControlPoint + t * t * quad.Point;
                        points.Add(pt);
                    }
                    currentPoint = quad.Point;
                }
                else if (segment is CubicBezierSegment cubic)
                {
                    int N = 16;
                    for (int i = 1; i <= N; i++)
                    {
                        float t = i / (float)N;
                        float oneMinusT = 1.0f - t;
                        var pt = oneMinusT * oneMinusT * oneMinusT * currentPoint 
                               + 3.0f * oneMinusT * oneMinusT * t * cubic.ControlPoint1 
                               + 3.0f * oneMinusT * t * t * cubic.ControlPoint2 
                               + t * t * t * cubic.Point;
                        points.Add(pt);
                    }
                    currentPoint = cubic.Point;
                }
            }

            if (figure.IsClosed && points.Count > 1 && points[0] != points[^1])
            {
                points.Add(points[0]);
            }
            figures.Add(points);
        }
        return figures;
    }

    private void CompilePathCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Path == null) return;
        int startIndex = _vectorVerticesList.Count;

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            var flattened = EvaluatePathFills(cmd.Path);
            foreach (var contour in flattened)
            {
                var transContour = new List<Vector2>(contour.Count);
                foreach (var pt in contour)
                {
                    transContour.Add(Vector2.Transform(pt, transform));
                }

                FillTessellator.TessellateFill(
                    transContour,
                    solidColor,
                    _vectorVerticesList,
                    _vectorIndicesList
                );
            }

            if (Matrix4x4.Invert(transform, out var invTransform))
            {
                for (int i = startIndex; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.TexCoord = Vector2.Transform(v.Position, invTransform);
                    v.BrushIndex = bIdx;
                    v.ShapeType = 4f;
                    _vectorVerticesList[i] = v;
                }
            }
        }

        if (cmd.Pen != null)
        {
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
            float thickness = cmd.Pen.Thickness;

            foreach (var figure in cmd.Path.Figures)
            {
                var currentPoint = figure.StartPoint;

                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        var p0_trans = Vector2.Transform(currentPoint, transform);
                        var p1_trans = Vector2.Transform(line.Point, transform);

                        uint idxStart = (uint)_vectorVerticesList.Count;

                        _vectorVerticesList.Add(new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f));
                        _vectorVerticesList.Add(new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f));
                        _vectorVerticesList.Add(new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f));
                        _vectorVerticesList.Add(new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f));

                        _vectorIndicesList.Add(idxStart);
                        _vectorIndicesList.Add((uint)(idxStart + 1));
                        _vectorIndicesList.Add((uint)(idxStart + 2));

                        _vectorIndicesList.Add((uint)(idxStart + 1));
                        _vectorIndicesList.Add((uint)(idxStart + 3));
                        _vectorIndicesList.Add((uint)(idxStart + 2));

                        currentPoint = line.Point;
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        var p0_trans = Vector2.Transform(currentPoint, transform);
                        var p1_trans = Vector2.Transform(quad.ControlPoint, transform);
                        var p2_trans = Vector2.Transform(quad.Point, transform);

                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        uint idxStart = (uint)_vectorVerticesList.Count;

                        for (int i = 0; i <= N; i++)
                        {
                            float t = i / (float)N;
                            var pColorLeft = new Vector4(1f, 1f, t, 1f);
                            var pColorRight = new Vector4(1f, 1f, t, -1f);
                            _vectorVerticesList.Add(new VectorVertex(p0_trans, pColorLeft, p1_trans, penBrushIdx, p2_trans, 0f, thickness, 5f));
                            _vectorVerticesList.Add(new VectorVertex(p0_trans, pColorRight, p1_trans, penBrushIdx, p2_trans, 0f, thickness, 5f));
                        }

                        for (int i = 0; i < N; i++)
                        {
                            uint currentLeft = (uint)(idxStart + 2 * i);
                            uint currentRight = (uint)(idxStart + 2 * i + 1);
                            uint nextLeft = (uint)(idxStart + 2 * i + 2);
                            uint nextRight = (uint)(idxStart + 2 * i + 3);

                            _vectorIndicesList.Add(currentLeft);
                            _vectorIndicesList.Add(currentRight);
                            _vectorIndicesList.Add(nextLeft);

                            _vectorIndicesList.Add(currentRight);
                            _vectorIndicesList.Add(nextRight);
                            _vectorIndicesList.Add(nextLeft);
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
                        uint idxStart = (uint)_vectorVerticesList.Count;

                        for (int i = 0; i <= N; i++)
                        {
                            float t = i / (float)N;
                            var pColorLeft = new Vector4(p3_trans.X, p3_trans.Y, t, 1f);
                            var pColorRight = new Vector4(p3_trans.X, p3_trans.Y, t, -1f);
                            _vectorVerticesList.Add(new VectorVertex(p0_trans, pColorLeft, p1_trans, penBrushIdx, p2_trans, 0f, thickness, 6f));
                            _vectorVerticesList.Add(new VectorVertex(p0_trans, pColorRight, p1_trans, penBrushIdx, p2_trans, 0f, thickness, 6f));
                        }

                        for (int i = 0; i < N; i++)
                        {
                            uint currentLeft = (uint)(idxStart + 2 * i);
                            uint currentRight = (uint)(idxStart + 2 * i + 1);
                            uint nextLeft = (uint)(idxStart + 2 * i + 2);
                            uint nextRight = (uint)(idxStart + 2 * i + 3);

                            _vectorIndicesList.Add(currentLeft);
                            _vectorIndicesList.Add(currentRight);
                            _vectorIndicesList.Add(nextLeft);

                            _vectorIndicesList.Add(currentRight);
                            _vectorIndicesList.Add(nextRight);
                            _vectorIndicesList.Add(nextLeft);
                        }

                        currentPoint = cubic.Point;
                    }
                }

                if (figure.IsClosed && currentPoint != figure.StartPoint)
                {
                    var p0_trans = Vector2.Transform(currentPoint, transform);
                    var p1_trans = Vector2.Transform(figure.StartPoint, transform);

                    uint idxStart = (uint)_vectorVerticesList.Count;

                    _vectorVerticesList.Add(new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f));
                    _vectorVerticesList.Add(new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f));
                    _vectorVerticesList.Add(new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f));
                    _vectorVerticesList.Add(new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f));

                    _vectorIndicesList.Add(idxStart);
                    _vectorIndicesList.Add((uint)(idxStart + 1));
                    _vectorIndicesList.Add((uint)(idxStart + 2));

                    _vectorIndicesList.Add((uint)(idxStart + 1));
                    _vectorIndicesList.Add((uint)(idxStart + 3));
                    _vectorIndicesList.Add((uint)(idxStart + 2));
                }
            }
        }

        if (_activeClipRect.HasValue)
        {
            for (int i = startIndex; i < _vectorVerticesList.Count; i++)
            {
                var v = _vectorVerticesList[i];
                v.Position = ClampToClip(v.Position);
                _vectorVerticesList[i] = v;
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

        uint idxStart = (uint)_vectorVerticesList.Count;

        // Start point P0 (Left + Right offsets)
        _vectorVerticesList.Add(new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, 3f, cmd.AnimAmp, cmd.AnimFreqPhase));
        _vectorVerticesList.Add(new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, 3f, cmd.AnimAmp, cmd.AnimFreqPhase));

        // End point P1 (Left + Right offsets)
        _vectorVerticesList.Add(new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, 3f, cmd.AnimAmp, cmd.AnimFreqPhase));
        _vectorVerticesList.Add(new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, 3f, cmd.AnimAmp, cmd.AnimFreqPhase));

        _vectorIndicesList.Add(idxStart);
        _vectorIndicesList.Add((uint)(idxStart + 1));
        _vectorIndicesList.Add((uint)(idxStart + 2));

        _vectorIndicesList.Add((uint)(idxStart + 1));
        _vectorIndicesList.Add((uint)(idxStart + 3));
        _vectorIndicesList.Add((uint)(idxStart + 2));

        if (_activeClipRect.HasValue)
        {
            for (int i = startIndex; i < _vectorVerticesList.Count; i++)
            {
                var v = _vectorVerticesList[i];
                v.Position = ClampToClip(v.Position);
                _vectorVerticesList[i] = v;
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
        uint idxStart = (uint)_vectorVerticesList.Count;

        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N;
            // Emit left (+1) and right (-1) offset vertices
            var pColorLeft = new Vector4(1f, 1f, t, 1f);
            var pColorRight = new Vector4(1f, 1f, t, -1f);
            _vectorVerticesList.Add(new VectorVertex(p0_trans, pColorLeft, p1_trans, penBrushIdx, p2_trans, 0f, thickness, 5f, cmd.AnimAmp, cmd.AnimFreqPhase));
            _vectorVerticesList.Add(new VectorVertex(p0_trans, pColorRight, p1_trans, penBrushIdx, p2_trans, 0f, thickness, 5f, cmd.AnimAmp, cmd.AnimFreqPhase));
        }

        for (int i = 0; i < N; i++)
        {
            uint currentLeft = (uint)(idxStart + 2 * i);
            uint currentRight = (uint)(idxStart + 2 * i + 1);
            uint nextLeft = (uint)(idxStart + 2 * i + 2);
            uint nextRight = (uint)(idxStart + 2 * i + 3);

            _vectorIndicesList.Add(currentLeft);
            _vectorIndicesList.Add(currentRight);
            _vectorIndicesList.Add(nextLeft);

            _vectorIndicesList.Add(currentRight);
            _vectorIndicesList.Add(nextRight);
            _vectorIndicesList.Add(nextLeft);
        }

        if (_activeClipRect.HasValue)
        {
            for (int i = startIndex; i < _vectorVerticesList.Count; i++)
            {
                var v = _vectorVerticesList[i];
                v.Position = ClampToClip(v.Position);
                _vectorVerticesList[i] = v;
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
        uint idxStart = (uint)_vectorVerticesList.Count;

        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N;
            // Emit left (+1) and right (-1) offset vertices
            var pColorLeft = new Vector4(p3_trans.X, p3_trans.Y, t, 1f);
            var pColorRight = new Vector4(p3_trans.X, p3_trans.Y, t, -1f);
            _vectorVerticesList.Add(new VectorVertex(p0_trans, pColorLeft, p1_trans, penBrushIdx, p2_trans, 0f, thickness, 6f, cmd.AnimAmp, cmd.AnimFreqPhase));
            _vectorVerticesList.Add(new VectorVertex(p0_trans, pColorRight, p1_trans, penBrushIdx, p2_trans, 0f, thickness, 6f, cmd.AnimAmp, cmd.AnimFreqPhase));
        }

        for (int i = 0; i < N; i++)
        {
            uint currentLeft = (uint)(idxStart + 2 * i);
            uint currentRight = (uint)(idxStart + 2 * i + 1);
            uint nextLeft = (uint)(idxStart + 2 * i + 2);
            uint nextRight = (uint)(idxStart + 2 * i + 3);

            _vectorIndicesList.Add(currentLeft);
            _vectorIndicesList.Add(currentRight);
            _vectorIndicesList.Add(nextLeft);

            _vectorIndicesList.Add(currentRight);
            _vectorIndicesList.Add(nextRight);
            _vectorIndicesList.Add(nextLeft);
        }

        if (_activeClipRect.HasValue)
        {
            for (int i = startIndex; i < _vectorVerticesList.Count; i++)
            {
                var v = _vectorVerticesList[i];
                v.Position = ClampToClip(v.Position);
                _vectorVerticesList[i] = v;
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

        var v0_pos = Vector2.Transform(new Vector2(center.X - rx, center.Y - ry), transform);
        var v1_pos = Vector2.Transform(new Vector2(center.X + rx, center.Y - ry), transform);
        var v2_pos = Vector2.Transform(new Vector2(center.X + rx, center.Y + ry), transform);
        var v3_pos = Vector2.Transform(new Vector2(center.X - rx, center.Y + ry), transform);

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            _vectorVerticesList.Add(new VectorVertex(v0_pos, solidColor, new Vector2(-rx, -ry), bIdx, shapeSize, 0f, 0f, 1f));
            _vectorVerticesList.Add(new VectorVertex(v1_pos, solidColor, new Vector2(rx, -ry), bIdx, shapeSize, 0f, 0f, 1f));
            _vectorVerticesList.Add(new VectorVertex(v2_pos, solidColor, new Vector2(rx, ry), bIdx, shapeSize, 0f, 0f, 1f));
            _vectorVerticesList.Add(new VectorVertex(v3_pos, solidColor, new Vector2(-rx, ry), bIdx, shapeSize, 0f, 0f, 1f));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 1));
            _vectorIndicesList.Add((uint)(idxStart + 2));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 2));
            _vectorIndicesList.Add((uint)(idxStart + 3));
        }

        if (cmd.Pen != null)
        {
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            _vectorVerticesList.Add(new VectorVertex(v0_pos, penSolidColor, new Vector2(-rx, -ry), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f));
            _vectorVerticesList.Add(new VectorVertex(v1_pos, penSolidColor, new Vector2(rx, -ry), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f));
            _vectorVerticesList.Add(new VectorVertex(v2_pos, penSolidColor, new Vector2(rx, ry), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f));
            _vectorVerticesList.Add(new VectorVertex(v3_pos, penSolidColor, new Vector2(-rx, ry), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 1));
            _vectorIndicesList.Add((uint)(idxStart + 2));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 2));
            _vectorIndicesList.Add((uint)(idxStart + 3));
        }

        if (_activeClipRect.HasValue)
        {
            for (int i = startIndex; i < _vectorVerticesList.Count; i++)
            {
                var v = _vectorVerticesList[i];
                v.Position = ClampToClip(v.Position);
                _vectorVerticesList[i] = v;
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

        var v0_pos = Vector2.Transform(new Vector2(r.X, r.Y), transform);
        var v1_pos = Vector2.Transform(new Vector2(r.X + r.Width, r.Y), transform);
        var v2_pos = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), transform);
        var v3_pos = Vector2.Transform(new Vector2(r.X, r.Y + r.Height), transform);

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            _vectorVerticesList.Add(new VectorVertex(v0_pos, solidColor, new Vector2(-wHalf, -hHalf), bIdx, shapeSize, radius, 0f, 2f));
            _vectorVerticesList.Add(new VectorVertex(v1_pos, solidColor, new Vector2(wHalf, -hHalf), bIdx, shapeSize, radius, 0f, 2f));
            _vectorVerticesList.Add(new VectorVertex(v2_pos, solidColor, new Vector2(wHalf, hHalf), bIdx, shapeSize, radius, 0f, 2f));
            _vectorVerticesList.Add(new VectorVertex(v3_pos, solidColor, new Vector2(-wHalf, hHalf), bIdx, shapeSize, radius, 0f, 2f));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 1));
            _vectorIndicesList.Add((uint)(idxStart + 2));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 2));
            _vectorIndicesList.Add((uint)(idxStart + 3));
        }

        if (cmd.Pen != null)
        {
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            _vectorVerticesList.Add(new VectorVertex(v0_pos, penSolidColor, new Vector2(-wHalf, -hHalf), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f));
            _vectorVerticesList.Add(new VectorVertex(v1_pos, penSolidColor, new Vector2(wHalf, -hHalf), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f));
            _vectorVerticesList.Add(new VectorVertex(v2_pos, penSolidColor, new Vector2(wHalf, hHalf), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f));
            _vectorVerticesList.Add(new VectorVertex(v3_pos, penSolidColor, new Vector2(-wHalf, hHalf), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 1));
            _vectorIndicesList.Add((uint)(idxStart + 2));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((uint)(idxStart + 2));
            _vectorIndicesList.Add((uint)(idxStart + 3));
        }

        if (_activeClipRect.HasValue)
        {
            for (int i = startIndex; i < _vectorVerticesList.Count; i++)
            {
                var v = _vectorVerticesList[i];
                v.Position = ClampToClip(v.Position);
                _vectorVerticesList[i] = v;
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

    private void CompileTextCommand(RenderCommand cmd, TextVisual? textNode, Matrix4x4 transform)
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
            layout = new TextLayout(cmd.Text, font, cmd.FontSize, 10000f, TextAlignment.Left, _atlas);
        }

        if (layout == null) return;

        float bIdx = RegisterBrush(cmd.Brush);
        var brush = cmd.Brush as SolidColorBrush;
        var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

        foreach (var runGlyph in layout.Glyphs)
        {
            var info = runGlyph.Glyph;
            if (info.Width == 0 || info.Height == 0) continue;

            int passCount = cmd.IsBold ? 2 : 1;
            float boldOffset = cmd.FontSize * 0.035f;

            for (int pass = 0; pass < passCount; pass++)
            {
                float xOffset = pass * boldOffset;
                float x0 = runGlyph.Position.X + cmd.Position.X + xOffset;
                float y0 = runGlyph.Position.Y + cmd.Position.Y;
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

                uint idxStart = (uint)_textVerticesList.Count;

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

                _textVerticesList.Add(new VectorVertex(v0, color, uv0, bIdx));
                _textVerticesList.Add(new VectorVertex(v1, color, uv1, bIdx));
                _textVerticesList.Add(new VectorVertex(v2, color, uv2, bIdx));
                _textVerticesList.Add(new VectorVertex(v3, color, uv3, bIdx));

                // Quads Triangle Indices
                _textIndicesList.Add(idxStart);
                _textIndicesList.Add((uint)(idxStart + 1));
                _textIndicesList.Add((uint)(idxStart + 2));

                _textIndicesList.Add(idxStart);
                _textIndicesList.Add((uint)(idxStart + 2));
                _textIndicesList.Add((uint)(idxStart + 3));
            }
        }
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

        _textureVerticesList.Add(new VectorVertex(v0, color, uv0));
        _textureVerticesList.Add(new VectorVertex(v1, color, uv1));
        _textureVerticesList.Add(new VectorVertex(v2, color, uv2));
        _textureVerticesList.Add(new VectorVertex(v3, color, uv3));

        _textureIndicesList.Add(idxStart);
        _textureIndicesList.Add((uint)(idxStart + 1));
        _textureIndicesList.Add((uint)(idxStart + 2));

        _textureIndicesList.Add(idxStart);
        _textureIndicesList.Add((uint)(idxStart + 2));
        _textureIndicesList.Add((uint)(idxStart + 3));

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
        _pipelineCache.Dispose();

        if (_atlasSampler != null) _context.Wgpu.SamplerRelease(_atlasSampler);

        if (_vectorUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_vectorUniformBindGroup);
        if (_textUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_textUniformBindGroup);
        if (_textureUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_textureUniformBindGroup);

        if (_vectorUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_vectorUniformBindGroupLayout);
        if (_textUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textUniformBindGroupLayout);
        if (_textureUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textureUniformBindGroupLayout);

        if (_atlasBindGroup != null) _context.Wgpu.BindGroupRelease(_atlasBindGroup);
        if (_atlasBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_atlasBindGroupLayout);

        if (_texturePipeline != null) _context.Wgpu.RenderPipelineRelease(_texturePipeline);
        if (_textureBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textureBindGroupLayout);

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
}
