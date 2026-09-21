using System.Diagnostics;
using System.Globalization;
using System.Text;
using ProGPU.Backend.Native;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Text.Shaping;

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
        NativeTextDirection nativeDirection = ReadDirection(args);
        ShapingDirection managedDirection = nativeDirection switch
        {
            NativeTextDirection.Unspecified => ShapingDirection.Unspecified,
            NativeTextDirection.LeftToRight => ShapingDirection.LeftToRight,
            NativeTextDirection.RightToLeft => ShapingDirection.RightToLeft,
            NativeTextDirection.TopToBottom => ShapingDirection.TopToBottom,
            NativeTextDirection.BottomToTop => ShapingDirection.BottomToTop,
            _ => throw new InvalidOperationException("Unsupported benchmark text direction.")
        };
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
        TextShapingOptions shapingOptions = (disabledFeature is null
            ? TextShapingOptions.Default
            : TextShapingOptions.WithFeatures(
                new OpenTypeFeatureSetting(disabledFeature, 0)))
            .WithDirection(managedDirection);
        NativeTextScalar[] scalars = Decode(text);
        var input = new NativeTextShapeInput(
            font.FontData.Span,
            scalars,
            direction: nativeDirection,
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
        int paragraphLevelOverride = nativeDirection == NativeTextDirection.RightToLeft ? 1 :
            nativeDirection == NativeTextDirection.LeftToRight ? 0 : -1;
        EnsureSuccess(
            NativeTextBidiInterop.Resolve(
                scalars,
                paragraphLevelOverride,
                bidiLevels,
                bidiScratch,
                out NativeTextBidiResult bidiResult),
            (NativeTextUnicodeError)bidiResult.ErrorCode);
        ValidateBidiLevels(
            bidiLevels.AsSpan(0, checked((int)bidiResult.LevelCount)),
            bidiResult,
            nativeDirection == NativeTextDirection.RightToLeft ? (sbyte)1 : (sbyte)0);
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
            new TextLayout(text, font, fontSize, shapingOptions: shapingOptions),
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
        if (dumpGlyphs)
        {
            DumpParagraphGlyphs(
                paragraphGlyphs.AsSpan(
                    0,
                    checked((int)paragraphResult.GlyphCount)));
            DumpInteraction(NativeTextParagraphSnapshot.Create(
                nativeContext,
                text,
                nativeDirection,
                in paragraphOptions,
                nativeFeatures));
        }
        ValidateParagraphParity(
            new TextLayout(text, font, fontSize, shapingOptions: shapingOptions),
            paragraphGlyphs.AsSpan(0, checked((int)paragraphResult.GlyphCount)),
            paragraphResult,
            nativeDirection == NativeTextDirection.RightToLeft ? (sbyte)1 : (sbyte)0);
        NativeTextParagraphSnapshot interactionSnapshot = NativeTextParagraphSnapshot.Create(
            nativeContext,
            text,
            nativeDirection,
            in paragraphOptions,
            nativeFeatures);
        var interactionInput = new NativeTextInteractionInput(
            interactionSnapshot.Glyphs.Span,
            interactionSnapshot.Lines.Span,
            interactionSnapshot.ClusterEnds.Span,
            interactionSnapshot.BidiLevels.Span);
        EnsureSuccess(
            NativeTextInteractionInterop.GetRequirements(
                in interactionInput,
                out NativeTextInteractionRequirements interactionRequirements),
            NativeTextFontError.None);
        var interactionOrigins = new float[interactionSnapshot.Lines.Length];
        var interactionBoxes = new NativeTextClusterBox[
            checked((int)interactionRequirements.ClusterBoxCapacity)];
        var interactionCarets = new NativeTextCaretStop[
            checked((int)interactionRequirements.CaretStopCapacity)];
        EnsureSuccess(
            NativeTextInteractionInterop.BuildAdvance(
                in interactionInput,
                interactionOrigins,
                interactionBoxes,
                interactionCarets,
                out NativeTextInteractionResult interactionResult),
            NativeTextFontError.None);
        if (interactionResult.ClusterBoxCount != interactionSnapshot.Boxes.Length ||
            interactionResult.CaretStopCount != interactionSnapshot.Carets.Length)
        {
            throw new InvalidOperationException(
                "Advance interaction output count differs from the retained snapshot.");
        }

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
            _ = new TextLayout(text, font, fontSize, shapingOptions: shapingOptions);
            EnsureSuccess(
                nativeContext.LayoutParagraph(
                    input,
                    paragraphOptions,
                    paragraphGlyphs,
                    paragraphLines,
                    paragraphScratch,
                    out paragraphResult),
                (NativeTextFontError)paragraphResult.ErrorCode);
            EnsureSuccess(
                NativeTextInteractionInterop.BuildAdvance(
                    in interactionInput,
                    interactionOrigins,
                    interactionBoxes,
                    interactionCarets,
                    out interactionResult),
                NativeTextFontError.None);
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
        long[] interactionSamples = new long[iterations];
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
                    paragraphLevelOverride,
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
            _ = new TextLayout(text, font, fontSize, shapingOptions: shapingOptions);
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
        long interactionAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long start = Stopwatch.GetTimestamp();
            EnsureSuccess(
                NativeTextInteractionInterop.BuildAdvance(
                    in interactionInput,
                    interactionOrigins,
                    interactionBoxes,
                    interactionCarets,
                    out interactionResult),
                NativeTextFontError.None);
            interactionSamples[index] = Stopwatch.GetTimestamp() - start;
        }
        long interactionAllocations =
            GC.GetAllocatedBytesForCurrentThread() - interactionAllocationStart;
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
        Array.Sort(interactionSamples);
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
        Print(
            "C++ interact",
            interactionSamples,
            iterations,
            interactionAllocations);
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
        NativeTextBidiResult result,
        sbyte expectedLevel)
    {
        if (result.ParagraphLevel != expectedLevel || result.LevelCount != levels.Length)
        {
            throw new InvalidOperationException("Unexpected native bidi paragraph result.");
        }
        for (int index = 0; index < levels.Length; index++)
        {
            if (levels[index].InputIndex != (uint)index ||
                levels[index].InputLength != 1 || levels[index].Level != expectedLevel ||
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
        NativeTextParagraphResult result,
        sbyte expectedParagraphLevel)
    {
        if (result.ParagraphLevel != expectedParagraphLevel || result.LineCount != 1 ||
            managed.Glyphs.Count != native.Length ||
            MathF.Abs(managed.ContentSize.X - result.ContentWidth) > 0.001f ||
            MathF.Abs(managed.ContentSize.Y - result.ContentHeight) > 0.001f ||
            MathF.Abs(managed.MeasuredSize.X - result.MeasuredWidth) > 0.001f ||
            MathF.Abs(managed.MeasuredSize.Y - result.MeasuredHeight) > 0.001f)
        {
            throw new InvalidOperationException(
                $"Managed/native paragraph metrics differ: " +
                $"managed content={managed.ContentSize}, measured={managed.MeasuredSize}, " +
                $"native content=({result.ContentWidth},{result.ContentHeight}), " +
                $"measured=({result.MeasuredWidth},{result.MeasuredHeight}), " +
                $"lines={result.LineCount}, glyphs={native.Length}/{managed.Glyphs.Count}.");
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

    private static void DumpParagraphGlyphs(
        ReadOnlySpan<NativePositionedTextGlyph> glyphs)
    {
        Console.WriteLine("paragraph index | glyph | cluster | position | advance | logical glyph | font");
        for (int index = 0; index < glyphs.Length; index++)
        {
            NativePositionedTextGlyph glyph = glyphs[index];
            Console.WriteLine(
                $"{index,15} | {glyph.GlyphId} | {glyph.Cluster} | " +
                $"({glyph.X},{glyph.Y}) | ({glyph.AdvanceX},{glyph.AdvanceY}) | " +
                $"{glyph.GlyphIndex} | {glyph.FontIndex}");
        }
    }

    private static void DumpInteraction(NativeTextParagraphSnapshot snapshot)
    {
        Console.WriteLine("cluster | source | level | bounds");
        foreach (NativeTextClusterBox box in snapshot.Boxes.Span)
        {
            Console.WriteLine(
                $"{box.InputStart} | [{box.InputStart},{box.InputEnd}) | {box.BidiLevel} | " +
                $"({box.X},{box.Y},{box.Width},{box.Height})");
        }
        Console.WriteLine("caret | source | trailing | level | position");
        int index = 0;
        foreach (NativeTextCaretStop caret in snapshot.Carets.Span)
        {
            Console.WriteLine(
                $"{index++} | {caret.InputPosition} | {caret.Trailing} | " +
                $"{caret.BidiLevel} | ({caret.X},{caret.Y},{caret.Height})");
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
        double p99 = samples[(int)Math.Floor((iterations - 1) * 0.99)] * tickToMicroseconds;
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0,-12}: median={1:F3} us, p95={2:F3} us, p99={3:F3} us, managed allocations={4:F1} bytes/run",
            name,
            median,
            p95,
            p99,
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

    private static NativeTextDirection ReadDirection(string[] args)
    {
        string? value = ReadOptional(args, "--direction");
        return value?.ToLowerInvariant() switch
        {
            null or "unspecified" => NativeTextDirection.Unspecified,
            "ltr" or "left-to-right" => NativeTextDirection.LeftToRight,
            "rtl" or "right-to-left" => NativeTextDirection.RightToLeft,
            "ttb" or "top-to-bottom" => NativeTextDirection.TopToBottom,
            "btt" or "bottom-to-top" => NativeTextDirection.BottomToTop,
            _ => throw new ArgumentException(
                "--direction requires unspecified, ltr, rtl, ttb, or btt.")
        };
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
