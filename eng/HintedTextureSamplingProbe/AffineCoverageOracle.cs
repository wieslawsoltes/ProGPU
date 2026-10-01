using System.Runtime.InteropServices;

namespace HintedTextureSamplingProbe;

// Independent, bounded integer-area oracle. Coordinates below are original
// physical corners in eighth-pixel units, not candidate WGSL inverse rows.
// The authored shear/reflection and 16x16 tile make determinants +/-16384 in
// those units: every barycentric address and binary bilinear weight is exactly
// representable. This is NOT the authentic non-binary italic/shear fixture.
internal static class AffineCoverageOracle
{
    internal const int TileSize = 16;
    private readonly record struct Point(long X, long Y);
    private static readonly Point[][] Corners =
    [
        [new(289, 259), new(417, 259), new(481, 387), new(353, 387)],
        [new(484, 260), new(356, 260), new(420, 388), new(548, 388)]
    ];

    internal static byte[] Instances()
    {
        byte[] bytes = Program.Instances();
        for (int occurrence = 0; occurrence < 2; occurrence++)
        {
            Span<float> value = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(occurrence * 96, 96));
            value[0] = occurrence == 0 ? 24.0625f : 32.25f;
            value[1] = occurrence == 0 ? 24.1875f : 24.25f;
            value[2] = occurrence == 0 ? 1 : -1; value[3] = 0;
            value[4] = .5f; value[5] = 1;
            value[6] = -4; value[7] = -16; value[8] = value[9] = TileSize;
            value[10] = value[11] = 2; value[12] = value[13] = 2 + TileSize;
        }
        return bytes;
    }

    internal static byte[] Atlas()
    {
        var atlas = new byte[1024 * 1024];
        for (int y = 4; y < 12; y++)
        for (int x = 4; x < 12; x++)
            atlas[(2 + y) * 1024 + 2 + x] = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
        return atlas;
    }

    private static long Area(Point a, Point b, Point c) =>
        checked((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));

    private static bool Edge(Point a, Point b, Point point, long sign)
    {
        long area = Area(a, b, point) * sign;
        long dx = (b.X - a.X) * sign, dy = (b.Y - a.Y) * sign;
        return area > 0 || (area == 0 && (dy < 0 || (dy == 0 && dx > 0)));
    }

    internal readonly record struct ExpectedSample(bool Present, bool Boundary, int Triangle,
        double TexelX, double TexelY, double NormalizedX, double NormalizedY, double Coverage);

    internal static ExpectedSample Expected(int occurrence, int x, int y, ReadOnlySpan<byte> atlas)
    {
        if ((uint)occurrence >= 2 || atlas.Length != 1024 * 1024) throw new ArgumentException("Wrong affine oracle input.");
        Point point = new(checked(x * 8L + 4), checked(y * 8L + 4));
        Point[] corners = Corners[occurrence];
        ExpectedSample result = default;
        for (int triangle = 0; triangle < 2; triangle++)
        {
            Point a = corners[0], b = corners[triangle == 0 ? 1 : 2], c = corners[triangle == 0 ? 2 : 3];
            long denominator = Area(a, b, c), sign = Math.Sign(denominator);
            if (Math.Abs(denominator) != 16384) throw new InvalidOperationException("Oracle escaped its authored dyadic domain.");
            if (!Edge(a, b, point, sign) || !Edge(b, c, point, sign) || !Edge(c, a, point, sign)) continue;
            if (result.Present) throw new InvalidOperationException("Original shared edge acquired two owners.");
            long first = Area(a, point, c), second = Area(a, b, point);
            long u = triangle == 0 ? first + second : first;
            long v = triangle == 0 ? second : first + second;
            double tx = (2 * denominator + 16 * u) / (double)denominator;
            double ty = (2 * denominator + 16 * v) / (double)denominator;
            double clampedX = Math.Clamp(tx, 2.5, 17.5), clampedY = Math.Clamp(ty, 2.5, 17.5);
            double coverage = CanonicalCoverageOracle.Bilinear(atlas, clampedX, clampedY);
            result = new(true, first == 0 || second == 0 || first + second == denominator, triangle,
                tx, ty, clampedX / 1024, clampedY / 1024, coverage);
        }
        return result;
    }

    internal static CanonicalCoverageOracle.Result Check(string capture, ReadOnlySpan<byte> bytes, uint size,
        int occurrence, ReadOnlySpan<byte> atlas, bool normalized)
    {
        if (bytes.Length != checked((int)(size * size * 16))) throw new ArgumentException("Wrong affine capture extent.");
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(bytes);
        int interior = 0, boundary = 0, outside = 0, differences = 0;
        var first = new List<string>();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            ExpectedSample expected = Expected(occurrence, x, y, atlas);
            ReadOnlySpan<float> pixel = values.Slice(checked((y * (int)size + x) * 4), 4);
            if (!expected.Present)
            {
                outside++;
                for (int channel = 0; channel < 4; channel++) Equal(x, y, channel, pixel[channel], 0);
                continue;
            }
            if (expected.Boundary) boundary++; else interior++;
            Equal(x, y, 0, pixel[0], normalized ? expected.NormalizedX : expected.TexelX);
            Equal(x, y, 1, pixel[1], normalized ? expected.NormalizedY : expected.TexelY);
            Equal(x, y, 2, pixel[2], expected.Coverage);
            if (!float.IsFinite(pixel[3]) || pixel[3] < 0 || pixel[3] > 1) Fail(x, y, 3, "gamma/alpha outside[0,1]");
            if (expected.Coverage == 0) Equal(x, y, 3, pixel[3], 0);
        }
        if (interior == 0) throw new InvalidOperationException("Affine oracle observed no original triangle interior.");
        return new(capture, interior, boundary, outside, differences, first.ToArray());

        void Equal(int x, int y, int channel, float actual, double expected)
        {
            float exact = (float)expected;
            if ((double)exact != expected) throw new InvalidOperationException("Affine oracle escaped exact float representability.");
            if (BitConverter.SingleToUInt32Bits(actual) != BitConverter.SingleToUInt32Bits(exact))
                Fail(x, y, channel, $"actual={actual:R}/{BitConverter.SingleToUInt32Bits(actual):X8}, expected={expected:R}/{BitConverter.SingleToUInt32Bits(exact):X8}");
        }
        void Fail(int x, int y, int channel, string detail)
        {
            differences++;
            if (first.Count < 24) first.Add($"({x},{y}) channel{channel}: {detail}");
        }
    }

    internal static object Provenance() => new
    {
        Domain = "Separate synthetic dyadic shear and reflected-shear control, NOT authentic hinted font coverage.",
        PhysicalCornerScale = 8,
        OriginalPhysicalCorners = Corners.Select(points => points.Select(p => new[] { p.X, p.Y }).ToArray()).ToArray(),
        PhysicalTriangleDeterminants = new[] { 256, -256 }, TileExtent = new[] { TileSize, TileSize },
        AddressOracle = "Independent checked integer signed areas over original corners, exact power-of-two rational denominators; both original triangles and half-open edge ownership.",
        CoverageOracle = "Binary0/255 atlas plus exact dyadic four-tap weights; no coordinate/sampler/gamma tolerance. Gamma/alpha observed separately."
    };

    internal static int RunControls()
    {
        int passed = 0;
        byte[] atlas = Atlas();
        byte[] instances = Instances();
        Check(ShaderDiagnostics.Hash(instances) == "CD1BE60F18411A388B64373285D4226AC295E5860F2CB3DDFF7EBAB1F2AECA4F",
            "Independent little-endian24float-per-instance assembly.");
        Check(atlas.Count(value => value == 255) == 32 && atlas.All(value => value is 0 or 255), "Original binary inventory.");
        for (int occurrence = 0; occurrence < 2; occurrence++)
        {
            ReadOnlySpan<float> value = MemoryMarshal.Cast<byte, float>(instances.AsSpan(occurrence * 96, 96));
            for (int corner = 0; corner < 4; corner++)
            {
                float lx = value[6] / 2 + (corner is 1 or 2 ? value[8] / 2 : 0);
                float ly = value[7] / 2 + (corner is 2 or 3 ? value[9] / 2 : 0);
                float px = (value[0] + (lx * value[2] + ly * value[4])) * 2;
                float py = (value[1] + (lx * value[3] + ly * value[5])) * 2;
                Check(px == Corners[occurrence][corner].X / 8f && py == Corners[occurrence][corner].Y / 8f,
                    "Original instance bytes must produce every independently authored physical corner exactly.");
            }
        }
        foreach (var sample in new[]
        {
            (Glyph: 0, X: 48, Y: 38, Triangle: 0, U: 11.3125, V: 8.125, Coverage: .578125),
            (Glyph: 0, X: 46, Y: 42, Triangle: 1, U: 7.3125, V: 12.125, Coverage: .578125),
            (Glyph: 1, X: 54, Y: 38, Triangle: 0, U: 11.0, V: 8.0, Coverage: .5),
            (Glyph: 1, X: 59, Y: 40, Triangle: 1, U: 7.0, V: 10.0, Coverage: .5)
        })
        {
            ExpectedSample expected = Expected(sample.Glyph, sample.X, sample.Y, atlas);
            Check(expected.Present && expected.Triangle == sample.Triangle && expected.TexelX == sample.U &&
                expected.TexelY == sample.V && expected.Coverage == sample.Coverage, "Independent hand-derived area/weight sample.");
        }
        ExpectedSample diagonal = Expected(1, 56, 40, atlas);
        Check(diagonal.Present && diagonal.Boundary && diagonal.Triangle == 1 &&
            diagonal.TexelX == 10 && diagonal.TexelY == 10, "Original reflected shared diagonal has one exact owner.");
        Check(!Expected(0, 0, 0, atlas).Present && !Expected(1, 0, 0, atlas).Present, "Outside original physical triangles.");
        for (int occurrence = 0; occurrence < 2; occurrence++)
        for (int normalized = 0; normalized < 2; normalized++)
        {
            var capture = new byte[96 * 96 * 16];
            Span<float> values = MemoryMarshal.Cast<byte, float>(capture.AsSpan());
            int[] triangles = new int[2];
            bool exactDomain = true;
            for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                ExpectedSample expected = Expected(occurrence, x, y, atlas);
                if (!expected.Present) continue;
                triangles[expected.Triangle]++;
                int start = (y * 96 + x) * 4;
                double u = normalized == 0 ? expected.TexelX : expected.NormalizedX;
                double v = normalized == 0 ? expected.TexelY : expected.NormalizedY;
                exactDomain &= (double)(float)u == u && (double)(float)v == v &&
                    (double)(float)expected.Coverage == expected.Coverage;
                values[start] = (float)u; values[start + 1] = (float)v; values[start + 2] = (float)expected.Coverage;
                // Synthetic checker schema only, not a manufactured gamma oracle.
                values[start + 3] = expected.Coverage == 0 ? 0 : .5f;
            }
            Check(exactDomain, "Entire oracle domain is exactly float-representable.");
            Check(triangles.All(count => count != 0) && triangles.Sum() == 256, "Both original triangles retain their exact area inventory.");
            Check(AffineCoverageOracle.Check("schema", capture, 96, occurrence, atlas, normalized != 0).DifferentComponents == 0,
                "Synthetic exact-output schema control.");
            values[0] = float.NaN;
            Check(AffineCoverageOracle.Check("outside-corruption", capture, 96, occurrence, atlas, normalized != 0).DifferentComponents == 1,
                "Nonfinite outside output cannot disappear.");
            values[0] = 0;
            int point = (38 * 96 + (occurrence == 0 ? 48 : 54)) * 4;
            values[point + 2] = MathF.BitIncrement(values[point + 2]);
            Check(AffineCoverageOracle.Check("coverage-corruption", capture, 96, occurrence, atlas, normalized != 0).DifferentComponents == 1,
                "One-bit coverage error must fail without tolerance.");
        }
        return passed;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
    }
}
