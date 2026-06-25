using ProGPU.DirectX;
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
        Assert.Equal("vs_main", shader.EntryPoint);
        Assert.NotEqual(string.Empty, shader.SourceHash);
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
            device.CreateShader(new DxShaderDescriptor
            {
                Stage = DxShaderStage.Pixel,
                SourceKind = DxShaderSourceKind.Wgsl,
                Source = ""
            }));

        using var renderOnlyTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Usage = DxTextureUsage.RenderTarget
        });
        Assert.Throws<ArgumentException>(() => device.CreateShaderResourceView(renderOnlyTexture));

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
    }
}
