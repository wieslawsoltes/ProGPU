using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Text;
using Xunit;
using Xunit.Abstractions;

namespace ProGPU.Tests;

// Original Windows bitmaps of our own authored TrueType outlines, with matching
// GDI+ and GGO_BITMAP observations. These are complete coverage comparisons,
// not system-font, package, source layout or application qualification.
public sealed class BinaryGlyphOriginalCoverageTests(ITestOutputHelper output)
{
    private sealed class Owner : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    [Theory]
    [InlineData("BinaryGlyphOriginals.json", "6D13E632B5F260217BC17D9C86B61E77506F463644EEEE923280400A52F3B092")]
    [InlineData("BinaryGlyphCurveOriginals.json", "B30470FEAE7D095ADAD5E549595ADEAED929058E3040D94640F0CA214313E2B8")]
    public void EveryOriginalAuthoredBitmapMatchesColdAndWarmCoverage(string resource, string digest)
    {
        using Stream stream = typeof(BinaryGlyphOriginalCoverageTests).Assembly
            .GetManifestResourceStream(resource)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Assert.Equal(digest,
            Convert.ToHexString(SHA256.HashData(bytes.ToArray())));
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
        JsonElement[] cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(880, cases.Length);
        Assert.Equal(880, cases.Select(c => (c.GetProperty("gasp").GetInt32(), c.GetProperty("index").GetInt32())).Distinct().Count());
        Assert.Equal(176, cases.Count(c => c.GetProperty("mode").GetInt32() == 2));
        var outlines = new GpuGlyphRecord[cases.Length];
        var placements = new FontGlyphPlacement[cases.Length];
        var segments = new List<GpuSegment>();
        for (int i = 0; i < cases.Length; i++)
        {
            JsonElement row = cases[i];
            uint start = checked((uint)segments.Count);
            int[][] source = row.GetProperty("segments").EnumerateArray()
                .Select(s => s.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
            Vector2 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            foreach (int[] values in source)
            {
                var segment = new GpuSegment
                {
                    SegmentType = checked((uint)values[0]),
                    P0 = new Vector2(values[1], values[2]) / 64f,
                    P1 = new Vector2(values[3], values[4]) / 64f,
                    P2 = new Vector2(values[5], values[6]) / 64f,
                    Pad0 = NativeFontScanMetadata.SegmentMarker | NativeFontScanMetadata.ContourOverride |
                        row.GetProperty("mode").GetUInt32(),
                    Pad1 = 0, Pad2 = checked((uint)source.Length)
                };
                minimum = Vector2.Min(minimum, Vector2.Min(segment.P0, segment.P1));
                maximum = Vector2.Max(maximum, Vector2.Max(segment.P0, segment.P1));
                if (segment.SegmentType == 1)
                {
                    minimum = Vector2.Min(minimum, segment.P2);
                    maximum = Vector2.Max(maximum, segment.P2);
                }
                segments.Add(segment);
            }
            outlines[i] = new GpuGlyphRecord { StartSegment = start, SegmentCount = checked((uint)source.Length),
                MinX = minimum.X, MinY = minimum.Y, MaxX = maximum.X, MaxY = maximum.Y };
            placements[i] = new(checked((uint)i), row.GetProperty("index").GetUInt32(), checked((uint)i), Vector2.Zero);
        }
        var owner = new Owner();
        using var geometry = HintedGlyphGeometry.FromFontPlacements(1f, outlines, segments.ToArray(), placements, owner);
        using var retained = geometry.RetainForRecording();
        geometry.Dispose();
        using var context = new WgpuContext { ComputeExecutionPreference = GpuComputeExecutionPreference.NativeCompute };
        context.Initialize(null);
        Assert.Equal(GpuComputeExecutionPath.NativeCompute, context.GlyphRasterizationPath);
        using var atlas = new GlyphAtlas(context, atlasSize: 2048, initialAtlasSize: 2048,
            colorAtlasSize: 64, initialColorAtlasSize: 64, uniformRingBufferSize: 4096,
            coverageRingBufferSize: 1024 * 1024);
        var locations = new GlyphInfo[cases.Length];
        atlas.BeginBatch();
        try
        {
            for (uint index = 0; index < locations.Length; index++)
                locations[index] = atlas.GetOrCreateHintedGlyph(geometry, index);
        }
        finally { atlas.EndBatch(); }
        byte[] cold = atlas.AtlasTexture.ReadPixels();
        Assert.Equal(2048 * 2048, cold.Length);
        Assert.Equal(1, context.CachedDeviceShaderModuleCount);
        Assert.Equal(2, context.CachedDeviceComputePipelineCount);
        Assert.Equal(0, context.CachedDeviceRenderPipelineCount);
        var differences = new List<string>();
        int failedCases = 0, failedPixels = 0;
        for (int index = 0; index < cases.Length; index++)
        {
            JsonElement row = cases[index];
            var original = row.GetProperty("black").EnumerateArray().Select(p => p.GetInt32()).ToHashSet();
            GlyphInfo location = locations[index];
            int different = 0;
            for (uint y = 0; y < location.Height; y++)
            for (uint x = 0; x < location.Width; x++)
            {
                int originalX = checked(8 + (int)location.BearX + (int)x);
                int originalY = checked(24 + (int)location.BearY + (int)y);
                byte expected = originalX is >= 0 and < 64 && originalY is >= 0 and < 64 &&
                    original.Contains(originalY * 64 + originalX) ? (byte)255 : (byte)0;
                byte actual = cold[checked((int)((location.Y + y) * 2048 + location.X + x))];
                if (actual != expected)
                {
                    different++;
                    output.WriteLine($"binary-original-difference case={index} x={originalX} y={originalY} expected={expected} actual={actual}");
                }
            }
            if (different == 0) continue;
            failedCases++; failedPixels += different;
            if (differences.Count < 16)
                differences.Add($"gasp={row.GetProperty("gasp")}, index={row.GetProperty("index")}, " +
                    $"kind={row.GetProperty("kind")}, mode={row.GetProperty("mode")}, phase={row.GetProperty("phase")}: {different} pixels");
        }
        ulong bindings = atlas.RasterBindGroupCreationCount, passes = atlas.RasterComputePassCount;
        atlas.BeginBatch();
        try
        {
            for (uint index = 0; index < locations.Length; index++)
                Assert.Equal(locations[index], atlas.GetOrCreateHintedGlyph(geometry, index));
        }
        finally { atlas.EndBatch(); }
        Assert.Equal(bindings, atlas.RasterBindGroupCreationCount);
        Assert.Equal(passes, atlas.RasterComputePassCount);
        Assert.Equal(cold, atlas.AtlasTexture.ReadPixels());
        retained.Dispose();
        Assert.Equal(1, owner.Disposals);
        Assert.True(failedCases == 0,
            $"Original binary coverage: {failedCases}/880 cases and {failedPixels} pixels differ. " + string.Join("; ", differences));
    }
}
