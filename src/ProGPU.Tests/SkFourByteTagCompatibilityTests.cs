using System.Runtime.InteropServices;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkFourByteTagCompatibilityTests
{
    [Fact]
    public void ValueAndCharacterConstructionUseBigEndianTagOrder()
    {
        var tag = new SKFourByteTag('l', 'i', 'g', 'a');

        Assert.Equal(4, Marshal.SizeOf<SKFourByteTag>());
        Assert.Equal(0x6c696761u, (uint)tag);
        Assert.Equal("liga", tag.ToString());
        Assert.Equal(tag, new SKFourByteTag(0x6c696761u));
    }

    [Fact]
    public void ParsePadsShortTagsTruncatesLongTagsAndPreservesEmptyIdentity()
    {
        Assert.Equal(0u, (uint)SKFourByteTag.Parse((string?)null));
        Assert.Equal(0u, (uint)SKFourByteTag.Parse(string.Empty));
        Assert.Equal(0x61202020u, (uint)SKFourByteTag.Parse("a"));
        Assert.Equal(0x61622020u, (uint)SKFourByteTag.Parse("ab"));
        Assert.Equal(0x61626320u, (uint)SKFourByteTag.Parse("abc"));
        Assert.Equal(0x61626364u, (uint)SKFourByteTag.Parse("abcde"));
        Assert.Equal(0x77676874u, (uint)SKFourByteTag.Parse("wght".AsSpan()));
    }

    [Fact]
    public void CharactersAreNarrowedToTheirLowByteLikeNativeTags()
    {
        Assert.Equal(0x00626364u, (uint)new SKFourByteTag('\u0100', 'b', 'c', 'd'));
        Assert.Equal(0xe9202020u, (uint)SKFourByteTag.Parse("é"));
        Assert.Equal("\0\0\0\0", new SKFourByteTag(0).ToString());
    }

    [Fact]
    public void EqualityHashAndImplicitConversionsUsePackedIdentity()
    {
        SKFourByteTag tag = 0x636d6170u;
        uint packed = tag;

        Assert.Equal(0x636d6170u, packed);
        Assert.True(tag == SKFourByteTag.Parse("cmap"));
        Assert.False(tag != SKFourByteTag.Parse("cmap"));
        Assert.NotEqual(tag, SKFourByteTag.Parse("head"));
        Assert.Equal(tag.GetHashCode(), SKFourByteTag.Parse("cmap").GetHashCode());
        Assert.True(tag.Equals((object)SKFourByteTag.Parse("cmap")));
        Assert.False(tag.Equals("cmap"));
    }
}
