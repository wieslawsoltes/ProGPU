using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public enum RenderMode3D
    {
        Solid = 0,
        Wireframe = 1,
        SolidWireframe = 2
    }

    public enum ShadingMode3D
    {
        Realistic = 0,
        Conceptual = 1,
        Flat = 2,
        HiddenLine = 3,
        ShadesOfGray = 4,
        XRay = 5,
        Normals = 6
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GpuVertex3D
    {
        public Vector3 Position;
        public Vector3 Normal;

        public GpuVertex3D(Vector3 position, Vector3 normal)
        {
            Position = position;
            Normal = normal;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GpuMesh3DRecord
    {
        public Matrix4x4 ModelTransform;      // 3D Model transform for lighting
        public Matrix4x4 NormalTransform;     // Inverse-transpose for normal transformation
        public Vector4 Color;                 // Diffuse Color Kd
        public Vector4 LightDirection;        // xyz = direction, w = intensity
        public Vector4 AmbientColor;          // rgb = color, w = intensity
        public Vector4 SpecularColor;         // rgb = Specular Ks, w = Exponent Ns
        public Vector4 MaterialAmbient;       // rgb = Material Ka, w = unused
        public float Opacity;
        public float RenderMode;              // 0.0f = Solid, 1.0f = Wireframe, 2.0f = SolidWireframe
        public float ShadingMode;             // AutoCAD Shading Mode (0=Realistic, 1=Conceptual, 2=Flat, 3=HiddenLine, 4=ShadesOfGray, 5=XRay, 6=Normals)
        private float _pad2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GpuMesh3DUniforms
    {
        public Matrix4x4 Projection;
        public Matrix4x4 View;
        public Vector3 CameraPosition;
        private float _pad;
    }

    public class Mesh3DExtensionPipeline : ICompositorExtension
    {


        private static readonly string Mesh3DSolidShaderCode = ShaderResource.Load(typeof(Mesh3DExtensionPipeline), "Mesh3DSolid.wgsl");
 
        private static readonly string Mesh3DWireframeShaderCode = ShaderResource.Load(typeof(Mesh3DExtensionPipeline), "Mesh3DWireframe.wgsl");

        private class CachedGeometry
        {
            public GpuBuffer VertexBuffer = null!;
            public uint VertexCount;
            public int Version;
        }

        private class ViewportResource
        {
            public GpuBuffer UniformsBuffer;
            public GpuBuffer? DynamicRecordsBuffer;
            public unsafe BindGroup* SolidBindGroup;
            public unsafe BindGroup* WireframeBindGroup;
            public int RecordGen = -1;

            public ViewportResource(WgpuContext context, uint uniformsSize)
            {
                UniformsBuffer = new GpuBuffer(context, uniformsSize, BufferUsage.Uniform | BufferUsage.CopyDst, "Mesh3D Uniforms Buffer");
            }
            
            public unsafe void Dispose(WgpuContext context)
            {
                UniformsBuffer.Dispose();
                DynamicRecordsBuffer?.Dispose();
                if (SolidBindGroup != null) context.Wgpu.BindGroupRelease(SolidBindGroup);
                if (WireframeBindGroup != null) context.Wgpu.BindGroupRelease(WireframeBindGroup);
            }
        }

        private readonly Dictionary<object, CachedGeometry> _geometryCache = new();
        private readonly List<ViewportResource> _viewportResources = new();
        private readonly List<nint> _pendingCommandBuffers = new();
        private int _currentCompileIndex;
        private WgpuContext? _context;
        
        private unsafe RenderPipeline* _cachedPipeline;
        private unsafe RenderPipeline* _cachedBackFacePipeline;
        private unsafe RenderPipeline* _cachedWireframePipeline;

        private unsafe RenderPipeline* CreateMeshPipeline(
            Compositor compositor,
            string shaderKey,
            string shaderCode,
            string shaderLabel,
            string pipelineKey,
            CullMode cullMode)
        {
            var shaderModule = compositor.PipelineCache.GetOrCreateShader(shaderKey, shaderCode, shaderLabel);

            Span<VertexAttribute> attrs = stackalloc VertexAttribute[2];
            attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 }; // Position
            attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 12, ShaderLocation = 1 }; // Normal

            Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];
            fixed (VertexAttribute* attrsPtr = attrs)
            {
                layouts[0] = new VertexBufferLayout
                {
                    ArrayStride = (uint)Unsafe.SizeOf<GpuVertex3D>(),
                    StepMode = VertexStepMode.Vertex,
                    AttributeCount = 2,
                    Attributes = attrsPtr
                };

                return compositor.PipelineCache.GetOrCreateRenderPipeline(
                    pipelineKey,
                    shaderModule,
                    layouts,
                    topology: PrimitiveTopology.TriangleList,
                    targetFormat: TextureFormat.Rgba8Unorm,
                    enableDepthStencil: true,
                    depthFormat: TextureFormat.Depth24PlusStencil8,
                    sampleCount: 4u,
                    depthWriteEnabled: true,
                    depthCompare: CompareFunction.LessEqual,
                    cullMode: cullMode
                );
            }
        }

        public unsafe void BeginFrame(Compositor compositor)
        {
            _currentCompileIndex = 0;
            if (_pendingCommandBuffers.Count > 0)
            {
                var wgpu = compositor.Context.Wgpu;
                for (int i = 0; i < _pendingCommandBuffers.Count; i++)
                {
                    wgpu.CommandBufferRelease((CommandBuffer*)_pendingCommandBuffers[i]);
                }
                _pendingCommandBuffers.Clear();
            }
        }

        public unsafe void Dispose()
        {
            foreach (var cache in _geometryCache.Values)
            {
                cache.VertexBuffer.Dispose();
            }
            _geometryCache.Clear();

            if (_context != null)
            {
                foreach (var res in _viewportResources)
                {
                    res.Dispose(_context);
                }
            }
            _viewportResources.Clear();
        }

        public unsafe void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            var payload = cmd.DataParam as Viewport3DCompilationPayload;
            if (payload == null || payload.Meshes.Count == 0 || payload.ColorTexture == null || payload.DepthTexture == null) return;

            _context = compositor.Context;
            var wgpu = compositor.Context.Wgpu;
            var device = compositor.Context.Device;
            var queue = compositor.Context.Queue;

            uint uniformsSize = (uint)Marshal.SizeOf<GpuMesh3DUniforms>();

            // Ensure pooled resource exists for current viewport compile index
            while (_viewportResources.Count <= _currentCompileIndex)
            {
                _viewportResources.Add(new ViewportResource(compositor.Context, uniformsSize));
            }
            var res = _viewportResources[_currentCompileIndex];

            // 1. Create or update dynamic record buffer
            int recordCount = payload.Meshes.Count;

            uint reqRecordsSize = (uint)recordCount * (uint)Marshal.SizeOf<GpuMesh3DRecord>();
            if (res.DynamicRecordsBuffer == null || res.DynamicRecordsBuffer.Size < reqRecordsSize)
            {
                res.DynamicRecordsBuffer?.Dispose();
                res.DynamicRecordsBuffer = new GpuBuffer(compositor.Context, reqRecordsSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic Mesh3D Records Buffer");
                res.RecordGen = -1; // Force bind group recreation
            }

            // 2. Upload records data
            var cpuRecords = new GpuMesh3DRecord[recordCount];
            int n = payload.Meshes.Count;
            for (int i = 0; i < n; i++)
            {
                var mesh = payload.Meshes[i];
                float rMode = 0.0f; // Solid
                if (payload.RenderMode == RenderMode3D.Wireframe)
                {
                    rMode = 1.0f;
                }
                else if (payload.RenderMode == RenderMode3D.SolidWireframe)
                {
                    rMode = 2.0f;
                }

                Matrix4x4 normalTransform = Matrix4x4.Identity;
                if (Matrix4x4.Invert(mesh.ModelTransform, out var invModel))
                {
                    normalTransform = Matrix4x4.Transpose(invModel);
                }

                cpuRecords[i] = new GpuMesh3DRecord
                {
                    ModelTransform = mesh.ModelTransform,
                    NormalTransform = normalTransform,
                    Color = mesh.Color,
                    LightDirection = new Vector4(payload.LightDirection, payload.LightIntensity),
                    AmbientColor = new Vector4(payload.AmbientColor, payload.AmbientIntensity),
                    SpecularColor = new Vector4(mesh.SpecularColor, mesh.Shininess),
                    MaterialAmbient = new Vector4(mesh.AmbientColor, 1.0f),
                    Opacity = mesh.Opacity * compositor.ActiveOpacity,
                    RenderMode = rMode,
                    ShadingMode = (float)payload.ShadingMode
                };
            }
            res.DynamicRecordsBuffer.Write(cpuRecords);

            Matrix4x4.Invert(cmd.CameraView, out var invView);
            Vector3 cameraPos = invView.Translation;

            // 3. Upload uniforms data
            var cpuUniforms = new GpuMesh3DUniforms
            {
                Projection = cmd.Transform, // Perspective projection matrix
                View = cmd.CameraView,      // View matrix
                CameraPosition = cameraPos
            };
            res.UniformsBuffer.WriteSingle(cpuUniforms);

            // 4. Create solid pipeline if needed
            if (_cachedPipeline == null)
            {
                _cachedPipeline = CreateMeshPipeline(
                    compositor,
                    "Mesh3DSolidShader_3D_v3",
                    Mesh3DSolidShaderCode,
                    "Mesh3D WGSL 3D Solid Shader",
                    "Mesh3DPipeline_3D_v3",
                    CullMode.Back);
            }

            if (_cachedBackFacePipeline == null)
            {
                _cachedBackFacePipeline = CreateMeshPipeline(
                    compositor,
                    "Mesh3DSolidShader_3D_v3",
                    Mesh3DSolidShaderCode,
                    "Mesh3D WGSL 3D Solid Shader",
                    "Mesh3DBackFacePipeline_3D_v3",
                    CullMode.Front);
            }

            // Create wireframe pipeline if needed (TriangleList with double sided rendering)
            if (_cachedWireframePipeline == null)
            {
                _cachedWireframePipeline = CreateMeshPipeline(
                    compositor,
                    "Mesh3DWireframeShader_3D_v3",
                    Mesh3DWireframeShaderCode,
                    "Mesh3D WGSL 3D Wireframe Shader",
                    "Mesh3DWireframePipeline_3D_v3",
                    CullMode.None);
            }

            // 5. Create or get cached BindGroup
            int currentGen = res.DynamicRecordsBuffer.GetHashCode() ^ res.UniformsBuffer.GetHashCode();
            if (res.SolidBindGroup == null || res.WireframeBindGroup == null || currentGen != res.RecordGen)
            {
                res.RecordGen = currentGen;

                var bgEntries = stackalloc BindGroupEntry[2];
                bgEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = res.UniformsBuffer.BufferPtr,
                    Offset = 0,
                    Size = uniformsSize
                };
                bgEntries[1] = new BindGroupEntry
                {
                    Binding = 1,
                    Buffer = res.DynamicRecordsBuffer.BufferPtr,
                    Offset = 0,
                    Size = res.DynamicRecordsBuffer.Size
                };

                // Bind group for Solid Pipeline
                var pipelineLayout = wgpu.RenderPipelineGetBindGroupLayout(_cachedPipeline, 0);
                var bgDesc = new BindGroupDescriptor
                {
                    Layout = pipelineLayout,
                    EntryCount = 2,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr("Mesh3D 3D BindGroup")
                };

                if (res.SolidBindGroup != null) wgpu.BindGroupRelease(res.SolidBindGroup);
                res.SolidBindGroup = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                SilkMarshal.Free((nint)bgDesc.Label);

                // Bind group for Wireframe Pipeline
                var wireframeLayout = wgpu.RenderPipelineGetBindGroupLayout(_cachedWireframePipeline, 0);
                var wireframeBgDesc = new BindGroupDescriptor
                {
                    Layout = wireframeLayout,
                    EntryCount = 2,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr("Mesh3D Wireframe BindGroup")
                };

                if (res.WireframeBindGroup != null) wgpu.BindGroupRelease(res.WireframeBindGroup);
                res.WireframeBindGroup = wgpu.DeviceCreateBindGroup(device, &wireframeBgDesc);
                SilkMarshal.Free((nint)wireframeBgDesc.Label);
            }

            // 6. Begin offscreen WebGPU Render Pass targeting the custom color and depth textures!
            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Mesh3D Offscreen Encoder") };
            var encoder = wgpu.DeviceCreateCommandEncoder(device, &encoderDesc);
            SilkMarshal.Free((nint)encoderDesc.Label);

            var colorAttachment = new RenderPassColorAttachment
            {
                View = payload.MsaaColorTexture != null ? payload.MsaaColorTexture.ViewPtr : payload.ColorTexture.ViewPtr,
                ResolveTarget = payload.MsaaColorTexture != null ? payload.ColorTexture.ViewPtr : null,
                LoadOp = LoadOp.Clear,
                StoreOp = payload.MsaaColorTexture != null ? StoreOp.Discard : StoreOp.Store,
                ClearValue = new Silk.NET.WebGPU.Color { R = 0.05f, G = 0.05f, B = 0.06f, A = 1.0f } // Slate premium dark background
            };

            var depthAttachment = new RenderPassDepthStencilAttachment
            {
                View = payload.DepthTexture.ViewPtr,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
                DepthClearValue = 1.0f,
                DepthReadOnly = false,
                StencilLoadOp = LoadOp.Clear,
                StencilStoreOp = StoreOp.Store,
                StencilClearValue = 0,
                StencilReadOnly = false
            };

            var passDesc = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
                DepthStencilAttachment = &depthAttachment
            };

            var pass = wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);

            // 7. Compile mesh buffers on demand
            for (int i = 0; i < payload.Meshes.Count; i++)
            {
                var entry = payload.Meshes[i];
                if (entry.Geometry == null) continue;

                bool needsRebuild = false;
                if (_geometryCache.TryGetValue(entry.Geometry, out var cache))
                {
                    if (cache.Version != entry.GeometryVersion)
                    {
                        cache.VertexBuffer.Dispose();
                        needsRebuild = true;
                    }
                }
                else
                {
                    needsRebuild = true;
                }

                if (needsRebuild)
                {
                    // Create De-indexed (non-indexed) Vertex Buffer
                    var cpuVertices = new GpuVertex3D[entry.Indices.Length];
                    for (int idx = 0; idx < entry.Indices.Length; idx++)
                    {
                        int vIdx = entry.Indices[idx];
                        var pos = (vIdx >= 0 && vIdx < entry.Positions.Length) ? entry.Positions[vIdx] : Vector3.Zero;
                        var norm = (vIdx >= 0 && vIdx < entry.Normals.Length) ? entry.Normals[vIdx] : Vector3.UnitY;
                        cpuVertices[idx] = new GpuVertex3D(pos, norm);
                    }

                    uint vSize = (uint)cpuVertices.Length * (uint)Marshal.SizeOf<GpuVertex3D>();
                    var vBuffer = new GpuBuffer(compositor.Context, vSize, BufferUsage.Vertex | BufferUsage.CopyDst, "3D Mesh Vertex Buffer");
                    vBuffer.Write(cpuVertices);

                    cache = new CachedGeometry
                    {
                        VertexBuffer = vBuffer,
                        VertexCount = (uint)entry.Indices.Length,
                        Version = entry.GeometryVersion
                    };
                    _geometryCache[entry.Geometry] = cache;
                }
            }

            // Draw Passes
            var mode = payload.RenderMode;

            if (mode == RenderMode3D.Solid)
            {
                wgpu.RenderPassEncoderSetPipeline(pass, _cachedPipeline);
                wgpu.RenderPassEncoderSetBindGroup(pass, 0, res.SolidBindGroup, 0, null);
                for (int i = 0; i < payload.Meshes.Count; i++)
                {
                    var entry = payload.Meshes[i];
                    if (entry.Geometry == null || entry.IsBackFace) continue;

                    var cache = _geometryCache[entry.Geometry];

                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, cache.VertexBuffer.BufferPtr, 0, cache.VertexBuffer.Size);
                    wgpu.RenderPassEncoderDraw(pass, cache.VertexCount, 1, 0, (uint)i);
                }

                wgpu.RenderPassEncoderSetPipeline(pass, _cachedBackFacePipeline);
                for (int i = 0; i < payload.Meshes.Count; i++)
                {
                    var entry = payload.Meshes[i];
                    if (entry.Geometry == null || !entry.IsBackFace) continue;

                    var cache = _geometryCache[entry.Geometry];

                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, cache.VertexBuffer.BufferPtr, 0, cache.VertexBuffer.Size);
                    wgpu.RenderPassEncoderDraw(pass, cache.VertexCount, 1, 0, (uint)i);
                }
            }
            else if (mode == RenderMode3D.Wireframe || mode == RenderMode3D.SolidWireframe)
            {
                wgpu.RenderPassEncoderSetPipeline(pass, _cachedWireframePipeline);
                wgpu.RenderPassEncoderSetBindGroup(pass, 0, res.WireframeBindGroup, 0, null);
                for (int i = 0; i < payload.Meshes.Count; i++)
                {
                    var entry = payload.Meshes[i];
                    if (entry.Geometry == null) continue;

                    var cache = _geometryCache[entry.Geometry];

                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, cache.VertexBuffer.BufferPtr, 0, cache.VertexBuffer.Size);
                    wgpu.RenderPassEncoderDraw(pass, cache.VertexCount, 1, 0, (uint)i);
                }
            }

            wgpu.RenderPassEncoderEnd(pass);
            wgpu.RenderPassEncoderRelease(pass);

            // 8. Add offscreen command buffer to the deferred submission queue
            var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Mesh3D Offscreen Command Buffer") };
            var cmdBuffer = wgpu.CommandEncoderFinish(encoder, &cmdDesc);
            SilkMarshal.Free((nint)cmdDesc.Label);

            _pendingCommandBuffers.Add((nint)cmdBuffer);

            wgpu.CommandEncoderRelease(encoder);

            _currentCompileIndex++;

            // DrawExtension is now a no-op in the main compositor pass since the offscreen pass is fully complete and
            // the Viewport3D control appends a separate DrawTexture command!
            cmd.PointBufferOffset = 0;
            cmd.PointBufferCount = 0;
        }

        public unsafe void EndFrame(Compositor compositor)
        {
            if (_pendingCommandBuffers.Count > 0)
            {
                var wgpu = compositor.Context.Wgpu;
                var queue = compositor.Context.Queue;

                int count = _pendingCommandBuffers.Count;
                var buffers = stackalloc CommandBuffer*[count];
                for (int i = 0; i < count; i++)
                {
                    buffers[i] = (CommandBuffer*)_pendingCommandBuffers[i];
                }

                wgpu.QueueSubmit(queue, (uint)count, buffers);

                for (int i = 0; i < count; i++)
                {
                    wgpu.CommandBufferRelease((CommandBuffer*)_pendingCommandBuffers[i]);
                }
                _pendingCommandBuffers.Clear();
            }
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            // Fully no-op
        }
    }

    public class Viewport3DCompilationPayload
    {
        public Vector2 ViewportSize { get; set; } = new Vector2(400f, 300f);
        public Vector3 LightDirection { get; set; } = new Vector3(0.5f, 1f, -0.5f);
        public float LightIntensity { get; set; } = 1.0f;
        public Vector3 AmbientColor { get; set; } = new Vector3(1f, 1f, 1f);
        public float AmbientIntensity { get; set; } = 0.2f;
        public List<MeshCompilationEntry> Meshes { get; } = new();

        public GpuTexture? ColorTexture { get; set; }
        public GpuTexture? MsaaColorTexture { get; set; }
        public GpuTexture? DepthTexture { get; set; }
        
        public RenderMode3D RenderMode { get; set; } = RenderMode3D.Solid;
        public ShadingMode3D ShadingMode { get; set; } = ShadingMode3D.Realistic;
    }

    public class MeshCompilationEntry
    {
        public object? Geometry { get; set; }
        public int GeometryVersion { get; set; }
        public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();
        public int[] Indices { get; set; } = Array.Empty<int>();
        public Matrix4x4 ModelTransform { get; set; } = Matrix4x4.Identity;
        public Vector4 Color { get; set; } = Vector4.One;
        public Vector3 SpecularColor { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);
        public float Shininess { get; set; } = 32.0f;
        public Vector3 AmbientColor { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);
        public float Opacity { get; set; } = 1.0f;
        public bool IsBackFace { get; set; } = false;
    }
}
