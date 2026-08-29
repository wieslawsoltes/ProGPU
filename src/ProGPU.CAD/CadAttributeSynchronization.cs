using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

/// <summary>Counts changed by one block-attribute property synchronization.</summary>
public readonly record struct CadAttributeSynchronizationResult(
    ulong ContentGeneration,
    int InsertCount,
    int AttributeCount);

/// <summary>
/// Synchronizes the retained ATTRIB properties of every reference to the block
/// selected through one model-space INSERT, while preserving assigned values.
/// </summary>
/// <remarks>
/// The first apply performs O(D + I + A) work and owns O(I + A) state for D
/// definitions, I registered INSERTs, and A retained attributes. Undo and Redo
/// are O(I + A). Counts are bounded before mutation. Structural definition/
/// reference count changes are rejected because ACadSharp does not yet expose a
/// handle-preserving reversible sequence replacement contract.
/// </remarks>
public sealed class CadSynchronizeBlockAttributePropertiesCommand : CadEditCommand
{
    public const int MaximumDefinitionCount = 4_096;
    public const int MaximumInsertCount = 65_536;
    public const int MaximumAttributeCount = 1_048_576;

    private readonly ulong _selectedInsertHandle;
    private Insert? _selectedInsert;
    private BlockRecord? _block;
    private Insert[]? _inserts;
    private AttributeOperation[]? _operations;

    public ulong SelectedInsertHandle => _selectedInsertHandle;

    public int InsertCount { get; private set; }

    public int AttributeCount { get; private set; }

    public CadSynchronizeBlockAttributePropertiesCommand(
        ulong selectedInsertHandle,
        string description = "Synchronize block attribute properties")
        : base(description)
    {
        if (selectedInsertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedInsertHandle));
        }

        _selectedInsertHandle = selectedInsertHandle;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            ValidateRetainedState(document);
            ApplyStates(useSynchronizedState: true);
            return;
        }

        Entity selectedEntity = ResolveModelSpaceEntity(
            document,
            _selectedInsertHandle);
        Insert selectedInsert = selectedEntity as Insert ??
            throw new InvalidOperationException(
                $"Model-space entity handle {_selectedInsertHandle:X} is not an INSERT.");
        BlockRecord block = selectedInsert.Block ??
            throw new InvalidOperationException(
                $"INSERT handle {_selectedInsertHandle:X} has no block definition.");
        ValidateBlock(block);

        AttributeDefinition[] definitions = block.AttributeDefinitions.ToArray();
        if (definitions.Length == 0)
        {
            throw new InvalidOperationException(
                $"Block '{block.Name}' contains no attribute definitions.");
        }
        if (definitions.Length > MaximumDefinitionCount)
        {
            throw new InvalidOperationException(
                $"Block '{block.Name}' exceeds the {MaximumDefinitionCount:N0}-definition " +
                "attribute synchronization limit.");
        }
        foreach (AttributeDefinition definition in definitions)
        {
            ValidateDefinition(definition);
        }

        Insert[] inserts = document.GetCadObjects<Insert>()
            .Where(insert => ReferenceEquals(insert.Block, block))
            .OrderBy(insert => insert.Handle)
            .ToArray();
        if (inserts.Length == 0 || !inserts.Contains(selectedInsert))
        {
            throw new InvalidOperationException(
                $"Block '{block.Name}' has no retained reference set containing " +
                "the selected INSERT.");
        }
        if (inserts.Length > MaximumInsertCount)
        {
            throw new InvalidOperationException(
                $"Block '{block.Name}' exceeds the {MaximumInsertCount:N0}-INSERT " +
                "attribute synchronization limit.");
        }

        long totalAttributeCount = checked(
            (long)definitions.Length * inserts.Length);
        if (totalAttributeCount > MaximumAttributeCount)
        {
            throw new InvalidOperationException(
                $"Block '{block.Name}' exceeds the {MaximumAttributeCount:N0}-attribute " +
                "synchronization limit.");
        }

        var operations = new List<AttributeOperation>(
            checked((int)totalAttributeCount));
        foreach (Insert insert in inserts)
        {
            if (HasLayerFlag(insert.Layer, LayerFlags.Locked))
            {
                throw new InvalidOperationException(
                    $"INSERT handle {insert.Handle:X} is on locked layer " +
                    $"'{insert.Layer.Name}' and cannot be synchronized.");
            }

            AttributeEntity[] attributes = insert.Attributes.ToArray();
            if (attributes.Length != definitions.Length)
            {
                throw new InvalidOperationException(
                    $"INSERT handle {insert.Handle:X} has {attributes.Length:N0} " +
                    $"attribute reference(s), but block '{block.Name}' has " +
                    $"{definitions.Length:N0} definition(s). Structural attribute " +
                    "reconciliation requires the handle-preserving sequence contract.");
            }

            int[] attributeIndices = MatchDefinitions(
                definitions,
                attributes);
            for (int definitionIndex = 0;
                definitionIndex < definitions.Length;
                definitionIndex++)
            {
                AttributeEntity attribute =
                    attributes[attributeIndices[definitionIndex]];
                AttributeState original = AttributeState.Capture(attribute);
                AttributeState synchronized = CreateSynchronizedState(
                    definitions[definitionIndex],
                    insert,
                    attribute);
                operations.Add(new AttributeOperation(
                    insert,
                    attribute,
                    original,
                    synchronized));
            }
        }

        _selectedInsert = selectedInsert;
        _block = block;
        _inserts = inserts;
        _operations = operations.ToArray();
        InsertCount = inserts.Length;
        AttributeCount = operations.Count;
        ApplyStates(useSynchronizedState: true);
    }

    internal override void Revert(CadDocument document)
    {
        ValidateRetainedState(document);
        ApplyStates(useSynchronizedState: false);
    }

    private void ValidateRetainedState(CadDocument document)
    {
        Insert selectedInsert = _selectedInsert ??
            throw new InvalidOperationException(
                "The attribute synchronization command has not been applied.");
        BlockRecord block = _block ??
            throw new InvalidOperationException(
                "The attribute synchronization command has not been applied.");
        Insert[] inserts = _inserts ??
            throw new InvalidOperationException(
                "The attribute synchronization command has not been applied.");
        AttributeOperation[] operations = _operations ??
            throw new InvalidOperationException(
                "The attribute synchronization command has not been applied.");

        Entity currentSelected = ResolveModelSpaceEntity(
            document,
            _selectedInsertHandle);
        if (!ReferenceEquals(currentSelected, selectedInsert) ||
            !ReferenceEquals(selectedInsert.Block, block))
        {
            throw new InvalidOperationException(
                "The selected INSERT no longer retains the synchronized block identity.");
        }
        if (!document.BlockRecords.TryGetValue(
                block.Name,
                out BlockRecord? currentBlock) ||
            !ReferenceEquals(currentBlock, block))
        {
            throw new InvalidOperationException(
                $"The synchronized block '{block.Name}' is no longer registered.");
        }

        foreach (Insert insert in inserts)
        {
            if (!document.TryGetCadObject(
                    insert.Handle,
                    out Insert? currentInsert) ||
                !ReferenceEquals(currentInsert, insert) ||
                !ReferenceEquals(insert.Block, block))
            {
                throw new InvalidOperationException(
                    "A synchronized INSERT no longer retains its registered identity.");
            }
            if (HasLayerFlag(insert.Layer, LayerFlags.Locked))
            {
                throw new InvalidOperationException(
                    $"INSERT handle {insert.Handle:X} is now on locked layer " +
                    $"'{insert.Layer.Name}'.");
            }
        }

        foreach (AttributeOperation operation in operations)
        {
            if (!ReferenceEquals(operation.Attribute.Owner, operation.Insert) ||
                !operation.Insert.Attributes.Contains(operation.Attribute))
            {
                throw new InvalidOperationException(
                    $"A synchronized attribute on INSERT handle " +
                    $"{operation.Insert.Handle:X} no longer retains its identity.");
            }
        }
    }

    private void ApplyStates(bool useSynchronizedState)
    {
        AttributeOperation[] operations = _operations ??
            throw new InvalidOperationException(
                "The attribute synchronization command has not been applied.");
        for (int index = 0; index < operations.Length; index++)
        {
            try
            {
                AttributeOperation operation = operations[index];
                (useSynchronizedState
                    ? operation.Synchronized
                    : operation.Original).ApplyTo(operation.Attribute);
            }
            catch
            {
                for (int rollback = index; rollback >= 0; rollback--)
                {
                    AttributeOperation operation = operations[rollback];
                    (useSynchronizedState
                        ? operation.Original
                        : operation.Synchronized).ApplyTo(operation.Attribute);
                }
                throw;
            }
        }
    }

    private static AttributeState CreateSynchronizedState(
        AttributeDefinition definition,
        Insert insert,
        AttributeEntity current)
    {
        string retainedValue = current.Value ?? string.Empty;
        string? retainedMTextValue = current.MText?.Value;
        if (current.AttributeType is not (
                AttributeType.SingleLine or
                AttributeType.MultiLine or
                AttributeType.ConstantMultiLine))
        {
            throw new InvalidOperationException(
                $"Attribute '{current.Tag}' on INSERT handle {insert.Handle:X} " +
                "uses an unsupported attribute type.");
        }
        if (current.AttributeType != AttributeType.SingleLine &&
            current.MText is null)
        {
            throw new InvalidOperationException(
                $"Attribute '{current.Tag}' on INSERT handle {insert.Handle:X} " +
                "has no embedded MTEXT payload.");
        }

        var synchronized = new AttributeEntity(definition)
        {
            BookColor = definition.BookColor,
        };
        synchronized.ApplyTransform(insert.GetTransform());
        if (synchronized.MText is MText mtext)
        {
            mtext.Value = retainedMTextValue ?? retainedValue;
            synchronized.Value = retainedValue;
        }
        else
        {
            synchronized.Value = retainedMTextValue ?? retainedValue;
        }
        return AttributeState.Capture(synchronized);
    }

    private static int[] MatchDefinitions(
        ReadOnlySpan<AttributeDefinition> definitions,
        ReadOnlySpan<AttributeEntity> attributes)
    {
        var byTag = new Dictionary<string, Queue<int>>(
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < attributes.Length; index++)
        {
            string tag = attributes[index].Tag ?? string.Empty;
            if (!byTag.TryGetValue(tag, out Queue<int>? indices))
            {
                indices = new Queue<int>();
                byTag.Add(tag, indices);
            }
            indices.Enqueue(index);
        }

        var result = new int[definitions.Length];
        Array.Fill(result, -1);
        var usedAttributes = new bool[attributes.Length];
        for (int definitionIndex = 0;
            definitionIndex < definitions.Length;
            definitionIndex++)
        {
            string tag = definitions[definitionIndex].Tag ?? string.Empty;
            if (!byTag.TryGetValue(tag, out Queue<int>? indices) ||
                !indices.TryDequeue(out int attributeIndex))
            {
                continue;
            }
            result[definitionIndex] = attributeIndex;
            usedAttributes[attributeIndex] = true;
        }

        var unmatchedAttributes = new Queue<int>();
        for (int index = 0; index < usedAttributes.Length; index++)
        {
            if (!usedAttributes[index])
            {
                unmatchedAttributes.Enqueue(index);
            }
        }
        for (int definitionIndex = 0;
            definitionIndex < result.Length;
            definitionIndex++)
        {
            if (result[definitionIndex] < 0)
            {
                result[definitionIndex] = unmatchedAttributes.Dequeue();
            }
        }
        return result;
    }

    private static void ValidateBlock(BlockRecord block)
    {
        if ((block.Flags & (
                BlockTypeFlags.XRef |
                BlockTypeFlags.XRefOverlay |
                BlockTypeFlags.XRefDependent)) != 0 ||
            block.BlockEntity.IsUnloaded)
        {
            throw new InvalidOperationException(
                $"External-reference block '{block.Name}' cannot synchronize " +
                "local attribute properties.");
        }
        if (block.EvaluationGraph is not null)
        {
            throw new InvalidOperationException(
                $"Dynamic block '{block.Name}' requires evaluation-aware " +
                "attribute synchronization.");
        }
    }

    private static void ValidateDefinition(AttributeDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Tag))
        {
            throw new InvalidOperationException(
                "Every synchronized attribute definition must have a non-empty tag.");
        }
        if (definition.AttributeType is not (
                AttributeType.SingleLine or
                AttributeType.MultiLine or
                AttributeType.ConstantMultiLine))
        {
            throw new InvalidOperationException(
                $"Attribute definition '{definition.Tag}' uses an unsupported " +
                "attribute type.");
        }
        if (definition.AttributeType != AttributeType.SingleLine &&
            definition.MText is null)
        {
            throw new InvalidOperationException(
                $"Attribute definition '{definition.Tag}' has no embedded MTEXT payload.");
        }
    }

    private sealed record AttributeOperation(
        Insert Insert,
        AttributeEntity Attribute,
        AttributeState Original,
        AttributeState Synchronized);

    private sealed class AttributeState
    {
        private readonly Layer _layer;
        private readonly LineType _lineType;
        private readonly Material? _material;
        private readonly BookColor? _bookColor;
        private readonly Color _color;
        private readonly LineWeightType _lineWeight;
        private readonly double _lineTypeScale;
        private readonly bool _isInvisible;
        private readonly Transparency _transparency;
        private readonly double _thickness;
        private readonly XYZ _insertPoint;
        private readonly double _height;
        private readonly string _value;
        private readonly double _rotation;
        private readonly double _widthFactor;
        private readonly double _obliqueAngle;
        private readonly TextStyle _style;
        private readonly TextMirrorFlag _mirror;
        private readonly TextHorizontalAlignment _horizontalAlignment;
        private readonly XYZ _alignmentPoint;
        private readonly XYZ _normal;
        private readonly TextVerticalAlignmentType _verticalAlignment;
        private readonly byte _version;
        private readonly string _tag;
        private readonly AttributeFlags _flags;
        private readonly AttributeType _attributeType;
        private readonly bool _isLocked;
        private readonly MText? _mtext;

        private AttributeState(AttributeEntity source)
        {
            _layer = source.Layer;
            _lineType = source.LineType;
            _material = source.Material;
            _bookColor = source.BookColor;
            _color = source.Color;
            _lineWeight = source.LineWeight;
            _lineTypeScale = source.LineTypeScale;
            _isInvisible = source.IsInvisible;
            _transparency = source.Transparency;
            _thickness = source.Thickness;
            _insertPoint = source.InsertPoint;
            _height = source.Height;
            _value = source.Value ?? string.Empty;
            _rotation = source.Rotation;
            _widthFactor = source.WidthFactor;
            _obliqueAngle = source.ObliqueAngle;
            _style = source.Style;
            _mirror = source.Mirror;
            _horizontalAlignment = source.HorizontalAlignment;
            _alignmentPoint = source.AlignmentPoint;
            _normal = source.Normal;
            _verticalAlignment = source.VerticalAlignment;
            _version = source.Version;
            _tag = source.Tag ?? string.Empty;
            _flags = source.Flags;
            _attributeType = source.AttributeType;
            _isLocked = source.IsLocked;
            _mtext = source.MText;
        }

        public static AttributeState Capture(AttributeEntity source) =>
            new(source);

        public void ApplyTo(AttributeEntity target)
        {
            target.Layer = _layer;
            target.LineType = _lineType;
            target.Material = _material;
            target.BookColor = _bookColor;
            target.Color = _color;
            target.LineWeight = _lineWeight;
            target.LineTypeScale = _lineTypeScale;
            target.IsInvisible = _isInvisible;
            target.Transparency = _transparency;
            target.Thickness = _thickness;
            target.InsertPoint = _insertPoint;
            target.Height = _height;
            target.Value = _value;
            target.Rotation = _rotation;
            target.WidthFactor = _widthFactor;
            target.ObliqueAngle = _obliqueAngle;
            target.Style = _style;
            target.Mirror = _mirror;
            target.HorizontalAlignment = _horizontalAlignment;
            target.AlignmentPoint = _alignmentPoint;
            target.Normal = _normal;
            target.VerticalAlignment = _verticalAlignment;
            target.Version = _version;
            target.Tag = _tag;
            target.Flags = _flags;
            target.AttributeType = _attributeType;
            target.IsLocked = _isLocked;
            target.MText = _mtext!;
        }
    }
}
