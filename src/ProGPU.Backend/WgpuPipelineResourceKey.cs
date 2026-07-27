using System;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Immutable semantic identity for a render pipeline. Pointer-bearing vertex
/// descriptors are copied into value records at cache acquisition time.
/// </summary>
internal unsafe sealed class WgpuRenderPipelineResourceKey :
    IEquatable<WgpuRenderPipelineResourceKey>
{
    private readonly VertexBufferLayoutKey[] _vertexBuffers;
    private readonly int _hashCode;

    private WgpuRenderPipelineResourceKey(
        string logicalKey,
        nint shaderModule,
        string vertexEntry,
        string fragmentEntry,
        TextureFormat targetFormat,
        PrimitiveTopology topology,
        VertexBufferLayoutKey[] vertexBuffers,
        bool enableBlend,
        bool enableDepthStencil,
        TextureFormat depthFormat,
        CompareFunction stencilCompare,
        StencilOperation stencilFail,
        StencilOperation stencilDepthFail,
        StencilOperation stencilPass,
        uint sampleCount,
        bool depthWriteEnabled,
        CompareFunction depthCompare,
        CullMode cullMode,
        GpuBlendMode blendMode,
        nint pipelineLayout,
        GpuTextureAlphaMode sourceAlphaMode)
    {
        LogicalKey = logicalKey;
        ShaderModule = shaderModule;
        VertexEntry = vertexEntry;
        FragmentEntry = fragmentEntry;
        TargetFormat = targetFormat;
        Topology = topology;
        _vertexBuffers = vertexBuffers;
        EnableBlend = enableBlend;
        EnableDepthStencil = enableDepthStencil;
        DepthFormat = depthFormat;
        StencilCompare = stencilCompare;
        StencilFail = stencilFail;
        StencilDepthFail = stencilDepthFail;
        StencilPass = stencilPass;
        SampleCount = sampleCount;
        DepthWriteEnabled = depthWriteEnabled;
        DepthCompare = depthCompare;
        CullMode = cullMode;
        BlendMode = blendMode;
        PipelineLayout = pipelineLayout;
        SourceAlphaMode = sourceAlphaMode;
        _hashCode = CalculateHashCode();
    }

    public string LogicalKey { get; }
    public nint ShaderModule { get; }
    public string VertexEntry { get; }
    public string FragmentEntry { get; }
    public TextureFormat TargetFormat { get; }
    public PrimitiveTopology Topology { get; }
    public bool EnableBlend { get; }
    public bool EnableDepthStencil { get; }
    public TextureFormat DepthFormat { get; }
    public CompareFunction StencilCompare { get; }
    public StencilOperation StencilFail { get; }
    public StencilOperation StencilDepthFail { get; }
    public StencilOperation StencilPass { get; }
    public uint SampleCount { get; }
    public bool DepthWriteEnabled { get; }
    public CompareFunction DepthCompare { get; }
    public CullMode CullMode { get; }
    public GpuBlendMode BlendMode { get; }
    public nint PipelineLayout { get; }
    public GpuTextureAlphaMode SourceAlphaMode { get; }

    public static WgpuRenderPipelineResourceKey Create(
        string logicalKey,
        ShaderModule* shaderModule,
        string vertexEntry,
        string fragmentEntry,
        TextureFormat targetFormat,
        PrimitiveTopology topology,
        ReadOnlySpan<VertexBufferLayout> vertexBufferLayouts,
        bool enableBlend,
        bool enableDepthStencil,
        TextureFormat depthFormat,
        CompareFunction stencilCompare,
        StencilOperation stencilFail,
        StencilOperation stencilDepthFail,
        StencilOperation stencilPass,
        uint sampleCount,
        bool depthWriteEnabled,
        CompareFunction depthCompare,
        CullMode cullMode,
        GpuBlendMode blendMode,
        PipelineLayout* pipelineLayout,
        GpuTextureAlphaMode sourceAlphaMode)
    {
        var vertexBuffers =
            new VertexBufferLayoutKey[vertexBufferLayouts.Length];
        for (var index = 0; index < vertexBufferLayouts.Length; index++)
        {
            vertexBuffers[index] =
                VertexBufferLayoutKey.Create(
                    vertexBufferLayouts[index]);
        }

        return new WgpuRenderPipelineResourceKey(
            logicalKey,
            (nint)shaderModule,
            vertexEntry,
            fragmentEntry,
            targetFormat,
            topology,
            vertexBuffers,
            enableBlend,
            enableDepthStencil,
            depthFormat,
            stencilCompare,
            stencilFail,
            stencilDepthFail,
            stencilPass,
            sampleCount,
            depthWriteEnabled,
            depthCompare,
            cullMode,
            blendMode,
            (nint)pipelineLayout,
            sourceAlphaMode);
    }

    public bool Equals(WgpuRenderPipelineResourceKey? other)
    {
        if (other is null ||
            _hashCode != other._hashCode ||
            ShaderModule != other.ShaderModule ||
            TargetFormat != other.TargetFormat ||
            Topology != other.Topology ||
            EnableBlend != other.EnableBlend ||
            EnableDepthStencil != other.EnableDepthStencil ||
            DepthFormat != other.DepthFormat ||
            StencilCompare != other.StencilCompare ||
            StencilFail != other.StencilFail ||
            StencilDepthFail != other.StencilDepthFail ||
            StencilPass != other.StencilPass ||
            SampleCount != other.SampleCount ||
            DepthWriteEnabled != other.DepthWriteEnabled ||
            DepthCompare != other.DepthCompare ||
            CullMode != other.CullMode ||
            BlendMode != other.BlendMode ||
            PipelineLayout != other.PipelineLayout ||
            SourceAlphaMode != other.SourceAlphaMode ||
            !string.Equals(
                LogicalKey,
                other.LogicalKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                VertexEntry,
                other.VertexEntry,
                StringComparison.Ordinal) ||
            !string.Equals(
                FragmentEntry,
                other.FragmentEntry,
                StringComparison.Ordinal) ||
            _vertexBuffers.Length != other._vertexBuffers.Length)
        {
            return false;
        }

        for (var index = 0; index < _vertexBuffers.Length; index++)
        {
            if (!_vertexBuffers[index].Equals(
                    other._vertexBuffers[index]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Validates a caller descriptor against this retained key without
    /// materializing another copied vertex-layout graph. This keeps repeated
    /// local cache hits allocation-free while still rejecting logical-key
    /// collisions.
    /// </summary>
    public bool Matches(
        string logicalKey,
        ShaderModule* shaderModule,
        string vertexEntry,
        string fragmentEntry,
        TextureFormat targetFormat,
        PrimitiveTopology topology,
        ReadOnlySpan<VertexBufferLayout> vertexBufferLayouts,
        bool enableBlend,
        bool enableDepthStencil,
        TextureFormat depthFormat,
        CompareFunction stencilCompare,
        StencilOperation stencilFail,
        StencilOperation stencilDepthFail,
        StencilOperation stencilPass,
        uint sampleCount,
        bool depthWriteEnabled,
        CompareFunction depthCompare,
        CullMode cullMode,
        GpuBlendMode blendMode,
        PipelineLayout* pipelineLayout,
        GpuTextureAlphaMode sourceAlphaMode)
    {
        if (ShaderModule != (nint)shaderModule ||
            TargetFormat != targetFormat ||
            Topology != topology ||
            EnableBlend != enableBlend ||
            EnableDepthStencil != enableDepthStencil ||
            DepthFormat != depthFormat ||
            StencilCompare != stencilCompare ||
            StencilFail != stencilFail ||
            StencilDepthFail != stencilDepthFail ||
            StencilPass != stencilPass ||
            SampleCount != sampleCount ||
            DepthWriteEnabled != depthWriteEnabled ||
            DepthCompare != depthCompare ||
            CullMode != cullMode ||
            BlendMode != blendMode ||
            PipelineLayout != (nint)pipelineLayout ||
            SourceAlphaMode != sourceAlphaMode ||
            !string.Equals(LogicalKey, logicalKey, StringComparison.Ordinal) ||
            !string.Equals(VertexEntry, vertexEntry, StringComparison.Ordinal) ||
            !string.Equals(FragmentEntry, fragmentEntry, StringComparison.Ordinal) ||
            _vertexBuffers.Length != vertexBufferLayouts.Length)
        {
            return false;
        }

        for (var index = 0; index < _vertexBuffers.Length; index++)
        {
            if (!_vertexBuffers[index].Matches(vertexBufferLayouts[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
        => obj is WgpuRenderPipelineResourceKey other &&
           Equals(other);

    public override int GetHashCode() => _hashCode;

    private int CalculateHashCode()
    {
        var hash = new HashCode();
        hash.Add(LogicalKey, StringComparer.Ordinal);
        hash.Add(ShaderModule);
        hash.Add(VertexEntry, StringComparer.Ordinal);
        hash.Add(FragmentEntry, StringComparer.Ordinal);
        hash.Add(TargetFormat);
        hash.Add(Topology);
        hash.Add(EnableBlend);
        hash.Add(EnableDepthStencil);
        hash.Add(DepthFormat);
        hash.Add(StencilCompare);
        hash.Add(StencilFail);
        hash.Add(StencilDepthFail);
        hash.Add(StencilPass);
        hash.Add(SampleCount);
        hash.Add(DepthWriteEnabled);
        hash.Add(DepthCompare);
        hash.Add(CullMode);
        hash.Add(BlendMode);
        hash.Add(PipelineLayout);
        hash.Add(SourceAlphaMode);
        foreach (VertexBufferLayoutKey vertexBuffer in _vertexBuffers)
        {
            hash.Add(vertexBuffer);
        }
        return hash.ToHashCode();
    }

    private sealed class VertexBufferLayoutKey(
        ulong arrayStride,
        VertexStepMode stepMode,
        VertexAttributeKey[] attributes) :
        IEquatable<VertexBufferLayoutKey>
    {
        private readonly ulong _arrayStride = arrayStride;
        private readonly VertexStepMode _stepMode = stepMode;
        private readonly VertexAttributeKey[] _attributes = attributes;

        public static VertexBufferLayoutKey Create(
            VertexBufferLayout layout)
        {
            int count = checked((int)layout.AttributeCount);
            var attributes = new VertexAttributeKey[count];
            for (var index = 0; index < count; index++)
            {
                VertexAttribute attribute =
                    layout.Attributes[index];
                attributes[index] = new VertexAttributeKey(
                    attribute.Format,
                    attribute.Offset,
                    attribute.ShaderLocation);
            }
            return new VertexBufferLayoutKey(
                layout.ArrayStride,
                layout.StepMode,
                attributes);
        }

        public bool Equals(VertexBufferLayoutKey? other)
            => other is not null &&
               _arrayStride == other._arrayStride &&
               _stepMode == other._stepMode &&
               _attributes.AsSpan().SequenceEqual(
                   other._attributes);

        public bool Matches(VertexBufferLayout layout)
        {
            if (_arrayStride != layout.ArrayStride ||
                _stepMode != layout.StepMode ||
                _attributes.Length != checked((int)layout.AttributeCount))
            {
                return false;
            }

            for (var index = 0; index < _attributes.Length; index++)
            {
                VertexAttribute attribute = layout.Attributes[index];
                VertexAttributeKey retained = _attributes[index];
                if (retained.Format != attribute.Format ||
                    retained.Offset != attribute.Offset ||
                    retained.ShaderLocation != attribute.ShaderLocation)
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
            => obj is VertexBufferLayoutKey other &&
               Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_arrayStride);
            hash.Add(_stepMode);
            foreach (VertexAttributeKey attribute in _attributes)
            {
                hash.Add(attribute);
            }
            return hash.ToHashCode();
        }
    }

    private readonly record struct VertexAttributeKey(
        VertexFormat Format,
        ulong Offset,
        uint ShaderLocation);
}

internal readonly record struct WgpuComputePipelineResourceKey(
    string LogicalKey,
    nint ShaderModule,
    string EntryPoint,
    nint PipelineLayout);
