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
        bool profileNativeOnly = HasFlag(args, "--profile-native-only");
        bool shapeOnly = HasFlag(args, "--shape-only");
        bool directShape = HasFlag(args, "--direct-shape");
        bool dumpGlyphs = HasFlag(args, "--dump-glyphs");
        string? requestedText = ReadOptional(args, "--text");
        string? fontPath = ReadOptional(args, "--font-path");
        string? disabledFeature = ReadOptional(args, "--disable-feature");
        if (disabledFeature is not null && !shapeOnly)
        {
            throw new ArgumentException(
                "--disable-feature requires --shape-only.");
        }
        string text = requestedText ??
            string.Concat(Enumerable.Repeat(Sample, repeats));
        TtfFont font = fontPath is null
            ? InterFontFamily.Regular
            : new TtfFont(Path.GetFullPath(fontPath));
        NativeTextFeature[] nativeFeatures = disabledFeature is null
            ? []
            : [new NativeTextFeature(ParseTag(disabledFeature), 0U)];
        TextShapingOptions shapingOptions = disabledFeature is null
            ? TextShapingOptions.Default
            : TextShapingOptions.WithFeatures(
                new OpenTypeFeatureSetting(disabledFeature, 0));
        NativeTextScalar[] scalars = Decode(text);
        var input = new NativeTextShapeInput(
            font.FontData.Span,
            scalars,
            direction: NativeTextDirection.Unspecified,
            features: nativeFeatures);
        using var nativeContext = new NativeTextShapingContext(font.FontData.Span);

        NativeRendererStatus requirementsStatus = directShape
            ? NativeTextShapingInterop.GetRequirements(input, out var requirements)
            : nativeContext.GetRequirements(input, out requirements);
        EnsureSuccess(requirementsStatus, (NativeTextFontError)requirements.ErrorCode);
        var nativeGlyphs = new NativeTextShapingGlyph[requirements.GlyphCapacity];
        var scratch = new byte[checked((int)requirements.ScratchBytes)];
        NativeRendererStatus shapeStatus = directShape
            ? NativeTextShapingInterop.Shape(
                input,
                nativeGlyphs,
                scratch,
                out var nativeResult)
            : nativeContext.Shape(
                input,
                nativeGlyphs,
                scratch,
                out nativeResult);
        EnsureSuccess(shapeStatus, (NativeTextFontError)nativeResult.ErrorCode);

        IReadOnlyList<ShapedGlyph> managed = OpenTypeTextShaper.Shape(
            text,
            font,
            font.UnitsPerEm,
            shapingOptions);
        if (dumpGlyphs)
        {
            DumpGlyphs(
                managed,
                nativeGlyphs.AsSpan(
                    0,
                    checked((int)nativeResult.GlyphCount)));
        }
        ValidateParity(managed, nativeGlyphs.AsSpan(0, checked((int)nativeResult.GlyphCount)));

        if (directShape)
        {
            Console.WriteLine("ProGPU managed/direct-C++ text shaping parity: PASS");
            return;
        }

        if (shapeOnly)
        {
            RunShapeOnlyBenchmark(
                text,
                font,
                shapingOptions,
                nativeContext,
                in input,
                nativeGlyphs,
                scratch,
                warmups,
                iterations,
                profileNativeOnly);
            return;
        }

        float fontSize = 16f;
        float scale = fontSize / font.UnitsPerEm;
        float lineHeight = (font.Ascender - font.Descender + font.LineGap) * scale;
        EnsureSuccess(
            NativeTextLineBreakInterop.GetRequirements(
                scalars,
                out NativeTextLineBreakRequirements breakRequirements),
            (NativeTextUnicodeError)breakRequirements.ErrorCode);
        var breaks = new NativeTextLineBreakKind[breakRequirements.BreakCapacity];
        var breakScratch = new byte[checked((int)breakRequirements.ScratchBytes)];
        EnsureSuccess(
            NativeTextLineBreakInterop.Resolve(
                scalars,
                breaks,
                breakScratch,
                out NativeTextLineBreakResult breakResult),
            (NativeTextUnicodeError)breakResult.ErrorCode);
        if (breakResult.BreakCount != nativeResult.GlyphCount)
        {
            throw new InvalidOperationException(
                "The benchmark requires one shaped glyph per decoded scalar.");
        }
        EnsureSuccess(
            NativeTextBidiInterop.GetRequirements(
                scalars,
                out NativeTextBidiRequirements bidiRequirements),
            (NativeTextUnicodeError)bidiRequirements.ErrorCode);
        var bidiLevels = new NativeTextBidiLevel[bidiRequirements.LevelCapacity];
        var bidiScratch = new byte[checked((int)bidiRequirements.ScratchBytes)];
        EnsureSuccess(
            NativeTextBidiInterop.Resolve(
                scalars,
                -1,
                bidiLevels,
                bidiScratch,
                out NativeTextBidiResult bidiResult),
            (NativeTextUnicodeError)bidiResult.ErrorCode);
        ValidateBidiLevels(
            bidiLevels.AsSpan(0, checked((int)bidiResult.LevelCount)),
            bidiResult);
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
        var paragraphOptions = new NativeTextParagraphOptions(
            scale,
            LineHeight: lineHeight);
        EnsureSuccess(
            nativeContext.GetParagraphRequirements(
                input,
                paragraphOptions,
                out NativeTextParagraphRequirements paragraphRequirements),
            (NativeTextFontError)paragraphRequirements.ErrorCode);
        var paragraphGlyphs =
            new NativePositionedTextGlyph[paragraphRequirements.GlyphCapacity];
        var paragraphLines =
            new NativePositionedTextLine[paragraphRequirements.LineCapacity];
        var paragraphScratch =
            new byte[checked((int)paragraphRequirements.ScratchBytes)];
        EnsureSuccess(
            nativeContext.LayoutParagraph(
                input,
                paragraphOptions,
                paragraphGlyphs,
                paragraphLines,
                paragraphScratch,
                out NativeTextParagraphResult paragraphResult),
            (NativeTextFontError)paragraphResult.ErrorCode);
        ValidateParagraphParity(
            new TextLayout(text, font, fontSize),
            paragraphGlyphs.AsSpan(0, checked((int)paragraphResult.GlyphCount)),
            paragraphResult);

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
            _ = new TextLayout(text, font, fontSize);
            EnsureSuccess(
                nativeContext.LayoutParagraph(
                    input,
                    paragraphOptions,
                    paragraphGlyphs,
                    paragraphLines,
                    paragraphScratch,
                    out paragraphResult),
                (NativeTextFontError)paragraphResult.ErrorCode);
        }

        if (profileNativeOnly)
        {
            long start = Stopwatch.GetTimestamp();
            for (int index = 0; index < iterations; index++)
            {
                EnsureSuccess(
                    nativeContext.Shape(
                        input,
                        nativeGlyphs,
                        scratch,
                        out nativeResult),
                    (NativeTextFontError)nativeResult.ErrorCode);
            }
            double elapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "C++ native-only profile: iterations={0:N0}, elapsed={1:F3} s, mean={2:F3} us",
                iterations,
                elapsedSeconds,
                elapsedSeconds * 1_000_000d / iterations));
            return;
        }

        long[] managedSamples = new long[iterations];
        long[] nativeSamples = new long[iterations];
        long[] layoutSamples = new long[iterations];
        long[] breakSamples = new long[iterations];
        long[] bidiSamples = new long[iterations];
        long[] managedParagraphSamples = new long[iterations];
        long[] nativeParagraphSamples = new long[iterations];
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
        long breakAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            EnsureSuccess(
                NativeTextLineBreakInterop.Resolve(
                    scalars,
                    breaks,
                    breakScratch,
                    out breakResult),
                (NativeTextUnicodeError)breakResult.ErrorCode);
            breakSamples[index] = Stopwatch.GetTimestamp() - start;
        }
        long breakAllocations =
            GC.GetAllocatedBytesForCurrentThread() - breakAllocationStart;
        long bidiAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            EnsureSuccess(
                NativeTextBidiInterop.Resolve(
                    scalars,
                    -1,
                    bidiLevels,
                    bidiScratch,
                    out bidiResult),
                (NativeTextUnicodeError)bidiResult.ErrorCode);
            bidiSamples[index] = Stopwatch.GetTimestamp() - start;
        }
        long bidiAllocations =
            GC.GetAllocatedBytesForCurrentThread() - bidiAllocationStart;
        long managedParagraphAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            _ = new TextLayout(text, font, fontSize);
            managedParagraphSamples[index] = Stopwatch.GetTimestamp() - start;
        }
        long managedParagraphAllocations =
            GC.GetAllocatedBytesForCurrentThread() - managedParagraphAllocationStart;
        long nativeParagraphAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            EnsureSuccess(
                nativeContext.LayoutParagraph(
                    input,
                    paragraphOptions,
                    paragraphGlyphs,
                    paragraphLines,
                    paragraphScratch,
                    out paragraphResult),
                (NativeTextFontError)paragraphResult.ErrorCode);
            nativeParagraphSamples[index] = Stopwatch.GetTimestamp() - start;
        }
        long nativeParagraphAllocations =
            GC.GetAllocatedBytesForCurrentThread() - nativeParagraphAllocationStart;
        ValidateParity(
            managed,
            nativeGlyphs.AsSpan(0, checked((int)nativeResult.GlyphCount)));

        Array.Sort(managedSamples);
        Array.Sort(nativeSamples);
        Array.Sort(layoutSamples);
        Array.Sort(breakSamples);
        Array.Sort(bidiSamples);
        Array.Sort(managedParagraphSamples);
        Array.Sort(nativeParagraphSamples);
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
        Print("C++ breaks", breakSamples, iterations, breakAllocations);
        Print("C++ bidi", bidiSamples, iterations, bidiAllocations);
        Print(
            "Managed para",
            managedParagraphSamples,
            iterations,
            managedParagraphAllocations);
        Print(
            "C++ para",
            nativeParagraphSamples,
            iterations,
            nativeParagraphAllocations);
    }

    private static void RunShapeOnlyBenchmark(
        string text,
        TtfFont font,
        TextShapingOptions shapingOptions,
        NativeTextShapingContext nativeContext,
        in NativeTextShapeInput input,
        NativeTextShapingGlyph[] nativeGlyphs,
        byte[] scratch,
        int warmups,
        int iterations,
        bool profileNativeOnly)
    {
        NativeTextShapeResult nativeResult = default;
        for (int index = 0; index < warmups; index++)
        {
            _ = OpenTypeTextShaper.Shape(
                text, font, font.UnitsPerEm, shapingOptions);
            EnsureSuccess(
                nativeContext.Shape(
                    input,
                    nativeGlyphs,
                    scratch,
                    out nativeResult),
                (NativeTextFontError)nativeResult.ErrorCode);
        }

        if (profileNativeOnly)
        {
            long start = Stopwatch.GetTimestamp();
            for (int index = 0; index < iterations; index++)
            {
                EnsureSuccess(
                    nativeContext.Shape(
                        input,
                        nativeGlyphs,
                        scratch,
                        out nativeResult),
                    (NativeTextFontError)nativeResult.ErrorCode);
            }
            double elapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "C++ native-only profile: iterations={0:N0}, elapsed={1:F3} s, mean={2:F3} us",
                iterations,
                elapsedSeconds,
                elapsedSeconds * 1_000_000d / iterations));
            return;
        }

        var managedSamples = new long[iterations];
        var nativeSamples = new long[iterations];
        long managedAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<ShapedGlyph> managed = [];
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            managed = OpenTypeTextShaper.Shape(
                text, font, font.UnitsPerEm, shapingOptions);
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
        ValidateParity(
            managed,
            nativeGlyphs.AsSpan(0, checked((int)nativeResult.GlyphCount)));

        Array.Sort(managedSamples);
        Array.Sort(nativeSamples);
        Console.WriteLine("ProGPU managed/C++ text shaping parity: PASS");
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Input: UTF-16={0}, scalars={1}, glyphs={2}, crossings=1/run",
            text.Length,
            input.Input.Length,
            nativeResult.GlyphCount));
        Print("Managed", managedSamples, iterations, managedAllocations);
        Print("C++ bulk", nativeSamples, iterations, nativeAllocations);
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

    private static void ValidateBidiLevels(
        ReadOnlySpan<NativeTextBidiLevel> levels,
        NativeTextBidiResult result)
    {
        if (result.ParagraphLevel != 0 || result.LevelCount != levels.Length)
        {
            throw new InvalidOperationException("Unexpected native bidi paragraph result.");
        }
        for (int index = 0; index < levels.Length; index++)
        {
            if (levels[index].InputIndex != (uint)index ||
                levels[index].InputLength != 1 || levels[index].Level != 0 ||
                levels[index].Reserved != 0)
            {
                throw new InvalidOperationException(
                    $"Unexpected native bidi level at scalar {index}.");
            }
        }
    }

    private static void ValidateParagraphParity(
        TextLayout managed,
        ReadOnlySpan<NativePositionedTextGlyph> native,
        NativeTextParagraphResult result)
    {
        if (result.ParagraphLevel != 0 || result.LineCount != 1 ||
            managed.Glyphs.Count != native.Length ||
            MathF.Abs(managed.ContentSize.X - result.ContentWidth) > 0.001f ||
            MathF.Abs(managed.ContentSize.Y - result.ContentHeight) > 0.001f ||
            MathF.Abs(managed.MeasuredSize.X - result.MeasuredWidth) > 0.001f ||
            MathF.Abs(managed.MeasuredSize.Y - result.MeasuredHeight) > 0.001f)
        {
            throw new InvalidOperationException(
                "Managed/native paragraph metrics differ.");
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
                    $"Paragraph parity mismatch at glyph {index}.");
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

    private static void DumpGlyphs(
        IReadOnlyList<ShapedGlyph> managed,
        ReadOnlySpan<NativeTextShapingGlyph> native)
    {
        Console.WriteLine("index | managed | native");
        int count = Math.Max(managed.Count, native.Length);
        for (int index = 0; index < count; index++)
        {
            string managedValue = index < managed.Count
                ? managed[index].ToString() ?? string.Empty
                : "<missing>";
            string nativeValue;
            if (index < native.Length)
            {
                NativeTextShapingGlyph glyph = native[index];
                nativeValue =
                    $"Glyph={glyph.GlyphId}, Cluster={glyph.Cluster}, " +
                    $"CodePoint={glyph.CodePoint}, Advance=({glyph.AdvanceX}," +
                    $"{glyph.AdvanceY}), Offset=({glyph.OffsetX},{glyph.OffsetY}), " +
                    $"Flags={glyph.Flags}";
            }
            else
            {
                nativeValue = "<missing>";
            }
            Console.WriteLine($"{index,5} | {managedValue} | {nativeValue}");
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

    private static void EnsureSuccess(
        NativeRendererStatus status,
        NativeTextUnicodeError error)
    {
        if (status != NativeRendererStatus.Success)
        {
            throw new InvalidOperationException(
                $"Native Unicode operation failed: status={status}, error={error}.");
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

    private static bool HasFlag(string[] args, string name) =>
        Array.Exists(
            args,
            value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOptional(string[] args, string name)
    {
        int index = Array.FindIndex(
            args,
            value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }
        if (index + 1 >= args.Length || string.IsNullOrEmpty(args[index + 1]))
        {
            throw new ArgumentException($"{name} requires a non-empty value.");
        }
        return args[index + 1];
    }

    private static uint ParseTag(string value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException(
                "OpenType feature tags must contain four characters.");
        }
        uint result = 0U;
        foreach (char character in value)
        {
            if (character is < (char)0x20 or > (char)0x7e)
            {
                throw new ArgumentException(
                    "OpenType feature tags must contain printable ASCII.");
            }
            result = result << 8 | character;
        }
        return result;
    }
}
