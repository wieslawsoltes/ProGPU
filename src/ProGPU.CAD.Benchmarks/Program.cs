using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using ProGPU.CAD;
using ProGPU.Fonts.Inter;
using ProGPU.Text;

int entityCount = ReadNonNegativeInt("--entities", 10_000);
bool resolveDrawOrder = HasFlag("--draw-order");
int drawOrderEditEntityCount = ReadNonNegativeInt(
    "--draw-order-edit-entities",
    0);
int blockArrayColumnCount = ReadNonNegativeInt("--block-array-columns", 0);
int textEntityCount = ReadNonNegativeInt("--text-entities", 0);
int mtextEntityCount = ReadNonNegativeInt("--mtext-entities", 0);
int shxTextEntityCount = ReadNonNegativeInt("--shx-text-entities", 0);
int shxMTextEntityCount = ReadNonNegativeInt("--shx-mtext-entities", 0);
int attributeInsertCount = ReadNonNegativeInt("--attribute-inserts", 0);
int dimensionEntityCount = ReadNonNegativeInt("--dimension-entities", 0);
int thickSolidEntityCount = ReadNonNegativeInt("--thick-solid-entities", 0);
int meshEntityCount = ReadNonNegativeInt("--mesh-entities", 0);
int meshSubdivisionLevel = ReadNonNegativeInt("--mesh-subdivision-level", 0);
int polygonMeshEntityCount = ReadNonNegativeInt("--polygon-mesh-entities", 0);
int polyfaceMeshEntityCount = ReadNonNegativeInt("--polyface-mesh-entities", 0);
int pointEntityCount = ReadNonNegativeInt("--point-entities", 0);
bool compoundPointMarkers = HasFlag("--compound-point-markers");
int constructionLineCount = ReadNonNegativeInt("--construction-lines", 0);
int solidHatchCount = ReadNonNegativeInt("--solid-hatches", 0);
int patternHatchCount = ReadNonNegativeInt("--pattern-hatches", 0);
bool complexPatternGrammar = HasFlag("--complex-pattern-grammar");
bool hatchIslandStyles = HasFlag("--hatch-island-styles");
bool hatchSplineEdges = HasFlag("--hatch-spline-edges");
bool rationalHatchSplineEdges = HasFlag("--rational-hatch-spline-edges");
bool rationalCubicHatchSplineEdges = HasFlag("--rational-cubic-hatch-spline-edges");
bool decorateText = HasFlag("--text-decorations");
bool decorateShxText = HasFlag("--shx-decorations");
bool lowerLineTypes = HasFlag("--linetypes");
bool lowerComplexLineTypes = HasFlag("--complex-linetypes");
bool lowerLinearSplineLineTypes = HasFlag("--linear-spline-linetypes");
bool lowerNurbsSplineLineTypes = HasFlag("--nurbs-spline-linetypes");
bool lowerPeriodicSplineLineTypes = HasFlag("--periodic-spline-linetypes");
bool measureSplineSelection = HasFlag("--spline-selection");
bool measureTextSelection = HasFlag("--text-selection");
bool measureHatchSelection = HasFlag("--hatch-selection");
int shxInterpretationCount = ReadNonNegativeInt("--shx-interpretations", 0);
int shxLayoutCount = ReadNonNegativeInt("--shx-layouts", 0);
int warmupCount = ReadNonNegativeInt("--warmup", 3);
int iterationCount = ReadPositiveInt("--iterations", 24);
int queryCount = ReadPositiveInt("--queries", 10_000);
string? outputPath = ReadString("--output-json");

if (entityCount == 0 && blockArrayColumnCount == 0 && textEntityCount == 0 &&
    mtextEntityCount == 0 && shxTextEntityCount == 0 && shxMTextEntityCount == 0 &&
    attributeInsertCount == 0 && dimensionEntityCount == 0 &&
    thickSolidEntityCount == 0 && meshEntityCount == 0 &&
    polygonMeshEntityCount == 0 && polyfaceMeshEntityCount == 0 &&
    pointEntityCount == 0 && constructionLineCount == 0 &&
    solidHatchCount == 0 && patternHatchCount == 0)
{
    throw new ArgumentException(
        "At least one ordinary entity, block-array column, text entity, attributed INSERT, DIMENSION, thick SOLID, MESH, or HATCH is required.");
}

if (blockArrayColumnCount > ushort.MaxValue)
{
    throw new ArgumentOutOfRangeException(
        nameof(blockArrayColumnCount),
        $"--block-array-columns cannot exceed {ushort.MaxValue}.");
}
if (meshSubdivisionLevel > CadSnapshotOptions.DefaultMaxMeshSubdivisionLevel)
{
    throw new ArgumentOutOfRangeException(
        nameof(meshSubdivisionLevel),
        $"--mesh-subdivision-level cannot exceed {CadSnapshotOptions.DefaultMaxMeshSubdivisionLevel}.");
}
if (meshSubdivisionLevel != 0 && meshEntityCount == 0)
{
    throw new ArgumentException(
        "--mesh-subdivision-level requires a positive --mesh-entities count.");
}
if (compoundPointMarkers && pointEntityCount == 0)
{
    throw new ArgumentException(
        "--compound-point-markers requires a positive --point-entities count.");
}

if (measureSplineSelection &&
    (entityCount == 0 || blockArrayColumnCount != 0 ||
     textEntityCount != 0 || mtextEntityCount != 0 || shxTextEntityCount != 0 ||
     shxMTextEntityCount != 0 || attributeInsertCount != 0 ||
     dimensionEntityCount != 0 || thickSolidEntityCount != 0 || meshEntityCount != 0 ||
     polygonMeshEntityCount != 0 || polyfaceMeshEntityCount != 0 || pointEntityCount != 0 ||
     constructionLineCount != 0 ||
     solidHatchCount != 0 || patternHatchCount != 0))
{
    throw new ArgumentException(
        "--spline-selection requires a positive --entities count and no block-array or text fixtures.");
}
if (measureTextSelection &&
    (entityCount != 0 || blockArrayColumnCount != 0 || solidHatchCount != 0 ||
     patternHatchCount != 0 || dimensionEntityCount != 0 ||
     thickSolidEntityCount != 0 || meshEntityCount != 0 ||
     polygonMeshEntityCount != 0 || polyfaceMeshEntityCount != 0 || pointEntityCount != 0 ||
     constructionLineCount != 0 ||
     new[]
     {
         textEntityCount,
         mtextEntityCount,
         shxTextEntityCount,
         shxMTextEntityCount,
         attributeInsertCount,
     }
         .Count(static count => count > 0) != 1))
{
    throw new ArgumentException(
        "--text-selection requires exactly one positive text or attributed-INSERT fixture count and no ordinary or block-array fixtures.");
}
if (measureHatchSelection &&
    ((solidHatchCount == 0) == (patternHatchCount == 0) ||
     entityCount != 0 || blockArrayColumnCount != 0 ||
     textEntityCount != 0 || mtextEntityCount != 0 || shxTextEntityCount != 0 ||
     shxMTextEntityCount != 0 || attributeInsertCount != 0 ||
     dimensionEntityCount != 0 || thickSolidEntityCount != 0 || meshEntityCount != 0 ||
     polygonMeshEntityCount != 0 || polyfaceMeshEntityCount != 0 || pointEntityCount != 0 ||
     constructionLineCount != 0))
{
    throw new ArgumentException(
        "--hatch-selection requires exactly one positive --solid-hatches or --pattern-hatches count and no other fixtures.");
}
if (hatchIslandStyles && solidHatchCount == 0 && patternHatchCount == 0)
{
    throw new ArgumentException(
        "--hatch-island-styles requires a positive solid or patterned HATCH count.");
}
if (hatchSplineEdges && solidHatchCount == 0 && patternHatchCount == 0)
{
    throw new ArgumentException(
        "--hatch-spline-edges requires a positive solid or patterned HATCH count.");
}
if (rationalHatchSplineEdges && !hatchSplineEdges)
{
    throw new ArgumentException(
        "--rational-hatch-spline-edges requires --hatch-spline-edges.");
}
if (rationalCubicHatchSplineEdges && !hatchSplineEdges)
{
    throw new ArgumentException(
        "--rational-cubic-hatch-spline-edges requires --hatch-spline-edges.");
}
if (rationalHatchSplineEdges && rationalCubicHatchSplineEdges)
{
    throw new ArgumentException(
        "Only one rational HATCH spline-edge fixture may be selected.");
}

CadDocumentSession session = CreateDocument(
    entityCount,
    blockArrayColumnCount,
    textEntityCount,
    mtextEntityCount,
    shxTextEntityCount,
    shxMTextEntityCount,
    attributeInsertCount,
    dimensionEntityCount,
    thickSolidEntityCount,
    meshEntityCount,
    meshSubdivisionLevel,
    polygonMeshEntityCount,
    polyfaceMeshEntityCount,
    pointEntityCount,
    compoundPointMarkers,
    constructionLineCount,
    solidHatchCount,
    patternHatchCount,
    complexPatternGrammar,
    hatchIslandStyles,
    hatchSplineEdges,
    rationalHatchSplineEdges,
    rationalCubicHatchSplineEdges,
    decorateText,
    decorateShxText,
    lowerLineTypes || lowerComplexLineTypes || lowerLinearSplineLineTypes ||
        lowerNurbsSplineLineTypes || lowerPeriodicSplineLineTypes,
    lowerComplexLineTypes,
    lowerLinearSplineLineTypes,
    lowerNurbsSplineLineTypes,
    lowerPeriodicSplineLineTypes,
    measureSplineSelection,
    resolveDrawOrder);
var snapshotCompiler = new CadSnapshotCompiler();
var pageSetupCompiler = new CadPageSetupCatalogCompiler();
var sceneCompiler = new CadPlanSceneCompiler();
var pointMarkerSceneCompiler = new CadPointMarkerSceneCompiler();
var mesh3DSceneCompiler = new CadMesh3DSceneCompiler();
var printPlanCompiler = new CadPrintPlanCompiler();
CadBounds3D? constructionClip = constructionLineCount == 0
    ? null
    : new CadBounds3D(
        new CadPoint3D(-100, -100, -100),
        new CadPoint3D(12_100, 500, 100));
var printOptions = new CadPrintPlanOptions
{
    PlotBounds = constructionClip,
};
var rotatedPrintOptions = new CadPrintPlanOptions
{
    Rotation = CadPageRotation.CounterClockwise270,
    PlotBounds = constructionClip,
};
CadShxFont? shxFont = shxInterpretationCount == 0 && shxLayoutCount == 0 &&
    shxTextEntityCount == 0 && shxMTextEntityCount == 0
    ? null
    : CreateBenchmarkShxFont();
CadShxGlyphCache? shxCache = shxLayoutCount == 0 && shxTextEntityCount == 0 &&
    shxMTextEntityCount == 0
    ? null
    : new CadShxGlyphCache(shxFont!);
CadShxFontCatalog? shxCatalog = null;
if (shxTextEntityCount != 0 || shxMTextEntityCount != 0)
{
    shxCatalog = new CadShxFontCatalog();
    shxCatalog.Register("benchmark.shx", shxCache!);
}
CadSnapshotOptions snapshotOptions = new()
{
    TextFontResolver = textEntityCount == 0 && mtextEntityCount == 0 &&
        attributeInsertCount == 0 && dimensionEntityCount == 0 &&
        !lowerComplexLineTypes
        ? null
        : new BenchmarkTextFontResolver(InterFontFamily.Regular),
    ShxFontResolver = shxTextEntityCount == 0 && shxMTextEntityCount == 0
        ? null
        : shxCatalog,
};

CadDocumentSnapshot validationSnapshot = snapshotCompiler.Compile(session, snapshotOptions);
ValidateRequestedEntities(validationSnapshot);
ulong[] drawOrderEditHandles = drawOrderEditEntityCount == 0
    ? []
    : session.Read(document =>
    {
        if (drawOrderEditEntityCount > document.Entities.Count)
        {
            throw new ArgumentException(
                "--draw-order-edit-entities cannot exceed the model-space entity count.");
        }
        return document.Entities
            .Take(drawOrderEditEntityCount)
            .Select(static entity => entity.Handle)
            .ToArray();
    });

for (int i = 0; i < warmupCount; i++)
{
    CadDocumentSnapshot warmSnapshot = snapshotCompiler.Compile(session, snapshotOptions);
    _ = pageSetupCompiler.Compile(session);
    _ = sceneCompiler.Compile(warmSnapshot);
    _ = pointMarkerSceneCompiler.Compile(
        warmSnapshot,
        new CadPointMarkerView(1_080.0f, 0.25));
    _ = mesh3DSceneCompiler.Compile(warmSnapshot);
    using CadPrintPlan warmPrintPlan = printPlanCompiler.Compile(warmSnapshot, printOptions);
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
Measurement pointMarkerSceneMeasurement = Measure(
    "point-marker-scene",
    iterationCount,
    () => pointMarkerSceneCompiler.Compile(
        snapshot,
        new CadPointMarkerView(1_080.0f, 0.25)));
CadRecordedPointMarkerScene recordedPointMarkerScene =
    pointMarkerSceneCompiler.Compile(
        snapshot,
        new CadPointMarkerView(1_080.0f, 0.25));
Measurement mesh3DSceneMeasurement = Measure(
    "mesh-3d-scene",
    iterationCount,
    () => mesh3DSceneCompiler.Compile(snapshot));
CadRecordedMesh3DScene recordedMesh3DScene = mesh3DSceneCompiler.Compile(snapshot);
var constructionCompiler = new CadConstructionSceneCompiler();
CadBounds3D overlayClip = constructionClip ?? snapshot.Bounds;
Measurement constructionSceneMeasurement = Measure(
    "construction-scene",
    iterationCount,
    () => constructionCompiler.Compile(snapshot, overlayClip));
CadRecordedConstructionScene recordedConstructionScene =
    constructionCompiler.Compile(snapshot, overlayClip);
Measurement printPlanMeasurement = Measure(
    "print-plan",
    iterationCount,
    () => printPlanCompiler.Compile(snapshot, printOptions));
Measurement rotatedPrintPlanMeasurement = Measure(
    "rotated-print-plan",
    iterationCount,
    () => printPlanCompiler.Compile(snapshot, rotatedPrintOptions));
Measurement queryMeasurement = MeasureQueries(snapshot, queryCount);
Measurement constructionQueryMeasurement = MeasureConstructionQueries(
    snapshot,
    overlayClip,
    queryCount);
Measurement? splinePointSelectionMeasurement = measureSplineSelection
    ? MeasureSplinePointSelections(snapshot, queryCount)
    : null;
Measurement? splineBoundsSelectionMeasurement = measureSplineSelection
    ? MeasureSplineBoundsSelections(snapshot, queryCount)
    : null;
Measurement? textPointSelectionMeasurement = measureTextSelection
    ? MeasureTextPointSelections(snapshot, queryCount)
    : null;
Measurement? textBoundsSelectionMeasurement = measureTextSelection
    ? MeasureTextBoundsSelections(snapshot, queryCount)
    : null;
Measurement? hatchPointSelectionMeasurement = measureHatchSelection
    ? MeasureHatchPointSelections(snapshot, queryCount)
    : null;
Measurement? hatchBoundsSelectionMeasurement = measureHatchSelection
    ? MeasureHatchBoundsSelections(snapshot, queryCount)
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
Measurement? drawOrderEditMeasurement = drawOrderEditHandles.Length == 0
    ? null
    : MeasureDrawOrderEdits(
        session,
        drawOrderEditHandles,
        warmupCount,
        iterationCount);

var report = new CadBenchmarkReport(
    DateTimeOffset.UtcNow,
    Environment.OSVersion.ToString(),
    System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    entityCount,
    resolveDrawOrder,
    drawOrderEditEntityCount,
    blockArrayColumnCount,
    textEntityCount,
    mtextEntityCount,
    shxTextEntityCount,
    shxMTextEntityCount,
    attributeInsertCount,
    dimensionEntityCount,
    thickSolidEntityCount,
    meshEntityCount,
    meshSubdivisionLevel,
    polygonMeshEntityCount,
    polyfaceMeshEntityCount,
    pointEntityCount,
    compoundPointMarkers,
    constructionLineCount,
    solidHatchCount,
    patternHatchCount,
    complexPatternGrammar,
    hatchIslandStyles,
    hatchSplineEdges,
    rationalHatchSplineEdges,
    rationalCubicHatchSplineEdges,
    decorateText,
    decorateShxText,
    lowerLineTypes || lowerComplexLineTypes || lowerLinearSplineLineTypes ||
        lowerNurbsSplineLineTypes || lowerPeriodicSplineLineTypes,
    lowerComplexLineTypes,
    lowerLinearSplineLineTypes,
    lowerNurbsSplineLineTypes,
    lowerPeriodicSplineLineTypes,
    measureSplineSelection,
    measureTextSelection,
    measureHatchSelection,
    shxInterpretationCount,
    shxLayoutCount,
    warmupCount,
    iterationCount,
    queryCount,
    snapshot.Statistics,
    snapshot.SpatialIndex.NodeCount,
    recordedScene.Statistics.RecordedCommandCount,
    recordedScene.Statistics,
    recordedPointMarkerScene.Statistics,
    recordedMesh3DScene.Statistics,
    recordedConstructionScene.Statistics,
    snapshotMeasurement,
    pageSetupMeasurement,
    sceneMeasurement,
    pointMarkerSceneMeasurement,
    mesh3DSceneMeasurement,
    constructionSceneMeasurement,
    printPlanMeasurement,
    rotatedPrintPlanMeasurement,
    queryMeasurement,
    constructionQueryMeasurement,
    splinePointSelectionMeasurement,
    splineBoundsSelectionMeasurement,
    textPointSelectionMeasurement,
    textBoundsSelectionMeasurement,
    hatchPointSelectionMeasurement,
    hatchBoundsSelectionMeasurement,
    shxMeasurement,
    shxLayoutMeasurement,
    drawOrderEditMeasurement,
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
        mtextEntityCount +
        shxTextEntityCount +
        shxMTextEntityCount +
        attributeInsertCount +
        dimensionEntityCount +
        thickSolidEntityCount +
        meshEntityCount +
        polygonMeshEntityCount +
        polyfaceMeshEntityCount +
        pointEntityCount +
        constructionLineCount +
        solidHatchCount +
        patternHatchCount);
    int expectedExpanded = checked(
        entityCount +
        (blockArrayColumnCount == 0 ? 0 : blockArrayColumnCount + 1) +
        textEntityCount +
        mtextEntityCount +
        shxTextEntityCount +
        shxMTextEntityCount +
        (attributeInsertCount * 2) +
        (dimensionEntityCount * 6) +
        thickSolidEntityCount +
        (meshEntityCount * checked(1 + (6 * Pow4(meshSubdivisionLevel)))) +
        (polygonMeshEntityCount * 13) +
        (polyfaceMeshEntityCount * 7) +
        pointEntityCount +
        constructionLineCount +
        solidHatchCount +
        patternHatchCount);
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

int Pow4(int exponent)
{
    int result = 1;
    for (int i = 0; i < exponent; i++) result = checked(result * 4);
    return result;
}

CadDocumentSession CreateDocument(
    int count,
    int arrayColumns,
    int textCount,
    int mtextCount,
    int shxTextCount,
    int shxMTextCount,
    int attributeCount,
    int dimensionCount,
    int thickSolidCount,
    int meshCount,
    int meshSubdivision,
    int polygonMeshCount,
    int polyfaceMeshCount,
    int pointCount,
    bool useCompoundPointMarkers,
    int constructionCount,
    int hatchCount,
    int patternedHatchCount,
    bool useComplexPatternGrammar,
    bool useHatchIslandStyles,
    bool useHatchSplineEdges,
    bool useRationalHatchSplineEdges,
    bool useRationalCubicHatchSplineEdges,
    bool decorateTextRuns,
    bool decorateShxTextRuns,
    bool useLineTypes,
    bool useComplexLineTypes,
    bool useLinearSplineLineTypes,
    bool useNurbsSplineLineTypes,
    bool usePeriodicSplineLineTypes,
    bool useSplineSelection,
    bool useDrawOrder)
{
    CadDocumentSession result = CadDocumentSession.CreateNew();
    result.Edit("Build benchmark document", document =>
    {
        if (useCompoundPointMarkers)
        {
            document.Header.PointDisplayMode = 98;
            document.Header.PointDisplaySize = -5.0;
        }
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

        if (mtextCount > 0)
        {
            var textStyle = new TextStyle("INTER_MTEXT") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            for (int i = 0; i < mtextCount; i++)
            {
                document.Entities.Add(new MText
                {
                    Style = textStyle,
                    Value = @"{\C1;\LProGPU\l} CAD\PUnicode مرحبا \S1/2; 0123456789",
                    InsertPoint = new XYZ((i % 100) * 90.0, (i / 100) * 18.0, 0),
                    Height = 2.5,
                    RectangleWidth = 80.0,
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

        if (shxMTextCount > 0)
        {
            var textStyle = new TextStyle("BENCHMARK_SHX_MTEXT")
            {
                Filename = "benchmark.shx",
            };
            document.TextStyles.Add(textStyle);
            for (int i = 0; i < shxMTextCount; i++)
            {
                var text = new MText
                {
                    Style = textStyle,
                    Value = @"{\C1;\LAAAA\l}\PAAAA\SAA/AA;",
                    InsertPoint = new XYZ((i % 100) * 46.0, (i / 100) * 10.0, 0),
                    Height = 2.5,
                };
                text.ColumnData.ColumnType = ColumnType.DynamicColumns;
                text.ColumnData.ColumnCount = 2;
                text.ColumnData.Width = 20.0;
                text.ColumnData.Gutter = 2.0;
                text.ColumnData.AutoHeight = true;
                document.Entities.Add(text);
            }
        }

        if (attributeCount > 0)
        {
            var textStyle = new TextStyle("INTER_ATTRIBUTE") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            var block = new BlockRecord("BENCHMARK_ATTRIBUTE");
            block.Entities.Add(new AttributeDefinition
            {
                Tag = "PART_NUMBER",
                Value = "DEFAULT",
                Style = textStyle,
                Height = 2.5,
            });
            var inserts = new Insert[attributeCount];
            for (int i = 0; i < attributeCount; i++)
            {
                double x = (i % 100) * 90.0;
                double y = (i / 100) * 8.0;
                var insert = new Insert(block)
                {
                    InsertPoint = new XYZ(x, y, 0),
                };
                AttributeEntity attribute = insert.Attributes.Single();
                attribute.Value = $"ProGPU CAD {i:D10}";
                attribute.InsertPoint = new XYZ(x, y, 0);
                inserts[i] = insert;
            }
            foreach (Insert insert in inserts)
            {
                document.Entities.Add(insert);
            }
        }

        if (dimensionCount > 0)
        {
            var textStyle = new TextStyle("INTER_DIMENSION") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            for (int i = 0; i < dimensionCount; i++)
            {
                double x = (i % 100) * 40.0;
                double y = (i / 100) * 12.0;
                var picture = new BlockRecord($"BENCHMARK_DIMENSION_{i}")
                {
                    IsAnonymous = true,
                };
                picture.Entities.Add(new Line(
                    new XYZ(x, y, 0),
                    new XYZ(x, y + 8, 0)));
                picture.Entities.Add(new Line(
                    new XYZ(x + 30, y, 0),
                    new XYZ(x + 30, y + 8, 0)));
                picture.Entities.Add(new Solid(
                    new XYZ(x, y + 7, 0),
                    new XYZ(x + 2, y + 6, 0),
                    new XYZ(x + 2, y + 8, 0)));
                picture.Entities.Add(new Solid(
                    new XYZ(x + 30, y + 7, 0),
                    new XYZ(x + 28, y + 6, 0),
                    new XYZ(x + 28, y + 8, 0)));
                picture.Entities.Add(new MText($"{i + 1}.00")
                {
                    Style = textStyle,
                    InsertPoint = new XYZ(x + 15, y + 9, 0),
                    Height = 2.5,
                });
                document.Entities.Add(new DimensionLinear
                {
                    Block = picture,
                    DefinitionPoint = new XYZ(x + 30, y + 7, 0),
                });
            }
        }

        for (int i = 0; i < thickSolidCount; i++)
        {
            double x = (i % 100) * 12.0;
            double y = (i / 100) * 12.0;
            bool crossed = (i & 1) != 0;
            document.Entities.Add(new Solid(
                new XYZ(x, y, 0),
                new XYZ(x + 8, y + (crossed ? 8 : 0), 0),
                crossed ? new XYZ(x + 8, y, 0) : new XYZ(x, y + 8, 0),
                crossed ? new XYZ(x, y + 8, 0) : new XYZ(x + 8, y + 8, 0))
            {
                Thickness = (i & 2) == 0 ? 4.0 : -4.0,
            });
        }

        for (int i = 0; i < meshCount; i++)
        {
            double x = (i % 100) * 12.0;
            double y = (i / 100) * 12.0;
            var mesh = new Mesh { SubdivisionLevel = meshSubdivision };
            mesh.Vertices.Add(new XYZ(x, y, 0));
            mesh.Vertices.Add(new XYZ(x + 8, y, 0));
            mesh.Vertices.Add(new XYZ(x + 4, y + 8, 0));
            mesh.Vertices.Add(new XYZ(x + 4, y + 3, 8));
            mesh.Faces.Add([0, 1, 2]);
            mesh.Faces.Add([0, 3, 1]);
            mesh.Faces.Add([1, 3, 2]);
            mesh.Faces.Add([2, 3, 0]);
            document.Entities.Add(mesh);
        }

        for (int i = 0; i < polygonMeshCount; i++)
        {
            double x = (i % 100) * 12.0;
            double y = (i / 100) * 12.0;
            var mesh = new PolygonMesh
            {
                MVertexCount = 3,
                NVertexCount = 3,
            };
            for (int m = 0; m < mesh.MVertexCount; m++)
            {
                for (int n = 0; n < mesh.NVertexCount; n++)
                {
                    mesh.Vertices.Add(new PolygonMeshVertex(
                        new XYZ(x + (m * 4), y + (n * 4), m + n)));
                }
            }
            document.Entities.Add(mesh);
        }

        for (int i = 0; i < polyfaceMeshCount; i++)
        {
            double x = (i % 100) * 12.0;
            double y = (i / 100) * 12.0;
            var mesh = new PolyfaceMesh();
            mesh.Vertices.Add(new VertexFaceMesh(new XYZ(x, y, 0)));
            mesh.Vertices.Add(new VertexFaceMesh(new XYZ(x + 8, y, 0)));
            mesh.Vertices.Add(new VertexFaceMesh(new XYZ(x + 4, y + 8, 0)));
            mesh.Vertices.Add(new VertexFaceMesh(new XYZ(x + 4, y + 3, 8)));
            mesh.Faces.Add(new VertexFaceRecord { Index1 = 1, Index2 = 2, Index3 = 3 });
            mesh.Faces.Add(new VertexFaceRecord { Index1 = 1, Index2 = 4, Index3 = 2 });
            mesh.Faces.Add(new VertexFaceRecord { Index1 = 2, Index2 = 4, Index3 = 3 });
            mesh.Faces.Add(new VertexFaceRecord { Index1 = 3, Index2 = 4, Index3 = 1 });
            document.Entities.Add(mesh);
        }

        for (int i = 0; i < pointCount; i++)
        {
            document.Entities.Add(new Point(new XYZ(
                (i % 1_000) * 12.0,
                (i / 1_000) * 12.0,
                i % 17))
            {
                Rotation = useCompoundPointMarkers
                    ? (i % 360) * Math.PI / 180.0
                    : 0.0,
            });
        }

        for (int i = 0; i < constructionCount; i++)
        {
            double x = (i % 1_000) * 12.0;
            double y = (i / 1_000) * 12.0;
            Entity entity = (i & 1) == 0
                ? new Ray
                {
                    StartPoint = new XYZ(x, y, i % 17),
                    Direction = new XYZ(1, 0.25, 0.05),
                }
                : new XLine
                {
                    FirstPoint = new XYZ(x, y, i % 17),
                    Direction = new XYZ(-0.25, 1, -0.05),
                };
            document.Entities.Add(entity);
        }

        for (int i = 0; i < hatchCount; i++)
        {
            double x = (i % 100) * 24.0;
            double y = (i / 100) * 24.0;
            var hatch = new Hatch
            {
                IsSolid = true,
                Pattern = HatchPattern.Solid,
                PatternType = HatchPatternType.SolidFill,
                Style = useHatchIslandStyles
                    ? (i & 1) == 0
                        ? HatchStyleType.Outer
                        : HatchStyleType.Ignore
                    : HatchStyleType.Normal,
            };
            hatch.Paths.Add(useHatchSplineEdges
                ? CreateHatchSplineCapLoop(
                    x,
                    y,
                    rationalQuadratic: useRationalHatchSplineEdges,
                    rationalCubic: useRationalCubicHatchSplineEdges)
                : CreateHatchLoop(
                    (x, y),
                    (x + 20, y),
                    (x + 20, y + 20),
                    (x, y + 20)));
            hatch.Paths.Add(CreateHatchLoop(
                (x + 7, y + 7),
                (x + 13, y + 7),
                (x + 13, y + 13),
                (x + 7, y + 13)));
            if (useHatchIslandStyles)
            {
                hatch.Paths.Add(CreateHatchLoop(
                    (x + 9, y + 9),
                    (x + 11, y + 9),
                    (x + 11, y + 11),
                    (x + 9, y + 11)));
            }
            document.Entities.Add(hatch);
        }

        for (int i = 0; i < patternedHatchCount; i++)
        {
            double x = (i % 100) * 24.0;
            double y = (i / 100) * 24.0;
            var pattern = new HatchPattern("BENCHMARK_USER");
            var hatch = new Hatch
            {
                IsSolid = false,
                Pattern = pattern,
                PatternType = HatchPatternType.PatternFill,
                Style = useHatchIslandStyles
                    ? (i & 1) == 0
                        ? HatchStyleType.Outer
                        : HatchStyleType.Ignore
                    : HatchStyleType.Normal,
            };
            pattern.Lines.Add(new HatchPattern.Line
            {
                Angle = 0.0,
                BasePoint = new XY(x, y + 2.0),
                Offset = new XY(3.0, 4.0),
            });
            if (useComplexPatternGrammar)
            {
                pattern.Lines[0].DashLengths.AddRange([4.0, -2.0, 0.0, -2.0]);
                pattern.Lines.Add(new HatchPattern.Line
                {
                    Angle = Math.PI / 2.0,
                    BasePoint = new XY(x + 10.0, y),
                    Offset = new XY(-6.0, 2.0),
                    DashLengths = { 2.0, -1.0 },
                });
            }
            hatch.Paths.Add(useHatchSplineEdges
                ? CreateHatchSplineCapLoop(
                    x,
                    y,
                    rationalQuadratic: useRationalHatchSplineEdges,
                    rationalCubic: useRationalCubicHatchSplineEdges)
                : CreateHatchLoop(
                    (x, y),
                    (x + 20, y),
                    (x + 20, y + 20),
                    (x, y + 20)));
            hatch.Paths.Add(CreateHatchLoop(
                (x + 7, y + 7),
                (x + 13, y + 7),
                (x + 13, y + 13),
                (x + 7, y + 13)));
            if (useHatchIslandStyles)
            {
                hatch.Paths.Add(CreateHatchLoop(
                    (x + 9, y + 9),
                    (x + 11, y + 9),
                    (x + 11, y + 11),
                    (x + 9, y + 11)));
            }
            document.Entities.Add(hatch);
        }

        if (useDrawOrder)
        {
            document.Header.EntitySortingFlags = ObjectSortingFlags.All;
            Entity[] authored = document.Entities.ToArray();
            SortEntitiesTable order = document.ModelSpace.CreateSortEntitiesTable();
            for (int i = 0; i < authored.Length; i++)
            {
                order.Add(authored[i], checked((ulong)(authored.Length - i)));
            }
        }
    });
    return result;
}

Hatch.BoundaryPath CreateHatchLoop(params (double X, double Y)[] vertices)
{
    var polyline = new Hatch.BoundaryPath.Polyline { IsClosed = true };
    foreach ((double x, double y) in vertices)
    {
        polyline.Vertices.Add(new XYZ(x, y, 0));
    }
    var path = new Hatch.BoundaryPath();
    path.Edges.Add(polyline);
    return path;
}

Hatch.BoundaryPath CreateHatchSplineCapLoop(
    double x,
    double y,
    bool rationalQuadratic,
    bool rationalCubic)
{
    var spline = new Hatch.BoundaryPath.Spline
    {
        Degree = rationalQuadratic ? 2 : 3,
        IsRational = rationalQuadratic || rationalCubic,
    };
    if (rationalQuadratic)
    {
        spline.ControlPoints.AddRange([
            new XYZ(x, y, 1.0),
            new XYZ(x + 10, y + 20, 0.5),
            new XYZ(x + 20, y, 1.0),
        ]);
        spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
    }
    else if (rationalCubic)
    {
        spline.ControlPoints.AddRange([
            new XYZ(x, y, 8.0),
            new XYZ(x, y + 20, 2.0),
            new XYZ(x + 20, y + 20, 3.0),
            new XYZ(x + 20, y, 1.0),
        ]);
        spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
    }
    else
    {
        spline.ControlPoints.AddRange([
            new XYZ(x, y, 0),
            new XYZ(x, y + 20, 0),
            new XYZ(x + 20, y + 20, 0),
            new XYZ(x + 20, y, 0),
        ]);
        spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
    }
    var path = new Hatch.BoundaryPath();
    path.Edges.Add(spline);
    path.Edges.Add(new Hatch.BoundaryPath.Line
    {
        Start = new XY(x + 20, y),
        End = new XY(x, y),
    });
    return path;
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

Measurement MeasureDrawOrderEdits(
    CadDocumentSession source,
    ulong[] handles,
    int warmups,
    int count)
{
    var history = new CadDocumentHistory(source);
    for (int i = 0; i < warmups; i++)
    {
        history.Execute(new CadSetModelSpaceDrawOrderCommand(
            handles,
            CadDrawOrderPlacement.BringToFront,
            maximumSelectionCount: handles.Length));
        if (!history.TryUndo(out _))
        {
            throw new InvalidOperationException(
                "Draw-order benchmark warmup could not restore its source order.");
        }
    }

    var elapsed = new double[count];
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    ulong checksum = 0;
    for (int i = 0; i < count; i++)
    {
        long started = Stopwatch.GetTimestamp();
        checksum ^= history.Execute(new CadSetModelSpaceDrawOrderCommand(
            handles,
            CadDrawOrderPlacement.BringToFront,
            maximumSelectionCount: handles.Length));
        if (!history.TryUndo(out ulong undoGeneration))
        {
            throw new InvalidOperationException(
                "Draw-order benchmark iteration could not restore its source order.");
        }
        checksum ^= undoGeneration;
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize(
        "draw-order-edit-and-undo",
        elapsed,
        allocated / count);
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

Measurement MeasureConstructionQueries(
    CadDocumentSnapshot source,
    CadBounds3D bounds,
    int count)
{
    var elapsed = new double[count];
    int capacity = Math.Min(source.Entities.Length, 512);
    var entityIndices = new int[capacity];
    var candidates = new CadSelectionCandidate[capacity];
    _ = CadSelectionQuery.QueryBounds(source, bounds, entityIndices, candidates);
    _ = GC.GetAllocatedBytesForCurrentThread();
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    int checksum = 0;
    double width = bounds.Max.X - bounds.Min.X;
    double height = bounds.Max.Y - bounds.Min.Y;
    for (int i = 0; i < count; i++)
    {
        double phase = (i % 997) / 997.0;
        double x = bounds.Min.X + (width * phase);
        double y = bounds.Min.Y + (height * (1.0 - phase));
        var query = new CadBounds3D(
            new CadPoint3D(x, y, bounds.Min.Z),
            new CadPoint3D(x + 120, y + 120, bounds.Max.Z));
        long started = Stopwatch.GetTimestamp();
        checksum += CadSelectionQuery.QueryBounds(
            source,
            query,
            entityIndices,
            candidates).TotalCount;
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize("construction-query-ns", elapsed, allocated / count);
}

Measurement MeasureSplinePointSelections(CadDocumentSnapshot source, int count)
{
    CadSelectionCandidate[] candidates = CreateSelectionCandidates(
        source,
        CadEntityKind.Spline,
        "spline");
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
    CadSelectionCandidate[] candidates = CreateSelectionCandidates(
        source,
        CadEntityKind.Spline,
        "spline");
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

Measurement MeasureTextPointSelections(CadDocumentSnapshot source, int count)
{
    CadSelectionCandidate[] candidates = CreateTextSelectionCandidates(source);
    var elapsed = new double[count];
    for (int i = 0; i < candidates.Length; i++)
    {
        _ = CadSelectionHitTester.HitTestPoint(
            source, candidates[i], candidates[i].Bounds.Center, tolerance: 0.5);
    }
    _ = GC.GetAllocatedBytesForCurrentThread();
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        CadSelectionCandidate candidate = candidates[i % candidates.Length];
        CadPoint3D center = candidate.Bounds.Center;
        var point = new CadPoint3D(
            center.X + ((i & 1) == 0 ? 0.125 : -0.125),
            center.Y,
            center.Z);
        long started = Stopwatch.GetTimestamp();
        CadPointHitResult result = CadSelectionHitTester.HitTestPoint(
            source, candidate, point, tolerance: 0.5);
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        checksum += (int)result.Status;
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize("text-point-selection-ns", elapsed, allocated / count);
}

Measurement MeasureTextBoundsSelections(CadDocumentSnapshot source, int count)
{
    CadSelectionCandidate[] candidates = CreateTextSelectionCandidates(source);
    var elapsed = new double[count];
    for (int i = 0; i < candidates.Length; i++)
    {
        _ = CadSelectionHitTester.HitTestBounds(
            source,
            candidates[i],
            CreateSelectionBounds(candidates[i].Bounds.Center),
            CadBoundsSelectionMode.Crossing);
    }
    _ = GC.GetAllocatedBytesForCurrentThread();
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        CadSelectionCandidate candidate = candidates[i % candidates.Length];
        CadBounds3D bounds = (i & 1) == 0
            ? CreateSelectionBounds(candidate.Bounds.Center)
            : candidate.Bounds;
        CadBoundsSelectionMode mode = (i & 1) == 0
            ? CadBoundsSelectionMode.Crossing
            : CadBoundsSelectionMode.Window;
        long started = Stopwatch.GetTimestamp();
        CadBoundsHitResult result = CadSelectionHitTester.HitTestBounds(
            source, candidate, bounds, mode);
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        checksum += (int)result.Status;
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize("text-bounds-selection-ns", elapsed, allocated / count);
}

Measurement MeasureHatchPointSelections(CadDocumentSnapshot source, int count)
{
    CadSelectionCandidate[] candidates = CreateSelectionCandidates(
        source,
        CadEntityKind.Hatch,
        "HATCH");
    var elapsed = new double[count];
    CadSelectionCandidate warmCandidate = candidates[0];
    CadPoint3D warmPoint = warmCandidate.Bounds.Min + new CadPoint3D(2, 2, 0);
    _ = CadSelectionHitTester.HitTestPoint(source, warmCandidate, warmPoint, tolerance: 0.25);
    _ = GC.GetAllocatedBytesForCurrentThread();
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    int checksum = 0;
    for (int i = 0; i < count; i++)
    {
        CadSelectionCandidate candidate = candidates[i % candidates.Length];
        CadPoint3D point = candidate.Bounds.Min + new CadPoint3D(2, 2, 0);
        long started = Stopwatch.GetTimestamp();
        CadPointHitResult result = CadSelectionHitTester.HitTestPoint(
            source,
            candidate,
            point,
            tolerance: 0.25);
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        checksum += (int)result.Status;
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    GC.KeepAlive(checksum);
    return Summarize("hatch-point-selection-ns", elapsed, allocated / count);
}

Measurement MeasureHatchBoundsSelections(CadDocumentSnapshot source, int count)
{
    CadSelectionCandidate[] candidates = CreateSelectionCandidates(
        source,
        CadEntityKind.Hatch,
        "HATCH");
    var elapsed = new double[count];
    CadSelectionCandidate warmCandidate = candidates[0];
    CadPoint3D warmCenter = warmCandidate.Bounds.Min + new CadPoint3D(2, 2, 0);
    CadBounds3D warmBounds = CreateSelectionBounds(warmCenter);
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
        CadPoint3D center = candidate.Bounds.Min + new CadPoint3D(2, 2, 0);
        CadBounds3D bounds = CreateSelectionBounds(center);
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
    return Summarize("hatch-bounds-selection-ns", elapsed, allocated / count);
}

CadSelectionCandidate[] CreateSelectionCandidates(
    CadDocumentSnapshot source,
    CadEntityKind expectedKind,
    string fixtureName)
{
    ReadOnlySpan<CadEntityHeader> entities = source.Entities.Span;
    var candidates = new CadSelectionCandidate[entities.Length];
    for (int i = 0; i < entities.Length; i++)
    {
        CadEntityHeader entity = entities[i];
        if (entity.Kind != expectedKind)
        {
            throw new InvalidOperationException(
                $"The {fixtureName}-selection benchmark requires a homogeneous fixture.");
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

CadSelectionCandidate[] CreateTextSelectionCandidates(CadDocumentSnapshot source)
{
    ReadOnlySpan<CadEntityHeader> entities = source.Entities.Span;
    var candidates = new CadSelectionCandidate[entities.Length];
    for (int i = 0; i < entities.Length; i++)
    {
        CadEntityHeader entity = entities[i];
        if (entity.Kind is not (
                CadEntityKind.Text or
                CadEntityKind.ShxText or
                CadEntityKind.MText or
                CadEntityKind.ShxMText))
        {
            throw new InvalidOperationException(
                "The text-selection benchmark requires an all-TEXT/MTEXT fixture.");
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
    bool ResolvedDrawOrder,
    int DrawOrderEditEntityCount,
    int BlockArrayColumnCount,
    int TextEntityCount,
    int MTextEntityCount,
    int ShxTextEntityCount,
    int ShxMTextEntityCount,
    int AttributeInsertCount,
    int DimensionEntityCount,
    int ThickSolidEntityCount,
    int MeshEntityCount,
    int MeshSubdivisionLevel,
    int PolygonMeshEntityCount,
    int PolyfaceMeshEntityCount,
    int PointEntityCount,
    bool CompoundPointMarkers,
    int ConstructionLineCount,
    int SolidHatchCount,
    int PatternHatchCount,
    bool ComplexPatternGrammar,
    bool HatchIslandStyles,
    bool HatchSplineEdges,
    bool RationalHatchSplineEdges,
    bool RationalCubicHatchSplineEdges,
    bool DecoratedText,
    bool DecoratedShxText,
    bool LoweredLineTypes,
    bool LoweredComplexLineTypes,
    bool LoweredLinearSplineLineTypes,
    bool LoweredNurbsSplineLineTypes,
    bool LoweredPeriodicSplineLineTypes,
    bool MeasuredSplineSelection,
    bool MeasuredTextSelection,
    bool MeasuredHatchSelection,
    int ShxInterpretationCount,
    int ShxLayoutCount,
    int WarmupCount,
    int IterationCount,
    int QueryCount,
    CadSnapshotStatistics Statistics,
    int SpatialNodeCount,
    int RecordedCommandCount,
    CadPlanSceneStatistics SceneStatistics,
    CadPointMarkerSceneStatistics PointMarkerSceneStatistics,
    CadMesh3DSceneStatistics Mesh3DSceneStatistics,
    CadConstructionSceneStatistics ConstructionSceneStatistics,
    Measurement SnapshotMilliseconds,
    Measurement PageSetupCatalogMilliseconds,
    Measurement PlanSceneMilliseconds,
    Measurement PointMarkerSceneMilliseconds,
    Measurement Mesh3DSceneMilliseconds,
    Measurement ConstructionSceneMilliseconds,
    Measurement PrintPlanMilliseconds,
    Measurement RotatedPrintPlanMilliseconds,
    Measurement SpatialQueryNanoseconds,
    Measurement ConstructionQueryNanoseconds,
    Measurement? SplinePointSelectionNanoseconds,
    Measurement? SplineBoundsSelectionNanoseconds,
    Measurement? TextPointSelectionNanoseconds,
    Measurement? TextBoundsSelectionNanoseconds,
    Measurement? HatchPointSelectionNanoseconds,
    Measurement? HatchBoundsSelectionNanoseconds,
    Measurement? ShxInterpretBatchMilliseconds,
    Measurement? ShxLayoutBatchMilliseconds,
    Measurement? DrawOrderEditAndUndoMilliseconds,
    long WorkingSetBytes);

internal sealed class BenchmarkTextFontResolver(TtfFont font) : ICadTextFontResolver
{
    public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
        new(font, IsSubstitution: false);
}
