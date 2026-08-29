using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Backend.Native;
using ProGPU.CAD.Native;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadEditingTests
{
    [Fact]
    public void TranslateCommandAdvancesGenerationAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(1, 2, 3), new XYZ(4, 5, 6));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var reasons = new List<string>();
        session.Changed += (_, args) => reasons.Add(args.Reason);

        ulong applied = history.Execute(new CadTranslateEntitiesCommand(
            [line.Handle],
            new CadPoint3D(10, -2, 4),
            "Move line"));

        Assert.Equal(1UL, applied);
        Assert.Equal(new XYZ(11, 0, 7), line.StartPoint);
        Assert.Equal(new XYZ(14, 3, 10), line.EndPoint);
        Assert.Equal(1, history.UndoCount);
        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(2UL, undone);
        Assert.Equal(new XYZ(1, 2, 3), line.StartPoint);
        Assert.Equal(new XYZ(4, 5, 6), line.EndPoint);
        Assert.Equal(1, history.RedoCount);
        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(3UL, redone);
        Assert.Equal(new XYZ(11, 0, 7), line.StartPoint);
        Assert.Equal([
            "Move line",
            "Undo: Move line",
            "Redo: Move line",
        ], reasons);
    }

    [Fact]
    public void RotateCommandNormalizesAxisAndRoundTripsThreeDimensionalGeometry()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(0, 1, 0), new XYZ(0, 2, 1));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadRotateEntitiesCommand(
            [line.Handle],
            new CadPoint3D(2, 0, 0),
            Math.PI / 2.0);

        history.Execute(command);

        Assert.Equal(new CadPoint3D(2, 0, 0), command.Axis);
        Assert.Equal(Math.PI / 2.0, command.Radians);
        AssertPoint(new XYZ(0, 0, 1), line.StartPoint);
        AssertPoint(new XYZ(0, -1, 2), line.EndPoint);

        Assert.True(history.TryUndo(out _));
        AssertPoint(new XYZ(0, 1, 0), line.StartPoint);
        AssertPoint(new XYZ(0, 2, 1), line.EndPoint);

        Assert.True(history.TryRedo(out _));
        AssertPoint(new XYZ(0, 0, 1), line.StartPoint);
        AssertPoint(new XYZ(0, -1, 2), line.EndPoint);
    }

    [Fact]
    public void RotateCommandUsesThreeDimensionalPivotAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(2, 1, 3), new XYZ(1, 2, 4));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadRotateEntitiesCommand(
            [line.Handle],
            new CadPoint3D(0, 0, 5),
            Math.PI / 2.0,
            new CadPoint3D(1, 1, 3));

        history.Execute(command);

        Assert.Equal(new CadPoint3D(1, 1, 3), command.Pivot);
        AssertPoint(new XYZ(1, 2, 3), line.StartPoint);
        AssertPoint(new XYZ(0, 1, 4), line.EndPoint);

        Assert.True(history.TryUndo(out _));
        AssertPoint(new XYZ(2, 1, 3), line.StartPoint);
        AssertPoint(new XYZ(1, 2, 4), line.EndPoint);

        Assert.True(history.TryRedo(out _));
        AssertPoint(new XYZ(1, 2, 3), line.StartPoint);
        AssertPoint(new XYZ(0, 1, 4), line.EndPoint);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, 1.0)]
    [InlineData(double.NaN, 0.0, 0.0, 1.0)]
    [InlineData(1.0, 0.0, 0.0, 0.0)]
    [InlineData(1.0, 0.0, 0.0, double.PositiveInfinity)]
    public void RotateCommandRejectsInvalidAxisOrAngle(
        double x,
        double y,
        double z,
        double radians)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new CadRotateEntitiesCommand(
                [1UL],
                new CadPoint3D(x, y, z),
                radians));
    }

    [Fact]
    public void RotateCommandRejectsNonFinitePivot()
    {
        Assert.Throws<ArgumentException>(() =>
            new CadRotateEntitiesCommand(
                [1UL],
                new CadPoint3D(0, 0, 1),
                Math.PI,
                new CadPoint3D(0, double.PositiveInfinity, 0)));
    }

    [Fact]
    public void ScaleCommandUsesPivotAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(2, 4, 6), new XYZ(4, 8, 10));
        var circle = new Circle(new XYZ(2, 4, 6), 3.0);
        document.Entities.Add(line);
        document.Entities.Add(circle);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadScaleEntitiesCommand(
            [line.Handle, circle.Handle],
            2.5,
            new CadPoint3D(1, 2, 3));

        history.Execute(command);

        Assert.Equal(2.5, command.Factor);
        Assert.Equal(new CadPoint3D(1, 2, 3), command.Origin);
        AssertPoint(new XYZ(3.5, 7, 10.5), line.StartPoint);
        AssertPoint(new XYZ(8.5, 17, 20.5), line.EndPoint);
        AssertPoint(new XYZ(3.5, 7, 10.5), circle.Center);
        Assert.Equal(7.5, circle.Radius, 12);

        Assert.True(history.TryUndo(out _));
        AssertPoint(new XYZ(2, 4, 6), line.StartPoint);
        AssertPoint(new XYZ(4, 8, 10), line.EndPoint);
        AssertPoint(new XYZ(2, 4, 6), circle.Center);
        Assert.Equal(3.0, circle.Radius, 12);

        Assert.True(history.TryRedo(out _));
        AssertPoint(new XYZ(3.5, 7, 10.5), line.StartPoint);
        AssertPoint(new XYZ(8.5, 17, 20.5), line.EndPoint);
        AssertPoint(new XYZ(3.5, 7, 10.5), circle.Center);
        Assert.Equal(7.5, circle.Radius, 12);
    }

    [Fact]
    public void SolidEditsPreserveOcsGeometryAndSignedThicknessThroughHistory()
    {
        var solid = new Solid(
            new XYZ(0, 0, 0),
            new XYZ(2, 0, 0),
            new XYZ(0, 3, 0),
            new XYZ(2, 3, 0))
        {
            Normal = XYZ.AxisY,
            Thickness = 2,
        };
        var document = new CadDocument();
        document.Entities.Add(solid);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadTranslateEntitiesCommand(
            [solid.Handle],
            new CadPoint3D(10, 20, 30)));
        CadDocumentSnapshot translated = new CadSnapshotCompiler().Compile(session);
        Assert.Equal(
            new CadBounds3D(
                new CadPoint3D(8, 20, 30),
                new CadPoint3D(10, 22, 33)),
            translated.Bounds);
        Assert.Equal(new CadPoint3D(0, 2, 0), translated.Faces.Span[0].Extrusion);

        history.Execute(new CadRotateEntitiesCommand(
            [solid.Handle],
            new CadPoint3D(0, 0, 1),
            Math.PI / 2));
        CadDocumentSnapshot rotated = new CadSnapshotCompiler().Compile(session);
        AssertCadPoint(new CadPoint3D(-2, 0, 0), rotated.Faces.Span[0].Extrusion);
        AssertCadBounds(
            new CadBounds3D(
                new CadPoint3D(-22, 8, 30),
                new CadPoint3D(-20, 10, 33)),
            rotated.Bounds);

        history.Execute(new CadScaleEntitiesCommand([solid.Handle], 2));
        CadDocumentSnapshot scaled = new CadSnapshotCompiler().Compile(session);
        AssertCadPoint(new CadPoint3D(-4, 0, 0), scaled.Faces.Span[0].Extrusion);
        Assert.Equal(4.0, solid.Thickness, 12);
        AssertCadBounds(
            new CadBounds3D(
                new CadPoint3D(-44, 16, 60),
                new CadPoint3D(-40, 20, 66)),
            scaled.Bounds);

        Assert.True(history.TryUndo(out _));
        Assert.Equal(2.0, solid.Thickness, 12);
        Assert.True(history.TryUndo(out _));
        AssertCadPoint(
            new CadPoint3D(0, 2, 0),
            new CadSnapshotCompiler().Compile(session).Faces.Span[0].Extrusion);
        Assert.True(history.TryUndo(out _));
        AssertCadBounds(
            new CadBounds3D(
                new CadPoint3D(-2, 0, 0),
                new CadPoint3D(0, 2, 3)),
            new CadSnapshotCompiler().Compile(session).Bounds);

        Assert.True(history.TryRedo(out _));
        Assert.True(history.TryRedo(out _));
        Assert.True(history.TryRedo(out _));
        Assert.Equal(4.0, solid.Thickness, 12);
        AssertCadPoint(
            new CadPoint3D(-4, 0, 0),
            new CadSnapshotCompiler().Compile(session).Faces.Span[0].Extrusion);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.Epsilon)]
    public void ScaleCommandRejectsNonReversibleFactors(double factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadScaleEntitiesCommand([1UL], factor));
    }

    [Fact]
    public void ScaleCommandRejectsNonFiniteOrigin()
    {
        Assert.Throws<ArgumentException>(() =>
            new CadScaleEntitiesCommand(
                [1UL],
                2.0,
                new CadPoint3D(double.NegativeInfinity, 0, 0)));
    }

    [Fact]
    public void VisibilityCommandUpdatesSnapshotAndRestoresPriorValues()
    {
        var document = new CadDocument();
        var first = new Line(XYZ.Zero, XYZ.AxisX);
        var second = new Line(XYZ.AxisY, new XYZ(1, 1, 0)) { IsInvisible = true };
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetEntityVisibilityCommand(
            [first.Handle, second.Handle],
            isInvisible: true));

        Assert.Empty(new CadSnapshotCompiler().Compile(session).Entities.ToArray());
        Assert.True(history.TryUndo(out _));
        Assert.False(first.IsInvisible);
        Assert.True(second.IsInvisible);
        Assert.Single(new CadSnapshotCompiler().Compile(session).Entities.ToArray());
    }

    [Fact]
    public void SolidThicknessCommandPreflightsTypeAndRestoresSignedValues()
    {
        var document = new CadDocument();
        var first = new Solid(
            new XYZ(0, 0, 0),
            new XYZ(4, 0, 0),
            new XYZ(0, 3, 0),
            new XYZ(4, 3, 0))
        {
            Thickness = 0.0,
        };
        var second = new Solid(
            new XYZ(10, 0, 0),
            new XYZ(14, 0, 0),
            new XYZ(10, 3, 0),
            new XYZ(14, 3, 0))
        {
            Thickness = -2.0,
        };
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(first);
        document.Entities.Add(second);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetSolidThicknessCommand(
            [first.Handle, second.Handle],
            3.25));

        Assert.Equal(3.25, first.Thickness, 12);
        Assert.Equal(3.25, second.Thickness, 12);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.Equal(2, applied.Faces.Length);
        Assert.All(
            applied.Faces.ToArray(),
            face => Assert.Equal(3.25, face.Extrusion.Z, 12));
        Assert.Equal(
            24,
            new CadMesh3DSceneCompiler().Compile(applied).Statistics.TriangleCount);
        CadRecordedMesh3DScene managed =
            new CadMesh3DSceneCompiler().Compile(applied);
        var camera = new CadNativeMesh3DCamera(
            System.Numerics.Matrix4x4.Identity,
            System.Numerics.Matrix4x4.Identity,
            new System.Numerics.Vector3(0, 0, 5),
            new NativeImageRect(0, 0, 640, 480));
        CadNativeMesh3DScene native = new CadNativeMesh3DSceneCompiler().Compile(
            managed,
            camera,
            sceneId: 93U);
        Assert.Equal(2, native.DrawBatchCount);
        Assert.Equal(72, native.VertexCount);
        Assert.Equal(72, native.IndexCount);

        Assert.True(history.TryUndo(out _));
        Assert.Equal(0.0, first.Thickness, 12);
        Assert.Equal(-2.0, second.Thickness, 12);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(3.25, first.Thickness, 12);

        ulong generation = session.ContentGeneration;
        Assert.Throws<InvalidOperationException>(() =>
            history.Execute(new CadSetSolidThicknessCommand(
                [first.Handle, line.Handle],
                -4.0)));
        Assert.Equal(generation, session.ContentGeneration);
        Assert.Equal(3.25, first.Thickness, 12);
        Assert.Equal(3.25, second.Thickness, 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SolidThicknessCommandRejectsNonFiniteValues(double thickness)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetSolidThicknessCommand([1UL], thickness));
    }

    [Fact]
    public void SolidThicknessCommandBoundsDistinctEntityCount()
    {
        IEnumerable<ulong> handles = Enumerable
            .Range(1, CadSetSolidThicknessCommand.MaximumEntityCount + 1)
            .Select(static value => (ulong)value);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetSolidThicknessCommand(handles, 1.0));
    }

    [Fact]
    public void ExternalEditInvalidatesHistoryBeforeUndo()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadSetEntityVisibilityCommand([line.Handle], true));

        session.Edit("External edit", _ => line.LineTypeScale = 2.0);

        Assert.False(history.TryUndo(out ulong generation));
        Assert.Equal(session.ContentGeneration, generation);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.True(line.IsInvisible);
    }

    [Fact]
    public void FailedCommandDoesNotAdvanceGenerationOrEnterHistory()
    {
        var session = CadDocumentSession.CreateNew();
        var history = new CadDocumentHistory(session);
        var command = new CadTranslateEntitiesCommand(
            [ulong.MaxValue],
            new CadPoint3D(1, 0, 0));

        Assert.Throws<InvalidOperationException>(() => history.Execute(command));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void NewCommandAfterUndoClearsRedoBranch()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadTranslateEntitiesCommand(
            [line.Handle],
            new CadPoint3D(2, 0, 0)));
        Assert.True(history.TryUndo(out _));

        history.Execute(new CadSetEntityVisibilityCommand([line.Handle], true));

        Assert.Equal(0, history.RedoCount);
        Assert.False(history.TryRedo(out _));
        Assert.Equal(XYZ.Zero, line.StartPoint);
        Assert.True(line.IsInvisible);
    }

    [Fact]
    public void AddCommandRoundTripsEntityOwnershipAndSnapshotContent()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        var history = new CadDocumentHistory(session);
        var line = new Line(new XYZ(2, 3, 0), new XYZ(7, 11, 0));
        var command = new CadAddModelSpaceEntityCommand(line, "Add line");

        history.Execute(command);

        Assert.NotEqual(0UL, command.CurrentHandle);
        Assert.Same(
            session.Read(document => document.ModelSpace),
            line.Owner);
        Assert.Single(new CadSnapshotCompiler().Compile(session).Entities.ToArray());

        Assert.True(history.TryUndo(out _));
        Assert.Equal(0UL, command.CurrentHandle);
        Assert.Null(line.Owner);
        Assert.Empty(new CadSnapshotCompiler().Compile(session).Entities.ToArray());

        Assert.True(history.TryRedo(out _));
        Assert.NotEqual(0UL, command.CurrentHandle);
        Assert.Single(new CadSnapshotCompiler().Compile(session).Entities.ToArray());
    }

    [Fact]
    public void RemoveUndoRestoresPriorCommandsAcrossHandleReassignment()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(1, 2, 0), new XYZ(4, 6, 0));
        document.Entities.Add(line);
        ulong originalHandle = line.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadTranslateEntitiesCommand(
            [originalHandle],
            new CadPoint3D(10, 0, 0)));
        var remove = new CadRemoveModelSpaceEntityCommand(originalHandle, "Delete line");

        history.Execute(remove);

        Assert.Equal(0UL, line.Handle);
        Assert.Empty(new CadSnapshotCompiler().Compile(session).Entities.ToArray());
        Assert.True(history.TryUndo(out _));
        Assert.NotEqual(0UL, remove.CurrentHandle);
        Assert.Single(new CadSnapshotCompiler().Compile(session).Entities.ToArray());
        Assert.True(history.TryUndo(out _));
        Assert.Equal(new XYZ(1, 2, 0), line.StartPoint);
        Assert.Equal(new XYZ(4, 6, 0), line.EndPoint);

        Assert.True(history.TryRedo(out _));
        Assert.Equal(new XYZ(11, 2, 0), line.StartPoint);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(0UL, line.Handle);
        Assert.Empty(new CadSnapshotCompiler().Compile(session).Entities.ToArray());
    }

    [Fact]
    public void CancelledRemovalDoesNotAdvanceGenerationOrHistory()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        document.Entities.OnBeforeRemove += (_, args) => args.Cancel = true;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRemoveModelSpaceEntityCommand(line.Handle)));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Same(document.ModelSpace, line.Owner);
        Assert.NotEqual(0UL, line.Handle);
    }

    [Fact]
    public void MultiRemovePublishesOneGenerationAndRoundTripsTheCompleteSet()
    {
        var document = new CadDocument();
        var first = new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0));
        var retained = new Circle { Center = new XYZ(5, 5, 0), Radius = 2 };
        var second = new Line(new XYZ(0, 2, 0), new XYZ(1, 2, 0));
        document.Entities.Add(first);
        document.Entities.Add(retained);
        document.Entities.Add(second);
        ulong firstHandle = first.Handle;
        ulong secondHandle = second.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadRemoveModelSpaceEntitiesCommand(
            [firstHandle, secondHandle, firstHandle],
            "Delete selection");

        ulong deleted = history.Execute(command);

        Assert.Equal(1UL, deleted);
        Assert.Equal(2, command.EntityCount);
        Assert.Equal([firstHandle, secondHandle], command.InitialHandles.ToArray());
        Assert.Equal(0UL, first.Handle);
        Assert.Equal(0UL, second.Handle);
        Assert.Same(document.ModelSpace, retained.Owner);
        Assert.Single(document.Entities);
        Assert.Single(new CadSnapshotCompiler().Compile(session).Entities.ToArray());
        Assert.Equal(1, history.UndoCount);

        Assert.True(history.TryUndo(out ulong restored));

        Assert.Equal(2UL, restored);
        Assert.Equal(3, document.Entities.Count);
        Assert.NotEqual(0UL, first.Handle);
        Assert.NotEqual(0UL, second.Handle);
        Assert.Same(document.ModelSpace, first.Owner);
        Assert.Same(document.ModelSpace, second.Owner);
        Assert.Equal(3, new CadSnapshotCompiler().Compile(session).Entities.Length);

        Assert.True(history.TryRedo(out ulong redeleted));

        Assert.Equal(3UL, redeleted);
        Assert.Single(document.Entities);
        Assert.Equal(0UL, first.Handle);
        Assert.Equal(0UL, second.Handle);
        Assert.Single(new CadSnapshotCompiler().Compile(session).Entities.ToArray());
    }

    [Fact]
    public void MultiRemovePreflightsMissingAndCancelledEntitiesWithoutPartialMutation()
    {
        var document = new CadDocument();
        var first = new Line(XYZ.Zero, XYZ.AxisX);
        var second = new Line(XYZ.AxisY, XYZ.AxisY + XYZ.AxisX);
        document.Entities.Add(first);
        document.Entities.Add(second);
        ulong firstHandle = first.Handle;
        ulong secondHandle = second.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRemoveModelSpaceEntitiesCommand(
                [firstHandle, ulong.MaxValue],
                "Invalid delete")));
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(2, document.Entities.Count);
        Assert.Equal(firstHandle, first.Handle);
        Assert.Equal(secondHandle, second.Handle);

        document.Entities.OnBeforeRemove += (_, args) =>
        {
            if (ReferenceEquals(args.Item, second))
            {
                args.Cancel = true;
            }
        };

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRemoveModelSpaceEntitiesCommand(
                [firstHandle, secondHandle],
                "Cancelled delete")));
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(2, document.Entities.Count);
        Assert.Equal(firstHandle, first.Handle);
        Assert.Equal(secondHandle, second.Handle);
        Assert.Same(document.ModelSpace, first.Owner);
        Assert.Same(document.ModelSpace, second.Owner);
    }

    [Fact]
    public void MultiRemoveRejectsEmptyZeroAndOverBudgetInputs()
    {
        Assert.Throws<ArgumentException>(() =>
            new CadRemoveModelSpaceEntitiesCommand(Array.Empty<ulong>()));
        Assert.Throws<ArgumentException>(() =>
            new CadRemoveModelSpaceEntitiesCommand([1UL, 0UL]));
        Assert.Throws<ArgumentException>(() =>
            new CadRemoveModelSpaceEntitiesCommand(
                [1UL, 1UL, 2UL],
                maximumEntityCount: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadRemoveModelSpaceEntitiesCommand(
                [1UL],
                maximumEntityCount: 0));
    }

    [Fact]
    public void AddAndRemoveCommandsRejectInvalidInitialOwnership()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);

        Assert.Throws<ArgumentException>(() =>
            new CadAddModelSpaceEntityCommand(line));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadRemoveModelSpaceEntityCommand(0));
    }

    [Fact]
    public void DuplicateCommandClonesOnceAndRoundTripsTranslatedEntity()
    {
        var document = new CadDocument();
        var layer = new Layer("DUPLICATES") { Color = ACadSharp.Color.Red };
        document.Layers.Add(layer);
        var source = new Line(new XYZ(1, 2, 3), new XYZ(4, 5, 6))
        {
            Layer = layer,
            Color = ACadSharp.Color.ByLayer,
            LineWeight = LineWeightType.W50,
        };
        document.Entities.Add(source);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadDuplicateModelSpaceEntityCommand(
            source.Handle,
            new CadPoint3D(10, -2, 4));

        history.Execute(command);

        var duplicate = Assert.IsType<Line>(command.Duplicate);
        Assert.NotSame(source, duplicate);
        Assert.NotEqual(0UL, command.CurrentHandle);
        Assert.Equal(new XYZ(1, 2, 3), source.StartPoint);
        Assert.Equal(new XYZ(11, 0, 7), duplicate.StartPoint);
        Assert.Equal(new XYZ(14, 3, 10), duplicate.EndPoint);
        Assert.Same(layer, duplicate.Layer);
        Assert.Equal(ACadSharp.Color.ByLayer, duplicate.Color);
        Assert.Equal(LineWeightType.W50, duplicate.LineWeight);
        Assert.Equal(2, new CadSnapshotCompiler().Compile(session).Entities.Length);

        Assert.True(history.TryUndo(out _));
        Assert.Equal(0UL, command.CurrentHandle);
        Assert.Null(duplicate.Owner);
        Assert.Single(new CadSnapshotCompiler().Compile(session).Entities.ToArray());

        Assert.True(history.TryRedo(out _));
        Assert.Same(duplicate, command.Duplicate);
        Assert.NotEqual(0UL, command.CurrentHandle);
        Assert.Equal(2, new CadSnapshotCompiler().Compile(session).Entities.Length);
    }

    [Fact]
    public void DuplicateCommandRejectsInvalidHandleOrTranslation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadDuplicateModelSpaceEntityCommand(0));
        Assert.Throws<ArgumentException>(() =>
            new CadDuplicateModelSpaceEntityCommand(
                1,
                new CadPoint3D(0, double.NaN, 0)));
    }

    [Fact]
    public void MissingDuplicateSourceDoesNotAdvanceGenerationOrHistory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadDuplicateModelSpaceEntityCommand(ulong.MaxValue)));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void MultiDuplicatePublishesOneGenerationAndRoundTripsRetainedClones()
    {
        var document = new CadDocument();
        var layer = new Layer("COPIES") { Color = ACadSharp.Color.Green };
        document.Layers.Add(layer);
        var first = new Line(new XYZ(1, 2, 3), new XYZ(4, 5, 6))
        {
            Layer = layer,
            LineWeight = LineWeightType.W50,
        };
        var second = new Circle
        {
            Center = new XYZ(-3, 8, 1),
            Radius = 2,
            Layer = layer,
        };
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadDuplicateModelSpaceEntitiesCommand(
            [first.Handle, second.Handle, first.Handle],
            new CadPoint3D(10, -2, 4),
            "Copy selection");

        history.Execute(command);

        Assert.Equal(1UL, session.ContentGeneration);
        Assert.Equal(2, command.EntityCount);
        Assert.Equal([first.Handle, second.Handle], command.SourceHandles.ToArray());
        Assert.Equal(2, command.CurrentHandles.Span.Length);
        Assert.All(command.CurrentHandles.ToArray(), handle => Assert.NotEqual(0UL, handle));
        Line duplicateLine = Assert.IsType<Line>(command.Duplicates.Span[0]);
        Circle duplicateCircle = Assert.IsType<Circle>(command.Duplicates.Span[1]);
        Assert.Equal(new XYZ(11, 0, 7), duplicateLine.StartPoint);
        Assert.Equal(new XYZ(14, 3, 10), duplicateLine.EndPoint);
        Assert.Equal(new XYZ(7, 6, 5), duplicateCircle.Center);
        Assert.Same(layer, duplicateLine.Layer);
        Assert.Same(layer, duplicateCircle.Layer);
        Assert.Equal(4, document.Entities.Count);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        Assert.Equal(4, snapshot.Entities.Length);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Equal(4, commands.Length);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(commands.Length, native.SourceCommandCount);

        Assert.True(history.TryUndo(out ulong undone));

        Assert.Equal(2UL, undone);
        Assert.All(command.CurrentHandles.ToArray(), handle => Assert.Equal(0UL, handle));
        Assert.All(command.Duplicates.ToArray(), duplicate =>
        {
            Assert.Null(duplicate.Owner);
            Assert.Null(duplicate.Document);
            Assert.Equal(0UL, duplicate.Handle);
        });
        Assert.Equal(2, document.Entities.Count);

        Assert.True(history.TryRedo(out ulong redone));

        Assert.Equal(3UL, redone);
        Assert.Same(duplicateLine, command.Duplicates.Span[0]);
        Assert.Same(duplicateCircle, command.Duplicates.Span[1]);
        Assert.All(command.CurrentHandles.ToArray(), handle => Assert.NotEqual(0UL, handle));
        Assert.Equal(4, document.Entities.Count);
    }

    [Fact]
    public void MultiDuplicatePreflightsSourcesAndBoundsWithoutPartialMutation()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        ulong lineHandle = line.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadDuplicateModelSpaceEntitiesCommand(
                [lineHandle, ulong.MaxValue],
                CadPoint3D.Zero)));
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Single(document.Entities);
        Assert.Equal(lineHandle, line.Handle);

        Assert.Throws<ArgumentException>(() =>
            new CadDuplicateModelSpaceEntitiesCommand(Array.Empty<ulong>()));
        Assert.Throws<ArgumentException>(() =>
            new CadDuplicateModelSpaceEntitiesCommand([lineHandle, 0UL]));
        Assert.Throws<ArgumentException>(() =>
            new CadDuplicateModelSpaceEntitiesCommand(
                [lineHandle, lineHandle + 1],
                maximumEntityCount: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadDuplicateModelSpaceEntitiesCommand(
                [lineHandle],
                maximumEntityCount: 0));
        Assert.Throws<ArgumentException>(() =>
            new CadDuplicateModelSpaceEntitiesCommand(
                [lineHandle],
                new CadPoint3D(double.NaN, 0, 0)));
    }

    [Fact]
    public void LayerCommandRestoresEachPriorLayerAcrossUndoRedo()
    {
        var document = new CadDocument();
        var source = new Layer("SOURCE");
        var target = new Layer("TARGET");
        document.Layers.Add(source);
        document.Layers.Add(target);
        var first = new Line(XYZ.Zero, XYZ.AxisX);
        var second = new Line(XYZ.AxisY, new XYZ(1, 1, 0)) { Layer = source };
        document.Entities.Add(first);
        document.Entities.Add(second);
        Layer firstLayer = first.Layer;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetEntityLayerCommand(
            [first.Handle, second.Handle],
            "target"));

        Assert.Same(target, first.Layer);
        Assert.Same(target, second.Layer);
        Assert.True(history.TryUndo(out _));
        Assert.Same(firstLayer, first.Layer);
        Assert.Same(source, second.Layer);
        Assert.True(history.TryRedo(out _));
        Assert.Same(target, first.Layer);
        Assert.Same(target, second.Layer);
    }

    [Fact]
    public void MissingLayerFailsBeforeEntityMutation()
    {
        var document = new CadDocument();
        var first = new Line(XYZ.Zero, XYZ.AxisX);
        var second = new Line(XYZ.AxisY, new XYZ(1, 1, 0));
        document.Entities.Add(first);
        document.Entities.Add(second);
        Layer original = first.Layer;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetEntityLayerCommand(
                [first.Handle, second.Handle],
                "MISSING")));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Same(original, first.Layer);
        Assert.Same(original, second.Layer);
    }

    [Fact]
    public void LayerVisibilityCommandFiltersSnapshotAndRestoresPriorStates()
    {
        var document = new CadDocument();
        var firstLayer = new Layer("FIRST");
        var secondLayer = new Layer("SECOND") { IsOn = false };
        document.Layers.Add(firstLayer);
        document.Layers.Add(secondLayer);
        var first = new Line(XYZ.Zero, XYZ.AxisX) { Layer = firstLayer };
        var second = new Line(XYZ.AxisY, new XYZ(1, 1, 0)) { Layer = secondLayer };
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetLayerVisibilityCommand(
            ["first", "SECOND", "FIRST"],
            isOn: false));

        Assert.False(firstLayer.IsOn);
        Assert.False(secondLayer.IsOn);
        Assert.Empty(new CadSnapshotCompiler().Compile(session).Entities.ToArray());

        Assert.True(history.TryUndo(out _));
        Assert.True(firstLayer.IsOn);
        Assert.False(secondLayer.IsOn);
        Assert.Single(new CadSnapshotCompiler().Compile(session).Entities.ToArray());

        Assert.True(history.TryRedo(out _));
        Assert.False(firstLayer.IsOn);
        Assert.False(secondLayer.IsOn);
    }

    [Fact]
    public void MissingLayerVisibilityTargetFailsBeforeMutation()
    {
        var document = new CadDocument();
        var layer = new Layer("EXISTING");
        document.Layers.Add(layer);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetLayerVisibilityCommand(
                ["EXISTING", "MISSING"],
                isOn: false)));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.True(layer.IsOn);
    }

    [Fact]
    public void LayerPlotFlagCommandRetainsScreenVisibilityAndRestoresPriorStates()
    {
        var document = new CadDocument();
        var firstLayer = new Layer("PLOT") { PlotFlag = true };
        var secondLayer = new Layer("SCREEN_ONLY") { PlotFlag = false };
        document.Layers.Add(firstLayer);
        document.Layers.Add(secondLayer);
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX) { Layer = firstLayer });
        document.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0)) { Layer = secondLayer });
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetLayerPlotFlagCommand(
            ["plot", "SCREEN_ONLY", "PLOT"],
            plotFlag: false));

        Assert.True(firstLayer.IsOn);
        Assert.True(secondLayer.IsOn);
        Assert.False(firstLayer.PlotFlag);
        Assert.False(secondLayer.PlotFlag);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.Equal(2, applied.Entities.Length);
        Assert.All(applied.Layers.ToArray(), layer => Assert.False(layer.IsPlottable));

        Assert.True(history.TryUndo(out _));
        Assert.True(firstLayer.PlotFlag);
        Assert.False(secondLayer.PlotFlag);
        Assert.True(history.TryRedo(out _));
        Assert.False(firstLayer.PlotFlag);
        Assert.False(secondLayer.PlotFlag);
    }

    [Fact]
    public void MissingLayerPlotTargetFailsBeforeMutation()
    {
        var document = new CadDocument();
        var layer = new Layer("EXISTING") { PlotFlag = true };
        document.Layers.Add(layer);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetLayerPlotFlagCommand(
                ["EXISTING", "MISSING"],
                plotFlag: false)));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.True(layer.PlotFlag);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task LayerVisibilityAndPlotFlagRoundTripThroughAdvertisedFormats(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        var layer = new Layer("PERSISTED_STATE")
        {
            IsOn = false,
            PlotFlag = false,
        };
        document.Layers.Add(layer);
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX) { Layer = layer });
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"layer-state.{format.ToString().ToLowerInvariant()}");

        Layer restored = loaded.Session.Read(source =>
            source.Layers["PERSISTED_STATE"]);
        Assert.False(restored.IsOn);
        Assert.False(restored.PlotFlag);
        Assert.Empty(new CadSnapshotCompiler()
            .Compile(loaded.Session)
            .Entities
            .ToArray());
    }

    [Fact]
    public void LayerColorCommandUpdatesInheritedSnapshotRgbAndRestoresPriorValues()
    {
        var document = new CadDocument();
        var firstLayer = new Layer("FIRST_COLOR") { Color = ACadSharp.Color.Red };
        var secondLayer = new Layer("SECOND_COLOR") { Color = ACadSharp.Color.Green };
        document.Layers.Add(firstLayer);
        document.Layers.Add(secondLayer);
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX)
        {
            Layer = firstLayer,
            Color = ACadSharp.Color.ByLayer,
        });
        document.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0))
        {
            Layer = secondLayer,
            Color = ACadSharp.Color.ByLayer,
        });
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var target = new ACadSharp.Color(12, 34, 56);

        history.Execute(new CadSetLayerColorCommand(
            ["first_color", "SECOND_COLOR", "FIRST_COLOR"],
            target));

        Assert.Equal(target, firstLayer.Color);
        Assert.Equal(target, secondLayer.Color);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.All(applied.Styles.ToArray(), style =>
        {
            Assert.Equal((byte)12, style.Red);
            Assert.Equal((byte)34, style.Green);
            Assert.Equal((byte)56, style.Blue);
        });

        Assert.True(history.TryUndo(out _));
        Assert.Equal(ACadSharp.Color.Red, firstLayer.Color);
        Assert.Equal(ACadSharp.Color.Green, secondLayer.Color);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(target, firstLayer.Color);
        Assert.Equal(target, secondLayer.Color);
    }

    [Fact]
    public void LayerColorCommandRejectsInheritanceAndHeaderSentinels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerColorCommand(["0"], ACadSharp.Color.ByLayer));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerColorCommand(["0"], ACadSharp.Color.ByBlock));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerColorCommand(["0"], ACadSharp.Color.ByEntity));
    }

    [Fact]
    public void LayerLineWeightCommandUpdatesInheritedThicknessAndRestoresValues()
    {
        var document = new CadDocument();
        var weightedLayer = new Layer("WEIGHTED_LAYER")
        {
            LineWeight = LineWeightType.W50,
        };
        var hairlineLayer = new Layer("HAIRLINE_LAYER")
        {
            LineWeight = LineWeightType.W0,
        };
        document.Layers.Add(weightedLayer);
        document.Layers.Add(hairlineLayer);
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX)
        {
            Layer = weightedLayer,
            LineWeight = LineWeightType.ByLayer,
        });
        document.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0))
        {
            Layer = hairlineLayer,
            LineWeight = LineWeightType.ByLayer,
        });
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetLayerLineWeightCommand(
            ["weighted_layer", "HAIRLINE_LAYER"],
            LineWeightType.W100));

        Assert.Equal(LineWeightType.W100, weightedLayer.LineWeight);
        Assert.Equal(LineWeightType.W100, hairlineLayer.LineWeight);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.All(
            applied.Styles.ToArray(),
            style => Assert.Equal(1.0, style.LineWeightMillimeters));

        Assert.True(history.TryUndo(out _));
        Assert.Equal(LineWeightType.W50, weightedLayer.LineWeight);
        Assert.Equal(LineWeightType.W0, hairlineLayer.LineWeight);
        CadDocumentSnapshot reverted = new CadSnapshotCompiler().Compile(session);
        Assert.Contains(reverted.Styles.ToArray(), style => style.LineWeightMillimeters == 0.5);
        Assert.Contains(reverted.Styles.ToArray(), style => style.IsHairline);
    }

    [Fact]
    public void LayerLineWeightCommandRejectsEntitySentinelsAndUndefinedValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerLineWeightCommand(["0"], LineWeightType.ByLayer));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerLineWeightCommand(["0"], LineWeightType.ByBlock));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerLineWeightCommand(["0"], LineWeightType.ByDIPs));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerLineWeightCommand(["0"], (LineWeightType)1234));
    }

    [Fact]
    public void LayerLineTypeCommandUpdatesInheritedSnapshotNameAndRestoresValue()
    {
        var document = new CadDocument();
        var target = new LineType("LAYER_DASH");
        document.LineTypes.Add(target);
        var layer = new Layer("DASH_LAYER")
        {
            LineType = document.LineTypes.Continuous,
        };
        document.Layers.Add(layer);
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX)
        {
            Layer = layer,
            LineType = document.LineTypes.ByLayer,
        });
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetLayerLineTypeCommand(
            ["dash_layer"],
            "layer_dash"));

        Assert.Same(target, layer.LineType);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.Equal("LAYER_DASH", Assert.Single(applied.Styles.ToArray()).LineTypeName);

        Assert.True(history.TryUndo(out _));
        Assert.Same(document.LineTypes.Continuous, layer.LineType);
        Assert.True(history.TryRedo(out _));
        Assert.Same(target, layer.LineType);
    }

    [Fact]
    public void LayerLineTypeCommandRejectsInheritanceSentinels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerLineTypeCommand(["0"], LineType.ByLayerName));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetLayerLineTypeCommand(["0"], LineType.ByBlockName));
    }

    [Fact]
    public void AddLayerCommandRoundTripsTableOwnershipAndHandle()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        var history = new CadDocumentHistory(session);
        var layer = new Layer("NEW_LAYER")
        {
            Color = new ACadSharp.Color(12, 34, 56),
            LineWeight = LineWeightType.W50,
        };
        var command = new CadAddLayerCommand(layer);

        history.Execute(command);

        Assert.NotEqual(0UL, command.CurrentHandle);
        Assert.Same(
            layer,
            session.Read(document => document.Layers["new_layer"]));

        Assert.True(history.TryUndo(out _));
        Assert.Equal(0UL, command.CurrentHandle);
        Assert.Null(layer.Owner);
        Assert.False(session.Read(document => document.Layers.Contains("NEW_LAYER")));

        Assert.True(history.TryRedo(out _));
        Assert.NotEqual(0UL, command.CurrentHandle);
        Assert.Same(
            layer,
            session.Read(document => document.Layers["NEW_LAYER"]));
        Assert.Equal(new ACadSharp.Color(12, 34, 56), layer.Color);
        Assert.Equal(LineWeightType.W50, layer.LineWeight);
    }

    [Fact]
    public void AddLayerCommandRejectsAttachedLayer()
    {
        var document = new CadDocument();
        var layer = new Layer("ATTACHED");
        document.Layers.Add(layer);

        Assert.Throws<ArgumentException>(() => new CadAddLayerCommand(layer));
    }

    [Fact]
    public void LineTypeCommandRestoresInheritanceAndRetainsSnapshotName()
    {
        var document = new CadDocument();
        var target = new LineType("DASHED_EDIT");
        document.LineTypes.Add(target);
        var inherited = new Line(XYZ.Zero, XYZ.AxisX)
        {
            LineType = document.LineTypes.ByLayer,
        };
        var byBlock = new Line(XYZ.AxisY, new XYZ(1, 1, 0))
        {
            LineType = document.LineTypes.ByBlock,
        };
        document.Entities.Add(inherited);
        document.Entities.Add(byBlock);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetEntityLineTypeCommand(
            [inherited.Handle, byBlock.Handle],
            "dashed_edit"));

        Assert.Same(target, inherited.LineType);
        Assert.Same(target, byBlock.LineType);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.All(
            applied.Styles.ToArray(),
            style => Assert.Equal("DASHED_EDIT", style.LineTypeName));

        Assert.True(history.TryUndo(out _));
        Assert.Same(document.LineTypes.ByLayer, inherited.LineType);
        Assert.Same(document.LineTypes.ByBlock, byBlock.LineType);
        Assert.True(history.TryRedo(out _));
        Assert.Same(target, inherited.LineType);
        Assert.Same(target, byBlock.LineType);
    }

    [Fact]
    public void MissingLineTypeFailsBeforeEntityMutation()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        LineType original = line.LineType;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetEntityLineTypeCommand([line.Handle], "MISSING")));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Same(original, line.LineType);
    }

    [Fact]
    public void LineTypeScaleCommandRestoresPriorValuesAndSnapshotState()
    {
        var document = new CadDocument();
        var first = new Line(XYZ.Zero, XYZ.AxisX) { LineTypeScale = 0.5 };
        var second = new Line(XYZ.AxisY, new XYZ(1, 1, 0)) { LineTypeScale = 4.0 };
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetEntityLineTypeScaleCommand(
            [first.Handle, second.Handle],
            2.5));

        Assert.Equal(2.5, first.LineTypeScale);
        Assert.Equal(2.5, second.LineTypeScale);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.All(
            applied.Styles.ToArray(),
            style => Assert.Equal(2.5, style.LineTypeScale));

        Assert.True(history.TryUndo(out _));
        Assert.Equal(0.5, first.LineTypeScale);
        Assert.Equal(4.0, second.LineTypeScale);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(2.5, first.LineTypeScale);
        Assert.Equal(2.5, second.LineTypeScale);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void LineTypeScaleCommandRejectsInvalidValues(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetEntityLineTypeScaleCommand([1UL], value));
    }

    [Fact]
    public void LineWeightCommandRetainsSemanticValuesAndRenderedThickness()
    {
        var document = new CadDocument();
        var layer = new Layer("WEIGHTED") { LineWeight = LineWeightType.W50 };
        document.Layers.Add(layer);
        var hairline = new Line(XYZ.Zero, XYZ.AxisX)
        {
            Layer = layer,
            LineWeight = LineWeightType.W0,
        };
        var inherited = new Line(XYZ.AxisY, new XYZ(1, 1, 0))
        {
            Layer = layer,
            LineWeight = LineWeightType.ByLayer,
        };
        document.Entities.Add(hairline);
        document.Entities.Add(inherited);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetEntityLineWeightCommand(
            [hairline.Handle, inherited.Handle],
            LineWeightType.W100));

        Assert.Equal(LineWeightType.W100, hairline.LineWeight);
        Assert.Equal(LineWeightType.W100, inherited.LineWeight);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.All(
            applied.Styles.ToArray(),
            style => Assert.Equal(1.0, style.LineWeightMillimeters));

        Assert.True(history.TryUndo(out _));
        Assert.Equal(LineWeightType.W0, hairline.LineWeight);
        Assert.Equal(LineWeightType.ByLayer, inherited.LineWeight);
        CadDocumentSnapshot reverted = new CadSnapshotCompiler().Compile(session);
        Assert.Contains(reverted.Styles.ToArray(), style => style.IsHairline);
        Assert.Contains(
            reverted.Styles.ToArray(),
            style => style.LineWeightMillimeters == 0.5 && !style.IsHairline);

        Assert.True(history.TryRedo(out _));
        Assert.Equal(LineWeightType.W100, hairline.LineWeight);
        Assert.Equal(LineWeightType.W100, inherited.LineWeight);
    }

    [Fact]
    public void LineWeightCommandRejectsUndefinedWireValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetEntityLineWeightCommand(
                [1UL],
                LineWeightType.ByDIPs));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetEntityLineWeightCommand(
                [1UL],
                (LineWeightType)1234));
    }

    [Fact]
    public void ColorCommandRestoresInheritanceAndRetainsTrueColorOutput()
    {
        var document = new CadDocument();
        var layer = new Layer("RED") { Color = ACadSharp.Color.Red };
        document.Layers.Add(layer);
        var inherited = new Line(XYZ.Zero, XYZ.AxisX)
        {
            Layer = layer,
            Color = ACadSharp.Color.ByLayer,
        };
        var byBlock = new Line(XYZ.AxisY, new XYZ(1, 1, 0))
        {
            Layer = layer,
            Color = ACadSharp.Color.ByBlock,
        };
        document.Entities.Add(inherited);
        document.Entities.Add(byBlock);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var trueColor = new ACadSharp.Color(12, 34, 56);

        history.Execute(new CadSetEntityColorCommand(
            [inherited.Handle, byBlock.Handle],
            trueColor));

        Assert.Equal(trueColor, inherited.Color);
        Assert.Equal(trueColor, byBlock.Color);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.All(applied.Styles.ToArray(), style =>
        {
            Assert.Equal((byte)12, style.Red);
            Assert.Equal((byte)34, style.Green);
            Assert.Equal((byte)56, style.Blue);
        });

        Assert.True(history.TryUndo(out _));
        Assert.Equal(ACadSharp.Color.ByLayer, inherited.Color);
        Assert.Equal(ACadSharp.Color.ByBlock, byBlock.Color);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(trueColor, inherited.Color);
        Assert.Equal(trueColor, byBlock.Color);
    }

    [Fact]
    public void ColorCommandRejectsNonEntitySentinel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetEntityColorCommand(
                [1UL],
                ACadSharp.Color.ByEntity));
    }

    [Fact]
    public void TransparencyCommandRestoresInheritanceAndRenderedAlpha()
    {
        var document = new CadDocument();
        var inherited = new Line(XYZ.Zero, XYZ.AxisX)
        {
            Transparency = Transparency.ByLayer,
        };
        var byBlock = new Line(XYZ.AxisY, new XYZ(1, 1, 0))
        {
            Transparency = Transparency.ByBlock,
        };
        document.Entities.Add(inherited);
        document.Entities.Add(byBlock);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetEntityTransparencyCommand(
            [inherited.Handle, byBlock.Handle],
            new Transparency(25)));

        Assert.Equal((short)25, inherited.Transparency.Value);
        Assert.Equal((short)25, byBlock.Transparency.Value);
        CadDocumentSnapshot applied = new CadSnapshotCompiler().Compile(session);
        Assert.All(
            applied.Styles.ToArray(),
            style => Assert.Equal((byte)191, style.Alpha));

        Assert.True(history.TryUndo(out _));
        Assert.True(inherited.Transparency.IsByLayer);
        Assert.True(byBlock.Transparency.IsByBlock);
        Assert.True(history.TryRedo(out _));
        Assert.Equal((short)25, inherited.Transparency.Value);
        Assert.Equal((short)25, byBlock.Transparency.Value);
    }

    [Fact]
    public void AttributeValueCommandUpdatesSnapshotAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        var block = new BlockRecord("EDITABLE_ATTRIBUTE");
        block.Entities.Add(new AttributeDefinition
        {
            Tag = "PART",
            Value = "OLD",
        });
        var insert = new Insert(block);
        AttributeEntity attribute = Assert.Single(insert.Attributes);
        document.Entities.Add(insert);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        ulong applied = history.Execute(new CadSetAttributeValueCommand(
            insert.Handle,
            "part",
            "NEW"));

        Assert.Equal(1UL, applied);
        Assert.Equal("NEW", attribute.Value);
        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(2UL, undone);
        Assert.Equal("OLD", attribute.Value);
        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(3UL, redone);
        Assert.Equal("NEW", attribute.Value);
    }

    [Fact]
    public void AttributeValueCommandSynchronizesEmbeddedMTextAndRejectsInvalidTargets()
    {
        var document = new CadDocument();
        var block = new BlockRecord("MULTILINE_ATTRIBUTE");
        block.Entities.Add(new AttributeDefinition
        {
            Tag = "NOTES",
            Value = "OLD-SINGLE",
            AttributeType = AttributeType.MultiLine,
            MText = new MText("OLD-MULTILINE"),
        });
        block.Entities.Add(new AttributeDefinition
        {
            Tag = "FIXED",
            Value = "CONSTANT",
            Flags = AttributeFlags.Constant,
        });
        var insert = new Insert(block);
        AttributeEntity notes = insert.Attributes.Single(attribute => attribute.Tag == "NOTES");
        document.Entities.Add(insert);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetAttributeValueCommand(
            insert.Handle,
            "NOTES",
            @"NEW\PVALUE"));

        Assert.Equal(@"NEW\PVALUE", notes.Value);
        Assert.Equal(@"NEW\PVALUE", notes.MText.Value);
        Assert.True(history.TryUndo(out _));
        Assert.Equal("OLD-SINGLE", notes.Value);
        Assert.Equal("OLD-MULTILINE", notes.MText.Value);

        ulong generation = session.ContentGeneration;
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetAttributeValueCommand(insert.Handle, "MISSING", "VALUE")));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetAttributeValueCommand(insert.Handle, "FIXED", "VALUE")));
        Assert.Equal(generation, session.ContentGeneration);
    }

    private static void AssertPoint(XYZ expected, XYZ actual)
    {
        const double tolerance = 1e-12;
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }

    private static void AssertCadPoint(CadPoint3D expected, CadPoint3D actual)
    {
        const double tolerance = 1e-10;
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }

    private static void AssertCadBounds(CadBounds3D expected, CadBounds3D actual)
    {
        AssertCadPoint(expected.Min, actual.Min);
        AssertCadPoint(expected.Max, actual.Max);
    }
}
