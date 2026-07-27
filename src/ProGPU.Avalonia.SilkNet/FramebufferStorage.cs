using System;
using System.Runtime.InteropServices;

namespace Avalonia.SilkNet;

/// <summary>
/// Owns the stable unmanaged staging address exposed by the Avalonia
/// framebuffer contract.
/// </summary>
/// <remarks>
/// Capacity growth is O(1) amortized and bounded to the requested size when
/// doubling would retain more memory than the caller needs. No allocation is
/// performed while an existing block is large enough.
/// </remarks>
public sealed unsafe class SilkNetFramebufferAddressProvider : IDisposable
{
    private byte* _address;
    private int _capacity;
    private bool _disposed;

    public int Capacity => _capacity;

    public IntPtr GetAddress(int requestedBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedBytes);

        if (requestedBytes <= _capacity)
            return (IntPtr)_address;

        int nextCapacity = _capacity == 0
            ? requestedBytes
            : _capacity <= int.MaxValue / 2
                ? Math.Max(requestedBytes, _capacity * 2)
                : requestedBytes;

        byte* replacement = (byte*)NativeMemory.Realloc(
            _address,
            checked((nuint)nextCapacity));
        if (replacement is null)
            throw new OutOfMemoryException();

        _address = replacement;
        _capacity = nextCapacity;
        return (IntPtr)_address;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        NativeMemory.Free(_address);
        _address = null;
        _capacity = 0;
    }
}
