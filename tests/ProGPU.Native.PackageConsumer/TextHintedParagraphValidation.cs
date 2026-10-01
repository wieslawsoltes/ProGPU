using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Backend.Native;

internal static unsafe class TextHintedParagraphValidation
{
    internal static void Run(string fontPath)
    {
        int cases = 0;
        bool observedLineReset = false;
        foreach (var policy in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
        foreach (var direction in new[] { NativeTextDirection.LeftToRight, NativeTextDirection.RightToLeft })
        foreach (var fixture in new[]
        {
            (Text: "AVA A A A A A ", Hard: false, Digits: false, SourceBidi: false),
            (Text: "A\U0001f642B \u05d0\u05d1 A  ", Hard: false, Digits: false, SourceBidi: false),
            (Text: "A\nB\rA\r\nB", Hard: true, Digits: false, SourceBidi: false),
            (Text: "A12 A34 ", Hard: false, Digits: true, SourceBidi: false),
            (Text: "A12 A34 ", Hard: false, Digits: true, SourceBidi: true),
        })
        {
            byte[] font = File.ReadAllBytes(fontPath);
            using var context = new NativeTextShapingContext(font);
            Check(context.AddFallbackFont(font, out uint second, identity: 0x7712) == NativeRendererStatus.Success && second == 1,
                "second original owned font source");
            Array.Clear(font);
            NativeTextScalar[] source = Decode(fixture.Text);
            NativeTextScalar[] original = source.ToArray();
            uint digitPolicy = fixture.Digits ? 0x0660U | (fixture.SourceBidi ? 0x40000000U : 0U) : 0U;
            NativeTextFeature[] features = [new() { Tag = 0x6B65726E, Value = 0, Start = 0, End = uint.MaxValue }];
            NativeTextStyleRun[] styles =
            [
                new() { ScalarStart = 0, ScalarCount = 2, FontIndex = 0, Scale = 13f / 2048,
                    FeatureCount = 1, DigitSubstitution = digitPolicy },
                new() { ScalarStart = 2, ScalarCount = checked((uint)source.Length - 2), FontIndex = 1,
                    Scale = 17f / 2048, FeatureCount = 1, DigitSubstitution = digitPolicy },
            ];
            NativeTextStyleMetrics[] metrics = [new() { Ascent = 10, Descent = 3 }, new() { Ascent = 14, Descent = 4 }];
            NativeHintedParagraphDeviceStyle[] devices =
            [
                new() { FontIndex = 0, SourceScale = styles[0].Scale, LogicalUnitsPerPhysicalPixel = 1,
                    XPixelsPerEm266 = 13 * 64, YPixelsPerEm266 = 13 * 64, Interpreter = (uint)policy },
                new() { FontIndex = 1, SourceScale = styles[1].Scale, LogicalUnitsPerPhysicalPixel = 1,
                    XPixelsPerEm266 = 17 * 64, YPixelsPerEm266 = 17 * 64, Interpreter = (uint)policy,
                    XPhase266 = 7, YPhase266 = 11 },
            ];
            var input = new NativeTextShapeInput([], source, direction: direction, features: features);
            var options = new NativeTextParagraphOptions(1, fixture.Hard ? 0 : 25, Alignment: NativeTextAlignment.Center);
            using var paragraph = context.LayoutHintedParagraph(in input, in options, styles, metrics, devices);
            VerifySources(paragraph, original, styles, metrics, fixture.Digits, fixture.SourceBidi, direction);
            VerifyOwners(paragraph, original);
            VerifyInteraction(paragraph);
            // The source-facing UTF-16 overload must retain the exact original
            // scalar/style transport, not introduce a second shaping result.
            int styleBoundary = checked((int)original[2].InputIndex);
            NativeTextParagraphStyle[] utf16Styles =
            [
                new(0, styleBoundary, 0, styles[0].Scale, FeatureCount: 1,
                    DigitZero: fixture.Digits ? 0x0660U : 0U, PreserveSourceDigitBidi: fixture.SourceBidi),
                new(styleBoundary, fixture.Text.Length - styleBoundary, 1, styles[1].Scale, FeatureCount: 1,
                    DigitZero: fixture.Digits ? 0x0660U : 0U, PreserveSourceDigitBidi: fixture.SourceBidi),
            ];
            using (var utf16 = context.LayoutHintedParagraph(fixture.Text.AsSpan(), direction,
                in options, utf16Styles, metrics, devices, features))
                EqualSnapshot(Snapshot(paragraph), utf16);
            observedLineReset |= VerifyLineFrames(paragraph, direction);
            if (fixture.Text == "AVA A A A A A ") VerifyOriginalRuns(context, paragraph, original, features, devices, policy);
            if (fixture.Hard)
            {
                Check(paragraph.Lines.Length == 4, "LF/CR/CRLF produce four populated original rows");
                for (int row = 0; row < paragraph.Lines.Length; row++)
                    Check(paragraph.Lines[row].GlyphCount > 0, "no fabricated empty-row glyph");
                var limitedOptions = options with { MaximumLines = 1 };
                using var limited = context.LayoutHintedParagraph(in input, in limitedOptions, styles, metrics, devices);
                Check(limited.Lines.Length == 1 && limited.Glyphs.Length < paragraph.Glyphs.Length &&
                    limited.SourceScalars.Length == original.Length && limited.LogicalGlyphs.Length == paragraph.LogicalGlyphs.Length,
                    "MaximumLines limits publication, not original paragraph shaping/source ownership");
                VerifyOwners(limited, original); VerifyInteraction(limited);
            }

            var saved = Snapshot(paragraph);
            var invalidDevices = devices.ToArray(); invalidDevices[^1].Reserved = 1;
            bool rejected = false;
            try { using var invalid = context.LayoutHintedParagraph(in input, in options, styles, metrics, invalidDevices); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "late invalid device metadata rejected");
            EqualSnapshot(saved, paragraph);
            var invalidSource = original.ToArray(); invalidSource[^1].CodePoint = 0x110000;
            var invalidInput = new NativeTextShapeInput([], invalidSource, direction: direction, features: features);
            rejected = false;
            try { using var invalid = context.LayoutHintedParagraph(in invalidInput, in options, styles, metrics, devices); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "late invalid original scalar rejected");
            EqualSnapshot(saved, paragraph);

            context.Dispose();
            Array.Clear(source); Array.Clear(styles); Array.Clear(metrics); Array.Clear(devices); Array.Clear(features);
            EqualSnapshot(saved, paragraph);
            VerifyOwners(paragraph, original); VerifyInteraction(paragraph);
            rejected = false;
            try { using var invalid = context.LayoutHintedParagraph(in input, in options, styles, metrics, devices); }
            catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "retired source context cannot publish another paragraph");
            paragraph.Dispose();
            EqualSnapshot(saved, paragraph); // managed snapshots independently own their original arrays
            cases++;
        }
        Check(observedLineReset, "RTL internal trailing whitespace exercises actual writer-used L1 levels");
        Console.WriteLine($"package-consumer: retained hinted paragraph passed ({cases} loaded-library layouts, source/run owners, UTF-16/bidi/hard breaks, measured interaction, retirement)");
    }

    private static NativeTextScalar[] Decode(string text)
    {
        var result = new List<NativeTextScalar>();
        uint position = 9; // original source frame is deliberately not zero-based
        foreach (Rune rune in text.EnumerateRunes())
        {
            result.Add(new NativeTextScalar { CodePoint = checked((uint)rune.Value), InputIndex = position,
                InputLength = checked((ushort)rune.Utf16SequenceLength) });
            position += checked((uint)rune.Utf16SequenceLength);
        }
        return result.ToArray();
    }

    private static void VerifySources(NativeHintedParagraph paragraph, NativeTextScalar[] source,
        NativeTextStyleRun[] styles, NativeTextStyleMetrics[] metrics, bool digits, bool sourceBidi, NativeTextDirection direction)
    {
        Check(Same<NativeTextScalar>(source, paragraph.SourceScalars), "original UTF-16 source records unchanged");
        var admitted = source.ToArray();
        if (digits)
            for (int i = 0; i < admitted.Length; i++)
                if (admitted[i].CodePoint is >= '0' and <= '9') admitted[i].CodePoint += 0x0660U - '0';
        for (int i = 0; i < admitted.Length; i++)
        {
            // Independent literal Unicode metadata for these exact fixtures,
            // not fields copied from the admitted/native paragraph. The raw
            // source remains untouched; admitted scalars are canonical records.
            admitted[i].CanonicalCombiningClass = 0;
            admitted[i].Reserved = 0;
            admitted[i].Script = admitted[i].CodePoint switch
            {
                'A' or 'B' or 'V' => 0x6C61746E, // Latin -> latn
                0x05D0 or 0x05D1 => 0x68656272, // Hebrew -> hebr
                >= 0x0660 and <= 0x0669 => 0x61726162, // Arabic -> arab
                >= '0' and <= '9' or ' ' or '\n' or '\r' or 0x1F642 => 0x44464C54, // Common -> DFLT
                _ => throw new InvalidOperationException("Add independent literal Unicode metadata for this fixture scalar.")
            };
        }
        Check(Same<NativeTextScalar>(admitted, paragraph.AdmittedScalars),
            "admitted code points and canonical Unicode metadata preserve original UTF-16 positions/lengths");
        Check(Same<NativeTextStyleRun>(styles, paragraph.Styles) && Same<NativeTextStyleMetrics>(metrics, paragraph.SourceMetrics),
            "original style/font/metric identities retained");
        var bidiSource = sourceBidi ? source : admitted;
        var levels = ResolveBidi(bidiSource, direction == NativeTextDirection.RightToLeft ? 1 : 0);
        Check(Same<NativeTextBidiLevel>(levels, paragraph.ScalarLevels), "independent original/substituted source bidi policy");
        Check(paragraph.Counts.SourceScalarCount == source.Length && paragraph.Counts.AdmittedScalarCount == source.Length &&
            paragraph.Counts.StyleCount == 2 && paragraph.Result.ParagraphLevel == (direction == NativeTextDirection.RightToLeft ? 1 : 0),
            "whole original paragraph counts/direction");
    }

    private static NativeTextBidiLevel[] ResolveBidi(NativeTextScalar[] source, int level)
    {
        Check(NativeTextBidiInterop.GetRequirements(source, out var requirements) == NativeRendererStatus.Success, "independent bidi requirements");
        var output = new NativeTextBidiLevel[checked((int)requirements.LevelCapacity)];
        uint alignment = requirements.ScratchAlignment;
        Check(alignment != 0, "native bidi scratch alignment");
        var scratch = new byte[checked((int)requirements.ScratchBytes + (int)alignment)];
        fixed (byte* pointer = scratch)
        {
            int padding = checked((int)((alignment - (nuint)pointer % alignment) % alignment));
            Check(NativeTextBidiInterop.Resolve(source, level, output,
                scratch.AsSpan(padding, checked((int)requirements.ScratchBytes)), out var result) == NativeRendererStatus.Success &&
                result.LevelCount == source.Length, "independent original bidi resolution");
        }
        return output;
    }

    private static void VerifyOwners(NativeHintedParagraph paragraph, NativeTextScalar[] source)
    {
        Check(paragraph.LogicalGlyphs.Length == paragraph.LogicalOwners.Length && paragraph.Glyphs.Length == paragraph.PositionedOwners.Length &&
            paragraph.Glyphs.Length == paragraph.ClusterEnds.Length && paragraph.Glyphs.Length == paragraph.BidiLevels.Length,
            "complete retained owner/metadata partitions");
        for (int i = 0; i < paragraph.LogicalGlyphs.Length; i++)
        {
            var owner = paragraph.LogicalOwners[i]; var glyph = paragraph.LogicalGlyphs[i];
            Check(owner.RunIndex < paragraph.Runs.Length, "logical run index is a retained source slot");
            var run = paragraph.Runs[checked((int)owner.RunIndex)];
            Check(i >= run.LogicalStart && i < run.LogicalStart + run.LogicalCount && owner.RunGlyphIndex < run.LogicalCount &&
                owner.DescriptorIndex < run.SourceDescriptorCount && run.StyleIndex < paragraph.Styles.Length &&
                run.FontIndex == paragraph.Styles[checked((int)run.StyleIndex)].FontIndex &&
                paragraph.GlyphScales[i] == run.LogicalUnitsPerPhysicalPixel / 64f,
                "exact original run/font/style/descriptor/device conversion identity");
            Check(source.Any(s => s.InputIndex == glyph.Cluster) && paragraph.LogicalClusterEnds[i] > glyph.Cluster &&
                source.Any(s => s.InputIndex + s.InputLength == paragraph.LogicalClusterEnds[i]), "original UTF-16 cluster boundary");
            Check(glyph.GlyphId != uint.MaxValue && glyph.GlyphId != uint.MaxValue - 1, "no synthetic caret/tab/object glyph");
        }
        for (int i = 0; i < paragraph.Glyphs.Length; i++)
        {
            var glyph = paragraph.Glyphs[i]; int logical = checked((int)glyph.GlyphIndex);
            Check(logical < paragraph.LogicalGlyphs.Length, "positioned original logical index");
            var original = paragraph.LogicalGlyphs[logical];
            Check(glyph.GlyphId == original.GlyphId && glyph.Cluster == original.Cluster &&
                SameValue(paragraph.PositionedOwners[i], paragraph.LogicalOwners[logical]) &&
                paragraph.ClusterEnds[i] == paragraph.LogicalClusterEnds[logical], "positioned exact source owner/cluster, not glyph-ID search");
        }
    }

    private static void VerifyOriginalRuns(NativeTextShapingContext context, NativeHintedParagraph paragraph,
        NativeTextScalar[] source, NativeTextFeature[] features, NativeHintedParagraphDeviceStyle[] devices, NativeFontHintInterpreter policy)
    {
        for (int runIndex = 0; runIndex < paragraph.Runs.Length; runIndex++)
        {
            var run = paragraph.Runs[runIndex]; var device = devices[checked((int)run.StyleIndex)];
            var style = paragraph.Styles[checked((int)run.StyleIndex)];
            int start = checked((int)run.ScalarStart), count = checked((int)run.ScalarCount);
            // Original Latin itemization carries the resolved latn script even
            // into a trailing-space-only RTL run, and passes whole-source context.
            var input = new NativeTextShapeInput([], source.AsSpan(start, count),
                direction: (run.BidiLevel & 1) == 0 ? NativeTextDirection.LeftToRight : NativeTextDirection.RightToLeft,
                unicodeScript: 0x6C61746E, language: style.Language,
                features: features.AsSpan(checked((int)style.FeatureStart), checked((int)style.FeatureCount)),
                preContext: source.AsSpan(0, start), postContext: source.AsSpan(start + count));
            using var original = context.ShapeHintedRun(in input, run.FontIndex, device.XPixelsPerEm266, device.YPixelsPerEm266,
                policy, device.XPhase266, device.YPhase266);
            var glyphs = new NativeTextShapingGlyph[checked((int)original.GlyphCount)];
            var descriptors = new uint[glyphs.Length]; original.CopyGlyphsTo(glyphs, descriptors);
            Check(original.GlyphCount == run.LogicalCount, "independently executed original full run count");
            for (int i = 0; i < run.LogicalCount; i++)
            {
                int logical = checked((int)run.LogicalStart + i); var owner = paragraph.LogicalOwners[logical];
                int originalIndex = checked((int)owner.RunGlyphIndex);
                Check(SameValue(glyphs[originalIndex], paragraph.LogicalGlyphs[logical]) && owner.RunIndex == runIndex &&
                    owner.DescriptorIndex == descriptors[originalIndex], "independent original hinted shaping and exact descriptor slots");
            }
        }
    }

    private static bool VerifyLineFrames(NativeHintedParagraph paragraph, NativeTextDirection direction)
    {
        Check(paragraph.Lines.Length == paragraph.LineOrigins.Length && paragraph.Lines.Length > 0, "writer-owned line frames");
        bool reset = false;
        float top = 0;
        for (int lineIndex = 0; lineIndex < paragraph.Lines.Length; lineIndex++)
        {
            var line = paragraph.Lines[lineIndex];
            Check(line.GlyphCount > 0 && line.Height >= 13 && line.BaselineY >= top && float.IsFinite(paragraph.LineOrigins[lineIndex]),
                "measured source line frame, not glyph ink");
            int greatest = -1;
            for (uint j = 0; j < line.GlyphCount; j++) greatest = Math.Max(greatest, checked((int)paragraph.Glyphs[checked((int)(line.GlyphStart + j))].GlyphIndex));
            for (int logical = greatest; logical >= 0 && paragraph.LogicalGlyphs[logical].CodePoint == ' '; logical--)
                for (uint j = 0; j < line.GlyphCount; j++)
                {
                    int positioned = checked((int)(line.GlyphStart + j));
                    if (paragraph.Glyphs[positioned].GlyphIndex != logical) continue;
                    sbyte level = direction == NativeTextDirection.RightToLeft ? (sbyte)1 : (sbyte)0;
                    Check(paragraph.BidiLevels[positioned] == level, "actual writer L1-adjusted trailing-space level");
                    reset |= paragraph.LogicalBidiLevels[logical] != level;
                }
            top += line.Height;
        }
        return reset;
    }

    private static void VerifyInteraction(NativeHintedParagraph paragraph)
    {
        var input = new NativeTextInteractionInput(paragraph.Glyphs, paragraph.Lines, paragraph.ClusterEnds, paragraph.BidiLevels);
        Check(NativeTextInteractionInterop.GetMeasuredRequirements(in input, out var required) == NativeRendererStatus.Success, "independent measured interaction requirements");
        var boxes = new NativeTextClusterBox[checked((int)required.ClusterBoxCapacity) + 1];
        var carets = new NativeTextCaretStop[checked((int)required.CaretStopCapacity) + 1];
        MemoryMarshal.AsBytes(boxes.AsSpan()).Fill(0xA5); MemoryMarshal.AsBytes(carets.AsSpan()).Fill(0xA5);
        var boxTail = MemoryMarshal.AsBytes(boxes.AsSpan(^1)).ToArray(); var caretTail = MemoryMarshal.AsBytes(carets.AsSpan(^1)).ToArray();
        Check(NativeTextInteractionInterop.BuildMeasuredAdvance(in input, paragraph.LineOrigins, boxes, carets, out var result) == NativeRendererStatus.Success,
            "independent original advance interaction build");
        Check(Same<NativeTextClusterBox>(boxes.AsSpan(0, checked((int)result.ClusterBoxCount)), paragraph.Boxes) &&
            Same<NativeTextCaretStop>(carets.AsSpan(0, checked((int)result.CaretStopCount)), paragraph.Carets), "cached interaction matches original builder, not another copied result");
        Check(boxTail.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(boxes.AsSpan(^1))) &&
            caretTail.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(carets.AsSpan(^1))), "original interaction caller tails unchanged");
        Check(paragraph.Boxes.Length > 0 && paragraph.Carets.Length > 0, "retained horizontal interaction populated");
        var box = paragraph.Boxes[0];
        Check(paragraph.HitTest(box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f, out var hit) == NativeRendererStatus.Success &&
            hit.InputPosition >= box.InputStart && hit.InputPosition <= box.InputEnd, "retired-generation hit remains in original source box");
    }

    private static byte[][] Snapshot(NativeHintedParagraph paragraph) =>
    [
        MemoryMarshal.AsBytes(paragraph.SourceScalars).ToArray(), MemoryMarshal.AsBytes(paragraph.AdmittedScalars).ToArray(),
        MemoryMarshal.AsBytes(paragraph.ScalarLevels).ToArray(), MemoryMarshal.AsBytes(paragraph.Styles).ToArray(),
        MemoryMarshal.AsBytes(paragraph.SourceMetrics).ToArray(), MemoryMarshal.AsBytes(paragraph.Runs).ToArray(),
        MemoryMarshal.AsBytes(paragraph.LogicalGlyphs).ToArray(), MemoryMarshal.AsBytes(paragraph.LogicalOwners).ToArray(),
        MemoryMarshal.AsBytes(paragraph.LogicalClusterEnds).ToArray(), MemoryMarshal.AsBytes(paragraph.LogicalBidiLevels).ToArray(),
        MemoryMarshal.AsBytes(paragraph.GlyphScales).ToArray(), MemoryMarshal.AsBytes(paragraph.Glyphs).ToArray(),
        MemoryMarshal.AsBytes(paragraph.PositionedOwners).ToArray(), MemoryMarshal.AsBytes(paragraph.ClusterEnds).ToArray(),
        MemoryMarshal.AsBytes(paragraph.BidiLevels).ToArray(), MemoryMarshal.AsBytes(paragraph.Lines).ToArray(),
        MemoryMarshal.AsBytes(paragraph.LineOrigins).ToArray(), MemoryMarshal.AsBytes(paragraph.Boxes).ToArray(), MemoryMarshal.AsBytes(paragraph.Carets).ToArray(),
    ];

    private static void EqualSnapshot(byte[][] expected, NativeHintedParagraph paragraph)
    {
        var actual = Snapshot(paragraph);
        Check(expected.Length == actual.Length && expected.Zip(actual).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
            "all original snapshots survive later rejection/context/input/paragraph retirement");
    }

    private static bool Same<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual) where T : unmanaged
        => MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual));
    private static bool SameValue<T>(T expected, T actual) where T : unmanaged
        => Same<T>(MemoryMarshal.CreateReadOnlySpan(ref expected, 1), MemoryMarshal.CreateReadOnlySpan(ref actual, 1));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native hinted paragraph package: " + message);
    }
}
