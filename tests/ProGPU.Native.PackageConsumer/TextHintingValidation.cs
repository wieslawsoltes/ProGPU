using System.Runtime.InteropServices;
using ProGPU.Backend.Native;

internal static class TextHintingValidation
{
    internal static void Run(string fontPath)
    {
        uint[] ids = [0, 3, 4, 3, 1, 2, 4];
        foreach (NativeFontHintInterpreter policy in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
        {
            byte[] source = File.ReadAllBytes(fontPath);
            using var context = new NativeTextShapingContext(source);
            Array.Clear(source);
            using NativeHintedFontBatch batch = context.CaptureHintedBatch(0, ids, 13 * 64, 13 * 64, policy);
            Check(batch.Counts.Glyphs == ids.Length && batch.Counts.Points > 0 && batch.Counts.Contours > 0, "complete generation");
            var expected = Copy(batch);
            for (int index = 0; index < ids.Length; index++)
                Check(expected.Glyphs[index].GlyphIndex == ids[index], "original repeated order");
            using (NativeHintedFontBatch warm = context.CaptureHintedBatch(0, ids, 13 * 64, 13 * 64, policy))
                Equal(expected, Copy(warm));
            using (NativeHintedFontBatch phased = context.CaptureHintedBatch(0, ids, 13 * 64, 13 * 64, policy, 32, 16))
            {
                var shifted = Copy(phased);
                Check(phased.Counts.Points == batch.Counts.Points && phased.Counts.Contours == batch.Counts.Contours, "phase topology");
                for (int index = 0; index < batch.Counts.Points; index++)
                    Check(shifted.Points[index].X266 == expected.Points[index].X266 + 32 &&
                        shifted.Points[index].Y266 == expected.Points[index].Y266 + 16, "exact native phase");
                for (int index = 0; index < ids.Length; index++)
                    Check(shifted.Glyphs[index].AdvanceX266 == expected.Glyphs[index].AdvanceX266, "phase advance identity");
            }
            bool rejected = false;
            try { using var invalid = context.CaptureHintedBatch(0, [0, 3, uint.MaxValue], 13 * 64, 13 * 64, policy); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "later invalid glyph rejected");
            Equal(expected, Copy(batch));

            var untouched = Buffers(batch);
            var sentinel = Buffers(batch);
            rejected = false;
            try { batch.CopyTo(untouched.Glyphs, untouched.Points, untouched.Tags.AsSpan(0, checked((int)batch.Counts.Points) - 1), untouched.Contours); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "short output rejected");
            Equal(sentinel, untouched);
            rejected = false;
            try { batch.CopyTo(untouched.Glyphs, untouched.Points, MemoryMarshal.AsBytes(untouched.Points.AsSpan()), untouched.Contours); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "overlapping output rejected");
            Equal(sentinel, untouched);

            for (uint phase = 1; phase <= 20; phase++)
                using (context.CaptureHintedBatch(0, ids, 13 * 64, 13 * 64, policy, phase)) { }
            Equal(expected, Copy(batch));
            Parallel.For(0, 8, _ => Equal(expected, Copy(batch)));
            context.Dispose();
            Equal(expected, Copy(batch));
            rejected = false;
            try { using var invalid = context.CaptureHintedBatch(0, ids, 13 * 64, 13 * 64, policy); }
            catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "disposed context rejected");
            batch.Dispose();
            rejected = false;
            try { Copy(batch); }
            catch (ObjectDisposedException error) { rejected = error.ObjectName == nameof(NativeHintedFontBatch); }
            Check(rejected, "disposed generation rejected");
        }
        Console.WriteLine("package-consumer: retained hinted generations passed (both interpreters, exact phase, atomic tails, eviction, shared-library ownership, disposal)");
    }

    private static (NativeHintedGlyph[] Glyphs, NativeHintedPoint[] Points, byte[] Tags, int[] Contours) Buffers(NativeHintedFontBatch batch)
    {
        var buffers = (Glyphs: new NativeHintedGlyph[checked((int)batch.Counts.Glyphs) + 1],
            Points: new NativeHintedPoint[checked((int)batch.Counts.Points) + 1],
            Tags: new byte[checked((int)batch.Counts.Points) + 1],
            Contours: new int[checked((int)batch.Counts.Contours) + 1]);
        MemoryMarshal.AsBytes(buffers.Glyphs.AsSpan()).Fill(0xA5);
        MemoryMarshal.AsBytes(buffers.Points.AsSpan()).Fill(0xA5);
        Array.Fill(buffers.Tags, (byte)0xA5);
        Array.Fill(buffers.Contours, -77);
        return buffers;
    }

    private static (NativeHintedGlyph[] Glyphs, NativeHintedPoint[] Points, byte[] Tags, int[] Contours) Copy(NativeHintedFontBatch batch)
    {
        var output = Buffers(batch);
        var sentinel = Buffers(batch);
        batch.CopyTo(output.Glyphs, output.Points, output.Tags, output.Contours);
        Check(MemoryMarshal.AsBytes(output.Glyphs.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Glyphs.AsSpan(^1))) &&
            MemoryMarshal.AsBytes(output.Points.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Points.AsSpan(^1))) &&
            output.Tags[^1] == 0xA5 && output.Contours[^1] == -77, "successful caller tails");
        return output;
    }

    private static void Equal((NativeHintedGlyph[] Glyphs, NativeHintedPoint[] Points, byte[] Tags, int[] Contours) expected,
        (NativeHintedGlyph[] Glyphs, NativeHintedPoint[] Points, byte[] Tags, int[] Contours) actual)
    {
        Check(MemoryMarshal.AsBytes(expected.Glyphs.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Glyphs.AsSpan())) &&
            MemoryMarshal.AsBytes(expected.Points.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Points.AsSpan())) &&
            expected.Tags.AsSpan().SequenceEqual(actual.Tags) && expected.Contours.AsSpan().SequenceEqual(actual.Contours), "raw complete generation equality");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native hinted package: " + message);
    }
}
