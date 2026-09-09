using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeWindowModalHintTests
{
    [Fact]
    public void ReopenDoesNotAdoptItsOwnPendingRemovalAsAnExternalHint()
    {
        NativeModalHintSubmission state = default;
        Assert.False(state.Observe(false));
        state.Submitted(true);
        state.Submitted(false);
        Assert.False(state.Observe(true)); // WM still exposes the previous Add.
        state.Submitted(true); // The reopened invocation must submit a new Add.
        Assert.True(state.Observe(false)); // Old Remove has now been processed.
        Assert.True(state.Observe(true)); // New Add acknowledged; stop overriding.
        Assert.False(state.Observe(false)); // Later external state is real again.
    }

    [Fact]
    public void UntouchedPreexistingStateRemainsObservable()
    {
        NativeModalHintSubmission state = default;
        Assert.True(state.Observe(true));
        Assert.False(state.Observe(false));
    }

    [Fact]
    public void DirectUnmappedPublicationDoesNotMaskLaterExternalState()
    {
        NativeModalHintSubmission state = default;
        state.Submitted(false, awaitWindowManager: false);
        Assert.True(state.Observe(true));
    }

    [Fact]
    public void EntryExceptionRollsBackAndPreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("Submission failed.");
        var api = new Operations { AddFailure = failure };
        NativeWindowModalHint? hint = null;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            NativeWindowModalHint.TryAcquire(api, out hint)));
        Assert.Null(hint);
        Assert.False(api.Modal);
        Assert.Equal(new[] { "Read", "Add", "Remove" }, api.Calls);
    }

    [Fact]
    public void SourceInputAndFocusReleaseFollowHintRemoval()
    {
        object owner = new(), dialog = new();
        bool ownerAllowed = true;
        using var gate = PortableModalInputScope.RegisterWindow(owner, allowed => ownerAllowed = allowed);
        var scope = PortableModalInputScope.Enter(dialog);
        var api = new Operations();
        Assert.True(NativeWindowModalHint.TryAcquire(api, out var hint));
        using (hint)
        {
            scope.ReleaseAfterNative(completed =>
            {
                Assert.False(ownerAllowed);
                hint!.Dispose();
                Assert.True(hint.IsReleased);
                Assert.False(ownerAllowed);
                completed();
            }, new Cleanup(() =>
            {
                Assert.True(ownerAllowed);
                Assert.True(scope.IsReleased);
                api.Calls.Add("Focus");
            }));
        }
        Assert.Equal(new[] { "Read", "Add", "Remove", "Focus" }, api.Calls);
    }

    [Fact]
    public void PreexistingModalHintIsNotRemovedByTheLease()
    {
        var api = new Operations { Modal = true };
        Assert.True(NativeWindowModalHint.TryAcquire(api, out var hint));
        hint!.Dispose();
        hint.Dispose();
        Assert.True(hint.IsReleased);
        Assert.True(api.Modal);
        Assert.Equal(new[] { "Read" }, api.Calls);
    }

    [Fact]
    public void UnsupportedStateReadDoesNotMutateAnything()
    {
        var api = new Operations { ReadAccepted = false };
        Assert.False(NativeWindowModalHint.TryAcquire(api, out var hint));
        Assert.Null(hint);
        Assert.Equal(new[] { "Read" }, api.Calls);
    }

    [Fact]
    public void RejectedEntryRollsBackPartialPropertyUpdate()
    {
        var api = new Operations { AddAccepted = false };
        Assert.False(NativeWindowModalHint.TryAcquire(api, out var hint));
        Assert.Null(hint);
        Assert.False(api.Modal);
        Assert.Equal(new[] { "Read", "Add", "Remove" }, api.Calls);
    }

    [Fact]
    public void FailedRollbackRetainsLeaseAndSupportsIdempotentRemovalRetry()
    {
        var api = new Operations { AddAccepted = false, RemoveAccepted = false };
        NativeWindowModalHint? hint = null;
        Assert.Throws<InvalidOperationException>(() => NativeWindowModalHint.TryAcquire(api, out hint));
        Assert.NotNull(hint);
        Assert.False(hint.IsReleased);
        api.RemoveAccepted = true;
        hint.Dispose();
        hint.Dispose();
        Assert.True(hint.IsReleased);
        Assert.Equal(new[] { "Read", "Add", "Remove", "Remove" }, api.Calls);
    }

    [Fact]
    public void FailedReleaseRetainsOwnershipUntilRemovalIsSubmitted()
    {
        var api = new Operations { RemoveAccepted = false };
        Assert.True(NativeWindowModalHint.TryAcquire(api, out var hint));
        Assert.Throws<InvalidOperationException>(() => hint!.Dispose());
        Assert.False(hint!.IsReleased);
        api.RemoveAccepted = true;
        hint.Dispose();
        Assert.True(hint.IsReleased);
    }

    [Fact]
    public void ForeignThreadCannotReleaseBorrowedNativeState()
    {
        var api = new Operations();
        Assert.True(NativeWindowModalHint.TryAcquire(api, out var hint));
        using (hint)
        {
            Exception? failure = null;
            var thread = new Thread(() => failure = Record.Exception(hint!.Dispose));
            thread.Start();
            thread.Join();
            Assert.IsType<InvalidOperationException>(failure);
            Assert.False(hint!.IsReleased);
            Assert.Equal(new[] { "Read", "Add" }, api.Calls);
        }
    }

    [Fact]
    public void X11AttributeTransportKeepsLp64NativeOffsets()
    {
        if (IntPtr.Size != 8) return;
        Assert.Equal(136, Unsafe.SizeOf<X11NativeWindowPlatform.XWindowAttributes>());
        Assert.Equal(32, Marshal.OffsetOf<X11NativeWindowPlatform.XWindowAttributes>("Root").ToInt32());
        Assert.Equal(92, Marshal.OffsetOf<X11NativeWindowPlatform.XWindowAttributes>("MapState").ToInt32());
        Assert.Equal(120, Marshal.OffsetOf<X11NativeWindowPlatform.XWindowAttributes>("OverrideRedirect").ToInt32());
    }

    private sealed class Cleanup(Action completed) : IDisposable
    {
        public void Dispose() => completed();
    }

    private sealed class Operations : INativeWindowModalHintOperations
    {
        public List<string> Calls { get; } = [];
        public bool Modal;
        public bool ReadAccepted = true;
        public bool AddAccepted = true;
        public bool RemoveAccepted = true;
        public Exception? AddFailure;
        public bool TryRead(out bool modal) { Calls.Add("Read"); modal = Modal; return ReadAccepted; }
        public bool TrySet(bool modal)
        {
            Calls.Add(modal ? "Add" : "Remove");
            Modal = modal; // Model property mutation before message submission.
            if (modal && AddFailure != null) throw AddFailure;
            return modal ? AddAccepted : RemoveAccepted;
        }
    }
}
