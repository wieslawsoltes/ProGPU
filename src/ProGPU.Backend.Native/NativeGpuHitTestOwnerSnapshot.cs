using System.Diagnostics.CodeAnalysis;

namespace ProGPU.Backend.Native;

/// <summary>
/// Couples immutable host identities to a specific compositor scene. This is
/// metadata ownership, not evidence that the scene contains a GPU hit-test index.
/// </summary>
public readonly struct NativeGpuHitTestOwnerSnapshot<TOwner> where TOwner : class
{
    private readonly NativeCompositor? _compositor;
    private readonly long _compositorIdentity;
    private readonly NativeGpuHitTestOwnerMap<TOwner>? _owners;

    internal NativeGpuHitTestOwnerSnapshot(
        NativeCompositor compositor, long compositorIdentity,
        NativeGpuHitTestOwnerMap<TOwner> owners, ulong sceneId, ulong generation)
    {
        _compositor = compositor;
        _compositorIdentity = compositorIdentity;
        _owners = owners;
        SceneId = sceneId;
        Generation = generation;
    }

    public ulong SceneId { get; }
    public ulong Generation { get; }
    public int OwnerCount => _owners?.Count ?? 0;
    public bool IsValid => _compositor is not null && SceneId != 0 && Generation != 0;

    public NativeGpuHitTestRequestToken BeginQuery(in NativeGpuHitTestQuery query)
    {
        if (!IsValid)
            throw new InvalidOperationException("The native hit-test owner snapshot is uninitialized.");
        return _compositor!.BeginGpuHitTest(query, SceneId, Generation);
    }

    /// <summary>
    /// Polls the original compositor, rejecting a token from any other snapshot.
    /// Results remain in native order and retain all traversal/intersection data.
    /// Caller spans and the existing native readback are reused without allocations.
    /// </summary>
    public bool TryPoll(
        NativeGpuHitTestRequestToken token, Span<NativeGpuHitTestResult> results,
        out int resultCount, out NativeGpuHitTestResult summary)
    {
        RequireToken(token);
        return _compositor!.TryPollGpuHitTest(token, results, out resultCount, out summary);
    }

    public bool TryGetOwner(
        NativeGpuHitTestRequestToken token, in NativeGpuHitTestResult result,
        [NotNullWhen(true)] out TOwner? owner)
    {
        RequireToken(token);
        owner = null;
        return result.Hit != 0 && _owners!.TryGetOwner(result.Id, out owner);
    }

    private void RequireToken(NativeGpuHitTestRequestToken token)
    {
        if (!IsValid || !token.IsValid || token.Owner != _compositorIdentity ||
            token.SceneId != SceneId || token.Generation != Generation)
            throw new ArgumentException(
                "The native hit-test request does not belong to this compositor scene snapshot.", nameof(token));
    }
}
