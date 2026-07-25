using System.Buffers;
using System.Reflection;

namespace ProGPU.Text;

/// <summary>
/// Keeps an embedded raw font backed by the assembly image. The runtime already maps
/// that image, so parsing the resource directly avoids a second managed payload copy.
/// </summary>
internal sealed unsafe class EmbeddedFontData : MemoryManager<byte>
{
    private UnmanagedMemoryStream? _stream;
    private byte* _pointer;
    private readonly int _length;

    internal nint DataAddress => (nint)_pointer;

    private EmbeddedFontData(UnmanagedMemoryStream stream)
    {
        _stream = stream;
        _length = checked((int)stream.Length);
        stream.Position = 0;
        _pointer = stream.PositionPointer;
    }

    public static bool TryOpen(
        Assembly assembly,
        string resourceName,
        out EmbeddedFontData? data)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        data = null;

        Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not UnmanagedMemoryStream unmanaged ||
            unmanaged.Length <= 0 ||
            unmanaged.Length > int.MaxValue)
        {
            stream?.Dispose();
            return false;
        }

        try
        {
            data = new EmbeddedFontData(unmanaged);
            return true;
        }
        catch
        {
            unmanaged.Dispose();
            throw;
        }
    }

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        return new Span<byte>(_pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
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
        UnmanagedMemoryStream? stream = Interlocked.Exchange(ref _stream, null);
        _pointer = null;
        stream?.Dispose();
    }

    ~EmbeddedFontData()
    {
        Dispose(disposing: false);
    }
}
