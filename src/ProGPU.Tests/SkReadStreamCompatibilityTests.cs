using System.Runtime.InteropServices;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkReadStreamCompatibilityTests
{
    [Fact]
    public void MemoryStreamCopiesInputAndResetsPositionWhenMemoryChanges()
    {
        Assert.Equal(typeof(SKStreamMemory), typeof(SKMemoryStream).BaseType);
        Assert.Equal(typeof(SKStreamAsset), typeof(SKStreamMemory).BaseType);

        var source = new byte[] { 1, 2, 3 };
        using var stream = new SKMemoryStream(source);
        source[0] = 9;

        Assert.True(stream.HasPosition);
        Assert.True(stream.HasLength);
        Assert.Equal(3, stream.Length);
        Assert.Equal(0, stream.Position);
        Assert.NotEqual(IntPtr.Zero, stream.GetMemoryBase());
        Assert.Equal(new byte[] { 1, 2, 3 }, Read(stream, 3));

        var replacement = new byte[] { 4, 5 };
        stream.SetMemory(replacement);
        replacement[0] = 9;
        Assert.Equal(0, stream.Position);
        Assert.Equal(2, stream.Length);
        Assert.Equal(new byte[] { 4, 5 }, Read(stream, 4));

        using var empty = new SKMemoryStream();
        Assert.Equal(0, empty.Length);
        using var allocated = new SKMemoryStream(3);
        Assert.Equal(new byte[] { 0, 0, 0 }, Read(allocated, 3));
        using var data = SKData.CreateCopy(new byte[] { 6, 7 });
        using var fromData = new SKMemoryStream(data);
        Assert.Equal(new byte[] { 6, 7 }, Read(fromData, 2));
    }

    [Fact]
    public void FrontBufferedStreamRewindsOnlyWhileInsideBuffer()
    {
        var source = new TrackingForwardOnlyStream(new byte[] { 1, 2, 3, 4, 5, 6 });
        using (var stream = new SKFrontBufferedStream(source, 4))
        {
            Assert.True(stream.CanRead);
            Assert.True(stream.CanSeek);
            Assert.False(stream.CanWrite);
            Assert.Equal(-1, stream.Length);
            Assert.Equal(new byte[] { 1, 2, 3 }, Read(stream, 3));
            Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, Read(stream, 4));
            Assert.Equal(new byte[] { 5, 6 }, Read(stream, 2));
            Assert.Throws<InvalidOperationException>(() => stream.Seek(0, SeekOrigin.Begin));
        }

        Assert.False(source.IsDisposed);

        var owned = new TrackingForwardOnlyStream(Array.Empty<byte>());
        using (new SKFrontBufferedStream(owned, disposeUnderlyingStream: true))
        {
        }

        Assert.True(owned.IsDisposed);
    }

    [Fact]
    public void FrontBufferedManagedStreamMatchesSkiaCallbackAndOwnershipContract()
    {
        var source = new TrackingForwardOnlyStream(new byte[] { 1, 2, 3, 4, 5, 6 });
        using (var stream = new SKFrontBufferedManagedStream(source, 4))
        {
            Assert.False(stream.HasPosition);
            Assert.False(stream.HasLength);
            Assert.Equal(0, stream.Position);
            Assert.Equal(0, stream.Length);
            Assert.False(stream.IsAtEnd);

            var pointer = Marshal.AllocHGlobal(4);
            try
            {
                Assert.Equal(2, stream.Peek(pointer, 2));
                Assert.Equal(1, Marshal.ReadByte(pointer));
                Assert.Equal(2, Marshal.ReadByte(pointer, 1));
                Assert.Equal(new byte[] { 1, 2, 3 }, Read(stream, 3));
                Assert.False(stream.IsAtEnd);
                Assert.True(stream.Rewind());
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, Read(stream, 4));
                Assert.Equal(new byte[] { 5, 6 }, Read(stream, 2));
                Assert.False(stream.IsAtEnd);
                Assert.Empty(Read(stream, 1));
                Assert.True(stream.IsAtEnd);
                Assert.False(stream.Rewind());
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        Assert.False(source.IsDisposed);

        var owned = new TrackingForwardOnlyStream(Array.Empty<byte>());
        using (new SKFrontBufferedManagedStream(owned, 4, disposeUnderlyingStream: true))
        {
        }

        Assert.True(owned.IsDisposed);
    }

    private static byte[] Read(SKStream stream, int count)
    {
        var bytes = new byte[count];
        var read = stream.Read(bytes, count);
        return bytes[..read];
    }

    private static byte[] Read(Stream stream, int count)
    {
        var bytes = new byte[count];
        var read = stream.Read(bytes, 0, count);
        return bytes[..read];
    }

    private sealed class TrackingForwardOnlyStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public TrackingForwardOnlyStream(byte[] data)
        {
            _data = data;
        }

        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var read = Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
