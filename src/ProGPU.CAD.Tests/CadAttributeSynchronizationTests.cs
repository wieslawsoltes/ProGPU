using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Tables;
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
    public void StructuralMismatchRejectsCompleteBatchBeforeMutation()
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
        Assert.True(second.Attributes.Remove(secondAttribute));
        definition.Height = 5;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSynchronizeBlockAttributePropertiesCommand(first.Handle)));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(1, firstAttribute.Height);
        Assert.Equal("DEFAULT", firstAttribute.Value);
        Assert.Empty(second.Attributes);
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
        definition.Height = 2.75;
        definition.Color = new ACadSharp.Color(12, 34, 56);
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
            AttributeEntity restored = loadedDocument.Entities
                .OfType<Insert>()
                .Single()
                .Attributes
                .Single();
            Assert.Equal("ASSIGNED", restored.Value);
            Assert.Equal(2.75, restored.Height);
            Assert.Equal(new ACadSharp.Color(12, 34, 56), restored.Color);
            Assert.Equal(synchronizedPoint, restored.InsertPoint);
            return true;
        });
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
