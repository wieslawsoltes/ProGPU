using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Vector;
using Silk.NET.WebGPU;

namespace ProGPU.Scene;

public unsafe partial class Compositor
{
    private List<GpuHintedGlyphPaint>? _hintedGlyphPaints;
    private GpuBuffer? _hintedGlyphPaintBuffer;
    private GpuBuffer? _hintedGlyphPaintRetiringBuffer;
    private IncrementalBufferShadow? _hintedGlyphPaintUploadShadow;
    private WgpuBindGroupLayoutLease? _hintedGlyphPaintUniformLayoutLease;
    private WgpuPipelineLayoutLease? _hintedGlyphPaintPipelineLayoutLease;
    private BindGroupLayout* _hintedGlyphPaintUniformLayout;
    private PipelineLayout* _hintedGlyphPaintPipelineLayout;
    private BindGroup* _hintedGlyphPaintUniformBindGroup;
    private nint _hintedPaintBoundBrushBuffer, _hintedPaintBoundStopBuffer, _hintedPaintBoundPaintBuffer;
    private uint _hintedPaintBoundBrushSize, _hintedPaintBoundStopSize, _hintedPaintBoundPaintSize;
    internal ReadOnlySpan<GpuHintedGlyphPaint> HintedGlyphPaintRecords => _hintedGlyphPaints == null
        ? ReadOnlySpan<GpuHintedGlyphPaint>.Empty : CollectionsMarshal.AsSpan(_hintedGlyphPaints);

    private void EnsureHintedGlyphPaintLayouts()
    {
        if (_hintedGlyphPaintUniformLayout == null)
        {
            var entries = stackalloc BindGroupLayoutEntry[4];
            for (uint index = 0; index < 4; index++)
                entries[index] = new BindGroupLayoutEntry
                {
                    Binding = index,
                    Visibility = index == 2 ? ShaderStage.Fragment : ShaderStage.Vertex | ShaderStage.Fragment,
                    Buffer = new BufferBindingLayout
                    {
                        Type = index == 0 ? BufferBindingType.Uniform : BufferBindingType.ReadOnlyStorage,
                        MinBindingSize = 0, HasDynamicOffset = false
                    }
                };
            var descriptor = new BindGroupLayoutDescriptor { EntryCount = 4, Entries = entries };
            _hintedGlyphPaintUniformLayoutLease = _context.AcquireSharedBindGroupLayout(
                new WgpuDeviceResourceKey("ProGPU.Scene.HintedGlyphPaint", "Uniform"), &descriptor);
            _hintedGlyphPaintUniformLayout = _hintedGlyphPaintUniformLayoutLease.Handle;
        }
        if (_hintedGlyphPaintPipelineLayout == null)
        {
            var layouts = stackalloc BindGroupLayout*[4];
            layouts[0] = _hintedGlyphPaintUniformLayout;
            layouts[1] = _atlasBindGroupLayout;
            layouts[2] = _maskBindGroupLayout;
            layouts[3] = _textureBindGroupLayout;
            var descriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 4, BindGroupLayouts = layouts };
            _hintedGlyphPaintPipelineLayoutLease = _context.AcquireSharedPipelineLayout(
                new WgpuDeviceResourceKey("ProGPU.Scene.HintedGlyphPaint", "Pipeline"), &descriptor);
            _hintedGlyphPaintPipelineLayout = _hintedGlyphPaintPipelineLayoutLease.Handle;
        }
    }

    private nint CreateHintedGlyphPaintUniformBindGroup(GpuBuffer uniforms)
    {
        EnsureHintedGlyphPaintLayouts();
        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = uniforms.BufferPtr, Size = (uint)Unsafe.SizeOf<GpuUniforms>() };
        entries[1] = new BindGroupEntry { Binding = 1, Buffer = _brushesStorageBuffer.BufferPtr, Size = _brushesStorageBuffer.Size };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = _gradientStopsStorageBuffer.BufferPtr, Size = _gradientStopsStorageBuffer.Size };
        entries[3] = new BindGroupEntry { Binding = 3, Buffer = _hintedGlyphPaintBuffer!.BufferPtr, Size = _hintedGlyphPaintBuffer.Size };
        var descriptor = new BindGroupDescriptor { Layout = _hintedGlyphPaintUniformLayout, EntryCount = 4, Entries = entries };
        var group = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
        if (group == null) throw new InvalidOperationException("Failed to create original hinted paint scene-state bindings.");
        return (nint)group;
    }

    private void UploadHintedGlyphPaints()
    {
        if (_hintedGlyphPaints is not { Count: > 0 }) return;
        if (_hintedGlyphPaintRetiringBuffer != null)
        {
            _hintedGlyphPaintRetiringBuffer.Dispose();
            _hintedGlyphPaintRetiringBuffer = null;
        }
        ulong requiredBytes = checked((ulong)_hintedGlyphPaints.Count * (uint)Unsafe.SizeOf<GpuHintedGlyphPaint>());
        if (_hintedGlyphPaintBuffer == null)
            _hintedGlyphPaintBuffer = new GpuBuffer(_context,
                CalculateBufferGrowth(0, requiredBytes, _context.MaxBufferSize, "Original hinted paints"),
                BufferUsage.Storage | BufferUsage.CopyDst, "Original hinted paints");
        else if ((ulong)_hintedGlyphPaintBuffer.Size < requiredBytes)
        {
            var replacement = new GpuBuffer(_context,
                CalculateBufferGrowth(_hintedGlyphPaintBuffer.Size, requiredBytes, _context.MaxBufferSize, "Original hinted paints"),
                BufferUsage.Storage | BufferUsage.CopyDst, "Original hinted paints");
            _hintedGlyphPaintRetiringBuffer = _hintedGlyphPaintBuffer;
            _hintedGlyphPaintBuffer = replacement;
            // Failed queued retirement retains BOTH actual owners for retry.
            _hintedGlyphPaintRetiringBuffer.Dispose();
            _hintedGlyphPaintRetiringBuffer = null;
        }

        if (_hintedPaintBoundBrushBuffer != (nint)_brushesStorageBuffer.BufferPtr ||
            _hintedPaintBoundBrushSize != _brushesStorageBuffer.Size ||
            _hintedPaintBoundStopBuffer != (nint)_gradientStopsStorageBuffer.BufferPtr ||
            _hintedPaintBoundStopSize != _gradientStopsStorageBuffer.Size ||
            _hintedPaintBoundPaintBuffer != (nint)_hintedGlyphPaintBuffer.BufferPtr ||
            _hintedPaintBoundPaintSize != _hintedGlyphPaintBuffer.Size)
        {
            BindGroup* replacement = (BindGroup*)CreateHintedGlyphPaintUniformBindGroup(_uniformBuffer);
            try
            {
                if (_hintedGlyphPaintUniformBindGroup != null)
                {
                    QueueBindGroupRelease((nint)_hintedGlyphPaintUniformBindGroup);
                    _hintedGlyphPaintUniformBindGroup = null;
                }
                foreach (MaskRenderResource resource in _maskRenderResources.Values)
                {
                    QueueBindGroupRelease(resource.HintedGlyphPaintUniformBindGroupPtr);
                    resource.HintedGlyphPaintUniformBindGroupPtr = 0;
                }
                foreach (AdvancedBlendPassResource resource in _advancedBlendPassResources)
                {
                    QueueBindGroupRelease(resource.HintedGlyphPaintUniformBindGroupPtr);
                    resource.HintedGlyphPaintUniformBindGroupPtr = 0;
                }
            }
            catch (Exception failure)
            {
                // The new group has never been encoded; original failed queue
                // owners remain in their fields, never orphaned or nulled early.
                try { _context.Api.BindGroupRelease(replacement); }
                catch (Exception cleanup)
                {
                    try { failure.Data["HintedGlyphPaintSetupCleanupFailure"] = cleanup; }
                    catch { }
                }
                throw;
            }
            _hintedGlyphPaintUniformBindGroup = replacement;
            _hintedPaintBoundBrushBuffer = (nint)_brushesStorageBuffer.BufferPtr;
            _hintedPaintBoundBrushSize = _brushesStorageBuffer.Size;
            _hintedPaintBoundStopBuffer = (nint)_gradientStopsStorageBuffer.BufferPtr;
            _hintedPaintBoundStopSize = _gradientStopsStorageBuffer.Size;
            _hintedPaintBoundPaintBuffer = (nint)_hintedGlyphPaintBuffer.BufferPtr;
            _hintedPaintBoundPaintSize = _hintedGlyphPaintBuffer.Size;
        }
        UploadIncrementalSceneBuffer(_hintedGlyphPaintBuffer,
            CollectionsMarshal.AsSpan(_hintedGlyphPaints), ref _hintedGlyphPaintUploadShadow);
    }

    private RenderPipeline* CreateHintedGlyphPaintPipeline(GpuBlendMode blendMode,
        bool isOffscreen, TextureFormat? overrideFormat, GpuTextureAlphaMode alphaMode, bool hasMask)
    {
        EnsureHintedGlyphPaintLayouts();
        var module = _pipelineCache.GetOrCreateShader("HintedGlyphPaint", Shaders.HintedGlyphPaintShader,
            "Original hinted glyph paint shader");
        Span<VertexAttribute> attributes = stackalloc VertexAttribute[9];
        attributes[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
        attributes[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 };
        attributes[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 16, ShaderLocation = 2 };
        attributes[3] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 24, ShaderLocation = 3 };
        attributes[4] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 40, ShaderLocation = 4 };
        attributes[5] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 56, ShaderLocation = 5 };
        attributes[6] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 72, ShaderLocation = 6 };
        attributes[7] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 88, ShaderLocation = 7 };
        attributes[8] = new VertexAttribute { Format = VertexFormat.Uint32, Offset = 92, ShaderLocation = 8 };
        bool writesMask = overrideFormat == TextureFormat.R8Unorm;
        bool premultipliedOutput = alphaMode == GpuTextureAlphaMode.Premultiplied || BlendModeRequiresPremultipliedSource(blendMode);
        string entry = writesMask ? "fs_mask" : premultipliedOutput ? "fs_main_premultiplied" : "fs_main";
        if (!hasMask) entry += "_unmasked";
        fixed (VertexAttribute* attributePointer = attributes)
        {
            Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];
            layouts[0] = new VertexBufferLayout
            {
                ArrayStride = (uint)Unsafe.SizeOf<GlyphInstance>(), StepMode = VertexStepMode.Instance,
                AttributeCount = 9, Attributes = attributePointer
            };
            return _pipelineCache.GetOrCreateRenderPipeline(
                $"HintedGlyphPaint_{blendMode}_{(isOffscreen ? 1 : Options.PrimarySampleCount)}_{overrideFormat ?? RenderFormat}_{alphaMode}_{entry}",
                module, layouts, "vs_main", entry, overrideFormat ?? RenderFormat, PrimitiveTopology.TriangleList,
                enableBlend: true, enableDepthStencil: false, sampleCount: isOffscreen ? 1u : Options.PrimarySampleCount,
                blendMode: blendMode, pipelineLayout: _hintedGlyphPaintPipelineLayout,
                sourceAlphaMode: GetPipelineSourceAlphaMode(DrawCallType.HintedGlyphPaint, blendMode, alphaMode, writesMask));
        }
    }

    private void EncodeHintedGlyphPaint(RenderPassEncoder* pass, in CompositorDrawCall drawCall,
        bool isOffscreen, BindGroup* uniforms, BindGroup* mask, TextureFormat? overrideFormat = null,
        GpuBlendMode? blendOverride = null)
    {
        if (drawCall.Texture != null && !IsTextureBindable(drawCall.Texture))
            throw new ObjectDisposedException(nameof(GpuTexture), "The original hinted paint texture has retired.");
        var pipeline = GetPipeline(DrawCallType.HintedGlyphPaint, blendOverride ?? drawCall.BlendMode,
            isOffscreen, overrideFormat, drawCall.TextureAlphaMode, HasMask(drawCall));
        _context.Api.RenderPassEncoderSetPipeline(pass, pipeline);
        _context.Api.RenderPassEncoderSetBindGroup(pass, 0, uniforms, 0, null);
        _context.Api.RenderPassEncoderSetBindGroup(pass, 1, _atlasBindGroup, 0, null);
        _context.Api.RenderPassEncoderSetBindGroup(pass, 2, mask, 0, null);
        BindGroup* paintTextureGroup;
        if (drawCall.Texture is GpuTexture texture)
            paintTextureGroup = (BindGroup*)GetOrCreatePersistentTextureBindGroup(texture, isOffscreen,
                drawCall.TextureSamplingMode, drawCall.TextureMaxAnisotropy,
                drawCall.TextureAddressModeU, drawCall.TextureAddressModeV, _textureBindGroupLayout).BindGroupPtr;
        else
            paintTextureGroup = (BindGroup*)GetOrCreatePersistentTextureBindGroup(_dummyMaskTexture!,
                isOffscreen, TextureSamplingMode.Linear, 1, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                _textureBindGroupLayout).BindGroupPtr;
        _context.Api.RenderPassEncoderSetBindGroup(pass, 3, paintTextureGroup, 0, null);
        _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, _textVertexBuffer.BufferPtr,
            (ulong)drawCall.IndexStart * GlyphInstanceStride, (ulong)drawCall.IndexCount * GlyphInstanceStride);
        uint paintIndex = BitConverter.SingleToUInt32Bits(_textVerticesList[checked((int)drawCall.IndexStart)].Padding);
        uint vertexCount = _hintedGlyphPaints![checked((int)paintIndex)].VertexCount;
        _context.Api.RenderPassEncoderDraw(pass, vertexCount, drawCall.IndexCount, 0, 0);
    }

    private bool TryGetHintedGlyphPaintSourceBounds(in CompositorDrawCall drawCall,
        uint targetWidth, uint targetHeight, out MaskPixelBounds bounds)
    {
        bounds = default;
        if (drawCall.IndexCount == 0 ||
            (ulong)drawCall.IndexStart + drawCall.IndexCount > (ulong)_textVerticesList.Count)
            return false;
        Vector2 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        var instances = CollectionsMarshal.AsSpan(_textVerticesList).Slice(
            checked((int)drawCall.IndexStart), checked((int)drawCall.IndexCount));
        foreach (GlyphInstance instance in instances)
        {
            Vector2 offset = new Vector2(instance.BearSize.X, instance.BearSize.Y) / _currentDpiScale;
            Vector2 extent = new Vector2(instance.BearSize.Z, instance.BearSize.W) / _currentDpiScale;
            minimum = Vector2.Min(minimum, instance.SnappedLogicalPos + offset);
            maximum = Vector2.Max(maximum, instance.SnappedLogicalPos + (offset + extent));
        }
        if (!float.IsFinite(minimum.X) || !float.IsFinite(minimum.Y) ||
            !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y)) return false;
        return TryGetAdvancedBlendBoundsFromExtents(drawCall, targetWidth, targetHeight,
            minimum.X, minimum.Y, maximum.X, maximum.Y, out bounds);
    }

    private void DisposeHintedGlyphPaintResources()
    {
        QueueBindGroupRelease((nint)_hintedGlyphPaintUniformBindGroup);
        _hintedGlyphPaintUniformBindGroup = null;
        _hintedGlyphPaintBuffer?.Dispose();
        _hintedGlyphPaintBuffer = null;
        _hintedGlyphPaintRetiringBuffer?.Dispose();
        _hintedGlyphPaintRetiringBuffer = null;
        _hintedGlyphPaintPipelineLayoutLease?.Dispose();
        _hintedGlyphPaintUniformLayoutLease?.Dispose();
        _hintedGlyphPaintUploadShadow = null;
        _hintedGlyphPaints?.Clear();
    }
}
