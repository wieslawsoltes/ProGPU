using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedOpacityEffectQualification
{
    private const uint Width = 56U;
    private const uint Height = 36U;
    private const ulong GroupSceneId = 0x4F50414345464631UL;
    private const ulong ReferenceSceneId = 0x4F50414345464632UL;
    private const ulong PrimitiveSceneId = 0x4F50414345464633UL;
    private static readonly NativeImageRect EffectBounds =
        new(4f, 4f, 44f, 28f);
    private static readonly NativeImageRect SourceBounds =
        new(10f, 10f, 32f, 16f);

    public static void Run()
    {
        using var context = new WgpuContext();
        context.Initialize(window: null);

        FrameResult grouped = Render(
            context,
            GroupSceneId,
            BuildScene(GroupSceneId, SceneKind.GroupOpacity));
        FrameResult reference = Render(
            context,
            ReferenceSceneId,
            BuildScene(ReferenceSceneId, SceneKind.UnionReference));
        FrameResult primitive = Render(
            context,
            PrimitiveSceneId,
            BuildScene(PrimitiveSceneId, SceneKind.PrimitiveOpacity));

        int referenceChanges = CountChangedPixels(
            grouped.Pixels, reference.Pixels);
        int primitiveChanges = CountChangedPixels(
            grouped.Pixels, primitive.Pixels);
        int groupedExclusive = Red(grouped.Pixels, 16, 18);
        int groupedOverlap = Red(grouped.Pixels, 26, 18);
        int primitiveExclusive = Red(primitive.Pixels, 16, 18);
        int primitiveOverlap = Red(primitive.Pixels, 26, 18);
        PixelExtent groupedExtent = Measure(grouped.Pixels);

        Require(
            grouped.Update.ValidationError == NativeSceneValidationError.None &&
            reference.Update.ValidationError == NativeSceneValidationError.None &&
            primitive.Update.ValidationError == NativeSceneValidationError.None &&
            grouped.Frame.SubmissionCount > 0U &&
            reference.Frame.SubmissionCount > 0U &&
            primitive.Frame.SubmissionCount > 0U &&
            grouped.Layer.ContentPassCount == 2U &&
            grouped.Layer.CompositePassCount == 2U &&
            grouped.Layer.EffectPassCount == 2U,
            "uncached opacity/effect layer metrics are invalid: " +
            $"grouped={grouped.Layer}; reference={reference.Layer}; " +
            $"primitive={primitive.Layer}");
        Require(
            referenceChanges == 0 && primitiveChanges > 32 &&
            Math.Abs(groupedExclusive - groupedOverlap) <= 1 &&
            Math.Abs(groupedExclusive - primitiveExclusive) <= 1 &&
            primitiveOverlap >= groupedOverlap + 40 &&
            groupedExtent.IsVisible,
            "opacity was not isolated before effect sampling: " +
            $"referenceChanges={referenceChanges}, " +
            $"primitiveChanges={primitiveChanges}, grouped=" +
            $"{groupedExclusive}/{groupedOverlap}, primitive=" +
            $"{primitiveExclusive}/{primitiveOverlap}, " +
            $"extent={groupedExtent}");

        Console.WriteLine(
            "Qualified live uncached opacity-before-effect isolation " +
            $"on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; passes=" +
            $"{grouped.Layer.ContentPassCount}/" +
            $"{grouped.Layer.CompositePassCount}/" +
            $"{grouped.Layer.EffectPassCount}, grouped=" +
            $"{groupedExclusive}/{groupedOverlap}, primitive=" +
            $"{primitiveExclusive}/{primitiveOverlap}, " +
            $"referenceChanged={referenceChanges}, " +
            $"primitiveChanged={primitiveChanges}, extent={groupedExtent}.");
    }

    private static FrameResult Render(
        WgpuContext context,
        ulong sceneId,
        byte[] scene)
    {
        using var target = new GpuTexture(
            context,
            Width,
            Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Uncached opacity-before-effect qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);
        NativeSceneUpdateMetrics update = compositor.UpdateScene(scene);
        NativeSceneFrameMetrics frame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            sceneId,
            generation: 1U,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        return new FrameResult(
            update,
            frame,
            compositor.GetLayerMetrics(),
            target.ReadPixels());
    }

    private static byte[] BuildScene(ulong sceneId, SceneKind kind)
    {
        Span<NativeSceneEffect> effects = stackalloc NativeSceneEffect[1];
        effects[0] = NativeSceneEffect.GaussianBlur(
            sigmaX: 2f,
            sigmaY: 2f,
            revision: 1U);
        Span<NativeAnalyticPrimitive> rectangles =
            stackalloc NativeAnalyticPrimitive[2];
        Vector4 color = kind == SceneKind.GroupOpacity
            ? new Vector4(1f, 0f, 0f, 1f)
            : new Vector4(1f, 0f, 0f, 0.5f);
        rectangles[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            10f,
            10f,
            kind == SceneKind.UnionReference ? 32f : 20f,
            16f,
            color,
            Matrix3x2.Identity);
        int rectangleCount = kind == SceneKind.UnionReference ? 1 : 2;
        if (rectangleCount == 2)
        {
            rectangles[1] = new NativeAnalyticPrimitive(
                NativeAnalyticPrimitiveKind.Rectangle,
                22f,
                10f,
                20f,
                16f,
                color,
                Matrix3x2.Identity);
        }
        int commandCount = kind == SceneKind.GroupOpacity ? 5 : 3;
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: commandCount,
            resourceCapacity: 2,
            arenaCapacity: 1024);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId,
            generation: 1U,
            commandCapacity: commandCount,
            resourceCapacity: 2);
        ReadOnlySpan<byte> stream = default;
        uint effectIndex = 0U;
        uint analyticIndex = 0U;
        bool success = builder.TryAddEffectChainResource(
                resourceId: 1U,
                generation: 1U,
                effects,
                revision: 1U,
                out effectIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 2U,
                generation: 1U,
                rectangles[..rectangleCount],
                out analyticIndex) &&
            builder.TryPushLayer(
                commandId: 1U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds,
                    bounds: EffectBounds,
                    effectResourceIndex: effectIndex,
                    contentRevision: 1U,
                    compositeRevision: 1U));
        if (kind == SceneKind.GroupOpacity)
        {
            success = success && builder.TryPushLayer(
                commandId: 2U,
                new NativeSceneLayer(
                    opacity: 0.5f,
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.ForceIsolation,
                    bounds: SourceBounds));
        }
        uint drawCommand = kind == SceneKind.GroupOpacity ? 3U : 2U;
        success = success && builder.TryDrawAnalytic(
            drawCommand,
            analyticIndex,
            SourceBounds);
        if (kind == SceneKind.GroupOpacity)
            success = success && builder.TryPopLayer(commandId: 4U);
        success = success && builder.TryPopLayer(
                commandId: kind == SceneKind.GroupOpacity ? 5U : 3U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the opacity/effect scene");
        return stream.ToArray();
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private enum SceneKind
    {
        GroupOpacity,
        UnionReference,
        PrimitiveOpacity
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
