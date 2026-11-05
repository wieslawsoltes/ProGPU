using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using ProGPU.Scene.Native;
using ProGPU.Text;

static byte[] Bytes<T>(ReadOnlySpan<T> value) where T : unmanaged => MemoryMarshal.AsBytes(value).ToArray();
static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
const uint mask = 7u | NativeFontScanMetadata.ContourOverride | (0x38u << 16);
int captures = 0, changed = 0;
foreach (string name in new[] { "ProGPU-Authored-Scan-1.ttf", "ProGPU-Authored-Curve-Scan-1.ttf" })
foreach (NativeFontHintInterpreter interpreter in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
foreach (NativeHintedProjectionPolicy projection in new[] { NativeHintedProjectionPolicy.Automatic,
    NativeHintedProjectionPolicy.IntrinsicSimd, NativeHintedProjectionPolicy.ScalarReference })
{
    using Stream stream = typeof(Program).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Missing owned font {name}");
    byte[] font = new byte[checked((int)stream.Length)];
    stream.ReadExactly(font);
    using var context = new NativeTextShapingContext(font);
    Array.Clear(font);
    uint[] ids = [1, 0, 2, 1];
    Vector2[] positions = [new(0, 1), new(12, 1), new(24, 1), new(36, 1)];
    using var rawBatch = context.CaptureOutlineBatch(0, ids, 2048, 2048, interpreter, NativeFontOutlinePolicy.Unhinted);
    using var raw = NativeHintedGlyphGeometryFactory.CreateFontBatch(rawBatch, positions, 1, projection: projection, coverage: NativeHintedCoverage.Monochrome);
    using var monoBatch = context.CaptureOutlineBatch(0, ids, 2048, 2048, interpreter, NativeFontOutlinePolicy.Monochrome);
    using var mono = NativeHintedGlyphGeometryFactory.CreateFontBatch(monoBatch, positions, 1, projection: projection, coverage: NativeHintedCoverage.Monochrome);
    using var paired = NativeHintedGlyphGeometryFactory.CaptureUnhintedFontBatchWithScanMetadata(context, 0, ids, positions,
        2048, 2048, interpreter, 1, projection: projection);
    Check(!paired.HasShapingMetadata, "Invented shaping metadata");
    Check(paired.FontPlacements.Length == 4 && paired.FontPlacements[1].OutlineIndex == uint.MaxValue, "Lost no-ink slot");
    Check(paired.FontPlacements[0].GlyphId == paired.FontPlacements[3].GlyphId, "Lost duplicate descriptor");
    Check(Bytes<GpuGlyphRecord>(raw.Outlines).SequenceEqual(Bytes<GpuGlyphRecord>(paired.Outlines)), "Raw outline bytes changed");
    Check(Bytes<FontGlyphPlacement>(raw.FontPlacements).SequenceEqual(Bytes<FontGlyphPlacement>(paired.FontPlacements)), "Source placements changed");
    Check(paired.Segments.Length == mono.Segments.Length, "Witness inventory changed");
    var restored = paired.Segments.ToArray();
    for (int i = 0; i < restored.Length; i++)
    {
        Check((restored[i].Pad0 & mask) == (mono.Segments[i].Pad0 & mask), "Scan bits not transferred");
        if ((restored[i].Pad0 & mask) != (raw.Segments[i].Pad0 & mask)) changed++;
        restored[i].Pad0 = raw.Segments[i].Pad0;
    }
    Check(Bytes<GpuSegment>(raw.Segments).SequenceEqual(Bytes<GpuSegment>(restored)), "Non-scan bytes changed");
    ids[0] = uint.MaxValue; positions[0] = new(float.NaN);
    Check(paired.FontPlacements[0].GlyphId == 1 && paired.FontPlacements[0].Position == new Vector2(0, 1), "Caller storage retained");
    using var selected = paired.SelectOccurrences([3, 1, 0]);
    paired.Dispose(); rawBatch.Dispose(); monoBatch.Dispose(); raw.Dispose(); mono.Dispose(); context.Dispose();
    Check(selected.FontPlacements.Length == 3 && selected.FontPlacements[1].OutlineIndex == uint.MaxValue && selected.Segments.Length > 0,
        "Selected source generation lost after all public owner retirement");
    Console.WriteLine($"font={name} interpreter={interpreter} projection={projection} segments={selected.SegmentCount} preserved=True");
    captures++;
}
Check(captures == 12 && changed > 0, "No actual scan metadata difference exercised");
Console.WriteLine($"completed=True captures={captures} changedScanSegments={changed} originalPixelsQualified=False");
