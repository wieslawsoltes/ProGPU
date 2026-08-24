using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

NativeRendererInfo info = NativeCompositor.GetInfo();
if (info.AbiVersion != 3 ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.ExternalImageMask) ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.ExplicitQueueTimeline) ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.WpfMilChannel))
{
    throw new InvalidOperationException("The packaged native ABI is incomplete.");
}

byte[] milBatch = CreateMilSeedBatch();
using (var mil = new NativeMilChannel())
{
    NativeMilBatchMetrics milMetrics = mil.Apply(milBatch);
    NativeMilCompiledScene scene = mil.CompileScene(42, 701, 1);
    if (milMetrics.CommandCount != 22 || mil.ResourceCount != 8 ||
        !mil.TryGetVisual(41, out NativeMilVisualSnapshot visual) ||
        visual.Handle != 41 || scene.Stream.Length == 0 ||
        scene.Metrics.VisualCount != 1 ||
        scene.Metrics.RectangleCount != 1 ||
        scene.Metrics.EllipseCount != 1 ||
        scene.Metrics.LineCount != 1 ||
        scene.Metrics.BrushCount != 1)
    {
        throw new InvalidOperationException(
            "The packaged wgpu-native MIL channel is incomplete.");
    }
}
using (var dawnMil = new NativeMilChannel(NativeMilBackend.Dawn))
{
    NativeMilBatchMetrics milMetrics = dawnMil.Apply(milBatch);
    NativeMilCompiledScene scene = dawnMil.CompileScene(42, 702, 1);
    if (milMetrics.CommandCount != 22 || dawnMil.ResourceCount != 8 ||
        scene.Stream.Length == 0 || scene.Metrics.VisualCount != 1 ||
        scene.Metrics.RectangleCount != 1 ||
        scene.Metrics.EllipseCount != 1 ||
        scene.Metrics.LineCount != 1 ||
        scene.Metrics.BrushCount != 1)
    {
        throw new InvalidOperationException(
            "The packaged Dawn MIL channel is incomplete.");
    }
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

using var context = new WgpuContext();
context.Initialize(window: null);
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
    batch.SetRenderData(43, renderData);
    batch.CreateGenericTarget(42, 64, 64);
    batch.SetTargetClearColor(42, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(42, 41);
    return batch.ToArray();
}
