using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeWindowCaretTests
{
    [Fact]
    public void UpdateReusesShapeAndRecreatesOnlyForNewOwnerOrSize()
    {
        var operations = new Operations();
        using var caret = new NativeWindowCaret(10, operations);
        object owner = new();
        Assert.True(caret.TryUpdate(owner, -3, 7, 1, 18));
        Assert.True(caret.TryUpdate(owner, 11, 9, 1, 18));
        Assert.Equal(1, operations.Creates);
        Assert.Equal((11, 9), operations.Position);
        Assert.True(caret.TryUpdate(owner, 11, 9, 2, 36));
        Assert.Equal(2, operations.Creates);
        Assert.Equal((2, 36), operations.Size);
        caret.Release(owner);
        caret.Release(owner);
        Assert.Equal(1, operations.Destroys);
    }

    [Fact]
    public void LateEditorReleaseCannotDestroyNewEditorOrWindowCaret()
    {
        var operations = new Operations();
        using var first = new NativeWindowCaret(10, operations);
        using var second = new NativeWindowCaret(20, operations);
        object oldOwner = new(), newOwner = new();
        Assert.True(first.TryUpdate(oldOwner, 0, 0, 1, 12));
        Assert.True(first.TryUpdate(newOwner, 0, 0, 1, 12));
        first.Release(oldOwner);
        Assert.Equal(0, operations.Destroys);
        Assert.True(second.TryUpdate(oldOwner, 0, 0, 1, 12));
        first.Release(newOwner);
        first.Dispose();
        Assert.Equal(0, operations.Destroys);
        Assert.Equal((nint)20, operations.Current);
        second.Release(oldOwner);
        Assert.Equal(1, operations.Destroys);
    }

    [Fact]
    public void ExternalNativeReplacementIsNotDestroyedAndCanBeReclaimed()
    {
        var operations = new Operations();
        using var caret = new NativeWindowCaret(10, operations);
        object owner = new();
        Assert.True(caret.TryUpdate(owner, 0, 0, 1, 12));
        operations.Current = 30;
        caret.Release(owner);
        Assert.Equal(0, operations.Destroys);
        Assert.Equal((nint)30, operations.Current);
        Assert.True(caret.TryUpdate(owner, 0, 0, 1, 12));
        Assert.Equal(2, operations.Creates);
        operations.Current = 0; // Window/native caret destroyed outside the adapter.
        Assert.True(caret.TryUpdate(owner, 0, 0, 1, 12));
        Assert.Equal(3, operations.Creates);
    }

    [Fact]
    public void FailedCreateDoesNotStealPreviousQueueOwnership()
    {
        var operations = new Operations();
        using var first = new NativeWindowCaret(10, operations);
        using var second = new NativeWindowCaret(20, operations);
        object owner = new();
        Assert.True(first.TryUpdate(owner, 0, 0, 1, 12));
        operations.CreateAllowed = false;
        Assert.False(second.TryUpdate(new object(), 0, 0, 1, 12));
        second.Dispose();
        Assert.Equal((nint)10, operations.Current);
        first.Release(owner);
        Assert.Equal(1, operations.Destroys);
    }

    [Fact]
    public void RejectedQueriesAndPositionUpdatesAreNotReportedAsSuccess()
    {
        var operations = new Operations { QueryAllowed = false };
        using var caret = new NativeWindowCaret(10, operations);
        object owner = new();
        Assert.False(caret.TryUpdate(owner, 0, 0, 1, 12));
        Assert.Equal(0, operations.Creates);
        operations.QueryAllowed = true;
        operations.PositionAllowed = false;
        Assert.False(caret.TryUpdate(owner, 0, 0, 1, 12));
        operations.PositionAllowed = true;
        Assert.True(caret.TryUpdate(owner, 4, 5, 1, 12));
        Assert.Equal(1, operations.Creates);
    }

    [Fact]
    public void FailedReleaseRetainsOwnershipForExplicitRetry()
    {
        var operations = new Operations();
        using var caret = new NativeWindowCaret(10, operations);
        object owner = new();
        Assert.True(caret.TryUpdate(owner, 0, 0, 1, 12));
        operations.DestroyAllowed = false;
        Assert.Throws<InvalidOperationException>(() => caret.Dispose());
        operations.DestroyAllowed = true;
        caret.Release(owner);
        Assert.Equal(2, operations.Destroys);
        Assert.Equal((nint)0, operations.Current);
    }

    [Fact]
    public void UnsupportedHandlesAndInvalidExtentsDoNotCreateACaret()
    {
        Assert.False(NativeWindowCaret.TryCreate(NativeWindowHandle.Empty, out var unavailable));
        Assert.Null(unavailable);
        var operations = new Operations { Local = false };
        using var caret = new NativeWindowCaret(10, operations);
        Assert.False(caret.TryUpdate(new object(), 0, 0, 1, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => caret.TryUpdate(new object(), 0, 0, 1, 0));
        Assert.Equal(0, operations.Creates);
    }

    [Fact]
    public void AnotherThreadCannotMutateOrDisposeOwnership()
    {
        var operations = new Operations();
        using var caret = new NativeWindowCaret(10, operations);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { caret.Dispose(); } catch (Exception error) { failure = error; }
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
        Assert.True(caret.TryUpdate(new object(), 0, 0, 1, 12));
    }

    [Fact]
    public void ReentrantNativeCallbacksCannotOverwriteNewerOwnership()
    {
        var operations = new Operations();
        using var caret = new NativeWindowCaret(10, operations);
        operations.BeforeCreate = () => Assert.Throws<InvalidOperationException>(
            () => caret.TryUpdate(new object(), 0, 0, 1, 12));
        Assert.True(caret.TryUpdate(new object(), 0, 0, 1, 12));
        Assert.Equal(1, operations.Creates);
    }

    [Fact]
    public void CallbackExceptionsDoNotLeaveTheThreadMutationGuardEntered()
    {
        var failure = new InvalidOperationException("native callback");
        var operations = new Operations { BeforeCreate = () => throw failure };
        using var caret = new NativeWindowCaret(10, operations);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => caret.TryUpdate(new object(), 0, 0, 1, 12)));
        operations.BeforeCreate = null;
        Assert.True(caret.TryUpdate(new object(), 0, 0, 1, 12));
    }

    private sealed class Operations : INativeWindowCaretOperations
    {
        internal nint Current;
        internal bool Local = true, QueryAllowed = true, CreateAllowed = true, PositionAllowed = true, DestroyAllowed = true;
        internal int Creates, Destroys;
        internal (int, int) Position, Size;
        internal Action? BeforeCreate;
        public bool IsLocalWindow(nint window) => Local;
        public bool TryGetCaretWindow(out nint window) { window = Current; return QueryAllowed; }
        public bool Create(nint window, int width, int height)
        {
            if (!CreateAllowed) return false;
            BeforeCreate?.Invoke();
            Creates++;
            Current = window;
            Size = (width, height);
            return true;
        }
        public bool SetPosition(int x, int y) { Position = (x, y); return PositionAllowed; }
        public bool Destroy()
        {
            Destroys++;
            if (!DestroyAllowed) return false;
            Current = 0;
            return true;
        }
    }
}
