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

    private const string RwStructuredBufferComputeHlsl = """
RWStructuredBuffer<float4> Output : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output[id.x] = float4(0.0, 1.0, 0.0, 1.0);
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

    private const string InstancedVertexHlsl = """
struct VertexInput
{
    float2 position : POSITION0;
    float2 offset : TEXCOORD0;
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
    float2 translated = input.position + input.offset;
    output.position = float4(translated, 0.0, 1.0);
    output.color = input.color;
    return output;
}
""";

    private const string SystemValueStructInstancedVertexHlsl = """
StructuredBuffer<float2> TrianglePositions : register(t0);
StructuredBuffer<float2> InstanceOffsets : register(t1);
StructuredBuffer<float4> InstanceColors : register(t2);

struct VertexInput
{
    uint vertexId : SV_VertexID;
    uint instanceId : SV_InstanceID;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float4 color : COLOR0;
};

VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    float2 position = TrianglePositions[input.vertexId] + InstanceOffsets[input.instanceId];
    output.position = float4(position, 0.0, 1.0);
    output.color = InstanceColors[input.instanceId];
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
    output.position = mul(float4(input.position, 1.0), WorldViewProjection);
    output.color = input.color;
    return output;
}
""";

    private const string TexturedTransformVertexHlsl = """
cbuffer Transform : register(b0)
{
    float4x4 WorldViewProjection;
};

struct VertexInput
{
    float3 position : POSITION;
    float2 uv : TEXCOORD0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.position = mul(WorldViewProjection, float4(input.position, 1.0));
    output.uv = input.uv;
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

    private const string TextureSamplePixelHlsl = """
Texture2D SourceTexture : register(t0);
SamplerState SourceSampler : register(s0);

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

float4 PSMain(VertexOutput input) : SV_Target
{
    return SourceTexture.Sample(SourceSampler, input.uv);
}
""";

    private const string TextureSampleLevelTintPixelHlsl = """
Texture2D SourceTexture : register(t0);
SamplerState SourceSampler : register(s0);

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

float4 PSMain(VertexOutput input) : SV_Target
{
    float4 sampled = SourceTexture.SampleLevel(SourceSampler, input.uv, 0.0);
    float4 loaded = SourceTexture.Load(int3(0, 0, 0), int2(0, 0));
    float4 biased = SourceTexture.SampleBias(SourceSampler, input.uv, 0.0);
    float4 grad = SourceTexture.SampleGrad(SourceSampler, input.uv, float2(0.0, 0.0), float2(0.0, 0.0));
    float3 normal = normalize(float3(sampled.r, sampled.g, sampled.b));
    float light = max(dot(normal, normalize(float3(1.0, 1.0, 1.0))), 0.0);
    float falloff = pow(sqrt(light), 1.0);
    float mask = saturate(lerp(0.0, sampled.r * 2.0, frac(1.5)) * falloff * loaded.r * biased.r * grad.r);
    return float4(mask, 0.0, 0.0, sampled.a * loaded.a * biased.a * grad.a);
}
""";

    private const string StructuredBufferVertexHlsl = """
StructuredBuffer<float4> Positions : register(t0);

float4 VSMain(uint vertexId : SV_VertexID) : SV_Position
{
    float4 position = Positions[vertexId];
    return float4(position.xy, 0.0, 1.0);
}
""";

    private const string TypedBufferVertexHlsl = """
Buffer<float4> Positions : register(t0);

float4 VSMain(uint vertexId : SV_VertexID) : SV_Position
{
    float4 position = Positions[vertexId];
    return float4(position.xy, 0.0, 1.0);
}
""";

    private const string StructuredBufferRecordVertexHlsl = """
struct ChartPoint
{
    float4 position;
    float4 color;
};

StructuredBuffer<ChartPoint> Points : register(t0);

struct VertexOutput
{
    float4 position : SV_Position;
    float4 color : COLOR0;
};

VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    ChartPoint point = Points[vertexId];
    VertexOutput output;
    output.position = float4(point.position.xy, 0.0, 1.0);
    output.color = point.color;
    return output;
}
""";

    private const string RwTypedBufferComputeHlsl = """
RWBuffer<float4> Output : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output[id.x] = float4(0.0, 0.0, 1.0, 1.0);
}
""";

    private const string ByteAddressBufferComputeHlsl = """
ByteAddressBuffer Input : register(t0);
RWByteAddressBuffer Output : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint4 values = Input.Load4(id.x * 16u);
    Output.Store(id.x * 8u, values.x + values.y);
    Output.Store(id.x * 8u + 4u, values.z + values.w);
}
""";

    private const string ByteAddressBufferInterlockedComputeHlsl = """
RWByteAddressBuffer Output : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output.Store(0u, 10u);
    Output.Store(8u, 0u);
    uint previous;
    Output.InterlockedAdd(0u, 5u, previous);
    Output.Store(4u, previous);
    Output.InterlockedOr(8u, 2u);
    Output.InterlockedXor(8u, 3u);
}
""";

    private const string ByteAddressBufferCompareExchangeComputeHlsl = """
RWByteAddressBuffer Output : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output.Store(0u, 12u);
    uint compareOriginal;
    Output.InterlockedCompareExchange(0u, 12u, 99u, compareOriginal);
    Output.Store(4u, compareOriginal);
    uint failedOriginal;
    Output.InterlockedCompareExchange(0u, 12u, 1u, failedOriginal);
    Output.Store(8u, failedOriginal);
}
""";

    private const string SolidGreenPixelHlsl = """
float4 PSMain() : SV_Target
{
    return float4(0.0, 1.0, 0.0, 1.0);
}
""";

    private const string ConditionalPixelHlsl = """
float4 PSMain() : SV_Target
{
    float value = 1.0 > 0.5 ? 1.0 : 0.0;
    return float4(value, 0.0, 0.0, 1.0);
}
""";

    private const string ClippedPixelHlsl = """
float4 PSMain() : SV_Target
{
    clip(-1.0);
    return float4(1.0, 0.0, 0.0, 1.0);
}
""";

    private const string FrontFacingDepthPixelHlsl = """
struct PixelInput
{
    float4 position : SV_Position;
    bool isFrontFace : SV_IsFrontFace;
};

struct PixelOutput
{
    float4 color : SV_Target;
    float depth : SV_Depth;
};

PixelOutput PSMain(PixelInput input)
{
    PixelOutput output;
    output.color = float4(1.0, 0.0, 0.0, 1.0);
    output.depth = 0.25;
    return output;
}
""";

    private const string DepthOutputPixelHlsl = """
struct PixelOutput
{
    float4 color : SV_Target;
    float depth : SV_Depth;
};

PixelOutput PSMain()
{
    PixelOutput output;
    output.color = float4(1.0, 0.0, 0.0, 1.0);
    output.depth = 0.25;
    return output;
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
    public void SciChartRenderContextCreatesTexturesAndRecordsTextureDraws()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        using var texture = renderContext.CreateTexture(2, 2);
        int[] pixels =
        [
            unchecked((int)0xFFFF0000),
            unchecked((int)0xFFFF0000),
            unchecked((int)0xFFFF0000),
            unchecked((int)0xFFFF0000)
        ];

        texture.SetData(pixels);
        renderContext.DrawTexture(
            texture,
            new DxRect(4, 6, 20, 10),
            ProGpuDirectXSciChartTextureFiltering.Point);

        Assert.Equal(ProGpuDirectXSciChartTextureFormat.Bgra8, texture.TextureFormat);
        Assert.Equal(DxResourceFormat.B8G8R8A8Unorm, texture.Resource.Descriptor.Format);
        Assert.True(texture.Generation > 0);
        Assert.Single(renderContext.TextureDraws);
        Assert.Equal(new DxRect(4, 6, 20, 10), renderContext.TextureDraws[0].ViewportRect);
        Assert.Equal(ProGpuDirectXCommandKind.Draw, renderContext.ImmediateContext.Commands[^1].Kind);

        using var floatTexture = renderContext.CreateTexture(2, 2, ProGpuDirectXSciChartTextureFormat.Float32);
        floatTexture.SetFloatData([0f, 0.25f, 0.5f, 1f]);
        Assert.Equal(DxResourceFormat.R32Float, floatTexture.Resource.Descriptor.Format);

        using var uintTexture = renderContext.CreateTexture(2, 2, ProGpuDirectXSciChartTextureFormat.UInt32);
        uintTexture.SetUIntData([0u, 1u, 2u, 3u]);
        Assert.Equal(DxResourceFormat.R32UInt, uintTexture.Resource.Descriptor.Format);
        Assert.Throws<NotSupportedException>(() =>
            renderContext.DrawTexture(uintTexture, new DxRect(0, 0, 8, 8)));
    }

    [Fact]
    public void SciChartRenderContextRecordsBatchedTextureVerticesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        using var texture = renderContext.CreateTexture(2, 2);
        texture.SetData(
        [
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF)
        ]);
        ProGpuDirectXSciChartTextureVertex[] vertices =
        [
            new(-10, -10, 0, 0, 0xFFFFFFFF),
            new(0, 0, 0, 0, 0xFF00FF00),
            new(16, 0, 1, 0, 0xFF00FF00),
            new(16, 16, 1, 1, 0xFF00FF00),
            new(0, 0, 0, 0, 0xFF00FF00),
            new(16, 16, 1, 1, 0xFF00FF00),
            new(0, 16, 0, 1, 0xFF00FF00)
        ];
        var drawVertexCount = vertices.Length - 1;

        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawTextureVertices(
            vertices,
            startIndex: 1,
            count: drawVertexCount,
            texture,
            new ProGpuDirectXSciChartVertexTransform(),
            ProGpuDirectXSciChartTextureFiltering.Point);

        Assert.Single(renderContext.TextureVertexDraws);
        Assert.Equal(new DxRect(0, 0, 8, 16), renderContext.TextureVertexDraws[0].ClipRect);
        Assert.Equal(drawVertexCount, renderContext.TextureVertexDraws[0].Vertices.Count);
        Assert.Equal(0, renderContext.TextureVertexDraws[0].Vertices[0].X);
        Assert.Equal(ProGpuDirectXCommandKind.Draw, renderContext.ImmediateContext.Commands[^1].Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawTextureVertices(vertices, vertices.Length, 1, texture, default));

        renderContext.BeginFrame();
        Assert.Empty(renderContext.TextureDraws);
        Assert.Empty(renderContext.TextureVertexDraws);
        Assert.Empty(renderContext.ShapedHeatmapDraws);
        Assert.Empty(renderContext.HeightTextureContourDraws);

        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawTextureVertices(
            vertices,
            startIndex: 1,
            count: drawVertexCount,
            texture,
            new ProGpuDirectXSciChartVertexTransform(),
            ProGpuDirectXSciChartTextureFiltering.Point);
        Assert.Empty(renderContext.TextureVertexDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsShapedHeatmapDrawsAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        using var heightsTexture = renderContext.CreateTexture(2, 2, ProGpuDirectXSciChartTextureFormat.Float32);
        using var gradientTexture = renderContext.CreateTexture(2, 1);
        heightsTexture.SetFloatData([0f, 0.5f, 0.75f, 1f]);
        gradientTexture.SetData(
        [
            unchecked((int)0xFF00FF00),
            unchecked((int)0xFF00FF00)
        ]);
        ProGpuDirectXSciChartTextureVertex[] vertices =
        [
            new(-10, -10, 0, 0, 0xFFFFFFFF),
            new(0, 0, 0, 0, 0xFFFFFFFF),
            new(16, 0, 1, 0, 0xFFFFFFFF),
            new(16, 16, 1, 1, 0xFFFFFFFF),
            new(0, 0, 0, 0, 0xFFFFFFFF),
            new(16, 16, 1, 1, 0xFFFFFFFF),
            new(0, 16, 0, 1, 0xFFFFFFFF)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawShapedHeatmap(
            vertices,
            startIndex: 1,
            count: vertices.Length - 1,
            colorMapMin: 0,
            colorMapMax: 1,
            heightsTexture,
            gradientTexture,
            ProGpuDirectXSciChartTextureFiltering.Point);

        Assert.Single(renderContext.ShapedHeatmapDraws);
        Assert.Equal(new DxRect(0, 0, 8, 16), renderContext.ShapedHeatmapDraws[0].ClipRect);
        Assert.Equal(vertices.Length - 1, renderContext.ShapedHeatmapDraws[0].Vertices.Count);
        Assert.Equal(0, renderContext.ShapedHeatmapDraws[0].Vertices[0].X);
        Assert.Equal(ProGpuDirectXCommandKind.Draw, renderContext.ImmediateContext.Commands[^1].Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawShapedHeatmap(vertices, 1, vertices.Length, 0, 1, heightsTexture, gradientTexture));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawShapedHeatmap(vertices, 1, 1, 1, 0, heightsTexture, gradientTexture));

        using var unsupportedHeights = renderContext.CreateTexture(2, 2);
        Assert.Throws<NotSupportedException>(() =>
            renderContext.DrawShapedHeatmap(vertices, 1, vertices.Length - 1, 0, 1, unsupportedHeights, gradientTexture));
    }

    [Fact]
    public void SciChartRenderContextRecordsHeightTextureContoursAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        using var heightsTexture = renderContext.CreateTexture(2, 2, ProGpuDirectXSciChartTextureFormat.Float32);
        using var contourTexture = renderContext.CreateTexture(2, 2);
        heightsTexture.SetFloatData([0f, 0.5f, 0.5f, 1f]);
        contourTexture.SetData(
        [
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF)
        ]);

        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawHeightTextureContours(
            heightsTexture,
            contourTexture,
            new DxRect(0, 0, 16, 16),
            new DxColor(0f, 1f, 0f, 1f),
            zMin: 0f,
            zMax: 1f,
            zStep: 0.5f,
            strokeThickness: 1f,
            opacity: 1f);

        Assert.Single(renderContext.HeightTextureContourDraws);
        Assert.Equal(new DxRect(0, 0, 8, 16), renderContext.HeightTextureContourDraws[0].ClipRect);
        Assert.Equal(new DxRect(0, 0, 16, 16), renderContext.HeightTextureContourDraws[0].ViewportRect);
        Assert.Equal(ProGpuDirectXCommandKind.Draw, renderContext.ImmediateContext.Commands[^1].Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawHeightTextureContours(
                heightsTexture,
                contourTexture,
                new DxRect(0, 0, 16, 16),
                new DxColor(0f, 1f, 0f, 1f),
                zMin: 0f,
                zMax: 1f,
                zStep: 0f,
                strokeThickness: 1f,
                opacity: 1f));

        using var unsupportedHeights = renderContext.CreateTexture(2, 2);
        Assert.Throws<NotSupportedException>(() =>
            renderContext.DrawHeightTextureContours(
                unsupportedHeights,
                contourTexture,
                new DxRect(0, 0, 16, 16),
                new DxColor(0f, 1f, 0f, 1f),
                zMin: 0f,
                zMax: 1f,
                zStep: 0.5f,
                strokeThickness: 1f,
                opacity: 1f));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawHeightTextureContours(
            heightsTexture,
            contourTexture,
            new DxRect(0, 0, 16, 16),
            new DxColor(0f, 1f, 0f, 1f),
            zMin: 0f,
            zMax: 1f,
            zStep: 0.5f,
            strokeThickness: 1f,
            opacity: 1f);
        Assert.Empty(renderContext.HeightTextureContourDraws);
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
Texture3D SourceTexture : register(t0);

float4 PSMain(float3 uv : TEXCOORD0) : SV_Target
{
    return float4(uv, 1.0);
}
""",
            EntryPoint = "PSMain"
        });

        Assert.False(shader.HasBackendShaderModule);
        Assert.Null(shader.BackendSource);
        Assert.Equal("PSMain", shader.EntryPoint);
    }

    [Fact]
    public void HlslTextShaderTranslatesTextureSampleCallsInsideExpressions()
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
    float4 sampled = SourceTexture.Sample(SourceSampler, float2(uv.x, uv.y));
    float4 loaded = SourceTexture.Load(int3(0, 0, 0), int2(0, 0));
    float4 biased = SourceTexture.SampleBias(SourceSampler, uv, 0.0);
    float4 grad = SourceTexture.SampleGrad(SourceSampler, uv, float2(0.0, 0.0), float2(0.0, 0.0));
    float3 normal = normalize(float3(sampled.r, sampled.g, sampled.b));
    float light = max(dot(normal, normalize(float3(1.0, 1.0, 1.0))), 0.0);
    float falloff = pow(sqrt(light), 1.0);
    float mask = saturate(lerp(0.0, sampled.r * 2.0, frac(1.5)) * falloff * loaded.r * biased.r * grad.r);
    return float4(mask, 0.0, 0.0, sampled.a * loaded.a * biased.a * grad.a) + SourceTexture.SampleLevel(SourceSampler, uv, 0.0) * float4(0.0, 0.5, 0.25, 0.0);
}
""",
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("var sampled: vec4<f32> = textureSample(SourceTexture, SourceSampler, vec2<f32>(uv.x, uv.y));", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var loaded: vec4<f32> = textureLoad(SourceTexture, ((vec3<i32>(0, 0, 0)).xy + vec2<i32>(0, 0)), (vec3<i32>(0, 0, 0)).z);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var biased: vec4<f32> = textureSampleBias(SourceTexture, SourceSampler, uv, 0.0);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var grad: vec4<f32> = textureSampleGrad(SourceTexture, SourceSampler, uv, vec2<f32>(0.0, 0.0), vec2<f32>(0.0, 0.0));", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var normal: vec3<f32> = normalize(vec3<f32>(sampled.r, sampled.g, sampled.b));", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var light: f32 = max(dot(normal, normalize(vec3<f32>(1.0, 1.0, 1.0))), 0.0);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var falloff: f32 = pow(sqrt(light), 1.0);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var mask: f32 = clamp(mix(0.0, sampled.r * 2.0, fract(1.5)) * falloff * loaded.r * biased.r * grad.r, 0.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("textureSampleLevel(SourceTexture, SourceSampler, uv, 0.0) * vec4<f32>(0.0, 0.5, 0.25, 0.0)", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesConditionalExpressions()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ConditionalPixelHlsl,
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("var value: f32 = select(0.0, 1.0, 1.0 > 0.5);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("return vec4<f32>(value, 0.0, 0.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesPixelClipToDiscard()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ClippedPixelHlsl,
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("if ((-1.0) < 0.0) {\n        discard;\n    }", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("return vec4<f32>(1.0, 0.0, 0.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderRejectsClipOutsidePixelShaders()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
[numthreads(1, 1, 1)]
void CSMain()
{
    clip(-1.0);
}
""",
            EntryPoint = "CSMain"
        });

        Assert.False(shader.HasBackendShaderModule);
        Assert.Null(shader.BackendSource);
    }

    [Fact]
    public void HlslTextShaderTranslatesFragmentFrontFaceAndDepthSystemValues()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = FrontFacingDepthPixelHlsl,
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@builtin(front_facing) isFrontFace: bool", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@builtin(frag_depth) depth: f32", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("output.depth = 0.25;", shader.BackendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("@location(0) isFrontFace: bool", shader.BackendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("@location(0) depth: f32", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesStructuredBufferResources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = StructuredBufferVertexHlsl,
            EntryPoint = "VSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(64) var<storage, read> Positions: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@vertex\nfn VSMain(@builtin(vertex_index) vertexId: u32) -> @builtin(position) vec4<f32>", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var position: vec4<f32> = Positions[vertexId];", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("return vec4<f32>(position.xy, 0.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesTypedBufferResources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = TypedBufferVertexHlsl,
            EntryPoint = "VSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(64) var<storage, read> Positions: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@vertex\nfn VSMain(@builtin(vertex_index) vertexId: u32) -> @builtin(position) vec4<f32>", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var position: vec4<f32> = Positions[vertexId];", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("return vec4<f32>(position.xy, 0.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesStructuredBufferRecordResources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = StructuredBufferRecordVertexHlsl,
            EntryPoint = "VSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("struct ChartPoint {\n    position: vec4<f32>,\n    color: vec4<f32>,\n}", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(64) var<storage, read> Points: array<ChartPoint>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@builtin(position) position: vec4<f32>", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@location(0) color: vec4<f32>", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var point: ChartPoint = Points[vertexId];", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("output.position = vec4<f32>(point.position.xy, 0.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesSystemValueFieldsInsideStructs()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = SystemValueStructInstancedVertexHlsl,
            EntryPoint = "VSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@builtin(vertex_index) vertexId: u32", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@builtin(instance_index) instanceId: u32", shader.BackendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("@location(0) vertexId: u32", shader.BackendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("@location(0) instanceId: u32", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(64) var<storage, read> TrianglePositions: array<vec2<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(65) var<storage, read> InstanceOffsets: array<vec2<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(66) var<storage, read> InstanceColors: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var position: vec2<f32> = TrianglePositions[input.vertexId] + InstanceOffsets[input.instanceId];", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesRwStructuredBufferResources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = RwStructuredBufferComputeHlsl,
            EntryPoint = "CSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@compute @workgroup_size(1, 1, 1)\nfn CSMain(@builtin(global_invocation_id) id: vec3<u32>)", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("Output[id.x] = vec4<f32>(0.0, 1.0, 0.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesRwTypedBufferResources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = RwTypedBufferComputeHlsl,
            EntryPoint = "CSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@compute @workgroup_size(1, 1, 1)\nfn CSMain(@builtin(global_invocation_id) id: vec3<u32>)", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("Output[id.x] = vec4<f32>(0.0, 0.0, 1.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesByteAddressBufferResources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ByteAddressBufferComputeHlsl,
            EntryPoint = "CSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(1600) var<storage, read> Input: array<u32>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<atomic<u32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var values: vec4<u32> = vec4<u32>(Input[((id.x * 16u) / 4u)], Input[(((id.x * 16u) / 4u) + 1u)], Input[(((id.x * 16u) / 4u) + 2u)], Input[(((id.x * 16u) / 4u) + 3u)]);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("atomicStore(&Output[((id.x * 8u) / 4u)], values.x + values.y);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("atomicStore(&Output[((id.x * 8u + 4u) / 4u)], values.z + values.w);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesRwByteAddressBufferInterlockedOperations()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ByteAddressBufferInterlockedComputeHlsl,
            EntryPoint = "CSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<atomic<u32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("atomicStore(&Output[((0u) / 4u)], 10u);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var previous: u32;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("previous = atomicAdd(&Output[((0u) / 4u)], 5u);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("atomicStore(&Output[((4u) / 4u)], previous);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("atomicOr(&Output[((8u) / 4u)], 2u);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("atomicXor(&Output[((8u) / 4u)], 3u);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesRwByteAddressBufferCompareExchange()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ByteAddressBufferCompareExchangeComputeHlsl,
            EntryPoint = "CSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<atomic<u32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var compareOriginal: u32;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("compareOriginal = atomicCompareExchangeWeak(&Output[((0u) / 4u)], 12u, 99u).old_value;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var failedOriginal: u32;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("failedOriginal = atomicCompareExchangeWeak(&Output[((0u) / 4u)], 12u, 1u).old_value;", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesVectorFirstMulAndMatrixTypes()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
cbuffer Transform : register(b0)
{
    float3x3 NormalMatrix;
};

struct VertexInput
{
    float4 position : POSITION;
    float3 normal : NORMAL0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float3 normal : NORMAL0;
};

VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.position = input.position;
    output.normal = normalize(mul(input.normal, NormalMatrix));
    return output;
}
""",
            EntryPoint = "VSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("NormalMatrix: mat3x3<f32>", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("output.normal = normalize((input.normal * transform.NormalMatrix));", shader.BackendSource, StringComparison.Ordinal);
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
    public void DrawInstancedCommandsPreserveDirectXArguments()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var indexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.Index | DxBufferUsage.CopyDestination
        });
        using var context = device.CreateImmediateContext();

        context.DrawInstanced(3, 4, 5, 6);
        context.SetIndexBuffer(indexBuffer, DxIndexFormat.UInt16);
        context.DrawIndexedInstanced(7, 8, 9, 10, 11, DxIndexFormat.UInt16);

        var draw = context.Commands[0].Draw!;
        Assert.Equal(3u, draw.VertexCount);
        Assert.Equal(4u, draw.InstanceCount);
        Assert.Equal(5u, draw.StartVertexLocation);
        Assert.Equal(6u, draw.StartInstanceLocation);

        var indexedDraw = context.Commands[^1].DrawIndexed!;
        Assert.Equal(7u, indexedDraw.IndexCount);
        Assert.Equal(8u, indexedDraw.InstanceCount);
        Assert.Equal(9u, indexedDraw.StartIndexLocation);
        Assert.Equal(10, indexedDraw.BaseVertexLocation);
        Assert.Equal(11u, indexedDraw.StartInstanceLocation);
        Assert.Equal(DxIndexFormat.UInt16, indexedDraw.IndexFormat);
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
    public void FlushSubmitsGpuBackedSciChartTextureDrawCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        using var texture = renderContext.CreateTexture(2, 2);
        int[] pixels =
        [
            unchecked((int)0xFFFF0000),
            unchecked((int)0xFFFF0000),
            unchecked((int)0xFFFF0000),
            unchecked((int)0xFFFF0000)
        ];

        texture.SetData(pixels);
        renderContext.Clear(DxColor.Black);
        renderContext.DrawTexture(
            texture,
            new DxRect(0, 0, 16, 16),
            ProGpuDirectXSciChartTextureFiltering.Point);
        renderContext.Flush();

        Assert.Single(renderContext.TextureDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);
        Assert.True(renderContext.ImmediateContext.SubmittedClearCount >= 1);

        var targetPixels = renderContext.ReadTargetPixels();
        var center = ReadRgbaPixel(targetPixels, 16, 8, 8);
        Assert.True(center.R > 200, $"Expected red center pixel after SciChart texture draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after SciChart texture draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after SciChart texture draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after SciChart texture draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartBatchedTextureVertexCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        using var texture = renderContext.CreateTexture(2, 2);
        texture.SetData(
        [
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF)
        ]);
        ProGpuDirectXSciChartTextureVertex[] vertices =
        [
            new(-10, -10, 0, 0, 0xFFFFFFFF),
            new(0, 0, 0, 0, 0xFF00FF00),
            new(16, 0, 1, 0, 0xFF00FF00),
            new(16, 16, 1, 1, 0xFF00FF00),
            new(0, 0, 0, 0, 0xFF00FF00),
            new(16, 16, 1, 1, 0xFF00FF00),
            new(0, 16, 0, 1, 0xFF00FF00)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawTextureVertices(
            vertices,
            startIndex: 1,
            count: vertices.Length - 1,
            texture,
            new ProGpuDirectXSciChartVertexTransform(),
            ProGpuDirectXSciChartTextureFiltering.Point);
        renderContext.Flush();

        Assert.Single(renderContext.TextureVertexDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected tinted green low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected tinted green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected tinted green low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected opaque tinted pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartShapedHeatmapCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        using var heightsTexture = renderContext.CreateTexture(2, 2, ProGpuDirectXSciChartTextureFormat.Float32);
        using var gradientTexture = renderContext.CreateTexture(2, 1);
        heightsTexture.SetFloatData([1f, 1f, 1f, 1f]);
        gradientTexture.SetData(
        [
            unchecked((int)0xFF00FF00),
            unchecked((int)0xFF00FF00)
        ]);
        ProGpuDirectXSciChartTextureVertex[] vertices =
        [
            new(-10, -10, 0, 0, 0xFFFFFFFF),
            new(0, 0, 0, 0, 0xFFFFFFFF),
            new(16, 0, 1, 0, 0xFFFFFFFF),
            new(16, 16, 1, 1, 0xFFFFFFFF),
            new(0, 0, 0, 0, 0xFFFFFFFF),
            new(16, 16, 1, 1, 0xFFFFFFFF),
            new(0, 16, 0, 1, 0xFFFFFFFF)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawShapedHeatmap(
            vertices,
            startIndex: 1,
            count: vertices.Length - 1,
            colorMapMin: 0,
            colorMapMax: 1,
            heightsTexture,
            gradientTexture,
            ProGpuDirectXSciChartTextureFiltering.Point);
        renderContext.Flush();

        Assert.Single(renderContext.ShapedHeatmapDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected shaped heatmap low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected shaped heatmap green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected shaped heatmap low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected shaped heatmap opaque pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart heatmap clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart heatmap clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart heatmap clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart heatmap clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartHeightTextureContourCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        using var heightsTexture = renderContext.CreateTexture(2, 2, ProGpuDirectXSciChartTextureFormat.Float32);
        using var contourTexture = renderContext.CreateTexture(2, 2);
        heightsTexture.SetFloatData([0.5f, 0.5f, 0.5f, 0.5f]);
        contourTexture.SetData(
        [
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFFFFFFFF)
        ]);

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawHeightTextureContours(
            heightsTexture,
            contourTexture,
            new DxRect(0, 0, 16, 16),
            new DxColor(0f, 1f, 0f, 1f),
            zMin: 0f,
            zMax: 1f,
            zStep: 0.5f,
            strokeThickness: 1f,
            opacity: 1f);
        renderContext.Flush();

        Assert.Single(renderContext.HeightTextureContourDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected height contour low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected height contour green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected height contour low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected height contour opaque pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart contour clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart contour clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart contour clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart contour clip, actual: {clippedOut}");
    }

    [Fact]
    public void GpuBackedHlslFragmentFrontFacePipelineHonorsBackendCapability()
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
            Source = """
float4 PSMain(bool isFrontFace : SV_IsFrontFace) : SV_Target
{
    return float4(1.0, 0.0, 0.0, 1.0);
}
""",
            EntryPoint = "PSMain",
            Label = "HLSL FrontFace Pixel"
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

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@builtin(front_facing) isFrontFace: bool", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Equal(device.Capabilities.SupportsFragmentFrontFacingBuiltin, pipeline.HasBackendPipeline);
        if (!device.Capabilities.SupportsFragmentFrontFacingBuiltin)
        {
            return;
        }

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.SetVertexBuffer(vertexBuffer);
        context.Draw(3);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after HLSL fragment system-value draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after HLSL fragment system-value draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after HLSL fragment system-value draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL fragment system-value draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslConditionalPixelDrawCommands()
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
            Source = SolidTriangleWgsl,
            EntryPoint = "vs_main",
            Label = "WGSL Triangle Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ConditionalPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Conditional Pixel"
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

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.NotNull(pixelShader.BackendSource);
        Assert.Contains("select(0.0, 1.0, 1.0 > 0.5)", pixelShader.BackendSource, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after HLSL conditional draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after HLSL conditional draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after HLSL conditional draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL conditional draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslDepthOutputDrawCommands()
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
            Format = DxResourceFormat.D32Float,
            Usage = DxTextureUsage.DepthStencil
        });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl,
            EntryPoint = "vs_main",
            Label = "WGSL Position Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = DepthOutputPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Depth Output Pixel"
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            DepthStencilFormat = DxResourceFormat.D32Float,
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

        context.ClearDepthStencil(depth, DxDepthStencilClearFlags.Depth, depth: 1f, stencil: 0);
        context.SetRenderTargets(target, depth);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.Draw(3);
        context.Flush();

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@builtin(frag_depth) depth: f32", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after HLSL depth-output draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after HLSL depth-output draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after HLSL depth-output draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL depth-output draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslPixelClipDiscardDrawCommands()
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
            Source = ClippedPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Clipped Pixel"
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

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("discard;", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R < 50, $"Expected clipped black center pixel after HLSL clip discard, actual: {center}");
        Assert.True(center.G < 50, $"Expected clipped black center pixel after HLSL clip discard, actual: {center}");
        Assert.True(center.B < 50, $"Expected clipped black center pixel after HLSL clip discard, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque clear alpha after HLSL clip discard, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslInstancedDrawCommands()
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
            SizeInBytes = 24,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 8
        });
        vertexBuffer.Write<float>(
        [
             0.0f, -0.35f,
             0.35f, 0.35f,
            -0.35f, 0.35f
        ]);
        using var instanceBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 48,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 24
        });
        instanceBuffer.Write<float>(
        [
            -0.45f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f,
             0.45f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f
        ]);
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = InstancedVertexHlsl,
            EntryPoint = "VSMain",
            Label = "HLSL Instanced Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = PassthroughPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Instanced Pixel"
        });
        var inputLayout = device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    InputSlot = 0,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    InputSlot = 1,
                    AlignedByteOffset = 0,
                    InputSlotClass = DxInputClassification.PerInstanceData,
                    InstanceDataStepRate = 1,
                    ShaderLocation = 1
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    InputSlot = 1,
                    AlignedByteOffset = 8,
                    InputSlotClass = DxInputClassification.PerInstanceData,
                    InstanceDataStepRate = 1,
                    ShaderLocation = 2
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
        context.SetVertexBuffer(0, vertexBuffer);
        context.SetVertexBuffer(1, instanceBuffer);
        context.DrawInstanced(3, 2);
        context.Flush();

        Assert.True(vertexShader.HasBackendShaderModule);
        Assert.Contains("@location(1) offset: vec2<f32>", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("@location(2) color: vec4<f32>", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var left = ReadRgbaPixel(pixels, 32, 8, 16);
        var right = ReadRgbaPixel(pixels, 32, 24, 16);
        Assert.True(left.R > 200, $"Expected red left instanced triangle, actual: {left}");
        Assert.True(left.G < 50, $"Expected low green left instanced triangle, actual: {left}");
        Assert.True(right.R < 50, $"Expected low red right instanced triangle, actual: {right}");
        Assert.True(right.G > 200, $"Expected green right instanced triangle, actual: {right}");
        Assert.True(left.A > 200 && right.A > 200, $"Expected opaque instanced triangles, actual: left {left}, right {right}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslSystemValueStructInstancedDrawCommands()
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
        using var trianglePositions = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 24,
            Usage = DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 8,
            Label = "System Value Triangle Positions"
        });
        trianglePositions.Write<float>(
        [
             0.0f, -0.35f,
             0.35f, 0.35f,
            -0.35f, 0.35f
        ]);
        using var instanceOffsets = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 8,
            Label = "System Value Instance Offsets"
        });
        instanceOffsets.Write<float>(
        [
            -0.45f, 0.0f,
             0.45f, 0.0f
        ]);
        using var instanceColors = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 32,
            Usage = DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = "System Value Instance Colors"
        });
        instanceColors.Write<float>(
        [
            1.0f, 0.0f, 0.0f, 1.0f,
            0.0f, 1.0f, 0.0f, 1.0f
        ]);
        using var trianglePositionsView = device.CreateShaderResourceView(
            trianglePositions,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 3,
                ElementStrideInBytes = 8
            });
        using var instanceOffsetsView = device.CreateShaderResourceView(
            instanceOffsets,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 2,
                ElementStrideInBytes = 8
            });
        using var instanceColorsView = device.CreateShaderResourceView(
            instanceColors,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 2,
                ElementStrideInBytes = 16
            });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = SystemValueStructInstancedVertexHlsl,
            EntryPoint = "VSMain",
            Label = "HLSL System Value Struct Instanced Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = PassthroughPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL System Value Struct Pixel"
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
        context.SetShaderResource(DxShaderStage.Vertex, 0, trianglePositionsView);
        context.SetShaderResource(DxShaderStage.Vertex, 1, instanceOffsetsView);
        context.SetShaderResource(DxShaderStage.Vertex, 2, instanceColorsView);
        context.DrawInstanced(3, 2);
        context.Flush();

        Assert.True(vertexShader.HasBackendShaderModule);
        Assert.Contains("@builtin(vertex_index) vertexId: u32", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("@builtin(instance_index) instanceId: u32", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var left = ReadRgbaPixel(pixels, 32, 8, 16);
        var right = ReadRgbaPixel(pixels, 32, 24, 16);
        Assert.True(left.R > 200, $"Expected red left system-value instanced triangle, actual: {left}");
        Assert.True(left.G < 50, $"Expected low green left system-value instanced triangle, actual: {left}");
        Assert.True(right.R < 50, $"Expected low red right system-value instanced triangle, actual: {right}");
        Assert.True(right.G > 200, $"Expected green right system-value instanced triangle, actual: {right}");
        Assert.True(left.A > 200 && right.A > 200, $"Expected opaque system-value instanced triangles, actual: left {left}, right {right}");
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
        Assert.Contains("output.position = (vec4<f32>(input.position, 1.0) * transform.WorldViewProjection);", vertexShader.BackendSource!, StringComparison.Ordinal);
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
    public void FlushSubmitsGpuBackedHlslTextDrawCommandsWithTextureSampler()
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
        using var sourceTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 1,
            Height = 1,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopyDestination,
            Label = "Source Texture"
        });
        sourceTexture.WritePixels<byte>([255, 255, 255, 255]);
        using var sourceView = device.CreateShaderResourceView(sourceTexture);
        using var sourceSampler = device.CreateSamplerState(new DxSamplerDescriptor
        {
            Filter = DxFilter.MinMagMipPoint,
            AddressU = DxTextureAddressMode.Clamp,
            AddressV = DxTextureAddressMode.Clamp
        });
        using var constants = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            Label = "Texture Transform Constants"
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
            SizeInBytes = 60,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 20
        });
        vertexBuffer.Write<float>(
        [
            -0.8f, -0.8f, 0.0f, 0.0f, 0.0f,
             0.8f, -0.8f, 0.0f, 1.0f, 0.0f,
             0.0f,  0.8f, 0.0f, 0.5f, 1.0f
        ]);
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = TexturedTransformVertexHlsl,
            EntryPoint = "VSMain",
            Label = "HLSL Textured Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = TextureSampleLevelTintPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Texture Pixel"
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
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
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
        context.SetShaderResource(DxShaderStage.Pixel, 0, sourceView);
        context.SetSampler(DxShaderStage.Pixel, 0, sourceSampler);
        context.Draw(3);
        context.Flush();

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@binding(576)", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("@binding(768)", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("var sampled: vec4<f32> = textureSampleLevel(SourceTexture, SourceSampler, input.uv, 0.0);", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("var loaded: vec4<f32> = textureLoad(SourceTexture, ((vec3<i32>(0, 0, 0)).xy + vec2<i32>(0, 0)), (vec3<i32>(0, 0, 0)).z);", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("var biased: vec4<f32> = textureSampleBias(SourceTexture, SourceSampler, input.uv, 0.0);", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("var grad: vec4<f32> = textureSampleGrad(SourceTexture, SourceSampler, input.uv, vec2<f32>(0.0, 0.0), vec2<f32>(0.0, 0.0));", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("var normal: vec3<f32> = normalize(vec3<f32>(sampled.r, sampled.g, sampled.b));", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("var light: f32 = max(dot(normal, normalize(vec3<f32>(1.0, 1.0, 1.0))), 0.0);", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("var falloff: f32 = pow(sqrt(light), 1.0);", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("var mask: f32 = clamp(mix(0.0, sampled.r * 2.0, fract(1.5)) * falloff * loaded.r * biased.r * grad.r, 0.0, 1.0);", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after HLSL texture sample draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after HLSL texture sample draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after HLSL texture sample draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL texture sample draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslStructuredBufferDrawCommands()
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
        using var positions = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 48,
            Usage = DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = "Structured Positions"
        });
        positions.Write<float>(
        [
            -0.8f, -0.8f, 0.0f, 1.0f,
             0.8f, -0.8f, 0.0f, 1.0f,
             0.0f,  0.8f, 0.0f, 1.0f
        ]);
        using var positionsView = device.CreateShaderResourceView(
            positions,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 3,
                ElementStrideInBytes = 16
            });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = StructuredBufferVertexHlsl,
            EntryPoint = "VSMain",
            Label = "HLSL StructuredBuffer Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = SolidGreenPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Green Pixel"
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
        context.SetShaderResource(DxShaderStage.Vertex, 0, positionsView);
        context.Draw(3);
        context.Flush();

        Assert.True(vertexShader.HasBackendShaderModule);
        Assert.Contains("@binding(64) var<storage, read> Positions: array<vec4<f32>>;", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R < 50, $"Expected low red center pixel after structured-buffer draw, actual: {center}");
        Assert.True(center.G > 200, $"Expected green center pixel after structured-buffer draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after structured-buffer draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after structured-buffer draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslTypedBufferDrawCommands()
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
        using var positions = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 48,
            Usage = DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = "Typed Buffer Positions"
        });
        positions.Write<float>(
        [
            -0.8f, -0.8f, 0.0f, 1.0f,
             0.8f, -0.8f, 0.0f, 1.0f,
             0.0f,  0.8f, 0.0f, 1.0f
        ]);
        using var positionsView = device.CreateShaderResourceView(
            positions,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 3,
                ElementStrideInBytes = 16
            });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = TypedBufferVertexHlsl,
            EntryPoint = "VSMain",
            Label = "HLSL Buffer Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = SolidGreenPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Green Pixel"
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
        context.SetShaderResource(DxShaderStage.Vertex, 0, positionsView);
        context.Draw(3);
        context.Flush();

        Assert.True(vertexShader.HasBackendShaderModule);
        Assert.Contains("@binding(64) var<storage, read> Positions: array<vec4<f32>>;", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R < 50, $"Expected low red center pixel after typed-buffer draw, actual: {center}");
        Assert.True(center.G > 200, $"Expected green center pixel after typed-buffer draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after typed-buffer draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after typed-buffer draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslStructuredBufferRecordDrawCommands()
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
        using var points = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 96,
            Usage = DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 32,
            Label = "Structured Chart Points"
        });
        points.Write<float>(
        [
            -0.8f, -0.8f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 1.0f,
             0.8f, -0.8f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 1.0f,
             0.0f,  0.8f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 1.0f
        ]);
        using var pointsView = device.CreateShaderResourceView(
            points,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 3,
                ElementStrideInBytes = 32
            });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = StructuredBufferRecordVertexHlsl,
            EntryPoint = "VSMain",
            Label = "HLSL StructuredBuffer Record Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = PassthroughPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Passthrough Pixel"
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
        context.SetShaderResource(DxShaderStage.Vertex, 0, pointsView);
        context.Draw(3);
        context.Flush();

        Assert.True(vertexShader.HasBackendShaderModule);
        Assert.Contains("@binding(64) var<storage, read> Points: array<ChartPoint>;", vertexShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R < 50, $"Expected low red center pixel after structured-buffer record draw, actual: {center}");
        Assert.True(center.G > 200, $"Expected green center pixel after structured-buffer record draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after structured-buffer record draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after structured-buffer record draw, actual: {center}");
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
    public void FlushSubmitsGpuBackedHlslRwStructuredBufferDispatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var output = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.Structured | DxBufferUsage.UnorderedAccess | DxBufferUsage.CopySource,
            StrideInBytes = 16,
            Label = "RWStructuredBuffer Output"
        });
        using var outputView = device.CreateUnorderedAccessView(
            output,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 1,
                ElementStrideInBytes = 16,
                Access = DxUnorderedAccessViewAccess.ReadWrite
            });
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = RwStructuredBufferComputeHlsl,
            EntryPoint = "CSMain",
            Label = "HLSL RWStructuredBuffer Compute"
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });
        using var context = device.CreateImmediateContext();

        context.SetComputePipeline(pipeline);
        context.SetUnorderedAccessView(0, outputView);
        context.Dispatch(1, 1, 1);
        context.Flush();

        Assert.True(computeShader.HasBackendShaderModule);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<vec4<f32>>;", computeShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDispatchCount);

        var values = MemoryMarshal.Cast<byte, float>(output.BackendBuffer!.ReadBytes(0, 16)).ToArray();
        Assert.Equal([0f, 1f, 0f, 1f], values);
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslRwTypedBufferDispatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var output = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.Structured | DxBufferUsage.UnorderedAccess | DxBufferUsage.CopySource,
            StrideInBytes = 16,
            Label = "RWBuffer Output"
        });
        using var outputView = device.CreateUnorderedAccessView(
            output,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 1,
                ElementStrideInBytes = 16,
                Access = DxUnorderedAccessViewAccess.ReadWrite
            });
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = RwTypedBufferComputeHlsl,
            EntryPoint = "CSMain",
            Label = "HLSL RWBuffer Compute"
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });
        using var context = device.CreateImmediateContext();

        context.SetComputePipeline(pipeline);
        context.SetUnorderedAccessView(0, outputView);
        context.Dispatch(1, 1, 1);
        context.Flush();

        Assert.True(computeShader.HasBackendShaderModule);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<vec4<f32>>;", computeShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDispatchCount);

        var values = MemoryMarshal.Cast<byte, float>(output.BackendBuffer!.ReadBytes(0, 16)).ToArray();
        Assert.Equal([0f, 0f, 1f, 1f], values);
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslByteAddressBufferDispatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var input = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.Structured | DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = 4,
            Label = "ByteAddressBuffer Input"
        });
        input.Write<uint>([10u, 20u, 30u, 40u]);
        using var inputView = device.CreateShaderResourceView(
            input,
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 4,
                ElementStrideInBytes = 4
            });
        using var output = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 8,
            Usage = DxBufferUsage.Structured | DxBufferUsage.UnorderedAccess | DxBufferUsage.CopySource,
            StrideInBytes = 4,
            Label = "RWByteAddressBuffer Output"
        });
        using var outputView = device.CreateUnorderedAccessView(
            output,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 2,
                ElementStrideInBytes = 4,
                Access = DxUnorderedAccessViewAccess.ReadWrite
            });
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ByteAddressBufferComputeHlsl,
            EntryPoint = "CSMain",
            Label = "HLSL ByteAddressBuffer Compute"
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });
        using var context = device.CreateImmediateContext();

        context.SetComputePipeline(pipeline);
        context.SetShaderResource(DxShaderStage.Compute, 0, inputView);
        context.SetUnorderedAccessView(0, outputView);
        context.Dispatch(1, 1, 1);
        context.Flush();

        Assert.True(computeShader.HasBackendShaderModule);
        Assert.Contains("@binding(1600) var<storage, read> Input: array<u32>;", computeShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<atomic<u32>>;", computeShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDispatchCount);

        var values = MemoryMarshal.Cast<byte, uint>(output.BackendBuffer!.ReadBytes(0, 8)).ToArray();
        Assert.Equal([30u, 70u], values);
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslRwByteAddressBufferInterlockedDispatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var output = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 12,
            Usage = DxBufferUsage.Structured | DxBufferUsage.UnorderedAccess | DxBufferUsage.CopySource,
            StrideInBytes = 4,
            Label = "RWByteAddressBuffer Interlocked Output"
        });
        using var outputView = device.CreateUnorderedAccessView(
            output,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 3,
                ElementStrideInBytes = 4,
                Access = DxUnorderedAccessViewAccess.ReadWrite
            });
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ByteAddressBufferInterlockedComputeHlsl,
            EntryPoint = "CSMain",
            Label = "HLSL RWByteAddressBuffer Interlocked Compute"
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });
        using var context = device.CreateImmediateContext();

        context.SetComputePipeline(pipeline);
        context.SetUnorderedAccessView(0, outputView);
        context.Dispatch(1, 1, 1);
        context.Flush();

        Assert.True(computeShader.HasBackendShaderModule);
        Assert.Contains("previous = atomicAdd(&Output[((0u) / 4u)], 5u);", computeShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDispatchCount);

        var values = MemoryMarshal.Cast<byte, uint>(output.BackendBuffer!.ReadBytes(0, 12)).ToArray();
        Assert.Equal([15u, 10u, 1u], values);
    }

    [Fact]
    public void GpuBackedRwByteAddressBufferCompareExchangePipelineHonorsBackendCapability()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var output = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 12,
            Usage = DxBufferUsage.Structured | DxBufferUsage.UnorderedAccess | DxBufferUsage.CopySource,
            StrideInBytes = 4,
            Label = "RWByteAddressBuffer CompareExchange Output"
        });
        using var outputView = device.CreateUnorderedAccessView(
            output,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 3,
                ElementStrideInBytes = 4,
                Access = DxUnorderedAccessViewAccess.ReadWrite
            });
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = ByteAddressBufferCompareExchangeComputeHlsl,
            EntryPoint = "CSMain",
            Label = "HLSL RWByteAddressBuffer CompareExchange Compute"
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });

        Assert.True(computeShader.HasBackendShaderModule);
        Assert.Contains("compareOriginal = atomicCompareExchangeWeak(&Output[((0u) / 4u)], 12u, 99u).old_value;", computeShader.BackendSource!, StringComparison.Ordinal);

        if (!device.Capabilities.SupportsRwByteAddressBufferInterlockedCompareExchange)
        {
            Assert.False(pipeline.HasBackendPipeline);
            return;
        }

        using var context = device.CreateImmediateContext();
        context.SetComputePipeline(pipeline);
        context.SetUnorderedAccessView(0, outputView);
        context.Dispatch(1, 1, 1);
        context.Flush();

        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDispatchCount);

        var values = MemoryMarshal.Cast<byte, uint>(output.BackendBuffer!.ReadBytes(0, 12)).ToArray();
        Assert.Equal([99u, 12u, 99u], values);
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
