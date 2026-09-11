using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedCacheEffectClipQualification
{
    private const uint Width = 48U;
    private const uint Height = 32U;
    private const ulong SceneId = 0x4341434846434C31UL;
    private const ulong CacheIdentity = 7301U;
    private static readonly NativeImageRect WideClip =
        new(0f, 0f, Width, Height);
    private static readonly NativeImageRect NarrowClip =
        new(14f, 8f, 12f, 14f);

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
            "Native retained cache effect final-clip qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        FrameResult first = Render(
            compositor, context, target, generation: 1U, WideClip);
        FrameResult stable = Render(
            compositor, context, target, generation: 2U, WideClip);
        FrameResult clipped = Render(
            compositor, context, target, generation: 3U, NarrowClip);

        Require(
            first.Update.ValidationError == NativeSceneValidationError.None &&
            stable.Update.ValidationError == NativeSceneValidationError.None &&
            clipped.Update.ValidationError == NativeSceneValidationError.None &&
            first.Frame.SubmissionCount > 0U &&
            stable.Frame.SubmissionCount > 0U &&
            clipped.Frame.SubmissionCount > 0U &&
            first.Layer.ContentPassCount == 2U &&
            stable.Layer.ContentPassCount == 1U &&
            clipped.Layer.ContentPassCount == 1U &&
            first.Layer.EffectPassCount == 2U &&
            stable.Layer.EffectPassCount == 2U &&
            clipped.Layer.EffectPassCount == 2U &&
            stable.Layer.EffectCacheHit && clipped.Layer.EffectCacheHit,
            "retained cache/effect final-clip reuse is invalid: " +
            $"first={first.Layer}; stable={stable.Layer}; " +
            $"clipped={clipped.Layer}");

        int stableChanges = CountChangedPixels(first.Pixels, stable.Pixels);
        int clippedChanges = CountChangedPixels(stable.Pixels, clipped.Pixels);
        int insideMismatches = 0;
        int outsideVisible = 0;
        for (int y = 0; y < Height; ++y)
        {
            for (int x = 0; x < Width; ++x)
            {
                bool inside = x >= NarrowClip.X &&
                    x < NarrowClip.X + NarrowClip.Width &&
                    y >= NarrowClip.Y && y < NarrowClip.Y + NarrowClip.Height;
                int wideRed = Red(stable.Pixels, x, y);
                int clippedRed = Red(clipped.Pixels, x, y);
                if (inside && wideRed != clippedRed)
                    ++insideMismatches;
                if (!inside && clippedRed != 0)
                    ++outsideVisible;
            }
        }
        PixelExtent wideExtent = Measure(stable.Pixels);
        PixelExtent clippedExtent = Measure(clipped.Pixels);
        Require(
            stableChanges == 0 && clippedChanges >= 16 &&
            insideMismatches == 0 && outsideVisible == 0 &&
            wideExtent.IsVisible && clippedExtent.IsVisible,
            "the final clip did not crop the completed effect output: " +
            $"stableChanges={stableChanges}, clippedChanges={clippedChanges}, " +
            $"insideMismatches={insideMismatches}, " +
            $"outsideVisible={outsideVisible}, wide={wideExtent}, " +
            $"clipped={clippedExtent}");

        Console.WriteLine(
            "Qualified live retained-cache effect final-output clipping " +
            $"on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; content passes=" +
            $"{first.Layer.ContentPassCount}->" +
            $"{stable.Layer.ContentPassCount}->" +
            $"{clipped.Layer.ContentPassCount}, effect passes=" +
            $"{first.Layer.EffectPassCount}->" +
            $"{stable.Layer.EffectPassCount}->" +
            $"{clipped.Layer.EffectPassCount}, wide={wideExtent}, " +
            $"clipped={clippedExtent}, changed={clippedChanges}.");
    }

    private static FrameResult Render(
        NativeCompositor compositor,
        WgpuContext context,
        GpuTexture target,
        ulong generation,
        NativeImageRect clip)
    {
        byte[] scene = BuildScene(generation, clip);
        NativeSceneUpdateMetrics update = compositor.UpdateScene(scene);
        NativeSceneFrameMetrics frame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        return new FrameResult(
            update,
            frame,
            compositor.GetLayerMetrics(),
            target.ReadPixels());
    }

    private static byte[] BuildScene(
        ulong generation,
        NativeImageRect clip)
    {
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
        var cacheComposite = new NativeSceneState(
            Matrix3x2.CreateTranslation(12f, 10f));
        var effectComposite = new NativeSceneState(
            Matrix3x2.Identity,
            flags: NativeSceneStateFlags.ClipRect,
            clipRect: clip);
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddStateResource(
                resourceId: 1U,
                generation: 1U,
                in cacheComposite,
                out uint cacheCompositeIndex) &&
            builder.TryAddStateResource(
                resourceId: 2U,
                generation,
                in effectComposite,
                out uint effectCompositeIndex) &&
            builder.TryAddEffectChainResource(
                resourceId: 3U,
                generation: 1U,
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
                        NativeSceneLayerFlags.ForceIsolation |
                        NativeSceneLayerFlags.CompositeState,
                    bounds: new NativeImageRect(0f, 0f, Width, Height),
                    effectResourceIndex: effectIndex,
                    contentRevision: 1U,
                    compositeRevision: 1U,
                    compositeStateResourceIndex: effectCompositeIndex)) &&
            builder.TryPushLayer(
                commandId: 2U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.CacheLocalSpace,
                    bounds: new NativeImageRect(0f, 0f, 16f, 12f),
                    contentRevision: 1U,
                    compositeRevision: CacheIdentity,
                    compositeStateResourceIndex: cacheCompositeIndex)) &&
            builder.TryDrawAnalytic(
                commandId: 3U,
                analyticIndex,
                new NativeImageRect(0f, 0f, 16f, 12f)) &&
            builder.TryPopLayer(commandId: 4U) &&
            builder.TryPopLayer(commandId: 5U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the cache/effect final-clip scene");
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
