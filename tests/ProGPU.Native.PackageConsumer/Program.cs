using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

if (args.Contains("--webgpu-init-only", StringComparer.Ordinal))
{
    Console.WriteLine(
        $"package-consumer: WebGPU init " +
        $"arch={RuntimeInformation.ProcessArchitecture}, " +
        $"temp={Path.GetTempPath()}");
    using var probeContext = new WgpuContext();
    Console.WriteLine("package-consumer: WebGPU context constructed");
    probeContext.Initialize(window: null);
    Console.WriteLine("ProGPU.Backend WebGPU initialization smoke passed.");
    return;
}

Console.WriteLine("package-consumer: native ABI");
NativeRendererInfo info = NativeCompositor.GetInfo();
if (info.AbiVersion != 3 ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.ExternalImageMask) ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.ExplicitQueueTimeline) ||
    !info.Capabilities.HasFlag(NativeRendererCapabilities.WpfMilChannel))
{
    throw new InvalidOperationException("The packaged native ABI is incomplete.");
}

bool milOnly = args.Contains("--mil-only", StringComparer.Ordinal);
bool renderOnly = args.Contains("--render-only", StringComparer.Ordinal);
bool gradientOnly = args.Contains("--mil-gradient-only", StringComparer.Ordinal);
bool geometryDrawingOnly = args.Contains(
    "--mil-geometry-drawing-only", StringComparer.Ordinal);
bool drawingGroupOnly = args.Contains(
    "--mil-drawing-group-only", StringComparer.Ordinal);
bool imageDrawingOnly = args.Contains(
    "--mil-image-drawing-only", StringComparer.Ordinal);
bool glyphRunDrawingOnly = args.Contains(
    "--mil-glyph-run-drawing-only", StringComparer.Ordinal);
bool drawingImageOnly = args.Contains(
    "--mil-drawing-image-only", StringComparer.Ordinal);
bool guidelineOnly = args.Contains(
    "--mil-guideline-only", StringComparer.Ordinal);
byte[]? compiledMilStream = null;
if (!renderOnly)
{
    bool arcGroupOnly =
        args.Contains("--mil-arc-group-only", StringComparer.Ordinal);
    bool arcBooleanOnly =
        args.Contains("--mil-arc-boolean-only", StringComparer.Ordinal);
    bool minimalArcGroup =
        args.Contains("--mil-arc-group-minimal", StringComparer.Ordinal);
    bool duplicateArcGroup =
        args.Contains("--mil-arc-group-duplicate", StringComparer.Ordinal);
    bool mixedArcGroup =
        args.Contains("--mil-arc-group-mixed", StringComparer.Ordinal);
    bool affineRecursiveOnly =
        args.Contains("--mil-affine-recursive-only", StringComparer.Ordinal);
    bool includeRecursiveGroupArc =
        !affineRecursiveOnly && !arcBooleanOnly;
    bool includeRecursiveBooleanArc =
        !affineRecursiveOnly && !arcGroupOnly;
    byte[] milBatch = guidelineOnly
        ? CreateMilGuidelineBatch()
        : drawingImageOnly
        ? CreateMilDrawingImageBatch()
        : glyphRunDrawingOnly
        ? CreateMilGlyphRunDrawingBatch()
        : imageDrawingOnly
        ? CreateMilImageDrawingBatch()
        : drawingGroupOnly
        ? CreateMilDrawingGroupBatch()
        : geometryDrawingOnly
            ? CreateMilGeometryDrawingBatch()
            : gradientOnly
            ? CreateMilGradientBatch()
            : CreateMilSeedBatch(
            includeRecursiveGroupArc,
            includeRecursiveBooleanArc,
            minimalArcGroup,
            duplicateArcGroup,
            mixedArcGroup);
    bool focusedMil = gradientOnly || geometryDrawingOnly ||
        drawingGroupOnly || imageDrawingOnly || glyphRunDrawingOnly ||
        drawingImageOnly || guidelineOnly;
    uint targetHandle = focusedMil ? 2U : 42U;
    uint visualHandle = focusedMil ? 1U : 41U;
    uint expectedCommandCount = guidelineOnly
        ? 19U
        : drawingImageOnly
        ? 19U
        : glyphRunDrawingOnly
        ? 14U
        : imageDrawingOnly
        ? 12U
        : drawingGroupOnly ? 25U : focusedMil ? 15U : 78U;
    uint expectedResourceCount = guidelineOnly
        ? 8U
        : drawingImageOnly
        ? 8U
        : glyphRunDrawingOnly
        ? 6U
        : imageDrawingOnly
        ? 5U
        : drawingGroupOnly ? 11U : focusedMil ? 6U : 36U;
    uint expectedRectangleCount = geometryDrawingOnly || drawingGroupOnly ||
        drawingImageOnly || guidelineOnly
        ? 1U
        : gradientOnly ? 2U : focusedMil ? 0U : 3U;
    uint expectedEllipseCount = gradientOnly ? 1U : focusedMil ? 0U : 4U;
    uint expectedRoundedRectangleCount = focusedMil ? 0U : 6U;
    uint expectedLineCount = focusedMil ? 0U : 3U;
    uint expectedBrushCount = imageDrawingOnly || glyphRunDrawingOnly
        ? 0U
        : gradientOnly ? 3U : 1U;
    using (var mil = new NativeMilChannel())
    {
        NativeMilBatchMetrics milMetrics = mil.Apply(milBatch);
        if (imageDrawingOnly)
        {
            BindFocusedBitmapSource(mil);
        }
        if (glyphRunDrawingOnly)
        {
            BindFocusedGlyphRunFont(mil);
        }
        if (drawingImageOnly)
        {
            BindFocusedDrawingImageBounds(mil);
        }
        NativeMilCompiledScene scene = mil.CompileScene(targetHandle, 701, 1);
        if (milMetrics.CommandCount != expectedCommandCount ||
            mil.ResourceCount != expectedResourceCount ||
            !mil.TryGetVisual(visualHandle, out NativeMilVisualSnapshot visual) ||
            visual.Handle != visualHandle || scene.Stream.Length == 0 ||
            scene.Metrics.VisualCount != 1 ||
            scene.Metrics.RectangleCount != expectedRectangleCount ||
            scene.Metrics.EllipseCount != expectedEllipseCount ||
            scene.Metrics.RoundedRectangleCount !=
                expectedRoundedRectangleCount ||
            scene.Metrics.LineCount != expectedLineCount ||
            scene.Metrics.BrushCount != expectedBrushCount)
        {
            throw new InvalidOperationException(
                "The packaged wgpu-native MIL channel is incomplete.");
        }
        compiledMilStream = scene.Stream;
    }
    Console.WriteLine("package-consumer: wgpu-native MIL");
    using (var dawnMil = new NativeMilChannel(NativeMilBackend.Dawn))
    {
        NativeMilBatchMetrics milMetrics = dawnMil.Apply(milBatch);
        if (imageDrawingOnly)
        {
            BindFocusedBitmapSource(dawnMil);
        }
        if (glyphRunDrawingOnly)
        {
            BindFocusedGlyphRunFont(dawnMil);
        }
        if (drawingImageOnly)
        {
            BindFocusedDrawingImageBounds(dawnMil);
        }
        NativeMilCompiledScene scene = dawnMil.CompileScene(
            targetHandle, 702, 1);
        if (milMetrics.CommandCount != expectedCommandCount ||
            dawnMil.ResourceCount != expectedResourceCount ||
            scene.Stream.Length == 0 || scene.Metrics.VisualCount != 1 ||
            scene.Metrics.RectangleCount != expectedRectangleCount ||
            scene.Metrics.EllipseCount != expectedEllipseCount ||
            scene.Metrics.RoundedRectangleCount !=
                expectedRoundedRectangleCount ||
            scene.Metrics.LineCount != expectedLineCount ||
            scene.Metrics.BrushCount != expectedBrushCount)
        {
            throw new InvalidOperationException(
                "The packaged Dawn MIL channel is incomplete.");
        }
    }
    Console.WriteLine("package-consumer: Dawn MIL");
}

NativeRendererInfo dawnInfo = NativeDawnAdapter.GetInfo();
if (dawnInfo.AbiVersion != 3 ||
    dawnInfo.BackendAbi != NativeDawnAdapter.BackendAbi ||
    NativeDawnAdapter.AdapterAbiVersion != 1 ||
    NativeDawnAdapter.RequiredProviderAbiVersion != 2 ||
    !dawnInfo.Name.Contains("Dawn provider", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "The packaged provider-resolved Dawn adapter is incomplete.");
}
Console.WriteLine("package-consumer: Dawn ABI");
if (milOnly)
{
    Console.WriteLine("ProGPU.Backend.Native MIL-only package smoke passed.");
    return;
}

using var context = new WgpuContext();
context.Initialize(window: null);
Console.WriteLine("package-consumer: WebGPU context");
using var target = new GpuTexture(
    context,
    64,
    64,
    TextureFormat.Rgba8Unorm,
    TextureUsage.RenderAttachment | TextureUsage.CopySrc,
    "Native package consumer target",
    alphaMode: GpuTextureAlphaMode.Premultiplied);
using var compositor = new NativeCompositor(context, TextureFormat.Rgba8Unorm);
if (compiledMilStream is not null)
{
    Console.WriteLine("package-consumer: retained MIL update begin");
    NativeSceneUpdateMetrics update = compositor.UpdateScene(compiledMilStream);
    Console.WriteLine("package-consumer: retained MIL render begin");
    NativeSceneFrameMetrics retainedMetrics = compositor.RenderScene(
        target,
        1f,
        701,
        1,
        new Vector4(0f, 0f, 0f, 1f));
    NativeSubmissionToken retainedSubmission =
        compositor.GetLastSubmissionToken();
    Console.WriteLine("package-consumer: retained MIL wait begin");
    if (!retainedSubmission.IsValid)
    {
        throw new InvalidOperationException(
            "The retained MIL renderer did not publish a submission token.");
    }
    compositor.WaitForSubmission(retainedSubmission);
    byte[] retainedPixels = target.ReadPixels();
    if (update.ResourceCount == 0 || update.DrawCount == 0 ||
        retainedMetrics.DrawCallCount == 0 ||
        !ContainsNonBlackPixel(retainedPixels))
    {
        throw new InvalidOperationException(
            "The packaged native renderer did not render the compiled retained MIL path scene.");
    }
    Console.WriteLine(
        $"package-consumer: retained MIL render " +
        $"resources={update.ResourceCount}, draws={retainedMetrics.DrawCallCount}, " +
        $"coverage={retainedMetrics.CoverageStagingBytes}");
}
NativeFrameMetrics metrics = compositor.Render(
    target,
    1f,
    [new NativeSolidRectangle(8, 8, 48, 48, new Vector4(1f, 0.25f, 0.1f, 1f))],
    new Vector4(0f, 0f, 0f, 1f));
Console.WriteLine("package-consumer: native render");
NativeSubmissionToken submission = compositor.GetLastSubmissionToken();
if (!submission.IsValid)
{
    throw new InvalidOperationException("The packaged renderer did not publish a submission token.");
}
compositor.WaitForSubmission(submission);
if (!compositor.IsSubmissionComplete(submission))
{
    throw new InvalidOperationException("The packaged renderer submission did not remain complete.");
}
using (var other = new NativeCompositor(context, TextureFormat.Rgba8Unorm))
{
    try
    {
        _ = other.IsSubmissionComplete(submission);
        throw new InvalidOperationException(
            "A submission token crossed compositor ownership domains.");
    }
    catch (ArgumentException)
    {
    }
}
byte[] pixels = target.ReadPixels();
if (metrics.DrawCallCount != 1 || pixels.All(static value => value == 0))
{
    throw new InvalidOperationException("The packaged native renderer did not draw.");
}

Console.WriteLine(
    $"ProGPU.Backend.Native package smoke passed: ABI {info.AbiVersion}, " +
    $"Dawn ABI {NativeDawnAdapter.AdapterAbiVersion}, " +
    $"draws={metrics.DrawCallCount}, pixels={pixels.Length}.");

static byte[] CreateMilGradientBatch()
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.DrawRectangle(4, 4, 56, 20, 4);
    renderData.DrawEllipse(32, 42, 24, 14, 5);
    renderData.DrawRectangle(8, 28, 12, 28, 6);
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(1, NativeMilResourceType.Visual);
    batch.CreateResource(2, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(3, NativeMilResourceType.RenderData);
    batch.CreateResource(4, NativeMilResourceType.LinearGradientBrush);
    batch.CreateResource(5, NativeMilResourceType.RadialGradientBrush);
    batch.CreateResource(6, NativeMilResourceType.SolidColorBrush);
    batch.CreateVisual(1);
    batch.SetVisualContent(1, 3);
    ReadOnlySpan<NativeMilGradientStop> stops =
    [
        new(0, new NativeMilColor(1, 0, 0, 1)),
        new(0.5, new NativeMilColor(0, 1, 0, 0.8f)),
        new(1, new NativeMilColor(0, 0, 1, 1))
    ];
    batch.SetLinearGradientBrush(
        4,
        new NativeMilLinearGradientBrush(
            new NativeMilPoint(0, 0),
            new NativeMilPoint(1, 0)),
        stops);
    batch.SetRadialGradientBrush(
        5,
        new NativeMilRadialGradientBrush(
            new NativeMilPoint(0.5, 0.5),
            new NativeMilPoint(0.4, 0.45),
            0.5,
            0.5),
        stops);
    batch.SetSolidColorBrush(6, new NativeMilColor(1, 1, 1, 1));
    batch.SetRenderData(3, renderData);
    batch.CreateGenericTarget(2, 64, 64);
    batch.SetTargetClearColor(2, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(2, 1);
    return batch.ToArray();
}

static byte[] CreateMilGeometryDrawingBatch()
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.DrawDrawing(6);
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(1, NativeMilResourceType.Visual);
    batch.CreateResource(2, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(3, NativeMilResourceType.RenderData);
    batch.CreateResource(4, NativeMilResourceType.SolidColorBrush);
    batch.CreateResource(5, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(6, NativeMilResourceType.GeometryDrawing);
    batch.CreateVisual(1);
    batch.SetVisualContent(1, 3);
    batch.SetSolidColorBrush(4, new NativeMilColor(0.1f, 0.6f, 1, 1));
    batch.SetRectangleGeometry(5, 8, 12, 48, 40);
    batch.SetGeometryDrawing(6, 4, 0, 5);
    batch.SetRenderData(3, renderData);
    batch.CreateGenericTarget(2, 64, 64);
    batch.SetTargetClearColor(2, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(2, 1);
    return batch.ToArray();
}

static byte[] CreateMilDrawingGroupBatch()
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.DrawDrawing(10);
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(1, NativeMilResourceType.Visual);
    batch.CreateResource(2, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(3, NativeMilResourceType.RenderData);
    batch.CreateResource(4, NativeMilResourceType.SolidColorBrush);
    batch.CreateResource(5, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(6, NativeMilResourceType.GeometryDrawing);
    batch.CreateResource(7, NativeMilResourceType.MatrixTransform);
    batch.CreateResource(8, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(9, NativeMilResourceType.DoubleResource);
    batch.CreateResource(10, NativeMilResourceType.DrawingGroup);
    batch.CreateResource(11, NativeMilResourceType.SolidColorBrush);
    batch.CreateVisual(1);
    batch.SetVisualContent(1, 3);
    batch.SetSolidColorBrush(4, new NativeMilColor(0.85f, 0.25f, 0.1f, 1));
    batch.SetRectangleGeometry(5, 8, 12, 48, 40);
    batch.SetGeometryDrawing(6, 4, 0, 5);
    batch.SetMatrixTransform(7, new NativeMilMatrix3x2(1, 0, 0, 1, 2, 4));
    batch.SetRectangleGeometry(8, 16, 16, 32, 32);
    batch.SetDoubleResource(9, 0.75);
    batch.SetSolidColorBrush(
        11,
        new NativeMilColor(1, 1, 1, 0.5f),
        opacity: 0.5);
    batch.SetDrawingGroup(
        10,
        new NativeMilDrawingGroup(
            Opacity: 1,
            ClipGeometryHandle: 8,
            OpacityAnimationHandle: 9,
            OpacityMaskHandle: 11,
            TransformHandle: 7,
            EdgeMode: NativeMilEdgeMode.Aliased,
            ClearTypeHint: NativeMilClearTypeHint.Enabled),
        [6]);
    batch.SetRenderData(3, renderData);
    batch.CreateGenericTarget(2, 64, 64);
    batch.SetTargetClearColor(2, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(2, 1);
    return batch.ToArray();
}

static byte[] CreateMilGuidelineBatch()
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.DrawDrawing(7);
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(1, NativeMilResourceType.Visual);
    batch.CreateResource(2, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(3, NativeMilResourceType.RenderData);
    batch.CreateResource(4, NativeMilResourceType.SolidColorBrush);
    batch.CreateResource(5, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(6, NativeMilResourceType.GeometryDrawing);
    batch.CreateResource(7, NativeMilResourceType.DrawingGroup);
    batch.CreateResource(8, NativeMilResourceType.GuidelineSet);
    batch.CreateVisual(1);
    batch.SetVisualContent(1, 3);
    batch.SetSolidColorBrush(4, new NativeMilColor(0.2f, 0.7f, 1, 1));
    batch.SetRectangleGeometry(5, 8.25, 12.5, 32, 24);
    batch.SetGeometryDrawing(6, 4, 0, 5);
    batch.SetGuidelineSet(8, false, [8.25], [12.5]);
    batch.SetDrawingGroup(
        7,
        new NativeMilDrawingGroup(GuidelineSetHandle: 8),
        [6]);
    batch.SetRenderData(3, renderData);
    batch.CreateGenericTarget(2, 64, 64);
    batch.SetTargetClearColor(2, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(2, 1);
    return batch.ToArray();
}

static byte[] CreateMilImageDrawingBatch()
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.DrawDrawing(5);
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(1, NativeMilResourceType.Visual);
    batch.CreateResource(2, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(3, NativeMilResourceType.RenderData);
    batch.CreateResource(4, NativeMilResourceType.BitmapSource);
    batch.CreateResource(5, NativeMilResourceType.ImageDrawing);
    batch.CreateVisual(1);
    batch.SetVisualContent(1, 3);
    batch.SetImageDrawing(5, 8, 12, 48, 40, 4);
    batch.SetRenderData(3, renderData);
    batch.CreateGenericTarget(2, 64, 64);
    batch.SetTargetClearColor(2, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(2, 1);
    return batch.ToArray();
}

static byte[] CreateMilGlyphRunDrawingBatch()
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.DrawDrawing(6);
    renderData.DrawGlyphRun(4, 5);
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(1, NativeMilResourceType.Visual);
    batch.CreateResource(2, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(3, NativeMilResourceType.RenderData);
    batch.CreateResource(4, NativeMilResourceType.SolidColorBrush);
    batch.CreateResource(6, NativeMilResourceType.GlyphRunDrawing);
    batch.CreateVisual(1);
    batch.SetVisualContent(1, 3);
    batch.SetSolidColorBrush(4, new NativeMilColor(0.2f, 0.6f, 1, 1));
    batch.SetGlyphRun(
        5,
        new NativeMilGlyphRun(
            new NativeMilPoint(8, 40),
            24,
            new NativeMilRect(8, 10, 48, 36)),
        [36, 37],
        ReadOnlySpan<float>.Empty,
        [new Vector2(0, 0), new Vector2(24, 0)]);
    batch.SetGlyphRunDrawing(6, 5, 4);
    batch.SetRenderData(3, renderData);
    batch.CreateGenericTarget(2, 64, 64);
    batch.SetTargetClearColor(2, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(2, 1);
    return batch.ToArray();
}

static byte[] CreateMilDrawingImageBatch()
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.DrawDrawing(8);
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(1, NativeMilResourceType.Visual);
    batch.CreateResource(2, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(3, NativeMilResourceType.RenderData);
    batch.CreateResource(4, NativeMilResourceType.SolidColorBrush);
    batch.CreateResource(5, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(6, NativeMilResourceType.GeometryDrawing);
    batch.CreateResource(7, NativeMilResourceType.DrawingImage);
    batch.CreateResource(8, NativeMilResourceType.ImageDrawing);
    batch.CreateVisual(1);
    batch.SetVisualContent(1, 3);
    batch.SetSolidColorBrush(4, new NativeMilColor(0.15f, 0.5f, 0.95f, 1));
    batch.SetRectangleGeometry(5, 10, 20, 20, 10);
    batch.SetGeometryDrawing(6, 4, 0, 5);
    batch.SetDrawingImage(7, 6);
    batch.SetImageDrawing(8, 2, 4, 40, 20, 7);
    batch.SetRenderData(3, renderData);
    batch.CreateGenericTarget(2, 64, 64);
    batch.SetTargetClearColor(2, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(2, 1);
    return batch.ToArray();
}

static void BindFocusedDrawingImageBounds(NativeMilChannel channel)
{
    channel.SetDrawingImageBounds(7, new NativeMilRect(10, 20, 20, 10));
}

static void BindFocusedBitmapSource(NativeMilChannel channel)
{
    const uint width = 4;
    const uint height = 4;
    const uint rowBytes = width * 4;
    byte[] pixels = new byte[rowBytes * height];
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int offset = checked((int)(y * rowBytes + x * 4));
            pixels[offset] = checked((byte)(48 + x * 48));
            pixels[offset + 1] = checked((byte)(32 + y * 56));
            pixels[offset + 2] = checked((byte)(224 - x * 32));
            pixels[offset + 3] = 255;
        }
    }
    channel.SetBitmapSourceRgba8(4, width, height, rowBytes, pixels);
}

static void BindFocusedGlyphRunFont(NativeMilChannel channel)
{
    byte[] fontBytes = File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "Inter-Regular.ttf"));
    channel.SetGlyphRunFontSfnt(
        5,
        fontBytes,
        faceIndex: 0,
        styleSimulations:
            NativeMilGlyphStyleSimulations.Bold |
            NativeMilGlyphStyleSimulations.Italic);
}

static byte[] CreateMilSeedBatch(
    bool includeRecursiveGroupArc,
    bool includeRecursiveBooleanArc,
    bool minimalArcGroup,
    bool duplicateArcGroup,
    bool mixedArcGroup)
{
    var renderData = new NativeMilRenderDataBuilder();
    renderData.PushTransform(45);
    renderData.PushClip(61);
    renderData.PushClip(52);
    renderData.DrawRectangle(8, 8, 48, 48, 44, 46);
    renderData.DrawLine(8, 8, 56, 56, 46);
    renderData.DrawLine(24, 24, 24, 24, 48);
    renderData.DrawEllipse(32, 32, 16, 12, 44, 48);
    renderData.DrawEllipse(32, 20, 10, 0, 44, 48);
    renderData.DrawRoundedRectangle(12, 16, 40, 32, 8, 8, 0, 48);
    renderData.DrawRoundedRectangle(20, 12, 24, 20, 6, 3, 44, 48);
    renderData.DrawRoundedRectangle(44, 8, 12, 16, 0, 4, 44, 48);
    renderData.DrawRectangle(16, 20, 0, 16, 0, 67);
    renderData.DrawRoundedRectangle(24, 20, 0, 16, 6, 6, 0, 48);
    renderData.DrawGeometry(0, 48, 49);
    renderData.DrawGeometry(44, 48, 50);
    renderData.DrawGeometry(44, 48, 51);
    renderData.DrawGeometry(44, 0, 52);
    renderData.DrawGeometry(44, 0, 54);
    renderData.DrawGeometry(44, 0, 55);
    renderData.DrawGeometry(0, 46, 56);
    renderData.DrawGeometry(0, 48, 59);
    renderData.DrawGeometry(0, 48, 60);
    renderData.DrawGeometry(0, 65, 63);
    renderData.DrawGeometry(44, 48, 66);
    renderData.DrawGeometry(0, 48, 68);
    renderData.DrawGeometry(44, 48, 69);
    renderData.Pop();
    renderData.Pop();
    renderData.Pop();
    renderData.PushTransform(62);
    renderData.DrawLine(4, 4, 60, 60, 46);
    renderData.DrawGeometry(44, 48, 54);
    renderData.DrawGeometry(44, 48, 55);
    renderData.DrawGeometry(44, 48, 52);
    renderData.DrawGeometry(44, 48, 50);
    renderData.Pop();
    var batch = new NativeMilBatchBuilder();
    batch.CreateResource(41, NativeMilResourceType.Visual);
    batch.CreateResource(42, NativeMilResourceType.GenericRenderTarget);
    batch.CreateResource(43, NativeMilResourceType.RenderData);
    batch.CreateResource(44, NativeMilResourceType.SolidColorBrush);
    batch.CreateResource(45, NativeMilResourceType.MatrixTransform);
    batch.CreateResource(46, NativeMilResourceType.Pen);
    batch.CreateResource(47, NativeMilResourceType.DashStyle);
    batch.CreateResource(48, NativeMilResourceType.Pen);
    batch.CreateResource(49, NativeMilResourceType.LineGeometry);
    batch.CreateResource(50, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(51, NativeMilResourceType.EllipseGeometry);
    batch.CreateResource(52, NativeMilResourceType.PathGeometry);
    batch.CreateResource(53, NativeMilResourceType.PathGeometry);
    batch.CreateResource(54, NativeMilResourceType.GeometryGroup);
    batch.CreateResource(55, NativeMilResourceType.CombinedGeometry);
    batch.CreateResource(56, NativeMilResourceType.PathGeometry);
    batch.CreateResource(57, NativeMilResourceType.GeometryGroup);
    batch.CreateResource(58, NativeMilResourceType.CombinedGeometry);
    batch.CreateResource(59, NativeMilResourceType.PathGeometry);
    batch.CreateResource(60, NativeMilResourceType.PathGeometry);
    batch.CreateResource(61, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(62, NativeMilResourceType.MatrixTransform);
    batch.CreateResource(63, NativeMilResourceType.PathGeometry);
    batch.CreateResource(64, NativeMilResourceType.DashStyle);
    batch.CreateResource(65, NativeMilResourceType.Pen);
    batch.CreateResource(66, NativeMilResourceType.EllipseGeometry);
    batch.CreateResource(67, NativeMilResourceType.Pen);
    batch.CreateResource(68, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(69, NativeMilResourceType.RectangleGeometry);
    batch.CreateResource(70, NativeMilResourceType.TransformGroup);
    batch.CreateResource(71, NativeMilResourceType.TranslateTransform);
    batch.CreateResource(72, NativeMilResourceType.ScaleTransform);
    batch.CreateResource(73, NativeMilResourceType.SkewTransform);
    batch.CreateResource(74, NativeMilResourceType.RotateTransform);
    batch.CreateResource(75, NativeMilResourceType.DoubleResource);
    batch.CreateResource(76, NativeMilResourceType.MatrixResource);
    batch.CreateVisual(41);
    batch.SetVisualOffset(41, 1, 2);
    batch.SetMatrixTransform(
        45,
        new NativeMilMatrix3x2(1, 0, 0, 1, 99, 99),
        76);
    batch.SetMatrixTransform(
        62,
        new NativeMilMatrix3x2(1, 0, 0, 0, 0, 0));
    batch.SetDoubleResource(75, 0);
    batch.SetMatrixResource(
        76,
        new NativeMilMatrix3x2(1, 0, 0, 1, 1, 1));
    batch.SetTranslateTransform(71, 99, 0, 75);
    batch.SetScaleTransform(72, 1, 1, 8, 12);
    batch.SetSkewTransform(73, 0, 0, 8, 12);
    batch.SetRotateTransform(74, 0, 8, 12);
    batch.SetTransformGroup(70, [45, 71, 72, 73, 74]);
    batch.SetVisualTransform(41, 70);
    batch.SetVisualOpacity(41, 0.9);
    batch.SetVisualContent(41, 43);
    batch.SetSolidColorBrush(44, new NativeMilColor(1, 0.25f, 0.1f, 1));
    batch.SetDashStyle(47, 0.5, [2.0, 1.0]);
    batch.SetPen(
        46,
        new NativeMilPen(
            44,
            2,
            NativeMilPenLineCap.Square,
            NativeMilPenLineCap.Round,
            DashStyleHandle: 47));
    batch.SetPen(
        48,
        new NativeMilPen(
            44,
            2,
            NativeMilPenLineCap.Round,
            NativeMilPenLineCap.Triangle));
    batch.SetDashStyle(64, 3, [3.0, 1.0]);
    batch.SetPen(
        65,
        new NativeMilPen(
            44,
            2,
            NativeMilPenLineCap.Round,
            NativeMilPenLineCap.Triangle,
            DashStyleHandle: 64));
    batch.SetPen(
        67,
        new NativeMilPen(
            44,
            2,
            LineJoin: NativeMilPenLineJoin.Round));
    batch.SetLineGeometry(49, 8, 56, 56, 8, 45);
    batch.SetRectangleGeometry(50, 12, 16, 40, 32, 8, 4, 45);
    batch.SetEllipseGeometry(51, 32, 32, 16, 12, 45);
    batch.SetEllipseGeometry(66, 20, 40, 0, 8, 45);
    batch.SetRectangleGeometry(68, 40, 20, 0, 16, transformHandle: 45);
    batch.SetRectangleGeometry(69, 44, 30, 12, 16, 0, 4, 45);
    batch.SetPathGeometry(
        52,
        CreateMilPath(0));
    batch.SetPathGeometry(
        53,
        CreateMilAffinePath(8),
        45);
    batch.SetGeometryGroup(
        57,
        NativeMilPathFillRule.EvenOdd,
        includeRecursiveGroupArc || includeRecursiveBooleanArc
            ? [52]
            : [53],
        45);
    batch.SetGeometryGroup(
        54,
        NativeMilPathFillRule.EvenOdd,
        minimalArcGroup
            ? [57]
            : mixedArcGroup
                ? [53, 57]
            : duplicateArcGroup
                ? [52, 57]
            : includeRecursiveGroupArc
                ? [53, 50, 51, 57]
            : [52, 53, 50, 51],
        45);
    batch.SetCombinedGeometry(
        58,
        NativeMilGeometryCombineMode.Intersect,
        includeRecursiveBooleanArc ? 57U : 53U,
        50,
        45);
    batch.SetCombinedGeometry(
        55,
        NativeMilGeometryCombineMode.Exclude,
        58,
        51,
        45);
    batch.SetPathGeometry(
        56,
        CreateMilLineStrokePath(),
        45);
    batch.SetPathGeometry(
        59,
        CreateMilArcStrokePath(),
        45);
    batch.SetPathGeometry(
        60,
        CreateMilJoinedCurveStrokePath(),
        45);
    batch.SetPathGeometry(
        63,
        CreateMilDegenerateStrokePath(),
        45);
    batch.SetRectangleGeometry(61, -1000, -1000, 2000, 2000);
    batch.SetRenderData(43, renderData);
    batch.CreateGenericTarget(42, 64, 64);
    batch.SetTargetClearColor(42, new NativeMilColor(0, 0, 0, 1));
    batch.SetTargetRoot(42, 41);
    return batch.ToArray();
}

static NativeMilPathGeometry CreateMilLineStrokePath()
{
    return new NativeMilPathGeometry(
        NativeMilPathFillRule.EvenOdd,
        4,
        4,
        60,
        60,
        [
            new NativeMilPathFigure(
                new NativeMilPoint(4, 4),
                IsFilled: false,
                IsClosed: true,
                [
                    NativeMilPathSegment.Line(
                        new NativeMilPoint(60, 4)),
                    NativeMilPathSegment.Line(
                        new NativeMilPoint(60, 60),
                        isStroked: false),
                    NativeMilPathSegment.Line(new NativeMilPoint(4, 60))
                ])
        ]);
}

static NativeMilPathGeometry CreateMilArcStrokePath()
{
    return new NativeMilPathGeometry(
        NativeMilPathFillRule.Nonzero,
        8,
        8,
        48,
        40,
        [
            new NativeMilPathFigure(
                new NativeMilPoint(12, 32),
                IsFilled: false,
                IsClosed: false,
                [
                    NativeMilPathSegment.Arc(
                        new NativeMilPoint(52, 32),
                        20,
                        12,
                        20,
                        isLargeArc: false,
                        isClockwise: true)
                ])
        ]);
}

static NativeMilPathGeometry CreateMilJoinedCurveStrokePath()
{
    return new NativeMilPathGeometry(
        NativeMilPathFillRule.Nonzero,
        8,
        6,
        48,
        46,
        [
            new NativeMilPathFigure(
                new NativeMilPoint(10, 44),
                IsFilled: false,
                IsClosed: true,
                [
                    NativeMilPathSegment.Line(
                        new NativeMilPoint(10, 18),
                        isSmoothJoin: true),
                    NativeMilPathSegment.QuadraticBezier(
                        new NativeMilPoint(30, 6),
                        new NativeMilPoint(48, 18)),
                    NativeMilPathSegment.CubicBezier(
                        new NativeMilPoint(54, 28),
                        new NativeMilPoint(38, 48),
                        new NativeMilPoint(10, 44))
                ])
        ]);
}

static NativeMilPathGeometry CreateMilDegenerateStrokePath()
{
    return new NativeMilPathGeometry(
        NativeMilPathFillRule.Nonzero,
        30,
        30,
        10,
        10,
        [
            new NativeMilPathFigure(
                new NativeMilPoint(30, 30),
                IsFilled: false,
                IsClosed: false,
                [
                    NativeMilPathSegment.Line(
                        new NativeMilPoint(30, 30))
                ]),
            new NativeMilPathFigure(
                new NativeMilPoint(40, 40),
                IsFilled: false,
                IsClosed: true,
                [
                    NativeMilPathSegment.Line(
                        new NativeMilPoint(40, 40))
                ])
        ]);
}

static NativeMilPathGeometry CreateMilAffinePath(double offsetX)
{
    return new NativeMilPathGeometry(
        NativeMilPathFillRule.Nonzero,
        8 + offsetX,
        4,
        42,
        44,
        [
            new NativeMilPathFigure(
                new NativeMilPoint(10 + offsetX, 44),
                IsFilled: true,
                IsClosed: true,
                [
                    NativeMilPathSegment.Line(
                        new NativeMilPoint(10 + offsetX, 16)),
                    NativeMilPathSegment.QuadraticBezier(
                        new NativeMilPoint(32 + offsetX, 4),
                        new NativeMilPoint(48 + offsetX, 16)),
                    NativeMilPathSegment.CubicBezier(
                        new NativeMilPoint(52 + offsetX, 24),
                        new NativeMilPoint(40 + offsetX, 40),
                        new NativeMilPoint(10 + offsetX, 44))
                ])
        ]);
}

static NativeMilPathGeometry CreateMilPath(double offsetX)
{
    return new NativeMilPathGeometry(
        NativeMilPathFillRule.Nonzero,
        8 + offsetX,
        4,
        42,
        46,
        [
            new NativeMilPathFigure(
                new NativeMilPoint(10 + offsetX, 48),
                IsFilled: true,
                IsClosed: true,
                [
                    NativeMilPathSegment.Line(
                        new NativeMilPoint(10 + offsetX, 16)),
                    NativeMilPathSegment.QuadraticBezier(
                        new NativeMilPoint(32 + offsetX, 4),
                        new NativeMilPoint(48 + offsetX, 16)),
                    NativeMilPathSegment.Arc(
                        new NativeMilPoint(10 + offsetX, 48),
                        24,
                        20,
                        15,
                        isLargeArc: false,
                        isClockwise: true)
                ])
        ]);
}

static bool ContainsNonBlackPixel(ReadOnlySpan<byte> pixels)
{
    for (int index = 0; index + 3 < pixels.Length; index += 4)
    {
        if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0)
        {
            return true;
        }
    }
    return false;
}
