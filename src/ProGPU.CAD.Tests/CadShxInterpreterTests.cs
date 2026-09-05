using System.Numerics;
using System.Text;
using ProGPU.CAD;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadShxInterpreterTests
{
    [Fact]
    public void DirectionVectorsRetainTheCompiledDboxPath()
    {
        CadShxFont font = ParseShapeFont(
            (230, "DBOX", new byte[] { 0x14, 0x10, 0x1C, 0x18, 0x12, 0 }));

        CadShxGeometry geometry = CadShxInterpreter.Interpret(font, 230);

        PathFigure figure = Assert.Single(geometry.Path.Figures);
        Assert.False(figure.IsFilled);
        Assert.Equal(5, figure.Segments.Count);
        Assert.Equal(new Vector2(1.0f, 1.0f), geometry.EndPoint);
        Assert.Equal(6, geometry.CommandCount);
        Assert.Equal(5, geometry.SegmentCount);
    }

    [Fact]
    public void AllSixteenDirectionCodesUseTheSpecifiedOrthogonalAndHalfSteps()
    {
        Vector2[] expected =
        {
            new(1.0f, 0.0f), new(1.0f, 0.5f), new(1.0f, 1.0f), new(0.5f, 1.0f),
            new(0.0f, 1.0f), new(-0.5f, 1.0f), new(-1.0f, 1.0f), new(-1.0f, 0.5f),
            new(-1.0f, 0.0f), new(-1.0f, -0.5f), new(-1.0f, -1.0f), new(-0.5f, -1.0f),
            new(0.0f, -1.0f), new(0.5f, -1.0f), new(1.0f, -1.0f), new(1.0f, -0.5f),
        };
        var definitions = new (ushort Number, string Name, byte[] Program)[16];
        for (int i = 0; i < definitions.Length; i++)
        {
            definitions[i] = ((ushort)(100 + i), $"DIRECTION{i}", new byte[] { (byte)(0x10 | i), 0 });
        }
        CadShxFont font = ParseShapeFont(definitions);

        for (int i = 0; i < expected.Length; i++)
        {
            CadShxGeometry geometry = CadShxInterpreter.Interpret(font, (ushort)(100 + i));
            Assert.Equal(expected[i], geometry.EndPoint);
        }
    }

    [Fact]
    public void ScaleStackAndSubshapeStateComposeWithoutFlattening()
    {
        CadShxFont font = ParseShapeFont(
            (65, "ROOT", new byte[]
            {
                2, 8, 10, 0,
                1, 3, 2, 5, 7, 66, 6, 4, 2,
                2, 8, 6, 0, 0,
            }),
            (66, "PART", new byte[] { 0x14, 0x10, 0 }));

        CadShxGeometry geometry = CadShxInterpreter.Interpret(font, 65);

        PathFigure figure = Assert.Single(geometry.Path.Figures);
        Assert.Equal(2, figure.Segments.Count);
        Assert.Equal(new Vector2(16.0f, 0.0f), geometry.EndPoint);
        Assert.Equal(new Vector2(10.0f, 0.5f),
            Assert.IsType<LineSegment>(figure.Segments[0]).Point);
        Assert.Equal(new Vector2(10.5f, 0.5f),
            Assert.IsType<LineSegment>(figure.Segments[1]).Point);
    }

    [Fact]
    public void OctantFractionalAndBulgeArcsRemainAnalytic()
    {
        CadShxFont font = ParseShapeFont(
            (65, "FULL", new byte[] { 2, 8, 1, 0, 1, 10, 1, 0x00, 0 }),
            (66, "FRACTION", new byte[] { 11, 56, 28, 0, 3, 0x12, 0 }),
            (67, "BULGE", new byte[] { 12, 10, 0, 127, 13, 10, 0, 0, 0, 0, 0 }));

        CadShxGeometry full = CadShxInterpreter.Interpret(font, 65);
        PathFigure fullFigure = Assert.Single(full.Path.Figures);
        Assert.Equal(2, fullFigure.Segments.Count);
        Assert.All(fullFigure.Segments, segment => Assert.IsType<ArcSegment>(segment));
        AssertVector(new Vector2(1.0f, 0.0f), full.EndPoint, 1e-5f);

        CadShxGeometry fractional = CadShxInterpreter.Interpret(font, 66);
        ArcSegment fractionalArc = Assert.IsType<ArcSegment>(
            Assert.Single(Assert.Single(fractional.Path.Figures).Segments));
        double start = 55.0 * Math.PI / 180.0;
        double end = 95.0 * Math.PI / 180.0;
        AssertVector(
            new Vector2(
                (float)(3.0 * (Math.Cos(end) - Math.Cos(start))),
                (float)(3.0 * (Math.Sin(end) - Math.Sin(start)))),
            fractionalArc.Point,
            0.02f);
        Assert.Equal(SweepDirection.Counterclockwise, fractionalArc.SweepDirection);

        CadShxGeometry bulge = CadShxInterpreter.Interpret(font, 67);
        PathFigure bulgeFigure = Assert.Single(bulge.Path.Figures);
        ArcSegment semicircle = Assert.IsType<ArcSegment>(bulgeFigure.Segments[0]);
        Assert.Equal(new Vector2(5.0f, 5.0f), semicircle.Size);
        Assert.Equal(SweepDirection.Counterclockwise, semicircle.SweepDirection);
        Assert.IsType<LineSegment>(bulgeFigure.Segments[1]);
        Assert.Equal(new Vector2(20.0f, 0.0f), bulge.EndPoint);
    }

    [Fact]
    public void VerticalConditionalsConsumeExactlyOneFollowingCommand()
    {
        CadShxFont font = ParseTextFont(
            modes: 2,
            (65, "UCA", new byte[]
            {
                2, 14, 8, unchecked((byte)-2), 6,
                1, 0x10,
                2, 0x10,
                14, 8, unchecked((byte)-4), unchecked((byte)-3),
                0,
            }));

        CadShxGeometry horizontal = CadShxInterpreter.Interpret(
            font,
            65,
            CadShxOrientation.Horizontal);
        CadShxGeometry vertical = CadShxInterpreter.Interpret(
            font,
            65,
            CadShxOrientation.Vertical);

        Assert.Equal(new Vector2(2.0f, 0.0f), horizontal.EndPoint);
        Assert.Equal(new Vector2(-4.0f, 3.0f), vertical.EndPoint);
        Assert.Throws<NotSupportedException>(() => CadShxInterpreter.Interpret(
            ParseTextFont(0, (65, "UCA", new byte[] { 0 })),
            65,
            CadShxOrientation.Vertical));
        Assert.Throws<InvalidDataException>(() => CadShxInterpreter.Interpret(
            ParseTextFont(0, (65, "UCA", new byte[] { 14, 0x10, 0 })),
            65));
        Assert.Throws<ArgumentOutOfRangeException>(() => CadShxInterpreter.Interpret(font, 0));
    }

    [Fact]
    public void InterpreterRejectsMalformedCyclesAndBoundOverruns()
    {
        CadShxFont cycle = ParseShapeFont(
            (65, "FIRST", new byte[] { 7, 66, 0 }),
            (66, "SECOND", new byte[] { 7, 65, 0 }));
        CadShxFont underflow = ParseShapeFont(
            (65, "UNDER", new byte[] { 6, 0 }));
        CadShxFont truncated = ParseShapeFont(
            (65, "SHORT", new byte[] { 8, 1, 0 }));
        CadShxFont line = ParseShapeFont(
            (65, "LINE", new byte[] { 0x10, 0x10, 0 }));

        Assert.Throws<InvalidDataException>(() => CadShxInterpreter.Interpret(cycle, 65));
        Assert.Throws<InvalidDataException>(() => CadShxInterpreter.Interpret(underflow, 65));
        Assert.Throws<InvalidDataException>(() => CadShxInterpreter.Interpret(truncated, 65));
        Assert.Throws<InvalidDataException>(() => CadShxInterpreter.Interpret(
            line,
            65,
            options: new CadShxInterpretOptions { MaxSegments = 1 }));
        Assert.Throws<InvalidDataException>(() => CadShxInterpreter.Interpret(
            line,
            65,
            options: new CadShxInterpretOptions { MaxCommands = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CadShxInterpreter.Interpret(
            line,
            65,
            options: new CadShxInterpretOptions { MaxCoordinateMagnitude = double.PositiveInfinity }));
    }

    [Fact]
    public void InterpreterExecutesThePinnedDependencyCompiledShapeFixture()
    {
        string root = FindRepositoryRoot();
        CadShxFont font = CadShxFont.Parse(File.ReadAllBytes(Path.Combine(
            root,
            "external",
            "ACadSharp",
            "samples",
            "test_shape.shx")));

        CadShxGeometry geometry = CadShxInterpreter.Interpret(font, 1);

        Assert.NotEmpty(geometry.Path.Figures);
        Assert.True(geometry.SegmentCount > 0);
        Assert.True(Vector2.Distance(Vector2.Zero, geometry.EndPoint) < 1e-3f);
    }

    private static CadShxFont ParseShapeFont(
        params (ushort Number, string Name, byte[] Program)[] shapes) =>
        CadShxFont.Parse(BuildStandardShx(shapes));

    private static CadShxFont ParseTextFont(
        byte modes,
        params (ushort Number, string Name, byte[] Program)[] shapes)
    {
        var definitions = new (ushort Number, string Name, byte[] Program)[shapes.Length + 1];
        definitions[0] = (0, "TESTFONT", new byte[] { 10, 2, modes, 0 });
        shapes.CopyTo(definitions, 1);
        return CadShxFont.Parse(BuildStandardShx(definitions));
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

    private static void AssertVector(Vector2 expected, Vector2 actual, float tolerance)
    {
        Assert.InRange(MathF.Abs(actual.X - expected.X), 0.0f, tolerance);
        Assert.InRange(MathF.Abs(actual.Y - expected.Y), 0.0f, tolerance);
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
