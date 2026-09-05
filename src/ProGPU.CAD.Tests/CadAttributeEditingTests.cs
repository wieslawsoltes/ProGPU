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
        Assert.Equal(4, entries.Length);
        Assert.Equal(
            new CadAttributeValueEntry(
                CadAttributeValueOwner.Definition,
                "LABEL",
                0,
                "CONSTANT VALUE",
                false,
                false,
                false,
                false,
                false,
                "Label prompt"),
            entries[0]);
        Assert.Equal(CadAttributeValueOwner.Definition, entries[1].Owner);
        Assert.Equal("NOTES", entries[1].Tag);
        Assert.Equal("CONSTANT MTEXT", entries[1].Value);
        Assert.True(entries[1].IsMultiline);
        Assert.True(entries[1].IsInvisible);
        Assert.False(entries[1].IsVerifiable);
        Assert.False(entries[1].IsPreset);
        Assert.False(entries[1].IsPositionLocked);
        Assert.Equal("Notes prompt", entries[1].Prompt);
        Assert.Equal(CadAttributeValueOwner.VariableDefinition, entries[2].Owner);
        Assert.Equal("PART", entries[2].Tag);
        Assert.Equal("DEFAULT", entries[2].Value);
        Assert.True(entries[2].IsMultiline);
        Assert.False(entries[2].IsVerifiable);
        Assert.False(entries[2].IsPreset);
        Assert.False(entries[2].IsPositionLocked);
        Assert.Equal("Part prompt", entries[2].Prompt);
        Assert.Equal(CadAttributeValueOwner.Reference, entries[3].Owner);
        Assert.Equal("PART", entries[3].Tag);
        Assert.Equal("REFERENCE VALUE", entries[3].Value);
        Assert.True(entries[3].IsMultiline);
        Assert.Empty(entries[3].Prompt);
    }

    [Fact]
    public void VariableDefaultEditFeedsFutureInsertsWithoutChangingAssignedValues()
    {
        (CadDocument document, Insert first, _, _) = CreateDocument();
        AttributeDefinition part = first.Block.AttributeDefinitions.Single(
            definition => definition.Tag == "PART");
        AttributeEntity firstValue = first.Attributes.Single(
            attribute => attribute.Tag == "PART");
        firstValue.Value = "FIRST ASSIGNED";
        firstValue.MText.Value = "FIRST ASSIGNED";
        var second = new Insert(first.Block);
        AttributeEntity secondValue = second.Attributes.Single(
            attribute => attribute.Tag == "PART");
        secondValue.Value = "SECOND ASSIGNED";
        secondValue.MText.Value = "SECOND ASSIGNED";
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetVariableAttributeDefinitionDefaultCommand(
            first.Handle,
            "part",
            "FUTURE DEFAULT"));

        Assert.Equal("FUTURE DEFAULT", part.Value);
        Assert.Equal("FUTURE DEFAULT", part.MText.Value);
        Assert.Equal("FIRST ASSIGNED", firstValue.Value);
        Assert.Equal("FIRST ASSIGNED", firstValue.MText.Value);
        Assert.Equal("SECOND ASSIGNED", secondValue.Value);
        Assert.Equal("SECOND ASSIGNED", secondValue.MText.Value);
        var future = new Insert(first.Block);
        AttributeEntity futureValue = future.Attributes.Single(
            attribute => attribute.Tag == "PART");
        Assert.Equal("FUTURE DEFAULT", futureValue.Value);
        Assert.Equal("FUTURE DEFAULT", futureValue.MText.Value);

        Assert.True(history.TryUndo(out _));
        Assert.Equal("DEFAULT", part.Value);
        Assert.Equal("DEFAULT", part.MText.Value);
        var afterUndo = new Insert(first.Block);
        Assert.Equal(
            "DEFAULT",
            afterUndo.Attributes.Single(attribute => attribute.Tag == "PART").Value);
        Assert.True(history.TryRedo(out _));
        Assert.Equal("FUTURE DEFAULT", part.Value);
        Assert.Equal("FIRST ASSIGNED", firstValue.Value);
        Assert.Equal("SECOND ASSIGNED", secondValue.Value);
    }

    [Fact]
    public void VariableDefaultUsesDefinitionOccurrenceAndRejectsConstantTarget()
    {
        var document = new CadDocument();
        var block = new BlockRecord("DUPLICATE_ATTRIBUTE_BLOCK");
        var constant = new AttributeDefinition
        {
            Tag = "DUPLICATE",
            Value = "CONSTANT",
            Flags = AttributeFlags.Constant,
        };
        var variable = new AttributeDefinition
        {
            Tag = "DUPLICATE",
            Value = "DEFAULT",
        };
        block.Entities.Add(constant);
        block.Entities.Add(variable);
        var insert = new Insert(block);
        document.Entities.Add(insert);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetVariableAttributeDefinitionDefaultCommand(
            insert.Handle,
            "duplicate",
            "UPDATED",
            occurrence: 1));

        Assert.Equal("CONSTANT", constant.Value);
        Assert.Equal("UPDATED", variable.Value);
        Assert.Contains(
            new CadAttributeValueCatalogCompiler()
                .Compile(session, insert.Handle)
                .Entries.ToArray(),
            entry => entry.Owner == CadAttributeValueOwner.VariableDefinition &&
                entry.Tag == "DUPLICATE" &&
                entry.Occurrence == 1 &&
                entry.Value == "UPDATED");
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetVariableAttributeDefinitionDefaultCommand(
                insert.Handle,
                "DUPLICATE",
                "INVALID",
                occurrence: 0)));
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
    public void DefinitionPromptEditRetainsExactTargetAndAssignedValues()
    {
        (CadDocument document, Insert insert, AttributeDefinition label, _) =
            CreateDocument();
        AttributeDefinition part = insert.Block.AttributeDefinitions.Single(
            definition => definition.Tag == "PART");
        AttributeEntity assigned = insert.Attributes.Single(
            attribute => attribute.Tag == "PART");
        assigned.Value = "ASSIGNED";
        assigned.MText.Value = "ASSIGNED";
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Equal(1UL, history.Execute(
            new CadSetAttributeDefinitionPromptCommand(
                insert.Handle,
                "part",
                "Updated part prompt")));

        Assert.Same(part, insert.Block.AttributeDefinitions.Single(
            definition => definition.Tag == "PART"));
        Assert.Equal("Updated part prompt", part.Prompt);
        Assert.Equal("Label prompt", label.Prompt);
        Assert.Equal("ASSIGNED", assigned.Value);
        Assert.Equal("ASSIGNED", assigned.MText.Value);
        Assert.Equal(
            "Updated part prompt",
            Assert.Single(new CadAttributeValueCatalogCompiler()
                .Compile(session, insert.Handle)
                .Entries.ToArray(),
                entry => entry.Owner == CadAttributeValueOwner.VariableDefinition &&
                    entry.Tag == "PART").Prompt);

        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(2UL, undone);
        Assert.Equal("Part prompt", part.Prompt);
        Assert.Equal("ASSIGNED", assigned.Value);
        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(3UL, redone);
        Assert.Equal("Updated part prompt", part.Prompt);
        Assert.Equal("ASSIGNED", assigned.Value);
    }

    [Fact]
    public void DefinitionPromptUsesOccurrenceAndRejectsLockedInsert()
    {
        (CadDocument document, Insert insert, AttributeDefinition label, _) =
            CreateDocument();
        var duplicate = new AttributeDefinition
        {
            Tag = "LABEL",
            Value = "SECOND",
            Prompt = "Second label prompt",
        };
        insert.Block.Entities.Add(duplicate);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetAttributeDefinitionPromptCommand(
            insert.Handle,
            "label",
            new string('p',
                CadSetAttributeDefinitionPromptCommand.MaximumPromptCodeUnits),
            occurrence: 1));

        Assert.Equal("Label prompt", label.Prompt);
        Assert.Equal(256, duplicate.Prompt.Length);
        Assert.Throws<ArgumentException>(() =>
            new CadSetAttributeDefinitionPromptCommand(
                insert.Handle,
                "LABEL",
                new string('p',
                    CadSetAttributeDefinitionPromptCommand.MaximumPromptCodeUnits + 1)));

        Assert.True(history.TryUndo(out _));
        insert.Layer.Flags |= LayerFlags.Locked;
        ulong generation = session.ContentGeneration;
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetAttributeDefinitionPromptCommand(
                insert.Handle,
                "LABEL",
                "Blocked")));
        Assert.Equal(generation, session.ContentGeneration);
        Assert.Equal("Label prompt", label.Prompt);
    }

    [Fact]
    public void DefinitionTagEditDefersReferenceRenameUntilSynchronization()
    {
        (CadDocument document, Insert insert, _, _) = CreateDocument();
        AttributeDefinition definition = insert.Block.AttributeDefinitions.Single(
            candidate => candidate.Tag == "PART");
        AttributeEntity assigned = insert.Attributes.Single(
            attribute => attribute.Tag == "PART");
        assigned.Value = "ASSIGNED";
        assigned.MText.Value = "ASSIGNED";
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Equal(1UL, history.Execute(
            new CadSetAttributeDefinitionTagCommand(
                insert.Handle,
                "part",
                "item")));

        Assert.Equal("ITEM", definition.Tag);
        Assert.Equal("PART", assigned.Tag);
        Assert.Equal("ASSIGNED", assigned.Value);
        Assert.Same(definition, insert.Block.AttributeDefinitions.Single(
            candidate => candidate.Tag == "ITEM"));

        Assert.Equal(2UL, history.Execute(
            new CadSynchronizeBlockAttributePropertiesCommand(insert.Handle)));
        Assert.Equal("ITEM", assigned.Tag);
        Assert.Equal("ASSIGNED", assigned.Value);

        Assert.True(history.TryUndo(out ulong synchronizationUndone));
        Assert.Equal(3UL, synchronizationUndone);
        Assert.Equal("PART", assigned.Tag);
        Assert.Equal("ASSIGNED", assigned.Value);
        Assert.True(history.TryUndo(out ulong tagUndone));
        Assert.Equal(4UL, tagUndone);
        Assert.Equal("PART", definition.Tag);
        Assert.Same(definition, insert.Block.AttributeDefinitions.Single(
            candidate => candidate.Tag == "PART"));
        Assert.True(history.TryRedo(out ulong tagRedone));
        Assert.Equal(5UL, tagRedone);
        Assert.Equal("ITEM", definition.Tag);
        Assert.Equal("PART", assigned.Tag);
    }

    [Fact]
    public void DefinitionTagUsesDuplicateOccurrenceAndValidatesToken()
    {
        (CadDocument document, Insert insert, AttributeDefinition label, _) =
            CreateDocument();
        var duplicate = new AttributeDefinition
        {
            Tag = "LABEL",
            Value = "SECOND",
        };
        insert.Block.Entities.Add(duplicate);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetAttributeDefinitionTagCommand(
            insert.Handle,
            "label",
            "renamed",
            occurrence: 1));

        Assert.Equal("LABEL", label.Tag);
        Assert.Equal("RENAMED", duplicate.Tag);
        Assert.Throws<ArgumentException>(() =>
            new CadSetAttributeDefinitionTagCommand(
                insert.Handle,
                "LABEL",
                "HAS SPACE"));
        Assert.Throws<ArgumentException>(() =>
            new CadSetAttributeDefinitionTagCommand(
                insert.Handle,
                "LABEL",
                "BANG!"));
        Assert.Throws<ArgumentException>(() =>
            new CadSetAttributeDefinitionTagCommand(
                insert.Handle,
                "LABEL",
                new string('T',
                    CadSetAttributeDefinitionTagCommand.MaximumTagCodeUnits + 1)));

        Assert.True(history.TryUndo(out _));
        insert.Layer.Flags |= LayerFlags.Locked;
        ulong generation = session.ContentGeneration;
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetAttributeDefinitionTagCommand(
                insert.Handle,
                "LABEL",
                "BLOCKED")));
        Assert.Equal(generation, session.ContentGeneration);
        Assert.Equal("LABEL", label.Tag);
    }

    [Fact]
    public void DefinitionModesPreserveOwnershipAndSynchronizeExplicitly()
    {
        (CadDocument document, Insert insert, AttributeDefinition label, _) =
            CreateDocument();
        AttributeDefinition part = insert.Block.AttributeDefinitions.Single(
            candidate => candidate.Tag == "PART");
        AttributeEntity assigned = insert.Attributes.Single(
            attribute => attribute.Tag == "PART");
        assigned.Value = "ASSIGNED";
        assigned.MText.Value = "ASSIGNED";
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetAttributeDefinitionModesCommand(
            insert.Handle,
            "PART",
            isInvisible: true,
            isVerifiable: true,
            isPreset: true,
            isPositionLocked: true));

        Assert.Equal(
            AttributeFlags.Hidden | AttributeFlags.Verify | AttributeFlags.Preset,
            part.Flags);
        Assert.True(part.IsLocked);
        Assert.Equal(AttributeFlags.None, assigned.Flags);
        Assert.False(assigned.IsLocked);
        Assert.Equal("ASSIGNED", assigned.Value);

        history.Execute(new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle));
        Assert.Equal(part.Flags, assigned.Flags);
        Assert.True(assigned.IsLocked);
        Assert.Equal("ASSIGNED", assigned.Value);

        Assert.True(history.TryUndo(out _));
        Assert.Equal(AttributeFlags.None, assigned.Flags);
        Assert.False(assigned.IsLocked);
        Assert.Equal("ASSIGNED", assigned.Value);
        Assert.True(history.TryUndo(out _));
        Assert.Equal(AttributeFlags.None, part.Flags);
        Assert.False(part.IsLocked);

        history.Execute(new CadSetAttributeDefinitionModesCommand(
            insert.Handle,
            "LABEL",
            isInvisible: true,
            isVerifiable: true,
            isPreset: true,
            isPositionLocked: true));
        Assert.Equal(
            AttributeFlags.Constant |
                AttributeFlags.Hidden |
                AttributeFlags.Verify |
                AttributeFlags.Preset,
            label.Flags);
        Assert.True(label.IsLocked);
    }

    [Fact]
    public void DefinitionModesUseOccurrenceAndRejectLockedInsert()
    {
        (CadDocument document, Insert insert, AttributeDefinition label, _) =
            CreateDocument();
        var duplicate = new AttributeDefinition
        {
            Tag = "LABEL",
            Value = "SECOND",
        };
        insert.Block.Entities.Add(duplicate);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetAttributeDefinitionModesCommand(
            insert.Handle,
            "label",
            isInvisible: true,
            isVerifiable: false,
            isPreset: true,
            isPositionLocked: true,
            occurrence: 1));

        Assert.Equal(AttributeFlags.Constant, label.Flags);
        Assert.False(label.IsLocked);
        Assert.Equal(
            AttributeFlags.Hidden | AttributeFlags.Preset,
            duplicate.Flags);
        Assert.True(duplicate.IsLocked);

        Assert.True(history.TryUndo(out _));
        insert.Layer.Flags |= LayerFlags.Locked;
        ulong generation = session.ContentGeneration;
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetAttributeDefinitionModesCommand(
                insert.Handle,
                "LABEL",
                isInvisible: true,
                isVerifiable: true,
                isPreset: true,
                isPositionLocked: true)));
        Assert.Equal(generation, session.ContentGeneration);
        Assert.Equal(AttributeFlags.Constant, label.Flags);
    }

    [Fact]
    public void ConstantModeTransitionsAllInsertOwnershipWithExactUndoRedo()
    {
        (CadDocument document, Insert first, AttributeDefinition label, _) =
            CreateDocument();
        AttributeDefinition part = first.Block.AttributeDefinitions.Single(
            definition => definition.Tag == "PART");
        AttributeEntity firstPart = Assert.Single(first.Attributes);
        firstPart.Value = "FIRST ASSIGNED";
        firstPart.MText.Value = "FIRST ASSIGNED";
        var second = new Insert(first.Block);
        document.Entities.Add(second);
        AttributeEntity secondPart = Assert.Single(second.Attributes);
        secondPart.Value = "SECOND ASSIGNED";
        secondPart.MText.Value = "SECOND ASSIGNED";
        ulong firstPartHandle = firstPart.Handle;
        ulong secondPartHandle = secondPart.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var makeConstant = new CadSetAttributeDefinitionConstantModeCommand(
            first.Handle,
            "PART",
            isConstant: true);

        history.Execute(makeConstant);

        Assert.Equal(AttributeFlags.Constant, part.Flags);
        Assert.Equal(AttributeType.ConstantMultiLine, part.AttributeType);
        Assert.Empty(first.Attributes);
        Assert.Empty(second.Attributes);
        Assert.Equal(2, makeConstant.InsertCount);
        Assert.Equal(0, makeConstant.AttributeCount);
        Assert.Equal(0, makeConstant.AddedAttributeCount);
        Assert.Equal(2, makeConstant.RemovedAttributeCount);
        Assert.Null(document.GetCadObject<AttributeEntity>(firstPartHandle));
        Assert.Null(document.GetCadObject<AttributeEntity>(secondPartHandle));

        Assert.True(history.TryUndo(out _));
        Assert.Equal(AttributeFlags.None, part.Flags);
        Assert.Equal(AttributeType.MultiLine, part.AttributeType);
        Assert.Same(firstPart, Assert.Single(first.Attributes));
        Assert.Same(secondPart, Assert.Single(second.Attributes));
        Assert.Equal("FIRST ASSIGNED", firstPart.MText.Value);
        Assert.Equal("SECOND ASSIGNED", secondPart.MText.Value);
        Assert.Equal(firstPartHandle, firstPart.Handle);
        Assert.Equal(secondPartHandle, secondPart.Handle);

        Assert.True(history.TryRedo(out _));
        Assert.Empty(first.Attributes);
        Assert.Empty(second.Attributes);
        Assert.True(history.TryUndo(out _));

        var makeVariable = new CadSetAttributeDefinitionConstantModeCommand(
            first.Handle,
            "LABEL",
            isConstant: false);
        history.Execute(makeVariable);

        Assert.Equal(AttributeFlags.None, label.Flags);
        Assert.Equal(AttributeType.SingleLine, label.AttributeType);
        Assert.Equal(2, makeVariable.AddedAttributeCount);
        Assert.Equal(0, makeVariable.RemovedAttributeCount);
        AttributeEntity firstLabel = first.Attributes.Single(
            attribute => attribute.Tag == "LABEL");
        AttributeEntity secondLabel = second.Attributes.Single(
            attribute => attribute.Tag == "LABEL");
        Assert.Equal("CONSTANT VALUE", firstLabel.Value);
        Assert.Equal("CONSTANT VALUE", secondLabel.Value);
        ulong firstLabelHandle = firstLabel.Handle;
        ulong secondLabelHandle = secondLabel.Handle;

        Assert.True(history.TryUndo(out _));
        Assert.Equal(AttributeFlags.Constant, label.Flags);
        Assert.DoesNotContain(first.Attributes, attribute => attribute.Tag == "LABEL");
        Assert.DoesNotContain(second.Attributes, attribute => attribute.Tag == "LABEL");
        Assert.True(history.TryRedo(out _));
        Assert.Same(
            firstLabel,
            first.Attributes.Single(attribute => attribute.Tag == "LABEL"));
        Assert.Same(
            secondLabel,
            second.Attributes.Single(attribute => attribute.Tag == "LABEL"));
        Assert.Equal(firstLabelHandle, firstLabel.Handle);
        Assert.Equal(secondLabelHandle, secondLabel.Handle);
    }

    [Fact]
    public void ConstantModeRejectsLockedSiblingBeforeAnyMutation()
    {
        (CadDocument document, Insert first, _, _) = CreateDocument();
        AttributeDefinition part = first.Block.AttributeDefinitions.Single(
            definition => definition.Tag == "PART");
        var lockedLayer = new Layer("LOCKED_CONSTANT_MODE")
        {
            Flags = LayerFlags.Locked,
        };
        document.Layers.Add(lockedLayer);
        var lockedSibling = new Insert(first.Block)
        {
            Layer = lockedLayer,
        };
        document.Entities.Add(lockedSibling);
        AttributeEntity firstPart = Assert.Single(first.Attributes);
        AttributeEntity lockedPart = Assert.Single(lockedSibling.Attributes);
        var session = new CadDocumentSession(document);

        Assert.Throws<InvalidOperationException>(() =>
            new CadDocumentHistory(session).Execute(
                new CadSetAttributeDefinitionConstantModeCommand(
                    first.Handle,
                    "PART",
                    isConstant: true)));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(AttributeFlags.None, part.Flags);
        Assert.Equal(AttributeType.MultiLine, part.AttributeType);
        Assert.Same(firstPart, Assert.Single(first.Attributes));
        Assert.Same(lockedPart, Assert.Single(lockedSibling.Attributes));
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
        Assert.Throws<ArgumentException>(() =>
            new CadSetVariableAttributeDefinitionDefaultCommand(
                insert.Handle,
                "PART",
                new string('x',
                    CadSetVariableAttributeDefinitionDefaultCommand
                        .MaximumValueCodeUnits + 1)));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task DefinitionValueEditsSurviveDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        (CadDocument document, Insert insert, _, _) = CreateDocument();
        AttributeEntity assigned = insert.Attributes.Single(
            attribute => attribute.Tag == "PART");
        assigned.Value = "ASSIGNED VALUE";
        assigned.MText.Value = "ASSIGNED VALUE";
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(
            new CadSetConstantAttributeDefinitionValueCommand(
                insert.Handle,
                "NOTES",
                @"PERSISTED\PVALUE"));
        history.Execute(
            new CadSetVariableAttributeDefinitionDefaultCommand(
                insert.Handle,
                "PART",
                @"FUTURE\PDEFAULT"));
        history.Execute(
            new CadSetAttributeDefinitionPromptCommand(
                insert.Handle,
                "PART",
                "Persisted part prompt"));
        history.Execute(
            new CadSetAttributeDefinitionTagCommand(
                insert.Handle,
                "PART",
                "item"));
        history.Execute(
            new CadSetAttributeDefinitionModesCommand(
                insert.Handle,
                "ITEM",
                isInvisible: false,
                isVerifiable: true,
                isPreset: true,
                isPositionLocked: true));
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
            AttributeDefinition variable = loadedDocument.BlockRecords[
                    "EDITABLE_BLOCK"]
                .AttributeDefinitions
                .Single(candidate => candidate.Tag == "ITEM");
            Assert.Equal(@"FUTURE\PDEFAULT", variable.Value);
            Assert.Equal(@"FUTURE\PDEFAULT", variable.MText.Value);
            Assert.Equal("Persisted part prompt", variable.Prompt);
            Assert.Equal(
                AttributeFlags.Verify | AttributeFlags.Preset,
                variable.Flags);
            Assert.True(variable.IsLocked);
            AttributeEntity existing = loadedDocument.Entities
                .OfType<Insert>()
                .Single()
                .Attributes
                .Single(candidate => candidate.Tag == "PART");
            Assert.Equal("ASSIGNED VALUE", existing.Value);
            Assert.Equal("ASSIGNED VALUE", existing.MText.Value);
            return true;
        });
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task ConstantOwnershipTransitionSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        (CadDocument document, Insert insert, _, _) = CreateDocument();
        AttributeEntity assigned = Assert.Single(insert.Attributes);
        assigned.Value = "ASSIGNED VALUE";
        assigned.MText.Value = "ASSIGNED VALUE";
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadSetAttributeDefinitionConstantModeCommand(
                insert.Handle,
                "PART",
                isConstant: true));
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
            sourceName: $"constant-ownership.{format.ToString().ToLowerInvariant()}");

        loaded.Session.Read(loadedDocument =>
        {
            AttributeDefinition restored = loadedDocument.BlockRecords[
                    "EDITABLE_BLOCK"]
                .AttributeDefinitions
                .Single(definition => definition.Tag == "PART");
            Assert.Equal(AttributeFlags.Constant, restored.Flags);
            Assert.Equal(AttributeType.ConstantMultiLine, restored.AttributeType);
            Assert.Empty(loadedDocument.Entities.OfType<Insert>().Single().Attributes);
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
            Prompt = "Label prompt",
            Flags = AttributeFlags.Constant,
        };
        var notes = new AttributeDefinition
        {
            Tag = "NOTES",
            Value = "CONSTANT SINGLE",
            Prompt = "Notes prompt",
            Flags = AttributeFlags.Constant | AttributeFlags.Hidden,
            AttributeType = AttributeType.ConstantMultiLine,
            MText = new MText("CONSTANT MTEXT"),
        };
        var part = new AttributeDefinition
        {
            Tag = "PART",
            Value = "DEFAULT",
            Prompt = "Part prompt",
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
