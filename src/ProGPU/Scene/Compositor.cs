using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene;

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

    // CPU-side collection lists for Batching
    private readonly List<VectorVertex> _vectorVerticesList = new();
    private readonly List<ushort> _vectorIndicesList = new();
    private readonly List<VectorVertex> _textVerticesList = new();
    private readonly List<ushort> _textIndicesList = new();
    private readonly List<VectorVertex> _textureVerticesList = new();
    private readonly List<ushort> _textureIndicesList = new();
    private readonly List<(GpuTexture texture, uint indexStart, uint indexCount)> _textureDrawCalls = new();
    private readonly Dictionary<nint, nint> _textureBindGroups = new();

    private bool _isDisposed;

    public GlyphAtlas Atlas => _atlas;
    public TextureFormat RenderFormat { get; private set; }

    public Compositor(WgpuContext context, TextureFormat? renderFormat = null)
    {
        _context = context;
        RenderFormat = renderFormat ?? _context.SwapChainFormat;
        _pipelineCache = new RenderPipelineCache(_context);
        
        // 1. Initialize Glyph Atlas (1024x1024)
        _atlas = new GlyphAtlas(_context, 1024);

        // 2. Uniform Buffer allocation (projection matrix - 64 bytes)
        _uniformBuffer = new GpuBuffer(
            _context, 
            64, 
            BufferUsage.Uniform | BufferUsage.CopyDst, 
            "Compositor Uniform Projection Buffer"
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

        // 6. Define Vertex Buffer Layout descriptors
        var vertexAttribs = new VertexAttribute[]
        {
            new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }, // Position
            new() { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }, // Color
            new() { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 } // TexCoord
        };

        fixed (VertexAttribute* attribsPtr = vertexAttribs)
        {
            var layoutDesc = new VertexBufferLayout
            {
                ArrayStride = (uint)Marshal.SizeOf<VectorVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 3,
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
            Size = 64
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

        // 1. Calculate orthographic projection matrix for modern 2D rendering
        // Maps X in [0, width] to [-1, 1], and Y in [0, height] to [1, -1]
        var projection = new Matrix4x4(
            2.0f / width, 0f, 0f, 0f,
            0f, -2.0f / height, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1.0f, 1.0f, 0f, 1.0f
        );

        // Write projection matrix to uniform buffer
        _uniformBuffer.WriteSingle(projection);

        // 2. Clear CPU collection batch lists
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textIndicesList.Clear();
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _textureDrawCalls.Clear();

        // 3. Compile entire visual tree scene graph into drawing batches
        CompileVisualTree(root, Matrix4x4.Identity);

        // 4. Upload CPU batches to dynamic GPU buffers
        if (_vectorVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorVertexBuffer, (uint)_vectorVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            EnsureBufferSize(ref _vectorIndexBuffer, (uint)_vectorIndicesList.Count * 2, BufferUsage.Index);
            
            _vectorVertexBuffer.Write(CollectionsMarshal.AsSpan(_vectorVerticesList));
            _vectorIndexBuffer.Write(CollectionsMarshal.AsSpan(_vectorIndicesList));
        }

        if (_textVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textVertexBuffer, (uint)_textVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            EnsureBufferSize(ref _textIndexBuffer, (uint)_textIndicesList.Count * 2, BufferUsage.Index);

            _textVertexBuffer.Write(CollectionsMarshal.AsSpan(_textVerticesList));
            _textIndexBuffer.Write(CollectionsMarshal.AsSpan(_textIndicesList));
        }

        if (_textureVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textureVertexBuffer, (uint)_textureVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            EnsureBufferSize(ref _textureIndexBuffer, (uint)_textureIndicesList.Count * 2, BufferUsage.Index);

            _textureVertexBuffer.Write(CollectionsMarshal.AsSpan(_textureVerticesList));
            _textureIndexBuffer.Write(CollectionsMarshal.AsSpan(_textureIndicesList));
        }

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

        var colorAttachment = new RenderPassColorAttachment
        {
            View = _msaaTextureView,
            ResolveTarget = targetView,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 0.08, G = 0.08, B = 0.1, A = 1.0 } // Elegant dark blue UI background
        };

        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = null
        };

        var pass = _context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);

        // Batch 1: Render all Vector geometry (solid shapes, gradients, curves)
        if (_vectorIndicesList.Count > 0)
        {
            _context.Wgpu.RenderPassEncoderSetPipeline(pass, _vectorPipeline);
            
            // Bind uniforms
            fixed (BindGroup** pGrp = &_vectorUniformBindGroup)
            {
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
            }

            // Bind vertices & indices
            var buffer = _vectorVertexBuffer.BufferPtr;
            ulong offset = 0;
            _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, offset, _vectorVertexBuffer.Size);
            _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint16, 0, _vectorIndexBuffer.Size);

            // Execute draw call (O(1) drawing driver submission)
            _context.Wgpu.RenderPassEncoderDrawIndexed(pass, (uint)_vectorIndicesList.Count, 1, 0, 0, 0);
        }

        // Batch 1.5: Render all Texture geometry (offscreen RGBA8 layer quads)
        if (_textureDrawCalls.Count > 0)
        {
            _context.Wgpu.RenderPassEncoderSetPipeline(pass, _texturePipeline);

            // Bind Uniforms
            fixed (BindGroup** pGrp = &_textureUniformBindGroup)
            {
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
            }

            // Bind vertex & index buffers
            var buffer = _textureVertexBuffer.BufferPtr;
            ulong offset = 0;
            _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, offset, _textureVertexBuffer.Size);
            _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint16, 0, _textureIndexBuffer.Size);

            // Execute draw calls
            foreach (var drawCall in _textureDrawCalls)
            {
                var viewPtr = drawCall.texture.ViewPtr;
                nint viewKey = (nint)viewPtr;

                if (!_textureBindGroups.TryGetValue(viewKey, out var bgPtrVal))
                {
                    // Create dynamic BindGroup
                    var samplerEntry = new BindGroupEntry
                    {
                        Binding = 0,
                        Sampler = _atlasSampler
                    };

                    var viewEntry = new BindGroupEntry
                    {
                        Binding = 1,
                        TextureView = viewPtr
                    };

                    var entries = new BindGroupEntry[2];
                    entries[0] = samplerEntry;
                    entries[1] = viewEntry;

                    fixed (BindGroupEntry* pEntries = entries)
                    {
                        var bgDesc = new BindGroupDescriptor
                        {
                            Layout = _textureBindGroupLayout,
                            EntryCount = 2,
                            Entries = pEntries
                        };
                        var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
                        bgPtrVal = (nint)bg;
                        _textureBindGroups[viewKey] = bgPtrVal;
                    }
                }

                var bindGroup = (BindGroup*)bgPtrVal;
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);

                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, drawCall.indexCount, 1, drawCall.indexStart, 0, 0);
            }
        }

        // Batch 2: Render all Typography Text geometry (dynamic glyph atlas textured quads)
        if (_textIndicesList.Count > 0)
        {
            _context.Wgpu.RenderPassEncoderSetPipeline(pass, _textPipeline);

            // Bind Uniforms
            fixed (BindGroup** pGrp = &_textUniformBindGroup)
            {
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
            }

            // Bind Glyph Atlas texture
            fixed (BindGroup** pAtlas = &_atlasBindGroup)
            {
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
            }

            // Bind text buffers
            var buffer = _textVertexBuffer.BufferPtr;
            ulong offset = 0;
            _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, offset, _textVertexBuffer.Size);
            _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textIndexBuffer.BufferPtr, IndexFormat.Uint16, 0, _textIndexBuffer.Size);

            // Execute typography draw call
            _context.Wgpu.RenderPassEncoderDrawIndexed(pass, (uint)_textIndicesList.Count, 1, 0, 0, 0);
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
    }

    private void CompileVisualTree(Visual node, Matrix4x4 parentTransform)
    {
        // 1. Calculate global transform
        var localTransform = node.GetLocalTransform();
        var globalTransform = localTransform * parentTransform;

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
    }

    private void CompileRectCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        var r = cmd.Rect;
        var brush = cmd.Brush as SolidColorBrush;
        var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

        if (cmd.Brush != null)
        {
            // Triangulate fill corners
            var v0 = Vector2.Transform(new Vector2(r.X, r.Y), transform);
            var v1 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y), transform);
            var v2 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), transform);
            var v3 = Vector2.Transform(new Vector2(r.X, r.Y + r.Height), transform);

            ushort idxStart = (ushort)_vectorVerticesList.Count;

            _vectorVerticesList.Add(new VectorVertex(v0, color, Vector2.Zero));
            _vectorVerticesList.Add(new VectorVertex(v1, color, Vector2.Zero));
            _vectorVerticesList.Add(new VectorVertex(v2, color, Vector2.Zero));
            _vectorVerticesList.Add(new VectorVertex(v3, color, Vector2.Zero));

            // Triangle 1
            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((ushort)(idxStart + 1));
            _vectorIndicesList.Add((ushort)(idxStart + 2));

            // Triangle 2
            _vectorIndicesList.Add(idxStart);
            _vectorIndicesList.Add((ushort)(idxStart + 2));
            _vectorIndicesList.Add((ushort)(idxStart + 3));
        }

        if (cmd.Pen != null)
        {
            var outline = new List<Vector2>
            {
                new(r.X, r.Y),
                new(r.X + r.Width, r.Y),
                new(r.X + r.Width, r.Y + r.Height),
                new(r.X, r.Y + r.Height)
            };

            var penBrush = cmd.Pen.Brush as SolidColorBrush;
            var penColor = penBrush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

            // CPU-side transform of outlines
            var transformedOutline = new List<Vector2>(outline.Count);
            foreach (var p in outline)
            {
                transformedOutline.Add(Vector2.Transform(p, transform));
            }

            StrokeTessellator.TessellateStroke(
                transformedOutline,
                cmd.Pen.Thickness,
                penColor,
                isClosed: true,
                _vectorVerticesList,
                _vectorIndicesList
            );
        }
    }

    private void CompilePathCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Path == null) return;
        var flattened = cmd.Path.Flatten(0.2f);

        if (cmd.Brush != null)
        {
            var brush = cmd.Brush as SolidColorBrush;
            var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

            foreach (var contour in flattened)
            {
                var transContour = new List<Vector2>(contour.Count);
                foreach (var pt in contour)
                {
                    transContour.Add(Vector2.Transform(pt, transform));
                }

                FillTessellator.TessellateFill(
                    transContour,
                    color,
                    _vectorVerticesList,
                    _vectorIndicesList
                );
            }
        }

        if (cmd.Pen != null)
        {
            var penBrush = cmd.Pen.Brush as SolidColorBrush;
            var penColor = penBrush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

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
                    penColor,
                    figure.IsClosed,
                    _vectorVerticesList,
                    _vectorIndicesList
                );
            }
        }
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

        var brush = cmd.Brush as SolidColorBrush;
        var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

        foreach (var runGlyph in layout.Glyphs)
        {
            var info = runGlyph.Glyph;
            if (info.Width == 0 || info.Height == 0) continue;

            // Bounding box of the glyph quad.
            // These coordinates correctly reflect the glyph padding (including any SDF spread/antialiasing padding)
            // scaled to the target font size via the GlyphRasterizer, ensuring no clipping of the SDF spread.
            float x0 = runGlyph.Position.X + cmd.Position.X;
            float y0 = runGlyph.Position.Y + cmd.Position.Y;
            float x1 = x0 + info.Width;
            float y1 = y0 + info.Height;

            // Transform vertices on CPU
            var v0 = Vector2.Transform(new Vector2(x0, y0), transform);
            var v1 = Vector2.Transform(new Vector2(x1, y0), transform);
            var v2 = Vector2.Transform(new Vector2(x1, y1), transform);
            var v3 = Vector2.Transform(new Vector2(x0, y1), transform);

            ushort idxStart = (ushort)_textVerticesList.Count;

            // Set dynamic UV texture mappings
            var uv0 = new Vector2(info.TexCoordMin.X, info.TexCoordMin.Y);
            var uv1 = new Vector2(info.TexCoordMax.X, info.TexCoordMin.Y);
            var uv2 = new Vector2(info.TexCoordMax.X, info.TexCoordMax.Y);
            var uv3 = new Vector2(info.TexCoordMin.X, info.TexCoordMax.Y);

            _textVerticesList.Add(new VectorVertex(v0, color, uv0));
            _textVerticesList.Add(new VectorVertex(v1, color, uv1));
            _textVerticesList.Add(new VectorVertex(v2, color, uv2));
            _textVerticesList.Add(new VectorVertex(v3, color, uv3));

            // Quads Triangle Indices
            _textIndicesList.Add(idxStart);
            _textIndicesList.Add((ushort)(idxStart + 1));
            _textIndicesList.Add((ushort)(idxStart + 2));

            _textIndicesList.Add(idxStart);
            _textIndicesList.Add((ushort)(idxStart + 2));
            _textIndicesList.Add((ushort)(idxStart + 3));
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

        _textureDrawCalls.Add((cmd.Texture, (uint)(_textureIndicesList.Count - 6), 6));
    }

    private void EnsureBufferSize(ref GpuBuffer buffer, uint requiredSize, BufferUsage usage)
    {
        if (buffer.Size >= requiredSize) return;

        // Resize buffer (double or pad size)
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

    ~Compositor()
    {
        Dispose();
    }
}
