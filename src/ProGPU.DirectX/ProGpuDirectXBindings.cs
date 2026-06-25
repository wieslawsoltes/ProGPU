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

            return context.Wgpu.DeviceCreateBindGroup(context.Device, &bindGroupDesc);
        }
        finally
        {
            SilkMarshal.Free(bindGroupLabelPtr);
        }
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
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView is { Texture: { } texture } =>
                $"srv-texture:{texture.Label}:{texture.Generation}:{RuntimeHelpers.GetHashCode(entry.ShaderResourceView)}",
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

            layout = context.Wgpu.DeviceCreateBindGroupLayout(context.Device, &layoutDesc);
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

            bindGroup = context.Wgpu.DeviceCreateBindGroup(context.Device, &bindGroupDesc);
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
        foreach (var entry in entries)
        {
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
                    if (entry.ShaderResourceView is { Texture: not null, BackendTextureView: not null })
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
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView?.Texture is { } texture => new BindGroupLayoutEntry
            {
                Binding = entry.NativeBinding,
                Visibility = ToShaderStage(entry.Stage),
                Texture = new TextureBindingLayout
                {
                    SampleType = ToTextureSampleType(entry.ShaderResourceView.Format),
                    ViewDimension = texture.Descriptor.ArraySize > 1
                        ? TextureViewDimension.Dimension2DArray
                        : TextureViewDimension.Dimension2D,
                    Multisampled = texture.Descriptor.SampleCount > 1
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
            ProGpuDirectXBindingKind.UnorderedAccessView when entry.UnorderedAccessView?.Texture is { } texture => new BindGroupLayoutEntry
            {
                Binding = entry.NativeBinding,
                Visibility = ToShaderStage(entry.Stage),
                StorageTexture = new StorageTextureBindingLayout
                {
                    Access = ProGpuDirectXFormatConverter.ToStorageTextureAccess(entry.UnorderedAccessView.Descriptor.Access),
                    Format = ProGpuDirectXFormatConverter.ToTextureFormat(entry.UnorderedAccessView.Format),
                    ViewDimension = texture.Descriptor.ArraySize > 1
                        ? TextureViewDimension.Dimension2DArray
                        : TextureViewDimension.Dimension2D
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
            ProGpuDirectXBindingKind.ShaderResourceView when entry.ShaderResourceView?.Texture is not null => new BindGroupEntry
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
