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
        ProGpuDirectXTexture3D? texture3D,
        ProGpuDirectXBuffer? buffer,
        DxResourceViewDimension dimension,
        DxResourceFormat format,
        string label,
        uint baseMipLevel,
        uint mipLevelCount,
        uint baseArrayLayer,
        uint arrayLayerCount,
        bool createTextureView,
        TextureViewDimension? nativeTextureViewDimension = null)
    {
        Device = device;
        Texture = texture;
        Texture3D = texture3D;
        Buffer = buffer;
        Dimension = dimension;
        Format = format;
        Label = label;

        var backendTexture = texture?.GetBackendTexture(baseArrayLayer) ?? texture3D?.BackendTexture;
        var sourceFormat = texture?.Descriptor.Format ?? texture3D?.Descriptor.Format ?? format;
        var nativeBaseArrayLayer = texture?.GetNativeArrayLayer(baseArrayLayer) ?? baseArrayLayer;
        if (createTextureView &&
            backendTexture is { IsDisposed: false, TexturePtr: not null })
        {
            _backendTextureView = (IntPtr)CreateTextureView(
                backendTexture.TexturePtr,
                device,
                dimension,
                format == DxResourceFormat.Unknown ? sourceFormat : format,
                label,
                baseMipLevel,
                mipLevelCount,
                nativeBaseArrayLayer,
                arrayLayerCount,
                nativeTextureViewDimension);
            _ownsTextureView = true;
        }
        else if (backendTexture is { IsDisposed: false, ViewPtr: not null } defaultTexture)
        {
            _backendTextureView = (IntPtr)defaultTexture.ViewPtr;
        }
    }

    public ProGpuDirectXDevice Device { get; }

    public ProGpuDirectXTexture2D? Texture { get; }

    public ProGpuDirectXTexture3D? Texture3D { get; }

    public ProGpuDirectXBuffer? Buffer { get; }

    public DxResourceViewDimension Dimension { get; }

    public DxResourceFormat Format { get; }

    public string Label { get; }

    public bool HasBackendTextureView => _backendTextureView != IntPtr.Zero;

    public bool IsTextureView => Texture is not null || Texture3D is not null;

    public uint TextureSampleCount => Texture?.Descriptor.SampleCount ?? 1;

    public uint TextureGeneration => Texture?.Generation ?? Texture3D?.Generation ?? 0;

    public string? TextureLabel => Texture?.Label ?? Texture3D?.Label;

    public IntPtr BackendTextureViewHandle => _backendTextureView;

    internal TextureView* BackendTextureView => (TextureView*)_backendTextureView;

    private static TextureView* CreateTextureView(
        Texture* texture,
        ProGpuDirectXDevice device,
        DxResourceViewDimension dimension,
        DxResourceFormat format,
        string label,
        uint baseMipLevel,
        uint mipLevelCount,
        uint baseArrayLayer,
        uint arrayLayerCount,
        TextureViewDimension? nativeTextureViewDimension)
    {
        var labelPtr = SilkMarshal.StringToPtr(label);
        try
        {
            var viewDesc = new TextureViewDescriptor
            {
                Label = (byte*)labelPtr,
                Format = ProGpuDirectXFormatConverter.ToTextureFormat(format),
                Dimension = nativeTextureViewDimension ?? (dimension switch
                {
                    DxResourceViewDimension.Texture2DArray => TextureViewDimension.Dimension2DArray,
                    DxResourceViewDimension.Texture3D => TextureViewDimension.Dimension3D,
                    _ => TextureViewDimension.Dimension2D
                }),
                BaseMipLevel = baseMipLevel,
                MipLevelCount = mipLevelCount,
                BaseArrayLayer = baseArrayLayer,
                ArrayLayerCount = arrayLayerCount,
                Aspect = TextureAspect.All
            };

            var view = device.Context!.Api.TextureCreateView(texture, &viewDesc);
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

public sealed class ProGpuDirectXRenderTargetView : ProGpuDirectXView
{
    internal ProGpuDirectXRenderTargetView(
        ProGpuDirectXDevice device,
        ProGpuDirectXTexture2D texture,
        DxRenderTargetViewDescriptor descriptor)
        : base(
            device,
            ValidateDescriptor(texture, descriptor),
            null,
            null,
            descriptor.Dimension,
            descriptor.Format == DxResourceFormat.Unknown ? texture.Descriptor.Format : descriptor.Format,
            descriptor.Label,
            descriptor.MipSlice,
            1,
            descriptor.FirstArraySlice,
            descriptor.ArraySize,
            createTextureView: true,
            nativeTextureViewDimension: GetRenderAttachmentTextureViewDimension(descriptor.Dimension, descriptor.ArraySize))
    {
        Descriptor = descriptor;
    }

    public DxRenderTargetViewDescriptor Descriptor { get; }

    private static ProGpuDirectXTexture2D ValidateDescriptor(
        ProGpuDirectXTexture2D texture,
        DxRenderTargetViewDescriptor descriptor)
    {
        if ((texture.Descriptor.Usage & DxTextureUsage.RenderTarget) == 0)
        {
            throw new ArgumentException("Texture was not created with render-target usage.", nameof(texture));
        }

        if (descriptor.Dimension is not DxResourceViewDimension.Texture2D and not DxResourceViewDimension.Texture2DArray)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Render-target views currently support Texture2D and Texture2DArray resources.");
        }

        if (descriptor.MipSlice >= texture.Descriptor.MipLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Render-target view mip slice exceeds the texture.");
        }

        if (descriptor.FirstArraySlice >= texture.Descriptor.ArraySize ||
            descriptor.ArraySize == 0 ||
            descriptor.ArraySize > texture.Descriptor.ArraySize - descriptor.FirstArraySlice)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Render-target view array range exceeds the texture.");
        }

        if (texture.UsesBackendArraySliceTextures && descriptor.ArraySize != 1)
        {
            throw new NotSupportedException("Multisampled texture array render-target views currently support one array slice per view.");
        }

        return texture;
    }

    private static TextureViewDimension GetRenderAttachmentTextureViewDimension(
        DxResourceViewDimension dimension,
        uint arrayLayerCount)
    {
        return dimension == DxResourceViewDimension.Texture2DArray && arrayLayerCount > 1
            ? TextureViewDimension.Dimension2DArray
            : TextureViewDimension.Dimension2D;
    }
}

public sealed class ProGpuDirectXDepthStencilView : ProGpuDirectXView
{
    internal ProGpuDirectXDepthStencilView(
        ProGpuDirectXDevice device,
        ProGpuDirectXTexture2D texture,
        DxDepthStencilViewDescriptor descriptor)
        : base(
            device,
            ValidateDescriptor(texture, descriptor),
            null,
            null,
            descriptor.Dimension,
            descriptor.Format == DxResourceFormat.Unknown ? texture.Descriptor.Format : descriptor.Format,
            descriptor.Label,
            descriptor.MipSlice,
            1,
            descriptor.FirstArraySlice,
            descriptor.ArraySize,
            createTextureView: true,
            nativeTextureViewDimension: GetDepthStencilTextureViewDimension(descriptor.Dimension, descriptor.ArraySize))
    {
        Descriptor = descriptor;
    }

    public DxDepthStencilViewDescriptor Descriptor { get; }

    private static ProGpuDirectXTexture2D ValidateDescriptor(
        ProGpuDirectXTexture2D texture,
        DxDepthStencilViewDescriptor descriptor)
    {
        if ((texture.Descriptor.Usage & DxTextureUsage.DepthStencil) == 0)
        {
            throw new ArgumentException("Texture was not created with depth-stencil usage.", nameof(texture));
        }

        if (descriptor.Dimension is not DxResourceViewDimension.Texture2D and not DxResourceViewDimension.Texture2DArray)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Depth-stencil views currently support Texture2D and Texture2DArray resources.");
        }

        if (descriptor.MipSlice >= texture.Descriptor.MipLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Depth-stencil view mip slice exceeds the texture.");
        }

        if (descriptor.FirstArraySlice >= texture.Descriptor.ArraySize ||
            descriptor.ArraySize == 0 ||
            descriptor.ArraySize > texture.Descriptor.ArraySize - descriptor.FirstArraySlice)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Depth-stencil view array range exceeds the texture.");
        }

        if (texture.UsesBackendArraySliceTextures && descriptor.ArraySize != 1)
        {
            throw new NotSupportedException("Multisampled texture array depth-stencil views currently support one array slice per view.");
        }

        return texture;
    }

    private static TextureViewDimension GetDepthStencilTextureViewDimension(
        DxResourceViewDimension dimension,
        uint arrayLayerCount)
    {
        return dimension == DxResourceViewDimension.Texture2DArray && arrayLayerCount > 1
            ? TextureViewDimension.Dimension2DArray
            : TextureViewDimension.Dimension2D;
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
            ValidateTextureDescriptor(texture, descriptor),
            null,
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
        Descriptor = descriptor;
    }

    internal ProGpuDirectXShaderResourceView(
        ProGpuDirectXDevice device,
        ProGpuDirectXTexture3D texture,
        DxShaderResourceViewDescriptor descriptor)
        : base(
            device,
            null,
            ValidateTextureDescriptor(texture, descriptor),
            null,
            descriptor.Dimension,
            descriptor.Format == DxResourceFormat.Unknown ? texture.Descriptor.Format : descriptor.Format,
            descriptor.Label,
            descriptor.MostDetailedMip,
            descriptor.MipLevels,
            0,
            1,
            createTextureView: true)
    {
        Descriptor = descriptor with { Dimension = DxResourceViewDimension.Texture3D, FirstArraySlice = 0, ArraySize = 1 };
    }

    internal ProGpuDirectXShaderResourceView(
        ProGpuDirectXDevice device,
        ProGpuDirectXBuffer buffer,
        DxShaderResourceViewDescriptor descriptor)
        : base(
            device,
            null,
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

    private static ProGpuDirectXTexture2D ValidateTextureDescriptor(
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

        if (texture.UsesBackendArraySliceTextures && descriptor.ArraySize != 1)
        {
            throw new NotSupportedException("Multisampled texture array shader-resource views currently support one array slice per view.");
        }

        return texture;
    }

    private static ProGpuDirectXTexture3D ValidateTextureDescriptor(
        ProGpuDirectXTexture3D texture,
        DxShaderResourceViewDescriptor descriptor)
    {
        if ((texture.Descriptor.Usage & DxTextureUsage.ShaderResource) == 0)
        {
            throw new ArgumentException("Texture was not created with shader-resource usage.", nameof(texture));
        }

        if (descriptor.Dimension != DxResourceViewDimension.Texture3D)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "3D texture shader-resource views require Texture3D dimension.");
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

        if (descriptor.FirstArraySlice != 0 || descriptor.ArraySize != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "3D texture shader-resource views do not expose array slices.");
        }

        return texture;
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
            ValidateTextureDescriptor(texture, descriptor),
            null,
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
        Descriptor = descriptor;
    }

    internal ProGpuDirectXUnorderedAccessView(
        ProGpuDirectXDevice device,
        ProGpuDirectXBuffer buffer,
        DxUnorderedAccessViewDescriptor descriptor)
        : base(
            device,
            null,
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

    private static ProGpuDirectXTexture2D ValidateTextureDescriptor(
        ProGpuDirectXTexture2D texture,
        DxUnorderedAccessViewDescriptor descriptor)
    {
        if ((texture.Descriptor.Usage & DxTextureUsage.UnorderedAccess) == 0)
        {
            throw new ArgumentException("Texture was not created with unordered-access usage.", nameof(texture));
        }

        ValidateAccess(descriptor.Access);

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

        if (texture.UsesBackendArraySliceTextures && descriptor.ArraySize != 1)
        {
            throw new NotSupportedException("Multisampled texture array unordered-access views currently support one array slice per view.");
        }

        return texture;
    }

    private static void ValidateBufferDescriptor(
        ProGpuDirectXBuffer buffer,
        DxUnorderedAccessViewDescriptor descriptor)
    {
        if ((buffer.Descriptor.Usage & DxBufferUsage.UnorderedAccess) == 0)
        {
            throw new ArgumentException("Buffer was not created with unordered-access usage.", nameof(buffer));
        }

        ValidateAccess(descriptor.Access);

        if (descriptor.ElementCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Buffer unordered-access views must expose at least one element.");
        }
    }

    private static void ValidateAccess(DxUnorderedAccessViewAccess access)
    {
        if (!Enum.IsDefined(access))
        {
            throw new ArgumentOutOfRangeException(nameof(access), "Unknown DirectX unordered-access view access mode.");
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

            var sampler = context.Api.DeviceCreateSampler(context.Device, &samplerDesc);
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
