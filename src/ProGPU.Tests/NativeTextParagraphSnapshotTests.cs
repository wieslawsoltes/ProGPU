using System.Text;
using ProGPU.Backend.Native;
using Xunit;

namespace Avalonia.ProGpu.UnitTests;

public sealed class NativeTextParagraphSnapshotTests
{
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
