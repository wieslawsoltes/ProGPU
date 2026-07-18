using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ProGPU.Browser;

public enum BrowserGpuOpcode : ushort
{
    BeginFrame = 1,
    EndFrame = 2,
    CreateBuffer = 10,
    CreateTexture = 11,
    CreateTextureView = 12,
    CreateSampler = 13,
    CreateShaderModule = 14,
    CreateBindGroupLayout = 15,
    CreatePipelineLayout = 16,
    CreateBindGroup = 17,
    CreateRenderPipeline = 18,
    CreateComputePipeline = 19,
    ReleaseResource = 20,
    DestroyBuffer = 21,
    DestroyTexture = 22,
    GetBindGroupLayout = 23,
    CreateCommandEncoder = 24,
    FinishCommandEncoder = 25,
    CreateSurfaceTexture = 26,
    CreateQuerySet = 27,
    BeginRenderPass = 30,
    EndRenderPass = 31,
    SetRenderPipeline = 32,
    SetBindGroup = 33,
    SetVertexBuffer = 34,
    SetIndexBuffer = 35,
    SetViewport = 36,
    SetScissorRect = 37,
    SetBlendConstant = 38,
    SetStencilReference = 39,
    Draw = 40,
    DrawIndexed = 41,
    BeginComputePass = 50,
    EndComputePass = 51,
    SetComputePipeline = 52,
    DispatchWorkgroups = 53,
    CopyBufferToBuffer = 60,
    CopyBufferToTexture = 61,
    CopyTextureToBuffer = 62,
    CopyTextureToTexture = 63,
    ClearBuffer = 64,
    WriteTimestamp = 65,
    ResolveQuerySet = 66,
    Submit = 70,
    QueueWriteBuffer = 71,
    QueueWriteTexture = 72,
    BufferUnmap = 73
}

[Flags]
public enum BrowserGpuPacketFlags : ushort
{
    None = 0,
    Validation = 1
}

public static class BrowserGpuProtocol
{
    public const uint Magic = 0x55504750;
    public const ushort Version = 1;
    public const int PacketHeaderSize = 16;
    public const int CommandHeaderSize = 8;
    public const int Alignment = 8;

    public static int Align(int value) => (value + Alignment - 1) & -Alignment;
}

public readonly ref struct BrowserGpuCommand
{
    public BrowserGpuCommand(BrowserGpuOpcode opcode, ushort flags, ReadOnlySpan<byte> payload)
    {
        Opcode = opcode;
        Flags = flags;
        Payload = payload;
    }

    public BrowserGpuOpcode Opcode { get; }
    public ushort Flags { get; }
    public ReadOnlySpan<byte> Payload { get; }
}

public ref struct BrowserGpuPacketReader
{
    private readonly ReadOnlySpan<byte> _packet;
    private readonly int _commandCount;
    private int _offset;
    private int _commandsRead;

    public BrowserGpuPacketReader(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < BrowserGpuProtocol.PacketHeaderSize)
            throw new InvalidDataException("The browser GPU packet header is truncated.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(packet) != BrowserGpuProtocol.Magic)
            throw new InvalidDataException("The browser GPU packet magic is invalid.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
        if (version != BrowserGpuProtocol.Version)
            throw new NotSupportedException($"Browser GPU protocol {version} is unsupported.");
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        if (declaredLength != packet.Length)
            throw new InvalidDataException("The browser GPU packet length does not match its header.");

        _packet = packet;
        Flags = (BrowserGpuPacketFlags)BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        _commandCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]));
        _offset = BrowserGpuProtocol.PacketHeaderSize;
        _commandsRead = 0;
    }

    public BrowserGpuPacketFlags Flags { get; }
    public int CommandCount => _commandCount;

    public bool TryRead(out BrowserGpuCommand command)
    {
        if (_commandsRead == _commandCount)
        {
            if (_offset != _packet.Length)
                throw new InvalidDataException("Trailing bytes remain after the declared browser GPU commands.");
            command = default;
            return false;
        }
        if (_offset + BrowserGpuProtocol.CommandHeaderSize > _packet.Length)
            throw new InvalidDataException("The browser GPU command header is truncated.");

        var header = _packet[_offset..];
        var commandLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
        if (commandLength < BrowserGpuProtocol.CommandHeaderSize || commandLength > _packet.Length - _offset)
            throw new InvalidDataException("The browser GPU command length is invalid.");
        var alignedLength = BrowserGpuProtocol.Align(commandLength);
        if (alignedLength > _packet.Length - _offset)
            throw new InvalidDataException("The browser GPU command alignment padding is truncated.");

        command = new BrowserGpuCommand(
            (BrowserGpuOpcode)BinaryPrimitives.ReadUInt16LittleEndian(header),
            BinaryPrimitives.ReadUInt16LittleEndian(header[2..]),
            header.Slice(BrowserGpuProtocol.CommandHeaderSize, commandLength - BrowserGpuProtocol.CommandHeaderSize));
        _offset += alignedLength;
        _commandsRead++;
        return true;
    }
}

public readonly record struct BrowserGpuHandle(uint Value)
{
    public int Index => (int)(Value & 0x000F_FFFFu);
    public int Generation => (int)(Value >> 20);
    public bool IsNull => Value == 0;

    public static BrowserGpuHandle Create(int index, int generation)
    {
        if ((uint)index is 0 or > 0x000F_FFFFu)
            throw new ArgumentOutOfRangeException(nameof(index));
        if ((uint)generation is 0 or > 0xFFFu)
            throw new ArgumentOutOfRangeException(nameof(generation));
        return new BrowserGpuHandle((uint)index | ((uint)generation << 20));
    }
}

public sealed class BrowserGpuHandlePool
{
    private readonly List<ushort> _generations = [0];
    private readonly Stack<int> _free = new();

    public BrowserGpuHandle Allocate()
    {
        int index;
        if (_free.Count != 0)
        {
            index = _free.Pop();
        }
        else
        {
            index = _generations.Count;
            if (index > 0x000F_FFFF)
                throw new InvalidOperationException("The browser GPU handle table is full.");
            _generations.Add(0);
        }

        var generation = (_generations[index] + 1) & 0xFFF;
        if (generation == 0) generation = 1;
        _generations[index] = (ushort)generation;
        return BrowserGpuHandle.Create(index, generation);
    }

    public void Release(BrowserGpuHandle handle)
    {
        if (!IsCurrent(handle))
            throw new InvalidOperationException("The browser GPU handle is stale.");
        _free.Push(handle.Index);
    }

    public bool IsCurrent(BrowserGpuHandle handle) =>
        handle.Index > 0 &&
        handle.Index < _generations.Count &&
        _generations[handle.Index] == handle.Generation;
}

public unsafe sealed class BrowserGpuCommandEncoder : IDisposable
{
    private byte* _buffer;
    private int _capacity;
    private int _length;
    private int _commandCount;
    private BrowserGpuPacketFlags _flags;
    private bool _containsFrame;

    public BrowserGpuCommandEncoder(int initialCapacity = 64 * 1024)
    {
        if (initialCapacity < BrowserGpuProtocol.PacketHeaderSize)
            initialCapacity = BrowserGpuProtocol.PacketHeaderSize;
        _capacity = BrowserGpuProtocol.Align(initialCapacity);
        _buffer = (byte*)NativeMemory.Alloc((nuint)_capacity);
        Reset();
    }

    public int Length => _length;
    public int CommandCount => _commandCount;
    public bool ContainsFrame => _containsFrame;
    public nint Address => (nint)_buffer;
    public ReadOnlySpan<byte> WrittenSpan => new(_buffer, _length);

    public void Reset(BrowserGpuPacketFlags flags = BrowserGpuPacketFlags.None)
    {
        ObjectDisposedException.ThrowIf(_buffer == null, this);
        _flags = flags;
        _length = BrowserGpuProtocol.PacketHeaderSize;
        _commandCount = 0;
        _containsFrame = false;
        WritePacketHeader();
    }

    public void BeginFrame(float red, float green, float blue, float alpha, uint width, uint height)
    {
        _containsFrame = true;
        var payload = Begin(BrowserGpuOpcode.BeginFrame, 24);
        WriteSingle(payload, 0, red);
        WriteSingle(payload, 4, green);
        WriteSingle(payload, 8, blue);
        WriteSingle(payload, 12, alpha);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[16..], width);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[20..], height);
        Complete();
    }

    public void EndFrame()
    {
        Begin(BrowserGpuOpcode.EndFrame, 0);
        Complete();
    }

    public void CreateBuffer(BrowserGpuHandle handle, ulong size, uint usage, bool mappedAtCreation = false)
    {
        var payload = Begin(BrowserGpuOpcode.CreateBuffer, 24);
        WriteHandle(payload, 0, handle);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[8..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[16..], usage);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[20..], mappedAtCreation ? 1u : 0u);
        Complete();
    }

    public void CreateShaderModule(BrowserGpuHandle handle, string wgsl)
    {
        ArgumentNullException.ThrowIfNull(wgsl);
        var byteCount = Encoding.UTF8.GetByteCount(wgsl);
        var payload = Begin(BrowserGpuOpcode.CreateShaderModule, checked(8 + byteCount));
        WriteHandle(payload, 0, handle);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], (uint)byteCount);
        Encoding.UTF8.GetBytes(wgsl, payload[8..]);
        Complete();
    }

    public void CreateRenderPipeline(
        BrowserGpuHandle handle,
        BrowserGpuHandle shaderModule,
        string colorFormat,
        uint sampleCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(colorFormat);
        if (sampleCount == 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        var byteCount = Encoding.UTF8.GetByteCount(colorFormat);
        var payload = Begin(BrowserGpuOpcode.CreateRenderPipeline, checked(16 + byteCount));
        WriteHandle(payload, 0, handle);
        WriteHandle(payload, 4, shaderModule);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], sampleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[12..], (uint)byteCount);
        Encoding.UTF8.GetBytes(colorFormat, payload[16..]);
        Complete();
    }

    public void ReleaseResource(BrowserGpuHandle handle)
    {
        var payload = Begin(BrowserGpuOpcode.ReleaseResource, 4);
        WriteHandle(payload, 0, handle);
        Complete();
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        var payload = Begin(BrowserGpuOpcode.Draw, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], instanceCount);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], firstVertex);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[12..], firstInstance);
        Complete();
    }

    public void SetRenderPipeline(BrowserGpuHandle pipeline)
    {
        var payload = Begin(BrowserGpuOpcode.SetRenderPipeline, 4);
        WriteHandle(payload, 0, pipeline);
        Complete();
    }

    public void DispatchWorkgroups(uint x, uint y = 1, uint z = 1)
    {
        var payload = Begin(BrowserGpuOpcode.DispatchWorkgroups, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, x);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], y);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], z);
        Complete();
    }

    public void WriteRaw(BrowserGpuOpcode opcode, ReadOnlySpan<byte> payload)
    {
        payload.CopyTo(Begin(opcode, payload.Length));
        Complete();
    }

    public void Seal()
    {
        WritePacketHeader();
    }

    internal Span<byte> BeginCommand(BrowserGpuOpcode opcode, int payloadLength) => Begin(opcode, payloadLength);

    internal void CompleteCommand() => Complete();

    private Span<byte> Begin(BrowserGpuOpcode opcode, int payloadLength)
    {
        if (payloadLength < 0) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        if (opcode is BrowserGpuOpcode.BeginFrame or BrowserGpuOpcode.CreateSurfaceTexture)
            _containsFrame = true;
        var commandLength = checked(BrowserGpuProtocol.CommandHeaderSize + payloadLength);
        var alignedLength = BrowserGpuProtocol.Align(commandLength);
        EnsureCapacity(checked(_length + alignedLength));
        var command = new Span<byte>(_buffer + _length, alignedLength);
        command.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(command, (ushort)opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(command[2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(command[4..], (uint)commandLength);
        return command.Slice(BrowserGpuProtocol.CommandHeaderSize, payloadLength);
    }

    private void Complete()
    {
        var commandLength = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_buffer + _length + 4, 4));
        _length += BrowserGpuProtocol.Align(checked((int)commandLength));
        _commandCount++;
        WritePacketHeader();
    }

    private void WritePacketHeader()
    {
        var header = new Span<byte>(_buffer, BrowserGpuProtocol.PacketHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, BrowserGpuProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], BrowserGpuProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)_flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)_length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)_commandCount);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _capacity) return;
        var capacity = _capacity;
        while (capacity < required)
            capacity = checked(capacity * 2);
        _buffer = (byte*)NativeMemory.Realloc(_buffer, (nuint)capacity);
        if (_buffer == null) throw new OutOfMemoryException();
        _capacity = capacity;
    }

    private static void WriteHandle(Span<byte> payload, int offset, BrowserGpuHandle handle) =>
        BinaryPrimitives.WriteUInt32LittleEndian(payload[offset..], handle.Value);

    private static void WriteSingle(Span<byte> payload, int offset, float value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(payload[offset..], BitConverter.SingleToUInt32Bits(value));

    public void Dispose()
    {
        if (_buffer == null) return;
        NativeMemory.Free(_buffer);
        _buffer = null;
        _capacity = 0;
        _length = 0;
        _commandCount = 0;
    }
}
