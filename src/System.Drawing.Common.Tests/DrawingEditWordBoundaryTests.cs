using System.Drawing.Text;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.SystemDrawing;
using Xunit;

namespace System.Drawing.Tests;

public sealed class DrawingEditWordBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactOriginalSourceAndDirectionSurviveCallerMutationAndDisposal(bool rtl)
    {
        const string source = "  alpha  beta  ";
        DrawingTextLayout layout;
        using (Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext()))
        using (var font = new Font(FontFamily.GenericSansSerif, 16))
        using (StringFormat format = Format(rtl))
        {
            layout = DrawingTextLayout.Create(graphics, source, font, new SizeF(21, 1), format);
            format.FormatFlags ^= StringFormatFlags.DirectionRightToLeft;
            format.SetDigitSubstitution(0x401, StringDigitSubstitute.National);
            format.Alignment = StringAlignment.Far;
        }
        int calls = 0;
        NativeEditWordBoundaryResult result = layout.GetEditWordBoundaries(
            (ReadOnlySpan<char> text, int level, Span<uint> positions) =>
            {
                calls++;
                Assert.Equal(source, text.ToString());
                Assert.Equal(rtl ? 1 : 0, level);
                Assert.Equal(source.Length + 1, positions.Length);
                new uint[] { 0, 2, 9, 15 }.CopyTo(positions);
                return Success(4, 2);
            }, out DrawingEditWordBoundarySnapshot? snapshot);
        Assert.Equal(NativeRendererStatus.Success, result.Status);
        Assert.NotNull(snapshot);
        Assert.Equal(new[] { 0, 2, 9, 15 }, snapshot.Positions);
        Assert.Equal(2, snapshot.LeadingContentStart);
        Assert.Equal(source.Length, snapshot.TextLength);
        Assert.Equal(rtl ? 1 : 0, snapshot.ParagraphLevel);
        result.BoundaryCount = 99; // Result transport is a copy, never the retained metadata.
        NativeEditWordBoundaryResult repeated = layout.GetEditWordBoundaries(out var same);
        Assert.Same(snapshot, same);
        Assert.Equal(4U, repeated.BoundaryCount);
        Assert.Equal(1, calls);
        var mutableView = Assert.IsAssignableFrom<IList<int>>(snapshot.Positions);
        Assert.Throws<NotSupportedException>(() => mutableView[1] = 3);
        Assert.False(snapshot.Positions is int[]);
        Assert.Equal(2, snapshot.Positions[1]);
    }

    [Theory]
    [InlineData("", new[] { 0 })]
    [InlineData("x\U0001F469\u200D\U0001F4BBy ", new[] { 0, 1, 4, 6, 8 })]
    [InlineData("a\r\nb\r\n", new[] { 0, 1, 3, 4, 6 })]
    public void CompleteInventoryKeepsEmptyHardRowsAndInteriorGraphemeEndpoints(string source, int[] expected)
    {
        DrawingTextLayout layout = Create(source);
        var result = layout.GetEditWordBoundaries(
            (ReadOnlySpan<char> text, int level, Span<uint> positions) =>
            {
                Assert.Equal(source, text.ToString());
                Assert.Equal(0, level);
                for (int i = 0; i < expected.Length; i++) positions[i] = (uint)expected[i];
                return Success((uint)expected.Length);
            }, out var snapshot);
        Assert.Equal(NativeRendererStatus.Success, result.Status);
        Assert.NotNull(snapshot);
        Assert.Equal(expected, snapshot.Positions);
        Assert.Equal(layout.TextLength, snapshot.TextLength);
    }

    [Fact]
    public void EachFormattedGenerationCapturesItsWholeSourceOnce()
    {
        const string source = "aa aa aa";
        DrawingTextLayout narrow = Create(source, 20);
        DrawingTextLayout wide = Create(source, 500);
        Assert.True(narrow.RowCount > wide.RowCount);
        int calls = 0;
        NativeEditWordBoundaryResult Resolve(ReadOnlySpan<char> text, int level, Span<uint> positions)
        {
            calls++;
            Assert.Equal(source, text.ToString());
            new uint[] { 0, 3, 6, 8 }.CopyTo(positions);
            return Success(4);
        }
        narrow.GetEditWordBoundaries(Resolve, out var first);
        wide.GetEditWordBoundaries(Resolve, out var second);
        Assert.NotSame(first, second);
        Assert.Equal(first!.Positions, second!.Positions);
        Assert.Equal(2, calls);
        narrow.GetEditWordBoundaries(out var repeated);
        Assert.Same(first, repeated);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(NativeRendererStatus.Unsupported, NativeEditWordBoundaryError.DependencyUnavailable)]
    [InlineData(NativeRendererStatus.Unsupported, NativeEditWordBoundaryError.UnqualifiedBmpSymbolPolicy)]
    [InlineData(NativeRendererStatus.Unsupported, NativeEditWordBoundaryError.UnqualifiedComplexScriptPolicy)]
    [InlineData(NativeRendererStatus.InternalError, NativeEditWordBoundaryError.DependencyFailure)]
    [InlineData(NativeRendererStatus.OutOfMemory, NativeEditWordBoundaryError.AllocationFailure)]
    public void ExactNativeFailureIsRetainedWithoutPublishingAnInventory(
        NativeRendererStatus status, NativeEditWordBoundaryError error)
    {
        DrawingTextLayout layout = Create("source");
        int calls = 0;
        var first = layout.GetEditWordBoundaries(
            (ReadOnlySpan<char> text, int level, Span<uint> positions) =>
            {
                calls++;
                return new() { Status = status, ErrorCode = error };
            }, out var snapshot);
        Assert.Null(snapshot);
        var second = layout.GetEditWordBoundaries(out var repeated);
        Assert.Null(repeated);
        Assert.Equal(status, first.Status);
        Assert.Equal(error, first.ErrorCode);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void BindingFailurePreservesOriginalExceptionAndNeverRetriesAHiddenProvider()
    {
        DrawingTextLayout layout = Create("source");
        var failure = new EntryPointNotFoundException("original native export");
        int calls = 0;
        EditWordBoundaryResolver resolver = (ReadOnlySpan<char> _, int _, Span<uint> _) =>
        {
            calls++;
            throw failure;
        };
        Assert.Same(failure, Assert.Throws<EntryPointNotFoundException>(
            () => layout.GetEditWordBoundaries(resolver, out _)));
        Assert.Same(failure, Assert.Throws<EntryPointNotFoundException>(
            () => layout.GetEditWordBoundaries(out _)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ConcurrentFirstQueriesPublishOneSnapshotAndOneBatch()
    {
        DrawingTextLayout layout = Create("word");
        int calls = 0;
        var snapshots = new DrawingEditWordBoundarySnapshot?[16];
        EditWordBoundaryResolver resolver = (ReadOnlySpan<char> text, int level, Span<uint> positions) =>
        {
            Interlocked.Increment(ref calls);
            positions[0] = 0;
            positions[1] = 4;
            return Success(2);
        };
        Parallel.For(0, snapshots.Length, i => layout.GetEditWordBoundaries(resolver, out snapshots[i]));
        Assert.Equal(1, calls);
        Assert.NotNull(snapshots[0]);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
    }

    [Theory]
    [InlineData("zero-count")]
    [InlineData("oversized-count")]
    [InlineData("success-error")]
    [InlineData("missing-start")]
    [InlineData("missing-end")]
    [InlineData("unordered")]
    [InlineData("duplicate")]
    [InlineData("out-of-range")]
    [InlineData("missing-leading")]
    [InlineData("failure-count")]
    [InlineData("failure-error")]
    public void MalformedProviderOutputCannotPublishOrFilterAnInventory(string kind)
    {
        DrawingTextLayout layout = Create("word");
        int calls = 0;
        EditWordBoundaryResolver resolver = (ReadOnlySpan<char> text, int level, Span<uint> positions) =>
        {
            calls++;
            positions.Clear();
            positions[1] = 4;
            NativeEditWordBoundaryResult result = Success(2);
            switch (kind)
            {
                case "zero-count": result.BoundaryCount = 0; break;
                case "oversized-count": result.BoundaryCount = 6; break;
                case "success-error": result.ErrorCode = NativeEditWordBoundaryError.DependencyUnavailable; break;
                case "missing-start": positions[0] = 1; break;
                case "missing-end": positions[1] = 3; break;
                case "unordered": new uint[] { 0, 3, 2, 4 }.CopyTo(positions); result.BoundaryCount = 4; break;
                case "duplicate": new uint[] { 0, 2, 2, 4 }.CopyTo(positions); result.BoundaryCount = 4; break;
                case "out-of-range": new uint[] { 0, 6, 4 }.CopyTo(positions); result.BoundaryCount = 3; break;
                case "missing-leading": result.LeadingContentStart = 2; break;
                case "failure-count": result.Status = NativeRendererStatus.Unsupported;
                    result.ErrorCode = NativeEditWordBoundaryError.DependencyUnavailable; break;
                case "failure-error": result = new() { Status = NativeRendererStatus.Unsupported }; break;
            }
            return result;
        };
        InvalidDataException original = Assert.Throws<InvalidDataException>(
            () => layout.GetEditWordBoundaries(resolver, out _));
        Assert.Same(original, Assert.Throws<InvalidDataException>(() => layout.GetEditWordBoundaries(out _)));
        Assert.Equal(1, calls);
    }

    private static NativeEditWordBoundaryResult Success(uint count, uint leading = 0)
        => new() { Status = NativeRendererStatus.Success, BoundaryCount = count, LeadingContentStart = leading };

    private static DrawingTextLayout Create(string source, float width = 300)
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        using StringFormat format = Format(false);
        return DrawingTextLayout.Create(graphics, source, font, new SizeF(width, 100), format);
    }

    private static StringFormat Format(bool rtl)
    {
        var format = new StringFormat(StringFormatFlags.MeasureTrailingSpaces |
            (rtl ? StringFormatFlags.DirectionRightToLeft : 0)) { Trimming = StringTrimming.None };
        format.SetDigitSubstitution(0, StringDigitSubstitute.None);
        return format;
    }
}
