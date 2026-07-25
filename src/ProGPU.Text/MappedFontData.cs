using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace ProGPU.Text;

/// <summary>
/// Provides a stable read-only view of a font file without copying the complete
/// payload into the managed heap. The operating system loads only touched pages,
/// and repeated faces over the same file remain shareable through the file cache.
/// </summary>
internal sealed unsafe class MappedFontData : MemoryManager<byte>
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private readonly int _length;
    private bool _disposed;

    internal nint DataAddress => (nint)_pointer;

    private MappedFontData(
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor view,
        int length)
    {
        _mapping = mapping;
        _view = view;
        _length = length;
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer + checked((nint)_view.PointerOffset);
    }

    public static bool TryOpen(string path, out MappedFontData? data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        data = null;

        try
        {
            var file = new FileInfo(path);
            if (file.Length <= 0 || file.Length > int.MaxValue)
            {
                return false;
            }

            MemoryMappedFile? mapping = null;
            MemoryMappedViewAccessor? view = null;
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.RandomAccess);
                mapping = MemoryMappedFile.CreateFromFile(
                    stream,
                    mapName: null,
                    capacity: file.Length,
                    MemoryMappedFileAccess.Read,
                    HandleInheritability.None,
                    leaveOpen: true);
                view = mapping.CreateViewAccessor(
                    0,
                    file.Length,
                    MemoryMappedFileAccess.Read);
                data = new MappedFontData(mapping, view, checked((int)file.Length));
                mapping = null;
                view = null;
                return true;
            }
            finally
            {
                view?.Dispose();
                mapping?.Dispose();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<byte>(_pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        if (elementIndex > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _view.Dispose();
        _mapping.Dispose();
    }

    ~MappedFontData()
    {
        Dispose(disposing: false);
    }
}
