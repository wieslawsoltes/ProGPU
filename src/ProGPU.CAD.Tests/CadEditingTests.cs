using ACadSharp;
using ACadSharp.Entities;
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
}
