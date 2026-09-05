using System.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadLinFileTests
{
    [Fact]
    public void ParserOwnsSimpleAndComplexDefinitionsWithExactTransforms()
    {
        CadLinFile file = Parse(
            "; source comment\r\n" +
            "*BORDER,Border __ . __ .\r\n" +
            "A,.5,-.25,0,-.25\r\n" +
            "\r\n" +
            "*UTILITIES,Utilities\n" +
            "A,.5,-.2,[\"HW,RETURN\",STANDARD,S=.1,A=100g,X=-.1,Y=-.05],-.2," +
            "[CAP,ep.shx,S=2,R=10d,X=.5,Y=1]\n");

        Assert.Equal(2, file.DefinitionCount);
        Assert.Equal(2, file.SupportedDefinitionCount);
        CadLinDefinition border = file.Definitions.Span[0];
        Assert.Equal("BORDER", border.Name);
        Assert.Equal("Border __ . __ .", border.Description);
        Assert.Equal(2, border.HeaderLineNumber);
        Assert.Equal(
            new[] { 0.5, -0.25, 0.0, -0.25 },
            border.Elements.Span.ToArray().Select(static element => element.Length));
        Assert.All(
            border.Elements.Span.ToArray(),
            element => Assert.Equal(CadLinElementKind.Stroke, element.Kind));

        CadLinElement[] complex = file.Definitions.Span[1].Elements.ToArray();
        CadLinElement text = complex[2];
        Assert.Equal(CadLinElementKind.Text, text.Kind);
        Assert.Equal("HW,RETURN", text.Payload);
        Assert.Equal("STANDARD", text.StyleOrFileName);
        Assert.Equal(0.1, text.Scale);
        Assert.Equal(CadLinRotationMode.Absolute, text.RotationMode);
        Assert.Equal(Math.PI * 0.5, text.RotationRadians, 12);
        Assert.Equal(-0.1, text.XOffset);
        Assert.Equal(-0.05, text.YOffset);
        CadLinElement shape = complex[4];
        Assert.Equal(CadLinElementKind.Shape, shape.Kind);
        Assert.Equal("CAP", shape.Payload);
        Assert.Equal("ep.shx", shape.StyleOrFileName);
        Assert.Equal(2.0, shape.Scale);
        Assert.Equal(CadLinRotationMode.Relative, shape.RotationMode);
        Assert.Equal(Math.PI / 18.0, shape.RotationRadians, 12);
    }

    [Fact]
    public void ParserRetainsUprightAsExplicitUnsupportedSemantics()
    {
        CadLinFile file = Parse(
            "*UPRIGHT,Upright text\n" +
            "A,1,-.2,[\"GAS\",STANDARD,U=0],-.2\n");

        CadLinDefinition definition = Assert.Single(file.Definitions.ToArray());
        Assert.False(definition.IsImportSupported);
        Assert.Equal(0, file.SupportedDefinitionCount);
        Assert.Equal(
            CadLinRotationMode.Upright,
            definition.Elements.Span[2].RotationMode);
    }

    [Theory]
    [InlineData("A,.5,-.25", "no preceding definition")]
    [InlineData("*A,description\n*B,description\nA,1,-1", "has no pattern")]
    [InlineData("*A,description\nB,1,-1", "require A")]
    [InlineData("*A,description\nA,-1,1", "first A-aligned")]
    [InlineData("*A,description\nA,0,0", "positive repeated length")]
    [InlineData("*A,description\nA,1,[\"X\",STANDARD,S=0]", "cannot be zero")]
    [InlineData("*A,description\nA,1,[CAP,file.txt]", ".shx extension")]
    [InlineData("*A,description\nA,1,[\"X\",STANDARD,Z=1]", "unsupported")]
    [InlineData("*A,description\nA,1,[\"X\",STANDARD,R=1,R=2]", "duplicated")]
    public void ParserRejectsMalformedLibraryTransactionally(
        string source,
        string expected)
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => Parse(source));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParserRejectsDuplicateNamesAndConfiguredBudgets()
    {
        Assert.Contains(
            "duplicated",
            Assert.Throws<InvalidDataException>(() => Parse(
                "*DASH,one\nA,1,-1\n*dash,two\nA,2,-2\n")).Message,
            StringComparison.OrdinalIgnoreCase);
        byte[] source = Encoding.ASCII.GetBytes(
            "*ONE,one\nA,1,-1\n*TWO,two\nA,2,-2\n");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CadLinFile.Parse(
                source,
                new CadLinParseOptions { MaxDefinitionCount = 1 }));

        Assert.Contains("definition count", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserRejectsNonAsciiAndOverlongPhysicalLines()
    {
        Assert.Throws<InvalidDataException>(() => CadLinFile.Parse([0x80]));
        byte[] source = Encoding.ASCII.GetBytes("*LONG,description\nA,1,-1\n");

        Assert.Throws<InvalidDataException>(() => CadLinFile.Parse(
            source,
            new CadLinParseOptions { MaxPhysicalLineLength = 8 }));
    }

    private static CadLinFile Parse(string source) =>
        CadLinFile.Parse(Encoding.ASCII.GetBytes(source));
}
