using BenchmarkDotNet.Attributes;
using ProGPU.SystemDrawing;
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
    private readonly Graphics.EnumerateMetafileProc _enumerate = static (_, _, _, _, _) => true;
    private readonly byte[] _comment = new byte[64];

    [GlobalSetup]
    public void CreateFixture()
    {
        _fixture = CreateEmf(StateRecordCount);
        _target = new Bitmap(1, 1);
        _graphics = Graphics.FromImage(_target);
        _metafile = new Metafile(new MemoryStream(_fixture, writable: false));
    }

    [GlobalCleanup]
    public void DisposeFixture()
    {
        _metafile.Dispose();
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

    private static void WriteUInt16(byte[] target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static void WriteInt32(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);
}
