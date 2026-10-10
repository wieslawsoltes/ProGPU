using System.Text;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public sealed class UnicodeLineBreakResolverTests
{
    // Matched by unicode_line_breaks_feed_native_layout_without_allocation in
    // progpu_native_text_tests.cpp. Bytes mean prohibited/opportunity/mandatory.
    // These are default-UAX14 controls, NOT the Windows EDIT reference corpus.
    [Theory]
    [InlineData("A B\r\nC\u0301\u00A0D\u4E00\u4E011.2\U0001F1E6\U0001F1E7\U0001F1E8",
        new byte[] { 0, 1, 0, 0, 2, 0, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1, 2 })]
    [InlineData("\r\r\n", new byte[] { 2, 0, 2 })]
    [InlineData("a\u00A0b\u202Fc", new byte[] { 0, 0, 0, 0, 2 })]
    [InlineData("a\u2603b\U0001F600c", new byte[] { 1, 1, 1, 1, 2 })]
    [InlineData("\u201Ca\u201D b", new byte[] { 0, 0, 0, 1, 2 })]
    [InlineData("\U0001F1E6\u0301\U0001F1E7\U0001F1E8", new byte[] { 0, 0, 1, 2 })]
    [InlineData("\U0001F466\U0001F3FB", new byte[] { 0, 2 })]
    [InlineData("\U0001F466\u200D\U0001F466", new byte[] { 0, 0, 2 })]
    [InlineData("\u1100\u1161\u11A8X", new byte[] { 0, 0, 1, 2 })]
    [InlineData("\u0E01\u0E31", new byte[] { 0, 2 })]
    public void DefaultBoundariesMatchNativeCoreFixtures(string text, byte[] expected)
    {
        UnicodeLineBreakScalar[] input = Decode(text);
        UnicodeLineBreakScalar[] before = input.ToArray();
        var classes = Enumerable.Repeat((UnicodeLineBreakClass)255, input.Length + 2).ToArray();
        var output = Enumerable.Repeat((UnicodeLineBreakKind)255, input.Length + 2).ToArray();
        Assert.True(UnicodeLineBreakResolver.TryResolve(input, classes, output, out var error));
        Assert.Equal(UnicodeLineBreakError.None, error);
        Assert.Equal(expected, output.Take(input.Length).Select(value => (byte)value));
        Assert.All(output.Skip(input.Length), value => Assert.Equal((UnicodeLineBreakKind)255, value));
        Assert.All(classes.Skip(input.Length), value => Assert.Equal((UnicodeLineBreakClass)255, value));
        Assert.Equal(before, input);
    }

    [Theory]
    [InlineData(0x20U, 42)]
    [InlineData(0x4E00U, 25)]
    [InlineData(0x1F1E6U, 39)]
    [InlineData(0xD800U, 0)]
    [InlineData(0xDFFFU, 0)]
    [InlineData(0x110000U, 0)]
    public void ClassLookupUsesTheSharedNativeIds(uint scalar, byte expected)
        => Assert.Equal(expected, (byte)UnicodeLineBreakResolver.GetClass(scalar));

    [Theory]
    [InlineData(0xD800U)]
    [InlineData(0xDFFFU)]
    [InlineData(0x110000U)]
    [InlineData(uint.MaxValue)]
    public void InvalidLaterScalarRetainsOutputAndNativeScratchPrefixSemantics(uint invalid)
    {
        UnicodeLineBreakScalar[] input = [new('A', 7, 1), new(invalid, 8, 1)];
        var classes = new[] { (UnicodeLineBreakClass)255, (UnicodeLineBreakClass)255 };
        var output = new[] { (UnicodeLineBreakKind)255, (UnicodeLineBreakKind)255 };
        Assert.False(UnicodeLineBreakResolver.TryResolve(input, classes, output, out var error));
        Assert.Equal(UnicodeLineBreakError.InvalidArgument, error);
        Assert.Equal(UnicodeLineBreakClass.Alphabetic, classes[0]);
        Assert.Equal((UnicodeLineBreakClass)255, classes[1]);
        Assert.All(output, value => Assert.Equal((UnicodeLineBreakKind)255, value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InsufficientCapacityPrecedesScalarValidationAndPublishesNothing(bool shortScratch)
    {
        UnicodeLineBreakScalar[] input = [new(0xD800, 0, 1), new('A', 1, 1)];
        var classes = Enumerable.Repeat((UnicodeLineBreakClass)255, shortScratch ? 1 : 2).ToArray();
        var output = Enumerable.Repeat((UnicodeLineBreakKind)255, shortScratch ? 2 : 1).ToArray();
        Assert.False(UnicodeLineBreakResolver.TryResolve(input, classes, output, out var error));
        Assert.Equal(UnicodeLineBreakError.InsufficientBuffer, error);
        Assert.All(classes, value => Assert.Equal((UnicodeLineBreakClass)255, value));
        Assert.All(output, value => Assert.Equal((UnicodeLineBreakKind)255, value));
    }

    [Fact]
    public void EmptyInputSucceedsWithoutWritingCallerStorage()
    {
        UnicodeLineBreakClass[] classes = [(UnicodeLineBreakClass)255];
        UnicodeLineBreakKind[] output = [(UnicodeLineBreakKind)255];
        Assert.True(UnicodeLineBreakResolver.TryResolve([], classes, output, out var error));
        Assert.Equal(UnicodeLineBreakError.None, error);
        Assert.Equal((UnicodeLineBreakClass)255, classes[0]);
        Assert.Equal((UnicodeLineBreakKind)255, output[0]);
    }

    [Fact]
    public void SourceMetadataDoesNotChangeScalarBoundaryResolution()
    {
        UnicodeLineBreakScalar[] input = [new('A', 7, 1), new(0x1F600, 8, 2), new('B', 10, 1)];
        var before = input.ToArray();
        UnicodeLineBreakKind[] output = new UnicodeLineBreakKind[3];
        Assert.True(UnicodeLineBreakResolver.TryResolve(input, new UnicodeLineBreakClass[3], output, out _));
        Assert.Equal(new[] { UnicodeLineBreakKind.Opportunity, UnicodeLineBreakKind.Opportunity,
            UnicodeLineBreakKind.Mandatory }, output);
        Assert.Equal(before, input);
        Assert.Equal(new uint[] { 8, 10, 11 }, input.Select(scalar => scalar.InputIndex + scalar.InputLength));

        // The native core resolves decoded scalar order; it neither validates nor
        // rewrites source-unit metadata. Transport/decoder admission is separate.
        UnicodeLineBreakScalar[] otherMetadata = [new('A', 99, 0), new(0x1F600, 0, 1), new('B', 3, 8)];
        UnicodeLineBreakKind[] otherOutput = new UnicodeLineBreakKind[3];
        Assert.True(UnicodeLineBreakResolver.TryResolve(otherMetadata, new UnicodeLineBreakClass[3], otherOutput, out _));
        Assert.Equal(output, otherOutput);
    }

    private static UnicodeLineBreakScalar[] Decode(string text)
    {
        var scalars = new List<UnicodeLineBreakScalar>();
        uint position = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            ushort length = checked((ushort)rune.Utf16SequenceLength);
            scalars.Add(new((uint)rune.Value, position, length));
            position += length;
        }
        return scalars.ToArray();
    }
}
