using Windows.Storage.Streams;

namespace Windows.Storage;

/// <summary>
/// Portable projection of the WinRT storage-file identity and readable-stream
/// contracts used by ProGPU.
/// </summary>
public interface IStorageFile : IRandomAccessStreamReference
{
    string ContentType { get; }
    string FileType { get; }
    string Name { get; }
    string Path { get; }
}
