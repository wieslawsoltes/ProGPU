using System.Diagnostics;
using System.Globalization;
using System.Text;
using ProGPU.Backend.Native;
using ProGPU.Fonts.Inter;
using ProGPU.Text;

internal static class NativeTextShapingBenchmark
{
    private const string Sample =
        "AVATAR office affine 1/2 Typography WebGPU native shaping parity ";

    public static void Run(string[] args)
    {
        int warmups = ReadPositive(args, "--warmup", 100);
        int iterations = ReadPositive(args, "--iterations", 2_000);
        int repeats = ReadPositive(args, "--text-repeats", 8);
        string text = string.Concat(Enumerable.Repeat(Sample, repeats));
        TtfFont font = InterFontFamily.Regular;
        NativeTextScalar[] scalars = Decode(text);
        var input = new NativeTextShapeInput(
            font.FontData.Span,
            scalars,
            direction: NativeTextDirection.Unspecified);
        using var nativeContext = new NativeTextShapingContext(font.FontData.Span);

        NativeRendererStatus requirementsStatus =
            nativeContext.GetRequirements(input, out var requirements);
        EnsureSuccess(requirementsStatus, (NativeTextFontError)requirements.ErrorCode);
        var nativeGlyphs = new NativeTextShapingGlyph[requirements.GlyphCapacity];
        var scratch = new byte[checked((int)requirements.ScratchBytes)];
        NativeRendererStatus shapeStatus = nativeContext.Shape(
            input,
            nativeGlyphs,
            scratch,
            out var nativeResult);
        EnsureSuccess(shapeStatus, (NativeTextFontError)nativeResult.ErrorCode);

        IReadOnlyList<ShapedGlyph> managed = OpenTypeTextShaper.Shape(
            text,
            font,
            font.UnitsPerEm);
        ValidateParity(managed, nativeGlyphs.AsSpan(0, checked((int)nativeResult.GlyphCount)));

        float fontSize = 16f;
        float scale = fontSize / font.UnitsPerEm;
        float lineHeight = (font.Ascender - font.Descender + font.LineGap) * scale;
        var breaks = new NativeTextLineBreakKind[nativeResult.GlyphCount];
        breaks[^1] = NativeTextLineBreakKind.Mandatory;
        var layoutInput = new NativeTextLayoutInput(
            nativeGlyphs.AsSpan(0, checked((int)nativeResult.GlyphCount)),
            breaks,
            scale,
            lineHeight: lineHeight);
        EnsureSuccess(
            NativeTextLayoutInterop.GetRequirements(
                layoutInput,
                out NativeTextLayoutRequirements layoutRequirements),
            (NativeTextFontError)layoutRequirements.ErrorCode);
        var positioned = new NativePositionedTextGlyph[layoutRequirements.GlyphCapacity];
        var lines = new NativePositionedTextLine[layoutRequirements.LineCapacity];
        var layoutScratch = new byte[checked((int)layoutRequirements.ScratchBytes)];
        EnsureSuccess(
            NativeTextLayoutInterop.Layout(
                layoutInput,
                positioned,
                lines,
                layoutScratch,
                out NativeTextLayoutResult layoutResult),
            (NativeTextFontError)layoutResult.ErrorCode);
        ValidateLayoutParity(
            new TextLayout(text, font, fontSize),
            positioned.AsSpan(0, checked((int)layoutResult.GlyphCount)),
            layoutResult);

        for (int index = 0; index < warmups; index++)
        {
            _ = OpenTypeTextShaper.Shape(text, font, font.UnitsPerEm);
            EnsureSuccess(
                nativeContext.Shape(
                    input,
                    nativeGlyphs,
                    scratch,
                    out nativeResult),
                (NativeTextFontError)nativeResult.ErrorCode);
            EnsureSuccess(
                NativeTextLayoutInterop.Layout(
                    layoutInput,
                    positioned,
                    lines,
                    layoutScratch,
                    out layoutResult),
                (NativeTextFontError)layoutResult.ErrorCode);
        }

        long[] managedSamples = new long[iterations];
        long[] nativeSamples = new long[iterations];
        long[] layoutSamples = new long[iterations];
        long managedAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            managed = OpenTypeTextShaper.Shape(text, font, font.UnitsPerEm);
            managedSamples[index] = Stopwatch.GetTimestamp() - start;
        }
        long managedAllocations =
            GC.GetAllocatedBytesForCurrentThread() - managedAllocationStart;

        long nativeAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            EnsureSuccess(
                nativeContext.Shape(
                    input,
                    nativeGlyphs,
                    scratch,
                    out nativeResult),
                (NativeTextFontError)nativeResult.ErrorCode);
            nativeSamples[index] = Stopwatch.GetTimestamp() - start;
        }
        long nativeAllocations =
            GC.GetAllocatedBytesForCurrentThread() - nativeAllocationStart;
        long layoutAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            EnsureSuccess(
                NativeTextLayoutInterop.Layout(
                    layoutInput,
                    positioned,
                    lines,
                    layoutScratch,
                    out layoutResult),
                (NativeTextFontError)layoutResult.ErrorCode);
            layoutSamples[index] = Stopwatch.GetTimestamp() - start;
        }
        long layoutAllocations =
            GC.GetAllocatedBytesForCurrentThread() - layoutAllocationStart;
        ValidateParity(
            managed,
            nativeGlyphs.AsSpan(0, checked((int)nativeResult.GlyphCount)));

        Array.Sort(managedSamples);
        Array.Sort(nativeSamples);
        Array.Sort(layoutSamples);
        Console.WriteLine("ProGPU managed/C++ text shaping/layout parity: PASS");
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Input: UTF-16={0}, scalars={1}, glyphs={2}, native scratch={3:N0} bytes, crossings=1/run",
            text.Length,
            scalars.Length,
            nativeResult.GlyphCount,
            requirements.ScratchBytes));
        Print("Managed", managedSamples, iterations, managedAllocations);
        Print("C++ bulk", nativeSamples, iterations, nativeAllocations);
        Print("C++ layout", layoutSamples, iterations, layoutAllocations);
    }

    private static void ValidateLayoutParity(
        TextLayout managed,
        ReadOnlySpan<NativePositionedTextGlyph> native,
        NativeTextLayoutResult result)
    {
        if (managed.Glyphs.Count != native.Length || result.LineCount != 1 ||
            MathF.Abs(managed.ContentSize.X - result.ContentWidth) > 0.001f ||
            MathF.Abs(managed.ContentSize.Y - result.ContentHeight) > 0.001f ||
            MathF.Abs(managed.MeasuredSize.X - result.MeasuredWidth) > 0.001f ||
            MathF.Abs(managed.MeasuredSize.Y - result.MeasuredHeight) > 0.001f)
        {
            throw new InvalidOperationException("Managed/native text layout metrics differ.");
        }
        for (int index = 0; index < native.Length; index++)
        {
            TextRunGlyph left = managed.Glyphs[index];
            NativePositionedTextGlyph right = native[index];
            if (left.GlyphIndex != right.GlyphId ||
                left.Cluster != right.Cluster ||
                MathF.Abs(left.Position.X - right.X) > 0.001f ||
                MathF.Abs(left.Glyph.Advance - right.AdvanceX) > 0.001f)
            {
                throw new InvalidOperationException(
                    $"Text layout parity mismatch at glyph {index}.");
            }
        }
    }

    private static NativeTextScalar[] Decode(string text)
    {
        var result = new NativeTextScalar[text.EnumerateRunes().Count()];
        int scalarIndex = 0;
        int inputIndex = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int length = rune.Utf16SequenceLength;
            result[scalarIndex++] = new NativeTextScalar(
                checked((uint)rune.Value),
                checked((uint)inputIndex),
                checked((ushort)length));
            inputIndex += length;
        }
        return result;
    }

    private static void ValidateParity(
        IReadOnlyList<ShapedGlyph> managed,
        ReadOnlySpan<NativeTextShapingGlyph> native)
    {
        if (managed.Count != native.Length)
        {
            throw new InvalidOperationException(
                $"Text parity count mismatch: managed={managed.Count}, native={native.Length}.");
        }
        for (int index = 0; index < native.Length; index++)
        {
            ShapedGlyph left = managed[index];
            NativeTextShapingGlyph right = native[index];
            if (left.GlyphIndex != right.GlyphId ||
                left.CodePoint != right.CodePoint ||
                left.Cluster != right.Cluster ||
                (uint)left.Flags != right.Flags ||
                left.AdvanceX != right.AdvanceX ||
                left.AdvanceY != right.AdvanceY ||
                left.OffsetX != right.OffsetX ||
                left.OffsetY != right.OffsetY)
            {
                throw new InvalidOperationException(
                    $"Text parity mismatch at glyph {index}: managed={left}, " +
                    $"native=({right.GlyphId},{right.CodePoint},{right.Cluster}," +
                    $"{right.AdvanceX},{right.AdvanceY},{right.OffsetX},{right.OffsetY}," +
                    $"{right.Flags}).");
            }
        }
    }

    private static void EnsureSuccess(
        NativeRendererStatus status,
        NativeTextFontError error)
    {
        if (status != NativeRendererStatus.Success)
        {
            throw new InvalidOperationException(
                $"Native text shaping failed: status={status}, error={error}.");
        }
    }

    private static void Print(
        string name,
        long[] samples,
        int iterations,
        long allocations)
    {
        double tickToMicroseconds = 1_000_000d / Stopwatch.Frequency;
        double median = samples[iterations / 2] * tickToMicroseconds;
        double p95 = samples[(int)Math.Floor((iterations - 1) * 0.95)] * tickToMicroseconds;
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0,-10}: median={1:F3} us, p95={2:F3} us, managed allocations={3:F1} bytes/run",
            name,
            median,
            p95,
            allocations / (double)iterations));
    }

    private static int ReadPositive(string[] args, string name, int fallback)
    {
        int index = Array.FindIndex(
            args,
            value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return fallback;
        if (index + 1 >= args.Length ||
            !int.TryParse(args[index + 1], out int value) || value <= 0)
        {
            throw new ArgumentException($"{name} requires a positive integer.");
        }
        return value;
    }
}
