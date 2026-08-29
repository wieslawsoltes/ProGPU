using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadAttributeEditingTests
{
    [Fact]
    public void CatalogSeparatesReferenceAndDefinitionOwnedValues()
    {
        (CadDocument document, Insert insert, _, _) = CreateDocument();
        AttributeEntity variable = insert.Attributes.Single(
            attribute => attribute.Tag == "PART");
        variable.Value = "REFERENCE VALUE";
        variable.MText!.Value = "REFERENCE VALUE";
        var session = new CadDocumentSession(document);

        CadAttributeValueCatalog catalog =
            new CadAttributeValueCatalogCompiler().Compile(
                session,
                insert.Handle);

        Assert.Equal(0UL, catalog.ContentGeneration);
        Assert.Equal(insert.Handle, catalog.InsertHandle);
        Assert.Equal("EDITABLE_BLOCK", catalog.BlockName);
        Assert.Equal(0, catalog.UnsupportedCount);
        CadAttributeValueEntry[] entries = catalog.Entries.ToArray();
        Assert.Equal(3, entries.Length);
        Assert.Equal(
            new CadAttributeValueEntry(
                CadAttributeValueOwner.Definition,
                "LABEL",
                0,
                "CONSTANT VALUE",
                false,
                false),
            entries[0]);
        Assert.Equal(CadAttributeValueOwner.Definition, entries[1].Owner);
        Assert.Equal("NOTES", entries[1].Tag);
        Assert.Equal("CONSTANT MTEXT", entries[1].Value);
        Assert.True(entries[1].IsMultiline);
        Assert.True(entries[1].IsInvisible);
        Assert.Equal(CadAttributeValueOwner.Reference, entries[2].Owner);
        Assert.Equal("PART", entries[2].Tag);
        Assert.Equal("REFERENCE VALUE", entries[2].Value);
        Assert.True(entries[2].IsMultiline);
    }

    [Fact]
    public void ConstantDefinitionEditUpdatesEveryInsertAndRoundTripsUndoRedo()
    {
        (CadDocument document, Insert first, AttributeDefinition label, _) =
            CreateDocument();
        var second = new Insert(first.Block);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        ulong applied = history.Execute(
            new CadSetConstantAttributeDefinitionValueCommand(
                first.Handle,
                "label",
                "UPDATED"));

        Assert.Equal(1UL, applied);
        Assert.Equal("UPDATED", label.Value);
        Assert.Equal(
            "UPDATED",
            Assert.Single(new CadAttributeValueCatalogCompiler()
                .Compile(session, first.Handle)
                .Entries.ToArray(),
                entry => entry.Owner == CadAttributeValueOwner.Definition &&
                    entry.Tag == "LABEL").Value);
        Assert.Equal(
            "UPDATED",
            Assert.Single(new CadAttributeValueCatalogCompiler()
                .Compile(session, second.Handle)
                .Entries.ToArray(),
                entry => entry.Owner == CadAttributeValueOwner.Definition &&
                    entry.Tag == "LABEL").Value);

        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(2UL, undone);
        Assert.Equal("CONSTANT VALUE", label.Value);
        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(3UL, redone);
        Assert.Equal("UPDATED", label.Value);
    }

    [Fact]
    public void ConstantMultilineEditSynchronizesPayloadAndRejectsVariableTarget()
    {
        (CadDocument document, Insert insert, _, AttributeDefinition notes) =
            CreateDocument();
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetConstantAttributeDefinitionValueCommand(
            insert.Handle,
            "NOTES",
            @"NEW\PCONSTANT"));

        Assert.Equal(@"NEW\PCONSTANT", notes.Value);
        Assert.Equal(@"NEW\PCONSTANT", notes.MText.Value);
        Assert.True(history.TryUndo(out _));
        Assert.Equal("CONSTANT SINGLE", notes.Value);
        Assert.Equal("CONSTANT MTEXT", notes.MText.Value);
        ulong generation = session.ContentGeneration;
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetConstantAttributeDefinitionValueCommand(
                insert.Handle,
                "PART",
                "NOT CONSTANT")));
        Assert.Equal(generation, session.ContentGeneration);
    }

    [Fact]
    public void CatalogReportsUnsupportedPayloadAndEnforcesOwnershipBudgets()
    {
        (CadDocument document, Insert insert, _, _) = CreateDocument();
        AttributeDefinition malformed = insert.Block.AttributeDefinitions
            .Single(definition => definition.Tag == "NOTES");
        malformed.MText = null!;
        var session = new CadDocumentSession(document);

        CadAttributeValueCatalog catalog =
            new CadAttributeValueCatalogCompiler().Compile(
                session,
                insert.Handle);

        Assert.Equal(1, catalog.UnsupportedCount);
        Assert.DoesNotContain(
            catalog.Entries.ToArray(),
            entry => entry.Tag == "NOTES");
        Assert.Throws<InvalidDataException>(() =>
            new CadAttributeValueCatalogCompiler().Compile(
                session,
                insert.Handle,
                new CadAttributeValueCatalogOptions { MaxEntries = 1 }));
        Assert.Throws<ArgumentException>(() =>
            new CadSetConstantAttributeDefinitionValueCommand(
                insert.Handle,
                "LABEL",
                new string('x',
                    CadSetConstantAttributeDefinitionValueCommand
                        .MaximumValueCodeUnits + 1)));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task ConstantDefinitionEditSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        (CadDocument document, Insert insert, _, _) = CreateDocument();
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadSetConstantAttributeDefinitionValueCommand(
                insert.Handle,
                "NOTES",
                @"PERSISTED\PVALUE"));
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
            sourceName: $"constant-attribute.{format.ToString().ToLowerInvariant()}");

        loaded.Session.Read(loadedDocument =>
        {
            AttributeDefinition definition = loadedDocument.BlockRecords[
                    "EDITABLE_BLOCK"]
                .AttributeDefinitions
                .Single(candidate => candidate.Tag == "NOTES");
            Assert.Equal(@"PERSISTED\PVALUE", definition.Value);
            Assert.Equal(@"PERSISTED\PVALUE", definition.MText.Value);
            return true;
        });
    }

    private static (
        CadDocument Document,
        Insert Insert,
        AttributeDefinition Label,
        AttributeDefinition Notes) CreateDocument()
    {
        var document = new CadDocument();
        var block = new BlockRecord("EDITABLE_BLOCK");
        var label = new AttributeDefinition
        {
            Tag = "LABEL",
            Value = "CONSTANT VALUE",
            Flags = AttributeFlags.Constant,
        };
        var notes = new AttributeDefinition
        {
            Tag = "NOTES",
            Value = "CONSTANT SINGLE",
            Flags = AttributeFlags.Constant | AttributeFlags.Hidden,
            AttributeType = AttributeType.ConstantMultiLine,
            MText = new MText("CONSTANT MTEXT"),
        };
        var part = new AttributeDefinition
        {
            Tag = "PART",
            Value = "DEFAULT",
            AttributeType = AttributeType.MultiLine,
            MText = new MText("DEFAULT"),
        };
        block.Entities.Add(label);
        block.Entities.Add(notes);
        block.Entities.Add(part);
        var insert = new Insert(block);
        document.Entities.Add(insert);
        return (document, insert, label, notes);
    }
}
