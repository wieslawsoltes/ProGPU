using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;

namespace ProGPU.CAD;

/// <summary>
/// Selects the persisted entity-order contract being captured by an immutable
/// snapshot.
/// </summary>
public enum CadDrawOrderPurpose : byte
{
    /// <summary>
    /// Resolve the order used by a modern AutoCAD regeneration. Persisted
    /// SORTENTSTABLE overrides are always honored.
    /// </summary>
    Regeneration = 0,

    /// <summary>
    /// Resolve plot order. Persisted SORTENTSTABLE overrides are honored only
    /// when the drawing's SORTENTS Plotting flag is enabled.
    /// </summary>
    Plotting = 1,
}

internal readonly record struct CadDrawOrderResolution(
    Entity[] Entities,
    bool HasOverrides);

/// <summary>
/// Resolves ACAD_SORTENTS without the per-entity linear scans performed by the
/// dependency convenience API. Work is O(E + S + E log E) and storage is O(E)
/// for E block entities and S sparse sort-table entries.
/// </summary>
internal static class CadDrawOrderResolver
{
    public static CadDrawOrderResolution Resolve(
        BlockRecord block,
        bool applySortOrder)
    {
        ArgumentNullException.ThrowIfNull(block);

        Entity[] source = block.Entities.ToArray();
        SortEntitiesTable? table = block.SortEntitiesTable;
        if (table is null)
        {
            if (applySortOrder)
            {
                SortByEffectiveHandle(source, overrides: null);
            }
            return new CadDrawOrderResolution(source, HasOverrides: false);
        }

        if (!ReferenceEquals(table.BlockOwner, block))
        {
            throw new InvalidDataException(
                $"SORTENTSTABLE for block '{block.Name}' has a mismatched block owner.");
        }

        var membership = new HashSet<Entity>(
            source,
            ReferenceEqualityComparer.Instance);
        var overrides = new Dictionary<Entity, ulong>(
            ReferenceEqualityComparer.Instance);
        foreach (SortEntitiesTable.Sorter sorter in table)
        {
            Entity? entity = sorter.Entity;
            if (entity is null ||
                !membership.Contains(entity) ||
                !ReferenceEquals(entity.Owner, block))
            {
                throw new InvalidDataException(
                    $"SORTENTSTABLE for block '{block.Name}' references an entity outside that block.");
            }
            if (!overrides.TryAdd(entity, sorter.SortHandle))
            {
                throw new InvalidDataException(
                    $"SORTENTSTABLE for block '{block.Name}' contains duplicate entries for handle {entity.Handle:X}.");
            }
        }

        if (applySortOrder)
        {
            SortByEffectiveHandle(source, overrides);
        }
        return new CadDrawOrderResolution(source, overrides.Count != 0);
    }

    private static void SortByEffectiveHandle(
        Entity[] entities,
        Dictionary<Entity, ulong>? overrides)
    {
        var ordered = new OrderedEntity[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            Entity entity = entities[i];
            ulong sortHandle = overrides is not null &&
                overrides.TryGetValue(entity, out ulong persisted)
                    ? persisted
                    : entity.Handle;
            ordered[i] = new OrderedEntity(entity, sortHandle, i);
        }

        Array.Sort(ordered);
        for (int i = 0; i < ordered.Length; i++)
        {
            entities[i] = ordered[i].Entity;
        }
    }

    private readonly record struct OrderedEntity(
        Entity Entity,
        ulong SortHandle,
        int SourceIndex) : IComparable<OrderedEntity>
    {
        public int CompareTo(OrderedEntity other)
        {
            int handleOrder = SortHandle.CompareTo(other.SortHandle);
            return handleOrder != 0
                ? handleOrder
                : SourceIndex.CompareTo(other.SourceIndex);
        }
    }
}
