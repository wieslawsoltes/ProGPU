using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class PerPointPathGuidelineQualification
{
    private const uint Width = 40U;
    private const uint Height = 28U;
    private const ulong SceneId = 0x504F494E54475549UL;
    private const ulong MilArcSceneId = 0x4D494C4152434755UL;

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

        // Qualify the path pipeline before measuring the baseline. The
        // CPU-only D3D12 adapter can complete its first tiny submission and
        // readback before the lazily compiled path pipeline contributes any
        // coverage; the immediately following submissions are stable. This
        // discarded frame uses the identical GPU scene and does not relax any
        // measured extent, color, deformation, or reference comparison.
        _ = Render(
            compositor, context, target, 1U, guided: false,
            reference: false);
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

        (PixelExtent Baseline, PixelExtent Guided, PixelExtent Reference,
            int Changed, int ReferenceChanged) milArc =
            QualifyMilArc(compositor, context, target);

        Require(
            SharedSegmentPathResourceIsRejected(compositor, target),
            "per-point deformation must reject shared path segment ranges");

        Console.WriteLine(
            "Qualified live per-point static multi-guideline path " +
            $"deformation on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; baseline={baselineExtent}, " +
            $"guided={guidedExtent}, reference={referenceExtent}, " +
            $"changed={changed}, referenceChanged={referenceChanged}; " +
            $"MIL arc baseline={milArc.Baseline}, " +
            $"guided={milArc.Guided}, reference={milArc.Reference}, " +
            $"changed={milArc.Changed}, " +
            $"referenceChanged={milArc.ReferenceChanged}.");
    }

    private static (PixelExtent Baseline, PixelExtent Guided,
        PixelExtent Reference, int Changed, int ReferenceChanged)
        QualifyMilArc(
            NativeCompositor compositor,
            WgpuContext context,
            GpuTexture target)
    {
        (NativeSceneUpdateMetrics Update, NativeSceneFrameMetrics Frame,
            byte[] Pixels) baseline = RenderMilArc(
                compositor, context, target, 1U, guided: false,
                reference: false);
        (NativeSceneUpdateMetrics Update, NativeSceneFrameMetrics Frame,
            byte[] Pixels) guided = RenderMilArc(
                compositor, context, target, 2U, guided: true,
                reference: false);
        (NativeSceneUpdateMetrics Update, NativeSceneFrameMetrics Frame,
            byte[] Pixels) reference = RenderMilArc(
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
            "the live retained WPF MIL arc did not match its independent " +
            $"pre-deformed cubic reference: baseline={baselineExtent}, " +
            $"guided={guidedExtent}, reference={referenceExtent}, " +
            $"changed={changed}, referenceChanged={referenceChanged}");
        return (baselineExtent, guidedExtent, referenceExtent, changed,
            referenceChanged);
    }

    private static (NativeSceneUpdateMetrics Update,
        NativeSceneFrameMetrics Frame, byte[] Pixels) RenderMilArc(
        NativeCompositor compositor,
        WgpuContext context,
        GpuTexture target,
        ulong generation,
        bool guided,
        bool reference)
    {
        byte[] scene = BuildMilArcScene(generation, guided, reference);
        NativeSceneUpdateMetrics update = compositor.UpdateScene(scene);
        NativeSceneFrameMetrics frame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            MilArcSceneId,
            generation,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        return (update, frame, target.ReadPixels());
    }

    private static byte[] BuildMilArcScene(
        ulong generation,
        bool guided,
        bool reference)
    {
        const uint visualHandle = 1U;
        const uint targetHandle = 2U;
        const uint contentHandle = 3U;
        const uint brushHandle = 4U;
        const uint geometryHandle = 5U;
        const double startX = 18.25;
        const double startY = 8.25;
        const double endX = 26.25;
        const double endY = 16.25;
        // WPF's ArcToBezier quarter-circle control distance. These guides are
        // intentionally independent of the native lowering implementation.
        const double quarterControl = 0.5522847498307936;
        double control1X = startX + 8.0 * quarterControl;
        double control2Y = endY - 8.0 * quarterControl;
        NativeMilPathFigure figure = reference
            ? new NativeMilPathFigure(
                new NativeMilPoint(18.0, 8.0),
                IsFilled: true,
                IsClosed: true,
                [NativeMilPathSegment.CubicBezier(
                    new NativeMilPoint(23.0, 8.0),
                    new NativeMilPoint(26.0, 12.0),
                    new NativeMilPoint(26.0, 16.0))])
            : new NativeMilPathFigure(
                new NativeMilPoint(startX, startY),
                IsFilled: true,
                IsClosed: true,
                [NativeMilPathSegment.Arc(
                    new NativeMilPoint(endX, endY),
                    radiusX: 8.0,
                    radiusY: 8.0,
                    rotationAngle: 0.0,
                    isLargeArc: false,
                    isClockwise: true)]);
        var geometry = new NativeMilPathGeometry(
            NativeMilPathFillRule.Nonzero,
            reference ? 18.0 : startX,
            reference ? 8.0 : startY,
            8.0,
            8.0,
            [figure]);
        var renderData = new NativeMilRenderDataBuilder();
        renderData.DrawGeometry(brushHandle, 0U, geometryHandle);
        var batch = new NativeMilBatchBuilder();
        batch.CreateResource(visualHandle, NativeMilResourceType.Visual);
        batch.CreateResource(
            targetHandle, NativeMilResourceType.GenericRenderTarget);
        batch.CreateResource(contentHandle, NativeMilResourceType.RenderData);
        batch.CreateResource(
            brushHandle, NativeMilResourceType.SolidColorBrush);
        batch.CreateResource(
            geometryHandle, NativeMilResourceType.PathGeometry);
        batch.CreateVisual(visualHandle);
        batch.SetVisualContent(visualHandle, contentHandle);
        if (guided)
        {
            batch.SetVisualGuidelines(
                visualHandle,
                [startX, control1X, endX],
                [startY, control2Y, endY]);
        }
        batch.SetSolidColorBrush(
            brushHandle, new NativeMilColor(1f, 0f, 0f, 1f));
        batch.SetPathGeometry(geometryHandle, geometry);
        batch.SetRenderData(contentHandle, renderData);
        batch.CreateGenericTarget(targetHandle, Width, Height);
        batch.SetTargetClearColor(
            targetHandle, new NativeMilColor(0f, 0f, 0f, 1f));
        batch.SetTargetRoot(targetHandle, visualHandle);

        using var channel = new NativeMilChannel();
        _ = channel.Apply(batch.WrittenSpan);
        NativeMilCompiledScene scene = channel.CompileScene(
            targetHandle, MilArcSceneId, generation);
        Require(
            scene.Stream.Length > 0 && scene.Metrics.VisualCount == 1U &&
            scene.Metrics.BrushCount == 1U,
            "the retained WPF MIL arc did not compile to a semantic scene");
        return scene.Stream;
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
