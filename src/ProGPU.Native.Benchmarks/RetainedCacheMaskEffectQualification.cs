using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedCacheMaskEffectQualification
{
    private const uint Width = 48U;
    private const uint Height = 32U;
    private const ulong SceneId = 0x434143484D465831UL;
    private const ulong CacheIdentity = 7201U;

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
            "Native retained cache mask-before-effect qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        FrameResult first = Render(
            compositor, context, target, generation: 1U, maskOpacity: 1f);
        FrameResult stable = Render(
            compositor, context, target, generation: 2U, maskOpacity: 1f);
        FrameResult changed = Render(
            compositor, context, target, generation: 3U, maskOpacity: 0.5f);

        Require(
            first.Update.ValidationError == NativeSceneValidationError.None &&
            stable.Update.ValidationError == NativeSceneValidationError.None &&
            changed.Update.ValidationError == NativeSceneValidationError.None &&
            first.Frame.SubmissionCount > 0U &&
            stable.Frame.SubmissionCount > 0U &&
            changed.Frame.SubmissionCount > 0U &&
            first.Layer.ContentPassCount == 2U &&
            stable.Layer.ContentPassCount == 1U &&
            changed.Layer.ContentPassCount == 1U &&
            first.Layer.EffectPassCount == 2U &&
            stable.Layer.EffectPassCount == 2U &&
            changed.Layer.EffectPassCount == 2U,
            "retained cache mask/effect pass reuse is invalid: " +
            $"first={first.Layer}; stable={stable.Layer}; " +
            $"changed={changed.Layer}");

        int stableChanges = CountChangedPixels(first.Pixels, stable.Pixels);
        int maskChanges = CountChangedPixels(stable.Pixels, changed.Pixels);
        PixelExtent firstExtent = Measure(first.Pixels);
        PixelExtent changedExtent = Measure(changed.Pixels);
        Require(
            stableChanges == 0 && maskChanges >= 16 &&
            firstExtent.IsVisible && changedExtent.IsVisible &&
            changedExtent.RedSum < firstExtent.RedSum,
            "the cached brush mask was not applied before the effect: " +
            $"stableChanges={stableChanges}, maskChanges={maskChanges}, " +
            $"first={firstExtent}, changed={changedExtent}");

        Console.WriteLine(
            "Qualified live retained-cache spatial-mask-before-effect " +
            $"composition on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; content passes=" +
            $"{first.Layer.ContentPassCount}->" +
            $"{stable.Layer.ContentPassCount}->" +
            $"{changed.Layer.ContentPassCount}, effect passes=" +
            $"{first.Layer.EffectPassCount}->" +
            $"{stable.Layer.EffectPassCount}->" +
            $"{changed.Layer.EffectPassCount}, first={firstExtent}, " +
            $"changed={changedExtent}, pixels={maskChanges}.");
    }

    private static FrameResult Render(
        NativeCompositor compositor,
        WgpuContext context,
        GpuTexture target,
        ulong generation,
        float maskOpacity)
    {
        byte[] scene = BuildScene(generation, maskOpacity);
        NativeSceneUpdateMetrics update = compositor.UpdateScene(scene);
        NativeSceneFrameMetrics frame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] pixels = target.ReadPixels();
        return new FrameResult(
            update,
            frame,
            compositor.GetLayerMetrics(),
            pixels);
    }

    private static byte[] BuildScene(ulong generation, float maskOpacity)
    {
        NativeSceneGradientStop[] stops =
        [
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 0f), 0f),
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 1f), 1f)
        ];
        NativeSceneBrush maskBrush = NativeSceneBrush.LinearGradient(
            Vector2.Zero,
            new Vector2(16f, 0f),
            stopOffset: 0U,
            stops,
            opacity: maskOpacity,
            coordinateTransform: Matrix3x2.CreateTranslation(-12f, -10f));
        var mask = new NativeSceneLayerBrushMask(
            new NativeImageRect(0f, 0f, 16f, 12f),
            Matrix3x2.CreateTranslation(12f, 10f),
            in maskBrush,
            gradientStopCount: (uint)stops.Length);
        Span<NativeSceneEffect> effects = stackalloc NativeSceneEffect[1];
        effects[0] = NativeSceneEffect.GaussianBlur(
            sigmaX: 2f,
            sigmaY: 2f,
            revision: 1U);
        Span<NativeAnalyticPrimitive> rectangle =
            stackalloc NativeAnalyticPrimitive[1];
        rectangle[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            0f,
            0f,
            16f,
            12f,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 5,
            resourceCapacity: 4,
            arenaCapacity: 2048);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation,
            commandCapacity: 5,
            resourceCapacity: 4);

        var compositeState = new NativeSceneState(
            Matrix3x2.CreateTranslation(12f, 10f));
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddStateResource(
                resourceId: 1U,
                generation: 1U,
                in compositeState,
                out uint compositeStateIndex) &&
            builder.TryAddLayerBrushMaskResource(
                resourceId: 2U,
                generation: maskOpacity == 1f ? 1U : 2U,
                in mask,
                stops,
                out uint maskResourceIndex) &&
            builder.TryAddEffectChainResource(
                resourceId: 3U,
                generation: 1U,
                effects,
                revision: 1U,
                out uint effectResourceIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 4U,
                generation: 1U,
                rectangle,
                out uint analyticResourceIndex) &&
            builder.TryPushLayer(
                commandId: 1U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.ForceIsolation,
                    bounds: new NativeImageRect(0f, 0f, Width, Height),
                    effectResourceIndex: effectResourceIndex,
                    contentRevision: 1U,
                    compositeRevision: 1U)) &&
            builder.TryPushLayer(
                commandId: 2U,
                new NativeSceneLayer(
                    opacity: 0.5f,
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.CacheLocalSpace,
                    bounds: new NativeImageRect(0f, 0f, 16f, 12f),
                    maskResourceIndex: maskResourceIndex,
                    contentRevision: 1U,
                    compositeRevision: CacheIdentity,
                    compositeStateResourceIndex: compositeStateIndex)) &&
            builder.TryDrawAnalytic(
                commandId: 3U,
                analyticResourceIndex,
                new NativeImageRect(0f, 0f, 16f, 12f)) &&
            builder.TryPopLayer(commandId: 4U) &&
            builder.TryPopLayer(commandId: 5U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the cache mask/effect scene");
        return stream.ToArray();
    }

    private static PixelExtent Measure(byte[] pixels)
    {
        int minimumX = checked((int)Width);
        int minimumY = checked((int)Height);
        int maximumX = -1;
        int maximumY = -1;
        long redSum = 0;
        for (int y = 0; y < Height; ++y)
        {
            for (int x = 0; x < Width; ++x)
            {
                int red = Red(pixels, x, y);
                redSum += red;
                if (red == 0)
                    continue;
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        }
        return new PixelExtent(
            minimumX,
            minimumY,
            maximumX,
            maximumY,
            redSum);
    }

    private static int CountChangedPixels(byte[] left, byte[] right)
    {
        int changed = 0;
        for (int offset = 0; offset < left.Length; offset += 4)
        {
            if (left[offset] != right[offset])
                ++changed;
        }
        return changed;
    }

    private static int Red(byte[] pixels, int x, int y) =>
        pixels[(y * checked((int)Width) + x) * 4];

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct FrameResult(
        NativeSceneUpdateMetrics Update,
        NativeSceneFrameMetrics Frame,
        NativeLayerMetrics Layer,
        byte[] Pixels);

    private readonly record struct PixelExtent(
        int MinimumX,
        int MinimumY,
        int MaximumX,
        int MaximumY,
        long RedSum)
    {
        internal bool IsVisible => MaximumX >= MinimumX &&
            MaximumY >= MinimumY;

        public override string ToString() =>
            $"[{MinimumX},{MinimumY}]-[{MaximumX},{MaximumY}], red={RedSum}";
    }
}
