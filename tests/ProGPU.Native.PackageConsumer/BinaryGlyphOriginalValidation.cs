using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

// Independent public native execution of every original authored-font bitmap.
// No managed atlas, source font lookup, tolerance or replacement expected image.
internal static class BinaryGlyphOriginalValidation
{
    internal static void Run(WgpuContext context, Func<NativeCompositor> create)
    {
        using Stream input = typeof(BinaryGlyphOriginalValidation).Assembly
            .GetManifestResourceStream("BinaryGlyphOriginals.json")!;
        using var storage = new MemoryStream();
        input.CopyTo(storage);
        byte[] source = storage.ToArray();
        Check(Convert.ToHexString(SHA256.HashData(source)) ==
            "6D13E632B5F260217BC17D9C86B61E77506F463644EEEE923280400A52F3B092", "original fixture digest");
        using JsonDocument document = JsonDocument.Parse(source);
        JsonElement[] cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Check(cases.Length == 880 && cases.Select(c => (c.GetProperty("gasp").GetInt32(),
            c.GetProperty("index").GetInt32())).Distinct().Count() == 880, "all 880 original cases");
        const int columns = 32, tile = 64, width = columns * tile, height = 28 * tile;
        var outlines = new NativeGlyphOutline[cases.Length];
        var glyphs = new NativePositionedGlyph[cases.Length];
        var segments = new List<NativePathSegment>();
        var expected = new byte[width * height * 4];
        for (int i = 0; i < cases.Length; i++)
        {
            JsonElement row = cases[i];
            int[][] original = row.GetProperty("segments").EnumerateArray()
                .Select(s => s.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
            int start = segments.Count;
            Vector2 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            foreach (int[] values in original)
            {
                Vector2 p0 = new Vector2(values[1], values[2]) / 64f;
                Vector2 p1 = new Vector2(values[3], values[4]) / 64f;
                Vector2 p2 = new Vector2(values[5], values[6]) / 64f;
                minimum = Vector2.Min(minimum, Vector2.Min(p0, p1));
                maximum = Vector2.Max(maximum, Vector2.Max(p0, p1));
                if (values[0] == 1)
                {
                    minimum = Vector2.Min(minimum, p2); maximum = Vector2.Max(maximum, p2);
                }
                segments.Add(new((NativePathSegmentKind)values[0], p0, p1, p2,
                    pad0: NativeFontScanMetadata.SegmentMarker | NativeFontScanMetadata.ContourOverride |
                        row.GetProperty("mode").GetUInt32(), pad2: checked((uint)original.Length)));
            }
            outlines[i] = new(checked((nuint)start), checked((nuint)original.Length), minimum, maximum, 1f);
            int left = i % columns * tile, top = i / columns * tile;
            glyphs[i] = new(checked((uint)i), new(left + 8, top + 24), Vector2.UnitX, Vector2.UnitY, Vector4.One);
            foreach (JsonElement value in row.GetProperty("black").EnumerateArray())
            {
                int point = value.GetInt32();
                Check(point is >= 0 and < tile * tile, "original pixel extent");
                int offset = ((top + point / tile) * width + left + point % tile) * 4;
                expected.AsSpan(offset, 4).Fill(255);
            }
        }
        Render(context, create, "original-segments", outlines, segments.ToArray(), glyphs, expected);
        foreach (NativeFontHintInterpreter interpreter in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
        {
            var capturedOutlines = new List<NativeGlyphOutline>();
            var capturedSegments = new List<NativePathSegment>();
            var capturedGlyphs = new NativePositionedGlyph[cases.Length];
            foreach (int gasp in new[] { 1, 3 })
            {
                using var fontStream = typeof(BinaryGlyphOriginalValidation).Assembly
                    .GetManifestResourceStream($"ProGPU-Authored-Scan-{gasp}.ttf")!;
                using var fontBytes = new MemoryStream(); fontStream.CopyTo(fontBytes);
                byte[] font = fontBytes.ToArray();
                Check(Convert.ToHexString(SHA256.HashData(font)) == (gasp == 1
                    ? "60D952F14B770AEC08221B8ADEE1A1B7D82A8ABDC259FB2484DE9666A2F45832"
                    : "FCBCFA2B3B54CB19E024648C8CF4AE17E6CB08EEE526DDDD9DA91C8BFAF5E9F7"), "original authored font bytes");
                int[] indices = Enumerable.Range(0, cases.Length).Where(i => cases[i].GetProperty("gasp").GetInt32() == gasp).ToArray();
                uint[] ids = indices.Select(i => cases[i].GetProperty("index").GetUInt32()).ToArray();
                using var producer = new NativeTextShapingContext(font);
                Array.Clear(font);
                using var batch = producer.CaptureOutlineBatch(0, ids, 32 * 64, 32 * 64,
                    interpreter, NativeFontOutlinePolicy.Monochrome);
                using var geometry = batch.CreateGeometry(coverage: NativeHintedCoverage.Monochrome);
                batch.Dispose(); producer.Dispose();
                var ownedOutlines = new NativeGlyphOutline[geometry.Counts.Outlines];
                var ownedSegments = new NativePathSegment[geometry.Counts.Segments];
                var ownedDescriptors = new NativeFontOutlineDescriptor[geometry.Counts.Descriptors];
                geometry.CopyTo(ownedOutlines, ownedSegments, ownedDescriptors);
                Check(ownedDescriptors.Length == ids.Length, "complete captured source descriptors after producer disposal");
                int outlineStart = capturedOutlines.Count, segmentStart = capturedSegments.Count;
                foreach (var outline in ownedOutlines)
                    capturedOutlines.Add(new(outline.SegmentOffset + checked((nuint)segmentStart),
                        outline.SegmentCount, outline.Minimum, outline.Maximum, outline.RasterScale, outline.SubpixelX));
                capturedSegments.AddRange(ownedSegments);
                for (int i = 0; i < indices.Length; i++)
                {
                    var descriptor = ownedDescriptors[i];
                    Check(descriptor.GlyphIndex == ids[i] && descriptor.OutlineIndex < ownedOutlines.Length,
                        "exact original source ID and ink descriptor");
                    var positioned = glyphs[indices[i]];
                    capturedGlyphs[indices[i]] = new(checked((uint)outlineStart + descriptor.OutlineIndex),
                        positioned.Position, positioned.BasisX, positioned.BasisY, positioned.Color);
                }
            }
            Render(context, create, $"captured-font-{interpreter}", capturedOutlines.ToArray(),
                capturedSegments.ToArray(), capturedGlyphs, expected);
        }
    }

    private static void Render(WgpuContext context, Func<NativeCompositor> create, string source,
        NativeGlyphOutline[] outlines, NativePathSegment[] payload, NativePositionedGlyph[] glyphs, byte[] expected)
    {
        const int columns = 32, tile = 64, width = columns * tile, height = 28 * tile;
        var rasterIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outline in outlines)
        {
            // Normalize only the buffer offset, which is not raster identity.
            var normalized = new NativeGlyphOutline(0, outline.SegmentCount, outline.Minimum,
                outline.Maximum, outline.RasterScale, outline.SubpixelX);
            byte[] descriptor = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                new[] { normalized }.AsSpan()).ToArray();
            byte[] segments = System.Runtime.InteropServices.MemoryMarshal.AsBytes(payload.AsSpan(
                checked((int)outline.SegmentOffset), checked((int)outline.SegmentCount))).ToArray();
            rasterIdentities.Add(Convert.ToHexString(descriptor) + Convert.ToHexString(segments));
        }
        using var renderer = create();
        using var target = new GpuTexture(context, width, height, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Original authored binary coverage",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        byte[]? cold = null;
        for (int frame = 0; frame < 2; frame++)
        {
            var before = renderer.GetLastSubmissionToken();
            var metrics = renderer.RenderGlyphs(target, 1f, outlines, payload, glyphs,
                Vector4.Zero, contentRevision: 1);
            var token = renderer.GetLastSubmissionToken();
            Check(token.IsValid && token.Value > before.Value, "actual native submission");
            renderer.WaitForSubmission(token);
            Check(renderer.IsSubmissionComplete(token), "actual completed native submission");
            byte[] pixels = target.ReadPixels();
            Check(pixels.Length == expected.Length && metrics.GlyphCount == 880, "complete target and inventory");
            if (frame == 0)
            {
                Check(metrics.RasterizedGlyphCount == rasterIdentities.Count && metrics.CoverageStagingBytes != 0,
                    $"cold native rasterization: {metrics.RasterizedGlyphCount}/{rasterIdentities.Count} exact distinct originals, {metrics.CoverageStagingBytes} bytes");
                cold = pixels;
            }
            else
            {
                Check(metrics.RasterizedGlyphCount == 0 && metrics.CoverageStagingBytes == 0 &&
                    cold!.AsSpan().SequenceEqual(pixels), "unchanged warm coverage without rasterization");
            }
        }
        int differences = 0;
        var casesWithDifferences = new HashSet<int>();
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            if (cold!.AsSpan(pixel * 4, 4).SequenceEqual(expected.AsSpan(pixel * 4, 4))) continue;
            differences++;
            int x = pixel % width, y = pixel / width;
            int index = y / tile * columns + x / tile;
            casesWithDifferences.Add(index);
            Console.WriteLine($"binary-original-difference case={index} x={x % tile} y={y % tile} " +
                $"expected={Convert.ToHexString(expected.AsSpan(pixel * 4, 4))} actual={Convert.ToHexString(cold.AsSpan(pixel * 4, 4))}");
        }
        Console.WriteLine($"binary-originals provider={context.BackendKind} source={source} cases=880 failedCases={casesWithDifferences.Count} pixels={differences}");
        Check(differences == 0, "every original full-frame pixel, including all blank padding");
    }

    private static void Check(bool value, string detail)
    {
        if (!value) throw new InvalidOperationException($"Binary original coverage: {detail}");
    }
}
