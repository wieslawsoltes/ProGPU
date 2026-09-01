using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Backend;
using ProGPU.CAD;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Text;
using ProGPU.Tests.Headless;

int mesh3DSmoothingGridSize = ReadNonNegativeInt(
    "--mesh3d-smoothing-grid",
    0);
if (mesh3DSmoothingGridSize != 0)
{
    RunMesh3DSmoothingBenchmark(
        mesh3DSmoothingGridSize,
        ReadPositiveInt("--mesh3d-smoothing-faces", 512),
        ReadNonNegativeInt("--warmup", 3),
        ReadPositiveInt("--iterations", 12),
        ReadString("--output-json"));
    return;
}

int mesh3DSubobjectEditGridSize = ReadNonNegativeInt(
    "--mesh3d-subobject-edit-grid",
    0);
if (mesh3DSubobjectEditGridSize != 0)
{
    RunMesh3DSubobjectEditBenchmark(
        mesh3DSubobjectEditGridSize,
        ReadPositiveInt("--mesh3d-subobject-edit-faces", 1_024),
        ReadNonNegativeInt("--warmup", 3),
        ReadPositiveInt("--iterations", 24),
        ReadString("--output-json"));
    return;
}

int mesh3DSelectionGridSize = ReadNonNegativeInt(
    "--mesh3d-selection-grid",
    0);
if (mesh3DSelectionGridSize != 0)
{
    RunMesh3DSelectionBenchmark(
        mesh3DSelectionGridSize,
        ReadPositiveInt("--mesh3d-selection-depth-layers", 1),
        ReadNonNegativeInt("--warmup", 3),
        ReadPositiveInt("--iterations", 12),
        ReadPositiveInt("--queries", 65_536),
        ReadString("--output-json"));
    return;
}

void RunMesh3DSmoothingBenchmark(
    int gridSize,
    int selectedFaceCount,
    int warmups,
    int iterations,
    string? reportPath)
{
    long totalFaceCount = checked((long)gridSize * gridSize);
    if (totalFaceCount > int.MaxValue)
    {
        throw new ArgumentOutOfRangeException(nameof(gridSize));
    }
    selectedFaceCount = Math.Min(
        selectedFaceCount,
        checked((int)totalFaceCount));
    if (selectedFaceCount >
        CadSetMeshSubobjectCreaseCommand.DefaultMaxSubobjects)
    {
        throw new ArgumentOutOfRangeException(
            nameof(selectedFaceCount),
            $"Selected faces cannot exceed {CadSetMeshSubobjectCreaseCommand.DefaultMaxSubobjects}.");
    }

    (
        Measurement EditAndRebuild,
        Measurement UndoRedo,
        ulong FinalContentGeneration,
        int ControlVertexCount,
        int AuthoredFaceCount) MeasureLane(bool crease)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        Mesh mesh = CreateMesh3DSubobjectEditGrid(gridSize);
        mesh.SubdivisionLevel = crease ? 1 : 0;
        int controlVertexCount = mesh.Vertices.Count;
        int authoredFaceCount = mesh.Faces.Count;
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session, capacity: 4);

        CadEditCommand CreateCommand(CadRecordedMesh3DScene scene)
        {
            if (!crease)
            {
                return new CadAdjustMeshSubdivisionLevelCommand(
                    [mesh.Handle],
                    delta: 1);
            }

            CadMesh3DSubobjectComponent component =
                scene.SubobjectComponents.Span[0];
            var ids = new CadMesh3DSubobjectId[selectedFaceCount];
            int stride = Math.Max(1, authoredFaceCount / selectedFaceCount);
            for (int index = 0; index < ids.Length; index++)
            {
                ids[index] = new CadMesh3DSubobjectId(
                    scene.ContentGeneration,
                    component.Handle,
                    component.ComponentIndex,
                    CadMesh3DSubobjectKind.Face,
                    Math.Min(authoredFaceCount - 1, index * stride));
            }
            return new CadSetMeshSubobjectCreaseCommand(scene, ids, -1.0);
        }

        object ApplyAndRebuild()
        {
            CadDocumentSnapshot snapshot =
                new CadSnapshotCompiler().Compile(session);
            CadRecordedMesh3DScene scene =
                new CadMesh3DSceneCompiler().Compile(snapshot);
            CadEditCommand command = CreateCommand(scene);
            ulong generation = history.Execute(command);
            CadDocumentSnapshot rebuilt =
                new CadSnapshotCompiler().Compile(session);
            CadRecordedMesh3DScene rebuiltScene =
                new CadMesh3DSceneCompiler().Compile(rebuilt);
            return HashCode.Combine(
                generation,
                rebuiltScene.Statistics.TriangleCount,
                rebuiltScene.SubobjectComponents.Span[0].Edges.Length);
        }

        for (int index = 0; index < warmups; index++)
        {
            _ = ApplyAndRebuild();
            if (!history.TryUndo(out _))
            {
                throw new InvalidOperationException(
                    "Mesh smoothing/crease benchmark warmup undo failed.");
            }
        }

        var editElapsed = new double[iterations];
        long editAllocated = 0;
        int checksum = 0;
        for (int index = 0; index < iterations; index++)
        {
            long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            object value = ApplyAndRebuild();
            editElapsed[index] =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            editAllocated +=
                GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
            checksum ^= value.GetHashCode();
            if (!history.TryUndo(out _))
            {
                throw new InvalidOperationException(
                    "Mesh smoothing/crease benchmark iteration undo failed.");
            }
        }
        string lane = crease ? "crease" : "smooth-more";
        Measurement editAndRebuild = Summarize(
            $"mesh-{lane}-snapshot-scene-ms",
            editElapsed,
            editAllocated / iterations);

        _ = ApplyAndRebuild();
        for (int index = 0; index < warmups; index++)
        {
            if (!history.TryUndo(out _) || !history.TryRedo(out _))
            {
                throw new InvalidOperationException(
                    "Mesh smoothing/crease undo/redo benchmark warmup failed.");
            }
        }
        var undoRedoElapsed = new double[iterations];
        long undoRedoAllocatedStart =
            GC.GetAllocatedBytesForCurrentThread();
        ulong undoRedoChecksum = 0;
        for (int index = 0; index < iterations; index++)
        {
            long started = Stopwatch.GetTimestamp();
            if (!history.TryUndo(out ulong undoGeneration) ||
                !history.TryRedo(out ulong redoGeneration))
            {
                throw new InvalidOperationException(
                    "Mesh smoothing/crease undo/redo benchmark iteration failed.");
            }
            undoRedoElapsed[index] =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            undoRedoChecksum ^= undoGeneration ^ redoGeneration;
        }
        long undoRedoAllocated =
            GC.GetAllocatedBytesForCurrentThread() - undoRedoAllocatedStart;
        GC.KeepAlive(checksum ^ undoRedoChecksum.GetHashCode());
        Measurement undoRedo = Summarize(
            $"mesh-{lane}-undo-redo-ms",
            undoRedoElapsed,
            undoRedoAllocated / iterations);
        return (
            editAndRebuild,
            undoRedo,
            session.ContentGeneration,
            controlVertexCount,
            authoredFaceCount);
    }

    var smoothing = MeasureLane(crease: false);
    var crease = MeasureLane(crease: true);
    var report = new CadMesh3DSmoothingBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        RuntimeInformation.FrameworkDescription,
        gridSize,
        smoothing.ControlVertexCount,
        smoothing.AuthoredFaceCount,
        selectedFaceCount,
        warmups,
        iterations,
        smoothing.EditAndRebuild,
        crease.EditAndRebuild,
        smoothing.UndoRedo,
        crease.UndoRedo,
        smoothing.FinalContentGeneration,
        crease.FinalContentGeneration,
        CaptureMesh3DReplayBinaryHashes());
    string json = JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, json);
    }
}

void RunMesh3DSubobjectEditBenchmark(
    int gridSize,
    int selectedFaceCount,
    int warmups,
    int iterations,
    string? reportPath)
{
    long totalFaceCount = checked((long)gridSize * gridSize);
    if (totalFaceCount > int.MaxValue)
    {
        throw new ArgumentOutOfRangeException(nameof(gridSize));
    }
    selectedFaceCount = Math.Min(selectedFaceCount, checked((int)totalFaceCount));
    if (selectedFaceCount > CadTranslateMeshSubobjectsCommand.DefaultMaxSubobjects)
    {
        throw new ArgumentOutOfRangeException(
            nameof(selectedFaceCount),
            $"Selected faces cannot exceed {CadTranslateMeshSubobjectsCommand.DefaultMaxSubobjects}.");
    }

    (
        Measurement EditAndRebuild,
        Measurement UndoRedo,
        ulong FinalContentGeneration,
        int ControlVertexCount,
        int AuthoredFaceCount) MeasureLane(
            CadMesh3DSubobjectTransform transform)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        Mesh mesh = CreateMesh3DSubobjectEditGrid(gridSize);
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(
            session,
            capacity: checked(warmups + iterations + 4));
        int operation = 0;

        object EditAndRebuild()
        {
            CadDocumentSnapshot snapshot =
                new CadSnapshotCompiler().Compile(session);
            CadRecordedMesh3DScene scene =
                new CadMesh3DSceneCompiler().Compile(snapshot);
            CadMesh3DSubobjectComponent component =
                scene.SubobjectComponents.Span[0];
            var ids = new CadMesh3DSubobjectId[selectedFaceCount];
            int stride = Math.Max(
                1,
                checked((int)totalFaceCount) / selectedFaceCount);
            for (int index = 0; index < ids.Length; index++)
            {
                ids[index] = new CadMesh3DSubobjectId(
                    scene.ContentGeneration,
                    component.Handle,
                    component.ComponentIndex,
                    CadMesh3DSubobjectKind.Face,
                    Math.Min(
                        checked((int)totalFaceCount) - 1,
                        index * stride));
            }
            int currentOperation = operation++;
            CadEditCommand command = transform switch
            {
                CadMesh3DSubobjectTransform.Translate =>
                    new CadTranslateMeshSubobjectsCommand(
                        scene,
                        ids,
                        new CadPoint3D(
                            0,
                            0,
                            (currentOperation & 1) == 0 ? 0.125 : -0.125)),
                CadMesh3DSubobjectTransform.Rotate =>
                    new CadRotateMeshSubobjectsCommand(
                        scene,
                        ids,
                        new CadPoint3D(1, 2, 3),
                        (currentOperation & 1) == 0 ? 0.001 : -0.001),
                CadMesh3DSubobjectTransform.Scale =>
                    new CadScaleMeshSubobjectsCommand(
                        scene,
                        ids,
                        (currentOperation & 1) == 0
                            ? 1.0001
                            : 1.0 / 1.0001),
                _ => throw new ArgumentOutOfRangeException(nameof(transform)),
            };
            ulong generation = history.Execute(command);
            CadDocumentSnapshot rebuilt =
                new CadSnapshotCompiler().Compile(session);
            CadRecordedMesh3DScene rebuiltScene =
                new CadMesh3DSceneCompiler().Compile(rebuilt);
            return HashCode.Combine(
                generation,
                rebuiltScene.Statistics.TriangleCount,
                rebuiltScene.SubobjectComponents.Span[0].VertexPositions.Length);
        }

        for (int index = 0; index < warmups; index++)
        {
            _ = EditAndRebuild();
        }
        Measurement editAndRebuild = Measure(
            $"mesh-subobject-{transform.ToString().ToLowerInvariant()}-snapshot-scene-ms",
            iterations,
            EditAndRebuild);

        for (int index = 0; index < warmups; index++)
        {
            if (!history.TryUndo(out _) || !history.TryRedo(out _))
            {
                throw new InvalidOperationException(
                    "Mesh-subobject undo/redo benchmark warmup failed.");
            }
        }
        var undoRedoElapsed = new double[iterations];
        _ = GC.GetAllocatedBytesForCurrentThread();
        long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
        ulong checksum = 0;
        for (int index = 0; index < iterations; index++)
        {
            long started = Stopwatch.GetTimestamp();
            if (!history.TryUndo(out ulong undoGeneration) ||
                !history.TryRedo(out ulong redoGeneration))
            {
                throw new InvalidOperationException(
                    "Mesh-subobject undo/redo benchmark iteration failed.");
            }
            undoRedoElapsed[index] =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            checksum ^= undoGeneration ^ redoGeneration;
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
        GC.KeepAlive(checksum);
        Measurement undoRedo = Summarize(
            $"mesh-subobject-{transform.ToString().ToLowerInvariant()}-undo-redo-ms",
            undoRedoElapsed,
            allocated / iterations);
        return (
            editAndRebuild,
            undoRedo,
            session.ContentGeneration,
            mesh.Vertices.Count,
            mesh.Faces.Count);
    }

    (
        Measurement DeleteAndRebuild,
        Measurement UndoRedo,
        ulong FinalContentGeneration,
        int ControlVertexCount,
        int AuthoredFaceCount) MeasureDeletionLane()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        Mesh mesh = CreateMesh3DSubobjectEditGrid(gridSize);
        int controlVertexCount = mesh.Vertices.Count;
        int authoredFaceCount = mesh.Faces.Count;
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session, capacity: 4);

        CadDeleteMeshSubobjectsCommand CreateCommand()
        {
            CadDocumentSnapshot snapshot =
                new CadSnapshotCompiler().Compile(session);
            CadRecordedMesh3DScene scene =
                new CadMesh3DSceneCompiler().Compile(snapshot);
            CadMesh3DSubobjectComponent component =
                scene.SubobjectComponents.Span[0];
            var ids = new CadMesh3DSubobjectId[selectedFaceCount];
            int stride = Math.Max(1, authoredFaceCount / selectedFaceCount);
            for (int index = 0; index < ids.Length; index++)
            {
                ids[index] = new CadMesh3DSubobjectId(
                    scene.ContentGeneration,
                    component.Handle,
                    component.ComponentIndex,
                    CadMesh3DSubobjectKind.Face,
                    Math.Min(authoredFaceCount - 1, index * stride));
            }
            return new CadDeleteMeshSubobjectsCommand(scene, ids);
        }

        object DeleteAndRebuild()
        {
            CadDeleteMeshSubobjectsCommand command = CreateCommand();
            ulong generation = history.Execute(command);
            CadDocumentSnapshot rebuilt =
                new CadSnapshotCompiler().Compile(session);
            CadRecordedMesh3DScene rebuiltScene =
                new CadMesh3DSceneCompiler().Compile(rebuilt);
            return HashCode.Combine(
                generation,
                command.DeletedFaceCount,
                rebuiltScene.Statistics.TriangleCount);
        }

        for (int index = 0; index < warmups; index++)
        {
            _ = DeleteAndRebuild();
            if (!history.TryUndo(out _))
            {
                throw new InvalidOperationException(
                    "Mesh-subobject deletion benchmark warmup undo failed.");
            }
        }

        var deletionElapsed = new double[iterations];
        long deletionAllocated = 0;
        int checksum = 0;
        for (int index = 0; index < iterations; index++)
        {
            long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            object value = DeleteAndRebuild();
            deletionElapsed[index] =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            deletionAllocated +=
                GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
            checksum ^= value.GetHashCode();
            if (!history.TryUndo(out _))
            {
                throw new InvalidOperationException(
                    "Mesh-subobject deletion benchmark iteration undo failed.");
            }
        }
        Measurement deletion = Summarize(
            "mesh-subobject-delete-snapshot-scene-ms",
            deletionElapsed,
            deletionAllocated / iterations);

        history.Execute(CreateCommand());
        for (int index = 0; index < warmups; index++)
        {
            if (!history.TryUndo(out _) || !history.TryRedo(out _))
            {
                throw new InvalidOperationException(
                    "Mesh-subobject deletion undo/redo benchmark warmup failed.");
            }
        }
        var undoRedoElapsed = new double[iterations];
        long allocatedUndoRedoStart =
            GC.GetAllocatedBytesForCurrentThread();
        ulong undoRedoChecksum = 0;
        for (int index = 0; index < iterations; index++)
        {
            long started = Stopwatch.GetTimestamp();
            if (!history.TryUndo(out ulong undoGeneration) ||
                !history.TryRedo(out ulong redoGeneration))
            {
                throw new InvalidOperationException(
                    "Mesh-subobject deletion undo/redo benchmark iteration failed.");
            }
            undoRedoElapsed[index] =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            undoRedoChecksum ^= undoGeneration ^ redoGeneration;
        }
        long undoRedoAllocated =
            GC.GetAllocatedBytesForCurrentThread() - allocatedUndoRedoStart;
        GC.KeepAlive(checksum ^ undoRedoChecksum.GetHashCode());
        Measurement undoRedo = Summarize(
            "mesh-subobject-delete-undo-redo-ms",
            undoRedoElapsed,
            undoRedoAllocated / iterations);
        return (
            deletion,
            undoRedo,
            session.ContentGeneration,
            controlVertexCount,
            authoredFaceCount);
    }

    var translation = MeasureLane(CadMesh3DSubobjectTransform.Translate);
    var rotation = MeasureLane(CadMesh3DSubobjectTransform.Rotate);
    var scale = MeasureLane(CadMesh3DSubobjectTransform.Scale);
    var deletion = MeasureDeletionLane();

    var report = new CadMesh3DSubobjectEditBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        RuntimeInformation.FrameworkDescription,
        gridSize,
        translation.ControlVertexCount,
        translation.AuthoredFaceCount,
        selectedFaceCount,
        warmups,
        iterations,
        translation.EditAndRebuild,
        rotation.EditAndRebuild,
        scale.EditAndRebuild,
        deletion.DeleteAndRebuild,
        translation.UndoRedo,
        rotation.UndoRedo,
        scale.UndoRedo,
        deletion.UndoRedo,
        translation.FinalContentGeneration,
        rotation.FinalContentGeneration,
        scale.FinalContentGeneration,
        deletion.FinalContentGeneration,
        CaptureMesh3DReplayBinaryHashes());
    string json = JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, json);
    }
}

Mesh CreateMesh3DSubobjectEditGrid(int gridSize)
{
    var mesh = new Mesh();
    int stride = checked(gridSize + 1);
    for (int y = 0; y <= gridSize; y++)
    {
        for (int x = 0; x <= gridSize; x++)
        {
            mesh.Vertices.Add(new XYZ(x, y, 0));
        }
    }
    for (int y = 0; y < gridSize; y++)
    {
        for (int x = 0; x < gridSize; x++)
        {
            int first = checked((y * stride) + x);
            mesh.Faces.Add([
                first,
                first + 1,
                first + stride + 1,
                first + stride,
            ]);
        }
    }
    return mesh;
}

int mesh3DReplayBatchCount = ReadNonNegativeInt(
    "--mesh3d-replay-batches",
    0);
if (mesh3DReplayBatchCount != 0)
{
    RunMesh3DReplayBenchmark(
        mesh3DReplayBatchCount,
        ReadNonNegativeInt("--warmup", 12),
        ReadPositiveInt("--iterations", 120),
        ReadString("--output-json"));
    return;
}

int polylineAuthoringSegmentCount = ReadNonNegativeInt(
    "--polyline-authoring-segments",
    0);
if (polylineAuthoringSegmentCount != 0)
{
    RunPolylineAuthoringBenchmark(
        polylineAuthoringSegmentCount,
        ReadNonNegativeInt("--warmup", 3),
        ReadPositiveInt("--iterations", 24),
        ReadString("--output-json"));
    return;
}

int arcAuthoringSolveCount = ReadNonNegativeInt(
    "--arc-authoring-solves",
    0);
if (arcAuthoringSolveCount != 0)
{
    RunArcAuthoringBenchmark(
        arcAuthoringSolveCount,
        ReadNonNegativeInt("--warmup", 3),
        ReadPositiveInt("--iterations", 24),
        ReadString("--output-json"));
    return;
}

int isocircleAuthoringSolveCount = ReadNonNegativeInt(
    "--isocircle-authoring-solves",
    0);
if (isocircleAuthoringSolveCount != 0)
{
    RunIsocircleAuthoringBenchmark(
        isocircleAuthoringSolveCount,
        ReadNonNegativeInt("--warmup", 3),
        ReadPositiveInt("--iterations", 24),
        ReadString("--output-json"));
    return;
}

int cameraUpdateCount = ReadNonNegativeInt("--camera-updates", 0);
if (cameraUpdateCount != 0)
{
    RunCameraUpdateBenchmark(
        cameraUpdateCount,
        ReadPositiveInt("--camera-entities", 10_000),
        ReadNonNegativeInt("--warmup", 3),
        ReadPositiveInt("--iterations", 24),
        ReadString("--output-json"));
    return;
}

int viewportCount = ReadNonNegativeInt("--viewports", 0);
if (viewportCount != 0)
{
    bool useSplineViewportBoundaries = HasFlag("--spline-viewport-boundaries");
    RunViewportBenchmark(
        viewportCount,
        ReadPositiveInt("--viewport-layer-variants", 4),
        HasFlag("--nonrectangular-viewports") || useSplineViewportBoundaries,
        useSplineViewportBoundaries,
        ReadPositiveInt("--entities", 10_000),
        ReadNonNegativeInt("--warmup", 3),
        ReadPositiveInt("--iterations", 24),
        ReadString("--output-json"));
    return;
}

int entityCount = ReadNonNegativeInt("--entities", 10_000);
bool useVariableWidthPolylines = HasFlag("--variable-width-polylines");
bool useConstantWidthPolylines = HasFlag("--constant-width-polylines");
bool freezeAlternatingEntityLayers = HasFlag("--alternating-frozen-layers");
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
AttributeVisibilityMode attributeDisplayMode = ReadAttributeDisplayMode();
int dimensionEntityCount = ReadNonNegativeInt("--dimension-entities", 0);
int toleranceEntityCount = ReadNonNegativeInt("--tolerance-entities", 0);
int tableEntityCount = ReadNonNegativeInt("--table-entities", 0);
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
bool useWipeouts = HasFlag("--wipeouts");
bool measureRasterOutput = HasFlag("--raster-output");
int rasterOutputDpi = ReadPositiveInt("--raster-output-dpi", 96);
int shxInterpretationCount = ReadNonNegativeInt("--shx-interpretations", 0);
int shxLayoutCount = ReadNonNegativeInt("--shx-layouts", 0);
int warmupCount = ReadNonNegativeInt("--warmup", 3);
int iterationCount = ReadPositiveInt("--iterations", 24);
int queryCount = ReadPositiveInt("--queries", 10_000);
string? outputPath = ReadString("--output-json");

if (entityCount == 0 && blockArrayColumnCount == 0 && textEntityCount == 0 &&
    mtextEntityCount == 0 && shxTextEntityCount == 0 && shxMTextEntityCount == 0 &&
    attributeInsertCount == 0 && dimensionEntityCount == 0 &&
    toleranceEntityCount == 0 && tableEntityCount == 0 &&
    thickSolidEntityCount == 0 && meshEntityCount == 0 &&
    polygonMeshEntityCount == 0 && polyfaceMeshEntityCount == 0 &&
    pointEntityCount == 0 && constructionLineCount == 0 &&
    solidHatchCount == 0 && patternHatchCount == 0)
{
    throw new ArgumentException(
        "At least one ordinary entity, block-array column, text entity, attributed INSERT, DIMENSION, TOLERANCE, TABLE, thick SOLID, MESH, HATCH, or WIPEOUT is required.");
}

if (useWipeouts &&
    (entityCount == 0 || blockArrayColumnCount != 0 || textEntityCount != 0 ||
     mtextEntityCount != 0 || shxTextEntityCount != 0 || shxMTextEntityCount != 0 ||
     attributeInsertCount != 0 || dimensionEntityCount != 0 ||
     toleranceEntityCount != 0 || tableEntityCount != 0 ||
     thickSolidEntityCount != 0 || meshEntityCount != 0 ||
     polygonMeshEntityCount != 0 || polyfaceMeshEntityCount != 0 ||
     pointEntityCount != 0 || constructionLineCount != 0 ||
     solidHatchCount != 0 || patternHatchCount != 0 ||
     lowerLineTypes || lowerComplexLineTypes || lowerLinearSplineLineTypes ||
     lowerNurbsSplineLineTypes || lowerPeriodicSplineLineTypes ||
     measureSplineSelection || measureTextSelection || measureHatchSelection ||
     complexPatternGrammar || hatchIslandStyles || hatchSplineEdges))
{
    throw new ArgumentException(
        "--wipeouts requires positive --entities and no additional fixture families.");
}

if (blockArrayColumnCount > ushort.MaxValue)
{
    throw new ArgumentOutOfRangeException(
        nameof(blockArrayColumnCount),
        $"--block-array-columns cannot exceed {ushort.MaxValue}.");
}
if (freezeAlternatingEntityLayers &&
    (entityCount == 0 || blockArrayColumnCount != 0 || textEntityCount != 0 ||
     mtextEntityCount != 0 || shxTextEntityCount != 0 || shxMTextEntityCount != 0 ||
     attributeInsertCount != 0 || dimensionEntityCount != 0 ||
     toleranceEntityCount != 0 || tableEntityCount != 0 ||
     thickSolidEntityCount != 0 || meshEntityCount != 0 ||
     polygonMeshEntityCount != 0 || polyfaceMeshEntityCount != 0 ||
     pointEntityCount != 0 || constructionLineCount != 0 ||
     solidHatchCount != 0 || patternHatchCount != 0))
{
    throw new ArgumentException(
        "--alternating-frozen-layers requires positive --entities and no additional fixture families.");
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
if ((useVariableWidthPolylines || useConstantWidthPolylines) &&
    (useWipeouts || lowerLinearSplineLineTypes || lowerNurbsSplineLineTypes ||
     lowerPeriodicSplineLineTypes || measureSplineSelection))
{
    throw new ArgumentException(
        "Wide-polyline fixtures cannot be combined with ordinary-entity Wipeout or spline fixtures.");
}
if (useVariableWidthPolylines && useConstantWidthPolylines)
{
    throw new ArgumentException(
        "Choose either --variable-width-polylines or --constant-width-polylines.");
}

if (measureSplineSelection &&
    (entityCount == 0 || blockArrayColumnCount != 0 ||
     textEntityCount != 0 || mtextEntityCount != 0 || shxTextEntityCount != 0 ||
     shxMTextEntityCount != 0 || attributeInsertCount != 0 ||
     dimensionEntityCount != 0 || toleranceEntityCount != 0 || tableEntityCount != 0 ||
     thickSolidEntityCount != 0 || meshEntityCount != 0 ||
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
     toleranceEntityCount != 0 || tableEntityCount != 0 ||
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
     dimensionEntityCount != 0 || toleranceEntityCount != 0 || tableEntityCount != 0 ||
     thickSolidEntityCount != 0 || meshEntityCount != 0 ||
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
    useVariableWidthPolylines,
    useConstantWidthPolylines,
    blockArrayColumnCount,
    textEntityCount,
    mtextEntityCount,
    shxTextEntityCount,
    shxMTextEntityCount,
    attributeInsertCount,
    attributeDisplayMode,
    dimensionEntityCount,
    toleranceEntityCount,
    tableEntityCount,
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
    useWipeouts,
    resolveDrawOrder,
    freezeAlternatingEntityLayers);
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
        toleranceEntityCount == 0 && tableEntityCount == 0 &&
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
Measurement? pdfOutputMeasurement = null;
Measurement? pngOutputMeasurement = null;
if (measureRasterOutput)
{
    CadDocumentSnapshot plottingSnapshot = snapshotCompiler.Compile(
        session,
        new CadSnapshotOptions
        {
            TextFontResolver = snapshotOptions.TextFontResolver,
            ShxFontResolver = snapshotOptions.ShxFontResolver,
            DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
            DrawingBackgroundColor = new CadColor32(255, 255, 255),
        });
    using CadPrintPlan outputPlan = printPlanCompiler.Compile(
        plottingSnapshot,
        new CadPrintPlanOptions { OutputDpi = rasterOutputDpi });
    using CadPrintJob outputJob = new CadPrintJobCompiler().Compile(
    [
        new CadPrintJobPageSource("Benchmark", outputPlan),
    ]);
    var outputWriter = new CadPrintOutputWriter();
    for (int index = 0; index < warmupCount; index++)
    {
        using var pdf = new MemoryStream();
        using var png = new MemoryStream();
        _ = outputWriter.WritePdf(outputJob, pdf);
        _ = outputWriter.WritePng(outputJob, 0, png);
    }
    pdfOutputMeasurement = Measure(
        "raster-pdf-output",
        iterationCount,
        () =>
        {
            using var destination = new MemoryStream();
            CadPrintOutputResult result = outputWriter.WritePdf(
                outputJob,
                destination);
            return result.EncodedByteCount;
        });
    pngOutputMeasurement = Measure(
        "png-output",
        iterationCount,
        () =>
        {
            using var destination = new MemoryStream();
            CadPrintOutputResult result = outputWriter.WritePng(
                outputJob,
                0,
                destination);
            return result.EncodedByteCount;
        });
}
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
    useVariableWidthPolylines,
    useConstantWidthPolylines,
    freezeAlternatingEntityLayers,
    resolveDrawOrder,
    drawOrderEditEntityCount,
    blockArrayColumnCount,
    textEntityCount,
    mtextEntityCount,
    shxTextEntityCount,
    shxMTextEntityCount,
    attributeInsertCount,
    attributeDisplayMode,
    dimensionEntityCount,
    toleranceEntityCount,
    tableEntityCount,
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
    useWipeouts,
    measureRasterOutput,
    rasterOutputDpi,
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
    pdfOutputMeasurement,
    pngOutputMeasurement,
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

void RunPolylineAuthoringBenchmark(
    int segmentCount,
    int warmups,
    int iterations,
    string? reportPath)
{
    if (segmentCount > CadPolylineAuthoringSession.DefaultMaximumSegmentCount)
    {
        throw new ArgumentOutOfRangeException(
            nameof(segmentCount),
            $"PLINE authoring cannot exceed {CadPolylineAuthoringSession.DefaultMaximumSegmentCount} segments.");
    }

    for (int i = 0; i < warmups; i++)
    {
        _ = CreatePolylineAuthoringSnapshot(segmentCount, changeEverySegment: false);
        _ = CreatePolylineAuthoringSnapshot(segmentCount, changeEverySegment: true);
        _ = CreateExplicitArcPolylineAuthoringSnapshot(segmentCount);
        _ = CreateNestedCenterAnglePolylineAuthoringSnapshot(segmentCount);
        _ = CreateClockwiseMajorRadiusPolylineAuthoringSnapshot(segmentCount);
    }

    Measurement inherited = Measure(
        "polyline-authoring-inherited-width",
        iterations,
        () => CreatePolylineAuthoringSnapshot(segmentCount, changeEverySegment: false));
    Measurement variable = Measure(
        "polyline-authoring-width-option-every-segment",
        iterations,
        () => CreatePolylineAuthoringSnapshot(segmentCount, changeEverySegment: true));
    Measurement explicitArcs = Measure(
        "polyline-authoring-explicit-angle-arcs",
        iterations,
        () => CreateExplicitArcPolylineAuthoringSnapshot(segmentCount));
    Measurement nestedCenterAngleArcs = Measure(
        "polyline-authoring-nested-center-angle-arcs",
        iterations,
        () => CreateNestedCenterAnglePolylineAuthoringSnapshot(segmentCount));
    Measurement clockwiseMajorRadiusArcs = Measure(
        "polyline-authoring-clockwise-major-radius-arcs",
        iterations,
        () => CreateClockwiseMajorRadiusPolylineAuthoringSnapshot(segmentCount));
    var report = new CadPolylineAuthoringBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        segmentCount,
        warmups,
        iterations,
        inherited,
        variable,
        explicitArcs,
        nestedCenterAngleArcs,
        clockwiseMajorRadiusArcs);
    var options = new JsonSerializerOptions { WriteIndented = true };
    string reportJson = JsonSerializer.Serialize(report, options);
    Console.WriteLine(reportJson);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, reportJson);
    }
}

CadPolylineAuthoringSnapshot CreatePolylineAuthoringSnapshot(
    int segmentCount,
    bool changeEverySegment)
{
    var authoring = new CadPolylineAuthoringSession(segmentCount, initialWidth: 2.0);
    if (!authoring.TryAcceptPoint(CadPoint3D.Zero, out string? firstError))
    {
        throw new InvalidOperationException(firstError);
    }
    double width = 2.0;
    for (int i = 0; i < segmentCount; i++)
    {
        if (changeEverySegment)
        {
            double endingWidth = (i & 1) == 0 ? 4.0 : 2.0;
            if (!authoring.TryBeginWidthInput(
                    CadPolylineWidthInputMode.Width,
                    out string? widthError) ||
                !authoring.TryAcceptWidthValue(width, out widthError) ||
                !authoring.TryAcceptWidthValue(endingWidth, out widthError))
            {
                throw new InvalidOperationException(widthError);
            }
            width = endingWidth;
        }
        if (!authoring.TryAcceptPoint(
                new CadPoint3D(i + 1.0, i % 17, 0.0),
                out string? pointError))
        {
            throw new InvalidOperationException(pointError);
        }
    }
    if (!authoring.TryCreateSnapshot(
            close: false,
            out CadPolylineAuthoringSnapshot? snapshot,
            out string? snapshotError))
    {
        throw new InvalidOperationException(snapshotError);
    }
    return snapshot!;
}

CadPolylineAuthoringSnapshot CreateExplicitArcPolylineAuthoringSnapshot(
    int segmentCount)
{
    var authoring = new CadPolylineAuthoringSession(segmentCount);
    if (!authoring.TryAcceptPoint(CadPoint3D.Zero, out string? firstError))
    {
        throw new InvalidOperationException(firstError);
    }
    authoring.Mode = CadPolylineAuthoringMode.TangentArc;
    for (int i = 0; i < segmentCount; i++)
    {
        if (!authoring.TryBeginArcConstruction(
                CadPolylineArcConstruction.IncludedAngle,
                out string? optionError) ||
            !authoring.TryAcceptArcScalar(Math.PI / 3.0, out optionError) ||
            !authoring.TryAcceptArcEndpoint(
                new CadPoint3D(i + 1.0, i % 17, 0.0),
                out _,
                out optionError))
        {
            throw new InvalidOperationException(optionError);
        }
    }
    if (!authoring.TryCreateSnapshot(
            close: false,
            out CadPolylineAuthoringSnapshot? snapshot,
            out string? snapshotError))
    {
        throw new InvalidOperationException(snapshotError);
    }
    return snapshot!;
}

CadPolylineAuthoringSnapshot CreateNestedCenterAnglePolylineAuthoringSnapshot(
    int segmentCount)
{
    var authoring = new CadPolylineAuthoringSession(segmentCount);
    if (!authoring.TryAcceptPoint(
            new CadPoint3D(10.0, 0.0, 0.0),
            out string? firstError))
    {
        throw new InvalidOperationException(firstError);
    }
    authoring.Mode = CadPolylineAuthoringMode.TangentArc;
    for (int i = 0; i < segmentCount; i++)
    {
        CadPoint3D start = authoring.CurrentPoint!.Value;
        CadPoint3D center = start + new CadPoint3D(-10.0, 0.0, 0.0);
        if (!authoring.TryBeginArcConstruction(
                CadPolylineArcConstruction.Center,
                out string? optionError) ||
            !authoring.TryAcceptArcControlPoint(center, out optionError) ||
            !authoring.TryBeginArcNestedOption(
                CadPolylineArcNestedOption.IncludedAngle,
                out optionError) ||
            !authoring.TryAcceptArcNestedScalar(
                Math.PI / 3.0,
                out _,
                out optionError))
        {
            throw new InvalidOperationException(optionError);
        }
    }
    if (!authoring.TryCreateSnapshot(
            close: false,
            out CadPolylineAuthoringSnapshot? snapshot,
            out string? snapshotError))
    {
        throw new InvalidOperationException(snapshotError);
    }
    return snapshot!;
}

CadPolylineAuthoringSnapshot CreateClockwiseMajorRadiusPolylineAuthoringSnapshot(
    int segmentCount)
{
    var authoring = new CadPolylineAuthoringSession(segmentCount);
    if (!authoring.TryAcceptPoint(CadPoint3D.Zero, out string? firstError))
    {
        throw new InvalidOperationException(firstError);
    }
    authoring.Mode = CadPolylineAuthoringMode.TangentArc;
    for (int i = 0; i < segmentCount; i++)
    {
        CadPoint3D endpoint = authoring.CurrentPoint!.Value +
            new CadPoint3D(1.0, 0.0, 0.0);
        if (!authoring.TryBeginArcConstruction(
                CadPolylineArcConstruction.Radius,
                out string? optionError) ||
            !authoring.TryAcceptArcScalar(-1.0, out optionError) ||
            !authoring.TryAcceptArcEndpoint(
                endpoint,
                clockwiseOverride: true,
                out _,
                out optionError))
        {
            throw new InvalidOperationException(optionError);
        }
    }
    if (!authoring.TryCreateSnapshot(
            close: false,
            out CadPolylineAuthoringSnapshot? snapshot,
            out string? snapshotError))
    {
        throw new InvalidOperationException(snapshotError);
    }
    return snapshot!;
}

void RunArcAuthoringBenchmark(
    int solveCount,
    int warmups,
    int iterations,
    string? reportPath)
{
    for (int i = 0; i < warmups; i++)
    {
        _ = CreatePointFinalArcChecksum(
            solveCount,
            clockwiseOverride: false);
        _ = CreatePointFinalArcChecksum(
            solveCount,
            clockwiseOverride: true);
    }

    Measurement defaultRoutes = Measure(
        "arc-authoring-point-finals-default",
        iterations,
        () => CreatePointFinalArcChecksum(
            solveCount,
            clockwiseOverride: false));
    Measurement clockwiseRoutes = Measure(
        "arc-authoring-point-finals-clockwise",
        iterations,
        () => CreatePointFinalArcChecksum(
            solveCount,
            clockwiseOverride: true));
    var report = new CadArcAuthoringBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        solveCount,
        warmups,
        iterations,
        defaultRoutes,
        clockwiseRoutes);
    var options = new JsonSerializerOptions { WriteIndented = true };
    string reportJson = JsonSerializer.Serialize(report, options);
    Console.WriteLine(reportJson);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, reportJson);
    }
}

double CreatePointFinalArcChecksum(
    int solveCount,
    bool clockwiseOverride)
{
    double checksum = 0.0;
    for (int i = 0; i < solveCount; i++)
    {
        double offset = i * 20.0;
        CadPoint3D center = new(offset, 10.0, 0.0);
        CadPoint3D start = new(offset, 0.0, 0.0);
        CadPoint3D end = new(offset + 10.0, 10.0, 0.0);
        CadPoint3D finalPoint;
        CadArcAuthoringMode mode;
        CadPoint3D first;
        CadPoint3D second;
        switch (i % 3)
        {
            case 0:
                mode = CadArcAuthoringMode.CenterStartEnd;
                first = center;
                second = start;
                finalPoint = end;
                break;
            case 1:
                mode = CadArcAuthoringMode.StartCenterEnd;
                first = start;
                second = center;
                finalPoint = end;
                break;
            default:
                mode = CadArcAuthoringMode.StartEndDirection;
                first = start;
                second = end;
                finalPoint = new CadPoint3D(offset + 10.0, 0.0, 0.0);
                break;
        }

        var authoring = new CadArcAuthoringSession(mode);
        if (!authoring.TryAcceptIntermediatePoint(first, out string? error) ||
            !authoring.TryAcceptIntermediatePoint(second, out error) ||
            !authoring.TryCreateSnapshot(
                finalPoint,
                clockwiseOverride,
                out CadArcAuthoringSnapshot snapshot,
                out error))
        {
            throw new InvalidOperationException(error);
        }
        checksum += snapshot.Center.X + snapshot.Radius + snapshot.SweepAngle;
    }
    return checksum;
}

void RunIsocircleAuthoringBenchmark(
    int solveCount,
    int warmups,
    int iterations,
    string? reportPath)
{
    for (int i = 0; i < warmups; i++)
    {
        _ = CreateIsocircleChecksum(solveCount, useDiameter: false);
        _ = CreateIsocircleChecksum(solveCount, useDiameter: true);
    }

    Measurement radius = Measure(
        "ellipse-isocircle-radius",
        iterations,
        () => CreateIsocircleChecksum(solveCount, useDiameter: false));
    Measurement diameter = Measure(
        "ellipse-isocircle-diameter",
        iterations,
        () => CreateIsocircleChecksum(solveCount, useDiameter: true));
    var report = new CadIsocircleAuthoringBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        solveCount,
        warmups,
        iterations,
        radius,
        diameter);
    var options = new JsonSerializerOptions { WriteIndented = true };
    string reportJson = JsonSerializer.Serialize(report, options);
    Console.WriteLine(reportJson);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, reportJson);
    }
}

double CreateIsocircleChecksum(int solveCount, bool useDiameter)
{
    CadPlanGridSnapSettings left =
        CadPlanGridSnapSettings.CreateIsometric(
            false,
            CadPoint3D.Zero,
            1.0,
            CadPlanIsoplane.Left,
            0.125);
    CadPlanGridSnapSettings top =
        CadPlanGridSnapSettings.CreateIsometric(
            false,
            CadPoint3D.Zero,
            1.0,
            CadPlanIsoplane.Top,
            -0.25);
    CadPlanGridSnapSettings right =
        CadPlanGridSnapSettings.CreateIsometric(
            false,
            CadPoint3D.Zero,
            1.0,
            CadPlanIsoplane.Right,
            0.375);
    double checksum = 0.0;
    for (int i = 0; i < solveCount; i++)
    {
        CadPlanGridSnapSettings settings = (i % 3) switch
        {
            0 => left,
            1 => top,
            _ => right,
        };
        var authoring = new CadEllipseAuthoringSession(
            useDiameter
                ? CadEllipseAuthoringMode.IsocircleDiameter
                : CadEllipseAuthoringMode.IsocircleRadius,
            isometricSnapSettings: settings);
        CadPoint3D center = new(i * 2.0, -i, i % 11);
        if (!authoring.TryAcceptPoint(
                center,
                out _,
                out bool intermediateCompleted,
                out string? error) ||
            intermediateCompleted ||
            !authoring.TryAcceptScalar(
                useDiameter ? 16.0 : 8.0,
                out CadEllipseAuthoringSnapshot snapshot,
                out bool completed,
                out error) ||
            !completed)
        {
            throw new InvalidOperationException(error);
        }
        checksum += snapshot.Center.X +
            snapshot.MajorAxisEndPoint.Y +
            snapshot.MinorRadius;
    }
    return checksum;
}

void RunCameraUpdateBenchmark(
    int updateCount,
    int largeEntityCount,
    int warmups,
    int iterations,
    string? reportPath)
{
    CadMesh3DViewCoordinator small = CreateCameraBenchmarkCoordinator(1);
    CadMesh3DViewCoordinator large =
        CreateCameraBenchmarkCoordinator(largeEntityCount);

    for (int i = 0; i < warmups; i++)
    {
        UpdateCameraBatch(small, updateCount);
        UpdateCameraBatch(large, updateCount);
    }

    CadMesh3DViewStatistics smallBefore = small.Statistics;
    CadMesh3DViewStatistics largeBefore = large.Statistics;
    Measurement smallMeasurement = MeasureCameraUpdateBatches(
        "camera-update-1-entity-batch-ms",
        small,
        updateCount,
        iterations);
    Measurement largeMeasurement = MeasureCameraUpdateBatches(
        $"camera-update-{largeEntityCount}-entity-batch-ms",
        large,
        updateCount,
        iterations);
    CadMesh3DViewStatistics smallAfter = small.Statistics;
    CadMesh3DViewStatistics largeAfter = large.Statistics;

    ValidateCameraBenchmarkCase(
        smallBefore,
        smallAfter,
        updateCount,
        iterations,
        smallMeasurement);
    ValidateCameraBenchmarkCase(
        largeBefore,
        largeAfter,
        updateCount,
        iterations,
        largeMeasurement);

    var report = new CadCameraUpdateBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        updateCount,
        largeEntityCount,
        warmups,
        iterations,
        smallMeasurement,
        largeMeasurement,
        largeMeasurement.P95 / smallMeasurement.P95,
        smallAfter,
        largeAfter);
    var options = new JsonSerializerOptions { WriteIndented = true };
    string reportJson = JsonSerializer.Serialize(report, options);
    Console.WriteLine(reportJson);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, reportJson);
    }
}

void RunMesh3DReplayBenchmark(
    int batchCount,
    int warmupCount,
    int iterationCount,
    string? reportPath)
{
    using var window = new HeadlessWindow(
        1280,
        720,
        CompositorOptions.Default with
        {
            EnableGpuHitTesting = false,
        });
    Viewport3D viewport = CreateMesh3DReplayViewport(batchCount);
    window.Content = viewport;
    window.Render();
    Mesh3DFrameMetrics initial = viewport.LastMesh3DFrameMetrics;
    if (initial.SceneCompilationCount != 1 ||
        initial.GeometryVertexUploadBytes == 0 ||
        initial.RecordUploadBytes == 0 ||
        initial.RecordIndexUploadBytes == 0 ||
        initial.GeometryResidentCount != 1 ||
        initial.GeometryBufferResidentBytes == 0 ||
        initial.ViewportResourceCount != 1 ||
        initial.ViewportBufferResidentBytes == 0 ||
        initial.LogicalTargetTextureBytes == 0 ||
        initial.DrawCallCount != batchCount)
    {
        throw new InvalidOperationException(
            $"Mesh3D first-frame contract failed: {initial}.");
    }

    var camera = (OrthographicCamera)viewport.Camera;
    for (int i = 0; i < warmupCount; i++)
    {
        UpdateMesh3DReplayCamera(camera, i);
        window.Render();
        ValidateStableMesh3DReplay(
            viewport.LastMesh3DFrameMetrics,
            batchCount);
    }

    var elapsed = new double[iterationCount];
    long allocatedStart =
        GC.GetAllocatedBytesForCurrentThread();
    long cameraAllocatedBytes = 0;
    long renderAllocatedBytes = 0;
    long validationAllocatedBytes = 0;
    HeadlessRenderAllocationMetrics renderAllocationBreakdown = default;
    ulong uniformUploadBytes = 0;
    for (int i = 0; i < iterationCount; i++)
    {
        long cameraAllocationStart =
            GC.GetAllocatedBytesForCurrentThread();
        UpdateMesh3DReplayCamera(camera, warmupCount + i);
        cameraAllocatedBytes +=
            GC.GetAllocatedBytesForCurrentThread() -
            cameraAllocationStart;
        long started = Stopwatch.GetTimestamp();
        long renderAllocationStart =
            GC.GetAllocatedBytesForCurrentThread();
        HeadlessRenderAllocationMetrics frameAllocations =
            window.RenderWithAllocationMetrics();
        renderAllocationBreakdown = new HeadlessRenderAllocationMetrics(
            renderAllocationBreakdown.AnimationBytes +
                frameAllocations.AnimationBytes,
            renderAllocationBreakdown.MeasureBytes +
                frameAllocations.MeasureBytes,
            renderAllocationBreakdown.ArrangeBytes +
                frameAllocations.ArrangeBytes,
            renderAllocationBreakdown.CompositorBytes +
                frameAllocations.CompositorBytes);
        renderAllocatedBytes +=
            GC.GetAllocatedBytesForCurrentThread() -
            renderAllocationStart;
        elapsed[i] = Stopwatch.GetElapsedTime(started)
            .TotalMilliseconds;
        Mesh3DFrameMetrics metrics =
            viewport.LastMesh3DFrameMetrics;
        long validationAllocationStart =
            GC.GetAllocatedBytesForCurrentThread();
        ValidateStableMesh3DReplay(metrics, batchCount);
        uniformUploadBytes += metrics.UniformUploadBytes;
        validationAllocatedBytes +=
            GC.GetAllocatedBytesForCurrentThread() -
            validationAllocationStart;
    }
    long allocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    Mesh3DFrameMetrics stable =
        viewport.LastMesh3DFrameMetrics;
    CadMesh3DReplayBinaryHashes binarySha256 =
        CaptureMesh3DReplayBinaryHashes();
    var report = new CadMesh3DReplayBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        Environment.Version.ToString(),
        binarySha256.Benchmark,
        binarySha256,
        batchCount,
        warmupCount,
        iterationCount,
        Summarize(
            "managed-mesh3d-camera-frame-ms",
            elapsed,
            allocatedBytes / iterationCount),
        allocatedBytes,
        cameraAllocatedBytes,
        renderAllocatedBytes,
        validationAllocatedBytes,
        renderAllocationBreakdown,
        uniformUploadBytes,
        initial,
        stable);
    string json = JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    Console.WriteLine(json);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, json);
    }
    window.Content = null;
}

void RunMesh3DSelectionBenchmark(
    int gridSize,
    int depthLayerCount,
    int warmupCount,
    int iterationCount,
    int queryCount,
    string? reportPath)
{
    if (depthLayerCount > CadMesh3DSelectionIndex.MaximumHitCount)
    {
        throw new ArgumentOutOfRangeException(
            nameof(depthLayerCount),
            $"Selection depth layers cannot exceed {CadMesh3DSelectionIndex.MaximumHitCount}.");
    }
    var document = new CadDocument();
    var meshes = new Mesh[depthLayerCount];
    for (int layer = 0; layer < meshes.Length; layer++)
    {
        meshes[layer] = CreateMesh3DSelectionGrid(
            gridSize,
            layer * 0.01);
        document.Entities.Add(meshes[layer]);
    }
    CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
        new CadDocumentSession(document));
    CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);
    int expectedTriangleCount = checked(
        gridSize * gridSize * 2 * depthLayerCount);
    if (scene.Statistics.TriangleCount != expectedTriangleCount)
    {
        throw new InvalidOperationException(
            $"The selection benchmark expected {expectedTriangleCount} triangles but compiled {scene.Statistics.TriangleCount}.");
    }

    for (int index = 0; index < warmupCount; index++)
    {
        _ = CadMesh3DSelectionIndex.Build(scene);
    }
    Measurement buildMeasurement = Measure(
        "mesh3d-selection-index-build-ms",
        iterationCount,
        () => CadMesh3DSelectionIndex.Build(scene));
    CadMesh3DSelectionIndex selectionIndex =
        CadMesh3DSelectionIndex.Build(scene);
    CadMesh3DViewport viewport = CadMesh3DViewport.Fit(scene);
    Vector2 viewportSize = new(1_920.0f, 1_080.0f);
    var queryPoints = new Vector2[queryCount];
    var aperturePoints = new Vector2[queryCount];
    var lassoPoints = new Vector2[checked(queryCount * 3)];
    var fencePoints = new Vector2[checked(queryCount * 2)];
    uint state = 0x9e3779b9U;
    int interiorSize = Math.Max(1, gridSize - 2);
    int interiorOffset = gridSize > 2 ? 1 : 0;
    for (int index = 0; index < queryPoints.Length; index++)
    {
        state = unchecked(state * 1_664_525U + 1_013_904_223U);
        int x = (int)(state % (uint)interiorSize) + interiorOffset;
        state = unchecked(state * 1_664_525U + 1_013_904_223U);
        int y = (int)(state % (uint)interiorSize) + interiorOffset;
        queryPoints[index] = ProjectMesh3DSelectionPoint(
            viewport,
            scene,
            viewportSize,
            new CadPoint3D(
                x + 0.375,
                y + 0.625,
                (depthLayerCount - 1) * 0.01));
        Vector2 boundary = ProjectMesh3DSelectionPoint(
            viewport,
            scene,
            viewportSize,
            new CadPoint3D(
                gridSize,
                y + 0.625,
                (depthLayerCount - 1) * 0.01));
        Vector2 outward = ProjectMesh3DSelectionPoint(
            viewport,
            scene,
            viewportSize,
            new CadPoint3D(
                gridSize + 1.0,
                y + 0.625,
                (depthLayerCount - 1) * 0.01)) - boundary;
        aperturePoints[index] = boundary +
            Vector2.Normalize(outward) * 0.75f;
        int lassoOffset = index * 3;
        lassoPoints[lassoOffset] = queryPoints[index] +
            new Vector2(-4.0f, -4.0f);
        lassoPoints[lassoOffset + 1] = queryPoints[index] +
            new Vector2(4.0f, -4.0f);
        lassoPoints[lassoOffset + 2] = queryPoints[index] +
            new Vector2(0.0f, 4.0f);
        int fenceOffset = index * 2;
        fencePoints[fenceOffset] = queryPoints[index] +
            new Vector2(-4.0f, 0.0f);
        fencePoints[fenceOffset + 1] = queryPoints[index] +
            new Vector2(4.0f, 0.0f);
    }

    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DSelectionResult warm = selectionIndex.Query(
            viewport,
            viewportSize,
            queryPoints[index]);
        if (!warm.IsHit)
        {
            throw new InvalidOperationException(
                "A warm selection benchmark query missed the retained grid.");
        }
    }

    var elapsed = new double[queryCount];
    long visitedNodeCount = 0;
    long testedTriangleCount = 0;
    int maximumVisitedNodeCount = 0;
    int maximumTestedTriangleCount = 0;
    ulong checksum = 0;
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DSelectionResult result = selectionIndex.Query(
            viewport,
            viewportSize,
            queryPoints[index]);
        elapsed[index] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        if (!result.IsHit)
        {
            throw new InvalidOperationException(
                "A measured selection benchmark query missed the retained grid.");
        }
        visitedNodeCount += result.VisitedNodeCount;
        testedTriangleCount += result.TestedTriangleCount;
        maximumVisitedNodeCount = Math.Max(
            maximumVisitedNodeCount,
            result.VisitedNodeCount);
        maximumTestedTriangleCount = Math.Max(
            maximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= result.Handle + (uint)result.TriangleIndex;
    }
    long allocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - allocationStart;
    GC.KeepAlive(checksum);

    var semanticHits =
        new CadMesh3DSelectionResult[depthLayerCount];
    var semanticElapsed = new double[queryCount];
    long semanticVisitedNodeCount = 0;
    long semanticTestedTriangleCount = 0;
    long semanticIntersectedTriangleCount = 0;
    int semanticMaximumVisitedNodeCount = 0;
    int semanticMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DSelectionHitQueryResult warm = selectionIndex.QueryHits(
            viewport,
            viewportSize,
            queryPoints[index],
            semanticHits);
        if (warm.HitCount != depthLayerCount || warm.WasTruncated)
        {
            throw new InvalidOperationException(
                "A warm semantic-depth benchmark query did not return every layer.");
        }
    }
    long semanticAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DSelectionHitQueryResult result = selectionIndex.QueryHits(
            viewport,
            viewportSize,
            queryPoints[index],
            semanticHits);
        semanticElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.HitCount != depthLayerCount || result.WasTruncated)
        {
            throw new InvalidOperationException(
                "A measured semantic-depth benchmark query did not return every layer.");
        }
        semanticVisitedNodeCount += result.VisitedNodeCount;
        semanticTestedTriangleCount += result.TestedTriangleCount;
        semanticIntersectedTriangleCount += result.IntersectedTriangleCount;
        semanticMaximumVisitedNodeCount = Math.Max(
            semanticMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        semanticMaximumTestedTriangleCount = Math.Max(
            semanticMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= semanticHits[0].Handle +
            semanticHits[result.HitCount - 1].Handle;
    }
    long semanticAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - semanticAllocationStart;
    GC.KeepAlive(checksum);

    var apertureHits =
        new CadMesh3DSelectionResult[depthLayerCount];
    var apertureElapsed = new double[queryCount];
    long apertureVisitedNodeCount = 0;
    long apertureTestedTriangleCount = 0;
    long apertureIntersectedTriangleCount = 0;
    long apertureHitCount = 0;
    int apertureMaximumVisitedNodeCount = 0;
    int apertureMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DSelectionHitQueryResult warm =
            selectionIndex.QueryApertureHits(
                viewport,
                viewportSize,
                aperturePoints[index],
                apertureHits);
        if (warm.HitCount == 0 || warm.WasTruncated)
        {
            throw new InvalidOperationException(
                "A warm pick-target benchmark query did not return a bounded hit.");
        }
    }
    long apertureAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < aperturePoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DSelectionHitQueryResult result =
            selectionIndex.QueryApertureHits(
                viewport,
                viewportSize,
                aperturePoints[index],
                apertureHits);
        apertureElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.HitCount == 0 || result.WasTruncated)
        {
            throw new InvalidOperationException(
                "A measured pick-target benchmark query did not return a bounded hit.");
        }
        apertureVisitedNodeCount += result.VisitedNodeCount;
        apertureTestedTriangleCount += result.TestedTriangleCount;
        apertureIntersectedTriangleCount += result.IntersectedTriangleCount;
        apertureHitCount += result.HitCount;
        apertureMaximumVisitedNodeCount = Math.Max(
            apertureMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        apertureMaximumTestedTriangleCount = Math.Max(
            apertureMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= apertureHits[0].Handle +
            apertureHits[result.HitCount - 1].Handle;
    }
    long apertureAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - apertureAllocationStart;
    GC.KeepAlive(checksum);

    var regionRootScratch = new int[selectionIndex.SemanticRootCount];
    var regionHandles = new ulong[selectionIndex.SemanticRootCount];
    var regionElapsed = new double[queryCount];
    long regionVisitedNodeCount = 0;
    long regionTestedTriangleCount = 0;
    long regionIntersectedTriangleCount = 0;
    int regionMaximumVisitedNodeCount = 0;
    int regionMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DRegionQueryResult warm = selectionIndex.QueryRegion(
            viewport,
            viewportSize,
            queryPoints[index] - new Vector2(4.0f),
            queryPoints[index] + new Vector2(4.0f),
            CadBoundsSelectionMode.Crossing,
            regionRootScratch,
            regionHandles);
        if (warm.HandleTotalCount != depthLayerCount ||
            warm.AreHandlesTruncated)
        {
            throw new InvalidOperationException(
                "A warm projected-region benchmark query did not return every layer.");
        }
    }
    long regionAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DRegionQueryResult result = selectionIndex.QueryRegion(
            viewport,
            viewportSize,
            queryPoints[index] - new Vector2(4.0f),
            queryPoints[index] + new Vector2(4.0f),
            CadBoundsSelectionMode.Crossing,
            regionRootScratch,
            regionHandles);
        regionElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.HandleTotalCount != depthLayerCount ||
            result.AreHandlesTruncated)
        {
            throw new InvalidOperationException(
                "A measured projected-region benchmark query did not return every layer.");
        }
        regionVisitedNodeCount += result.VisitedNodeCount;
        regionTestedTriangleCount += result.TestedTriangleCount;
        regionIntersectedTriangleCount += result.IntersectedTriangleCount;
        regionMaximumVisitedNodeCount = Math.Max(
            regionMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        regionMaximumTestedTriangleCount = Math.Max(
            regionMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= regionHandles[0] +
            regionHandles[result.HandleWrittenCount - 1];
    }
    long regionAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - regionAllocationStart;
    GC.KeepAlive(checksum);

    var lassoElapsed = new double[queryCount];
    long lassoVisitedNodeCount = 0;
    long lassoTestedTriangleCount = 0;
    long lassoIntersectedTriangleCount = 0;
    long lassoHandleCount = 0;
    int lassoMaximumVisitedNodeCount = 0;
    int lassoMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DRegionQueryResult warm = selectionIndex.QueryLasso(
            viewport,
            viewportSize,
            lassoPoints.AsSpan(index * 3, 3),
            CadBoundsSelectionMode.Crossing,
            regionRootScratch,
            regionHandles);
        if (warm.HandleTotalCount == 0 || warm.AreHandlesTruncated)
        {
            throw new InvalidOperationException(
                "A warm projected-lasso benchmark query did not return a bounded hit.");
        }
    }
    long lassoAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DRegionQueryResult result = selectionIndex.QueryLasso(
            viewport,
            viewportSize,
            lassoPoints.AsSpan(index * 3, 3),
            CadBoundsSelectionMode.Crossing,
            regionRootScratch,
            regionHandles);
        lassoElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.HandleTotalCount == 0 || result.AreHandlesTruncated)
        {
            throw new InvalidOperationException(
                "A measured projected-lasso benchmark query did not return a bounded hit.");
        }
        lassoVisitedNodeCount += result.VisitedNodeCount;
        lassoTestedTriangleCount += result.TestedTriangleCount;
        lassoIntersectedTriangleCount += result.IntersectedTriangleCount;
        lassoHandleCount += result.HandleTotalCount;
        lassoMaximumVisitedNodeCount = Math.Max(
            lassoMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        lassoMaximumTestedTriangleCount = Math.Max(
            lassoMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= regionHandles[0] +
            regionHandles[result.HandleWrittenCount - 1];
    }
    long lassoAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - lassoAllocationStart;
    GC.KeepAlive(checksum);

    var fenceElapsed = new double[queryCount];
    long fenceVisitedNodeCount = 0;
    long fenceTestedTriangleCount = 0;
    long fenceIntersectedTriangleCount = 0;
    long fenceHandleCount = 0;
    int fenceMaximumVisitedNodeCount = 0;
    int fenceMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DRegionQueryResult warm = selectionIndex.QueryFence(
            viewport,
            viewportSize,
            fencePoints.AsSpan(index * 2, 2),
            regionRootScratch,
            regionHandles);
        if (warm.HandleTotalCount == 0 || warm.AreHandlesTruncated)
        {
            throw new InvalidOperationException(
                "A warm projected-fence benchmark query did not return a bounded hit.");
        }
    }
    long fenceAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DRegionQueryResult result = selectionIndex.QueryFence(
            viewport,
            viewportSize,
            fencePoints.AsSpan(index * 2, 2),
            regionRootScratch,
            regionHandles);
        fenceElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.HandleTotalCount == 0 || result.AreHandlesTruncated)
        {
            throw new InvalidOperationException(
                "A measured projected-fence benchmark query did not return a bounded hit.");
        }
        fenceVisitedNodeCount += result.VisitedNodeCount;
        fenceTestedTriangleCount += result.TestedTriangleCount;
        fenceIntersectedTriangleCount += result.IntersectedTriangleCount;
        fenceHandleCount += result.HandleTotalCount;
        fenceMaximumVisitedNodeCount = Math.Max(
            fenceMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        fenceMaximumTestedTriangleCount = Math.Max(
            fenceMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= regionHandles[0] +
            regionHandles[result.HandleWrittenCount - 1];
    }
    long fenceAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - fenceAllocationStart;
    GC.KeepAlive(checksum);

    var subobjectHits =
        new CadMesh3DSubobjectSelectionResult[depthLayerCount];
    var subobjectElapsed = new double[queryCount];
    long subobjectVisitedNodeCount = 0;
    long subobjectTestedTriangleCount = 0;
    long subobjectIntersectedTriangleCount = 0;
    long subobjectHitCount = 0;
    int subobjectMaximumVisitedNodeCount = 0;
    int subobjectMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DSubobjectQueryResult warm =
            selectionIndex.QuerySubobjects(
                viewport,
                viewportSize,
                queryPoints[index],
                CadMesh3DSubobjectFilter.Face,
                subobjectHits,
                0.01f);
        if (warm.HitCount != depthLayerCount || warm.WasTruncated)
        {
            throw new InvalidOperationException(
                "A warm modern-MESH face-subobject benchmark query did not return every layer.");
        }
    }
    long subobjectAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DSubobjectQueryResult result =
            selectionIndex.QuerySubobjects(
                viewport,
                viewportSize,
                queryPoints[index],
                CadMesh3DSubobjectFilter.Face,
                subobjectHits,
                0.01f);
        subobjectElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.HitCount != depthLayerCount || result.WasTruncated)
        {
            throw new InvalidOperationException(
                "A measured modern-MESH face-subobject benchmark query did not return every layer.");
        }
        subobjectVisitedNodeCount += result.VisitedNodeCount;
        subobjectTestedTriangleCount += result.TestedTriangleCount;
        subobjectIntersectedTriangleCount += result.IntersectedTriangleCount;
        subobjectHitCount += result.HitCount;
        subobjectMaximumVisitedNodeCount = Math.Max(
            subobjectMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        subobjectMaximumTestedTriangleCount = Math.Max(
            subobjectMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= subobjectHits[0].Id.Handle +
            (uint)subobjectHits[result.HitCount - 1].Id.Index;
    }
    long subobjectAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - subobjectAllocationStart;
    GC.KeepAlive(checksum);

    var subobjectRegionScratch = new int[selectionIndex.SubobjectCount];
    var subobjectRegionHits = new CadMesh3DSubobjectId[
        CadMesh3DSelectionIndex.MaximumHitCount];
    var subobjectRegionElapsed = new double[queryCount];
    long subobjectRegionVisitedNodeCount = 0;
    long subobjectRegionTestedTriangleCount = 0;
    long subobjectRegionIntersectedTriangleCount = 0;
    long subobjectRegionHitCount = 0;
    int subobjectRegionMaximumVisitedNodeCount = 0;
    int subobjectRegionMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DSubobjectRegionQueryResult warm =
            selectionIndex.QuerySubobjectRegion(
                viewport,
                viewportSize,
                queryPoints[index] - new Vector2(4.0f),
                queryPoints[index] + new Vector2(4.0f),
                CadBoundsSelectionMode.Crossing,
                CadMesh3DSubobjectFilter.Face,
                subobjectRegionScratch,
                subobjectRegionHits);
        if (warm.SubobjectTotalCount == 0 || warm.AreSubobjectsTruncated)
        {
            throw new InvalidOperationException(
                "A warm modern-MESH face-subobject region benchmark query did not return a bounded hit.");
        }
    }
    long subobjectRegionAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DSubobjectRegionQueryResult result =
            selectionIndex.QuerySubobjectRegion(
                viewport,
                viewportSize,
                queryPoints[index] - new Vector2(4.0f),
                queryPoints[index] + new Vector2(4.0f),
                CadBoundsSelectionMode.Crossing,
                CadMesh3DSubobjectFilter.Face,
                subobjectRegionScratch,
                subobjectRegionHits);
        subobjectRegionElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.SubobjectTotalCount == 0 || result.AreSubobjectsTruncated)
        {
            throw new InvalidOperationException(
                "A measured modern-MESH face-subobject region benchmark query did not return a bounded hit.");
        }
        subobjectRegionVisitedNodeCount += result.VisitedNodeCount;
        subobjectRegionTestedTriangleCount += result.TestedTriangleCount;
        subobjectRegionIntersectedTriangleCount +=
            result.IntersectedTriangleCount;
        subobjectRegionHitCount += result.SubobjectTotalCount;
        subobjectRegionMaximumVisitedNodeCount = Math.Max(
            subobjectRegionMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        subobjectRegionMaximumTestedTriangleCount = Math.Max(
            subobjectRegionMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= subobjectRegionHits[0].Handle +
            (uint)subobjectRegionHits[result.SubobjectWrittenCount - 1].Index;
    }
    long subobjectRegionAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() -
        subobjectRegionAllocationStart;
    GC.KeepAlive(checksum);

    var subobjectLassoElapsed = new double[queryCount];
    long subobjectLassoVisitedNodeCount = 0;
    long subobjectLassoTestedTriangleCount = 0;
    long subobjectLassoIntersectedTriangleCount = 0;
    long subobjectLassoHitCount = 0;
    int subobjectLassoMaximumVisitedNodeCount = 0;
    int subobjectLassoMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DSubobjectRegionQueryResult warm =
            selectionIndex.QuerySubobjectLasso(
                viewport,
                viewportSize,
                lassoPoints.AsSpan(index * 3, 3),
                CadBoundsSelectionMode.Crossing,
                CadMesh3DSubobjectFilter.Face,
                subobjectRegionScratch,
                subobjectRegionHits);
        if (warm.SubobjectTotalCount == 0 || warm.AreSubobjectsTruncated)
        {
            throw new InvalidOperationException(
                "A warm modern-MESH face-subobject lasso benchmark query did not return a bounded hit.");
        }
    }
    long subobjectLassoAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DSubobjectRegionQueryResult result =
            selectionIndex.QuerySubobjectLasso(
                viewport,
                viewportSize,
                lassoPoints.AsSpan(index * 3, 3),
                CadBoundsSelectionMode.Crossing,
                CadMesh3DSubobjectFilter.Face,
                subobjectRegionScratch,
                subobjectRegionHits);
        subobjectLassoElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.SubobjectTotalCount == 0 || result.AreSubobjectsTruncated)
        {
            throw new InvalidOperationException(
                "A measured modern-MESH face-subobject lasso benchmark query did not return a bounded hit.");
        }
        subobjectLassoVisitedNodeCount += result.VisitedNodeCount;
        subobjectLassoTestedTriangleCount += result.TestedTriangleCount;
        subobjectLassoIntersectedTriangleCount +=
            result.IntersectedTriangleCount;
        subobjectLassoHitCount += result.SubobjectTotalCount;
        subobjectLassoMaximumVisitedNodeCount = Math.Max(
            subobjectLassoMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        subobjectLassoMaximumTestedTriangleCount = Math.Max(
            subobjectLassoMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= subobjectRegionHits[0].Handle +
            (uint)subobjectRegionHits[result.SubobjectWrittenCount - 1].Index;
    }
    long subobjectLassoAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() -
        subobjectLassoAllocationStart;
    GC.KeepAlive(checksum);

    var subobjectFenceElapsed = new double[queryCount];
    long subobjectFenceVisitedNodeCount = 0;
    long subobjectFenceTestedTriangleCount = 0;
    long subobjectFenceIntersectedTriangleCount = 0;
    long subobjectFenceHitCount = 0;
    int subobjectFenceMaximumVisitedNodeCount = 0;
    int subobjectFenceMaximumTestedTriangleCount = 0;
    for (int index = 0; index < Math.Min(queryCount, 4_096); index++)
    {
        CadMesh3DSubobjectRegionQueryResult warm =
            selectionIndex.QuerySubobjectFence(
                viewport,
                viewportSize,
                fencePoints.AsSpan(index * 2, 2),
                CadMesh3DSubobjectFilter.Face,
                subobjectRegionScratch,
                subobjectRegionHits);
        if (warm.SubobjectTotalCount == 0 || warm.AreSubobjectsTruncated)
        {
            throw new InvalidOperationException(
                "A warm modern-MESH face-subobject fence benchmark query did not return a bounded hit.");
        }
    }
    long subobjectFenceAllocationStart =
        GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < queryPoints.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        CadMesh3DSubobjectRegionQueryResult result =
            selectionIndex.QuerySubobjectFence(
                viewport,
                viewportSize,
                fencePoints.AsSpan(index * 2, 2),
                CadMesh3DSubobjectFilter.Face,
                subobjectRegionScratch,
                subobjectRegionHits);
        subobjectFenceElapsed[index] = Stopwatch.GetElapsedTime(started)
            .TotalNanoseconds;
        if (result.SubobjectTotalCount == 0 || result.AreSubobjectsTruncated)
        {
            throw new InvalidOperationException(
                "A measured modern-MESH face-subobject fence benchmark query did not return a bounded hit.");
        }
        subobjectFenceVisitedNodeCount += result.VisitedNodeCount;
        subobjectFenceTestedTriangleCount += result.TestedTriangleCount;
        subobjectFenceIntersectedTriangleCount +=
            result.IntersectedTriangleCount;
        subobjectFenceHitCount += result.SubobjectTotalCount;
        subobjectFenceMaximumVisitedNodeCount = Math.Max(
            subobjectFenceMaximumVisitedNodeCount,
            result.VisitedNodeCount);
        subobjectFenceMaximumTestedTriangleCount = Math.Max(
            subobjectFenceMaximumTestedTriangleCount,
            result.TestedTriangleCount);
        checksum ^= subobjectRegionHits[0].Handle +
            (uint)subobjectRegionHits[result.SubobjectWrittenCount - 1].Index;
    }
    long subobjectFenceAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() -
        subobjectFenceAllocationStart;
    GC.KeepAlive(checksum);

    var report = new CadMesh3DSelectionBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        Environment.Version.ToString(),
        HashAssembly(Assembly.GetExecutingAssembly()),
        HashAssembly(typeof(CadMesh3DSelectionIndex).Assembly),
        gridSize,
        depthLayerCount,
        expectedTriangleCount,
        warmupCount,
        iterationCount,
        queryCount,
        buildMeasurement,
        Summarize(
            "mesh3d-selection-query-ns",
            elapsed,
            allocatedBytes / queryCount),
        allocatedBytes,
        Summarize(
            "mesh3d-selection-semantic-depth-query-ns",
            semanticElapsed,
            semanticAllocatedBytes / queryCount),
        semanticAllocatedBytes,
        Summarize(
            "mesh3d-selection-projected-pick-target-query-ns",
            apertureElapsed,
            apertureAllocatedBytes / queryCount),
        apertureAllocatedBytes,
        Summarize(
            "mesh3d-selection-modern-mesh-face-subobject-query-ns",
            subobjectElapsed,
            subobjectAllocatedBytes / queryCount),
        subobjectAllocatedBytes,
        Summarize(
            "mesh3d-selection-modern-mesh-face-subobject-region-query-ns",
            subobjectRegionElapsed,
            subobjectRegionAllocatedBytes / queryCount),
        subobjectRegionAllocatedBytes,
        Summarize(
            "mesh3d-selection-modern-mesh-face-subobject-lasso-query-ns",
            subobjectLassoElapsed,
            subobjectLassoAllocatedBytes / queryCount),
        subobjectLassoAllocatedBytes,
        Summarize(
            "mesh3d-selection-modern-mesh-face-subobject-fence-query-ns",
            subobjectFenceElapsed,
            subobjectFenceAllocatedBytes / queryCount),
        subobjectFenceAllocatedBytes,
        Summarize(
            "mesh3d-selection-projected-crossing-query-ns",
            regionElapsed,
            regionAllocatedBytes / queryCount),
        regionAllocatedBytes,
        Summarize(
            "mesh3d-selection-projected-lasso-query-ns",
            lassoElapsed,
            lassoAllocatedBytes / queryCount),
        lassoAllocatedBytes,
        Summarize(
            "mesh3d-selection-projected-fence-query-ns",
            fenceElapsed,
            fenceAllocatedBytes / queryCount),
        fenceAllocatedBytes,
        selectionIndex.Statistics,
        (double)visitedNodeCount / queryCount,
        (double)testedTriangleCount / queryCount,
        maximumVisitedNodeCount,
        maximumTestedTriangleCount,
        (double)semanticVisitedNodeCount / queryCount,
        (double)semanticTestedTriangleCount / queryCount,
        (double)semanticIntersectedTriangleCount / queryCount,
        semanticMaximumVisitedNodeCount,
        semanticMaximumTestedTriangleCount,
        (double)apertureVisitedNodeCount / queryCount,
        (double)apertureTestedTriangleCount / queryCount,
        (double)apertureIntersectedTriangleCount / queryCount,
        (double)apertureHitCount / queryCount,
        apertureMaximumVisitedNodeCount,
        apertureMaximumTestedTriangleCount,
        (double)subobjectVisitedNodeCount / queryCount,
        (double)subobjectTestedTriangleCount / queryCount,
        (double)subobjectIntersectedTriangleCount / queryCount,
        (double)subobjectHitCount / queryCount,
        subobjectMaximumVisitedNodeCount,
        subobjectMaximumTestedTriangleCount,
        (double)subobjectRegionVisitedNodeCount / queryCount,
        (double)subobjectRegionTestedTriangleCount / queryCount,
        (double)subobjectRegionIntersectedTriangleCount / queryCount,
        (double)subobjectRegionHitCount / queryCount,
        subobjectRegionMaximumVisitedNodeCount,
        subobjectRegionMaximumTestedTriangleCount,
        (double)subobjectLassoVisitedNodeCount / queryCount,
        (double)subobjectLassoTestedTriangleCount / queryCount,
        (double)subobjectLassoIntersectedTriangleCount / queryCount,
        (double)subobjectLassoHitCount / queryCount,
        subobjectLassoMaximumVisitedNodeCount,
        subobjectLassoMaximumTestedTriangleCount,
        (double)subobjectFenceVisitedNodeCount / queryCount,
        (double)subobjectFenceTestedTriangleCount / queryCount,
        (double)subobjectFenceIntersectedTriangleCount / queryCount,
        (double)subobjectFenceHitCount / queryCount,
        subobjectFenceMaximumVisitedNodeCount,
        subobjectFenceMaximumTestedTriangleCount,
        (double)regionVisitedNodeCount / queryCount,
        (double)regionTestedTriangleCount / queryCount,
        (double)regionIntersectedTriangleCount / queryCount,
        regionMaximumVisitedNodeCount,
        regionMaximumTestedTriangleCount,
        (double)lassoVisitedNodeCount / queryCount,
        (double)lassoTestedTriangleCount / queryCount,
        (double)lassoIntersectedTriangleCount / queryCount,
        (double)lassoHandleCount / queryCount,
        lassoMaximumVisitedNodeCount,
        lassoMaximumTestedTriangleCount,
        (double)fenceVisitedNodeCount / queryCount,
        (double)fenceTestedTriangleCount / queryCount,
        (double)fenceIntersectedTriangleCount / queryCount,
        (double)fenceHandleCount / queryCount,
        fenceMaximumVisitedNodeCount,
        fenceMaximumTestedTriangleCount,
        checksum);
    string json = JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, json);
    }
}

Mesh CreateMesh3DSelectionGrid(int gridSize, double elevation)
{
    var mesh = new Mesh();
    int stride = checked(gridSize + 1);
    for (int y = 0; y <= gridSize; y++)
    {
        for (int x = 0; x <= gridSize; x++)
        {
            mesh.Vertices.Add(new XYZ(x, y, elevation));
        }
    }
    for (int y = 0; y < gridSize; y++)
    {
        for (int x = 0; x < gridSize; x++)
        {
            int first = checked(y * stride + x);
            mesh.Faces.Add([
                first,
                first + 1,
                first + stride + 1,
                first + stride,
            ]);
        }
    }
    return mesh;
}

Vector2 ProjectMesh3DSelectionPoint(
    CadMesh3DViewport viewport,
    CadRecordedMesh3DScene scene,
    Vector2 viewportSize,
    CadPoint3D worldPoint)
{
    CadPoint3D local = worldPoint - scene.RebaseOrigin;
    CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
    Matrix4x4 matrix = camera.CreateViewMatrix() *
        camera.CreateProjectionMatrix(viewportSize.X / viewportSize.Y);
    Vector4 clip = Vector4.Transform(
        new Vector4(
            (float)local.X,
            (float)local.Y,
            (float)local.Z,
            1.0f),
        matrix);
    if (!float.IsFinite(clip.W) || clip.W == 0.0f)
    {
        throw new InvalidOperationException(
            "The selection benchmark projection is not finite.");
    }
    float inverseW = 1.0f / clip.W;
    return new Vector2(
        (clip.X * inverseW + 1.0f) * 0.5f * viewportSize.X,
        (1.0f - clip.Y * inverseW) * 0.5f * viewportSize.Y);
}

CadMesh3DReplayBinaryHashes CaptureMesh3DReplayBinaryHashes() =>
    new(
        HashAssembly(Assembly.GetExecutingAssembly()),
        HashAssembly(typeof(GpuTexture).Assembly),
        HashAssembly(typeof(CadMesh3DViewCoordinator).Assembly),
        HashAssembly(typeof(Compositor).Assembly),
        HashAssembly(typeof(HeadlessWindow).Assembly),
        HashAssembly(typeof(Viewport3D).Assembly));

string HashAssembly(Assembly assembly) =>
    Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(assembly.Location)))
        .ToLowerInvariant();

Viewport3D CreateMesh3DReplayViewport(int batchCount)
{
    var mesh = new MeshGeometry3D
    {
        Positions =
        [
            new Vector3(-0.45f, -0.35f, 0f),
            new Vector3(0.45f, -0.35f, 0f),
            new Vector3(0f, 0.45f, 0f),
        ],
        Normals =
        [
            -Vector3.UnitZ,
            -Vector3.UnitZ,
            -Vector3.UnitZ,
        ],
        TriangleIndices = [0, 1, 2],
    };
    var material = new DiffuseMaterial
    {
        Color = new Vector4(0.25f, 0.72f, 0.92f, 1f),
        AmbientColor = new Vector3(0.2f),
        SpecularColor = new Vector3(0.15f),
        Shininess = 16f,
    };
    var viewport = new Viewport3D
    {
        EnableRetainedSceneCache = true,
        Camera = new OrthographicCamera
        {
            Position = new Vector3(0f, 0f, -8f),
            LookDirection = Vector3.UnitZ,
            Width = 80f,
        },
        ShadingMode = ShadingMode3D.Flat,
    };
    int columns = (int)Math.Ceiling(Math.Sqrt(batchCount));
    for (int i = 0; i < batchCount; i++)
    {
        float x = (i % columns) - columns * 0.5f;
        float y = (i / columns) - columns * 0.5f;
        viewport.Children.Add(new ModelVisual3D
        {
            Content = new GeometryModel3D
            {
                Geometry = mesh,
                Material = material,
                Transform = Matrix4x4.CreateTranslation(x, y, 0f),
            },
        });
    }
    viewport.InvalidateScene();
    return viewport;
}

void UpdateMesh3DReplayCamera(
    OrthographicCamera camera,
    int frame)
{
    float phase = frame * 0.0075f;
    Vector3 position = new(
        MathF.Sin(phase) * 0.75f,
        MathF.Cos(phase) * 0.5f,
        -8f);
    camera.SetView(position, -position);
}

void ValidateStableMesh3DReplay(
    Mesh3DFrameMetrics metrics,
    int batchCount)
{
    ulong expectedUniformBytes =
        (ulong)Marshal.SizeOf<GpuMesh3DUniforms>();
    if (!metrics.SceneReused ||
        metrics.SceneCompilationCount != 0 ||
        metrics.ModelVisualVisitCount != 0 ||
        metrics.GeometryVertexUploadBytes != 0 ||
        metrics.RecordUploadBytes != 0 ||
        metrics.RecordIndexUploadBytes != 0 ||
        metrics.UniformUploadBytes != expectedUniformBytes ||
        metrics.GeometryResidentCount != 1 ||
        metrics.GeometryBufferResidentBytes == 0 ||
        metrics.ViewportResourceCount != 1 ||
        metrics.ViewportBufferResidentBytes == 0 ||
        metrics.LogicalTargetTextureBytes == 0 ||
        metrics.DrawCallCount != batchCount ||
        metrics.CommandBufferCount != 1 ||
        metrics.QueueSubmissionCount != 1)
    {
        throw new InvalidOperationException(
            $"Stable Mesh3D replay contract failed: {metrics}.");
    }
}

CadMesh3DViewCoordinator CreateCameraBenchmarkCoordinator(int entityCount)
{
    var document = new CadDocument();
    for (int i = 0; i < entityCount; i++)
    {
        double x = (i % 1_000) * 12.0;
        double y = (i / 1_000) * 12.0;
        double z = i % 17;
        document.Entities.Add(new Face3D
        {
            FirstCorner = new XYZ(x, y, z),
            SecondCorner = new XYZ(x + 8.0, y, z),
            ThirdCorner = new XYZ(x, y + 6.0, z + 4.0),
            FourthCorner = new XYZ(x, y + 6.0, z + 4.0),
        });
    }

    var coordinator = new CadMesh3DViewCoordinator();
    coordinator.ReplaceSnapshot(
        new CadSnapshotCompiler().Compile(new CadDocumentSession(document)),
        resetCamera: true);
    return coordinator;
}

Measurement MeasureCameraUpdateBatches(
    string name,
    CadMesh3DViewCoordinator coordinator,
    int updateCount,
    int iterations)
{
    var elapsed = new double[iterations];
    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < iterations; i++)
    {
        long started = Stopwatch.GetTimestamp();
        UpdateCameraBatch(coordinator, updateCount);
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    return Summarize(name, elapsed, allocated / iterations);
}

void UpdateCameraBatch(
    CadMesh3DViewCoordinator coordinator,
    int updateCount)
{
    CadMesh3DProjectionCamera camera = coordinator.Viewport!.Value
        .CreateProjectionCamera();
    Vector3 origin = camera.Position;
    for (int i = 0; i < updateCount; i++)
    {
        coordinator.CaptureCamera(camera with
        {
            Position = origin + new Vector3(
                (i & 31) * 0.125f,
                -(i & 15) * 0.25f,
                (i & 7) * 0.0625f),
        });
    }
}

void ValidateCameraBenchmarkCase(
    CadMesh3DViewStatistics before,
    CadMesh3DViewStatistics after,
    int updateCount,
    int iterations,
    Measurement measurement)
{
    long expectedUpdates = checked((long)updateCount * iterations);
    if (after.SceneCompilationCount != before.SceneCompilationCount ||
        after.SceneReplacementCount != before.SceneReplacementCount ||
        after.CompiledEntityVisitCount != before.CompiledEntityVisitCount ||
        after.CameraUpdateCount - before.CameraUpdateCount != expectedUpdates ||
        after.CameraOnlySceneCompilationCount != 0 ||
        after.CameraOnlyEntityVisitCount != 0 ||
        after.CameraOnlyDrawBatchVisitCount != 0 ||
        after.CameraOnlyUploadByteCount != 0 ||
        measurement.AllocatedBytesPerOperation != 0)
    {
        throw new InvalidOperationException(
            "The camera benchmark observed work outside the allocation-free O(1) contract.");
    }
}

void RunViewportBenchmark(
    int measuredViewportCount,
    int layerVariantCount,
    bool useNonRectangularViewports,
    bool useSplineViewportBoundaries,
    int modelEntityCount,
    int warmups,
    int iterations,
    string? reportPath)
{
    CadDocumentSession viewportSession = CadDocumentSession.CreateNew();
    viewportSession.Edit("Build VIEWPORT benchmark document", document =>
    {
        var layers = new Layer[layerVariantCount];
        for (int i = 0; i < layers.Length; i++)
        {
            layers[i] = new Layer($"VIEWPORT_VARIANT_{i}");
            document.Layers.Add(layers[i]);
        }
        for (int i = 0; i < modelEntityCount; i++)
        {
            double x = (i % 1_000) * 12.0;
            double y = (i / 1_000) * 12.0;
            document.Entities.Add(new Line(
                new XYZ(x, y, 0.0),
                new XYZ(x + 10.0, y + 5.0, 0.0))
            {
                Layer = layers[i % layers.Length],
            });
        }

        Layout layout = document.Layouts[Layout.PaperLayoutName];
        layout.Flags = PlotFlags.DrawViewportsFirst |
            PlotFlags.PrintLineweights |
            PlotFlags.UseStandardScale;
        layout.PaperWidth = 1_000;
        layout.PaperHeight = Math.Max(1_000, measuredViewportCount / 4.0);
        layout.UnprintableMargin = new PaperMargin(5, 5, 5, 5);
        layout.PaperUnits = PlotPaperUnits.Millimeters;
        layout.PlotType = PlotType.LayoutInformation;
        layout.ScaledFit = ScaledType._16;
        layout.StandardScale = 1.0;
        layout.ShadePlotMode = ShadePlotMode.Wireframe;
        layout.StyleSheet = string.Empty;
        layout.UpdatePaperViewport();
        for (int i = 0; i < measuredViewportCount; i++)
        {
            double centerX = 25.0 + ((i % 20) * 48.0);
            double centerY = 20.0 + ((i / 20) * 36.0);
            var viewport = new Viewport
            {
                Center = new XYZ(centerX, centerY, 0.0),
                Width = 44.0,
                Height = 32.0,
                ViewCenter = new XY((i % 100) * 12.0, (i / 100) * 12.0),
                ViewDirection = XYZ.AxisZ,
                ViewHeight = 100.0,
                ActiveStatus = 1,
                RenderMode = RenderMode.Optimized2D,
                ShadePlotMode = ShadePlotMode.Wireframe,
            };
            if (useNonRectangularViewports)
            {
                Entity boundary;
                if (useSplineViewportBoundaries)
                {
                    var spline = new Spline
                    {
                        Degree = 2,
                        IsClosed = true,
                        IsPeriodic = true,
                    };
                    spline.ControlPoints.AddRange([
                        new XYZ(centerX - 22.0, centerY, 0.0),
                        new XYZ(centerX, centerY + 16.0, 0.0),
                        new XYZ(centerX + 22.0, centerY, 0.0),
                        new XYZ(centerX, centerY - 16.0, 0.0),
                    ]);
                    spline.Knots.AddRange([0.0, 1.0, 2.0, 3.0, 4.0]);
                    spline.Weights.AddRange([1.0, 2.0, 1.0, 2.0]);
                    boundary = spline;
                }
                else
                {
                    var polyline = new LwPolyline
                    {
                        Flags = LwPolylineFlags.Closed,
                    };
                    polyline.Vertices.Add(new LwPolyline.Vertex(
                        centerX - 22.0,
                        centerY - 16.0)
                    {
                        Bulge = 0.125,
                    });
                    polyline.Vertices.Add(new LwPolyline.Vertex(
                        centerX + 22.0,
                        centerY - 16.0));
                    polyline.Vertices.Add(new LwPolyline.Vertex(
                        centerX,
                        centerY + 16.0));
                    boundary = polyline;
                }
                layout.AssociatedBlock.Entities.Add(boundary);
                viewport.Boundary = boundary;
                viewport.Status |= ViewportStatusFlags.NonRectangularClipping;
            }
            viewport.FrozenLayers.Add(layers[i % layers.Length]);
            layout.AddViewport(viewport);
        }
    });

    var viewportSnapshotCompiler = new CadLayoutSnapshotCompiler();
    var viewportSceneCompiler = new CadLayoutSceneCompiler();
    var viewportPrintCompiler = new CadLayoutPrintPlanCompiler();
    var viewportPageCatalogCompiler = new CadPageSetupCatalogCompiler();
    var viewportSnapshotOptions = new CadSnapshotOptions
    {
        DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
    };
    var viewportPrintOptions = new CadPageSetupPrintOptionsCompilerOptions
    {
        OutputDpi = 96.0f,
        MaxPagePixelCount = 1_000_000_000,
    };
    CadPageSetupSnapshot viewportPageSetup = viewportPageCatalogCompiler
        .Compile(viewportSession)
        .FindLayout(Layout.PaperLayoutName)!;

    for (int i = 0; i < warmups; i++)
    {
        CadLayoutSnapshot warmSnapshot = viewportSnapshotCompiler.Compile(
            viewportSession,
            Layout.PaperLayoutName,
            viewportSnapshotOptions);
        using CadRecordedLayoutScene warmScene =
            viewportSceneCompiler.Compile(warmSnapshot);
        using CadPrintPlan warmPlan = viewportPrintCompiler.Compile(
            warmSnapshot,
            viewportPageSetup,
            viewportPrintOptions);
    }

    Measurement layoutSnapshotMeasurement = Measure(
        "layout-snapshot",
        iterations,
        () => viewportSnapshotCompiler.Compile(
            viewportSession,
            Layout.PaperLayoutName,
            viewportSnapshotOptions));
    CadLayoutSnapshot retainedSnapshot = viewportSnapshotCompiler.Compile(
        viewportSession,
        Layout.PaperLayoutName,
        viewportSnapshotOptions);
    Measurement layoutSceneMeasurement = Measure(
        "layout-scene",
        iterations,
        () => viewportSceneCompiler.Compile(retainedSnapshot));
    using CadRecordedLayoutScene retainedLayoutScene =
        viewportSceneCompiler.Compile(retainedSnapshot);
    Measurement layoutPrintMeasurement = Measure(
        "layout-print-plan",
        iterations,
        () => viewportPrintCompiler.Compile(
            retainedSnapshot,
            viewportPageSetup,
            viewportPrintOptions));
    Measurement layoutPictureCloneMeasurement = Measure(
        "layout-picture-clone",
        iterations,
        () => retainedLayoutScene.CreatePicture());

    var viewportReport = new
    {
        CapturedAt = DateTimeOffset.UtcNow,
        OperatingSystem = Environment.OSVersion.ToString(),
        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ViewportCount = measuredViewportCount,
        LayerVariantCount = layerVariantCount,
        NonRectangularViewports = useNonRectangularViewports,
        SplineViewportBoundaries = useSplineViewportBoundaries,
        ModelEntityCount = modelEntityCount,
        WarmupCount = warmups,
        IterationCount = iterations,
        ModelSnapshotStatistics = retainedSnapshot.ModelSpace.Statistics,
        PaperSnapshotStatistics = retainedSnapshot.PaperSpace.Statistics,
        LayoutSceneStatistics = retainedLayoutScene.Statistics,
        LayoutSnapshotMilliseconds = layoutSnapshotMeasurement,
        LayoutSceneMilliseconds = layoutSceneMeasurement,
        LayoutPrintPlanMilliseconds = layoutPrintMeasurement,
        LayoutPictureCloneMilliseconds = layoutPictureCloneMeasurement,
        WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
    };
    string viewportJson = JsonSerializer.Serialize(
        viewportReport,
        new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(viewportJson);
    if (reportPath is not null)
    {
        File.WriteAllText(reportPath, viewportJson);
    }
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
        toleranceEntityCount +
        tableEntityCount +
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
        (attributeInsertCount *
            (attributeDisplayMode == AttributeVisibilityMode.None ? 1 : 2)) +
        (dimensionEntityCount * 6) +
        toleranceEntityCount +
        (tableEntityCount * 8) +
        thickSolidEntityCount +
        (meshEntityCount * checked(1 + (6 * Pow4(meshSubdivisionLevel)))) +
        (polygonMeshEntityCount * 13) +
        (polyfaceMeshEntityCount * 7) +
        pointEntityCount +
        constructionLineCount +
        solidHatchCount +
        patternHatchCount);
    int expectedVisible = freezeAlternatingEntityLayers
        ? (entityCount + 1) / 2
        : expectedSource;
    if (freezeAlternatingEntityLayers)
    {
        expectedExpanded = expectedVisible;
    }
    if (source.Statistics.SourceEntityCount == expectedSource &&
        source.Statistics.VisibleEntityCount == expectedVisible &&
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
        $"expected/observed visible {expectedVisible}/{source.Statistics.VisibleEntityCount}, " +
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
    bool useVariableWidthPolylines,
    bool useConstantWidthPolylines,
    int arrayColumns,
    int textCount,
    int mtextCount,
    int shxTextCount,
    int shxMTextCount,
    int attributeCount,
    AttributeVisibilityMode attributeVisibility,
    int dimensionCount,
    int toleranceCount,
    int tableCount,
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
    bool createWipeouts,
    bool useDrawOrder,
    bool freezeAlternatingLayers)
{
    CadDocumentSession result = CadDocumentSession.CreateNew();
    result.Edit("Build benchmark document", document =>
    {
        document.Header.AttributeVisibility = attributeVisibility;
        if (useCompoundPointMarkers)
        {
            document.Header.PointDisplayMode = 98;
            document.Header.PointDisplaySize = -5.0;
        }
        LineType? benchmarkLineType = null;
        Layer? frozenLayer = null;
        if (freezeAlternatingLayers)
        {
            frozenLayer = new Layer("BENCHMARK_FROZEN")
            {
                Flags = LayerFlags.Frozen,
            };
            document.Layers.Add(frozenLayer);
        }
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
            if (createWipeouts)
            {
                var wipeout = new Wipeout
                {
                    InsertPoint = new XYZ(x, y, i % 17),
                    UVector = new XYZ(0.5, 0, 0),
                    VVector = new XYZ(0, 0.5, 0),
                    Size = new XY(20, 20),
                    ClippingState = true,
                    ClipMode = (i & 1) == 0 ? ClipMode.Outside : ClipMode.Inside,
                };
                wipeout.ClipBoundaryVertices.AddRange([
                    new XY(-0.5, -0.5),
                    new XY(19.5, -0.5),
                    new XY(17.5, 19.5),
                    new XY(1.5, 19.5),
                ]);
                document.Entities.Add(wipeout);
                continue;
            }
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

            if (useVariableWidthPolylines || useConstantWidthPolylines)
            {
                var polyline = new LwPolyline
                {
                    ConstantWidth = useConstantWidthPolylines ? 2.5 : 0.0,
                    LineType = benchmarkLineType ?? document.LineTypes.Continuous,
                    Flags = LwPolylineFlags.Plinegen,
                };
                var first = new LwPolyline.Vertex(x, y);
                var second = new LwPolyline.Vertex(x + 6, y);
                if (useVariableWidthPolylines)
                {
                    first.StartWidth = 1.0;
                    first.EndWidth = 3.0;
                    second.StartWidth = 4.0;
                    second.EndWidth = 2.0;
                }
                polyline.Vertices.Add(first);
                polyline.Vertices.Add(second);
                polyline.Vertices.Add(new LwPolyline.Vertex(x + 6, y + 8));
                document.Entities.Add(polyline);
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

        if (frozenLayer is not null)
        {
            int ordinal = 0;
            foreach (Entity entity in document.Entities.Take(count))
            {
                if ((ordinal++ & 1) != 0)
                {
                    entity.Layer = frozenLayer;
                }
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

        if (toleranceCount > 0)
        {
            var textStyle = new TextStyle("INTER_TOLERANCE")
            {
                Filename = "Inter.ttf",
            };
            document.TextStyles.Add(textStyle);
            var dimensionStyle = new DimensionStyle("BENCHMARK_TOLERANCE")
            {
                ScaleFactor = 1.0,
                TextHeight = 2.5,
                DimensionLineGap = 0.5,
                Style = textStyle,
            };
            document.DimensionStyles.Add(dimensionStyle);
            for (int i = 0; i < toleranceCount; i++)
            {
                document.Entities.Add(new Tolerance
                {
                    Style = dimensionStyle,
                    Text = "{\\Fgdt;j}%%v{\\Fgdt;n}0.10{\\Fgdt;m}%%vA",
                    InsertionPoint = new XYZ(
                        (i % 100) * 48.0,
                        (i / 100) * 6.0,
                        0.0),
                    Direction = XYZ.AxisX,
                    Normal = XYZ.AxisZ,
                });
            }
        }

        if (tableCount > 0)
        {
            var textStyle = new TextStyle("INTER_TABLE")
            {
                Filename = "Inter.ttf",
            };
            document.TextStyles.Add(textStyle);
            for (int i = 0; i < tableCount; i++)
            {
                var cache = new BlockRecord($"*T_BENCHMARK_{i}")
                {
                    IsAnonymous = true,
                };
                cache.Entities.Add(new Line(XYZ.Zero, new XYZ(40, 0, 0)));
                cache.Entities.Add(new Line(new XYZ(40, 0, 0), new XYZ(40, 6, 0)));
                cache.Entities.Add(new Line(new XYZ(40, 6, 0), new XYZ(0, 6, 0)));
                cache.Entities.Add(new Line(new XYZ(0, 6, 0), XYZ.Zero));
                cache.Entities.Add(new Line(new XYZ(0, 3, 0), new XYZ(40, 3, 0)));
                cache.Entities.Add(new Solid(
                    new XYZ(0, 3, 0),
                    new XYZ(40, 3, 0),
                    new XYZ(0, 6, 0),
                    new XYZ(40, 6, 0)));
                cache.Entities.Add(new MText($"TABLE {i:D6}")
                {
                    Style = textStyle,
                    InsertPoint = new XYZ(1, 5, 0),
                    Height = 2.0,
                });
                document.BlockRecords.Add(cache);
                document.Entities.Add(new TableEntity(cache)
                {
                    InsertPoint = new XYZ(
                        (i % 100) * 48.0,
                        (i / 100) * 9.0,
                        0.0),
                    HorizontalDirection = XYZ.AxisX,
                    Normal = XYZ.AxisZ,
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
            entity.LineType = benchmarkLineType ?? document.LineTypes.Continuous;
            entity.LineTypeScale = benchmarkLineType is null ? 1.0 : 20.0;
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

AttributeVisibilityMode ReadAttributeDisplayMode()
{
    string? value = ReadString("--attribute-display");
    return value?.ToLowerInvariant() switch
    {
        null or "normal" => AttributeVisibilityMode.Normal,
        "on" or "all" => AttributeVisibilityMode.All,
        "off" or "none" => AttributeVisibilityMode.None,
        _ => throw new ArgumentException(
            "--attribute-display must be normal, on, or off."),
    };
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

internal sealed record CadPolylineAuthoringBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    int SegmentCount,
    int WarmupCount,
    int IterationCount,
    Measurement InheritedWidthMilliseconds,
    Measurement WidthOptionEverySegmentMilliseconds,
    Measurement ExplicitAngleArcMilliseconds,
    Measurement NestedCenterAngleArcMilliseconds,
    Measurement ClockwiseMajorRadiusArcMilliseconds);

internal sealed record CadArcAuthoringBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    int SolveCount,
    int WarmupCount,
    int IterationCount,
    Measurement DefaultPointFinalMilliseconds,
    Measurement ClockwisePointFinalMilliseconds);

internal sealed record CadIsocircleAuthoringBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    int SolveCount,
    int WarmupCount,
    int IterationCount,
    Measurement RadiusMilliseconds,
    Measurement DiameterMilliseconds);

internal sealed record CadCameraUpdateBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    int UpdatesPerBatch,
    int LargeSceneEntityCount,
    int WarmupCount,
    int IterationCount,
    Measurement OneEntityBatchMilliseconds,
    Measurement LargeSceneBatchMilliseconds,
    double LargeToOneEntityP95Ratio,
    CadMesh3DViewStatistics OneEntityStatistics,
    CadMesh3DViewStatistics LargeSceneStatistics);

internal sealed record CadMesh3DReplayBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    string BinarySha256,
    CadMesh3DReplayBinaryHashes RelevantBinarySha256,
    int BatchCount,
    int WarmupCount,
    int IterationCount,
    Measurement FrameMilliseconds,
    long TotalManagedAllocatedBytes,
    long CameraManagedAllocatedBytes,
    long RenderManagedAllocatedBytes,
    long ValidationManagedAllocatedBytes,
    HeadlessRenderAllocationMetrics RenderAllocationBreakdown,
    ulong TotalUniformUploadBytes,
    Mesh3DFrameMetrics InitialFrame,
    Mesh3DFrameMetrics StableFrame);

internal sealed record CadMesh3DSelectionBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    string BenchmarkBinarySha256,
    string CadBinarySha256,
    int GridSize,
    int DepthLayerCount,
    int TriangleCount,
    int WarmupCount,
    int BuildIterationCount,
    int QueryCount,
    Measurement IndexBuildMilliseconds,
    Measurement QueryNanoseconds,
    long TotalQueryManagedAllocatedBytes,
    Measurement SemanticDepthQueryNanoseconds,
    long TotalSemanticDepthQueryManagedAllocatedBytes,
    Measurement ProjectedPickTargetQueryNanoseconds,
    long TotalProjectedPickTargetQueryManagedAllocatedBytes,
    Measurement ModernMeshFaceSubobjectQueryNanoseconds,
    long TotalModernMeshFaceSubobjectQueryManagedAllocatedBytes,
    Measurement ModernMeshFaceSubobjectRegionQueryNanoseconds,
    long TotalModernMeshFaceSubobjectRegionQueryManagedAllocatedBytes,
    Measurement ModernMeshFaceSubobjectLassoQueryNanoseconds,
    long TotalModernMeshFaceSubobjectLassoQueryManagedAllocatedBytes,
    Measurement ModernMeshFaceSubobjectFenceQueryNanoseconds,
    long TotalModernMeshFaceSubobjectFenceQueryManagedAllocatedBytes,
    Measurement ProjectedCrossingQueryNanoseconds,
    long TotalProjectedCrossingQueryManagedAllocatedBytes,
    Measurement ProjectedLassoQueryNanoseconds,
    long TotalProjectedLassoQueryManagedAllocatedBytes,
    Measurement ProjectedFenceQueryNanoseconds,
    long TotalProjectedFenceQueryManagedAllocatedBytes,
    CadMesh3DSelectionIndexStatistics IndexStatistics,
    double AverageVisitedNodeCount,
    double AverageTestedTriangleCount,
    int MaximumVisitedNodeCount,
    int MaximumTestedTriangleCount,
    double SemanticDepthAverageVisitedNodeCount,
    double SemanticDepthAverageTestedTriangleCount,
    double SemanticDepthAverageIntersectedTriangleCount,
    int SemanticDepthMaximumVisitedNodeCount,
    int SemanticDepthMaximumTestedTriangleCount,
    double ProjectedPickTargetAverageVisitedNodeCount,
    double ProjectedPickTargetAverageTestedTriangleCount,
    double ProjectedPickTargetAverageIntersectedTriangleCount,
    double ProjectedPickTargetAverageHitCount,
    int ProjectedPickTargetMaximumVisitedNodeCount,
    int ProjectedPickTargetMaximumTestedTriangleCount,
    double ModernMeshFaceSubobjectAverageVisitedNodeCount,
    double ModernMeshFaceSubobjectAverageTestedTriangleCount,
    double ModernMeshFaceSubobjectAverageIntersectedTriangleCount,
    double ModernMeshFaceSubobjectAverageHitCount,
    int ModernMeshFaceSubobjectMaximumVisitedNodeCount,
    int ModernMeshFaceSubobjectMaximumTestedTriangleCount,
    double ModernMeshFaceSubobjectRegionAverageVisitedNodeCount,
    double ModernMeshFaceSubobjectRegionAverageTestedTriangleCount,
    double ModernMeshFaceSubobjectRegionAverageIntersectedTriangleCount,
    double ModernMeshFaceSubobjectRegionAverageHitCount,
    int ModernMeshFaceSubobjectRegionMaximumVisitedNodeCount,
    int ModernMeshFaceSubobjectRegionMaximumTestedTriangleCount,
    double ModernMeshFaceSubobjectLassoAverageVisitedNodeCount,
    double ModernMeshFaceSubobjectLassoAverageTestedTriangleCount,
    double ModernMeshFaceSubobjectLassoAverageIntersectedTriangleCount,
    double ModernMeshFaceSubobjectLassoAverageHitCount,
    int ModernMeshFaceSubobjectLassoMaximumVisitedNodeCount,
    int ModernMeshFaceSubobjectLassoMaximumTestedTriangleCount,
    double ModernMeshFaceSubobjectFenceAverageVisitedNodeCount,
    double ModernMeshFaceSubobjectFenceAverageTestedTriangleCount,
    double ModernMeshFaceSubobjectFenceAverageIntersectedTriangleCount,
    double ModernMeshFaceSubobjectFenceAverageHitCount,
    int ModernMeshFaceSubobjectFenceMaximumVisitedNodeCount,
    int ModernMeshFaceSubobjectFenceMaximumTestedTriangleCount,
    double ProjectedCrossingAverageVisitedNodeCount,
    double ProjectedCrossingAverageTestedTriangleCount,
    double ProjectedCrossingAverageIntersectedTriangleCount,
    int ProjectedCrossingMaximumVisitedNodeCount,
    int ProjectedCrossingMaximumTestedTriangleCount,
    double ProjectedLassoAverageVisitedNodeCount,
    double ProjectedLassoAverageTestedTriangleCount,
    double ProjectedLassoAverageIntersectedTriangleCount,
    double ProjectedLassoAverageHandleCount,
    int ProjectedLassoMaximumVisitedNodeCount,
    int ProjectedLassoMaximumTestedTriangleCount,
    double ProjectedFenceAverageVisitedNodeCount,
    double ProjectedFenceAverageTestedTriangleCount,
    double ProjectedFenceAverageIntersectedTriangleCount,
    double ProjectedFenceAverageHandleCount,
    int ProjectedFenceMaximumVisitedNodeCount,
    int ProjectedFenceMaximumTestedTriangleCount,
    ulong Checksum);

internal sealed record CadMesh3DReplayBinaryHashes(
    string Benchmark,
    string Backend,
    string Cad,
    string Scene,
    string TestsHeadless,
    string WinUI);

internal sealed record CadMesh3DSubobjectEditBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    int GridSize,
    int ControlVertexCount,
    int AuthoredFaceCount,
    int SelectedFaceCount,
    int WarmupCount,
    int IterationCount,
    Measurement TranslationSnapshotSceneMilliseconds,
    Measurement RotationSnapshotSceneMilliseconds,
    Measurement ScaleSnapshotSceneMilliseconds,
    Measurement DeletionSnapshotSceneMilliseconds,
    Measurement TranslationUndoRedoMilliseconds,
    Measurement RotationUndoRedoMilliseconds,
    Measurement ScaleUndoRedoMilliseconds,
    Measurement DeletionUndoRedoMilliseconds,
    ulong TranslationFinalContentGeneration,
    ulong RotationFinalContentGeneration,
    ulong ScaleFinalContentGeneration,
    ulong DeletionFinalContentGeneration,
    CadMesh3DReplayBinaryHashes RelevantBinarySha256);

internal sealed record CadMesh3DSmoothingBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    int GridSize,
    int ControlVertexCount,
    int AuthoredFaceCount,
    int SelectedFaceCount,
    int WarmupCount,
    int IterationCount,
    Measurement SmoothMoreSnapshotSceneMilliseconds,
    Measurement CreaseSnapshotSceneMilliseconds,
    Measurement SmoothMoreUndoRedoMilliseconds,
    Measurement CreaseUndoRedoMilliseconds,
    ulong SmoothMoreFinalContentGeneration,
    ulong CreaseFinalContentGeneration,
    CadMesh3DReplayBinaryHashes RelevantBinarySha256);

internal enum CadMesh3DSubobjectTransform
{
    Translate,
    Rotate,
    Scale,
}

internal sealed record CadBenchmarkReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string Runtime,
    int EntityCount,
    bool VariableWidthPolylines,
    bool ConstantWidthPolylines,
    bool AlternatingFrozenLayers,
    bool ResolvedDrawOrder,
    int DrawOrderEditEntityCount,
    int BlockArrayColumnCount,
    int TextEntityCount,
    int MTextEntityCount,
    int ShxTextEntityCount,
    int ShxMTextEntityCount,
    int AttributeInsertCount,
    AttributeVisibilityMode AttributeDisplayMode,
    int DimensionEntityCount,
    int ToleranceEntityCount,
    int TableEntityCount,
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
    bool Wipeouts,
    bool MeasuredRasterOutput,
    int RasterOutputDpi,
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
    Measurement? RasterPdfOutputMilliseconds,
    Measurement? PngOutputMilliseconds,
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
