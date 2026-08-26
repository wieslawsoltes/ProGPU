using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedCacheMultiGuidelineQualification
{
    private const uint Width = 48U;
    private const uint Height = 32U;
    private const ulong SceneId = 0x4341434847554944UL;
    private const ulong ContentRevision = 1U;
    private const ulong CompositeRevision = 7005U;

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
            "Native retained cache multi-guideline qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        byte[] baselineScene = BuildScene(
            generation: 1U,
            guided: false,
            reference: false);
        NativeSceneUpdateMetrics baselineUpdate =
            compositor.UpdateScene(baselineScene);
        NativeSceneFrameMetrics baselineFrame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation: 1U,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] baselinePixels = target.ReadPixels();
        NativeLayerMetrics baselineLayer = compositor.GetLayerMetrics();

        byte[] guidedScene = BuildScene(
            generation: 2U,
            guided: true,
            reference: false);
        NativeSceneUpdateMetrics guidedUpdate =
            compositor.UpdateScene(guidedScene);
        NativeSceneFrameMetrics guidedFrame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation: 2U,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] guidedPixels = target.ReadPixels();
        NativeLayerMetrics guidedLayer = compositor.GetLayerMetrics();

        byte[] referenceScene = BuildScene(
            generation: 3U,
            guided: false,
            reference: true);
        NativeSceneUpdateMetrics referenceUpdate =
            compositor.UpdateScene(referenceScene);
        NativeSceneFrameMetrics referenceFrame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation: 3U,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] referencePixels = target.ReadPixels();
        NativeLayerMetrics referenceLayer = compositor.GetLayerMetrics();

        Require(
            baselineUpdate.ValidationError ==
                NativeSceneValidationError.None &&
            guidedUpdate.ValidationError == NativeSceneValidationError.None &&
            referenceUpdate.ValidationError ==
                NativeSceneValidationError.None &&
            baselineFrame.SubmissionCount > 0U &&
            guidedFrame.SubmissionCount > 0U &&
            referenceFrame.SubmissionCount > 0U &&
            baselineLayer.ContentPassCount == 1U &&
            baselineLayer.CompositePassCount == 1U &&
            guidedLayer.ContentPassCount == 0U &&
            guidedLayer.CompositePassCount == 1U &&
            referenceLayer.ContentPassCount == 0U &&
            referenceLayer.CompositePassCount == 1U,
            "retained cache multi-guideline replay metrics are invalid: " +
            $"baseline update={baselineUpdate}, frame={baselineFrame}, " +
            $"layer={baselineLayer}; guided update={guidedUpdate}, " +
            $"frame={guidedFrame}, layer={guidedLayer}; reference update=" +
            $"{referenceUpdate}, frame={referenceFrame}, " +
            $"layer={referenceLayer}");

        PixelExtent baseline = Measure(baselinePixels);
        PixelExtent guided = Measure(guidedPixels);
        PixelExtent reference = Measure(referencePixels);
        int changedPixels = CountChangedPixels(
            baselinePixels,
            guidedPixels);
        int referenceChanges = CountChangedPixels(
            guidedPixels,
            referencePixels);
        Require(
            baseline.IsVisible && guided.IsVisible &&
            reference.IsVisible &&
            changedPixels >= 8 && baseline.RedSum != guided.RedSum &&
            referenceChanges == 0 && Red(guidedPixels, 2, 2) == 0,
            "the live retained cache mask/guideline path did not deform " +
            $"the composite: baseline={baseline}, guided={guided}, " +
            $"reference={reference}, changed={changedPixels}, " +
            $"referenceChanged={referenceChanges}");

        Console.WriteLine(
            "Qualified live local retained cache mask/multi-guideline " +
            $"composition on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; passes=" +
            $"{baselineLayer.ContentPassCount}/" +
            $"{baselineLayer.CompositePassCount}->" +
            $"{guidedLayer.ContentPassCount}/" +
            $"{guidedLayer.CompositePassCount}->" +
            $"{referenceLayer.ContentPassCount}/" +
            $"{referenceLayer.CompositePassCount}, baseline={baseline}, " +
            $"guided={guided}, reference={reference}, " +
            $"changed={changedPixels}, referenceChanged={referenceChanges}.");
    }

    private static byte[] BuildScene(
        ulong generation,
        bool guided,
        bool reference)
    {
        Span<NativeAnalyticPrimitive> rectangle =
            stackalloc NativeAnalyticPrimitive[1];
        rectangle[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            0f,
            0f,
            16f,
            8f,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        const int resourceCapacity = 4;
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 3,
            resourceCapacity,
            arenaCapacity: 1024);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation,
            commandCapacity: 3,
            resourceCapacity);

        uint nextResourceId = 1U;
        uint guidelineIndex = uint.MaxValue;
        ReadOnlySpan<byte> stream = default;
        bool success =
            builder.TryAddCompositeGuidelineSetResource(
                nextResourceId++,
                generation,
                [10.5, 26.0],
                [8.5, 16.0],
                out guidelineIndex);
        Matrix3x2 placement = reference
            ? new Matrix3x2(0.96875f, 0f, 0f, 0.9375f, 10.75f, 8.75f)
            : new Matrix3x2(1f, 0f, 0f, 1f, 10.25f, 8.25f);
        var compositeState = new NativeSceneState(
            placement,
            flags: guided
                ? NativeSceneStateFlags.GuidelineSet
                : NativeSceneStateFlags.None,
            guidelineResourceIndex: guided ? guidelineIndex : 0U);
        NativeSceneGradientStop[] stops =
        [
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 0f),
                0f),
            new NativeSceneGradientStop(
                new Vector4(1f, 1f, 1f, 1f),
                1f)
        ];
        Matrix3x2 brushPlacement = reference
            ? new Matrix3x2(1f, 0f, 0f, 1f, 10.25f, 8.25f)
            : placement;
        Require(
            Matrix3x2.Invert(brushPlacement, out Matrix3x2 brushMapping),
            "failed to invert the cache mask placement");
        NativeSceneBrush maskBrush = NativeSceneBrush.LinearGradient(
            Vector2.Zero,
            new Vector2(16f, 0f),
            stopOffset: 0U,
            stops,
            opacity: 1f,
            coordinateTransform: brushMapping);
        var mask = new NativeSceneLayerBrushMask(
            new NativeImageRect(0f, 0f, 16f, 8f),
            placement,
            in maskBrush,
            gradientStopCount: (uint)stops.Length);
        success &= builder.TryAddStateResource(
                nextResourceId++,
                generation,
                in compositeState,
                out uint compositeStateIndex) &&
            builder.TryAddLayerBrushMaskResource(
                nextResourceId++,
                generation,
                in mask,
                stops,
                out uint maskResourceIndex) &&
            builder.TryAddAnalyticResource(
                nextResourceId,
                ContentRevision,
                rectangle,
                out uint analyticResourceIndex) &&
            builder.TryPushLayer(
                commandId: 1U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.CacheLocalSpace,
                    bounds: new NativeImageRect(0f, 0f, 16f, 8f),
                    maskResourceIndex: maskResourceIndex,
                    contentRevision: ContentRevision,
                    compositeRevision: CompositeRevision,
                    compositeStateResourceIndex: compositeStateIndex)) &&
            builder.TryDrawAnalytic(
                commandId: 2U,
                analyticResourceIndex,
                new NativeImageRect(0f, 0f, 16f, 8f)) &&
            builder.TryPopLayer(commandId: 3U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the retained cache guideline scene");
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
