using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiWriteAuditTests
{
    [Fact]
    public async Task AStalledDispatcherCannotHoldAnAcknowledgedResultOrPublishALateAudit()
    {
        var project = HmiDemoProject.Create();
        var runtime = new HmiRuntime(project); runtime.Start();
        var profile = new HmiConnectionProfile
        {
            TimeoutMilliseconds = 100,
            Mappings = [new HmiIoMapping { Tag = "Pump.Setpoint", Writable = true }]
        };
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatches = 0;
        Action? delayedAudit = null;
        var connection = new Connection();
        var coordinator = new HmiWriteCoordinator(project, profile, runtime, connection, (action, token) =>
        {
            if (++dispatches == 1) { action(); return ValueTask.CompletedTask; }
            delayedAudit = action;
            return new(never.Task);
        }, (_, _) => ValueTask.FromResult(true));
        var review = coordinator.Prepare("Pump.Setpoint", HmiValue.From(50d), "operator", "test");
        var result = await coordinator.ConfirmAsync(review.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HmiWriteDisposition.DeviceAcknowledged, result.Disposition);
        Assert.Equal(1, connection.Writes);
        Assert.Contains("Audit publication failed", result.Detail);
        Assert.NotNull(delayedAudit);
        delayedAudit();
        Assert.Single(coordinator.Audit);
        Assert.Equal("PrepareWrite", coordinator.Audit[0].Operation);
    }
    private sealed class Connection : IHmiConnection
    {
        public bool IsConnected => true;
        public int Writes { get; private set; }
        public ValueTask ConnectAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyDictionary<string, HmiTagSample>> ReadAsync(CancellationToken token)
            => ValueTask.FromResult<IReadOnlyDictionary<string, HmiTagSample>>(new Dictionary<string, HmiTagSample>());
        public ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken token)
        {
            Writes++;
            return ValueTask.FromResult(new HmiWriteResult(HmiWriteDisposition.DeviceAcknowledged, "Test acknowledgement"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
