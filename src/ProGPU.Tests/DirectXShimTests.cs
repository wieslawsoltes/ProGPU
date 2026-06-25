using ProGPU.Backend;
using ProGPU.DirectX;
using System.Runtime.InteropServices;
using Xunit;

namespace ProGPU.Tests;

public sealed class DirectXShimTests
{
    private const string PassthroughWgsl = """
struct VertexIn {
    @location(0) position: vec3<f32>,
    @location(1) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
};

@vertex
fn vs_main(input: VertexIn) -> VertexOut {
    var output: VertexOut;
    output.position = vec4<f32>(input.position, 1.0);
    output.color = input.color;
    return output;
}

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    return input.color;
}
""";

    private const string ComputeWgsl = """
@compute @workgroup_size(1)
fn cs_main() {
}
""";

    private const string SolidTriangleWgsl = """
@vertex
fn vs_main(@builtin(vertex_index) vertexIndex: u32) -> @builtin(position) vec4<f32> {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-0.9, -0.9),
        vec2<f32>(0.9, -0.9),
        vec2<f32>(0.0, 0.9));
    let p = positions[vertexIndex];
    return vec4<f32>(p, 0.0, 1.0);
}

@fragment
fn fs_main() -> @location(0) vec4<f32> {
    return vec4<f32>(1.0, 0.0, 0.0, 1.0);
}
""";

    private const string PassthroughVertexHlsl = """
struct VertexInput
{
    float3 position : POSITION;
    float4 color : COLOR0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float4 color : COLOR0;
};

VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.position = float4(input.position, 1.0);
    output.color = input.color;
    return output;
}
""";

    private const string TransformVertexHlsl = """
cbuffer Transform : register(b0)
{
    float4x4 WorldViewProjection;
};

struct VertexInput
{
    float3 position : POSITION;
    float4 color : COLOR0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float4 color : COLOR0;
};

VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.position = mul(WorldViewProjection, float4(input.position, 1.0));
    output.color = input.color;
    return output;
}
""";

    private const string PassthroughPixelHlsl = """
struct VertexOutput
{
    float4 position : SV_Position;
    float4 color : COLOR0;
};

float4 PSMain(VertexOutput input) : SV_Target
{
    return input.color;
}
""";

    private const string SolidLineWgsl = """
@vertex
fn vs_main(@builtin(vertex_index) vertexIndex: u32) -> @builtin(position) vec4<f32> {
    var positions = array<vec2<f32>, 2>(
        vec2<f32>(-0.9, 0.0),
        vec2<f32>(0.9, 0.0));
    let p = positions[vertexIndex];
    return vec4<f32>(p, 0.0, 1.0);
}

@fragment
fn fs_main() -> @location(0) vec4<f32> {
    return vec4<f32>(0.0, 1.0, 0.0, 1.0);
}
""";

    [Fact]
    public void MetadataDeviceAdvertisesSciChartFeatureLevelRange()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();

        Assert.False(device.IsGpuBacked);
        Assert.True(device.Capabilities.SupportsFeatureLevel(DxFeatureLevel.Direct3D9_3));
        Assert.True(device.Capabilities.SupportsFeatureLevel(DxFeatureLevel.Direct3D10_0));
        Assert.True(device.Capabilities.SupportsFeatureLevel(DxFeatureLevel.Direct3D11_0));
        Assert.Equal(DxFeatureLevel.Direct3D11_1, device.Capabilities.HighestFeatureLevel);
    }

    [Fact]
    public void RequireGpuBackedResourcesFailsClosedWithoutContext()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProGpuDirectXDevice.CreateMetadataDevice(new ProGpuDirectXDeviceOptions
            {
                RequireGpuBackedResources = true
            }));
    }

    [Fact]
    public void CanCreateResizeAndPresentSwapChain()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var swapChain = device.CreateSwapChain(new DxSwapChainDescriptor
        {
            Width = 640,
            Height = 480
        });

        Assert.Equal(640u, swapChain.BackBuffer.Width);
        Assert.Equal(480u, swapChain.BackBuffer.Height);

        swapChain.Resize(800, 600);
        swapChain.Present();

        Assert.Equal(800u, swapChain.BackBuffer.Width);
        Assert.Equal(600u, swapChain.BackBuffer.Height);
        Assert.Equal(1u, swapChain.PresentCount);
    }

    [Fact]
    public void ImmediateContextRecordsSciChartStyleRenderCommands()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 320,
            Height = 200,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.ShaderResource | DxTextureUsage.CopyDestination
        });
        using var vertexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 1024,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 16
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 320, 200));
        context.SetScissorRect(new DxRect(0, 0, 320, 200));
        context.SetPrimitiveTopology(DxPrimitiveTopology.TriangleList);
        context.SetVertexBuffer(vertexBuffer);
        context.ClearRenderTarget(target, new DxColor(0.1f, 0.2f, 0.3f, 1f));
        context.Draw(6);

        Assert.Equal(7, context.Commands.Count);
        Assert.Equal(ProGpuDirectXCommandKind.SetRenderTargets, context.Commands[0].Kind);
        Assert.Equal(ProGpuDirectXCommandKind.Draw, context.Commands[^1].Kind);
        Assert.Equal(6u, context.Commands[^1].Draw!.VertexCount);

        context.Flush();

        Assert.Empty(context.Commands);
    }

    [Fact]
    public void CanCreateSciChartStyleShaderInputLayoutAndPipelineMetadata()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = PassthroughWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = PassthroughWgsl
        });
        var inputLayout = device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32B32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 12,
                    ShaderLocation = 1
                }
            ]
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            Topology = DxPrimitiveTopology.TriangleList,
            RenderTargetFormat = DxResourceFormat.B8G8R8A8Unorm,
            DepthStencilFormat = DxResourceFormat.D24UnormS8UInt,
            DepthStencilState = new DxDepthStencilStateDescriptor
            {
                DepthEnable = true,
                DepthWriteMask = DxDepthWriteMask.All,
                DepthFunction = DxComparisonFunction.LessEqual
            },
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.Back,
                FrontFace = DxFrontFace.CounterClockwise,
                ScissorEnable = true
            }
        });
        using var context = device.CreateImmediateContext();

        context.SetGraphicsPipeline(pipeline);
        context.DrawIndexed(36, indexFormat: DxIndexFormat.UInt16);

        Assert.False(vertexShader.HasBackendShaderModule);
        Assert.False(pipeline.HasBackendPipeline);
        Assert.Equal(28u, inputLayout.GetInferredStride(0));
        Assert.Contains(vertexShader.SourceHash, pipeline.PipelineKey, StringComparison.Ordinal);
        Assert.Same(pipeline, context.GraphicsPipeline);
        Assert.Equal(ProGpuDirectXCommandKind.SetGraphicsPipeline, context.Commands[0].Kind);
        Assert.Equal(ProGpuDirectXCommandKind.DrawIndexed, context.Commands[1].Kind);
        Assert.Same(pipeline, context.Commands[1].GraphicsPipeline);
        Assert.Equal(DxIndexFormat.UInt16, context.Commands[1].DrawIndexed!.IndexFormat);
    }

    [Fact]
    public void CanCreateDirectXStencilPipelineMetadata()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            DepthStencilFormat = DxResourceFormat.D24UnormS8UInt,
            DepthStencilState = new DxDepthStencilStateDescriptor
            {
                StencilEnable = true,
                StencilReference = 7,
                StencilReadMask = 0x0F,
                StencilWriteMask = 0xF0,
                FrontFace = new DxStencilFaceDescriptor
                {
                    Function = DxComparisonFunction.Equal,
                    FailOperation = DxStencilOperation.Replace,
                    DepthFailOperation = DxStencilOperation.IncrementSaturate,
                    PassOperation = DxStencilOperation.Invert
                },
                BackFace = new DxStencilFaceDescriptor
                {
                    Function = DxComparisonFunction.NotEqual,
                    FailOperation = DxStencilOperation.Zero,
                    DepthFailOperation = DxStencilOperation.DecrementSaturate,
                    PassOperation = DxStencilOperation.Decrement
                }
            }
        });

        Assert.False(pipeline.HasBackendPipeline);
        Assert.True(pipeline.Descriptor.DepthStencilState.StencilEnable);
        Assert.Equal(7, pipeline.Descriptor.DepthStencilState.StencilReference);
        Assert.Equal(0x0F, pipeline.Descriptor.DepthStencilState.StencilReadMask);
        Assert.Equal(0xF0, pipeline.Descriptor.DepthStencilState.StencilWriteMask);
        Assert.Equal(DxComparisonFunction.Equal, pipeline.Descriptor.DepthStencilState.FrontFace.Function);
        Assert.Equal(DxStencilOperation.Invert, pipeline.Descriptor.DepthStencilState.FrontFace.PassOperation);
        Assert.Equal(DxStencilOperation.Decrement, pipeline.Descriptor.DepthStencilState.BackFace.PassOperation);
    }

    [Fact]
    public void HlslBytecodeShadersRemainMetadataUntilTranslatorOrNativeFacadeIsConnected()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = new byte[] { 0x44, 0x58, 0x42, 0x43 }
        });

        Assert.False(shader.HasBackendShaderModule);
        Assert.Null(shader.BackendSource);
        Assert.Equal("vs_main", shader.EntryPoint);
        Assert.NotEqual(string.Empty, shader.SourceHash);
    }

    [Fact]
    public void UnsupportedHlslTextShadersRemainMetadata()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
Texture2D SourceTexture : register(t0);
SamplerState SourceSampler : register(s0);

float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
{
    return SourceTexture.Sample(SourceSampler, uv);
}
""",
            EntryPoint = "PSMain"
        });

        Assert.False(shader.HasBackendShaderModule);
        Assert.Null(shader.BackendSource);
        Assert.Equal("PSMain", shader.EntryPoint);
    }

    [Fact]
    public void RejectsMismatchedPipelineShaderStages()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = PassthroughWgsl
        });

        Assert.Throws<ArgumentException>(() =>
            device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
            {
                VertexShader = pixelShader
            }));
    }

    [Fact]
    public void CanRecordComputePipelineAndDispatchMetadata()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = ComputeWgsl
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });
        using var context = device.CreateImmediateContext();

        context.SetComputePipeline(pipeline);
        context.Dispatch(8, 4, 1);

        Assert.False(pipeline.HasBackendPipeline);
        Assert.Same(pipeline, context.ComputePipeline);
        Assert.Equal(ProGpuDirectXCommandKind.Dispatch, context.Commands[1].Kind);
        Assert.Equal(new DxDispatchCall(8, 4, 1), context.Commands[1].Dispatch);
    }

    [Fact]
    public void DrawCommandsCaptureDeferredVertexAndIndexBufferState()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var vertexBuffer0 = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 128,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 16
        });
        using var vertexBuffer1 = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 128,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 16
        });
        using var indexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.Index | DxBufferUsage.CopyDestination
        });
        using var context = device.CreateImmediateContext();

        context.SetVertexBuffer(0, vertexBuffer0);
        context.SetVertexBuffer(1, vertexBuffer1);
        context.SetIndexBuffer(indexBuffer, DxIndexFormat.UInt16);
        context.DrawIndexed(6);

        var command = context.Commands[^1];

        Assert.Equal(ProGpuDirectXCommandKind.DrawIndexed, command.Kind);
        Assert.NotNull(command.VertexBuffers);
        Assert.Same(vertexBuffer0, command.VertexBuffers[0]);
        Assert.Same(vertexBuffer1, command.VertexBuffers[1]);
        Assert.Same(indexBuffer, command.IndexBuffer);
        Assert.Equal(DxIndexFormat.UInt16, command.DrawIndexed!.IndexFormat);
        Assert.Equal(DxIndexFormat.UInt16, command.IndexFormat);
    }

    [Fact]
    public void FlushSubmitsGpuBackedDrawCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.Draw(3);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after DirectX draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after DirectX draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after DirectX draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after DirectX draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslTextDrawCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var vertexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 84,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 28
        });
        vertexBuffer.Write<float>(
        [
            -0.8f, -0.8f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f,
             0.8f, -0.8f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f,
             0.0f,  0.8f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f
        ]);
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = PassthroughVertexHlsl,
            EntryPoint = "VSMain",
            Label = "HLSL Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = PassthroughPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Pixel"
        });
        var inputLayout = device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32B32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 12,
                    ShaderLocation = 1
                }
            ]
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.SetVertexBuffer(vertexBuffer);
        context.Draw(3);
        context.Flush();

        Assert.True(vertexShader.HasBackendShaderModule);
        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.NotNull(vertexShader.BackendSource);
        Assert.Contains("@vertex", vertexShader.BackendSource, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after HLSL DirectX draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after HLSL DirectX draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after HLSL DirectX draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL DirectX draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslTextDrawCommandsWithConstantBuffer()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var constants = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            Label = "Transform Constants"
        });
        constants.Write<float>(
        [
            1.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 1.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        ]);
        using var vertexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 84,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 28
        });
        vertexBuffer.Write<float>(
        [
            -0.8f, -0.8f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f,
             0.8f, -0.8f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f,
             0.0f,  0.8f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f
        ]);
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = TransformVertexHlsl,
            EntryPoint = "VSMain",
            Label = "HLSL Transform Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = PassthroughPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Pixel"
        });
        var inputLayout = device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32B32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 12,
                    ShaderLocation = 1
                }
            ]
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.SetVertexBuffer(vertexBuffer);
        context.SetConstantBuffer(DxShaderStage.Vertex, 0, constants);
        context.Draw(3);
        context.Flush();

        Assert.True(vertexShader.HasBackendShaderModule);
        Assert.Contains("@binding(0)", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("transform.WorldViewProjection", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after HLSL constant-buffer draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after HLSL constant-buffer draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after HLSL constant-buffer draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL constant-buffer draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedLineListDrawCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 64,
            Height = 64,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidLineWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidLineWgsl
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            Topology = DxPrimitiveTopology.LineList,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 64, 64));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.Draw(2);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        Assert.Contains(
            Enumerable.Range(0, 64 * 64),
            index =>
            {
                var pixel = ReadRgbaPixel(pixels, 64, index % 64, index / 64);
                return pixel.R < 50 && pixel.G > 150 && pixel.B < 50 && pixel.A > 200;
            });
    }

    [Fact]
    public void FlushSubmitsGpuBackedWireframeTriangleDrawCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 64,
            Height = 64,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            Topology = DxPrimitiveTopology.TriangleList,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor
            {
                FillMode = DxFillMode.Wireframe,
                CullMode = DxCullMode.None
            }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 64, 64));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.Draw(3);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Equal(1ul, context.SubmittedWireframeDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        Assert.Contains(
            Enumerable.Range(0, 64 * 64),
            index =>
            {
                var pixel = ReadRgbaPixel(pixels, 64, index % 64, index / 64);
                return pixel.R > 150 && pixel.G < 50 && pixel.B < 50 && pixel.A > 200;
            });

        var center = ReadRgbaPixel(pixels, 64, 32, 32);
        Assert.True(center.R < 50 && center.G < 50 && center.B < 50, $"Expected unfilled wireframe center, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedIndexedWireframeTriangleDrawCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 64,
            Height = 64,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var indexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 6,
            Usage = DxBufferUsage.Index | DxBufferUsage.CopyDestination
        });
        indexBuffer.Write<ushort>([0, 1, 2]);
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            Topology = DxPrimitiveTopology.TriangleList,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor
            {
                FillMode = DxFillMode.Wireframe,
                CullMode = DxCullMode.None
            }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 64, 64));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.SetIndexBuffer(indexBuffer, DxIndexFormat.UInt16);
        context.DrawIndexed(3, indexFormat: DxIndexFormat.UInt16);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Equal(1ul, context.SubmittedWireframeDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        Assert.Contains(
            Enumerable.Range(0, 64 * 64),
            index =>
            {
                var pixel = ReadRgbaPixel(pixels, 64, index % 64, index / 64);
                return pixel.R > 150 && pixel.G < 50 && pixel.B < 50 && pixel.A > 200;
            });

        var center = ReadRgbaPixel(pixels, 64, 32, 32);
        Assert.True(center.R < 50 && center.G < 50 && center.B < 50, $"Expected unfilled indexed wireframe center, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedDepthClearAndDepthDrawCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var depth = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.D24UnormS8UInt,
            Usage = DxTextureUsage.DepthStencil
        });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            DepthStencilFormat = DxResourceFormat.D24UnormS8UInt,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None },
            DepthStencilState = new DxDepthStencilStateDescriptor
            {
                DepthEnable = true,
                DepthWriteMask = DxDepthWriteMask.All,
                DepthFunction = DxComparisonFunction.LessEqual
            }
        });
        using var context = device.CreateImmediateContext();

        context.ClearDepthStencil(depth, DxDepthStencilClearFlags.DepthStencil, depth: 1f, stencil: 0);
        context.SetRenderTargets(target, depth);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.SetGraphicsPipeline(pipeline);
        context.Draw(3);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after DirectX depth draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after DirectX depth draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after DirectX depth draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after DirectX depth draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedStencilReferenceAndOperations()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var target = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var depthStencil = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.D24UnormS8UInt,
            Usage = DxTextureUsage.DepthStencil
        });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var stencilWritePipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            DepthStencilFormat = DxResourceFormat.D24UnormS8UInt,
            BlendState = new DxBlendStateDescriptor
            {
                EnableBlend = false,
                WriteMask = DxColorWriteMask.None
            },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None },
            DepthStencilState = new DxDepthStencilStateDescriptor
            {
                StencilEnable = true,
                StencilReference = 1,
                StencilWriteMask = 0xFF,
                FrontFace = new DxStencilFaceDescriptor
                {
                    Function = DxComparisonFunction.Always,
                    PassOperation = DxStencilOperation.Replace
                },
                BackFace = new DxStencilFaceDescriptor
                {
                    Function = DxComparisonFunction.Always,
                    PassOperation = DxStencilOperation.Replace
                }
            }
        });
        using var stencilTestPipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            DepthStencilFormat = DxResourceFormat.D24UnormS8UInt,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None },
            DepthStencilState = new DxDepthStencilStateDescriptor
            {
                StencilEnable = true,
                StencilReference = 1,
                StencilReadMask = 0xFF,
                FrontFace = new DxStencilFaceDescriptor
                {
                    Function = DxComparisonFunction.Equal
                },
                BackFace = new DxStencilFaceDescriptor
                {
                    Function = DxComparisonFunction.Equal
                }
            }
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(target, DxColor.Black);
        context.ClearDepthStencil(depthStencil, DxDepthStencilClearFlags.DepthStencil, depth: 1f, stencil: 0);
        context.SetRenderTargets(target, depthStencil);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.SetGraphicsPipeline(stencilWritePipeline);
        context.Draw(3);
        context.SetGraphicsPipeline(stencilTestPipeline);
        context.Draw(3);
        context.Flush();

        Assert.Equal(2ul, context.SubmittedClearCount);
        Assert.Equal(2ul, context.SubmittedDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected stencil-gated red center pixel, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after stencil test, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after stencil test, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after stencil test, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedTextureCopyCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var source = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource
        });
        using var destination = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopyDestination | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(source, new DxColor(0f, 1f, 0f, 1f));
        context.CopyResource(destination, source);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedCopyCount);
        Assert.Empty(context.Commands);

        var pixels = destination.ReadPixels();
        var center = ReadRgbaPixel(pixels, 16, 8, 8);
        Assert.True(center.R < 50, $"Expected low red copied pixel, actual: {center}");
        Assert.True(center.G > 200, $"Expected green copied pixel, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue copied pixel, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque copied pixel, actual: {center}");
    }

    [Fact]
    public void ResolveSubresourceRecordsDirectXResolveMetadata()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var source = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget,
            SampleCount = 4
        });
        using var destination = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.ShaderResource
        });
        using var context = device.CreateImmediateContext();

        context.ResolveSubresource(destination, 0, source, 0, DxResourceFormat.R8G8B8A8Unorm);

        var command = Assert.Single(context.Commands);
        Assert.Equal(ProGpuDirectXCommandKind.ResolveTexture, command.Kind);
        Assert.Same(source, command.SourceTexture);
        Assert.Same(destination, command.DestinationTexture);
        Assert.Equal(new DxResolveSubresourceCall(0, 0, DxResourceFormat.R8G8B8A8Unorm), command.Resolve);
    }

    [Fact]
    public void FlushSubmitsGpuBackedMultisampleResolveCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var source = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget,
            SampleCount = 4
        });
        using var destination = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(source, new DxColor(0f, 1f, 0f, 1f));
        context.ResolveResource(destination, source);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedResolveCount);
        Assert.Empty(context.Commands);

        var pixels = destination.ReadPixels();
        var center = ReadRgbaPixel(pixels, 16, 8, 8);
        Assert.True(center.R < 50, $"Expected low red resolved pixel, actual: {center}");
        Assert.True(center.G > 200, $"Expected green resolved pixel, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue resolved pixel, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque resolved pixel, actual: {center}");
    }

    [Fact]
    public void CpuReadableBuffersSupportMetadataShadowCopies()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var source = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopySource | DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var destination = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var context = device.CreateImmediateContext();

        source.Write<uint>([10, 20, 30, 40]);
        context.CopyResource(destination, source);
        context.Flush();

        Assert.Equal([10u, 20u, 30u, 40u], destination.Read<uint>(4));
    }

    [Fact]
    public void CpuReadableBuffersSupportGpuStagingCopies()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var source = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopySource | DxBufferUsage.CopyDestination
        });
        using var staging = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var context = device.CreateImmediateContext();

        source.Write<uint>([1, 3, 5, 7]);
        context.CopyResource(staging, source);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedCopyCount);
        Assert.Equal([1u, 3u, 5u, 7u], staging.Read<uint>(4));
    }

    [Fact]
    public void CpuReadableBuffersSupportUnalignedByteReads()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var source = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopySource | DxBufferUsage.CopyDestination
        });
        using var staging = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var context = device.CreateImmediateContext();

        source.Write<byte>([
            0xA0, 0xA1, 0xA2, 0xA3,
            0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xAB,
            0xAC, 0xAD, 0xAE, 0xAF
        ]);
        context.CopyResource(staging, source);
        context.Flush();

        var expected = new byte[] { 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7 };
        Assert.Equal(expected, staging.ReadBytes(offsetBytes: 1, sizeInBytes: 7));
        Assert.Equal(expected, source.BackendBuffer!.ReadBytes(offsetBytes: 1, sizeBytes: 7));
    }

    [Fact]
    public void TextureMapWriteDiscardAndReadBackUsesDirectXPitches()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopySource | DxTextureUsage.CopyDestination | DxTextureUsage.ShaderResource,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write
        });
        using var context = device.CreateImmediateContext();
        byte[] pixels =
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ];

        using var writeMap = context.Map(texture, DxMapMode.WriteDiscard);
        Assert.True(texture.IsMapped);
        Assert.Same(texture, writeMap.Texture);
        Assert.Equal(8u, writeMap.RowPitch);
        Assert.Equal(16u, writeMap.DepthPitch);
        writeMap.Write<byte>(pixels);
        Assert.Throws<InvalidOperationException>(() => context.Map(texture, DxMapMode.Read));
        context.Unmap(writeMap);

        Assert.False(texture.IsMapped);
        Assert.Equal(16u, texture.LastWriteSizeInBytes);
        Assert.Equal(pixels, texture.ReadPixels());

        using var readMap = context.Map(texture, DxMapMode.Read);
        Assert.Equal(pixels, readMap.Read<byte>(16));
    }

    [Fact]
    public void TextureMapRejectsInvalidAccessAndUnsupportedSubresources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var gpuOnly = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopyDestination
        });
        using var readOnly = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var writeOnly = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Write
        });
        using var multisampled = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget,
            CpuAccess = DxCpuAccessFlags.Write,
            SampleCount = 4
        });
        using var arrayTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Write,
            ArraySize = 2
        });

        Assert.Throws<InvalidOperationException>(() => gpuOnly.Map(DxMapMode.Write));
        Assert.Throws<InvalidOperationException>(() => gpuOnly.Map(DxMapMode.Read));
        Assert.Throws<InvalidOperationException>(() => readOnly.Map(DxMapMode.Write));
        Assert.Throws<InvalidOperationException>(() => writeOnly.Map(DxMapMode.Read));
        Assert.Throws<NotSupportedException>(() => writeOnly.Map(DxMapMode.Write, subresource: 1));
        Assert.Throws<NotSupportedException>(() => multisampled.Map(DxMapMode.Write));
        Assert.Throws<NotSupportedException>(() => arrayTexture.Map(DxMapMode.Write));
    }

    [Fact]
    public void TextureMapWriteUploadsGpuBackedTextureOnUnmap()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopyDestination | DxTextureUsage.CopySource | DxTextureUsage.ShaderResource,
            CpuAccess = DxCpuAccessFlags.Write
        });
        using var context = device.CreateImmediateContext();
        byte[] pixels =
        [
            10, 20, 30, 255, 40, 50, 60, 255,
            70, 80, 90, 255, 100, 110, 120, 255
        ];

        using var mapping = context.Map(texture, DxMapMode.WriteDiscard);
        mapping.Write<byte>(pixels);
        mapping.Unmap();

        Assert.Equal(16u, texture.LastWriteSizeInBytes);
        Assert.Equal(pixels, texture.BackendTexture!.ReadPixels());
    }

    [Fact]
    public void BuffersSupportContextMapWriteDiscardAndReadBack()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var buffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopySource | DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write
        });
        using var context = device.CreateImmediateContext();

        using var writeMap = context.Map(buffer, DxMapMode.WriteDiscard);
        Assert.True(buffer.IsMapped);
        Assert.Equal(DxMapMode.WriteDiscard, writeMap.Mode);
        Assert.Equal(16u, writeMap.RowPitch);
        writeMap.Write<uint>([10, 20, 30, 40]);
        Assert.Throws<InvalidOperationException>(() => context.Map(buffer, DxMapMode.Write));
        context.Unmap(writeMap);

        Assert.False(buffer.IsMapped);
        Assert.Equal(0u, buffer.LastWriteOffsetInBytes);
        Assert.Equal(16u, buffer.LastWriteSizeInBytes);
        Assert.Equal([10u, 20u, 30u, 40u], buffer.Read<uint>(4));

        using var readMap = context.Map(buffer, DxMapMode.Read);
        Assert.Equal([10u, 20u, 30u, 40u], readMap.Read<uint>(4));
    }

    [Fact]
    public void BufferMapWriteNoOverwritePreservesUnmappedBytes()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var buffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 8,
            Usage = DxBufferUsage.CopySource | DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write
        });

        buffer.Write<byte>([0, 1, 2, 3, 4, 5, 6, 7]);
        using var mapping = buffer.Map(DxMapMode.WriteNoOverwrite, offsetBytes: 2, sizeInBytes: 3);
        mapping.Write<byte>([0xA0, 0xA1, 0xA2]);
        mapping.Unmap();

        Assert.Equal(2u, buffer.LastWriteOffsetInBytes);
        Assert.Equal(3u, buffer.LastWriteSizeInBytes);
        Assert.Equal(
            [0, 1, 0xA0, 0xA1, 0xA2, 5, 6, 7],
            buffer.ReadBytes());
    }

    [Fact]
    public void BufferMapRejectsInvalidAccessAndRanges()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var gpuOnly = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination
        });
        using var readOnly = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopySource | DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var writeOnly = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Write
        });

        Assert.Throws<InvalidOperationException>(() => gpuOnly.Map(DxMapMode.Write));
        Assert.Throws<InvalidOperationException>(() => gpuOnly.Map(DxMapMode.Read));
        Assert.Throws<InvalidOperationException>(() => readOnly.Map(DxMapMode.Write));
        Assert.Throws<InvalidOperationException>(() => writeOnly.Map(DxMapMode.Read));
        Assert.Throws<ArgumentOutOfRangeException>(() => writeOnly.Map(DxMapMode.Write, offsetBytes: 15, sizeInBytes: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => writeOnly.Map((DxMapMode)99));
    }

    [Fact]
    public void BufferMapWriteUploadsGpuBackedRangeOnUnmap()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var buffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopySource | DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Write
        });
        using var context = device.CreateImmediateContext();

        using var mapping = context.Map(buffer, DxMapMode.WriteDiscard);
        mapping.Write<uint>([100, 200, 300, 400]);
        mapping.Unmap();

        Assert.Equal(0u, buffer.LastWriteOffsetInBytes);
        Assert.Equal(16u, buffer.LastWriteSizeInBytes);
        Assert.Equal(
            [100u, 200u, 300u, 400u],
            MemoryMarshal.Cast<byte, uint>(buffer.BackendBuffer!.ReadBytes(0, 16)).ToArray());
    }

    [Fact]
    public void FlushSubmitsGpuBackedComputeDispatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = ComputeWgsl
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });
        using var context = device.CreateImmediateContext();

        context.SetComputePipeline(pipeline);
        context.Dispatch(1, 1, 1);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedDispatchCount);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void CanCreateAndBindShaderResourcesAndSamplers()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 256,
            Height = 128,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopySource
        });
        using var structuredBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 4096,
            Usage = DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 32
        });
        using var textureView = device.CreateShaderResourceView(texture);
        using var bufferView = device.CreateShaderResourceView(
            structuredBuffer,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                FirstElement = 0,
                ElementCount = 128,
                ElementStrideInBytes = 32
            });
        using var sampler = device.CreateSamplerState(new DxSamplerDescriptor
        {
            Filter = DxFilter.Anisotropic,
            AddressU = DxTextureAddressMode.Wrap,
            AddressV = DxTextureAddressMode.Clamp,
            MaximumAnisotropy = 4
        });
        using var context = device.CreateImmediateContext();

        context.SetShaderResource(DxShaderStage.Pixel, 0, textureView);
        context.SetShaderResource(DxShaderStage.Vertex, 1, bufferView);
        context.SetSampler(DxShaderStage.Pixel, 0, sampler);

        var pixelSlot = new DxShaderResourceBinding(DxShaderStage.Pixel, 0);
        var vertexSlot = new DxShaderResourceBinding(DxShaderStage.Vertex, 1);

        Assert.False(textureView.HasBackendTextureView);
        Assert.False(sampler.HasBackendSampler);
        Assert.Same(textureView, context.ShaderResourceViews[pixelSlot]);
        Assert.Same(bufferView, context.ShaderResourceViews[vertexSlot]);
        Assert.Same(sampler, context.Samplers[pixelSlot]);
        Assert.Equal(ProGpuDirectXCommandKind.SetShaderResource, context.Commands[0].Kind);
        Assert.Equal(pixelSlot, context.Commands[0].ResourceBinding);
        Assert.Equal(ProGpuDirectXCommandKind.SetSampler, context.Commands[2].Kind);
        Assert.Equal(4, sampler.Descriptor.MaximumAnisotropy);
    }

    [Fact]
    public void DrawCommandsCaptureGraphicsBindingSnapshot()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var vertexConstants = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 256,
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination
        });
        using var pixelConstants = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 128,
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination
        });
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 64,
            Height = 64,
            Usage = DxTextureUsage.ShaderResource
        });
        using var textureView = device.CreateShaderResourceView(texture);
        using var sampler = device.CreateSamplerState(new DxSamplerDescriptor());
        using var context = device.CreateImmediateContext();

        context.SetConstantBuffer(DxShaderStage.Vertex, 0, vertexConstants);
        context.SetConstantBuffer(DxShaderStage.Pixel, 1, pixelConstants);
        context.SetShaderResource(DxShaderStage.Pixel, 0, textureView);
        context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        context.Draw(3);

        var snapshot = context.Commands[^1].BindingSnapshot;

        Assert.NotNull(snapshot);
        Assert.False(snapshot.HasBackendBindGroup);
        Assert.Equal(DxShaderStageFlags.AllGraphics, snapshot.StageMask);
        Assert.Equal(4, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.ConstantBuffer &&
            entry.Stage == DxShaderStage.Vertex &&
            entry.Slot == 0 &&
            ReferenceEquals(entry.ConstantBuffer, vertexConstants));
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.ConstantBuffer &&
            entry.Stage == DxShaderStage.Pixel &&
            entry.Slot == 1 &&
            ReferenceEquals(entry.ConstantBuffer, pixelConstants));
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.ShaderResourceView &&
            entry.Stage == DxShaderStage.Pixel &&
            entry.Slot == 0 &&
            ReferenceEquals(entry.ShaderResourceView, textureView));
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.Sampler &&
            entry.Stage == DxShaderStage.Pixel &&
            entry.Slot == 0 &&
            ReferenceEquals(entry.Sampler, sampler));
        Assert.Contains("ConstantBuffer:Vertex:0", snapshot.BindingKey, StringComparison.Ordinal);
        Assert.Contains("ShaderResourceView:Pixel:0", snapshot.BindingKey, StringComparison.Ordinal);
        Assert.Contains("Sampler:Pixel:0", snapshot.BindingKey, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchCommandsCaptureComputeBindingSnapshotWithUavMetadata()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var constants = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 256,
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination
        });
        using var input = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 1024,
            Usage = DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 16
        });
        using var output = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 1024,
            Usage = DxBufferUsage.Structured | DxBufferUsage.UnorderedAccess | DxBufferUsage.CopySource,
            StrideInBytes = 16
        });
        using var inputView = device.CreateShaderResourceView(
            input,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 64,
                ElementStrideInBytes = 16
            });
        using var outputView = device.CreateUnorderedAccessView(
            output,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 64,
                ElementStrideInBytes = 16
            });
        using var context = device.CreateImmediateContext();

        context.SetConstantBuffer(DxShaderStage.Compute, 0, constants);
        context.SetShaderResource(DxShaderStage.Compute, 1, inputView);
        context.SetUnorderedAccessView(2, outputView);
        context.Dispatch(4, 2, 1);

        var snapshot = context.Commands[^1].BindingSnapshot;

        Assert.NotNull(snapshot);
        Assert.Equal(DxShaderStageFlags.Compute, snapshot.StageMask);
        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.ConstantBuffer &&
            entry.Stage == DxShaderStage.Compute &&
            entry.Slot == 0 &&
            ReferenceEquals(entry.ConstantBuffer, constants));
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.ShaderResourceView &&
            entry.Stage == DxShaderStage.Compute &&
            entry.Slot == 1 &&
            ReferenceEquals(entry.ShaderResourceView, inputView));
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.UnorderedAccessView &&
            entry.Stage == DxShaderStage.Compute &&
            entry.Slot == 2 &&
            ReferenceEquals(entry.UnorderedAccessView, outputView));
        Assert.Contains("UnorderedAccessView:Compute:2", snapshot.BindingKey, StringComparison.Ordinal);
    }

    [Fact]
    public void CanCreateUnorderedAccessViewsAndRecordCopies()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var sourceTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 64,
            Height = 64,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopySource
        });
        using var destinationTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 64,
            Height = 64,
            Usage = DxTextureUsage.UnorderedAccess | DxTextureUsage.CopyDestination | DxTextureUsage.ShaderResource
        });
        using var sourceBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 256,
            Usage = DxBufferUsage.CopySource
        });
        using var destinationBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 512,
            Usage = DxBufferUsage.CopyDestination
        });
        using var uav = device.CreateUnorderedAccessView(destinationTexture);
        using var context = device.CreateImmediateContext();

        context.SetUnorderedAccessView(0, uav);
        context.CopyResource(destinationTexture, sourceTexture);
        context.CopyResource(destinationBuffer, sourceBuffer);

        Assert.False(uav.HasBackendTextureView);
        Assert.Same(uav, context.UnorderedAccessViews[0]);
        Assert.Equal(ProGpuDirectXCommandKind.SetUnorderedAccessView, context.Commands[0].Kind);
        Assert.Equal(ProGpuDirectXCommandKind.CopyTexture, context.Commands[1].Kind);
        Assert.Same(sourceTexture, context.Commands[1].SourceTexture);
        Assert.Same(destinationTexture, context.Commands[1].DestinationTexture);
        Assert.Equal(ProGpuDirectXCommandKind.CopyBuffer, context.Commands[2].Kind);
        Assert.Same(sourceBuffer, context.Commands[2].SourceBuffer);
        Assert.Same(destinationBuffer, context.Commands[2].DestinationBuffer);
    }

    [Fact]
    public void TextureUnorderedAccessViewsCreateGpuBackedBindGroups()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.UnorderedAccess | DxTextureUsage.ShaderResource | DxTextureUsage.CopySource
        });
        using var uav = device.CreateUnorderedAccessView(
            texture,
            new DxUnorderedAccessViewDescriptor
            {
                Format = DxResourceFormat.R8G8B8A8Unorm,
                Label = "GpuTextureUav"
            });
        using var context = device.CreateImmediateContext();

        context.SetUnorderedAccessView(0, uav);
        using var snapshot = context.CreateBindingSnapshot(DxShaderStage.Compute, "Texture UAV Snapshot");

        Assert.True(uav.HasBackendTextureView);
        Assert.Equal(DxUnorderedAccessViewAccess.WriteOnly, uav.Descriptor.Access);
        Assert.True(snapshot.HasBackendBindGroup);
        Assert.Single(snapshot.Entries);
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.UnorderedAccessView &&
            entry.Stage == DxShaderStage.Compute &&
            entry.Slot == 0 &&
            ReferenceEquals(entry.UnorderedAccessView, uav));
        Assert.Contains("uav-texture", snapshot.BindingKey, StringComparison.Ordinal);
    }

    [Fact]
    public void UnorderedAccessViewsPreserveAccessMetadata()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Format = DxResourceFormat.R32Float,
            Usage = DxTextureUsage.UnorderedAccess | DxTextureUsage.ShaderResource
        });
        using var buffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.UnorderedAccess,
            StrideInBytes = 4
        });

        using var readOnlyTextureUav = device.CreateUnorderedAccessView(
            texture,
            new DxUnorderedAccessViewDescriptor
            {
                Format = DxResourceFormat.R32Float,
                Access = DxUnorderedAccessViewAccess.ReadOnly,
                Label = "ReadOnlyTextureUav"
            });
        using var readWriteBufferUav = device.CreateUnorderedAccessView(
            buffer,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                Access = DxUnorderedAccessViewAccess.ReadWrite,
                ElementCount = 16,
                ElementStrideInBytes = 4,
                Label = "ReadWriteBufferUav"
            });

        Assert.Equal(DxUnorderedAccessViewAccess.ReadOnly, readOnlyTextureUav.Descriptor.Access);
        Assert.Equal(DxUnorderedAccessViewAccess.ReadWrite, readWriteBufferUav.Descriptor.Access);
    }

    [Fact]
    public void ReadWriteTextureUnorderedAccessViewsRespectDeviceFeature()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Format = DxResourceFormat.R32Float,
            Usage = DxTextureUsage.UnorderedAccess | DxTextureUsage.ShaderResource | DxTextureUsage.CopySource
        });
        using var uav = device.CreateUnorderedAccessView(
            texture,
            new DxUnorderedAccessViewDescriptor
            {
                Format = DxResourceFormat.R32Float,
                Access = DxUnorderedAccessViewAccess.ReadWrite,
                Label = "ReadWriteTextureUav"
            });
        using var context = device.CreateImmediateContext();

        context.SetUnorderedAccessView(0, uav);
        using var snapshot = context.CreateBindingSnapshot(DxShaderStage.Compute, "ReadWrite Texture UAV Snapshot");

        Assert.True(uav.HasBackendTextureView);
        Assert.Equal(
            device.Context!.SupportsReadOnlyAndReadWriteStorageTextures,
            device.Capabilities.SupportsReadWriteStorageTextures);
        Assert.Equal(device.Capabilities.SupportsReadWriteStorageTextures, snapshot.HasBackendBindGroup);
        Assert.Contains(snapshot.Entries, entry =>
            entry.Kind == ProGpuDirectXBindingKind.UnorderedAccessView &&
            entry.Stage == DxShaderStage.Compute &&
            entry.Slot == 0 &&
            entry.UnorderedAccessView?.Descriptor.Access == DxUnorderedAccessViewAccess.ReadWrite &&
            ReferenceEquals(entry.UnorderedAccessView, uav));
    }

    [Fact]
    public void InvalidDescriptorsAreRejected()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateTexture2D(new DxTexture2DDescriptor
            {
                Width = 0,
                Height = 1
            }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateBuffer(new DxBufferDescriptor
            {
                SizeInBytes = 0
            }));

        Assert.Throws<ArgumentException>(() =>
            device.CreateBuffer(new DxBufferDescriptor
            {
                SizeInBytes = 16,
                Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
                CpuAccess = DxCpuAccessFlags.Read
            }));

        Assert.Throws<ArgumentException>(() =>
            device.CreateShader(new DxShaderDescriptor
            {
                Stage = DxShaderStage.Pixel,
                SourceKind = DxShaderSourceKind.Wgsl,
                Source = ""
            }));

        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        Assert.Throws<ArgumentException>(() =>
            device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
            {
                VertexShader = vertexShader,
                DepthStencilFormat = DxResourceFormat.D32Float,
                DepthStencilState = new DxDepthStencilStateDescriptor
                {
                    StencilEnable = true
                }
            }));

        using var renderOnlyTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Usage = DxTextureUsage.RenderTarget
        });
        Assert.Throws<ArgumentException>(() => device.CreateShaderResourceView(renderOnlyTexture));
        Assert.Throws<InvalidOperationException>(() => renderOnlyTexture.ReadPixels());

        using var shaderBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 128,
            Usage = DxBufferUsage.ShaderResource
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateShaderResourceView(
                shaderBuffer,
                new DxShaderResourceViewDescriptor
                {
                    Dimension = DxResourceViewDimension.Buffer,
                    ElementCount = 0
                }));

        using var unorderedTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Usage = DxTextureUsage.UnorderedAccess
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateUnorderedAccessView(
                unorderedTexture,
                new DxUnorderedAccessViewDescriptor
                {
                    Access = (DxUnorderedAccessViewAccess)99
                }));

        using var unorderedBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.UnorderedAccess
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateUnorderedAccessView(
                unorderedBuffer,
                new DxUnorderedAccessViewDescriptor
                {
                    Dimension = DxResourceViewDimension.Buffer,
                    Access = (DxUnorderedAccessViewAccess)99,
                    ElementCount = 16,
                    ElementStrideInBytes = 4
                }));

        using var copySource = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Usage = DxTextureUsage.CopySource
        });
        using var mismatchedDestination = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 16,
            Usage = DxTextureUsage.CopyDestination
        });
        using var context = device.CreateImmediateContext();
        Assert.Throws<ArgumentOutOfRangeException>(() => context.CopyResource(mismatchedDestination, copySource));

        Assert.Throws<ArgumentException>(() => context.ClearDepthStencil(renderOnlyTexture));

        using var vertexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 128,
            Usage = DxBufferUsage.Vertex
        });
        Assert.Throws<ArgumentException>(() =>
            context.SetConstantBuffer(DxShaderStage.Vertex, 0, vertexBuffer));
    }

    private static (byte R, byte G, byte B, byte A) ReadRgbaPixel(byte[] pixels, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }
}
