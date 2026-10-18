using System.Runtime.InteropServices;
using ProGPU.Backend.Native;

internal static class TextFontOutlineValidation
{
    internal static void Run(string fontPath)
    {
        uint[] ids = [0, 3, 4, 3, 1, 2, 4];
        foreach (NativeFontHintInterpreter interpreter in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
        foreach (NativeFontOutlinePolicy policy in Enum.GetValues<NativeFontOutlinePolicy>())
        {
            byte[] font = File.ReadAllBytes(fontPath);
            using var context = new NativeTextShapingContext(font);
            Array.Clear(font);
            using var batch = context.CaptureOutlineBatch(0, ids, 13 * 64, 13 * 64, interpreter, policy);
            var glyphs = new NativeHintedGlyph[batch.Counts.Glyphs];
            batch.CopyTo(glyphs, new NativeHintedPoint[batch.Counts.Points],
                new byte[batch.Counts.Points], new int[batch.Counts.Contours]);
            using var geometry = batch.CreateGeometry(coverage: NativeHintedCoverage.AntialiasedVector);
            using var scalar = batch.CreateGeometry(NativeHintedProjectionPolicy.ScalarReference,
                NativeHintedCoverage.AntialiasedVector);
            Check(geometry.Counts.Descriptors == ids.Length && geometry.Counts.Outlines > 0 &&
                geometry.Counts.Segments > 0, "complete actual font geometry");
            var expected = Copy(geometry);
            Equal(expected, Copy(scalar));
            for (int i = 0; i < ids.Length; i++)
            {
                ref readonly var descriptor = ref expected.Descriptors[i];
                Check(descriptor.GlyphIndex == ids[i] && glyphs[i].GlyphIndex == ids[i], "original repeated IDs");
                Check(glyphs[i].PointCount == 0
                    ? descriptor.OutlineIndex == uint.MaxValue
                    : descriptor.OutlineIndex < geometry.Counts.Outlines, "original ink/no-ink mapping");
            }

            var untouched = Buffers(geometry);
            var sentinel = Buffers(geometry);
            bool rejected = false;
            try { geometry.CopyTo(untouched.Outlines, untouched.Segments, untouched.Descriptors.AsSpan(0, ids.Length - 1)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "short descriptor output rejected");
            Equal(sentinel, untouched);

            // The geometry owns its source generation independently of both
            // public source handles, across the actual loaded package boundary.
            batch.Dispose();
            context.Dispose();
            Equal(expected, Copy(geometry));
            Parallel.For(0, 8, _ => Equal(expected, Copy(geometry)));
            scalar.Dispose();
            Equal(expected, Copy(geometry));
            geometry.Dispose();
            rejected = false;
            try { Copy(geometry); }
            catch (ObjectDisposedException error) { rejected = error.ObjectName == nameof(NativeFontOutlineGeometry); }
            Check(rejected, "disposed geometry rejected");
        }
        Console.WriteLine("package-consumer: font outline geometry passed (four load targets, both interpreters, scalar equality, original descriptor order, atomic tails, independent lifetime)");
    }

    private static (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativeFontOutlineDescriptor[] Descriptors)
        Buffers(NativeFontOutlineGeometry geometry)
    {
        var result = (Outlines: new NativeGlyphOutline[checked((int)geometry.Counts.Outlines) + 1],
            Segments: new NativePathSegment[checked((int)geometry.Counts.Segments) + 1],
            Descriptors: new NativeFontOutlineDescriptor[checked((int)geometry.Counts.Descriptors) + 1]);
        MemoryMarshal.AsBytes(result.Outlines.AsSpan()).Fill(0xA5);
        MemoryMarshal.AsBytes(result.Segments.AsSpan()).Fill(0xA5);
        MemoryMarshal.AsBytes(result.Descriptors.AsSpan()).Fill(0xA5);
        return result;
    }

    private static (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativeFontOutlineDescriptor[] Descriptors)
        Copy(NativeFontOutlineGeometry geometry)
    {
        var result = Buffers(geometry);
        var sentinel = Buffers(geometry);
        geometry.CopyTo(result.Outlines, result.Segments, result.Descriptors);
        Check(MemoryMarshal.AsBytes(result.Outlines.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Outlines.AsSpan(^1))) &&
            MemoryMarshal.AsBytes(result.Segments.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Segments.AsSpan(^1))) &&
            MemoryMarshal.AsBytes(result.Descriptors.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Descriptors.AsSpan(^1))),
            "unchanged caller tails");
        return result;
    }

    private static void Equal(
        (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativeFontOutlineDescriptor[] Descriptors) expected,
        (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativeFontOutlineDescriptor[] Descriptors) actual)
    {
        Check(MemoryMarshal.AsBytes(expected.Outlines.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Outlines.AsSpan())) &&
            MemoryMarshal.AsBytes(expected.Segments.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Segments.AsSpan())) &&
            MemoryMarshal.AsBytes(expected.Descriptors.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Descriptors.AsSpan())),
            "complete original geometry equality");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native font outline package: " + message);
    }
}
