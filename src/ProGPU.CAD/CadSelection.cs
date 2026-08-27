namespace ProGPU.CAD;

/// <summary>One immutable broad-phase selection candidate from a document snapshot.</summary>
public readonly record struct CadSelectionCandidate(
    int EntityIndex,
    ulong Handle,
    CadEntityKind Kind,
    CadBounds3D Bounds);

public readonly record struct CadSelectionQueryResult(
    ulong ContentGeneration,
    int WrittenCount,
    int TotalCount)
{
    public bool IsTruncated => WrittenCount != TotalCount;
}

/// <summary>Caller-buffered broad-phase selection over immutable snapshot bounds.</summary>
public static class CadSelectionQuery
{
    /// <summary>Maps intersecting BVH entries to source primitive candidates.</summary>
    /// <remarks>
    /// Work is O(log E + K) on typical spatial data and O(E + K) worst-case for E
    /// snapshot primitives and K intersecting bounds. Expanded block primitives may
    /// share one semantic root handle and remain separate candidates for exact
    /// geometry testing. The smaller buffer capacity controls the written count.
    /// </remarks>
    public static CadSelectionQueryResult QueryBounds(
        CadDocumentSnapshot snapshot,
        CadBounds3D bounds,
        Span<int> entityIndexScratch,
        Span<CadSelectionCandidate> destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int capacity = Math.Min(entityIndexScratch.Length, destination.Length);
        CadSpatialQueryResult spatial = snapshot.SpatialIndex.Query(
            bounds,
            entityIndexScratch[..capacity]);
        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        for (int i = 0; i < spatial.WrittenCount; i++)
        {
            int entityIndex = entityIndexScratch[i];
            CadEntityHeader entity = entities[entityIndex];
            destination[i] = new CadSelectionCandidate(
                entityIndex,
                entity.Handle,
                entity.Kind,
                entity.Bounds);
        }

        return new CadSelectionQueryResult(
            snapshot.ContentGeneration,
            spatial.WrittenCount,
            spatial.TotalCount);
    }
}
