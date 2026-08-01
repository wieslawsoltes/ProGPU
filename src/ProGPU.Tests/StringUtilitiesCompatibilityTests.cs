using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class StringUtilitiesCompatibilityTests
{
    [Theory]
    [InlineData(SKTextEncoding.Utf8, "41F09F9880C3A9")]
    [InlineData(SKTextEncoding.Utf16, "41003DD800DEE900")]
    [InlineData(SKTextEncoding.Utf32, "4100000000F60100E9000000")]
    public void TextEncodingAndDecodingMatchOfficialBytes(
        SKTextEncoding encoding,
        string expectedHex)
    {
        const string text = "A😀é";
        var fromString = StringUtilities.GetEncodedText(text, encoding);
        var fromSpan = StringUtilities.GetEncodedText(text.AsSpan(), encoding);

        Assert.Equal(expectedHex, Convert.ToHexString(fromString));
        Assert.Equal(fromString, fromSpan);
        Assert.NotSame(fromString, fromSpan);
        Assert.Equal(text, StringUtilities.GetString(fromString, encoding));
        Assert.Equal(text, StringUtilities.GetString((ReadOnlySpan<byte>)fromString, encoding));
        Assert.Equal(text, StringUtilities.GetString(fromString, 0, fromString.Length, encoding));
        Assert.Equal(text, StringUtilities.GetString((ReadOnlySpan<byte>)fromString, 0, fromString.Length, encoding));
    }

    [Fact]
    public void InvalidUnicodeUsesReplacementFallbackLikeOfficialEncodingHelpers()
    {
        var unpairedHigh = new string(['\ud800']);

        Assert.Equal("EFBFBD", Convert.ToHexString(StringUtilities.GetEncodedText(unpairedHigh, SKTextEncoding.Utf8)));
        Assert.Equal("FDFF", Convert.ToHexString(StringUtilities.GetEncodedText(unpairedHigh, SKTextEncoding.Utf16)));
        Assert.Equal("FDFF0000", Convert.ToHexString(StringUtilities.GetEncodedText(unpairedHigh, SKTextEncoding.Utf32)));
        Assert.Equal("\ufffd", StringUtilities.GetString(new byte[] { 0xff }, SKTextEncoding.Utf8));
        Assert.Equal("\ufffd", StringUtilities.GetString(new byte[] { 0x41 }, SKTextEncoding.Utf16));
        Assert.Equal("\ufffd", StringUtilities.GetString(new byte[] { 0x41 }, SKTextEncoding.Utf32));
    }

    [Fact]
    public unsafe void PointerAndSliceOverloadsRespectBoundsAndContent()
    {
        byte[] data = [0xcc, 0x41, 0, 0x42, 0, 0xdd];
        Assert.Equal("AB", StringUtilities.GetString(data, 1, 4, SKTextEncoding.Utf16));
        Assert.Equal("AB", StringUtilities.GetString((ReadOnlySpan<byte>)data, 1, 4, SKTextEncoding.Utf16));

        fixed (byte* pointer = data)
            Assert.Equal("AB", StringUtilities.GetString((IntPtr)(pointer + 1), 4, SKTextEncoding.Utf16));

        Assert.Equal(string.Empty, StringUtilities.GetString(IntPtr.Zero, 0, SKTextEncoding.Utf8));
        Assert.Throws<ArgumentOutOfRangeException>(() => StringUtilities.GetString(data, -1, 1, SKTextEncoding.Utf8));
        Assert.Throws<ArgumentOutOfRangeException>(() => StringUtilities.GetString((ReadOnlySpan<byte>)data, 5, 2, SKTextEncoding.Utf8));
    }

    [Theory]
    [InlineData(SKTextEncoding.Utf8)]
    [InlineData(SKTextEncoding.Utf16)]
    [InlineData(SKTextEncoding.Utf32)]
    public void UnicodeCharacterCodeReturnsOneCompleteScalar(SKTextEncoding encoding)
    {
        Assert.Equal(0x41, StringUtilities.GetUnicodeCharacterCode("A", encoding));
        Assert.Equal(0x1f600, StringUtilities.GetUnicodeCharacterCode("😀", encoding));
        Assert.Throws<ArgumentException>(() => StringUtilities.GetUnicodeCharacterCode(string.Empty, encoding));
        Assert.Throws<ArgumentException>(() => StringUtilities.GetUnicodeCharacterCode("AB", encoding));
        Assert.Throws<ArgumentException>(() => StringUtilities.GetUnicodeCharacterCode(new string(['\ud800']), encoding));
    }

    [Fact]
    public void GlyphAndOutOfRangeEncodingsAreRejected()
    {
        foreach (var encoding in new[] { SKTextEncoding.GlyphId, (SKTextEncoding)(-1), (SKTextEncoding)4 })
        {
            Assert.Equal("encoding", Assert.Throws<ArgumentOutOfRangeException>(() => StringUtilities.GetEncodedText("A", encoding)).ParamName);
            Assert.Equal("encoding", Assert.Throws<ArgumentOutOfRangeException>(() => StringUtilities.GetString(new byte[] { 65 }, encoding)).ParamName);
            Assert.Equal("encoding", Assert.Throws<ArgumentOutOfRangeException>(() => StringUtilities.GetUnicodeCharacterCode("A", encoding)).ParamName);
        }
    }

    [Fact]
    public void EmptyAndNullTextHaveBoundedOwnership()
    {
        Assert.Empty(StringUtilities.GetEncodedText((string)null!, SKTextEncoding.Utf8));
        Assert.Empty(StringUtilities.GetEncodedText(string.Empty, SKTextEncoding.Utf16));
        Assert.Equal(string.Empty, StringUtilities.GetString(Array.Empty<byte>(), SKTextEncoding.Utf32));
        Assert.Throws<ArgumentNullException>(() => StringUtilities.GetString((byte[])null!, SKTextEncoding.Utf8));
        Assert.Throws<ArgumentNullException>(() => StringUtilities.GetUnicodeCharacterCode(null!, SKTextEncoding.Utf8));
    }
}
