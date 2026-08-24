using System.Numerics;
using System.Buffers.Binary;
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
    if (milMetrics.CommandCount != 5 || mil.ResourceCount != 2 ||
        !mil.TryGetVisual(41, out NativeMilVisualSnapshot visual) ||
        visual.Handle != 41 || scene.Stream.Length == 0 ||
        scene.Metrics.VisualCount != 1)
    {
        throw new InvalidOperationException(
            "The packaged wgpu-native MIL channel is incomplete.");
    }
}
using (var dawnMil = new NativeMilChannel(NativeMilBackend.Dawn))
{
    NativeMilBatchMetrics milMetrics = dawnMil.Apply(milBatch);
    NativeMilCompiledScene scene = dawnMil.CompileScene(42, 702, 1);
    if (milMetrics.CommandCount != 5 || dawnMil.ResourceCount != 2 ||
        scene.Stream.Length == 0 || scene.Metrics.VisualCount != 1)
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
    byte[] batch = new byte[100];
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(0, 4), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(4, 4), 0x07);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(8, 4), 41);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(12, 4), 39);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(16, 4), 12);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(20, 4), 0x1a);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(24, 4), 41);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(28, 4), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(32, 4), 0x07);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(36, 4), 42);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(40, 4), 47);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(44, 4), 40);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(48, 4), 0x34);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(52, 4), 42);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(72, 4), 64);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(76, 4), 64);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(84, 4), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(88, 4), 0x35);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(92, 4), 42);
    BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(96, 4), 41);
    return batch;
}
