using System.Diagnostics.CodeAnalysis;

namespace ProGPU.Backend.Native;

/// <summary>
/// Immutable host-object identities for one retained source snapshot. Owners
/// never cross the native ABI; the native index carries only their integer IDs.
/// </summary>
/// <remarks>
/// Construction copies O(N) entries once; lookup is amortized O(1), allocation-free
/// and CPU metadata only. No reflection, geometry preparation or GPU initialization
/// occurs. Retain this map with the submitted scene, not with a mutable visual tree.
/// </remarks>
public sealed class NativeGpuHitTestOwnerMap<TOwner> where TOwner : class
{
    private readonly Dictionary<int, TOwner> _owners;

    public static NativeGpuHitTestOwnerMap<TOwner> Empty { get; } = new([]);

    public NativeGpuHitTestOwnerMap(ReadOnlySpan<KeyValuePair<int, TOwner>> owners)
    {
        _owners = new Dictionary<int, TOwner>(owners.Length);
        foreach (KeyValuePair<int, TOwner> entry in owners)
        {
            ArgumentNullException.ThrowIfNull(entry.Value);
            if (!_owners.TryAdd(entry.Key, entry.Value))
                throw new ArgumentException("Hit-test owner IDs must be unique.", nameof(owners));
        }
    }

    public int Count => _owners.Count;

    public bool TryGetOwner(int id, [NotNullWhen(true)] out TOwner? owner)
        => _owners.TryGetValue(id, out owner);

    /// <summary>
    /// Resolves an ordered native result span without sorting/deduplicating it.
    /// Missing IDs and no-hit records do not occupy output slots. O(R) metadata
    /// traversal, allocation-free; the caller retains this map with its query.
    /// </summary>
    public int CopyOwners(ReadOnlySpan<NativeGpuHitTestResult> results, Span<TOwner?> owners)
    {
        int count = 0;
        for (int i = 0; i < results.Length && count < owners.Length; i++)
            if (results[i].HasHit && TryGetOwner(results[i].Id, out TOwner? owner))
                owners[count++] = owner;
        return count;
    }
}

/// <summary>Metadata of the exact installed native index; never inferred from a managed index.</summary>
public readonly record struct NativeGpuHitTestIndexInfo(
    bool HasIndex, bool IsUploaded, uint PrimitiveCount, uint NodeCount,
    uint PrimitiveIndexCount, uint PathSegmentCount);
