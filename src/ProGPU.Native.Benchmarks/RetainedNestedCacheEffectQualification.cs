using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedNestedCacheEffectQualification
{
    private const uint Width = 48U;
    private const uint Height = 32U;
    private const ulong SceneId = 0x4E43414348465831UL;
    private const ulong RootIdentity = 7101U;
    private const ulong ChildIdentity = 7102U;

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
            "Native retained nested-cache effect qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        FrameResult first = Render(
            compositor, context, target, generation: 1U,
            rootContentRevision: 1U, childX: 8f);
        FrameResult stable = Render(
            compositor, context, target, generation: 2U,
            rootContentRevision: 1U, childX: 8f);
        FrameResult moved = Render(
            compositor, context, target, generation: 3U,
            rootContentRevision: 2U, childX: 13f);

        Require(
            first.Update.ValidationError == NativeSceneValidationError.None &&
            stable.Update.ValidationError == NativeSceneValidationError.None &&
            moved.Update.ValidationError == NativeSceneValidationError.None &&
            first.Frame.SubmissionCount > 0U &&
            stable.Frame.SubmissionCount > 0U &&
            moved.Frame.SubmissionCount > 0U &&
            first.Layer.ContentPassCount == 3U &&
            stable.Layer.ContentPassCount == 0U &&
            moved.Layer.ContentPassCount == 2U &&
            first.Layer.EffectPassCount == 2U &&
            stable.Layer.EffectPassCount == 0U &&
            moved.Layer.EffectPassCount == 2U,
            "nested retained-cache pass reuse is invalid: " +
            $"first={first.Layer}; stable={stable.Layer}; moved={moved.Layer}");

        int stableChanges = CountChangedPixels(first.Pixels, stable.Pixels);
        int movedChanges = CountChangedPixels(stable.Pixels, moved.Pixels);
        PixelExtent firstExtent = Measure(first.Pixels);
        PixelExtent movedExtent = Measure(moved.Pixels);
        Require(
            stableChanges == 0 && movedChanges >= 12 &&
            firstExtent.IsVisible && movedExtent.IsVisible &&
            movedExtent.MinimumX > firstExtent.MinimumX &&
            firstExtent.RedSum == movedExtent.RedSum,
            "the nested cache/effect composite was not retained and moved " +
            $"as expected: stableChanges={stableChanges}, " +
            $"movedChanges={movedChanges}, first={firstExtent}, " +
            $"moved={movedExtent}");

        Console.WriteLine(
            "Qualified live nested retained-cache opacity-before-effect " +
            $"composition on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; content passes=" +
            $"{first.Layer.ContentPassCount}->" +
            $"{stable.Layer.ContentPassCount}->" +
            $"{moved.Layer.ContentPassCount}, first={firstExtent}, " +
            $"moved={movedExtent}, changed={movedChanges}.");
    }

    private static FrameResult Render(
        NativeCompositor compositor,
        WgpuContext context,
        GpuTexture target,
        ulong generation,
        ulong rootContentRevision,
        float childX)
    {
        byte[] scene = BuildScene(
            generation, rootContentRevision, childX);
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

    private static byte[] BuildScene(
        ulong generation,
        ulong rootContentRevision,
        float childX)
    {
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
        Span<NativeSceneEffect> effects = stackalloc NativeSceneEffect[1];
        effects[0] = NativeSceneEffect.GaussianBlur(
            sigmaX: 2f,
            sigmaY: 2f,
            revision: 1U);
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 7,
            resourceCapacity: 4,
            arenaCapacity: 2048);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation,
            commandCapacity: 7,
            resourceCapacity: 4);

        var rootComposite = new NativeSceneState(Matrix3x2.Identity);
        var childComposite = new NativeSceneState(
            new Matrix3x2(1f, 0f, 0f, 1f, childX, 8f));
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddStateResource(
                resourceId: 1U,
                generation,
                in rootComposite,
                out uint rootCompositeIndex) &&
            builder.TryAddStateResource(
                resourceId: 2U,
                generation,
                in childComposite,
                out uint childCompositeIndex) &&
            builder.TryAddEffectChainResource(
                resourceId: 3U,
                generation,
                effects,
                revision: 1U,
                out uint effectIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 4U,
                generation: 1U,
                rectangle,
                out uint analyticIndex) &&
            builder.TryPushLayer(
                commandId: 1U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.CacheLocalSpace,
                    bounds: new NativeImageRect(0f, 0f, Width, Height),
                    contentRevision: rootContentRevision,
                    compositeRevision: RootIdentity,
                    compositeStateResourceIndex: rootCompositeIndex)) &&
            builder.TryPushLayer(
                commandId: 2U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.ForceIsolation,
                    bounds: new NativeImageRect(0f, 0f, Width, Height),
                    effectResourceIndex: effectIndex,
                    contentRevision: 1U,
                    compositeRevision: 1U)) &&
            builder.TryPushLayer(
                commandId: 3U,
                new NativeSceneLayer(
                    opacity: 0.5f,
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.CacheLocalSpace,
                    bounds: new NativeImageRect(0f, 0f, 16f, 12f),
                    contentRevision: 1U,
                    compositeRevision: ChildIdentity,
                    compositeStateResourceIndex: childCompositeIndex)) &&
            builder.TryDrawAnalytic(
                commandId: 4U,
                analyticIndex,
                new NativeImageRect(0f, 0f, 16f, 12f)) &&
            builder.TryPopLayer(commandId: 5U) &&
            builder.TryPopLayer(commandId: 6U) &&
            builder.TryPopLayer(commandId: 7U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the nested retained-cache scene");
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
