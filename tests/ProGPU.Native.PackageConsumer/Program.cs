using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

if (args.Contains("--webgpu-init-only", StringComparer.Ordinal))
{
    Console.WriteLine(
        $"package-consumer: WebGPU init " +
        $"arch={RuntimeInformation.ProcessArchitecture}, " +
        $"temp={Path.GetTempPath()}");
    using var probeContext = new WgpuContext();
    Console.WriteLine("package-consumer: WebGPU context constructed");
    probeContext.Initialize(window: null);
    Console.WriteLine("ProGPU.Backend WebGPU initialization smoke passed.");
    return;
}

Console.WriteLine("package-consumer: native ABI");
NativeRendererInfo info = NativeCompositor.GetInfo();
if (info.AbiVersion != 3 ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.ExternalImageMask) ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.ExplicitQueueTimeline) ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.WpfMilChannel))
{
    throw new InvalidOperationException("The packaged native ABI is incomplete.");
}

bool milOnly = args.Contains("--mil-only", StringComparer.Ordinal);
bool renderOnly = args.Contains("--render-only", StringComparer.Ordinal);
if (!renderOnly)
{
    byte[] milBatch = CreateMilSeedBatch();
    using (var mil = new NativeMilChannel())
    {
        NativeMilBatchMetrics milMetrics = mil.Apply(milBatch);
        NativeMilCompiledScene scene = mil.CompileScene(42, 701, 1);
        if (milMetrics.CommandCount != 28 || mil.ResourceCount != 11 ||
            !mil.TryGetVisual(41, out NativeMilVisualSnapshot visual) ||
            visual.Handle != 41 || scene.Stream.Length == 0 ||
            scene.Metrics.VisualCount != 1 ||
            scene.Metrics.RectangleCount != 1 ||
            scene.Metrics.EllipseCount != 2 ||
            scene.Metrics.RoundedRectangleCount != 2 ||
            scene.Metrics.LineCount != 2 ||
            scene.Metrics.BrushCount != 1)
        {
            throw new InvalidOperationException(
                "The packaged wgpu-native MIL channel is incomplete.");
        }
    }
    Console.WriteLine("package-consumer: wgpu-native MIL");
    using (var dawnMil = new NativeMilChannel(NativeMilBackend.Dawn))
    {
        NativeMilBatchMetrics milMetrics = dawnMil.Apply(milBatch);
        NativeMilCompiledScene scene = dawnMil.CompileScene(42, 702, 1);
        if (milMetrics.CommandCount != 28 || dawnMil.ResourceCount != 11 ||
            scene.Stream.Length == 0 || scene.Metrics.VisualCount != 1 ||
            scene.Metrics.RectangleCount != 1 ||
            scene.Metrics.EllipseCount != 2 ||
            scene.Metrics.RoundedRectangleCount != 2 ||
            scene.Metrics.LineCount != 2 ||
            scene.Metrics.BrushCount != 1)
        {
            throw new InvalidOperationException(
                "The packaged Dawn MIL channel is incomplete.");
        }
    }
    Console.WriteLine("package-consumer: Dawn MIL");
}

NativeRendererInfo dawnInfo = NativeDawnAdapter.GetInfo();
if (dawnInfo.AbiVersion != 3 ||
    dawnInfo.BackendAbi != NativeDawnAdapter.BackendAbi ||
    NativeDawnAdapter.AdapterAbiVersion != 1 ||
    NativeDawnAdapter.RequiredProviderAbiVersion != 2 ||
    !dawnInfo.Name.Contains("Dawn provider", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "The packaged provider-resolved Dawn adapter is incomplete.");
}
Console.WriteLine("package-consumer: Dawn ABI");
if (milOnly)
{
    Console.WriteLine("ProGPU.Backend.Native MIL-only package smoke passed.");
    return;
}

using var context = new WgpuContext();
context.Initialize(window: null);
Console.WriteLine("package-consumer: WebGPU context");
using var target = new GpuTexture(
    context,
    64,
    64,
    TextureFormat.Rgba8Unorm,
    TextureUsage.RenderAttachment | TextureUsage.CopySrc,
    "Native package consumer target",
    alphaMode: GpuTextureAlphaMode.Premultiplied);
using var compositor = new NativeCompositor(context, TextureFormat.Rgba8Unorm);
NativeFrameMetrics metrics = compositor.Render(
    target,
    1f,
    [new NativeSolidRectangle(8, 8, 48, 48, new Vector4(1f, 0.25f, 0.1f, 1f))],
    new Vector4(0f, 0f, 0f, 1f));
Console.WriteLine("package-consumer: native render");
NativeSubmissionToken submission = compositor.GetLastSubmissionToken();
if (!submission.IsValid)
{
    throw new InvalidOperationException("The packaged renderer did not publish a submission token.");
}
compositor.WaitForSubmission(submission);
if (!compositor.IsSubmissionComplete(submission))
{
    throw new InvalidOperationException("The packaged renderer submission did not remain complete.");
}
using (var other = new NativeCompositor(context, TextureFormat.Rgba8Unorm))
{
    try
    {
        _ = other.IsSubmissionComplete(submission);
        throw new InvalidOperationException(
            "A submission token crossed compositor ownership domains.");
    }
    catch (ArgumentException)
    {
    }
}
byte[] pixels = target.ReadPixels();
if (metrics.DrawCallCount != 1 || pixels.All(static value => value == 0))
{
    throw new InvalidOperationException("The packaged native renderer did not draw.");
}

Console.WriteLine(
    $"ProGPU.Backend.Native package smoke passed: ABI {info.AbiVersion}, " +
    $"Dawn ABI {NativeDawnAdapter.AdapterAbiVersion}, " +
    $"draws={metrics.DrawCallCount}, pixels={pixels.Length}.");

static byte[] CreateMilSeedBatch()
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.PushTransform(45);
    renderData.DrawRectangle(8, 8, 48, 48, 44, 46);
    renderData.DrawLine(8, 8, 56, 56, 46);
    renderData.DrawEllipse(32, 32, 16, 12, 44, 48);
    renderData.DrawRoundedRectangle(12, 16, 40, 32, 8, 8, 0, 48);
    renderData.DrawGeometry(0, 48, 49);
    renderData.DrawGeometry(44, 48, 50);
    renderData.DrawGeometry(44, 48, 51);
    renderData.Pop();
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(41, NativeMilResourceType.Visual);
    batch.CreateResource(42, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(43, NativeMilResourceType.RenderData);
    batch.CreateResource(44, NativeMilResourceType.SolidColorBrush);
    batch.CreateResource(45, NativeMilResourceType.MatrixTransform);
    batch.CreateResource(46, NativeMilResourceType.Pen);
    batch.CreateResource(47, NativeMilResourceType.DashStyle);
    batch.CreateResource(48, NativeMilResourceType.Pen);
    batch.CreateResource(49, NativeMilResourceType.LineGeometry);
    batch.CreateResource(50, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(51, NativeMilResourceType.EllipseGeometry);
    batch.CreateVisual(41);
    batch.SetVisualOffset(41, 1, 2);
    batch.SetMatrixTransform(
        45,
        new NativeMilMatrix3x2(1, 0, 0, 1, 1, 1));
    batch.SetVisualTransform(41, 45);
    batch.SetVisualOpacity(41, 0.9);
    batch.SetVisualContent(41, 43);
    batch.SetSolidColorBrush(44, new NativeMilColor(1, 0.25f, 0.1f, 1));
    batch.SetDashStyle(47, 0.5, [2.0, 1.0]);
    batch.SetPen(
        46,
        new NativeMilPen(
            44,
            2,
            NativeMilPenLineCap.Square,
            NativeMilPenLineCap.Round,
            DashStyleHandle: 47));
    batch.SetPen(48, new NativeMilPen(44, 2));
    batch.SetLineGeometry(49, 8, 56, 56, 8, 45);
    batch.SetRectangleGeometry(50, 12, 16, 40, 32, 8, 8, 45);
    batch.SetEllipseGeometry(51, 32, 32, 16, 12, 45);
    batch.SetRenderData(43, renderData);
    batch.CreateGenericTarget(42, 64, 64);
    batch.SetTargetClearColor(42, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(42, 41);
    return batch.ToArray();
}
