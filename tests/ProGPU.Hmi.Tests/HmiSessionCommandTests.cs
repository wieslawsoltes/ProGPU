using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiSessionCommandTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Connection : IHmiConditionalWriteConnection
    {
        public bool IsConnected { get; private set; } = true;
        public long ConnectionGeneration { get; private set; } = 1;
        public int Writes { get; private set; }
        public int LegacyWrites { get; private set; }
        public Action? BeforeAdmission { get; set; }
        public void Reconnect() => ConnectionGeneration++;
        public ValueTask ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); IsConnected = true; Reconnect(); return ValueTask.CompletedTask; }
        public ValueTask DisconnectAsync(CancellationToken token) { IsConnected = false; Reconnect(); return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyDictionary<string, HmiTagSample>> ReadAsync(CancellationToken token)
            => ValueTask.FromResult<IReadOnlyDictionary<string, HmiTagSample>>(new Dictionary<string, HmiTagSample>());
        public ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken token)
        {
            LegacyWrites++;
            return ValueTask.FromResult(new HmiWriteResult(HmiWriteDisposition.NotSent, "The coordinator must choose generation-checked admission."));
        }
        public ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, long expectedGeneration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BeforeAdmission?.Invoke();
            if (expectedGeneration != ConnectionGeneration)
                return ValueTask.FromResult(new HmiWriteResult(HmiWriteDisposition.NotSent, "Transport session changed while waiting for admission."));
            Writes++;
            return ValueTask.FromResult(new HmiWriteResult(HmiWriteDisposition.DeviceAcknowledged, "Protocol acknowledgement only."));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static ValueTask Dispatch(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); action(); return ValueTask.CompletedTask;
    }
    private static HmiConnectionProfile Profile() => new()
    {
        Mappings = [new HmiIoMapping { Tag = "Pump.Setpoint", Writable = true, InterlockTag = "Valve.Open" }]
    };
    private static HmiWriteCoordinator Coordinator(Connection connection, Clock clock, HmiWriteAuthorizer? authorizer = null, HmiRuntimeDispatcher? dispatch = null)
    {
        var project = HmiDemoProject.Create();
        var runtime = new HmiRuntime(project, clock.Now); runtime.Start();
        return new HmiWriteCoordinator(project, Profile(), runtime, connection, dispatch ?? Dispatch, authorizer ?? ((_, _) => ValueTask.FromResult(true)), clock);
    }
    private static HmiWriteRequest Prepare(HmiWriteCoordinator coordinator) => coordinator.Prepare("Pump.Setpoint", HmiValue.From(40d), "authenticated-host-user", "Reviewed setpoint change");

    [Fact]
    public async Task ReviewCannotCrossAReconnectEvenWhenFeedbackStillMatches()
    {
        var connection = new Connection(); var clock = new Clock();
        int authorizations = 0;
        var coordinator = Coordinator(connection, clock, (_, _) => { authorizations++; return ValueTask.FromResult(true); });
        var review = Prepare(coordinator);
        connection.Reconnect();
        var result = await coordinator.ConfirmAsync(review.Id);
        Assert.Equal(HmiWriteDisposition.NotSent, result.Disposition);
        Assert.Equal(0, authorizations); Assert.Equal(0, connection.Writes); Assert.Equal(0, connection.LegacyWrites);
    }
    [Fact]
    public async Task AsynchronousAuthorizationCannotReviveAnOldTransportSession()
    {
        var connection = new Connection(); var clock = new Clock();
        var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Coordinator(connection, clock, (_, _) => { entered.SetResult(); return new(permission.Task); });
        var confirmation = coordinator.ConfirmAsync(Prepare(coordinator).Id).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        connection.Reconnect(); permission.SetResult(true);
        Assert.Equal(HmiWriteDisposition.NotSent, (await confirmation.WaitAsync(TimeSpan.FromSeconds(5))).Disposition);
        Assert.Equal(0, connection.Writes);
    }
    [Fact]
    public async Task DriverAdmissionClosesTheReconnectRaceAfterOwnerThreadValidation()
    {
        var connection = new Connection(); var clock = new Clock();
        connection.BeforeAdmission = connection.Reconnect;
        var coordinator = Coordinator(connection, clock);
        var result = await coordinator.ConfirmAsync(Prepare(coordinator).Id);
        Assert.Equal(HmiWriteDisposition.NotSent, result.Disposition);
        Assert.Equal(0, connection.Writes); Assert.Equal(0, connection.LegacyWrites);
    }
    [Fact]
    public async Task AnUnchangedSessionUsesTheConditionalPathExactlyOnce()
    {
        var connection = new Connection(); var coordinator = Coordinator(connection, new Clock());
        var review = Prepare(coordinator);
        Assert.Equal(HmiWriteDisposition.DeviceAcknowledged, (await coordinator.ConfirmAsync(review.Id)).Disposition);
        Assert.Equal(HmiWriteDisposition.NotSent, (await coordinator.ConfirmAsync(review.Id)).Disposition);
        Assert.Equal(1, connection.Writes); Assert.Equal(0, connection.LegacyWrites);
    }
    [Fact]
    public async Task FailedAuditPublicationDoesNotEraseAnAcknowledgedTransportOutcome()
    {
        var connection = new Connection(); int dispatches = 0;
        var coordinator = Coordinator(connection, new Clock(), dispatch: (action, token) =>
        {
            if (++dispatches > 1) throw new ObjectDisposedException("retired UI dispatcher");
            return Dispatch(action, token);
        });
        var result = await coordinator.ConfirmAsync(Prepare(coordinator).Id);
        Assert.Equal(HmiWriteDisposition.DeviceAcknowledged, result.Disposition);
        Assert.Contains("Audit publication failed", result.Detail);
        Assert.Equal(1, connection.Writes);
    }
    [Fact]
    public async Task CallerCancellationBoundsAnAuthorizationProviderThatIgnoresCancellation()
    {
        var connection = new Connection(); var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Coordinator(connection, new Clock(), (_, _) => { entered.SetResult(); return new(permission.Task); });
        using var stop = new CancellationTokenSource();
        var confirmation = coordinator.ConfirmAsync(Prepare(coordinator).Id, stop.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        Assert.Equal(HmiWriteDisposition.NotSent, (await confirmation.WaitAsync(TimeSpan.FromSeconds(5))).Disposition);
        permission.SetResult(true);
        Assert.Equal(0, connection.Writes);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MqttCommandDestinationsCannotAliasOtherWritersOrTelemetry(bool duplicateWriter)
    {
        var profile = new HmiConnectionProfile
        {
            Protocol = HmiConnectionProtocol.Mqtt, UseTls = true, Port = 8883,
            Mappings =
            [
                new HmiIoMapping { Tag = "first", Topic = "plant/first", CommandTopic = "plant/command", Writable = true },
                new HmiIoMapping { Tag = "second", Topic = duplicateWriter ? "plant/second" : "plant/command", CommandTopic = "plant/command", Writable = duplicateWriter }
            ]
        };
        Assert.Throws<InvalidDataException>(() => profile.Validate());
    }
}
