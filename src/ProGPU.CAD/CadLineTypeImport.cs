using ACadSharp;
using ACadSharp.Extensions;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

public enum CadLineTypeImportConflictPolicy
{
    Reject,
    ReplaceExisting,
}

public readonly record struct CadLineTypeImportResult(
    ulong ContentGeneration,
    int ImportedCount,
    int CreatedCount,
    int ReplacedCount,
    int UnsupportedCount);

/// <summary>
/// Imports a bounded detached LIN definition set as one reversible document
/// generation. Reload preserves existing LineType identities and references.
/// </summary>
/// <remarks>
/// Initial preflight/materialization is O(T + D + E) for T document text
/// styles, D definitions, and E descriptors. Apply/Undo/Redo are O(D + E).
/// Retained command storage is O(D + E). No scene, font, or file IO occurs
/// while the document edit lock is mutating ownership.
/// </remarks>
public sealed class CadImportLineTypesCommand : CadEditCommand
{
    public const int MaximumDefinitionCount = 4_096;

    private static readonly string[] ProtectedNames =
    [
        LineType.ByBlockName,
        LineType.ByLayerName,
        LineType.ContinuousName,
    ];

    private readonly CadLinDefinition[] _definitions;
    private readonly CadLineTypeImportConflictPolicy _conflictPolicy;
    private readonly ICadShxShapeResolver? _shapeResolver;
    private ImportItem[]? _items;
    private TextStyle[]? _createdShapeStyles;

    public int ImportedCount => _definitions.Length;

    public int CreatedCount { get; private set; }

    public int ReplacedCount { get; private set; }

    public int UnsupportedCount { get; }

    public ReadOnlyMemory<string> ImportedNames { get; }

    public CadImportLineTypesCommand(
        IEnumerable<CadLinDefinition> definitions,
        CadLineTypeImportConflictPolicy conflictPolicy,
        ICadShxShapeResolver? shapeResolver = null,
        string description = "Import LIN linetypes")
        : this(
            NormalizeDefinitions(definitions),
            conflictPolicy,
            shapeResolver,
            unsupportedCount: 0,
            description)
    {
    }

    private CadImportLineTypesCommand(
        CadLinDefinition[] definitions,
        CadLineTypeImportConflictPolicy conflictPolicy,
        ICadShxShapeResolver? shapeResolver,
        int unsupportedCount,
        string description)
        : base(description)
    {
        if (!Enum.IsDefined(conflictPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
        }
        foreach (CadLinDefinition definition in definitions)
        {
            if (!definition.IsImportSupported)
            {
                throw new NotSupportedException(
                    $"LIN definition '{definition.Name}' uses upright U= " +
                    "rotation, which the persisted ACadSharp segment flags " +
                    "cannot represent without changing semantics.");
            }
        }
        _definitions = definitions;
        _conflictPolicy = conflictPolicy;
        _shapeResolver = shapeResolver;
        UnsupportedCount = unsupportedCount;
        ImportedNames = definitions.Select(static definition => definition.Name).ToArray();
    }

    public static CadImportLineTypesCommand CaptureSupported(
        CadLinFile file,
        CadLineTypeImportConflictPolicy conflictPolicy,
        ICadShxShapeResolver? shapeResolver = null,
        string description = "Import LIN linetypes")
    {
        ArgumentNullException.ThrowIfNull(file);
        CadLinDefinition[] supported = file.Definitions.Span
            .ToArray()
            .Where(static definition => definition.IsImportSupported)
            .ToArray();
        if (supported.Length == 0)
        {
            throw new NotSupportedException(
                "The LIN library contains no importable definition; every " +
                "definition requires upright U= rotation.");
        }
        return new CadImportLineTypesCommand(
            supported,
            conflictPolicy,
            shapeResolver,
            file.DefinitionCount - supported.Length,
            description);
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (!isRedo)
        {
            Capture(document);
        }
        ImportItem[] items = _items ?? throw new InvalidOperationException(
            "The LIN import command has not been materialized.");
        TextStyle[] createdStyles = _createdShapeStyles ?? [];
        ValidateApplyState(document, items, createdStyles);

        int addedStyleCount = 0;
        int changedItemCount = 0;
        try
        {
            for (; addedStyleCount < createdStyles.Length; addedStyleCount++)
            {
                document.TextStyles.Add(createdStyles[addedStyleCount]);
            }
            for (; changedItemCount < items.Length; changedItemCount++)
            {
                ApplyItem(document, items[changedItemCount]);
            }
        }
        catch
        {
            RollBackAppliedPrefix(
                document,
                items,
                changedItemCount,
                createdStyles,
                addedStyleCount);
            throw;
        }
    }

    internal override void Revert(CadDocument document)
    {
        ImportItem[] items = _items ?? throw new InvalidOperationException(
            "The LIN import command has not been applied.");
        TextStyle[] createdStyles = _createdShapeStyles ?? [];
        ValidateRevertState(document, items, createdStyles);

        int revertedItemCount = 0;
        int removedStyleCount = 0;
        try
        {
            for (int i = items.Length - 1; i >= 0; i--)
            {
                RevertItem(document, items[i]);
                revertedItemCount++;
            }
            for (int i = createdStyles.Length - 1; i >= 0; i--)
            {
                TextStyle removed = document.TextStyles.Remove(createdStyles[i].Name) ??
                    throw new InvalidOperationException(
                        $"Created LIN shape style '{createdStyles[i].Name}' " +
                        "could not be removed.");
                if (!ReferenceEquals(removed, createdStyles[i]))
                {
                    throw new InvalidOperationException(
                        "Removing a LIN shape style returned a different table entry.");
                }
                removedStyleCount++;
            }
        }
        catch
        {
            RollBackRevertedSuffix(
                document,
                items,
                revertedItemCount,
                createdStyles,
                removedStyleCount);
            throw;
        }
    }

    private void Capture(CadDocument document)
    {
        var shapeStylesByFile = new Dictionary<string, TextStyle>(
            StringComparer.OrdinalIgnoreCase);
        foreach (TextStyle style in document.TextStyles)
        {
            if (style.IsShapeFile && !string.IsNullOrWhiteSpace(style.Filename))
            {
                shapeStylesByFile.TryAdd(
                    NormalizeShxFilename(style.Filename),
                    style);
            }
        }
        var reservedStyleNames = new HashSet<string>(
            document.TextStyles.Select(static style => style.Name),
            StringComparer.OrdinalIgnoreCase);
        var createdStyles = new List<TextStyle>();
        var items = new ImportItem[_definitions.Length];
        int createdCount = 0;
        int replacedCount = 0;
        for (int i = 0; i < _definitions.Length; i++)
        {
            CadLinDefinition definition = _definitions[i];
            ValidateDefinitionName(document, definition.Name);
            LineType? existing = null;
            if (document.LineTypes.TryGetValue(definition.Name, out LineType? candidate))
            {
                existing = candidate;
                if (_conflictPolicy == CadLineTypeImportConflictPolicy.Reject)
                {
                    throw new InvalidOperationException(
                        $"Document linetype '{definition.Name}' already exists.");
                }
                if ((existing.Flags & StandardFlags.XrefDependent) != 0)
                {
                    throw new InvalidOperationException(
                        $"Xref-dependent linetype '{definition.Name}' cannot be reloaded.");
                }
                replacedCount++;
            }
            else
            {
                createdCount++;
            }

            LineType.Segment[] segments = MaterializeSegments(
                document,
                definition,
                shapeStylesByFile,
                reservedStyleNames,
                createdStyles);
            if (existing is null)
            {
                var created = new LineType(definition.Name)
                {
                    Description = definition.Description,
                };
                foreach (LineType.Segment segment in segments)
                {
                    created.AddSegment(segment);
                }
                items[i] = ImportItem.Created(created);
            }
            else
            {
                items[i] = ImportItem.Replacement(
                    existing,
                    definition.Description,
                    segments);
            }
        }

        _items = items;
        _createdShapeStyles = createdStyles.ToArray();
        CreatedCount = createdCount;
        ReplacedCount = replacedCount;
    }

    private LineType.Segment[] MaterializeSegments(
        CadDocument document,
        CadLinDefinition definition,
        Dictionary<string, TextStyle> shapeStylesByFile,
        HashSet<string> reservedStyleNames,
        List<TextStyle> createdStyles)
    {
        ReadOnlySpan<CadLinElement> source = definition.Elements.Span;
        var segments = new LineType.Segment[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            CadLinElement element = source[i];
            if (!element.IsImportSupported)
            {
                throw new NotSupportedException(
                    $"LIN definition '{definition.Name}' uses upright U= rotation.");
            }
            var segment = new LineType.Segment
            {
                Length = element.Length,
                Scale = element.Scale,
                Rotation = element.RotationRadians,
                Offset = new XY(element.XOffset, element.YOffset),
            };
            if (element.RotationMode == CadLinRotationMode.Absolute)
            {
                segment.Flags |= LineTypeShapeFlags.RotationIsAbsolute;
            }
            if (element.Kind == CadLinElementKind.Text)
            {
                if (!document.TextStyles.TryGetValue(
                        element.StyleOrFileName,
                        out TextStyle? textStyle))
                {
                    throw new InvalidOperationException(
                        $"LIN definition '{definition.Name}' requires missing " +
                        $"text style '{element.StyleOrFileName}'.");
                }
                segment.Flags |= LineTypeShapeFlags.Text;
                segment.Text = element.Payload;
                segment.Style = textStyle;
            }
            else if (element.Kind == CadLinElementKind.Shape)
            {
                ICadShxShapeResolver resolver = _shapeResolver ??
                    throw new InvalidOperationException(
                        $"LIN definition '{definition.Name}' requires SHX shape " +
                        $"'{element.Payload}' from '{element.StyleOrFileName}', " +
                        "but no shape resolver is available.");
                string filename = NormalizeShxFilename(element.StyleOrFileName);
                CadShxShapeResolution resolution = resolver.ResolveShape(
                    new CadShxShapeRequest(element.Payload, 0, filename));
                if (resolution.GlyphCache is null ||
                    resolution.ShapeNumber == 0 ||
                    resolution.ShapeNumber > short.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"LIN definition '{definition.Name}' cannot resolve SHX " +
                        $"shape '{element.Payload}' from '{filename}'.");
                }
                if (!shapeStylesByFile.TryGetValue(filename, out TextStyle? shapeStyle))
                {
                    shapeStyle = new TextStyle(CreateShapeStyleName(
                        filename,
                        document.Header.Version,
                        reservedStyleNames))
                    {
                        Filename = filename,
                        Flags = StyleFlags.IsShape,
                    };
                    shapeStylesByFile.Add(filename, shapeStyle);
                    createdStyles.Add(shapeStyle);
                }
                segment.Flags |= LineTypeShapeFlags.Shape;
                segment.ShapeNumber = checked((short)resolution.ShapeNumber);
                segment.Style = shapeStyle;
            }
            segments[i] = segment;
        }
        return segments;
    }

    private static void ApplyItem(CadDocument document, ImportItem item)
    {
        if (item.IsCreated)
        {
            document.LineTypes.Add(item.LineType);
            return;
        }
        item.SwapDefinition();
    }

    private static void RevertItem(CadDocument document, ImportItem item)
    {
        if (item.IsCreated)
        {
            LineType removed = document.LineTypes.Remove(item.LineType.Name) ??
                throw new InvalidOperationException(
                    $"Imported linetype '{item.LineType.Name}' could not be removed.");
            if (!ReferenceEquals(removed, item.LineType))
            {
                throw new InvalidOperationException(
                    "Removing an imported linetype returned a different table entry.");
            }
            return;
        }
        item.SwapDefinition();
    }

    private static void ValidateApplyState(
        CadDocument document,
        ReadOnlySpan<ImportItem> items,
        ReadOnlySpan<TextStyle> createdStyles)
    {
        foreach (TextStyle style in createdStyles)
        {
            if (style.Owner is not null || style.Handle != 0 ||
                document.TextStyles.Contains(style.Name))
            {
                throw new InvalidOperationException(
                    $"Retained LIN shape style '{style.Name}' is not detached.");
            }
        }
        foreach (ImportItem item in items)
        {
            if (item.IsCreated)
            {
                if (item.LineType.Owner is not null || item.LineType.Handle != 0 ||
                    document.LineTypes.Contains(item.LineType.Name))
                {
                    throw new InvalidOperationException(
                        $"Retained imported linetype '{item.LineType.Name}' is not detached.");
                }
            }
            else
            {
                ValidateRegisteredLineType(document, item.LineType);
            }
        }
    }

    private static void ValidateRevertState(
        CadDocument document,
        ReadOnlySpan<ImportItem> items,
        ReadOnlySpan<TextStyle> createdStyles)
    {
        foreach (TextStyle style in createdStyles)
        {
            if (!document.TextStyles.TryGetValue(style.Name, out TextStyle? registered) ||
                !ReferenceEquals(registered, style))
            {
                throw new InvalidOperationException(
                    $"Retained LIN shape style '{style.Name}' is no longer registered.");
            }
        }
        foreach (ImportItem item in items)
        {
            ValidateRegisteredLineType(document, item.LineType);
        }
    }

    private static void ValidateRegisteredLineType(
        CadDocument document,
        LineType lineType)
    {
        if (!document.LineTypes.TryGetValue(lineType.Name, out LineType? registered) ||
            !ReferenceEquals(registered, lineType))
        {
            throw new InvalidOperationException(
                $"Retained linetype '{lineType.Name}' is no longer registered.");
        }
    }

    private static void RollBackAppliedPrefix(
        CadDocument document,
        ImportItem[] items,
        int changedItemCount,
        TextStyle[] styles,
        int addedStyleCount)
    {
        for (int i = changedItemCount - 1; i >= 0; i--)
        {
            if (!items[i].IsCreated)
            {
                RevertItem(document, items[i]);
            }
        }
        for (int i = items.Length - 1; i >= 0; i--)
        {
            ImportItem item = items[i];
            if (item.IsCreated &&
                document.LineTypes.TryGetValue(
                    item.LineType.Name,
                    out LineType? registered) &&
                ReferenceEquals(registered, item.LineType))
            {
                document.LineTypes.Remove(item.LineType.Name);
            }
        }
        _ = addedStyleCount;
        for (int i = styles.Length - 1; i >= 0; i--)
        {
            if (document.TextStyles.TryGetValue(styles[i].Name, out TextStyle? registered) &&
                ReferenceEquals(registered, styles[i]))
            {
                document.TextStyles.Remove(styles[i].Name);
            }
        }
    }

    private static void RollBackRevertedSuffix(
        CadDocument document,
        ImportItem[] items,
        int revertedItemCount,
        TextStyle[] styles,
        int removedStyleCount)
    {
        _ = removedStyleCount;
        for (int i = 0; i < styles.Length; i++)
        {
            if (styles[i].Owner is null && styles[i].Handle == 0 &&
                !document.TextStyles.Contains(styles[i].Name))
            {
                document.TextStyles.Add(styles[i]);
            }
        }
        int firstRevertedItem = items.Length - revertedItemCount;
        for (int i = firstRevertedItem; i < items.Length; i++)
        {
            if (!items[i].IsCreated)
            {
                ApplyItem(document, items[i]);
            }
        }
        foreach (ImportItem item in items)
        {
            if (item.IsCreated && item.LineType.Owner is null &&
                item.LineType.Handle == 0 &&
                !document.LineTypes.Contains(item.LineType.Name))
            {
                document.LineTypes.Add(item.LineType);
            }
        }
    }

    private static CadLinDefinition[] NormalizeDefinitions(
        IEnumerable<CadLinDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        CadLinDefinition[] bounded = definitions
            .Take(MaximumDefinitionCount + 1)
            .ToArray();
        if (bounded.Length == 0)
        {
            throw new ArgumentException(
                "At least one LIN definition is required.",
                nameof(definitions));
        }
        if (bounded.Length > MaximumDefinitionCount)
        {
            throw new ArgumentException(
                $"LIN import supports at most {MaximumDefinitionCount:N0} definitions.",
                nameof(definitions));
        }
        if (bounded.Any(static definition => definition is null))
        {
            throw new ArgumentException(
                "LIN definitions cannot contain null entries.",
                nameof(definitions));
        }
        if (bounded.Select(static definition => definition.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != bounded.Length)
        {
            throw new ArgumentException(
                "LIN definition names must be distinct.",
                nameof(definitions));
        }
        return bounded;
    }

    private static void ValidateDefinitionName(CadDocument document, string name)
    {
        if (ProtectedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Protected linetype '{name}' cannot be loaded or redefined.");
        }
        if (!new LineType(name).HasValidDxfName(document.Header.Version))
        {
            throw new ArgumentException(
                $"Linetype name '{name}' is not valid for " +
                $"{document.Header.Version} DXF/DWG persistence.");
        }
    }

    private static string NormalizeShxFilename(string value)
    {
        string normalized = value.Trim().Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        if (slash >= 0)
        {
            normalized = normalized[(slash + 1)..];
        }
        if (normalized.Length == 0 ||
            !normalized.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"LIN SHX filename '{value}' is invalid.");
        }
        return normalized;
    }

    private static string CreateShapeStyleName(
        string filename,
        ACadVersion version,
        HashSet<string> reservedNames)
    {
        string stem = filename[..^4];
        var builder = new System.Text.StringBuilder(stem.Length + 8);
        builder.Append("LIN_");
        foreach (char value in stem)
        {
            builder.Append(
                INamedCadObjectExtensions.InvalidCharacters.Contains(value)
                    ? '_'
                    : value);
        }
        string prefix = builder.ToString();
        int maxLength = version <= ACadVersion.AC1015 ? 31 : 255;
        if (prefix.Length > maxLength - 5)
        {
            prefix = prefix[..(maxLength - 5)];
        }
        for (int suffix = 0; suffix < MaximumDefinitionCount; suffix++)
        {
            string candidate = suffix == 0
                ? prefix
                : $"{prefix}_{suffix}";
            if (candidate.Length <= maxLength && reservedNames.Add(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException(
            $"No bounded text-style name is available for SHX file '{filename}'.");
    }

    private sealed class ImportItem
    {
        public LineType LineType { get; }

        public bool IsCreated { get; }

        private LineType.Segment[] _detachedSegments;
        private string _detachedDescription;

        private ImportItem(
            LineType lineType,
            bool isCreated,
            string detachedDescription,
            LineType.Segment[] detachedSegments)
        {
            LineType = lineType;
            IsCreated = isCreated;
            _detachedDescription = detachedDescription;
            _detachedSegments = detachedSegments;
        }

        public static ImportItem Created(LineType lineType) =>
            new(lineType, true, string.Empty, []);

        public static ImportItem Replacement(
            LineType lineType,
            string description,
            LineType.Segment[] segments) =>
            new(lineType, false, description, segments);

        public void SwapDefinition()
        {
            _detachedSegments = LineType.ReplaceSegments(_detachedSegments);
            (LineType.Description, _detachedDescription) =
                (_detachedDescription, LineType.Description);
        }
    }
}
