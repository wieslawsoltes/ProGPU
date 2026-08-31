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
    private Metafile _wmfRoundRectanglePlaybackMetafile = null!;
    private Metafile _wmfPiePlaybackMetafile = null!;
    private Metafile _wmfChordPlaybackMetafile = null!;
    private Metafile _wmfLinePlaybackMetafile = null!;
    private Metafile _wmfPixelPlaybackMetafile = null!;
    private Metafile _wmfPolyPolygonPlaybackMetafile = null!;
    private Metafile _wmfMappedPixelPlaybackMetafile = null!;
    private Metafile _wmfPatBltPlaybackMetafile = null!;
    private Metafile _wmfOffsetClipPatBltPlaybackMetafile = null!;
    private Metafile _wmfTextPlaybackMetafile = null!;
    private Metafile _wmfExtendedTextPlaybackMetafile = null!;
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
        _wmfRoundRectanglePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBoxes(256, 0x061C), writable: false));
        _wmfPiePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfFilledArcs(256, 0x081A), writable: false));
        _wmfChordPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfFilledArcs(256, 0x0830), writable: false));
        _wmfLinePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfLineOrPixels(256, setPixels: false), writable: false));
        _wmfPixelPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfLineOrPixels(256, setPixels: true), writable: false));
        _wmfPolyPolygonPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfPolyPolygons(256), writable: false));
        _wmfMappedPixelPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfMappedPixels(256), writable: false));
        _wmfPatBltPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfPatBlts(256), writable: false));
        _wmfOffsetClipPatBltPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfPatBlts(256, includeOffsetClipState: true), writable: false));
        _wmfTextPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfText(256), writable: false));
        _wmfExtendedTextPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfExtendedText(256), writable: false));
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
        _wmfRoundRectanglePlaybackMetafile.Dispose();
        _wmfPiePlaybackMetafile.Dispose();
        _wmfChordPlaybackMetafile.Dispose();
        _wmfLinePlaybackMetafile.Dispose();
        _wmfPixelPlaybackMetafile.Dispose();
        _wmfPolyPolygonPlaybackMetafile.Dispose();
        _wmfMappedPixelPlaybackMetafile.Dispose();
        _wmfPatBltPlaybackMetafile.Dispose();
        _wmfOffsetClipPatBltPlaybackMetafile.Dispose();
        _wmfTextPlaybackMetafile.Dispose();
        _wmfExtendedTextPlaybackMetafile.Dispose();
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

    [Benchmark]
    public int Playback256WmfRoundRectanglesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfRoundRectanglePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPiesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPiePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfChordsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfChordPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfLinesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfLinePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfSetPixelsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPixelPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPolyPolygonsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPolyPolygonPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfMappedPixelsWithViewportState()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfMappedPixelPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPatternCopiesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPatBltPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPatternCopiesWithOffsetClipState()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfOffsetClipPatBltPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfTextOutToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfTextPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfExtTextOutWithClipAndAdvances()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfExtendedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
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

    private static byte[] CreatePlaybackWmfPolyPolygons(int recordCount)
    {
        const int recordWords = 22;
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + recordCount * recordWords + 3);
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
        WriteUInt32(bytes, 34, recordWords);

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

        for (int index = 0; index < recordCount; index++)
        {
            short left = checked((short)((index % 16) * 40));
            short top = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x0538);
            WriteUInt16(bytes, cursor + 6, 2);
            WriteUInt16(bytes, cursor + 8, 4);
            WriteUInt16(bytes, cursor + 10, 4);
            WriteWmfPoint(bytes, cursor + 12, left, top);
            WriteWmfPoint(bytes, cursor + 16, checked((short)(left + 14)), top);
            WriteWmfPoint(bytes, cursor + 20, checked((short)(left + 14)), checked((short)(top + 22)));
            WriteWmfPoint(bytes, cursor + 24, left, checked((short)(top + 22)));
            WriteWmfPoint(bytes, cursor + 28, checked((short)(left + 18)), top);
            WriteWmfPoint(bytes, cursor + 32, checked((short)(left + 32)), top);
            WriteWmfPoint(bytes, cursor + 36, checked((short)(left + 32)), checked((short)(top + 22)));
            WriteWmfPoint(bytes, cursor + 40, checked((short)(left + 18)), checked((short)(top + 22)));
            cursor += recordWords * 2;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfBoxes(
        int recordCount,
        ushort function,
        bool includeClipState = false)
    {
        bool roundRectangle = function == 0x061C;
        int shapeWords = roundRectangle ? 9 : 7;
        int clipWords = includeClipState ? 21 : 0;
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + clipWords + recordCount * shapeWords + 3);
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
        WriteUInt32(bytes, 34, roundRectangle ? 9u : 8u);

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
            WriteUInt32(bytes, cursor, 3);
            WriteUInt16(bytes, cursor + 4, 0x001E);
            cursor += 6;
            WriteWmfBoxRecord(bytes, cursor, 0x0415, left: 280, top: 180, right: 360, bottom: 300);
            cursor += 14;
        }

        for (int index = 0; index < recordCount; index++)
        {
            if (includeClipState && index == recordCount / 2)
            {
                WriteUInt32(bytes, cursor, 4);
                WriteUInt16(bytes, cursor + 4, 0x0127);
                WriteInt16(bytes, cursor + 6, -1);
                cursor += 8;
            }

            short left = checked((short)((index % 16) * 40));
            short top = checked((short)((index / 16) * 30));
            if (roundRectangle)
            {
                WriteWmfRoundRectangleRecord(
                    bytes,
                    cursor,
                    left,
                    top,
                    checked((short)(left + 32)),
                    checked((short)(top + 22)),
                    width: 8,
                    height: 8);
                cursor += 18;
            }
            else
            {
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
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfFilledArcs(int recordCount, ushort function)
    {
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + recordCount * 11 + 3);
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
        WriteUInt32(bytes, 34, 11);

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

        for (int index = 0; index < recordCount; index++)
        {
            short left = checked((short)((index % 16) * 40));
            short top = checked((short)((index / 16) * 30));
            short right = checked((short)(left + 32));
            short bottom = checked((short)(top + 22));
            WriteWmfFilledArcRecord(bytes, cursor, function, left, top, right, bottom);
            cursor += 22;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfLineOrPixels(int recordCount, bool setPixels)
    {
        int setupWords = setPixels ? 0 : 8 + 4 + 5;
        int recordWords = setPixels ? 7 : 5;
        int declaredWords = checked(9 + setupWords + recordCount * recordWords + 3);
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
        WriteUInt16(bytes, 32, setPixels ? (ushort)0 : (ushort)1);
        WriteUInt32(bytes, 34, setPixels ? 7u : 8u);

        int cursor = 40;
        if (!setPixels)
        {
            WriteUInt32(bytes, cursor, 8);
            WriteUInt16(bytes, cursor + 4, 0x02FA);
            WriteUInt16(bytes, cursor + 6, 0);
            WriteInt16(bytes, cursor + 8, 1);
            cursor += 16;

            WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
            cursor += 8;
            WriteUInt32(bytes, cursor, 5);
            WriteUInt16(bytes, cursor + 4, 0x0214);
            WriteInt16(bytes, cursor + 6, 0);
            WriteInt16(bytes, cursor + 8, 0);
            cursor += 10;
        }

        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index * 37) % 640));
            short y = checked((short)((index * 29) % 480));
            if (setPixels)
            {
                WriteUInt32(bytes, cursor, 7);
                WriteUInt16(bytes, cursor + 4, 0x041F);
                WriteUInt32(bytes, cursor + 6, 0x00FF_00FF);
                WriteInt16(bytes, cursor + 10, y);
                WriteInt16(bytes, cursor + 12, x);
                cursor += 14;
            }
            else
            {
                WriteUInt32(bytes, cursor, 5);
                WriteUInt16(bytes, cursor + 4, 0x0213);
                WriteInt16(bytes, cursor + 6, y);
                WriteInt16(bytes, cursor + 8, x);
                cursor += 10;
            }
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfMappedPixels(int recordCount)
    {
        const int setupWords = 24;
        const int cycleWords = 55;
        int declaredWords = checked(9 + setupWords + recordCount * cycleWords + 3);
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
        WriteUInt16(bytes, 32, 0);
        WriteUInt32(bytes, 34, 7);

        int cursor = 40;
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x0103, 8);
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x020C, 480, 640);
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x020B, 0, 0);
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x020E, 480, 640);
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x020D, 0, 0);

        for (int index = 0; index < recordCount; index++)
        {
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x020F, 1, 1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x020F, -1, -1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0211, 1, 1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0211, -1, -1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0410, 1, 2, 1, 2);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0410, 2, 1, 2, 1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0412, 1, 2, 1, 2);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0412, 2, 1, 2, 1);

            short x = checked((short)((index * 37) % 640));
            short y = checked((short)((index * 29) % 480));
            WriteUInt32(bytes, cursor, 7);
            WriteUInt16(bytes, cursor + 4, 0x041F);
            WriteUInt32(bytes, cursor + 6, 0x00FF_FF00);
            WriteInt16(bytes, cursor + 10, y);
            WriteInt16(bytes, cursor + 12, x);
            cursor += 14;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfPatBlts(
        int recordCount,
        bool includeOffsetClipState = false)
    {
        const int recordWords = 9;
        int clipSetupWords = includeOffsetClipState ? 7 : 0;
        int perRecordWords = includeOffsetClipState ? 19 : recordWords;
        int declaredWords = checked(9 + 7 + 4 + clipSetupWords + recordCount * perRecordWords + 3);
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
        WriteUInt16(bytes, 32, 1);
        WriteUInt32(bytes, 34, recordWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        if (includeOffsetClipState)
        {
            WriteWmfBoxRecord(bytes, cursor, 0x0416, left: 0, top: 0, right: 640, bottom: 480);
            cursor += 14;
        }

        for (int index = 0; index < recordCount; index++)
        {
            if (includeOffsetClipState)
            {
                cursor += WriteWmfWordsRecord(bytes, cursor, 0x0220, 1, 1);
            }
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x061D);
            WriteUInt32(bytes, cursor + 6, 0x00F0_0021);
            WriteInt16(bytes, cursor + 10, 22);
            WriteInt16(bytes, cursor + 12, 32);
            WriteInt16(bytes, cursor + 14, y);
            WriteInt16(bytes, cursor + 16, x);
            cursor += recordWords * 2;
            if (includeOffsetClipState)
            {
                cursor += WriteWmfWordsRecord(bytes, cursor, 0x0220, -1, -1);
            }
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfText(int recordCount)
    {
        const int fontWords = 28;
        const int textWords = 8;
        int declaredWords = checked(9 + fontWords + 4 + 4 + 5 + recordCount * textWords + 3);
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
        WriteUInt16(bytes, 32, 1);
        WriteUInt32(bytes, 34, fontWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, fontWords);
        WriteUInt16(bytes, cursor + 4, 0x02FB);
        WriteInt16(bytes, cursor + 6, -14);
        WriteInt16(bytes, cursor + 14, 400);
        bytes[cursor + 19] = 1;
        cursor += fontWords * 2;

        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x0102, 1);

        WriteUInt32(bytes, cursor, 5);
        WriteUInt16(bytes, cursor + 4, 0x0209);
        WriteUInt32(bytes, cursor + 6, 0x0044_4444);
        cursor += 10;

        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, textWords);
            WriteUInt16(bytes, cursor + 4, 0x0521);
            WriteInt16(bytes, cursor + 6, 3);
            bytes[cursor + 8] = (byte)'W';
            bytes[cursor + 9] = (byte)'M';
            bytes[cursor + 10] = (byte)'F';
            WriteInt16(bytes, cursor + 12, y);
            WriteInt16(bytes, cursor + 14, x);
            cursor += textWords * 2;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfExtendedText(int recordCount)
    {
        const int fontWords = 28;
        const int recordWords = 16;
        int declaredWords = checked(9 + fontWords + 4 + 4 + 5 + 5 + recordCount * recordWords + 3);
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
        WriteUInt16(bytes, 32, 1);
        WriteUInt32(bytes, 34, fontWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, fontWords);
        WriteUInt16(bytes, cursor + 4, 0x02FB);
        WriteInt16(bytes, cursor + 6, -14);
        WriteInt16(bytes, cursor + 14, 400);
        bytes[cursor + 19] = 1;
        cursor += fontWords * 2;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x0102, 1);

        WriteUInt32(bytes, cursor, 5);
        WriteUInt16(bytes, cursor + 4, 0x0201);
        WriteUInt32(bytes, cursor + 6, 0x00FF_FFFF);
        cursor += 10;
        WriteUInt32(bytes, cursor, 5);
        WriteUInt16(bytes, cursor + 4, 0x0209);
        WriteUInt32(bytes, cursor + 6, 0x0044_4444);
        cursor += 10;

        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x0A32);
            WriteInt16(bytes, cursor + 6, y);
            WriteInt16(bytes, cursor + 8, x);
            WriteInt16(bytes, cursor + 10, 3);
            WriteUInt16(bytes, cursor + 12, 0x0006);
            WriteInt16(bytes, cursor + 14, x);
            WriteInt16(bytes, cursor + 16, y);
            WriteInt16(bytes, cursor + 18, checked((short)(x + 32)));
            WriteInt16(bytes, cursor + 20, checked((short)(y + 22)));
            bytes[cursor + 22] = (byte)'W';
            bytes[cursor + 23] = (byte)'M';
            bytes[cursor + 24] = (byte)'F';
            WriteInt16(bytes, cursor + 26, 10);
            WriteInt16(bytes, cursor + 28, 10);
            WriteInt16(bytes, cursor + 30, 10);
            cursor += recordWords * 2;
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

    private static void WriteWmfRoundRectangleRecord(
        byte[] target,
        int offset,
        short left,
        short top,
        short right,
        short bottom,
        short width,
        short height)
    {
        WriteUInt32(target, offset, 9);
        WriteUInt16(target, offset + 4, 0x061C);
        WriteInt16(target, offset + 6, height);
        WriteInt16(target, offset + 8, width);
        WriteInt16(target, offset + 10, bottom);
        WriteInt16(target, offset + 12, right);
        WriteInt16(target, offset + 14, top);
        WriteInt16(target, offset + 16, left);
    }

    private static void WriteWmfFilledArcRecord(
        byte[] target,
        int offset,
        ushort function,
        short left,
        short top,
        short right,
        short bottom)
    {
        WriteUInt32(target, offset, 11);
        WriteUInt16(target, offset + 4, function);
        WriteInt16(target, offset + 6, top);
        WriteInt16(target, offset + 8, checked((short)(left + (right - left) / 2)));
        WriteInt16(target, offset + 10, checked((short)(top + (bottom - top) / 2)));
        WriteInt16(target, offset + 12, right);
        WriteInt16(target, offset + 14, bottom);
        WriteInt16(target, offset + 16, right);
        WriteInt16(target, offset + 18, top);
        WriteInt16(target, offset + 20, left);
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

    private static int WriteWmfWordsRecord(
        byte[] target,
        int offset,
        ushort function,
        params short[] values)
    {
        int byteCount = checked(6 + values.Length * 2);
        WriteUInt32(target, offset, checked((uint)(byteCount / 2)));
        WriteUInt16(target, offset + 4, function);
        for (int index = 0; index < values.Length; index++)
        {
            WriteInt16(target, offset + 6 + index * 2, values[index]);
        }
        return byteCount;
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
