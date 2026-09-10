using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// Defines how the caller-supplied displacement is applied to a linear COPY
/// array.
/// </summary>
public enum CadLinearCopyMode : byte
{
    /// <summary>
    /// Treats the displacement as the step between adjacent array items.
    /// </summary>
    Incremental,

    /// <summary>
    /// Treats the displacement as the distance from the source to the final
    /// array item and distributes intermediate items uniformly.
    /// </summary>
    Fit,
}

/// <summary>
/// Duplicates a bounded stable model-space selection into a linear array as
/// one reversible edit.
/// </summary>
/// <remarks>
/// <c>itemCount</c> includes the original selection, matching the
/// CAD COPY Array contract. The command retains only the detached duplicate
/// entity graphs and one handle per duplicate. Construction and execution use
/// O(S + S(C - 1)) time and O(S(C - 1)) retained storage for S unique source
/// roots and C items. The configured source and duplicate limits are checked
/// before cloning or document mutation.
/// </remarks>
public sealed class CadLinearCopyModelSpaceEntitiesCommand : CadEditCommand
{
    public const int DefaultMaximumSourceEntityCount = 65_536;
    public const int DefaultMaximumDuplicateEntityCount = 65_536;

    private readonly ulong[] _sourceHandles;
    private readonly ulong[] _currentHandles;
    private Entity[]? _duplicates;

    public ReadOnlyMemory<ulong> SourceHandles => _sourceHandles;

    /// <summary>
    /// Current duplicate handles in placement-major, source-order sequence.
    /// Values are zero while the command is undone.
    /// </summary>
    public ReadOnlyMemory<ulong> CurrentHandles => _currentHandles;

    /// <summary>
    /// Detached or attached duplicate graphs in placement-major, source-order
    /// sequence.
    /// </summary>
    public ReadOnlyMemory<Entity> Duplicates =>
        _duplicates ?? ReadOnlyMemory<Entity>.Empty;

    public int SourceEntityCount => _sourceHandles.Length;

    /// <summary>Number of array items including the original selection.</summary>
    public int ItemCount { get; }

    public int PlacementCount => ItemCount - 1;

    public int DuplicateEntityCount => _currentHandles.Length;

    public CadPoint3D Displacement { get; }

    public CadLinearCopyMode Mode { get; }

    public int MaximumSourceEntityCount { get; }

    public int MaximumDuplicateEntityCount { get; }

    public CadLinearCopyModelSpaceEntitiesCommand(
        IEnumerable<ulong> sourceHandles,
        CadPoint3D displacement,
        int itemCount,
        CadLinearCopyMode mode = CadLinearCopyMode.Incremental,
        string description = "Copy entities in a linear array",
        int maximumSourceEntityCount = DefaultMaximumSourceEntityCount,
        int maximumDuplicateEntityCount = DefaultMaximumDuplicateEntityCount)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(sourceHandles);
        ArgumentOutOfRangeException.ThrowIfLessThan(itemCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumSourceEntityCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumDuplicateEntityCount);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!IsFinite(displacement))
        {
            throw new ArgumentException(
                "A linear-copy displacement must be finite.",
                nameof(displacement));
        }

        var unique = new HashSet<ulong>();
        var retainedHandles = new List<ulong>();
        foreach (ulong handle in sourceHandles)
        {
            if (handle == 0)
            {
                throw new ArgumentException(
                    "Every model-space source handle must be non-zero.",
                    nameof(sourceHandles));
            }
            if (!unique.Add(handle))
            {
                continue;
            }
            if (retainedHandles.Count == maximumSourceEntityCount)
            {
                throw new ArgumentException(
                    $"The linear-copy source set exceeds the configured limit of {maximumSourceEntityCount} unique entities.",
                    nameof(sourceHandles));
            }
            retainedHandles.Add(handle);
        }

        if (retainedHandles.Count == 0)
        {
            throw new ArgumentException(
                "At least one non-zero model-space source handle is required.",
                nameof(sourceHandles));
        }

        int placementCount = itemCount - 1;
        int duplicateCount;
        try
        {
            duplicateCount = checked(retainedHandles.Count * placementCount);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemCount),
                itemCount,
                "The linear-copy duplicate count exceeds the supported integer range.");
        }
        if (duplicateCount > maximumDuplicateEntityCount)
        {
            throw new ArgumentException(
                $"The linear-copy result contains {duplicateCount} duplicates and exceeds the configured limit of {maximumDuplicateEntityCount}.",
                nameof(itemCount));
        }

        if (mode == CadLinearCopyMode.Incremental)
        {
            CadPoint3D finalDisplacement = Scale(displacement, placementCount);
            if (!IsFinite(finalDisplacement))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(itemCount),
                    "The final incremental array displacement is not finite.");
            }
        }

        MaximumSourceEntityCount = maximumSourceEntityCount;
        MaximumDuplicateEntityCount = maximumDuplicateEntityCount;
        ItemCount = itemCount;
        Displacement = displacement;
        Mode = mode;
        _sourceHandles = retainedHandles.ToArray();
        _currentHandles = new ulong[duplicateCount];
    }

    /// <summary>
    /// Returns the WCS displacement for a zero-based duplicate placement.
    /// </summary>
    public CadPoint3D GetPlacementDisplacement(int placementIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)placementIndex,
            (uint)PlacementCount,
            nameof(placementIndex));
        if (Mode == CadLinearCopyMode.Incremental)
        {
            return Scale(Displacement, placementIndex + 1);
        }

        if (placementIndex == PlacementCount - 1)
        {
            return Displacement;
        }
        double fraction = (double)(placementIndex + 1) / PlacementCount;
        return Scale(Displacement, fraction);
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] duplicates;
        if (isRedo)
        {
            duplicates = _duplicates ??
                throw new InvalidOperationException(
                    "The linear-copy command has not been applied.");
        }
        else
        {
            Entity[] sources = ResolveModelSpaceEntities(
                document,
                _sourceHandles);
            duplicates = CreateDuplicates(sources);
            _duplicates = duplicates;
        }

        foreach (Entity duplicate in duplicates)
        {
            ValidateDetachedDuplicate(duplicate);
        }
        document.Entities.AddRange(duplicates);
        for (int i = 0; i < duplicates.Length; i++)
        {
            _currentHandles[i] = duplicates[i].Handle;
        }
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] duplicates = _duplicates ??
            throw new InvalidOperationException(
                "The linear-copy command has not been applied.");
        ValidateModelSpaceEntities(document, duplicates);
        if (!document.Entities.TryRemoveRange(duplicates))
        {
            throw new InvalidOperationException(
                "The linear-copy batch removal was cancelled before mutation.");
        }
        Array.Clear(_currentHandles);
    }

    private Entity[] CreateDuplicates(Entity[] sources)
    {
        var duplicates = new Entity[_currentHandles.Length];
        int output = 0;
        for (int placementIndex = 0;
             placementIndex < PlacementCount;
             placementIndex++)
        {
            CadPoint3D placement = GetPlacementDisplacement(placementIndex);
            var translation = new XYZ(
                placement.X,
                placement.Y,
                placement.Z);
            for (int sourceIndex = 0;
                 sourceIndex < sources.Length;
                 sourceIndex++)
            {
                Entity duplicate = (Entity)sources[sourceIndex].Clone();
                ValidateDetachedDuplicate(duplicate);
                if (translation != XYZ.Zero)
                {
                    ApplyEntityTranslation(duplicate, translation);
                }
                duplicates[output++] = duplicate;
            }
        }
        return duplicates;
    }

    private static void ValidateDetachedDuplicate(Entity duplicate)
    {
        if (duplicate.Owner is not null ||
            duplicate.Document is not null ||
            duplicate.Handle != 0)
        {
            throw new InvalidOperationException(
                "A linear-copy duplicate is not detached and cannot be added to model space.");
        }
    }

    private static bool IsFinite(CadPoint3D value) =>
        double.IsFinite(value.X) &&
        double.IsFinite(value.Y) &&
        double.IsFinite(value.Z);

    private static CadPoint3D Scale(CadPoint3D value, double factor) =>
        new(
            value.X * factor,
            value.Y * factor,
            value.Z * factor);
}
