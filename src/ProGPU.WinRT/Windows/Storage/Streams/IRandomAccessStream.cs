namespace Windows.Storage.Streams;

/// <summary>
/// Cross-platform projection of a readable WinRT stream. The neutral
/// <see cref="AsStream"/> bridge keeps framework code independent of a
/// platform-specific stream implementation.
/// </summary>
public interface IInputStream
{
    Stream AsStream();
}

/// <summary>
/// Cross-platform projection of the WinRT random-access stream ownership
/// contract. Platform adapters expose their native seekable stream without
/// copying through <see cref="AsStream"/>.
/// </summary>
public interface IRandomAccessStream :
    IInputStream,
    IDisposable
{
    bool CanRead { get; }
    bool CanWrite { get; }
    ulong Position { get; }
    ulong Size { get; set; }
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
