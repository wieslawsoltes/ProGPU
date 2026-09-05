using System.Buffers.Binary;
using ProGPU.Scene;

namespace System.Drawing.Imaging;

internal sealed class PortableMetafileRecordingSession
{
    private const int EmfHeaderSize = 88;
    private const int EmfEofSize = 20;
    private const int EmfPlusHeaderSize = 28;
    private const int EmfPlusEofSize = 12;
    private const int EmfCommentPrefixSize = 12;
    private const int MaxRecordBytes = 16 * 1024 * 1024;
    private const uint EmfSignature = 0x464D4520;
    private const uint EmfPlusSignature = 0x2B464D45;
    private const uint EmfVersion = 0x00010000;
    private const uint EmfPlusVersion = 0xDBC01002;
    private const int LogicalDpi = 96;

    private readonly object _gate = new();
    private readonly Stream _target;
    private readonly Rectangle _bounds;
    private readonly MemoryStream _commentRecords = new();
    private RecordingState _state;

    internal PortableMetafileRecordingSession(Stream target, Rectangle bounds)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.CanWrite)
        {
            throw new ArgumentException("The portable metafile target must be writable.", nameof(target));
        }

        if (bounds.Width < 0 || bounds.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Metafile bounds cannot have negative dimensions.");
        }

        try
        {
            int right = checked(bounds.X + bounds.Width);
            int bottom = checked(bounds.Y + bounds.Height);
            _ = PixelsToFrameUnits(bounds.X);
            _ = PixelsToFrameUnits(bounds.Y);
            _ = PixelsToFrameUnits(right);
            _ = PixelsToFrameUnits(bottom);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                "Metafile bounds must fit in signed 32-bit coordinates.");
        }

        _target = target;
        _bounds = bounds;
    }

    internal Rectangle Bounds => _bounds;

    internal void Acquire()
    {
        lock (_gate)
        {
            if (_state != RecordingState.Created)
            {
                throw new InvalidOperationException(
                    "A portable metafile supports exactly one active Graphics recording session.");
            }

            _state = RecordingState.Active;
        }
    }

    internal void AddComment(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            EnsureActive();
            int paddedLength = Align4(data.Length);
            long projectedInnerSize = checked(
                4L + EmfPlusHeaderSize + _commentRecords.Length + 12L + paddedLength + EmfPlusEofSize);
            long projectedEnvelopeSize = checked(EmfCommentPrefixSize + projectedInnerSize);
            if (projectedEnvelopeSize > MaxRecordBytes)
            {
                throw new ArgumentException(
                    "The comment would exceed the bounded EMF+ transport record.",
                    nameof(data));
            }

            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)EmfPlusRecordType.Comment);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(12 + paddedLength)));
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], checked((uint)data.Length));
            _commentRecords.Write(header);
            _commentRecords.Write(data);
            if (paddedLength != data.Length)
            {
                Span<byte> padding = stackalloc byte[3];
                _commentRecords.Write(padding[..(paddedLength - data.Length)]);
            }
        }
    }

    internal MetafileDocument Complete(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        lock (_gate)
        {
            EnsureActive();
            if (drawingContext.Commands.Count != 0)
            {
                _state = RecordingState.Aborted;
                throw new NotSupportedException(
                    "Portable metafile recording currently supports comment records only; drawing commands were not written.");
            }

            try
            {
                byte[] source = Encode();
                MetafileDocument document = MetafileParser.ParseOwnedSource(source);
                _target.Write(source);
                _target.Flush();
                _state = RecordingState.Completed;
                return document;
            }
            catch
            {
                _state = RecordingState.Aborted;
                throw;
            }
        }
    }

    internal void Abort()
    {
        lock (_gate)
        {
            if (_state is RecordingState.Created or RecordingState.Active)
            {
                _state = RecordingState.Aborted;
            }
        }
    }

    private byte[] Encode()
    {
        int innerSize = checked(4 + EmfPlusHeaderSize + (int)_commentRecords.Length + EmfPlusEofSize);
        int envelopeSize = checked(EmfCommentPrefixSize + innerSize);
        int totalSize = checked(EmfHeaderSize + envelopeSize + EmfEofSize);
        byte[] source = GC.AllocateUninitializedArray<byte>(totalSize);
        Span<byte> bytes = source;
        bytes.Clear();

        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, EmfHeaderSize);
        WriteInt32(bytes, 8, _bounds.Left);
        WriteInt32(bytes, 12, _bounds.Top);
        WriteInt32(bytes, 16, _bounds.Right);
        WriteInt32(bytes, 20, _bounds.Bottom);
        WriteInt32(bytes, 24, PixelsToFrameUnits(_bounds.Left));
        WriteInt32(bytes, 28, PixelsToFrameUnits(_bounds.Top));
        WriteInt32(bytes, 32, PixelsToFrameUnits(_bounds.Right));
        WriteInt32(bytes, 36, PixelsToFrameUnits(_bounds.Bottom));
        WriteUInt32(bytes, 40, EmfSignature);
        WriteUInt32(bytes, 44, EmfVersion);
        WriteUInt32(bytes, 48, checked((uint)totalSize));
        WriteUInt32(bytes, 52, 3);
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 960);
        WriteInt32(bytes, 76, 960);
        WriteInt32(bytes, 80, 254);
        WriteInt32(bytes, 84, 254);

        int envelopeOffset = EmfHeaderSize;
        WriteUInt32(bytes, envelopeOffset, (uint)EmfPlusRecordType.EmfGdiComment);
        WriteUInt32(bytes, envelopeOffset + 4, checked((uint)envelopeSize));
        WriteUInt32(bytes, envelopeOffset + 8, checked((uint)innerSize));
        WriteUInt32(bytes, envelopeOffset + 12, EmfPlusSignature);

        int plusHeaderOffset = envelopeOffset + 16;
        WriteUInt16(bytes, plusHeaderOffset, (ushort)EmfPlusRecordType.Header);
        WriteUInt32(bytes, plusHeaderOffset + 4, EmfPlusHeaderSize);
        WriteUInt32(bytes, plusHeaderOffset + 8, 16);
        WriteUInt32(bytes, plusHeaderOffset + 12, EmfPlusVersion);
        WriteUInt32(bytes, plusHeaderOffset + 16, 1);
        WriteInt32(bytes, plusHeaderOffset + 20, LogicalDpi);
        WriteInt32(bytes, plusHeaderOffset + 24, LogicalDpi);

        int commentOffset = plusHeaderOffset + EmfPlusHeaderSize;
        _commentRecords.GetBuffer().AsSpan(0, checked((int)_commentRecords.Length)).CopyTo(bytes[commentOffset..]);

        int plusEofOffset = checked(commentOffset + (int)_commentRecords.Length);
        WriteUInt16(bytes, plusEofOffset, (ushort)EmfPlusRecordType.EndOfFile);
        WriteUInt32(bytes, plusEofOffset + 4, EmfPlusEofSize);

        int emfEofOffset = checked(envelopeOffset + envelopeSize);
        WriteUInt32(bytes, emfEofOffset, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, emfEofOffset + 4, EmfEofSize);
        WriteUInt32(bytes, emfEofOffset + 16, EmfEofSize);
        return source;
    }

    private void EnsureActive()
    {
        if (_state != RecordingState.Active)
        {
            throw new InvalidOperationException("The portable metafile recording session is not active.");
        }
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static int PixelsToFrameUnits(int pixels) =>
        checked((int)Math.Round(pixels * (2540d / LogicalDpi), MidpointRounding.AwayFromZero));

    private static void WriteUInt16(Span<byte> target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(offset, 2), value);

    private static void WriteUInt32(Span<byte> target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset, 4), value);

    private static void WriteInt32(Span<byte> target, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(offset, 4), value);

    private enum RecordingState
    {
        Created,
        Active,
        Completed,
        Aborted
    }
}
