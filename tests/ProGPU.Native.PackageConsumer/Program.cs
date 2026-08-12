using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

NativeRendererInfo info = NativeCompositor.GetInfo();
if (info.AbiVersion != 3 ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.ExternalImageMask) ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.ExplicitQueueTimeline))
{
    throw new InvalidOperationException("The packaged native ABI is incomplete.");
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
