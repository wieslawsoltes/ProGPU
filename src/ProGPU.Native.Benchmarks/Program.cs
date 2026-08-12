using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using Silk.NET.WebGPU;

const uint width = 960;
const uint height = 540;
int rectangleCount = ReadPositiveArgument("--rectangles", 384);
int warmupCount = ReadNonNegativeArgument("--warmup", 60);
int iterationCount = ReadPositiveArgument("--iterations", 300);
float dpiScale = ReadPositiveFloatArgument("--dpi", 1f);
float logicalWidth = width / dpiScale;
float logicalHeight = height / dpiScale;
bool synchronizeEachFrame = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--sync",
        StringComparison.OrdinalIgnoreCase));
bool drainEachPair = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--drain-each-pair",
        StringComparison.OrdinalIgnoreCase));
bool groupMeasurements = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--grouped",
        StringComparison.OrdinalIgnoreCase));
bool managedGroupFirst = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--managed-first",
        StringComparison.OrdinalIgnoreCase));
if (drainEachPair && (synchronizeEachFrame || groupMeasurements))
{
    throw new ArgumentException(
        "--drain-each-pair cannot be combined with --sync or --grouped.");
}
bool useAnalyticScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--analytic",
        StringComparison.OrdinalIgnoreCase));
bool useGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry",
        StringComparison.OrdinalIgnoreCase));
bool useCurveGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry-curves",
        StringComparison.OrdinalIgnoreCase));
bool usePolylineGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry-polylines",
        StringComparison.OrdinalIgnoreCase));
bool useSplineGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry-splines",
        StringComparison.OrdinalIgnoreCase));
bool useDashedGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry-dashes",
        StringComparison.OrdinalIgnoreCase));
bool usePathScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--paths",
        StringComparison.OrdinalIgnoreCase));
bool useGlyphScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--glyphs",
        StringComparison.OrdinalIgnoreCase));
bool enableManagedCompiledSceneCache = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--managed-compiled-scene-cache",
        StringComparison.OrdinalIgnoreCase));
useGeometryScene |= useCurveGeometryScene || usePolylineGeometryScene ||
    useSplineGeometryScene || useDashedGeometryScene;
bool writeImages = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--write-images",
        StringComparison.OrdinalIgnoreCase));
int analyticKind = ReadArgument("--analytic-kind", -1);
int geometryKind = ReadArgument("--geometry-kind", -1);
int geometryLineMode = ReadArgument("--geometry-line-mode", -1);
int geometryStartCap = ReadArgument("--geometry-start-cap", -1);
int geometryEndCap = ReadArgument("--geometry-end-cap", -1);
int geometryJoin = ReadArgument("--geometry-join", -1);

NativeSolidRectangle[] rectangles = CreateRectangles(
    rectangleCount,
    logicalWidth,
    logicalHeight);
NativeAnalyticPrimitive[] analyticPrimitives = useAnalyticScene
    ? CreateAnalyticPrimitives(
        rectangleCount,
        analyticKind,
        logicalWidth,
        logicalHeight)
    : [];
NativeGeometryPrimitive[] geometryPrimitives = useGeometryScene
    && !usePolylineGeometryScene
    && !useDashedGeometryScene
    && !useSplineGeometryScene
    ? CreateGeometryPrimitives(
        rectangleCount,
        geometryKind,
        geometryLineMode,
        geometryStartCap,
        geometryEndCap,
        useCurveGeometryScene,
        logicalWidth,
        logicalHeight)
    : [];
(NativePathFill[] nativePaths, NativePathSegment[] nativePathSegments) =
    usePathScene
        ? CreateNativePaths(rectangleCount, logicalWidth, logicalHeight)
        : ([], []);
TtfFont? glyphFont = useGlyphScene ? InterFontFamily.Regular : null;
(NativeGlyphOutline[] nativeGlyphOutlines,
 NativePathSegment[] nativeGlyphSegments,
 NativePositionedGlyph[] nativeGlyphs,
 ushort[] managedGlyphIndices,
 Vector2[] managedGlyphPositions) = useGlyphScene
    ? CreateGlyphScene(glyphFont!, rectangleCount, dpiScale, logicalWidth, logicalHeight)
    : ([], [], [], [], []);
(Vector2[] geometryPoints,
 NativePolyline[] geometryPolylines,
 double[] geometryDoubles,
 NativeDashStyle[] geometryDashStyles,
 NativeSpline[] geometrySplines) =
    useDashedGeometryScene
        ? CreateDashedPolylines(
            rectangleCount,
            geometryLineMode,
            geometryStartCap,
            geometryEndCap,
            geometryJoin,
            logicalWidth,
            logicalHeight)
        : useSplineGeometryScene
        ? CreateSplines(
            rectangleCount,
            geometryLineMode,
            geometryStartCap,
            geometryEndCap,
            geometryJoin,
            logicalWidth,
            logicalHeight)
        : usePolylineGeometryScene
        ? CreatePolylines(
            rectangleCount,
            geometryLineMode,
            geometryStartCap,
            geometryEndCap,
            geometryJoin,
            logicalWidth,
            logicalHeight)
        : ([], [], [], [], []);
Vector4 clearColor = new(0.015f, 0.02f, 0.035f, 1f);
uint nativeVertexCount = 0;
uint nativeIndexCount = 0;
int managedVertexCount = 0;
int managedIndexCount = 0;
uint nativeRasterizedPathCount = 0;
ulong nativePathUploadBytes = 0;
ulong nativeCoverageStagingBytes = 0;
uint nativeRasterizedGlyphCount = 0;
ulong nativeGlyphOutlineUploadBytes = 0;
ulong nativeGlyphInstanceUploadBytes = 0;

using var context = new WgpuContext();
context.Initialize(window: null);
using var nativeTarget = CreateTarget(context, "Native benchmark target");
using var managedTarget = CreateTarget(context, "Managed benchmark target");
using var native = new NativeCompositor(context, TextureFormat.Rgba8Unorm);
using var managed = new Compositor(
    context,
    TextureFormat.Rgba8Unorm,
    CompositorOptions.Default with
    {
        // The DrawingVisual already retains its command compilation. Keep the
        // additional whole-scene cache opt-in so its overhead can be measured
        // independently instead of changing the established baseline.
        EnableCompiledSceneCache = enableManagedCompiledSceneCache,
        EnableGpuHitTesting = false,
        PrimarySampleCount = 1
    });
DrawingVisual managedVisual = useGlyphScene
    ? CreateManagedGlyphVisual(
        glyphFont!,
        managedGlyphIndices,
        managedGlyphPositions,
        logicalWidth,
        logicalHeight)
    : usePathScene
    ? CreateManagedPathVisual(rectangleCount, logicalWidth, logicalHeight)
    : useGeometryScene
    ? CreateManagedGeometryVisual(
        geometryPrimitives,
        geometryPoints,
        geometryPolylines,
        geometryDoubles,
        geometryDashStyles,
        geometrySplines,
        logicalWidth,
        logicalHeight)
    : useAnalyticScene
        ? CreateManagedAnalyticVisual(
        analyticPrimitives,
        logicalWidth,
        logicalHeight)
        : CreateManagedVisual(rectangles, logicalWidth, logicalHeight);

// Compile both shader/pipeline paths before correctness or timing evidence.
RenderNative();
RenderManaged();
context.PollDevice(wait: true);

// Compare a second fully warmed submission, not the pipeline's first draw.
ulong nativePayloadHash = RenderNative(capturePayloadHash: true);
RenderManaged();
context.PollDevice(wait: true);

byte[] nativePixels = nativeTarget.ReadPixels();
byte[] managedPixels = managedTarget.ReadPixels();
if (writeImages)
{
    Directory.CreateDirectory("artifacts/progpu-native/differential");
    string imageStem = useGlyphScene
        ? "glyphs"
        : usePathScene
        ? "paths"
        : useDashedGeometryScene ? "dashes" : "latest";
    WritePpm(
        $"artifacts/progpu-native/differential/{imageStem}-native.ppm",
        nativePixels,
        width,
        height);
    WritePpm(
        $"artifacts/progpu-native/differential/{imageStem}-managed.ppm",
        managedPixels,
        width,
        height);
    WriteDifferencePpm(
        $"artifacts/progpu-native/differential/{imageStem}-difference-64x.ppm",
        nativePixels,
        managedPixels,
        width,
        height,
        amplification: 64);
}
PixelComparison comparison = ComparePixels(nativePixels, managedPixels);
bool requiresExactPixels = useGlyphScene ||
    (!useAnalyticScene && !useGeometryScene && !usePathScene && dpiScale == 1f);
bool usesGeometryDifferential = useGeometryScene || usePathScene;
bool usesTightDifferential =
    (useAnalyticScene && analyticKind is 1 or 2) ||
    (!useAnalyticScene && !useGeometryScene && !requiresExactPixels);
int maximumAllowedDifference = requiresExactPixels
    ? 0
    : usesGeometryDifferential ? 204 : usesTightDifferential ? 3 : 96;
int maximumAllowedPixelsOverTolerance =
    usesGeometryDifferential
        ? useSplineGeometryScene || useDashedGeometryScene
            ? Math.Max(1, rectangleCount / 32)
            : 1
        : requiresExactPixels || usesTightDifferential
        ? 0
        : comparison.PixelCount / 40;
double maximumAllowedMeanAbsoluteDifference = requiresExactPixels
    ? 0.0
    // The independently expanded paths can differ by one byte on shared AA
    // edge ties. Keep the aggregate budget below 0.004/255 per channel while
    // retaining the stricter high-difference pixel limit above.
    : useDashedGeometryScene ? 0.004
    : usesGeometryDifferential || usesTightDifferential ? 0.001 : 0.15;
if (comparison.MaximumChannelDifference > maximumAllowedDifference ||
    comparison.PixelsOverTolerance > maximumAllowedPixelsOverTolerance ||
    comparison.MeanAbsoluteChannelDifference >
        maximumAllowedMeanAbsoluteDifference)
{
    throw new InvalidOperationException(
        $"Native/managed output diverged: max={comparison.MaximumChannelDifference}, " +
        $"pixelsOverTolerance={comparison.PixelsOverTolerance}/{comparison.PixelCount}; " +
        $"meanAbsolute={comparison.MeanAbsoluteChannelDifference:F6}; " +
        $"allowedMax={maximumAllowedDifference}, " +
        $"allowedPixels={maximumAllowedPixelsOverTolerance}, " +
        $"allowedMean={maximumAllowedMeanAbsoluteDifference:F6}; " +
        $"nativePayloadHash={nativePayloadHash:X16}; " +
        $"nativeHash={comparison.NativeFnv1A64}, " +
        $"managedHash={comparison.ManagedFnv1A64}.");
}

for (int index = 0; index < warmupCount; index++)
{
    if ((index & 1) == 0)
    {
        RenderNative();
        SynchronizeIfRequested();
        RenderManaged();
        SynchronizeIfRequested();
    }
    else
    {
        RenderManaged();
        SynchronizeIfRequested();
        RenderNative();
        SynchronizeIfRequested();
    }

    if (drainEachPair)
    {
        context.PollDevice(wait: true);
    }
}
context.PollDevice(wait: true);

var nativeTimes = new double[iterationCount];
var managedTimes = new double[iterationCount];
var nativeSubmissionTimes = new double[iterationCount];
var nativeCompletionWaitTimes = new double[iterationCount];
var managedSubmissionTimes = new double[iterationCount];
var managedCompletionWaitTimes = new double[iterationCount];
long nativeAllocationStart = GC.GetAllocatedBytesForCurrentThread();
long nativeAllocatedBytes = 0;
long managedAllocatedBytes = 0;
if (groupMeasurements)
{
    void MeasureNativeGroup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        for (int index = 0; index < iterationCount; index++)
        {
            nativeTimes[index] = MeasureNative(
                out long allocated,
                out nativeSubmissionTimes[index],
                out nativeCompletionWaitTimes[index]);
            nativeAllocatedBytes += allocated;
        }
    }

    void MeasureManagedGroup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        for (int index = 0; index < iterationCount; index++)
        {
            managedTimes[index] = MeasureManaged(
                out long allocated,
                out managedSubmissionTimes[index],
                out managedCompletionWaitTimes[index]);
            managedAllocatedBytes += allocated;
        }
    }

    if (managedGroupFirst)
    {
        MeasureManagedGroup();
        context.PollDevice(wait: true);
        MeasureNativeGroup();
    }
    else
    {
        MeasureNativeGroup();
        context.PollDevice(wait: true);
        MeasureManagedGroup();
    }
}
else
{
    for (int index = 0; index < iterationCount; index++)
    {
        if ((index & 1) == 0)
        {
            nativeTimes[index] = MeasureNative(
                out long allocated,
                out nativeSubmissionTimes[index],
                out nativeCompletionWaitTimes[index]);
            nativeAllocatedBytes += allocated;
            managedTimes[index] = MeasureManaged(
                out allocated,
                out managedSubmissionTimes[index],
                out managedCompletionWaitTimes[index]);
            managedAllocatedBytes += allocated;
        }
        else
        {
            managedTimes[index] = MeasureManaged(
                out long allocated,
                out managedSubmissionTimes[index],
                out managedCompletionWaitTimes[index]);
            managedAllocatedBytes += allocated;
            nativeTimes[index] = MeasureNative(
                out allocated,
                out nativeSubmissionTimes[index],
                out nativeCompletionWaitTimes[index]);
            nativeAllocatedBytes += allocated;
        }

        if (drainEachPair)
        {
            // Keep the queue bounded without charging the shared GPU wait to
            // either renderer's CPU submission interval.
            context.PollDevice(wait: true);
        }
    }
}
context.PollDevice(wait: true);
GC.KeepAlive(nativeAllocationStart);

TimingSummary nativeSummary = Summarize(nativeTimes, nativeAllocatedBytes);
TimingSummary managedSummary = Summarize(managedTimes, managedAllocatedBytes);
TimingSummary nativeSubmissionSummary = Summarize(nativeSubmissionTimes, 0);
TimingSummary nativeCompletionWaitSummary = Summarize(nativeCompletionWaitTimes, 0);
TimingSummary managedSubmissionSummary = Summarize(managedSubmissionTimes, 0);
TimingSummary managedCompletionWaitSummary = Summarize(managedCompletionWaitTimes, 0);
ulong combinedMetalAllocatedBytes =
    context.TryCaptureNativeResourceSnapshot(out var resourceSnapshot)
        ? resourceSnapshot.MetalAllocatedBytes
        : 0UL;
var report = new BenchmarkReport(
    RuntimeInformation: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Adapter: context.AdapterName,
    Backend: context.AdapterBackendType.ToString(),
    Scene: useGlyphScene
        ? "RetainedPositionedGlyphAtlas"
        : usePathScene
        ? "RetainedPathAtlas"
        : useGeometryScene
        ? useDashedGeometryScene
            ? "IndexedGeometryDashes"
            : useSplineGeometryScene
            ? "IndexedGeometrySplines"
            : usePolylineGeometryScene
            ? "IndexedGeometryPolylines"
            : useCurveGeometryScene
            ? "IndexedGeometryCurves"
            : "IndexedGeometry"
        : useAnalyticScene ? "IndexedAnalytic" : "SolidRectangles",
    DifferentialContract: requiresExactPixels
        ? "Exact"
        : usesGeometryDifferential
            ? "Near-exact; bounded raster edge ownership ties"
        : usesTightDifferential
            ? "Near-exact pipeline (at most 3/255 per channel)"
            : "Bounded against managed solid-rectangle specialization",
    RectangleCount: rectangleCount,
    DpiScale: dpiScale,
    WarmupIterations: warmupCount,
    MeasuredIterations: iterationCount,
    SynchronizeEachFrame: synchronizeEachFrame,
    DrainEachPair: drainEachPair,
    ManagedCompiledSceneCache: enableManagedCompiledSceneCache,
    MeasurementOrder: groupMeasurements
        ? managedGroupFirst ? "GroupedManagedFirst" : "GroupedNativeFirst"
        : "Alternating",
    Native: nativeSummary,
    Managed: managedSummary,
    NativeSubmission: nativeSubmissionSummary,
    NativeCompletionWait: nativeCompletionWaitSummary,
    ManagedSubmission: managedSubmissionSummary,
    ManagedCompletionWait: managedCompletionWaitSummary,
    NativeToManagedP95Ratio: managedSummary.P95Milliseconds == 0
        ? 0
        : nativeSummary.P95Milliseconds / managedSummary.P95Milliseconds,
    CombinedMetalAllocatedBytes: combinedMetalAllocatedBytes,
    NativeVertexCount: nativeVertexCount,
    NativeIndexCount: nativeIndexCount,
    ManagedVertexCount: managedVertexCount,
    ManagedIndexCount: managedIndexCount,
    NativePayloadHash: nativePayloadHash.ToString("X16"),
    NativeRasterizedPathCount: nativeRasterizedPathCount,
    NativePathUploadBytes: nativePathUploadBytes,
    NativeCoverageStagingBytes: nativeCoverageStagingBytes,
    NativeRasterizedGlyphCount: nativeRasterizedGlyphCount,
    NativeGlyphOutlineUploadBytes: nativeGlyphOutlineUploadBytes,
    NativeGlyphInstanceUploadBytes: nativeGlyphInstanceUploadBytes,
    PixelParity: comparison);

Console.WriteLine(JsonSerializer.Serialize(
    report,
    new JsonSerializerOptions { WriteIndented = true }));

ulong RenderNative(bool capturePayloadHash = false)
{
    if (useGlyphScene)
    {
        NativeGlyphFrameMetrics metrics = native.RenderGlyphs(
            nativeTarget,
            dpiScale,
            nativeGlyphOutlines,
            nativeGlyphSegments,
            nativeGlyphs,
            clearColor,
            capturePayloadHash,
            contentRevision: 1U);
        nativeRasterizedGlyphCount = Math.Max(
            nativeRasterizedGlyphCount,
            metrics.RasterizedGlyphCount);
        nativeGlyphOutlineUploadBytes = Math.Max(
            nativeGlyphOutlineUploadBytes,
            metrics.OutlineUploadBytes);
        nativeGlyphInstanceUploadBytes = Math.Max(
            nativeGlyphInstanceUploadBytes,
            metrics.InstanceUploadBytes);
        nativeCoverageStagingBytes = Math.Max(
            nativeCoverageStagingBytes,
            metrics.CoverageStagingBytes);
        return metrics.PayloadHash;
    }
    if (usePathScene)
    {
        NativePathFrameMetrics metrics = native.RenderPaths(
            nativeTarget,
            dpiScale,
            nativePaths,
            nativePathSegments,
            clearColor,
            capturePayloadHash,
            contentRevision: 1U);
        nativeVertexCount = metrics.VertexCount;
        nativeIndexCount = metrics.IndexCount;
        nativeRasterizedPathCount = Math.Max(
            nativeRasterizedPathCount,
            metrics.RasterizedPathCount);
        nativePathUploadBytes = Math.Max(
            nativePathUploadBytes,
            metrics.PathUploadBytes);
        nativeCoverageStagingBytes = Math.Max(
            nativeCoverageStagingBytes,
            metrics.CoverageStagingBytes);
        return metrics.PayloadHash;
    }
    if (useGeometryScene || usePathScene)
    {
        NativeGeometryFrameMetrics metrics = useSplineGeometryScene
            ? native.RenderGeometry(
                nativeTarget,
                dpiScale,
                geometryPrimitives,
                geometryPoints,
                geometryPolylines,
                geometryDoubles,
                geometrySplines,
                clearColor,
                capturePayloadHash,
                contentRevision: 1U)
            : useDashedGeometryScene
            ? native.RenderGeometry(
                nativeTarget,
                dpiScale,
                geometryPrimitives,
                geometryPoints,
                geometryPolylines,
                geometryDoubles,
                geometryDashStyles,
                geometrySplines,
                clearColor,
                capturePayloadHash,
                contentRevision: 1U)
            : usePolylineGeometryScene
            ? native.RenderGeometry(
                nativeTarget,
                dpiScale,
                geometryPrimitives,
                geometryPoints,
                geometryPolylines,
                clearColor,
                capturePayloadHash,
                contentRevision: 1U)
            : native.RenderGeometry(
                nativeTarget,
                dpiScale,
                geometryPrimitives,
                clearColor,
                capturePayloadHash,
                contentRevision: 1U);
        nativeVertexCount = metrics.VertexCount;
        nativeIndexCount = metrics.IndexCount;
        return metrics.PayloadHash;
    }
    if (useAnalyticScene)
    {
        native.RenderAnalytic(
            nativeTarget,
            dpiScale,
            analyticPrimitives,
            clearColor);
    }
    else
    {
        native.Render(
            nativeTarget,
            dpiScale,
            rectangles,
            clearColor);
    }
    return 0UL;
}

void RenderManaged()
{
    managed.RenderOffscreen(
        managedVisual,
        Math.Max(1U, (uint)MathF.Round(logicalWidth)),
        Math.Max(1U, (uint)MathF.Round(logicalHeight)),
        managedTarget,
        padding: 0f,
        dpiScale,
        clearColor);
    if (useGeometryScene)
    {
        managedVertexCount = managed.Metrics.VectorVerticesCount;
        managedIndexCount = managed.Metrics.VectorIndicesCount;
    }
}

void SynchronizeIfRequested()
{
    if (synchronizeEachFrame)
    {
        context.PollDevice(wait: true);
    }
}

double MeasureNative(
    out long allocatedBytes,
    out double submissionMilliseconds,
    out double completionWaitMilliseconds)
{
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    long timestamp = Stopwatch.GetTimestamp();
    RenderNative();
    submissionMilliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    long waitTimestamp = Stopwatch.GetTimestamp();
    SynchronizeIfRequested();
    completionWaitMilliseconds = synchronizeEachFrame
        ? Stopwatch.GetElapsedTime(waitTimestamp).TotalMilliseconds
        : 0.0;
    double milliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
    return milliseconds;
}

double MeasureManaged(
    out long allocatedBytes,
    out double submissionMilliseconds,
    out double completionWaitMilliseconds)
{
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    long timestamp = Stopwatch.GetTimestamp();
    RenderManaged();
    submissionMilliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    long waitTimestamp = Stopwatch.GetTimestamp();
    SynchronizeIfRequested();
    completionWaitMilliseconds = synchronizeEachFrame
        ? Stopwatch.GetElapsedTime(waitTimestamp).TotalMilliseconds
        : 0.0;
    double milliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
    return milliseconds;
}

int ReadPositiveArgument(string name, int fallback)
{
    int value = ReadArgument(name, fallback);
    return value > 0 ? value : fallback;
}

int ReadNonNegativeArgument(string name, int fallback)
{
    int value = ReadArgument(name, fallback);
    return value >= 0 ? value : fallback;
}

float ReadPositiveFloatArgument(string name, float fallback)
{
    for (int index = 0; index + 1 < args.Length; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            float.TryParse(
                args[index + 1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value) &&
            float.IsFinite(value) &&
            value > 0f)
        {
            return value;
        }
    }
    return fallback;
}

int ReadArgument(string name, int fallback)
{
    for (int index = 0; index + 1 < args.Length; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[index + 1], out int value))
        {
            return value;
        }
    }
    return fallback;
}

static GpuTexture CreateTarget(WgpuContext context, string label) =>
    new(
        context,
        width,
        height,
        TextureFormat.Rgba8Unorm,
        TextureUsage.RenderAttachment |
        TextureUsage.TextureBinding |
        TextureUsage.CopySrc,
        label,
        alphaMode: GpuTextureAlphaMode.Premultiplied);

static NativeSolidRectangle[] CreateRectangles(
    int count,
    float logicalWidth,
    float logicalHeight)
{
    var result = new NativeSolidRectangle[count];
    const float inset = 18f;
    const float gap = 3f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        Vector4 color = new(
            0.12f + 0.45f * Wave(phase),
            0.3f + 0.62f * Wave(phase + 0.333f),
            0.45f + 0.5f * Wave(phase + 0.666f),
            1f);
        result[index] = new NativeSolidRectangle(
            inset + column * cellWidth + gap * 0.5f,
            inset + row * cellHeight + gap * 0.5f,
            Math.Max(1f, cellWidth - gap),
            Math.Max(1f, cellHeight - gap),
            color);
    }
    return result;

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static DrawingVisual CreateManagedVisual(
    ReadOnlySpan<NativeSolidRectangle> rectangles,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    foreach (ref readonly NativeSolidRectangle rectangle in rectangles)
    {
        visual.Context.DrawRectangle(
            new SolidColorBrush(rectangle.Color),
            pen: null,
            new Rect(
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height));
    }
    return visual;
}

static NativeAnalyticPrimitive[] CreateAnalyticPrimitives(
    int count,
    int forcedKind,
    float logicalWidth,
    float logicalHeight)
{
    var result = new NativeAnalyticPrimitive[count];
    const float inset = 24f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float itemWidth = Math.Max(2f, cellWidth * 0.64f);
        float itemHeight = Math.Max(2f, cellHeight * 0.58f);
        float centerX = inset + (column + 0.5f) * cellWidth;
        float centerY = inset + (row + 0.5f) * cellHeight;
        float phase = index * 0.61803398875f % 1f;
        Vector4 color = new(
            0.16f + 0.68f * Wave(phase),
            0.2f + 0.72f * Wave(phase + 0.333f),
            0.25f + 0.7f * Wave(phase + 0.666f),
            0.55f + 0.4f * Wave(phase + 0.17f));
        Matrix3x2 transform =
            Matrix3x2.CreateScale(
                0.82f + 0.32f * Wave(phase + 0.21f),
                0.78f + 0.38f * Wave(phase + 0.49f)) *
            Matrix3x2.CreateSkew(
                (Wave(phase + 0.77f) - 0.5f) * 0.18f,
                0f) *
            Matrix3x2.CreateRotation(
                (Wave(phase + 0.91f) - 0.5f) * 0.28f) *
            Matrix3x2.CreateTranslation(centerX, centerY);
        var kind = forcedKind is >= 0 and <= 2
            ? (NativeAnalyticPrimitiveKind)forcedKind
            : (NativeAnalyticPrimitiveKind)(index % 3);
        bool stroke = (index & 1) != 0;
        result[index] = new NativeAnalyticPrimitive(
            kind,
            -itemWidth * 0.5f,
            -itemHeight * 0.5f,
            itemWidth,
            itemHeight,
            color,
            transform,
            cornerRadius: Math.Min(itemWidth, itemHeight) * 0.22f,
            strokeThickness: stroke ? 1f + index % 4 : 0f);
    }
    return result;

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static DrawingVisual CreateManagedAnalyticVisual(
    ReadOnlySpan<NativeAnalyticPrimitive> primitives,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    foreach (ref readonly NativeAnalyticPrimitive primitive in primitives)
    {
        var solid = new SolidColorBrush(primitive.Color);
        Brush? fill = primitive.StrokeThickness > 0f ? null : solid;
        Pen? pen = primitive.StrokeThickness > 0f
            ? new Pen(solid, primitive.StrokeThickness)
            : null;
        Matrix3x2 affine = primitive.Transform;
        var transform = new Matrix4x4(
            affine.M11, affine.M12, 0f, 0f,
            affine.M21, affine.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            affine.M31, affine.M32, 0f, 1f);
        var rect = new Rect(
            primitive.X,
            primitive.Y,
            primitive.Width,
            primitive.Height);
        switch (primitive.Kind)
        {
            case NativeAnalyticPrimitiveKind.Rectangle:
                visual.Context.DrawRectangle(fill, pen, rect, transform);
                break;
            case NativeAnalyticPrimitiveKind.Ellipse:
                visual.Context.DrawEllipse(
                    fill,
                    pen,
                    new Vector2(
                        primitive.X + primitive.Width * 0.5f,
                        primitive.Y + primitive.Height * 0.5f),
                    primitive.Width * 0.5f,
                    primitive.Height * 0.5f,
                    transform);
                break;
            case NativeAnalyticPrimitiveKind.RoundedRectangle:
                visual.Context.DrawRoundedRectangle(
                    fill,
                    pen,
                    rect,
                    primitive.CornerRadius,
                    primitive.CornerRadius,
                    transform);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported analytic primitive {primitive.Kind}.");
        }
    }
    return visual;
}

static NativeGeometryPrimitive[] CreateGeometryPrimitives(
    int count,
    int forcedKind,
    int forcedLineMode,
    int forcedStartCap,
    int forcedEndCap,
    bool curvesOnly,
    float logicalWidth,
    float logicalHeight)
{
    var result = new NativeGeometryPrimitive[count];
    const float inset = 24f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    NativeStrokeCap startCap = forcedStartCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedStartCap
        : NativeStrokeCap.Flat;
    NativeStrokeCap endCap = forcedEndCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedEndCap
        : NativeStrokeCap.Flat;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        float itemWidth = Math.Max(2f, cellWidth * 0.58f);
        float itemHeight = Math.Max(2f, cellHeight * 0.52f);
        Vector2 center = new(
            inset + (column + 0.5f) * cellWidth,
            inset + (row + 0.5f) * cellHeight);
        Vector4 color = new(
            0.16f + 0.68f * Wave(phase),
            0.2f + 0.72f * Wave(phase + 0.333f),
            0.25f + 0.7f * Wave(phase + 0.666f),
            0.6f + 0.35f * Wave(phase + 0.17f));
        Matrix3x2 transform =
            Matrix3x2.CreateScale(
                0.82f + 0.36f * Wave(phase + 0.21f),
                0.76f + 0.48f * Wave(phase + 0.49f)) *
            Matrix3x2.CreateSkew(
                (Wave(phase + 0.77f) - 0.5f) * 0.24f,
                (Wave(phase + 0.43f) - 0.5f) * 0.12f) *
            Matrix3x2.CreateRotation(
                (Wave(phase + 0.91f) - 0.5f) * 0.32f) *
            Matrix3x2.CreateTranslation(center);
        int kind = forcedKind is >= 0 and <= 4
            ? forcedKind
            : curvesOnly ? 3 + index % 2 : index % 3;
        switch (kind)
        {
            case 0:
                int lineMode = forcedLineMode is >= 0 and <= 2
                    ? forcedLineMode
                    : index % 9 / 3;
                NativeGeometryPrimitiveFlags flags = lineMode switch
                {
                    0 => NativeGeometryPrimitiveFlags.Hairline,
                    1 => NativeGeometryPrimitiveFlags.FixedDeviceStroke,
                    _ => NativeGeometryPrimitiveFlags.None
                };
                result[index] = new NativeGeometryPrimitive(
                    NativeGeometryPrimitiveKind.Line,
                    new Vector2(-itemWidth * 0.5f, -itemHeight * 0.22f),
                    new Vector2(itemWidth * 0.5f, itemHeight * 0.22f),
                    color,
                    transform,
                    strokeThickness: flags == NativeGeometryPrimitiveFlags.Hairline
                        ? 0f
                        : 1f + index % 4,
                    flags: flags,
                    startCap: startCap,
                    endCap: endCap);
                break;
            case 1:
                result[index] = new NativeGeometryPrimitive(
                    NativeGeometryPrimitiveKind.Triangle,
                    new Vector2(-itemWidth * 0.5f, itemHeight * 0.45f),
                    new Vector2(0f, -itemHeight * 0.5f),
                    color,
                    transform,
                    p2: new Vector2(itemWidth * 0.5f, itemHeight * 0.45f));
                break;
            case 2:
                result[index] = new NativeGeometryPrimitive(
                    NativeGeometryPrimitiveKind.Quadrilateral,
                    new Vector2(-itemWidth * 0.5f, -itemHeight * 0.35f),
                    new Vector2(itemWidth * 0.35f, -itemHeight * 0.5f),
                    color,
                    transform,
                    p2: new Vector2(itemWidth * 0.5f, itemHeight * 0.35f),
                    p3: new Vector2(-itemWidth * 0.35f, itemHeight * 0.5f));
                break;
            case 3:
            case 4:
                int curveLineMode = forcedLineMode is >= 0 and <= 2
                    ? forcedLineMode
                    : index % 9 / 3;
                NativeGeometryPrimitiveFlags curveFlags = curveLineMode switch
                {
                    0 => NativeGeometryPrimitiveFlags.Hairline,
                    1 => NativeGeometryPrimitiveFlags.FixedDeviceStroke,
                    _ => NativeGeometryPrimitiveFlags.None
                };
                result[index] = new NativeGeometryPrimitive(
                    kind == 3
                        ? NativeGeometryPrimitiveKind.QuadraticBezier
                        : NativeGeometryPrimitiveKind.CubicBezier,
                    new Vector2(-itemWidth * 0.5f, itemHeight * 0.22f),
                    new Vector2(-itemWidth * 0.18f, -itemHeight * 0.62f),
                    color,
                    transform,
                    p2: new Vector2(itemWidth * 0.18f, itemHeight * 0.58f),
                    p3: new Vector2(itemWidth * 0.5f, -itemHeight * 0.18f),
                    strokeThickness: curveFlags == NativeGeometryPrimitiveFlags.Hairline
                        ? 0f
                        : 1f + index % 4,
                    flags: curveFlags,
                    startCap: startCap,
                    endCap: endCap);
                break;
            default:
                throw new InvalidOperationException("Unsupported geometry kind.");
        }
    }
    return result;

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static (Vector2[] Points,
        NativePolyline[] Polylines,
        double[] Doubles,
        NativeDashStyle[] DashStyles,
        NativeSpline[] Splines) CreateDashedPolylines(
    int count,
    int forcedLineMode,
    int forcedStartCap,
    int forcedEndCap,
    int forcedJoin,
    float logicalWidth,
    float logicalHeight)
{
    var scene = CreatePolylines(
        count,
        forcedLineMode is >= 0 and <= 2 ? forcedLineMode : 2,
        forcedStartCap,
        forcedEndCap,
        forcedJoin,
        logicalWidth,
        logicalHeight);
    var polylines = scene.Polylines;
    for (int index = 0; index < polylines.Length; index++)
    {
        NativePolyline source = polylines[index];
        polylines[index] = new NativePolyline(
            source.PointOffset,
            source.PointCount,
            source.Color,
            source.Transform,
            source.StrokeThickness,
            source.MiterLimit,
            source.Flags,
            source.StartCap,
            source.EndCap,
            source.LineJoin,
            source.IsClosed,
            dashStyle: 1);
    }
    double[] intervals = [1.75, 0.9, 0.45];
    NativeDashStyle[] styles =
    [
        new NativeDashStyle(
            0,
            (nuint)intervals.Length,
            -0.35,
            NativeStrokeCap.Round)
    ];
    return (scene.Points, polylines, intervals, styles, []);
}

static (Vector2[] Points,
        NativePolyline[] Polylines,
        double[] Doubles,
        NativeDashStyle[] DashStyles,
        NativeSpline[] Splines) CreatePolylines(
    int count,
    int forcedLineMode,
    int forcedStartCap,
    int forcedEndCap,
    int forcedJoin,
    float logicalWidth,
    float logicalHeight)
{
    const int pointsPerPolyline = 4;
    var points = new Vector2[count * pointsPerPolyline];
    var polylines = new NativePolyline[count];
    const float inset = 24f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    NativeStrokeCap startCap = forcedStartCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedStartCap
        : NativeStrokeCap.Round;
    NativeStrokeCap endCap = forcedEndCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedEndCap
        : NativeStrokeCap.Triangle;

    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        float itemWidth = Math.Max(3f, cellWidth * 0.62f);
        float itemHeight = Math.Max(3f, cellHeight * 0.58f);
        int offset = index * pointsPerPolyline;
        points[offset] = new(-itemWidth * 0.5f, itemHeight * 0.22f);
        points[offset + 1] = new(-itemWidth * 0.18f, -itemHeight * 0.46f);
        points[offset + 2] = new(itemWidth * 0.16f, itemHeight * 0.44f);
        points[offset + 3] = new(itemWidth * 0.5f, -itemHeight * 0.18f);
        Vector2 center = new(
            inset + (column + 0.5f) * cellWidth,
            inset + (row + 0.5f) * cellHeight);
        Matrix3x2 transform =
            Matrix3x2.CreateScale(
                0.8f + 0.4f * Wave(phase + 0.21f),
                0.72f + 0.56f * Wave(phase + 0.49f)) *
            Matrix3x2.CreateSkew(
                (Wave(phase + 0.77f) - 0.5f) * 0.28f,
                (Wave(phase + 0.43f) - 0.5f) * 0.14f) *
            Matrix3x2.CreateRotation(
                (Wave(phase + 0.91f) - 0.5f) * 0.36f) *
            Matrix3x2.CreateTranslation(center);
        Vector4 color = new(
            0.16f + 0.68f * Wave(phase),
            0.2f + 0.72f * Wave(phase + 0.333f),
            0.25f + 0.7f * Wave(phase + 0.666f),
            0.6f + 0.35f * Wave(phase + 0.17f));
        int lineMode = forcedLineMode is >= 0 and <= 2
            ? forcedLineMode
            : index % 9 / 3;
        NativePolylineFlags flags = lineMode switch
        {
            0 => NativePolylineFlags.Hairline,
            1 => NativePolylineFlags.FixedDeviceStroke,
            _ => NativePolylineFlags.None
        };
        NativeStrokeJoin join = forcedJoin is >= 0 and <= 2
            ? (NativeStrokeJoin)forcedJoin
            : (NativeStrokeJoin)(index % 3);
        polylines[index] = new NativePolyline(
            (nuint)offset,
            (nuint)pointsPerPolyline,
            color,
            transform,
            flags == NativePolylineFlags.Hairline ? 0f : 1f + index % 4,
            miterLimit: 2f + index % 5,
            flags: flags,
            startCap: startCap,
            endCap: endCap,
            lineJoin: join,
            isClosed: index % 4 == 3);
    }
    return (points, polylines, [], [], []);

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static (Vector2[] Points,
        NativePolyline[] Polylines,
        double[] Doubles,
        NativeDashStyle[] DashStyles,
        NativeSpline[] Splines) CreateSplines(
    int count,
    int forcedLineMode,
    int forcedStartCap,
    int forcedEndCap,
    int forcedJoin,
    float logicalWidth,
    float logicalHeight)
{
    const int controlPointsPerSpline = 6;
    const int knotsPerSpline = 10;
    const int weightsPerSpline = 6;
    const int doublesPerSpline = knotsPerSpline + weightsPerSpline;
    var points = new Vector2[count * controlPointsPerSpline];
    var doubles = new double[count * doublesPerSpline];
    var splines = new NativeSpline[count];
    const float inset = 24f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    NativeStrokeCap startCap = forcedStartCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedStartCap
        : NativeStrokeCap.Round;
    NativeStrokeCap endCap = forcedEndCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedEndCap
        : NativeStrokeCap.Triangle;
    ReadOnlySpan<double> knots = [0, 0, 0, 0, 1, 2, 3, 3, 3, 3];

    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        float itemWidth = Math.Max(4f, cellWidth * 0.72f);
        float itemHeight = Math.Max(4f, cellHeight * 0.68f);
        int pointOffset = index * controlPointsPerSpline;
        points[pointOffset] = new(-itemWidth * 0.5f, itemHeight * 0.12f);
        points[pointOffset + 1] = new(-itemWidth * 0.34f, -itemHeight * 0.5f);
        points[pointOffset + 2] = new(-itemWidth * 0.1f, itemHeight * 0.48f);
        points[pointOffset + 3] = new(itemWidth * 0.12f, -itemHeight * 0.46f);
        points[pointOffset + 4] = new(itemWidth * 0.34f, itemHeight * 0.5f);
        points[pointOffset + 5] = new(itemWidth * 0.5f, -itemHeight * 0.1f);
        int doubleOffset = index * doublesPerSpline;
        knots.CopyTo(doubles.AsSpan(doubleOffset, knotsPerSpline));
        for (int weight = 0; weight < weightsPerSpline; weight++)
        {
            doubles[doubleOffset + knotsPerSpline + weight] =
                0.78 + 0.44 * Wave(phase + weight * 0.137f);
        }
        Vector2 center = new(
            inset + (column + 0.5f) * cellWidth,
            inset + (row + 0.5f) * cellHeight);
        Matrix3x2 transform =
            Matrix3x2.CreateScale(
                0.82f + 0.38f * Wave(phase + 0.21f),
                0.74f + 0.52f * Wave(phase + 0.49f)) *
            Matrix3x2.CreateSkew(
                (Wave(phase + 0.77f) - 0.5f) * 0.24f,
                (Wave(phase + 0.43f) - 0.5f) * 0.12f) *
            Matrix3x2.CreateRotation(
                (Wave(phase + 0.91f) - 0.5f) * 0.32f) *
            Matrix3x2.CreateTranslation(center);
        Vector4 color = new(
            0.16f + 0.68f * Wave(phase),
            0.2f + 0.72f * Wave(phase + 0.333f),
            0.25f + 0.7f * Wave(phase + 0.666f),
            0.6f + 0.35f * Wave(phase + 0.17f));
        int lineMode = forcedLineMode is >= 0 and <= 2
            ? forcedLineMode
            : index % 9 / 3;
        NativePolylineFlags flags = lineMode switch
        {
            0 => NativePolylineFlags.Hairline,
            1 => NativePolylineFlags.FixedDeviceStroke,
            _ => NativePolylineFlags.None
        };
        NativeStrokeJoin join = forcedJoin is >= 0 and <= 2
            ? (NativeStrokeJoin)forcedJoin
            : (NativeStrokeJoin)(index % 3);
        var stroke = new NativePolyline(
            (nuint)pointOffset,
            controlPointsPerSpline,
            color,
            transform,
            flags == NativePolylineFlags.Hairline ? 0f : 1f + index % 4,
            miterLimit: 2f + index % 5,
            flags: flags,
            startCap: startCap,
            endCap: endCap,
            lineJoin: join,
            isClosed: index % 4 == 3);
        splines[index] = new NativeSpline(
            stroke,
            (nuint)doubleOffset,
            knotsPerSpline,
            degree: 3,
            weightOffset: (nuint)(doubleOffset + knotsPerSpline),
            weightCount: weightsPerSpline);
    }
    return (points, [], doubles, [], splines);

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static DrawingVisual CreateManagedGeometryVisual(
    ReadOnlySpan<NativeGeometryPrimitive> primitives,
    ReadOnlySpan<Vector2> points,
    ReadOnlySpan<NativePolyline> polylines,
    ReadOnlySpan<double> doubles,
    ReadOnlySpan<NativeDashStyle> dashStyles,
    ReadOnlySpan<NativeSpline> splines,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    foreach (ref readonly NativeGeometryPrimitive primitive in primitives)
    {
        var brush = new SolidColorBrush(primitive.Color);
        Matrix3x2 affine = primitive.Transform;
        var transform = new Matrix4x4(
            affine.M11, affine.M12, 0f, 0f,
            affine.M21, affine.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            affine.M31, affine.M32, 0f, 1f);
        switch (primitive.Kind)
        {
            case NativeGeometryPrimitiveKind.Line:
                var mode = (primitive.Flags &
                    NativeGeometryPrimitiveFlags.FixedDeviceStroke) != 0
                    ? PenStrokeTransformMode.Fixed
                    : PenStrokeTransformMode.Normal;
                float thickness = (primitive.Flags &
                    NativeGeometryPrimitiveFlags.Hairline) != 0
                    ? Pen.HairlineThickness
                    : primitive.StrokeThickness;
                visual.Context.DrawLine(
                    new Pen(
                        brush,
                        thickness,
                        startLineCap: ToPenLineCap(primitive.StartCap),
                        endLineCap: ToPenLineCap(primitive.EndCap),
                        strokeTransformMode: mode),
                    primitive.P0,
                    primitive.P1,
                    transform);
                break;
            case NativeGeometryPrimitiveKind.Triangle:
                visual.Context.FillTriangle(
                    brush,
                    Vector2.Transform(primitive.P0, affine),
                    Vector2.Transform(primitive.P1, affine),
                    Vector2.Transform(primitive.P2, affine));
                break;
            case NativeGeometryPrimitiveKind.Quadrilateral:
                visual.Context.FillQuad(
                    brush,
                    Vector2.Transform(primitive.P0, affine),
                    Vector2.Transform(primitive.P1, affine),
                    Vector2.Transform(primitive.P2, affine),
                    Vector2.Transform(primitive.P3, affine));
                break;
            case NativeGeometryPrimitiveKind.QuadraticBezier:
            case NativeGeometryPrimitiveKind.CubicBezier:
                var curveMode = (primitive.Flags &
                    NativeGeometryPrimitiveFlags.FixedDeviceStroke) != 0
                    ? PenStrokeTransformMode.Fixed
                    : PenStrokeTransformMode.Normal;
                float curveThickness = (primitive.Flags &
                    NativeGeometryPrimitiveFlags.Hairline) != 0
                    ? Pen.HairlineThickness
                    : primitive.StrokeThickness;
                var curvePen = new Pen(
                    brush,
                    curveThickness,
                    startLineCap: ToPenLineCap(primitive.StartCap),
                    endLineCap: ToPenLineCap(primitive.EndCap),
                    strokeTransformMode: curveMode);
                visual.Context.Commands.Add(new RenderCommand
                {
                    Type = primitive.Kind == NativeGeometryPrimitiveKind.QuadraticBezier
                        ? RenderCommandType.DrawBezier
                        : RenderCommandType.DrawCubicBezier,
                    Pen = curvePen,
                    Position = primitive.P0,
                    Position2 = primitive.P1,
                    Position3 = primitive.P2,
                    Position4 = primitive.P3,
                    Transform = transform,
                    IsPenThicknessLocal = true,
                    IsEdgeAliased = (primitive.Flags &
                        NativeGeometryPrimitiveFlags.EdgeAliased) != 0,
                    GeometryCache = primitive.StartCap != NativeStrokeCap.Flat ||
                        primitive.EndCap != NativeStrokeCap.Flat
                        ? RenderCommandGeometryCache.ForStrokePath(
                            primitive.Kind == NativeGeometryPrimitiveKind.QuadraticBezier
                                ? RenderCommandGeometryCache.CreateQuadraticBezierPath(
                                    primitive.P0,
                                    primitive.P1,
                                    primitive.P2)
                                : RenderCommandGeometryCache.CreateCubicBezierPath(
                                    primitive.P0,
                                    primitive.P1,
                                    primitive.P2,
                                    primitive.P3))
                        : null
                });
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported geometry primitive {primitive.Kind}.");
        }
    }
    foreach (ref readonly NativePolyline polyline in polylines)
    {
        int offset = checked((int)polyline.PointOffset);
        int count = checked((int)polyline.PointCount);
        var brush = new SolidColorBrush(polyline.Color);
        var mode = (polyline.Flags &
            NativePolylineFlags.FixedDeviceStroke) != 0
            ? PenStrokeTransformMode.Fixed
            : PenStrokeTransformMode.Normal;
        float thickness = (polyline.Flags & NativePolylineFlags.Hairline) != 0
            ? Pen.HairlineThickness
            : polyline.StrokeThickness;
        double[]? dashArray = null;
        double dashOffset = 0.0;
        PenLineCap dashCap = PenLineCap.Flat;
        if (polyline.DashStyle != 0U)
        {
            NativeDashStyle dash = dashStyles[
                checked((int)polyline.DashStyle - 1)];
            dashArray = doubles.Slice(
                checked((int)dash.IntervalOffset),
                checked((int)dash.IntervalCount)).ToArray();
            dashOffset = dash.Offset;
            dashCap = ToPenLineCap(dash.Cap);
        }
        visual.Context.DrawPolyline(
            new Pen(
                brush,
                thickness,
                lineJoin: ToPenLineJoin(polyline.LineJoin),
                miterLimit: polyline.MiterLimit,
                startLineCap: ToPenLineCap(polyline.StartCap),
                endLineCap: ToPenLineCap(polyline.EndCap),
                dashCap: dashCap,
                dashArray: dashArray,
                dashOffset: dashOffset,
                strokeTransformMode: mode),
            points.Slice(offset, count),
            polyline.IsClosed);
        RenderCommand command = visual.Context.Commands[^1];
        Matrix3x2 affine = polyline.Transform;
        command.Transform = new Matrix4x4(
            affine.M11, affine.M12, 0f, 0f,
            affine.M21, affine.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            affine.M31, affine.M32, 0f, 1f);
        command.IsPenThicknessLocal = true;
        command.IsEdgeAliased =
            (polyline.Flags & NativePolylineFlags.EdgeAliased) != 0;
        visual.Context.Commands[^1] = command;
    }
    foreach (ref readonly NativeSpline spline in splines)
    {
        NativePolyline stroke = spline.Stroke;
        int pointOffset = checked((int)stroke.PointOffset);
        int pointCount = checked((int)stroke.PointCount);
        int knotOffset = checked((int)spline.KnotOffset);
        int knotCount = checked((int)spline.KnotCount);
        int weightOffset = checked((int)spline.WeightOffset);
        int weightCount = checked((int)spline.WeightCount);
        var brush = new SolidColorBrush(stroke.Color);
        var mode = (stroke.Flags & NativePolylineFlags.FixedDeviceStroke) != 0
            ? PenStrokeTransformMode.Fixed
            : PenStrokeTransformMode.Normal;
        float thickness = (stroke.Flags & NativePolylineFlags.Hairline) != 0
            ? Pen.HairlineThickness
            : stroke.StrokeThickness;
        double[]? dashArray = null;
        double dashOffset = 0.0;
        PenLineCap dashCap = PenLineCap.Flat;
        if (stroke.DashStyle != 0U)
        {
            NativeDashStyle dash = dashStyles[
                checked((int)stroke.DashStyle - 1)];
            dashArray = doubles.Slice(
                checked((int)dash.IntervalOffset),
                checked((int)dash.IntervalCount)).ToArray();
            dashOffset = dash.Offset;
            dashCap = ToPenLineCap(dash.Cap);
        }
        visual.Context.DrawSpline(
            new Pen(
                brush,
                thickness,
                lineJoin: ToPenLineJoin(stroke.LineJoin),
                miterLimit: stroke.MiterLimit,
                startLineCap: ToPenLineCap(stroke.StartCap),
                endLineCap: ToPenLineCap(stroke.EndCap),
                dashCap: dashCap,
                dashArray: dashArray,
                dashOffset: dashOffset,
                strokeTransformMode: mode),
            points.Slice(pointOffset, pointCount),
            doubles.Slice(knotOffset, knotCount),
            weightCount == 0
                ? ReadOnlySpan<double>.Empty
                : doubles.Slice(weightOffset, weightCount),
            checked((int)spline.Degree),
            stroke.IsClosed);
        RenderCommand command = visual.Context.Commands[^1];
        Matrix3x2 affine = stroke.Transform;
        command.Transform = new Matrix4x4(
            affine.M11, affine.M12, 0f, 0f,
            affine.M21, affine.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            affine.M31, affine.M32, 0f, 1f);
        command.IsPenThicknessLocal = true;
        command.IsEdgeAliased =
            (stroke.Flags & NativePolylineFlags.EdgeAliased) != 0;
        visual.Context.Commands[^1] = command;
    }
    return visual;

    static PenLineCap ToPenLineCap(NativeStrokeCap cap) => cap switch
    {
        NativeStrokeCap.Square => PenLineCap.Square,
        NativeStrokeCap.Round => PenLineCap.Round,
        NativeStrokeCap.Triangle => PenLineCap.Triangle,
        _ => PenLineCap.Flat
    };

    static PenLineJoin ToPenLineJoin(NativeStrokeJoin join) => join switch
    {
        NativeStrokeJoin.Bevel => PenLineJoin.Bevel,
        NativeStrokeJoin.Round => PenLineJoin.Round,
        _ => PenLineJoin.Miter
    };
}

static (NativePathFill[] Paths, NativePathSegment[] Segments) CreateNativePaths(
    int count,
    float logicalWidth,
    float logicalHeight)
{
    const float radius = 12f;
    const float kappa = 0.55228475f;
    var segments = new[]
    {
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(0f, -radius),
            new Vector2(radius * kappa, -radius),
            new Vector2(radius, -radius * kappa),
            new Vector2(radius, 0f)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(radius, 0f),
            new Vector2(radius, radius * kappa),
            new Vector2(radius * kappa, radius),
            new Vector2(0f, radius)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(0f, radius),
            new Vector2(-radius * kappa, radius),
            new Vector2(-radius, radius * kappa),
            new Vector2(-radius, 0f)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(-radius, 0f),
            new Vector2(-radius, -radius * kappa),
            new Vector2(-radius * kappa, -radius),
            new Vector2(0f, -radius))
    };
    var paths = new NativePathFill[count];
    int columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(
        count * logicalWidth / logicalHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = logicalWidth / columns;
    float cellHeight = logicalHeight / rows;
    float scale = MathF.Min(cellWidth, cellHeight) / (radius * 2.8f);
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        Vector4 color = new(
            0.2f + 0.7f * (0.5f + 0.5f * MathF.Sin(phase * MathF.Tau)),
            0.25f + 0.65f * (0.5f + 0.5f * MathF.Sin((phase + 0.333f) * MathF.Tau)),
            0.3f + 0.6f * (0.5f + 0.5f * MathF.Sin((phase + 0.666f) * MathF.Tau)),
            1f);
        Matrix3x2 transform = Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(
                (column + 0.5f) * cellWidth,
                (row + 0.5f) * cellHeight);
        paths[index] = new NativePathFill(
            0,
            (nuint)segments.Length,
            new Vector2(-radius),
            new Vector2(radius),
            color,
            transform);
    }
    return (paths, segments);
}

static DrawingVisual CreateManagedPathVisual(
    int count,
    float logicalWidth,
    float logicalHeight)
{
    const float radius = 12f;
    const float kappa = 0.55228475f;
    var path = new PathGeometry();
    var figure = new PathFigure(new Vector2(0f, -radius), isClosed: true);
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(radius * kappa, -radius),
        new Vector2(radius, -radius * kappa),
        new Vector2(radius, 0f)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(radius, radius * kappa),
        new Vector2(radius * kappa, radius),
        new Vector2(0f, radius)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(-radius * kappa, radius),
        new Vector2(-radius, radius * kappa),
        new Vector2(-radius, 0f)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(-radius, -radius * kappa),
        new Vector2(-radius * kappa, -radius),
        new Vector2(0f, -radius)));
    path.Figures.Add(figure);

    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    int columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(
        count * logicalWidth / logicalHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = logicalWidth / columns;
    float cellHeight = logicalHeight / rows;
    float scale = MathF.Min(cellWidth, cellHeight) / (radius * 2.8f);
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        Vector4 color = new(
            0.2f + 0.7f * (0.5f + 0.5f * MathF.Sin(phase * MathF.Tau)),
            0.25f + 0.65f * (0.5f + 0.5f * MathF.Sin((phase + 0.333f) * MathF.Tau)),
            0.3f + 0.6f * (0.5f + 0.5f * MathF.Sin((phase + 0.666f) * MathF.Tau)),
            1f);
        Matrix3x2 affine = Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(
                (column + 0.5f) * cellWidth,
                (row + 0.5f) * cellHeight);
        visual.Context.DrawPath(
            new SolidColorBrush(color),
            pen: null,
            path,
            new Matrix4x4(
                affine.M11, affine.M12, 0f, 0f,
                affine.M21, affine.M22, 0f, 0f,
                0f, 0f, 1f, 0f,
                affine.M31, affine.M32, 0f, 1f));
    }
    return visual;
}

static (
    NativeGlyphOutline[] Outlines,
    NativePathSegment[] Segments,
    NativePositionedGlyph[] Glyphs,
    ushort[] GlyphIndices,
    Vector2[] GlyphPositions) CreateGlyphScene(
    TtfFont font,
    int count,
    float dpiScale,
    float logicalWidth,
    float logicalHeight)
{
    const float fontSize = 20f;
    const string alphabet = "ProGPUWebNative0123456789ABCDEFGHJKLMNQRSTUVXYZ";
    float targetRasterSize = Math.Clamp(fontSize * dpiScale, 4f, 256f);
    float rasterScale = targetRasterSize / font.UnitsPerEm;
    float atlasToLogicalScale = fontSize * dpiScale / targetRasterSize;
    float subpixelX = targetRasterSize <= 24f ? 0.25f : 0f;
    int columns = Math.Max(1, (int)(logicalWidth / 44f));
    int rows = Math.Max(1, (int)MathF.Ceiling(count / (float)columns));
    float cellHeight = Math.Min(42f, logicalHeight / rows);

    var outlines = new List<NativeGlyphOutline>();
    var segments = new List<NativePathSegment>();
    var glyphs = new List<NativePositionedGlyph>(count);
    var glyphIndices = new ushort[count];
    var glyphPositions = new Vector2[count];
    var outlineIndices = new Dictionary<ushort, uint>();
    Vector4 color = new(0.92f, 0.96f, 1f, 1f);

    for (int index = 0; index < count; index++)
    {
        ushort glyphIndex = font.GetGlyphIndex(alphabet[index % alphabet.Length]);
        int column = index % columns;
        int row = index / columns;
        var managedPosition = new Vector2(
            18f + column * 44f + subpixelX / dpiScale,
            Math.Min(logicalHeight - 10f, 34f + row * cellHeight));
        var nativePosition = new Vector2(
            MathF.Floor(managedPosition.X * dpiScale),
            MathF.Round(managedPosition.Y * dpiScale)) / dpiScale;
        glyphIndices[index] = glyphIndex;
        glyphPositions[index] = managedPosition;

        if (!outlineIndices.TryGetValue(glyphIndex, out uint outlineIndex))
        {
            PathGeometry? outline = font.GetGlyphOutline(glyphIndex);
            if (outline == null ||
                !outline.TryGetBounds(out Vector2 minimum, out Vector2 maximum))
            {
                throw new InvalidOperationException(
                    $"Glyph {glyphIndex} has no renderable outline.");
            }
            nuint segmentOffset = (nuint)segments.Count;
            foreach (PathFigure figure in outline.Figures)
            {
                Vector2 current = figure.StartPoint;
                foreach (PathSegment segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            segments.Add(new NativePathSegment(
                                NativePathSegmentKind.Line,
                                current,
                                line.Point));
                            current = line.Point;
                            break;
                        case QuadraticBezierSegment quadratic:
                            segments.Add(new NativePathSegment(
                                NativePathSegmentKind.Quadratic,
                                current,
                                quadratic.ControlPoint,
                                quadratic.Point));
                            current = quadratic.Point;
                            break;
                        case CubicBezierSegment cubic:
                            segments.Add(new NativePathSegment(
                                NativePathSegmentKind.Cubic,
                                current,
                                cubic.ControlPoint1,
                                cubic.ControlPoint2,
                                cubic.Point));
                            current = cubic.Point;
                            break;
                    }
                }
                if (figure.IsClosed && current != figure.StartPoint)
                {
                    segments.Add(new NativePathSegment(
                        NativePathSegmentKind.Line,
                        current,
                        figure.StartPoint));
                }
            }
            nuint segmentCount = (nuint)segments.Count - segmentOffset;
            if (segmentCount == 0)
            {
                throw new InvalidOperationException(
                    $"Glyph {glyphIndex} has an empty outline.");
            }
            outlineIndex = checked((uint)outlines.Count);
            outlineIndices.Add(glyphIndex, outlineIndex);
            outlines.Add(new NativeGlyphOutline(
                segmentOffset,
                segmentCount,
                minimum,
                maximum,
                rasterScale,
                subpixelX));
        }

        glyphs.Add(new NativePositionedGlyph(
            outlineIndex,
            nativePosition,
            Vector2.UnitX,
            Vector2.UnitY,
            color,
            atlasToLogicalScale));
    }

    return (
        outlines.ToArray(),
        segments.ToArray(),
        glyphs.ToArray(),
        glyphIndices,
        glyphPositions);
}

static DrawingVisual CreateManagedGlyphVisual(
    TtfFont font,
    ushort[] glyphIndices,
    Vector2[] glyphPositions,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    visual.Context.DrawGlyphRun(
        glyphIndices,
        glyphPositions,
        font,
        20f,
        new SolidColorBrush(new Vector4(0.92f, 0.96f, 1f, 1f)),
        Vector2.Zero,
        textRenderingMode: TextRenderingMode.Grayscale,
        preferGlyphAtlas: true);
    return visual;
}

static PixelComparison ComparePixels(
    ReadOnlySpan<byte> native,
    ReadOnlySpan<byte> managed)
{
    if (native.Length != managed.Length || native.Length % 4 != 0)
    {
        throw new InvalidOperationException("Pixel buffers are not comparable.");
    }
    int maximum = 0;
    int pixelsOverTolerance = 0;
    long totalAbsoluteDifference = 0;
    ulong nativeHash = 14695981039346656037UL;
    ulong managedHash = 14695981039346656037UL;
    for (int offset = 0; offset < native.Length; offset += 4)
    {
        bool overTolerance = false;
        for (int channel = 0; channel < 4; channel++)
        {
            int difference = Math.Abs(native[offset + channel] - managed[offset + channel]);
            maximum = Math.Max(maximum, difference);
            totalAbsoluteDifference += difference;
            overTolerance |= difference > 3;
            nativeHash = (nativeHash ^ native[offset + channel]) * 1099511628211UL;
            managedHash = (managedHash ^ managed[offset + channel]) * 1099511628211UL;
        }
        if (overTolerance)
        {
            pixelsOverTolerance++;
        }
    }
    return new PixelComparison(
        native.Length / 4,
        maximum,
        pixelsOverTolerance,
        totalAbsoluteDifference,
        totalAbsoluteDifference / (double)native.Length,
        nativeHash.ToString("X16"),
        managedHash.ToString("X16"));
}

static void WritePpm(
    string path,
    ReadOnlySpan<byte> rgba,
    uint imageWidth,
    uint imageHeight)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write(System.Text.Encoding.ASCII.GetBytes(
        $"P6\n{imageWidth} {imageHeight}\n255\n"));
    for (int offset = 0; offset < rgba.Length; offset += 4)
    {
        writer.Write(rgba[offset]);
        writer.Write(rgba[offset + 1]);
        writer.Write(rgba[offset + 2]);
    }
}

static void WriteDifferencePpm(
    string path,
    ReadOnlySpan<byte> left,
    ReadOnlySpan<byte> right,
    uint imageWidth,
    uint imageHeight,
    int amplification)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write(System.Text.Encoding.ASCII.GetBytes(
        $"P6\n{imageWidth} {imageHeight}\n255\n"));
    for (int offset = 0; offset < left.Length; offset += 4)
    {
        writer.Write((byte)Math.Min(
            byte.MaxValue,
            Math.Abs(left[offset] - right[offset]) * amplification));
        writer.Write((byte)Math.Min(
            byte.MaxValue,
            Math.Abs(left[offset + 1] - right[offset + 1]) * amplification));
        writer.Write((byte)Math.Min(
            byte.MaxValue,
            Math.Abs(left[offset + 2] - right[offset + 2]) * amplification));
    }
}

static TimingSummary Summarize(double[] samples, long allocatedBytes)
{
    double[] ordered = (double[])samples.Clone();
    Array.Sort(ordered);
    return new TimingSummary(
        MeanMilliseconds: samples.Average(),
        P50Milliseconds: Percentile(ordered, 0.50),
        P95Milliseconds: Percentile(ordered, 0.95),
        MaximumMilliseconds: ordered[^1],
        TotalManagedAllocatedBytes: allocatedBytes,
        ManagedAllocatedBytesPerFrame: allocatedBytes / (double)samples.Length);
}

static double Percentile(double[] ordered, double percentile)
{
    int index = Math.Clamp(
        (int)Math.Ceiling(percentile * ordered.Length) - 1,
        0,
        ordered.Length - 1);
    return ordered[index];
}

internal sealed record BenchmarkReport(
    string RuntimeInformation,
    string OperatingSystem,
    string Adapter,
    string Backend,
    string Scene,
    string DifferentialContract,
    int RectangleCount,
    float DpiScale,
    int WarmupIterations,
    int MeasuredIterations,
    bool SynchronizeEachFrame,
    bool DrainEachPair,
    bool ManagedCompiledSceneCache,
    string MeasurementOrder,
    TimingSummary Native,
    TimingSummary Managed,
    TimingSummary NativeSubmission,
    TimingSummary NativeCompletionWait,
    TimingSummary ManagedSubmission,
    TimingSummary ManagedCompletionWait,
    double NativeToManagedP95Ratio,
    ulong CombinedMetalAllocatedBytes,
    uint NativeVertexCount,
    uint NativeIndexCount,
    int ManagedVertexCount,
    int ManagedIndexCount,
    string NativePayloadHash,
    uint NativeRasterizedPathCount,
    ulong NativePathUploadBytes,
    ulong NativeCoverageStagingBytes,
    uint NativeRasterizedGlyphCount,
    ulong NativeGlyphOutlineUploadBytes,
    ulong NativeGlyphInstanceUploadBytes,
    PixelComparison PixelParity);

internal sealed record TimingSummary(
    double MeanMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds,
    long TotalManagedAllocatedBytes,
    double ManagedAllocatedBytesPerFrame);

internal sealed record PixelComparison(
    int PixelCount,
    int MaximumChannelDifference,
    int PixelsOverTolerance,
    long TotalAbsoluteChannelDifference,
    double MeanAbsoluteChannelDifference,
    string NativeFnv1A64,
    string ManagedFnv1A64);
