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
    private const ulong MaskedSceneId = 0x4F50414345464634UL;
    private const ulong PostEffectMaskSceneId = 0x4F50414345464635UL;
    private const ulong InheritedOpacitySceneId = 0x4F50414345464636UL;
    private const ulong FlattenedOpacitySceneId = 0x4F50414345464637UL;
    private const ulong InheritedMaskSceneId = 0x4F50414345464638UL;
    private const ulong FlattenedMaskSceneId = 0x4F50414345464639UL;
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

        RunSpatialMaskOrdering(context);
        RunInheritedOpacityOwnership(context);
        RunInheritedMaskOwnership(context);
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

    private static void RunSpatialMaskOrdering(WgpuContext context)
    {
        FrameResult masked = Render(
            context,
            MaskedSceneId,
            BuildMaskScene(MaskedSceneId, maskBeforeEffect: true));
        FrameResult postEffect = Render(
            context,
            PostEffectMaskSceneId,
            BuildMaskScene(PostEffectMaskSceneId, maskBeforeEffect: false));
        int changed = CountChangedPixels(masked.Pixels, postEffect.Pixels);
        int maskedLeft = Red(masked.Pixels, 14, 18);
        int maskedRight = Red(masked.Pixels, 38, 18);
        PixelExtent maskedExtent = Measure(masked.Pixels);
        PixelExtent postEffectExtent = Measure(postEffect.Pixels);

        Require(
            masked.Update.ValidationError == NativeSceneValidationError.None &&
            postEffect.Update.ValidationError ==
                NativeSceneValidationError.None &&
            masked.Frame.SubmissionCount > 0U &&
            postEffect.Frame.SubmissionCount > 0U &&
            masked.Layer.ContentPassCount == 2U &&
            masked.Layer.CompositePassCount == 2U &&
            masked.Layer.EffectPassCount == 2U,
            "uncached mask/effect layer metrics are invalid: " +
            $"masked={masked.Layer}; postEffect={postEffect.Layer}");
        Require(
            changed > 32 && maskedRight >= maskedLeft + 80 &&
            maskedExtent.IsVisible && postEffectExtent.IsVisible,
            "the spatial mask was not isolated before effect sampling: " +
            $"changed={changed}, samples={maskedLeft}/{maskedRight}, " +
            $"masked={maskedExtent}, postEffect={postEffectExtent}");

        Console.WriteLine(
            "Qualified live uncached spatial-mask-before-effect isolation " +
            $"on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; passes=" +
            $"{masked.Layer.ContentPassCount}/" +
            $"{masked.Layer.CompositePassCount}/" +
            $"{masked.Layer.EffectPassCount}, samples=" +
            $"{maskedLeft}/{maskedRight}, postEffectChanged={changed}, " +
            $"masked={maskedExtent}, postEffect={postEffectExtent}.");
    }

    private static byte[] BuildMaskScene(
        ulong sceneId,
        bool maskBeforeEffect)
    {
        NativeSceneGradientStop[] stops =
        [
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 0f), 0f),
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 1f), 1f)
        ];
        NativeSceneBrush maskBrush = NativeSceneBrush.LinearGradient(
            new Vector2(10f, 0f),
            new Vector2(42f, 0f),
            stopOffset: 0U,
            stops,
            coordinateTransform: Matrix3x2.Identity);
        var mask = new NativeSceneLayerBrushMask(
            SourceBounds,
            Matrix3x2.Identity,
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
            SourceBounds.X,
            SourceBounds.Y,
            SourceBounds.Width,
            SourceBounds.Height,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 5,
            resourceCapacity: 3,
            arenaCapacity: 2048);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId,
            generation: 1U,
            commandCapacity: 5,
            resourceCapacity: 3);
        ReadOnlySpan<byte> stream = default;
        uint maskIndex = 0U;
        uint effectIndex = 0U;
        uint analyticIndex = 0U;
        bool success = builder.TryAddLayerBrushMaskResource(
                resourceId: 1U,
                generation: 1U,
                in mask,
                stops,
                out maskIndex) &&
            builder.TryAddEffectChainResource(
                resourceId: 2U,
                generation: 1U,
                effects,
                revision: 1U,
                out effectIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 3U,
                generation: 1U,
                rectangle,
                out analyticIndex);
        NativeSceneLayer effectLayer = new(
            flags: NativeSceneLayerFlags.Bounds,
            bounds: EffectBounds,
            effectResourceIndex: effectIndex);
        NativeSceneLayer maskLayer = new(
            flags: NativeSceneLayerFlags.Bounds |
                NativeSceneLayerFlags.ForceIsolation,
            bounds: maskBeforeEffect ? SourceBounds : EffectBounds,
            maskResourceIndex: maskIndex);
        success = success && builder.TryPushLayer(
            commandId: 1U,
            maskBeforeEffect ? effectLayer : maskLayer);
        success = success && builder.TryPushLayer(
            commandId: 2U,
            maskBeforeEffect ? maskLayer : effectLayer);
        success = success && builder.TryDrawAnalytic(
                commandId: 3U,
                analyticIndex,
                SourceBounds) &&
            builder.TryPopLayer(commandId: 4U) &&
            builder.TryPopLayer(commandId: 5U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the uncached mask/effect scene");
        return stream.ToArray();
    }

    private static void RunInheritedOpacityOwnership(WgpuContext context)
    {
        FrameResult inherited = Render(
            context,
            InheritedOpacitySceneId,
            BuildInheritedOpacityScene(
                InheritedOpacitySceneId,
                preserveParentBoundary: true));
        FrameResult flattened = Render(
            context,
            FlattenedOpacitySceneId,
            BuildInheritedOpacityScene(
                FlattenedOpacitySceneId,
                preserveParentBoundary: false));
        int changed = CountChangedPixels(inherited.Pixels, flattened.Pixels);
        int inheritedExclusive = Red(inherited.Pixels, 34, 18);
        int inheritedOverlap = Red(inherited.Pixels, 26, 18);
        int flattenedExclusive = Red(flattened.Pixels, 34, 18);
        int flattenedOverlap = Red(flattened.Pixels, 26, 18);
        PixelExtent inheritedExtent = Measure(inherited.Pixels);
        PixelExtent flattenedExtent = Measure(flattened.Pixels);

        Require(
            inherited.Update.ValidationError ==
                NativeSceneValidationError.None &&
            flattened.Update.ValidationError ==
                NativeSceneValidationError.None &&
            inherited.Frame.SubmissionCount > 0U &&
            flattened.Frame.SubmissionCount > 0U &&
            inherited.Layer.ContentPassCount == 2U &&
            inherited.Layer.CompositePassCount == 2U &&
            inherited.Layer.EffectPassCount == 2U &&
            flattened.Layer.ContentPassCount == 2U &&
            flattened.Layer.CompositePassCount == 2U &&
            flattened.Layer.EffectPassCount == 2U,
            "inherited opacity/effect layer metrics are invalid: " +
            $"inherited={inherited.Layer}; flattened={flattened.Layer}");
        Require(
            changed > 32 &&
            Math.Abs(inheritedExclusive - inheritedOverlap) <= 2 &&
            Math.Abs(inheritedExclusive - flattenedExclusive) <= 2 &&
            flattenedOverlap >= inheritedOverlap + 40 &&
            inheritedExtent.IsVisible && flattenedExtent.IsVisible,
            "ancestor opacity was flattened across a descendant effect: " +
            $"changed={changed}, inherited=" +
            $"{inheritedExclusive}/{inheritedOverlap}, flattened=" +
            $"{flattenedExclusive}/{flattenedOverlap}, " +
            $"inheritedExtent={inheritedExtent}, " +
            $"flattenedExtent={flattenedExtent}");

        Console.WriteLine(
            "Qualified live inherited opacity outside descendant effect " +
            $"on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; passes=" +
            $"{inherited.Layer.ContentPassCount}/" +
            $"{inherited.Layer.CompositePassCount}/" +
            $"{inherited.Layer.EffectPassCount}, flattenedPasses=" +
            $"{flattened.Layer.ContentPassCount}/" +
            $"{flattened.Layer.CompositePassCount}/" +
            $"{flattened.Layer.EffectPassCount}, inherited=" +
            $"{inheritedExclusive}/{inheritedOverlap}, flattened=" +
            $"{flattenedExclusive}/{flattenedOverlap}, " +
            $"flattenedChanged={changed}, inherited={inheritedExtent}, " +
            $"flattened={flattenedExtent}.");
    }

    private static byte[] BuildInheritedOpacityScene(
        ulong sceneId,
        bool preserveParentBoundary)
    {
        Span<NativeSceneEffect> effects = stackalloc NativeSceneEffect[1];
        effects[0] = NativeSceneEffect.GaussianBlur(
            sigmaX: 2f,
            sigmaY: 2f,
            revision: 1U);
        Span<NativeAnalyticPrimitive> child =
            stackalloc NativeAnalyticPrimitive[1];
        child[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            10f,
            10f,
            20f,
            16f,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        Span<NativeAnalyticPrimitive> sibling =
            stackalloc NativeAnalyticPrimitive[1];
        sibling[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            22f,
            10f,
            20f,
            16f,
            preserveParentBoundary
                ? new Vector4(1f, 0f, 0f, 1f)
                : new Vector4(1f, 0f, 0f, 0.5f),
            Matrix3x2.Identity);
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 6,
            resourceCapacity: 3,
            arenaCapacity: 1536);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId,
            generation: 1U,
            commandCapacity: 6,
            resourceCapacity: 3);
        uint effectIndex = 0U;
        uint childIndex = 0U;
        uint siblingIndex = 0U;
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddEffectChainResource(
                resourceId: 1U,
                generation: 1U,
                effects,
                revision: 1U,
                out effectIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 2U,
                generation: 1U,
                child,
                out childIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 3U,
                generation: 1U,
                sibling,
                out siblingIndex);
        NativeSceneLayer effectLayer = new(
            flags: NativeSceneLayerFlags.Bounds,
            bounds: EffectBounds,
            effectResourceIndex: effectIndex);
        NativeSceneLayer opacityLayer = new(
            opacity: 0.5f,
            flags: NativeSceneLayerFlags.Bounds |
                NativeSceneLayerFlags.ForceIsolation,
            bounds: preserveParentBoundary
                ? EffectBounds
                : new NativeImageRect(10f, 10f, 20f, 16f));
        success = success && builder.TryPushLayer(
            commandId: 1U,
            preserveParentBoundary ? opacityLayer : effectLayer);
        success = success && builder.TryPushLayer(
            commandId: 2U,
            preserveParentBoundary ? effectLayer : opacityLayer);
        success = success && builder.TryDrawAnalytic(
                commandId: 3U,
                childIndex,
                new NativeImageRect(10f, 10f, 20f, 16f)) &&
            builder.TryPopLayer(commandId: 4U);
        if (!preserveParentBoundary)
            success = success && builder.TryPopLayer(commandId: 5U);
        success = success && builder.TryDrawAnalytic(
            commandId: preserveParentBoundary ? 5U : 6U,
            siblingIndex,
            new NativeImageRect(22f, 10f, 20f, 16f));
        if (preserveParentBoundary)
            success = success && builder.TryPopLayer(commandId: 6U);
        success = success && builder.TryBuild(out stream);
        Require(success, "failed to build inherited opacity/effect scene");
        return stream.ToArray();
    }

    private static void RunInheritedMaskOwnership(WgpuContext context)
    {
        FrameResult inherited = Render(
            context,
            InheritedMaskSceneId,
            BuildInheritedMaskScene(
                InheritedMaskSceneId,
                preserveParentBoundary: true));
        FrameResult flattened = Render(
            context,
            FlattenedMaskSceneId,
            BuildInheritedMaskScene(
                FlattenedMaskSceneId,
                preserveParentBoundary: false));
        int changed = CountChangedPixels(inherited.Pixels, flattened.Pixels);
        int inheritedLeft = Red(inherited.Pixels, 14, 18);
        int inheritedRight = Red(inherited.Pixels, 38, 18);
        int flattenedLeft = Red(flattened.Pixels, 14, 18);
        int flattenedRight = Red(flattened.Pixels, 38, 18);
        PixelExtent inheritedExtent = Measure(inherited.Pixels);
        PixelExtent flattenedExtent = Measure(flattened.Pixels);

        Require(
            inherited.Update.ValidationError ==
                NativeSceneValidationError.None &&
            flattened.Update.ValidationError ==
                NativeSceneValidationError.None &&
            inherited.Frame.SubmissionCount > 0U &&
            flattened.Frame.SubmissionCount > 0U &&
            inherited.Layer.ContentPassCount == 2U &&
            inherited.Layer.CompositePassCount == 2U &&
            inherited.Layer.EffectPassCount == 2U &&
            flattened.Layer.ContentPassCount == 3U &&
            flattened.Layer.CompositePassCount == 3U &&
            flattened.Layer.EffectPassCount == 2U,
            "inherited mask/effect layer metrics are invalid: " +
            $"inherited={inherited.Layer}; flattened={flattened.Layer}");
        Require(
            changed > 32 &&
            inheritedRight >= inheritedLeft + 64 &&
            flattenedExtent.RedSum >= inheritedExtent.RedSum + 4096 &&
            inheritedExtent.IsVisible && flattenedExtent.IsVisible,
            "ancestor opacity mask was flattened into descendants: " +
            $"changed={changed}, inherited=" +
            $"{inheritedLeft}/{inheritedRight}, flattened=" +
            $"{flattenedLeft}/{flattenedRight}, " +
            $"inheritedExtent={inheritedExtent}, " +
            $"flattenedExtent={flattenedExtent}");

        Console.WriteLine(
            "Qualified live inherited opacity mask outside descendant " +
            $"effect on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; passes=" +
            $"{inherited.Layer.ContentPassCount}/" +
            $"{inherited.Layer.CompositePassCount}/" +
            $"{inherited.Layer.EffectPassCount}, flattenedPasses=" +
            $"{flattened.Layer.ContentPassCount}/" +
            $"{flattened.Layer.CompositePassCount}/" +
            $"{flattened.Layer.EffectPassCount}, inherited=" +
            $"{inheritedLeft}/{inheritedRight}, flattened=" +
            $"{flattenedLeft}/{flattenedRight}, " +
            $"flattenedChanged={changed}, inherited={inheritedExtent}, " +
            $"flattened={flattenedExtent}.");
    }

    private static byte[] BuildInheritedMaskScene(
        ulong sceneId,
        bool preserveParentBoundary)
    {
        NativeSceneGradientStop[] stops =
        [
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 0f), 0f),
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 1f), 1f)
        ];
        NativeSceneBrush maskBrush = NativeSceneBrush.LinearGradient(
            new Vector2(4f, 0f),
            new Vector2(48f, 0f),
            stopOffset: 0U,
            stops,
            coordinateTransform: Matrix3x2.Identity);
        var mask = new NativeSceneLayerBrushMask(
            EffectBounds,
            Matrix3x2.Identity,
            in maskBrush,
            gradientStopCount: (uint)stops.Length);
        Span<NativeSceneEffect> effects = stackalloc NativeSceneEffect[1];
        effects[0] = NativeSceneEffect.GaussianBlur(
            sigmaX: 2f,
            sigmaY: 2f,
            revision: 1U);
        Span<NativeAnalyticPrimitive> child =
            stackalloc NativeAnalyticPrimitive[1];
        child[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            10f,
            10f,
            20f,
            16f,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        Span<NativeAnalyticPrimitive> sibling =
            stackalloc NativeAnalyticPrimitive[1];
        sibling[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            22f,
            10f,
            20f,
            16f,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        int commandCapacity = preserveParentBoundary ? 6 : 8;
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity,
            resourceCapacity: 4,
            arenaCapacity: 2560);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId,
            generation: 1U,
            commandCapacity,
            resourceCapacity: 4);
        uint maskIndex = 0U;
        uint effectIndex = 0U;
        uint childIndex = 0U;
        uint siblingIndex = 0U;
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddLayerBrushMaskResource(
                resourceId: 1U,
                generation: 1U,
                in mask,
                stops,
                out maskIndex) &&
            builder.TryAddEffectChainResource(
                resourceId: 2U,
                generation: 1U,
                effects,
                revision: 1U,
                out effectIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 3U,
                generation: 1U,
                child,
                out childIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 4U,
                generation: 1U,
                sibling,
                out siblingIndex);
        NativeSceneLayer effectLayer = new(
            flags: NativeSceneLayerFlags.Bounds,
            bounds: EffectBounds,
            effectResourceIndex: effectIndex);
        NativeSceneLayer parentMaskLayer = new(
            flags: NativeSceneLayerFlags.Bounds |
                NativeSceneLayerFlags.ForceIsolation,
            bounds: EffectBounds,
            maskResourceIndex: maskIndex);
        NativeSceneLayer childMaskLayer = new(
            flags: NativeSceneLayerFlags.Bounds |
                NativeSceneLayerFlags.ForceIsolation,
            bounds: SourceBounds,
            maskResourceIndex: maskIndex);
        NativeSceneLayer siblingMaskLayer = new(
            flags: NativeSceneLayerFlags.Bounds |
                NativeSceneLayerFlags.ForceIsolation,
            bounds: new NativeImageRect(22f, 10f, 20f, 16f),
            maskResourceIndex: maskIndex);
        success = success && builder.TryPushLayer(
            commandId: 1U,
            preserveParentBoundary ? parentMaskLayer : effectLayer);
        success = success && builder.TryPushLayer(
            commandId: 2U,
            preserveParentBoundary ? effectLayer : childMaskLayer);
        success = success && builder.TryDrawAnalytic(
                commandId: 3U,
                childIndex,
                SourceBounds) &&
            builder.TryPopLayer(commandId: 4U);
        if (!preserveParentBoundary)
            success = success && builder.TryPopLayer(commandId: 5U);
        if (!preserveParentBoundary)
            success = success && builder.TryPushLayer(
                commandId: 6U,
                siblingMaskLayer);
        success = success && builder.TryDrawAnalytic(
                commandId: preserveParentBoundary ? 5U : 7U,
                siblingIndex,
                new NativeImageRect(22f, 10f, 20f, 16f));
        success = success && builder.TryPopLayer(
                commandId: preserveParentBoundary ? 6U : 8U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build inherited mask/effect scene");
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
