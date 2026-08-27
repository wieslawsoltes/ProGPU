using System.Text;
using ProGPU.CAD;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadShxFontTests
{
    [Fact]
    public void StandardContainerRetainsPackedFontPrograms()
    {
        byte[] source = BuildStandardShx(
            (0, "TESTFONT", new byte[] { 10, 2, 0, 0 }),
            (65, "UCA", new byte[] { 1, 8, 0, 10, 2, 8, 8, 0, 0 }),
            (66, "UCB", new byte[] { 1, 0x14, 0x18, 0x1C, 0 }));

        CadShxFont font = CadShxFont.Parse(source);

        Assert.True(font.IsTextFont);
        Assert.False(font.SupportsVerticalOrientation);
        Assert.Equal("TESTFONT", font.Name);
        Assert.Equal(10, font.Above);
        Assert.Equal(2, font.Below);
        Assert.Equal(0, font.Modes);
        Assert.Equal(3, font.ShapeCount);
        Assert.True(font.TryGetShape(65, out CadShxShape? shape));
        Assert.Equal("UCA", shape!.Name);
        Assert.Equal(new byte[] { 1, 8, 0, 10, 2, 8, 8, 0, 0 }, shape.Program.ToArray());

        source.AsSpan().Fill(0);
        Assert.Equal(1, shape.Program.Span[0]);
    }

    [Fact]
    public void StandardShapeContainerDoesNotClaimFontMetrics()
    {
        CadShxFont shapes = CadShxFont.Parse(BuildStandardShx(
            (230, "DBOX", new byte[] { 0x14, 0x10, 0x1C, 0x18, 0x12, 0 })));

        Assert.False(shapes.IsTextFont);
        Assert.Equal(string.Empty, shapes.Name);
        Assert.Equal(0, shapes.Above);
        Assert.True(shapes.TryGetShape(230, out _));
    }

    [Fact]
    public void ParserReadsThePinnedDependencyCompiledShapeFixture()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "external", "ACadSharp", "samples", "test_shape.shx");

        CadShxFont shapes = CadShxFont.Parse(File.ReadAllBytes(path));

        CadShxShape shape = Assert.Single(shapes.Shapes).Value;
        Assert.Equal((ushort)1, shape.Number);
        Assert.Equal("MY-SHAPE", shape.Name);
        Assert.Equal(354, shape.Program.Length);
        Assert.Equal((byte)0, shape.Program.Span[^1]);
    }

    [Fact]
    public void ParserRejectsForeignTruncatedDuplicateAndOversizedContainers()
    {
        byte[] valid = BuildStandardShx(
            (0, "TESTFONT", new byte[] { 10, 2, 0, 0 }),
            (65, "UCA", new byte[] { 0 }));
        byte[] duplicate = BuildStandardShx(
            (65, "FIRST", new byte[] { 0 }),
            (65, "SECOND", new byte[] { 0 }));

        Assert.Throws<NotSupportedException>(() => CadShxFont.Parse("not shx"u8));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(valid.AsSpan(0, valid.Length - 1)));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(duplicate));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(
            valid,
            new CadShxParseOptions { MaxShapeBytes = 3 }));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(
            valid,
            new CadShxParseOptions { MaxFileBytes = valid.Length - 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CadShxFont.Parse(
            valid,
            new CadShxParseOptions { MaxShapeCount = ushort.MaxValue + 1 }));
    }

    private static byte[] BuildStandardShx(
        params (ushort Number, string Name, byte[] Program)[] shapes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(shape => shape.Number));
        writer.Write(shapes.Max(shape => shape.Number));
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string name, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return stream.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, ".gitmodules")))
        {
            directory = directory.Parent;
        }
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
