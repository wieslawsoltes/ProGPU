namespace ProGPU.DirectX;

public readonly record struct DxColor(float R, float G, float B, float A)
{
    public static DxColor Transparent { get; } = new(0f, 0f, 0f, 0f);
    public static DxColor Black { get; } = new(0f, 0f, 0f, 1f);
}

public readonly record struct DxViewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth = 0f,
    float MaxDepth = 1f);

public readonly record struct DxRect(int X, int Y, int Width, int Height);

public sealed record ProGpuDirectXDeviceOptions
{
    public DxFeatureLevel MinimumFeatureLevel { get; init; } = DxFeatureLevel.Direct3D9_3;
    public bool RequireGpuBackedResources { get; init; }
    public bool EnableValidation { get; init; }
    public string Label { get; init; } = "ProGPU DirectX Device";
}

public sealed record DxBufferDescriptor
{
    public required uint SizeInBytes { get; init; }
    public DxBufferUsage Usage { get; init; } = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination;
    public uint StrideInBytes { get; init; }
    public string Label { get; init; } = "DirectXBuffer";
}

public sealed record DxTexture2DDescriptor
{
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public DxResourceFormat Format { get; init; } = DxResourceFormat.B8G8R8A8Unorm;
    public DxTextureUsage Usage { get; init; } = DxTextureUsage.ShaderResource | DxTextureUsage.RenderTarget | DxTextureUsage.CopyDestination;
    public uint MipLevels { get; init; } = 1;
    public uint ArraySize { get; init; } = 1;
    public uint SampleCount { get; init; } = 1;
    public string Label { get; init; } = "DirectXTexture2D";
}

public sealed record DxSwapChainDescriptor
{
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public DxResourceFormat Format { get; init; } = DxResourceFormat.B8G8R8A8Unorm;
    public DxPresentMode PresentMode { get; init; } = DxPresentMode.Immediate;
    public string Label { get; init; } = "DirectXSwapChain";
}

public sealed record DxDrawCall(
    DxPrimitiveTopology Topology,
    uint VertexCount,
    uint StartVertexLocation,
    uint InstanceCount,
    uint StartInstanceLocation);

public sealed record DxDrawIndexedCall(
    DxPrimitiveTopology Topology,
    uint IndexCount,
    uint StartIndexLocation,
    int BaseVertexLocation,
    uint InstanceCount,
    uint StartInstanceLocation,
    DxIndexFormat IndexFormat);

public sealed record DxShaderDescriptor
{
    public required DxShaderStage Stage { get; init; }
    public DxShaderSourceKind SourceKind { get; init; } = DxShaderSourceKind.Wgsl;
    public string? Source { get; init; }
    public ReadOnlyMemory<byte> Bytecode { get; init; } = ReadOnlyMemory<byte>.Empty;
    public string? EntryPoint { get; init; }
    public string Label { get; init; } = "DirectXShader";
}

public sealed record DxInputElementDescriptor
{
    public required string SemanticName { get; init; }
    public uint SemanticIndex { get; init; }
    public DxResourceFormat Format { get; init; } = DxResourceFormat.R32G32B32A32Float;
    public uint InputSlot { get; init; }
    public uint AlignedByteOffset { get; init; }
    public DxInputClassification InputSlotClass { get; init; } = DxInputClassification.PerVertexData;
    public uint InstanceDataStepRate { get; init; }
    public uint? ShaderLocation { get; init; }
}

public sealed record DxInputLayoutDescriptor
{
    public IReadOnlyList<DxInputElementDescriptor> Elements { get; init; } = Array.Empty<DxInputElementDescriptor>();
    public string Label { get; init; } = "DirectXInputLayout";
}

public sealed record DxRasterizerStateDescriptor
{
    public DxFillMode FillMode { get; init; } = DxFillMode.Solid;
    public DxCullMode CullMode { get; init; } = DxCullMode.Back;
    public DxFrontFace FrontFace { get; init; } = DxFrontFace.CounterClockwise;
    public bool ScissorEnable { get; init; }
    public int DepthBias { get; init; }
    public float DepthBiasClamp { get; init; }
    public float SlopeScaledDepthBias { get; init; }
}

public sealed record DxBlendStateDescriptor
{
    public bool EnableBlend { get; init; } = true;
    public DxBlendFactor SourceColor { get; init; } = DxBlendFactor.SourceAlpha;
    public DxBlendFactor DestinationColor { get; init; } = DxBlendFactor.InverseSourceAlpha;
    public DxBlendOperation ColorOperation { get; init; } = DxBlendOperation.Add;
    public DxBlendFactor SourceAlpha { get; init; } = DxBlendFactor.One;
    public DxBlendFactor DestinationAlpha { get; init; } = DxBlendFactor.InverseSourceAlpha;
    public DxBlendOperation AlphaOperation { get; init; } = DxBlendOperation.Add;
    public DxColorWriteMask WriteMask { get; init; } = DxColorWriteMask.All;
}

public sealed record DxDepthStencilStateDescriptor
{
    public bool DepthEnable { get; init; }
    public DxDepthWriteMask DepthWriteMask { get; init; } = DxDepthWriteMask.All;
    public DxComparisonFunction DepthFunction { get; init; } = DxComparisonFunction.LessEqual;
    public bool StencilEnable { get; init; }
}

public sealed record DxGraphicsPipelineDescriptor
{
    public required ProGpuDirectXShader VertexShader { get; init; }
    public ProGpuDirectXShader? PixelShader { get; init; }
    public ProGpuDirectXInputLayout? InputLayout { get; init; }
    public DxPrimitiveTopology Topology { get; init; } = DxPrimitiveTopology.TriangleList;
    public DxResourceFormat RenderTargetFormat { get; init; } = DxResourceFormat.B8G8R8A8Unorm;
    public DxResourceFormat DepthStencilFormat { get; init; } = DxResourceFormat.Unknown;
    public uint SampleCount { get; init; } = 1;
    public DxRasterizerStateDescriptor RasterizerState { get; init; } = new();
    public DxBlendStateDescriptor BlendState { get; init; } = new();
    public DxDepthStencilStateDescriptor DepthStencilState { get; init; } = new();
    public string Label { get; init; } = "DirectXGraphicsPipeline";
}

public sealed record DxComputePipelineDescriptor
{
    public required ProGpuDirectXShader ComputeShader { get; init; }
    public string Label { get; init; } = "DirectXComputePipeline";
}

public sealed record DxDispatchCall(uint ThreadGroupCountX, uint ThreadGroupCountY, uint ThreadGroupCountZ);
