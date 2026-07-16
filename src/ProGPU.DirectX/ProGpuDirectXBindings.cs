using System.Runtime.CompilerServices;
using ProGPU.Backend;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.DirectX;

public enum ProGpuDirectXBindingKind
{
    ConstantBuffer,
    ShaderResourceView,
    Sampler,
    UnorderedAccessView
}

public sealed record ProGpuDirectXBindingEntry
{
    public required ProGpuDirectXBindingKind Kind { get; init; }
    public required DxShaderStage Stage { get; init; }
    public required uint Slot { get; init; }
    public uint NativeBinding { get; init; }
    public ProGpuDirectXBuffer? ConstantBuffer { get; init; }
    public ProGpuDirectXShaderResourceView? ShaderResourceView { get; init; }
    public ProGpuDirectXSamplerState? Sampler { get; init; }
    public ProGpuDirectXUnorderedAccessView? UnorderedAccessView { get; init; }
}

public enum ProGpuDirectXBindingValidationIssueKind
{
    UnsupportedReflectedRequirements,
    MissingBinding
}

public sealed record ProGpuDirectXBindingValidationIssue(
    ProGpuDirectXBindingValidationIssueKind IssueKind,
    string Message,
    string? ResourceName = null,
    DxShaderStage? Stage = null,
    ProGpuDirectXBindingKind? Kind = null,
    uint? Slot = null,
    uint? NativeBinding = null);

public sealed record ProGpuDirectXBindingValidationResult(IReadOnlyList<ProGpuDirectXBindingValidationIssue> Issues)
{
    public static ProGpuDirectXBindingValidationResult Success { get; } =
        new(Array.Empty<ProGpuDirectXBindingValidationIssue>());

    public bool IsValid => Issues.Count == 0;

    public string ToExceptionMessage()
    {
        return IsValid
            ? "DirectX binding validation succeeded."
            : string.Join(Environment.NewLine, Issues.Select(issue => issue.Message));
    }
}

public sealed unsafe class ProGpuDirectXBindingSnapshot : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private readonly IntPtr _backendBindGroupLayout;
    private readonly IntPtr _backendBindGroup;
    private bool _isDisposed;

    internal ProGpuDirectXBindingSnapshot(
        ProGpuDirectXDevice device,
        DxShaderStageFlags stageMask,
        IReadOnlyList<ProGpuDirectXBindingEntry> entries,
        string label,
        bool createStandaloneBackendBindGroup)
    {
        _device = device;
        StageMask = stageMask;
        Entries = entries;
        Label = label;
        BindingKey = BuildBindingKey(entries);
        BackendBindingKey = BuildBackendBindingKey(entries);

        if (createStandaloneBackendBindGroup &&
            entries.Count > 0 &&
            device.Context is { } context &&
            device.IsGpuBacked &&
            TryCreateBackendBindGroup(context, entries, label, out var layout, out var bindGroup))
        {
            _backendBindGroupLayout = (IntPtr)layout;
            _backendBindGroup = (IntPtr)bindGroup;
        }
    }

    public DxShaderStageFlags StageMask { get; }

    public IReadOnlyList<ProGpuDirectXBindingEntry> Entries { get; }

    public string Label { get; }

    public string BindingKey { get; }

    internal string BackendBindingKey { get; }

    public bool HasBackendBindGroup => _backendBindGroup != IntPtr.Zero;

    public IntPtr BackendBindGroupLayoutHandle => _backendBindGroupLayout;

    public IntPtr BackendBindGroupHandle => _backendBindGroup;

    internal BindGroupLayout* BackendBindGroupLayout => (BindGroupLayout*)_backendBindGroupLayout;

    internal BindGroup* BackendBindGroup => (BindGroup*)_backendBindGroup;

    internal BindGroup* CreateBackendBindGroupFromLayout(
        WgpuContext context,
        BindGroupLayout* layout,
        string label)
    {
        if (layout == null || !CanCreateBackendBindGroup(Entries))
        {
            return null;
        }

        var bindGroupEntries = stackalloc BindGroupEntry[Entries.Count];
        for (var i = 0; i < Entries.Count; i++)
        {
            bindGroupEntries[i] = CreateBindGroupEntry(Entries[i]);
        }

        var bindGroupLabelPtr = SilkMarshal.StringToPtr(label);
        try
        {
            var bindGroupDesc = new BindGroupDescriptor
            {
                Label = (byte*)bindGroupLabelPtr,
                Layout = layout,
                EntryCount = (uint)Entries.Count,
                Entries = bindGroupEntries
            };

            return context.Api.DeviceCreateBindGroup(context.Device, &bindGroupDesc);
        }
        finally
        {
            SilkMarshal.Free(bindGroupLabelPtr);
        }
    }

    internal string DescribeEntries()
    {
        if (Entries.Count == 0)
        {
            return "no entries";
        }

        return string.Join(", ", Entries.Select(entry =>
            $"{entry.Kind} {entry.Stage}[{entry.Slot}] native={entry.NativeBinding} resource={GetBackendResourceToken(entry)}"));
    }

    private static string BuildBindingKey(IReadOnlyList<ProGpuDirectXBindingEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "empty";
        }

        return string.Join(
            "|",
            entries.Select(entry =>
                $"{entry.Kind}:{entry.Stage}:{entry.Slot}:{entry.NativeBinding}:{GetResourceToken(entry)}"));
    }

    private static string GetResourceToken(ProGpuDirectXBindingEntry entry)
    {
        return entry.Kind switch
        {
            ProGpuDirectXBindingKind.ConstantBuffer when entry.ConstantBuffer is { } buffer =>
                $"buffer:{buffer.Label}:{buffer.Descriptor.SizeInBytes}:{RuntimeHelpers.GetHashCode(buffer)}",
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView is { IsTextureView: true } view =>
                $"srv-texture:{view.TextureLabel}:{view.TextureGeneration}:{RuntimeHelpers.GetHashCode(entry.ShaderResourceView)}",
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView is { Buffer: { } buffer } =>
                $"srv-buffer:{buffer.Label}:{RuntimeHelpers.GetHashCode(entry.ShaderResourceView)}",
            ProGpuDirectXBindingKind.Sampler when entry.Sampler is { } sampler =>
                $"sampler:{sampler.Descriptor.Filter}:{sampler.Descriptor.AddressU}:{RuntimeHelpers.GetHashCode(sampler)}",
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView is { Texture: { } texture } =>
                $"uav-texture:{texture.Label}:{texture.Generation}:{RuntimeHelpers.GetHashCode(entry.UnorderedAccessView)}",
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView is { Buffer: { } buffer } =>
                $"uav-buffer:{buffer.Label}:{RuntimeHelpers.GetHashCode(entry.UnorderedAccessView)}",
            _ => "null"
        };
    }

    private static string BuildBackendBindingKey(IReadOnlyList<ProGpuDirectXBindingEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "empty";
        }

        return string.Join(
            "|",
            entries.Select(entry =>
                $"{entry.Kind}:{entry.Stage}:{entry.Slot}:{entry.NativeBinding}:{GetBackendResourceToken(entry)}"));
    }

    private static string GetBackendResourceToken(ProGpuDirectXBindingEntry entry)
    {
        return entry.Kind switch
        {
            ProGpuDirectXBindingKind.ConstantBuffer when entry.ConstantBuffer?.BackendBuffer is { } buffer =>
                $"buffer:{(IntPtr)buffer.BufferPtr}:{buffer.Size}",
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView is { IsTextureView: true } view =>
                $"srv-texture:{view.BackendTextureViewHandle}:{view.Dimension}:{view.Format}:{view.Descriptor.MostDetailedMip}:{view.Descriptor.MipLevels}:{view.Descriptor.FirstArraySlice}:{view.Descriptor.ArraySize}",
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView is { Buffer.BackendBuffer: { } buffer } view =>
                $"srv-buffer:{(IntPtr)buffer.BufferPtr}:{view.Dimension}:{view.Format}:{view.Descriptor.FirstElement}:{view.Descriptor.ElementCount}:{view.Descriptor.ElementStrideInBytes}",
            ProGpuDirectXBindingKind.Sampler when entry.Sampler is { } sampler =>
                $"sampler:{sampler.BackendSamplerHandle}:{sampler.Descriptor}",
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView is { Texture: not null } view =>
                $"uav-texture:{view.BackendTextureViewHandle}:{view.Dimension}:{view.Format}:{view.Descriptor.Access}:{view.Descriptor.MipSlice}:{view.Descriptor.FirstArraySlice}:{view.Descriptor.ArraySize}",
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView is { Buffer.BackendBuffer: { } buffer } view =>
                $"uav-buffer:{(IntPtr)buffer.BufferPtr}:{view.Dimension}:{view.Format}:{view.Descriptor.Access}:{view.Descriptor.FirstElement}:{view.Descriptor.ElementCount}:{view.Descriptor.ElementStrideInBytes}",
            _ => "null"
        };
    }

    private static bool TryCreateBackendBindGroup(
        WgpuContext context,
        IReadOnlyList<ProGpuDirectXBindingEntry> entries,
        string label,
        out BindGroupLayout* layout,
        out BindGroup* bindGroup)
    {
        layout = null;
        bindGroup = null;

        if (!CanCreateBackendBindGroup(entries))
        {
            return false;
        }

        var layoutEntries = stackalloc BindGroupLayoutEntry[entries.Count];
        var bindGroupEntries = stackalloc BindGroupEntry[entries.Count];

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            layoutEntries[i] = CreateLayoutEntry(entry);
            bindGroupEntries[i] = CreateBindGroupEntry(entry);
        }

        var layoutLabelPtr = SilkMarshal.StringToPtr($"{label} Layout");
        var bindGroupLabelPtr = SilkMarshal.StringToPtr(label);
        try
        {
            var layoutDesc = new BindGroupLayoutDescriptor
            {
                Label = (byte*)layoutLabelPtr,
                EntryCount = (uint)entries.Count,
                Entries = layoutEntries
            };

            layout = context.Api.DeviceCreateBindGroupLayout(context.Device, &layoutDesc);
            if (layout == null)
            {
                return false;
            }

            var bindGroupDesc = new BindGroupDescriptor
            {
                Label = (byte*)bindGroupLabelPtr,
                Layout = layout,
                EntryCount = (uint)entries.Count,
                Entries = bindGroupEntries
            };

            bindGroup = context.Api.DeviceCreateBindGroup(context.Device, &bindGroupDesc);
            if (bindGroup != null)
            {
                return true;
            }

            context.QueueBindGroupLayoutDisposal((IntPtr)layout);
            layout = null;
            return false;
        }
        finally
        {
            SilkMarshal.Free(layoutLabelPtr);
            SilkMarshal.Free(bindGroupLabelPtr);
        }
    }

    private static bool CanCreateBackendBindGroup(IReadOnlyList<ProGpuDirectXBindingEntry> entries)
    {
        var entryCount = entries.Count;
        for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            var entry = entries[entryIndex];
            if (entry.Stage == DxShaderStage.Geometry)
            {
                return false;
            }

            switch (entry.Kind)
            {
                case ProGpuDirectXBindingKind.ConstantBuffer:
                    if (entry.ConstantBuffer?.BackendBuffer?.BufferPtr == null)
                    {
                        return false;
                    }
                    break;
                case ProGpuDirectXBindingKind.ShaderResourceView:
                    if (entry.ShaderResourceView is { IsTextureView: true, BackendTextureView: not null })
                    {
                        break;
                    }

                    if (entry.ShaderResourceView is { Buffer.BackendBuffer.BufferPtr: not null })
                    {
                        break;
                    }

                    return false;
                case ProGpuDirectXBindingKind.Sampler:
                    if (entry.Sampler?.BackendSampler == null)
                    {
                        return false;
                    }
                    break;
                case ProGpuDirectXBindingKind.UnorderedAccessView:
                    if (entry.UnorderedAccessView is { Texture: not null, BackendTextureView: not null } textureView)
                    {
                        if (RequiresReadWriteStorageTextureFeature(textureView.Descriptor.Access) &&
                            !textureView.Device.Capabilities.SupportsReadWriteStorageTextures)
                        {
                            return false;
                        }

                        break;
                    }

                    if (entry.UnorderedAccessView is { Buffer.BackendBuffer.BufferPtr: not null })
                    {
                        break;
                    }

                    return false;
                default:
                    return false;
            }
        }

        return true;
    }

    private static BindGroupLayoutEntry CreateLayoutEntry(ProGpuDirectXBindingEntry entry)
    {
        return entry.Kind switch
        {
            ProGpuDirectXBindingKind.ConstantBuffer => new BindGroupLayoutEntry
            {
                Binding = entry.NativeBinding,
                Visibility = ToShaderStage(entry.Stage),
                Buffer = new BufferBindingLayout
                {
                    Type = BufferBindingType.Uniform,
                    HasDynamicOffset = false,
                    MinBindingSize = 0
                }
            },
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView is { IsTextureView: true } view => new BindGroupLayoutEntry
            {
                Binding = entry.NativeBinding,
                Visibility = ToShaderStage(entry.Stage),
                Texture = new TextureBindingLayout
                {
                    SampleType = ToTextureSampleType(entry.ShaderResourceView.Format),
                    ViewDimension = ToTextureViewDimension(entry.ShaderResourceView.Dimension),
                    Multisampled = view.TextureSampleCount > 1
                }
            },
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView?.Buffer is not null => new BindGroupLayoutEntry
            {
                Binding = entry.NativeBinding,
                Visibility = ToShaderStage(entry.Stage),
                Buffer = new BufferBindingLayout
                {
                    Type = BufferBindingType.ReadOnlyStorage,
                    HasDynamicOffset = false,
                    MinBindingSize = 0
                }
            },
            ProGpuDirectXBindingKind.Sampler => new BindGroupLayoutEntry
            {
                Binding = entry.NativeBinding,
                Visibility = ToShaderStage(entry.Stage),
                Sampler = new SamplerBindingLayout
                {
                    Type = entry.Sampler?.Descriptor.ComparisonFunction.HasValue == true
                        ? SamplerBindingType.Comparison
                        : SamplerBindingType.Filtering
                }
            },
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView?.Texture is not null => new BindGroupLayoutEntry
            {
                Binding = entry.NativeBinding,
                Visibility = ToShaderStage(entry.Stage),
                StorageTexture = new StorageTextureBindingLayout
                {
                    Access = ProGpuDirectXFormatConverter.ToStorageTextureAccess(entry.UnorderedAccessView.Descriptor.Access),
                    Format = ProGpuDirectXFormatConverter.ToTextureFormat(entry.UnorderedAccessView.Format),
                    ViewDimension = ToTextureViewDimension(entry.UnorderedAccessView.Dimension)
                }
            },
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView?.Buffer is not null => new BindGroupLayoutEntry
            {
                Binding = entry.NativeBinding,
                Visibility = ToShaderStage(entry.Stage),
                Buffer = new BufferBindingLayout
                {
                    Type = BufferBindingType.Storage,
                    HasDynamicOffset = false,
                    MinBindingSize = 0
                }
            },
            _ => throw new InvalidOperationException($"Unsupported DirectX binding entry '{entry.Kind}'.")
        };
    }

    private static bool RequiresReadWriteStorageTextureFeature(DxUnorderedAccessViewAccess access)
    {
        return access is DxUnorderedAccessViewAccess.ReadOnly or DxUnorderedAccessViewAccess.ReadWrite;
    }

    private static BindGroupEntry CreateBindGroupEntry(ProGpuDirectXBindingEntry entry)
    {
        return entry.Kind switch
        {
            ProGpuDirectXBindingKind.ConstantBuffer when entry.ConstantBuffer?.BackendBuffer is { } buffer => new BindGroupEntry
            {
                Binding = entry.NativeBinding,
                Buffer = buffer.BufferPtr,
                Offset = 0,
                Size = buffer.Size
            },
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView is { IsTextureView: true } => new BindGroupEntry
            {
                Binding = entry.NativeBinding,
                TextureView = entry.ShaderResourceView.BackendTextureView
            },
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView is { Buffer.BackendBuffer: { } buffer } view => new BindGroupEntry
            {
                Binding = entry.NativeBinding,
                Buffer = buffer.BufferPtr,
                Offset = GetBufferViewOffset(view.Descriptor.FirstElement, view.Descriptor.ElementStrideInBytes, view.Buffer.Descriptor.StrideInBytes),
                Size = GetBufferViewSize(view.Descriptor.ElementCount, view.Descriptor.ElementStrideInBytes, view.Buffer.Descriptor.StrideInBytes, buffer.Size)
            },
            ProGpuDirectXBindingKind.Sampler when entry.Sampler is { } sampler => new BindGroupEntry
            {
                Binding = entry.NativeBinding,
                Sampler = sampler.BackendSampler
            },
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView?.Texture is not null => new BindGroupEntry
            {
                Binding = entry.NativeBinding,
                TextureView = entry.UnorderedAccessView.BackendTextureView
            },
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView is { Buffer.BackendBuffer: { } buffer } view => new BindGroupEntry
            {
                Binding = entry.NativeBinding,
                Buffer = buffer.BufferPtr,
                Offset = GetBufferViewOffset(view.Descriptor.FirstElement, view.Descriptor.ElementStrideInBytes, view.Buffer.Descriptor.StrideInBytes),
                Size = GetBufferViewSize(view.Descriptor.ElementCount, view.Descriptor.ElementStrideInBytes, view.Buffer.Descriptor.StrideInBytes, buffer.Size)
            },
            _ => throw new InvalidOperationException($"Unsupported DirectX binding entry '{entry.Kind}'.")
        };
    }

    private static ulong GetBufferViewOffset(uint firstElement, uint elementStride, uint fallbackStride)
    {
        var stride = Math.Max(1u, elementStride == 0 ? fallbackStride : elementStride);
        return firstElement * (ulong)stride;
    }

    private static ulong GetBufferViewSize(uint elementCount, uint elementStride, uint fallbackStride, uint fallbackSize)
    {
        var stride = elementStride == 0 ? fallbackStride : elementStride;
        return stride == 0
            ? fallbackSize
            : elementCount * (ulong)stride;
    }

    private static ShaderStage ToShaderStage(DxShaderStage stage)
    {
        return stage switch
        {
            DxShaderStage.Vertex => ShaderStage.Vertex,
            DxShaderStage.Pixel => ShaderStage.Fragment,
            DxShaderStage.Compute => ShaderStage.Compute,
            _ => ShaderStage.None
        };
    }

    private static TextureSampleType ToTextureSampleType(DxResourceFormat format)
    {
        return format is DxResourceFormat.D24UnormS8UInt or DxResourceFormat.D32Float
            ? TextureSampleType.Depth
            : TextureSampleType.Float;
    }

    private static TextureViewDimension ToTextureViewDimension(DxResourceViewDimension dimension)
    {
        return dimension switch
        {
            DxResourceViewDimension.Texture2DArray => TextureViewDimension.Dimension2DArray,
            DxResourceViewDimension.Texture3D => TextureViewDimension.Dimension3D,
            _ => TextureViewDimension.Dimension2D
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_device.Context is { IsDisposed: false } context)
        {
            context.QueueBindGroupDisposal(_backendBindGroup);
            context.QueueBindGroupLayoutDisposal(_backendBindGroupLayout);
        }

        _isDisposed = true;
    }
}
