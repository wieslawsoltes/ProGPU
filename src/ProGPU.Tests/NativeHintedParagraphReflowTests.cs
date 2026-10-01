using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

// Device-free ownership controls exercise the actual backend operation. The
// native fixture separately checks writer/source identity and cluster bounds.
public sealed unsafe class NativeHintedParagraphReflowTests
{
    [Theory]
    [InlineData(-1, 10f)]
    [InlineData(0, -1f)]
    [InlineData(0, float.NaN)]
    [InlineData(0, float.PositiveInfinity)]
    [InlineData(0, float.NegativeInfinity)]
    public void InvalidInputsDoNotAcquireOrInvokeProducer(int start, float width)
    {
        int calls = 0, destroys = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        using var original = new NativeHintedGlyphResource(17, in view, _ => destroys++, reflow:
            (_, _, _, _, _, _, _) => { calls++; throw new Exception("Unexpected producer call."); });
        Assert.Throws<ArgumentOutOfRangeException>(() => original.Reflow(start, width));
        original.Dispose();
        Assert.Equal(0, calls);
        Assert.Equal(1, destroys);
    }

    [Fact]
    public void ExactInheritedPolicyAndOriginalOwnerSurviveUntilIndependentPublication()
    {
        int oldDestroys = 0, newDestroys = 0;
        var view = new NativeMethods.HintedGlyphResourceView
        {
            DpiScale = 1.25f, ProjectionPolicy = (uint)NativeHintedProjectionPolicy.ScalarReference,
            Coverage = (uint)NativeHintedCoverage.AntialiasedVector
        };
        NativeHintedGlyphResource? original = null;
        var result = new NativeHintedGlyphResource(23, in view, _ => newDestroys++);
        original = new NativeHintedGlyphResource(19, in view, _ => oldDestroys++, nominalMetrics:
            new NativeMethods.HintedGlyphNominalMetricsView(), reflow: (handle, start, width, dpi, projection, coverage, nominal) =>
            {
                Assert.Equal((nint)19, handle);
                Assert.Equal(38, start);
                Assert.Equal(203.25f, width);
                Assert.Equal(1.25f, dpi);
                Assert.Equal(NativeHintedProjectionPolicy.ScalarReference, projection);
                Assert.Equal(NativeHintedCoverage.AntialiasedVector, coverage);
                Assert.True(nominal);
                original!.Dispose();
                Assert.Equal(0, oldDestroys); // Synchronous producer still owns its exact input.
                return result;
            });
        Assert.Same(result, original.Reflow(38, 203.25f));
        Assert.Equal(1, oldDestroys);
        using var read = result.AcquireReadLease();
        Assert.Equal(1.25f, read.DpiScale);
        result.Dispose();
        Assert.Equal(0, newDestroys);
        read.Dispose();
        Assert.Equal(1, newDestroys);
    }

    [Fact]
    public void ProducerFailurePreservesPrimaryAndExactFailedCleanupForRetry()
    {
        int attempts = 0;
        var primary = new InvalidOperationException("producer");
        var cleanup = new InvalidOperationException("retirement");
        NativeMethods.HintedGlyphResourceView view = default;
        NativeHintedGlyphResource? original = null;
        original = new NativeHintedGlyphResource(29, in view,
            handle => { Assert.Equal((nint)29, handle); if (++attempts == 1) throw cleanup; },
            reflow: (_, _, _, _, _, _, _) => { original!.Dispose(); throw primary; });
        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => original.Reflow(0, 0)));
        Assert.Same(cleanup, primary.Data["HintedReflowCleanupFailure"]);
        original.Dispose();
        original.Dispose();
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void FailedInputRetirementRollsBackUnpublishedResultAndAllowsRetry()
    {
        int attempts = 0, resultDestroys = 0;
        var cleanup = new InvalidOperationException("retirement");
        NativeMethods.HintedGlyphResourceView view = default;
        NativeHintedGlyphResource? original = null;
        var result = new NativeHintedGlyphResource(37, in view, _ => resultDestroys++);
        original = new NativeHintedGlyphResource(31, in view, _ => { if (++attempts == 1) throw cleanup; },
            reflow: (_, _, _, _, _, _, nominal) => { Assert.False(nominal); original!.Dispose(); return result; });
        Assert.Same(cleanup, Assert.Throws<InvalidOperationException>(() => original.Reflow(0, 0)));
        Assert.Equal(1, resultDestroys);
        original.Dispose();
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ExistingReadersAndReflowShareOneRetirementCount()
    {
        int destroys = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        using var result = new NativeHintedGlyphResource(43, in view, _ => { });
        NativeHintedGlyphResource? original = null;
        original = new NativeHintedGlyphResource(41, in view, _ => destroys++, reflow:
            (_, _, _, _, _, _, _) => { original!.Dispose(); return result; });
        using var reader = original.AcquireReadLease();
        Assert.Same(result, original.Reflow(0, 0));
        Assert.Equal(0, destroys);
        Assert.Throws<ObjectDisposedException>(() => original.Reflow(0, 0));
        reader.Dispose();
        Assert.Equal(1, destroys);
    }

    [Fact]
    public void FailedResultRollbackRemainsOwnedForExplicitRetryAfterOldHandleIsGone()
    {
        int oldAttempts = 0, resultAttempts = 0;
        var oldFailure = new InvalidOperationException("old owner");
        var newFailure = new InvalidOperationException("new owner");
        NativeMethods.HintedGlyphResourceView view = default;
        NativeHintedGlyphResource? original = null;
        var result = new NativeHintedGlyphResource(53, in view,
            handle => { Assert.Equal((nint)53, handle); if (++resultAttempts < 3) throw newFailure; });
        original = new NativeHintedGlyphResource(47, in view,
            handle => { Assert.Equal((nint)47, handle); if (++oldAttempts == 1) throw oldFailure; },
            reflow: (_, _, _, _, _, _, _) => { original!.Dispose(); return result; });
        Assert.Same(oldFailure, Assert.Throws<InvalidOperationException>(() => original.Reflow(0, 0)));
        Assert.Same(newFailure, oldFailure.Data["HintedReflowCleanupFailure"]);
        Assert.Equal(1, resultAttempts);
        Assert.Same(newFailure, Assert.Throws<InvalidOperationException>(() => original.Dispose()));
        Assert.Equal(2, oldAttempts);
        Assert.Equal(2, resultAttempts);
        original.Dispose(); // Original native handle is zero, but exact rollback owner is still queued.
        original.Dispose();
        Assert.Equal(2, oldAttempts);
        Assert.Equal(3, resultAttempts);
    }
}
