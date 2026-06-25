using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.DirectX;

public abstract unsafe class ProGpuDirectXView : IDisposable
{
    private readonly IntPtr _backendTextureView;
    private readonly bool _ownsTextureView;
    private bool _isDisposed;

    protected ProGpuDirectXView(
        ProGpuDirectXDevice device,
        ProGpuDirectXTexture2D? texture,
        ProGpuDirectXBuffer? buffer,
        DxResourceViewDimension dimension,
        DxResourceFormat format,
        string label,
        uint baseMipLevel,
        uint mipLevelCount,
        uint baseArrayLayer,
        uint arrayLayerCount,
        bool createTextureView)
    {
        Device = device;
        Texture = texture;
        Buffer = buffer;
        Dimension = dimension;
        Format = format;
        Label = label;

        if (createTextureView &&
            texture?.BackendTexture is { IsDisposed: false, TexturePtr: not null } backendTexture)
        {
            _backendTextureView = (IntPtr)CreateTextureView(
                backendTexture.TexturePtr,
                texture,
                format,
                label,
                baseMipLevel,
                mipLevelCount,
                baseArrayLayer,
                arrayLayerCount);
            _ownsTextureView = true;
        }
        else if (texture?.BackendTexture is { IsDisposed: false, ViewPtr: not null } defaultTexture)
        {
            _backendTextureView = (IntPtr)defaultTexture.ViewPtr;
        }
    }

    public ProGpuDirectXDevice Device { get; }

    public ProGpuDirectXTexture2D? Texture { get; }

    public ProGpuDirectXBuffer? Buffer { get; }

    public DxResourceViewDimension Dimension { get; }

    public DxResourceFormat Format { get; }

    public string Label { get; }

    public bool HasBackendTextureView => _backendTextureView != IntPtr.Zero;

    public IntPtr BackendTextureViewHandle => _backendTextureView;

    internal TextureView* BackendTextureView => (TextureView*)_backendTextureView;

    private static TextureView* CreateTextureView(
        Texture* texture,
        ProGpuDirectXTexture2D source,
        DxResourceFormat format,
        string label,
        uint baseMipLevel,
        uint mipLevelCount,
        uint baseArrayLayer,
        uint arrayLayerCount)
    {
        var viewFormat = format == DxResourceFormat.Unknown ? source.Descriptor.Format : format;
        var labelPtr = SilkMarshal.StringToPtr(label);
        try
        {
            var viewDesc = new TextureViewDescriptor
            {
                Label = (byte*)labelPtr,
                Format = ProGpuDirectXFormatConverter.ToTextureFormat(viewFormat),
                Dimension = TextureViewDimension.Dimension2D,
                BaseMipLevel = baseMipLevel,
                MipLevelCount = mipLevelCount,
                BaseArrayLayer = baseArrayLayer,
                ArrayLayerCount = arrayLayerCount,
                Aspect = TextureAspect.All
            };

            var view = source.Device.Context!.Wgpu.TextureCreateView(texture, &viewDesc);
            if (view == null)
            {
                throw new InvalidOperationException($"Failed to create DirectX texture view '{label}'.");
            }

            return view;
        }
        finally
        {
            SilkMarshal.Free(labelPtr);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_ownsTextureView &&
            _backendTextureView != IntPtr.Zero &&
            Device.Context is { IsDisposed: false } context)
        {
            context.QueueTextureViewDisposal(_backendTextureView);
        }

        _isDisposed = true;
    }
}

public sealed class ProGpuDirectXShaderResourceView : ProGpuDirectXView
{
    internal ProGpuDirectXShaderResourceView(
        ProGpuDirectXDevice device,
        ProGpuDirectXTexture2D texture,
        DxShaderResourceViewDescriptor descriptor)
        : base(
            device,
            texture,
            null,
            descriptor.Dimension,
            descriptor.Format == DxResourceFormat.Unknown ? texture.Descriptor.Format : descriptor.Format,
            descriptor.Label,
            descriptor.MostDetailedMip,
            descriptor.MipLevels,
            descriptor.FirstArraySlice,
            descriptor.ArraySize,
            createTextureView: true)
    {
        ValidateTextureDescriptor(texture, descriptor);
        Descriptor = descriptor;
    }

    internal ProGpuDirectXShaderResourceView(
        ProGpuDirectXDevice device,
        ProGpuDirectXBuffer buffer,
        DxShaderResourceViewDescriptor descriptor)
        : base(
            device,
            null,
            buffer,
            DxResourceViewDimension.Buffer,
            descriptor.Format,
            descriptor.Label,
            0,
            1,
            0,
            1,
            createTextureView: false)
    {
        ValidateBufferDescriptor(buffer, descriptor);
        Descriptor = descriptor with { Dimension = DxResourceViewDimension.Buffer };
    }

    public DxShaderResourceViewDescriptor Descriptor { get; }

    private static void ValidateTextureDescriptor(
        ProGpuDirectXTexture2D texture,
        DxShaderResourceViewDescriptor descriptor)
    {
        if ((texture.Descriptor.Usage & DxTextureUsage.ShaderResource) == 0)
        {
            throw new ArgumentException("Texture was not created with shader-resource usage.", nameof(texture));
        }

        if (descriptor.MipLevels == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Shader-resource views must expose at least one mip level.");
        }

        if (descriptor.MostDetailedMip >= texture.Descriptor.MipLevels ||
            descriptor.MipLevels > texture.Descriptor.MipLevels - descriptor.MostDetailedMip)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Shader-resource view mip range exceeds the texture.");
        }

        if (descriptor.FirstArraySlice >= texture.Descriptor.ArraySize ||
            descriptor.ArraySize == 0 ||
            descriptor.ArraySize > texture.Descriptor.ArraySize - descriptor.FirstArraySlice)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Shader-resource view array range exceeds the texture.");
        }
    }

    private static void ValidateBufferDescriptor(
        ProGpuDirectXBuffer buffer,
        DxShaderResourceViewDescriptor descriptor)
    {
        if ((buffer.Descriptor.Usage & DxBufferUsage.ShaderResource) == 0)
        {
            throw new ArgumentException("Buffer was not created with shader-resource usage.", nameof(buffer));
        }

        if (descriptor.ElementCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Buffer shader-resource views must expose at least one element.");
        }
    }
}

public sealed class ProGpuDirectXUnorderedAccessView : ProGpuDirectXView
{
    internal ProGpuDirectXUnorderedAccessView(
        ProGpuDirectXDevice device,
        ProGpuDirectXTexture2D texture,
        DxUnorderedAccessViewDescriptor descriptor)
        : base(
            device,
            texture,
            null,
            descriptor.Dimension,
            descriptor.Format == DxResourceFormat.Unknown ? texture.Descriptor.Format : descriptor.Format,
            descriptor.Label,
            descriptor.MipSlice,
            1,
            descriptor.FirstArraySlice,
            descriptor.ArraySize,
            createTextureView: true)
    {
        ValidateTextureDescriptor(texture, descriptor);
        Descriptor = descriptor;
    }

    internal ProGpuDirectXUnorderedAccessView(
        ProGpuDirectXDevice device,
        ProGpuDirectXBuffer buffer,
        DxUnorderedAccessViewDescriptor descriptor)
        : base(
            device,
            null,
            buffer,
            DxResourceViewDimension.Buffer,
            descriptor.Format,
            descriptor.Label,
            0,
            1,
            0,
            1,
            createTextureView: false)
    {
        ValidateBufferDescriptor(buffer, descriptor);
        Descriptor = descriptor with { Dimension = DxResourceViewDimension.Buffer };
    }

    public DxUnorderedAccessViewDescriptor Descriptor { get; }

    private static void ValidateTextureDescriptor(
        ProGpuDirectXTexture2D texture,
        DxUnorderedAccessViewDescriptor descriptor)
    {
        if ((texture.Descriptor.Usage & DxTextureUsage.UnorderedAccess) == 0)
        {
            throw new ArgumentException("Texture was not created with unordered-access usage.", nameof(texture));
        }

        if (descriptor.MipSlice >= texture.Descriptor.MipLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Unordered-access view mip slice exceeds the texture.");
        }

        if (descriptor.FirstArraySlice >= texture.Descriptor.ArraySize ||
            descriptor.ArraySize == 0 ||
            descriptor.ArraySize > texture.Descriptor.ArraySize - descriptor.FirstArraySlice)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Unordered-access view array range exceeds the texture.");
        }
    }

    private static void ValidateBufferDescriptor(
        ProGpuDirectXBuffer buffer,
        DxUnorderedAccessViewDescriptor descriptor)
    {
        if ((buffer.Descriptor.Usage & DxBufferUsage.UnorderedAccess) == 0)
        {
            throw new ArgumentException("Buffer was not created with unordered-access usage.", nameof(buffer));
        }

        if (descriptor.ElementCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Buffer unordered-access views must expose at least one element.");
        }
    }
}

public sealed unsafe class ProGpuDirectXSamplerState : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private readonly IntPtr _backendSampler;
    private bool _isDisposed;

    internal ProGpuDirectXSamplerState(ProGpuDirectXDevice device, DxSamplerDescriptor descriptor)
    {
        _device = device;
        Descriptor = NormalizeDescriptor(descriptor);

        if (device.Context is { } context && device.IsGpuBacked)
        {
            _backendSampler = (IntPtr)CreateSampler(context, Descriptor);
        }
    }

    public DxSamplerDescriptor Descriptor { get; }

    public bool HasBackendSampler => _backendSampler != IntPtr.Zero;

    public IntPtr BackendSamplerHandle => _backendSampler;

    internal Sampler* BackendSampler => (Sampler*)_backendSampler;

    private static DxSamplerDescriptor NormalizeDescriptor(DxSamplerDescriptor descriptor)
    {
        var maxAnisotropy = descriptor.Filter == DxFilter.Anisotropic
            ? Math.Max((ushort)2, descriptor.MaximumAnisotropy)
            : Math.Max((ushort)1, descriptor.MaximumAnisotropy);

        return descriptor with { MaximumAnisotropy = maxAnisotropy };
    }

    private static Sampler* CreateSampler(ProGPU.Backend.WgpuContext context, DxSamplerDescriptor descriptor)
    {
        var labelPtr = SilkMarshal.StringToPtr(descriptor.Label);
        try
        {
            var samplerDesc = new SamplerDescriptor
            {
                Label = (byte*)labelPtr,
                AddressModeU = ProGpuDirectXFormatConverter.ToAddressMode(descriptor.AddressU),
                AddressModeV = ProGpuDirectXFormatConverter.ToAddressMode(descriptor.AddressV),
                AddressModeW = ProGpuDirectXFormatConverter.ToAddressMode(descriptor.AddressW),
                MagFilter = ProGpuDirectXFormatConverter.ToFilterMode(descriptor.Filter),
                MinFilter = ProGpuDirectXFormatConverter.ToFilterMode(descriptor.Filter),
                MipmapFilter = ProGpuDirectXFormatConverter.ToMipmapFilterMode(descriptor.Filter),
                LodMinClamp = descriptor.MinimumLod,
                LodMaxClamp = descriptor.MaximumLod,
                MaxAnisotropy = descriptor.MaximumAnisotropy,
                Compare = descriptor.ComparisonFunction.HasValue
                    ? ProGpuDirectXFormatConverter.ToCompareFunction(descriptor.ComparisonFunction.Value)
                    : CompareFunction.Undefined
            };

            var sampler = context.Wgpu.DeviceCreateSampler(context.Device, &samplerDesc);
            if (sampler == null)
            {
                throw new InvalidOperationException($"Failed to create DirectX sampler '{descriptor.Label}'.");
            }

            return sampler;
        }
        finally
        {
            SilkMarshal.Free(labelPtr);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_backendSampler != IntPtr.Zero &&
            _device.Context is { IsDisposed: false } context)
        {
            context.QueueSamplerDisposal(_backendSampler);
        }

        _isDisposed = true;
    }
}
