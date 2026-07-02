using ProGPU.Backend;
using ProGPU.DirectX;
using System.Buffers.Binary;
using System.Numerics;
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

    private const string TextureComparisonSamplePixelHlsl = """
Texture2D ShadowMap : register(t0);
SamplerComparisonState ShadowSampler : register(s0);

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

float4 PSMain(VertexOutput input) : SV_Target
{
    float passNear = ShadowMap.SampleCmp(ShadowSampler, input.uv, 0.25);
    float passFar = ShadowMap.SampleCmpLevelZero(ShadowSampler, input.uv, 0.75);
    return float4(passNear, passFar, 0.0, 1.0);
}
""";

    private const string Texture2DArraySamplePixelHlsl = """
Texture2DArray SourceTextureArray : register(t0);
SamplerState SourceSampler : register(s0);

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

float4 PSMain(VertexOutput input) : SV_Target
{
    float3 uvw = float3(input.uv, 1.0);
    float4 sampled = SourceTextureArray.Sample(SourceSampler, uvw);
    float4 loaded = SourceTextureArray.Load(int4(0, 0, 1, 0));
    return float4(sampled.r * loaded.r, sampled.g * loaded.g, sampled.b * loaded.b, sampled.a * loaded.a);
}
""";

    private const string Texture3DSamplePixelHlsl = """
Texture3D SourceTextureVolume : register(t0);
SamplerState SourceSampler : register(s0);

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

float4 PSMain(VertexOutput input) : SV_Target
{
    float4 sampled = SourceTextureVolume.Sample(SourceSampler, float3(input.uv, 0.75));
    float4 loaded = SourceTextureVolume.Load(int4(1, 1, 1, 0));
    return float4(sampled.g, loaded.g, sampled.r, loaded.a);
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

    private const string RwTexture2DComputeHlsl = """
RWTexture2D<float4> Output : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output[int2(id.xy)] = float4(0.0, 1.0, 0.0, 1.0);
}
""";

    private const string RwTexture2DReadWriteComputeHlsl = """
RWTexture2D<float4> Output : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    float4 previous = Output[int2(id.xy)];
    Output[int2(id.xy)] = float4(previous.r, 1.0, previous.b, previous.a);
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
    public void NativeDependencyInspectorReportsPInvokeModulesAndEntries()
    {
        var report = CreateNativeDependencyFixtureReport();

        Assert.True(report.RequiresNativeRuntime);
        Assert.True(report.RequiresModule("USER32.DLL"));
        Assert.True(report.RequiresModule("d3d11.dll"));
        Assert.True(report.RequiresModule("VXccelEngine3D.dll"));
        Assert.True(report.RequiresModule("SciChart.Charting3D.dll"));
        Assert.False(report.RequiresModule("*.so"));
        Assert.False(report.RequiresModule(".So"));
        Assert.Contains("user32.dll", report.DescribeModules(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("d3d11.dll", report.DescribeModules(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            report.ModuleHints,
            hint => hint.ModuleName.Equals("VXccelEngine3D.dll", StringComparison.OrdinalIgnoreCase)
                && hint.Source == "AssemblyString");

        var messageBoxImport = Assert.Single(
            report.Imports,
            import => import.TypeName.Contains(nameof(NativeDependencyFixture), StringComparison.Ordinal)
                && import.MethodName == nameof(NativeDependencyFixture.MessageBoxW));
        Assert.Equal("user32.dll", messageBoxImport.ModuleName);
        Assert.Equal("MessageBoxW", messageBoxImport.EntryPoint);
        Assert.Equal(CallingConvention.StdCall, messageBoxImport.CallingConvention);
        Assert.Equal(CharSet.Unicode, messageBoxImport.CharSet);
        Assert.True(messageBoxImport.SetLastError);
        Assert.Equal("System.Int32", messageBoxImport.ReturnTypeName);
        Assert.Collection(
            messageBoxImport.Parameters,
            parameter =>
            {
                Assert.Equal("hwnd", parameter.Name);
                Assert.Equal("System.IntPtr", parameter.TypeName);
            },
            parameter =>
            {
                Assert.Equal("text", parameter.Name);
                Assert.Equal("System.String", parameter.TypeName);
            },
            parameter =>
            {
                Assert.Equal("caption", parameter.Name);
                Assert.Equal("System.String", parameter.TypeName);
            },
            parameter =>
            {
                Assert.Equal("type", parameter.Name);
                Assert.Equal("System.UInt32", parameter.TypeName);
            });
        Assert.Contains(
            "System.Int32 ProGPU.Tests.DirectXShimTests+NativeDependencyFixture.MessageBoxW(System.IntPtr hwnd, System.String text, System.String caption, System.UInt32 type)",
            messageBoxImport.ManagedSignature,
            StringComparison.Ordinal);

        var d3dImport = Assert.Single(
            report.Imports,
            import => import.TypeName.Contains(nameof(NativeDependencyFixture), StringComparison.Ordinal)
                && import.MethodName == nameof(NativeDependencyFixture.D3D11CreateDevice));
        Assert.Equal("d3d11.dll", d3dImport.ModuleName);
        Assert.Equal("D3D11CreateDevice", d3dImport.EntryPoint);
        Assert.True(d3dImport.ExactSpelling);
    }

    [Fact]
    public void NativeDependencyInspectorCreatesAssemblyImageHintsFromAnchorTypes()
    {
        var hints = ProGpuDirectXNativeDependencyInspector.CreateModuleHintsFromAssemblyImages(
            [typeof(NativeDependencyFixture), typeof(NativeDependencyFixture)],
            "TestAssemblyImage");

        Assert.Contains(
            hints,
            hint => hint.ModuleName.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)
                && hint.Source == "TestAssemblyImage");
        Assert.Equal(
            hints.Count,
            hints.DistinctBy(static hint => (hint.AssemblyName, hint.ModuleName, hint.Source)).Count());
    }

    [Fact]
    public void NativeDependencyInspectorUsesExplicitMetadataWithoutReflectionScanning()
    {
        var source = File.ReadAllText(FindRepoFile("src", "ProGPU.DirectX", "ProGpuDirectXNativeDependencyInspector.cs"));

        Assert.Contains("CreateReport(", source, StringComparison.Ordinal);
        Assert.Contains("CreateModuleHintsFromText", source, StringComparison.Ordinal);
        Assert.Contains("CreateModuleHintsFromAssemblyImages", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BindingFlags", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTypes(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetMethods(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCustomAttribute", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DllImportAttribute", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCompatibilityPlannerClassifiesSciChartAndDirectXModules()
    {
        var report = CreateNativeDependencyFixtureReport();
        var plan = ProGpuDirectXNativeCompatibilityPlanner.Create(report);

        Assert.True(plan.RequiresProGpuNativeFacade);
        Assert.True(plan.RequiresHostOsAbstraction);
        Assert.Contains("ProGPU native facade:", plan.DescribeRequiredActions(), StringComparison.Ordinal);
        Assert.Contains("host OS abstraction:", plan.DescribeRequiredActions(), StringComparison.Ordinal);
        Assert.Contains("managed assembly hints:", plan.DescribeRequiredActions(), StringComparison.Ordinal);
        Assert.Contains("investigate:", plan.DescribeRequiredActions(), StringComparison.Ordinal);

        var d3dModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.Direct3D, d3dModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, d3dModule.Action);

        var visualXcceleratorModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("VXccelEngine3D.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.SciChartVisualXccelerator, visualXcceleratorModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, visualXcceleratorModule.Action);

        var embeddedVisualXcceleratorModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("SciChart.Data.Resources.x64.VXccelEngine2D.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.SciChartVisualXccelerator, embeddedVisualXcceleratorModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, embeddedVisualXcceleratorModule.Action);

        var embeddedLicensingModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("SciChart.Core.Resources.x64.AbtLicensingNative.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.SciChartLicensing, embeddedLicensingModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, embeddedLicensingModule.Action);

        var bareLicensingModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("AbtLicensingNative", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.SciChartLicensing, bareLicensingModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, bareLicensingModule.Action);

        var coreNativeModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("SciChartCoreNative", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.SciChartLicensing, coreNativeModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, coreNativeModule.Action);

        var embeddedCompilerModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("SciChart.Data.Resources.x64.D3DCompiler_47.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.D3DCompiler, embeddedCompilerModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, embeddedCompilerModule.Action);

        var win32Module = Assert.Single(plan.Modules, module => module.ModuleName.Equals("user32.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.Win32System, win32Module.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction, win32Module.Action);

        var rpcModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("RPCRT4.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.Win32System, rpcModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction, rpcModule.Action);

        var managedModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("SciChart.Charting3D.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.ManagedAssemblyHint, managedModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ManagedAssemblyReferenceOnly, managedModule.Action);

        var unknownModule = Assert.Single(plan.Modules, module => module.ModuleName.Equals("VendorNativeExtension.dylib", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.Unknown, unknownModule.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.Investigate, unknownModule.Action);
    }

    [Fact]
    public void NativeAbiPlannerReportsExportsAndDynamicModuleHints()
    {
        var report = CreateNativeDependencyFixtureReport();
        var plan = ProGpuDirectXNativeAbiPlanner.Create(report);

        Assert.Contains("d3d11.dll: D3D11CreateDevice", plan.DescribeActionableExports(), StringComparison.Ordinal);
        Assert.Contains("user32.dll: MessageBoxW", plan.DescribeActionableExports(), StringComparison.Ordinal);
        Assert.Contains("VXccelEngine3D.dll: dynamic module hint", plan.DescribeActionableExports(), StringComparison.Ordinal);

        var d3dExport = Assert.Single(
            plan.ActionableExports,
            export => export.DisplayName.Equals("d3d11.dll!D3D11CreateDevice", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.Direct3D, d3dExport.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, d3dExport.Action);
        Assert.Equal(CallingConvention.Winapi, d3dExport.CallingConvention);
        Assert.True(d3dExport.ExactSpelling);
        Assert.Contains(
            "System.Int32 ProGPU.Tests.DirectXShimTests+NativeDependencyFixture.D3D11CreateDevice()",
            d3dExport.ManagedSignatures,
            StringComparer.Ordinal);

        var licensingExport = Assert.Single(
            plan.ActionableExports,
            export => export.DisplayName.Equals("AbtLicensingNative!SciChartLicenseCheck", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.SciChartLicensing, licensingExport.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, licensingExport.Action);

        var win32Export = Assert.Single(
            plan.ActionableExports,
            export => export.DisplayName.Equals("user32.dll!MessageBoxW", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.Win32System, win32Export.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction, win32Export.Action);
        Assert.Equal(CallingConvention.StdCall, win32Export.CallingConvention);
        Assert.Equal(CharSet.Unicode, win32Export.CharSet);
        Assert.True(win32Export.SetLastError);
        Assert.Contains(
            "System.Int32 ProGPU.Tests.DirectXShimTests+NativeDependencyFixture.MessageBoxW(System.IntPtr hwnd, System.String text, System.String caption, System.UInt32 type)",
            win32Export.ManagedSignatures,
            StringComparer.Ordinal);

        var dynamicHint = Assert.Single(
            plan.DynamicModuleHints,
            module => module.ModuleName.Equals("VXccelEngine3D.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.SciChartVisualXccelerator, dynamicHint.Kind);
        Assert.True(dynamicHint.HasOnlyDynamicHints);

        Assert.DoesNotContain(
            plan.ActionableExports,
            export => export.ModuleName.Equals("SciChart.Charting3D.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeFacadeSourceEmitterGeneratesNativeAotExportScaffold()
    {
        var report = CreateNativeDependencyFixtureReport();
        var plan = ProGpuDirectXNativeAbiPlanner.Create(report);
        var source = ProGpuDirectXNativeFacadeSourceEmitter.Emit(
            plan,
            new ProGpuDirectXNativeFacadeSourceOptions(
                "ProGPU.Tests.GeneratedNativeFacade",
                "SciChartNativeFacadeExports"));

        Assert.Contains("namespace ProGPU.Tests.GeneratedNativeFacade", source.SourceText, StringComparison.Ordinal);
        Assert.Contains("public static unsafe partial class SciChartNativeFacadeExports", source.SourceText, StringComparison.Ordinal);
        Assert.Contains("[UnmanagedCallersOnly(EntryPoint = \"D3D11CreateDevice\", CallConvs = new[] { typeof(CallConvStdcall) })]", source.SourceText, StringComparison.Ordinal);
        Assert.Contains("public static int d3d11_dll_D3D11CreateDevice()", source.SourceText, StringComparison.Ordinal);
        Assert.Contains("[UnmanagedCallersOnly(EntryPoint = \"MessageBoxW\", CallConvs = new[] { typeof(CallConvStdcall) })]", source.SourceText, StringComparison.Ordinal);
        Assert.Contains("public static int user32_dll_MessageBoxW(nint hwnd, nint text, nint caption, uint type)", source.SourceText, StringComparison.Ordinal);
        Assert.Contains("return 0;", source.SourceText, StringComparison.Ordinal);
        Assert.Contains("supported native facade exports", source.DescribeSupport(), StringComparison.Ordinal);

        var d3dExport = Assert.Single(
            source.SupportedExports,
            export => export.EntryPoint == "D3D11CreateDevice");
        Assert.Equal("d3d11.dll", d3dExport.ModuleName);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, d3dExport.Action);
        Assert.Equal(CallingConvention.Winapi, d3dExport.CallingConvention);
        Assert.Equal(
            "System.Int32 ProGPU.Tests.DirectXShimTests+NativeDependencyFixture.D3D11CreateDevice()",
            d3dExport.ManagedSignature);

        var messageBoxExport = Assert.Single(
            source.SupportedExports,
            export => export.EntryPoint == "MessageBoxW");
        Assert.Equal("user32.dll", messageBoxExport.ModuleName);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction, messageBoxExport.Action);
        Assert.Equal(CallingConvention.StdCall, messageBoxExport.CallingConvention);

        Assert.DoesNotContain(
            source.Exports,
            export => export.ModuleName.Equals("VXccelEngine3D.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeFacadeProjectEmitterGeneratesBuildableNativeAotProjectScaffold()
    {
        var report = CreateNativeDependencyFixtureReport();
        var plan = ProGpuDirectXNativeAbiPlanner.Create(report);
        var project = ProGpuDirectXNativeFacadeProjectEmitter.Emit(
            plan,
            new ProGpuDirectXNativeFacadeProjectOptions(
                "ProGPU.Tests.NativeFacade",
                "net10.0",
                RuntimeIdentifier: "osx-arm64"),
            new ProGpuDirectXNativeFacadeSourceOptions(
                "ProGPU.Tests.GeneratedNativeFacade",
                "SciChartNativeFacadeExports"));

        Assert.Equal("ProGPU.Tests.NativeFacade.csproj", project.ProjectFileName);
        Assert.Equal("SciChartNativeFacadeExports.g.cs", project.SourceFileName);
        Assert.Equal("README.md", project.ReadmeFileName);
        Assert.Contains("<PublishAot>true</PublishAot>", project.ProjectFileText, StringComparison.Ordinal);
        Assert.Contains("<NativeLib>Shared</NativeLib>", project.ProjectFileText, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>osx-arm64</RuntimeIdentifier>", project.ProjectFileText, StringComparison.Ordinal);
        Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", project.ProjectFileText, StringComparison.Ordinal);
        Assert.Contains("public static unsafe partial class SciChartNativeFacadeExports", project.SourceText, StringComparison.Ordinal);
        Assert.Contains("dotnet publish ProGPU.Tests.NativeFacade.csproj -c Release -r osx-arm64", project.ReadmeText, StringComparison.Ordinal);
        Assert.Contains("Supported exports: `", project.ReadmeText, StringComparison.Ordinal);
        Assert.Contains("Unsupported exports: `", project.ReadmeText, StringComparison.Ordinal);
        Assert.Contains("supported native facade exports", project.DescribeSupport(), StringComparison.Ordinal);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"progpu-directx-native-facade-{Guid.NewGuid():N}");
        try
        {
            project.WriteToDirectory(outputDirectory);
            Assert.True(File.Exists(Path.Combine(outputDirectory, project.ProjectFileName)));
            Assert.True(File.Exists(Path.Combine(outputDirectory, project.SourceFileName)));
            Assert.True(File.Exists(Path.Combine(outputDirectory, project.ReadmeFileName)));
            Assert.Contains("<NativeLib>Shared</NativeLib>", File.ReadAllText(Path.Combine(outputDirectory, project.ProjectFileName)), StringComparison.Ordinal);
            Assert.Contains("D3D11CreateDevice", File.ReadAllText(Path.Combine(outputDirectory, project.SourceFileName)), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void NativeResolverClassifiesRequestsWithoutMaskingMissingFacade()
    {
        var report = CreateNativeDependencyFixtureReport();
        var plan = ProGpuDirectXNativeCompatibilityPlanner.Create(report);
        var registration = ProGpuDirectXNativeResolver.CreateAnchorRegistration(
            typeof(NativeDependencyFixture),
            plan);

        Assert.Equal(ProGpuDirectXNativeResolverRegistrationStatus.Created, registration.Status);
        Assert.Equal(IntPtr.Zero, registration.ResolveForAnchorType("d3d11.dll", typeof(NativeDependencyFixture)));
        Assert.Equal(IntPtr.Zero, registration.ResolveForAnchorType("USER32", typeof(NativeDependencyFixture)));
        Assert.Equal(IntPtr.Zero, registration.ResolveForAnchorType("SciChart.Charting3D.dll", typeof(NativeDependencyFixture)));
        Assert.Contains("default facade: not configured", registration.Describe(), StringComparison.Ordinal);

        var d3dAttempt = Assert.Single(
            registration.Attempts,
            attempt => attempt.ModuleName.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.Direct3D, d3dAttempt.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, d3dAttempt.Action);
        Assert.Equal(ProGpuDirectXNativeResolverModuleStatus.FacadeNotConfigured, d3dAttempt.Status);

        var user32Attempt = Assert.Single(
            registration.Attempts,
            attempt => attempt.ModuleName.Equals("USER32", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.Win32System, user32Attempt.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction, user32Attempt.Action);
        Assert.Equal(ProGpuDirectXNativeResolverModuleStatus.FacadeNotConfigured, user32Attempt.Status);

        var managedAttempt = Assert.Single(
            registration.Attempts,
            attempt => attempt.ModuleName.Equals("SciChart.Charting3D.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProGpuDirectXNativeModuleKind.ManagedAssemblyHint, managedAttempt.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ManagedAssemblyReferenceOnly, managedAttempt.Action);
        Assert.Equal(ProGpuDirectXNativeResolverModuleStatus.Ignored, managedAttempt.Status);
    }

    [Fact]
    public void NativeResolverRegistersDistinctAnchorTypes()
    {
        var report = CreateNativeDependencyFixtureReport();
        var plan = ProGpuDirectXNativeCompatibilityPlanner.Create(report);

        var registrations = ProGpuDirectXNativeResolver.RegisterAnchorTypes(
            [typeof(NativeDependencyFixture), typeof(NativeDependencyFixture)],
            plan);

        var registration = Assert.Single(registrations);
        Assert.Equal(ProGpuDirectXNativeResolverRegistrationStatus.Installed, registration.Status);
        Assert.Equal(typeof(NativeDependencyFixture).Assembly.GetName().Name, registration.AssemblyName);
    }

    [Fact]
    public void NativeResolverReportsConfiguredFacadeLoadFailure()
    {
        var report = CreateNativeDependencyFixtureReport();
        var plan = ProGpuDirectXNativeCompatibilityPlanner.Create(report);
        var missingFacadePath = Path.Combine(Path.GetTempPath(), "progpu-directx-native-facade-missing.dylib");
        var registration = ProGpuDirectXNativeResolver.CreateAnchorRegistration(
            typeof(NativeDependencyFixture),
            plan,
            new ProGpuDirectXNativeResolverOptions(missingFacadePath));

        Assert.Equal(IntPtr.Zero, registration.ResolveForAnchorType("VXccelEngine3D.dll", typeof(NativeDependencyFixture)));

        var attempt = Assert.Single(registration.Attempts);
        Assert.Equal("VXccelEngine3D.dll", attempt.ModuleName);
        Assert.Equal(ProGpuDirectXNativeModuleKind.SciChartVisualXccelerator, attempt.Kind);
        Assert.Equal(ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade, attempt.Action);
        Assert.Equal(ProGpuDirectXNativeResolverModuleStatus.FacadeLoadFailed, attempt.Status);
        Assert.Equal(missingFacadePath, attempt.FacadeLibraryPath);
        Assert.False(string.IsNullOrWhiteSpace(attempt.Failure));
        Assert.Contains("attempts: VXccelEngine3D.dll=facadeloadfailed", registration.Describe(), StringComparison.Ordinal);
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

    private static ProGpuDirectXNativeDependencyReport CreateNativeDependencyFixtureReport()
    {
        var assemblyName = typeof(NativeDependencyFixture).Assembly.GetName().Name ?? string.Empty;
        var typeName = typeof(NativeDependencyFixture).FullName ?? nameof(NativeDependencyFixture);
        var moduleHints = ProGpuDirectXNativeDependencyInspector.CreateModuleHintsFromText(
            assemblyName,
            string.Join(
                ' ',
                NativeDependencyFixture.GetDynamicModuleName(),
                NativeDependencyFixture.GetEmbeddedNativeResourceNames(),
                NativeDependencyFixture.GetInvalidPatternModuleNames(),
                NativeDependencyFixture.GetManagedAssemblyHintName(),
                NativeDependencyFixture.GetUnknownModuleHintName()),
            "AssemblyString");

        return ProGpuDirectXNativeDependencyInspector.CreateReport(
            [
                new ProGpuDirectXNativeImport(
                    assemblyName,
                    typeName,
                    nameof(NativeDependencyFixture.MessageBoxW),
                    "user32.dll",
                    "MessageBoxW",
                    "System.Int32",
                    [
                        new ProGpuDirectXNativeImportParameter("hwnd", "System.IntPtr", IsIn: false, IsOut: false, IsByRef: false, IsOptional: false),
                        new ProGpuDirectXNativeImportParameter("text", "System.String", IsIn: false, IsOut: false, IsByRef: false, IsOptional: false),
                        new ProGpuDirectXNativeImportParameter("caption", "System.String", IsIn: false, IsOut: false, IsByRef: false, IsOptional: false),
                        new ProGpuDirectXNativeImportParameter("type", "System.UInt32", IsIn: false, IsOut: false, IsByRef: false, IsOptional: false)
                    ],
                    CallingConvention.StdCall,
                    CharSet.Unicode,
                    SetLastError: true,
                    ExactSpelling: false),
                new ProGpuDirectXNativeImport(
                    assemblyName,
                    typeName,
                    nameof(NativeDependencyFixture.D3D11CreateDevice),
                    "d3d11.dll",
                    "D3D11CreateDevice",
                    "System.Int32",
                    [],
                    CallingConvention.Winapi,
                    CharSet.None,
                    SetLastError: false,
                    ExactSpelling: true),
                new ProGpuDirectXNativeImport(
                    assemblyName,
                    typeName,
                    nameof(NativeDependencyFixture.SciChartLicenseCheck),
                    "AbtLicensingNative",
                    "SciChartLicenseCheck",
                    "System.Int32",
                    [],
                    CallingConvention.Winapi,
                    CharSet.None,
                    SetLastError: false,
                    ExactSpelling: true),
                new ProGpuDirectXNativeImport(
                    assemblyName,
                    typeName,
                    nameof(NativeDependencyFixture.SciChartCoreInitialize),
                    "SciChartCoreNative",
                    "SciChartCoreInitialize",
                    "System.Int32",
                    [],
                    CallingConvention.Winapi,
                    CharSet.None,
                    SetLastError: false,
                    ExactSpelling: true)
            ],
            moduleHints);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(pathParts)}.");
    }

    private static class NativeDependencyFixture
    {
        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        internal static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
        internal static extern int D3D11CreateDevice();

        [DllImport("AbtLicensingNative", EntryPoint = "SciChartLicenseCheck", ExactSpelling = true)]
        internal static extern int SciChartLicenseCheck();

        [DllImport("SciChartCoreNative", EntryPoint = "SciChartCoreInitialize", ExactSpelling = true)]
        internal static extern int SciChartCoreInitialize();

        internal static string GetDynamicModuleName() => "VXccelEngine3D.dll";

        internal static string GetEmbeddedNativeResourceNames() => "SciChart.Data.Resources.x64.VXccelEngine2D.dll SciChart.Core.Resources.x64.AbtLicensingNative.dll SciChart.Data.Resources.x64.D3DCompiler_47.dll RPCRT4.dll";

        internal static string GetInvalidPatternModuleNames() => "*.so .So";

        internal static string GetManagedAssemblyHintName() => "SciChart.Charting3D.dll";

        internal static string GetUnknownModuleHintName() => "VendorNativeExtension.dylib";
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
    public void SciChartRenderContext3DRecordsPointCloudAndTriangleMesh()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext3D(device, 64, 64);
        ProGpuDirectXSciChartVertex3D[] pointVertices =
        [
            new(0f, 0f, 0.5f, 0f, 0f, 1f, 0xFFFFFFFF)
        ];
        ProGpuDirectXSciChartVertex3D[] lineVertices =
        [
            new(-0.75f, -0.50f, 0.5f, 0f, 0f, 1f, 0xFFFFFF00),
            new( 0.00f,  0.50f, 0.5f, 0f, 0f, 1f, 0xFFFFFF00),
            new( 0.75f, -0.50f, 0.5f, 0f, 0f, 1f, 0xFFFFFF00)
        ];
        ProGpuDirectXSciChartVertex3D[] meshVertices =
        [
            new(-0.75f, -0.75f, 0.5f, 0f, 0f, 1f, 0xFFFF0000),
            new( 0.75f, -0.75f, 0.5f, 0f, 0f, 1f, 0xFF00FF00),
            new( 0.00f,  0.75f, 0.5f, 0f, 0f, 1f, 0xFF0000FF)
        ];
        ProGpuDirectXSciChartVertex3D[] stripVertices =
        [
            new(-0.75f, -0.75f, 0.25f, 0f, 0f, 1f, 0xFFFF0000),
            new( 0.75f, -0.75f, 0.25f, 0f, 0f, 1f, 0xFFFF0000),
            new(-0.75f,  0.75f, 0.25f, 0f, 0f, 1f, 0xFFFF0000),
            new( 0.75f,  0.75f, 0.25f, 0f, 0f, 1f, 0xFFFF0000)
        ];
        float[] surfaceHeights = [-0.75f, -0.75f, 0.75f, 0.75f];
        uint[] indices = [0, 1, 2];

        renderContext.SetClipRect(new DxRect(0, 0, 32, 64));
        renderContext.DrawPointCloud(pointVertices, Matrix4x4.Identity);
        renderContext.DrawLineStrip(lineVertices, Matrix4x4.Identity);
        renderContext.DrawTriangleMesh(
            meshVertices,
            indices,
            Matrix4x4.Identity,
            new Vector3(0f, 0f, 1f),
            DxCullMode.None);
        renderContext.DrawTriangleStrip(
            stripVertices,
            Matrix4x4.Identity,
            new Vector3(0f, 0f, 1f),
            DxCullMode.None);
        renderContext.DrawSurfaceMesh(
            surfaceHeights,
            columns: 2,
            rows: 2,
            worldViewProjection: Matrix4x4.Identity,
            xRange: new Vector2(-0.75f, 0.75f),
            zRange: new Vector2(0.25f, 0.25f),
            lowColorArgb: 0xFF0000FF,
            highColorArgb: 0xFFFF0000,
            lightDirection: new Vector3(0f, 0f, -1f),
            cullMode: DxCullMode.None);

        Assert.Single(renderContext.PointCloudDraws);
        Assert.Single(renderContext.LineDraws);
        Assert.Single(renderContext.MeshDraws);
        Assert.Single(renderContext.TriangleStripDraws);
        Assert.Single(renderContext.SurfaceMeshDraws);
        Assert.Equal(new DxRect(0, 0, 32, 64), renderContext.PointCloudDraws[0].ClipRect);
        Assert.Equal(new DxRect(0, 0, 32, 64), renderContext.LineDraws[0].ClipRect);
        Assert.True(renderContext.LineDraws[0].IsStrip);
        Assert.Equal(lineVertices, renderContext.LineDraws[0].Vertices);
        Assert.Equal(new DxRect(0, 0, 32, 64), renderContext.MeshDraws[0].ClipRect);
        Assert.Equal(new DxRect(0, 0, 32, 64), renderContext.TriangleStripDraws[0].ClipRect);
        Assert.Equal(stripVertices, renderContext.TriangleStripDraws[0].Vertices);
        Assert.Equal(DxCullMode.None, renderContext.TriangleStripDraws[0].CullMode);
        Assert.Equal(new DxRect(0, 0, 32, 64), renderContext.SurfaceMeshDraws[0].ClipRect);
        Assert.Equal(4, renderContext.SurfaceMeshDraws[0].Vertices.Count);
        Assert.Equal(6, renderContext.SurfaceMeshDraws[0].Indices.Count);
        Assert.Equal(surfaceHeights, renderContext.SurfaceMeshDraws[0].Heights);
        Assert.Equal(DxPrimitiveTopology.TriangleList, renderContext.ImmediateContext.GraphicsPipeline?.Descriptor.Topology);
        Assert.Equal(DxResourceFormat.D32Float, renderContext.ImmediateContext.GraphicsPipeline?.Descriptor.DepthStencilFormat);
        Assert.True(renderContext.ImmediateContext.GraphicsPipeline?.Descriptor.DepthStencilState.DepthEnable);
        Assert.Equal(ProGpuDirectXCommandKind.DrawIndexed, renderContext.ImmediateContext.Commands[^1].Kind);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawTriangleMesh(meshVertices, [0, 1], Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawTriangleMesh(meshVertices, [0, 1, 3], Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawPointCloud(pointVertices, Matrix4x4.Identity, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawLineList(lineVertices, Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawTriangleStrip(meshVertices[..2], Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawSurfaceMesh([0f, 1f, 2f], columns: 2, rows: 2, worldViewProjection: Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawSurfaceMesh([0f, float.NaN, 1f, 2f], columns: 2, rows: 2, worldViewProjection: Matrix4x4.Identity));
    }

    [Fact]
    public void SciChartRenderContext3DDrawsXyzSeriesThroughNativePointLineAndRibbonPaths()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext3D(device, 64, 64);
        ProGpuDirectXSciChartXyzPoint3D[] points =
        [
            new(10d, -2d, 100d),
            new(15d,  0d, 150d, 0xFFFF0000),
            new(20d,  2d, 200d)
        ];
        var options = new ProGpuDirectXSciChartXyzSeries3DOptions
        {
            ColorArgb = 0xFF42C6FF,
            Normal = new Vector3(0f, 1f, 0f)
        };

        renderContext.SetClipRect(new DxRect(4, 6, 48, 40));
        renderContext.DrawXyzDataSeriesLineStrip(points, Matrix4x4.Identity, options);
        renderContext.DrawXyzDataSeriesRibbon(points, Matrix4x4.Identity, halfThickness: 0.05f, options);
        renderContext.DrawXyzDataSeriesPointCloud(points, Matrix4x4.Identity, options);

        Assert.Single(renderContext.LineDraws);
        Assert.Single(renderContext.TriangleStripDraws);
        Assert.Single(renderContext.PointCloudDraws);
        Assert.True(renderContext.LineDraws[0].IsStrip);
        Assert.Equal(new DxRect(4, 6, 48, 40), renderContext.LineDraws[0].ClipRect);
        Assert.Equal(new DxRect(4, 6, 48, 40), renderContext.TriangleStripDraws[0].ClipRect);
        Assert.Equal(new DxRect(4, 6, 48, 40), renderContext.PointCloudDraws[0].ClipRect);

        var lineVertices = renderContext.LineDraws[0].Vertices;
        Assert.Equal(-1f, lineVertices[0].X);
        Assert.Equal(0f, lineVertices[1].X);
        Assert.Equal(1f, lineVertices[2].X);
        Assert.Equal(-1f, lineVertices[0].Y);
        Assert.Equal(0f, lineVertices[1].Y);
        Assert.Equal(1f, lineVertices[2].Y);
        Assert.Equal(-1f, lineVertices[0].Z);
        Assert.Equal(0f, lineVertices[1].Z);
        Assert.Equal(1f, lineVertices[2].Z);
        Assert.Equal(0xFF42C6FFu, lineVertices[0].ColorArgb);
        Assert.Equal(0xFFFF0000u, lineVertices[1].ColorArgb);
        Assert.Equal(0f, lineVertices[0].NormalX);
        Assert.Equal(1f, lineVertices[0].NormalY);
        Assert.Equal(0f, lineVertices[0].NormalZ);
        Assert.Equal(6, renderContext.TriangleStripDraws[0].Vertices.Count);
        Assert.Equal(-1.05f, renderContext.TriangleStripDraws[0].Vertices[0].Y);
        Assert.Equal(-0.95f, renderContext.TriangleStripDraws[0].Vertices[1].Y);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawXyzDataSeriesLineStrip([new ProGpuDirectXSciChartXyzPoint3D(double.NaN, 0d, 0d)], Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawXyzDataSeriesRibbon(points, Matrix4x4.Identity, halfThickness: 0f, options));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawXyzDataSeriesPointCloud(
                points,
                Matrix4x4.Identity,
                new ProGpuDirectXSciChartXyzSeries3DOptions { Normal = Vector3.Zero }));
    }

    [Fact]
    public void SciChartRenderContext3DDrawsWaterfallDataSeriesThroughNativeMeshPath()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext3D(device, 64, 64);
        float[] heights = [0f, 1f, 2f, 3f, 4f, 5f];
        var options = new ProGpuDirectXSciChartWaterfall3DOptions
        {
            LowColorArgb = 0xFF1455D9,
            HighColorArgb = 0xFFFFD166,
            Normal = new Vector3(0f, 0f, 1f)
        };

        renderContext.SetClipRect(new DxRect(2, 4, 40, 42));
        renderContext.DrawWaterfallDataSeries(
            heights,
            columns: 3,
            rows: 2,
            worldViewProjection: Matrix4x4.Identity,
            options,
            new Vector3(0f, 0f, 1f),
            DxCullMode.None);

        Assert.Single(renderContext.WaterfallDraws);
        Assert.Equal(new DxRect(2, 4, 40, 42), renderContext.WaterfallDraws[0].ClipRect);
        Assert.Equal(12, renderContext.WaterfallDraws[0].Vertices.Count);
        Assert.Equal(24, renderContext.WaterfallDraws[0].Indices.Count);
        Assert.Equal(heights, renderContext.WaterfallDraws[0].Heights);
        Assert.Equal(new ProGpuDirectXSciChartDoubleRange(0d, 2d), renderContext.WaterfallDraws[0].XRange);
        Assert.Equal(new ProGpuDirectXSciChartDoubleRange(0d, 5d), renderContext.WaterfallDraws[0].YRange);
        Assert.Equal(new ProGpuDirectXSciChartDoubleRange(0d, 1d), renderContext.WaterfallDraws[0].ZRange);

        var vertices = renderContext.WaterfallDraws[0].Vertices;
        Assert.Equal(-1f, vertices[0].X);
        Assert.Equal(-1f, vertices[0].Y);
        Assert.Equal(-1f, vertices[0].Z);
        Assert.Equal(0xFF1455D9u, vertices[0].ColorArgb);
        Assert.Equal(-1f, vertices[1].Y);
        Assert.Equal(1f, vertices[^2].X);
        Assert.Equal(1f, vertices[^2].Y);
        Assert.Equal(1f, vertices[^2].Z);
        Assert.Equal(0xFFFFD166u, vertices[^2].ColorArgb);
        Assert.Equal(-1f, vertices[^1].Y);
        Assert.Equal(DxPrimitiveTopology.TriangleList, renderContext.ImmediateContext.GraphicsPipeline?.Descriptor.Topology);
        Assert.Equal(ProGpuDirectXCommandKind.DrawIndexed, renderContext.ImmediateContext.Commands[^1].Kind);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawWaterfallDataSeries([0f], columns: 1, rows: 1, worldViewProjection: Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawWaterfallDataSeries([0f, 1f], columns: 2, rows: 0, worldViewProjection: Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawWaterfallDataSeries([0f, 1f, 2f], columns: 2, rows: 2, worldViewProjection: Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawWaterfallDataSeries([0f, float.NaN], columns: 2, rows: 1, worldViewProjection: Matrix4x4.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawWaterfallDataSeries(
                [0f, 1f],
                columns: 2,
                rows: 1,
                worldViewProjection: Matrix4x4.Identity,
                new ProGpuDirectXSciChartWaterfall3DOptions { Normal = Vector3.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawWaterfallDataSeries(
                [0f, 1f],
                columns: 2,
                rows: 1,
                worldViewProjection: Matrix4x4.Identity,
                new ProGpuDirectXSciChartWaterfall3DOptions { BaseY = float.NaN }));
    }

    [Fact]
    public void SciChartRenderContextRecordsTextDrawsAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        var font = TryCreateSciChartFont(renderContext);
        if (font is null)
        {
            return;
        }

        renderContext.SetClipRect(new DxRect(0, 0, 32, 16));
        renderContext.DrawText(
            "Axis",
            font,
            12f,
            0xFF00FF00,
            new ProGpuDirectXSciChartPoint(16, 8),
            rotationRadians: 0.25f,
            horizontalAlignment: ProGpuDirectXSciChartTextAlignment.Center,
            verticalAlignment: ProGpuDirectXSciChartVerticalTextAlignment.Baseline,
            isBold: true,
            isItalic: true,
            isAntiAliased: false);

        Assert.Single(renderContext.TextDraws);
        var draw = renderContext.TextDraws[0];
        Assert.Equal("Axis", draw.Text);
        Assert.Equal(font, draw.Font);
        Assert.Equal(12f, draw.FontSize);
        Assert.Equal(0xFF00FF00u, draw.ColorArgb);
        Assert.Equal(new ProGpuDirectXSciChartPoint(16, 8), draw.Position);
        Assert.Equal(0.25f, draw.RotationRadians);
        Assert.Equal(ProGpuDirectXSciChartTextAlignment.Center, draw.HorizontalAlignment);
        Assert.Equal(ProGpuDirectXSciChartVerticalTextAlignment.Baseline, draw.VerticalAlignment);
        Assert.True(draw.IsBold);
        Assert.True(draw.IsItalic);
        Assert.False(draw.IsAntiAliased);
        Assert.Equal(new DxRect(0, 0, 32, 16), draw.ClipRect);

        Assert.Throws<ArgumentNullException>(() =>
            renderContext.DrawText(null!, font, 12f, 0xFF00FF00, new ProGpuDirectXSciChartPoint(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawText("Axis", font, 0f, 0xFF00FF00, new ProGpuDirectXSciChartPoint(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawText("Axis", font, 12f, 0xFF00FF00, new ProGpuDirectXSciChartPoint(float.NaN, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawText("Axis", font, 12f, 0xFF00FF00, new ProGpuDirectXSciChartPoint(0, 0), float.NaN));

        renderContext.BeginFrame();
        Assert.Empty(renderContext.TextDraws);

        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawText(
            "Axis",
            font,
            12f,
            0xFF00FF00,
            new ProGpuDirectXSciChartPoint(16, 8));
        Assert.Empty(renderContext.TextDraws);
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
        Assert.Empty(renderContext.LineBatchDraws);
        Assert.Empty(renderContext.ColumnBatchDraws);
        Assert.Empty(renderContext.RectBatchDraws);
        Assert.Empty(renderContext.SpriteBatchDraws);
        Assert.Empty(renderContext.FinancialBatchDraws);
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
    public void SciChartRenderContextRecordsLineBatchesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        ProGpuDirectXSciChartColorVertex[] vertices =
        [
            new(0, 8, 0, 0xFF00FF00),
            new(16, 8, 0, 0xFF00FF00),
            new(float.NaN, float.NaN, 0, 0xFF00FF00),
            new(16, 24, 0, 0xFF00FF00),
            new(32, 24, 0, 0xFF00FF00)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.LineBatchDraws);
        Assert.Equal(new DxRect(0, 0, 8, 16), renderContext.LineBatchDraws[0].ClipRect);
        Assert.Equal(vertices.Length, renderContext.LineBatchDraws[0].Vertices.Count);
        var drawCommand = renderContext.ImmediateContext.Commands[^1];
        var draw = drawCommand.Draw ?? throw new InvalidOperationException("Expected SciChart line batch draw command payload.");
        Assert.Equal(ProGpuDirectXCommandKind.Draw, drawCommand.Kind);
        Assert.Equal(4u, draw.VertexCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawLinesBatch(vertices, count: 1, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawLinesBatch(vertices, count: vertices.Length + 1, default));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform());
        Assert.Empty(renderContext.LineBatchDraws);

        renderContext.BeginFrame();
        var publicLinePen = renderContext.CreatePen(0xFFFF0000, strokeThickness: 2f, isAntiAliased: false);
        ProGpuDirectXSciChartColoredVertex[] publicVertices =
        [
            new(0, 0, 0, 0xFFFFFFFF),
            new(4, 12, 2, 0xFFFF0000),
            new(20, 12, 2, 0xFFFF0000)
        ];

        renderContext.DrawLineStrip(
            publicLinePen,
            publicVertices,
            startIndex: 1,
            count: 2,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.LineBatchDraws);
        Assert.Equal(publicLinePen, renderContext.LineBatchDraws[0].Pen);
        Assert.True(renderContext.LineBatchDraws[0].IsStrips);
        Assert.Equal(2, renderContext.LineBatchDraws[0].Vertices.Count);
        Assert.Equal(2, renderContext.LineBatchDraws[0].Vertices[0].Offset);
        Assert.Equal(0xFFFF0000u, renderContext.LineBatchDraws[0].Vertices[0].ColorArgb);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawLineStrip(publicLinePen, publicVertices, startIndex: 1, count: 1, transform: default));
    }

    [Fact]
    public void SciChartRenderContextRecordsPenAwareLineBatches()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        var pen = renderContext.CreatePen(0xFF00FF00, strokeThickness: 3f, isAntiAliased: false);
        ProGpuDirectXSciChartColorVertex[] vertices =
        [
            new(0, 4, 0, 0),
            new(8, 12, 0, 0),
            new(16, 12, 0, 0xFFFF0000),
            new(24, 12, 0, 0xFFFF0000),
            new(32, 12, 0, 0xFF0000FF)
        ];

        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            pen,
            isStrips: false,
            isDigital: true,
            isDrawNanAsGaps: false,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.LineBatchDraws);
        Assert.Equal(pen, renderContext.LineBatchDraws[0].Pen);
        Assert.False(renderContext.LineBatchDraws[0].IsStrips);
        Assert.True(renderContext.LineBatchDraws[0].IsDigital);
        Assert.False(renderContext.LineBatchDraws[0].IsDrawNanAsGaps);
        Assert.Equal(vertices.Length, renderContext.LineBatchDraws[0].Vertices.Count);
        Assert.Equal(DxPrimitiveTopology.TriangleList, renderContext.ImmediateContext.GraphicsPipeline?.Descriptor.Topology);
        var draw = renderContext.ImmediateContext.Commands[^1].Draw ?? throw new InvalidOperationException("Expected SciChart pen-aware line draw command payload.");
        Assert.Equal(18u, draw.VertexCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.CreatePen(0xFF00FF00, strokeThickness: 0f));
    }

    [Fact]
    public void SciChartRenderContextExpandsAntialiasedThickLines()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        var antialiasedPen = renderContext.CreatePen(0xFF00FF00, strokeThickness: 3f);
        var hardEdgePen = renderContext.CreatePen(0xFF00FF00, strokeThickness: 3f, isAntiAliased: false);
        ProGpuDirectXSciChartColorVertex[] vertices =
        [
            new(0, 8, 0, 0xFF00FF00),
            new(16, 8, 0, 0xFF00FF00)
        ];

        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            antialiasedPen,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.LineBatchDraws);
        Assert.True(renderContext.LineBatchDraws[0].Pen.IsAntiAliased);
        Assert.Equal(DxPrimitiveTopology.TriangleList, renderContext.ImmediateContext.GraphicsPipeline?.Descriptor.Topology);
        var antialiasedDraw = renderContext.ImmediateContext.Commands[^1].Draw
            ?? throw new InvalidOperationException("Expected SciChart antialiased line draw command payload.");
        Assert.Equal(18u, antialiasedDraw.VertexCount);

        renderContext.BeginFrame();
        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            hardEdgePen,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.LineBatchDraws);
        Assert.False(renderContext.LineBatchDraws[0].Pen.IsAntiAliased);
        var hardEdgeDraw = renderContext.ImmediateContext.Commands[^1].Draw
            ?? throw new InvalidOperationException("Expected SciChart hard-edge line draw command payload.");
        Assert.Equal(6u, hardEdgeDraw.VertexCount);
    }

    [Fact]
    public void SciChartRenderContextRecordsBasePrimitivesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        var pen = renderContext.CreatePen(0xFF00FF00);
        var thickPen = renderContext.CreatePen(0xFF00FF00, strokeThickness: 2f);
        var brush = renderContext.CreateBrush(0xFF00FF00);
        ProGpuDirectXSciChartPoint[] points =
        [
            new(0, 8),
            new(16, 8),
            new(32, 16)
        ];
        ProGpuDirectXSciChartPoint[] polygon =
        [
            new(0, 0),
            new(16, 0),
            new(16, 16),
            new(0, 16)
        ];
        ProGpuDirectXSciChartAreaSegment[] area =
        [
            new(new ProGpuDirectXSciChartPoint(0, 4), new ProGpuDirectXSciChartPoint(0, 12)),
            new(new ProGpuDirectXSciChartPoint(16, 4), new ProGpuDirectXSciChartPoint(16, 12))
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 32, 32));
        renderContext.DrawLine(pen, new ProGpuDirectXSciChartPoint(0, 4), new ProGpuDirectXSciChartPoint(16, 4));
        renderContext.DrawLines(pen, points);
        renderContext.DrawQuad(thickPen, new ProGpuDirectXSciChartPoint(0, 0), new ProGpuDirectXSciChartPoint(16, 16));
        renderContext.FillRectangle(brush, new ProGpuDirectXSciChartPoint(0, 0), new ProGpuDirectXSciChartPoint(16, 16), opacity: 0.5d);
        renderContext.FillPolygon(brush, polygon);
        renderContext.FillArea(brush, area, isVerticalChart: true, gradientRotationAngle: 90d);

        Assert.Equal(6, renderContext.PrimitiveDraws.Count);
        Assert.Equal(
            [
                ProGpuDirectXSciChartPrimitiveKind.Line,
                ProGpuDirectXSciChartPrimitiveKind.Lines,
                ProGpuDirectXSciChartPrimitiveKind.Quad,
                ProGpuDirectXSciChartPrimitiveKind.RectangleFill,
                ProGpuDirectXSciChartPrimitiveKind.PolygonFill,
                ProGpuDirectXSciChartPrimitiveKind.AreaFill
            ],
            renderContext.PrimitiveDraws.Select(draw => draw.Kind).ToArray());
        Assert.Equal(new DxRect(0, 0, 32, 32), renderContext.PrimitiveDraws[0].ClipRect);
        Assert.Equal(0.5d, renderContext.PrimitiveDraws[3].Opacity);
        Assert.True(renderContext.PrimitiveDraws[5].IsVerticalChart);
        Assert.Equal(90d, renderContext.PrimitiveDraws[5].GradientRotationAngle);
        Assert.Equal(4, renderContext.PrimitiveDraws[5].Points.Count);

        var drawVertexCounts = renderContext.ImmediateContext.Commands
            .Where(command => command.Kind == ProGpuDirectXCommandKind.Draw)
            .Select(command => (command.Draw ?? throw new InvalidOperationException("Expected SciChart primitive draw payload.")).VertexCount)
            .ToArray();
        Assert.Equal([2u, 4u, 48u, 6u, 6u, 6u], drawVertexCounts);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawLines(pen, points, count: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.FillPolygon(brush, polygon, count: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.FillArea(brush, area, count: 1, isVerticalChart: false, gradientRotationAngle: 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.FillRectangle(brush, new ProGpuDirectXSciChartPoint(0, 0), new ProGpuDirectXSciChartPoint(16, 16), opacity: 2d));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawLine(pen, new ProGpuDirectXSciChartPoint(0, 0), new ProGpuDirectXSciChartPoint(16, 0));
        renderContext.FillRectangle(brush, new ProGpuDirectXSciChartPoint(0, 0), new ProGpuDirectXSciChartPoint(16, 16));
        Assert.Empty(renderContext.PrimitiveDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsGradientPrimitiveBrushes()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        var gradientBrush = renderContext.CreateLinearGradientBrush(0xFFFF0000, 0xFF0000FF, gradientRotationAngle: 90d);

        Assert.True(gradientBrush.IsGradient);
        Assert.Equal(0xFFFF0000u, gradientBrush.ColorArgb);
        Assert.Equal(0xFFFF0000u, gradientBrush.StartColorArgb);
        Assert.Equal(0xFF0000FFu, gradientBrush.EndColorArgb);
        Assert.Equal(90d, gradientBrush.GradientRotationAngle);

        renderContext.FillRectangle(
            gradientBrush,
            new ProGpuDirectXSciChartPoint(0, 0),
            new ProGpuDirectXSciChartPoint(16, 16),
            opacity: 0.75d);

        Assert.Single(renderContext.PrimitiveDraws);
        Assert.Equal(ProGpuDirectXSciChartPrimitiveKind.RectangleFill, renderContext.PrimitiveDraws[0].Kind);
        Assert.Equal(gradientBrush, renderContext.PrimitiveDraws[0].Brush);
        Assert.Equal(0.75d, renderContext.PrimitiveDraws[0].Opacity);
        var draw = renderContext.ImmediateContext.Commands[^1].Draw
            ?? throw new InvalidOperationException("Expected SciChart gradient primitive draw payload.");
        Assert.Equal(6u, draw.VertexCount);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.CreateLinearGradientBrush(0xFFFF0000, 0xFF0000FF, double.NaN));
    }

    [Fact]
    public void SciChartRenderContextRecordsEllipsePrimitivesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        var strokePen = renderContext.CreatePen(0xFFFF0000, strokeThickness: 2f);
        var fillBrush = renderContext.CreateBrush(0xFF00FF00);
        var center = new ProGpuDirectXSciChartPoint(16, 16);

        renderContext.SetClipRect(new DxRect(0, 0, 32, 32));
        renderContext.DrawEllipse(strokePen, fillBrush, center, width: 16d, height: 12d);

        Assert.Single(renderContext.PrimitiveDraws);
        var primitive = renderContext.PrimitiveDraws[0];
        Assert.Equal(ProGpuDirectXSciChartPrimitiveKind.Ellipse, primitive.Kind);
        Assert.Equal(center, primitive.Points[0]);
        Assert.Equal(strokePen, primitive.Pen);
        Assert.Equal(fillBrush, primitive.Brush);
        Assert.Equal(16d, primitive.Width);
        Assert.Equal(12d, primitive.Height);
        Assert.Equal(new DxRect(0, 0, 32, 32), primitive.ClipRect);

        var drawVertexCounts = renderContext.ImmediateContext.Commands
            .Where(command => command.Kind == ProGpuDirectXCommandKind.Draw)
            .Select(command => (command.Draw ?? throw new InvalidOperationException("Expected SciChart ellipse draw payload.")).VertexCount)
            .ToArray();
        Assert.Equal([84u, 336u], drawVertexCounts);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawEllipse(strokePen, fillBrush, center, width: 0d, height: 12d));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawEllipse(strokePen, fillBrush, center, width: 16d, height: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawEllipse(strokePen, fillBrush, new ProGpuDirectXSciChartPoint(float.NaN, 16), width: 16d, height: 12d));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawEllipse(strokePen, fillBrush, center, width: 16d, height: 12d);
        Assert.Empty(renderContext.PrimitiveDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsMountainBatches()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        var pen = renderContext.CreatePen(0xFFFF0000);
        var brush = renderContext.CreateBrush(0xFF00FF00);
        ProGpuDirectXSciChartBandVertex[] vertices =
        [
            new(0, 8, 24),
            new(16, 8, 24),
            new(32, 16, 24)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 32, 32));
        renderContext.DrawMountainBatch(
            vertices,
            count: vertices.Length,
            pen,
            brush,
            isDigital: false,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.MountainBatchDraws);
        Assert.Equal(new DxRect(0, 0, 32, 32), renderContext.MountainBatchDraws[0].ClipRect);
        Assert.Equal(pen, renderContext.MountainBatchDraws[0].Pen);
        Assert.Equal(brush, renderContext.MountainBatchDraws[0].Brush);
        var drawVertexCounts = renderContext.ImmediateContext.Commands
            .Where(command => command.Kind == ProGpuDirectXCommandKind.Draw)
            .Select(command => (command.Draw ?? throw new InvalidOperationException("Expected SciChart mountain draw payload.")).VertexCount)
            .ToArray();
        Assert.Equal([12u, 4u], drawVertexCounts);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawMountainBatch(vertices, count: 1, pen, brush, false, default));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawMountainBatch(
            vertices,
            count: vertices.Length,
            pen,
            brush,
            isDigital: false,
            transform: new ProGpuDirectXSciChartVertexTransform());
        Assert.Empty(renderContext.MountainBatchDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsBandBatches()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        var penA = renderContext.CreatePen(0xFFFF0000);
        var penB = renderContext.CreatePen(0xFF0000FF);
        var brushPositive = renderContext.CreateBrush(0xFF00FF00);
        var brushNegative = renderContext.CreateBrush(0xFFFFFF00);
        ProGpuDirectXSciChartBandVertex[] vertices =
        [
            new(0, 8, 24),
            new(16, 24, 8),
            new(32, 24, 8)
        ];

        renderContext.DrawBandsBatch(
            vertices,
            count: vertices.Length,
            penA,
            penB,
            brushPositive,
            brushNegative,
            isDigital: false,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.BandBatchDraws);
        Assert.Equal(penA, renderContext.BandBatchDraws[0].PenA);
        Assert.Equal(penB, renderContext.BandBatchDraws[0].PenB);
        Assert.Equal(brushPositive, renderContext.BandBatchDraws[0].BrushPositive);
        Assert.Equal(brushNegative, renderContext.BandBatchDraws[0].BrushNegative);
        var drawVertexCounts = renderContext.ImmediateContext.Commands
            .Where(command => command.Kind == ProGpuDirectXCommandKind.Draw)
            .Select(command => (command.Draw ?? throw new InvalidOperationException("Expected SciChart band draw payload.")).VertexCount)
            .ToArray();
        Assert.Equal([18u, 4u, 4u], drawVertexCounts);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawBandsBatch(vertices, count: vertices.Length + 1, penA, penB, brushPositive, brushNegative, false, default));
    }

    [Fact]
    public void SciChartRenderContextLineBatchHonorsNanGapPolicy()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        ProGpuDirectXSciChartColorVertex[] vertices =
        [
            new(0, 8, 0, 0xFF00FF00),
            new(float.NaN, float.NaN, 0, 0xFF00FF00),
            new(16, 8, 0, 0xFF00FF00),
            new(32, 8, 0, 0xFF00FF00)
        ];

        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            ProGpuDirectXSciChartPen2D.Default,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            transform: new ProGpuDirectXSciChartVertexTransform());
        var gapDraw = renderContext.ImmediateContext.Commands[^1].Draw ?? throw new InvalidOperationException("Expected SciChart gap line draw command payload.");
        Assert.Equal(2u, gapDraw.VertexCount);

        renderContext.BeginFrame();
        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            ProGpuDirectXSciChartPen2D.Default,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: false,
            transform: new ProGpuDirectXSciChartVertexTransform());
        var closedDraw = renderContext.ImmediateContext.Commands[^1].Draw ?? throw new InvalidOperationException("Expected SciChart closed line draw command payload.");
        Assert.Equal(4u, closedDraw.VertexCount);
    }

    [Fact]
    public void SciChartRenderContextRecordsColumnBatchesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        ProGpuDirectXSciChartColumnVertex[] vertices =
        [
            new(0, 0, 8, 16, 0xFF00FF00, 0xFFFF0000),
            new(10, 0, 8, 16, 0x0000FF00, 0xFFFF0000),
            new(float.NaN, 0, 8, 16, 0xFF00FF00, 0xFFFF0000)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 24, 16));
        renderContext.DrawColumnsBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.ColumnBatchDraws);
        Assert.Equal(new DxRect(0, 0, 24, 16), renderContext.ColumnBatchDraws[0].ClipRect);
        Assert.Equal(vertices.Length, renderContext.ColumnBatchDraws[0].Vertices.Count);
        var drawVertexCounts = renderContext.ImmediateContext.Commands
            .Where(command => command.Kind == ProGpuDirectXCommandKind.Draw)
            .Select(command => (command.Draw ?? throw new InvalidOperationException("Expected SciChart column draw payload.")).VertexCount)
            .ToArray();
        Assert.Equal([6u, 16u], drawVertexCounts);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawColumnsBatch(vertices, count: 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawColumnsBatch(vertices, count: vertices.Length + 1, default));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawColumnsBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform());
        Assert.Empty(renderContext.ColumnBatchDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsRectBatchesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        ProGpuDirectXSciChartRectVertex[] vertices =
        [
            new(8, 8, 8, 8, 0xFF00FF00),
            new(24, 8, 8, 8, 0x0000FF00),
            new(float.NaN, 8, 8, 8, 0xFF00FF00)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 16, 16));
        renderContext.DrawRectsBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            anchor: ProGpuDirectXSciChartSpriteAnchor.Center);

        Assert.Single(renderContext.RectBatchDraws);
        Assert.Equal(new DxRect(0, 0, 16, 16), renderContext.RectBatchDraws[0].ClipRect);
        Assert.Equal(ProGpuDirectXSciChartSpriteAnchor.Center, renderContext.RectBatchDraws[0].Anchor);
        Assert.Equal(vertices.Length, renderContext.RectBatchDraws[0].Vertices.Count);
        var drawCommand = renderContext.ImmediateContext.Commands[^1];
        var draw = drawCommand.Draw ?? throw new InvalidOperationException("Expected SciChart rect batch draw command payload.");
        Assert.Equal(ProGpuDirectXCommandKind.Draw, drawCommand.Kind);
        Assert.Equal(6u, draw.VertexCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawRectsBatch(vertices, count: 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawRectsBatch(vertices, count: vertices.Length + 1, default));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawRectsBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform());
        Assert.Empty(renderContext.RectBatchDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsVerticalPixelsAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        int[] colors =
        [
            unchecked((int)0xFF00FF00),
            unchecked((int)0x0000FF00),
            unchecked((int)0xFFFF0000)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 16, 16));
        renderContext.DrawPixelsVertically(
            xLeft: 0,
            xRight: 8,
            yStartBottom: 16,
            yEndTop: 0,
            colors,
            opacity: 0.5d,
            yAxisIsFlipped: false);

        Assert.Single(renderContext.VerticalPixelsDraws);
        Assert.Single(renderContext.TextureDraws);
        var uniformDraw = renderContext.VerticalPixelsDraws[0];
        Assert.Equal(new DxRect(0, 0, 16, 16), uniformDraw.ClipRect);
        Assert.True(uniformDraw.IsUniform);
        Assert.False(uniformDraw.YAxisIsFlipped);
        Assert.Equal(colors.Length, uniformDraw.PixelColorsArgb.Count);
        Assert.Null(uniformDraw.YCoordinates);
        var uniformCommand = renderContext.ImmediateContext.Commands[^1];
        var uniformPayload = uniformCommand.Draw ?? throw new InvalidOperationException("Expected SciChart vertical texture draw command payload.");
        Assert.Equal(ProGpuDirectXCommandKind.Draw, uniformCommand.Kind);
        Assert.Equal(6u, uniformPayload.VertexCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawPixelsVertically(0, 0, 16, 0, colors, 1d, yAxisIsFlipped: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawPixelsVertically(0, 8, 0, 0, colors, 1d, yAxisIsFlipped: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawPixelsVertically(0, 8, 16, 0, ReadOnlySpan<int>.Empty, 1d, yAxisIsFlipped: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawPixelsVertically(0, 8, 16, 0, colors, 1.5d, yAxisIsFlipped: false));

        renderContext.BeginFrame();
        int[] yCoordinates = [0, 8, 16];
        int[] runColors =
        [
            unchecked((int)0xFF00FF00),
            unchecked((int)0xFFFF0000)
        ];
        renderContext.SetClipRect(new DxRect(0, 0, 16, 16));
        renderContext.DrawPixelsVertically(
            xLeft: 0,
            xRight: 8,
            yCoordinates,
            runColors,
            opacity: 1d,
            isUniform: false,
            yAxisIsFlipped: true);

        Assert.Single(renderContext.VerticalPixelsDraws);
        var coordinateDraw = renderContext.VerticalPixelsDraws[0];
        Assert.False(coordinateDraw.IsUniform);
        Assert.True(coordinateDraw.YAxisIsFlipped);
        Assert.Equal(yCoordinates, coordinateDraw.YCoordinates);
        Assert.Equal(runColors, coordinateDraw.PixelColorsArgb);
        var coordinateCommand = renderContext.ImmediateContext.Commands[^1];
        var coordinatePayload = coordinateCommand.Draw ?? throw new InvalidOperationException("Expected SciChart vertical coordinate draw command payload.");
        Assert.Equal(12u, coordinatePayload.VertexCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawPixelsVertically(0, 8, yCoordinates.AsSpan(0, 1), runColors, 1d, isUniform: false, yAxisIsFlipped: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawPixelsVertically(0, 8, yCoordinates, runColors.AsSpan(0, 1), 1d, isUniform: false, yAxisIsFlipped: false));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawPixelsVertically(0, 8, 16, 0, colors, 1d, yAxisIsFlipped: false);
        Assert.Empty(renderContext.VerticalPixelsDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsSpriteBatchesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        using var sprite = renderContext.CreateSprite(4, 4);
        using var strokeSprite = renderContext.CreateSprite(4, 4);
        sprite.SetData(Enumerable.Repeat(unchecked((int)0xFFFFFFFF), 16).ToArray());
        strokeSprite.SetData(Enumerable.Repeat(unchecked((int)0xFFFFFFFF), 16).ToArray());
        ProGpuDirectXSciChartSpriteVertex[] vertices =
        [
            new(8, 8, 0xFF00FF00, 0xFFFF0000),
            new(24, 8, 0x0000FF00, 0xFFFF0000),
            new(float.NaN, 8, 0xFF00FF00, 0xFFFF0000)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 16, 16));
        renderContext.DrawSpritesBatch(
            vertices,
            count: vertices.Length,
            sprite,
            strokeSprite,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            centeredAmount: 0.5f,
            ProGpuDirectXSciChartTextureFiltering.Point);

        Assert.Single(renderContext.SpriteBatchDraws);
        Assert.Equal(new DxRect(0, 0, 16, 16), renderContext.SpriteBatchDraws[0].ClipRect);
        Assert.Equal(0.5f, renderContext.SpriteBatchDraws[0].CenteredAmount);
        Assert.Equal(vertices.Length, renderContext.SpriteBatchDraws[0].Vertices.Count);
        var drawVertexCounts = renderContext.ImmediateContext.Commands
            .Where(command => command.Kind == ProGpuDirectXCommandKind.Draw)
            .Select(command => (command.Draw ?? throw new InvalidOperationException("Expected SciChart sprite draw payload.")).VertexCount)
            .ToArray();
        Assert.Equal([6u, 12u], drawVertexCounts);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawSpritesBatch(vertices, count: 0, sprite, null, default, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawSpritesBatch(vertices, count: vertices.Length + 1, sprite, null, default, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawSpritesBatch(vertices, count: vertices.Length, sprite, null, default, float.NaN));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawSpritesBatch(
            vertices,
            count: vertices.Length,
            sprite,
            strokeSprite,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            centeredAmount: 0.5f);
        Assert.Empty(renderContext.SpriteBatchDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsColoredSpritesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        using var sprite = renderContext.CreateSprite(4, 4);
        sprite.SetData(Enumerable.Repeat(unchecked((int)0xFFFFFFFF), 16).ToArray());
        ProGpuDirectXSciChartColoredSpriteVertex[] vertices =
        [
            new(4, 4, 0xFF00FF00),
            new(8, 8, 0xFF00FF00),
            new(24, 8, 0xFFFF0000),
            new(float.NaN, 8, 0xFF00FF00)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 16, 16));
        renderContext.DrawColoredSprites(
            sprite,
            vertices,
            startIndex: 1,
            count: 2,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            centeredAmount: 0.5f,
            filtering: ProGpuDirectXSciChartTextureFiltering.Point);

        Assert.Single(renderContext.ColoredSpriteDraws);
        var drawRecord = renderContext.ColoredSpriteDraws[0];
        Assert.Equal(new DxRect(0, 0, 16, 16), drawRecord.ClipRect);
        Assert.Equal(1, drawRecord.StartIndex);
        Assert.Equal(2, drawRecord.Count);
        Assert.Equal(0.5f, drawRecord.CenteredAmount);
        Assert.Equal(2, drawRecord.Vertices.Count);
        Assert.Equal(8, drawRecord.Vertices[0].X);
        var drawCommand = renderContext.ImmediateContext.Commands[^1];
        var draw = drawCommand.Draw ?? throw new InvalidOperationException("Expected SciChart colored sprite draw command payload.");
        Assert.Equal(ProGpuDirectXCommandKind.Draw, drawCommand.Kind);
        Assert.Equal(6u, draw.VertexCount);
        Assert.Equal(2u, draw.InstanceCount);
        Assert.NotNull(drawCommand.VertexBufferBindings);
        Assert.True(drawCommand.VertexBufferBindings.ContainsKey(0));
        Assert.True(drawCommand.VertexBufferBindings.ContainsKey(1));
        Assert.Equal(8u, drawCommand.VertexBufferBindings[0].StrideInBytes);
        Assert.Equal(32u, drawCommand.VertexBufferBindings[1].StrideInBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawColoredSprites(sprite, vertices, startIndex: -1, count: 1, transform: default, centeredAmount: 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawColoredSprites(sprite, vertices, startIndex: 0, count: 0, transform: default, centeredAmount: 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawColoredSprites(sprite, vertices, startIndex: vertices.Length, count: 1, transform: default, centeredAmount: 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawColoredSprites(sprite, vertices, startIndex: 0, count: 1, transform: default, centeredAmount: float.NaN));

        renderContext.BeginFrame();
        ProGpuDirectXSciChartColoredVertex[] publicVertices =
        [
            new(1, 1, 0, 0xFFFFFFFF),
            new(4, 4, 7, 0xFF00FF00),
            new(12, 8, 9, 0xFFFF0000)
        ];

        renderContext.DrawColoredSprites(
            sprite,
            publicVertices,
            startIndex: 1,
            count: 2,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            centeredAmount: 0.5f,
            filtering: ProGpuDirectXSciChartTextureFiltering.Point);

        Assert.Single(renderContext.ColoredSpriteDraws);
        var publicVertexDraw = renderContext.ColoredSpriteDraws[0];
        Assert.Equal(1, publicVertexDraw.StartIndex);
        Assert.Equal(2, publicVertexDraw.Count);
        Assert.Equal(2, publicVertexDraw.Vertices.Count);
        Assert.Equal(4, publicVertexDraw.Vertices[0].X);
        Assert.Equal(0xFF00FF00u, publicVertexDraw.Vertices[0].ColorArgb);

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawColoredSprites(
            sprite,
            vertices,
            startIndex: 0,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            centeredAmount: 0.5f);
        Assert.Empty(renderContext.ColoredSpriteDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsCandleBatchesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        ProGpuDirectXSciChartOhlcCandleVertex[] vertices =
        [
            new(8, 6, 4, 14, 12, 0xFF00FF00, 0xFFFF0000),
            new(24, 6, 4, 14, 12, 0x0000FF00, 0x00000000),
            new(float.NaN, 6, 4, 14, 12, 0xFF00FF00, 0xFFFF0000)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 32, 16));
        renderContext.DrawCandlesBatch(
            vertices,
            count: vertices.Length,
            width: 4,
            transform: new ProGpuDirectXSciChartVertexTransform());

        Assert.Single(renderContext.FinancialBatchDraws);
        Assert.Equal(new DxRect(0, 0, 32, 16), renderContext.FinancialBatchDraws[0].ClipRect);
        Assert.Equal(ProGpuDirectXSciChartFinancialBatchKind.Candles, renderContext.FinancialBatchDraws[0].Kind);
        Assert.Equal(4, renderContext.FinancialBatchDraws[0].Width);
        Assert.Equal(vertices.Length, renderContext.FinancialBatchDraws[0].Vertices.Count);
        var drawVertexCounts = renderContext.ImmediateContext.Commands
            .Where(command => command.Kind == ProGpuDirectXCommandKind.Draw)
            .Select(command => (command.Draw ?? throw new InvalidOperationException("Expected SciChart candle draw payload.")).VertexCount)
            .ToArray();
        Assert.Equal([6u, 10u], drawVertexCounts);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawCandlesBatch(vertices, count: 0, width: 4, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawCandlesBatch(vertices, count: vertices.Length + 1, width: 4, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawCandlesBatch(vertices, count: vertices.Length, width: 0, default));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawCandlesBatch(
            vertices,
            count: vertices.Length,
            width: 4,
            transform: new ProGpuDirectXSciChartVertexTransform());
        Assert.Empty(renderContext.FinancialBatchDraws);
    }

    [Fact]
    public void SciChartRenderContextRecordsOhlcBatchesAndClip()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(device, 64, 32);
        ProGpuDirectXSciChartOhlcCandleVertex[] vertices =
        [
            new(8, 6, 4, 14, 12, 0x00000000, 0xFF00FF00),
            new(24, 6, 4, 14, 12, 0x00000000, 0x00000000),
            new(float.NaN, 6, 4, 14, 12, 0x00000000, 0xFF00FF00)
        ];

        renderContext.SetClipRect(new DxRect(0, 0, 32, 16));
        renderContext.DrawOhlcBatch(
            vertices,
            count: vertices.Length,
            width: 4,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            isDigital: true,
            isVerticalChart: true);

        Assert.Single(renderContext.FinancialBatchDraws);
        Assert.Equal(new DxRect(0, 0, 32, 16), renderContext.FinancialBatchDraws[0].ClipRect);
        Assert.Equal(ProGpuDirectXSciChartFinancialBatchKind.Ohlc, renderContext.FinancialBatchDraws[0].Kind);
        Assert.True(renderContext.FinancialBatchDraws[0].IsDigital);
        Assert.True(renderContext.FinancialBatchDraws[0].IsVerticalChart);
        Assert.Equal(vertices.Length, renderContext.FinancialBatchDraws[0].Vertices.Count);
        var drawCommand = renderContext.ImmediateContext.Commands[^1];
        var draw = drawCommand.Draw ?? throw new InvalidOperationException("Expected SciChart OHLC draw command payload.");
        Assert.Equal(ProGpuDirectXCommandKind.Draw, drawCommand.Kind);
        Assert.Equal(6u, draw.VertexCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawOhlcBatch(vertices, count: 0, width: 4, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawOhlcBatch(vertices, count: vertices.Length + 1, width: 4, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderContext.DrawOhlcBatch(vertices, count: vertices.Length, width: float.NaN, default));

        renderContext.BeginFrame();
        renderContext.SetClipRect(new DxRect(100, 100, 8, 8));
        renderContext.DrawOhlcBatch(
            vertices,
            count: vertices.Length,
            width: 4,
            transform: new ProGpuDirectXSciChartVertexTransform());
        Assert.Empty(renderContext.FinancialBatchDraws);
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
    public void GraphicsPipelineDescriptorUsesDirect3DCompatibleDefaults()
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
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm
        });

        Assert.False(pipeline.Descriptor.BlendState.EnableBlend);
        Assert.Equal(DxCullMode.Back, pipeline.Descriptor.RasterizerState.CullMode);
        Assert.Equal(DxFrontFace.Clockwise, pipeline.Descriptor.RasterizerState.FrontFace);
        Assert.True(pipeline.Descriptor.DepthStencilState.DepthEnable);
        Assert.Equal(DxDepthWriteMask.All, pipeline.Descriptor.DepthStencilState.DepthWriteMask);
        Assert.Equal(DxComparisonFunction.Less, pipeline.Descriptor.DepthStencilState.DepthFunction);
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
        Assert.NotNull(shader.BytecodeInfo);
        Assert.Equal(DxShaderBytecodeContainerKind.Dxbc, shader.BytecodeInfo.ContainerKind);
        Assert.False(shader.BytecodeInfo.IsValid);
        Assert.Contains("incomplete", shader.BytecodeInfo.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HlslBytecodeShadersReflectDxbcContainerMetadata()
    {
        var bytecode = CreateDxbcBytecode(
            ("ISGN", CreateSignatureChunk(("POSITION", 0u, 0u, 1u, 0u, 0x7u, 0x7u))),
            ("RDEF", CreateResourceDefinitionChunk(("SourceTexture", 2u, 5u, 4u, 0u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });

        Assert.False(shader.HasBackendShaderModule);
        Assert.Null(shader.BackendSource);
        Assert.NotNull(shader.BytecodeInfo);
        Assert.True(shader.BytecodeInfo.IsValid);
        Assert.Equal(DxShaderBytecodeContainerKind.Dxbc, shader.BytecodeInfo.ContainerKind);
        Assert.Equal(DxShaderProgramKind.Vertex, shader.BytecodeInfo.ProgramKind);
        Assert.Equal(5u, shader.BytecodeInfo.ShaderModelMajor);
        Assert.Equal(0u, shader.BytecodeInfo.ShaderModelMinor);
        Assert.True(shader.BytecodeInfo.HasInputSignature);
        Assert.True(shader.BytecodeInfo.HasResourceDefinition);
        Assert.True(shader.BytecodeInfo.HasTokenizedProgram);
        Assert.Equal(3, shader.BytecodeInfo.Chunks.Count);
        Assert.NotNull(shader.BytecodeInfo.GetChunk("SHEX"));

        var input = Assert.Single(shader.BytecodeInfo.InputSignature);
        Assert.Equal("POSITION", input.SemanticName);
        Assert.Equal(0u, input.SemanticIndex);
        Assert.Equal(0u, input.Register);
        Assert.Equal(0x7u, input.Mask);
        Assert.Equal(0x7u, input.ReadWriteMask);

        var resource = Assert.Single(shader.BytecodeInfo.ResourceBindings);
        Assert.Equal("SourceTexture", resource.Name);
        Assert.Equal(2u, resource.Type);
        Assert.Equal(4u, resource.Dimension);
        Assert.Equal(0u, resource.BindPoint);
        Assert.Equal(1u, resource.BindCount);
    }

    [Fact]
    public void HlslBytecodeResourceDefinitionInfersBindingRequirements()
    {
        var bytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(
                ("PerDrawConstants", (uint)DxReflectedShaderResourceType.ConstantBuffer, 0u, 0u, 0u, 1u, 0u),
                ("DiffuseTexture", (uint)DxReflectedShaderResourceType.Texture, 5u, 4u, 1u, 1u, 0u),
                ("LinearSampler", (uint)DxReflectedShaderResourceType.Sampler, 0u, 0u, 0u, 1u, 0u),
                ("PointBuffer", (uint)DxReflectedShaderResourceType.StructuredBuffer, 0u, 1u, 2u, 1u, 0u),
                ("OutputTexture", (uint)DxReflectedShaderResourceType.UnorderedAccessTyped, 5u, 4u, 0u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Pixel, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });

        Assert.NotNull(shader.BytecodeInfo);
        Assert.True(shader.BytecodeInfo.TryCreateBindingRequirements(DxShaderStage.Pixel, out var requirements));
        Assert.Equal(5, requirements.Count);
        Assert.True(shader.ReflectedBindingRequirementsSupported);
        Assert.Null(shader.ReflectedBindingRequirementsFailureReason);
        Assert.True(shader.HasReflectedBindingRequirements);
        Assert.Equal(requirements, shader.ReflectedBindingRequirements);

        AssertBindingRequirement(requirements, "PerDrawConstants", ProGpuDirectXBindingKind.ConstantBuffer, slot: 0, nativeBinding: 512);
        AssertBindingRequirement(requirements, "DiffuseTexture", ProGpuDirectXBindingKind.ShaderResourceView, slot: 1, nativeBinding: 577);
        AssertBindingRequirement(requirements, "LinearSampler", ProGpuDirectXBindingKind.Sampler, slot: 0, nativeBinding: 768);
        AssertBindingRequirement(requirements, "PointBuffer", ProGpuDirectXBindingKind.ShaderResourceView, slot: 2, nativeBinding: 578);
        AssertBindingRequirement(requirements, "OutputTexture", ProGpuDirectXBindingKind.UnorderedAccessView, slot: 0, nativeBinding: 832);
    }

    [Fact]
    public void HlslBytecodeResourceDefinitionRejectsUnknownBindingTypes()
    {
        var bytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(("MysteryResource", 99u, 0u, 0u, 0u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Pixel, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });

        Assert.NotNull(shader.BytecodeInfo);
        Assert.False(shader.BytecodeInfo.TryCreateBindingRequirements(DxShaderStage.Pixel, out var requirements));
        Assert.Empty(requirements);
        Assert.False(shader.ReflectedBindingRequirementsSupported);
        Assert.False(shader.HasReflectedBindingRequirements);
        Assert.Contains("unsupported", shader.ReflectedBindingRequirementsFailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HlslBytecodeGraphicsPipelineCombinesReflectedBindingRequirements()
    {
        var vertexBytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(
                ("FrameConstants", (uint)DxReflectedShaderResourceType.ConstantBuffer, 0u, 0u, 0u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        var pixelBytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(
                ("DiffuseTexture", (uint)DxReflectedShaderResourceType.Texture, 5u, 4u, 1u, 1u, 0u),
                ("DiffuseSampler", (uint)DxReflectedShaderResourceType.Sampler, 0u, 0u, 0u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Pixel, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = vertexBytecode
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = pixelBytecode
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader
        });

        Assert.True(pipeline.ReflectedBindingRequirementsSupported);
        Assert.Null(pipeline.ReflectedBindingRequirementsFailureReason);
        Assert.True(pipeline.HasReflectedBindingRequirements);
        Assert.Equal(3, pipeline.ReflectedBindingRequirements.Count);
        AssertBindingRequirement(
            pipeline.ReflectedBindingRequirements,
            "FrameConstants",
            ProGpuDirectXBindingKind.ConstantBuffer,
            slot: 0,
            nativeBinding: 0,
            stage: DxShaderStage.Vertex);
        AssertBindingRequirement(
            pipeline.ReflectedBindingRequirements,
            "DiffuseTexture",
            ProGpuDirectXBindingKind.ShaderResourceView,
            slot: 1,
            nativeBinding: 577);
        AssertBindingRequirement(
            pipeline.ReflectedBindingRequirements,
            "DiffuseSampler",
            ProGpuDirectXBindingKind.Sampler,
            slot: 0,
            nativeBinding: 768);
    }

    [Fact]
    public void HlslBytecodeComputePipelineExposesReflectedBindingRequirements()
    {
        var computeBytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(
                ("OutputValues", (uint)DxReflectedShaderResourceType.UnorderedAccessStructured, 0u, 1u, 2u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Compute, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = computeBytecode
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });

        Assert.True(pipeline.ReflectedBindingRequirementsSupported);
        Assert.Null(pipeline.ReflectedBindingRequirementsFailureReason);
        Assert.True(pipeline.HasReflectedBindingRequirements);
        AssertBindingRequirement(
            pipeline.ReflectedBindingRequirements,
            "OutputValues",
            ProGpuDirectXBindingKind.UnorderedAccessView,
            slot: 2,
            nativeBinding: 1858,
            stage: DxShaderStage.Compute);
    }

    [Fact]
    public void HlslBytecodeInputSignatureInfersInputLayoutDescriptor()
    {
        var bytecode = CreateDxbcBytecode(
            ("ISGN", CreateSignatureChunk(
                ("POSITION", 0u, 0u, 3u, 0u, 0x7u, 0x7u),
                ("COLOR", 0u, 0u, 3u, 1u, 0xFu, 0xFu),
                ("SV_InstanceID", 0u, 8u, 1u, 2u, 0x1u, 0x1u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });

        Assert.NotNull(shader.BytecodeInfo);
        Assert.True(shader.BytecodeInfo.TryCreateInputLayoutDescriptor(out var descriptor));
        Assert.NotNull(descriptor);
        var inputLayout = device.CreateInputLayout(descriptor);

        Assert.Equal(2, inputLayout.Elements.Count);
        Assert.Equal("POSITION", inputLayout.Elements[0].SemanticName);
        Assert.Equal(DxResourceFormat.R32G32B32Float, inputLayout.Elements[0].Format);
        Assert.Equal(0u, inputLayout.Elements[0].AlignedByteOffset);
        Assert.Equal(0u, inputLayout.Elements[0].ShaderLocation);
        Assert.Equal("COLOR", inputLayout.Elements[1].SemanticName);
        Assert.Equal(DxResourceFormat.R32G32B32A32Float, inputLayout.Elements[1].Format);
        Assert.Equal(12u, inputLayout.Elements[1].AlignedByteOffset);
        Assert.Equal(1u, inputLayout.Elements[1].ShaderLocation);
        Assert.DoesNotContain(inputLayout.Elements, element => element.SemanticName.StartsWith("SV_", StringComparison.Ordinal));
    }

    [Fact]
    public void InputLayoutRejectsElementsWithoutShaderLocations()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();

        var exception = Assert.Throws<ArgumentException>(() => device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 12
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32B32Float,
                    AlignedByteOffset = 0
                }
            ]
        }));

        Assert.Contains("shader location", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DxResourceFormat.R8Unorm)]
    [InlineData(DxResourceFormat.R16Float)]
    public void InputLayoutRejectsScalarFormatsWithoutWebGpuVertexEquivalent(DxResourceFormat format)
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();

        var exception = Assert.Throws<ArgumentException>(() => device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "WEIGHT",
                    Format = format,
                    ShaderLocation = 0
                }
            ]
        }));

        Assert.Contains("WebGPU vertex format", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HlslBytecodeInputSignatureInfersIntegerVectorFormats()
    {
        var bytecode = CreateDxbcBytecode(
            ("ISGN", CreateSignatureChunk(
                ("INDEX", 0u, 0u, 1u, 0u, 0x3u, 0x3u),
                ("DELTA", 0u, 0u, 2u, 1u, 0xFu, 0xFu))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });

        Assert.NotNull(shader.BytecodeInfo);
        Assert.True(shader.BytecodeInfo.TryCreateInputLayoutDescriptor(out var descriptor, inputSlot: 3, inputSlotClass: DxInputClassification.PerInstanceData, instanceDataStepRate: 1));
        Assert.NotNull(descriptor);
        var inputLayout = device.CreateInputLayout(descriptor);

        Assert.Equal(2, inputLayout.Elements.Count);
        Assert.Equal(DxResourceFormat.R32G32UInt, inputLayout.Elements[0].Format);
        Assert.Equal(3u, inputLayout.Elements[0].InputSlot);
        Assert.Equal(DxInputClassification.PerInstanceData, inputLayout.Elements[0].InputSlotClass);
        Assert.Equal(1u, inputLayout.Elements[0].InstanceDataStepRate);
        Assert.Equal(DxResourceFormat.R32G32B32A32SInt, inputLayout.Elements[1].Format);
        Assert.Equal(8u, inputLayout.Elements[1].AlignedByteOffset);
    }

    [Fact]
    public void HlslBytecodeInputSignatureRejectsUnsupportedMasks()
    {
        var bytecode = CreateDxbcBytecode(
            ("ISGN", CreateSignatureChunk(("TEXCOORD", 0u, 0u, 3u, 0u, 0x5u, 0x5u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });

        Assert.NotNull(shader.BytecodeInfo);
        Assert.False(shader.BytecodeInfo.TryCreateInputLayoutDescriptor(out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void HlslBytecodeGraphicsPipelineUsesReflectedInputLayoutWhenDescriptorOmitsOne()
    {
        var bytecode = CreateDxbcBytecode(
            ("ISGN", CreateSignatureChunk(
                ("POSITION", 0u, 0u, 3u, 0u, 0x7u, 0x7u),
                ("COLOR", 0u, 0u, 3u, 1u, 0xFu, 0xFu))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = shader
        });

        Assert.Null(pipeline.Descriptor.InputLayout);
        Assert.True(pipeline.UsesReflectedInputLayout);
        Assert.True(pipeline.ReflectedInputLayoutSupported);
        Assert.Null(pipeline.ReflectedInputLayoutFailureReason);
        Assert.NotNull(pipeline.EffectiveInputLayout);
        Assert.Equal(2, pipeline.EffectiveInputLayout.Elements.Count);
        Assert.Equal("POSITION", pipeline.EffectiveInputLayout.Elements[0].SemanticName);
        Assert.Equal(DxResourceFormat.R32G32B32Float, pipeline.EffectiveInputLayout.Elements[0].Format);
        Assert.Equal(0u, pipeline.EffectiveInputLayout.Elements[0].ShaderLocation);
        Assert.Equal("COLOR", pipeline.EffectiveInputLayout.Elements[1].SemanticName);
        Assert.Equal(DxResourceFormat.R32G32B32A32Float, pipeline.EffectiveInputLayout.Elements[1].Format);
        Assert.Equal(1u, pipeline.EffectiveInputLayout.Elements[1].ShaderLocation);
        Assert.Contains("POSITION0", pipeline.PipelineKey, StringComparison.Ordinal);
        Assert.DoesNotContain("no-layout", pipeline.PipelineKey, StringComparison.Ordinal);

        using var context = device.CreateImmediateContext();
        context.SetGraphicsPipeline(pipeline);

        Assert.Same(pipeline.EffectiveInputLayout, context.InputLayout);
    }

    [Fact]
    public void ExplicitGraphicsPipelineInputLayoutOverridesReflectedInputLayout()
    {
        var bytecode = CreateDxbcBytecode(
            ("ISGN", CreateSignatureChunk(
                ("POSITION", 0u, 0u, 3u, 0u, 0x7u, 0x7u),
                ("COLOR", 0u, 0u, 3u, 1u, 0xFu, 0xFu))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });
        var explicitInputLayout = device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    InputSlot = 2,
                    ShaderLocation = 5
                }
            ]
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = shader,
            InputLayout = explicitInputLayout
        });

        Assert.False(pipeline.UsesReflectedInputLayout);
        Assert.True(pipeline.ReflectedInputLayoutSupported);
        Assert.Null(pipeline.ReflectedInputLayoutFailureReason);
        Assert.Same(explicitInputLayout, pipeline.EffectiveInputLayout);
        Assert.Contains("TEXCOORD0", pipeline.PipelineKey, StringComparison.Ordinal);
        Assert.DoesNotContain("POSITION0", pipeline.PipelineKey, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslBytecodeGraphicsPipelineReportsUnsupportedReflectedInputLayout()
    {
        var bytecode = CreateDxbcBytecode(
            ("ISGN", CreateSignatureChunk(("TEXCOORD", 0u, 0u, 3u, 0u, 0x5u, 0x5u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = bytecode
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = shader
        });

        Assert.False(pipeline.UsesReflectedInputLayout);
        Assert.False(pipeline.ReflectedInputLayoutSupported);
        Assert.Contains("unsupported", pipeline.ReflectedInputLayoutFailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(pipeline.EffectiveInputLayout);
        Assert.Contains("no-layout", pipeline.PipelineKey, StringComparison.Ordinal);
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
TextureCube SourceTexture : register(t0);

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
    public void HlslTextShaderTranslatesTexture3DSampleAndLoadCalls()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = Texture3DSamplePixelHlsl,
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(576) var SourceTextureVolume: texture_3d<f32>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(768) var SourceSampler: sampler;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("textureSample(SourceTextureVolume, SourceSampler, vec3<f32>(input.uv, 0.75))", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("textureLoad(SourceTextureVolume", shader.BackendSource, StringComparison.Ordinal);
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
    public void HlslTextShaderTranslatesTextureComparisonSampleCalls()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
Texture2D ShadowMap : register(t2);
SamplerComparisonState ShadowSampler : register(s3);

float4 PSMain(float3 shadow : TEXCOORD0) : SV_Target
{
    float visibility = ShadowMap.SampleCmp(ShadowSampler, shadow.xy, shadow.z);
    float visibilityLevelZero = ShadowMap.SampleCmpLevelZero(ShadowSampler, shadow.xy, shadow.z);
    return float4(visibility, visibilityLevelZero, 0.0, 1.0);
}
""",
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(578) var ShadowMap: texture_depth_2d;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(771) var ShadowSampler: sampler_comparison;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var visibility: f32 = textureSampleCompare(ShadowMap, ShadowSampler, shadow.xy, shadow.z);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var visibilityLevelZero: f32 = textureSampleCompare(ShadowMap, ShadowSampler, shadow.xy, shadow.z);", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderAssignsRegistersToUnannotatedConstantBuffers()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
cbuffer Transform
{
    float4 Offset;
};

cbuffer Lighting : register(b3)
{
    float4 Tint;
};

cbuffer Material
{
    float4 Color;
};

float4 VSMain(float3 position : POSITION) : SV_Position
{
    return float4(position, 1.0) + Offset + Tint + Color;
}
""",
            EntryPoint = "VSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(0) var<uniform> transform: Transform;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(1) var<uniform> material: Material;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(3) var<uniform> lighting: Lighting;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("transform.Offset", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("material.Color", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("lighting.Tint", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderPreservesPackedConstantBufferFieldOffsets()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
cbuffer Settings : register(b0)
{
    float red;
    float2 greenBlue;
    float alpha;
};

float4 PSMain() : SV_Target
{
    return float4(red, greenBlue.x, greenBlue.y, alpha);
}
""",
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("_r0: vec4<u32>", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("bitcast<f32>(settings._r0.x)", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("vec2<f32>(bitcast<f32>(settings._r0.y), bitcast<f32>(settings._r0.z))", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("bitcast<f32>(settings._r0.w)", shader.BackendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.greenBlue", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderAssignsRegistersToUnannotatedResourcesInSourceOrder()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
Texture2D BaseTexture;
StructuredBuffer<float4> Points;
Texture2D ExplicitTexture : register(t5);
Buffer<float4> MorePoints;
SamplerState BaseSampler;
SamplerState ExplicitSampler : register(s3);

float4 PSMain() : SV_Target
{
    float2 uv = float2(0.5, 0.5);
    return BaseTexture.Sample(BaseSampler, uv) + ExplicitTexture.Sample(ExplicitSampler, uv) + Points[0] + MorePoints[0];
}
""",
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(576) var BaseTexture: texture_2d<f32>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(577) var<storage, read> Points: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(578) var<storage, read> MorePoints: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(581) var ExplicitTexture: texture_2d<f32>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(768) var BaseSampler: sampler;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(771) var ExplicitSampler: sampler;", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderAssignsRegistersToUnannotatedUnorderedAccessResources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
RWStructuredBuffer<float4> Output;
RWBuffer<float4> ExplicitOutput : register(u4);
RWByteAddressBuffer RawOutput;

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output[id.x] = float4(1.0, 0.0, 0.0, 1.0);
    RawOutput.Store(0, 1);
}
""",
            EntryPoint = "CSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(1856) var<storage, read_write> Output: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(1857) var<storage, read_write> RawOutput: array<atomic<u32>>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(1860) var<storage, read_write> ExplicitOutput: array<vec4<f32>>;", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderEmitsVectorAwareSaturateForLocalVectors()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
float4 PSMain(float4 color : COLOR0) : SV_Target
{
    float4 tone = color;
    return saturate(tone);
}
""",
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("return clamp(tone, vec4<f32>(0.0), vec4<f32>(1.0));", shader.BackendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("clamp(tone, 0.0, 1.0)", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderEmitsVectorAwareClipForParameters()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
float4 PSMain(float4 color : COLOR0) : SV_Target
{
    clip(color);
    return color;
}
""",
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("if (any((color) < vec4<f32>(0.0)))", shader.BackendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("if ((color) < 0.0)", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderPreservesLocalAndParameterShadowsOverConstantBufferFields()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
cbuffer Settings : register(b0)
{
    float scale;
};

float4 PSMain(float scale : TEXCOORD0) : SV_Target
{
    float local = scale;
    return float4(local, scale, 0.0, 1.0);
}
""",
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("var local: f32 = scale;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("return vec4<f32>(local, scale, 0.0, 1.0);", shader.BackendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.scale", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderUsesSemanticIndicesForStructLocations()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
struct VertexOut
{
    float4 position : SV_Position;
    float2 uv1 : TEXCOORD1;
    float2 uv0 : TEXCOORD0;
};

VertexOut VSMain(float3 position : POSITION)
{
    VertexOut output;
    output.position = float4(position, 1.0);
    output.uv1 = float2(0.0, 1.0);
    output.uv0 = float2(1.0, 0.0);
    return output;
}
""",
            EntryPoint = "VSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@location(1) uv1: vec2<f32>", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@location(0) uv0: vec2<f32>", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderAssignsUniqueLocationsForParameterSemantics()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
struct VertexOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VertexOut VSMain(float3 pos : POSITION0, float2 uv : TEXCOORD0)
{
    VertexOut output;
    output.position = float4(pos, 1.0);
    output.uv = uv;
    return output;
}
""",
            EntryPoint = "VSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("fn VSMain(@location(0) pos: vec3<f32>, @location(1) uv: vec2<f32>)", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesTexture2DArraySampleCallsInsideExpressions()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
Texture2DArray SourceTextureArray : register(t2);
SamplerState SourceSampler : register(s1);

float4 PSMain(float3 uvw : TEXCOORD0) : SV_Target
{
    float4 sampled = SourceTextureArray.Sample(SourceSampler, uvw);
    float4 leveled = SourceTextureArray.SampleLevel(SourceSampler, uvw, 0.0);
    float4 biased = SourceTextureArray.SampleBias(SourceSampler, uvw, 0.0);
    float4 grad = SourceTextureArray.SampleGrad(SourceSampler, uvw, float2(0.0, 0.0), float2(0.0, 0.0));
    float4 loaded = SourceTextureArray.Load(int4(0, 0, 1, 0), int2(0, 0));
    return sampled + leveled + biased + grad + loaded;
}
""",
            EntryPoint = "PSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(578) var SourceTextureArray: texture_2d_array<f32>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@binding(769) var SourceSampler: sampler;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var sampled: vec4<f32> = textureSample(SourceTextureArray, SourceSampler, uvw.xy, i32(uvw.z));", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var leveled: vec4<f32> = textureSampleLevel(SourceTextureArray, SourceSampler, uvw.xy, i32(uvw.z), 0.0);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var biased: vec4<f32> = textureSampleBias(SourceTextureArray, SourceSampler, uvw.xy, i32(uvw.z), 0.0);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var grad: vec4<f32> = textureSampleGrad(SourceTextureArray, SourceSampler, uvw.xy, i32(uvw.z), (vec2<f32>(0.0, 0.0)).xy, (vec2<f32>(0.0, 0.0)).xy);", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var loaded: vec4<f32> = textureLoad(SourceTextureArray, ((vec4<i32>(0, 0, 1, 0)).xy + vec2<i32>(0, 0)), i32((vec4<i32>(0, 0, 1, 0)).z), (vec4<i32>(0, 0, 1, 0)).w);", shader.BackendSource, StringComparison.Ordinal);
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
    public void HlslTextShaderTranslatesRwTexture2DResources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = RwTexture2DComputeHlsl,
            EntryPoint = "CSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(1856) var Output: texture_storage_2d<rgba8unorm, write>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("@compute @workgroup_size(1, 1, 1)\nfn CSMain(@builtin(global_invocation_id) id: vec3<u32>)", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("textureStore(Output, vec2<i32>(id.xy), vec4<f32>(0.0, 1.0, 0.0, 1.0));", shader.BackendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HlslTextShaderTranslatesRwTexture2DReadIndexers()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var shader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = RwTexture2DReadWriteComputeHlsl,
            EntryPoint = "CSMain"
        });

        Assert.NotNull(shader.BackendSource);
        Assert.Contains("@binding(1856) var Output: texture_storage_2d<rgba8unorm, read_write>;", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("var previous: vec4<f32> = textureLoad(Output, vec2<i32>(id.xy));", shader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("textureStore(Output, vec2<i32>(id.xy), vec4<f32>(previous.r, 1.0, previous.b, previous.a));", shader.BackendSource, StringComparison.Ordinal);
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
        context.SetVertexBuffer(1, vertexBuffer1, strideInBytes: 20, offsetBytes: 32);
        context.SetIndexBuffer(indexBuffer, DxIndexFormat.UInt16);
        context.DrawIndexed(6);

        var command = context.Commands[^1];

        Assert.Equal(ProGpuDirectXCommandKind.DrawIndexed, command.Kind);
        Assert.NotNull(command.VertexBuffers);
        Assert.Same(vertexBuffer0, command.VertexBuffers[0]);
        Assert.Same(vertexBuffer1, command.VertexBuffers[1]);
        Assert.NotNull(command.VertexBufferBindings);
        Assert.Same(vertexBuffer0, command.VertexBufferBindings[0].Buffer);
        Assert.Equal(16u, command.VertexBufferBindings[0].StrideInBytes);
        Assert.Equal(0ul, command.VertexBufferBindings[0].OffsetBytes);
        Assert.Same(vertexBuffer1, command.VertexBufferBindings[1].Buffer);
        Assert.Equal(20u, command.VertexBufferBindings[1].StrideInBytes);
        Assert.Equal(32ul, command.VertexBufferBindings[1].OffsetBytes);
        Assert.Equal(20u, context.VertexBufferBindings[1].StrideInBytes);
        Assert.Equal(32ul, context.VertexBufferBindings[1].OffsetBytes);
        Assert.Same(indexBuffer, command.IndexBuffer);
        Assert.Equal(DxIndexFormat.UInt16, command.DrawIndexed!.IndexFormat);
        Assert.Equal(DxIndexFormat.UInt16, command.IndexFormat);
    }

    [Fact]
    public void SetVertexBufferValidatesUsageStrideAndOffset()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var vertexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 128,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination
        });
        using var indexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.Index | DxBufferUsage.CopyDestination
        });
        using var context = device.CreateImmediateContext();

        Assert.Throws<ArgumentException>(() => context.SetVertexBuffer(0, indexBuffer, 16));
        var offsetException = Assert.Throws<ArgumentOutOfRangeException>(() => context.SetVertexBuffer(0, vertexBuffer, 16, 128));
        Assert.Equal("offsetBytes", offsetException.ParamName);
        var strideException = Assert.Throws<ArgumentOutOfRangeException>(() => context.SetVertexBuffer(0, vertexBuffer, 0));
        Assert.Equal("strideInBytes", strideException.ParamName);
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
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.None,
                FrontFace = DxFrontFace.CounterClockwise
            }
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
    public void GpuBackedDrawUsesVertexBufferBindingOffset()
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
            SizeInBytes = 168,
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 28
        });
        vertexBuffer.Write<float>(
        [
            -0.8f, -0.8f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f,
             0.8f, -0.8f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f,
             0.0f,  0.8f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f,
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
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.None,
                FrontFace = DxFrontFace.CounterClockwise
            }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.SetVertexBuffer(0, vertexBuffer, strideInBytes: 28, offsetBytes: 84);
        context.Draw(3);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel from offset vertex binding, actual: {center}");
        Assert.True(center.G < 50, $"Expected skipped green vertices from offset vertex binding, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel from offset vertex binding, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel from offset vertex binding, actual: {center}");
    }

    [Fact]
    public void GpuBackedDrawPreservesNonContiguousInputSlotNumbers()
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
                    InputSlot = 1,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    InputSlot = 1,
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
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.None,
                FrontFace = DxFrontFace.CounterClockwise
            }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.SetVertexBuffer(1, vertexBuffer);
        context.Draw(3);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel from slot 1 vertex binding, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel from slot 1 vertex binding, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel from slot 1 vertex binding, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel from slot 1 vertex binding, actual: {center}");
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
    public void FlushSubmitsGpuBackedSciChart3DDepthTestedMeshCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext3D(
            device,
            32,
            32,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartVertex3D[] nearRedTriangle =
        [
            new(-0.8f, -0.8f, 0.25f, 0f, 0f, 1f, 0xFFFF0000),
            new( 0.8f, -0.8f, 0.25f, 0f, 0f, 1f, 0xFFFF0000),
            new( 0.0f,  0.8f, 0.25f, 0f, 0f, 1f, 0xFFFF0000)
        ];
        ProGpuDirectXSciChartVertex3D[] farGreenTriangle =
        [
            new(-0.8f, -0.8f, 0.75f, 0f, 0f, 1f, 0xFF00FF00),
            new( 0.8f, -0.8f, 0.75f, 0f, 0f, 1f, 0xFF00FF00),
            new( 0.0f,  0.8f, 0.75f, 0f, 0f, 1f, 0xFF00FF00)
        ];
        uint[] indices = [0, 1, 2];

        renderContext.Clear(DxColor.Black);
        renderContext.DrawTriangleMesh(
            nearRedTriangle,
            indices,
            Matrix4x4.Identity,
            new Vector3(0f, 0f, 1f),
            DxCullMode.None);
        renderContext.DrawTriangleMesh(
            farGreenTriangle,
            indices,
            Matrix4x4.Identity,
            new Vector3(0f, 0f, 1f),
            DxCullMode.None);
        renderContext.Flush();

        Assert.Equal(2ul, renderContext.ImmediateContext.SubmittedDrawCount);
        Assert.Equal(2, renderContext.MeshDraws.Count);

        var targetPixels = renderContext.ReadTargetPixels();
        var center = ReadRgbaPixel(targetPixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel from nearer SciChart 3D triangle, actual: {center}");
        Assert.True(center.G < 50, $"Expected far green triangle to fail depth at center, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after SciChart 3D draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after SciChart 3D draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChart3DSurfaceMeshCommandsWithClip()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext3D(
            device,
            32,
            32,
            DxResourceFormat.R8G8B8A8Unorm);
        float[] heights = [-0.8f, -0.8f, 0.8f, 0.8f];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 16, 32));
        renderContext.DrawSurfaceMesh(
            heights,
            columns: 2,
            rows: 2,
            worldViewProjection: Matrix4x4.Identity,
            xRange: new Vector2(-0.8f, 0.8f),
            zRange: new Vector2(0.25f, 0.25f),
            lowColorArgb: 0xFF0000FF,
            highColorArgb: 0xFFFF0000,
            lightDirection: new Vector3(0f, 0f, -1f),
            cullMode: DxCullMode.None);
        renderContext.Flush();

        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);
        Assert.Single(renderContext.SurfaceMeshDraws);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 32, 8, 16);
        Assert.True(clippedIn.R + clippedIn.G + clippedIn.B > 20, $"Expected visible SciChart 3D surface mesh pixel inside clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected opaque SciChart 3D surface mesh pixel inside clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 32, 24, 16);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart 3D surface mesh clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart 3D surface mesh clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart 3D surface mesh clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart 3D surface mesh clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChart3DWaterfallCommandsWithClip()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext3D(
            device,
            32,
            32,
            DxResourceFormat.R8G8B8A8Unorm);

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 16, 32));
        renderContext.DrawWaterfallDataSeries(
            [0.8f, 0.8f],
            columns: 2,
            rows: 1,
            worldViewProjection: Matrix4x4.Identity,
            new ProGpuDirectXSciChartWaterfall3DOptions
            {
                YRange = new ProGpuDirectXSciChartDoubleRange(-0.8d, 0.8d),
                BaseY = -1f,
                LowColorArgb = 0xFF00FF00,
                HighColorArgb = 0xFF00FF00,
                Normal = new Vector3(0f, 0f, 1f)
            },
            new Vector3(0f, 0f, 1f),
            DxCullMode.None);
        renderContext.Flush();

        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);
        Assert.Single(renderContext.WaterfallDraws);
        Assert.Equal(DxPrimitiveTopology.TriangleList, renderContext.ImmediateContext.GraphicsPipeline?.Descriptor.Topology);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 32, 8, 16);
        Assert.True(clippedIn.R < 50, $"Expected waterfall low red pixel inside clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 120, $"Expected visible waterfall green pixel inside clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected waterfall low blue pixel inside clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected opaque waterfall pixel inside clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 32, 24, 16);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart 3D waterfall clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart 3D waterfall clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart 3D waterfall clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart 3D waterfall clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChart3DTriangleStripCommandsWithClip()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext3D(
            device,
            32,
            32,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartVertex3D[] vertices =
        [
            new(-0.8f, -0.8f, 0.25f, 0f, 0f, 1f, 0xFF00FF00),
            new( 0.8f, -0.8f, 0.25f, 0f, 0f, 1f, 0xFF00FF00),
            new(-0.8f,  0.8f, 0.25f, 0f, 0f, 1f, 0xFF00FF00),
            new( 0.8f,  0.8f, 0.25f, 0f, 0f, 1f, 0xFF00FF00)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 16, 32));
        renderContext.DrawTriangleStrip(
            vertices,
            Matrix4x4.Identity,
            new Vector3(0f, 0f, 1f),
            DxCullMode.None);
        renderContext.Flush();

        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);
        Assert.Single(renderContext.TriangleStripDraws);
        Assert.Equal(DxPrimitiveTopology.TriangleStrip, renderContext.ImmediateContext.GraphicsPipeline?.Descriptor.Topology);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 32, 8, 16);
        Assert.True(clippedIn.R < 50, $"Expected triangle strip low red pixel inside clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected triangle strip green pixel inside clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected triangle strip low blue pixel inside clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected opaque triangle strip pixel inside clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 32, 24, 16);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart 3D triangle strip clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart 3D triangle strip clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart 3D triangle strip clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart 3D triangle strip clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChart3DLineCommandsWithClip()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext3D(
            device,
            32,
            32,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartVertex3D[] vertices =
        [
            new(-0.8f, 0.0f, 0.25f, 0f, 0f, 1f, 0xFF00FF00),
            new( 0.8f, 0.0f, 0.25f, 0f, 0f, 1f, 0xFF00FF00)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 16, 32));
        renderContext.DrawLineStrip(
            vertices,
            Matrix4x4.Identity,
            new Vector3(0f, 0f, 1f));
        renderContext.Flush();

        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);
        Assert.Single(renderContext.LineDraws);
        Assert.True(renderContext.LineDraws[0].IsStrip);

        var targetPixels = renderContext.ReadTargetPixels();
        var visibleInsideClip = false;
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var pixel = ReadRgbaPixel(targetPixels, 32, x, y);
                visibleInsideClip |= pixel.R < 50 && pixel.G > 150 && pixel.B < 50 && pixel.A > 200;
            }
        }

        Assert.True(visibleInsideClip, "Expected at least one green SciChart 3D line pixel inside the scissor clip.");

        for (var y = 0; y < 32; y++)
        {
            for (var x = 16; x < 32; x++)
            {
                var pixel = ReadRgbaPixel(targetPixels, 32, x, y);
                Assert.True(pixel.R < 50, $"Expected low red pixel outside SciChart 3D line clip, actual: {pixel}");
                Assert.True(pixel.G < 50, $"Expected black pixel outside SciChart 3D line clip, actual: {pixel}");
                Assert.True(pixel.B < 50, $"Expected low blue pixel outside SciChart 3D line clip, actual: {pixel}");
                Assert.True(pixel.A > 200, $"Expected opaque clear alpha outside SciChart 3D line clip, actual: {pixel}");
            }
        }
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartTextDrawCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            96,
            48,
            DxResourceFormat.R8G8B8A8Unorm);
        var font = TryCreateSciChartFont(renderContext);
        if (font is null)
        {
            return;
        }

        renderContext.Clear(DxColor.Black);
        renderContext.DrawText(
            "WPF",
            font,
            28f,
            0xFF00FF00,
            new ProGpuDirectXSciChartPoint(4, 4));
        renderContext.Flush();

        Assert.Single(renderContext.TextDraws);
        var targetPixels = renderContext.ReadTargetPixels();
        AssertContainsGreenPixel(targetPixels, 96, "SciChart text draw");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartTextDrawCommandsWithClip()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            96,
            48,
            DxResourceFormat.R8G8B8A8Unorm);
        var font = TryCreateSciChartFont(renderContext);
        if (font is null)
        {
            return;
        }

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 32, 48));
        renderContext.DrawText(
            "WPF",
            font,
            28f,
            0xFF00FF00,
            new ProGpuDirectXSciChartPoint(4, 4));
        renderContext.Flush();

        Assert.Single(renderContext.TextDraws);
        var targetPixels = renderContext.ReadTargetPixels();
        AssertContainsGreenPixelInRegion(targetPixels, 96, 0, 0, 32, 48, "SciChart clipped text draw");
        AssertNoGreenPixelInRegion(targetPixels, 96, 40, 0, 56, 48, "outside SciChart clipped text draw");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartMountainBatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartBandVertex[] vertices =
        [
            new(2, 4, 12),
            new(14, 4, 12)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 16, 16));
        renderContext.DrawMountainBatch(
            vertices,
            count: vertices.Length,
            renderContext.CreatePen(0x00000000),
            renderContext.CreateBrush(0xFF00FF00),
            isDigital: false,
            transform: new ProGpuDirectXSciChartVertexTransform());
        renderContext.Flush();

        Assert.Single(renderContext.MountainBatchDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var filled = ReadRgbaPixel(targetPixels, 16, 8, 8);
        Assert.True(filled.R < 50, $"Expected mountain batch low red fill pixel, actual: {filled}");
        Assert.True(filled.G > 200, $"Expected mountain batch green fill pixel, actual: {filled}");
        Assert.True(filled.B < 50, $"Expected mountain batch low blue fill pixel, actual: {filled}");
        Assert.True(filled.A > 200, $"Expected mountain batch opaque fill pixel, actual: {filled}");

        var outside = ReadRgbaPixel(targetPixels, 16, 8, 14);
        Assert.True(outside.R < 50, $"Expected black pixel outside SciChart mountain fill, actual: {outside}");
        Assert.True(outside.G < 50, $"Expected black pixel outside SciChart mountain fill, actual: {outside}");
        Assert.True(outside.B < 50, $"Expected black pixel outside SciChart mountain fill, actual: {outside}");
        Assert.True(outside.A > 200, $"Expected opaque clear alpha outside SciChart mountain fill, actual: {outside}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartCandleBatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartOhlcCandleVertex[] vertices =
        [
            new(4, 2, 2, 14, 14, 0xFF00FF00, 0x00000000),
            new(12, 2, 2, 14, 14, 0xFFFF0000, 0x00000000)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawCandlesBatch(
            vertices,
            count: vertices.Length,
            width: 8,
            transform: new ProGpuDirectXSciChartVertexTransform());
        renderContext.Flush();

        Assert.Single(renderContext.FinancialBatchDraws);
        Assert.Equal(ProGpuDirectXSciChartFinancialBatchKind.Candles, renderContext.FinancialBatchDraws[0].Kind);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected candle batch low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected candle batch green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected candle batch low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected candle batch opaque pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart candle clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart candle clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart candle clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart candle clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartOhlcBatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartOhlcCandleVertex[] vertices =
        [
            new(4, 4, 0, 15, 12, 0x00000000, 0xFF00FF00),
            new(12, 4, 0, 15, 12, 0x00000000, 0xFFFF0000)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawOhlcBatch(
            vertices,
            count: vertices.Length,
            width: 8,
            transform: new ProGpuDirectXSciChartVertexTransform());
        renderContext.Flush();

        Assert.Single(renderContext.FinancialBatchDraws);
        Assert.Equal(ProGpuDirectXSciChartFinancialBatchKind.Ohlc, renderContext.FinancialBatchDraws[0].Kind);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        Assert.Contains(
            Enumerable.Range(0, 8 * 16),
            index =>
            {
                var pixel = ReadRgbaPixel(targetPixels, 16, index % 8, index / 8);
                return pixel.R < 50 && pixel.G > 150 && pixel.B < 50 && pixel.A > 200;
            });
        Assert.DoesNotContain(
            Enumerable.Range(0, 8 * 16),
            index =>
            {
                var pixel = ReadRgbaPixel(targetPixels, 16, 8 + (index % 8), index / 8);
                return pixel.R > 150 || pixel.G > 150 || pixel.B > 150;
            });
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartSpriteBatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        using var sprite = renderContext.CreateSprite(8, 16);
        sprite.SetData(Enumerable.Repeat(unchecked((int)0xFFFFFFFF), 128).ToArray());
        ProGpuDirectXSciChartSpriteVertex[] vertices =
        [
            new(0, 0, 0xFF00FF00, 0x00000000),
            new(8, 0, 0xFFFF0000, 0x00000000)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawSpritesBatch(
            vertices,
            count: vertices.Length,
            sprite,
            strokeSprite: null,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            centeredAmount: 0f,
            ProGpuDirectXSciChartTextureFiltering.Point);
        renderContext.Flush();

        Assert.Single(renderContext.SpriteBatchDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected sprite batch low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected sprite batch green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected sprite batch low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected sprite batch opaque pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart sprite clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart sprite clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart sprite clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart sprite clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartColoredSpriteCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        using var sprite = renderContext.CreateSprite(8, 16);
        sprite.SetData(Enumerable.Repeat(unchecked((int)0xFFFFFFFF), 128).ToArray());
        ProGpuDirectXSciChartColoredSpriteVertex[] vertices =
        [
            new(0, 0, 0xFF00FF00),
            new(8, 0, 0xFFFF0000)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawColoredSprites(
            sprite,
            vertices,
            startIndex: 0,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform(),
            centeredAmount: 0f,
            filtering: ProGpuDirectXSciChartTextureFiltering.Point);
        var drawCommand = renderContext.ImmediateContext.Commands[^1];
        var draw = drawCommand.Draw ?? throw new InvalidOperationException("Expected SciChart colored sprite draw command payload.");
        Assert.Equal(6u, draw.VertexCount);
        Assert.Equal(2u, draw.InstanceCount);
        renderContext.Flush();

        Assert.Single(renderContext.ColoredSpriteDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected colored sprite low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected colored sprite green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected colored sprite low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected colored sprite opaque pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart colored sprite clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart colored sprite clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart colored sprite clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart colored sprite clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartRectBatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartRectVertex[] vertices =
        [
            new(0, 0, 8, 16, 0xFF00FF00),
            new(8, 0, 8, 16, 0xFFFF0000)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawRectsBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform());
        renderContext.Flush();

        Assert.Single(renderContext.RectBatchDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected rect batch low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected rect batch green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected rect batch low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected rect batch opaque pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart rect clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart rect clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart rect clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart rect clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartVerticalPixelCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        int[] green = [unchecked((int)0xFF00FF00)];
        int[] yCoordinates = [8, 16];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawPixelsVertically(
            xLeft: 0,
            xRight: 16,
            yStartBottom: 0,
            yEndTop: 8,
            green,
            opacity: 1d,
            yAxisIsFlipped: false);
        renderContext.DrawPixelsVertically(
            xLeft: 0,
            xRight: 16,
            yCoordinates,
            green,
            opacity: 1d,
            isUniform: false,
            yAxisIsFlipped: false);
        renderContext.Flush();

        Assert.Equal(2, renderContext.VerticalPixelsDraws.Count);
        Assert.Equal(2ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var uniformIn = ReadRgbaPixel(targetPixels, 16, 4, 4);
        Assert.True(uniformIn.R < 50, $"Expected vertical pixel texture low red pixel inside SciChart clip, actual: {uniformIn}");
        Assert.True(uniformIn.G > 200, $"Expected vertical pixel texture green pixel inside SciChart clip, actual: {uniformIn}");
        Assert.True(uniformIn.B < 50, $"Expected vertical pixel texture low blue pixel inside SciChart clip, actual: {uniformIn}");
        Assert.True(uniformIn.A > 200, $"Expected vertical pixel texture opaque pixel inside SciChart clip, actual: {uniformIn}");

        var coordinateIn = ReadRgbaPixel(targetPixels, 16, 4, 12);
        Assert.True(coordinateIn.R < 50, $"Expected vertical pixel coordinate low red pixel inside SciChart clip, actual: {coordinateIn}");
        Assert.True(coordinateIn.G > 200, $"Expected vertical pixel coordinate green pixel inside SciChart clip, actual: {coordinateIn}");
        Assert.True(coordinateIn.B < 50, $"Expected vertical pixel coordinate low blue pixel inside SciChart clip, actual: {coordinateIn}");
        Assert.True(coordinateIn.A > 200, $"Expected vertical pixel coordinate opaque pixel inside SciChart clip, actual: {coordinateIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart vertical pixel clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart vertical pixel clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart vertical pixel clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart vertical pixel clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartColumnBatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartColumnVertex[] vertices =
        [
            new(0, 0, 8, 16, 0xFF00FF00, 0x00000000),
            new(8, 0, 8, 16, 0xFFFF0000, 0x00000000)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawColumnsBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform());
        renderContext.Flush();

        Assert.Single(renderContext.ColumnBatchDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected column batch low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected column batch green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected column batch low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected column batch opaque pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart column clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart column clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart column clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart column clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartLineBatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartColorVertex[] vertices =
        [
            new(0, 8, 0, 0xFF00FF00),
            new(15, 8, 0, 0xFF00FF00)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            transform: new ProGpuDirectXSciChartVertexTransform());
        renderContext.Flush();

        Assert.Single(renderContext.LineBatchDraws);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        Assert.Contains(
            Enumerable.Range(0, 8 * 16),
            index =>
            {
                var pixel = ReadRgbaPixel(targetPixels, 16, index % 8, index / 8);
                return pixel.R < 50 && pixel.G > 150 && pixel.B < 50 && pixel.A > 200;
            });
        Assert.DoesNotContain(
            Enumerable.Range(0, 8 * 16),
            index =>
            {
                var pixel = ReadRgbaPixel(targetPixels, 16, 8 + (index % 8), index / 8);
                return pixel.R < 50 && pixel.G > 150 && pixel.B < 50 && pixel.A > 200;
            });
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartThickLineBatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        var pen = renderContext.CreatePen(0xFF00FF00, strokeThickness: 5f);
        ProGpuDirectXSciChartColorVertex[] vertices =
        [
            new(8, 0, 0, 0),
            new(8, 15, 0, 0)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            pen,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            transform: new ProGpuDirectXSciChartVertexTransform());
        renderContext.Flush();

        Assert.Single(renderContext.LineBatchDraws);
        Assert.Equal(pen, renderContext.LineBatchDraws[0].Pen);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var center = ReadRgbaPixel(targetPixels, 16, 8, 8);
        Assert.True(center.R < 50, $"Expected thick line low red center pixel, actual: {center}");
        Assert.True(center.G > 150, $"Expected thick line green center pixel, actual: {center}");
        Assert.True(center.B < 50, $"Expected thick line low blue center pixel, actual: {center}");
        Assert.True(center.A > 200, $"Expected thick line opaque center pixel, actual: {center}");

        var edge = ReadRgbaPixel(targetPixels, 16, 9, 8);
        Assert.True(edge.R < 50, $"Expected thick line low red edge pixel, actual: {edge}");
        Assert.True(edge.G > 150, $"Expected thick line green edge pixel, actual: {edge}");
        Assert.True(edge.B < 50, $"Expected thick line low blue edge pixel, actual: {edge}");
        Assert.True(edge.A > 200, $"Expected thick line opaque edge pixel, actual: {edge}");

        var outside = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(outside.R < 50, $"Expected black outside thick line, actual: {outside}");
        Assert.True(outside.G < 50, $"Expected black outside thick line, actual: {outside}");
        Assert.True(outside.B < 50, $"Expected black outside thick line, actual: {outside}");
        Assert.True(outside.A > 200, $"Expected opaque clear alpha outside thick line, actual: {outside}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartLineBatchAppliesVertexOffset()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);
        var pen = renderContext.CreatePen(0xFF00FF00, strokeThickness: 3f, isAntiAliased: false);
        ProGpuDirectXSciChartColorVertex[] vertices =
        [
            new(2, 4, 6, 0),
            new(13, 4, 6, 0)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.DrawLinesBatch(
            vertices,
            count: vertices.Length,
            pen,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            transform: new ProGpuDirectXSciChartVertexTransform());
        renderContext.Flush();

        Assert.Single(renderContext.LineBatchDraws);
        Assert.Equal(pen, renderContext.LineBatchDraws[0].Pen);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var shifted = ReadRgbaPixel(targetPixels, 16, 8, 10);
        Assert.True(shifted.R < 50, $"Expected low red shifted line pixel, actual: {shifted}");
        Assert.True(shifted.G > 150, $"Expected green shifted line pixel, actual: {shifted}");
        Assert.True(shifted.B < 50, $"Expected low blue shifted line pixel, actual: {shifted}");
        Assert.True(shifted.A > 200, $"Expected opaque shifted line pixel, actual: {shifted}");

        var unshifted = ReadRgbaPixel(targetPixels, 16, 8, 4);
        Assert.True(unshifted.R < 50, $"Expected black unshifted line pixel, actual: {unshifted}");
        Assert.True(unshifted.G < 50, $"Expected black unshifted line pixel, actual: {unshifted}");
        Assert.True(unshifted.B < 50, $"Expected black unshifted line pixel, actual: {unshifted}");
        Assert.True(unshifted.A > 200, $"Expected opaque clear alpha at unshifted line pixel, actual: {unshifted}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartPrimitiveFillCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 8, 16));
        renderContext.FillRectangle(
            renderContext.CreateBrush(0xFF00FF00),
            new ProGpuDirectXSciChartPoint(0, 0),
            new ProGpuDirectXSciChartPoint(16, 16));
        renderContext.Flush();

        Assert.Single(renderContext.PrimitiveDraws);
        Assert.Equal(ProGpuDirectXSciChartPrimitiveKind.RectangleFill, renderContext.PrimitiveDraws[0].Kind);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var clippedIn = ReadRgbaPixel(targetPixels, 16, 4, 8);
        Assert.True(clippedIn.R < 50, $"Expected primitive fill low red pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.G > 200, $"Expected primitive fill green pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.B < 50, $"Expected primitive fill low blue pixel inside SciChart clip, actual: {clippedIn}");
        Assert.True(clippedIn.A > 200, $"Expected primitive fill opaque pixel inside SciChart clip, actual: {clippedIn}");

        var clippedOut = ReadRgbaPixel(targetPixels, 16, 12, 8);
        Assert.True(clippedOut.R < 50, $"Expected black pixel outside SciChart primitive clip, actual: {clippedOut}");
        Assert.True(clippedOut.G < 50, $"Expected black pixel outside SciChart primitive clip, actual: {clippedOut}");
        Assert.True(clippedOut.B < 50, $"Expected black pixel outside SciChart primitive clip, actual: {clippedOut}");
        Assert.True(clippedOut.A > 200, $"Expected opaque clear alpha outside SciChart primitive clip, actual: {clippedOut}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartGradientPrimitiveFillCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            16,
            16,
            DxResourceFormat.R8G8B8A8Unorm);

        renderContext.Clear(DxColor.Black);
        renderContext.FillRectangle(
            renderContext.CreateLinearGradientBrush(0xFFFF0000, 0xFF00FF00),
            new ProGpuDirectXSciChartPoint(0, 0),
            new ProGpuDirectXSciChartPoint(16, 16));
        renderContext.Flush();

        Assert.Single(renderContext.PrimitiveDraws);
        Assert.Equal(ProGpuDirectXSciChartPrimitiveKind.RectangleFill, renderContext.PrimitiveDraws[0].Kind);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        var left = ReadRgbaPixel(targetPixels, 16, 2, 8);
        Assert.True(left.R > 150, $"Expected gradient left red channel, actual: {left}");
        Assert.True(left.G < 100, $"Expected gradient left low green channel, actual: {left}");
        Assert.True(left.B < 50, $"Expected gradient left low blue channel, actual: {left}");
        Assert.True(left.A > 200, $"Expected gradient left opaque alpha, actual: {left}");

        var right = ReadRgbaPixel(targetPixels, 16, 13, 8);
        Assert.True(right.R < 100, $"Expected gradient right low red channel, actual: {right}");
        Assert.True(right.G > 150, $"Expected gradient right green channel, actual: {right}");
        Assert.True(right.B < 50, $"Expected gradient right low blue channel, actual: {right}");
        Assert.True(right.A > 200, $"Expected gradient right opaque alpha, actual: {right}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartEllipsePrimitiveCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            32,
            32,
            DxResourceFormat.R8G8B8A8Unorm);

        renderContext.Clear(DxColor.Black);
        renderContext.SetClipRect(new DxRect(0, 0, 16, 32));
        renderContext.DrawEllipse(
            strokePen: null,
            fillBrush: renderContext.CreateBrush(0xFF00FF00),
            center: new ProGpuDirectXSciChartPoint(16, 16),
            width: 20d,
            height: 12d);
        renderContext.Flush();

        Assert.Single(renderContext.PrimitiveDraws);
        Assert.Equal(ProGpuDirectXSciChartPrimitiveKind.Ellipse, renderContext.PrimitiveDraws[0].Kind);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        AssertGreenPixel(targetPixels, 32, 10, 16, "clipped ellipse fill");
        AssertBlackPixel(targetPixels, 32, 22, 16, "ellipse clipped side");
        AssertBlackPixel(targetPixels, 32, 10, 7, "outside ellipse vertical radius");
    }

    [Fact]
    public void FlushSubmitsGpuBackedSciChartConcavePolygonFillCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderContext = new ProGpuDirectXSciChartRenderContext2D(
            device,
            32,
            32,
            DxResourceFormat.R8G8B8A8Unorm);
        ProGpuDirectXSciChartPoint[] points =
        [
            new(0, 0),
            new(32, 0),
            new(32, 8),
            new(8, 8),
            new(8, 24),
            new(32, 24),
            new(32, 32),
            new(0, 32)
        ];

        renderContext.Clear(DxColor.Black);
        renderContext.FillPolygon(renderContext.CreateBrush(0xFF00FF00), points);
        renderContext.Flush();

        Assert.Single(renderContext.PrimitiveDraws);
        Assert.Equal(ProGpuDirectXSciChartPrimitiveKind.PolygonFill, renderContext.PrimitiveDraws[0].Kind);
        Assert.Equal(1ul, renderContext.ImmediateContext.SubmittedDrawCount);

        var targetPixels = renderContext.ReadTargetPixels();
        AssertGreenPixel(targetPixels, 32, 16, 4, "top C-shape bar");
        AssertGreenPixel(targetPixels, 32, 4, 16, "left C-shape bar");
        AssertGreenPixel(targetPixels, 32, 16, 28, "bottom C-shape bar");
        AssertBlackPixel(targetPixels, 32, 16, 16, "C-shape hollow");
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
    public void GpuBackedHlslFragmentFrontFacePipelineUsesNativeEmulationWhenBackendCapabilityIsMissing()
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
float4 PSMain(float4 color : COLOR0, bool isFrontFace : SV_IsFrontFace) : SV_Target
{
    return isFrontFace
        ? float4(1.0, 0.0, 0.0, 1.0)
        : float4(0.0, 0.0, 1.0, 1.0);
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
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.None,
                FrontFace = DxFrontFace.CounterClockwise
            }
        });
        using var context = device.CreateImmediateContext();

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@builtin(front_facing) isFrontFace: bool", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("vec4<bool>(isFrontFace, isFrontFace, isFrontFace, isFrontFace)", pixelShader.BackendSource, StringComparison.Ordinal);
        Assert.False(device.Capabilities.SupportsFragmentFrontFacingBuiltin);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.True(pipeline.UsesFragmentFrontFacingEmulation);

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
    public void GpuBackedHlslFragmentFrontFaceEmulationRendersBackFacingBranch()
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
float4 PSMain(float4 color : COLOR0, bool isFrontFace : SV_IsFrontFace) : SV_Target
{
    return isFrontFace
        ? float4(1.0, 0.0, 0.0, 1.0)
        : float4(0.0, 0.0, 1.0, 1.0);
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
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.None,
                FrontFace = DxFrontFace.Clockwise
            }
        });
        using var context = device.CreateImmediateContext();

        Assert.True(pipeline.HasBackendPipeline);
        Assert.True(pipeline.UsesFragmentFrontFacingEmulation);

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
        Assert.True(center.R < 50, $"Expected low red center pixel after back-facing emulation draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after back-facing emulation draw, actual: {center}");
        Assert.True(center.B > 200, $"Expected blue center pixel after back-facing emulation draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after back-facing emulation draw, actual: {center}");
    }

    [Fact]
    public void GpuBackedHlslFragmentFrontFaceEmulationSupportsIndexedDraws()
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
        using var indexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 6,
            Usage = DxBufferUsage.Index | DxBufferUsage.CopyDestination
        });
        indexBuffer.Write<ushort>([0, 1, 2]);
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
float4 PSMain(float4 color : COLOR0, bool isFrontFace : SV_IsFrontFace) : SV_Target
{
    return isFrontFace
        ? float4(1.0, 0.0, 0.0, 1.0)
        : float4(0.0, 0.0, 1.0, 1.0);
}
""",
            EntryPoint = "PSMain",
            Label = "HLSL FrontFace Indexed Pixel"
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
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.None,
                FrontFace = DxFrontFace.CounterClockwise
            }
        });
        using var context = device.CreateImmediateContext();

        Assert.True(pipeline.HasBackendPipeline);
        Assert.True(pipeline.UsesFragmentFrontFacingEmulation);

        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.SetVertexBuffer(vertexBuffer);
        context.SetIndexBuffer(indexBuffer, DxIndexFormat.UInt16);
        context.DrawIndexed(3, indexFormat: DxIndexFormat.UInt16);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after indexed front-facing emulation draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after indexed front-facing emulation draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after indexed front-facing emulation draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after indexed front-facing emulation draw, actual: {center}");
    }

    [Fact]
    public void GpuBackedHlslFragmentFrontFaceEmulationRejectsNoCullBlendedPipelines()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
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
float4 PSMain(float4 color : COLOR0, bool isFrontFace : SV_IsFrontFace) : SV_Target
{
    return isFrontFace
        ? float4(1.0, 0.0, 0.0, 0.5)
        : float4(0.0, 0.0, 1.0, 0.5);
}
""",
            EntryPoint = "PSMain",
            Label = "HLSL Blended FrontFace Pixel"
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
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@builtin(front_facing) isFrontFace: bool", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.False(pipeline.HasBackendPipeline);
        Assert.False(pipeline.UsesFragmentFrontFacingEmulation);
        Assert.Contains("Order-preserving", pipeline.FrontFacingEmulationFailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuBackedHlslFragmentFrontFaceEmulationAllowsCulledBlendedPipelines()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
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
float4 PSMain(float4 color : COLOR0, bool isFrontFace : SV_IsFrontFace) : SV_Target
{
    return isFrontFace
        ? float4(1.0, 0.0, 0.0, 0.5)
        : float4(0.0, 0.0, 1.0, 0.5);
}
""",
            EntryPoint = "PSMain",
            Label = "HLSL Culled Blended FrontFace Pixel"
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
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.Back }
        });

        Assert.True(pipeline.HasBackendPipeline);
        Assert.True(pipeline.UsesFragmentFrontFacingEmulation);
        Assert.Null(pipeline.FrontFacingEmulationFailureReason);
    }

    [Fact]
    public void GpuBackedHlslFragmentFrontFaceInputStructUsesNativeEmulation()
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
struct PixelInput
{
    float4 color : COLOR0;
    bool isFrontFace : SV_IsFrontFace;
};

float4 PSMain(PixelInput input) : SV_Target
{
    return input.isFrontFace
        ? input.color
        : float4(0.0, 0.0, 1.0, 1.0);
}
""",
            EntryPoint = "PSMain",
            Label = "HLSL FrontFace Struct Pixel"
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
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.None,
                FrontFace = DxFrontFace.CounterClockwise
            }
        });
        using var context = device.CreateImmediateContext();

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@builtin(front_facing) isFrontFace: bool", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.True(pipeline.UsesFragmentFrontFacingEmulation);

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
        Assert.True(center.R > 200, $"Expected red center pixel after input-struct front-facing emulation draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after input-struct front-facing emulation draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after input-struct front-facing emulation draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after input-struct front-facing emulation draw, actual: {center}");
    }

    [Fact]
    public void GpuBackedWgslFrontFacingOverrideVariantDraws()
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
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = """
@fragment
fn PSMain() -> @location(0) vec4<f32> {
    return select(vec4<f32>(0.0, 0.0, 1.0, 1.0), vec4<f32>(1.0, 0.0, 0.0, 1.0), vec4<bool>(true, true, true, true));
}
""",
            EntryPoint = "PSMain",
            Label = "WGSL FrontFace Override Pixel"
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

        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after WGSL override draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after WGSL override draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after WGSL override draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after WGSL override draw, actual: {center}");
    }

    [Fact]
    public void GpuBackedWgslFrontFacingOverrideVariantDrawsWithHlslVertexInput()
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
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = """
@fragment
fn PSMain(@location(0) color: vec4<f32>) -> @location(0) vec4<f32> {
    return select(vec4<f32>(0.0, 0.0, 1.0, 1.0), vec4<f32>(1.0, 0.0, 0.0, 1.0), vec4<bool>(true, true, true, true));
}
""",
            EntryPoint = "PSMain",
            Label = "WGSL FrontFace Override Pixel"
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

        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after WGSL override HLSL vertex draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after WGSL override HLSL vertex draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after WGSL override HLSL vertex draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after WGSL override HLSL vertex draw, actual: {center}");
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
    public void FlushSubmitsGpuBackedHlslVectorConditionalPixelDrawCommands()
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
            Source = """
float4 PSMain() : SV_Target
{
    return 1.0 > 0.5
        ? float4(1.0, 0.0, 0.0, 1.0)
        : float4(0.0, 0.0, 1.0, 1.0);
}
""",
            EntryPoint = "PSMain",
            Label = "HLSL Vector Conditional Pixel"
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
        Assert.Contains("vec4<bool>(1.0 > 0.5, 1.0 > 0.5, 1.0 > 0.5, 1.0 > 0.5)", pixelShader.BackendSource, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after HLSL vector conditional draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after HLSL vector conditional draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after HLSL vector conditional draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL vector conditional draw, actual: {center}");
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
    public void FlushSubmitsGpuBackedHlslPackedConstantBufferDrawCommands()
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
            SizeInBytes = 16,
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            Label = "Packed Settings Constants"
        });
        constants.Write<float>([1.0f, 0.0f, 0.0f, 1.0f]);
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl,
            EntryPoint = "vs_main"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = """
cbuffer Settings : register(b0)
{
    float red;
    float2 greenBlue;
    float alpha;
};

float4 PSMain() : SV_Target
{
    return float4(red, greenBlue.x, greenBlue.y, alpha);
}
""",
            EntryPoint = "PSMain"
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
        context.SetConstantBuffer(DxShaderStage.Pixel, 0, constants);
        context.Draw(3);
        context.Flush();

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("_r0: vec4<u32>", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel from packed cbuffer, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel from packed cbuffer, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel from packed cbuffer, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel from packed cbuffer, actual: {center}");
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
    public void FlushSubmitsGpuBackedHlslTextDrawCommandsWithComparisonSampler()
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
        using var shadowMap = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.D32Float,
            Usage = DxTextureUsage.DepthStencil | DxTextureUsage.ShaderResource,
            Label = "Comparison Shadow Map"
        });
        using var shadowView = device.CreateShaderResourceView(shadowMap);
        using var shadowSampler = device.CreateSamplerState(new DxSamplerDescriptor
        {
            Filter = DxFilter.ComparisonMinMagMipLinear,
            AddressU = DxTextureAddressMode.Clamp,
            AddressV = DxTextureAddressMode.Clamp,
            ComparisonFunction = DxComparisonFunction.LessEqual
        });
        using var constants = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            Label = "Comparison Transform Constants"
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
            Label = "HLSL Comparison Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = TextureComparisonSamplePixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Comparison Pixel"
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

        context.ClearDepthStencil(shadowMap, DxDepthStencilClearFlags.Depth, depth: 0.5f, stencil: 0);
        context.Flush();
        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.SetVertexBuffer(vertexBuffer);
        context.SetConstantBuffer(DxShaderStage.Vertex, 0, constants);
        context.SetShaderResource(DxShaderStage.Pixel, 0, shadowView);
        context.SetSampler(DxShaderStage.Pixel, 0, shadowSampler);
        context.Draw(3);
        context.Flush();

        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@binding(576) var ShadowMap: texture_depth_2d;", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("@binding(768) var ShadowSampler: sampler_comparison;", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("textureSampleCompare(ShadowMap, ShadowSampler, input.uv, 0.25)", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected near comparison to pass after HLSL SampleCmp draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected far comparison to fail after HLSL SampleCmp draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue after HLSL SampleCmp draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL SampleCmp draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslTextDrawCommandsWithTexture2DArraySampler()
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
            ArraySize = 2,
            Label = "Source Texture Array"
        });
        sourceTexture.WritePixels<byte>(
        [
            255, 0, 0, 255,
            0, 255, 0, 255
        ]);
        using var sourceView = device.CreateShaderResourceView(sourceTexture, new DxShaderResourceViewDescriptor
        {
            Dimension = DxResourceViewDimension.Texture2DArray,
            ArraySize = 2,
            Label = "Source Texture Array SRV"
        });
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
            Label = "Texture Array Transform Constants"
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
            Label = "HLSL Texture Array Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = Texture2DArraySamplePixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Texture Array Pixel"
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

        Assert.Equal(2u, sourceTexture.BackendTexture!.DepthOrArrayLayers);
        Assert.Equal(DxResourceViewDimension.Texture2DArray, sourceView.Dimension);
        Assert.True(sourceView.HasBackendTextureView);
        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@binding(576) var SourceTextureArray: texture_2d_array<f32>;", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("textureSample(SourceTextureArray, SourceSampler, uvw.xy, i32(uvw.z))", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("textureLoad(SourceTextureArray, (vec4<i32>(0, 0, 1, 0)).xy, i32((vec4<i32>(0, 0, 1, 0)).z), (vec4<i32>(0, 0, 1, 0)).w)", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R < 50, $"Expected low red center pixel after HLSL texture array sample draw, actual: {center}");
        Assert.True(center.G > 200, $"Expected green center pixel after HLSL texture array sample draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after HLSL texture array sample draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL texture array sample draw, actual: {center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslTextDrawCommandsWithTexture3DSampler()
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
        using var sourceTexture = device.CreateTexture3D(new DxTexture3DDescriptor
        {
            Width = 2,
            Height = 2,
            Depth = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopyDestination | DxTextureUsage.CopySource,
            Label = "Source Texture Volume"
        });
        sourceTexture.WritePixels<byte>(
        [
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 255, 0, 255,
            0, 255, 0, 255,
            0, 255, 0, 255
        ]);
        using var sourceView = device.CreateShaderResourceView(sourceTexture, new DxShaderResourceViewDescriptor
        {
            Dimension = DxResourceViewDimension.Texture3D,
            Label = "Source Texture Volume SRV"
        });
        using var sourceSampler = device.CreateSamplerState(new DxSamplerDescriptor
        {
            Filter = DxFilter.MinMagMipPoint,
            AddressU = DxTextureAddressMode.Clamp,
            AddressV = DxTextureAddressMode.Clamp,
            AddressW = DxTextureAddressMode.Clamp
        });
        using var constants = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 64,
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            Label = "Texture Volume Transform Constants"
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
            Label = "HLSL Texture Volume Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = Texture3DSamplePixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Texture Volume Pixel"
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

        Assert.Equal(GpuTextureDimension.Dimension3D, sourceTexture.BackendTexture!.Dimension);
        Assert.Equal(2u, sourceTexture.BackendTexture.DepthOrArrayLayers);
        Assert.Equal(DxResourceViewDimension.Texture3D, sourceView.Dimension);
        Assert.True(sourceView.HasBackendTextureView);
        Assert.True(pixelShader.HasBackendShaderModule);
        Assert.Contains("@binding(576) var SourceTextureVolume: texture_3d<f32>;", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("textureSample(SourceTextureVolume, SourceSampler, vec3<f32>(input.uv, 0.75))", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.Contains("textureLoad(SourceTextureVolume", pixelShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDrawCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected sampled green channel to reach red output after HLSL texture volume sample draw, actual: {center}");
        Assert.True(center.G > 200, $"Expected loaded green channel after HLSL texture volume load draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low sampled red channel after HLSL texture volume sample draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after HLSL texture volume sample draw, actual: {center}");
    }

    [Fact]
    public void GpuBackedDrawsReusePipelineCompatibleBindGroupsAcrossFlushes()
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
            Label = "Cached Source Texture"
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
            Label = "Cached Texture Transform Constants"
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
            Label = "HLSL Cached Textured Vertex"
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = TextureSampleLevelTintPixelHlsl,
            EntryPoint = "PSMain",
            Label = "HLSL Cached Texture Pixel"
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

        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Equal(1ul, context.DrawBindGroupCacheMissCount);
        Assert.Equal(0ul, context.DrawBindGroupCacheHitCount);
        Assert.Equal(1, context.CachedPipelineBindGroupCount);

        constants.Write<float>(
        [
            1.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 1.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        ]);
        sourceTexture.WritePixels<byte>([255, 255, 255, 255]);
        context.ClearRenderTarget(target, DxColor.Black);
        context.Draw(3);
        context.Flush();

        Assert.Equal(2ul, context.SubmittedDrawCount);
        Assert.Equal(1ul, context.DrawBindGroupCacheMissCount);
        Assert.Equal(1ul, context.DrawBindGroupCacheHitCount);
        Assert.Equal(1, context.CachedPipelineBindGroupCount);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after cached HLSL texture sample draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after cached HLSL texture sample draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after cached HLSL texture sample draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after cached HLSL texture sample draw, actual: {center}");
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
    public void FlushSubmitsGpuBackedDepthOnlyDrawCommands()
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
            Source = SolidTriangleWgsl
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SolidTriangleWgsl
        });
        using var depthOnlyPipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = null,
            RenderTargetFormat = DxResourceFormat.Unknown,
            DepthStencilFormat = DxResourceFormat.D32Float,
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });
        using var colorPipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
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
                DepthFunction = DxComparisonFunction.Less
            }
        });
        using var context = device.CreateImmediateContext();

        context.ClearDepthStencil(depth, DxDepthStencilClearFlags.Depth, depth: 1f, stencil: 0);
        context.SetRenderTargets(null, depth);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.SetGraphicsPipeline(depthOnlyPipeline);
        context.Draw(3);
        context.ClearRenderTarget(target, DxColor.Black);
        context.SetRenderTargets(target, depth);
        context.SetGraphicsPipeline(colorPipeline);
        context.Draw(3);
        context.Flush();

        Assert.True(depthOnlyPipeline.HasBackendPipeline);
        Assert.True(depthOnlyPipeline.Descriptor.DepthStencilState.DepthEnable);
        Assert.Equal(DxComparisonFunction.Less, depthOnlyPipeline.Descriptor.DepthStencilState.DepthFunction);
        Assert.Equal(2ul, context.SubmittedDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R < 50, $"Expected depth-only prepass to reject later red draw, actual: {center}");
        Assert.True(center.G < 50, $"Expected depth-only prepass to keep green channel clear, actual: {center}");
        Assert.True(center.B < 50, $"Expected depth-only prepass to keep blue channel clear, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque clear after depth-only prepass, actual: {center}");
    }

    [Fact]
    public void GpuBackedPixelDrawRejectsMissingRenderTargetBeforeQueueing()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
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
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm
        });
        using var context = device.CreateImmediateContext();

        context.SetGraphicsPipeline(pipeline);

        Assert.Throws<InvalidOperationException>(() => context.Draw(3));
        Assert.DoesNotContain(context.Commands, command => command.Kind == ProGpuDirectXCommandKind.Draw);
    }

    [Fact]
    public void FlushAppliesQueuedDepthStencilStateToGpuBackedDrawCommands()
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
            DepthStencilFormat = DxResourceFormat.D32Float,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None },
            DepthStencilState = new DxDepthStencilStateDescriptor
            {
                DepthEnable = true,
                DepthWriteMask = DxDepthWriteMask.All,
                DepthFunction = DxComparisonFunction.Less
            }
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(target, DxColor.Black);
        context.ClearDepthStencil(depth, DxDepthStencilClearFlags.Depth, depth: 0f, stencil: 0);
        context.SetRenderTargets(target, depth);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.SetGraphicsPipeline(pipeline);
        context.SetDepthStencilState(new DxDepthStencilStateDescriptor
        {
            DepthEnable = false,
            DepthWriteMask = DxDepthWriteMask.Zero,
            DepthFunction = DxComparisonFunction.Less
        });
        context.Draw(3);

        var drawCommand = context.Commands.Last(command => command.Kind == ProGpuDirectXCommandKind.Draw);
        Assert.NotNull(drawCommand.DepthStencilState);
        Assert.False(drawCommand.DepthStencilState!.DepthEnable);

        context.Flush();

        Assert.Equal(2ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Equal(1, context.CachedDynamicGraphicsPipelineCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after depth-state override disabled depth testing, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after depth-state override, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after depth-state override, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after depth-state override, actual: {center}");
    }

    [Fact]
    public void FlushIgnoresQueuedScissorWhenRasterizerScissorIsDisabled()
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
            RasterizerState = new DxRasterizerStateDescriptor
            {
                CullMode = DxCullMode.None,
                ScissorEnable = false
            }
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(target, DxColor.Black);
        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.SetScissorRect(new DxRect(0, 0, 4, 4));
        context.SetGraphicsPipeline(pipeline);
        context.Draw(3);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel because disabled scissor ignores queued scissor rect, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after disabled-scissor draw, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after disabled-scissor draw, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after disabled-scissor draw, actual: {center}");
    }

    [Fact]
    public void FlushAppliesQueuedBlendStateToGpuBackedDrawCommands()
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
            BlendState = new DxBlendStateDescriptor
            {
                EnableBlend = false,
                WriteMask = DxColorWriteMask.All
            },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(target, DxColor.Black);
        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.SetGraphicsPipeline(pipeline);
        context.SetBlendState(new DxBlendStateDescriptor
        {
            EnableBlend = false,
            WriteMask = DxColorWriteMask.None
        });
        context.Draw(3);

        var drawCommand = context.Commands.Last(command => command.Kind == ProGpuDirectXCommandKind.Draw);
        Assert.NotNull(drawCommand.BlendState);
        Assert.Equal(DxColorWriteMask.None, drawCommand.BlendState!.WriteMask);

        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Equal(1, context.CachedDynamicGraphicsPipelineCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R < 50, $"Expected low red center pixel after queued blend write mask disabled color writes, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after queued blend write mask disabled color writes, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after queued blend write mask disabled color writes, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque cleared center pixel after queued blend write mask disabled color writes, actual: {center}");
    }

    [Fact]
    public void FlushAppliesQueuedPrimitiveTopologyToGpuBackedDrawCommands()
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
            Topology = DxPrimitiveTopology.LineList,
            RenderTargetFormat = DxResourceFormat.R8G8B8A8Unorm,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(target, DxColor.Black);
        context.SetRenderTargets(target);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.SetGraphicsPipeline(pipeline);
        context.SetPrimitiveTopology(DxPrimitiveTopology.TriangleList);
        context.Draw(3);

        var drawCommand = context.Commands.Last(command => command.Kind == ProGpuDirectXCommandKind.Draw);
        Assert.Equal(DxPrimitiveTopology.TriangleList, drawCommand.Topology);

        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Equal(1, context.CachedDynamicGraphicsPipelineCount);
        Assert.Empty(context.Commands);

        var pixels = target.BackendTexture!.ReadPixels();
        var center = ReadRgbaPixel(pixels, 32, 16, 16);
        Assert.True(center.R > 200, $"Expected red center pixel after queued topology selected triangle pipeline, actual: {center}");
        Assert.True(center.G < 50, $"Expected low green center pixel after queued topology selected triangle pipeline, actual: {center}");
        Assert.True(center.B < 50, $"Expected low blue center pixel after queued topology selected triangle pipeline, actual: {center}");
        Assert.True(center.A > 200, $"Expected opaque center pixel after queued topology selected triangle pipeline, actual: {center}");
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
                DepthEnable = false,
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
                DepthEnable = false,
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
    public void FlushSubmitsGpuBackedMultisampleResolveForArraySubresources()
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
            SampleCount = 4,
            ArraySize = 2
        });
        using var destination = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read,
            ArraySize = 2
        });
        using var sourceSlice0 = device.CreateRenderTargetView(source, new DxRenderTargetViewDescriptor
        {
            Dimension = DxResourceViewDimension.Texture2DArray,
            FirstArraySlice = 0,
            ArraySize = 1,
            Label = "ResolveSourceSlice0"
        });
        using var sourceSlice1 = device.CreateRenderTargetView(source, new DxRenderTargetViewDescriptor
        {
            Dimension = DxResourceViewDimension.Texture2DArray,
            FirstArraySlice = 1,
            ArraySize = 1,
            Label = "ResolveSourceSlice1"
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(sourceSlice0, new DxColor(1f, 0f, 0f, 1f));
        context.ClearRenderTarget(sourceSlice1, new DxColor(0f, 0f, 1f, 1f));
        context.ResolveSubresource(destination, destinationSubresource: 0, source, sourceSubresource: 0, DxResourceFormat.R8G8B8A8Unorm);
        context.ResolveSubresource(destination, destinationSubresource: 1, source, sourceSubresource: 1, DxResourceFormat.R8G8B8A8Unorm);
        context.Flush();

        Assert.Equal(2ul, context.SubmittedClearCount);
        Assert.Equal(2ul, context.SubmittedResolveCount);
        Assert.Empty(context.Commands);

        var pixels = destination.ReadPixels();
        var sliceSize = checked((int)(destination.Width * destination.Height * 4));
        var slice0Center = ReadRgbaPixel(pixels.AsSpan(0, sliceSize).ToArray(), 16, 8, 8);
        var slice1Center = ReadRgbaPixel(pixels.AsSpan(sliceSize, sliceSize).ToArray(), 16, 8, 8);
        Assert.True(slice0Center.R > 200, $"Expected red resolved layer 0 pixel, actual: {slice0Center}");
        Assert.True(slice0Center.G < 50, $"Expected low green resolved layer 0 pixel, actual: {slice0Center}");
        Assert.True(slice0Center.B < 50, $"Expected low blue resolved layer 0 pixel, actual: {slice0Center}");
        Assert.True(slice0Center.A > 200, $"Expected opaque resolved layer 0 pixel, actual: {slice0Center}");
        Assert.True(slice1Center.R < 50, $"Expected low red resolved array pixel, actual: {slice1Center}");
        Assert.True(slice1Center.G < 50, $"Expected low green resolved array pixel, actual: {slice1Center}");
        Assert.True(slice1Center.B > 200, $"Expected blue resolved array pixel, actual: {slice1Center}");
        Assert.True(slice1Center.A > 200, $"Expected opaque resolved array pixel, actual: {slice1Center}");
    }

    [Fact]
    public void FlushSubmitsGpuBackedDrawIntoMultisampleArraySlice()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var source = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget,
            SampleCount = 4,
            ArraySize = 2
        });
        using var destination = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read,
            ArraySize = 2
        });
        using var sourceSlice1 = device.CreateRenderTargetView(source, new DxRenderTargetViewDescriptor
        {
            Dimension = DxResourceViewDimension.Texture2DArray,
            FirstArraySlice = 1,
            ArraySize = 1,
            Label = "DrawSourceSlice1"
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
            SampleCount = 4,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None }
        });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(sourceSlice1);
        context.SetViewport(new DxViewport(0, 0, 32, 32));
        context.ClearRenderTarget(sourceSlice1, DxColor.Black);
        context.SetGraphicsPipeline(pipeline);
        context.Draw(3);
        context.ResolveSubresource(destination, destinationSubresource: 1, source, sourceSubresource: 1, DxResourceFormat.R8G8B8A8Unorm);
        context.Flush();

        Assert.Equal(1ul, context.SubmittedClearCount);
        Assert.Equal(1ul, context.SubmittedDrawCount);
        Assert.Equal(1ul, context.SubmittedResolveCount);
        Assert.Empty(context.Commands);

        var pixels = destination.ReadPixels();
        var sliceSize = checked((int)(destination.Width * destination.Height * 4));
        var slice0Center = ReadRgbaPixel(pixels.AsSpan(0, sliceSize).ToArray(), 32, 16, 16);
        var slice1Center = ReadRgbaPixel(pixels.AsSpan(sliceSize, sliceSize).ToArray(), 32, 16, 16);
        Assert.True(slice0Center.R < 50, $"Expected unresolved layer 0 low red, actual: {slice0Center}");
        Assert.True(slice1Center.R > 200, $"Expected red resolved draw pixel, actual: {slice1Center}");
        Assert.True(slice1Center.G < 50, $"Expected low green resolved draw pixel, actual: {slice1Center}");
        Assert.True(slice1Center.B < 50, $"Expected low blue resolved draw pixel, actual: {slice1Center}");
        Assert.True(slice1Center.A > 200, $"Expected opaque resolved draw pixel, actual: {slice1Center}");
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
    public void GpuBackedCpuReadableCopySourceBuffersAreRejected()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);

        var exception = Assert.Throws<ArgumentException>(() => device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 16,
            Usage = DxBufferUsage.CopySource | DxBufferUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read
        }));

        Assert.Contains("map-read", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Throws<ArgumentOutOfRangeException>(() => writeOnly.Map(DxMapMode.Write, subresource: 1));
        Assert.Throws<NotSupportedException>(() => multisampled.Map(DxMapMode.Write));
        Assert.Throws<ArgumentOutOfRangeException>(() => arrayTexture.Map(DxMapMode.Write, subresource: 2));
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
    public void TextureMapSupportsArraySubresources()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopyDestination | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write,
            ArraySize = 2
        });
        byte[] layerPixels =
        [
            10, 20, 30, 255, 40, 50, 60, 255,
            70, 80, 90, 255, 100, 110, 120, 255
        ];

        using var mapping = texture.Map(DxMapMode.WriteDiscard, subresource: 1);
        Assert.Equal(16u, mapping.OffsetBytes);
        Assert.Equal(8u, mapping.RowPitch);
        Assert.Equal(16u, mapping.DepthPitch);
        mapping.Write<byte>(layerPixels);
        mapping.Unmap();

        Assert.Equal(16u, texture.LastWriteSizeInBytes);
        var pixels = texture.ReadPixels();
        Assert.Equal(new byte[16], pixels[..16]);
        Assert.Equal(layerPixels, pixels[16..32]);
    }

    [Fact]
    public void TextureMapSupportsMipSubresourcesInCpuShadow()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopyDestination | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write,
            MipLevels = 3,
            ArraySize = 2
        });
        byte[] mipPixels = [10, 20, 30, 255];

        using var mapping = texture.Map(DxMapMode.WriteDiscard, subresource: 5);
        Assert.Equal(5u, mapping.Subresource);
        Assert.Equal(164u, mapping.OffsetBytes);
        Assert.Equal(4u, mapping.RowPitch);
        Assert.Equal(4u, mapping.DepthPitch);
        mapping.Write<byte>(mipPixels);
        mapping.Unmap();

        Assert.Equal(4u, texture.LastWriteSizeInBytes);
        var pixels = texture.ReadPixels();
        Assert.Equal(168, pixels.Length);
        Assert.Equal(new byte[164], pixels[..164]);
        Assert.Equal(mipPixels, pixels[164..168]);
    }

    [Fact]
    public void GenerateMipsBuildsTexture2DSubresourcesFromDefaultShaderResourceView()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.RenderTarget | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write,
            MipLevels = 3
        });
        using var shaderResourceView = device.CreateShaderResourceView(texture);
        using var context = device.CreateImmediateContext();
        var pixels = CreateGeneratedMipSourcePixels();

        texture.WritePixels<byte>(pixels);
        context.GenerateMips(shaderResourceView);

        Assert.Equal(ProGpuDirectXCommandKind.GenerateMips, context.Commands.Single().Kind);
        Assert.Same(shaderResourceView, context.Commands.Single().ShaderResourceView);

        var generated = texture.ReadPixels();
        Assert.Equal(24, generated[64]);
        Assert.Equal(104, generated[68]);
        Assert.Equal(40, generated[72]);
        Assert.Equal(120, generated[76]);
        Assert.Equal(72, generated[80]);
        Assert.Equal(255, generated[83]);
        Assert.Equal(20u, texture.LastWriteSizeInBytes);
    }

    [Fact]
    public void GenerateMipsUploadsGpuBackedTexture2DSubresources()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource |
                DxTextureUsage.RenderTarget |
                DxTextureUsage.CopyDestination |
                DxTextureUsage.CopySource,
            MipLevels = 3
        });
        using var shaderResourceView = device.CreateShaderResourceView(texture);
        using var context = device.CreateImmediateContext();

        texture.WritePixels<byte>(CreateGeneratedMipSourcePixels());
        context.GenerateMips(shaderResourceView);

        var mip1 = texture.BackendTexture!.ReadPixels(mipLevel: 1);
        var mip2 = texture.BackendTexture!.ReadPixels(mipLevel: 2);
        Assert.Equal(24, mip1[0]);
        Assert.Equal(104, mip1[4]);
        Assert.Equal(40, mip1[8]);
        Assert.Equal(120, mip1[12]);
        Assert.Equal(72, mip2[0]);
        Assert.Equal(255, mip2[3]);
    }

    [Fact]
    public void GenerateMipsUsesNativeGpuPathForRenderedSourceMipWithoutCopyDestination()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource |
                DxTextureUsage.RenderTarget |
                DxTextureUsage.CopySource,
            MipLevels = 2
        });
        using var renderTargetView = device.CreateRenderTargetView(texture);
        using var shaderResourceView = device.CreateShaderResourceView(texture);
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(renderTargetView, new DxColor(1f, 0f, 0f, 1f));
        context.Flush();
        context.GenerateMips(shaderResourceView);

        var mip1 = texture.BackendTexture!.ReadPixels(mipLevel: 1);
        Assert.True(mip1[0] > 200, $"Expected rendered red source mip to generate red mip1, actual R={mip1[0]}.");
        Assert.True(mip1[1] < 50, $"Expected low green from rendered source mip, actual G={mip1[1]}.");
        Assert.True(mip1[2] < 50, $"Expected low blue from rendered source mip, actual B={mip1[2]}.");
        Assert.True(mip1[3] > 200, $"Expected opaque generated mip1, actual A={mip1[3]}.");
    }

    [Fact]
    public void GenerateMipsReusesNativeGpuPathAcrossTexturesAndCalls()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var textureA = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource |
                DxTextureUsage.RenderTarget |
                DxTextureUsage.CopySource,
            MipLevels = 2
        });
        using var textureB = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource |
                DxTextureUsage.RenderTarget |
                DxTextureUsage.CopySource,
            MipLevels = 2
        });
        using var renderTargetA = device.CreateRenderTargetView(textureA);
        using var renderTargetB = device.CreateRenderTargetView(textureB);
        using var shaderResourceA = device.CreateShaderResourceView(textureA);
        using var shaderResourceB = device.CreateShaderResourceView(textureB);
        using var context = device.CreateImmediateContext();

        GenerateMipFromRenderedClear(context, renderTargetA, shaderResourceA, new DxColor(1f, 0f, 0f, 1f));
        var redMip = textureA.BackendTexture!.ReadPixels(mipLevel: 1);
        Assert.True(redMip[0] > 200, $"Expected red mip from first cached-path call, actual R={redMip[0]}.");
        Assert.True(redMip[1] < 50, $"Expected low green from first cached-path call, actual G={redMip[1]}.");

        GenerateMipFromRenderedClear(context, renderTargetB, shaderResourceB, new DxColor(0f, 1f, 0f, 1f));
        var greenMip = textureB.BackendTexture!.ReadPixels(mipLevel: 1);
        Assert.True(greenMip[1] > 200, $"Expected green mip from second cached-path texture, actual G={greenMip[1]}.");
        Assert.True(greenMip[0] < 50, $"Expected low red from second cached-path texture, actual R={greenMip[0]}.");

        GenerateMipFromRenderedClear(context, renderTargetA, shaderResourceA, new DxColor(0f, 0f, 1f, 1f));
        var blueMip = textureA.BackendTexture!.ReadPixels(mipLevel: 1);
        Assert.True(blueMip[2] > 200, $"Expected blue mip from repeated cached-path call, actual B={blueMip[2]}.");
        Assert.True(blueMip[0] < 50, $"Expected low red from repeated cached-path call, actual R={blueMip[0]}.");
        Assert.True(blueMip[3] > 200, $"Expected opaque generated mip, actual A={blueMip[3]}.");

        static void GenerateMipFromRenderedClear(
            ProGpuDirectXDeviceContext context,
            ProGpuDirectXRenderTargetView renderTargetView,
            ProGpuDirectXShaderResourceView shaderResourceView,
            DxColor color)
        {
            context.ClearRenderTarget(renderTargetView, color);
            context.Flush();
            context.GenerateMips(shaderResourceView);
            context.ClearRecordedCommands();
        }
    }

    [Fact]
    public void TextureMapSupportsMipSubresourcePitchesForNonPowerOfTwoTextures()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 5,
            Height = 3,
            Format = DxResourceFormat.R16Float,
            Usage = DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Write,
            MipLevels = 4
        });

        using var mip1 = texture.Map(DxMapMode.WriteDiscard, subresource: 1);
        Assert.Equal(30u, mip1.OffsetBytes);
        Assert.Equal(4u, mip1.RowPitch);
        Assert.Equal(4u, mip1.DepthPitch);
        mip1.Unmap();

        using var mip3 = texture.Map(DxMapMode.WriteDiscard, subresource: 3);
        Assert.Equal(36u, mip3.OffsetBytes);
        Assert.Equal(2u, mip3.RowPitch);
        Assert.Equal(2u, mip3.DepthPitch);
    }

    [Fact]
    public void TextureMapRejectsOutOfRangeMipSubresource()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Write,
            MipLevels = 3,
            ArraySize = 2
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => texture.Map(DxMapMode.Write, subresource: 6));
    }

    [Theory]
    [InlineData(DxResourceFormat.D32Float)]
    [InlineData(DxResourceFormat.D24UnormS8UInt)]
    public void TextureMapSupportsDepthFormatsInCpuShadow(DxResourceFormat format)
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = format,
            Usage = DxTextureUsage.DepthStencil | DxTextureUsage.CopySource | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write,
            MipLevels = 2
        });
        byte[] depthBytes = [0x11, 0x22, 0x33, 0x44];

        using var mapping = texture.Map(DxMapMode.WriteDiscard, subresource: 1);
        Assert.Equal(16u, mapping.OffsetBytes);
        Assert.Equal(4u, mapping.RowPitch);
        Assert.Equal(4u, mapping.DepthPitch);
        mapping.Write<byte>(depthBytes);
        mapping.Unmap();

        var pixels = texture.ReadPixels();
        Assert.Equal(20, pixels.Length);
        Assert.Equal(new byte[16], pixels[..16]);
        Assert.Equal(depthBytes, pixels[16..20]);
    }

    [Fact]
    public void GpuBackedD32FloatTextureReadMapUsesBackendDepthStaging()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = DxResourceFormat.D32Float,
            Usage = DxTextureUsage.DepthStencil | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var context = device.CreateImmediateContext();
        float[] depthValues = [0.5f, 0.5f, 0.5f, 0.5f];
        var depthBytes = MemoryMarshal.AsBytes(depthValues.AsSpan()).ToArray();

        context.ClearDepthStencil(texture, DxDepthStencilClearFlags.Depth, depth: 0.5f, stencil: 0);
        context.Flush();
        Assert.Equal(depthBytes, texture.BackendTexture!.ReadPixels());

        using var readMap = texture.Map(DxMapMode.Read);
        Assert.Equal(depthBytes, readMap.Read<byte>(16));
    }

    [Theory]
    [InlineData(DxResourceFormat.D32Float)]
    [InlineData(DxResourceFormat.D24UnormS8UInt)]
    public void GpuBackedDepthTextureWriteMapFailsClosedUntilDepthUploadIsValidated(DxResourceFormat format)
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = format,
            Usage = DxTextureUsage.DepthStencil | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Write
        });

        var exception = Assert.Throws<NotSupportedException>(() => texture.Map(DxMapMode.WriteDiscard));
        Assert.Contains("read staging", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TextureMapMipSubresourceUploadsGpuBackedTextureOnUnmap()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopyDestination | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write,
            MipLevels = 2
        });
        byte[] mipPixels =
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ];

        using var mapping = texture.Map(DxMapMode.WriteDiscard, subresource: 1);
        Assert.Equal(64u, mapping.OffsetBytes);
        Assert.Equal(8u, mapping.RowPitch);
        Assert.Equal(16u, mapping.DepthPitch);
        mapping.Write<byte>(mipPixels);
        mapping.Unmap();

        Assert.Equal(mipPixels, texture.BackendTexture!.ReadPixels(mipLevel: 1));

        using var readMap = texture.Map(DxMapMode.Read, subresource: 1);
        Assert.Equal(mipPixels, readMap.Read<byte>(16));
    }

    [Theory]
    [InlineData(DxResourceFormat.R8G8B8A8Unorm)]
    [InlineData(DxResourceFormat.B8G8R8A8Unorm)]
    public void GpuBackedDirectXColorTexturesUseStraightAlpha(DxResourceFormat format)
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 1,
            Height = 1,
            Format = format,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopyDestination | DxTextureUsage.CopySource
        });

        Assert.Equal(GpuTextureAlphaMode.Straight, texture.BackendTexture!.AlphaMode);
    }

    [Fact]
    public void GpuBackedCpuReadableTextureAllocatesCopySourceUsage()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 1,
            Height = 1,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(texture, new DxColor(1f, 0f, 0f, 1f));
        context.Flush();

        Assert.Equal([255, 0, 0, 255], texture.ReadPixels());
    }

    [Fact]
    public void GpuBackedCpuWritableTextureAllocatesCopyDestinationUsage()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 1,
            Height = 1,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Write
        });
        byte[] pixels = [10, 20, 30, 40];

        using var mapping = texture.Map(DxMapMode.WriteDiscard);
        mapping.Write<byte>(pixels);
        mapping.Unmap();

        Assert.Equal(pixels, texture.BackendTexture!.ReadPixels());
    }

    [Fact]
    public void Texture3DMapMipSubresourceUploadsGpuBackedTextureOnUnmap()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture3D(new DxTexture3DDescriptor
        {
            Width = 4,
            Height = 4,
            Depth = 4,
            Format = DxResourceFormat.R8Unorm,
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopyDestination | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write,
            MipLevels = 2
        });
        byte[] mipPixels = [10, 20, 30, 40, 50, 60, 70, 80];

        using var mapping = texture.Map(DxMapMode.WriteDiscard, subresource: 1);
        Assert.Equal(64u, mapping.OffsetBytes);
        Assert.Equal(2u, mapping.RowPitch);
        Assert.Equal(4u, mapping.DepthPitch);
        Assert.Equal(8u, mapping.SizeInBytes);
        mapping.Write<byte>(mipPixels);
        mapping.Unmap();

        Assert.Equal(mipPixels, texture.BackendTexture!.ReadPixels(mipLevel: 1));

        using var readMap = texture.Map(DxMapMode.Read, subresource: 1);
        Assert.Equal(mipPixels, readMap.Read<byte>(8));
    }

    [Fact]
    public void TextureMapArraySubresourceUploadsGpuBackedTextureOnUnmap()
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
            CpuAccess = DxCpuAccessFlags.Write,
            ArraySize = 2
        });
        byte[] layer0Pixels =
        [
            10, 10, 10, 255, 20, 20, 20, 255,
            30, 30, 30, 255, 40, 40, 40, 255
        ];
        byte[] layerPixels =
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ];
        byte[] initialPixels = [.. layer0Pixels, .. new byte[16]];
        texture.BackendTexture!.WritePixels(initialPixels);

        using var mapping = texture.Map(DxMapMode.WriteDiscard, subresource: 1);
        mapping.Write<byte>(layerPixels);
        mapping.Unmap();

        var pixels = texture.BackendTexture!.ReadPixels();
        Assert.Equal(layer0Pixels, pixels[..16]);
        Assert.Equal(layerPixels, pixels[16..32]);
    }

    [Theory]
    [InlineData(DxResourceFormat.R16Float, 2)]
    [InlineData(DxResourceFormat.R32G32Float, 8)]
    [InlineData(DxResourceFormat.R32G32UInt, 8)]
    [InlineData(DxResourceFormat.R32G32SInt, 8)]
    [InlineData(DxResourceFormat.R32G32B32A32Float, 16)]
    [InlineData(DxResourceFormat.R32G32B32A32UInt, 16)]
    [InlineData(DxResourceFormat.R32G32B32A32SInt, 16)]
    public void TextureMapSupportsWiderSingleMipFormats(DxResourceFormat format, int bytesPerPixel)
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 2,
            Height = 2,
            Format = format,
            Usage = DxTextureUsage.CopyDestination | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write
        });
        var rowPitch = checked((uint)(2 * bytesPerPixel));
        var depthPitch = checked((uint)(2 * 2 * bytesPerPixel));
        var pixels = new byte[depthPitch];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i + 1);
        }

        using var mapping = texture.Map(DxMapMode.WriteDiscard);
        Assert.Equal(0u, mapping.OffsetBytes);
        Assert.Equal(rowPitch, mapping.RowPitch);
        Assert.Equal(depthPitch, mapping.DepthPitch);
        mapping.Write<byte>(pixels);
        mapping.Unmap();

        Assert.Equal(depthPitch, texture.LastWriteSizeInBytes);
        Assert.Equal(pixels, texture.ReadPixels());
    }

    [Fact]
    public void TextureMapWiderFormatUploadsGpuBackedTextureOnUnmap()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 1,
            Height = 2,
            Format = DxResourceFormat.R32G32B32A32UInt,
            Usage = DxTextureUsage.CopyDestination | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Write
        });
        byte[] pixels =
        [
            1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0,
            5, 0, 0, 0, 6, 0, 0, 0, 7, 0, 0, 0, 8, 0, 0, 0
        ];

        using var mapping = texture.Map(DxMapMode.WriteDiscard);
        Assert.Equal(16u, mapping.RowPitch);
        Assert.Equal(32u, mapping.DepthPitch);
        mapping.Write<byte>(pixels);
        mapping.Unmap();

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
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopySource,
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
    public void FlushSubmitsGpuBackedHlslRwTexture2DDispatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var output = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.UnorderedAccess | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read,
            Label = "RWTexture2D Output"
        });
        using var outputView = device.CreateUnorderedAccessView(
            output,
            new DxUnorderedAccessViewDescriptor
            {
                Format = DxResourceFormat.R8G8B8A8Unorm,
                Access = DxUnorderedAccessViewAccess.WriteOnly,
                Label = "RWTexture2D Output UAV"
            });
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = RwTexture2DComputeHlsl,
            EntryPoint = "CSMain",
            Label = "HLSL RWTexture2D Compute"
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
        Assert.Contains("@binding(1856) var Output: texture_storage_2d<rgba8unorm, write>;", computeShader.BackendSource!, StringComparison.Ordinal);
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDispatchCount);

        var pixels = output.ReadPixels();
        Assert.Equal(0, pixels[0]);
        Assert.True(pixels[1] > 200, $"Expected green storage-texture write, actual G={pixels[1]}.");
        Assert.Equal(0, pixels[2]);
        Assert.True(pixels[3] > 200, $"Expected opaque storage-texture write, actual A={pixels[3]}.");
    }

    [Fact]
    public void FlushSubmitsGpuBackedHlslRwTexture2DReadWriteDispatchCommands()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var output = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 4,
            Height = 4,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.UnorderedAccess | DxTextureUsage.CopySource | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read | DxCpuAccessFlags.Write,
            Label = "RWTexture2D ReadWrite Output"
        });
        byte[] initialPixels =
        [
            128, 0, 64, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255
        ];
        output.WritePixels(initialPixels);
        using var outputView = device.CreateUnorderedAccessView(
            output,
            new DxUnorderedAccessViewDescriptor
            {
                Format = DxResourceFormat.R8G8B8A8Unorm,
                Access = DxUnorderedAccessViewAccess.ReadWrite,
                Label = "RWTexture2D ReadWrite Output UAV"
            });
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslText,
            Source = RwTexture2DReadWriteComputeHlsl,
            EntryPoint = "CSMain",
            Label = "HLSL RWTexture2D ReadWrite Compute"
        });

        Assert.NotNull(computeShader.BackendSource);
        Assert.Contains("@binding(1856) var Output: texture_storage_2d<rgba8unorm, read_write>;", computeShader.BackendSource, StringComparison.Ordinal);
        Assert.Contains("textureLoad(Output, vec2<i32>(id.xy))", computeShader.BackendSource, StringComparison.Ordinal);

        if (!device.Capabilities.SupportsReadWriteStorageTextures)
        {
            return;
        }

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
        Assert.True(pipeline.HasBackendPipeline);
        Assert.Equal(1ul, context.SubmittedDispatchCount);

        var pixels = output.ReadPixels();
        Assert.InRange(pixels[0], 120, 136);
        Assert.True(pixels[1] > 200, $"Expected green storage-texture read/write result, actual G={pixels[1]}.");
        Assert.InRange(pixels[2], 56, 72);
        Assert.True(pixels[3] > 200, $"Expected opaque storage-texture read/write result, actual A={pixels[3]}.");
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
    public void GraphicsBindingValidationPreflightsReflectedPipelineRequirements()
    {
        var vertexBytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(
                ("FrameConstants", (uint)DxReflectedShaderResourceType.ConstantBuffer, 0u, 0u, 0u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        var pixelBytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(
                ("DiffuseTexture", (uint)DxReflectedShaderResourceType.Texture, 5u, 4u, 1u, 1u, 0u),
                ("LinearSampler", (uint)DxReflectedShaderResourceType.Sampler, 0u, 0u, 0u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Pixel, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = vertexBytecode
        });
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = pixelBytecode
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader
        });
        using var constants = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 256,
            Usage = DxBufferUsage.Constant
        });
        using var texture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 32,
            Height = 32,
            Usage = DxTextureUsage.ShaderResource
        });
        using var textureView = device.CreateShaderResourceView(texture);
        using var sampler = device.CreateSamplerState(new DxSamplerDescriptor());
        using var context = device.CreateImmediateContext();

        context.SetGraphicsPipeline(pipeline);
        context.SetConstantBuffer(DxShaderStage.Vertex, 0, constants);
        context.SetShaderResource(DxShaderStage.Pixel, 1, textureView);

        var missingSampler = context.ValidateGraphicsPipelineBindings();
        Assert.False(missingSampler.IsValid);
        var issue = Assert.Single(missingSampler.Issues);
        Assert.Equal(ProGpuDirectXBindingValidationIssueKind.MissingBinding, issue.IssueKind);
        Assert.Equal("LinearSampler", issue.ResourceName);
        Assert.Equal(DxShaderStage.Pixel, issue.Stage);
        Assert.Equal(ProGpuDirectXBindingKind.Sampler, issue.Kind);
        Assert.Equal(0u, issue.Slot);
        Assert.Equal(768u, issue.NativeBinding);

        context.Draw(3);
        Assert.NotNull(context.Commands[^1].BindingValidation);
        Assert.False(context.Commands[^1].BindingValidation!.IsValid);

        context.SetSampler(DxShaderStage.Pixel, 0, sampler);

        var valid = context.ValidateGraphicsPipelineBindings();
        Assert.True(valid.IsValid);
        context.Draw(3);
        Assert.NotNull(context.Commands[^1].BindingValidation);
        Assert.True(context.Commands[^1].BindingValidation!.IsValid);
    }

    [Fact]
    public void ValidationEnabledDeviceRejectsDrawWithMissingReflectedBindings()
    {
        var vertexBytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(
                ("FrameConstants", (uint)DxReflectedShaderResourceType.ConstantBuffer, 0u, 0u, 0u, 1u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Vertex, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice(new ProGpuDirectXDeviceOptions
        {
            EnableValidation = true
        });
        using var vertexShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = vertexBytecode
        });
        using var pipeline = device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader
        });
        using var context = device.CreateImmediateContext();

        context.SetGraphicsPipeline(pipeline);

        var exception = Assert.Throws<InvalidOperationException>(() => context.Draw(3));
        Assert.Contains("FrameConstants", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ConstantBuffer", exception.Message, StringComparison.Ordinal);
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
    public void ComputeBindingValidationHonorsReflectedUavArrayCounts()
    {
        var computeBytecode = CreateDxbcBytecode(
            ("RDEF", CreateResourceDefinitionChunk(
                ("OutputValues", (uint)DxReflectedShaderResourceType.UnorderedAccessStructured, 0u, 1u, 0u, 2u, 0u))),
            ("SHEX", CreateProgramChunk(DxShaderProgramKind.Compute, 5, 0)));
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var computeShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Compute,
            SourceKind = DxShaderSourceKind.HlslBytecode,
            Bytecode = computeBytecode
        });
        using var pipeline = device.CreateComputePipeline(new DxComputePipelineDescriptor
        {
            ComputeShader = computeShader
        });
        using var output0 = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 256,
            Usage = DxBufferUsage.Structured | DxBufferUsage.UnorderedAccess,
            StrideInBytes = 16
        });
        using var output1 = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 256,
            Usage = DxBufferUsage.Structured | DxBufferUsage.UnorderedAccess,
            StrideInBytes = 16
        });
        using var outputView0 = device.CreateUnorderedAccessView(
            output0,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 16,
                ElementStrideInBytes = 16
            });
        using var outputView1 = device.CreateUnorderedAccessView(
            output1,
            new DxUnorderedAccessViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                ElementCount = 16,
                ElementStrideInBytes = 16
            });
        using var context = device.CreateImmediateContext();

        context.SetComputePipeline(pipeline);
        context.SetUnorderedAccessView(0, outputView0);

        var missingSecondUav = context.ValidateComputePipelineBindings();
        Assert.False(missingSecondUav.IsValid);
        var issue = Assert.Single(missingSecondUav.Issues);
        Assert.Equal("OutputValues", issue.ResourceName);
        Assert.Equal(DxShaderStage.Compute, issue.Stage);
        Assert.Equal(ProGpuDirectXBindingKind.UnorderedAccessView, issue.Kind);
        Assert.Equal(1u, issue.Slot);
        Assert.Equal(1857u, issue.NativeBinding);

        context.SetUnorderedAccessView(1, outputView1);

        var valid = context.ValidateComputePipelineBindings();
        Assert.True(valid.IsValid);
        context.Dispatch(1, 1, 1);
        Assert.NotNull(context.Commands[^1].BindingValidation);
        Assert.True(context.Commands[^1].BindingValidation!.IsValid);
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
    public void CanCreateRenderTargetAndDepthStencilViewsAndBindThem()
    {
        using var device = ProGpuDirectXDevice.CreateMetadataDevice();
        using var renderTarget = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 64,
            Height = 64,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.ShaderResource | DxTextureUsage.CopySource
        });
        using var depthStencil = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 64,
            Height = 64,
            Format = DxResourceFormat.D32Float,
            Usage = DxTextureUsage.DepthStencil
        });
        using var renderTargetView = device.CreateRenderTargetView(
            renderTarget,
            new DxRenderTargetViewDescriptor
            {
                Format = DxResourceFormat.R8G8B8A8Unorm,
                Label = "SharpDXStyleRtv"
            });
        using var depthStencilView = device.CreateDepthStencilView(
            depthStencil,
            new DxDepthStencilViewDescriptor
            {
                Format = DxResourceFormat.D32Float,
                Label = "SharpDXStyleDsv"
            });
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(renderTargetView, depthStencilView);
        context.ClearRenderTarget(renderTargetView, new DxColor(0.2f, 0.4f, 0.6f, 1f));
        context.ClearDepthStencil(depthStencilView, DxDepthStencilClearFlags.Depth, depth: 1f);

        Assert.Same(renderTargetView, context.RenderTargetView);
        Assert.Same(depthStencilView, context.DepthStencilView);
        Assert.Same(renderTarget, context.RenderTarget);
        Assert.Same(depthStencil, context.DepthStencil);
        Assert.Equal(DxResourceFormat.R8G8B8A8Unorm, renderTargetView.Format);
        Assert.Equal(DxResourceFormat.D32Float, depthStencilView.Format);
        Assert.Equal(ProGpuDirectXCommandKind.SetRenderTargets, context.Commands[0].Kind);
        Assert.Same(renderTargetView, context.Commands[0].RenderTargetView);
        Assert.Same(depthStencilView, context.Commands[0].DepthStencilView);
        Assert.Equal(ProGpuDirectXCommandKind.ClearRenderTarget, context.Commands[1].Kind);
        Assert.Same(renderTargetView, context.Commands[1].RenderTargetView);
        Assert.Equal(ProGpuDirectXCommandKind.ClearDepthStencil, context.Commands[2].Kind);
        Assert.Same(depthStencilView, context.Commands[2].DepthStencilView);

        using var shaderOnlyTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Usage = DxTextureUsage.ShaderResource
        });
        Assert.Throws<ArgumentException>(() => device.CreateRenderTargetView(shaderOnlyTexture));
        Assert.Throws<ArgumentException>(() => device.CreateDepthStencilView(renderTarget));
    }

    [Fact]
    public void GpuBackedRenderTargetAndDepthStencilViewsClearNativeTargets()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderTarget = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read
        });
        using var depthStencil = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.D32Float,
            Usage = DxTextureUsage.DepthStencil
        });
        using var renderTargetView = device.CreateRenderTargetView(renderTarget);
        using var depthStencilView = device.CreateDepthStencilView(depthStencil);
        using var context = device.CreateImmediateContext();

        context.SetRenderTargets(renderTargetView, depthStencilView);
        context.ClearRenderTarget(renderTargetView, new DxColor(1f, 0f, 0f, 1f));
        context.ClearDepthStencil(depthStencilView, DxDepthStencilClearFlags.Depth, depth: 0.5f);
        context.Flush();

        Assert.True(renderTargetView.HasBackendTextureView);
        Assert.True(depthStencilView.HasBackendTextureView);
        Assert.Equal(2ul, context.SubmittedClearCount);

        var pixels = renderTarget.ReadPixels();
        var pixel = ReadRgbaPixel(pixels, 8, 4, 4);
        Assert.True(pixel.R > 200, $"Expected render-target view clear to red, actual: {pixel}");
        Assert.True(pixel.G < 50, $"Expected render-target view clear low green, actual: {pixel}");
        Assert.True(pixel.B < 50, $"Expected render-target view clear low blue, actual: {pixel}");
        Assert.True(pixel.A > 200, $"Expected opaque render-target view clear, actual: {pixel}");
    }

    [Fact]
    public void GpuBackedRenderTargetArrayViewsClearSelectedNativeSlice()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var renderTarget = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource,
            CpuAccess = DxCpuAccessFlags.Read,
            ArraySize = 2
        });
        using var renderTargetView = device.CreateRenderTargetView(
            renderTarget,
            new DxRenderTargetViewDescriptor
            {
                Dimension = DxResourceViewDimension.Texture2DArray,
                FirstArraySlice = 1,
                ArraySize = 1,
                Label = "ArraySlice1Rtv"
            });
        using var context = device.CreateImmediateContext();

        context.ClearRenderTarget(renderTargetView, new DxColor(0f, 0f, 1f, 1f));
        context.Flush();

        Assert.True(renderTargetView.HasBackendTextureView);
        Assert.Equal(1ul, context.SubmittedClearCount);

        var pixels = renderTarget.ReadPixels();
        var sliceSize = checked((int)(renderTarget.Width * renderTarget.Height * 4));
        var slice0Center = ReadRgbaPixel(pixels.AsSpan(0, sliceSize).ToArray(), 8, 4, 4);
        var slice1Center = ReadRgbaPixel(pixels.AsSpan(sliceSize, sliceSize).ToArray(), 8, 4, 4);
        Assert.True(slice0Center.B < 50, $"Expected layer 0 to stay non-blue, actual: {slice0Center}");
        Assert.True(slice1Center.R < 50, $"Expected low red array clear pixel, actual: {slice1Center}");
        Assert.True(slice1Center.G < 50, $"Expected low green array clear pixel, actual: {slice1Center}");
        Assert.True(slice1Center.B > 200, $"Expected blue array clear pixel, actual: {slice1Center}");
        Assert.True(slice1Center.A > 200, $"Expected opaque array clear pixel, actual: {slice1Center}");
    }

    [Fact]
    public void GpuBackedTextureViewsValidateDescriptorsBeforeNativeViewCreation()
    {
        using var wgpu = new WgpuContext();
        wgpu.Initialize(null);
        using var device = ProGpuDirectXDevice.FromContext(wgpu);
        using var shaderResource = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.ShaderResource,
            ArraySize = 1
        });
        using var unorderedAccess = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.UnorderedAccess,
            ArraySize = 1
        });
        using var renderTarget = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget,
            ArraySize = 1
        });
        using var depthStencil = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.D32Float,
            Usage = DxTextureUsage.DepthStencil,
            ArraySize = 1
        });
        using var multisampledArray = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 8,
            Height = 8,
            Format = DxResourceFormat.R8G8B8A8Unorm,
            Usage = DxTextureUsage.RenderTarget,
            SampleCount = 4,
            ArraySize = 2
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateShaderResourceView(
                shaderResource,
                new DxShaderResourceViewDescriptor
                {
                    Dimension = DxResourceViewDimension.Texture2DArray,
                    ArraySize = 2
                }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateUnorderedAccessView(
                unorderedAccess,
                new DxUnorderedAccessViewDescriptor
                {
                    Dimension = DxResourceViewDimension.Texture2DArray,
                    ArraySize = 2
                }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateRenderTargetView(
                renderTarget,
                new DxRenderTargetViewDescriptor
                {
                    ArraySize = 2
                }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateDepthStencilView(
                depthStencil,
                new DxDepthStencilViewDescriptor
                {
                    ArraySize = 2
                }));
        Assert.Throws<NotSupportedException>(() =>
            device.CreateRenderTargetView(
                multisampledArray,
                new DxRenderTargetViewDescriptor
                {
                    Dimension = DxResourceViewDimension.Texture2DArray,
                    ArraySize = 2
                }));
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
        using var pixelShader = device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
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
        Assert.Throws<ArgumentException>(() =>
            device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
            {
                VertexShader = vertexShader,
                PixelShader = pixelShader,
                RenderTargetFormat = DxResourceFormat.Unknown
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

        using var shaderOnlyTexture = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = 16,
            Height = 16,
            Usage = DxTextureUsage.ShaderResource
        });
        Assert.Throws<ArgumentException>(() => context.SetRenderTargets(shaderOnlyTexture));
        Assert.Throws<ArgumentException>(() => context.ClearRenderTarget(shaderOnlyTexture, DxColor.Black));

        using var vertexBuffer = device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = 128,
            Usage = DxBufferUsage.Vertex
        });
        Assert.Throws<ArgumentException>(() =>
            context.SetConstantBuffer(DxShaderStage.Vertex, 0, vertexBuffer));
    }

    private static void AssertBindingRequirement(
        IReadOnlyList<DxReflectedShaderBindingRequirement> requirements,
        string name,
        ProGpuDirectXBindingKind kind,
        uint slot,
        uint nativeBinding,
        DxShaderStage stage = DxShaderStage.Pixel)
    {
        var requirement = Assert.Single(requirements, requirement => requirement.Name == name);
        Assert.Equal(kind, requirement.Kind);
        Assert.Equal(stage, requirement.Stage);
        Assert.Equal(slot, requirement.Slot);
        Assert.Equal(1u, requirement.Count);
        Assert.Equal(nativeBinding, requirement.NativeBinding);
    }

    private static byte[] CreateDxbcBytecode(params (string FourCC, byte[] Data)[] chunks)
    {
        var headerSize = checked(32 + chunks.Length * 4);
        var bytes = Enumerable.Repeat((byte)0, headerSize).ToList();
        WriteAscii(bytes, 0, "DXBC");
        WriteUInt32(bytes, 20, 1);
        WriteUInt32(bytes, 28, checked((uint)chunks.Length));

        for (var i = 0; i < chunks.Length; i++)
        {
            Align4(bytes);
            var chunkOffset = checked((uint)bytes.Count);
            WriteUInt32(bytes, 32 + i * 4, chunkOffset);
            AppendAscii(bytes, chunks[i].FourCC);
            AppendUInt32(bytes, checked((uint)chunks[i].Data.Length));
            bytes.AddRange(chunks[i].Data);
        }

        WriteUInt32(bytes, 24, checked((uint)bytes.Count));
        return bytes.ToArray();
    }

    private static byte[] CreateProgramChunk(DxShaderProgramKind kind, uint major, uint minor)
    {
        var bytes = new List<byte>();
        var programKind = kind switch
        {
            DxShaderProgramKind.Pixel => 0u,
            DxShaderProgramKind.Vertex => 1u,
            DxShaderProgramKind.Geometry => 2u,
            DxShaderProgramKind.Hull => 3u,
            DxShaderProgramKind.Domain => 4u,
            DxShaderProgramKind.Compute => 5u,
            _ => 0xFFFFu
        };
        AppendUInt32(bytes, (programKind << 16) | ((major & 0xFu) << 4) | (minor & 0xFu));
        return bytes.ToArray();
    }

    private static byte[] CreateSignatureChunk(
        params (string SemanticName, uint SemanticIndex, uint SystemValueType, uint ComponentType, uint Register, uint Mask, uint ReadWriteMask)[] parameters)
    {
        const int headerSize = 8;
        const int entrySize = 24;
        var bytes = Enumerable.Repeat((byte)0, headerSize + parameters.Length * entrySize).ToList();
        WriteUInt32(bytes, 0, checked((uint)parameters.Length));
        WriteUInt32(bytes, 4, headerSize);

        for (var i = 0; i < parameters.Length; i++)
        {
            var entryOffset = headerSize + i * entrySize;
            var nameOffset = checked((uint)bytes.Count);
            AppendNullTerminatedAscii(bytes, parameters[i].SemanticName);
            WriteUInt32(bytes, entryOffset, nameOffset);
            WriteUInt32(bytes, entryOffset + 4, parameters[i].SemanticIndex);
            WriteUInt32(bytes, entryOffset + 8, parameters[i].SystemValueType);
            WriteUInt32(bytes, entryOffset + 12, parameters[i].ComponentType);
            WriteUInt32(bytes, entryOffset + 16, parameters[i].Register);
            bytes[entryOffset + 20] = checked((byte)parameters[i].Mask);
            bytes[entryOffset + 21] = checked((byte)parameters[i].ReadWriteMask);
        }

        return bytes.ToArray();
    }

    private static byte[] CreateResourceDefinitionChunk(
        params (string Name, uint Type, uint ReturnType, uint Dimension, uint BindPoint, uint BindCount, uint Flags)[] resources)
    {
        const int headerSize = 28;
        const int entrySize = 32;
        var bytes = Enumerable.Repeat((byte)0, headerSize + resources.Length * entrySize).ToList();
        WriteUInt32(bytes, 8, checked((uint)resources.Length));
        WriteUInt32(bytes, 12, headerSize);

        for (var i = 0; i < resources.Length; i++)
        {
            var entryOffset = headerSize + i * entrySize;
            var nameOffset = checked((uint)bytes.Count);
            AppendNullTerminatedAscii(bytes, resources[i].Name);
            WriteUInt32(bytes, entryOffset, nameOffset);
            WriteUInt32(bytes, entryOffset + 4, resources[i].Type);
            WriteUInt32(bytes, entryOffset + 8, resources[i].ReturnType);
            WriteUInt32(bytes, entryOffset + 12, resources[i].Dimension);
            WriteUInt32(bytes, entryOffset + 20, resources[i].BindPoint);
            WriteUInt32(bytes, entryOffset + 24, resources[i].BindCount);
            WriteUInt32(bytes, entryOffset + 28, resources[i].Flags);
        }

        return bytes.ToArray();
    }

    private static void AppendUInt32(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void WriteUInt32(List<byte> bytes, int offset, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        for (var i = 0; i < buffer.Length; i++)
        {
            bytes[offset + i] = buffer[i];
        }
    }

    private static void AppendAscii(List<byte> bytes, string value)
    {
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(value));
    }

    private static void WriteAscii(List<byte> bytes, int offset, string value)
    {
        var ascii = System.Text.Encoding.ASCII.GetBytes(value);
        for (var i = 0; i < ascii.Length; i++)
        {
            bytes[offset + i] = ascii[i];
        }
    }

    private static void AppendNullTerminatedAscii(List<byte> bytes, string value)
    {
        AppendAscii(bytes, value);
        bytes.Add(0);
    }

    private static void Align4(List<byte> bytes)
    {
        while ((bytes.Count & 3) != 0)
        {
            bytes.Add(0);
        }
    }

    private static byte[] CreateGeneratedMipSourcePixels(int width = 4, int height = 4, int mipLevels = 3)
    {
        var size = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var mip = 0; mip < mipLevels; mip++)
        {
            size += mipWidth * mipHeight * 4;
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        var pixels = new byte[size];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset] = checked((byte)(x * 40 + y * 8));
                pixels[offset + 1] = checked((byte)(x * 10));
                pixels[offset + 2] = checked((byte)(y * 10));
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static void AssertGreenPixel(byte[] pixels, int width, int x, int y, string region)
    {
        var pixel = ReadRgbaPixel(pixels, width, x, y);
        Assert.True(pixel.R < 50, $"Expected low red pixel in {region}, actual: {pixel}");
        Assert.True(pixel.G > 200, $"Expected green pixel in {region}, actual: {pixel}");
        Assert.True(pixel.B < 50, $"Expected low blue pixel in {region}, actual: {pixel}");
        Assert.True(pixel.A > 200, $"Expected opaque pixel in {region}, actual: {pixel}");
    }

    private static void AssertBlackPixel(byte[] pixels, int width, int x, int y, string region)
    {
        var pixel = ReadRgbaPixel(pixels, width, x, y);
        Assert.True(pixel.R < 50, $"Expected low red pixel in {region}, actual: {pixel}");
        Assert.True(pixel.G < 50, $"Expected low green pixel in {region}, actual: {pixel}");
        Assert.True(pixel.B < 50, $"Expected low blue pixel in {region}, actual: {pixel}");
        Assert.True(pixel.A > 200, $"Expected opaque pixel in {region}, actual: {pixel}");
    }

    private static void AssertContainsGreenPixel(byte[] pixels, int width, string region)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var pixel = (R: pixels[offset], G: pixels[offset + 1], B: pixels[offset + 2], A: pixels[offset + 3]);
            if (pixel.R < 80 && pixel.G > 80 && pixel.B < 80 && pixel.A > 0)
            {
                return;
            }
        }

        Assert.Fail($"Expected at least one green pixel in {region} ({width}px-wide RGBA buffer).");
    }

    private static void AssertContainsGreenPixelInRegion(
        byte[] pixels,
        int width,
        int x,
        int y,
        int regionWidth,
        int regionHeight,
        string region)
    {
        for (var row = y; row < y + regionHeight; row++)
        {
            for (var column = x; column < x + regionWidth; column++)
            {
                var pixel = ReadRgbaPixel(pixels, width, column, row);
                if (pixel.R < 80 && pixel.G > 80 && pixel.B < 80 && pixel.A > 0)
                {
                    return;
                }
            }
        }

        Assert.Fail($"Expected at least one green pixel in {region}.");
    }

    private static void AssertNoGreenPixelInRegion(
        byte[] pixels,
        int width,
        int x,
        int y,
        int regionWidth,
        int regionHeight,
        string region)
    {
        for (var row = y; row < y + regionHeight; row++)
        {
            for (var column = x; column < x + regionWidth; column++)
            {
                var pixel = ReadRgbaPixel(pixels, width, column, row);
                if (pixel.R < 80 && pixel.G > 80 && pixel.B < 80 && pixel.A > 0)
                {
                    Assert.Fail($"Expected no green pixels in {region}, found {pixel} at ({column}, {row}).");
                }
            }
        }
    }

    private static ProGpuDirectXSciChartFont? TryCreateSciChartFont(ProGpuDirectXSciChartRenderContext2D renderContext)
    {
        try
        {
            return renderContext.CreateDefaultFont();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static (byte R, byte G, byte B, byte A) ReadRgbaPixel(byte[] pixels, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }
}
