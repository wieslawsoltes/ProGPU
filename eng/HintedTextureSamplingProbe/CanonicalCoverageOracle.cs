using System.Runtime.InteropServices;

namespace HintedTextureSamplingProbe;

// Independent double-precision oracle for the captured axis-aligned physical
// frames, not a port of the candidate WGSL mapping. Binary texels, 1024 atlas
// dimensions and these dyadic sample locations have exact binary four-tap
// weights. The selected GPU must meet exact raw-coordinate/coverage controls;
// this is not a claim that every API/adapter guarantees ideal R8 filtering.
internal static class CanonicalCoverageOracle
{
    internal readonly record struct Sample(double TexelX, double TexelY,
        double NormalizedX, double NormalizedY, double Coverage, bool Interior, bool Boundary, bool GlyphRaster);
    internal sealed record Result(string Capture, int InteriorPixels, int BoundaryPixels,
        int OutsidePixels, int DifferentComponents, string[] FirstDifferences);

    internal static byte[] Atlas()
    {
        byte[] atlas = new byte[1024 * 1024];
        // Actual native tile(2,2),20x22,4px padding; deliberately NOT O coverage.
        for (int y = 4; y < 18; y++)
        for (int x = 4; x < 16; x++)
            atlas[(2 + y) * 1024 + 2 + x] = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
        return atlas;
    }

    internal static Sample Expected(int occurrence, int x, int y, ReadOnlySpan<byte> atlas)
    {
        if ((uint)occurrence > 1) throw new ArgumentOutOfRangeException(nameof(occurrence));
        if (atlas.Length != 1024 * 1024) throw new ArgumentException("Expected the original1024 atlas.", nameof(atlas));
        // Independently retained physical frames from the authentic receipt:
        // writer positions plus native padded bounds, at original DPI2. No
        // candidate shader frame/reciprocal or interpolated varying is reused.
        double left = occurrence == 0 ? 5.125 : 5.5;
        double top = occurrence == 0 ? 8.375 : 8.5;
        double px = x + .5, py = y + .5;
        bool closed = px >= left && py >= top && px <= left + 20 && py <= top + 22;
        bool interior = px > left && py > top && px < left + 20 && py < top + 22;
        bool glyphRaster = px >= left && py >= top && px < left + 20 && py < top + 22;
        double tx = 2 + (px - left), ty = 2 + (py - top);
        double u = Math.Clamp(tx, 2.5, 21.5), v = Math.Clamp(ty, 2.5, 23.5);
        return new(tx, ty, u / 1024, v / 1024, Bilinear(atlas, u, v), interior, closed && !interior, glyphRaster);
    }

    internal static double Bilinear(ReadOnlySpan<byte> atlas, double texelX, double texelY)
    {
        double sx = texelX - .5, sy = texelY - .5;
        int x = checked((int)Math.Floor(sx)), y = checked((int)Math.Floor(sy));
        double fx = sx - x, fy = sy - y;
        double a = atlas[y * 1024 + x] / 255.0;
        double b = atlas[y * 1024 + x + 1] / 255.0;
        double c = atlas[(y + 1) * 1024 + x] / 255.0;
        double d = atlas[(y + 1) * 1024 + x + 1] / 255.0;
        return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
    }

    internal static Result Check(string capture, ReadOnlySpan<byte> bytes, uint size,
        int occurrence, ReadOnlySpan<byte> atlas, bool normalized, bool boundedTexture)
    {
        if (bytes.Length != checked((int)(size * size * 16))) throw new ArgumentException("Wrong float capture extent.", nameof(bytes));
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(bytes);
        int interior = 0, boundary = 0, outside = 0, differences = 0;
        var first = new List<string>();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var expected = Expected(occurrence, x, y, atlas);
            ReadOnlySpan<float> pixel = values.Slice(checked((y * (int)size + x) * 4), 4);
            for (int channel = 0; channel < 4; channel++)
                if (!float.IsFinite(pixel[channel])) Fail(x, y, channel, "nonfinite");
            if (expected.Interior) interior++;
            else if (expected.Boundary) boundary++;
            else outside++;

            // Native glyph triangles retain top/left-inclusive, bottom/right-
            // exclusive raster ownership. Original image triangles cover the
            // whole target; their unchanged glyph guard includes equality edges.
            bool absent = pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 0 && pixel[3] == 0;
            if (expected.GlyphRaster || (boundedTexture && expected.Boundary))
            {
                Equal(x, y, 0, pixel[0], normalized ? expected.NormalizedX : expected.TexelX);
                Equal(x, y, 1, pixel[1], normalized ? expected.NormalizedY : expected.TexelY);
                Equal(x, y, 2, pixel[2], expected.Coverage);
                // Gamma/alpha retain original WGSL arithmetic and paired exact
                // controls. CPU Math.Pow is deliberately not a bitwise oracle.
                if (pixel[3] < 0 || pixel[3] > 1) Fail(x, y, 3, "gamma/alpha outside[0,1]");
                if (expected.Coverage == 0) Equal(x, y, 3, pixel[3], 0);
            }
            else if (!absent)
                Fail(x, y, 0, "fragment outside the original per-path raster/guard support");
        }
        return new(capture, interior, boundary, outside, differences, first.ToArray());

        void Equal(int x, int y, int channel, float actual, double expected)
        {
            // Exact by construction, not an epsilon or driver-tuned tolerance.
            float rounded = (float)expected;
            if ((double)rounded != expected) throw new InvalidOperationException("Oracle escaped its exact dyadic domain.");
            if (BitConverter.SingleToUInt32Bits(actual) != BitConverter.SingleToUInt32Bits(rounded))
                Fail(x, y, channel, $"actual={actual:R}/{BitConverter.SingleToUInt32Bits(actual):X8}, expected={expected:R}/{BitConverter.SingleToUInt32Bits(rounded):X8}");
        }
        void Fail(int x, int y, int channel, string detail)
        {
            differences++;
            if (first.Count < 24) first.Add($"({x},{y}) channel{channel}: {detail}");
        }
    }

    internal static int RunControls()
    {
        int passed = 0;
        byte[] binary = Atlas(), oneHot = new byte[1024 * 1024];
        Check(binary.All(value => value is 0 or 255) && binary.Count(value => value == 255) == 84,
            "Oracle pattern must contain only binary texels and original clear padding.");
        int[] corners = [6 * 1024 + 7, 6 * 1024 + 8, 7 * 1024 + 7, 7 * 1024 + 8];
        double[] weights = [3 / 64.0, 21 / 64.0, 5 / 64.0, 35 / 64.0];
        for (int corner = 0; corner < 4; corner++)
        {
            Array.Clear(oneHot); oneHot[corners[corner]] = 255;
            Check(Expected(0, 11, 13, oneHot).Coverage == weights[corner], "Independent one-hot frame0 weight changed.");
            Check(Expected(1, 11, 13, oneHot).Coverage == .25, "Independent one-hot frame1 weight changed.");
        }
        Check(Expected(0, 11, 13, binary).Coverage == 38 / 64.0 && Expected(1, 11, 13, binary).Coverage == .5,
            "Independent checkerboard golden values changed.");
        var clamp = Expected(0, 5, 8, binary);
        Check(clamp.NormalizedX == 2.5 / 1024 && clamp.NormalizedY == 2.5 / 1024 && clamp.Coverage == 0,
            "Original half-texel inset/clamp changed.");
        var minimum = Expected(1, 5, 8, binary); var maximum = Expected(1, 25, 30, binary);
        Check(minimum.Boundary && minimum.GlyphRaster && maximum.Boundary && !maximum.GlyphRaster,
            "Top-left glyph raster and closed image-guard edges were conflated.");
        Check(!Expected(1, 26, 30, binary).Boundary && !Expected(1, 26, 30, binary).GlyphRaster,
            "Outside pixel became an original glyph boundary.");
        for (int occurrence = 0; occurrence < 2; occurrence++)
        foreach (bool bounded in new[] { false, true })
        foreach (bool normalized in new[] { false, true })
        {
            // Synthetic CPU validator input, not evidence of GPU output. The
            // independent one-hot/golden cases above validate the oracle math.
            float[] capture = new float[96 * 96 * 4];
            for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                var sample = Expected(occurrence, x, y, binary);
                if (!sample.GlyphRaster && !(bounded && sample.Boundary)) continue;
                int i = (y * 96 + x) * 4;
                capture[i] = (float)(normalized ? sample.NormalizedX : sample.TexelX);
                capture[i + 1] = (float)(normalized ? sample.NormalizedY : sample.TexelY);
                capture[i + 2] = (float)sample.Coverage;
                capture[i + 3] = (float)sample.Coverage; // Finite placeholder, never a CPU gamma oracle.
            }
            Result Validate() => CanonicalCoverageOracle.Check("CPU-only", MemoryMarshal.AsBytes(capture.AsSpan()), 96,
                occurrence, binary, normalized, bounded);
            Check(Validate().DifferentComponents == 0, "Exact CPU validator input rejected.");
            int tested = (13 * 96 + 11) * 4;
            float coordinate = capture[tested]; capture[tested] = float.BitIncrement(coordinate);
            Check(Validate().DifferentComponents == 1, "One coordinate ULP escaped exact rejection.");
            capture[tested] = coordinate;
            float coverage = capture[tested + 2]; capture[tested + 2] = float.BitIncrement(coverage);
            Check(Validate().DifferentComponents == 1, "One raw-coverage ULP escaped exact rejection.");
            capture[tested + 2] = coverage;
            float alpha = capture[tested + 3]; capture[tested + 3] = float.NaN;
            Check(Validate().DifferentComponents != 0, "Nonfinite alpha escaped the independent capture guard.");
            capture[tested + 3] = alpha;
            capture[0] = 1;
            Check(Validate().DifferentComponents != 0, "Outside fragment escaped original geometry guard.");
        }
        return passed;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
    }
}
