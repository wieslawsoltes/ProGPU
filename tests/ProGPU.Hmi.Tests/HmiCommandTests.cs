using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiCommandTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Connection : IHmiConnection
    {
        public bool IsConnected { get; private set; } = true;
        public int Writes;
        public ValueTask ConnectAsync(CancellationToken cancellationToken) { IsConnected = true; return ValueTask.CompletedTask; }
        public ValueTask DisconnectAsync(CancellationToken cancellationToken) { IsConnected = false; return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyDictionary<string, HmiTagSample>> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<string, HmiTagSample>>(new Dictionary<string, HmiTagSample>());
        public ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Writes++; return ValueTask.FromResult(new HmiWriteResult(HmiWriteDisposition.DeviceAcknowledged, "test")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static ValueTask Dispatch(Action action, CancellationToken token) { token.ThrowIfCancellationRequested(); action(); return ValueTask.CompletedTask; }
    private static HmiConnectionProfile Profile() => new()
    {
        Mappings = [new() { Tag = "Pump.Setpoint", Writable = true, InterlockTag = "Valve.Open" }]
    };
    [Fact]
    public async Task DefaultAuthorizationDeniesAndAuditsWithoutSending()
    {
        var clock = new Clock(); var project = HmiDemoProject.Create(); var runtime = new HmiRuntime(project, clock.Now); runtime.Start(); var connection = new Connection();
        var coordinator = new HmiWriteCoordinator(project, Profile(), runtime, connection, Dispatch, clock: clock);
        var request = coordinator.Prepare("Pump.Setpoint", HmiValue.From(40d), "operator", "test");
        Assert.Equal(HmiWriteDisposition.NotSent, (await coordinator.ConfirmAsync(request.Id)).Disposition);
        Assert.Equal(0, connection.Writes); Assert.Contains(coordinator.Audit, a => a.Operation == "Write:NotSent");
    }
    [Fact]
    public async Task ConfirmationsAreSingleUseAndNeverOptimisticallyUpdateFeedback()
    {
        var clock = new Clock(); var project = HmiDemoProject.Create(); var runtime = new HmiRuntime(project, clock.Now); runtime.Start(); var connection = new Connection();
        var coordinator = new HmiWriteCoordinator(project, Profile(), runtime, connection, Dispatch, (_, _) => ValueTask.FromResult(true), clock);
        var request = coordinator.Prepare("Pump.Setpoint", HmiValue.From(40d), "operator", "reviewed");
        Assert.Equal(HmiWriteDisposition.DeviceAcknowledged, (await coordinator.ConfirmAsync(request.Id)).Disposition);
        Assert.Equal(HmiWriteDisposition.NotSent, (await coordinator.ConfirmAsync(request.Id)).Disposition);
        Assert.Equal(1, connection.Writes); Assert.Equal(65d, runtime.Read("Pump.Setpoint").Value.Number);
    }
    [Fact]
    public async Task ExpiredAndRevokedRequestsNeverSend()
    {
        var clock = new Clock(); var project = HmiDemoProject.Create(); var runtime = new HmiRuntime(project, clock.Now); runtime.Start(); var connection = new Connection();
        var coordinator = new HmiWriteCoordinator(project, Profile(), runtime, connection, Dispatch, (_, _) => ValueTask.FromResult(true), clock);
        var request = coordinator.Prepare("Pump.Setpoint", HmiValue.From(40d), "operator", "test");
        clock.Now = clock.Now.AddSeconds(21);
        Assert.Equal(HmiWriteDisposition.NotSent, (await coordinator.ConfirmAsync(request.Id)).Disposition);
        clock.Now = runtime.Now;
        request = coordinator.Prepare("Pump.Setpoint", HmiValue.From(40d), "operator", "test"); coordinator.RevokeAll();
        Assert.Equal(HmiWriteDisposition.NotSent, (await coordinator.ConfirmAsync(request.Id)).Disposition); Assert.Equal(0, connection.Writes);
    }
    [Fact]
    public async Task FeedbackAndInterlockAreRecheckedAfterAuthorization()
    {
        var clock = new Clock(); var project = HmiDemoProject.Create(); var runtime = new HmiRuntime(project, clock.Now); runtime.Start(); var connection = new Connection();
        var coordinator = new HmiWriteCoordinator(project, Profile(), runtime, connection, Dispatch, (_, _) =>
        {
            runtime.Publish(new Dictionary<string, HmiTagSample> { ["Valve.Open"] = new(HmiValue.From(false), HmiQuality.Good, clock.Now) }, clock.Now);
            return ValueTask.FromResult(true);
        }, clock);
        var request = coordinator.Prepare("Pump.Setpoint", HmiValue.From(40d), "operator", "test");
        Assert.Equal(HmiWriteDisposition.NotSent, (await coordinator.ConfirmAsync(request.Id)).Disposition); Assert.Equal(0, connection.Writes);
    }
    [Fact]
    public async Task RevokingDuringAsynchronousAuthorizationInvalidatesTheClaimedRequest()
    {
        var clock = new Clock(); var project = HmiDemoProject.Create(); var runtime = new HmiRuntime(project, clock.Now); runtime.Start(); var connection = new Connection();
        var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new HmiWriteCoordinator(project, Profile(), runtime, connection, Dispatch, (_, _) => { entered.SetResult(); return new ValueTask<bool>(permission.Task); }, clock);
        var request = coordinator.Prepare("Pump.Setpoint", HmiValue.From(40d), "operator", "test");
        var confirm = coordinator.ConfirmAsync(request.Id).AsTask(); await entered.Task;
        coordinator.RevokeAll(); permission.SetResult(true);
        Assert.Equal(HmiWriteDisposition.NotSent, (await confirm).Disposition); Assert.Equal(0, connection.Writes);
    }
    [Fact]
    public void OverlappingModbusWriteMappingsAreRejected()
    {
        var profile = Profile(); profile.Mappings.Add(new HmiIoMapping { Tag = "other", Address = 0, Writable = true });
        Assert.Throws<InvalidDataException>(() => profile.Validate());
    }
}
