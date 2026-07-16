using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public class GpuScatterSeriesExtensionPipeline : ICompositorExtension
    {
        private static readonly string ChartScatterShaderCode = ShaderResource.Load(typeof(GpuScatterSeriesExtensionPipeline), "GpuScatterSeries.wgsl");

        [StructLayout(LayoutKind.Sequential)]
        private struct ScatterVsUniforms
        {
            public Matrix4x4 Transform;
            public Vector2 ViewportPx;
            public Vector2 Pad0;
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

                    int requiredLength = pointsCount * 3;
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
                            bool isOriginally2D = floatsSpan.Length == pointsCount * 2;
                            if (isOriginally2D)
                            {
                                float radiusVal = cmd.RadiusX;
                                if (cachedBuffer.CachedInterleaved[2] != radiusVal)
                                {
                                    needsUpload = true;
                                }
                                else
                                {
                                    for (int i = 0; i < pointsCount; i++)
                                    {
                                        if (floatsSpan[i * 2] != cachedBuffer.CachedInterleaved[i * 3] ||
                                            floatsSpan[i * 2 + 1] != cachedBuffer.CachedInterleaved[i * 3 + 1])
                                        {
                                            needsUpload = true;
                                            break;
                                        }
                                    }
                                }
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
                    }

                    if (needsUpload)
                    {
                        if (cachedBuffer.CachedInterleaved == null || cachedBuffer.CachedInterleaved.Length < requiredLength)
                        {
                            cachedBuffer.CachedInterleaved = new float[requiredLength];
                        }

                        bool isOriginally2D = floatsSpan.Length == pointsCount * 2;
                        if (isOriginally2D)
                        {
                            float radiusVal = cmd.RadiusX;
                            int srcIdx = 0;
                            int destIdx = 0;
                            for (int i = 0; i < pointsCount; i++)
                            {
                                cachedBuffer.CachedInterleaved[destIdx++] = floatsSpan[srcIdx++];
                                cachedBuffer.CachedInterleaved[destIdx++] = floatsSpan[srcIdx++];
                                cachedBuffer.CachedInterleaved[destIdx++] = radiusVal;
                            }
                        }
                        else
                        {
                            floatsSpan.Slice(0, requiredLength).CopyTo(cachedBuffer.CachedInterleaved);
                        }

                        cachedBuffer.Upload(cachedBuffer.CachedInterleaved.AsSpan(0, requiredLength), pointsCount);
                    }
                    staticBuffer = cachedBuffer;
                }
                else if (!floatsSpan.IsEmpty)
                {
                    // Uncached fallback path: upload direct
                    var tempBuffer = new GpuSeriesBuffer();
                    bool isOriginally2D = floatsSpan.Length == pointsCount * 2;
                    if (isOriginally2D)
                    {
                        var array = ArrayPool<float>.Shared.Rent(pointsCount * 3);
                        float radiusVal = cmd.RadiusX;
                        int srcIdx = 0;
                        int destIdx = 0;
                        try
                        {
                            for (int i = 0; i < pointsCount; i++)
                            {
                                array[destIdx++] = floatsSpan[srcIdx++];
                                array[destIdx++] = floatsSpan[srcIdx++];
                                array[destIdx++] = radiusVal;
                            }

                            tempBuffer.Upload(array.AsSpan(0, pointsCount * 3), pointsCount);
                        }
                        finally
                        {
                            ArrayPool<float>.Shared.Return(array);
                        }
                    }
                    else
                    {
                        tempBuffer.Upload(floatsSpan.Slice(0, pointsCount * 3), pointsCount);
                    }
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
            if (dc.StaticBuffer is not GpuSeriesBuffer seriesBuffer || seriesBuffer.Buffer == null || seriesBuffer.PointsCount < 1) return;

            var wgpu = compositor.Context.Api;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            if (seriesBuffer.VsUniformBuffer == null)
            {
                seriesBuffer.VsUniformBuffer = new GpuBuffer(compositor.Context, 96, BufferUsage.Uniform | BufferUsage.CopyDst, "ChartScatter VS Uniforms");
            }
            if (seriesBuffer.FsUniformBuffer == null)
            {
                seriesBuffer.FsUniformBuffer = new GpuBuffer(compositor.Context, 16, BufferUsage.Uniform | BufferUsage.CopyDst, "ChartScatter FS Uniforms");
            }

            var vsUniforms = new ScatterVsUniforms
            {
                Transform = dc.Transform * compositor.CurrentProjection,
                ViewportPx = new Vector2(compositor.CurrentWidth, compositor.CurrentHeight),
                Pad0 = Vector2.Zero,
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
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("ChartScatterShader", ChartScatterShaderCode, "ChartScatter WGSL Shader");

                Span<VertexAttribute> attrs = stackalloc VertexAttribute[2];
                attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // center
                attrs[1] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 8, ShaderLocation = 1 }; // radiusPx
                fixed (VertexAttribute* attrsPtr = attrs)
                {
                    Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];
                    layouts[0] = new VertexBufferLayout
                    {
                        ArrayStride = (uint)Unsafe.SizeOf<Vector3>(),
                        StepMode = VertexStepMode.Instance,
                        AttributeCount = 2,
                        Attributes = attrsPtr
                    };

                    var pipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                        isOffscreen ? "ChartScatter_Offscreen" : "ChartScatter",
                        shaderModule,
                        layouts,
                        "vs_main",
                        "fs_main",
                        compositor.RenderFormat,
                        PrimitiveTopology.TriangleList,
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
            }

            var activeBindGroup = isOffscreen ? seriesBuffer.ScatterBindGroupOffscreen : seriesBuffer.ScatterBindGroup;
            if (activeBindGroup == 0)
            {
                var bgl = wgpu.RenderPipelineGetBindGroupLayout(activePipeline, 0);
                
                var entries = stackalloc BindGroupEntry[2];
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
                
                var bgDesc = new BindGroupDescriptor
                {
                    Layout = bgl,
                    EntryCount = 2,
                    Entries = entries,
                    Label = (byte*)SilkMarshal.StringToPtr("ChartScatter BindGroup")
                };
                var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                SilkMarshal.Free((nint)bgDesc.Label);
                activeBindGroup = (nint)bg;
                if (isOffscreen)
                {
                    seriesBuffer.ScatterBindGroupOffscreen = activeBindGroup;
                }
                else
                {
                    seriesBuffer.ScatterBindGroup = activeBindGroup;
                }
            }
            
            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            
            var scatterBg = (BindGroup*)activeBindGroup;
            wgpu.RenderPassEncoderSetBindGroup(pass, 0, scatterBg, 0, null);

            var buffer = seriesBuffer.Buffer.BufferPtr;
            wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, seriesBuffer.Buffer.Size);

            uint instanceCount = (uint)seriesBuffer.PointsCount;
            wgpu.RenderPassEncoderDraw(pass, 6, instanceCount, 0, 0);
        }
    }
}
