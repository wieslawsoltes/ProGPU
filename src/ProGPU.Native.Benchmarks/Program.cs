using System.Diagnostics;
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
bool synchronizeEachFrame = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--sync",
        StringComparison.OrdinalIgnoreCase));

NativeSolidRectangle[] rectangles = CreateRectangles(rectangleCount);
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
DrawingVisual managedVisual = CreateManagedVisual(rectangles);

// Compile both shader/pipeline paths before correctness or timing evidence.
RenderNative();
RenderManaged();
context.PollDevice(wait: true);

byte[] nativePixels = nativeTarget.ReadPixels();
byte[] managedPixels = managedTarget.ReadPixels();
PixelComparison comparison = ComparePixels(nativePixels, managedPixels);
if (comparison.MaximumChannelDifference > 3 ||
    comparison.PixelsOverTolerance > comparison.PixelCount / 1000)
{
    throw new InvalidOperationException(
        $"Native/managed output diverged: max={comparison.MaximumChannelDifference}, " +
        $"pixelsOverTolerance={comparison.PixelsOverTolerance}/{comparison.PixelCount}.");
}

for (int index = 0; index < warmupCount; index++)
{
    if ((index & 1) == 0)
    {
        RenderNative();
        RenderManaged();
    }
    else
    {
        RenderManaged();
        RenderNative();
    }
    SynchronizeIfRequested();
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
    SynchronizeIfRequested();
}
context.PollDevice(wait: true);
GC.KeepAlive(nativeAllocationStart);

TimingSummary nativeSummary = Summarize(nativeTimes, nativeAllocatedBytes);
TimingSummary managedSummary = Summarize(managedTimes, managedAllocatedBytes);
var report = new BenchmarkReport(
    RuntimeInformation: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Adapter: context.AdapterName,
    Backend: context.AdapterBackendType.ToString(),
    RectangleCount: rectangleCount,
    WarmupIterations: warmupCount,
    MeasuredIterations: iterationCount,
    SynchronizeEachFrame: synchronizeEachFrame,
    Native: nativeSummary,
    Managed: managedSummary,
    NativeToManagedP95Ratio: managedSummary.P95Milliseconds == 0
        ? 0
        : nativeSummary.P95Milliseconds / managedSummary.P95Milliseconds,
    PixelParity: comparison);

Console.WriteLine(JsonSerializer.Serialize(
    report,
    new JsonSerializerOptions { WriteIndented = true }));

void RenderNative()
{
    native.Render(
        nativeTarget,
        dpiScale: 1f,
        rectangles,
        clearColor);
}

void RenderManaged()
{
    managed.RenderOffscreen(
        managedVisual,
        width,
        height,
        managedTarget,
        padding: 0f,
        dpiScale: 1f,
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
    double milliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
    return milliseconds;
}

double MeasureManaged(out long allocatedBytes)
{
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    long timestamp = Stopwatch.GetTimestamp();
    RenderManaged();
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

static NativeSolidRectangle[] CreateRectangles(int count)
{
    var result = new NativeSolidRectangle[count];
    const float inset = 18f;
    const float gap = 3f;
    float usableWidth = width - inset * 2f;
    float usableHeight = height - inset * 2f;
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
    ReadOnlySpan<NativeSolidRectangle> rectangles)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(width, height)
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
    ulong nativeHash = 14695981039346656037UL;
    ulong managedHash = 14695981039346656037UL;
    for (int offset = 0; offset < native.Length; offset += 4)
    {
        bool overTolerance = false;
        for (int channel = 0; channel < 4; channel++)
        {
            int difference = Math.Abs(native[offset + channel] - managed[offset + channel]);
            maximum = Math.Max(maximum, difference);
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
        nativeHash.ToString("X16"),
        managedHash.ToString("X16"));
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
    int RectangleCount,
    int WarmupIterations,
    int MeasuredIterations,
    bool SynchronizeEachFrame,
    TimingSummary Native,
    TimingSummary Managed,
    double NativeToManagedP95Ratio,
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
    string NativeFnv1A64,
    string ManagedFnv1A64);
