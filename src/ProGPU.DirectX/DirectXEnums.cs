namespace ProGPU.DirectX;

public enum DxFeatureLevel
{
    Direct3D9_3,
    Direct3D10_0,
    Direct3D10_1,
    Direct3D11_0,
    Direct3D11_1
}

public enum DxResourceFormat
{
    Unknown,
    R8Unorm,
    R8G8B8A8Unorm,
    R8G8B8A8UnormSrgb,
    B8G8R8A8Unorm,
    B8G8R8A8UnormSrgb,
    R16Float,
    R32Float,
    R32G32Float,
    R32G32B32Float,
    R32G32B32A32Float,
    D24UnormS8UInt,
    D32Float
}

public enum DxShaderStage
{
    Vertex,
    Pixel,
    Geometry,
    Compute
}

[Flags]
public enum DxShaderStageFlags
{
    None = 0,
    Vertex = 1 << 0,
    Pixel = 1 << 1,
    Geometry = 1 << 2,
    Compute = 1 << 3,
    AllGraphics = Vertex | Pixel | Geometry,
    All = Vertex | Pixel | Geometry | Compute
}

public enum DxShaderSourceKind
{
    Wgsl,
    HlslText,
    HlslBytecode
}

public enum DxPrimitiveTopology
{
    Undefined,
    PointList,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip
}

[Flags]
public enum DxBufferUsage
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Constant = 1 << 2,
    Structured = 1 << 3,
    ShaderResource = 1 << 4,
    UnorderedAccess = 1 << 5,
    CopySource = 1 << 6,
    CopyDestination = 1 << 7
}

[Flags]
public enum DxTextureUsage
{
    None = 0,
    ShaderResource = 1 << 0,
    RenderTarget = 1 << 1,
    DepthStencil = 1 << 2,
    UnorderedAccess = 1 << 3,
    CopySource = 1 << 4,
    CopyDestination = 1 << 5,
    Present = 1 << 6
}

public enum DxPresentMode
{
    Immediate,
    Fifo
}

public enum DxIndexFormat
{
    UInt16,
    UInt32
}

public enum DxInputClassification
{
    PerVertexData,
    PerInstanceData
}

public enum DxFillMode
{
    Solid,
    Wireframe
}

public enum DxCullMode
{
    None,
    Front,
    Back
}

public enum DxFrontFace
{
    CounterClockwise,
    Clockwise
}

public enum DxComparisonFunction
{
    Never,
    Less,
    Equal,
    LessEqual,
    Greater,
    NotEqual,
    GreaterEqual,
    Always
}

public enum DxDepthWriteMask
{
    Zero,
    All
}

public enum DxBlendFactor
{
    Zero,
    One,
    SourceColor,
    InverseSourceColor,
    SourceAlpha,
    InverseSourceAlpha,
    DestinationColor,
    InverseDestinationColor,
    DestinationAlpha,
    InverseDestinationAlpha
}

public enum DxBlendOperation
{
    Add,
    Subtract,
    ReverseSubtract,
    Min,
    Max
}

[Flags]
public enum DxColorWriteMask
{
    None = 0,
    Red = 1 << 0,
    Green = 1 << 1,
    Blue = 1 << 2,
    Alpha = 1 << 3,
    All = Red | Green | Blue | Alpha
}

public enum DxResourceViewDimension
{
    Unknown,
    Buffer,
    Texture2D,
    Texture2DArray
}

public enum DxFilter
{
    MinMagMipPoint,
    MinMagMipLinear,
    Anisotropic,
    ComparisonMinMagMipLinear
}

public enum DxTextureAddressMode
{
    Wrap,
    Mirror,
    Clamp,
    Border
}
