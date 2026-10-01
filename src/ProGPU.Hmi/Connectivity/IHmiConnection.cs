namespace ProGPU.Hmi;

public enum HmiWriteDisposition { NotSent, Rejected, DeviceAcknowledged, BrokerAcknowledged, Indeterminate }

/// <summary>Transport acknowledgement is not process feedback or confirmation that equipment changed state.</summary>
public sealed record HmiWriteResult(HmiWriteDisposition Disposition, string Detail)
{
    public bool Acknowledged => Disposition is HmiWriteDisposition.DeviceAcknowledged or HmiWriteDisposition.BrokerAcknowledged;
}

/// <summary>Explicitly managed acquisition/absolute-write transport. Implementations must never automatically retry writes.</summary>
public interface IHmiConnection : IHmiTagSource
{
    bool IsConnected { get; }
    ValueTask ConnectAsync(CancellationToken cancellationToken);
    ValueTask DisconnectAsync(CancellationToken cancellationToken);
    ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken cancellationToken);
}

public enum HmiConnectionState { Disconnected, Connecting, Online, Reconnecting, Faulted, Stopping }

public sealed record HmiConnectionDiagnostics(HmiConnectionState State, long Polls, long Failures, double LastPollMilliseconds, DateTimeOffset? LastSuccess, string Message);

/// <summary>Must execute and await the callback on the runtime owner thread; cancellation must prevent late publication.</summary>
public delegate ValueTask HmiRuntimeDispatcher(Action action, CancellationToken cancellationToken);

public sealed record HmiWriteRequest(Guid Id, string ConnectionId, string Tag, HmiValue Value, HmiValue ObservedValue, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Actor, string Reason);

/// <summary>Host-supplied authorization. A typed actor string is audit context, not proof of authenticated identity.</summary>
public delegate ValueTask<bool> HmiWriteAuthorizer(HmiWriteRequest request, CancellationToken cancellationToken);
