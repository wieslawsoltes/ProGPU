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

        Require(
            SharedSegmentPathResourceIsRejected(compositor, target),
            "per-point deformation must reject shared path segment ranges");

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
        float secondLeft = reference ? 29f : 29.25f;
        float secondTop = reference ? 19f : 19.25f;
        float secondRight = reference ? 36f : 35.75f;
        float secondBottom = reference ? 25f : 24.75f;
        float quadraticControlX = reference ? 32f : 32.25f;
        float quadraticControlY = reference ? 17.5f : 17.25f;
        float cubicControl1X = reference ? 34.5f : 34.25f;
        float cubicControl2X = reference ? 30.5f : 30.75f;
        float cubicControlY = reference ? 26.5f : 26.25f;
        Span<NativePathSegment> segments = stackalloc NativePathSegment[8]
        {
            new(NativePathSegmentKind.Line, new(left, top), new(right, top)),
            new(NativePathSegmentKind.Line, new(right, top), new(right, bottom)),
            new(NativePathSegmentKind.Line, new(right, bottom), new(left, bottom)),
            new(NativePathSegmentKind.Line, new(left, bottom), new(left, top)),
            new(NativePathSegmentKind.Quadratic,
                new(secondLeft, secondTop),
                new(quadraticControlX, quadraticControlY),
                new(secondRight, secondTop)),
            new(NativePathSegmentKind.Line,
                new(secondRight, secondTop), new(secondRight, secondBottom)),
            new(NativePathSegmentKind.Cubic,
                new(secondRight, secondBottom),
                new(cubicControl1X, cubicControlY),
                new(cubicControl2X, cubicControlY),
                new(secondLeft, secondBottom)),
            new(NativePathSegmentKind.Line,
                new(secondLeft, secondBottom), new(secondLeft, secondTop))
        };
        Span<NativeScenePathFill> paths = stackalloc NativeScenePathFill[2]
        {
            new(
                0U,
                4U,
                new Vector2(left, top),
                new Vector2(right, bottom),
                new Vector4(1f, 0f, 0f, 1f),
                Matrix3x2.Identity,
                NativeFillRule.NonZero,
                8U),
            new(
                4U,
                4U,
                new Vector2(secondLeft, quadraticControlY),
                new Vector2(secondRight, cubicControlY),
                new Vector4(0f, 1f, 0f, 1f),
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
            [10.25, 25.75, 29.25, 35.75],
            [8.25, 17.75, 19.25, 24.75],
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
                new NativeImageRect(
                    left,
                    top,
                    secondRight - left,
                    cubicControlY - top),
                stateIndex: stateIndex) &&
            builder.TryBuild(out stream);
        Require(success, "failed to build the per-point guideline path scene");
        return stream.ToArray();
    }

    private static bool SharedSegmentPathResourceIsRejected(
        NativeCompositor compositor,
        GpuTexture target)
    {
        Span<NativePathSegment> segments = stackalloc NativePathSegment[1]
        {
            new(NativePathSegmentKind.Line, new(2f, 2f), new(8f, 8f))
        };
        Span<NativeScenePathFill> paths = stackalloc NativeScenePathFill[2]
        {
            new(0U, 1U, new(2f, 2f), new(8f, 8f), Vector4.One,
                Matrix3x2.Identity),
            new(0U, 1U, new(2f, 2f), new(8f, 8f), Vector4.One,
                Matrix3x2.Identity)
        };
        Span<byte> destination = stackalloc byte[2048];
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation: 4U,
            commandCapacity: 1,
            resourceCapacity: 3);
        bool success = builder.TryAddPerPointGuidelineSetResource(
            1U,
            generation: 4U,
            [2.25, 7.75],
            [2.25, 7.75],
            out uint guidelineIndex);
        var state = new NativeSceneState(
            Matrix3x2.Identity,
            flags: NativeSceneStateFlags.GuidelineSet,
            guidelineResourceIndex: guidelineIndex);
        success &= builder.TryAddStateResource(
                2U,
                generation: 4U,
                in state,
                out uint stateIndex) &&
            builder.TryAddPathResource(
                3U,
                generation: 4U,
                paths,
                segments,
                out uint pathIndex) &&
            builder.TryDrawPath(
                1U,
                pathIndex,
                new NativeImageRect(2f, 2f, 6f, 6f),
                stateIndex: stateIndex);
        if (!success || !builder.TryBuild(out ReadOnlySpan<byte> stream))
        {
            return false;
        }
        compositor.UpdateScene(stream);
        try
        {
            compositor.RenderScene(
                target,
                dpiScale: 1f,
                SceneId,
                generation: 4U,
                clearColor: new Vector4(0f, 0f, 0f, 1f));
        }
        catch (NativeRendererException exception)
        {
            return exception.Status == NativeRendererStatus.Unsupported &&
                exception.Message.Contains(
                    "ordered disjoint segment ranges",
                    StringComparison.Ordinal);
        }
        return false;
    }

    private static PixelExtent Measure(byte[] pixels)
    {
        int minimumX = checked((int)Width);
        int minimumY = checked((int)Height);
        int maximumX = -1;
        int maximumY = -1;
        long redSum = 0;
        long greenSum = 0;
        for (int y = 0; y < Height; ++y)
        {
            for (int x = 0; x < Width; ++x)
            {
                int offset = (y * checked((int)Width) + x) * 4;
                int red = pixels[offset];
                int green = pixels[offset + 1];
                int blue = pixels[offset + 2];
                redSum += red;
                greenSum += green;
                if (red == 0 && green == 0 && blue == 0)
                    continue;
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        }
        return new PixelExtent(
            minimumX, minimumY, maximumX, maximumY, redSum, greenSum);
    }

    private static int CountChangedPixels(byte[] left, byte[] right)
    {
        int changed = 0;
        for (int offset = 0; offset < left.Length; offset += 4)
        {
            if (left[offset] != right[offset] ||
                left[offset + 1] != right[offset + 1] ||
                left[offset + 2] != right[offset + 2] ||
                left[offset + 3] != right[offset + 3])
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
        long RedSum,
        long GreenSum)
    {
        internal bool IsVisible => MaximumX >= MinimumX &&
            MaximumY >= MinimumY;

        public override string ToString() =>
            $"[{MinimumX},{MinimumY}]-[{MaximumX},{MaximumY}], " +
            $"red={RedSum}, green={GreenSum}";
    }
}
