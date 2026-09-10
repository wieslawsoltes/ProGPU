using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadDocumentFoundationTests
{
    [Fact]
    public void SuccessfulEditAdvancesOneGenerationAndRaisesOneEvent()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        CadDocumentChangedEventArgs? change = null;
        session.Changed += (_, args) => change = args;

        ulong generation = session.Edit(
            "Add line",
            document => document.Entities.Add(
                new Line(XYZ.Zero, new XYZ(10, 20, 0))));

        Assert.Equal(1UL, generation);
        Assert.Equal(1UL, session.ContentGeneration);
        Assert.Equal(0UL, session.SavedGeneration);
        Assert.True(session.IsDirty);
        Assert.NotNull(change);
        Assert.Equal(1UL, change.Generation);
        Assert.Equal("Add line", change.Reason);
        Assert.Equal(1, session.Read(document => document.Entities.Count));
    }

    [Fact]
    public void FailedEditDoesNotPublishGenerationOrEvent()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        int eventCount = 0;
        session.Changed += (_, _) => eventCount++;

        Assert.Throws<InvalidOperationException>(() =>
            session.Edit("Fail", _ => throw new InvalidOperationException("test")));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.False(session.IsDirty);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task DxfRoundTripPreservesEntityAndCallerOwnedStreams()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit(
            "Add line",
            document => document.Entities.Add(
                new Line(new XYZ(1, 2, 3), new XYZ(4, 5, 6))));

        var store = new CadDocumentStore();
        using var output = new MemoryStream();
        CadSaveResult save = await store.SaveAsync(
            session,
            output,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });

        Assert.True(output.CanWrite);
        Assert.True(output.Length > 0);
        Assert.Equal(1UL, save.SavedGeneration);
        Assert.False(session.IsDirty);

        output.Position = 0;
        CadLoadResult load = await store.LoadAsync(
            output,
            CadDocumentFormat.Auto,
            sourceName: "roundtrip.dxf");

        Assert.True(output.CanRead);
        Assert.Equal(CadDocumentFormat.Dxf, load.Session.SourceFormat);
        Assert.Equal("roundtrip.dxf", load.Session.SourceName);
        Assert.Equal(1, load.Session.Read(document => document.Entities.Count));
        Assert.False(load.Session.IsDirty);
    }

    [Fact]
    public async Task LoadRejectsInputOverConfiguredByteLimit()
    {
        var store = new CadDocumentStore();
        using var source = new MemoryStream(new byte[32]);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.LoadAsync(
                source,
                options: new CadLoadOptions { MaxInputBytes = 16 }));

        Assert.Contains("configured limit", exception.Message);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task UncertifiedSaveRequiresExplicitOptInAndDoesNotMarkClean()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit(
            "Add circle",
            document => document.Entities.Add(new Circle(XYZ.Zero, 5)));
        var store = new CadDocumentStore();
        using var output = new MemoryStream();

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await store.SaveAsync(
                    session,
                    output,
                    CadDocumentFormat.Dxf));

        Assert.Contains("round-trip certification", exception.Message);
        Assert.True(session.IsDirty);
        Assert.Equal(0UL, session.SavedGeneration);
        Assert.Equal(0, output.Length);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task DeferredSaveMarksCleanOnlyAfterDestinationCommit()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit(
            "Add line",
            document => document.Entities.Add(new Line(XYZ.Zero, new XYZ(1, 2, 0))));
        var store = new CadDocumentStore();
        using var output = new MemoryStream();

        CadSaveResult save = await store.SaveAsync(
            session,
            output,
            CadDocumentFormat.Dxf,
            new CadSaveOptions
            {
                AllowUncertifiedWrite = true,
                DeferSavedGenerationCommit = true,
            });

        Assert.True(session.IsDirty);
        Assert.Equal(0UL, session.SavedGeneration);
        Assert.True(save.RequiresSavedGenerationCommit);
        Assert.True(save.CommitSavedGeneration());
        Assert.False(save.RequiresSavedGenerationCommit);
        Assert.False(session.IsDirty);
        Assert.False(save.CommitSavedGeneration());
    }

    [Fact]
    public async Task DeferredSaveKeepsLaterEditsDirtyAfterCommit()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit(
            "Add line",
            document => document.Entities.Add(new Line(XYZ.Zero, new XYZ(1, 2, 0))));
        var store = new CadDocumentStore();
        using var output = new MemoryStream();

        CadSaveResult save = await store.SaveAsync(
            session,
            output,
            CadDocumentFormat.Dxf,
            new CadSaveOptions
            {
                AllowUncertifiedWrite = true,
                DeferSavedGenerationCommit = true,
            });
        session.Edit(
            "Add newer line",
            document => document.Entities.Add(new Line(XYZ.Zero, new XYZ(3, 4, 0))));

        Assert.True(save.CommitSavedGeneration());
        Assert.Equal(1UL, session.SavedGeneration);
        Assert.Equal(2UL, session.ContentGeneration);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void DwgVersionCapabilitiesRejectUnsupportedWriterVersion()
    {
        CadFormatCapabilities unsupported = CadFormatSupport.GetCapabilities(
            CadDocumentFormat.Dwg,
            ACadVersion.AC1021);
        CadFormatCapabilities supported = CadFormatSupport.GetCapabilities(
            CadDocumentFormat.Dwg,
            ACadVersion.AC1032);

        Assert.True(unsupported.CanRead);
        Assert.False(unsupported.CanWrite);
        Assert.True(supported.CanRead);
        Assert.True(supported.CanWrite);
        Assert.False(supported.IsWriteCertified);
    }
}
