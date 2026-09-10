using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadDirectDistanceInputTests
{
    [Theory]
    [InlineData("1", 1.0)]
    [InlineData(" +2.5e1 ", 25.0)]
    [InlineData("0.0001", 0.0001)]
    public void ParserAcceptsBoundedInvariantPositiveDistance(
        string text,
        double expected)
    {
        Assert.True(CadDirectDistanceInput.TryParse(
            text,
            out CadDirectDistanceInput input));
        Assert.Equal(expected, input.Distance, 12);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,2")]
    [InlineData("1<45")]
    public void ParserRejectsNonPositiveNonFiniteOrCoordinateInput(string text)
    {
        Assert.False(CadDirectDistanceInput.TryParse(text, out _));
    }

    [Fact]
    public void ResolverNormalizesDirectionAndPreservesBasePlane()
    {
        Assert.True(CadDirectDistanceInput.TryParse(
            "10",
            out CadDirectDistanceInput input));

        Assert.True(input.TryResolve(
            new CadPoint3D(1.0, 2.0, 7.0),
            new CadPoint3D(3.0, 4.0, 0.0),
            out CadPoint3D point));

        Assert.Equal(new CadPoint3D(7.0, 10.0, 7.0), point);
    }

    [Fact]
    public void ParserAndResolverRejectBoundsDegenerateDirectionAndOverflow()
    {
        Assert.False(CadDirectDistanceInput.TryParse(
            new string('1', CadDirectDistanceInput.MaximumCodeUnits + 1),
            out _));
        Assert.True(CadDirectDistanceInput.TryParse(
            "1e308",
            out CadDirectDistanceInput input));

        Assert.False(input.TryResolve(
            new CadPoint3D(1e308, 0.0, 0.0),
            new CadPoint3D(1.0, 0.0, 0.0),
            out _));
        Assert.False(input.TryResolve(
            CadPoint3D.Zero,
            CadPoint3D.Zero,
            out _));
        Assert.False(input.TryResolve(
            CadPoint3D.Zero,
            new CadPoint3D(double.NaN, 0.0, 0.0),
            out _));
    }

    [Fact]
    public void WarmParserAndResolverAllocateNoManagedMemory()
    {
        const string Text = "12.5";
        CadPoint3D basePoint = new(1.0, -2.0, 3.0);
        CadPoint3D direction = new(3.0, 4.0, 0.0);
        Assert.True(CadDirectDistanceInput.TryParse(
            Text,
            out CadDirectDistanceInput warmInput));
        Assert.True(warmInput.TryResolve(basePoint, direction, out _));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allResolved = true;
        for (int i = 0; i < 1_024; i++)
        {
            allResolved &= CadDirectDistanceInput.TryParse(
                Text,
                out CadDirectDistanceInput input) &&
                input.TryResolve(basePoint, direction, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allResolved);
        Assert.Equal(0, allocated);
    }
}
