using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.DirectX;

internal static class ProGpuDirectXFormatConverter
{
    public static TextureFormat ToTextureFormat(DxResourceFormat format)
    {
        return format switch
        {
            DxResourceFormat.R8Unorm => TextureFormat.R8Unorm,
            DxResourceFormat.R8G8B8A8Unorm => TextureFormat.Rgba8Unorm,
            DxResourceFormat.R8G8B8A8UnormSrgb => TextureFormat.Rgba8UnormSrgb,
            DxResourceFormat.B8G8R8A8Unorm => TextureFormat.Bgra8Unorm,
            DxResourceFormat.B8G8R8A8UnormSrgb => TextureFormat.Bgra8UnormSrgb,
            DxResourceFormat.R16Float => TextureFormat.R16float,
            DxResourceFormat.R32Float => TextureFormat.R32float,
            DxResourceFormat.R32G32Float => TextureFormat.RG32float,
            DxResourceFormat.R32G32B32A32Float => TextureFormat.Rgba32float,
            DxResourceFormat.D24UnormS8UInt => TextureFormat.Depth24PlusStencil8,
            DxResourceFormat.D32Float => TextureFormat.Depth32float,
            _ => TextureFormat.Bgra8Unorm
        };
    }

    public static TextureUsage ToTextureUsage(DxTextureUsage usage)
    {
        var result = TextureUsage.None;
        if ((usage & DxTextureUsage.ShaderResource) != 0)
        {
            result |= TextureUsage.TextureBinding;
        }

        if ((usage & (DxTextureUsage.RenderTarget | DxTextureUsage.DepthStencil | DxTextureUsage.Present)) != 0)
        {
            result |= TextureUsage.RenderAttachment;
        }

        if ((usage & DxTextureUsage.UnorderedAccess) != 0)
        {
            result |= TextureUsage.StorageBinding;
        }

        if ((usage & DxTextureUsage.CopySource) != 0)
        {
            result |= TextureUsage.CopySrc;
        }

        if ((usage & DxTextureUsage.CopyDestination) != 0)
        {
            result |= TextureUsage.CopyDst;
        }

        return result == TextureUsage.None
            ? TextureUsage.TextureBinding | TextureUsage.CopyDst
            : result;
    }

    public static BufferUsage ToBufferUsage(DxBufferUsage usage)
    {
        var result = BufferUsage.None;
        if ((usage & DxBufferUsage.Vertex) != 0)
        {
            result |= BufferUsage.Vertex;
        }

        if ((usage & DxBufferUsage.Index) != 0)
        {
            result |= BufferUsage.Index;
        }

        if ((usage & DxBufferUsage.Constant) != 0)
        {
            result |= BufferUsage.Uniform;
        }

        if ((usage & (DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.UnorderedAccess)) != 0)
        {
            result |= BufferUsage.Storage;
        }

        if ((usage & DxBufferUsage.CopySource) != 0)
        {
            result |= BufferUsage.CopySrc;
        }

        if ((usage & DxBufferUsage.CopyDestination) != 0)
        {
            result |= BufferUsage.CopyDst;
        }

        return result == BufferUsage.None
            ? BufferUsage.Vertex | BufferUsage.CopyDst
            : result;
    }

    public static PrimitiveTopology ToPrimitiveTopology(DxPrimitiveTopology topology)
    {
        return topology switch
        {
            DxPrimitiveTopology.PointList => PrimitiveTopology.PointList,
            DxPrimitiveTopology.LineList => PrimitiveTopology.LineList,
            DxPrimitiveTopology.LineStrip => PrimitiveTopology.LineStrip,
            DxPrimitiveTopology.TriangleStrip => PrimitiveTopology.TriangleStrip,
            _ => PrimitiveTopology.TriangleList
        };
    }

    public static VertexFormat ToVertexFormat(DxResourceFormat format)
    {
        return format switch
        {
            DxResourceFormat.R8Unorm => VertexFormat.Unorm8x2,
            DxResourceFormat.R8G8B8A8Unorm or DxResourceFormat.R8G8B8A8UnormSrgb => VertexFormat.Unorm8x4,
            DxResourceFormat.B8G8R8A8Unorm or DxResourceFormat.B8G8R8A8UnormSrgb => VertexFormat.Unorm8x4,
            DxResourceFormat.R16Float => VertexFormat.Float16x2,
            DxResourceFormat.R32Float => VertexFormat.Float32,
            DxResourceFormat.R32G32Float => VertexFormat.Float32x2,
            DxResourceFormat.R32G32B32Float => VertexFormat.Float32x3,
            DxResourceFormat.R32G32B32A32Float => VertexFormat.Float32x4,
            _ => VertexFormat.Float32x4
        };
    }

    public static uint GetVertexFormatSizeInBytes(DxResourceFormat format)
    {
        return format switch
        {
            DxResourceFormat.R8Unorm => 1,
            DxResourceFormat.R8G8B8A8Unorm or DxResourceFormat.R8G8B8A8UnormSrgb => 4,
            DxResourceFormat.B8G8R8A8Unorm or DxResourceFormat.B8G8R8A8UnormSrgb => 4,
            DxResourceFormat.R16Float => 2,
            DxResourceFormat.R32Float => 4,
            DxResourceFormat.R32G32Float => 8,
            DxResourceFormat.R32G32B32Float => 12,
            DxResourceFormat.R32G32B32A32Float => 16,
            _ => 16
        };
    }

    public static VertexStepMode ToVertexStepMode(DxInputClassification classification)
    {
        return classification == DxInputClassification.PerInstanceData
            ? VertexStepMode.Instance
            : VertexStepMode.Vertex;
    }

    public static CullMode ToCullMode(DxCullMode mode)
    {
        return mode switch
        {
            DxCullMode.Front => CullMode.Front,
            DxCullMode.Back => CullMode.Back,
            _ => CullMode.None
        };
    }

    public static FrontFace ToFrontFace(DxFrontFace frontFace)
    {
        return frontFace == DxFrontFace.Clockwise
            ? FrontFace.CW
            : FrontFace.Ccw;
    }

    public static CompareFunction ToCompareFunction(DxComparisonFunction comparison)
    {
        return comparison switch
        {
            DxComparisonFunction.Never => CompareFunction.Never,
            DxComparisonFunction.Less => CompareFunction.Less,
            DxComparisonFunction.Equal => CompareFunction.Equal,
            DxComparisonFunction.LessEqual => CompareFunction.LessEqual,
            DxComparisonFunction.Greater => CompareFunction.Greater,
            DxComparisonFunction.NotEqual => CompareFunction.NotEqual,
            DxComparisonFunction.GreaterEqual => CompareFunction.GreaterEqual,
            _ => CompareFunction.Always
        };
    }

    public static BlendFactor ToBlendFactor(DxBlendFactor factor)
    {
        return factor switch
        {
            DxBlendFactor.Zero => BlendFactor.Zero,
            DxBlendFactor.One => BlendFactor.One,
            DxBlendFactor.SourceColor => BlendFactor.Src,
            DxBlendFactor.InverseSourceColor => BlendFactor.OneMinusSrc,
            DxBlendFactor.SourceAlpha => BlendFactor.SrcAlpha,
            DxBlendFactor.InverseSourceAlpha => BlendFactor.OneMinusSrcAlpha,
            DxBlendFactor.DestinationColor => BlendFactor.Dst,
            DxBlendFactor.InverseDestinationColor => BlendFactor.OneMinusDst,
            DxBlendFactor.DestinationAlpha => BlendFactor.DstAlpha,
            DxBlendFactor.InverseDestinationAlpha => BlendFactor.OneMinusDstAlpha,
            _ => BlendFactor.One
        };
    }

    public static BlendOperation ToBlendOperation(DxBlendOperation operation)
    {
        return operation switch
        {
            DxBlendOperation.Subtract => BlendOperation.Subtract,
            DxBlendOperation.ReverseSubtract => BlendOperation.ReverseSubtract,
            DxBlendOperation.Min => BlendOperation.Min,
            DxBlendOperation.Max => BlendOperation.Max,
            _ => BlendOperation.Add
        };
    }

    public static ColorWriteMask ToColorWriteMask(DxColorWriteMask mask)
    {
        var result = ColorWriteMask.None;
        if ((mask & DxColorWriteMask.Red) != 0)
        {
            result |= ColorWriteMask.Red;
        }

        if ((mask & DxColorWriteMask.Green) != 0)
        {
            result |= ColorWriteMask.Green;
        }

        if ((mask & DxColorWriteMask.Blue) != 0)
        {
            result |= ColorWriteMask.Blue;
        }

        if ((mask & DxColorWriteMask.Alpha) != 0)
        {
            result |= ColorWriteMask.Alpha;
        }

        return result;
    }

    public static FilterMode ToFilterMode(DxFilter filter)
    {
        return filter == DxFilter.MinMagMipPoint
            ? FilterMode.Nearest
            : FilterMode.Linear;
    }

    public static MipmapFilterMode ToMipmapFilterMode(DxFilter filter)
    {
        return filter == DxFilter.MinMagMipPoint
            ? MipmapFilterMode.Nearest
            : MipmapFilterMode.Linear;
    }

    public static AddressMode ToAddressMode(DxTextureAddressMode mode)
    {
        return mode switch
        {
            DxTextureAddressMode.Wrap => AddressMode.Repeat,
            DxTextureAddressMode.Mirror => AddressMode.MirrorRepeat,
            _ => AddressMode.ClampToEdge
        };
    }

    public static GpuTextureAlphaMode ToTextureAlphaMode(DxResourceFormat format)
    {
        return format is DxResourceFormat.B8G8R8A8Unorm or DxResourceFormat.R8G8B8A8Unorm
            ? GpuTextureAlphaMode.Premultiplied
            : GpuTextureAlphaMode.Straight;
    }
}
