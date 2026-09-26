using ProGPU.Android;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

[Collection(WgpuContextLossCollection.Name)]
public sealed class AndroidGpuDiagnosticSubscriptionTests
{
    [Fact]
    public void ReportsTypedFailuresWithoutInitializingADevice()
    {
        var messages = new List<string>();
        using var subscription = new AndroidGpuDiagnosticSubscription(messages.Add);
        using var context = new WgpuContext();
        WgpuContext.RaiseWebGpuError(ErrorType.Validation, "invalid command");
        context.ReportDeviceLost(DeviceLostReason.Unknown, "lost test device");
        Assert.False(context.IsInitialized);
        Assert.Equal(new[]
        {
            "WebGPU error (Validation): invalid command",
            "WebGPU device lost (Unknown): lost test device"
        }, messages);
    }

    [Fact]
    public void DisposalDoesNotRemoveAnotherHostsSubscription()
    {
        var oldMessages = new List<string>();
        var currentMessages = new List<string>();
        var old = new AndroidGpuDiagnosticSubscription(oldMessages.Add);
        using var current = new AndroidGpuDiagnosticSubscription(currentMessages.Add);
        old.Dispose();
        old.Dispose();
        WgpuContext.RaiseWebGpuError(ErrorType.Validation, "current host");
        using var context = new WgpuContext();
        context.ReportDeviceLost(DeviceLostReason.Unknown, "current device");
        Assert.Empty(oldMessages);
        Assert.Equal(2, currentMessages.Count);
    }

    [Fact]
    public void AlreadyCapturedEventCallbackCannotUseReleasedSink()
    {
        var messages = new List<string>();
        AndroidGpuDiagnosticSubscription? subscription = null;
        void DisposeBeforeSubscriber(ErrorType type, string message) => subscription!.Dispose();
        WgpuContext.OnWebGpuError += DisposeBeforeSubscriber;
        try
        {
            subscription = new AndroidGpuDiagnosticSubscription(messages.Add);
            // The event snapshots both delegates before invoking the first.
            WgpuContext.RaiseWebGpuError(ErrorType.Validation, "late callback");
            Assert.Empty(messages);
        }
        finally
        {
            subscription?.Dispose();
            WgpuContext.OnWebGpuError -= DisposeBeforeSubscriber;
        }
    }

    [Fact]
    public void RejectsMissingLogSinkBeforeSubscribing()
    {
        Assert.Throws<ArgumentNullException>(() => new AndroidGpuDiagnosticSubscription(null!));
    }
}
