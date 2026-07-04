using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public class GpuLineSeriesExtensionPipeline : ICompositorExtension
    {
        private const string ChartLineShaderCode = @"
const AA_PADDING: f32 = 1.5;

struct VSUniforms {
  transform       : mat4x4<f32>,
  canvasSize      : vec2<f32>,
  devicePixelRatio: f32,
  lineWidthCssPx  : f32,
  scale           : vec2<f32>,
  translate       : vec2<f32>,
};

@group(0) @binding(0) var<uniform> vsUniforms : VSUniforms;

struct FSUniforms {
  color : vec4<f32>,
};

@group(0) @binding(1) var<uniform> fsUniforms : FSUniforms;

@group(0) @binding(2) var<storage, read> points : array<vec2<f32>>;

struct VSOut {
  @builtin(position) clipPosition : vec4<f32>,
  @location(0) acrossDevice       : f32,
  @location(1) @interpolate(flat) widthDevice : f32,
};

fn quadUv(vid : u32) -> vec2<f32> {
  switch (vid) {
    case 0u: { return vec2<f32>(0.0, 0.0); }
    case 1u: { return vec2<f32>(1.0, 0.0); }
    case 2u: { return vec2<f32>(0.0, 1.0); }
    case 3u: { return vec2<f32>(0.0, 1.0); }
    case 4u: { return vec2<f32>(1.0, 0.0); }
    default: { return vec2<f32>(1.0, 1.0); }
  }
}

@vertex
fn vs_main(
  @builtin(vertex_index) vid : u32,
  @builtin(instance_index) iid : u32,
) -> VSOut {
  let uv = quadUv(vid);
  let pA_data = points[iid];
  let pB_data = points[iid + 1u];

  if (pA_data.x != pA_data.x || pA_data.y != pA_data.y ||
      pB_data.x != pB_data.x || pB_data.y != pB_data.y) {
    var out: VSOut;
    out.clipPosition = vec4<f32>(0.0, 0.0, 0.0, 0.0);
    out.acrossDevice = 0.0;
    out.widthDevice = 0.0;
    return out;
  }

  let pA_scaled = pA_data * vsUniforms.scale + vsUniforms.translate;
  let pB_scaled = pB_data * vsUniforms.scale + vsUniforms.translate;

  let clipA = vsUniforms.transform * vec4<f32>(pA_scaled, 0.0, 1.0);
  let clipB = vsUniforms.transform * vec4<f32>(pB_scaled, 0.0, 1.0);

  let ndcA = clipA.xy / clipA.w;
  let ndcB = clipB.xy / clipB.w;
  let screenA = vec2<f32>(
    (ndcA.x * 0.5 + 0.5) * vsUniforms.canvasSize.x,
    (1.0 - (ndcA.y * 0.5 + 0.5)) * vsUniforms.canvasSize.y,
  );
  let screenB = vec2<f32>(
    (ndcB.x * 0.5 + 0.5) * vsUniforms.canvasSize.x,
    (1.0 - (ndcB.y * 0.5 + 0.5)) * vsUniforms.canvasSize.y,
  );

  let delta = screenB - screenA;
  let segLen = length(delta);

  if (segLen < 1e-6) {
    var out : VSOut;
    out.clipPosition = clipA;
    out.acrossDevice = 0.0;
    out.widthDevice = 0.0;
    return out;
  }

  let dir = delta / segLen;
  let perp = vec2<f32>(dir.y, -dir.x);

  let dpr = max(vsUniforms.devicePixelRatio, 1e-6);
  let widthDevice = max(1.0, vsUniforms.lineWidthCssPx * dpr);
  let halfExtent = widthDevice * 0.5 + AA_PADDING;

  let baseScreen = mix(screenA, screenB, uv.x);
  let side = mix(1.0, -1.0, uv.y);
  let screenPos = baseScreen + perp * halfExtent * side;

  let acrossDeviceVal = halfExtent * (1.0 + side);

  let clipX = (screenPos.x / vsUniforms.canvasSize.x) * 2.0 - 1.0;
  let clipY = 1.0 - (screenPos.y / vsUniforms.canvasSize.y) * 2.0;

  var out : VSOut;
  out.clipPosition = vec4<f32>(clipX, clipY, 0.0, 1.0);
  out.acrossDevice = acrossDeviceVal;
  out.widthDevice = widthDevice;
  return out;
}

@fragment
fn fs_main(in : VSOut) -> @location(0) vec4<f32> {
  let totalExtent = in.widthDevice + 2.0 * AA_PADDING;
  let edgeDist = min(in.acrossDevice, totalExtent - in.acrossDevice);

  let aa = max(fwidth(in.acrossDevice), 1e-3) * 1.25;
  let edgeCoverage = smoothstep(0.0, aa, edgeDist);

  let nominalDist = min(in.acrossDevice - AA_PADDING, (AA_PADDING + in.widthDevice) - in.acrossDevice);
  let paddingCoverage = smoothstep(0.0, aa, nominalDist);

  let coverage = min(edgeCoverage, paddingCoverage);

  var color = fsUniforms.color;
  color = vec4<f32>(color.rgb, color.a * coverage);
  return color;
}
";

        [StructLayout(LayoutKind.Sequential)]
        private struct LineVsUniforms
        {
            public Matrix4x4 Transform;
            public Vector2 CanvasSize;
            public float DevicePixelRatio;
            public float LineWidthCssPx;
            public Vector2 Scale;
            public Vector2 Translate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ChartFsUniforms
        {
            public Vector4 Color;
        }

        private unsafe RenderPipeline* _cachedPipeline;
        private unsafe RenderPipeline* _cachedPipelineOffscreen;

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            object? staticBuffer = cmd.StaticBuffer;
            if (staticBuffer == null)
            {
                object? cacheKey = null;
                ReadOnlySpan<float> floatsSpan = default;
                int pointsCount = cmd.GpuPointsCount;

                if (cmd.SeriesCacheKey != null)
                {
                    cacheKey = cmd.SeriesCacheKey;
                }
                else if (provider is GpuPicture picture)
                {
                    cacheKey = picture.FloatBuffer;
                }
                else if (cmd.GpuPoints != null)
                {
                    cacheKey = cmd.GpuPoints;
                }

                if (provider != null)
                {
                    floatsSpan = provider.GetFloats(cmd.FloatBufferOffset, cmd.FloatBufferCount);
                }
                else if (cmd.GpuPoints != null)
                {
                    floatsSpan = cmd.GpuPoints;
                }

                if (cacheKey != null)
                {
                    if (!compositor.DynamicGpuBufferCache.TryGetValue(cacheKey, out var cachedBuffer))
                    {
                        cachedBuffer = new GpuSeriesBuffer();
                        compositor.DynamicGpuBufferCache.Add(cacheKey, cachedBuffer);
                    }

                    int requiredLength = pointsCount * 2;
                    bool needsUpload =
                        !cachedBuffer.IsOwnedBy(compositor.Context) ||
                        cachedBuffer.PointsCount != pointsCount ||
                        cachedBuffer.Buffer == null;

                    if (!needsUpload)
                    {
                        if (cachedBuffer.CachedInterleaved == null || cachedBuffer.CachedInterleaved.Length < requiredLength)
                        {
                            needsUpload = true;
                        }
                        else
                        {
                            var cachedSpan = new ReadOnlySpan<float>(cachedBuffer.CachedInterleaved, 0, requiredLength);
                            if (!floatsSpan.Slice(0, requiredLength).SequenceEqual(cachedSpan))
                            {
                                needsUpload = true;
                            }
                        }
                    }

                    if (needsUpload)
                    {
                        if (cachedBuffer.CachedInterleaved == null || cachedBuffer.CachedInterleaved.Length < requiredLength)
                        {
                            cachedBuffer.CachedInterleaved = new float[requiredLength];
                        }
                        floatsSpan.Slice(0, requiredLength).CopyTo(cachedBuffer.CachedInterleaved);
                        cachedBuffer.Upload(cachedBuffer.CachedInterleaved.AsSpan(0, requiredLength), pointsCount);
                    }
                    staticBuffer = cachedBuffer;
                }
                else if (!floatsSpan.IsEmpty)
                {
                    // Uncached fallback path: upload direct
                    var tempBuffer = new GpuSeriesBuffer();
                    tempBuffer.Upload(floatsSpan.Slice(0, pointsCount * 2), pointsCount);
                    staticBuffer = tempBuffer;
                }
            }

            cmd.StaticBuffer = staticBuffer;

            compositor.PendingVectorStart = (uint)compositor.VectorIndices.Count;
            compositor.PendingTextStart = (uint)compositor.TextIndexCount;
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            if (dc.StaticBuffer is not GpuSeriesBuffer seriesBuffer || seriesBuffer.Buffer == null || seriesBuffer.PointsCount < 2) return;

            var wgpu = compositor.Context.Wgpu;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            if (seriesBuffer.VsUniformBuffer == null)
            {
                seriesBuffer.VsUniformBuffer = new GpuBuffer(compositor.Context, 96, BufferUsage.Uniform | BufferUsage.CopyDst, "ChartLine VS Uniforms");
            }
            if (seriesBuffer.FsUniformBuffer == null)
            {
                seriesBuffer.FsUniformBuffer = new GpuBuffer(compositor.Context, 16, BufferUsage.Uniform | BufferUsage.CopyDst, "ChartLine FS Uniforms");
            }

            var vsUniforms = new LineVsUniforms
            {
                Transform = dc.Transform * compositor.CurrentProjection,
                CanvasSize = new Vector2(compositor.CurrentWidth, compositor.CurrentHeight),
                DevicePixelRatio = compositor.CurrentDpiScale,
                LineWidthCssPx = dc.LineThicknessOrRadius,
                Scale = dc.Scale,
                Translate = dc.Translate
            };
            seriesBuffer.VsUniformBuffer.WriteSingle(vsUniforms);

            var fsUniforms = new ChartFsUniforms
            {
                Color = dc.Color
            };
            seriesBuffer.FsUniformBuffer.WriteSingle(fsUniforms);

            var activePipeline = isOffscreen ? _cachedPipelineOffscreen : _cachedPipeline;
            if (activePipeline == null)
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("ChartLineShader", ChartLineShaderCode, "ChartLine WGSL Shader");
                
                var pipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                    isOffscreen ? "ChartLine_Offscreen" : "ChartLine",
                    shaderModule,
                    "vs_main",
                    "fs_main",
                    compositor.RenderFormat,
                    PrimitiveTopology.TriangleList,
                    Array.Empty<VertexBufferLayout>(),
                    enableBlend: true,
                    sampleCount: isOffscreen ? 1u : 4u
                );

                if (isOffscreen)
                {
                    _cachedPipelineOffscreen = pipeline;
                    activePipeline = _cachedPipelineOffscreen;
                }
                else
                {
                    _cachedPipeline = pipeline;
                    activePipeline = _cachedPipeline;
                }
            }

            var activeBindGroup = isOffscreen ? seriesBuffer.LineBindGroupOffscreen : seriesBuffer.LineBindGroup;
            if (activeBindGroup == 0)
            {
                var bgl = wgpu.RenderPipelineGetBindGroupLayout(activePipeline, 0);

                var entries = stackalloc BindGroupEntry[3];
                entries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = seriesBuffer.VsUniformBuffer.BufferPtr,
                    Offset = 0,
                    Size = 96
                };
                entries[1] = new BindGroupEntry
                {
                    Binding = 1,
                    Buffer = seriesBuffer.FsUniformBuffer.BufferPtr,
                    Offset = 0,
                    Size = 16
                };
                entries[2] = new BindGroupEntry
                {
                    Binding = 2,
                    Buffer = seriesBuffer.Buffer.BufferPtr,
                    Offset = 0,
                    Size = seriesBuffer.Buffer.Size
                };

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = bgl,
                    EntryCount = 3,
                    Entries = entries,
                    Label = (byte*)SilkMarshal.StringToPtr("ChartLine BindGroup")
                };
                var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                SilkMarshal.Free((nint)bgDesc.Label);
                activeBindGroup = (nint)bg;
                if (isOffscreen)
                {
                    seriesBuffer.LineBindGroupOffscreen = activeBindGroup;
                }
                else
                {
                    seriesBuffer.LineBindGroup = activeBindGroup;
                }
            }

            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            
            var lineBg = (BindGroup*)activeBindGroup;
            wgpu.RenderPassEncoderSetBindGroup(pass, 0, lineBg, 0, null);

            uint instanceCount = (uint)(seriesBuffer.PointsCount - 1);
            wgpu.RenderPassEncoderDraw(pass, 6, instanceCount, 0, 0);
        }
    }
}
