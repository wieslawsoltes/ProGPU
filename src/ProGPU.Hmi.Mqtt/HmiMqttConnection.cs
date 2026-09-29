using System.Buffers;
using System.Security.Authentication;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;

namespace ProGPU.Hmi.Mqtt;

public sealed record HmiMqttCredentials(string UserName, string Password);

/// <summary>
/// MQTTnet-backed MQTT 5 adapter. Exact-topic subscriptions, clean sessions, strict TLS,
/// typed bounded latest-value telemetry, non-retained QoS1 absolute commands, no offline command replay.
/// </summary>
public sealed class HmiMqttConnection : IHmiConnection
{
    private readonly HmiConnectionProfile _profile;
    private readonly Func<CancellationToken, ValueTask<HmiMqttCredentials?>>? _credentials;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _bufferGate = new();
    private readonly Dictionary<string, HmiTagSample> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HmiTagSample> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HmiIoMapping[]> _topics;
    private IMqttClient? _client;
    private bool _disposed;
    private long _rejected, _coalesced;
    public bool IsConnected => !_disposed && _client?.IsConnected == true;
    public long RejectedMessages => Interlocked.Read(ref _rejected);
    public long CoalescedSamples => Interlocked.Read(ref _coalesced);

    public HmiMqttConnection(HmiConnectionProfile profile, Func<CancellationToken, ValueTask<HmiMqttCredentials?>>? credentials = null, TimeProvider? clock = null)
    {
        _profile = profile.Copy(); _profile.Validate();
        if (profile.Protocol != HmiConnectionProtocol.Mqtt) throw new ArgumentException("An MQTT profile is required.", nameof(profile));
        _credentials = credentials; _clock = clock ?? TimeProvider.System;
        _topics = _profile.Mappings.GroupBy(m => m.Topic, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }
    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _profile.RequireTransportPermission();
        using var timeout = Timeout(cancellationToken);
        await _serial.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsConnected) return;
            CloseClient();
            var factory = new MqttClientFactory();
            var client = factory.CreateMqttClient();
            _client = client;
            client.ApplicationMessageReceivedAsync += args => Receive(client, args);
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_profile.Host, _profile.Port)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithClientId("progpu-hmi-" + Guid.NewGuid().ToString("N"))
                .WithCleanSession()
                .WithSessionExpiryInterval(0)
                .WithMaximumPacketSize(65536)
                .WithReceiveMaximum(32)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(20))
                .WithTimeout(TimeSpan.FromMilliseconds(_profile.TimeoutMilliseconds));
            if (_profile.UseTls)
                options.WithTlsOptions(tls => tls.UseTls().WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13));
            var credentials = _credentials == null ? null : await _credentials(timeout.Token).ConfigureAwait(false);
            if (credentials != null)
            {
                if (!_profile.UseTls) throw new InvalidOperationException("MQTT credentials require TLS; plaintext secrets are never admitted.");
                options.WithCredentials(credentials.UserName, credentials.Password);
            }
            var result = await client.ConnectAsync(options.Build(), timeout.Token).ConfigureAwait(false);
            if ((int)result.ResultCode != 0) throw new IOException("MQTT connection rejected: " + result.ResultCode);
            if (_topics.Count > 0)
            {
                // Bound SUBSCRIBE packet size as well as the stored mapping count.
                foreach (var batch in _topics.Keys.Chunk(24))
                {
                    var subscribe = factory.CreateSubscribeOptionsBuilder();
                    foreach (var topic in batch) subscribe.WithTopicFilter(t => t.WithTopic(topic).WithAtLeastOnceQoS());
                    var response = await client.SubscribeAsync(subscribe.Build(), timeout.Token).ConfigureAwait(false);
                    if (response.Items.Any(item => (int)item.ResultCode >= 128)) throw new IOException("MQTT broker rejected one or more mapped subscriptions.");
                }
            }
        }
        catch (OperationCanceledException) { CloseClient(); throw; }
        catch (Exception error)
        {
            CloseClient();
            throw new IOException("MQTT connection/subscription failed: " + error.Message, error);
        }
        finally { _serial.Release(); }
    }
    private Task Receive(IMqttClient sender, MqttApplicationMessageReceivedEventArgs args)
    {
        lock (_bufferGate)
        {
            if (_disposed || !ReferenceEquals(sender, _client) || !_topics.TryGetValue(args.ApplicationMessage.Topic, out var mappings)) return Task.CompletedTask;
            var message = args.ApplicationMessage;
            if (message.Payload.Length > HmiMqttPayloadCodec.MaximumPayloadBytes)
            {
                Interlocked.Increment(ref _rejected);
                foreach (var mapping in mappings) MarkBad(mapping);
                return Task.CompletedTask;
            }
            byte[] payload = message.Payload.ToArray();
            var received = _clock.GetUtcNow();
            foreach (var mapping in mappings)
            {
                try
                {
                    var sample = HmiMqttPayloadCodec.Decode(payload, mapping.Type, message.Retain, received);
                    if (_last.TryGetValue(mapping.Tag, out var previous) && sample.Timestamp <= previous.Timestamp)
                    {
                        if (sample != previous) Interlocked.Increment(ref _rejected);
                        continue;
                    }
                    _last[mapping.Tag] = sample;
                    if (_pending.TryGetValue(mapping.Tag, out var pending) && (pending.Value != sample.Value || pending.Quality != HmiQuality.Good))
                    {
                        // This adapter is latest-value telemetry, not an alarm/event historian.
                        // Signal lost intermediate transitions instead of silently calling the batch good.
                        sample = sample with { Quality = HmiQuality.Uncertain };
                        Interlocked.Increment(ref _coalesced);
                    }
                    _pending[mapping.Tag] = sample;
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
                { Interlocked.Increment(ref _rejected); MarkBad(mapping); }
            }
        }
        return Task.CompletedTask;
    }
    private void MarkBad(HmiIoMapping mapping)
    {
        var sample = _last.TryGetValue(mapping.Tag, out var previous) ? previous :
            new HmiTagSample(mapping.Type switch { HmiTagType.Boolean => HmiValue.From(false), HmiTagType.Text => HmiValue.From(""), _ => HmiValue.From(0d) }, HmiQuality.Bad, DateTimeOffset.MinValue);
        _pending[mapping.Tag] = sample with { Quality = HmiQuality.Bad };
    }
    public ValueTask<IReadOnlyDictionary<string, HmiTagSample>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected) throw new IOException("MQTT connection lost.");
        lock (_bufferGate)
        {
            IReadOnlyDictionary<string, HmiTagSample> batch = new Dictionary<string, HmiTagSample>(_pending, StringComparer.Ordinal);
            _pending.Clear();
            return ValueTask.FromResult(batch);
        }
    }
    public async ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken cancellationToken)
    {
        var mapping = _profile.Mappings.SingleOrDefault(m => m.Tag == tag && m.Writable);
        if (mapping == null || value.Type != mapping.Type) return new(HmiWriteDisposition.NotSent, "No writable matching topic mapping.");
        bool entered = false, started = false;
        using var timeout = Timeout(cancellationToken);
        try
        {
            var payload = HmiMqttPayloadCodec.EncodeCommand(value, _clock.GetUtcNow());
            await _serial.WaitAsync(timeout.Token).ConfigureAwait(false); entered = true;
            if (!IsConnected) return new(HmiWriteDisposition.NotSent, "MQTT is disconnected; commands are not queued.");
            var message = new MqttApplicationMessageBuilder().WithTopic(mapping.CommandTopic).WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build();
            timeout.Token.ThrowIfCancellationRequested(); started = true;
            var result = await _client!.PublishAsync(message, timeout.Token).ConfigureAwait(false);
            return (int)result.ReasonCode >= 128
                ? new(HmiWriteDisposition.Rejected, "Broker rejected command: " + result.ReasonCode)
                : new(HmiWriteDisposition.BrokerAcknowledged, "Broker accepted a non-retained absolute command. This is NOT equipment acknowledgement; await separate feedback.");
        }
        catch (Exception error)
        {
            if (entered) CloseClient();
            return new(started ? HmiWriteDisposition.Indeterminate : HmiWriteDisposition.NotSent,
                started ? "Broker command outcome unknown; no automatic retry. " + error.Message : error.Message);
        }
        finally { if (entered) _serial.Release(); }
    }
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CloseClient(); }
        finally { _serial.Release(); }
    }
    private void CloseClient()
    {
        IMqttClient? client;
        lock (_bufferGate) { client = _client; _client = null; _pending.Clear(); }
        client?.Dispose();
    }
    private CancellationTokenSource Timeout(CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        source.CancelAfter(_profile.TimeoutMilliseconds); return source;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        await _serial.WaitAsync().ConfigureAwait(false);
        try { CloseClient(); lock (_bufferGate) _last.Clear(); }
        finally { _serial.Release(); }
    }
}
