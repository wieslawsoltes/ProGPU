using ACadSharp;
using ACadSharp.Objects;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// Applies one named page setup to an existing layout as a reversible edit.
/// </summary>
/// <remarks>
/// The command copies only the fixed plot-settings contract. Layout identity,
/// block ownership, tab order, limits, extents, UCS, and viewport state remain
/// untouched. Construction is O(N) time and storage for at most 8,192 copied
/// target/source name code units. Apply, Undo, and Redo are O(1) time and
/// retain a fixed number of plot values and existing immutable strings.
/// </remarks>
public sealed class CadApplyNamedPageSetupCommand : CadEditCommand
{
    public const int MaximumNameCodeUnits = 4_096;

    private readonly string _targetLayoutName;
    private readonly string _namedPageSetupName;
    private Layout? _layout;
    private CadPlotSettingsState? _previousState;
    private CadPlotSettingsState? _appliedState;

    public string TargetLayoutName => _targetLayoutName;

    public string NamedPageSetupName => _namedPageSetupName;

    public CadApplyNamedPageSetupCommand(
        string targetLayoutName,
        string namedPageSetupName,
        string description = "Apply named page setup")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLayoutName);
        ArgumentException.ThrowIfNullOrWhiteSpace(namedPageSetupName);
        if (targetLayoutName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The target layout name exceeds the command ownership budget.",
                nameof(targetLayoutName));
        }
        if (namedPageSetupName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The named page-setup name exceeds the command ownership budget.",
                nameof(namedPageSetupName));
        }
        _targetLayoutName = new string(targetLayoutName.AsSpan());
        _namedPageSetupName = new string(namedPageSetupName.AsSpan());
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            Layout retainedLayout = GetRetainedLayout(document);
            CadPlotSettingsState redoState = _appliedState ??
                throw new InvalidOperationException(
                    "The page-setup command has not been applied.");
            CadPlotSettingsState.ApplyTransactional(retainedLayout, redoState);
            return;
        }

        Layout layout = ResolveLayout(document, _targetLayoutName);
        PlotSettings namedPageSetup = ResolveNamedPageSetup(
            document,
            _namedPageSetupName);
        bool layoutTargetsModel = !layout.IsPaperSpace;
        bool setupTargetsModel =
            (namedPageSetup.Flags & PlotFlags.ModelType) != 0;
        if (layoutTargetsModel != setupTargetsModel)
        {
            throw new InvalidOperationException(
                $"Named page setup '{_namedPageSetupName}' targets " +
                $"{(setupTargetsModel ? "model" : "paper")} space and cannot be " +
                $"applied to {(layoutTargetsModel ? "model" : "paper")}-space " +
                $"layout '{_targetLayoutName}'.");
        }

        CadPlotSettingsState previous = CadPlotSettingsState.Capture(layout);
        CadPlotSettingsState appliedState = CadPlotSettingsState.Capture(namedPageSetup)
            .WithModelType(layoutTargetsModel);
        CadPlotSettingsState.ApplyTransactional(layout, appliedState);
        _layout = layout;
        _previousState = previous;
        _appliedState = appliedState;
    }

    internal override void Revert(CadDocument document)
    {
        Layout layout = GetRetainedLayout(document);
        CadPlotSettingsState previous = _previousState ??
            throw new InvalidOperationException(
                "The page-setup command has not been applied.");
        CadPlotSettingsState.ApplyTransactional(layout, previous);
    }

    private Layout GetRetainedLayout(CadDocument document)
    {
        Layout layout = _layout ??
            throw new InvalidOperationException(
                "The page-setup command has not been applied.");
        Layout current = ResolveLayout(document, _targetLayoutName);
        if (!ReferenceEquals(layout, current))
        {
            throw new InvalidOperationException(
                $"Layout '{_targetLayoutName}' is no longer the retained layout.");
        }
        return layout;
    }

    private static Layout ResolveLayout(CadDocument document, string name)
    {
        if (document.Layouts is null ||
            !document.Layouts.TryGet(name, out Layout layout))
        {
            throw new InvalidOperationException(
                $"Layout '{name}' does not exist.");
        }
        return layout;
    }

    private static PlotSettings ResolveNamedPageSetup(
        CadDocument document,
        string name)
    {
        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups) ||
            !pageSetups.TryGetEntry(name, out PlotSettings pageSetup) ||
            pageSetup is Layout)
        {
            throw new InvalidOperationException(
                $"Named page setup '{name}' does not exist.");
        }
        return pageSetup;
    }

}

/// <summary>
/// Replaces one named page setup's fixed plot contract from an existing layout
/// while preserving the named setup's identity.
/// </summary>
/// <remarks>
/// Construction is O(N) time and storage for at most 8,192 copied name code
/// units. Apply, Undo, and Redo are transactional O(1) operations retaining two
/// fixed plot-value records and existing immutable strings.
/// </remarks>
public sealed class CadUpdateNamedPageSetupFromLayoutCommand : CadEditCommand
{
    public const int MaximumNameCodeUnits = 4_096;

    private readonly string _sourceLayoutName;
    private readonly string _targetPageSetupName;
    private PlotSettings? _targetPageSetup;
    private CadPlotSettingsState? _previousState;
    private CadPlotSettingsState? _appliedState;

    public string SourceLayoutName => _sourceLayoutName;

    public string TargetPageSetupName => _targetPageSetupName;

    public CadUpdateNamedPageSetupFromLayoutCommand(
        string sourceLayoutName,
        string targetPageSetupName,
        string description = "Update named page setup from layout")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLayoutName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPageSetupName);
        if (sourceLayoutName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The source layout name exceeds the command ownership budget.",
                nameof(sourceLayoutName));
        }
        if (targetPageSetupName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The target page-setup name exceeds the command ownership budget.",
                nameof(targetPageSetupName));
        }
        _sourceLayoutName = new string(sourceLayoutName.AsSpan());
        _targetPageSetupName = new string(targetPageSetupName.AsSpan());
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            PlotSettings retained = GetRetainedPageSetup(document);
            CadPlotSettingsState redoState = _appliedState ??
                throw new InvalidOperationException(
                    "The page-setup update command has not been applied.");
            CadPlotSettingsState.ApplyTransactional(retained, redoState);
            return;
        }

        Layout source = ResolveLayout(document, _sourceLayoutName);
        PlotSettings target = ResolveNamedPageSetup(
            document,
            _targetPageSetupName);
        bool sourceTargetsModel = !source.IsPaperSpace;
        bool targetTargetsModel = (target.Flags & PlotFlags.ModelType) != 0;
        if (sourceTargetsModel != targetTargetsModel)
        {
            throw new InvalidOperationException(
                $"Named page setup '{_targetPageSetupName}' targets " +
                $"{(targetTargetsModel ? "model" : "paper")} space and cannot be " +
                $"updated from {(sourceTargetsModel ? "model" : "paper")}-space " +
                $"layout '{_sourceLayoutName}'.");
        }

        CadPlotSettingsState previous = CadPlotSettingsState.Capture(target);
        CadPlotSettingsState applied = CadPlotSettingsState.Capture(source)
            .WithModelType(sourceTargetsModel)
            .WithPageName(target.PageName);
        CadPlotSettingsState.ApplyTransactional(target, applied);
        _targetPageSetup = target;
        _previousState = previous;
        _appliedState = applied;
    }

    internal override void Revert(CadDocument document)
    {
        PlotSettings retained = GetRetainedPageSetup(document);
        CadPlotSettingsState previous = _previousState ??
            throw new InvalidOperationException(
                "The page-setup update command has not been applied.");
        CadPlotSettingsState.ApplyTransactional(retained, previous);
    }

    private PlotSettings GetRetainedPageSetup(CadDocument document)
    {
        PlotSettings retained = _targetPageSetup ??
            throw new InvalidOperationException(
                "The page-setup update command has not been applied.");
        PlotSettings current = ResolveNamedPageSetup(
            document,
            _targetPageSetupName);
        if (!ReferenceEquals(retained, current))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_targetPageSetupName}' is no longer the retained setup.");
        }
        return retained;
    }

    private static Layout ResolveLayout(CadDocument document, string name)
    {
        if (document.Layouts is null ||
            !document.Layouts.TryGet(name, out Layout layout))
        {
            throw new InvalidOperationException(
                $"Layout '{name}' does not exist.");
        }
        return layout;
    }

    private static PlotSettings ResolveNamedPageSetup(
        CadDocument document,
        string name)
    {
        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups) ||
            !pageSetups.TryGetEntry(name, out PlotSettings pageSetup) ||
            pageSetup is Layout)
        {
            throw new InvalidOperationException(
                $"Named page setup '{name}' does not exist.");
        }
        return pageSetup;
    }
}

/// <summary>
/// Renames one named page setup and its referring layout markers atomically.
/// </summary>
/// <remarks>
/// Construction is O(N) for two bounded owned names. First Apply scans O(L)
/// layouts and retains at most 4,096 exact layout references and their existing
/// immutable page-name strings. Undo and Redo are O(R), where R is the number
/// of referring layouts. Plot values, layout geometry, ownership, and document
/// handles remain unchanged.
/// </remarks>
public sealed class CadRenameNamedPageSetupCommand : CadEditCommand
{
    public const int MaximumNameCodeUnits = 4_096;
    public const int MaximumReferencedLayoutCount = 4_096;

    private readonly string _oldName;
    private readonly string _newName;
    private CadDictionary? _pageSetups;
    private PlotSettings? _pageSetup;
    private string? _previousPageName;
    private Layout[]? _referencedLayouts;
    private string[]? _previousLayoutPageNames;

    public string OldName => _oldName;

    public string NewName => _newName;

    /// <summary>The retained setup after the command is first applied.</summary>
    public PlotSettings? RenamedPageSetup => _pageSetup;

    public CadRenameNamedPageSetupCommand(
        string oldName,
        string newName,
        string description = "Rename named page setup")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (oldName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The current page-setup name exceeds the command ownership budget.",
                nameof(oldName));
        }
        if (newName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The new page-setup name exceeds the command ownership budget.",
                nameof(newName));
        }
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The new page-setup name must be distinct from the current name.",
                nameof(newName));
        }
        _oldName = new string(oldName.AsSpan());
        _newName = new string(newName.AsSpan());
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        PlotSettings pageSetup = ResolveNamedPageSetup(pageSetups, _oldName);
        EnsureNameAvailable(pageSetups, _newName);

        if (isRedo)
        {
            ValidateRetainedState(
                document,
                pageSetups,
                pageSetup,
                expectRenamed: false);
            RenameTransactional(forward: true);
            return;
        }

        Layout[] layouts = FindReferencedLayouts(document, pageSetup);
        var layoutPageNames = new string[layouts.Length];
        for (int i = 0; i < layouts.Length; i++)
        {
            layoutPageNames[i] = layouts[i].PageName;
        }
        _pageSetups = pageSetups;
        _pageSetup = pageSetup;
        _previousPageName = pageSetup.PageName;
        _referencedLayouts = layouts;
        _previousLayoutPageNames = layoutPageNames;
        RenameTransactional(forward: true);
    }

    internal override void Revert(CadDocument document)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        PlotSettings pageSetup = ResolveNamedPageSetup(pageSetups, _newName);
        EnsureNameAvailable(pageSetups, _oldName);
        ValidateRetainedState(
            document,
            pageSetups,
            pageSetup,
            expectRenamed: true);
        RenameTransactional(forward: false);
    }

    private void RenameTransactional(bool forward)
    {
        PlotSettings pageSetup = _pageSetup ??
            throw new InvalidOperationException(
                "The page-setup rename command has not been applied.");
        Layout[] layouts = _referencedLayouts ??
            throw new InvalidOperationException(
                "The page-setup rename command has not captured its layouts.");
        string[] previousLayoutPageNames = _previousLayoutPageNames ??
            throw new InvalidOperationException(
                "The page-setup rename command has not captured layout names.");
        string desiredObjectName = forward ? _newName : _oldName;
        string desiredPageName = forward
            ? _newName
            : _previousPageName!;
        int changedLayoutCount = 0;
        bool objectNameChanged = false;
        bool pageNameChanged = false;
        try
        {
            pageSetup.Name = desiredObjectName;
            objectNameChanged = true;
            pageSetup.PageName = desiredPageName;
            pageNameChanged = true;
            for (; changedLayoutCount < layouts.Length; changedLayoutCount++)
            {
                layouts[changedLayoutCount].PageName = forward
                    ? _newName
                    : previousLayoutPageNames[changedLayoutCount];
            }
        }
        catch (Exception renameException)
        {
            try
            {
                for (int i = changedLayoutCount - 1; i >= 0; i--)
                {
                    layouts[i].PageName = forward
                        ? previousLayoutPageNames[i]
                        : _newName;
                }
                if (pageNameChanged)
                {
                    pageSetup.PageName = forward
                        ? _previousPageName!
                        : _newName;
                }
                if (objectNameChanged)
                {
                    pageSetup.Name = forward ? _oldName : _newName;
                }
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "Renaming the page setup failed and its rollback also failed.",
                    new AggregateException(renameException, rollbackException));
            }
            throw;
        }
    }

    private void ValidateRetainedState(
        CadDocument document,
        CadDictionary pageSetups,
        PlotSettings pageSetup,
        bool expectRenamed)
    {
        if (!ReferenceEquals(_pageSetups, pageSetups) ||
            !ReferenceEquals(_pageSetup, pageSetup))
        {
            throw new InvalidOperationException(
                "The named page setup is no longer the retained setup.");
        }
        Layout[] layouts = _referencedLayouts ??
            throw new InvalidOperationException(
                "The page-setup rename command has not captured its layouts.");
        string[] previousLayoutPageNames = _previousLayoutPageNames ??
            throw new InvalidOperationException(
                "The page-setup rename command has not captured layout names.");
        string expectedPageName = expectRenamed
            ? _newName
            : _previousPageName!;
        if (!string.Equals(
            pageSetup.PageName,
            expectedPageName,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The retained page setup's page name changed outside the edit history.");
        }
        for (int i = 0; i < layouts.Length; i++)
        {
            Layout layout = layouts[i];
            if (document.Layouts is null ||
                !document.Layouts.TryGet(layout.Name, out Layout current) ||
                !ReferenceEquals(layout, current))
            {
                throw new InvalidOperationException(
                    $"Layout '{layout.Name}' is no longer the retained layout.");
            }
            string expectedLayoutPageName = expectRenamed
                ? _newName
                : previousLayoutPageNames[i];
            if (!string.Equals(
                layout.PageName,
                expectedLayoutPageName,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Layout '{layout.Name}' changed its page setup outside the edit history.");
            }
        }
    }

    private static Layout[] FindReferencedLayouts(
        CadDocument document,
        PlotSettings pageSetup)
    {
        if (document.Layouts is null)
        {
            return [];
        }
        var layouts = new List<Layout>();
        foreach (Layout layout in document.Layouts)
        {
            if (!string.Equals(
                    layout.PageName,
                    pageSetup.Name,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    layout.PageName,
                    pageSetup.PageName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (layouts.Count == MaximumReferencedLayoutCount)
            {
                throw new InvalidOperationException(
                    $"Named page setup '{pageSetup.Name}' exceeds the retained-layout budget.");
            }
            layouts.Add(layout);
        }
        return layouts.ToArray();
    }

    private static void EnsureNameAvailable(
        CadDictionary pageSetups,
        string name)
    {
        if (pageSetups.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"Named page setup '{name}' already exists.");
        }
    }

    private static PlotSettings ResolveNamedPageSetup(
        CadDictionary pageSetups,
        string name)
    {
        if (!pageSetups.TryGetEntry(name, out PlotSettings pageSetup) ||
            pageSetup is Layout)
        {
            throw new InvalidOperationException(
                $"Named page setup '{name}' does not exist.");
        }
        return pageSetup;
    }

    private static CadDictionary ResolvePageSetupDictionary(CadDocument document)
    {
        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups))
        {
            throw new InvalidOperationException(
                "The document has no ACAD_PLOTSETTINGS dictionary.");
        }
        return pageSetups;
    }
}

/// <summary>Controls collisions while importing named page setups.</summary>
public enum CadPageSetupImportConflictPolicy : byte
{
    /// <summary>Reject the entire import before mutating any target entry.</summary>
    Reject,

    /// <summary>Replace the fixed plot state of existing named entries.</summary>
    ReplaceExisting,
}

/// <summary>Summarizes one completed named page-setup import edit.</summary>
public readonly record struct CadPageSetupImportResult(
    ulong ContentGeneration,
    int ImportedCount,
    int CreatedCount,
    int ReplacedCount);

/// <summary>
/// Imports a bounded detached snapshot of named page setups as one reversible edit.
/// </summary>
/// <remarks>
/// Capture is O(I + S) time and storage for I imported setups and S owned string
/// code units. Apply, Undo, and Redo are O(I). Existing target entries preserve
/// object identity and handles; newly created entries retain object identity
/// across Undo/Redo and receive a fresh handle when reattached. Layouts are not
/// changed implicitly. The command never retains the source document or session.
/// </remarks>
public sealed class CadImportNamedPageSetupsCommand : CadEditCommand
{
    public const int MaximumSetupCount = 4_096;
    public const int MaximumStringCodeUnits = 4_096;
    public const int MaximumTotalStringCodeUnits = 1_048_576;

    private readonly ImportedEntry[] _imports;
    private readonly CadPageSetupImportConflictPolicy _conflictPolicy;
    private CadDictionary? _pageSetups;
    private TargetEntry[]? _targets;

    public CadPageSetupImportConflictPolicy ConflictPolicy => _conflictPolicy;

    public int ImportedCount => _imports.Length;

    public int CreatedCount { get; private set; }

    public int ReplacedCount { get; private set; }

    private CadImportNamedPageSetupsCommand(
        ImportedEntry[] imports,
        CadPageSetupImportConflictPolicy conflictPolicy,
        string description)
        : base(description)
    {
        if (!Enum.IsDefined(conflictPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
        }
        _imports = imports;
        _conflictPolicy = conflictPolicy;
    }

    /// <summary>Captures every standalone named page setup from a source session.</summary>
    public static CadImportNamedPageSetupsCommand CaptureAll(
        CadDocumentSession source,
        CadPageSetupImportConflictPolicy conflictPolicy =
            CadPageSetupImportConflictPolicy.Reject,
        string description = "Import named page setups")
    {
        ArgumentNullException.ThrowIfNull(source);
        return new CadImportNamedPageSetupsCommand(
            source.Read(CaptureAllEntries),
            conflictPolicy,
            description);
    }

    /// <summary>Captures an explicit case-insensitive subset from a source session.</summary>
    public static CadImportNamedPageSetupsCommand Capture(
        CadDocumentSession source,
        IEnumerable<string> pageSetupNames,
        CadPageSetupImportConflictPolicy conflictPolicy =
            CadPageSetupImportConflictPolicy.Reject,
        string description = "Import named page setups")
    {
        ArgumentNullException.ThrowIfNull(source);
        string[] names = OwnSelectedNames(pageSetupNames);
        return new CadImportNamedPageSetupsCommand(
            source.Read(document => CaptureSelectedEntries(document, names)),
            conflictPolicy,
            description);
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        if (isRedo)
        {
            ValidateRetainedDictionary(pageSetups);
            TargetEntry[] targets = GetTargets();
            ValidateForwardState(pageSetups, targets);
            ApplyForwardTransactional(pageSetups, targets);
            return;
        }

        TargetEntry[] capturedTargets = PreflightFirstApply(pageSetups);
        ApplyForwardTransactional(pageSetups, capturedTargets);
        _pageSetups = pageSetups;
        _targets = capturedTargets;
        CreatedCount = capturedTargets.Count(static target => target.IsCreated);
        ReplacedCount = capturedTargets.Length - CreatedCount;
    }

    internal override void Revert(CadDocument document)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        ValidateRetainedDictionary(pageSetups);
        TargetEntry[] targets = GetTargets();
        ValidateReverseState(pageSetups, targets);
        ApplyReverseTransactional(pageSetups, targets);
    }

    private TargetEntry[] PreflightFirstApply(CadDictionary pageSetups)
    {
        var targets = new TargetEntry[_imports.Length];
        for (int i = 0; i < _imports.Length; i++)
        {
            ImportedEntry imported = _imports[i];
            if (pageSetups.TryGetEntry(
                    imported.Name,
                    out NonGraphicalObject current))
            {
                if (current is not PlotSettings existing || current is Layout)
                {
                    throw new InvalidOperationException(
                        $"Target entry '{imported.Name}' is not a named PLOTSETTINGS object.");
                }
                if (_conflictPolicy == CadPageSetupImportConflictPolicy.Reject)
                {
                    throw new InvalidOperationException(
                        $"Named page setup '{imported.Name}' already exists; " +
                        "the import was not applied.");
                }
                targets[i] = new TargetEntry(
                    existing.Name,
                    imported.State.WithPageName(existing.Name),
                    existing,
                    CadPlotSettingsState.Capture(existing),
                    IsCreated: false);
                continue;
            }

            var created = new PlotSettings(imported.Name);
            imported.State
                .WithPageName(imported.Name)
                .ApplyTo(created);
            targets[i] = new TargetEntry(
                imported.Name,
                CadPlotSettingsState.Capture(created),
                created,
                default,
                IsCreated: true);
        }
        return targets;
    }

    private static void ApplyForwardTransactional(
        CadDictionary pageSetups,
        TargetEntry[] targets)
    {
        int completed = 0;
        try
        {
            for (; completed < targets.Length; completed++)
            {
                TargetEntry target = targets[completed];
                if (target.IsCreated)
                {
                    AddTransactional(pageSetups, target.PageSetup);
                }
                else
                {
                    CadPlotSettingsState.ApplyTransactional(
                        target.PageSetup,
                        target.ImportedState);
                }
            }
        }
        catch (Exception applyException)
        {
            try
            {
                for (int i = completed - 1; i >= 0; i--)
                {
                    ApplyReverse(pageSetups, targets[i]);
                }
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "Importing page setups failed and its rollback also failed.",
                    new AggregateException(applyException, rollbackException));
            }
            throw;
        }
    }

    private static void ApplyReverseTransactional(
        CadDictionary pageSetups,
        TargetEntry[] targets)
    {
        int index = targets.Length - 1;
        try
        {
            for (; index >= 0; index--)
            {
                ApplyReverse(pageSetups, targets[index]);
            }
        }
        catch (Exception revertException)
        {
            try
            {
                for (int i = index + 1; i < targets.Length; i++)
                {
                    TargetEntry target = targets[i];
                    if (target.IsCreated)
                    {
                        AddTransactional(pageSetups, target.PageSetup);
                    }
                    else
                    {
                        CadPlotSettingsState.ApplyTransactional(
                            target.PageSetup,
                            target.ImportedState);
                    }
                }
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "Undoing the page-setup import failed and its rollback also failed.",
                    new AggregateException(revertException, rollbackException));
            }
            throw;
        }
    }

    private static void ApplyReverse(
        CadDictionary pageSetups,
        TargetEntry target)
    {
        if (!target.IsCreated)
        {
            CadPlotSettingsState.ApplyTransactional(
                target.PageSetup,
                target.PreviousState);
            return;
        }
        if (!pageSetups.Remove(target.Name, out NonGraphicalObject removed) ||
            !ReferenceEquals(target.PageSetup, removed))
        {
            throw new InvalidOperationException(
                $"Imported page setup '{target.Name}' could not be removed.");
        }
    }

    private static void ValidateForwardState(
        CadDictionary pageSetups,
        TargetEntry[] targets)
    {
        foreach (TargetEntry target in targets)
        {
            if (target.IsCreated)
            {
                if (pageSetups.ContainsKey(target.Name) ||
                    target.PageSetup.Owner is not null ||
                    target.PageSetup.Handle != 0)
                {
                    throw new InvalidOperationException(
                        $"Imported page setup '{target.Name}' is not detached for Redo.");
                }
            }
            else
            {
                EnsureRetainedEntry(pageSetups, target);
            }
        }
    }

    private static void ValidateReverseState(
        CadDictionary pageSetups,
        TargetEntry[] targets)
    {
        foreach (TargetEntry target in targets)
        {
            EnsureRetainedEntry(pageSetups, target);
        }
    }

    private static void EnsureRetainedEntry(
        CadDictionary pageSetups,
        TargetEntry target)
    {
        if (!pageSetups.TryGetEntry(target.Name, out PlotSettings current) ||
            current is Layout ||
            !ReferenceEquals(target.PageSetup, current))
        {
            throw new InvalidOperationException(
                $"Named page setup '{target.Name}' is no longer the retained setup.");
        }
    }

    private void ValidateRetainedDictionary(CadDictionary pageSetups)
    {
        if (!ReferenceEquals(_pageSetups, pageSetups))
        {
            throw new InvalidOperationException(
                "The ACAD_PLOTSETTINGS dictionary is no longer the retained dictionary.");
        }
    }

    private TargetEntry[] GetTargets() => _targets ??
        throw new InvalidOperationException(
            "The page-setup import command has not been applied.");

    private static ImportedEntry[] CaptureAllEntries(CadDocument document)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        var entries = new List<ImportedEntry>();
        var ownership = new StringOwnershipBudget();
        foreach (NonGraphicalObject entry in pageSetups)
        {
            if (entry is not PlotSettings pageSetup || entry is Layout)
            {
                continue;
            }
            EnsureSetupCapacity(entries.Count);
            entries.Add(CaptureEntry(pageSetup, ownership));
        }
        EnsureNotEmpty(entries.Count);
        entries.Sort(ImportedEntryComparer.Instance);
        return entries.ToArray();
    }

    private static ImportedEntry[] CaptureSelectedEntries(
        CadDocument document,
        string[] names)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        var entries = new ImportedEntry[names.Length];
        var ownership = new StringOwnershipBudget();
        for (int i = 0; i < names.Length; i++)
        {
            if (!pageSetups.TryGetEntry(names[i], out PlotSettings pageSetup) ||
                pageSetup is Layout)
            {
                throw new InvalidOperationException(
                    $"Source named page setup '{names[i]}' does not exist.");
            }
            entries[i] = CaptureEntry(pageSetup, ownership);
        }
        Array.Sort(entries, ImportedEntryComparer.Instance);
        return entries;
    }

    private static ImportedEntry CaptureEntry(
        PlotSettings pageSetup,
        StringOwnershipBudget ownership)
    {
        string name = ownership.CopyRequired(pageSetup.Name, "page-setup name");
        CadPlotSettingsState state = CadPlotSettingsState.Capture(pageSetup)
            .CopyStrings(ownership.CopyOptional);
        return new ImportedEntry(name, state);
    }

    private static string[] OwnSelectedNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var owned = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalCodeUnits = 0;
        foreach (string name in names)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            EnsureSetupCapacity(owned.Count);
            if (name.Length > MaximumStringCodeUnits ||
                totalCodeUnits > MaximumTotalStringCodeUnits - name.Length)
            {
                throw new ArgumentException(
                    "Selected page-setup names exceed the import ownership budget.",
                    nameof(names));
            }
            string copy = new(name.AsSpan());
            if (!unique.Add(copy))
            {
                throw new ArgumentException(
                    $"Selected page-setup name '{copy}' is duplicated.",
                    nameof(names));
            }
            totalCodeUnits += copy.Length;
            owned.Add(copy);
        }
        EnsureNotEmpty(owned.Count);
        return owned.ToArray();
    }

    private static void EnsureSetupCapacity(int count)
    {
        if (count >= MaximumSetupCount)
        {
            throw new InvalidDataException(
                "The source exceeds the named page-setup import count budget.");
        }
    }

    private static void EnsureNotEmpty(int count)
    {
        if (count == 0)
        {
            throw new InvalidOperationException(
                "The source contains no selected named page setups.");
        }
    }

    private static CadDictionary ResolvePageSetupDictionary(CadDocument document)
    {
        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups))
        {
            throw new InvalidOperationException(
                "The document has no ACAD_PLOTSETTINGS dictionary.");
        }
        return pageSetups;
    }

    private static void AddTransactional(
        CadDictionary pageSetups,
        PlotSettings pageSetup)
    {
        try
        {
            pageSetups.Add(pageSetup);
        }
        catch
        {
            if (pageSetups.TryGetEntry(pageSetup.Name, out PlotSettings current) &&
                ReferenceEquals(pageSetup, current))
            {
                pageSetups.Remove(pageSetup.Name);
            }
            throw;
        }
    }

    private readonly record struct ImportedEntry(
        string Name,
        CadPlotSettingsState State);

    private readonly record struct TargetEntry(
        string Name,
        CadPlotSettingsState ImportedState,
        PlotSettings PageSetup,
        CadPlotSettingsState PreviousState,
        bool IsCreated);

    private sealed class ImportedEntryComparer : IComparer<ImportedEntry>
    {
        public static ImportedEntryComparer Instance { get; } = new();

        public int Compare(ImportedEntry left, ImportedEntry right)
        {
            int insensitive = StringComparer.OrdinalIgnoreCase.Compare(
                left.Name,
                right.Name);
            return insensitive != 0
                ? insensitive
                : StringComparer.Ordinal.Compare(left.Name, right.Name);
        }
    }

    private sealed class StringOwnershipBudget
    {
        private int _totalCodeUnits;

        public string CopyRequired(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException(
                    $"The source {field} is empty.");
            }
            return Copy(value, field)!;
        }

        public string? CopyOptional(string? value) =>
            Copy(value, "plot-settings string");

        private string? Copy(string? value, string field)
        {
            if (value is null)
            {
                return null;
            }
            if (value.Length > MaximumStringCodeUnits ||
                _totalCodeUnits > MaximumTotalStringCodeUnits - value.Length)
            {
                throw new InvalidDataException(
                    $"The source {field} exceeds the import ownership budget.");
            }
            _totalCodeUnits += value.Length;
            return new string(value.AsSpan());
        }
    }
}

/// <summary>
/// Removes one unassigned named page setup as a reversible document edit.
/// </summary>
/// <remarks>
/// Construction is O(N) time and storage for one bounded owned name. Apply and
/// Redo preflight O(L) layouts before O(1) dictionary removal, where L is the
/// layout count. Undo is O(1). The detached setup object is retained exactly;
/// ACadSharp assigns it a fresh document handle when Undo reattaches it.
/// </remarks>
public sealed class CadDeleteNamedPageSetupCommand : CadEditCommand
{
    public const int MaximumNameCodeUnits = 4_096;

    private readonly string _pageSetupName;
    private CadDictionary? _pageSetups;
    private PlotSettings? _deletedPageSetup;

    public string PageSetupName => _pageSetupName;

    /// <summary>The retained detached setup after Apply or Redo.</summary>
    public PlotSettings? DeletedPageSetup => _deletedPageSetup;

    public CadDeleteNamedPageSetupCommand(
        string pageSetupName,
        string description = "Delete named page setup")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageSetupName);
        if (pageSetupName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The page-setup name exceeds the command ownership budget.",
                nameof(pageSetupName));
        }
        _pageSetupName = new string(pageSetupName.AsSpan());
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        PlotSettings pageSetup = ResolveNamedPageSetup(
            pageSetups,
            _pageSetupName);
        if (isRedo)
        {
            if (!ReferenceEquals(_pageSetups, pageSetups) ||
                !ReferenceEquals(_deletedPageSetup, pageSetup))
            {
                throw new InvalidOperationException(
                    $"Named page setup '{_pageSetupName}' is no longer the retained setup.");
            }
        }

        EnsureNotAssignedToLayout(document, _pageSetupName);
        if (!pageSetups.Remove(_pageSetupName, out NonGraphicalObject removed) ||
            !ReferenceEquals(pageSetup, removed))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_pageSetupName}' could not be removed.");
        }

        if (!isRedo)
        {
            _pageSetups = pageSetups;
            _deletedPageSetup = pageSetup;
        }
    }

    internal override void Revert(CadDocument document)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        if (!ReferenceEquals(_pageSetups, pageSetups))
        {
            throw new InvalidOperationException(
                "The ACAD_PLOTSETTINGS dictionary is no longer the retained dictionary.");
        }
        if (pageSetups.ContainsKey(_pageSetupName))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_pageSetupName}' already exists.");
        }
        PlotSettings retained = _deletedPageSetup ??
            throw new InvalidOperationException(
                "The page-setup delete command has not been applied.");
        if (retained.Owner is not null || retained.Handle != 0)
        {
            throw new InvalidOperationException(
                $"Named page setup '{_pageSetupName}' is not detached.");
        }

        AddTransactional(pageSetups, retained);
    }

    private static void EnsureNotAssignedToLayout(
        CadDocument document,
        string pageSetupName)
    {
        if (document.Layouts is null)
        {
            return;
        }
        foreach (Layout layout in document.Layouts)
        {
            if (string.Equals(
                layout.PageName,
                pageSetupName,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Named page setup '{pageSetupName}' is assigned to layout " +
                    $"'{layout.Name}' and cannot be deleted.");
            }
        }
    }

    private static PlotSettings ResolveNamedPageSetup(
        CadDictionary pageSetups,
        string name)
    {
        if (!pageSetups.TryGetEntry(name, out PlotSettings pageSetup) ||
            pageSetup is Layout)
        {
            throw new InvalidOperationException(
                $"Named page setup '{name}' does not exist.");
        }
        return pageSetup;
    }

    private static CadDictionary ResolvePageSetupDictionary(CadDocument document)
    {
        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups))
        {
            throw new InvalidOperationException(
                "The document has no ACAD_PLOTSETTINGS dictionary.");
        }
        return pageSetups;
    }

    private static void AddTransactional(
        CadDictionary pageSetups,
        PlotSettings pageSetup)
    {
        try
        {
            pageSetups.Add(pageSetup);
        }
        catch
        {
            if (pageSetups.TryGetEntry(pageSetup.Name, out PlotSettings current) &&
                ReferenceEquals(pageSetup, current))
            {
                pageSetups.Remove(pageSetup.Name);
            }
            throw;
        }
    }
}

/// <summary>
/// Creates one named page setup from an existing layout as a reversible edit.
/// </summary>
/// <remarks>
/// The created object owns a fixed copy of the source layout's plot contract
/// and never retains layout geometry. Construction is O(N) time and storage for
/// at most 8,192 source/new-name code units. Apply, Undo, and Redo are O(1) and
/// retain one bounded PLOTSETTINGS object.
/// </remarks>
public sealed class CadCreateNamedPageSetupCommand : CadEditCommand
{
    public const int MaximumNameCodeUnits = 4_096;

    private readonly string _sourceLayoutName;
    private readonly string _newPageSetupName;
    private CadDictionary? _pageSetups;
    private PlotSettings? _createdPageSetup;

    public string SourceLayoutName => _sourceLayoutName;

    public string NewPageSetupName => _newPageSetupName;

    /// <summary>The retained setup after the command is first applied.</summary>
    public PlotSettings? CreatedPageSetup => _createdPageSetup;

    public CadCreateNamedPageSetupCommand(
        string sourceLayoutName,
        string newPageSetupName,
        string description = "Create named page setup")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLayoutName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPageSetupName);
        if (sourceLayoutName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The source layout name exceeds the command ownership budget.",
                nameof(sourceLayoutName));
        }
        if (newPageSetupName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The new page-setup name exceeds the command ownership budget.",
                nameof(newPageSetupName));
        }
        _sourceLayoutName = new string(sourceLayoutName.AsSpan());
        _newPageSetupName = new string(newPageSetupName.AsSpan());
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        EnsureNameAvailable(pageSetups);

        if (isRedo)
        {
            if (!ReferenceEquals(_pageSetups, pageSetups))
            {
                throw new InvalidOperationException(
                    "The ACAD_PLOTSETTINGS dictionary is no longer the retained dictionary.");
            }
            PlotSettings retained = _createdPageSetup ??
                throw new InvalidOperationException(
                    "The page-setup command has not been applied.");
            if (retained.Owner is not null || retained.Handle != 0)
            {
                throw new InvalidOperationException(
                    $"Named page setup '{_newPageSetupName}' is not detached.");
            }
            AddTransactional(pageSetups, retained);
            return;
        }

        Layout source = ResolveLayout(document, _sourceLayoutName);
        bool modelType = !source.IsPaperSpace;
        var created = new PlotSettings(_newPageSetupName);
        CadPlotSettingsState.Capture(source)
            .WithModelType(modelType)
            .WithPageName(_newPageSetupName)
            .ApplyTo(created);
        AddTransactional(pageSetups, created);
        _pageSetups = pageSetups;
        _createdPageSetup = created;
    }

    internal override void Revert(CadDocument document)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        if (!ReferenceEquals(_pageSetups, pageSetups))
        {
            throw new InvalidOperationException(
                "The ACAD_PLOTSETTINGS dictionary is no longer the retained dictionary.");
        }
        PlotSettings created = _createdPageSetup ??
            throw new InvalidOperationException(
                "The page-setup command has not been applied.");
        if (!pageSetups.TryGetEntry(_newPageSetupName, out PlotSettings current) ||
            !ReferenceEquals(created, current))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_newPageSetupName}' is no longer the retained setup.");
        }
        if (!pageSetups.Remove(_newPageSetupName, out NonGraphicalObject removed) ||
            !ReferenceEquals(created, removed))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_newPageSetupName}' could not be removed.");
        }
    }

    private void EnsureNameAvailable(CadDictionary pageSetups)
    {
        if (pageSetups.ContainsKey(_newPageSetupName))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_newPageSetupName}' already exists.");
        }
    }

    private static void AddTransactional(
        CadDictionary pageSetups,
        PlotSettings pageSetup)
    {
        try
        {
            pageSetups.Add(pageSetup);
        }
        catch
        {
            if (pageSetups.TryGetEntry(pageSetup.Name, out PlotSettings current) &&
                ReferenceEquals(pageSetup, current))
            {
                pageSetups.Remove(pageSetup.Name);
            }
            throw;
        }
    }

    private static Layout ResolveLayout(CadDocument document, string name)
    {
        if (document.Layouts is null ||
            !document.Layouts.TryGet(name, out Layout layout))
        {
            throw new InvalidOperationException(
                $"Layout '{name}' does not exist.");
        }
        return layout;
    }

    private static CadDictionary ResolvePageSetupDictionary(CadDocument document)
    {
        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups))
        {
            throw new InvalidOperationException(
                "The document has no ACAD_PLOTSETTINGS dictionary.");
        }
        return pageSetups;
    }
}

internal readonly record struct CadPlotSettingsState(
    double DenominatorScale,
    PlotFlags Flags,
    double NumeratorScale,
    string? PageName,
    double PaperHeight,
    XY PaperImageOrigin,
    double PaperImageOriginX,
    double PaperImageOriginY,
    PlotRotation PaperRotation,
    string? PaperSize,
    PlotPaperUnits PaperUnits,
    double PaperWidth,
    double PlotOriginX,
    double PlotOriginY,
    PlotType PlotType,
    string? PlotViewName,
    ScaledType ScaledFit,
    short ShadePlotDpi,
    ulong ShadePlotIdHandle,
    ShadePlotMode ShadePlotMode,
    ShadePlotResolutionMode ShadePlotResolutionMode,
    double StandardScale,
    string? StyleSheet,
    string? SystemPrinterName,
    PaperMargin UnprintableMargin,
    double WindowLowerLeftX,
    double WindowLowerLeftY,
    double WindowUpperLeftX,
    double WindowUpperLeftY)
{
    public static CadPlotSettingsState Capture(PlotSettings source) => new(
        source.DenominatorScale,
        source.Flags,
        source.NumeratorScale,
        source.PageName,
        source.PaperHeight,
        source.PaperImageOrigin,
        source.PaperImageOriginX,
        source.PaperImageOriginY,
        source.PaperRotation,
        source.PaperSize,
        source.PaperUnits,
        source.PaperWidth,
        source.PlotOriginX,
        source.PlotOriginY,
        source.PlotType,
        source.PlotViewName,
        source.ScaledFit,
        source.ShadePlotDPI,
        source.ShadePlotIDHandle,
        source.ShadePlotMode,
        source.ShadePlotResolutionMode,
        source.StandardScale,
        source.StyleSheet,
        source.SystemPrinterName,
        source.UnprintableMargin,
        source.WindowLowerLeftX,
        source.WindowLowerLeftY,
        source.WindowUpperLeftX,
        source.WindowUpperLeftY);

    public CadPlotSettingsState WithModelType(bool modelType) => this with
    {
        Flags = modelType
            ? Flags | PlotFlags.ModelType
            : Flags & ~PlotFlags.ModelType,
    };

    public CadPlotSettingsState WithPageName(string pageName) => this with
    {
        PageName = pageName,
    };

    public CadPlotSettingsState CopyStrings(
        Func<string?, string?> copy) => this with
    {
        PageName = copy(PageName),
        PaperSize = copy(PaperSize),
        PlotViewName = copy(PlotViewName),
        StyleSheet = copy(StyleSheet),
        SystemPrinterName = copy(SystemPrinterName),
    };

    public static void ApplyTransactional(
        PlotSettings target,
        CadPlotSettingsState desired)
    {
        CadPlotSettingsState rollback = Capture(target);
        try
        {
            desired.ApplyTo(target);
        }
        catch (Exception applyException)
        {
            try
            {
                rollback.ApplyTo(target);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "Applying page setup failed and its rollback also failed.",
                    new AggregateException(applyException, rollbackException));
            }
            throw;
        }
    }

    public void ApplyTo(PlotSettings target)
    {
        target.DenominatorScale = DenominatorScale;
        target.Flags = Flags;
        target.NumeratorScale = NumeratorScale;
        target.PageName = PageName!;
        target.PaperHeight = PaperHeight;
        target.PaperImageOrigin = PaperImageOrigin;
        target.PaperImageOriginX = PaperImageOriginX;
        target.PaperImageOriginY = PaperImageOriginY;
        target.PaperRotation = PaperRotation;
        target.PaperSize = PaperSize!;
        target.PaperUnits = PaperUnits;
        target.PaperWidth = PaperWidth;
        target.PlotOriginX = PlotOriginX;
        target.PlotOriginY = PlotOriginY;
        target.PlotType = PlotType;
        target.PlotViewName = PlotViewName!;
        target.ScaledFit = ScaledFit;
        target.ShadePlotDPI = ShadePlotDpi;
        target.ShadePlotIDHandle = ShadePlotIdHandle;
        target.ShadePlotMode = ShadePlotMode;
        target.ShadePlotResolutionMode = ShadePlotResolutionMode;
        target.StandardScale = StandardScale;
        target.StyleSheet = StyleSheet!;
        target.SystemPrinterName = SystemPrinterName!;
        target.UnprintableMargin = UnprintableMargin;
        target.WindowLowerLeftX = WindowLowerLeftX;
        target.WindowLowerLeftY = WindowLowerLeftY;
        target.WindowUpperLeftX = WindowUpperLeftX;
        target.WindowUpperLeftY = WindowUpperLeftY;
    }
}
