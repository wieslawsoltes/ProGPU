using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadCoordinateInputTests
{
    [Theory]
    [InlineData("1.25,-2", CadCoordinateInputKind.AbsoluteCartesian, 1.25, -2.0, 0.0)]
    [InlineData(" 1e2 , -2.5e-1 , 3 ", CadCoordinateInputKind.AbsoluteCartesian, 100.0, -0.25, 3.0)]
    [InlineData("@4,5", CadCoordinateInputKind.RelativeCartesian, 4.0, 5.0, 0.0)]
    [InlineData(" @ 4, 5, -6 ", CadCoordinateInputKind.RelativeCartesian, 4.0, 5.0, -6.0)]
    [InlineData("5<0", CadCoordinateInputKind.AbsolutePolar, 5.0, 0.0, 0.0)]
    [InlineData("@5<450", CadCoordinateInputKind.RelativePolar, 0.0, 5.0, 0.0)]
    public void ParserAcceptsBoundedInvariantCartesianAndPolarGrammar(
        string text,
        CadCoordinateInputKind expectedKind,
        double expectedX,
        double expectedY,
        double expectedZ)
    {
        Assert.True(CadCoordinateInput.TryParse(
            text,
            out CadCoordinateInput coordinate));

        Assert.Equal(expectedKind, coordinate.Kind);
        Assert.Equal(
            expectedKind is CadCoordinateInputKind.RelativeCartesian or
                CadCoordinateInputKind.RelativePolar,
            coordinate.IsRelative);
        Assert.Equal(expectedX, coordinate.Value.X, 12);
        Assert.Equal(expectedY, coordinate.Value.Y, 12);
        Assert.Equal(expectedZ, coordinate.Value.Z, 12);
    }

    [Fact]
    public void RelativeCoordinateResolutionUsesCallerOwnedWcsOrigin()
    {
        Assert.True(CadCoordinateInput.TryParse(
            "@2.5,-4,6",
            out CadCoordinateInput relative));
        Assert.True(relative.TryResolve(
            new CadPoint3D(10, 20, 30),
            out CadPoint3D resolved));
        Assert.Equal(new CadPoint3D(12.5, 16, 36), resolved);

        Assert.True(CadCoordinateInput.TryParse(
            "2.5,-4,6",
            out CadCoordinateInput absolute));
        Assert.True(absolute.TryResolve(
            new CadPoint3D(10, 20, 30),
            out resolved));
        Assert.Equal(new CadPoint3D(2.5, -4, 6), resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("1")]
    [InlineData(",2")]
    [InlineData("1,")]
    [InlineData("1,2,")]
    [InlineData("1,2,3,4")]
    [InlineData("1;2")]
    [InlineData("NaN,0")]
    [InlineData("Infinity,0")]
    [InlineData("1e309,0")]
    [InlineData("-1<45")]
    [InlineData("1<")]
    [InlineData("<45")]
    [InlineData("1<2<3")]
    [InlineData("1,2<45")]
    [InlineData("#1,2")]
    public void ParserRejectsMalformedAmbiguousOrNonFiniteInput(string text)
    {
        Assert.False(CadCoordinateInput.TryParse(text, out _));
    }

    [Fact]
    public void ParserAndResolverRejectBoundAndArithmeticOverflow()
    {
        Assert.False(CadCoordinateInput.TryParse(
            new string('1', CadCoordinateInput.MaximumCodeUnits + 1),
            out _));
        Assert.True(CadCoordinateInput.TryParse(
            "@1e308,0",
            out CadCoordinateInput coordinate));

        Assert.False(coordinate.TryResolve(
            new CadPoint3D(1e308, 0, 0),
            out _));
        Assert.False(coordinate.TryResolve(
            new CadPoint3D(double.NaN, 0, 0),
            out _));
    }
}
