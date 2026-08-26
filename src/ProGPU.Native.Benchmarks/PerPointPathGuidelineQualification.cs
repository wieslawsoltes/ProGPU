using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class PerPointPathGuidelineQualification
{
    private const uint Width = 40U;
    private const uint Height = 28U;
    private const ulong SceneId = 0x504F494E54475549UL;

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
            "Native per-point path guideline qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        (NativeSceneUpdateMetrics Update, NativeSceneFrameMetrics Frame,
            byte[] Pixels) baseline = Render(
                compositor, context, target, 1U, guided: false,
                reference: false);
        (NativeSceneUpdateMetrics Update, NativeSceneFrameMetrics Frame,
            byte[] Pixels) guided = Render(
                compositor, context, target, 2U, guided: true,
                reference: false);
        (NativeSceneUpdateMetrics Update, NativeSceneFrameMetrics Frame,
            byte[] Pixels) reference = Render(
                compositor, context, target, 3U, guided: false,
                reference: true);

        PixelExtent baselineExtent = Measure(baseline.Pixels);
        PixelExtent guidedExtent = Measure(guided.Pixels);
        PixelExtent referenceExtent = Measure(reference.Pixels);
        int changed = CountChangedPixels(baseline.Pixels, guided.Pixels);
        int referenceChanged = CountChangedPixels(
            guided.Pixels, reference.Pixels);
        Require(
            baseline.Update.ValidationError == NativeSceneValidationError.None &&
            guided.Update.ValidationError == NativeSceneValidationError.None &&
            reference.Update.ValidationError == NativeSceneValidationError.None &&
            baseline.Frame.SubmissionCount > 0U &&
            guided.Frame.SubmissionCount > 0U &&
            reference.Frame.SubmissionCount > 0U &&
            baselineExtent.IsVisible && guidedExtent.IsVisible &&
            referenceExtent.IsVisible && changed > 0 &&
            referenceChanged == 0,
            "the live static multi-guideline path did not match its " +
            $"independent deformed reference: baseline={baselineExtent}, " +
            $"guided={guidedExtent}, reference={referenceExtent}, " +
            $"changed={changed}, referenceChanged={referenceChanged}");

        Console.WriteLine(
            "Qualified live per-point static multi-guideline path " +
            $"deformation on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; baseline={baselineExtent}, " +
            $"guided={guidedExtent}, reference={referenceExtent}, " +
            $"changed={changed}, referenceChanged={referenceChanged}.");
    }

    private static (NativeSceneUpdateMetrics Update,
        NativeSceneFrameMetrics Frame, byte[] Pixels) Render(
        NativeCompositor compositor,
        WgpuContext context,
        GpuTexture target,
        ulong generation,
        bool guided,
        bool reference)
    {
        byte[] scene = BuildScene(generation, guided, reference);
        NativeSceneUpdateMetrics update = compositor.UpdateScene(scene);
        NativeSceneFrameMetrics frame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        return (update, frame, target.ReadPixels());
    }

    private static byte[] BuildScene(
        ulong generation,
        bool guided,
        bool reference)
    {
        float left = reference ? 10f : 10.25f;
        float top = reference ? 8f : 8.25f;
        float right = reference ? 26f : 25.75f;
        float bottom = reference ? 18f : 17.75f;
        Span<NativePathSegment> segments = stackalloc NativePathSegment[4]
        {
            new(NativePathSegmentKind.Line, new(left, top), new(right, top)),
            new(NativePathSegmentKind.Line, new(right, top), new(right, bottom)),
            new(NativePathSegmentKind.Line, new(right, bottom), new(left, bottom)),
            new(NativePathSegmentKind.Line, new(left, bottom), new(left, top))
        };
        Span<NativeScenePathFill> paths = stackalloc NativeScenePathFill[1]
        {
            new(
                0U,
                4U,
                new Vector2(left, top),
                new Vector2(right, bottom),
                new Vector4(1f, 0f, 0f, 1f),
                Matrix3x2.Identity,
                NativeFillRule.NonZero,
                8U)
        };
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 1,
            resourceCapacity: 3,
            arenaCapacity: 1024);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation,
            commandCapacity: 1,
            resourceCapacity: 3);
        bool success = builder.TryAddPerPointGuidelineSetResource(
            1U,
            generation,
            [10.25, 25.75],
            [8.25, 17.75],
            out uint guidelineIndex);
        var state = new NativeSceneState(
            Matrix3x2.Identity,
            flags: guided
                ? NativeSceneStateFlags.GuidelineSet
                : NativeSceneStateFlags.None,
            guidelineResourceIndex: guided ? guidelineIndex : 0U);
        ReadOnlySpan<byte> stream = default;
        success &= builder.TryAddStateResource(
                2U,
                generation,
                in state,
                out uint stateIndex) &&
            builder.TryAddPathResource(
                3U,
                generation,
                paths,
                segments,
                out uint pathIndex) &&
            builder.TryDrawPath(
                1U,
                pathIndex,
                new NativeImageRect(left, top, right - left, bottom - top),
                stateIndex: stateIndex) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the per-point guideline path scene");
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
            minimumX, minimumY, maximumX, maximumY, redSum);
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
