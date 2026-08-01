using System.Runtime.InteropServices;
using System.Text;

namespace SkiaSharp;

public delegate void SKDataReleaseDelegate(IntPtr address, object context);

public class SKData : SKObject
{
    private sealed class Storage
    {
        private readonly byte[]? _managedBytes;
        private readonly SKDataReleaseDelegate? _release;
        private readonly object? _context;
        private GCHandle _pin;
        private int _referenceCount = 1;

        public Storage(byte[] bytes)
        {
            _managedBytes = bytes;
            if (bytes.Length > 0)
            {
                _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                Address = _pin.AddrOfPinnedObject();
            }
        }

        public Storage(
            IntPtr address,
            SKDataReleaseDelegate? release,
            object? context)
        {
            Address = address;
            _release = release;
            _context = context;
        }

        public IntPtr Address { get; }

        public void AddReference() => Interlocked.Increment(ref _referenceCount);

        public void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _referenceCount) != 0)
            {
                return;
            }

            if (_pin.IsAllocated)
            {
                _pin.Free();
            }

            _release?.Invoke(Address, _context!);
        }

        public byte[] GetManagedBytes(int offset, int length)
        {
            if (_managedBytes is not null && offset == 0 && length == _managedBytes.Length)
            {
                return _managedBytes;
            }

            return GetReadOnlySpan(offset, length).ToArray();
        }

        public unsafe Span<byte> GetSpan(int offset, int length) =>
            length == 0
                ? Span<byte>.Empty
                : new Span<byte>((byte*)Address + offset, length);

        public unsafe ReadOnlySpan<byte> GetReadOnlySpan(int offset, int length) =>
            length == 0
                ? ReadOnlySpan<byte>.Empty
                : new ReadOnlySpan<byte>((byte*)Address + offset, length);
    }

    private sealed class DataStream : UnmanagedMemoryStream
    {
        private SKData? _host;
        private readonly bool _disposeHost;

        public unsafe DataStream(SKData host, bool disposeHost)
            : base((byte*)host.Data, host.Size, host.Size, FileAccess.ReadWrite)
        {
            _host = host;
            _disposeHost = disposeHost;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _disposeHost)
            {
                _host?.Dispose();
            }

            _host = null;
        }
    }

    private static readonly SKData s_empty = new(Array.Empty<byte>(), disposeProtected: true);
    private readonly Storage _storage;
    private readonly int _offset;
    private readonly int _length;
    private readonly bool _disposeProtected;
    private int _disposed;

    static SKData()
    {
        s_empty.PreventPublicDisposal();
    }

    internal SKData(byte[] bytes)
        : this(bytes, disposeProtected: false)
    {
    }

    private SKData(byte[] bytes, bool disposeProtected)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _storage = new Storage(bytes);
        _length = bytes.Length;
        _disposeProtected = disposeProtected;
    }

    private SKData(Storage storage, int offset, int length)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _storage = storage;
        _offset = offset;
        _length = length;
        storage.AddReference();
    }

    private SKData(
        IntPtr address,
        int length,
        SKDataReleaseDelegate? release,
        object? context)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _storage = new Storage(address, release, context);
        _length = length;
    }

    public static SKData Empty => s_empty;
    public bool IsEmpty => Size == 0;
    public long Size
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public IntPtr Data
    {
        get
        {
            ThrowIfDisposed();
            return _length == 0 ? IntPtr.Zero : IntPtr.Add(_storage.Address, _offset);
        }
    }

    public Span<byte> Span
    {
        get
        {
            ThrowIfDisposed();
            return _storage.GetSpan(_offset, _length);
        }
    }

    internal byte[] Bytes
    {
        get
        {
            ThrowIfDisposed();
            return _storage.GetManagedBytes(_offset, _length);
        }
    }

    public static SKData CreateCopy(IntPtr bytes, int length) =>
        CreateCopy(bytes, (long)length);

    public static SKData CreateCopy(IntPtr bytes, long length) =>
        CreateCopyCore(bytes, GetManagedLength(length, nameof(length)));

    public static SKData CreateCopy(IntPtr bytes, ulong length) =>
        CreateCopyCore(bytes, GetManagedLength(length, nameof(length)));

    private static SKData CreateCopyCore(IntPtr bytes, int count)
    {
        if (count == 0)
        {
            return new SKData(Array.Empty<byte>());
        }

        if (bytes == IntPtr.Zero)
        {
            throw new ArgumentException("A non-empty data source requires a valid address.", nameof(bytes));
        }

        var copy = GC.AllocateUninitializedArray<byte>(count);
        Marshal.Copy(bytes, copy, 0, count);
        return new SKData(copy);
    }

    public static SKData CreateCopy(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return CreateCopy(bytes.AsSpan());
    }

    public static SKData CreateCopy(byte[] bytes, ulong length)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var count = GetManagedLength(length, nameof(length));
        if (count > bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return CreateCopy(bytes.AsSpan(0, count));
    }

    public static SKData CreateCopy(ReadOnlySpan<byte> bytes) =>
        new(bytes.ToArray());

    public static SKData Create(int size) =>
        Create((long)size);

    public static SKData Create(long size)
    {
        var count = GetManagedLength(size, nameof(size));
        return new SKData(GC.AllocateUninitializedArray<byte>(count));
    }

    public static SKData Create(ulong size)
    {
        var count = GetManagedLength(size, nameof(size));
        return new SKData(GC.AllocateUninitializedArray<byte>(count));
    }

    public static SKData Create(string filename)
    {
        if (string.IsNullOrEmpty(filename))
        {
            throw new ArgumentException("The filename cannot be empty.", nameof(filename));
        }

        try
        {
            return new SKData(File.ReadAllBytes(filename));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null!;
        }
    }

    public static SKData Create(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            return Create(stream, checked(stream.Length - stream.Position));
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new SKData(copy.ToArray());
    }

    public static SKData Create(Stream stream, int length) =>
        Create(stream, (long)length);

    public static SKData Create(Stream stream, ulong length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CreateFromStream(stream, GetManagedLength(length, nameof(length)));
    }

    public static SKData Create(Stream stream, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CreateFromStream(stream, GetManagedLength(length, nameof(length)));
    }

    public static SKData Create(SKStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Create(stream, stream.Length);
    }

    public static SKData Create(SKStream stream, int length) =>
        Create(stream, (long)length);

    public static SKData Create(SKStream stream, ulong length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CreateFromStream(stream, GetManagedLength(length, nameof(length)));
    }

    public static SKData Create(SKStream stream, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CreateFromStream(stream, GetManagedLength(length, nameof(length)));
    }

    public static SKData Create(IntPtr address, int length) =>
        Create(address, length, null!, null!);

    public static SKData Create(
        IntPtr address,
        int length,
        SKDataReleaseDelegate releaseProc) =>
        Create(address, length, releaseProc, null!);

    public static SKData Create(
        IntPtr address,
        int length,
        SKDataReleaseDelegate releaseProc,
        object context)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length > 0 && address == IntPtr.Zero)
        {
            throw new ArgumentException("A non-empty data source requires a valid address.", nameof(address));
        }

        return new SKData(address, length, releaseProc, context);
    }

    internal static SKData FromCString(string? value)
    {
        var textBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
        var bytes = GC.AllocateUninitializedArray<byte>(checked(textBytes.Length + 1));
        textBytes.CopyTo(bytes, 0);
        bytes[^1] = 0;
        return new SKData(bytes);
    }

    public SKData Subset(ulong offset, ulong length)
    {
        ThrowIfDisposed();
        if (offset > (ulong)_length || length > (ulong)_length - offset)
        {
            return new SKData(Array.Empty<byte>());
        }

        return length == 0
            ? new SKData(Array.Empty<byte>())
            : new SKData(
                _storage,
                checked(_offset + (int)offset),
                checked((int)length));
    }

    public byte[] ToArray() => AsSpan().ToArray();

    public Stream AsStream() => AsStream(streamDisposesData: false);

    public Stream AsStream(bool streamDisposesData)
    {
        ThrowIfDisposed();
        return new DataStream(this, streamDisposesData);
    }

    public ReadOnlySpan<byte> AsSpan()
    {
        ThrowIfDisposed();
        return _storage.GetReadOnlySpan(_offset, _length);
    }

    public void SaveTo(Stream target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Write(AsSpan());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                ReleaseStorage();
            }
            finally
            {
                base.Dispose(disposing);
            }
            return;
        }

        try
        {
            ReleaseStorage();
        }
        catch
        {
            // Release callbacks must not terminate the process from the finalizer thread.
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private void ReleaseStorage()
    {
        if (_disposeProtected || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _storage.ReleaseReference();
    }

    private static SKData CreateFromStream(Stream stream, int length)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var read = 0;
        while (read < length)
        {
            var count = stream.Read(bytes, read, length - read);
            if (count == 0)
            {
                return null!;
            }

            read += count;
        }

        return new SKData(bytes);
    }

    private static unsafe SKData CreateFromStream(SKStream stream, int length)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var read = 0;
        fixed (byte* data = bytes)
        {
            while (read < length)
            {
                var count = stream.Read((IntPtr)(data + read), length - read);
                if (count == 0)
                {
                    return null!;
                }

                read += count;
            }
        }

        return new SKData(bytes);
    }

    private static int GetManagedLength(long length, string parameterName)
    {
        if (length < 0 || length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return (int)length;
    }

    private static int GetManagedLength(ulong length, string parameterName)
    {
        if (length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return (int)length;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }
}
