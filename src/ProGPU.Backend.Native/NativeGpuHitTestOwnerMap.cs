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
}
