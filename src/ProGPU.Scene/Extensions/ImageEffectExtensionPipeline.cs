using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public unsafe class ImageEffectExtensionPipeline : ICompositorExtension, IDisposable
    {
        private const string CrossContextTextureErrorPrefix =
            "Image effect texture belongs to a different WebGPU context";
        private const string UnbindableTextureErrorPrefix =
            "Image effect texture is no longer bindable";

        private static readonly string ShaderCode = ShaderResource.Load(typeof(ImageEffectExtensionPipeline), "ImageEffect.wgsl");

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct EffectUniforms
        {
            public Vector4 ColorMatrixRed;
            public Vector4 ColorMatrixGreen;
            public Vector4 ColorMatrixBlue;
            public Vector4 ColorMatrixAlpha;
            public Vector4 ColorMatrixOffset;
            public Vector4 Effects0;
            public Vector4 Effects1;
            public Vector4 Texture0;
            public Vector4 Flags0;
            public Vector4 YuvRange;
            public Vector4 YuvRed;
            public Vector4 YuvGreen;
            public Vector4 YuvBlue;
            public Vector4 Spherical0;
            public Vector4 SphericalUvRect;
            public Vector4 SphericalRotation0;
            public Vector4 SphericalRotation1;
            public Vector4 SphericalRotation2;
        }

        private struct EffectGpuResources
        {
            public GpuBuffer UniformBuffer;
            public nint BindGroupPtr; // BindGroup*
        }

        private sealed class LiveBlurResources : IDisposable
        {
            private const TextureUsage Usage =
                TextureUsage.TextureBinding |
                TextureUsage.RenderAttachment;

            public LiveBlurResources(
                GpuTexture source,
                bool isPlanar,
                int resourceIndex)
            {
                TextureFormat format = isPlanar
                    ? TextureFormat.Rgba16float
                    : source.Format;
                GpuTextureAlphaMode alphaMode =
                    isPlanar
                        ? GpuTextureAlphaMode.Straight
                        : source.AlphaMode;
                IsPlanar = isPlanar;
                Intermediate = new GpuTexture(
                    source.Context,
                    source.Width,
                    source.Height,
                    format,
                    Usage,
                    $"Live image blur intermediate {resourceIndex}",
                    alphaMode: alphaMode);
                try
                {
                    Output = new GpuTexture(
                        source.Context,
                        source.Width,
                        source.Height,
                        format,
                        Usage,
                        $"Live image blur output {resourceIndex}",
                        alphaMode: alphaMode);
                }
                catch
                {
                    Intermediate.Dispose();
                    throw;
                }
            }

            public GpuTexture Intermediate { get; }
            public GpuTexture Output { get; }
            public bool IsPlanar { get; }
            public ulong LastUsedFrame { get; set; }
            public ulong PreparedFrame { get; set; }
            public ulong PreparedSourceId { get; set; }
            public uint PreparedSourceGeneration { get; set; }
            public ulong PreparedChromaId { get; set; }
            public uint PreparedChromaGeneration { get; set; }
            public ImageEffectYuvConversion?
                PreparedYuvConversion { get; set; }
            public int PreparedSigmaBits { get; set; }

            public bool MatchesStorage(
                GpuTexture source,
                bool isPlanar)
            {
                return
                    IsPlanar == isPlanar &&
                    ReferenceEquals(
                        Intermediate.Context,
                        source.Context) &&
                    Intermediate.Width == source.Width &&
                    Intermediate.Height == source.Height &&
                    Intermediate.Format ==
                        (isPlanar
                            ? TextureFormat.Rgba16float
                            : source.Format) &&
                    Intermediate.AlphaMode ==
                        (isPlanar
                            ? GpuTextureAlphaMode.Straight
                            : source.AlphaMode) &&
                    !Intermediate.IsDisposed &&
                    !Output.IsDisposed;
            }

            public bool MatchesPrepared(
                GpuTexture source,
                GpuTexture? chroma,
                ImageEffectYuvConversion? yuvConversion,
                float standardDeviation,
                ulong frame)
            {
                return PreparedFrame == frame &&
                    PreparedSourceId == source.Id &&
                    PreparedSourceGeneration ==
                        source.Generation &&
                    PreparedChromaId ==
                        (chroma?.Id ?? 0) &&
                    PreparedChromaGeneration ==
                        (chroma?.Generation ?? 0) &&
                    YuvConversionsEqual(
                        PreparedYuvConversion,
                        yuvConversion) &&
                    PreparedSigmaBits ==
                        BitConverter.SingleToInt32Bits(
                            standardDeviation);
            }

            private static bool YuvConversionsEqual(
                ImageEffectYuvConversion? left,
                ImageEffectYuvConversion? right)
            {
                if (!left.HasValue ||
                    !right.HasValue)
                {
                    return left.HasValue ==
                        right.HasValue;
                }

                return left.Value.Range ==
                        right.Value.Range &&
                    left.Value.Red == right.Value.Red &&
                    left.Value.Green ==
                        right.Value.Green &&
                    left.Value.Blue ==
                        right.Value.Blue;
            }

            public void Dispose()
            {
                Output.Dispose();
                Intermediate.Dispose();
            }
        }

        private readonly Dictionary<(bool IsOffscreen, GpuTextureAlphaMode PipelineSourceAlphaMode, GpuBlendMode BlendMode), nint> _cachedPipelines = new();
        private WgpuContext? _contextRef;
        private BindGroupLayout* _effectBindGroupLayout;
        private BindGroupLayout* _textureBindGroupLayout;
        private PipelineLayout* _onscreenPipelineLayout;
        private PipelineLayout* _offscreenPipelineLayout;

        // Dynamic pool to recycle uniform buffers and bind groups without frame allocation
        private readonly List<EffectGpuResources> _pool = new();
        private int _usedCount;
        private readonly List<LiveBlurResources>
            _liveBlurPool = new();
        private int _usedLiveBlurCount;
        private int _preparedLiveBlurDrawCallCount;
        private int _liveBlurSubmissionCount;

        internal int LiveBlurResourceCount =>
            _liveBlurPool.Count;
        internal int PreparedLiveBlurDrawCallCount =>
            _preparedLiveBlurDrawCallCount;
        internal int LiveBlurSubmissionCount =>
            _liveBlurSubmissionCount;

        // Texture bind groups cache
        private readonly record struct TexturePairCacheKey(
            ulong LumaId,
            uint LumaViewGeneration,
            ulong ChromaId,
            uint ChromaViewGeneration,
            bool IsOffscreen,
            TextureSamplingMode SamplingMode);

        private readonly Dictionary<
            TexturePairCacheKey,
            Compositor.CachedBindGroup> _textureBindGroups = new();

        private static bool BlendModeRequiresPremultipliedSource(GpuBlendMode blendMode)
        {
            return blendMode is GpuBlendMode.DstOver or GpuBlendMode.Multiply or GpuBlendMode.Screen;
        }

        private static GpuTextureAlphaMode GetPipelineSourceAlphaMode(
            GpuTextureAlphaMode textureAlphaMode,
            GpuBlendMode blendMode)
        {
            return BlendModeRequiresPremultipliedSource(blendMode)
                ? GpuTextureAlphaMode.Premultiplied
                : textureAlphaMode;
        }

        private void EnsureLayouts(Compositor compositor)
        {
            if (_effectBindGroupLayout != null)
            {
                return;
            }

            _contextRef = compositor.Context;
            var wgpu = _contextRef.Api;
            var device = _contextRef.Device;

            var effectEntry = new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Buffer = new BufferBindingLayout
                {
                    Type = BufferBindingType.Uniform,
                    HasDynamicOffset = false,
                    MinBindingSize = 0
                }
            };
            var effectLayoutDesc = new BindGroupLayoutDescriptor
            {
                EntryCount = 1,
                Entries = &effectEntry
            };
            _effectBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &effectLayoutDesc);

            var textureEntries = stackalloc BindGroupLayoutEntry[3];
            textureEntries[0] = new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Sampler = new SamplerBindingLayout
                {
                    Type = SamplerBindingType.Filtering
                }
            };
            textureEntries[1] = new BindGroupLayoutEntry
            {
                Binding = 1,
                Visibility = ShaderStage.Fragment,
                Texture = new TextureBindingLayout
                {
                    SampleType = TextureSampleType.Float,
                    ViewDimension = TextureViewDimension.Dimension2D,
                    Multisampled = false
                }
            };
            textureEntries[2] = new BindGroupLayoutEntry
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Texture = new TextureBindingLayout
                {
                    SampleType = TextureSampleType.Float,
                    ViewDimension =
                        TextureViewDimension.Dimension2D,
                    Multisampled = false
                }
            };
            var textureLayoutDesc = new BindGroupLayoutDescriptor
            {
                EntryCount = 3,
                Entries = textureEntries
            };
            _textureBindGroupLayout = wgpu.DeviceCreateBindGroupLayout(device, &textureLayoutDesc);

            var onscreenLayouts = stackalloc BindGroupLayout*[4];
            onscreenLayouts[0] = compositor.VectorUniformBindGroupLayout;
            onscreenLayouts[1] = _effectBindGroupLayout;
            onscreenLayouts[2] = _textureBindGroupLayout;
            onscreenLayouts[3] = compositor.MaskBindGroupLayout;
            var onscreenDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 4,
                BindGroupLayouts = onscreenLayouts
            };
            _onscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &onscreenDesc);

            var offscreenLayouts = stackalloc BindGroupLayout*[4];
            offscreenLayouts[0] = compositor.VectorUniformBindGroupLayoutOffscreen;
            offscreenLayouts[1] = _effectBindGroupLayout;
            offscreenLayouts[2] = _textureBindGroupLayout;
            offscreenLayouts[3] = compositor.MaskBindGroupLayoutOffscreen;
            var offscreenDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 4,
                BindGroupLayouts = offscreenLayouts
            };
            _offscreenPipelineLayout = wgpu.DeviceCreatePipelineLayout(device, &offscreenDesc);
        }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            if (!TryGetCommandState(
                    in cmd,
                    out GpuTexture texture,
                    out Rect rect,
                    out Rect sourceRect,
                    out _,
                    out _,
                    out _))
            {
                return;
            }

            var r = rect;
            float opacity = compositor.ActiveOpacity;
            var color = new Vector4(1f, 1f, 1f, opacity);

            var v0 = Vector2.Transform(new Vector2(r.X, r.Y), transform);
            var v1 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y), transform);
            var v2 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), transform);
            var v3 = Vector2.Transform(new Vector2(r.X, r.Y + r.Height), transform);

            Vector2 uv0, uv1, uv2, uv3;
            if (sourceRect.Width > 0f && sourceRect.Height > 0f)
            {
                float texW = texture.Width;
                float texH = texture.Height;
                float l = sourceRect.X / texW;
                float t = sourceRect.Y / texH;
                float right = (sourceRect.X + sourceRect.Width) / texW;
                float b = (sourceRect.Y + sourceRect.Height) / texH;

                uv0 = new Vector2(l, t);
                uv1 = new Vector2(right, t);
                uv2 = new Vector2(right, b);
                uv3 = new Vector2(l, b);
            }
            else
            {
                uv0 = new Vector2(0f, 0f);
                uv1 = new Vector2(1f, 0f);
                uv2 = new Vector2(1f, 1f);
                uv3 = new Vector2(0f, 1f);
            }

            if (compositor.ActiveClipRect.HasValue &&
                !QuadClipper.TryClipAxisAlignedQuad(
                    compositor.ActiveClipRect.Value,
                    ref v0,
                    ref v1,
                    ref v2,
                    ref v3,
                    ref uv0,
                    ref uv1,
                    ref uv2,
                    ref uv3))
            {
                cmd.PointBufferOffset = compositor.VectorIndices.Count;
                cmd.PointBufferCount = 0;
                return;
            }

            int startIndex = compositor.VectorIndices.Count;

            int originalVertexCount = compositor.VectorVertices.Count;
            CollectionsMarshal.SetCount(compositor.VectorVertices, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(compositor.VectorVertices).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(v0, color, uv0);
            vertexSpan[1] = new VectorVertex(v1, color, uv1);
            vertexSpan[2] = new VectorVertex(v2, color, uv2);
            vertexSpan[3] = new VectorVertex(v3, color, uv3);

            int originalIndexCount = compositor.VectorIndices.Count;
            CollectionsMarshal.SetCount(compositor.VectorIndices, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(compositor.VectorIndices).Slice(originalIndexCount, 6);

            uint idxStart = (uint)originalVertexCount;
            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;

            int indexCount = compositor.VectorIndices.Count - startIndex;
            cmd.PointBufferOffset = startIndex;
            cmd.PointBufferCount = indexCount;
        }

        public void BeginFrame(Compositor compositor)
        {
            _usedCount = 0;
            _usedLiveBlurCount = 0;
            _preparedLiveBlurDrawCallCount = 0;
            _liveBlurSubmissionCount = 0;
        }

        public void EndFrame(Compositor compositor)
        {
            // Prune unused texture bind groups periodically
            ulong frame = compositor.FrameNumber;
            lock (_textureBindGroups)
            {
                TexturePairCacheKey[]? keysToRemove = null;
                int keysToRemoveCount = 0;
                try
                {
                    var textureBindGroupEnumerator = _textureBindGroups.GetEnumerator();
                    while (textureBindGroupEnumerator.MoveNext())
                    {
                        var kvp = textureBindGroupEnumerator.Current;
                        if (frame - kvp.Value.LastUsedFrame > 120)
                        {
                            if (kvp.Value.BindGroupPtr != 0 && !compositor.Context.IsDisposed)
                            {
                                QueueBindGroupRelease(compositor.Context, kvp.Value.BindGroupPtr);
                            }
                            PooledRemovalBuffer.Add(ref keysToRemove, ref keysToRemoveCount, _textureBindGroups.Count, kvp.Key);
                        }
                    }

                    for (int i = 0; i < keysToRemoveCount; i++)
                    {
                        _textureBindGroups.Remove(keysToRemove![i]);
                    }
                }
                finally
                {
                    PooledRemovalBuffer.Return(keysToRemove, keysToRemoveCount);
                }
            }

            for (int index = _liveBlurPool.Count - 1;
                 index >= _usedLiveBlurCount;
                 index--)
            {
                LiveBlurResources resources =
                    _liveBlurPool[index];
                if (frame - resources.LastUsedFrame <= 240)
                {
                    continue;
                }

                resources.Dispose();
                _liveBlurPool.RemoveAt(index);
            }
        }

        public bool TryPrepareDrawCall(
            Compositor compositor,
            bool isOffscreen,
            in Compositor.CompositorDrawCall drawCall,
            out Compositor.CompositorDrawCall preparedDrawCall)
        {
            preparedDrawCall = drawCall;
            if (!TryGetDrawCallState(
                    in drawCall,
                    out GpuTexture source,
                    out ImageEffectCommandData effect,
                    out _,
                    out ImageEffectParams? legacyParameters) ||
                !CanUseLiveBlurPrepass(
                    compositor.Context,
                    source,
                    in effect))
            {
                return false;
            }

            GpuTexture output = PrepareLiveBlur(
                compositor,
                source,
                effect.ChromaTexture,
                effect.YuvConversion,
                effect.BlurSigma);
            preparedDrawCall.Texture = output;
            preparedDrawCall.TextureAlphaMode =
                output.AlphaMode;
            preparedDrawCall.HasImageEffect = true;
            preparedDrawCall.ImageEffect =
                effect.WithRgbSourceWithoutBlur();
            _preparedLiveBlurDrawCallCount++;

            if (legacyParameters?.LastError?.StartsWith(
                    CrossContextTextureErrorPrefix,
                    StringComparison.Ordinal) == true)
            {
                legacyParameters.LastError = null;
            }

            return true;
        }

        private GpuTexture PrepareLiveBlur(
            Compositor compositor,
            GpuTexture source,
            GpuTexture? chroma,
            ImageEffectYuvConversion? yuvConversion,
            float standardDeviation)
        {
            ulong frame = compositor.FrameNumber;
            for (int index = 0;
                 index < _usedLiveBlurCount;
                 index++)
            {
                LiveBlurResources prepared =
                    _liveBlurPool[index];
                if (prepared.MatchesPrepared(
                        source,
                        chroma,
                        yuvConversion,
                        standardDeviation,
                        frame))
                {
                    prepared.LastUsedFrame = frame;
                    return prepared.Output;
                }
            }

            LiveBlurResources resources =
                AcquireLiveBlurResources(
                    source,
                    chroma is not null);
            if (chroma is not null &&
                yuvConversion.HasValue)
            {
                ImageEffectYuvConversion conversion =
                    yuvConversion.Value;
                var gpuConversion =
                    new GpuPlanarYuvConversion(
                        conversion.Range,
                        conversion.Red,
                        conversion.Green,
                        conversion.Blue);
                GpuTextureGaussianBlur.BlurPlanar(
                    source,
                    chroma,
                    resources.Intermediate,
                    resources.Output.ViewPtr,
                    resources.Output.Format,
                    standardDeviation,
                    in gpuConversion,
                    GpuTextureColorTransform.Identity);
            }
            else
            {
                GpuTextureGaussianBlur.Blur(
                    source,
                    resources.Intermediate,
                    resources.Output.ViewPtr,
                    resources.Output.Format,
                    standardDeviation,
                    GpuTextureColorTransform.Identity);
            }
            resources.Output.MarkContentsDirty();
            resources.LastUsedFrame = frame;
            resources.PreparedFrame = frame;
            resources.PreparedSourceId = source.Id;
            resources.PreparedSourceGeneration =
                source.Generation;
            resources.PreparedChromaId =
                chroma?.Id ?? 0;
            resources.PreparedChromaGeneration =
                chroma?.Generation ?? 0;
            resources.PreparedYuvConversion =
                yuvConversion;
            resources.PreparedSigmaBits =
                BitConverter.SingleToInt32Bits(
                    standardDeviation);
            _liveBlurSubmissionCount++;
            return resources.Output;
        }

        private LiveBlurResources AcquireLiveBlurResources(
            GpuTexture source,
            bool isPlanar)
        {
            for (int index = _usedLiveBlurCount;
                 index < _liveBlurPool.Count;
                 index++)
            {
                LiveBlurResources candidate =
                    _liveBlurPool[index];
                if (!candidate.MatchesStorage(
                        source,
                        isPlanar))
                {
                    continue;
                }

                if (index != _usedLiveBlurCount)
                {
                    LiveBlurResources displaced =
                        _liveBlurPool[_usedLiveBlurCount];
                    _liveBlurPool[_usedLiveBlurCount] =
                        candidate;
                    _liveBlurPool[index] = displaced;
                }

                return _liveBlurPool[
                    _usedLiveBlurCount++];
            }

            var created = new LiveBlurResources(
                source,
                isPlanar,
                _liveBlurPool.Count);
            _liveBlurPool.Add(created);
            int createdIndex = _liveBlurPool.Count - 1;
            if (createdIndex != _usedLiveBlurCount)
            {
                LiveBlurResources displaced =
                    _liveBlurPool[_usedLiveBlurCount];
                _liveBlurPool[_usedLiveBlurCount] =
                    created;
                _liveBlurPool[createdIndex] =
                    displaced;
            }

            return _liveBlurPool[_usedLiveBlurCount++];
        }

        private static bool CanUseLiveBlurPrepass(
            WgpuContext compositorContext,
            GpuTexture source,
            in ImageEffectCommandData effect)
        {
            if (!float.IsFinite(effect.BlurSigma) ||
                effect.BlurSigma <= 0.01f ||
                effect.BlurSigma >
                    GpuTextureGaussianBlur
                        .MaximumStandardDeviation ||
                !ValidateTextureContext(
                    compositorContext,
                    source,
                    "source",
                    out _) ||
                (source.Usage &
                    TextureUsage.TextureBinding) == 0 ||
                source.Dimension !=
                    GpuTextureDimension.Dimension2D ||
                source.DepthOrArrayLayers != 1 ||
                source.SampleCount != 1)
            {
                return false;
            }

            bool hasChroma =
                effect.ChromaTexture is not null;
            if (hasChroma !=
                effect.YuvConversion.HasValue)
            {
                return false;
            }

            if (!hasChroma)
            {
                return source.Format is
                    TextureFormat.Rgba8Unorm or
                    TextureFormat.Rgba8UnormSrgb or
                    TextureFormat.Bgra8Unorm or
                    TextureFormat.Bgra8UnormSrgb or
                    TextureFormat.Rgba16float;
            }

            GpuTexture chroma =
                effect.ChromaTexture!;
            bool supportedPlaneFormats =
                source.Format ==
                    TextureFormat.R8Unorm &&
                chroma.Format ==
                    TextureFormat.RG8Unorm ||
                compositorContext
                        .SupportsTextureFormatsTier1 &&
                    source.Format ==
                        ProGpuTextureFormats.R16Unorm &&
                    chroma.Format ==
                        ProGpuTextureFormats.RG16Unorm;
            return supportedPlaneFormats &&
                chroma.Width ==
                    (source.Width + 1) / 2 &&
                chroma.Height ==
                    (source.Height + 1) / 2 &&
                ValidateTextureContext(
                    compositorContext,
                    chroma,
                    "chroma",
                    out _) &&
                (chroma.Usage &
                    TextureUsage.TextureBinding) != 0 &&
                chroma.Dimension ==
                    GpuTextureDimension.Dimension2D &&
                chroma.DepthOrArrayLayers == 1 &&
                chroma.SampleCount == 1;
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            if (dc.PointBufferCount <= 0 ||
                !TryGetDrawCallState(
                    in dc,
                    out GpuTexture texture,
                    out ImageEffectCommandData effect,
                    out TextureSamplingMode samplingMode,
                    out ImageEffectParams? legacyParameters))
            {
                return;
            }

            EnsureLayouts(compositor);

            if (!ValidateTextureContext(compositor.Context, texture, "source", out var textureContextError)
                || (effect.ChromaTexture != null && !ValidateTextureContext(compositor.Context, effect.ChromaTexture, "chroma", out textureContextError))
                || (effect.MaskTexture != null && !ValidateTextureContext(compositor.Context, effect.MaskTexture, "mask", out textureContextError))
                || (dc.MaskTexture != null && !ValidateTextureContext(compositor.Context, dc.MaskTexture, "active mask", out textureContextError)))
            {
                if (legacyParameters != null)
                {
                    legacyParameters.LastError = textureContextError;
                }
                return;
            }

            if (legacyParameters?.LastError?.StartsWith(
                    CrossContextTextureErrorPrefix,
                    StringComparison.Ordinal) == true)
            {
                legacyParameters.LastError = null;
            }

            var wgpu = compositor.Context.Api;
            var device = compositor.Context.Device;
            var pass = (RenderPassEncoder*)renderPassEncoder;

            bool hasYuvConversion =
                effect.ChromaTexture is not null &&
                effect.YuvConversion.HasValue;
            var sourceAlphaMode = hasYuvConversion
                ? GpuTextureAlphaMode.Straight
                : texture.AlphaMode;
            var pipelineSourceAlphaMode = GetPipelineSourceAlphaMode(sourceAlphaMode, dc.BlendMode);
            var pipelineCacheKey = (isOffscreen, pipelineSourceAlphaMode, dc.BlendMode);
            if (!_cachedPipelines.TryGetValue(pipelineCacheKey, out var activePipelinePtr))
            {
                var shaderModule = compositor.PipelineCache.GetOrCreateShader("ImageEffectShader_v3", ShaderCode, "ImageEffect WGSL Shader");
                
                Span<VertexAttribute> attrs = stackalloc VertexAttribute[3];
                attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // Position
                attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }; // Color
                attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // TexCoord

                Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];
                fixed (VertexAttribute* attrsPtr = attrs)
                {
                    layouts[0] = new VertexBufferLayout
                    {
                        ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>(),
                        StepMode = VertexStepMode.Vertex,
                        AttributeCount = 3,
                        Attributes = attrsPtr
                    };

                    var pipeline = compositor.PipelineCache.GetOrCreateRenderPipeline(
                        isOffscreen
                            ? $"ImageEffectPipeline_v3_Offscreen_{pipelineSourceAlphaMode}_{dc.BlendMode}"
                            : $"ImageEffectPipeline_v3_{pipelineSourceAlphaMode}_{dc.BlendMode}",
                        shaderModule,
                        layouts,
                        topology: PrimitiveTopology.TriangleList,
                        targetFormat: compositor.RenderFormat,
                        sampleCount: isOffscreen ? 1u : compositor.Options.PrimarySampleCount,
                        pipelineLayout: isOffscreen ? _offscreenPipelineLayout : _onscreenPipelineLayout,
                        blendMode: dc.BlendMode,
                        sourceAlphaMode: pipelineSourceAlphaMode
                    );

                    activePipelinePtr = (nint)pipeline;
                    _cachedPipelines[pipelineCacheKey] = activePipelinePtr;
                }
            }

            var activePipeline = (RenderPipeline*)activePipelinePtr;

            // 1. Uniform parameters buffer management
            if (_usedCount >= _pool.Count)
            {
                var uniformSize = (uint)Unsafe.SizeOf<EffectUniforms>();
                var buf = new GpuBuffer(compositor.Context, uniformSize, BufferUsage.Uniform | BufferUsage.CopyDst, $"ImageEffect Uniforms {_pool.Count}");

                var bgEntries = stackalloc BindGroupEntry[1];
                bgEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = buf.BufferPtr,
                    Offset = 0,
                    Size = uniformSize
                };

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = _effectBindGroupLayout,
                    EntryCount = 1,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr($"ImageEffect Param BG {_pool.Count}")
                };

                var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                SilkMarshal.Free((nint)bgDesc.Label);

                _pool.Add(new EffectGpuResources { UniformBuffer = buf, BindGroupPtr = (nint)bg });
            }

            var gpuRes = _pool[_usedCount++];
            var effectiveMaskTexture = effect.MaskTexture ?? dc.MaskTexture;
            bool usesDrawCallMask = effect.MaskTexture == null;
            bool hasEffectiveMask = effectiveMaskTexture != null ||
                usesDrawCallMask && dc.MaskBindGroupOverride != 0;
            var maskCanvasWidth = effectiveMaskTexture?.Width ?? compositor.CurrentCanvasPixelWidth;
            var maskCanvasHeight = effectiveMaskTexture?.Height ?? compositor.CurrentCanvasPixelHeight;
            var colorMatrix = effect.ColorMatrix;
            ImageEffectYuvConversion? yuv =
                effect.YuvConversion;
            ImageEffectSphericalProjection? spherical =
                effect.SphericalProjection;
            Matrix4x4 sphericalRotation = spherical.HasValue
                ? Matrix4x4.CreateFromQuaternion(
                    spherical.Value.ViewOrientation)
                : Matrix4x4.Identity;
            gpuRes.UniformBuffer.WriteSingle(new EffectUniforms
            {
                ColorMatrixRed = colorMatrix?.Red ?? default,
                ColorMatrixGreen = colorMatrix?.Green ?? default,
                ColorMatrixBlue = colorMatrix?.Blue ?? default,
                ColorMatrixAlpha = colorMatrix?.Alpha ?? default,
                ColorMatrixOffset = colorMatrix?.Offset ?? default,
                Effects0 = new Vector4(
                    effect.Brightness,
                    effect.Contrast,
                    effect.Saturation,
                    effect.Grayscale),
                Effects1 = new Vector4(
                    effect.Sepia,
                    effect.Invert,
                    effect.BlurSigma,
                    hasEffectiveMask ? 1f : 0f),
                Texture0 = new Vector4(
                    MathF.Max(1f, maskCanvasWidth),
                    MathF.Max(1f, maskCanvasHeight),
                    sourceAlphaMode == GpuTextureAlphaMode.Premultiplied ? 1f : 0f,
                    pipelineSourceAlphaMode == GpuTextureAlphaMode.Premultiplied ? 1f : 0f),
                Flags0 = new Vector4(
                    hasYuvConversion ? 1f : 0f,
                    0f,
                    colorMatrix.HasValue ? 1f : 0f,
                    effect.LuminanceToAlpha ? 1f : 0f),
                YuvRange = yuv?.Range ?? default,
                YuvRed = yuv?.Red ?? default,
                YuvGreen = yuv?.Green ?? default,
                YuvBlue = yuv?.Blue ?? default,
                Spherical0 = spherical.HasValue
                    ? new Vector4(
                        1f,
                        spherical.Value
                            .HorizontalFieldOfViewRadians,
                        spherical.Value.OutputAspectRatio,
                        0f)
                    : default,
                SphericalUvRect =
                    spherical?.SourceUvRect ?? default,
                SphericalRotation0 = new Vector4(
                    sphericalRotation.M11,
                    sphericalRotation.M12,
                    sphericalRotation.M13,
                    0f),
                SphericalRotation1 = new Vector4(
                    sphericalRotation.M21,
                    sphericalRotation.M22,
                    sphericalRotation.M23,
                    0f),
                SphericalRotation2 = new Vector4(
                    sphericalRotation.M31,
                    sphericalRotation.M32,
                    sphericalRotation.M33,
                    0f)
            });

            // 2. Texture & Sampler BindGroup (Group 2)
            GpuTexture chromaTexture =
                effect.ChromaTexture ?? texture;
            var textureCacheKey = new TexturePairCacheKey(
                texture.Id,
                texture.ViewGeneration,
                chromaTexture.Id,
                chromaTexture.ViewGeneration,
                isOffscreen,
                samplingMode);
            Compositor.CachedBindGroup? cachedBg;
            lock (_textureBindGroups)
            {
                if (!_textureBindGroups.TryGetValue(textureCacheKey, out cachedBg))
                {
                    var textureEntries =
                        stackalloc BindGroupEntry[3];
                    textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = compositor.GetTextureSampler(samplingMode) };
                    textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = texture.ViewPtr };
                    textureEntries[2] = new BindGroupEntry
                    {
                        Binding = 2,
                        TextureView = chromaTexture.ViewPtr
                    };

                    var bgDesc = new BindGroupDescriptor
                    {
                        Layout = _textureBindGroupLayout,
                        EntryCount = 3,
                        Entries = textureEntries,
                        Label = (byte*)SilkMarshal.StringToPtr("ImageEffect Texture BG")
                    };

                    var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                    SilkMarshal.Free((nint)bgDesc.Label);
                    cachedBg = new Compositor.CachedBindGroup((nint)bg, compositor.FrameNumber);
                    _textureBindGroups[textureCacheKey] = cachedBg;
                }
                else
                {
                    cachedBg.LastUsedFrame = compositor.FrameNumber;
                }
            }

            // 3. Mask BindGroup (Group 3)
            var maskBg = usesDrawCallMask
                ? compositor.GetDrawCallMaskBindGroup(dc, isOffscreen)
                : compositor.GetMaskBindGroup(
                    effectiveMaskTexture,
                    isOffscreen);

            // 4. Set states & draw
            var vertexBuffer = compositor.VectorVertexBuffer.BufferPtr;
            wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, vertexBuffer, 0, compositor.VectorVertexBuffer.Size);
            wgpu.RenderPassEncoderSetIndexBuffer(pass, compositor.VectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, compositor.VectorIndexBuffer.Size);

            var group0 = isOffscreen ? compositor.VectorUniformBindGroupOffscreen : compositor.VectorUniformBindGroup;
            wgpu.RenderPassEncoderSetBindGroup(pass, 0, group0, 0, null);
            wgpu.RenderPassEncoderSetBindGroup(pass, 1, (BindGroup*)gpuRes.BindGroupPtr, 0, null);
            wgpu.RenderPassEncoderSetBindGroup(pass, 2, (BindGroup*)cachedBg.BindGroupPtr, 0, null);
            wgpu.RenderPassEncoderSetBindGroup(pass, 3, maskBg, 0, null);

            wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
            wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
        }

        private static bool TryGetCommandState(
            in RenderCommand command,
            out GpuTexture texture,
            out Rect rect,
            out Rect sourceRect,
            out ImageEffectCommandData effect,
            out TextureSamplingMode samplingMode,
            out ImageEffectParams? legacyParameters)
        {
            if (command.HasImageEffect && command.Texture != null)
            {
                texture = command.Texture;
                rect = command.Rect;
                sourceRect = command.SrcRect;
                effect = command.ImageEffect;
                samplingMode = command.TextureSamplingMode;
                legacyParameters = null;
                return true;
            }

            legacyParameters = command.DataParam as ImageEffectParams;
            if (legacyParameters != null)
            {
                texture = legacyParameters.Texture;
                rect = legacyParameters.Rect;
                sourceRect = legacyParameters.SourceRect;
                effect = ToCommandData(legacyParameters);
                samplingMode = legacyParameters.SamplingMode;
                return true;
            }

            texture = null!;
            rect = default;
            sourceRect = default;
            effect = default;
            samplingMode = default;
            return false;
        }

        private static bool TryGetDrawCallState(
            in Compositor.CompositorDrawCall drawCall,
            out GpuTexture texture,
            out ImageEffectCommandData effect,
            out TextureSamplingMode samplingMode,
            out ImageEffectParams? legacyParameters)
        {
            if (drawCall.HasImageEffect && drawCall.Texture != null)
            {
                texture = drawCall.Texture;
                effect = drawCall.ImageEffect;
                samplingMode = drawCall.TextureSamplingMode;
                legacyParameters = null;
                return true;
            }

            legacyParameters = drawCall.DataParam as ImageEffectParams;
            if (legacyParameters != null)
            {
                texture = legacyParameters.Texture;
                effect = ToCommandData(legacyParameters);
                samplingMode = legacyParameters.SamplingMode;
                return true;
            }

            texture = null!;
            effect = default;
            samplingMode = default;
            return false;
        }

        private static ImageEffectCommandData ToCommandData(
            ImageEffectParams parameters)
        {
            return new ImageEffectCommandData(
                parameters.Brightness,
                parameters.Contrast,
                parameters.Saturation,
                parameters.Grayscale,
                parameters.Sepia,
                parameters.Invert,
                parameters.BlurSigma,
                parameters.MaskTexture,
                parameters.ColorMatrix,
                parameters.LuminanceToAlpha);
        }

        private static bool ValidateTextureContext(
            WgpuContext targetContext,
            GpuTexture texture,
            string role,
            out string? error)
        {
            if (texture.IsDisposed ||
                texture.Context.IsDisposed ||
                texture.TexturePtr == null ||
                texture.ViewPtr == null)
            {
                error =
                    $"{UnbindableTextureErrorPrefix} for {role}.";
                return false;
            }
            if (!texture.Context.SharesDeviceWith(targetContext))
            {
                error = $"{CrossContextTextureErrorPrefix} for {role}. " +
                    "Create or copy the texture in the compositor target device domain before rendering the effect.";
                return false;
            }

            error = null;
            return true;
        }

        public void Dispose()
        {
            for (int index = 0;
                 index < _liveBlurPool.Count;
                 index++)
            {
                _liveBlurPool[index].Dispose();
            }

            if (_contextRef != null && !_contextRef.IsDisposed)
            {
                for (int i = 0; i < _pool.Count; i++)
                {
                    var resource = _pool[i];
                    if (resource.BindGroupPtr != 0)
                    {
                        QueueBindGroupRelease(_contextRef, resource.BindGroupPtr);
                    }

                    resource.UniformBuffer.Dispose();
                }

                var textureBindGroupValueEnumerator = _textureBindGroups.Values.GetEnumerator();
                while (textureBindGroupValueEnumerator.MoveNext())
                {
                    var cached = textureBindGroupValueEnumerator.Current;
                    if (cached.BindGroupPtr != 0)
                    {
                        QueueBindGroupRelease(_contextRef, cached.BindGroupPtr);
                    }
                }

                if (_effectBindGroupLayout != null)
                {
                    _contextRef.QueueBindGroupLayoutDisposal((IntPtr)_effectBindGroupLayout);
                    _effectBindGroupLayout = null;
                }

                if (_textureBindGroupLayout != null)
                {
                    _contextRef.QueueBindGroupLayoutDisposal((IntPtr)_textureBindGroupLayout);
                    _textureBindGroupLayout = null;
                }

                if (_onscreenPipelineLayout != null)
                {
                    _contextRef.QueuePipelineLayoutDisposal((IntPtr)_onscreenPipelineLayout);
                    _onscreenPipelineLayout = null;
                }

                if (_offscreenPipelineLayout != null)
                {
                    _contextRef.QueuePipelineLayoutDisposal((IntPtr)_offscreenPipelineLayout);
                    _offscreenPipelineLayout = null;
                }
            }

            _pool.Clear();
            _liveBlurPool.Clear();
            _textureBindGroups.Clear();
        }

        private static void QueueBindGroupRelease(WgpuContext context, nint bindGroupPtr)
        {
            if (bindGroupPtr != 0 && !context.IsDisposed)
            {
                context.QueueBindGroupDisposal((IntPtr)bindGroupPtr);
            }
        }
    }
}
