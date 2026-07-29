namespace Windows.Storage.Streams;

/// <summary>
/// Cross-platform projection of the WinRT random-access stream ownership
/// contract. Platform adapters expose their native seekable stream without
/// copying through <see cref="AsStream"/>.
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

public interface IContentTypeProvider
{
    string ContentType { get; }
}

public interface IRandomAccessStreamWithContentType :
    IRandomAccessStream,
    IContentTypeProvider
{
    IRandomAccessStream CloneStream();
}
