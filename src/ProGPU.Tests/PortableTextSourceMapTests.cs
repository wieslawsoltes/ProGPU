using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class PortableTextSourceMapTests
{
    [Fact]
    public void HiddenBoundariesKeepBothAffinitiesAndRoundTripVisiblePositions()
    {
        PortableTextSourceRange[] ranges = [new(2, 0, 3), new(8, 3, 2)];
        var map = new PortableTextSourceMap(12, 5, ranges);
        ranges[0] = default; // Map owns its snapshot.
        Assert.Equal(0, map.ToSource(0, false));
        Assert.Equal(2, map.ToSource(0, true));
        Assert.Equal(5, map.ToSource(3, false));
        Assert.Equal(8, map.ToSource(3, true));
        Assert.Equal(10, map.ToSource(5, false));
        Assert.Equal(12, map.ToSource(5, true));
        for (int source = 0; source <= 12; source++)
        {
            int expected = Math.Clamp(source - 2, 0, 3) + Math.Clamp(source - 8, 0, 2);
            Assert.Equal(expected, map.ToText(source));
        }
        for (int text = 0; text <= 5; text++)
        {
            Assert.Equal(text, map.ToText(map.ToSource(text, false)));
            Assert.Equal(text, map.ToText(map.ToSource(text, true)));
        }
    }

    [Fact]
    public void EmptyHiddenOnlyAndIdentityMapsHaveExplicitEdgeSemantics()
    {
        var hidden = new PortableTextSourceMap(6, 0, []);
        Assert.Equal(0, hidden.ToText(4));
        Assert.Equal(0, hidden.ToSource(0, false));
        Assert.Equal(6, hidden.ToSource(0, true));
        var identity = new PortableTextSourceMap(5, 5, [new(0, 0, 2), new(2, 2, 3)]);
        for (int i = 0; i <= 5; i++)
        {
            Assert.Equal(i, identity.ToText(i));
            Assert.Equal(i, identity.ToSource(i, false));
            Assert.Equal(i, identity.ToSource(i, true));
        }
        var empty = new PortableTextSourceMap(0, 0, []);
        Assert.Equal(0, empty.ToSource(0, true));
        Assert.Equal(0, empty.ToText(0));
    }

    [Fact]
    public void InvalidPartitionsAndOutOfRangeQueriesFailClosed()
    {
        Assert.Throws<ArgumentException>(() => new PortableTextSourceMap(5, 2, [new(0, 1, 2)]));
        Assert.Throws<ArgumentException>(() => new PortableTextSourceMap(5, 4, [new(0, 0, 2), new(1, 2, 2)]));
        Assert.Throws<ArgumentException>(() => new PortableTextSourceMap(5, 2, [new(4, 0, 2)]));
        Assert.Throws<ArgumentException>(() => new PortableTextSourceMap(5, 2, []));
        Assert.Throws<ArgumentException>(() => new PortableTextSourceMap(int.MaxValue, 1, [new(int.MaxValue, 0, 1)]));
        var map = new PortableTextSourceMap(5, 2, [new(1, 0, 2)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ToText(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ToText(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ToSource(3, true));
        var large = new PortableTextSourceMap(int.MaxValue, int.MaxValue - 1,
            [new(0, 0, int.MaxValue - 3), new(int.MaxValue - 2, int.MaxValue - 3, 2)]);
        Assert.Equal(int.MaxValue, large.ToSource(int.MaxValue - 1, false));
        Assert.Equal(int.MaxValue - 1, large.ToText(int.MaxValue));
    }
}
