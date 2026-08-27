using System.Diagnostics;
using System.Text.Json;
using ACadSharp.Entities;
using CSMath;
using ProGPU.CAD;

int entityCount = ReadNonNegativeInt("--entities", 10_000);
int blockArrayColumnCount = ReadNonNegativeInt("--block-array-columns", 0);
int warmupCount = ReadNonNegativeInt("--warmup", 3);
int iterationCount = ReadPositiveInt("--iterations", 24);
int queryCount = ReadPositiveInt("--queries", 10_000);
string? outputPath = ReadString("--output-json");

if (entityCount == 0 && blockArrayColumnCount == 0)
{
    throw new ArgumentException(
        "At least one ordinary entity or block-array column is required.");
}

if (blockArrayColumnCount > ushort.MaxValue)
{
    throw new ArgumentOutOfRangeException(
        nameof(blockArrayColumnCount),
        $"--block-array-columns cannot exceed {ushort.MaxValue}.");
}

CadDocumentSession session = CreateDocument(entityCount, blockArrayColumnCount);
var snapshotCompiler = new CadSnapshotCompiler();
var sceneCompiler = new CadPlanSceneCompiler();

for (int i = 0; i < warmupCount; i++)
{
    CadDocumentSnapshot warmSnapshot = snapshotCompiler.Compile(session);
    _ = sceneCompiler.Compile(warmSnapshot);
}

Measurement snapshotMeasurement = Measure(
    "snapshot",
    iterationCount,
    () => snapshotCompiler.Compile(session));
CadDocumentSnapshot snapshot = snapshotCompiler.Compile(session);
Measurement sceneMeasurement = Measure(
    "plan-scene",
    iterationCount,
    () => sceneCompiler.Compile(snapshot));
CadRecordedPlanScene recordedScene = sceneCompiler.Compile(snapshot);
Measurement queryMeasurement = MeasureQueries(snapshot, queryCount);

var report = new CadBenchmarkReport(
    DateTimeOffset.UtcNow,
    Environment.OSVersion.ToString(),
    System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    entityCount,
    blockArrayColumnCount,
    warmupCount,
    iterationCount,
    queryCount,
    snapshot.Statistics,
    snapshot.SpatialIndex.NodeCount,
    recordedScene.Statistics.RecordedCommandCount,
    snapshotMeasurement,
    sceneMeasurement,
    queryMeasurement,
    Process.GetCurrentProcess().WorkingSet64);

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
string json = JsonSerializer.Serialize(report, jsonOptions);
Console.WriteLine(json);
if (outputPath is not null)
{
    File.WriteAllText(outputPath, json);
}

CadDocumentSession CreateDocument(int count, int arrayColumns)
{
    CadDocumentSession result = CadDocumentSession.CreateNew();
    result.Edit("Build benchmark document", document =>
    {
        for (int i = 0; i < count; i++)
        {
            double x = (i % 1_000) * 12.0;
            double y = (i / 1_000) * 12.0;
            switch (i & 3)
            {
                case 0:
                    document.Entities.Add(new Line(
                        new XYZ(x, y, i % 17),
                        new XYZ(x + 9, y + 7, (i % 17) + 2)));
                    break;
                case 1:
                    document.Entities.Add(new Circle
                    {
                        Center = new XYZ(x, y, 0),
                        Radius = 4,
                        Normal = i % 13 == 0 ? new XYZ(0, 1, 1) : XYZ.AxisZ,
                    });
                    break;
                case 2:
                    document.Entities.Add(new Arc
                    {
                        Center = new XYZ(x, y, 0),
                        Radius = 5,
                        StartAngle = 0.17,
                        EndAngle = 4.71,
                        Normal = XYZ.AxisZ,
                    });
                    break;
                default:
                    var polyline = new LwPolyline();
                    polyline.Vertices.Add(new LwPolyline.Vertex(x, y) { Bulge = 0.35 });
                    polyline.Vertices.Add(new LwPolyline.Vertex(x + 5, y + 8));
                    polyline.Vertices.Add(new LwPolyline.Vertex(x + 10, y));
                    document.Entities.Add(polyline);
                break;
            }
        }

        if (arrayColumns > 0)
        {
            var block = new ACadSharp.Tables.BlockRecord("BENCHMARK_ARRAY_ITEM");
            block.Entities.Add(new Line(XYZ.Zero, new XYZ(9, 7, 0)));
            document.Entities.Add(new Insert(block)
            {
                ColumnCount = checked((ushort)arrayColumns),
                ColumnSpacing = 12,
            });
        }
    });
    return result;
}

Measurement Measure(string name, int count, Func<object> action)
{
    var elapsed = new double[count];
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        long started = Stopwatch.GetTimestamp();
        object value = action();
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        checksum ^= value.GetHashCode();
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize(name, elapsed, allocated / count);
}

Measurement MeasureQueries(CadDocumentSnapshot source, int count)
{
    var elapsed = new double[count];
    Span<int> hits = stackalloc int[512];
    CadBounds3D bounds = source.Bounds;
    double width = bounds.Max.X - bounds.Min.X;
    double height = bounds.Max.Y - bounds.Min.Y;
    _ = source.SpatialIndex.Query(bounds, hits);
    _ = GC.GetAllocatedBytesForCurrentThread();
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        double phase = (i % 997) / 997.0;
        double x = bounds.Min.X + (width * phase);
        double y = bounds.Min.Y + (height * (1.0 - phase));
        var query = new CadBounds3D(
            new CadPoint3D(x, y, bounds.Min.Z),
            new CadPoint3D(x + 120, y + 120, bounds.Max.Z));
        long started = Stopwatch.GetTimestamp();
        checksum += source.SpatialIndex.Query(query, hits).TotalCount;
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize("spatial-query-ns", elapsed, allocated / count);
}

static Measurement Summarize(string name, double[] values, long allocatedBytesPerOperation)
{
    Array.Sort(values);
    return new Measurement(
        name,
        Percentile(values, 0.50),
        Percentile(values, 0.95),
        Percentile(values, 0.99),
        values.Average(),
        allocatedBytesPerOperation);
}

static double Percentile(double[] sorted, double percentile)
{
    int index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
    return sorted[index];
}

int ReadPositiveInt(string name, int fallback)
{
    string? value = ReadString(name);
    return value is null
        ? fallback
        : int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{name} must be a positive integer.");
}

int ReadNonNegativeInt(string name, int fallback)
{
    string? value = ReadString(name);
    return value is null
        ? fallback
        : int.TryParse(value, out int parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{name} must be a non-negative integer.");
}

string? ReadString(string name)
{
    int index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index < 0 || index + 1 >= args.Length ? null : args[index + 1];
}

internal sealed record Measurement(
    string Name,
    double P50,
    double P95,
    double P99,
    double Mean,
    long AllocatedBytesPerOperation);

internal sealed record CadBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    int EntityCount,
    int BlockArrayColumnCount,
    int WarmupCount,
    int IterationCount,
    int QueryCount,
    CadSnapshotStatistics Statistics,
    int SpatialNodeCount,
    int RecordedCommandCount,
    Measurement SnapshotMilliseconds,
    Measurement PlanSceneMilliseconds,
    Measurement SpatialQueryNanoseconds,
    long WorkingSetBytes);
