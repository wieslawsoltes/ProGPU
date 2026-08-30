using BenchmarkDotNet.Attributes;
using ProGPU.SystemDrawing;
using ProGPU.Scene;
using System.Buffers.Binary;
using System.Drawing.Imaging;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class MetafileBenchmarks
{
    private const int StateRecordCount = 4_096;
    private byte[] _fixture = null!;
    private Bitmap _target = null!;
    private Graphics _graphics = null!;
    private Metafile _metafile = null!;
    private DrawingContext _playbackContext = null!;
    private Graphics _playbackGraphics = null!;
    private Metafile _playbackMetafile = null!;
    private Metafile _wmfPlaybackMetafile = null!;
    private Metafile _wmfRectanglePlaybackMetafile = null!;
    private Metafile _wmfClippedRectanglePlaybackMetafile = null!;
    private Metafile _wmfEllipsePlaybackMetafile = null!;
    private readonly Graphics.EnumerateMetafileProc _enumerate = static (_, _, _, _, _) => true;
    private readonly byte[] _comment = new byte[64];

    [GlobalSetup]
    public void CreateFixture()
    {
        _fixture = CreateEmf(StateRecordCount);
        _target = new Bitmap(1, 1);
        _graphics = Graphics.FromImage(_target);
        _metafile = new Metafile(new MemoryStream(_fixture, writable: false));
        _playbackContext = new DrawingContext();
        _playbackGraphics = Graphics.FromProGpuDrawingContext(_playbackContext);
        _playbackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmf(256), writable: false));
        _wmfPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmf(256), writable: false));
        _wmfRectanglePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBoxes(256, 0x041B), writable: false));
        _wmfClippedRectanglePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBoxes(256, 0x041B, includeClipState: true), writable: false));
        _wmfEllipsePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBoxes(256, 0x0418), writable: false));
    }

    [GlobalCleanup]
    public void DisposeFixture()
    {
        _metafile.Dispose();
        _playbackMetafile.Dispose();
        _wmfPlaybackMetafile.Dispose();
        _wmfRectanglePlaybackMetafile.Dispose();
        _wmfClippedRectanglePlaybackMetafile.Dispose();
        _wmfEllipsePlaybackMetafile.Dispose();
        _playbackGraphics.Dispose();
        _graphics.Dispose();
        _target.Dispose();
    }

    [Benchmark]
    public int ParseAndEnumerate4096RecordFixture()
    {
        using var stream = new MemoryStream(_fixture, writable: false);
        using var metafile = new Metafile(stream);
        return metafile.Records.Length;
    }

    [Benchmark]
    public void Enumerate4098RecordsWithoutPayloadCopies() =>
        _graphics.EnumerateMetafile(_metafile, Point.Empty, _enumerate);

    [Benchmark]
    public int RecordAndFinalize256PortableComments()
    {
        using var target = new MemoryStream(capacity: 24 * 1024);
        using Metafile metafile = PortableMetafile.Create(target, new Rectangle(0, 0, 640, 480));
        using (Graphics recorder = Graphics.FromImage(metafile))
        {
            for (int index = 0; index < 256; index++)
            {
                recorder.AddMetafileComment(_comment);
            }
        }

        return checked((int)target.Length);
    }

    [Benchmark]
    public int Playback256RectanglesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_playbackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPolygonsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfRectanglesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfRectanglePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfRectanglesWithClipState()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfClippedRectanglePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfEllipsesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfEllipsePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    private static byte[] CreateEmf(int stateRecordCount)
    {
        int totalBytes = checked(88 + stateRecordCount * 8 + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, 1);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, (uint)(stateRecordCount + 2));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        for (int index = 0; index < stateRecordCount; index++)
        {
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSaveDC);
            WriteUInt32(bytes, cursor + 4, 8);
            cursor += 8;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmf(int rectangleCount)
    {
        int totalBytes = checked(88 + 24 + rectangleCount * 24 + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)(rectangleCount + 4)));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteSelectObject(bytes, cursor, 0x8000_0004);
        cursor += 12;
        WriteSelectObject(bytes, cursor, 0x8000_0008);
        cursor += 12;
        for (int index = 0; index < rectangleCount; index++)
        {
            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfRectangle);
            WriteUInt32(bytes, cursor + 4, 24);
            WriteInt32(bytes, cursor + 8, x);
            WriteInt32(bytes, cursor + 12, y);
            WriteInt32(bytes, cursor + 16, x + 32);
            WriteInt32(bytes, cursor + 20, y + 22);
            cursor += 24;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackWmf(int polygonCount)
    {
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + polygonCount * 12 + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 2);
        WriteUInt32(bytes, 34, 12);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;

        WriteUInt32(bytes, cursor, 8);
        WriteUInt16(bytes, cursor + 4, 0x02FA);
        WriteUInt16(bytes, cursor + 6, 5);
        WriteInt16(bytes, cursor + 8, 1);
        cursor += 16;

        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 1);
        cursor += 8;

        for (int index = 0; index < polygonCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, 12);
            WriteUInt16(bytes, cursor + 4, 0x0324);
            WriteInt16(bytes, cursor + 6, 4);
            WriteWmfPoint(bytes, cursor + 8, x, y);
            WriteWmfPoint(bytes, cursor + 12, checked((short)(x + 32)), y);
            WriteWmfPoint(bytes, cursor + 16, checked((short)(x + 32)), checked((short)(y + 22)));
            WriteWmfPoint(bytes, cursor + 20, x, checked((short)(y + 22)));
            cursor += 24;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfBoxes(
        int recordCount,
        ushort function,
        bool includeClipState = false)
    {
        int clipWords = includeClipState ? 14 : 0;
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + clipWords + recordCount * 7 + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 2);
        WriteUInt32(bytes, 34, 8);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;

        WriteUInt32(bytes, cursor, 8);
        WriteUInt16(bytes, cursor + 4, 0x02FA);
        WriteUInt16(bytes, cursor + 6, 0);
        WriteInt16(bytes, cursor + 8, 1);
        cursor += 16;

        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 1);
        cursor += 8;

        if (includeClipState)
        {
            WriteWmfBoxRecord(bytes, cursor, 0x0416, left: 0, top: 0, right: 640, bottom: 480);
            cursor += 14;
            WriteWmfBoxRecord(bytes, cursor, 0x0415, left: 280, top: 180, right: 360, bottom: 300);
            cursor += 14;
        }

        for (int index = 0; index < recordCount; index++)
        {
            short left = checked((short)((index % 16) * 40));
            short top = checked((short)((index / 16) * 30));
            WriteWmfBoxRecord(
                bytes,
                cursor,
                function,
                left,
                top,
                checked((short)(left + 32)),
                checked((short)(top + 22)));
            cursor += 14;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static void WriteWmfBoxRecord(
        byte[] target,
        int offset,
        ushort function,
        short left,
        short top,
        short right,
        short bottom)
    {
        WriteUInt32(target, offset, 7);
        WriteUInt16(target, offset + 4, function);
        WriteInt16(target, offset + 6, bottom);
        WriteInt16(target, offset + 8, right);
        WriteInt16(target, offset + 10, top);
        WriteInt16(target, offset + 12, left);
    }

    private static void WriteWmfObjectIndexRecord(
        byte[] target,
        int offset,
        ushort function,
        ushort index)
    {
        WriteUInt32(target, offset, 4);
        WriteUInt16(target, offset + 4, function);
        WriteUInt16(target, offset + 6, index);
    }

    private static void WriteWmfPoint(byte[] target, int offset, short x, short y)
    {
        WriteInt16(target, offset, x);
        WriteInt16(target, offset + 2, y);
    }

    private static void WriteSelectObject(byte[] target, int offset, uint index)
    {
        WriteUInt32(target, offset, (uint)EmfPlusRecordType.EmfSelectObject);
        WriteUInt32(target, offset + 4, 12);
        WriteUInt32(target, offset + 8, index);
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static void WriteInt16(byte[] target, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteInt32(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);
}
