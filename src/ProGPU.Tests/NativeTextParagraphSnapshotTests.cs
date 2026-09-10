using System.Text;
using ProGPU.Backend.Native;
using Xunit;

namespace Avalonia.ProGpu.UnitTests;

public sealed class NativeTextParagraphSnapshotTests
{
    [Fact]
    public void InlineObjectsMapUtf16PositionsToActualScalarIndices()
    {
        const string text = "\U0001f642\ufffcA\ufffc";
        var scalars = new NativeTextScalar[text.Length];
        int count = NativeTextParagraphSnapshot.DecodeUtf16(text, scalars);
        var mapped = NativeTextParagraphSnapshot.MapInlineObjects(
            [new(2, 30.25f, 35, 7), new(4, 0, 5, 2)], scalars.AsSpan(0, count));
        Assert.Equal(2, mapped.Length);
        Assert.Equal(1U, mapped[0].ScalarIndex);
        Assert.Equal(3U, mapped[1].ScalarIndex);
        Assert.Equal(30.25f, mapped[0].Width);
        Assert.Equal(35, mapped[0].Ascent);
        Assert.Equal(7, mapped[0].Descent);
        Assert.Equal(0, mapped[1].Width);
    }

    [Fact]
    public void InlineObjectsRejectMissingDuplicateUnorderedAndNonObjectPositions()
    {
        const string text = "\U0001f642\ufffcA\ufffc";
        var scalars = new NativeTextScalar[text.Length];
        int count = NativeTextParagraphSnapshot.DecodeUtf16(text, scalars);
        foreach (var positions in new int[][] { [], [2], [2, 2], [4, 2], [1, 4], [2, 3], [2, 4, 5] })
        {
            var objects = positions.Select(static p => new NativeTextParagraphInlineObject(p, 1, 1, 1)).ToArray();
            Assert.Throws<ArgumentException>(() =>
                NativeTextParagraphSnapshot.MapInlineObjects(objects, scalars.AsSpan(0, count)));
        }
        Assert.Empty(NativeTextParagraphSnapshot.MapInlineObjects([], []));
    }

    [Fact]
    public void StyledUtf16RangesPreserveScalarBoundariesAndFaceMetadata()
    {
        const string text = "a\U0001f642bc";
        var scalars = new NativeTextScalar[text.Length];
        int count = NativeTextParagraphSnapshot.DecodeUtf16(text, scalars);
        var mapped = NativeTextParagraphSnapshot.MapStyles(
            [new(0, 3, 0, .01f), new(3, 2, 2, .025f, 3, 4, 123)], scalars.AsSpan(0, count), text.Length);
        Assert.Equal(2U, mapped[0].ScalarCount);
        Assert.Equal(2U, mapped[1].ScalarStart);
        Assert.Equal(2U, mapped[1].ScalarCount);
        Assert.Equal(2U, mapped[1].FontIndex);
        Assert.Equal(.025f, mapped[1].Scale);
        Assert.Equal(3U, mapped[1].FeatureStart);
        Assert.Equal(4U, mapped[1].FeatureCount);
        Assert.Equal(123U, mapped[1].Language);
        Assert.Throws<ArgumentException>(() => NativeTextParagraphSnapshot.MapStyles(
            [new(0, 2, 0, 1), new(2, 3, 0, 1)], scalars.AsSpan(0, count), text.Length));
        Assert.Throws<ArgumentException>(() => NativeTextParagraphSnapshot.MapStyles(
            [new(0, 3, 0, 1)], scalars.AsSpan(0, count), text.Length));
        Assert.Throws<ArgumentException>(() => NativeTextParagraphSnapshot.MapStyles(
            [new(0, 3, 0, 1), new(2, 3, 0, 1)], scalars.AsSpan(0, count), text.Length));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abcdefghijk")]
    [InlineData("\U0001f642\U0001f643\U0001f644\U0001f645\U0001f646x")]
    [InlineData("a\u0301\u05d0\u0628\u4e2d\U0001f642z")]
    [InlineData("abc\ud800d\udc00efgh\ud800\ud800\udc00i")]
    public void IntrinsicUtf16ExpansionMatchesRuneOracleAndOriginalOffsets(string text)
    {
        var output = new NativeTextScalar[text.Length + 2];
        output[^1].CodePoint = 123;
        int written = NativeTextParagraphSnapshot.DecodeUtf16(text, output);
        int index = 0, offset = 0;
        while (offset < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out int consumed);
            if (status != System.Buffers.OperationStatus.Done) { rune = Rune.ReplacementChar; consumed = 1; }
            Assert.Equal((uint)rune.Value, output[index].CodePoint);
            Assert.Equal((uint)offset, output[index].InputIndex);
            Assert.Equal((ushort)consumed, output[index].InputLength);
            Assert.Equal(0, output[index].CanonicalCombiningClass);
            Assert.Equal(0U, output[index].Script);
            index++; offset += consumed;
        }
        Assert.Equal(index, written);
        Assert.Equal(123U, output[^1].CodePoint);
    }

    [Fact]
    public void Utf16ExpansionRejectsInsufficientOutputBeforeWriting()
    {
        var output = new NativeTextScalar[1]; output[0].CodePoint = 123;
        Assert.Throws<ArgumentException>(() => NativeTextParagraphSnapshot.DecodeUtf16("abcd", output));
        Assert.Equal(123U, output[0].CodePoint);
    }
}
