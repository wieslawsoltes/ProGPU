namespace Windows.Storage.Streams;

/// <summary>
/// WinRT-compatible mutable byte-buffer contract.
/// </summary>
public interface IBuffer
{
    uint Capacity { get; }
    uint Length { get; set; }
}

/// <summary>
/// Managed implementation of the WinRT byte-buffer contract.
/// </summary>
public sealed class Buffer : IBuffer
{
    private readonly byte[] _storage;
    private uint _length;

    public Buffer(uint capacity)
    {
        _storage = new byte[checked((int)capacity)];
    }

    public uint Capacity => checked((uint)_storage.Length);

    public uint Length
    {
        get => _length;
        set
        {
            if (value > Capacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _length = value;
        }
    }

    internal Memory<byte> Memory =>
        _storage.AsMemory(0, checked((int)_length));
}
