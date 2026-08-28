using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.CAD;
using ProGPU.Fonts.Inter;
using ProGPU.Text;

int entityCount = ReadNonNegativeInt("--entities", 10_000);
int blockArrayColumnCount = ReadNonNegativeInt("--block-array-columns", 0);
int textEntityCount = ReadNonNegativeInt("--text-entities", 0);
int shxTextEntityCount = ReadNonNegativeInt("--shx-text-entities", 0);
bool decorateText = HasFlag("--text-decorations");
bool decorateShxText = HasFlag("--shx-decorations");
bool lowerLineTypes = HasFlag("--linetypes");
bool lowerComplexLineTypes = HasFlag("--complex-linetypes");
bool lowerLinearSplineLineTypes = HasFlag("--linear-spline-linetypes");
bool lowerNurbsSplineLineTypes = HasFlag("--nurbs-spline-linetypes");
bool lowerPeriodicSplineLineTypes = HasFlag("--periodic-spline-linetypes");
bool measureSplineSelection = HasFlag("--spline-selection");
int shxInterpretationCount = ReadNonNegativeInt("--shx-interpretations", 0);
int shxLayoutCount = ReadNonNegativeInt("--shx-layouts", 0);
int warmupCount = ReadNonNegativeInt("--warmup", 3);
int iterationCount = ReadPositiveInt("--iterations", 24);
int queryCount = ReadPositiveInt("--queries", 10_000);
string? outputPath = ReadString("--output-json");

if (entityCount == 0 && blockArrayColumnCount == 0 && textEntityCount == 0 &&
    shxTextEntityCount == 0)
{
    throw new ArgumentException(
        "At least one ordinary entity, block-array column, or text entity is required.");
}

if (blockArrayColumnCount > ushort.MaxValue)
{
    throw new ArgumentOutOfRangeException(
        nameof(blockArrayColumnCount),
        $"--block-array-columns cannot exceed {ushort.MaxValue}.");
}

if (measureSplineSelection &&
    (entityCount == 0 || blockArrayColumnCount != 0 ||
     textEntityCount != 0 || shxTextEntityCount != 0))
{
    throw new ArgumentException(
        "--spline-selection requires a positive --entities count and no block-array or text fixtures.");
}

CadDocumentSession session = CreateDocument(
    entityCount,
    blockArrayColumnCount,
    textEntityCount,
    shxTextEntityCount,
    decorateText,
    decorateShxText,
    lowerLineTypes || lowerComplexLineTypes || lowerLinearSplineLineTypes ||
        lowerNurbsSplineLineTypes || lowerPeriodicSplineLineTypes,
    lowerComplexLineTypes,
    lowerLinearSplineLineTypes,
    lowerNurbsSplineLineTypes,
    lowerPeriodicSplineLineTypes,
    measureSplineSelection);
var snapshotCompiler = new CadSnapshotCompiler();
var pageSetupCompiler = new CadPageSetupCatalogCompiler();
var sceneCompiler = new CadPlanSceneCompiler();
var printPlanCompiler = new CadPrintPlanCompiler();
var rotatedPrintOptions = new CadPrintPlanOptions
{
    Rotation = CadPageRotation.CounterClockwise270,
};
CadShxFont? shxFont = shxInterpretationCount == 0 && shxLayoutCount == 0 &&
    shxTextEntityCount == 0
    ? null
    : CreateBenchmarkShxFont();
CadShxGlyphCache? shxCache = shxLayoutCount == 0 && shxTextEntityCount == 0
    ? null
    : new CadShxGlyphCache(shxFont!);
CadShxFontCatalog? shxCatalog = null;
if (shxTextEntityCount != 0)
{
    shxCatalog = new CadShxFontCatalog();
    shxCatalog.Register("benchmark.shx", shxCache!);
}
CadSnapshotOptions snapshotOptions = new()
{
    TextFontResolver = textEntityCount == 0 && !lowerComplexLineTypes
        ? null
        : new BenchmarkTextFontResolver(InterFontFamily.Regular),
    ShxFontResolver = shxTextEntityCount == 0
        ? null
        : shxCatalog,
};

CadDocumentSnapshot validationSnapshot = snapshotCompiler.Compile(session, snapshotOptions);
ValidateRequestedEntities(validationSnapshot);

for (int i = 0; i < warmupCount; i++)
{
    CadDocumentSnapshot warmSnapshot = snapshotCompiler.Compile(session, snapshotOptions);
    _ = pageSetupCompiler.Compile(session);
    _ = sceneCompiler.Compile(warmSnapshot);
    using CadPrintPlan warmPrintPlan = printPlanCompiler.Compile(warmSnapshot);
    using CadPrintPlan warmRotatedPrintPlan = printPlanCompiler.Compile(
        warmSnapshot,
        rotatedPrintOptions);
    if (shxFont is not null)
    {
        if (shxInterpretationCount != 0)
        {
            _ = InterpretShxBatch(shxFont, shxInterpretationCount);
        }
        if (shxCache is not null && shxLayoutCount != 0)
        {
            _ = LayoutShxBatch(shxCache, shxLayoutCount);
        }
    }
}

Measurement snapshotMeasurement = Measure(
    "snapshot",
    iterationCount,
    () => snapshotCompiler.Compile(session, snapshotOptions));
Measurement pageSetupMeasurement = Measure(
    "page-setup-catalog",
    iterationCount,
    () => pageSetupCompiler.Compile(session));
CadDocumentSnapshot snapshot = snapshotCompiler.Compile(session, snapshotOptions);
Measurement sceneMeasurement = Measure(
    "plan-scene",
    iterationCount,
    () => sceneCompiler.Compile(snapshot));
CadRecordedPlanScene recordedScene = sceneCompiler.Compile(snapshot);
Measurement printPlanMeasurement = Measure(
    "print-plan",
    iterationCount,
    () => printPlanCompiler.Compile(snapshot));
Measurement rotatedPrintPlanMeasurement = Measure(
    "rotated-print-plan",
    iterationCount,
    () => printPlanCompiler.Compile(snapshot, rotatedPrintOptions));
Measurement queryMeasurement = MeasureQueries(snapshot, queryCount);
Measurement? splinePointSelectionMeasurement = measureSplineSelection
    ? MeasureSplinePointSelections(snapshot, queryCount)
    : null;
Measurement? splineBoundsSelectionMeasurement = measureSplineSelection
    ? MeasureSplineBoundsSelections(snapshot, queryCount)
    : null;
Measurement? shxMeasurement = shxInterpretationCount == 0
    ? null
    : Measure(
        "shx-interpret-batch",
        iterationCount,
        () => InterpretShxBatch(shxFont!, shxInterpretationCount));
Measurement? shxLayoutMeasurement = shxCache is null || shxLayoutCount == 0
    ? null
    : Measure(
        "shx-layout-batch",
        iterationCount,
        () => LayoutShxBatch(shxCache, shxLayoutCount));

var report = new CadBenchmarkReport(
    DateTimeOffset.UtcNow,
    Environment.OSVersion.ToString(),
    System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    entityCount,
    blockArrayColumnCount,
    textEntityCount,
    shxTextEntityCount,
    decorateText,
    decorateShxText,
    lowerLineTypes || lowerComplexLineTypes || lowerLinearSplineLineTypes ||
        lowerNurbsSplineLineTypes || lowerPeriodicSplineLineTypes,
    lowerComplexLineTypes,
    lowerLinearSplineLineTypes,
    lowerNurbsSplineLineTypes,
    lowerPeriodicSplineLineTypes,
    measureSplineSelection,
    shxInterpretationCount,
    shxLayoutCount,
    warmupCount,
    iterationCount,
    queryCount,
    snapshot.Statistics,
    snapshot.SpatialIndex.NodeCount,
    recordedScene.Statistics.RecordedCommandCount,
    recordedScene.Statistics,
    snapshotMeasurement,
    pageSetupMeasurement,
    sceneMeasurement,
    printPlanMeasurement,
    rotatedPrintPlanMeasurement,
    queryMeasurement,
    splinePointSelectionMeasurement,
    splineBoundsSelectionMeasurement,
    shxMeasurement,
    shxLayoutMeasurement,
    Process.GetCurrentProcess().WorkingSet64);

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
string json = JsonSerializer.Serialize(report, jsonOptions);
Console.WriteLine(json);
if (outputPath is not null)
{
    File.WriteAllText(outputPath, json);
}

void ValidateRequestedEntities(CadDocumentSnapshot source)
{
    int expectedSource = checked(
        entityCount +
        (blockArrayColumnCount == 0 ? 0 : 1) +
        textEntityCount +
        shxTextEntityCount);
    int expectedExpanded = checked(
        entityCount +
        (blockArrayColumnCount == 0 ? 0 : blockArrayColumnCount + 1) +
        textEntityCount +
        shxTextEntityCount);
    if (source.Statistics.SourceEntityCount == expectedSource &&
        source.Statistics.VisibleEntityCount == expectedSource &&
        source.Statistics.ExpandedEntityCount == expectedExpanded &&
        source.Statistics.UnsupportedEntityCount == 0 &&
        source.Statistics.InvalidEntityCount == 0)
    {
        return;
    }

    string diagnostics = string.Join(
        Environment.NewLine,
        source.Diagnostics.Span.ToArray().Select(item => $"{item.Code}: {item.Message}"));
    throw new InvalidOperationException(
        $"The benchmark fixture did not compile exactly: expected {expectedSource} source entities, " +
        $"observed {source.Statistics.SourceEntityCount}, unsupported " +
        $"{source.Statistics.UnsupportedEntityCount}, invalid {source.Statistics.InvalidEntityCount}, " +
        $"expected/observed expanded {expectedExpanded}/{source.Statistics.ExpandedEntityCount}." +
        (diagnostics.Length == 0 ? string.Empty : Environment.NewLine + diagnostics));
}

CadDocumentSession CreateDocument(
    int count,
    int arrayColumns,
    int textCount,
    int shxTextCount,
    bool decorateTextRuns,
    bool decorateShxTextRuns,
    bool useLineTypes,
    bool useComplexLineTypes,
    bool useLinearSplineLineTypes,
    bool useNurbsSplineLineTypes,
    bool usePeriodicSplineLineTypes,
    bool useSplineSelection)
{
    CadDocumentSession result = CadDocumentSession.CreateNew();
    result.Edit("Build benchmark document", document =>
    {
        LineType? benchmarkLineType = null;
        if (useLineTypes)
        {
            TextStyle? lineTypeTextStyle = null;
            if (useComplexLineTypes)
            {
                lineTypeTextStyle = new TextStyle("BENCHMARK_LTYPE_TEXT")
                {
                    Filename = "Inter.ttf",
                };
                document.TextStyles.Add(lineTypeTextStyle);
            }
            benchmarkLineType = new LineType(
                useComplexLineTypes ? "BENCHMARK_COMPLEX" : "BENCHMARK_DASHDOT");
            benchmarkLineType.AddSegment(new LineType.Segment { Length = 3.0 });
            benchmarkLineType.AddSegment(new LineType.Segment { Length = -1.5 });
            benchmarkLineType.AddSegment(useComplexLineTypes
                ? new LineType.Segment
                {
                    Text = "GAS",
                    Style = lineTypeTextStyle,
                    Scale = 0.5,
                    Flags = LineTypeShapeFlags.Text,
                }
                : new LineType.Segment { Length = 0.0 });
            benchmarkLineType.AddSegment(new LineType.Segment { Length = -1.5 });
            document.LineTypes.Add(benchmarkLineType);
        }

        for (int i = 0; i < count; i++)
        {
            double x = (i % 1_000) * 12.0;
            double y = (i / 1_000) * 12.0;
            if (usePeriodicSplineLineTypes)
            {
                var spline = new Spline
                {
                    Degree = 2,
                    IsClosed = true,
                    IsPeriodic = true,
                    LineType = benchmarkLineType ?? document.LineTypes.Continuous,
                };
                spline.ControlPoints.AddRange([
                    new XYZ(x, y, i % 17),
                    new XYZ(x + 4, y + 6, (i % 17) + 1),
                    new XYZ(x + 8, y, (i % 17) + 2),
                    new XYZ(x + 4, y - 6, (i % 17) + 1),
                ]);
                spline.Knots.AddRange([-2, -1, 0, 1, 2, 3, 4, 5, 6]);
                spline.Weights.AddRange([1, 2, 1, 2]);
                document.Entities.Add(spline);
                continue;
            }
            if (useNurbsSplineLineTypes || useSplineSelection)
            {
                var spline = new Spline
                {
                    Degree = 2,
                    LineType = benchmarkLineType ?? document.LineTypes.Continuous,
                };
                spline.ControlPoints.AddRange([
                    new XYZ(x, y, i % 17),
                    new XYZ(x + 2, y + 4, (i % 17) + 1),
                    new XYZ(x + 4, y, (i % 17) + 2),
                    new XYZ(x + 6, y - 4, (i % 17) + 1),
                    new XYZ(x + 8, y, i % 17),
                ]);
                spline.Knots.AddRange([0, 0, 0, 1, 2, 3, 3, 3]);
                spline.Weights.AddRange([1, 2, 1, 3, 1]);
                document.Entities.Add(spline);
                continue;
            }
            if (useLinearSplineLineTypes)
            {
                var spline = new Spline
                {
                    Degree = 1,
                    LineType = benchmarkLineType ?? document.LineTypes.Continuous,
                };
                spline.ControlPoints.AddRange([
                    new XYZ(x, y, i % 17),
                    new XYZ(x + 5, y + 8, (i % 17) + 1),
                    new XYZ(x + 10, y, (i % 17) + 2),
                ]);
                spline.Knots.AddRange([0, 0, 1, 2, 2]);
                spline.Weights.AddRange([1, 2, 1]);
                document.Entities.Add(spline);
                continue;
            }

            switch (i & 3)
            {
                case 0:
                    document.Entities.Add(new Line(
                        new XYZ(x, y, i % 17),
                        new XYZ(x + 9, y + 7, (i % 17) + 2))
                    {
                        LineType = benchmarkLineType ?? document.LineTypes.Continuous,
                    });
                    break;
                case 1:
                    document.Entities.Add(new Circle
                    {
                        Center = new XYZ(x, y, 0),
                        Radius = 4,
                        Normal = i % 13 == 0 ? new XYZ(0, 1, 1) : XYZ.AxisZ,
                        LineType = benchmarkLineType ?? document.LineTypes.Continuous,
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
                        LineType = benchmarkLineType ?? document.LineTypes.Continuous,
                    });
                    break;
                default:
                    var polyline = new LwPolyline
                    {
                        LineType = benchmarkLineType ?? document.LineTypes.Continuous,
                        Flags = LwPolylineFlags.Plinegen,
                    };
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

        if (textCount > 0)
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            for (int i = 0; i < textCount; i++)
            {
                document.Entities.Add(new TextEntity(
                    decorateTextRuns
                        ? "%%uProGPU%%u %%oCAD%%o %%k0123456789%%k"
                        : "ProGPU CAD 0123456789")
                {
                    Style = textStyle,
                    InsertPoint = new XYZ((i % 100) * 24.0, (i / 100) * 4.0, 0),
                    Height = 2.5,
                    WidthFactor = 0.9,
                    ObliqueAngle = (i & 1) == 0 ? 0.0 : 0.08,
                });
            }
        }

        if (shxTextCount > 0)
        {
            var textStyle = new TextStyle("BENCHMARK_SHX") { Filename = "benchmark.shx" };
            document.TextStyles.Add(textStyle);
            for (int i = 0; i < shxTextCount; i++)
            {
                document.Entities.Add(new TextEntity(
                    decorateShxTextRuns
                        ? "%%uAAA%%u%%oAAA%%o%%kAA%%k"
                        : "AAAAAAAA")
                {
                    Style = textStyle,
                    InsertPoint = new XYZ((i % 100) * 32.0, (i / 100) * 4.0, 0),
                    Height = 2.5,
                    WidthFactor = 0.9,
                    ObliqueAngle = (i & 1) == 0 ? 0.0 : 0.08,
                });
            }
        }
    });
    return result;
}

CadShxFont CreateBenchmarkShxFont()
{
    byte[] header = { 10, 2, 0, 0 };
    byte[] program =
    {
        0x14, 0x10, 0x1C, 0x18, 0x12,
        2, 8, 1, 0, 1, 10, 1, 0x02,
        12, 10, 0, 127,
        13, 10, 0, 0, 0, 0,
        2, 8, 10, unchecked((byte)-2),
        0,
    };
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
    writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
    writer.Write((ushort)0);
    writer.Write((ushort)65);
    writer.Write((ushort)2);
    writer.Write((ushort)0);
    writer.Write(checked((ushort)("BENCHMARK".Length + 1 + header.Length)));
    writer.Write((ushort)65);
    writer.Write(checked((ushort)("BENCHMARK".Length + 1 + program.Length)));
    writer.Write("BENCHMARK"u8);
    writer.Write((byte)0);
    writer.Write(header);
    writer.Write("BENCHMARK"u8);
    writer.Write((byte)0);
    writer.Write(program);
    writer.Write("EOF"u8);
    return CadShxFont.Parse(stream.ToArray());
}

object InterpretShxBatch(CadShxFont font, int count)
{
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        CadShxGeometry geometry = CadShxInterpreter.Interpret(font, 65);
        checksum = HashCode.Combine(checksum, geometry.SegmentCount, geometry.EndPoint);
    }
    return checksum;
}

object LayoutShxBatch(CadShxGlyphCache cache, int count)
{
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        var layout = new CadShxTextLayout("AAAAAAAA", cache);
        checksum = HashCode.Combine(checksum, layout.Glyphs.Length, layout.Advance);
    }
    return checksum;
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
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
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

Measurement MeasureSplinePointSelections(CadDocumentSnapshot source, int count)
{
    CadSelectionCandidate[] candidates = CreateSelectionCandidates(source);
    var elapsed = new double[count];
    CadSelectionCandidate warmCandidate = candidates[0];
    _ = CadSelectionHitTester.HitTestPoint(
        source,
        warmCandidate,
        warmCandidate.Bounds.Center,
        tolerance: 1.0);
    _ = GC.GetAllocatedBytesForCurrentThread();
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        CadSelectionCandidate candidate = candidates[i % candidates.Length];
        CadPoint3D center = candidate.Bounds.Center;
        var point = new CadPoint3D(
            center.X + ((i & 1) == 0 ? 0.25 : -0.25),
            center.Y + 0.5,
            center.Z);
        long started = Stopwatch.GetTimestamp();
        CadPointHitResult result = CadSelectionHitTester.HitTestPoint(
            source,
            candidate,
            point,
            tolerance: 1.0);
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        checksum += (int)result.Status;
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize("spline-point-selection-ns", elapsed, allocated / count);
}

Measurement MeasureSplineBoundsSelections(CadDocumentSnapshot source, int count)
{
    CadSelectionCandidate[] candidates = CreateSelectionCandidates(source);
    var elapsed = new double[count];
    CadSelectionCandidate warmCandidate = candidates[0];
    CadBounds3D warmBounds = CreateSelectionBounds(warmCandidate.Bounds.Center);
    _ = CadSelectionHitTester.HitTestBounds(
        source,
        warmCandidate,
        warmBounds,
        CadBoundsSelectionMode.Crossing);
    _ = GC.GetAllocatedBytesForCurrentThread();
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        CadSelectionCandidate candidate = candidates[i % candidates.Length];
        CadBounds3D bounds = CreateSelectionBounds(candidate.Bounds.Center);
        CadBoundsSelectionMode mode = (i & 1) == 0
            ? CadBoundsSelectionMode.Crossing
            : CadBoundsSelectionMode.Window;
        long started = Stopwatch.GetTimestamp();
        CadBoundsHitResult result = CadSelectionHitTester.HitTestBounds(
            source,
            candidate,
            bounds,
            mode);
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        checksum += (int)result.Status;
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize("spline-bounds-selection-ns", elapsed, allocated / count);
}

CadSelectionCandidate[] CreateSelectionCandidates(CadDocumentSnapshot source)
{
    ReadOnlySpan<CadEntityHeader> entities = source.Entities.Span;
    var candidates = new CadSelectionCandidate[entities.Length];
    for (int i = 0; i < entities.Length; i++)
    {
        CadEntityHeader entity = entities[i];
        if (entity.Kind != CadEntityKind.Spline)
        {
            throw new InvalidOperationException(
                "The spline-selection benchmark requires an all-spline fixture.");
        }
        candidates[i] = new CadSelectionCandidate(
            source.ContentGeneration,
            i,
            entity.Handle,
            entity.Kind,
            entity.Bounds);
    }
    return candidates;
}

static CadBounds3D CreateSelectionBounds(CadPoint3D center) =>
    new(
        new CadPoint3D(center.X - 0.5, center.Y - 0.5, center.Z - 0.5),
        new CadPoint3D(center.X + 0.5, center.Y + 0.5, center.Z + 0.5));

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

bool HasFlag(string name) =>
    Array.Exists(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));

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
    int TextEntityCount,
    int ShxTextEntityCount,
    bool DecoratedText,
    bool DecoratedShxText,
    bool LoweredLineTypes,
    bool LoweredComplexLineTypes,
    bool LoweredLinearSplineLineTypes,
    bool LoweredNurbsSplineLineTypes,
    bool LoweredPeriodicSplineLineTypes,
    bool MeasuredSplineSelection,
    int ShxInterpretationCount,
    int ShxLayoutCount,
    int WarmupCount,
    int IterationCount,
    int QueryCount,
    CadSnapshotStatistics Statistics,
    int SpatialNodeCount,
    int RecordedCommandCount,
    CadPlanSceneStatistics SceneStatistics,
    Measurement SnapshotMilliseconds,
    Measurement PageSetupCatalogMilliseconds,
    Measurement PlanSceneMilliseconds,
    Measurement PrintPlanMilliseconds,
    Measurement RotatedPrintPlanMilliseconds,
    Measurement SpatialQueryNanoseconds,
    Measurement? SplinePointSelectionNanoseconds,
    Measurement? SplineBoundsSelectionNanoseconds,
    Measurement? ShxInterpretBatchMilliseconds,
    Measurement? ShxLayoutBatchMilliseconds,
    long WorkingSetBytes);

internal sealed class BenchmarkTextFontResolver(TtfFont font) : ICadTextFontResolver
{
    public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
        new(font, IsSubstitution: false);
}
