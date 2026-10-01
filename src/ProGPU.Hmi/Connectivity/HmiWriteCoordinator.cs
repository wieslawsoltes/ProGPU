namespace ProGPU.Hmi;

/// <summary>Bounded single-use command review, authenticated host authorization and one transport attempt.</summary>
public sealed class HmiWriteCoordinator
{
    private sealed record Pending(HmiWriteRequest Request, long? TransportGeneration);
    private readonly HmiConnectionProfile _profile;
    private readonly HmiProject _project;
    private readonly HmiRuntime _runtime;
    private readonly IHmiConnection _connection;
    private readonly HmiRuntimeDispatcher _dispatch;
    private readonly HmiWriteAuthorizer? _authorize;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, Pending> _pending = [];
    private readonly object _gate = new();
    private readonly HmiHistory<HmiAuditEntry> _audit = new(512);
    private int _busy, _generation;
    public IReadOnlyList<HmiAuditEntry> Audit => _audit;

    public HmiWriteCoordinator(HmiProject project, HmiConnectionProfile profile, HmiRuntime runtime, IHmiConnection connection,
        HmiRuntimeDispatcher dispatcher, HmiWriteAuthorizer? authorizer = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _project = HmiProjectSerializer.Clone(project);
        profile.Validate(_project.Tags); _profile = profile.Copy();
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _dispatch = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _authorize = authorizer; _clock = clock ?? TimeProvider.System;
    }
    /// <summary>Call on the runtime owner thread. Preparation captures feedback and the current transport-session identity.</summary>
    public HmiWriteRequest Prepare(string tag, HmiValue value, string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason) || actor.Length > 256 || reason.Length > 1024)
            throw new InvalidOperationException("An operator identity and reason are required within the configured text limits.");
        Check(tag, value);
        var now = _clock.GetUtcNow();
        var request = new HmiWriteRequest(Guid.NewGuid(), _profile.Id, tag, value, _runtime.Read(tag).Value, now, now.AddSeconds(20), actor, reason);
        long? transportGeneration = (_connection as IHmiConnectionGeneration)?.ConnectionGeneration;
        lock (_gate)
        {
            foreach (var expired in _pending.Where(p => p.Value.Request.ExpiresAt <= now).Select(p => p.Key).ToArray()) _pending.Remove(expired);
            if (_pending.Count >= 32) throw new InvalidOperationException("Too many pending confirmations.");
            _pending.Add(request.Id, new(request, transportGeneration));
        }
        Log("PrepareWrite", tag, $"{request.Id}: {request.ObservedValue} → {value}; {actor}; {reason}");
        return request;
    }
    public void RevokeAll()
    {
        lock (_gate) { _pending.Clear(); Interlocked.Increment(ref _generation); }
    }
    public async ValueTask<HmiWriteResult> ConfirmAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Pending? pending;
        int generation;
        lock (_gate) { _pending.Remove(id, out pending); generation = _generation; }
        if (pending == null) return new(HmiWriteDisposition.NotSent, "Unknown, revoked or already consumed confirmation.");
        var request = pending.Request;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return await CompleteAsync(request, new(HmiWriteDisposition.NotSent, "Another command is in progress; prepare a new confirmation.")).ConfigureAwait(false);
        bool handedToTransport = false;
        HmiWriteResult result;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = request.ExpiresAt - _clock.GetUtcNow();
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            CheckGeneration(pending);
            if (_authorize == null || !await _authorize(request, token).AsTask().WaitAsync(token).ConfigureAwait(false))
                throw new InvalidOperationException("Host authorization denied the command.");
            await _dispatch(() =>
            {
                if (generation != Volatile.Read(ref _generation)) throw new InvalidOperationException("Confirmation was revoked during authorization.");
                if (_clock.GetUtcNow() >= request.ExpiresAt) throw new InvalidOperationException("Confirmation expired during authorization.");
                CheckGeneration(pending);
                Check(request.Tag, request.Value);
                if (_runtime.Read(request.Tag).Value != request.ObservedValue)
                    throw new InvalidOperationException("Feedback changed; review and prepare a new command.");
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _generation)) throw new InvalidOperationException("Confirmation was revoked.");
            CheckGeneration(pending);
            handedToTransport = true;
            result = _connection is IHmiConditionalWriteConnection conditional && pending.TransportGeneration is { } epoch
                ? await conditional.WriteAsync(request.Tag, request.Value, epoch, token).ConfigureAwait(false)
                : await _connection.WriteAsync(request.Tag, request.Value, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            result = new(handedToTransport ? HmiWriteDisposition.Indeterminate : HmiWriteDisposition.NotSent,
                handedToTransport ? "Transport outcome unknown; do not retry automatically. " + error.Message : error.Message);
        }
        finally { Volatile.Write(ref _busy, 0); }
        return await CompleteAsync(request, result).ConfigureAwait(false);
    }
    private void CheckGeneration(Pending pending)
    {
        if (pending.TransportGeneration is { } epoch && (_connection as IHmiConnectionGeneration)?.ConnectionGeneration != epoch)
            throw new InvalidOperationException("Transport session changed; discard the old review and prepare a new command.");
    }
    private async ValueTask<HmiWriteResult> CompleteAsync(HmiWriteRequest request, HmiWriteResult result)
    {
        using var deadline = new CancellationTokenSource(_profile.TimeoutMilliseconds);
        var auditToken = deadline.Token;
        try
        {
            await _dispatch(() =>
            {
                // A copied token remains safe to inspect even after the deadline source has been disposed.
                if (auditToken.IsCancellationRequested) return;
                Log("Write:" + result.Disposition, request.Tag, $"{request.Id}: {result.Detail}");
            }, auditToken).AsTask().WaitAsync(auditToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A retired/stalled dispatcher cannot hide an already observed transport result or hold it indefinitely.
            return result with { Detail = result.Detail + " Audit publication failed: " + error.Message };
        }
        return result;
    }
    private void Check(string name, HmiValue value)
    {
        if (!_connection.IsConnected || !_runtime.IsRunning) throw new InvalidOperationException("A live connected runtime is required.");
        var mapping = _profile.Mappings.SingleOrDefault(m => m.Tag == name && m.Writable) ?? throw new InvalidOperationException("The destination mapping is read-only or absent.");
        var tag = _project.Tags.Single(t => t.Name == name);
        if (!tag.Writable) throw new InvalidOperationException("The tag is read-only.");
        HmiProjectSerializer.ValidateValue(tag, value, true);
        var sample = _runtime.Read(name);
        var now = _clock.GetUtcNow();
        if (sample.Quality != HmiQuality.Good || now < sample.Timestamp || (now - sample.Timestamp).TotalMilliseconds > tag.StaleAfterMilliseconds)
            throw new InvalidOperationException("Feedback must be fresh and good quality.");
        if (mapping.InterlockTag.Length > 0)
        {
            var interlock = _runtime.Read(mapping.InterlockTag);
            var definition = _project.Tags.Single(t => t.Name == mapping.InterlockTag);
            if (interlock.Quality != HmiQuality.Good || !interlock.Value.AsBoolean() || now < interlock.Timestamp || (now - interlock.Timestamp).TotalMilliseconds > definition.StaleAfterMilliseconds)
                throw new InvalidOperationException("The configured write permissive is false, stale or invalid.");
        }
    }
    private void Log(string operation, string tag, string detail) => _audit.Add(new(_clock.GetUtcNow(), operation, tag, detail));
}
