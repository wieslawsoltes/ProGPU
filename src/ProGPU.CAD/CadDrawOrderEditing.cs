using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;

namespace ProGPU.CAD;

/// <summary>
/// Selects how model-space entities are repositioned in persisted back-to-front
/// draw order.
/// </summary>
public enum CadDrawOrderPlacement : byte
{
    /// <summary>Places the selected entities in front of every other entity.</summary>
    BringToFront = 0,

    /// <summary>Places the selected entities behind every other entity.</summary>
    SendToBack = 1,

    /// <summary>Places the selected entities immediately above the reference set.</summary>
    BringAbove = 2,

    /// <summary>Places the selected entities immediately under the reference set.</summary>
    SendUnder = 3,
}

/// <summary>
/// Atomically rewrites persisted model-space draw order while preserving the
/// relative order of the selected and unselected entity subsequences.
/// </summary>
/// <remarks>
/// The command first resolves and validates the complete effective model-space
/// order, computes one stable permutation, and then replaces ACAD_SORTENTS with
/// unique compact ascending keys. Undo restores the exact prior sparse pairs or
/// removes a table created by this command. Work and retained history storage
/// are O(E + S) after the resolver's O(E + S + E log E) validation for E model
/// entities and S prior sparse entries.
/// </remarks>
public sealed class CadSetModelSpaceDrawOrderCommand : CadEditCommand
{
    public const int DefaultMaximumSelectionCount = 65_536;
    public const int DefaultMaximumModelSpaceEntityCount =
        CadSnapshotOptions.DefaultMaxExpandedEntities;

    private readonly ulong[] _handles;
    private readonly ulong[] _referenceHandles;
    private PersistedDrawOrderState? _previousState;
    private PersistedDrawOrderState? _appliedState;
    private Entity[]? _retainedModelEntities;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public ReadOnlyMemory<ulong> ReferenceHandles => _referenceHandles;

    public CadDrawOrderPlacement Placement { get; }

    public int MaximumSelectionCount { get; }

    public int MaximumModelSpaceEntityCount { get; }

    public CadSetModelSpaceDrawOrderCommand(
        IEnumerable<ulong> handles,
        CadDrawOrderPlacement placement,
        IEnumerable<ulong>? referenceHandles = null,
        string description = "Change model-space draw order",
        int maximumSelectionCount = DefaultMaximumSelectionCount,
        int maximumModelSpaceEntityCount = DefaultMaximumModelSpaceEntityCount)
        : base(description)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSelectionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumModelSpaceEntityCount);
        if (placement is not
            (CadDrawOrderPlacement.BringToFront or
             CadDrawOrderPlacement.SendToBack or
             CadDrawOrderPlacement.BringAbove or
             CadDrawOrderPlacement.SendUnder))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        _handles = NormalizeHandles(
            handles,
            maximumSelectionCount,
            nameof(handles));
        _referenceHandles = referenceHandles is null
            ? []
            : NormalizeHandles(
                referenceHandles,
                maximumSelectionCount,
                nameof(referenceHandles),
                allowEmpty: true);
        if (_referenceHandles.Length > maximumSelectionCount - _handles.Length)
        {
            throw new ArgumentException(
                $"The combined selected and reference entity sets exceed the configured limit of {maximumSelectionCount} unique handles.",
                nameof(referenceHandles));
        }

        bool needsReferences = placement is
            CadDrawOrderPlacement.BringAbove or
            CadDrawOrderPlacement.SendUnder;
        if (needsReferences != (_referenceHandles.Length != 0))
        {
            throw new ArgumentException(
                needsReferences
                    ? "Above and under placement require at least one reference entity."
                    : "Front and back placement do not accept reference entities.",
                nameof(referenceHandles));
        }

        var selected = new HashSet<ulong>(_handles);
        if (_referenceHandles.Any(selected.Contains))
        {
            throw new ArgumentException(
                "Selected and reference entity sets must not overlap.",
                nameof(referenceHandles));
        }

        Placement = placement;
        MaximumSelectionCount = maximumSelectionCount;
        MaximumModelSpaceEntityCount = maximumModelSpaceEntityCount;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            Entity[] retained = GetRetainedModelEntities(document);
            PersistedDrawOrderState redoState = _appliedState ??
                throw new InvalidOperationException(
                    "The draw-order command has not been applied.");
            ValidateCompleteOrder(document.ModelSpace, retained);
            ReplaceStateTransactional(document.ModelSpace, redoState);
            return;
        }

        BlockRecord modelSpace = document.ModelSpace;
        if (modelSpace.Entities.Count > MaximumModelSpaceEntityCount)
        {
            throw new InvalidOperationException(
                $"Model space contains {modelSpace.Entities.Count} entities, exceeding the configured draw-order limit of {MaximumModelSpaceEntityCount}.");
        }

        Entity[] selected = ResolveModelSpaceEntities(document, _handles);
        Entity[] references = ResolveModelSpaceEntitiesForReference(
            document,
            _referenceHandles);
        CadDrawOrderResolution resolution = CadDrawOrderResolver.Resolve(
            modelSpace,
            applySortOrder: true);
        PersistedDrawOrderState previous = CaptureState(modelSpace);
        Entity[] desired = BuildOrder(
            resolution.Entities,
            selected,
            references,
            Placement);
        PersistedDrawOrderState appliedState = CreateCanonicalState(desired);

        ReplaceStateTransactional(modelSpace, appliedState);
        _previousState = previous;
        _appliedState = appliedState;
        _retainedModelEntities = desired;
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] retained = GetRetainedModelEntities(document);
        PersistedDrawOrderState previous = _previousState ??
            throw new InvalidOperationException(
                "The draw-order command has not been applied.");
        ValidateCompleteOrder(document.ModelSpace, retained);
        ReplaceStateTransactional(document.ModelSpace, previous);
    }

    private Entity[] GetRetainedModelEntities(CadDocument document)
    {
        Entity[] retained = _retainedModelEntities ??
            throw new InvalidOperationException(
                "The draw-order command has not been applied.");
        ValidateModelSpaceEntities(document, retained);
        return retained;
    }

    private static Entity[] BuildOrder(
        Entity[] source,
        Entity[] selected,
        Entity[] references,
        CadDrawOrderPlacement placement)
    {
        var selectedSet = new HashSet<Entity>(
            selected,
            ReferenceEqualityComparer.Instance);
        var referenceSet = new HashSet<Entity>(
            references,
            ReferenceEqualityComparer.Instance);
        var selectedInOrder = new Entity[selected.Length];
        var unselected = new Entity[source.Length - selected.Length];
        int selectedCount = 0;
        int unselectedCount = 0;
        foreach (Entity entity in source)
        {
            if (selectedSet.Contains(entity))
            {
                selectedInOrder[selectedCount++] = entity;
            }
            else
            {
                unselected[unselectedCount++] = entity;
            }
        }

        if (selectedCount != selected.Length ||
            unselectedCount != unselected.Length)
        {
            throw new InvalidOperationException(
                "The resolved model-space order does not contain the complete selection exactly once.");
        }

        int insertionIndex = placement switch
        {
            CadDrawOrderPlacement.BringToFront => unselected.Length,
            CadDrawOrderPlacement.SendToBack => 0,
            CadDrawOrderPlacement.BringAbove =>
                FindFrontmostReference(unselected, referenceSet) + 1,
            CadDrawOrderPlacement.SendUnder =>
                FindBackmostReference(unselected, referenceSet),
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };

        var result = new Entity[source.Length];
        Array.Copy(unselected, 0, result, 0, insertionIndex);
        Array.Copy(
            selectedInOrder,
            0,
            result,
            insertionIndex,
            selectedInOrder.Length);
        Array.Copy(
            unselected,
            insertionIndex,
            result,
            insertionIndex + selectedInOrder.Length,
            unselected.Length - insertionIndex);
        return result;
    }

    private static int FindFrontmostReference(
        Entity[] entities,
        HashSet<Entity> references)
    {
        for (int i = entities.Length - 1; i >= 0; i--)
        {
            if (references.Contains(entities[i]))
            {
                return i;
            }
        }
        throw new InvalidOperationException(
            "The resolved model-space order does not contain every reference entity.");
    }

    private static int FindBackmostReference(
        Entity[] entities,
        HashSet<Entity> references)
    {
        for (int i = 0; i < entities.Length; i++)
        {
            if (references.Contains(entities[i]))
            {
                return i;
            }
        }
        throw new InvalidOperationException(
            "The resolved model-space order does not contain every reference entity.");
    }

    private static PersistedDrawOrderState CreateCanonicalState(Entity[] order)
    {
        var entries = new PersistedDrawOrderEntry[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            entries[i] = new PersistedDrawOrderEntry(
                order[i],
                checked((ulong)i + 1UL));
        }
        return new PersistedDrawOrderState(Exists: true, Entries: entries);
    }

    private static PersistedDrawOrderState CaptureState(BlockRecord block)
    {
        SortEntitiesTable? table = block.SortEntitiesTable;
        if (table is null)
        {
            return new PersistedDrawOrderState(Exists: false, Entries: []);
        }

        return new PersistedDrawOrderState(
            Exists: true,
            table.Select(static sorter => new PersistedDrawOrderEntry(
                sorter.Entity,
                sorter.SortHandle)).ToArray());
    }

    private static void ReplaceStateTransactional(
        BlockRecord block,
        PersistedDrawOrderState target)
    {
        PersistedDrawOrderState rollback = CaptureState(block);
        try
        {
            ReplaceState(block, target);
        }
        catch (Exception replacementError)
        {
            try
            {
                ReplaceState(block, rollback);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Draw-order replacement and rollback both failed.",
                    replacementError,
                    rollbackError);
            }
            throw;
        }
    }

    private static void ReplaceState(
        BlockRecord block,
        PersistedDrawOrderState state)
    {
        if (!state.Exists)
        {
            if (block.SortEntitiesTable is not null &&
                (block.XDictionary is null ||
                 !block.XDictionary.Remove(SortEntitiesTable.DictionaryEntryName)))
            {
                throw new InvalidOperationException(
                    "The model-space SORTENTSTABLE could not be removed.");
            }
            return;
        }

        SortEntitiesTable table = block.CreateSortEntitiesTable();
        table.Clear();
        foreach (PersistedDrawOrderEntry entry in state.Entries)
        {
            if (!ReferenceEquals(entry.Entity.Owner, block))
            {
                throw new InvalidOperationException(
                    "Retained draw-order state references an entity outside model space.");
            }
            table.Add(entry.Entity, entry.SortHandle);
        }
    }

    private static void ValidateCompleteOrder(
        BlockRecord modelSpace,
        Entity[] entities)
    {
        if (entities.Length != modelSpace.Entities.Count)
        {
            throw new InvalidOperationException(
                "Model-space membership changed outside the draw-order history action.");
        }
    }

    private static ulong[] NormalizeHandles(
        IEnumerable<ulong> handles,
        int maximumCount,
        string parameterName,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(handles, parameterName);
        var unique = new HashSet<ulong>();
        var retained = new List<ulong>();
        foreach (ulong handle in handles)
        {
            if (handle == 0)
            {
                throw new ArgumentException(
                    "Every model-space entity handle must be non-zero.",
                    parameterName);
            }
            if (!unique.Add(handle))
            {
                continue;
            }
            if (retained.Count == maximumCount)
            {
                throw new ArgumentException(
                    $"The entity set exceeds the configured limit of {maximumCount} unique handles.",
                    parameterName);
            }
            retained.Add(handle);
        }

        if (!allowEmpty && retained.Count == 0)
        {
            throw new ArgumentException(
                "At least one non-zero model-space entity handle is required.",
                parameterName);
        }
        return retained.ToArray();
    }

    private sealed record PersistedDrawOrderState(
        bool Exists,
        PersistedDrawOrderEntry[] Entries);

    private readonly record struct PersistedDrawOrderEntry(
        Entity Entity,
        ulong SortHandle);
}
