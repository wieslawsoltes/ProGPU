using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkWriteStreamCompatibilityTests
{
    [Fact]
    public void PrimitiveTextAndPackedWritesMatchSkiaEncoding()
    {
        using var stream = new SKDynamicMemoryWStream();

        Assert.True(stream.Write(new byte[] { 0xaa, 0xbb, 0xcc }, 2));
        Assert.True(stream.NewLine());
        Assert.True(stream.Write8(0x12));
        Assert.True(stream.Write16(0x3456));
        Assert.True(stream.Write32(0x789abcde));
        Assert.True(stream.WriteText("Hi"));
        Assert.True(stream.WriteDecimalAsTest(-42));
        Assert.True(stream.WriteBigDecimalAsText(42, 5));
        Assert.True(stream.WriteHexAsText(0x1a, 4));
        Assert.True(stream.WriteScalarAsText(1.25f));
        Assert.True(stream.WriteBool(true));
        Assert.True(stream.WriteBool(false));
        Assert.True(stream.WriteScalar(1.5f));
        Assert.True(stream.WritePackedUInt32(0xfd));
        Assert.True(stream.WritePackedUInt32(0xfe));
        Assert.True(stream.WritePackedUInt32(0x10000));

        using var data = stream.CopyToData();
        Assert.Equal(
            "AABB0A125634DEBC9A7848692D3432303030343230303141312E323501000000C03FFDFEFE00FF00000100",
            Convert.ToHexString(data.ToArray()));
        Assert.Equal(data.Size, stream.BytesWritten);
        Assert.Equal(1, SKWStream.GetSizeOfPackedUInt32(0xfd));
        Assert.Equal(3, SKWStream.GetSizeOfPackedUInt32(0xfe));
        Assert.Equal(3, SKWStream.GetSizeOfPackedUInt32(ushort.MaxValue));
        Assert.Equal(5, SKWStream.GetSizeOfPackedUInt32(0x10000));
        Assert.Equal(5, SKWStream.GetSizeOfPackedUInt32(uint.MaxValue));
    }

    [Fact]
    public void ScalarFormattingPreservesSkiaSpecialValuesAndUnsignedBigDecimal()
    {
        using var stream = new SKDynamicMemoryWStream();

        Assert.True(stream.WriteBigDecimalAsText(-42, 5));
        Assert.True(stream.WriteText("|"));
        Assert.True(stream.WriteHexAsText(0xabcdef, 2));
        Assert.True(stream.WriteText("|"));
        Assert.True(stream.WriteScalarAsText(-0f));
        Assert.True(stream.WriteText("|"));
        Assert.True(stream.WriteScalarAsText(float.PositiveInfinity));
        Assert.True(stream.WriteText("|"));
        Assert.True(stream.WriteScalarAsText(float.NaN));

        using var data = stream.CopyToData();
        Assert.Equal(
            "18446744073709551574|ABCDEF|-0|inf|nan",
            System.Text.Encoding.UTF8.GetString(data.AsSpan()));
    }

    [Fact]
    public void ManagedAndDynamicStreamsPreserveOwnershipCopyAndDetachContracts()
    {
        using var output = new MemoryStream();
        using (var writer = new SKManagedWStream(output))
        {
            Assert.True(writer.Write8(1));
        }

        Assert.True(output.CanWrite);
        using (var writer = new SKManagedWStream(output, disposeManagedStream: true))
        {
            Assert.True(writer.Write8(2));
        }

        Assert.False(output.CanWrite);

        using var dynamicStream = new SKDynamicMemoryWStream();
        Assert.True(dynamicStream.Write(new byte[] { 3, 4, 5, 6 }, 4));
        var destination = new byte[6];
        dynamicStream.CopyTo(destination);
        Assert.Equal(new byte[] { 3, 4, 5, 6, 0, 0 }, destination);
        using (var copy = dynamicStream.CopyToData())
        {
            Assert.Equal(new byte[] { 3, 4, 5, 6 }, copy.ToArray());
        }

        using (var detached = dynamicStream.DetachAsData())
        {
            Assert.Equal(new byte[] { 3, 4, 5, 6 }, detached.ToArray());
        }

        Assert.Equal(0, dynamicStream.BytesWritten);
        Assert.True(dynamicStream.Write(new byte[] { 7, 8, 9 }, 3));
        using var detachedStream = dynamicStream.DetachAsStream();
        var detachedBytes = new byte[4];
        Assert.Equal(3, detachedStream.Read(detachedBytes, detachedBytes.Length));
        Assert.Equal(new byte[] { 7, 8, 9, 0 }, detachedBytes);
        Assert.Equal(0, dynamicStream.BytesWritten);
    }

    [Fact]
    public void ManagedReadStreamMatchesOwnershipCopyAndSnapshotContracts()
    {
        Assert.Throws<ArgumentNullException>(() => new SKManagedStream(null!));
        Assert.Null(typeof(SKManagedStream).GetProperty(
            "Stream",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.DeclaredOnly));

        using var source = new TrackingMemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        source.Position = 2;
        using (var managed = new SKManagedStream(source))
        using (var output = new TrackingMemoryStream())
        using (var destination = new SKManagedWStream(output))
        {
            Assert.Equal(3, managed.CopyTo(destination));
            Assert.Equal(new byte[] { 3, 4, 5 }, output.ToArray());
            Assert.Equal(1, output.FlushCount);
            Assert.Equal(source.Length, source.Position);
        }

        Assert.True(source.CanRead);
        source.Position = 1;
        using (var managed = new SKManagedStream(source))
        using (var snapshot = managed.ToMemoryStream())
        {
            var bytes = new byte[8];
            Assert.Equal(4, snapshot.Read(bytes, bytes.Length));
            Assert.Equal(new byte[] { 2, 3, 4, 5, 0, 0, 0, 0 }, bytes);
            Assert.Equal(source.Length, source.Position);
        }

        var owned = new MemoryStream(new byte[] { 6, 7 });
        using (var managed = new SKManagedStream(owned, disposeManagedStream: true))
        {
            Assert.True(owned.CanRead);
        }

        Assert.False(owned.CanRead);

        var disposed = new SKManagedStream(new MemoryStream());
        disposed.Dispose();
        using var unusedDestination = new SKDynamicMemoryWStream();
        Assert.Throws<ObjectDisposedException>(() => disposed.CopyTo(unusedDestination));
    }

    [Fact]
    public void ManagedReadStreamUsesDirectReadsAndBoundedSkips()
    {
        using var source = new TrackingMemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        using var managed = new SKManagedStream(source);
        var pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(3);
        try
        {
            Assert.Equal(3, managed.Read(pointer, 3));
            Assert.Equal(1, source.SpanReadCount);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);
        }

        var readCount = source.TotalReadCount;
        Assert.Equal(2, managed.Skip(2));
        Assert.Equal(readCount, source.TotalReadCount);
        Assert.Equal(source.Length, source.Position);
        Assert.False(managed.Seek(-1));
        Assert.False(managed.Move(1));

        using var nonSeekable = new NonSeekableReadStream(new byte[100_000]);
        using var nonSeekableManaged = new SKManagedStream(nonSeekable);
        Assert.False(nonSeekableManaged.IsAtEnd);
        Assert.Equal(100_000, nonSeekableManaged.Skip(100_000));
        Assert.InRange(nonSeekable.MaximumReadSize, 1, 81920);
        Assert.False(nonSeekableManaged.IsAtEnd);
        Assert.Equal(0, nonSeekableManaged.Skip(1));
        Assert.True(nonSeekableManaged.IsAtEnd);
    }

    [Fact]
    public void StreamAndFileAdaptersUseBoundedSafeFailureSemantics()
    {
        using var output = new SKDynamicMemoryWStream();
        using var input = new SKManagedStream(new MemoryStream(new byte[] { 1, 2 }));
        Assert.True(output.WriteStream(input, 4));
        using (var data = output.CopyToData())
        {
            Assert.Equal(new byte[] { 1, 2, 0, 0 }, data.ToArray());
        }

        var path = Path.Combine(Path.GetTempPath(), $"progpu-{Guid.NewGuid():N}.bin");
        try
        {
            using (var file = new SKFileWStream(path))
            {
                Assert.True(file.IsValid);
                Assert.True(file.Write32(0x12345678));
                file.Flush();
                Assert.Equal(4, file.BytesWritten);
            }

            Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, File.ReadAllBytes(path));
            using var invalid = new SKFileWStream(string.Empty);
            Assert.False(invalid.IsValid);
            Assert.False(invalid.Write8(1));
            Assert.Null(SKFileWStream.OpenStream(null!));
            Assert.True(SKFileWStream.IsPathSupported(null!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public int FlushCount { get; private set; }
        public int SpanReadCount { get; private set; }
        public int ArrayReadCount { get; private set; }
        public int TotalReadCount => SpanReadCount + ArrayReadCount;

        public TrackingMemoryStream()
        {
        }

        public TrackingMemoryStream(byte[] buffer)
            : base(buffer)
        {
        }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }

        public override int Read(Span<byte> buffer)
        {
            SpanReadCount++;
            return base.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArrayReadCount++;
            return base.Read(buffer, offset, count);
        }
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public NonSeekableReadStream(byte[] data)
        {
            _data = data;
        }

        public int MaximumReadSize { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            MaximumReadSize = Math.Max(MaximumReadSize, count);
            var available = Math.Min(count, _data.Length - _position);
            _data.AsSpan(_position, available).CopyTo(buffer.AsSpan(offset, available));
            _position += available;
            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
