using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedCacheMaskQualification
{
    private const uint Width = 64U;
    private const uint Height = 48U;
    private const ulong SceneId = 0x434143484D41534BUL;
    private const ulong ContentRevision = 1U;
    private const ulong CompositeRevision = 7003U;

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
            "Native retained cache brush-mask qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        byte[] firstScene = BuildScene(generation: 1U, maskOpacity: 1f);
        NativeSceneUpdateMetrics firstUpdate = compositor.UpdateScene(firstScene);
        NativeSceneFrameMetrics firstFrame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation: 1U,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] firstPixels = target.ReadPixels();
        NativeLayerMetrics firstLayer = compositor.GetLayerMetrics();
        Require(
            firstUpdate.ValidationError == NativeSceneValidationError.None &&
            firstFrame.SubmissionCount > 0U &&
            firstLayer.MaskKind == NativeGroupMaskKind.Texture &&
            firstLayer.ContentPassCount == 1U &&
            firstLayer.CompositePassCount == 1U,
            $"initial retained cache brush-mask metrics are invalid: " +
            $"update={firstUpdate}, frame={firstFrame}, layer={firstLayer}");

        byte[] changedScene = BuildScene(generation: 2U, maskOpacity: 0.5f);
        NativeSceneUpdateMetrics changedUpdate = compositor.UpdateScene(changedScene);
        NativeSceneFrameMetrics changedFrame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation: 2U,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] changedPixels = target.ReadPixels();
        NativeLayerMetrics changedLayer = compositor.GetLayerMetrics();
        Require(
            changedUpdate.ValidationError == NativeSceneValidationError.None &&
            changedFrame.SubmissionCount > 0U &&
            changedLayer.MaskKind == NativeGroupMaskKind.Texture &&
            changedLayer.ContentPassCount == 0U &&
            changedLayer.CompositePassCount == 1U,
            $"mask-only retained cache replay metrics are invalid: " +
            $"update={changedUpdate}, frame={changedFrame}, layer={changedLayer}");

        int firstLeft = Green(firstPixels, 14, 16);
        int firstRight = Green(firstPixels, 34, 16);
        int changedRight = Green(changedPixels, 34, 16);
        int outside = Green(changedPixels, 4, 4);
        Require(
            firstRight > firstLeft + 80 &&
            changedRight > 0 &&
            Math.Abs(changedRight * 2 - firstRight) <= 4 &&
            outside == 0,
            "the live retained cache brush mask did not produce the expected " +
            $"gradient/composite-only pixels: first={firstLeft}/{firstRight}, " +
            $"changed={changedRight}, outside={outside}.");

        Console.WriteLine(
            "Qualified live local retained cache linear brush masking on " +
            $"adapter '{context.AdapterName}', backend={context.AdapterBackendType}; " +
            $"first passes={firstLayer.ContentPassCount}/" +
            $"{firstLayer.CompositePassCount}, changed passes=" +
            $"{changedLayer.ContentPassCount}/{changedLayer.CompositePassCount}, " +
            $"green={firstLeft}/{firstRight}->{changedRight}.");
    }

    private static byte[] BuildScene(ulong generation, float maskOpacity)
    {
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 3,
            resourceCapacity: 3,
            arenaCapacity: 1024);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation,
            commandCapacity: 3,
            resourceCapacity: 3);

        var compositeState = new NativeSceneState(
            Matrix3x2.CreateTranslation(12f, 8f));
        NativeSceneGradientStop[] stops =
        [
            new NativeSceneGradientStop(new Vector4(1f, 1f, 1f, 0f), 0f),
            new NativeSceneGradientStop(new Vector4(1f, 1f, 1f, 1f), 1f)
        ];
        NativeSceneBrush brush = NativeSceneBrush.LinearGradient(
            Vector2.Zero,
            new Vector2(24f, 0f),
            stopOffset: 0U,
            stops,
            opacity: maskOpacity,
            coordinateTransform: Matrix3x2.CreateTranslation(-12f, -8f));
        var mask = new NativeSceneLayerBrushMask(
            new NativeImageRect(0f, 0f, 24f, 18f),
            Matrix3x2.CreateTranslation(12f, 8f),
            in brush,
            gradientStopCount: (uint)stops.Length);
        NativeAnalyticPrimitive[] primitives =
        [
            new NativeAnalyticPrimitive(
                NativeAnalyticPrimitiveKind.Rectangle,
                0f,
                0f,
                24f,
                18f,
                new Vector4(0.2f, 1f, 0.3f, 1f),
                Matrix3x2.Identity)
        ];

        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddStateResource(
                resourceId: 1U,
                generation,
                in compositeState,
                out uint compositeStateIndex) &&
            builder.TryAddLayerBrushMaskResource(
                resourceId: 2U,
                generation,
                in mask,
                stops,
                out uint maskResourceIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 3U,
                generation,
                primitives,
                out uint analyticResourceIndex) &&
            builder.TryPushLayer(
                commandId: 1U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.CacheLocalSpace,
                    bounds: new NativeImageRect(0f, 0f, 24f, 18f),
                    maskResourceIndex: maskResourceIndex,
                    contentRevision: ContentRevision,
                    compositeRevision: CompositeRevision,
                    compositeStateResourceIndex: compositeStateIndex)) &&
            builder.TryDrawAnalytic(
                commandId: 2U,
                analyticResourceIndex,
                new NativeImageRect(0f, 0f, 24f, 18f)) &&
            builder.TryPopLayer(commandId: 3U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the retained cache brush-mask scene");
        return stream.ToArray();
    }

    private static int Green(byte[] pixels, int x, int y) =>
        pixels[(y * checked((int)Width) + x) * 4 + 1];

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
