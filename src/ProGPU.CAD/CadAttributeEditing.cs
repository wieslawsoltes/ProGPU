using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Tables;

namespace ProGPU.CAD;

public enum CadAttributeValueOwner : byte
{
    Reference = 0,
    Definition = 1,
    VariableDefinition = 2,
}

/// <summary>
/// Replaces the drawing-wide persisted ATTMODE value as one reversible edit.
/// </summary>
/// <remarks>
/// Apply, Undo, and Redo are O(1), retain the exact header identity, and reject
/// intervening header replacement or value mutation. Snapshot recompilation is
/// owned by the caller so rendering, selection, native replay, and plotting all
/// observe one immutable generation.
/// </remarks>
public sealed class CadSetAttributeVisibilityModeCommand : CadEditCommand
{
    private CadHeader? _header;
    private AttributeVisibilityMode _previousMode;

    public AttributeVisibilityMode Mode { get; }

    public CadSetAttributeVisibilityModeCommand(
        AttributeVisibilityMode mode,
        string description = "Set attribute display mode")
        : base(description)
    {
        ValidateMode(mode, nameof(mode));
        Mode = mode;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        CadHeader header;
        if (isRedo)
        {
            header = GetRetainedHeader(document, _previousMode);
        }
        else
        {
            header = document.Header;
            ValidateMode(header.AttributeVisibility, nameof(document));
            if (header.AttributeVisibility == Mode)
            {
                throw new InvalidOperationException(
                    $"Drawing ATTMODE is already {(int)Mode} ({Mode}).");
            }

            _header = header;
            _previousMode = header.AttributeVisibility;
        }

        header.AttributeVisibility = Mode;
    }

    internal override void Revert(CadDocument document)
    {
        CadHeader header = GetRetainedHeader(document, Mode);
        header.AttributeVisibility = _previousMode;
    }

    private CadHeader GetRetainedHeader(
        CadDocument document,
        AttributeVisibilityMode expectedMode)
    {
        CadHeader header = _header ?? throw new InvalidOperationException(
            "The attribute-display command has not been applied.");
        if (!ReferenceEquals(document.Header, header))
        {
            throw new InvalidOperationException(
                "The drawing header is no longer the retained header.");
        }
        if (header.AttributeVisibility != expectedMode)
        {
            throw new InvalidOperationException(
                $"Drawing ATTMODE changed from the expected value " +
                $"{(int)expectedMode} ({expectedMode}).");
        }
        return header;
    }

    private static void ValidateMode(
        AttributeVisibilityMode mode,
        string parameterName)
    {
        if (mode is not (
            AttributeVisibilityMode.None or
            AttributeVisibilityMode.Normal or
            AttributeVisibilityMode.All))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                mode,
                "ATTMODE must be Off (0), Normal (1), or On (2).");
        }
    }
}

/// <summary>One detached editable value exposed by a selected INSERT.</summary>
public readonly record struct CadAttributeValueEntry(
    CadAttributeValueOwner Owner,
    string Tag,
    int Occurrence,
    string Value,
    bool IsMultiline,
    bool IsInvisible,
    bool IsVerifiable,
    bool IsPreset,
    bool IsPositionLocked,
    string Prompt);

public sealed class CadAttributeValueCatalogOptions
{
    public const int DefaultMaxEntries = 4_096;
    public const int DefaultMaxCodeUnitsPerString =
        CadSnapshotOptions.DefaultMaxTextCodeUnitsPerEntity;
    public const int DefaultMaxTotalCodeUnits = 1_048_576;

    public int MaxEntries { get; init; } = DefaultMaxEntries;

    public int MaxCodeUnitsPerString { get; init; } =
        DefaultMaxCodeUnitsPerString;

    public int MaxTotalCodeUnits { get; init; } = DefaultMaxTotalCodeUnits;
}

/// <summary>
/// A bounded, generation-tagged copy of the values editable through one INSERT.
/// </summary>
public sealed class CadAttributeValueCatalog
{
    private readonly CadAttributeValueEntry[] _entries;

    public ulong ContentGeneration { get; }

    public ulong InsertHandle { get; }

    public string BlockName { get; }

    public ReadOnlyMemory<CadAttributeValueEntry> Entries => _entries;

    public int UnsupportedCount { get; }

    internal CadAttributeValueCatalog(
        ulong contentGeneration,
        ulong insertHandle,
        string blockName,
        CadAttributeValueEntry[] entries,
        int unsupportedCount)
    {
        ContentGeneration = contentGeneration;
        InsertHandle = insertHandle;
        BlockName = blockName;
        _entries = entries;
        UnsupportedCount = unsupportedCount;
    }
}

/// <summary>
/// Copies reference-owned variable values plus constant and variable-definition
/// values reachable from one model-space INSERT into immutable ProGPU state.
/// </summary>
/// <remarks>
/// Compilation is O(D + A + S) time and O(D + A + S) storage for D block
/// definitions, A reference attributes, and S copied UTF-16 code units. It
/// performs no renderer, font, or GPU work while the session lock is held.
/// </remarks>
public sealed class CadAttributeValueCatalogCompiler
{
    public CadAttributeValueCatalog Compile(
        CadDocumentSession session,
        ulong insertHandle,
        CadAttributeValueCatalogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (insertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(insertHandle));
        }
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CadAttributeValueCatalogOptions();
        ValidateOptions(options);

        return session.Capture((document, generation) => CompileCore(
            document,
            generation,
            insertHandle,
            options,
            cancellationToken));
    }

    private static CadAttributeValueCatalog CompileCore(
        CadDocument document,
        ulong contentGeneration,
        ulong insertHandle,
        CadAttributeValueCatalogOptions options,
        CancellationToken cancellationToken)
    {
        Entity? entity = document.GetCadObject<Entity>(insertHandle);
        if (entity is not Insert insert ||
            !ReferenceEquals(entity.Owner, document.ModelSpace))
        {
            throw new InvalidOperationException(
                $"Model-space entity handle {insertHandle:X} is not an INSERT.");
        }
        BlockRecord block = insert.Block ?? throw new InvalidDataException(
            $"INSERT handle {insertHandle:X} has no block definition.");
        int totalCodeUnits = 0;
        string Copy(string source, string field)
        {
            source ??= string.Empty;
            if (source.Length > options.MaxCodeUnitsPerString ||
                totalCodeUnits > options.MaxTotalCodeUnits - source.Length)
            {
                throw new InvalidDataException(
                    $"Selected INSERT {field} exceeds the attribute catalog " +
                    "ownership budget.");
            }
            totalCodeUnits += source.Length;
            return new string(source.AsSpan());
        }

        string blockName = Copy(block.Name, "block name");
        var entries = new List<CadAttributeValueEntry>();
        var definitionOccurrences = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        int unsupportedCount = 0;
        foreach (AttributeDefinition definition in block.AttributeDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string tag = definition.Tag ?? string.Empty;
            int occurrence = TakeOccurrence(definitionOccurrences, tag);
            if (!TryGetEditableValue(
                    definition,
                    out string value,
                    out bool isMultiline) ||
                string.IsNullOrWhiteSpace(tag))
            {
                unsupportedCount++;
                continue;
            }
            EnsureCapacity(entries.Count, options.MaxEntries);
            entries.Add(new CadAttributeValueEntry(
                IsDefinitionOwned(definition)
                    ? CadAttributeValueOwner.Definition
                    : CadAttributeValueOwner.VariableDefinition,
                Copy(tag, "attribute tag"),
                occurrence,
                Copy(value, "attribute value"),
                isMultiline,
                (definition.Flags & AttributeFlags.Hidden) != 0,
                (definition.Flags & AttributeFlags.Verify) != 0,
                (definition.Flags & AttributeFlags.Preset) != 0,
                definition.IsLocked,
                Copy(definition.Prompt ?? string.Empty, "attribute prompt")));
        }

        var referenceOccurrences = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        foreach (AttributeEntity attribute in insert.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string tag = attribute.Tag ?? string.Empty;
            int occurrence = TakeOccurrence(referenceOccurrences, tag);
            if (IsDefinitionOwned(attribute))
            {
                continue;
            }
            if (!TryGetEditableValue(
                    attribute,
                    out string value,
                    out bool isMultiline) ||
                string.IsNullOrWhiteSpace(tag))
            {
                unsupportedCount++;
                continue;
            }
            EnsureCapacity(entries.Count, options.MaxEntries);
            entries.Add(new CadAttributeValueEntry(
                CadAttributeValueOwner.Reference,
                Copy(tag, "attribute tag"),
                occurrence,
                Copy(value, "attribute value"),
                isMultiline,
                (attribute.Flags & AttributeFlags.Hidden) != 0,
                (attribute.Flags & AttributeFlags.Verify) != 0,
                (attribute.Flags & AttributeFlags.Preset) != 0,
                attribute.IsLocked,
                string.Empty));
        }

        return new CadAttributeValueCatalog(
            contentGeneration,
            insertHandle,
            blockName,
            entries.ToArray(),
            unsupportedCount);
    }

    private static int TakeOccurrence(
        Dictionary<string, int> occurrences,
        string tag)
    {
        occurrences.TryGetValue(tag, out int occurrence);
        occurrences[tag] = checked(occurrence + 1);
        return occurrence;
    }

    private static bool IsDefinitionOwned(AttributeBase attribute) =>
        (attribute.Flags & AttributeFlags.Constant) != 0 ||
        attribute.AttributeType == AttributeType.ConstantMultiLine;

    private static bool TryGetEditableValue(
        AttributeBase attribute,
        out string value,
        out bool isMultiline)
    {
        switch (attribute.AttributeType)
        {
            case AttributeType.SingleLine:
                value = attribute.Value ?? string.Empty;
                isMultiline = false;
                return true;
            case AttributeType.MultiLine:
            case AttributeType.ConstantMultiLine:
                if (attribute.MText is MText mtext)
                {
                    value = mtext.Value ?? string.Empty;
                    isMultiline = true;
                    return true;
                }
                break;
        }
        value = string.Empty;
        isMultiline = false;
        return false;
    }

    private static void EnsureCapacity(int count, int maximum)
    {
        if (count >= maximum)
        {
            throw new InvalidDataException(
                $"Selected INSERT exceeds the {maximum:N0}-entry attribute " +
                "catalog limit.");
        }
    }

    private static void ValidateOptions(CadAttributeValueCatalogOptions options)
    {
        if (options.MaxEntries <= 0 ||
            options.MaxCodeUnitsPerString <= 0 ||
            options.MaxTotalCodeUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Every attribute catalog ownership limit must be positive.");
        }
    }
}

/// <summary>
/// Replaces one ATTDEF insertion prompt selected through a model-space INSERT,
/// tag, and zero-based duplicate-tag occurrence.
/// </summary>
/// <remarks>
/// Resolution is O(D) for D definitions in the selected INSERT block. Apply,
/// Undo, and Redo retain the exact INSERT, block, and definition identities;
/// the retained mutations are O(1). Existing ATTRIB values are not rewritten.
/// The 256-code-unit limit matches AutoCAD's single-line ATTDEF command prompt
/// contract; an empty persisted prompt is allowed.
/// </remarks>
public sealed class CadSetAttributeDefinitionPromptCommand : CadEditCommand
{
    public const int MaximumTagCodeUnits = 4_096;
    public const int MaximumPromptCodeUnits = 256;

    private readonly ulong _insertHandle;
    private Insert? _insert;
    private BlockRecord? _block;
    private AttributeDefinition? _definition;
    private string? _previousPrompt;

    public ulong InsertHandle => _insertHandle;

    public string Tag { get; }

    public int Occurrence { get; }

    public string Prompt { get; }

    public CadSetAttributeDefinitionPromptCommand(
        ulong insertHandle,
        string tag,
        string prompt,
        int occurrence = 0,
        string description = "Set attribute definition prompt")
        : base(description)
    {
        if (insertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(insertHandle));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);
        if (tag.Length > MaximumTagCodeUnits)
        {
            throw new ArgumentException(
                "The attribute tag exceeds the command ownership budget.",
                nameof(tag));
        }
        if (prompt.Length > MaximumPromptCodeUnits)
        {
            throw new ArgumentException(
                "The attribute prompt exceeds the 256-code-unit ATTDEF limit.",
                nameof(prompt));
        }

        _insertHandle = insertHandle;
        Tag = new string(tag.AsSpan());
        Prompt = new string(prompt.AsSpan());
        Occurrence = occurrence;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        AttributeDefinition definition;
        if (isRedo)
        {
            definition = GetRetainedDefinition(document);
        }
        else
        {
            Entity entity = ResolveModelSpaceEntity(document, _insertHandle);
            Insert insert = entity as Insert ?? throw new InvalidOperationException(
                $"Model-space entity handle {_insertHandle:X} is not an INSERT.");
            BlockRecord block = insert.Block ?? throw new InvalidOperationException(
                $"INSERT handle {_insertHandle:X} has no block definition.");
            definition = ResolveDefinition(block, Tag, Occurrence);
            _insert = insert;
            _block = block;
            _definition = definition;
            _previousPrompt = definition.Prompt ?? string.Empty;
        }

        definition.Prompt = Prompt;
    }

    internal override void Revert(CadDocument document)
    {
        AttributeDefinition definition = GetRetainedDefinition(document);
        definition.Prompt = _previousPrompt ?? throw new InvalidOperationException(
            "The attribute-prompt command has not been applied.");
    }

    private AttributeDefinition GetRetainedDefinition(CadDocument document)
    {
        Insert insert = _insert ?? throw new InvalidOperationException(
            "The attribute-prompt command has not been applied.");
        BlockRecord block = _block ?? throw new InvalidOperationException(
            "The attribute-prompt command has not been applied.");
        AttributeDefinition definition = _definition ??
            throw new InvalidOperationException(
                "The attribute-prompt command has not been applied.");
        ValidateModelSpaceEntity(document, insert);
        if (!ReferenceEquals(insert.Block, block))
        {
            throw new InvalidOperationException(
                "The selected INSERT no longer references the retained block definition.");
        }
        AttributeDefinition current = ResolveDefinition(block, Tag, Occurrence);
        if (!ReferenceEquals(current, definition))
        {
            throw new InvalidOperationException(
                $"Attribute definition '{Tag}' occurrence {Occurrence} is no " +
                "longer the retained definition.");
        }
        return definition;
    }

    private static AttributeDefinition ResolveDefinition(
        BlockRecord block,
        string tag,
        int occurrence)
    {
        int currentOccurrence = 0;
        foreach (AttributeDefinition definition in block.AttributeDefinitions)
        {
            if (!string.Equals(
                    definition.Tag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (currentOccurrence == occurrence)
            {
                return definition;
            }
            currentOccurrence++;
        }
        throw new InvalidOperationException(
            $"Block '{block.Name}' has no attribute definition '{tag}' " +
            $"occurrence {occurrence}.");
    }
}

/// <summary>
/// Replaces one ATTDEF tag selected through a model-space INSERT, current tag,
/// and zero-based duplicate-tag occurrence.
/// </summary>
/// <remarks>
/// Initial resolution and retained identity validation are O(D) for D block
/// definitions and storage is O(S) for the two owned tag strings. Existing
/// ATTRIB tags and assigned values remain unchanged until an explicit attribute
/// synchronization edit is requested.
/// </remarks>
public sealed class CadSetAttributeDefinitionTagCommand : CadEditCommand
{
    public const int MaximumAddressTagCodeUnits = 4_096;
    public const int MaximumTagCodeUnits = 256;

    private readonly ulong _insertHandle;
    private Insert? _insert;
    private BlockRecord? _block;
    private AttributeDefinition? _definition;
    private string? _previousTag;
    private int _definitionIndex = -1;

    public ulong InsertHandle => _insertHandle;

    public string CurrentTag { get; }

    public int Occurrence { get; }

    public string NewTag { get; }

    public CadSetAttributeDefinitionTagCommand(
        ulong insertHandle,
        string currentTag,
        string newTag,
        int occurrence = 0,
        string description = "Set attribute definition tag")
        : base(description)
    {
        if (insertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(insertHandle));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTag);
        ArgumentNullException.ThrowIfNull(newTag);
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);
        string normalizedTag = newTag.ToUpperInvariant();
        if (currentTag.Length > MaximumAddressTagCodeUnits)
        {
            throw new ArgumentException(
                "The current attribute tag exceeds the command ownership budget.",
                nameof(currentTag));
        }
        if (!IsValidNewTag(newTag) || !IsValidNewTag(normalizedTag))
        {
            throw new ArgumentException(
                "An attribute tag must contain 1 to 256 code units and cannot " +
                "contain whitespace or an exclamation mark.",
                nameof(newTag));
        }

        _insertHandle = insertHandle;
        CurrentTag = new string(currentTag.AsSpan());
        NewTag = normalizedTag;
        Occurrence = occurrence;
    }

    public static bool IsValidNewTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) ||
            tag.Length > MaximumTagCodeUnits ||
            tag.Contains('!'))
        {
            return false;
        }
        foreach (char codeUnit in tag)
        {
            if (char.IsWhiteSpace(codeUnit))
            {
                return false;
            }
        }
        return true;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        AttributeDefinition definition;
        if (isRedo)
        {
            definition = GetRetainedDefinition(
                document,
                _previousTag ?? throw new InvalidOperationException(
                    "The attribute-tag command has not been applied."));
        }
        else
        {
            Entity entity = ResolveModelSpaceEntity(document, _insertHandle);
            Insert insert = entity as Insert ?? throw new InvalidOperationException(
                $"Model-space entity handle {_insertHandle:X} is not an INSERT.");
            BlockRecord block = insert.Block ?? throw new InvalidOperationException(
                $"INSERT handle {_insertHandle:X} has no block definition.");
            definition = ResolveDefinition(
                block,
                CurrentTag,
                Occurrence,
                out int definitionIndex);
            _insert = insert;
            _block = block;
            _definition = definition;
            _previousTag = definition.Tag ?? string.Empty;
            _definitionIndex = definitionIndex;
        }

        definition.Tag = NewTag;
    }

    internal override void Revert(CadDocument document)
    {
        AttributeDefinition definition = GetRetainedDefinition(document, NewTag);
        definition.Tag = _previousTag ?? throw new InvalidOperationException(
            "The attribute-tag command has not been applied.");
    }

    private AttributeDefinition GetRetainedDefinition(
        CadDocument document,
        string expectedTag)
    {
        Insert insert = _insert ?? throw new InvalidOperationException(
            "The attribute-tag command has not been applied.");
        BlockRecord block = _block ?? throw new InvalidOperationException(
            "The attribute-tag command has not been applied.");
        AttributeDefinition definition = _definition ??
            throw new InvalidOperationException(
                "The attribute-tag command has not been applied.");
        ValidateModelSpaceEntity(document, insert);
        if (!ReferenceEquals(insert.Block, block))
        {
            throw new InvalidOperationException(
                "The selected INSERT no longer references the retained block definition.");
        }

        int index = 0;
        foreach (AttributeDefinition current in block.AttributeDefinitions)
        {
            if (index++ != _definitionIndex)
            {
                continue;
            }
            if (!ReferenceEquals(current, definition) ||
                !string.Equals(
                    current.Tag,
                    expectedTag,
                    StringComparison.Ordinal))
            {
                break;
            }
            return definition;
        }
        throw new InvalidOperationException(
            "The retained attribute definition identity, order, or tag changed.");
    }

    private static AttributeDefinition ResolveDefinition(
        BlockRecord block,
        string tag,
        int occurrence,
        out int definitionIndex)
    {
        int index = 0;
        int currentOccurrence = 0;
        foreach (AttributeDefinition definition in block.AttributeDefinitions)
        {
            if (string.Equals(
                    definition.Tag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (currentOccurrence == occurrence)
                {
                    definitionIndex = index;
                    return definition;
                }
                currentOccurrence++;
            }
            index++;
        }
        throw new InvalidOperationException(
            $"Block '{block.Name}' has no attribute definition '{tag}' " +
            $"occurrence {occurrence}.");
    }
}

/// <summary>
/// Replaces the non-structural ATTDEF modes selected through a model-space
/// INSERT, tag, and zero-based duplicate-tag occurrence.
/// </summary>
/// <remarks>
/// Resolution is O(D) for D definitions. Apply, Undo, and Redo retain the exact
/// definition identity and mutate O(1) state. Constant and multiline ownership
/// bits are preserved; changing either requires a separate structural command.
/// Existing variable ATTRIB modes remain unchanged until explicit synchronization.
/// </remarks>
public sealed class CadSetAttributeDefinitionModesCommand : CadEditCommand
{
    public const int MaximumTagCodeUnits = 4_096;

    private const AttributeFlags EditableFlags =
        AttributeFlags.Hidden |
        AttributeFlags.Verify |
        AttributeFlags.Preset;

    private readonly ulong _insertHandle;
    private Insert? _insert;
    private BlockRecord? _block;
    private AttributeDefinition? _definition;
    private AttributeFlags _previousFlags;
    private AttributeFlags _updatedFlags;
    private bool _previousPositionLocked;

    public ulong InsertHandle => _insertHandle;

    public string Tag { get; }

    public int Occurrence { get; }

    public bool IsInvisible { get; }

    public bool IsVerifiable { get; }

    public bool IsPreset { get; }

    public bool IsPositionLocked { get; }

    public CadSetAttributeDefinitionModesCommand(
        ulong insertHandle,
        string tag,
        bool isInvisible,
        bool isVerifiable,
        bool isPreset,
        bool isPositionLocked,
        int occurrence = 0,
        string description = "Set attribute definition modes")
        : base(description)
    {
        if (insertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(insertHandle));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);
        if (tag.Length > MaximumTagCodeUnits)
        {
            throw new ArgumentException(
                "The attribute tag exceeds the command ownership budget.",
                nameof(tag));
        }

        _insertHandle = insertHandle;
        Tag = new string(tag.AsSpan());
        Occurrence = occurrence;
        IsInvisible = isInvisible;
        IsVerifiable = isVerifiable;
        IsPreset = isPreset;
        IsPositionLocked = isPositionLocked;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        AttributeDefinition definition;
        if (isRedo)
        {
            definition = GetRetainedDefinition(document);
        }
        else
        {
            Entity entity = ResolveModelSpaceEntity(document, _insertHandle);
            Insert insert = entity as Insert ?? throw new InvalidOperationException(
                $"Model-space entity handle {_insertHandle:X} is not an INSERT.");
            BlockRecord block = insert.Block ?? throw new InvalidOperationException(
                $"INSERT handle {_insertHandle:X} has no block definition.");
            definition = ResolveDefinition(block, Tag, Occurrence);
            _insert = insert;
            _block = block;
            _definition = definition;
            _previousFlags = definition.Flags;
            _previousPositionLocked = definition.IsLocked;
            _updatedFlags = BuildFlags(definition.Flags);
        }

        SetModesTransactional(
            definition,
            _updatedFlags,
            IsPositionLocked);
    }

    internal override void Revert(CadDocument document)
    {
        AttributeDefinition definition = GetRetainedDefinition(document);
        SetModesTransactional(
            definition,
            _previousFlags,
            _previousPositionLocked);
    }

    private AttributeFlags BuildFlags(AttributeFlags current)
    {
        AttributeFlags updated = current & ~EditableFlags;
        if (IsInvisible)
        {
            updated |= AttributeFlags.Hidden;
        }
        if (IsVerifiable)
        {
            updated |= AttributeFlags.Verify;
        }
        if (IsPreset)
        {
            updated |= AttributeFlags.Preset;
        }
        return updated;
    }

    private AttributeDefinition GetRetainedDefinition(CadDocument document)
    {
        Insert insert = _insert ?? throw new InvalidOperationException(
            "The attribute-modes command has not been applied.");
        BlockRecord block = _block ?? throw new InvalidOperationException(
            "The attribute-modes command has not been applied.");
        AttributeDefinition definition = _definition ??
            throw new InvalidOperationException(
                "The attribute-modes command has not been applied.");
        ValidateModelSpaceEntity(document, insert);
        if (!ReferenceEquals(insert.Block, block))
        {
            throw new InvalidOperationException(
                "The selected INSERT no longer references the retained block definition.");
        }
        AttributeDefinition current = ResolveDefinition(block, Tag, Occurrence);
        if (!ReferenceEquals(current, definition))
        {
            throw new InvalidOperationException(
                $"Attribute definition '{Tag}' occurrence {Occurrence} is no " +
                "longer the retained definition.");
        }
        return definition;
    }

    private static AttributeDefinition ResolveDefinition(
        BlockRecord block,
        string tag,
        int occurrence)
    {
        int currentOccurrence = 0;
        foreach (AttributeDefinition definition in block.AttributeDefinitions)
        {
            if (!string.Equals(
                    definition.Tag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (currentOccurrence == occurrence)
            {
                return definition;
            }
            currentOccurrence++;
        }
        throw new InvalidOperationException(
            $"Block '{block.Name}' has no attribute definition '{tag}' " +
            $"occurrence {occurrence}.");
    }

    private static void SetModesTransactional(
        AttributeDefinition definition,
        AttributeFlags flags,
        bool isPositionLocked)
    {
        AttributeFlags rollbackFlags = definition.Flags;
        bool rollbackPositionLocked = definition.IsLocked;
        try
        {
            definition.Flags = flags;
            definition.IsLocked = isPositionLocked;
        }
        catch
        {
            definition.Flags = rollbackFlags;
            definition.IsLocked = rollbackPositionLocked;
            throw;
        }
    }
}

/// <summary>
/// Changes one ATTDEF between reference-owned variable and definition-owned
/// constant storage, synchronizing every retained INSERT as one edit.
/// </summary>
/// <remarks>
/// First Apply, Undo, and Redo perform O(D * I + A + X) bounded work and retain
/// the same amount of state as block-attribute synchronization for D variable
/// definitions, I registered INSERTs, A original ATTRIBs, and X reference-owned
/// XData entries. Single-line versus multiline payload shape is preserved.
/// </remarks>
public sealed class CadSetAttributeDefinitionConstantModeCommand : CadEditCommand
{
    public const int MaximumTagCodeUnits = 4_096;

    private readonly ulong _insertHandle;
    private readonly CadSynchronizeBlockAttributePropertiesCommand _synchronization;
    private Insert? _insert;
    private BlockRecord? _block;
    private AttributeDefinition? _definition;
    private AttributeFlags _previousFlags;
    private AttributeFlags _updatedFlags;
    private AttributeType _previousType;
    private AttributeType _updatedType;

    public ulong InsertHandle => _insertHandle;

    public string Tag { get; }

    public int Occurrence { get; }

    public bool IsConstant { get; }

    public int InsertCount => _synchronization.InsertCount;

    public int AttributeCount => _synchronization.AttributeCount;

    public int AddedAttributeCount => _synchronization.AddedAttributeCount;

    public int RemovedAttributeCount => _synchronization.RemovedAttributeCount;

    public int ClearedExtendedDataEntryCount =>
        _synchronization.ClearedExtendedDataEntryCount;

    public CadSetAttributeDefinitionConstantModeCommand(
        ulong insertHandle,
        string tag,
        bool isConstant,
        int occurrence = 0,
        string description = "Set attribute definition constant mode")
        : base(description)
    {
        if (insertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(insertHandle));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);
        if (tag.Length > MaximumTagCodeUnits)
        {
            throw new ArgumentException(
                "The attribute tag exceeds the command ownership budget.",
                nameof(tag));
        }

        _insertHandle = insertHandle;
        Tag = new string(tag.AsSpan());
        Occurrence = occurrence;
        IsConstant = isConstant;
        _synchronization = new CadSynchronizeBlockAttributePropertiesCommand(
            insertHandle,
            description);
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            AttributeDefinition retained = GetRetainedDefinition(document);
            SetOwnershipTransactional(
                retained,
                _updatedFlags,
                _updatedType);
            try
            {
                _synchronization.Redo(document);
            }
            catch
            {
                SetOwnershipTransactional(
                    retained,
                    _previousFlags,
                    _previousType);
                throw;
            }
            return;
        }

        Entity entity = ResolveModelSpaceEntity(document, _insertHandle);
        Insert insert = entity as Insert ?? throw new InvalidOperationException(
            $"Model-space entity handle {_insertHandle:X} is not an INSERT.");
        BlockRecord block = insert.Block ?? throw new InvalidOperationException(
            $"INSERT handle {_insertHandle:X} has no block definition.");
        AttributeDefinition definition = ResolveDefinition(
            block,
            Tag,
            Occurrence);
        bool wasConstant = IsDefinitionOwned(definition);
        if (wasConstant == IsConstant)
        {
            throw new InvalidOperationException(
                $"Attribute definition '{Tag}' occurrence {Occurrence} is already " +
                (IsConstant ? "constant." : "variable."));
        }
        ValidatePayload(definition);

        _insert = insert;
        _block = block;
        _definition = definition;
        _previousFlags = definition.Flags;
        _previousType = definition.AttributeType;
        _updatedFlags = IsConstant
            ? definition.Flags | AttributeFlags.Constant
            : definition.Flags & ~AttributeFlags.Constant;
        _updatedType = definition.AttributeType switch
        {
            AttributeType.SingleLine => AttributeType.SingleLine,
            AttributeType.MultiLine or AttributeType.ConstantMultiLine =>
                IsConstant
                    ? AttributeType.ConstantMultiLine
                    : AttributeType.MultiLine,
            _ => throw new InvalidOperationException(
                $"Attribute definition '{Tag}' uses an unsupported attribute type."),
        };

        SetOwnershipTransactional(
            definition,
            _updatedFlags,
            _updatedType);
        try
        {
            _synchronization.ExecuteFirst(document);
        }
        catch
        {
            SetOwnershipTransactional(
                definition,
                _previousFlags,
                _previousType);
            throw;
        }
    }

    internal override void Revert(CadDocument document)
    {
        AttributeDefinition definition = GetRetainedDefinition(document);
        _synchronization.Undo(document);
        try
        {
            SetOwnershipTransactional(
                definition,
                _previousFlags,
                _previousType);
        }
        catch
        {
            _synchronization.Redo(document);
            throw;
        }
    }

    internal override void Discard(CadDocument document) =>
        _synchronization.Discard(document);

    private AttributeDefinition GetRetainedDefinition(CadDocument document)
    {
        Insert insert = _insert ?? throw new InvalidOperationException(
            "The attribute-constant command has not been applied.");
        BlockRecord block = _block ?? throw new InvalidOperationException(
            "The attribute-constant command has not been applied.");
        AttributeDefinition definition = _definition ??
            throw new InvalidOperationException(
                "The attribute-constant command has not been applied.");
        ValidateModelSpaceEntity(document, insert);
        if (!ReferenceEquals(insert.Block, block))
        {
            throw new InvalidOperationException(
                "The selected INSERT no longer references the retained block definition.");
        }
        AttributeDefinition current = ResolveDefinition(block, Tag, Occurrence);
        if (!ReferenceEquals(current, definition))
        {
            throw new InvalidOperationException(
                $"Attribute definition '{Tag}' occurrence {Occurrence} is no " +
                "longer the retained definition.");
        }
        return definition;
    }

    private static AttributeDefinition ResolveDefinition(
        BlockRecord block,
        string tag,
        int occurrence)
    {
        int currentOccurrence = 0;
        foreach (AttributeDefinition definition in block.AttributeDefinitions)
        {
            if (!string.Equals(
                    definition.Tag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (currentOccurrence == occurrence)
            {
                return definition;
            }
            currentOccurrence++;
        }
        throw new InvalidOperationException(
            $"Block '{block.Name}' has no attribute definition '{tag}' " +
            $"occurrence {occurrence}.");
    }

    private static bool IsDefinitionOwned(AttributeDefinition definition) =>
        (definition.Flags & AttributeFlags.Constant) != 0 ||
        definition.AttributeType == AttributeType.ConstantMultiLine;

    private static void ValidatePayload(AttributeDefinition definition)
    {
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

    private static void SetOwnershipTransactional(
        AttributeDefinition definition,
        AttributeFlags flags,
        AttributeType type)
    {
        AttributeFlags rollbackFlags = definition.Flags;
        AttributeType rollbackType = definition.AttributeType;
        try
        {
            definition.Flags = flags;
            definition.AttributeType = type;
        }
        catch
        {
            definition.Flags = rollbackFlags;
            definition.AttributeType = rollbackType;
            throw;
        }
    }
}

/// <summary>
/// Replaces one definition-owned constant ATTDEF value selected through a
/// model-space INSERT, tag, and zero-based duplicate-tag occurrence.
/// </summary>
/// <remarks>
/// Resolution is O(D) for D definitions in the selected INSERT block. Apply,
/// Undo, and Redo retain the exact definition identity and use O(1) mutation.
/// Existing INSERT references are not rewritten because constant values remain
/// definition-owned and snapshot expansion reads the retained ATTDEF.
/// </remarks>
public sealed class CadSetConstantAttributeDefinitionValueCommand : CadEditCommand
{
    public const int MaximumTagCodeUnits = 4_096;
    public const int MaximumValueCodeUnits =
        CadSnapshotOptions.DefaultMaxTextCodeUnitsPerEntity;

    private readonly ulong _insertHandle;
    private Insert? _insert;
    private BlockRecord? _block;
    private AttributeDefinition? _definition;
    private string? _previousValue;
    private string? _previousMTextValue;

    public ulong InsertHandle => _insertHandle;

    public string Tag { get; }

    public int Occurrence { get; }

    public string Value { get; }

    public CadSetConstantAttributeDefinitionValueCommand(
        ulong insertHandle,
        string tag,
        string value,
        int occurrence = 0,
        string description = "Set constant attribute definition value")
        : base(description)
    {
        if (insertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(insertHandle));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);
        if (tag.Length > MaximumTagCodeUnits)
        {
            throw new ArgumentException(
                "The attribute tag exceeds the command ownership budget.",
                nameof(tag));
        }
        if (value.Length > MaximumValueCodeUnits)
        {
            throw new ArgumentException(
                "The attribute value exceeds the snapshot per-entity text budget.",
                nameof(value));
        }

        _insertHandle = insertHandle;
        Tag = new string(tag.AsSpan());
        Value = new string(value.AsSpan());
        Occurrence = occurrence;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        AttributeDefinition definition;
        if (isRedo)
        {
            definition = GetRetainedDefinition(document);
        }
        else
        {
            Entity entity = ResolveModelSpaceEntity(document, _insertHandle);
            Insert insert = entity as Insert ?? throw new InvalidOperationException(
                $"Model-space entity handle {_insertHandle:X} is not an INSERT.");
            BlockRecord block = insert.Block ?? throw new InvalidOperationException(
                $"INSERT handle {_insertHandle:X} has no block definition.");
            definition = ResolveDefinition(block, Tag, Occurrence);
            if (!IsDefinitionOwned(definition))
            {
                throw new InvalidOperationException(
                    $"Attribute definition '{Tag}' occurrence {Occurrence} is " +
                    "variable and reference-owned.");
            }
            ValidatePayload(definition);
            _insert = insert;
            _block = block;
            _definition = definition;
            _previousValue = definition.Value;
            _previousMTextValue = definition.MText?.Value;
        }

        SetValueTransactional(definition, Value, Value);
    }

    internal override void Revert(CadDocument document)
    {
        AttributeDefinition definition = GetRetainedDefinition(document);
        string previous = _previousValue ?? throw new InvalidOperationException(
            "The constant attribute command has not been applied.");
        SetValueTransactional(definition, previous, _previousMTextValue);
    }

    private AttributeDefinition GetRetainedDefinition(CadDocument document)
    {
        Insert insert = _insert ?? throw new InvalidOperationException(
            "The constant attribute command has not been applied.");
        BlockRecord block = _block ?? throw new InvalidOperationException(
            "The constant attribute command has not been applied.");
        AttributeDefinition definition = _definition ??
            throw new InvalidOperationException(
                "The constant attribute command has not been applied.");
        ValidateModelSpaceEntity(document, insert);
        if (!ReferenceEquals(insert.Block, block))
        {
            throw new InvalidOperationException(
                "The selected INSERT no longer references the retained block definition.");
        }
        AttributeDefinition current = ResolveDefinition(block, Tag, Occurrence);
        if (!ReferenceEquals(current, definition))
        {
            throw new InvalidOperationException(
                $"Attribute definition '{Tag}' occurrence {Occurrence} is no " +
                "longer the retained definition.");
        }
        return definition;
    }

    private static AttributeDefinition ResolveDefinition(
        BlockRecord block,
        string tag,
        int occurrence)
    {
        int currentOccurrence = 0;
        foreach (AttributeDefinition definition in block.AttributeDefinitions)
        {
            if (!string.Equals(
                    definition.Tag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (currentOccurrence == occurrence)
            {
                return definition;
            }
            currentOccurrence++;
        }
        throw new InvalidOperationException(
            $"Block '{block.Name}' has no attribute definition '{tag}' " +
            $"occurrence {occurrence}.");
    }

    private static bool IsDefinitionOwned(AttributeDefinition definition) =>
        (definition.Flags & AttributeFlags.Constant) != 0 ||
        definition.AttributeType == AttributeType.ConstantMultiLine;

    private static void ValidatePayload(AttributeDefinition definition)
    {
        if (definition.AttributeType != AttributeType.SingleLine &&
            definition.MText is null)
        {
            throw new InvalidOperationException(
                $"Constant attribute definition '{definition.Tag}' has no " +
                "embedded MTEXT payload.");
        }
        if (definition.AttributeType is not (
            AttributeType.SingleLine or
            AttributeType.MultiLine or
            AttributeType.ConstantMultiLine))
        {
            throw new InvalidOperationException(
                $"Constant attribute definition '{definition.Tag}' uses an " +
                "unsupported attribute type.");
        }
    }

    private static void SetValueTransactional(
        AttributeDefinition definition,
        string value,
        string? mtextValue)
    {
        string rollbackValue = definition.Value;
        string? rollbackMTextValue = definition.MText?.Value;
        try
        {
            definition.Value = value;
            if (definition.MText is MText mtext)
            {
                mtext.Value = mtextValue ?? value;
            }
        }
        catch
        {
            definition.Value = rollbackValue;
            if (definition.MText is MText mtext && rollbackMTextValue is not null)
            {
                mtext.Value = rollbackMTextValue;
            }
            throw;
        }
    }
}

/// <summary>
/// Replaces one variable ATTDEF default selected through a model-space INSERT,
/// tag, and zero-based duplicate-tag occurrence.
/// </summary>
/// <remarks>
/// Resolution is O(D) for D definitions in the selected INSERT block. Apply,
/// Undo, and Redo retain the exact definition identity and use O(1) mutation.
/// Values already assigned to existing INSERT references are deliberately not
/// changed; a later INSERT created from the block receives the edited default.
/// </remarks>
public sealed class CadSetVariableAttributeDefinitionDefaultCommand : CadEditCommand
{
    public const int MaximumTagCodeUnits = 4_096;
    public const int MaximumValueCodeUnits =
        CadSnapshotOptions.DefaultMaxTextCodeUnitsPerEntity;

    private readonly ulong _insertHandle;
    private Insert? _insert;
    private BlockRecord? _block;
    private AttributeDefinition? _definition;
    private string? _previousValue;
    private string? _previousMTextValue;

    public ulong InsertHandle => _insertHandle;

    public string Tag { get; }

    public int Occurrence { get; }

    public string Value { get; }

    public CadSetVariableAttributeDefinitionDefaultCommand(
        ulong insertHandle,
        string tag,
        string value,
        int occurrence = 0,
        string description = "Set variable attribute definition default")
        : base(description)
    {
        if (insertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(insertHandle));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);
        if (tag.Length > MaximumTagCodeUnits)
        {
            throw new ArgumentException(
                "The attribute tag exceeds the command ownership budget.",
                nameof(tag));
        }
        if (value.Length > MaximumValueCodeUnits)
        {
            throw new ArgumentException(
                "The attribute value exceeds the snapshot per-entity text budget.",
                nameof(value));
        }

        _insertHandle = insertHandle;
        Tag = new string(tag.AsSpan());
        Value = new string(value.AsSpan());
        Occurrence = occurrence;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        AttributeDefinition definition;
        if (isRedo)
        {
            definition = GetRetainedDefinition(document);
        }
        else
        {
            Entity entity = ResolveModelSpaceEntity(document, _insertHandle);
            Insert insert = entity as Insert ?? throw new InvalidOperationException(
                $"Model-space entity handle {_insertHandle:X} is not an INSERT.");
            BlockRecord block = insert.Block ?? throw new InvalidOperationException(
                $"INSERT handle {_insertHandle:X} has no block definition.");
            definition = ResolveDefinition(block, Tag, Occurrence);
            if (IsDefinitionOwned(definition))
            {
                throw new InvalidOperationException(
                    $"Attribute definition '{Tag}' occurrence {Occurrence} is " +
                    "constant and does not own a variable default.");
            }
            ValidatePayload(definition);
            _insert = insert;
            _block = block;
            _definition = definition;
            _previousValue = definition.Value;
            _previousMTextValue = definition.MText?.Value;
        }

        SetValueTransactional(definition, Value, Value);
    }

    internal override void Revert(CadDocument document)
    {
        AttributeDefinition definition = GetRetainedDefinition(document);
        string previous = _previousValue ?? throw new InvalidOperationException(
            "The variable-default command has not been applied.");
        SetValueTransactional(definition, previous, _previousMTextValue);
    }

    private AttributeDefinition GetRetainedDefinition(CadDocument document)
    {
        Insert insert = _insert ?? throw new InvalidOperationException(
            "The variable-default command has not been applied.");
        BlockRecord block = _block ?? throw new InvalidOperationException(
            "The variable-default command has not been applied.");
        AttributeDefinition definition = _definition ??
            throw new InvalidOperationException(
                "The variable-default command has not been applied.");
        ValidateModelSpaceEntity(document, insert);
        if (!ReferenceEquals(insert.Block, block))
        {
            throw new InvalidOperationException(
                "The selected INSERT no longer references the retained block definition.");
        }
        AttributeDefinition current = ResolveDefinition(block, Tag, Occurrence);
        if (!ReferenceEquals(current, definition))
        {
            throw new InvalidOperationException(
                $"Attribute definition '{Tag}' occurrence {Occurrence} is no " +
                "longer the retained definition.");
        }
        return definition;
    }

    private static AttributeDefinition ResolveDefinition(
        BlockRecord block,
        string tag,
        int occurrence)
    {
        int currentOccurrence = 0;
        foreach (AttributeDefinition definition in block.AttributeDefinitions)
        {
            if (!string.Equals(
                    definition.Tag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (currentOccurrence == occurrence)
            {
                return definition;
            }
            currentOccurrence++;
        }
        throw new InvalidOperationException(
            $"Block '{block.Name}' has no attribute definition '{tag}' " +
            $"occurrence {occurrence}.");
    }

    private static bool IsDefinitionOwned(AttributeDefinition definition) =>
        (definition.Flags & AttributeFlags.Constant) != 0 ||
        definition.AttributeType == AttributeType.ConstantMultiLine;

    private static void ValidatePayload(AttributeDefinition definition)
    {
        if (definition.AttributeType != AttributeType.SingleLine &&
            definition.MText is null)
        {
            throw new InvalidOperationException(
                $"Variable attribute definition '{definition.Tag}' has no " +
                "embedded MTEXT payload.");
        }
        if (definition.AttributeType is not (
            AttributeType.SingleLine or AttributeType.MultiLine))
        {
            throw new InvalidOperationException(
                $"Variable attribute definition '{definition.Tag}' uses an " +
                "unsupported attribute type.");
        }
    }

    private static void SetValueTransactional(
        AttributeDefinition definition,
        string value,
        string? mtextValue)
    {
        string rollbackValue = definition.Value;
        string? rollbackMTextValue = definition.MText?.Value;
        try
        {
            definition.Value = value;
            if (definition.MText is MText mtext)
            {
                mtext.Value = mtextValue ?? value;
            }
        }
        catch
        {
            definition.Value = rollbackValue;
            if (definition.MText is MText mtext && rollbackMTextValue is not null)
            {
                mtext.Value = rollbackMTextValue;
            }
            throw;
        }
    }
}
