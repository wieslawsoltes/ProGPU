using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedBoundedEffectQualification
{
    private const uint Width = 96U;
    private const uint Height = 64U;
    private const ulong FullSceneId = 0x424F554E44454631UL;
    private const ulong BoundedSceneId = 0x424F554E44454632UL;
    private static readonly NativeImageRect FullBounds =
        new(0f, 0f, Width, Height);
    private static readonly NativeImageRect BoundedBounds =
        new(24f, 14f, 28f, 24f);

    public static void Run()
    {
        using var context = new WgpuContext();
        context.Initialize(window: null);

        FrameResult full = Render(
            context,
            FullSceneId,
            FullBounds,
            "full-target effect qualification target");
        FrameResult bounded = Render(
            context,
            BoundedSceneId,
            BoundedBounds,
            "bounded effect qualification target");

        int changedPixels = CountChangedPixels(full.Pixels, bounded.Pixels);
        PixelExtent fullExtent = Measure(full.Pixels);
        PixelExtent boundedExtent = Measure(bounded.Pixels);
        Require(
            full.Update.ValidationError == NativeSceneValidationError.None &&
            bounded.Update.ValidationError == NativeSceneValidationError.None &&
            full.Frame.SubmissionCount > 0U &&
            bounded.Frame.SubmissionCount > 0U &&
            full.Layer.TextureWidth == Width &&
            full.Layer.TextureHeight == Height &&
            bounded.Layer.TextureWidth == (uint)BoundedBounds.Width &&
            bounded.Layer.TextureHeight == (uint)BoundedBounds.Height &&
            bounded.Layer.TextureBytes < full.Layer.TextureBytes &&
            bounded.Layer.EffectTextureBytes < full.Layer.EffectTextureBytes &&
            full.Layer.EffectPassCount == 2U &&
            bounded.Layer.EffectPassCount == 2U,
            "bounded effect allocation metrics are invalid: " +
            $"full={full.Layer}; bounded={bounded.Layer}");
        Require(
            changedPixels == 0 && fullExtent.IsVisible &&
            fullExtent == boundedExtent,
            "bounded effect pixels differ from full-target isolation: " +
            $"changed={changedPixels}, full={fullExtent}, " +
            $"bounded={boundedExtent}");

        Console.WriteLine(
            "Qualified live bounded effect isolation " +
            $"on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; layer=" +
            $"{full.Layer.TextureWidth}x{full.Layer.TextureHeight}->" +
            $"{bounded.Layer.TextureWidth}x{bounded.Layer.TextureHeight}, " +
            $"layerBytes={full.Layer.TextureBytes}->" +
            $"{bounded.Layer.TextureBytes}, effectBytes=" +
            $"{full.Layer.EffectTextureBytes}->" +
            $"{bounded.Layer.EffectTextureBytes}, extent={boundedExtent}, " +
            $"changed={changedPixels}.");
    }

    private static FrameResult Render(
        WgpuContext context,
        ulong sceneId,
        NativeImageRect bounds,
        string label)
    {
        using var target = new GpuTexture(
            context,
            Width,
            Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            label);
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);
        byte[] scene = BuildScene(sceneId, bounds);
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

    private static byte[] BuildScene(
        ulong sceneId,
        NativeImageRect bounds)
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
            30f,
            20f,
            16f,
            12f,
            new Vector4(1f, 0f, 0f, 1f),
            Matrix3x2.Identity);
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 3,
            resourceCapacity: 2,
            arenaCapacity: 1024);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId,
            generation: 1U,
            commandCapacity: 3,
            resourceCapacity: 2);
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddEffectChainResource(
                resourceId: 1U,
                generation: 1U,
                effects,
                revision: 1U,
                out uint effectIndex) &&
            builder.TryAddAnalyticResource(
                resourceId: 2U,
                generation: 1U,
                rectangle,
                out uint analyticIndex) &&
            builder.TryPushLayer(
                commandId: 1U,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds,
                    bounds: bounds,
                    effectResourceIndex: effectIndex,
                    contentRevision: 1U,
                    compositeRevision: 1U)) &&
            builder.TryDrawAnalytic(
                commandId: 2U,
                analyticIndex,
                new NativeImageRect(30f, 20f, 16f, 12f)) &&
            builder.TryPopLayer(commandId: 3U) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the bounded effect scene");
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
                int red = pixels[(y * checked((int)Width) + x) * 4];
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
