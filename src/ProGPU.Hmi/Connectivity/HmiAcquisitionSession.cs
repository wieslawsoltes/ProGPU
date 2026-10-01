using System.Diagnostics;

namespace ProGPU.Hmi;

/// <summary>
/// One bounded acquisition loop with awaited owner-thread publication. Reconnect retries reads only.
/// Stop cancels the generation before transport retirement; a queued old result cannot publish.
/// </summary>
public sealed class HmiAcquisitionSession : IAsyncDisposable
{
    private readonly IHmiConnection _connection;
    private readonly HmiConnectionProfile _profile;
    private readonly HmiRuntime _runtime;
    private readonly HmiRuntimeDispatcher _dispatch;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _loop;
    private Task? _disposal;
    private long _polls, _failures;
    private int _generation;
    private DateTimeOffset? _lastSuccess;
    private HmiConnectionDiagnostics _diagnostics = new(HmiConnectionState.Disconnected, 0, 0, 0, null, "Not connected");
    public HmiConnectionDiagnostics Diagnostics => Volatile.Read(ref _diagnostics);

    public HmiAcquisitionSession(IHmiConnection connection, HmiConnectionProfile profile, HmiRuntime runtime, HmiRuntimeDispatcher dispatcher, TimeProvider? clock = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _profile = profile.Copy(); _profile.Validate();
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatch = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? TimeProvider.System;
    }
    public void Start()
    {
        lock (_gate)
        {
            if (_disposal != null) throw new ObjectDisposedException(nameof(HmiAcquisitionSession));
            if (_loop != null) throw new InvalidOperationException("An acquisition session starts once.");
            int generation = ++_generation;
            _loop = Task.Run(() => RunAsync(generation, _stop.Token));
        }
    }
    private async Task RunAsync(int generation, CancellationToken token)
    {
        int attempts = 0;
        try
        {
            // Never display serialized training defaults as real controller measurements.
            await PublishQualityAsync(HmiQuality.Uncertain, generation, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    if (!_connection.IsConnected)
                    {
                        SetStatus(attempts == 0 ? HmiConnectionState.Connecting : HmiConnectionState.Reconnecting, "Connecting");
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(_profile.TimeoutMilliseconds);
                        await _connection.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    }
                    IReadOnlyDictionary<string, HmiTagSample> batch;
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(_profile.TimeoutMilliseconds);
                        batch = await _connection.ReadAsync(timeout.Token).ConfigureAwait(false);
                    }
                    // Own the returned collection before crossing threads.
                    var copy = batch.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    var allowed = _profile.Mappings.Select(m => m.Tag).ToHashSet(StringComparer.Ordinal);
                    if (copy.Keys.Any(k => !allowed.Contains(k))) throw new InvalidDataException("Adapter returned an unconfigured tag.");
                    await _dispatch(() =>
                    {
                        if (generation != Volatile.Read(ref _generation) || token.IsCancellationRequested) return;
                        var now = CurrentTime();
                        _runtime.Publish(copy, now);
                    }, token).ConfigureAwait(false);
                    Interlocked.Increment(ref _polls);
                    _lastSuccess = _clock.GetUtcNow(); attempts = 0;
                    SetStatus(HmiConnectionState.Online, "Acquiring; writes require separate authorization", watch.Elapsed.TotalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(_profile.PollMilliseconds), _clock, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or InvalidOperationException or ArgumentException or OperationCanceledException)
                {
                    Interlocked.Increment(ref _failures);
                    SetStatus(HmiConnectionState.Reconnecting, error.Message, watch.Elapsed.TotalMilliseconds);
                    await PublishQualityAsync(HmiQuality.Bad, generation, token).ConfigureAwait(false);
                    try { await _connection.DisconnectAsync(token).ConfigureAwait(false); }
                    catch (Exception disconnectError) when (disconnectError is IOException or OperationCanceledException or InvalidOperationException) { }
                    int delay = Math.Min(10000, 250 * (1 << Math.Min(attempts++, 5)));
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), _clock, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failures);
            try { await PublishQualityAsync(HmiQuality.Bad, generation, token).ConfigureAwait(false); }
            catch (Exception qualityError) { SetStatus(HmiConnectionState.Faulted, error.Message + " Quality publication failed: " + qualityError.Message); }
            if (Diagnostics.State != HmiConnectionState.Faulted) SetStatus(HmiConnectionState.Faulted, error.Message);
        }
        finally
        {
            if (Diagnostics.State != HmiConnectionState.Faulted) SetStatus(HmiConnectionState.Disconnected, "Acquisition stopped");
        }
    }
    private DateTimeOffset CurrentTime()
    {
        var now = _clock.GetUtcNow();
        return now < _runtime.Now ? _runtime.Now : now;
    }
    private ValueTask PublishQualityAsync(HmiQuality quality, int generation, CancellationToken token) => _dispatch(() =>
    {
        if (generation != Volatile.Read(ref _generation) || token.IsCancellationRequested) return;
        var batch = new Dictionary<string, HmiTagSample>(StringComparer.Ordinal);
        foreach (var mapping in _profile.Mappings)
            if (_runtime.TryRead(mapping.Tag, out var old)) batch[mapping.Tag] = old with { Quality = quality };
        _runtime.Publish(batch, CurrentTime());
    }, token);
    private void SetStatus(HmiConnectionState state, string message, double milliseconds = 0) =>
        Volatile.Write(ref _diagnostics, new(state, Interlocked.Read(ref _polls), Interlocked.Read(ref _failures), milliseconds, _lastSuccess, message));
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal == null)
            {
                Interlocked.Increment(ref _generation);
                _stop.Cancel();
                SetStatus(HmiConnectionState.Stopping, "Retiring transport");
                _disposal = DisposeCoreAsync();
            }
            return new ValueTask(_disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        try
        {
            if (_loop != null) await _loop.ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally { _stop.Dispose(); SetStatus(HmiConnectionState.Disconnected, "Transport disposed"); }
    }
}
