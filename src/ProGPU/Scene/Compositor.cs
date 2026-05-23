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

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuBrush
{
    public uint Type;             // 0 = Solid, 1 = Linear, 2 = Radial
    public float Opacity;
    public Vector2 StartPoint;
    public Vector2 EndPoint;
    public Vector2 Center;
    public float Radius;
    public uint StopCount;
    public uint Pad;
    
    public Vector4 Color0;
    public Vector4 Color1;
    public Vector4 Color2;
    public Vector4 Color3;
    public Vector4 Offsets;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuUniforms
{
    public Matrix4x4 Projection;
    public GpuBrush Brush0;
    public GpuBrush Brush1;
    public GpuBrush Brush2;
    public GpuBrush Brush3;
    public GpuBrush Brush4;
    public GpuBrush Brush5;
    public GpuBrush Brush6;
    public GpuBrush Brush7;
    public GpuBrush Brush8;
    public GpuBrush Brush9;
    public GpuBrush Brush10;
    public GpuBrush Brush11;
    public GpuBrush Brush12;
    public GpuBrush Brush13;
    public GpuBrush Brush14;
    public GpuBrush Brush15;
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
    private readonly List<ushort> _vectorIndicesList = new();
    private readonly List<VectorVertex> _textVerticesList = new();
    private readonly List<ushort> _textIndicesList = new();
    private readonly List<VectorVertex> _textureVerticesList = new();
    private readonly List<ushort> _textureIndicesList = new();
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

    public Compositor(WgpuContext context, TextureFormat? renderFormat = null)
    {
        _context = context;
        RenderFormat = renderFormat ?? _context.SwapChainFormat;
        _pipelineCache = new RenderPipelineCache(_context);
        
        // 1. Initialize Glyph Atlas (1024x1024)
        _atlas = new GlyphAtlas(_context, 1024);

        // 2. Uniform Buffer allocation (Projection Matrix & 16 Brushes - 2112 bytes)
        _uniformBuffer = new GpuBuffer(
            _context, 
            2112, 
            BufferUsage.Uniform | BufferUsage.CopyDst, 
            "Compositor Uniform Projection & Brushes Buffer"
        );

        // 3. Dynamic mesh buffer setup (Vertex format: VectorVertex)
        uint initialVertexCount = 100000;
        uint initialIndexCount = 150000;
        uint vertexStride = (uint)Marshal.SizeOf<VectorVertex>();

        _vectorVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Vector Vertex Buffer");
        _vectorIndexBuffer = new GpuBuffer(_context, initialIndexCount * 2, BufferUsage.Index | BufferUsage.CopyDst, "Vector Index Buffer");

        _textVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Text Vertex Buffer");
        _textIndexBuffer = new GpuBuffer(_context, initialIndexCount * 2, BufferUsage.Index | BufferUsage.CopyDst, "Text Index Buffer");

        _textureVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Texture Vertex Buffer");
        _textureIndexBuffer = new GpuBuffer(_context, initialIndexCount * 2, BufferUsage.Index | BufferUsage.CopyDst, "Texture Index Buffer");

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

        // 6. Define Vertex Buffer Layout descriptors (format stride 36 bytes)
        var vertexAttribs = new VertexAttribute[]
        {
            new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }, // Position
            new() { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }, // Color
            new() { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }, // TexCoord
            new() { Format = VertexFormat.Float32, Offset = 32, ShaderLocation = 3 } // BrushIndex
        };

        fixed (VertexAttribute* attribsPtr = vertexAttribs)
        {
            var layoutDesc = new VertexBufferLayout
            {
                ArrayStride = (uint)Marshal.SizeOf<VectorVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 4,
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
            Size = 2112
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
            EnsureBufferSize(ref _vectorIndexBuffer, (uint)_vectorIndicesList.Count * 2, BufferUsage.Index);
            _vectorIndexBuffer.Write(CollectionsMarshal.AsSpan(_vectorIndicesList));
        }

        if (_textVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textVertexBuffer, (uint)_textVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textVertexBuffer.Write(CollectionsMarshal.AsSpan(_textVerticesList));
        }
        if (_textIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textIndexBuffer, (uint)_textIndicesList.Count * 2, BufferUsage.Index);
            _textIndexBuffer.Write(CollectionsMarshal.AsSpan(_textIndicesList));
        }

        if (_textureVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textureVertexBuffer, (uint)_textureVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textureVertexBuffer.Write(CollectionsMarshal.AsSpan(_textureVerticesList));
        }
        if (_textureIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textureIndexBuffer, (uint)_textureIndicesList.Count * 2, BufferUsage.Index);
            _textureIndexBuffer.Write(CollectionsMarshal.AsSpan(_textureIndicesList));
        }

        // Upload unified projection matrix and compiled brushes to GpuUniforms
        var uniforms = new GpuUniforms();
        uniforms.Projection = projection;
        if (_activeBrushes.Count > 0) uniforms.Brush0 = _activeBrushes[0];
        if (_activeBrushes.Count > 1) uniforms.Brush1 = _activeBrushes[1];
        if (_activeBrushes.Count > 2) uniforms.Brush2 = _activeBrushes[2];
        if (_activeBrushes.Count > 3) uniforms.Brush3 = _activeBrushes[3];
        if (_activeBrushes.Count > 4) uniforms.Brush4 = _activeBrushes[4];
        if (_activeBrushes.Count > 5) uniforms.Brush5 = _activeBrushes[5];
        if (_activeBrushes.Count > 6) uniforms.Brush6 = _activeBrushes[6];
        if (_activeBrushes.Count > 7) uniforms.Brush7 = _activeBrushes[7];
        if (_activeBrushes.Count > 8) uniforms.Brush8 = _activeBrushes[8];
        if (_activeBrushes.Count > 9) uniforms.Brush9 = _activeBrushes[9];
        if (_activeBrushes.Count > 10) uniforms.Brush10 = _activeBrushes[10];
        if (_activeBrushes.Count > 11) uniforms.Brush11 = _activeBrushes[11];
        if (_activeBrushes.Count > 12) uniforms.Brush12 = _activeBrushes[12];
        if (_activeBrushes.Count > 13) uniforms.Brush13 = _activeBrushes[13];
        if (_activeBrushes.Count > 14) uniforms.Brush14 = _activeBrushes[14];
        if (_activeBrushes.Count > 15) uniforms.Brush15 = _activeBrushes[15];
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
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint16, 0, _vectorIndexBuffer.Size);
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
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textIndexBuffer.BufferPtr, IndexFormat.Uint16, 0, _textIndexBuffer.Size);
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
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint16, 0, _textureIndexBuffer.Size);
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

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            var v0 = Vector2.Transform(new Vector2(r.X, r.Y), transform);
            var v1 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y), transform);
            var v2 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), transform);
            var v3 = Vector2.Transform(new Vector2(r.X, r.Y + r.Height), transform);

            var local0 = new Vector2(r.X, r.Y);
            var local1 = new Vector2(r.X + r.Width, r.Y);
            var local2 = new Vector2(r.X + r.Width, r.Y + r.Height);
            var local3 = new Vector2(r.X, r.Y + r.Height);

            ushort idxStart = (ushort)_vectorVerticesList.Count;

            _vectorVerticesList.Add(new VectorVertex(v0, solidColor, local0, bIdx));
            _vectorVerticesList.Add(new VectorVertex(v1, solidColor, local1, bIdx));
            _vectorVerticesList.Add(new VectorVertex(v2, solidColor, local2, bIdx));
            _vectorVerticesList.Add(new VectorVertex(v3, solidColor, local3, bIdx));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((ushort)(idxStart + 1));
            _vectorIndicesList.Add((ushort)(idxStart + 2));

            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((ushort)(idxStart + 2));
            _vectorIndicesList.Add((ushort)(idxStart + 3));
        }

        if (cmd.Pen != null)
        {
            int penStartIndex = _vectorVerticesList.Count;
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solidPen) ? solidPen.Color : new Vector4(1f, 1f, 1f, 1f);

            var outline = new List<Vector2>
            {
                new(r.X, r.Y),
                new(r.X + r.Width, r.Y),
                new(r.X + r.Width, r.Y + r.Height),
                new(r.X, r.Y + r.Height)
            };

            var transformedOutline = new List<Vector2>(outline.Count);
            foreach (var p in outline)
            {
                transformedOutline.Add(Vector2.Transform(p, transform));
            }

            StrokeTessellator.TessellateStroke(
                transformedOutline,
                cmd.Pen.Thickness,
                penSolidColor,
                isClosed: true,
                _vectorVerticesList,
                _vectorIndicesList
            );

            if (Matrix4x4.Invert(transform, out var invTransform))
            {
                for (int i = penStartIndex; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.TexCoord = Vector2.Transform(v.Position, invTransform);
                    v.BrushIndex = penBrushIdx;
                    _vectorVerticesList[i] = v;
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

    private void CompilePathCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Path == null) return;
        int startIndex = _vectorVerticesList.Count;
        var flattened = cmd.Path.Flatten(0.2f);

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

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
                    _vectorVerticesList[i] = v;
                }
            }
        }

        if (cmd.Pen != null)
        {
            int penStartIndex = _vectorVerticesList.Count;
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            foreach (var figure in cmd.Path.Figures)
            {
                var contour = figure.Flatten(0.2f);
                var transContour = new List<Vector2>(contour.Count);
                foreach (var pt in contour)
                {
                    transContour.Add(Vector2.Transform(pt, transform));
                }

                StrokeTessellator.TessellateStroke(
                    transContour,
                    cmd.Pen.Thickness,
                    penSolidColor,
                    figure.IsClosed,
                    _vectorVerticesList,
                    _vectorIndicesList
                );
            }

            if (Matrix4x4.Invert(transform, out var invTransform))
            {
                for (int i = penStartIndex; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.TexCoord = Vector2.Transform(v.Position, invTransform);
                    v.BrushIndex = penBrushIdx;
                    _vectorVerticesList[i] = v;
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

        var path = new List<Vector2> { cmd.Position, cmd.Position2 };
        var transPath = new List<Vector2>
        {
            Vector2.Transform(path[0], transform),
            Vector2.Transform(path[1], transform)
        };

        StrokeTessellator.TessellateStroke(
            transPath,
            cmd.Pen.Thickness,
            penSolidColor,
            isClosed: false,
            _vectorVerticesList,
            _vectorIndicesList
        );

        if (Matrix4x4.Invert(transform, out var invTransform))
        {
            for (int i = startIndex; i < _vectorVerticesList.Count; i++)
            {
                var v = _vectorVerticesList[i];
                v.TexCoord = Vector2.Transform(v.Position, invTransform);
                v.BrushIndex = penBrushIdx;
                _vectorVerticesList[i] = v;
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

    private void CompileEllipseCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        int startIndex = _vectorVerticesList.Count;
        var center = cmd.Position2;
        var rx = cmd.RadiusX;
        var ry = cmd.RadiusY;

        var points = new List<Vector2>(64);
        for (int i = 0; i < 64; i++)
        {
            float angle = (float)(i * 2 * Math.PI / 64);
            points.Add(new Vector2(center.X + rx * (float)Math.Cos(angle), center.Y + ry * (float)Math.Sin(angle)));
        }

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            var transPoints = new List<Vector2>(64);
            foreach (var pt in points) transPoints.Add(Vector2.Transform(pt, transform));

            FillTessellator.TessellateFill(transPoints, solidColor, _vectorVerticesList, _vectorIndicesList);

            if (Matrix4x4.Invert(transform, out var invTransform))
            {
                for (int i = startIndex; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.TexCoord = Vector2.Transform(v.Position, invTransform);
                    v.BrushIndex = bIdx;
                    _vectorVerticesList[i] = v;
                }
            }
        }

        if (cmd.Pen != null)
        {
            int penStartIndex = _vectorVerticesList.Count;
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            var transPoints = new List<Vector2>(64);
            foreach (var pt in points) transPoints.Add(Vector2.Transform(pt, transform));

            StrokeTessellator.TessellateStroke(
                transPoints,
                cmd.Pen.Thickness,
                penSolidColor,
                isClosed: true,
                _vectorVerticesList,
                _vectorIndicesList
            );

            if (Matrix4x4.Invert(transform, out var invTransform))
            {
                for (int i = penStartIndex; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.TexCoord = Vector2.Transform(v.Position, invTransform);
                    v.BrushIndex = penBrushIdx;
                    _vectorVerticesList[i] = v;
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

        var points = new List<Vector2>(32);
        
        for (int i = 0; i < 8; i++)
        {
            float angle = (float)(Math.PI + i * Math.PI / 2.0 / 7.0);
            points.Add(new Vector2(r.X + radius + radius * (float)Math.Cos(angle), r.Y + radius + radius * (float)Math.Sin(angle)));
        }
        for (int i = 0; i < 8; i++)
        {
            float angle = (float)(1.5 * Math.PI + i * Math.PI / 2.0 / 7.0);
            points.Add(new Vector2(r.X + r.Width - radius + radius * (float)Math.Cos(angle), r.Y + radius + radius * (float)Math.Sin(angle)));
        }
        for (int i = 0; i < 8; i++)
        {
            float angle = (float)(0.0 + i * Math.PI / 2.0 / 7.0);
            points.Add(new Vector2(r.X + r.Width - radius + radius * (float)Math.Cos(angle), r.Y + r.Height - radius + radius * (float)Math.Sin(angle)));
        }
        for (int i = 0; i < 8; i++)
        {
            float angle = (float)(0.5 * Math.PI + i * Math.PI / 2.0 / 7.0);
            points.Add(new Vector2(r.X + radius + radius * (float)Math.Cos(angle), r.Y + r.Height - radius + radius * (float)Math.Sin(angle)));
        }

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            var transPoints = new List<Vector2>(32);
            foreach (var pt in points) transPoints.Add(Vector2.Transform(pt, transform));

            FillTessellator.TessellateFill(transPoints, solidColor, _vectorVerticesList, _vectorIndicesList);

            if (Matrix4x4.Invert(transform, out var invTransform))
            {
                for (int i = startIndex; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.TexCoord = Vector2.Transform(v.Position, invTransform);
                    v.BrushIndex = bIdx;
                    _vectorVerticesList[i] = v;
                }
            }
        }

        if (cmd.Pen != null)
        {
            int penStartIndex = _vectorVerticesList.Count;
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

            var transPoints = new List<Vector2>(32);
            foreach (var pt in points) transPoints.Add(Vector2.Transform(pt, transform));

            StrokeTessellator.TessellateStroke(
                transPoints,
                cmd.Pen.Thickness,
                penSolidColor,
                isClosed: true,
                _vectorVerticesList,
                _vectorIndicesList
            );

            if (Matrix4x4.Invert(transform, out var invTransform))
            {
                for (int i = penStartIndex; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.TexCoord = Vector2.Transform(v.Position, invTransform);
                    v.BrushIndex = penBrushIdx;
                    _vectorVerticesList[i] = v;
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

        if (_activeBrushes.Count < 16)
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

                ushort idxStart = (ushort)_textVerticesList.Count;

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
                _textIndicesList.Add((ushort)(idxStart + 1));
                _textIndicesList.Add((ushort)(idxStart + 2));

                _textIndicesList.Add(idxStart);
                _textIndicesList.Add((ushort)(idxStart + 2));
                _textIndicesList.Add((ushort)(idxStart + 3));
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

        ushort idxStart = (ushort)_textureVerticesList.Count;

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
        _textureIndicesList.Add((ushort)(idxStart + 1));
        _textureIndicesList.Add((ushort)(idxStart + 2));

        _textureIndicesList.Add(idxStart);
        _textureIndicesList.Add((ushort)(idxStart + 2));
        _textureIndicesList.Add((ushort)(idxStart + 3));

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
