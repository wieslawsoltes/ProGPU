using System;
using System.Numerics;
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
        private const string CommonShaderHelpers = @"
struct VSUniforms {
    projection: mat4x4<f32>,
    view: mat4x4<f32>,
    cameraPosition: vec3<f32>,
    _pad: f32,
};

struct GpuMesh3DRecord {
    modelTransform: mat4x4<f32>,
    color: vec4<f32>,
    lightDirection: vec4<f32>,
    ambientColor: vec4<f32>,
    specularColor: vec4<f32>,
    materialAmbient: vec4<f32>,
    opacity: f32,
    renderMode: f32,
    shadingMode: f32,
    _pad2: f32,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;
@group(0) @binding(1) var<storage, read> meshRecords: array<GpuMesh3DRecord>;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) worldPosition: vec3<f32>,
    @location(1) worldNormal: vec3<f32>,
    @location(2) @interpolate(flat) instanceIdx: u32,
};

struct VertexOutputWireframe {
    @builtin(position) position: vec4<f32>,
    @location(0) worldPosition: vec3<f32>,
    @location(1) worldNormal: vec3<f32>,
    @location(2) barycentric: vec3<f32>,
    @location(3) renderMode: f32,
    @location(4) @interpolate(flat) instanceIdx: u32,
};

fn DistributionGGX(N: vec3<f32>, H: vec3<f32>, roughness: f32) -> f32 {
    let alpha = roughness * roughness;
    let alpha2 = alpha * alpha;
    let NdotH = max(dot(N, H), 0.0);
    let NdotH2 = NdotH * NdotH;
    
    let denom = (NdotH2 * (alpha2 - 1.0) + 1.0);
    return alpha2 / (3.1415926535 * denom * denom);
}

fn VisibilitySchlickGGX(NdotV: f32, NdotL: f32, roughness: f32) -> f32 {
    let r = (roughness + 1.0);
    let k = (r * r) / 8.0;
    let denom = (NdotV * (1.0 - k) + k) * (NdotL * (1.0 - k) + k) * 4.0;
    return 1.0 / max(denom, 0.0001);
}

fn FresnelSchlick(cosTheta: f32, F0: vec3<f32>) -> vec3<f32> {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

fn GoochShading(N: vec3<f32>, L: vec3<f32>, diffuseColor: vec3<f32>) -> vec3<f32> {
    let NdotL = dot(N, L);
    let t = NdotL * 0.5 + 0.5;
    let coolCol = vec3<f32>(0.0, 0.0, 0.55) + 0.25 * diffuseColor;
    let warmCol = vec3<f32>(0.3, 0.3, 0.0) + 0.25 * diffuseColor;
    return mix(coolCol, warmCol, t);
}

fn ComputeLighting(
    instanceIdx: u32,
    worldPos: vec3<f32>,
    worldNormal: vec3<f32>
) -> vec4<f32> {
    let record = meshRecords[instanceIdx];
    let shading = u32(record.shadingMode + 0.5);

    let N = normalize(worldNormal);

    if (shading == 6u) { // Normals Diagnostic
        let normalColor = N * 0.5 + 0.5;
        return vec4<f32>(normalColor, record.opacity);
    }

    if (shading == 2u) { // Flat / Unlit
        return vec4<f32>(record.color.rgb, record.opacity);
    }

    if (shading == 3u) { // Hidden Line
        return vec4<f32>(0.05, 0.05, 0.06, record.opacity); // background solid fill
    }

    let V = normalize(uniforms.cameraPosition - worldPos);

    let shininess = record.specularColor.w;
    let roughness = clamp(sqrt(2.0 / (max(shininess, 0.001) + 2.0)), 0.04, 1.0);
    let F0 = mix(vec3<f32>(0.04), record.color.rgb, 0.1);

    let keyDir = normalize(record.lightDirection.xyz);
    let keyIntensity = record.lightDirection.w;

    let fillDir = normalize(vec3<f32>(-keyDir.x, 0.5, -keyDir.z));
    let fillIntensity = keyIntensity * 0.35;
    let fillCol = vec3<f32>(0.8, 0.88, 1.0);

    let backDir = normalize(vec3<f32>(-keyDir.x, -keyDir.y, -keyDir.z));
    let backIntensity = keyIntensity * 0.45;
    let backCol = vec3<f32>(1.0, 0.95, 0.9);

    var diffuseOut = vec3<f32>(0.0);
    var specularOut = vec3<f32>(0.0);

    if (shading == 1u) { // Conceptual (Gooch Shading)
        diffuseOut += GoochShading(N, keyDir, record.color.rgb) * keyIntensity;
        diffuseOut += GoochShading(N, fillDir, record.color.rgb) * fillIntensity * fillCol;
        diffuseOut += GoochShading(N, backDir, record.color.rgb) * backIntensity * backCol;

        let H = normalize(keyDir + V);
        let NdotL = max(dot(N, keyDir), 0.0);
        let NdotV = max(dot(N, V), 0.0);
        if (NdotL > 0.0) {
            let D = DistributionGGX(N, H, roughness);
            let V_joint = VisibilitySchlickGGX(NdotV, NdotL, roughness);
            let F = FresnelSchlick(max(dot(H, V), 0.0), F0);
            specularOut += D * V_joint * F * NdotL * keyIntensity;
        }
    } else { // Realistic (PBR GGX) or ShadesOfGray or XRay
        // 1. KEY LIGHT
        {
            let L = keyDir;
            let H = normalize(L + V);
            let NdotL = max(dot(N, L), 0.0);
            let NdotV = max(dot(N, V), 0.0);
            if (NdotL > 0.0) {
                let D = DistributionGGX(N, H, roughness);
                let V_joint = VisibilitySchlickGGX(NdotV, NdotL, roughness);
                let F = FresnelSchlick(max(dot(H, V), 0.0), F0);
                let spec = D * V_joint * F;
                let kS = F;
                let kD = (vec3<f32>(1.0) - kS);
                diffuseOut += (kD * record.color.rgb / 3.1415926535) * NdotL * keyIntensity;
                specularOut += spec * NdotL * keyIntensity;
            }
        }

        // 2. FILL LIGHT
        {
            let L = fillDir;
            let H = normalize(L + V);
            let NdotL = max(dot(N, L), 0.0);
            let NdotV = max(dot(N, V), 0.0);
            if (NdotL > 0.0) {
                let D = DistributionGGX(N, H, roughness);
                let V_joint = VisibilitySchlickGGX(NdotV, NdotL, roughness);
                let F = FresnelSchlick(max(dot(H, V), 0.0), F0);
                let spec = D * V_joint * F;
                let kS = F;
                let kD = (vec3<f32>(1.0) - kS);
                diffuseOut += (kD * record.color.rgb / 3.1415926535) * NdotL * fillIntensity * fillCol;
                specularOut += spec * NdotL * fillIntensity * fillCol;
            }
        }

        // 3. BACK LIGHT
        {
            let L = backDir;
            let H = normalize(L + V);
            let NdotL = max(dot(N, L), 0.0);
            let NdotV = max(dot(N, V), 0.0);
            if (NdotL > 0.0) {
                let D = DistributionGGX(N, H, roughness);
                let V_joint = VisibilitySchlickGGX(NdotV, NdotL, roughness);
                let F = FresnelSchlick(max(dot(H, V), 0.0), F0);
                let spec = D * V_joint * F;
                let kS = F;
                let kD = (vec3<f32>(1.0) - kS);
                diffuseOut += (kD * record.color.rgb / 3.1415926535) * NdotL * backIntensity * backCol;
                specularOut += spec * NdotL * backIntensity * backCol;
            }
        }
    }

    let skyFactor = N.y * 0.5 + 0.5;
    let skyAmbient = record.ambientColor.rgb * record.ambientColor.w;
    let groundAmbient = record.ambientColor.rgb * record.ambientColor.w * 0.4;
    let ambient = mix(groundAmbient, skyAmbient, skyFactor) * record.materialAmbient.rgb;

    let F_rim = pow(1.0 - max(dot(N, V), 0.0), 4.0);
    let rimColor = vec3<f32>(0.85, 0.90, 1.0) * F_rim * 0.25 * keyIntensity;

    var resultColor = ambient + diffuseOut + specularOut + rimColor;

    if (shading == 4u) { // Shades of Gray
        let gray = dot(resultColor, vec3<f32>(0.2126, 0.7152, 0.0722));
        resultColor = vec3<f32>(gray);
    }

    var opacity = record.opacity;
    if (shading == 5u) { // X-Ray Mode
        opacity = clamp(0.15 + 0.55 * pow(1.0 - max(dot(N, V), 0.0), 3.0), 0.0, 1.0) * record.opacity;
    }

    return vec4<f32>(resultColor, opacity);
}
";

        private const string Mesh3DSolidShaderCode = CommonShaderHelpers + @"
@vertex
fn vs_main(input: VertexInput, @builtin(instance_index) instanceIdx: u32) -> VertexOutput {
    var output: VertexOutput;
    let record = meshRecords[instanceIdx];

    let worldPos = record.modelTransform * vec4<f32>(input.position, 1.0);
    let worldNormal = normalize((record.modelTransform * vec4<f32>(input.normal, 0.0)).xyz);

    output.position = uniforms.projection * uniforms.view * worldPos;
    output.worldPosition = worldPos.xyz;
    output.worldNormal = worldNormal;
    output.instanceIdx = instanceIdx;

    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return ComputeLighting(input.instanceIdx, input.worldPosition, input.worldNormal);
}
";

        private const string Mesh3DWireframeShaderCode = CommonShaderHelpers + @"
@vertex
fn vs_main(input: VertexInput, @builtin(vertex_index) vertexIdx: u32, @builtin(instance_index) instanceIdx: u32) -> VertexOutputWireframe {
    var output: VertexOutputWireframe;
    let record = meshRecords[instanceIdx];

    let worldPos = record.modelTransform * vec4<f32>(input.position, 1.0);
    let worldNormal = normalize((record.modelTransform * vec4<f32>(input.normal, 0.0)).xyz);

    output.position = uniforms.projection * uniforms.view * worldPos;
    output.worldPosition = worldPos.xyz;
    output.worldNormal = worldNormal;
    output.renderMode = record.renderMode;
    output.instanceIdx = instanceIdx;

    let triVertexIdx = vertexIdx % 3u;
    if (triVertexIdx == 0u) {
        output.barycentric = vec3<f32>(1.0, 0.0, 0.0);
    } else if (triVertexIdx == 1u) {
        output.barycentric = vec3<f32>(0.0, 1.0, 0.0);
    } else {
        output.barycentric = vec3<f32>(0.0, 0.0, 1.0);
    }

    return output;
}

@fragment
fn fs_main(input: VertexOutputWireframe) -> @location(0) vec4<f32> {
    let mode = u32(input.renderMode + 0.5);
    let solidColor = ComputeLighting(input.instanceIdx, input.worldPosition, input.worldNormal);

    let dFdx = dpdx(input.barycentric);
    let dFdy = dpdy(input.barycentric);
    let g = max(sqrt(dFdx * dFdx + dFdy * dFdy), vec3<f32>(0.00001));
    let dist = input.barycentric / g;
    let minDist = min(dist.x, min(dist.y, dist.z));

    let lineWidth = 1.0; 
    let edge = smoothstep(lineWidth - 0.5, lineWidth + 0.5, minDist);

    let wireframeColor = vec4<f32>(0.85, 0.85, 0.9, solidColor.a);

    if (mode == 1u) {
        let alpha = (1.0 - edge) * solidColor.a;
        if (alpha < 0.01) {
            discard;
        }
        return vec4<f32>(wireframeColor.rgb, alpha);
    }

    let finalColor = mix(wireframeColor.rgb, solidColor.rgb, edge);
    return vec4<f32>(finalColor, solidColor.a);
}
";

        private class CachedGeometry
        {
            public GpuBuffer VertexBuffer;
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
        private unsafe RenderPipeline* _cachedWireframePipeline;

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

                cpuRecords[i] = new GpuMesh3DRecord
                {
                    ModelTransform = mesh.ModelTransform,
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
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("Mesh3DSolidShader_3D_v3", Mesh3DSolidShaderCode, "Mesh3D WGSL 3D Solid Shader");

                var layouts = new VertexBufferLayout[]
                {
                    new VertexBufferLayout
                    {
                        ArrayStride = (uint)Marshal.SizeOf<GpuVertex3D>(),
                        StepMode = VertexStepMode.Vertex,
                        AttributeCount = 2,
                        Attributes = (VertexAttribute*)Marshal.AllocHGlobal(Marshal.SizeOf<VertexAttribute>() * 2)
                    }
                };

                var attrs = layouts[0].Attributes;
                attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 }; // Position
                attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 12, ShaderLocation = 1 }; // Normal

                _cachedPipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                    "Mesh3DPipeline_3D_v3",
                    shaderModule,
                    vertexBufferLayouts: layouts,
                    topology: PrimitiveTopology.TriangleList,
                    targetFormat: TextureFormat.Rgba8Unorm,
                    enableDepthStencil: true,
                    depthFormat: TextureFormat.Depth24PlusStencil8,
                    sampleCount: 4u,
                    depthWriteEnabled: true,
                    depthCompare: CompareFunction.LessEqual,
                    cullMode: CullMode.Back
                );

                Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes);
            }

            // Create wireframe pipeline if needed (TriangleList with double sided rendering)
            if (_cachedWireframePipeline == null)
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("Mesh3DWireframeShader_3D_v3", Mesh3DWireframeShaderCode, "Mesh3D WGSL 3D Wireframe Shader");

                var layouts = new VertexBufferLayout[]
                {
                    new VertexBufferLayout
                    {
                        ArrayStride = (uint)Marshal.SizeOf<GpuVertex3D>(),
                        StepMode = VertexStepMode.Vertex,
                        AttributeCount = 2,
                        Attributes = (VertexAttribute*)Marshal.AllocHGlobal(Marshal.SizeOf<VertexAttribute>() * 2)
                    }
                };

                var attrs = layouts[0].Attributes;
                attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 }; // Position
                attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 12, ShaderLocation = 1 }; // Normal

                _cachedWireframePipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                    "Mesh3DWireframePipeline_3D_v3",
                    shaderModule,
                    vertexBufferLayouts: layouts,
                    topology: PrimitiveTopology.TriangleList,
                    targetFormat: TextureFormat.Rgba8Unorm,
                    enableDepthStencil: true,
                    depthFormat: TextureFormat.Depth24PlusStencil8,
                    sampleCount: 4u,
                    depthWriteEnabled: true,
                    depthCompare: CompareFunction.LessEqual,
                    cullMode: CullMode.None
                );

                Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes);
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
                    if (entry.Geometry == null) continue;

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
    }
}
