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
    private const ulong NestedSceneId = 0x434143484D465832UL;
    private const ulong RootCacheIdentity = 7202U;
    private const ulong ChildCacheIdentity = 7203U;

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

        RunNestedCacheOwnership(context);
    }

    private static void RunNestedCacheOwnership(WgpuContext context)
    {
        using var target = new GpuTexture(
            context,
            Width,
            Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Nested retained cache mask ownership qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);
        FrameResult first = RenderNested(
            compositor,
            context,
            target,
            generation: 1U,
            parentMaskOpacity: 1f,
            childMaskOpacity: 1f,
            rootContentRevision: 1U,
            childCompositeRevision: 1U);
        FrameResult stable = RenderNested(
            compositor,
            context,
            target,
            generation: 2U,
            parentMaskOpacity: 1f,
            childMaskOpacity: 1f,
            rootContentRevision: 1U,
            childCompositeRevision: 1U);
        FrameResult parentChanged = RenderNested(
            compositor,
            context,
            target,
            generation: 3U,
            parentMaskOpacity: 0.5f,
            childMaskOpacity: 1f,
            rootContentRevision: 1U,
            childCompositeRevision: 1U);
        FrameResult childChanged = RenderNested(
            compositor,
            context,
            target,
            generation: 4U,
            parentMaskOpacity: 0.5f,
            childMaskOpacity: 0.5f,
            rootContentRevision: 2U,
            childCompositeRevision: 2U);

        int stableChanges = CountChangedPixels(
            first.Pixels, stable.Pixels);
        int parentChanges = CountChangedPixels(
            stable.Pixels, parentChanged.Pixels);
        int childChanges = CountChangedPixels(
            parentChanged.Pixels, childChanged.Pixels);
        PixelExtent firstExtent = Measure(first.Pixels);
        PixelExtent parentExtent = Measure(parentChanged.Pixels);
        PixelExtent childExtent = Measure(childChanged.Pixels);
        Require(
            first.Update.ValidationError == NativeSceneValidationError.None &&
            stable.Update.ValidationError == NativeSceneValidationError.None &&
            parentChanged.Update.ValidationError ==
                NativeSceneValidationError.None &&
            childChanged.Update.ValidationError ==
                NativeSceneValidationError.None &&
            first.Frame.SubmissionCount > 0U &&
            stable.Frame.SubmissionCount > 0U &&
            parentChanged.Frame.SubmissionCount > 0U &&
            childChanged.Frame.SubmissionCount > 0U &&
            first.Layer.ContentPassCount == 3U &&
            stable.Layer.ContentPassCount == 0U &&
            parentChanged.Layer.ContentPassCount == 0U &&
            childChanged.Layer.ContentPassCount == 2U &&
            first.Layer.EffectPassCount == 2U &&
            stable.Layer.EffectPassCount == 0U &&
            parentChanged.Layer.EffectPassCount == 0U &&
            childChanged.Layer.EffectPassCount == 2U,
            "nested cache mask frame validation failed: " +
            $"first={first.Layer}; stable={stable.Layer}; parent=" +
            $"{parentChanged.Layer}; child={childChanged.Layer}");
        Require(
            stableChanges == 0 && parentChanges >= 16 &&
            childChanges >= 8 && firstExtent.IsVisible &&
            parentExtent.IsVisible && childExtent.IsVisible &&
            parentExtent.RedSum < firstExtent.RedSum &&
            childExtent.RedSum < parentExtent.RedSum,
            "nested cache mask ownership pixels are invalid: " +
            $"stableChanges={stableChanges}, parentChanges={parentChanges}, " +
            $"childChanges={childChanges}, first={firstExtent}, " +
            $"parent={parentExtent}, child={childExtent}");

        Console.WriteLine(
            "Qualified live nested retained-cache mask ownership " +
            $"on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; content passes=" +
            $"{first.Layer.ContentPassCount}->" +
            $"{stable.Layer.ContentPassCount}->" +
            $"{parentChanged.Layer.ContentPassCount}->" +
            $"{childChanged.Layer.ContentPassCount}, effect passes=" +
            $"{first.Layer.EffectPassCount}->" +
            $"{stable.Layer.EffectPassCount}->" +
            $"{parentChanged.Layer.EffectPassCount}->" +
            $"{childChanged.Layer.EffectPassCount}, changes=" +
            $"{stableChanges}/{parentChanges}/{childChanges}, first=" +
            $"{firstExtent}, parent={parentExtent}, child={childExtent}.");
    }

    private static FrameResult RenderNested(
        NativeCompositor compositor,
        WgpuContext context,
        GpuTexture target,
        ulong generation,
        float parentMaskOpacity,
        float childMaskOpacity,
        ulong rootContentRevision,
        ulong childCompositeRevision)
    {
        byte[] scene = BuildNestedScene(
            generation,
            parentMaskOpacity,
            childMaskOpacity,
            rootContentRevision,
            childCompositeRevision);
        NativeSceneUpdateMetrics update = compositor.UpdateScene(scene);
        NativeSceneFrameMetrics frame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            NestedSceneId,
            generation,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        return new FrameResult(
            update,
            frame,
            compositor.GetLayerMetrics(),
            target.ReadPixels());
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

    private static byte[] BuildNestedScene(
        ulong generation,
        float parentMaskOpacity,
        float childMaskOpacity,
        ulong rootContentRevision,
        ulong childCompositeRevision)
    {
        NativeSceneGradientStop[] parentStops =
        [
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 0f), 0f),
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 1f), 1f)
        ];
        NativeSceneBrush parentBrush = NativeSceneBrush.LinearGradient(
            Vector2.Zero,
            new Vector2(32f, 0f),
            stopOffset: 0U,
            parentStops,
            opacity: parentMaskOpacity,
            coordinateTransform: Matrix3x2.CreateTranslation(-6f, -6f));
        var parentMask = new NativeSceneLayerBrushMask(
            new NativeImageRect(0f, 0f, 32f, 20f),
            Matrix3x2.CreateTranslation(6f, 6f),
            in parentBrush,
            gradientStopCount: (uint)parentStops.Length);
        NativeSceneGradientStop[] childStops =
        [
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 1f), 0f),
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 0f), 1f)
        ];
        NativeSceneBrush childBrush = NativeSceneBrush.LinearGradient(
            Vector2.Zero,
            new Vector2(0f, 12f),
            stopOffset: 0U,
            childStops,
            opacity: childMaskOpacity,
            coordinateTransform: Matrix3x2.CreateTranslation(-4f, -4f));
        var childMask = new NativeSceneLayerBrushMask(
            new NativeImageRect(0f, 0f, 16f, 12f),
            Matrix3x2.CreateTranslation(4f, 4f),
            in childBrush,
            gradientStopCount: (uint)childStops.Length);
        Span<NativeSceneEffect> effects = stackalloc NativeSceneEffect[1];
        effects[0] = NativeSceneEffect.GaussianBlur(
            sigmaX: 2f,
            sigmaY: 2f,
            revision: 1U);
        Span<NativeAnalyticPrimitive> child =
            stackalloc NativeAnalyticPrimitive[1];
        child[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            0f,
            0f,
            16f,
            12f,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        Span<NativeAnalyticPrimitive> sibling =
            stackalloc NativeAnalyticPrimitive[1];
        sibling[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            12f,
            4f,
            16f,
            12f,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 8,
            resourceCapacity: 7,
            arenaCapacity: 3584);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            NestedSceneId,
            generation,
            commandCapacity: 8,
            resourceCapacity: 7);
        var rootCompositeState = new NativeSceneState(
            Matrix3x2.CreateTranslation(6f, 6f));
        var childCompositeState = new NativeSceneState(
            Matrix3x2.CreateTranslation(4f, 4f));
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddStateResource(
                resourceId: 1U,
                generation: 1U,
                in rootCompositeState,
                out uint rootStateIndex) &&
            builder.TryAddStateResource(
                resourceId: 2U,
                generation: 1U,
                in childCompositeState,
                out uint childStateIndex) &&
            builder.TryAddLayerBrushMaskResource(
                resourceId: 3U,
                generation: parentMaskOpacity == 1f ? 1U : 2U,
                in parentMask,
                parentStops,
                out uint parentMaskIndex) &&
            builder.TryAddLayerBrushMaskResource(
                resourceId: 4U,
                generation: childMaskOpacity == 1f ? 1U : 2U,
                in childMask,
                childStops,
                out uint childMaskIndex) &&
            builder.TryAddEffectChainResource(
                resourceId: 5U,
                generation: 1U,
                effects,
                revision: 1U,
                out uint effectIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 6U,
                generation: 1U,
                child,
                out uint childIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 7U,
                generation: 1U,
                sibling,
                out uint siblingIndex) &&
            builder.TryPushLayer(
                commandId: 1U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.CacheLocalSpace,
                    bounds: new NativeImageRect(0f, 0f, 32f, 20f),
                    maskResourceIndex: parentMaskIndex,
                    contentRevision: rootContentRevision,
                    compositeRevision: RootCacheIdentity,
                    compositeStateResourceIndex: rootStateIndex)) &&
            builder.TryPushLayer(
                commandId: 2U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.ForceIsolation,
                    bounds: new NativeImageRect(0f, 0f, 24f, 20f),
                    effectResourceIndex: effectIndex,
                    contentRevision: childCompositeRevision,
                    compositeRevision: childCompositeRevision)) &&
            builder.TryPushLayer(
                commandId: 3U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.CacheLocalSpace,
                    bounds: new NativeImageRect(0f, 0f, 16f, 12f),
                    maskResourceIndex: childMaskIndex,
                    contentRevision: 1U,
                    compositeRevision: ChildCacheIdentity,
                    compositeStateResourceIndex: childStateIndex)) &&
            builder.TryDrawAnalytic(
                commandId: 4U,
                childIndex,
                new NativeImageRect(0f, 0f, 16f, 12f)) &&
            builder.TryPopLayer(commandId: 5U) &&
            builder.TryPopLayer(commandId: 6U) &&
            builder.TryDrawAnalytic(
                commandId: 7U,
                siblingIndex,
                new NativeImageRect(12f, 4f, 16f, 12f)) &&
            builder.TryPopLayer(commandId: 8U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build nested cache mask scene");
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
