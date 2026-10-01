using System.Runtime.InteropServices;
using ProGPU.Backend.Native;

internal static class TextHintedRunValidation
{
    internal static void Run(string fontPath)
    {
        const uint kern = 0x6B65726E;
        NativeTextScalar[] text = "AVA ffi".Select((character, index) => new NativeTextScalar
        {
            CodePoint = character, InputIndex = checked((uint)index), InputLength = 1,
        }).ToArray();
        NativeTextFeature[] features = [new() { Tag = kern, Value = 0, Start = 0, End = uint.MaxValue }];
        foreach (NativeFontHintInterpreter policy in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
        foreach (NativeTextDirection direction in new[] { NativeTextDirection.LeftToRight, NativeTextDirection.RightToLeft })
        {
            byte[] font = File.ReadAllBytes(fontPath);
            using var context = new NativeTextShapingContext(font);
            Array.Clear(font);
            var input = new NativeTextShapeInput([], text, direction: direction, unicodeScript: 0x6C61746E, features: features);
            using NativeHintedTextRun run = context.ShapeHintedRun(in input, 0, 13 * 64, 13 * 64, policy);
            Check(run.GlyphCount > 0 && run.OutlineCounts.Glyphs >= run.GlyphCount, "complete owned shaping generation");
            var expected = Copy(run);
            var outlines = CopyOutlines(run);
            for (int index = 0; index < run.GlyphCount; index++)
            {
                uint descriptor = expected.Descriptors[index];
                Check(descriptor < run.OutlineCounts.Glyphs, "source descriptor range");
                NativeHintedGlyph raw = outlines.Glyphs[descriptor];
                NativeTextShapingGlyph shaped = expected.Glyphs[index];
                Check(shaped.GlyphId == raw.GlyphIndex && shaped.AdvanceX == raw.AdvanceX266 &&
                    shaped.AdvanceY == -raw.AdvanceY266, "same original device metrics and exact descriptor identity");
                Check(shaped.Cluster >= 0 && shaped.Cluster < text.Length, "original source cluster");
            }
            using (NativeHintedTextRun warm = context.ShapeHintedRun(in input, 0, 13 * 64, 13 * 64, policy))
                Equal(expected, Copy(warm));
            using (NativeHintedTextRun phased = context.ShapeHintedRun(in input, 0, 13 * 64, 13 * 64, policy, 32, 16))
            {
                Equal(expected, Copy(phased));
                var shifted = CopyOutlines(phased);
                Check(phased.OutlineCounts.Points == run.OutlineCounts.Points, "phase point identity");
                for (int index = 0; index < run.OutlineCounts.Points; index++)
                    Check(shifted.Points[index].X266 == outlines.Points[index].X266 + 32 &&
                        shifted.Points[index].Y266 == outlines.Points[index].Y266 + 16, "one captured phase");
            }
            var untouched = Buffers(run.GlyphCount);
            var sentinel = Buffers(run.GlyphCount);
            bool rejected = false;
            try { run.CopyGlyphsTo(untouched.Glyphs, untouched.Descriptors.AsSpan(0, checked((int)run.GlyphCount) - 1)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "short descriptor output rejected");
            Equal(sentinel, untouched);
            rejected = false;
            try { run.CopyGlyphsTo(untouched.Glyphs, MemoryMarshal.Cast<NativeTextShapingGlyph, uint>(untouched.Glyphs.AsSpan())); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "full-capacity output overlap rejected");
            Equal(sentinel, untouched);
            Parallel.For(0, 8, _ => Equal(expected, Copy(run)));
            context.Dispose();
            Equal(expected, Copy(run));
            var retiredOutlines = CopyOutlines(run);
            Check(MemoryMarshal.AsBytes(retiredOutlines.Glyphs.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(outlines.Glyphs.AsSpan())) &&
                MemoryMarshal.AsBytes(retiredOutlines.Points.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(outlines.Points.AsSpan())) &&
                retiredOutlines.Tags.AsSpan().SequenceEqual(outlines.Tags) && retiredOutlines.Contours.AsSpan().SequenceEqual(outlines.Contours),
                "outline generation survives context retirement");
            run.Dispose();
            rejected = false;
            try { Copy(run); }
            catch (ObjectDisposedException error) { rejected = error.ObjectName == nameof(NativeHintedTextRun); }
            Check(rejected, "shaped run disposal identity");
        }
        Console.WriteLine("package-consumer: retained hinted shaping passed (both interpreters, LTR/RTL, original device metrics, descriptor identity, phase, atomic tails, retirement)");
    }

    private static (NativeTextShapingGlyph[] Glyphs, uint[] Descriptors) Buffers(uint count)
    {
        var output = (Glyphs: new NativeTextShapingGlyph[checked((int)count) + 1], Descriptors: new uint[checked((int)count) + 1]);
        MemoryMarshal.AsBytes(output.Glyphs.AsSpan()).Fill(0xA5);
        Array.Fill(output.Descriptors, uint.MaxValue);
        return output;
    }

    private static (NativeTextShapingGlyph[] Glyphs, uint[] Descriptors) Copy(NativeHintedTextRun run)
    {
        var output = Buffers(run.GlyphCount);
        var sentinel = Buffers(run.GlyphCount);
        run.CopyGlyphsTo(output.Glyphs, output.Descriptors);
        Check(MemoryMarshal.AsBytes(output.Glyphs.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Glyphs.AsSpan(^1))) &&
            output.Descriptors[^1] == uint.MaxValue, "successful caller tails");
        return output;
    }

    private static (NativeHintedGlyph[] Glyphs, NativeHintedPoint[] Points, byte[] Tags, int[] Contours) CopyOutlines(NativeHintedTextRun run)
    {
        var output = (Glyphs: new NativeHintedGlyph[checked((int)run.OutlineCounts.Glyphs) + 1],
            Points: new NativeHintedPoint[checked((int)run.OutlineCounts.Points) + 1],
            Tags: new byte[checked((int)run.OutlineCounts.Points) + 1], Contours: new int[checked((int)run.OutlineCounts.Contours) + 1]);
        MemoryMarshal.AsBytes(output.Glyphs.AsSpan()).Fill(0xA5);
        MemoryMarshal.AsBytes(output.Points.AsSpan()).Fill(0xA5);
        Array.Fill(output.Tags, (byte)0xA5);
        Array.Fill(output.Contours, -77);
        var glyphTail = MemoryMarshal.AsBytes(output.Glyphs.AsSpan(^1)).ToArray();
        var pointTail = MemoryMarshal.AsBytes(output.Points.AsSpan(^1)).ToArray();
        run.CopyOutlinesTo(output.Glyphs, output.Points, output.Tags, output.Contours);
        Check(MemoryMarshal.AsBytes(output.Glyphs.AsSpan(^1)).SequenceEqual(glyphTail) &&
            MemoryMarshal.AsBytes(output.Points.AsSpan(^1)).SequenceEqual(pointTail) &&
            output.Tags[^1] == 0xA5 && output.Contours[^1] == -77, "successful outline tails");
        return output;
    }

    private static void Equal((NativeTextShapingGlyph[] Glyphs, uint[] Descriptors) expected,
        (NativeTextShapingGlyph[] Glyphs, uint[] Descriptors) actual) => Check(
        MemoryMarshal.AsBytes(expected.Glyphs.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Glyphs.AsSpan())) &&
        expected.Descriptors.AsSpan().SequenceEqual(actual.Descriptors), "raw complete shaped generation equality");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native hinted shaping package: " + message);
    }
}
