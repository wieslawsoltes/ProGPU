using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ProGPU.Hmi.Tests;

internal sealed class ModbusLoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    public ushort[] Registers { get; } = new ushort[65536];
    public bool[] Coils { get; } = new bool[65536];
    public int Port { get; }
    public int ReadCount;
    public int WriteCount;
    public bool FragmentReplies { get; set; }
    public bool WrongTransaction { get; set; }
    public bool OversizedReply { get; set; }
    public bool CloseAfterWrite { get; set; }
    public bool SuppressReplies { get; set; }
    public byte ExceptionCode { get; set; }
    public ModbusLoopbackServer()
    {
        _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _run = RunAsync();
    }
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                try { await ServeAsync(client); }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        var stream = client.GetStream();
        while (!_stop.IsCancellationRequested)
        {
            var header = new byte[7]; await stream.ReadExactlyAsync(header, _stop.Token);
            int size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1;
            if (size is < 1 or > 253) throw new InvalidDataException();
            var pdu = new byte[size]; await stream.ReadExactlyAsync(pdu, _stop.Token);
            int address = Read16(pdu, 1), argument = Read16(pdu, 3);
            bool write = pdu[0] is 5 or 6 or 16;
            if (write) Interlocked.Increment(ref WriteCount); else Interlocked.Increment(ref ReadCount);
            byte[] reply;
            if (ExceptionCode != 0) reply = [(byte)(pdu[0] | 0x80), ExceptionCode];
            else if (pdu[0] is 1 or 2)
            {
                reply = new byte[2 + (argument + 7) / 8]; reply[0] = pdu[0]; reply[1] = (byte)(reply.Length - 2);
                for (int i = 0; i < argument; i++) if (Coils[address + i]) reply[2 + i / 8] |= (byte)(1 << (i % 8));
            }
            else if (pdu[0] is 3 or 4)
            {
                reply = new byte[2 + argument * 2]; reply[0] = pdu[0]; reply[1] = (byte)(reply.Length - 2);
                for (int i = 0; i < argument; i++) Put(reply, 2 + i * 2, Registers[address + i]);
            }
            else
            {
                if (pdu[0] == 5) Coils[address] = argument == 0xff00;
                else if (pdu[0] == 6) Registers[address] = (ushort)argument;
                else if (pdu[0] == 16) for (int i = 0; i < argument; i++) Registers[address + i] = Read16(pdu, 6 + i * 2);
                else throw new InvalidDataException("Unexpected function in fixture.");
                reply = pdu[..5];
            }
            if (write && CloseAfterWrite) return;
            if (SuppressReplies) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
            byte[] frame = new byte[7 + reply.Length]; header.CopyTo(frame, 0);
            if (WrongTransaction) Put(frame, 0, (Read16(header, 0) + 1) % 65536);
            Put(frame, 4, OversizedReply ? 65535 : reply.Length + 1); reply.CopyTo(frame, 7);
            if (FragmentReplies)
            {
                for (int i = 0; i < frame.Length; i++) await stream.WriteAsync(frame.AsMemory(i, 1), _stop.Token);
            }
            else await stream.WriteAsync(frame, _stop.Token);
        }
    }
    private static ushort Read16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
    private static void Put(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), checked((ushort)value));
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _run; _stop.Dispose();
    }
}
