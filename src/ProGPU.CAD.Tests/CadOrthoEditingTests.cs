using ACadSharp;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadOrthoEditingTests
{
    [Fact]
    public void OrthoModeCommandUsesOneGenerationAndExactUndoRedo()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(
            ACadVersion.AC1032);
        var history = new CadDocumentHistory(session);

        Assert.Equal(1UL, history.Execute(new CadSetOrthoModeCommand(true)));
        Assert.True(session.Read(document => document.Header.OrthoMode));
        Assert.True(new CadSnapshotCompiler().Compile(session)
            .IsOrthoModeEnabled);

        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(2UL, undone);
        Assert.False(session.Read(document => document.Header.OrthoMode));
        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(3UL, redone);
        Assert.True(session.Read(document => document.Header.OrthoMode));

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetOrthoModeCommand(true)));
        Assert.Equal(redone, session.ContentGeneration);
    }

    [Fact]
    public void OrthoModeCommandRejectsInterveningHeaderMutation()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadSetOrthoModeCommand(true));
        document.Header.OrthoMode = false;

        Assert.Throws<InvalidOperationException>(() =>
            history.TryUndo(out _));
        Assert.Equal(1UL, session.ContentGeneration);
        Assert.Equal(1, history.UndoCount);
        Assert.False(document.Header.OrthoMode);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task EditedOrthoModeSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(
            ACadVersion.AC1032);
        new CadDocumentHistory(session).Execute(
            new CadSetOrthoModeCommand(true));
        using var stream = new MemoryStream();
        var store = new CadDocumentStore();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"ortho-mode.{format.ToString().ToLowerInvariant()}");

        Assert.True(loaded.Session.Read(
            document => document.Header.OrthoMode));
        Assert.True(new CadSnapshotCompiler().Compile(loaded.Session)
            .IsOrthoModeEnabled);
    }
}
