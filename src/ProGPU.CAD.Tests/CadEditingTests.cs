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
}
