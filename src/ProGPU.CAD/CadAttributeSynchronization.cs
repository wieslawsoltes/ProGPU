using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.XData;
using CSMath;

namespace ProGPU.CAD;

/// <summary>Counts changed by one block-attribute property synchronization.</summary>
public readonly record struct CadAttributeSynchronizationResult(
    ulong ContentGeneration,
    int InsertCount,
    int AttributeCount,
    int AddedAttributeCount,
    int RemovedAttributeCount,
    int ClearedExtendedDataEntryCount);

/// <summary>
/// Synchronizes the retained ATTRIB properties of every reference to the block
/// selected through one model-space INSERT, while preserving assigned values.
/// </summary>
/// <remarks>
/// The first apply performs O(D * I + A + X) work and owns O(D * I + A + X)
/// bounded state for D variable definitions, I registered INSERTs, A original
/// attributes, and X reference-owned XData application entries. Undo and Redo
/// have the same bound. Constant definitions remain definition-owned and any
/// malformed constant references are removed. Existing variable-reference
/// values are retained by case-insensitive tag and then stable unmatched order;
/// new references use definition defaults. XData
/// on each INSERT, its ATTRIB sequence, and its active SEQEND is cleared while
/// definition-owned XData remains unchanged. Removed references keep exact
/// handles in bounded leases until this command leaves undo/redo history.
/// </remarks>
public sealed class CadSynchronizeBlockAttributePropertiesCommand : CadEditCommand
{
    public const int MaximumDefinitionCount = 4_096;
    public const int MaximumInsertCount = 65_536;
    public const int MaximumAttributeCount = 1_048_576;
    public const int MaximumExtendedDataEntryCount = 1_048_576;

    private readonly ulong _selectedInsertHandle;
    private Insert? _selectedInsert;
    private BlockRecord? _block;
    private Insert[]? _inserts;
    private AttributeOperation[]? _operations;
    private StructuralOperation[]? _structuralOperations;
    private ExtendedDataOperation[]? _extendedDataOperations;

    public ulong SelectedInsertHandle => _selectedInsertHandle;

    public int InsertCount { get; private set; }

    public int AttributeCount { get; private set; }

    public int AddedAttributeCount { get; private set; }

    public int RemovedAttributeCount { get; private set; }

    public int ClearedExtendedDataEntryCount { get; private set; }

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
            ValidateRetainedState(document, expectStructuralApplied: false);
            ApplySynchronizedState();
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

        AttributeDefinition[] allDefinitions =
            block.AttributeDefinitions.ToArray();
        if (allDefinitions.Length > MaximumDefinitionCount)
        {
            throw new InvalidOperationException(
                $"Block '{block.Name}' exceeds the {MaximumDefinitionCount:N0}-definition " +
                "attribute synchronization limit.");
        }
        foreach (AttributeDefinition definition in allDefinitions)
        {
            ValidateDefinition(definition);
        }
        AttributeDefinition[] definitions = allDefinitions
            .Where(definition => !IsDefinitionOwned(definition))
            .ToArray();

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
        var preparedInserts = new List<PreparedInsert>(inserts.Length);
        long originalAttributeCount = 0;
        int addedAttributeCount = 0;
        int removedAttributeCount = 0;
        foreach (Insert insert in inserts)
        {
            if (HasLayerFlag(insert.Layer, LayerFlags.Locked))
            {
                throw new InvalidOperationException(
                    $"INSERT handle {insert.Handle:X} is on locked layer " +
                    $"'{insert.Layer.Name}' and cannot be synchronized.");
            }

            AttributeEntity[] attributes = insert.Attributes.ToArray();
            originalAttributeCount = checked(
                originalAttributeCount + attributes.Length);
            if (originalAttributeCount > MaximumAttributeCount)
            {
                throw new InvalidOperationException(
                    $"Block '{block.Name}' exceeds the {MaximumAttributeCount:N0}-source " +
                    "attribute synchronization limit.");
            }

            int[] attributeIndices = MatchDefinitions(
                definitions,
                attributes);
            var replacement = new AttributeEntity[definitions.Length];
            var retainedAttributes = new bool[attributes.Length];
            for (int definitionIndex = 0;
                definitionIndex < definitions.Length;
                definitionIndex++)
            {
                int attributeIndex = attributeIndices[definitionIndex];
                if (attributeIndex < 0)
                {
                    replacement[definitionIndex] = CreateDefaultAttribute(
                        definitions[definitionIndex],
                        insert);
                    addedAttributeCount = checked(addedAttributeCount + 1);
                    continue;
                }

                AttributeEntity attribute = attributes[attributeIndex];
                retainedAttributes[attributeIndex] = true;
                replacement[definitionIndex] = attribute;
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

            for (int index = 0; index < retainedAttributes.Length; index++)
            {
                if (!retainedAttributes[index])
                {
                    removedAttributeCount = checked(removedAttributeCount + 1);
                }
            }

            preparedInserts.Add(new PreparedInsert(
                insert,
                attributes,
                replacement));
        }

        var structuralOperations = new List<StructuralOperation>();
        ExtendedDataOperation[] extendedDataOperations =
            CaptureExtendedDataOperations(
                block,
                preparedInserts,
                out int clearedExtendedDataEntryCount);
        try
        {
            foreach (PreparedInsert prepared in preparedInserts)
            {
                if (!HasStructuralChange(
                        prepared.Original,
                        prepared.Replacement))
                {
                    continue;
                }

                structuralOperations.Add(new StructuralOperation(
                    prepared.Insert,
                    prepared.Insert.Attributes.CreateReversibleReplacement(
                        prepared.Replacement)));
            }
        }
        catch
        {
            ReleaseStructuralOperations(structuralOperations);
            throw;
        }

        _selectedInsert = selectedInsert;
        _block = block;
        _inserts = inserts;
        _operations = operations.ToArray();
        _structuralOperations = structuralOperations.ToArray();
        _extendedDataOperations = extendedDataOperations;
        InsertCount = inserts.Length;
        AttributeCount = checked((int)totalAttributeCount);
        AddedAttributeCount = addedAttributeCount;
        RemovedAttributeCount = removedAttributeCount;
        ClearedExtendedDataEntryCount = clearedExtendedDataEntryCount;
        try
        {
            ApplySynchronizedState();
        }
        catch
        {
            ReleaseStructuralOperations(structuralOperations);
            _selectedInsert = null;
            _block = null;
            _inserts = null;
            _operations = null;
            _structuralOperations = null;
            _extendedDataOperations = null;
            InsertCount = 0;
            AttributeCount = 0;
            AddedAttributeCount = 0;
            RemovedAttributeCount = 0;
            ClearedExtendedDataEntryCount = 0;
            throw;
        }
    }

    internal override void Revert(CadDocument document)
    {
        ValidateRetainedState(document, expectStructuralApplied: true);
        SetStructuralState(applied: false);
        try
        {
            ApplyStates(useSynchronizedState: false);
            SetExtendedDataState(cleared: false, document);
        }
        catch
        {
            SetExtendedDataState(cleared: true, document);
            ApplyStates(useSynchronizedState: true);
            SetStructuralState(applied: true);
            throw;
        }
    }

    internal override void Discard(CadDocument document)
    {
        StructuralOperation[] operations = _structuralOperations ?? [];
        ReleaseStructuralOperations(operations);
        _structuralOperations = [];
        _extendedDataOperations = [];
    }

    private void ValidateRetainedState(
        CadDocument document,
        bool expectStructuralApplied)
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
        ExtendedDataOperation[] extendedDataOperations =
            _extendedDataOperations ??
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

        foreach (StructuralOperation operation in _structuralOperations ?? [])
        {
            if (operation.Replacement.IsApplied != expectStructuralApplied)
            {
                throw new InvalidOperationException(
                    $"The attribute sequence on INSERT handle " +
                    $"{operation.Insert.Handle:X} no longer matches command state.");
            }
        }

        foreach (ExtendedDataOperation operation in extendedDataOperations)
        {
            if (!ReferenceEquals(operation.Owner.Document, document) ||
                !operation.Matches(cleared: expectStructuralApplied))
            {
                throw new InvalidOperationException(
                    "Reference-owned XData no longer matches attribute " +
                    "synchronization history state.");
            }
        }
    }

    private void ApplySynchronizedState()
    {
        ApplyStates(useSynchronizedState: true);
        try
        {
            SetStructuralState(applied: true);
        }
        catch
        {
            ApplyStates(useSynchronizedState: false);
            throw;
        }
        SetExtendedDataState(cleared: true, _selectedInsert!.Document!);
    }

    private void SetExtendedDataState(bool cleared, CadDocument document)
    {
        ExtendedDataOperation[] operations = _extendedDataOperations ??
            throw new InvalidOperationException(
                "The attribute synchronization command has not been applied.");
        if (cleared)
        {
            foreach (ExtendedDataOperation operation in operations)
            {
                operation.Owner.ExtendedData.Clear();
            }
            return;
        }

        foreach (ExtendedDataOperation operation in operations)
        {
            if (operation.Owner.ExtendedData.Count != 0)
            {
                throw new InvalidOperationException(
                    "Reference-owned XData was populated outside attribute " +
                    "synchronization history.");
            }
            foreach (KeyValuePair<AppId, ExtendedData> entry in operation.Original)
            {
                if (!document.AppIds.TryGetValue(
                        entry.Key.Name,
                        out AppId? registered) ||
                    !ReferenceEquals(registered, entry.Key))
                {
                    throw new InvalidOperationException(
                        $"XData application '{entry.Key.Name}' no longer retains " +
                        "its registered identity.");
                }
            }
        }

        try
        {
            foreach (ExtendedDataOperation operation in operations)
            {
                foreach (KeyValuePair<AppId, ExtendedData> entry in operation.Original)
                {
                    operation.Owner.ExtendedData.Add(entry.Key, entry.Value);
                }
            }
        }
        catch
        {
            foreach (ExtendedDataOperation operation in operations)
            {
                operation.Owner.ExtendedData.Clear();
            }
            throw;
        }
    }

    private static ExtendedDataOperation[] CaptureExtendedDataOperations(
        BlockRecord block,
        IEnumerable<PreparedInsert> preparedInserts,
        out int entryCount)
    {
        var owners = new HashSet<CadObject>(ReferenceEqualityComparer.Instance);
        var operations = new List<ExtendedDataOperation>();
        long totalEntryCount = 0;

        foreach (PreparedInsert prepared in preparedInserts)
        {
            Add(prepared.Insert);
            foreach (AttributeEntity attribute in prepared.Original)
            {
                Add(attribute);
            }
            foreach (AttributeEntity attribute in prepared.Replacement)
            {
                Add(attribute);
            }
            if (prepared.Insert.Attributes.Seqend is Seqend seqend)
            {
                Add(seqend);
            }
        }

        entryCount = checked((int)totalEntryCount);
        return operations.ToArray();

        void Add(CadObject owner)
        {
            if (owner.ExtendedData.Count == 0 || !owners.Add(owner))
            {
                return;
            }

            KeyValuePair<AppId, ExtendedData>[] entries =
                owner.ExtendedData.ToArray();
            totalEntryCount = checked(totalEntryCount + entries.Length);
            if (totalEntryCount > MaximumExtendedDataEntryCount)
            {
                throw new InvalidOperationException(
                    $"Block '{block.Name}' exceeds the " +
                    $"{MaximumExtendedDataEntryCount:N0}-entry reference XData " +
                    "synchronization limit.");
            }
            operations.Add(new ExtendedDataOperation(owner, entries));
        }
    }

    private void SetStructuralState(bool applied)
    {
        StructuralOperation[] operations = _structuralOperations ??
            throw new InvalidOperationException(
                "The attribute synchronization command has not been applied.");
        var originalStates = new bool[operations.Length];
        for (int index = 0; index < operations.Length; index++)
        {
            originalStates[index] = operations[index].Replacement.IsApplied;
        }

        try
        {
            if (applied)
            {
                for (int index = 0; index < operations.Length; index++)
                {
                    if (!operations[index].Replacement.TryApply())
                    {
                        throw new InvalidOperationException(
                            "Attribute sequence replacement was cancelled.");
                    }
                }
            }
            else
            {
                for (int index = operations.Length - 1; index >= 0; index--)
                {
                    if (!operations[index].Replacement.TryRevert())
                    {
                        throw new InvalidOperationException(
                            "Attribute sequence restoration was cancelled.");
                    }
                }
            }
        }
        catch
        {
            for (int index = operations.Length - 1; index >= 0; index--)
            {
                bool current = operations[index].Replacement.IsApplied;
                if (current == originalStates[index])
                {
                    continue;
                }

                bool restored = originalStates[index]
                    ? operations[index].Replacement.TryApply()
                    : operations[index].Replacement.TryRevert();
                if (!restored)
                {
                    throw new InvalidOperationException(
                        "Attribute sequence rollback was cancelled.");
                }
            }
            throw;
        }
    }

    private static bool HasStructuralChange(
        ReadOnlySpan<AttributeEntity> original,
        ReadOnlySpan<AttributeEntity> replacement)
    {
        if (original.Length != replacement.Length)
        {
            return true;
        }
        for (int index = 0; index < original.Length; index++)
        {
            if (!ReferenceEquals(original[index], replacement[index]))
            {
                return true;
            }
        }
        return false;
    }

    private static void ReleaseStructuralOperations(
        IEnumerable<StructuralOperation> operations)
    {
        foreach (StructuralOperation operation in operations)
        {
            operation.Replacement.Release();
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

    private static AttributeEntity CreateDefaultAttribute(
        AttributeDefinition definition,
        Insert insert)
    {
        var attribute = new AttributeEntity(definition)
        {
            BookColor = definition.BookColor,
        };
        attribute.ApplyTransform(insert.GetTransform());
        return attribute;
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
                result[definitionIndex] = unmatchedAttributes.TryDequeue(
                    out int attributeIndex)
                    ? attributeIndex
                    : -1;
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

    private static bool IsDefinitionOwned(AttributeDefinition definition) =>
        (definition.Flags & AttributeFlags.Constant) != 0 ||
        definition.AttributeType == AttributeType.ConstantMultiLine;

    private sealed record AttributeOperation(
        Insert Insert,
        AttributeEntity Attribute,
        AttributeState Original,
        AttributeState Synchronized);

    private sealed record PreparedInsert(
        Insert Insert,
        AttributeEntity[] Original,
        AttributeEntity[] Replacement);

    private sealed record StructuralOperation(
        Insert Insert,
        CadObjectCollection<AttributeEntity>.ReversibleReplacement Replacement);

    private sealed record ExtendedDataOperation(
        CadObject Owner,
        KeyValuePair<AppId, ExtendedData>[] Original)
    {
        public bool Matches(bool cleared)
        {
            if (cleared)
            {
                return Owner.ExtendedData.Count == 0;
            }
            if (Owner.ExtendedData.Count != Original.Length)
            {
                return false;
            }
            foreach (KeyValuePair<AppId, ExtendedData> entry in Original)
            {
                if (!Owner.ExtendedData.TryGet(
                        entry.Key,
                        out ExtendedData? current) ||
                    !ReferenceEquals(current, entry.Value))
                {
                    return false;
                }
            }
            return true;
        }
    }

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
