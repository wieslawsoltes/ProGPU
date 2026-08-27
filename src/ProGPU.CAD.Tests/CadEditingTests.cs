using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
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

    private static void AssertPoint(XYZ expected, XYZ actual)
    {
        const double tolerance = 1e-12;
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }
}
