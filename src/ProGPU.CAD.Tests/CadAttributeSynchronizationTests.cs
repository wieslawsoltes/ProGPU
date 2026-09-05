using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Tables;
using ACadSharp.XData;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadAttributeSynchronizationTests
{
    [Fact]
    public void SynchronizesEveryRegisteredReferenceAndPreservesAssignedValues()
    {
        var document = new CadDocument();
        var attributeLayer = new Layer("ATTRIBUTE_LAYER");
        var attributeStyle = new TextStyle("ATTRIBUTE_STYLE")
        {
            Filename = "Inter.ttf",
        };
        document.Layers.Add(attributeLayer);
        document.TextStyles.Add(attributeStyle);
        var block = new BlockRecord("SYNC_BLOCK");
        var definition = new AttributeDefinition
        {
            Tag = "PART",
            Value = "DEFAULT",
            InsertPoint = new XYZ(2, 3, 0),
            Height = 1,
        };
        block.Entities.Add(definition);
        Insert first = CreateInsert(block, new XYZ(10, 20, 0), 0.25);
        document.Entities.Add(first);
        Insert second = CreateInsert(
            first.Block,
            new XYZ(-8, 14, 0),
            -0.4,
            xScale: 1.5,
            yScale: 0.75);
        document.Entities.Add(second);
        AttributeEntity firstAttribute = Assert.Single(first.Attributes);
        AttributeEntity secondAttribute = Assert.Single(second.Attributes);
        firstAttribute.Value = "FIRST ASSIGNED";
        secondAttribute.Value = "SECOND ASSIGNED";
        firstAttribute.Height = 0.2;
        secondAttribute.Height = 0.3;
        XYZ firstOriginalPoint = firstAttribute.InsertPoint;
        XYZ secondOriginalPoint = secondAttribute.InsertPoint;

        definition.Layer = attributeLayer;
        definition.Style = attributeStyle;
        definition.Color = new ACadSharp.Color(12, 34, 56);
        definition.LineWeight = LineWeightType.W50;
        definition.LineTypeScale = 2.5;
        definition.Transparency = new Transparency(37);
        definition.IsInvisible = true;
        definition.Thickness = 0.75;
        definition.InsertPoint = new XYZ(6, -4, 2);
        definition.Height = 3.5;
        definition.Rotation = 0.6;
        definition.WidthFactor = 1.4;
        definition.ObliqueAngle = 0.2;
        definition.Mirror = TextMirrorFlag.Backward;
        definition.HorizontalAlignment = TextHorizontalAlignment.Center;
        definition.AlignmentPoint = new XYZ(7, -3, 2);
        definition.VerticalAlignment = TextVerticalAlignmentType.Middle;
        definition.Version = 2;
        definition.Flags = AttributeFlags.Hidden | AttributeFlags.Verify;
        definition.IsLocked = true;
        AttributeEntity expectedFirst = CreateExpected(definition, first);
        AttributeEntity expectedSecond = CreateExpected(definition, second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadSynchronizeBlockAttributePropertiesCommand(
            first.Handle);

        ulong applied = history.Execute(command);

        Assert.Equal(1UL, applied);
        Assert.Equal(2, command.InsertCount);
        Assert.Equal(2, command.AttributeCount);
        Assert.Equal("FIRST ASSIGNED", firstAttribute.Value);
        Assert.Equal("SECOND ASSIGNED", secondAttribute.Value);
        AssertSynchronizedProperties(expectedFirst, firstAttribute);
        AssertSynchronizedProperties(expectedSecond, secondAttribute);

        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(2UL, undone);
        Assert.Equal("FIRST ASSIGNED", firstAttribute.Value);
        Assert.Equal("SECOND ASSIGNED", secondAttribute.Value);
        Assert.Equal(0.2, firstAttribute.Height);
        Assert.Equal(0.3, secondAttribute.Height);
        Assert.Equal(firstOriginalPoint, firstAttribute.InsertPoint);
        Assert.Equal(secondOriginalPoint, secondAttribute.InsertPoint);

        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(3UL, redone);
        AssertSynchronizedProperties(expectedFirst, firstAttribute);
        AssertSynchronizedProperties(expectedSecond, secondAttribute);
        Assert.Equal("FIRST ASSIGNED", firstAttribute.Value);
        Assert.Equal("SECOND ASSIGNED", secondAttribute.Value);
    }

    [Fact]
    public void MultilineSyncPreservesPayloadAndExactUndoRedoIdentity()
    {
        var document = new CadDocument();
        var block = new BlockRecord("MULTILINE_SYNC_BLOCK");
        var definition = new AttributeDefinition
        {
            Tag = "NOTES",
            Value = "DEFAULT SINGLE",
            AttributeType = AttributeType.MultiLine,
            MText = new MText("DEFAULT MULTILINE")
            {
                InsertPoint = new XYZ(3, 4, 0),
                Height = 2,
                RectangleWidth = 12,
            },
        };
        block.Entities.Add(definition);
        Insert insert = CreateInsert(block, new XYZ(5, 7, 0), 0.3);
        document.Entities.Add(insert);
        AttributeEntity attribute = Assert.Single(insert.Attributes);
        attribute.Value = "ASSIGNED SINGLE";
        attribute.MText.Value = "ASSIGNED MULTILINE";
        attribute.MText.Height = 0.25;
        MText originalMText = attribute.MText;
        definition.MText.Height = 4;
        definition.MText.RectangleWidth = 18;
        definition.MText.AlignmentPoint = new XYZ(
            Math.Cos(0.45),
            Math.Sin(0.45),
            0);
        definition.Flags = AttributeFlags.Hidden | AttributeFlags.Preset;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle));

        MText synchronizedMText = attribute.MText;
        Assert.NotSame(originalMText, synchronizedMText);
        Assert.Equal("ASSIGNED SINGLE", attribute.Value);
        Assert.Equal("ASSIGNED MULTILINE", synchronizedMText.Value);
        Assert.Equal(4, synchronizedMText.Height);
        Assert.Equal(18, synchronizedMText.RectangleWidth);
        Assert.Equal(0.45, synchronizedMText.Rotation);
        Assert.Equal(definition.Flags, attribute.Flags);

        Assert.True(history.TryUndo(out _));
        Assert.Same(originalMText, attribute.MText);
        Assert.Equal("ASSIGNED MULTILINE", attribute.MText.Value);
        Assert.Equal(0.25, attribute.MText.Height);
        Assert.True(history.TryRedo(out _));
        Assert.Same(synchronizedMText, attribute.MText);
        Assert.Equal("ASSIGNED MULTILINE", attribute.MText.Value);
    }

    [Fact]
    public void ClearsReferenceSequenceXDataAndRestoresExactPayloads()
    {
        var document = new CadDocument();
        var block = new BlockRecord("XDATA_SYNC_BLOCK");
        var definition = new AttributeDefinition
        {
            Tag = "PART",
            Value = "DEFAULT",
        };
        block.Entities.Add(definition);
        var insert = new Insert(block);
        document.Entities.Add(insert);
        AttributeEntity attribute = Assert.Single(insert.Attributes);
        Seqend seqend = insert.Attributes.Seqend;
        var appId = new AppId("PROGPU_SYNC_TEST");
        document.AppIds.Add(appId);
        ExtendedData insertData = CreateTestXData("INSERT");
        ExtendedData attributeData = CreateTestXData("ATTRIB");
        ExtendedData seqendData = CreateTestXData("SEQEND");
        ExtendedData definitionData = CreateTestXData("ATTDEF");
        ExtendedData blockData = CreateTestXData("BLOCK_RECORD");
        insert.ExtendedData.Add(appId, insertData);
        attribute.ExtendedData.Add(appId, attributeData);
        seqend.ExtendedData.Add(appId, seqendData);
        definition.ExtendedData.Add(appId, definitionData);
        block.ExtendedData.Add(appId, blockData);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle);

        history.Execute(command);

        Assert.Equal(3, command.ClearedExtendedDataEntryCount);
        Assert.Empty(insert.ExtendedData);
        Assert.Empty(attribute.ExtendedData);
        Assert.Empty(seqend.ExtendedData);
        AssertExactXData(definition, appId, definitionData);
        AssertExactXData(block, appId, blockData);

        Assert.True(history.TryUndo(out _));
        AssertExactXData(insert, appId, insertData);
        AssertExactXData(attribute, appId, attributeData);
        AssertExactXData(seqend, appId, seqendData);
        AssertExactXData(definition, appId, definitionData);
        AssertExactXData(block, appId, blockData);

        Assert.True(history.TryRedo(out _));
        Assert.Empty(insert.ExtendedData);
        Assert.Empty(attribute.ExtendedData);
        Assert.Empty(seqend.ExtendedData);
    }

    [Fact]
    public void DuplicateTagsAndOneRenamedDefinitionKeepLogicalValues()
    {
        var document = new CadDocument();
        var block = new BlockRecord("DUPLICATE_SYNC_BLOCK");
        var firstDefinition = new AttributeDefinition
        {
            Tag = "DUPLICATE",
            Value = "FIRST DEFAULT",
        };
        var renamedDefinition = new AttributeDefinition
        {
            Tag = "OLD_TAG",
            Value = "SECOND DEFAULT",
        };
        var secondDuplicateDefinition = new AttributeDefinition
        {
            Tag = "DUPLICATE",
            Value = "THIRD DEFAULT",
        };
        block.Entities.Add(firstDefinition);
        block.Entities.Add(renamedDefinition);
        block.Entities.Add(secondDuplicateDefinition);
        var insert = new Insert(block);
        document.Entities.Add(insert);
        AttributeEntity[] attributes = insert.Attributes.ToArray();
        attributes[0].Value = "FIRST VALUE";
        attributes[1].Value = "RENAMED VALUE";
        attributes[2].Value = "THIRD VALUE";
        renamedDefinition.Tag = "NEW_TAG";
        firstDefinition.Height = 2;
        renamedDefinition.Height = 3;
        secondDuplicateDefinition.Height = 4;
        var session = new CadDocumentSession(document);

        new CadDocumentHistory(session).Execute(
            new CadSynchronizeBlockAttributePropertiesCommand(insert.Handle));

        Assert.Equal("DUPLICATE", attributes[0].Tag);
        Assert.Equal("FIRST VALUE", attributes[0].Value);
        Assert.Equal(2, attributes[0].Height);
        Assert.Equal("NEW_TAG", attributes[1].Tag);
        Assert.Equal("RENAMED VALUE", attributes[1].Value);
        Assert.Equal(3, attributes[1].Height);
        Assert.Equal("DUPLICATE", attributes[2].Tag);
        Assert.Equal("THIRD VALUE", attributes[2].Value);
        Assert.Equal(4, attributes[2].Height);
    }

    [Fact]
    public void ConstantDefinitionsRemoveMalformedReferencesWithExactUndoRedo()
    {
        var document = new CadDocument();
        var block = new BlockRecord("CONSTANT_OWNERSHIP_SYNC_BLOCK");
        var constant = new AttributeDefinition
        {
            Tag = "FIXED",
            Value = "CONSTANT",
            Flags = AttributeFlags.Constant,
        };
        var variable = new AttributeDefinition
        {
            Tag = "PART",
            Value = "DEFAULT",
        };
        block.Entities.Add(constant);
        block.Entities.Add(variable);
        var first = new Insert(block);
        document.Entities.Add(first);
        var second = new Insert(first.Block);
        document.Entities.Add(second);
        AttributeEntity firstVariable = Assert.Single(first.Attributes);
        AttributeEntity secondVariable = Assert.Single(second.Attributes);
        firstVariable.Value = "FIRST";
        secondVariable.Value = "SECOND";
        var firstMalformed = new AttributeEntity(constant);
        var secondMalformed = new AttributeEntity(constant);
        first.Attributes.Add(firstMalformed);
        second.Attributes.Add(secondMalformed);
        ulong firstMalformedHandle = firstMalformed.Handle;
        ulong secondMalformedHandle = secondMalformed.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadSynchronizeBlockAttributePropertiesCommand(
            first.Handle);

        history.Execute(command);

        Assert.Equal(2, command.InsertCount);
        Assert.Equal(2, command.AttributeCount);
        Assert.Equal(0, command.AddedAttributeCount);
        Assert.Equal(2, command.RemovedAttributeCount);
        Assert.Same(firstVariable, Assert.Single(first.Attributes));
        Assert.Same(secondVariable, Assert.Single(second.Attributes));
        Assert.Equal("FIRST", firstVariable.Value);
        Assert.Equal("SECOND", secondVariable.Value);
        Assert.Null(document.GetCadObject<AttributeEntity>(firstMalformedHandle));
        Assert.Null(document.GetCadObject<AttributeEntity>(secondMalformedHandle));

        Assert.True(history.TryUndo(out _));
        Assert.Same(firstMalformed, first.Attributes.ToArray()[1]);
        Assert.Same(secondMalformed, second.Attributes.ToArray()[1]);
        Assert.Equal(firstMalformedHandle, firstMalformed.Handle);
        Assert.Equal(secondMalformedHandle, secondMalformed.Handle);

        Assert.True(history.TryRedo(out _));
        Assert.Same(firstVariable, Assert.Single(first.Attributes));
        Assert.Same(secondVariable, Assert.Single(second.Attributes));
    }

    [Fact]
    public void AddsMissingReferencesAcrossEveryInsertWithDefaultsAndExactUndoRedo()
    {
        var document = new CadDocument();
        var block = new BlockRecord("STRUCTURAL_SYNC_BLOCK");
        var definition = new AttributeDefinition
        {
            Tag = "PART",
            Value = "DEFAULT",
            Height = 1,
        };
        block.Entities.Add(definition);
        var first = new Insert(block);
        document.Entities.Add(first);
        var second = new Insert(first.Block);
        document.Entities.Add(second);
        AttributeEntity firstAttribute = Assert.Single(first.Attributes);
        AttributeEntity secondAttribute = Assert.Single(second.Attributes);
        firstAttribute.Value = "FIRST ASSIGNED";
        secondAttribute.Value = "SECOND ASSIGNED";
        ulong firstHandle = firstAttribute.Handle;
        ulong secondHandle = secondAttribute.Handle;
        var addedDefinition = new AttributeDefinition
        {
            Tag = "SERIAL",
            Value = "SERIAL DEFAULT",
            Height = 2,
        };
        block.Entities.Add(addedDefinition);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadSynchronizeBlockAttributePropertiesCommand(
            first.Handle);

        history.Execute(command);

        Assert.Equal(2, command.InsertCount);
        Assert.Equal(4, command.AttributeCount);
        Assert.Equal(2, command.AddedAttributeCount);
        Assert.Equal(0, command.RemovedAttributeCount);
        AttributeEntity[] firstSynchronized = first.Attributes.ToArray();
        AttributeEntity[] secondSynchronized = second.Attributes.ToArray();
        Assert.Same(firstAttribute, firstSynchronized[0]);
        Assert.Same(secondAttribute, secondSynchronized[0]);
        Assert.Equal(firstHandle, firstAttribute.Handle);
        Assert.Equal(secondHandle, secondAttribute.Handle);
        Assert.Equal("FIRST ASSIGNED", firstAttribute.Value);
        Assert.Equal("SECOND ASSIGNED", secondAttribute.Value);
        Assert.Equal("SERIAL", firstSynchronized[1].Tag);
        Assert.Equal("SERIAL DEFAULT", firstSynchronized[1].Value);
        Assert.Equal("SERIAL DEFAULT", secondSynchronized[1].Value);
        ulong firstAddedHandle = firstSynchronized[1].Handle;
        ulong secondAddedHandle = secondSynchronized[1].Handle;

        Assert.True(history.TryUndo(out _));
        Assert.Same(firstAttribute, Assert.Single(first.Attributes));
        Assert.Same(secondAttribute, Assert.Single(second.Attributes));
        Assert.Null(document.GetCadObject<AttributeEntity>(firstAddedHandle));
        Assert.Null(document.GetCadObject<AttributeEntity>(secondAddedHandle));
        Assert.Equal(firstAddedHandle, firstSynchronized[1].Handle);
        Assert.Equal(secondAddedHandle, secondSynchronized[1].Handle);

        Assert.True(history.TryRedo(out _));
        Assert.Same(firstSynchronized[1], first.Attributes.ToArray()[1]);
        Assert.Same(secondSynchronized[1], second.Attributes.ToArray()[1]);
        Assert.Equal(firstAddedHandle, firstSynchronized[1].Handle);
        Assert.Equal(secondAddedHandle, secondSynchronized[1].Handle);
    }

    [Fact]
    public void RemovingAllDefinitionsLeasesExactReferencesUntilHistoryClear()
    {
        var document = new CadDocument();
        var block = new BlockRecord("EMPTY_STRUCTURAL_SYNC_BLOCK");
        var definition = new AttributeDefinition
        {
            Tag = "OBSOLETE",
            Value = "DEFAULT",
        };
        block.Entities.Add(definition);
        var insert = new Insert(block);
        document.Entities.Add(insert);
        AttributeEntity attribute = Assert.Single(insert.Attributes);
        Seqend seqend = insert.Attributes.Seqend;
        var appId = new AppId("PROGPU_REMOVED_SYNC_TEST");
        document.AppIds.Add(appId);
        ExtendedData attributeData = CreateTestXData("REMOVED_ATTRIB");
        ExtendedData seqendData = CreateTestXData("REMOVED_SEQEND");
        attribute.ExtendedData.Add(appId, attributeData);
        seqend.ExtendedData.Add(appId, seqendData);
        ulong attributeHandle = attribute.Handle;
        ulong seqendHandle = seqend.Handle;
        Assert.True(block.Entities.Remove(definition));
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle);

        history.Execute(command);

        Assert.Equal(0, command.AttributeCount);
        Assert.Equal(0, command.AddedAttributeCount);
        Assert.Equal(1, command.RemovedAttributeCount);
        Assert.Equal(2, command.ClearedExtendedDataEntryCount);
        Assert.Empty(insert.Attributes);
        Assert.Empty(attribute.ExtendedData);
        Assert.Empty(seqend.ExtendedData);
        Assert.Null(document.GetCadObject<AttributeEntity>(attributeHandle));
        Assert.Null(document.GetCadObject<Seqend>(seqendHandle));
        Assert.Same(document, attribute.Document);
        Assert.Same(document, seqend.Document);
        Assert.Equal(attributeHandle, attribute.Handle);
        Assert.Equal(seqendHandle, seqend.Handle);

        Assert.True(history.TryUndo(out _));
        Assert.Same(attribute, Assert.Single(insert.Attributes));
        Assert.Same(seqend, insert.Attributes.Seqend);
        Assert.Equal(attributeHandle, attribute.Handle);
        Assert.Equal(seqendHandle, seqend.Handle);
        AssertExactXData(attribute, appId, attributeData);
        AssertExactXData(seqend, appId, seqendData);

        Assert.True(history.TryRedo(out _));
        Assert.Empty(attribute.ExtendedData);
        Assert.Empty(seqend.ExtendedData);
        history.Clear();
        Assert.Null(attribute.Document);
        Assert.Null(seqend.Document);
        Assert.Equal(0UL, attribute.Handle);
        Assert.Equal(0UL, seqend.Handle);
        document.RestoreHandles();
    }

    [Fact]
    public void HistoryEvictionAndRedoReplacementReleaseInactiveReferenceLeases()
    {
        var document = new CadDocument();
        var block = new BlockRecord("LEASE_LIFETIME_SYNC_BLOCK");
        var obsoleteDefinition = new AttributeDefinition
        {
            Tag = "OBSOLETE",
            Value = "OLD DEFAULT",
        };
        block.Entities.Add(obsoleteDefinition);
        var insert = new Insert(block);
        document.Entities.Add(insert);
        AttributeEntity obsolete = Assert.Single(insert.Attributes);
        Assert.True(block.Entities.Remove(obsoleteDefinition));
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session, capacity: 1);

        history.Execute(new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle));
        Assert.Same(document, obsolete.Document);

        var replacementDefinition = new AttributeDefinition
        {
            Tag = "CURRENT",
            Value = "CURRENT DEFAULT",
        };
        block.Entities.Add(replacementDefinition);
        history.Execute(new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle));
        AttributeEntity firstReplacement = Assert.Single(insert.Attributes);

        Assert.Null(obsolete.Document);
        Assert.Equal(0UL, obsolete.Handle);
        Assert.True(history.TryUndo(out _));
        Assert.Empty(insert.Attributes);
        Assert.Same(document, firstReplacement.Document);

        history.Execute(new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle));

        AttributeEntity secondReplacement = Assert.Single(insert.Attributes);
        Assert.NotSame(firstReplacement, secondReplacement);
        Assert.Null(firstReplacement.Document);
        Assert.Equal(0UL, firstReplacement.Handle);
        Assert.Equal(0, history.RedoCount);
        history.Clear();
        document.RestoreHandles();
    }

    [Fact]
    public void ClearingLaterRedoKeepsSeqendRequiredByOlderUndo()
    {
        var document = new CadDocument();
        var block = new BlockRecord("SHARED_SEQEND_SYNC_BLOCK");
        var originalDefinition = new AttributeDefinition
        {
            Tag = "ORIGINAL",
            Value = "ORIGINAL DEFAULT",
        };
        block.Entities.Add(originalDefinition);
        var insert = new Insert(block);
        document.Entities.Add(insert);
        AttributeEntity original = Assert.Single(insert.Attributes);
        Seqend seqend = insert.Attributes.Seqend;
        ulong originalHandle = original.Handle;
        ulong seqendHandle = seqend.Handle;
        Assert.True(block.Entities.Remove(originalDefinition));
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle));
        block.Entities.Add(new AttributeDefinition
        {
            Tag = "LATER",
            Value = "LATER DEFAULT",
        });
        history.Execute(new CadSynchronizeBlockAttributePropertiesCommand(
            insert.Handle));
        Assert.True(history.TryUndo(out _));
        Assert.Empty(insert.Attributes);

        history.Execute(new CadSetEntityVisibilityCommand(
            new[] { insert.Handle },
            isInvisible: true));

        Assert.Equal(0, history.RedoCount);
        Assert.Same(document, seqend.Document);
        Assert.Equal(seqendHandle, seqend.Handle);
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        Assert.Same(original, Assert.Single(insert.Attributes));
        Assert.Same(seqend, insert.Attributes.Seqend);
        Assert.Equal(originalHandle, original.Handle);
        Assert.Equal(seqendHandle, seqend.Handle);
        Assert.Same(seqend, document.GetCadObject<Seqend>(seqendHandle));
    }

    [Fact]
    public void LockedSiblingReferenceRejectsCompleteBatchBeforeMutation()
    {
        var document = new CadDocument();
        var lockedLayer = new Layer("LOCKED_SYNC_LAYER")
        {
            Flags = LayerFlags.Locked,
        };
        document.Layers.Add(lockedLayer);
        var block = new BlockRecord("LOCKED_SYNC_BLOCK");
        var definition = new AttributeDefinition
        {
            Tag = "PART",
            Value = "DEFAULT",
            Height = 1,
        };
        block.Entities.Add(definition);
        var first = new Insert(block);
        document.Entities.Add(first);
        var lockedSibling = new Insert(first.Block)
        {
            Layer = lockedLayer,
        };
        document.Entities.Add(lockedSibling);
        AttributeEntity firstAttribute = Assert.Single(first.Attributes);
        AttributeEntity lockedAttribute = Assert.Single(lockedSibling.Attributes);
        definition.Height = 5;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSynchronizeBlockAttributePropertiesCommand(first.Handle)));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(1, firstAttribute.Height);
        Assert.Equal(1, lockedAttribute.Height);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task SynchronizedPropertiesAndAssignedValuesRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        var block = new BlockRecord("PERSISTED_SYNC_BLOCK");
        var definition = new AttributeDefinition
        {
            Tag = "PART",
            Value = "DEFAULT",
            InsertPoint = new XYZ(2, 3, 0),
            Height = 1,
        };
        block.Entities.Add(definition);
        Insert insert = CreateInsert(block, new XYZ(10, 20, 0), 0.25);
        document.Entities.Add(insert);
        AttributeEntity attribute = Assert.Single(insert.Attributes);
        attribute.Value = "ASSIGNED";
        attribute.Height = 0.5;
        var appId = new AppId("PROGPU_PERSISTED_SYNC_TEST");
        document.AppIds.Add(appId);
        insert.ExtendedData.Add(appId, CreateTestXData("INSERT"));
        attribute.ExtendedData.Add(appId, CreateTestXData("ATTRIB"));
        var unrelated = new Line(XYZ.Zero, new XYZ(1, 1, 0));
        unrelated.ExtendedData.Add(appId, CreateTestXData("UNRELATED"));
        document.Entities.Add(unrelated);
        definition.Height = 2.75;
        definition.Color = new ACadSharp.Color(12, 34, 56);
        var addedDefinition = new AttributeDefinition
        {
            Tag = "SERIAL",
            Value = "SERIAL DEFAULT",
            Height = 1.5,
        };
        block.Entities.Add(addedDefinition);
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadSynchronizeBlockAttributePropertiesCommand(insert.Handle));
        XYZ synchronizedPoint = attribute.InsertPoint;
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
            sourceName: $"attribute-sync.{format.ToString().ToLowerInvariant()}");

        loaded.Session.Read(loadedDocument =>
        {
            Insert restoredInsert = loadedDocument.Entities
                .OfType<Insert>()
                .Single();
            AttributeEntity[] restored = restoredInsert.Attributes.ToArray();
            Assert.Equal(2, restored.Length);
            Assert.Empty(restoredInsert.ExtendedData);
            Assert.All(restored, item => Assert.Empty(item.ExtendedData));
            Assert.Equal("ASSIGNED", restored[0].Value);
            Assert.Equal(2.75, restored[0].Height);
            Assert.Equal(new ACadSharp.Color(12, 34, 56), restored[0].Color);
            Assert.Equal(synchronizedPoint, restored[0].InsertPoint);
            Assert.Equal("SERIAL", restored[1].Tag);
            Assert.Equal("SERIAL DEFAULT", restored[1].Value);
            Assert.Equal(1.5, restored[1].Height);
            Assert.True(loadedDocument.Entities
                .OfType<Line>()
                .Single()
                .ExtendedData
                .TryGet("PROGPU_PERSISTED_SYNC_TEST", out _));
            return true;
        });
    }

    private static ExtendedData CreateTestXData(string value) =>
        new(
        [
            ExtendedDataControlString.Open,
            new ExtendedDataString(value),
            ExtendedDataControlString.Close,
        ]);

    private static void AssertExactXData(
        CadObject owner,
        AppId appId,
        ExtendedData expected)
    {
        KeyValuePair<AppId, ExtendedData> entry = Assert.Single(
            owner.ExtendedData);
        Assert.Same(appId, entry.Key);
        Assert.Same(expected, entry.Value);
    }

    private static Insert CreateInsert(
        BlockRecord block,
        XYZ insertPoint,
        double rotation,
        double xScale = 1,
        double yScale = 1)
    {
        var insert = new Insert(block)
        {
            InsertPoint = insertPoint,
            Rotation = rotation,
            XScale = xScale,
            YScale = yScale,
        };
        foreach (AttributeEntity attribute in insert.Attributes)
        {
            attribute.ApplyTransform(insert.GetTransform());
        }
        return insert;
    }

    private static AttributeEntity CreateExpected(
        AttributeDefinition definition,
        Insert insert)
    {
        var expected = new AttributeEntity(definition)
        {
            BookColor = definition.BookColor,
        };
        expected.ApplyTransform(insert.GetTransform());
        return expected;
    }

    private static void AssertSynchronizedProperties(
        AttributeEntity expected,
        AttributeEntity actual)
    {
        Assert.Equal(expected.Layer.Name, actual.Layer.Name);
        Assert.Same(actual.Document.Layers[expected.Layer.Name], actual.Layer);
        Assert.Equal(expected.LineType.Name, actual.LineType.Name);
        Assert.Same(
            actual.Document.LineTypes[expected.LineType.Name],
            actual.LineType);
        Assert.Equal(expected.Material?.Name, actual.Material?.Name);
        Assert.Equal(expected.BookColor?.Name, actual.BookColor?.Name);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.LineWeight, actual.LineWeight);
        Assert.Equal(expected.LineTypeScale, actual.LineTypeScale);
        Assert.Equal(expected.Transparency, actual.Transparency);
        Assert.Equal(expected.IsInvisible, actual.IsInvisible);
        Assert.Equal(expected.Thickness, actual.Thickness);
        Assert.Equal(expected.InsertPoint, actual.InsertPoint);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Rotation, actual.Rotation);
        Assert.Equal(expected.WidthFactor, actual.WidthFactor);
        Assert.Equal(expected.ObliqueAngle, actual.ObliqueAngle);
        Assert.Equal(expected.Style.Name, actual.Style.Name);
        Assert.Equal(expected.Mirror, actual.Mirror);
        Assert.Equal(expected.HorizontalAlignment, actual.HorizontalAlignment);
        Assert.Equal(expected.AlignmentPoint, actual.AlignmentPoint);
        Assert.Equal(expected.Normal, actual.Normal);
        Assert.Equal(expected.VerticalAlignment, actual.VerticalAlignment);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Tag, actual.Tag);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.AttributeType, actual.AttributeType);
        Assert.Equal(expected.IsLocked, actual.IsLocked);
    }
}
