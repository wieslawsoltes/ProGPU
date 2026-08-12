using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Scene;
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
useGeometryScene |= useCurveGeometryScene;
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
Vector4 clearColor = new(0.015f, 0.02f, 0.035f, 1f);

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
        EnableCompiledSceneCache = false,
        EnableGpuHitTesting = false,
        PrimarySampleCount = 1
    });
DrawingVisual managedVisual = useGeometryScene
    ? CreateManagedGeometryVisual(
        geometryPrimitives,
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
    WritePpm(
        "artifacts/progpu-native/differential/native.ppm",
        nativePixels,
        width,
        height);
    WritePpm(
        "artifacts/progpu-native/differential/managed.ppm",
        managedPixels,
        width,
        height);
    WriteDifferencePpm(
        "artifacts/progpu-native/differential/difference.ppm",
        nativePixels,
        managedPixels,
        width,
        height);
}
PixelComparison comparison = ComparePixels(nativePixels, managedPixels);
bool requiresExactPixels = !useAnalyticScene && !useGeometryScene && dpiScale == 1f;
bool usesGeometryDifferential = useGeometryScene;
bool usesTightDifferential =
    (useAnalyticScene && analyticKind is 1 or 2) ||
    (!useAnalyticScene && !useGeometryScene && !requiresExactPixels);
int maximumAllowedDifference = requiresExactPixels
    ? 0
    : usesGeometryDifferential ? 204 : usesTightDifferential ? 3 : 96;
int maximumAllowedPixelsOverTolerance =
    usesGeometryDifferential
        ? 1
        : requiresExactPixels || usesTightDifferential
        ? 0
        : comparison.PixelCount / 40;
double maximumAllowedMeanAbsoluteDifference = requiresExactPixels
    ? 0.0
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
}
context.PollDevice(wait: true);

var nativeTimes = new double[iterationCount];
var managedTimes = new double[iterationCount];
long nativeAllocationStart = GC.GetAllocatedBytesForCurrentThread();
long nativeAllocatedBytes = 0;
long managedAllocatedBytes = 0;
for (int index = 0; index < iterationCount; index++)
{
    if ((index & 1) == 0)
    {
        nativeTimes[index] = MeasureNative(out long allocated);
        nativeAllocatedBytes += allocated;
        managedTimes[index] = MeasureManaged(out allocated);
        managedAllocatedBytes += allocated;
    }
    else
    {
        managedTimes[index] = MeasureManaged(out long allocated);
        managedAllocatedBytes += allocated;
        nativeTimes[index] = MeasureNative(out allocated);
        nativeAllocatedBytes += allocated;
    }
}
context.PollDevice(wait: true);
GC.KeepAlive(nativeAllocationStart);

TimingSummary nativeSummary = Summarize(nativeTimes, nativeAllocatedBytes);
TimingSummary managedSummary = Summarize(managedTimes, managedAllocatedBytes);
ulong combinedMetalAllocatedBytes =
    context.TryCaptureNativeResourceSnapshot(out var resourceSnapshot)
        ? resourceSnapshot.MetalAllocatedBytes
        : 0UL;
var report = new BenchmarkReport(
    RuntimeInformation: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Adapter: context.AdapterName,
    Backend: context.AdapterBackendType.ToString(),
    Scene: useGeometryScene
        ? useCurveGeometryScene
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
    Native: nativeSummary,
    Managed: managedSummary,
    NativeToManagedP95Ratio: managedSummary.P95Milliseconds == 0
        ? 0
        : nativeSummary.P95Milliseconds / managedSummary.P95Milliseconds,
    CombinedMetalAllocatedBytes: combinedMetalAllocatedBytes,
    NativePayloadHash: nativePayloadHash.ToString("X16"),
    PixelParity: comparison);

Console.WriteLine(JsonSerializer.Serialize(
    report,
    new JsonSerializerOptions { WriteIndented = true }));

ulong RenderNative(bool capturePayloadHash = false)
{
    if (useGeometryScene)
    {
        return native.RenderGeometry(
            nativeTarget,
            dpiScale,
            geometryPrimitives,
            clearColor,
            capturePayloadHash).PayloadHash;
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
}

void SynchronizeIfRequested()
{
    if (synchronizeEachFrame)
    {
        context.PollDevice(wait: true);
    }
}

double MeasureNative(out long allocatedBytes)
{
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    long timestamp = Stopwatch.GetTimestamp();
    RenderNative();
    SynchronizeIfRequested();
    double milliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
    return milliseconds;
}

double MeasureManaged(out long allocatedBytes)
{
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    long timestamp = Stopwatch.GetTimestamp();
    RenderManaged();
    SynchronizeIfRequested();
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

static DrawingVisual CreateManagedGeometryVisual(
    ReadOnlySpan<NativeGeometryPrimitive> primitives,
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
    return visual;

    static PenLineCap ToPenLineCap(NativeStrokeCap cap) => cap switch
    {
        NativeStrokeCap.Square => PenLineCap.Square,
        NativeStrokeCap.Round => PenLineCap.Round,
        NativeStrokeCap.Triangle => PenLineCap.Triangle,
        _ => PenLineCap.Flat
    };
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
    uint imageHeight)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write(System.Text.Encoding.ASCII.GetBytes(
        $"P6\n{imageWidth} {imageHeight}\n255\n"));
    for (int offset = 0; offset < left.Length; offset += 4)
    {
        writer.Write((byte)Math.Abs(left[offset] - right[offset]));
        writer.Write((byte)Math.Abs(left[offset + 1] - right[offset + 1]));
        writer.Write((byte)Math.Abs(left[offset + 2] - right[offset + 2]));
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
    TimingSummary Native,
    TimingSummary Managed,
    double NativeToManagedP95Ratio,
    ulong CombinedMetalAllocatedBytes,
    string NativePayloadHash,
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
