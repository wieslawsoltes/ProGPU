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
        private static readonly string ChartLineShaderCode = ShaderResource.Load(typeof(GpuLineSeriesExtensionPipeline), "GpuLineSeries.wgsl");

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

            var wgpu = compositor.Context.Api;
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
                    sampleCount: isOffscreen ? 1u : compositor.Options.PrimarySampleCount
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
