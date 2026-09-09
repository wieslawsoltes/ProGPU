using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeGpuHitTestOwnerMapTests
{
    [Fact]
    public void SnapshotOwnsEntriesAndPreservesReferenceIdentity()
    {
        var owner = new object();
        KeyValuePair<int, object>[] entries = [new(7, owner)];
        var map = new NativeGpuHitTestOwnerMap<object>(entries);
        entries[0] = new(7, new object());
        Assert.Equal(1, map.Count);
        Assert.True(map.TryGetOwner(7, out object? actual));
        Assert.Same(owner, actual);
        Assert.False(map.TryGetOwner(8, out _));
        Assert.False(map.TryGetOwner(-1, out _));
    }

    [Fact]
    public void SameNativeIdInLaterSnapshotDoesNotChangeEarlierOwner()
    {
        var first = new object();
        var second = new object();
        var before = new NativeGpuHitTestOwnerMap<object>([new(3, first)]);
        var after = new NativeGpuHitTestOwnerMap<object>([new(3, second)]);
        Assert.True(before.TryGetOwner(3, out object? oldOwner));
        Assert.True(after.TryGetOwner(3, out object? newOwner));
        Assert.Same(first, oldOwner);
        Assert.Same(second, newOwner);
    }

    [Fact]
    public void InvalidAndDuplicateIdentitiesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NativeGpuHitTestOwnerMap<object>([new(1, null!)]));
        Assert.Throws<ArgumentException>(() =>
            new NativeGpuHitTestOwnerMap<object>([new(1, new object()), new(1, new object())]));
        Assert.Equal(0, NativeGpuHitTestOwnerMap<object>.Empty.Count);
    }

    [Fact]
    public void AllSignedIdBitsArePreservedRatherThanConfusedWithTheHitFlag()
    {
        var owner = new object();
        var map = new NativeGpuHitTestOwnerMap<object>([new(-1, owner), new(int.MinValue, owner)]);
        Assert.True(map.TryGetOwner(-1, out object? actual));
        Assert.Same(owner, actual);
        Assert.True(map.TryGetOwner(int.MinValue, out actual));
        Assert.Same(owner, actual);
    }

    [Fact]
    public void UninitializedSnapshotRejectsSubmissionPollingAndOwnerResolution()
    {
        var snapshot = default(NativeGpuHitTestOwnerSnapshot<object>);
        Assert.False(snapshot.IsValid);
        Assert.Throws<InvalidOperationException>(() => snapshot.BeginQuery(default));
        Assert.Throws<ArgumentException>(() => snapshot.TryPoll(default, [], out _, out _));
        Assert.Throws<ArgumentException>(() => snapshot.Wait(default, [], out _));
        Assert.Throws<InvalidOperationException>(() => snapshot.GetIndexInfo());
        Assert.Throws<ArgumentException>(() => snapshot.CopyOwners(default, [], []));
        Assert.Throws<ArgumentException>(() => snapshot.TryGetOwner(default, default, out _));
    }

    [Fact]
    public void OrderedCopiesSkipUnknownIdsButPreserveRepeatedOwnersAndCapacity()
    {
        object first = new(), second = new(), sentinel = new();
        var map = new NativeGpuHitTestOwnerMap<object>([new(1, first), new(2, second)]);
        NativeGpuHitTestResult[] results = [
            new() { Hit = 1, Id = 99 }, new() { Hit = 1, Id = 2 },
            new() { Hit = 0, Id = 1 }, new() { Hit = 1, Id = 2 }, new() { Hit = 1, Id = 1 }];
        object?[] owners = [null, null, sentinel];
        Assert.Equal(2, map.CopyOwners(results, owners.AsSpan(0, 2)));
        Assert.Same(second, owners[0]);
        Assert.Same(second, owners[1]);
        Assert.Same(sentinel, owners[2]);
        Assert.Equal(0, map.CopyOwners(results, []));
        Assert.Equal(3, map.CopyOwners(results, owners));
        Assert.Same(first, owners[2]);
    }

    [Fact]
    public void TokenIdentityIncludesCompositorAndImmutableSceneGeneration()
    {
        var token = new NativeGpuHitTestRequestToken(5, 7, 11, 13);
        Assert.True(token.IsValid);
        Assert.Equal(11UL, token.SceneId);
        Assert.Equal(13UL, token.Generation);
        Assert.Equal(token, new NativeGpuHitTestRequestToken(5, 7, 11, 13));
        Assert.NotEqual(token, new NativeGpuHitTestRequestToken(5, 8, 11, 13));
        Assert.NotEqual(token, new NativeGpuHitTestRequestToken(5, 7, 12, 13));
        Assert.NotEqual(token, new NativeGpuHitTestRequestToken(5, 7, 11, 14));
        Assert.False(default(NativeGpuHitTestRequestToken).IsValid);
        Assert.False(new NativeGpuHitTestRequestToken(5, 7, 0, 13).IsValid);
    }
}
