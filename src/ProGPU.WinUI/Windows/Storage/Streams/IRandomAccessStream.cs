using System;
using System.IO;

namespace Windows.Storage.Streams;

/// <summary>
/// Cross-platform, synchronous projection of the WinRT random-access stream used by
/// the text object model. Platform adapters can expose their native stream without
/// copying by implementing this interface.
/// </summary>
public interface IRandomAccessStream : IDisposable
{
    bool CanRead { get; }
    bool CanWrite { get; }
    ulong Position { get; }
    ulong Size { get; set; }
    Stream AsStream();
    void Seek(ulong position);
}
