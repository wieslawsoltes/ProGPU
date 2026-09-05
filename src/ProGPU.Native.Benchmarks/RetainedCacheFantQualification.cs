using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedCacheFantQualification
{
    private const uint Width = 64U;
    private const uint Height = 48U;
    private const ulong SceneId = 0x4341434846414E54UL;
    private const ulong ContentRevision = 1U;
    private const ulong CompositeRevision = 7004U;

    public static void Run()
    {
        using var context = new WgpuContext();
        context.Initialize(window: null);
        using var target = new GpuTexture(
            context,
            Width,
            Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Native retained cache Fant qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        byte[] linearScene = BuildScene(generation: 1U, fant: false);
        NativeSceneUpdateMetrics linearUpdate = compositor.UpdateScene(linearScene);
        NativeSceneFrameMetrics linearFrame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation: 1U,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] linearPixels = target.ReadPixels();
        NativeLayerMetrics linearLayer = compositor.GetLayerMetrics();
        Require(
            linearUpdate.ValidationError == NativeSceneValidationError.None &&
            linearFrame.SubmissionCount > 0U &&
            linearLayer.ContentPassCount == 1U &&
            linearLayer.CompositePassCount == 1U,
            $"initial linear cache metrics are invalid: update={linearUpdate}, " +
            $"frame={linearFrame}, layer={linearLayer}");

        byte[] fantScene = BuildScene(generation: 2U, fant: true);
        NativeSceneUpdateMetrics fantUpdate = compositor.UpdateScene(fantScene);
        NativeSceneFrameMetrics fantFrame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation: 2U,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] fantPixels = target.ReadPixels();
        NativeLayerMetrics fantLayer = compositor.GetLayerMetrics();
        Require(
            fantUpdate.ValidationError == NativeSceneValidationError.None &&
            fantFrame.SubmissionCount > 0U &&
            fantLayer.ContentPassCount == 0U &&
            fantLayer.CompositePassCount == 1U,
            $"Fant-only cache replay metrics are invalid: update={fantUpdate}, " +
            $"frame={fantFrame}, layer={fantLayer}");

        (int linearMinimum, int linearMaximum, int linearMean) =
            MeasureInterior(linearPixels);
        (int fantMinimum, int fantMaximum, int fantMean) =
            MeasureInterior(fantPixels);
        int outside = Red(fantPixels, 4, 4);
        Require(
            fantMean is >= 112 and <= 143 &&
            fantMaximum - fantMinimum <= 128 &&
            (fantMaximum - fantMinimum) * 2 <
                linearMaximum - linearMinimum &&
            outside == 0,
            "the live retained cache Fant path did not suppress the striped " +
            $"minification alias: linear={linearMinimum}/{linearMean}/" +
            $"{linearMaximum}, Fant={fantMinimum}/{fantMean}/{fantMaximum}, " +
            $"outside={outside}.");

        Console.WriteLine(
            "Qualified live local retained cache Fant minification on " +
            $"adapter '{context.AdapterName}', backend={context.AdapterBackendType}; " +
            $"passes={linearLayer.ContentPassCount}/" +
            $"{linearLayer.CompositePassCount}->{fantLayer.ContentPassCount}/" +
            $"{fantLayer.CompositePassCount}, red min/mean/max=" +
            $"{linearMinimum}/{linearMean}/{linearMaximum}->" +
            $"{fantMinimum}/{fantMean}/{fantMaximum}.");
    }

    private static byte[] BuildScene(ulong generation, bool fant)
    {
        NativeAnalyticPrimitive[] stripes = new NativeAnalyticPrimitive[32];
        for (int index = 0; index < stripes.Length; ++index)
        {
            float channel = (index & 1) == 0 ? 0f : 1f;
            stripes[index] = new NativeAnalyticPrimitive(
                NativeAnalyticPrimitiveKind.Rectangle,
                index,
                0f,
                1f,
                16f,
                new Vector4(channel, channel, channel, 1f),
                Matrix3x2.Identity);
        }

        int arenaCapacity = stripes.Length * 96 + 512;
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 3,
            resourceCapacity: 2,
            arenaCapacity);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation,
            commandCapacity: 3,
            resourceCapacity: 2);
        var compositeState = new NativeSceneState(
            new Matrix3x2(0.3f, 0f, 0f, 1f, 12f, 8f));
        NativeSceneLayerFlags flags = NativeSceneLayerFlags.Bounds |
            NativeSceneLayerFlags.CacheContent |
            NativeSceneLayerFlags.CacheLocalSpace;
        if (fant)
            flags |= NativeSceneLayerFlags.CacheFant;

        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddStateResource(
                resourceId: 1U,
                generation,
                in compositeState,
                out uint compositeStateIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 2U,
                generation: ContentRevision,
                stripes,
                out uint analyticResourceIndex) &&
            builder.TryPushLayer(
                commandId: 1U,
                new NativeSceneLayer(
                    flags: flags,
                    bounds: new NativeImageRect(0f, 0f, 32f, 16f),
                    contentRevision: ContentRevision,
                    compositeRevision: CompositeRevision,
                    compositeStateResourceIndex: compositeStateIndex)) &&
            builder.TryDrawAnalytic(
                commandId: 2U,
                analyticResourceIndex,
                new NativeImageRect(0f, 0f, 32f, 16f)) &&
            builder.TryPopLayer(commandId: 3U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the retained cache Fant scene");
        return stream.ToArray();
    }

    private static (int Minimum, int Maximum, int Mean) MeasureInterior(
        byte[] pixels)
    {
        int minimum = 255;
        int maximum = 0;
        int sum = 0;
        int count = 0;
        for (int y = 10; y < 22; ++y)
        {
            for (int x = 13; x < 21; ++x)
            {
                int value = Red(pixels, x, y);
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                sum += value;
                ++count;
            }
        }
        return (minimum, maximum, sum / count);
    }

    private static int Red(byte[] pixels, int x, int y) =>
        pixels[(y * checked((int)Width) + x) * 4];

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
