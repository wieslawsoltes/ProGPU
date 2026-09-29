using System.Buffers.Binary;
using System.Net.Sockets;

namespace ProGPU.Hmi.Modbus;

/// <summary>
/// Modbus TCP client: FC01/02/03/04 reads, FC05/06/10 absolute writes.
/// One in-flight request per stream; strict MBAP/length/function/echo validation and no write retries.
/// </summary>
public sealed class HmiModbusConnection : IHmiConnection
{
    private sealed record ReadBlock(HmiModbusArea Area, int Address, int Count, HmiIoMapping[] Mappings);
    private readonly HmiConnectionProfile _profile;
    private readonly ReadBlock[] _blocks;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _clock;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _transaction;
    private bool _disposed;
    public bool IsConnected => !_disposed && _stream != null;

    public HmiModbusConnection(HmiConnectionProfile profile, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile.Copy(); _profile.Validate();
        if (profile.Protocol != HmiConnectionProtocol.ModbusTcp) throw new ArgumentException("A Modbus TCP profile is required.", nameof(profile));
        _clock = clock ?? TimeProvider.System;
        _blocks = Plan(_profile.Mappings);
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
            if (_stream != null) return;
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(_profile.Host, _profile.Port, timeout.Token).ConfigureAwait(false);
                _client = client; _stream = client.GetStream();
            }
            catch { client.Dispose(); throw; }
        }
        finally { _serial.Release(); }
    }
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CloseStream(); }
        finally { _serial.Release(); }
    }
    public async ValueTask<IReadOnlyDictionary<string, HmiTagSample>> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = Timeout(cancellationToken);
        await _serial.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var values = new Dictionary<string, HmiTagSample>(StringComparer.Ordinal);
            foreach (var block in _blocks)
            {
                byte function = block.Area switch { HmiModbusArea.Coil => 1, HmiModbusArea.DiscreteInput => 2, HmiModbusArea.HoldingRegister => 3, _ => 4 };
                var request = new byte[5]; request[0] = function;
                Put(request, 1, block.Address); Put(request, 3, block.Count);
                byte[] reply = await ExchangeAsync(request, timeout.Token).ConfigureAwait(false);
                bool bits = function <= 2;
                int byteCount = bits ? (block.Count + 7) / 8 : block.Count * 2;
                if (reply.Length != byteCount + 2 || reply[1] != byteCount) throw new InvalidDataException("Modbus read response length/count does not match the request.");
                var timestamp = _clock.GetUtcNow();
                foreach (var mapping in block.Mappings)
                {
                    int offset = mapping.Address - block.Address;
                    HmiValue value;
                    HmiQuality quality = HmiQuality.Good;
                    if (bits) value = HmiValue.From((reply[2 + offset / 8] & (1 << (offset % 8))) != 0);
                    else
                    {
                        try { value = HmiValue.From(HmiRegisterCodec.Decode(reply.AsSpan(2 + offset * 2, mapping.RegisterCount * 2), mapping)); }
                        catch (InvalidDataException) { value = HmiValue.From(0d); quality = HmiQuality.Bad; }
                    }
                    values.Add(mapping.Tag, new(value, quality, timestamp));
                }
            }
            return values;
        }
        catch (HmiModbusException) { throw; }
        catch { CloseStream(); throw; }
        finally { _serial.Release(); }
    }
    public async ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken cancellationToken)
    {
        var mapping = _profile.Mappings.SingleOrDefault(m => m.Tag == tag && m.Writable);
        if (mapping == null || value.Type != mapping.Type) return new(HmiWriteDisposition.NotSent, "Unknown, read-only or type-incompatible destination.");
        byte[] request;
        try
        {
            if (mapping.Area == HmiModbusArea.Coil)
            {
                request = new byte[5]; request[0] = 5;
                Put(request, 1, mapping.Address); Put(request, 3, value.Boolean ? 0xff00 : 0);
            }
            else
            {
                var bytes = HmiRegisterCodec.Encode(value.Number, mapping);
                if (mapping.RegisterCount == 1)
                {
                    request = new byte[5]; request[0] = 6; Put(request, 1, mapping.Address);
                    bytes.CopyTo(request, 3);
                }
                else
                {
                    request = new byte[6 + bytes.Length]; request[0] = 16;
                    Put(request, 1, mapping.Address); Put(request, 3, mapping.RegisterCount);
                    request[5] = (byte)bytes.Length; bytes.CopyTo(request, 6);
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or OverflowException)
        { return new(HmiWriteDisposition.NotSent, error.Message); }
        bool entered = false, transmissionStarted = false;
        using var timeout = Timeout(cancellationToken);
        try
        {
            await _serial.WaitAsync(timeout.Token).ConfigureAwait(false); entered = true;
            if (_disposed || _stream == null) return new(HmiWriteDisposition.NotSent, "Not connected.");
            timeout.Token.ThrowIfCancellationRequested();
            transmissionStarted = true;
            byte[] reply = await ExchangeAsync(request, timeout.Token).ConfigureAwait(false);
            if (reply.Length != 5 || !reply.AsSpan().SequenceEqual(request.AsSpan(0, 5)))
                throw new InvalidDataException("Modbus write acknowledgement did not echo the requested address/value/count.");
            return new(HmiWriteDisposition.DeviceAcknowledged, "Controller acknowledged the absolute write; await independent process feedback.");
        }
        catch (HmiModbusException error) { return new(HmiWriteDisposition.Rejected, error.Message); }
        catch (Exception error) when (error is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (entered) CloseStream();
            return new(transmissionStarted ? HmiWriteDisposition.Indeterminate : HmiWriteDisposition.NotSent,
                transmissionStarted ? "Write outcome unknown; do not automatically retry. " + error.Message : error.Message);
        }
        finally { if (entered) _serial.Release(); }
    }
    private async Task<byte[]> ExchangeAsync(byte[] pdu, CancellationToken token)
    {
        var stream = _stream ?? throw new InvalidOperationException("Modbus is disconnected.");
        ushort transaction = unchecked(++_transaction);
        byte[] frame = new byte[pdu.Length + 7];
        Put(frame, 0, transaction); Put(frame, 2, 0); Put(frame, 4, pdu.Length + 1); frame[6] = _profile.UnitId;
        pdu.CopyTo(frame, 7);
        await stream.WriteAsync(frame, token).ConfigureAwait(false);
        byte[] header = new byte[7];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != transaction || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 || header[6] != _profile.UnitId || length is < 2 or > 254)
            throw new InvalidDataException("Invalid Modbus MBAP identity, protocol, unit or bounded length.");
        byte[] reply = new byte[length - 1];
        await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        if (reply[0] == (pdu[0] | 0x80))
        {
            if (reply.Length != 2) throw new InvalidDataException("Malformed Modbus exception.");
            throw new HmiModbusException(pdu[0], reply[1]);
        }
        if (reply[0] != pdu[0]) throw new InvalidDataException("Modbus response function mismatch.");
        return reply;
    }
    private CancellationTokenSource Timeout(CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        source.CancelAfter(_profile.TimeoutMilliseconds);
        return source;
    }
    private void CloseStream() { _stream?.Dispose(); _stream = null; _client?.Dispose(); _client = null; }
    private static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), checked((ushort)value));
    private static ReadBlock[] Plan(IEnumerable<HmiIoMapping> mappings)
    {
        var blocks = new List<ReadBlock>();
        foreach (var area in mappings.GroupBy(m => m.Area))
        {
            var current = new List<HmiIoMapping>(); int start = 0, end = 0;
            int limit = area.Key is HmiModbusArea.Coil or HmiModbusArea.DiscreteInput ? 2000 : 125;
            foreach (var mapping in area.OrderBy(m => m.Address))
            {
                int nextEnd = mapping.Address + mapping.RegisterCount;
                if (current.Count > 0 && (mapping.Address > end || nextEnd - start > limit))
                {
                    blocks.Add(new(area.Key, start, end - start, current.ToArray())); current.Clear();
                }
                if (current.Count == 0) { start = mapping.Address; end = nextEnd; }
                end = Math.Max(end, nextEnd); current.Add(mapping);
            }
            if (current.Count > 0) blocks.Add(new(area.Key, start, end - start, current.ToArray()));
        }
        return blocks.ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        await _serial.WaitAsync().ConfigureAwait(false);
        try { CloseStream(); }
        finally { _serial.Release(); }
        // Keep the lightweight synchronization objects alive for safely rejected concurrent callers.
    }
}
