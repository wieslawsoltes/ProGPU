using System;
using System.IO;

namespace Windows.Storage.Streams;

public sealed class RandomAccessStream : IRandomAccessStream
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    public RandomAccessStream(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("A random-access stream must be seekable.", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public bool CanRead => _stream.CanRead;
    public bool CanWrite => _stream.CanWrite;
    public ulong Position => checked((ulong)_stream.Position);
    public ulong Size
    {
        get => checked((ulong)_stream.Length);
        set => _stream.SetLength(checked((long)value));
    }

    public Stream AsStream() => _stream;
    public void Seek(ulong position) => _stream.Position = checked((long)position);
    public void Dispose() { if (!_leaveOpen) _stream.Dispose(); }
}
