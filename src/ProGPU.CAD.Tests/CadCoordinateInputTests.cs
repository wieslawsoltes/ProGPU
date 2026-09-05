using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadCoordinateInputTests
{
    [Theory]
    [InlineData("1.25,-2", CadCoordinateInputKind.AbsoluteCartesian, 1.25, -2.0, 0.0)]
    [InlineData(" 1e2 , -2.5e-1 , 3 ", CadCoordinateInputKind.AbsoluteCartesian, 100.0, -0.25, 3.0)]
    [InlineData("@4,5", CadCoordinateInputKind.RelativeCartesian, 4.0, 5.0, 0.0)]
    [InlineData(" @ 4, 5, -6 ", CadCoordinateInputKind.RelativeCartesian, 4.0, 5.0, -6.0)]
    [InlineData("@", CadCoordinateInputKind.RelativeCartesian, 0.0, 0.0, 0.0)]
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

    [Fact]
    public void CurrentUcsResolutionUsesRawCartesianAndAngularPolarBases()
    {
        var context = new CadPlanAuthoringContext(
            new CadPoint3D(100, 200, 300),
            new CadPoint3D(0, 1, 0),
            new CadPoint3D(-1, 0, 0),
            angleBaseRadians: Math.PI / 2.0,
            isClockwise: true);
        Assert.True(CadCoordinateInput.TryParse(
            "2,3,4",
            out CadCoordinateInput absoluteCartesian));
        Assert.True(absoluteCartesian.TryResolve(
            context,
            new CadPoint3D(double.NaN, 0, 0),
            out CadPoint3D resolved));
        Assert.Equal(new CadPoint3D(97, 202, 304), resolved);

        Assert.True(CadCoordinateInput.TryParse(
            "@2,3,4",
            out CadCoordinateInput relativeCartesian));
        Assert.True(relativeCartesian.TryResolve(
            context,
            new CadPoint3D(10, 20, 30),
            out resolved));
        Assert.Equal(new CadPoint3D(7, 22, 34), resolved);

        Assert.True(CadCoordinateInput.TryParse(
            "5<90",
            out CadCoordinateInput absolutePolar));
        Assert.True(absolutePolar.TryResolve(
            context,
            CadPoint3D.Zero,
            out resolved));
        Assert.Equal(100, resolved.X, 12);
        Assert.Equal(205, resolved.Y, 12);
        Assert.Equal(300, resolved.Z, 12);

        Assert.False(absoluteCartesian.TryResolve(
            default,
            CadPoint3D.Zero,
            out _));
    }

    [Theory]
    [InlineData("")]
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
