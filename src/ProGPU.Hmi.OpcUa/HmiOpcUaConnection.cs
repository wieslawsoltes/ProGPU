using System.Security.Authentication;
using Opc.Ua;
using Opc.Ua.Client;

namespace ProGPU.Hmi.OpcUa;

/// <summary>
/// Explicit OPC UA client with one serialized service operation, bounded reads/browse and no write retries.
/// Application configuration and user identities are supplied out-of-band; project files carry neither secrets nor trust decisions.
/// </summary>
public sealed class HmiOpcUaConnection : IHmiConditionalWriteConnection, IHmiNodeBrowser, IHmiConnectionGeneration
{
    private readonly HmiConnectionProfile _profile;
    private readonly Func<CancellationToken, ValueTask<ApplicationConfiguration>> _configurationFactory;
    private readonly Func<CancellationToken, ValueTask<IUserIdentity>>? _identityFactory;
    private readonly ITelemetryContext _telemetry;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _clock;
    private ApplicationConfiguration? _configuration;
    private ISession? _session;
    private NodeId[] _nodes = [];
    private Dictionary<string, HmiTagSample> _last = new(StringComparer.Ordinal);
    private Task? _disposal;
    private int _disposed;
    private long _generation;
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _session)?.Connected == true;
    public long ConnectionGeneration => Interlocked.Read(ref _generation);

    public HmiOpcUaConnection(HmiConnectionProfile profile,
        Func<CancellationToken, ValueTask<ApplicationConfiguration>>? configurationFactory = null,
        Func<CancellationToken, ValueTask<IUserIdentity>>? identityFactory = null,
        ITelemetryContext? telemetry = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (profile.Protocol != HmiConnectionProtocol.OpcUa) throw new ArgumentException("An OPC UA profile is required.", nameof(profile));
        _profile = profile.Copy();
        _configurationFactory = configurationFactory ?? (ct => HmiOpcUaApplication.CreateAsync(cancellationToken: ct));
        _identityFactory = identityFactory;
        _telemetry = telemetry ?? HmiOpcUaApplication.Telemetry;
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        _profile.RequireTransportPermission();
        using var timeout = Timeout(cancellationToken);
        await _operation.WaitAsync(timeout.Token).ConfigureAwait(false);
        ISession? candidate = null;
        try
        {
            ThrowIfDisposed();
            if (IsConnected) return;
            DropSession();
            _configuration ??= await _configurationFactory(timeout.Token).ConfigureAwait(false);
            if (_configuration.SecurityConfiguration.AutoAcceptUntrustedCertificates)
                throw new AuthenticationException("Automatic certificate acceptance is not allowed for HMI connections.");
            var endpointConfiguration = EndpointConfiguration.Create(_configuration);
            endpointConfiguration.OperationTimeout = _profile.TimeoutMilliseconds;
            string url = EndpointUrl(_profile);
            using var discovery = await DiscoveryClient.CreateAsync(new Uri(url), endpointConfiguration, _telemetry, ct: timeout.Token).ConfigureAwait(false);
            var descriptions = await discovery.GetEndpointsAsync(null, timeout.Token).ConfigureAwait(false);
            var description = SelectEndpoint(_profile, descriptions);
            var endpoint = new ConfiguredEndpoint(null, description, endpointConfiguration);
            IUserIdentity identity = _identityFactory == null ? new UserIdentity() : await _identityFactory(timeout.Token).ConfigureAwait(false);
            if (_profile.OpcUa.SecurityMode == HmiOpcUaSecurityMode.None && identity.TokenType != UserTokenType.Anonymous)
                throw new AuthenticationException("User credentials require an encrypted OPC UA SecureChannel.");
            candidate = await new DefaultSessionFactory(_telemetry).CreateAsync(
                _configuration, endpoint, false, true, "ProGPU HMI / " + _profile.Id,
                (uint)_profile.OpcUa.SessionTimeoutMilliseconds, identity, null, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var nodes = _profile.Mappings.Select(m => HmiOpcUaCodec.Resolve(m.OpcUa, candidate.NamespaceUris)).ToArray();
            // Canonical SDK resolution also catches textual aliases such as equivalent base64 identifiers.
            var writableNodes = nodes.Where((_, index) => _profile.Mappings[index].Writable).ToArray();
            if (writableNodes.Distinct().Count() != writableNodes.Length) throw new InvalidDataException("Multiple writers resolve to the same OPC UA node.");
            _nodes = nodes;
            Volatile.Write(ref _session, candidate);
            candidate = null;
            Interlocked.Increment(ref _generation);
        }
        catch (ServiceResultException error) { throw new IOException("OPC UA session establishment failed: " + error.StatusCode, error); }
        finally { candidate?.Dispose(); _operation.Release(); }
    }

    public async ValueTask<IReadOnlyDictionary<string, HmiTagSample>> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        await _operation.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            var next = new Dictionary<string, HmiTagSample>(_profile.Mappings.Count, StringComparer.Ordinal);
            int batchSize = _profile.OpcUa.ReadBatchSize;
            // Honor server-advertised operation limits without ever increasing the configured bound.
            uint limit = session.OperationLimits.MaxNodesPerRead;
            if (limit > 0) batchSize = (int)Math.Min((uint)batchSize, limit);
            for (int offset = 0; offset < _nodes.Length; offset += batchSize)
            {
                int count = Math.Min(batchSize, _nodes.Length - offset);
                var requests = new ReadValueIdCollection();
                for (int i = 0; i < count; i++) requests.Add(new ReadValueId { NodeId = _nodes[offset + i], AttributeId = Attributes.Value });
                var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, requests, timeout.Token).ConfigureAwait(false);
                if (response.Results == null || response.Results.Count != count) throw new InvalidDataException("OPC UA Read response count does not match the request.");
                var now = _clock.GetUtcNow();
                for (int i = 0; i < count; i++)
                {
                    var mapping = _profile.Mappings[offset + i];
                    var sample = HmiOpcUaCodec.Decode(mapping, response.Results[i], now);
                    if (_last.TryGetValue(mapping.Tag, out var previous) &&
                        (sample.Timestamp < previous.Timestamp || sample.Timestamp == previous.Timestamp && sample.Value != previous.Value))
                    {
                        // Quarantine time reversal/equal-time conflicting values. Do not turn old data into fresh telemetry.
                        sample = previous with { Quality = HmiQuality.Bad };
                    }
                    next.Add(mapping.Tag, sample);
                }
            }
            timeout.Token.ThrowIfCancellationRequested();
            _last = next;
            return new Dictionary<string, HmiTagSample>(next, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is ServiceResultException or InvalidDataException or OperationCanceledException or IOException)
        {
            DropSession();
            if (error is ServiceResultException service) throw new IOException("OPC UA read failed: " + service.StatusCode, service);
            throw;
        }
        finally { _operation.Release(); }
    }

    public ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken cancellationToken)
        => WriteCoreAsync(tag, value, null, cancellationToken);
    public ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, long expectedConnectionGeneration, CancellationToken cancellationToken)
        => WriteCoreAsync(tag, value, expectedConnectionGeneration, cancellationToken);
    private async ValueTask<HmiWriteResult> WriteCoreAsync(string tag, HmiValue value, long? expectedConnectionGeneration, CancellationToken cancellationToken)
    {
        var mapping = _profile.Mappings.SingleOrDefault(m => m.Tag == tag && m.Writable);
        if (mapping == null) return new(HmiWriteDisposition.NotSent, "No writable OPC UA mapping.");
        object wireValue;
        try { wireValue = HmiOpcUaCodec.Encode(mapping, value); }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return new(HmiWriteDisposition.NotSent, error.Message); }
        using var timeout = Timeout(cancellationToken);
        bool acquired = false, attempted = false;
        try
        {
            await _operation.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            var session = RequireSession();
            if (expectedConnectionGeneration.HasValue && expectedConnectionGeneration.Value != ConnectionGeneration)
                return new(HmiWriteDisposition.NotSent, "OPC UA session changed after command review.");
            var node = HmiOpcUaCodec.Resolve(mapping.OpcUa, session.NamespaceUris);
            var requests = new WriteValueCollection
            {
                new WriteValue { NodeId = node, AttributeId = Attributes.Value, Value = new DataValue(new Variant(wireValue)) }
            };
            timeout.Token.ThrowIfCancellationRequested();
            attempted = true;
            var response = await session.WriteAsync(null, requests, timeout.Token).ConfigureAwait(false);
            if (response.Results == null || response.Results.Count != 1) throw new InvalidDataException("Malformed OPC UA Write result count.");
            var status = response.Results[0];
            if (StatusCode.IsBad(status)) return new(HmiWriteDisposition.Rejected, "OPC UA server rejected the write: " + status);
            if (!StatusCode.IsGood(status)) return new(HmiWriteDisposition.Indeterminate, "Uncertain OPC UA Write status: " + status + ". Verify independent readback; do not retry automatically.");
            return new(HmiWriteDisposition.DeviceAcknowledged, "OPC UA server acknowledged an absolute-value write (" + status + "); equipment state requires independent readback.");
        }
        catch (Exception error)
        {
            if (acquired) DropSession();
            return new(attempted ? HmiWriteDisposition.Indeterminate : HmiWriteDisposition.NotSent,
                attempted ? "Write outcome unknown; do not retry automatically. " + error.Message : error.Message);
        }
        finally { if (acquired) _operation.Release(); }
    }

    public async ValueTask<HmiBrowseResult> BrowseAsync(string namespaceUri, string identifier, int maximumResults, CancellationToken cancellationToken)
    {
        if (maximumResults is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumResults));
        using var timeout = Timeout(cancellationToken);
        await _operation.WaitAsync(timeout.Token).ConfigureAwait(false);
        byte[]? continuation = null;
        try
        {
            var session = RequireSession();
            var node = HmiOpcUaCodec.Resolve(new HmiOpcUaAddress { NamespaceUri = namespaceUri, Identifier = identifier }, session.NamespaceUris);
            var requests = new BrowseDescriptionCollection
            {
                new BrowseDescription { NodeId = node, BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences, IncludeSubtypes = true,
                    NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable), ResultMask = (uint)BrowseResultMask.All }
            };
            var response = await session.BrowseAsync(null, null, (uint)Math.Min(128, maximumResults), requests, timeout.Token).ConfigureAwait(false);
            BrowseResultCollection results = response.Results;
            var nodes = new List<HmiBrowseNode>();
            var identities = new HashSet<(string, string)>();
            bool truncated = false;
            for (int page = 0; ; page++)
            {
                if (results == null || results.Count != 1) throw new InvalidDataException("Malformed OPC UA Browse result count.");
                var result = results[0];
                continuation = result.ContinuationPoint;
                if (StatusCode.IsBad(result.StatusCode)) throw new IOException("OPC UA browse rejected: " + result.StatusCode);
                foreach (var reference in result.References)
                {
                    if (reference.NodeId.ServerIndex != 0) continue; // Never follow another server implicitly.
                    var local = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
                    if (local == null) continue;
                    string uri = local.NamespaceIndex == 0 ? "" : session.NamespaceUris.GetString(local.NamespaceIndex);
                    if (uri == null) continue;
                    string id = new NodeId(local.Identifier, 0).ToString();
                    if (!identities.Add((uri, id))) continue;
                    if (nodes.Count == maximumResults) { truncated = true; break; }
                    string display = reference.DisplayName.Text ?? reference.BrowseName.Name ?? id;
                    nodes.Add(new(uri, id, display.Length <= 4096 ? display : display[..4096], reference.NodeClass.ToString()));
                }
                if (continuation == null || continuation.Length == 0) break;
                if (truncated || nodes.Count >= maximumResults || page >= 31) { truncated = true; break; }
                var more = await session.BrowseNextAsync(null, false, new ByteStringCollection { continuation }, timeout.Token).ConfigureAwait(false);
                results = more.Results;
            }
            return new(nodes.AsReadOnly(), truncated);
        }
        catch (ServiceResultException error) { DropSession(); throw new IOException("OPC UA browse failed: " + error.StatusCode, error); }
        catch (OperationCanceledException) { DropSession(); throw; }
        catch (InvalidDataException) { DropSession(); throw; }
        finally
        {
            try
            {
                if (continuation is { Length: > 0 } && _session is { Connected: true } session)
                {
                    using var releaseTimeout = new CancellationTokenSource(_profile.TimeoutMilliseconds);
                    await session.BrowseNextAsync(null, true, new ByteStringCollection { continuation }, releaseTimeout.Token).ConfigureAwait(false);
                }
            }
            catch { DropSession(); }
            finally { _operation.Release(); }
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { DropSession(); }
        finally { _operation.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposal == null)
            {
                Volatile.Write(ref _disposed, 1);
                _shutdown.Cancel();
                _disposal = DisposeCoreAsync();
            }
            return new(_disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try { DropSession(); _last.Clear(); _configuration = null; }
        finally { _operation.Release(); }
        // Keep synchronization objects alive so calls racing retirement fail deterministically, not on a disposed semaphore.
    }
    private void DropSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        _nodes = [];
        if (session == null) return;
        Interlocked.Increment(ref _generation);
        session.Dispose();
    }
    private ISession RequireSession()
    {
        ThrowIfDisposed();
        return _session is { Connected: true } session ? session : throw new InvalidOperationException("OPC UA session is disconnected.");
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    private CancellationTokenSource Timeout(CancellationToken token)
    {
        ThrowIfDisposed();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        timeout.CancelAfter(_profile.TimeoutMilliseconds);
        return timeout;
    }
    public static string EndpointUrl(HmiConnectionProfile profile)
    {
        profile.Validate();
        return new UriBuilder("opc.tcp", profile.Host, profile.Port, profile.OpcUa.EndpointPath).Uri.AbsoluteUri;
    }
    public static EndpointDescription SelectEndpoint(HmiConnectionProfile profile, IEnumerable<EndpointDescription> endpoints)
    {
        string url = EndpointUrl(profile);
        var mode = profile.OpcUa.SecurityMode == HmiOpcUaSecurityMode.None ? MessageSecurityMode.None : MessageSecurityMode.SignAndEncrypt;
        string policy = mode == MessageSecurityMode.None ? SecurityPolicies.None : profile.OpcUa.SecurityPolicy switch
        {
            HmiOpcUaSecurityPolicy.Basic256Sha256 => SecurityPolicies.Basic256Sha256,
            HmiOpcUaSecurityPolicy.Aes128Sha256RsaOaep => SecurityPolicies.Aes128_Sha256_RsaOaep,
            HmiOpcUaSecurityPolicy.Aes256Sha256RsaPss => SecurityPolicies.Aes256_Sha256_RsaPss,
            _ => throw new InvalidDataException("Unsupported OPC UA security policy.")
        };
        var match = endpoints.FirstOrDefault(e => e.SecurityMode == mode && e.SecurityPolicyUri == policy &&
            Uri.TryCreate(e.EndpointUrl, UriKind.Absolute, out var advertised) && advertised == new Uri(url));
        return match ?? throw new AuthenticationException("The server does not advertise the exact configured endpoint, security mode and policy. No redirect or security downgrade was attempted.");
    }
}
