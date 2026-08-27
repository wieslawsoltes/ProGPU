using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ProGPU.Text;

public unsafe partial class GlyphAtlas
{
    private readonly record struct CpuCrossing(float X, int Direction);

    private static readonly Vector128<float> s_simdLaneIndices128 =
        Vector128.Create(0F, 1F, 2F, 3F);

    private static readonly Vector256<float> s_simdLaneIndices256 =
        Vector256.Create(0F, 1F, 2F, 3F, 4F, 5F, 6F, 7F);

    internal static byte[] RasterizeGlyphCoverageCpu(
        ReadOnlySpan<GpuSegment> segments,
        GpuGlyphRecord record,
        int xStart,
        int yStart,
        float scale,
        float subpixelX,
        uint width,
        uint height,
        bool useSimd)
    {
        byte[] coverage = new byte[checked((int)(width * height))];
        bool useIntrinsicSimd = useSimd &&
            Vector128.IsHardwareAccelerated;
        if (!useIntrinsicSimd)
        {
            RasterizeGlyphCoverageScalar(
                segments,
                record,
                xStart,
                yStart,
                scale,
                subpixelX,
                width,
                height,
                coverage);
            return coverage;
        }

        int maximumCrossings = checked((int)record.SegmentCount * 3);
        CpuCrossing[] crossingArray =
            ArrayPool<CpuCrossing>.Shared.Rent(Math.Max(maximumCrossings, 1));
        try
        {
            float inverseScale = 1f / scale;
            float glyphSampleStep = 0.125f * inverseScale;
            for (uint y = 0; y < height; y++)
            {
                float pixelY = yStart + y;
                Span<byte> coveredRow = coverage.AsSpan(
                    checked((int)(y * width)),
                    checked((int)width));
                for (uint sampleY = 0; sampleY < 8; sampleY++)
                {
                    float glyphY = -(
                        pixelY + 0.0625f + sampleY * 0.125f) * inverseScale;
                    int crossingCount = CollectGlyphCrossingsCpu(
                        glyphY,
                        record,
                        segments,
                        crossingArray);
                    ReadOnlySpan<CpuCrossing> crossings =
                        crossingArray.AsSpan(0, crossingCount);
                    for (uint x = 0; x < width; x++)
                    {
                        float firstGlyphX = (
                            xStart + x + 0.0625f - subpixelX) * inverseScale;
                        coveredRow[checked((int)x)] += (byte)
                            CountCoveredSamplesSimd(
                                firstGlyphX,
                                glyphSampleStep,
                                crossings);
                    }
                }

                NormalizeCoverageRow(coveredRow);
            }
        }
        finally
        {
            ArrayPool<CpuCrossing>.Shared.Return(crossingArray);
        }

        return coverage;
    }

    private static uint CountCoveredSamplesSimd(
        float firstSampleX,
        float sampleStep,
        ReadOnlySpan<CpuCrossing> crossings)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> sampleXs =
                Vector256.Create(firstSampleX) +
                s_simdLaneIndices256 * Vector256.Create(sampleStep);
            Vector256<int> windings = Vector256<int>.Zero;
            foreach (CpuCrossing crossing in crossings)
            {
                Vector256<int> mask = Vector256.LessThan(
                    sampleXs,
                    Vector256.Create(crossing.X)).AsInt32();
                windings = crossing.Direction > 0
                    ? windings - mask
                    : windings + mask;
            }

            uint zeroMask = Vector256.ExtractMostSignificantBits(
                Vector256.Equals(windings, Vector256<int>.Zero));
            return 8U - (uint)BitOperations.PopCount(zeroMask);
        }

        Vector128<float> samplesLow =
            Vector128.Create(firstSampleX) +
            s_simdLaneIndices128 * Vector128.Create(sampleStep);
        Vector128<float> samplesHigh = samplesLow +
            Vector128.Create(4F * sampleStep);
        Vector128<int> windingsLow = Vector128<int>.Zero;
        Vector128<int> windingsHigh = Vector128<int>.Zero;
        foreach (CpuCrossing crossing in crossings)
        {
            Vector128<int> lowMask = Vector128.LessThan(
                samplesLow,
                Vector128.Create(crossing.X)).AsInt32();
            Vector128<int> highMask = Vector128.LessThan(
                samplesHigh,
                Vector128.Create(crossing.X)).AsInt32();
            if (crossing.Direction > 0)
            {
                windingsLow -= lowMask;
                windingsHigh -= highMask;
            }
            else
            {
                windingsLow += lowMask;
                windingsHigh += highMask;
            }
        }

        uint lowZeroMask = Vector128.ExtractMostSignificantBits(
            Vector128.Equals(windingsLow, Vector128<int>.Zero));
        uint highZeroMask = Vector128.ExtractMostSignificantBits(
            Vector128.Equals(windingsHigh, Vector128<int>.Zero));
        return 8U - (uint)BitOperations.PopCount(
            lowZeroMask | (highZeroMask << 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte CoverageFromSampleCount(byte coveredSamples)
    {
        // Positive round-away-from-zero for coveredSamples * 255 / 64.
        // The integer form is bit-identical for the bounded [0, 64] domain.
        return (byte)((coveredSamples * 255U + 32U) >> 6);
    }

    private static void NormalizeCoverageRow(Span<byte> coveredRow)
    {
        int index = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte start = ref System.Runtime.InteropServices.MemoryMarshal
                .GetReference(coveredRow);
            Vector128<ushort> multiplier = Vector128.Create((ushort)255);
            Vector128<ushort> rounding = Vector128.Create((ushort)32);
            for (; index <= coveredRow.Length - Vector128<byte>.Count;
                 index += Vector128<byte>.Count)
            {
                (Vector128<ushort> low, Vector128<ushort> high) =
                    Vector128.Widen(
                        Vector128.LoadUnsafe(ref start, (nuint)index));
                low = (low * multiplier + rounding) >> 6;
                high = (high * multiplier + rounding) >> 6;
                Vector128.Narrow(low, high).StoreUnsafe(
                    ref start,
                    (nuint)index);
            }
        }

        for (; index < coveredRow.Length; index++)
        {
            coveredRow[index] = CoverageFromSampleCount(coveredRow[index]);
        }
    }

    private static int CollectGlyphCrossingsCpu(
        float sampleY,
        GpuGlyphRecord record,
        ReadOnlySpan<GpuSegment> segments,
        Span<CpuCrossing> crossings)
    {
        int crossingCount = 0;
        uint end = checked(record.StartSegment + record.SegmentCount);
        for (uint index = record.StartSegment; index < end; index++)
        {
            GpuSegment segment = segments[checked((int)index)];
            Vector2 a = segment.P0;
            Vector2 b = segment.P1;
            if (segment.SegmentType == 0U)
            {
                if (a.Y <= sampleY && b.Y > sampleY)
                {
                    float t = (sampleY - a.Y) / (b.Y - a.Y);
                    crossings[crossingCount++] = new(
                        a.X + t * (b.X - a.X), 1);
                }
                else if (a.Y > sampleY && b.Y <= sampleY)
                {
                    float t = (sampleY - a.Y) / (b.Y - a.Y);
                    crossings[crossingCount++] = new(
                        a.X + t * (b.X - a.X), -1);
                }
                continue;
            }

            Vector2 c = segment.P2;
            if (segment.SegmentType == 1U)
            {
                int rootCount = SolveQuadraticCpu(
                    a.Y - 2f * b.Y + c.Y,
                    2f * (b.Y - a.Y),
                    a.Y - sampleY,
                    out float root0,
                    out float root1);
                for (int rootIndex = 0; rootIndex < rootCount; rootIndex++)
                {
                    float t = rootIndex == 0 ? root0 : root1;
                    if (t < -0.01f || t > 1.01f)
                    {
                        continue;
                    }
                    float evaluatedT = Math.Clamp(t, 0.00001f, 0.99999f);
                    float derivativeY =
                        2f * (1f - evaluatedT) * (b.Y - a.Y) +
                        2f * evaluatedT * (c.Y - b.Y);
                    if (!IsWindingRootValid(
                            t, derivativeY, sampleY, a.Y, c.Y))
                    {
                        continue;
                    }
                    float clampedT = Math.Clamp(t, 0f, 1f);
                    float oneMinusT = 1f - clampedT;
                    float crossingX =
                        oneMinusT * oneMinusT * a.X +
                        2f * oneMinusT * clampedT * b.X +
                        clampedT * clampedT * c.X;
                    int direction = derivativeY > 0f
                        ? 1
                        : derivativeY < 0f ? -1 : 0;
                    if (direction != 0)
                    {
                        crossings[crossingCount++] = new(
                            crossingX, direction);
                    }
                }
                continue;
            }

            Vector2 d = segment.P3;
            float ca = -a.Y + 3f * b.Y - 3f * c.Y + d.Y;
            float cb = 3f * a.Y - 6f * b.Y + 3f * c.Y;
            float cc = -3f * a.Y + 3f * b.Y;
            int cubicRootCount = SolveCubicCpu(
                ca,
                cb,
                cc,
                a.Y - sampleY,
                out float cubicRoot0,
                out float cubicRoot1,
                out float cubicRoot2);
            for (int rootIndex = 0; rootIndex < cubicRootCount; rootIndex++)
            {
                float t = rootIndex switch
                {
                    0 => cubicRoot0,
                    1 => cubicRoot1,
                    _ => cubicRoot2
                };
                if (t < -0.01f || t > 1.01f)
                {
                    continue;
                }
                float evaluatedT = Math.Clamp(t, 0.00001f, 0.99999f);
                float derivativeY =
                    3f * ca * evaluatedT * evaluatedT +
                    2f * cb * evaluatedT + cc;
                if (!IsWindingRootValid(t, derivativeY, sampleY, a.Y, d.Y))
                {
                    continue;
                }
                float clampedT = Math.Clamp(t, 0f, 1f);
                float oneMinusT = 1f - clampedT;
                float crossingX =
                    oneMinusT * oneMinusT * oneMinusT * a.X +
                    3f * oneMinusT * oneMinusT * clampedT * b.X +
                    3f * oneMinusT * clampedT * clampedT * c.X +
                    clampedT * clampedT * clampedT * d.X;
                int direction = derivativeY > 0f
                    ? 1
                    : derivativeY < 0f ? -1 : 0;
                if (direction != 0)
                {
                    crossings[crossingCount++] = new(crossingX, direction);
                }
            }
        }
        return crossingCount;
    }

    private static void RasterizeGlyphCoverageScalar(
        ReadOnlySpan<GpuSegment> segments,
        GpuGlyphRecord record,
        int xStart,
        int yStart,
        float scale,
        float subpixelX,
        uint width,
        uint height,
        Span<byte> coverage)
    {
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                uint coveredSamples = 0;
                float pixelX = xStart + x;
                float pixelY = yStart + y;
                for (uint sampleY = 0; sampleY < 8; sampleY++)
                {
                    float glyphY = -(
                        pixelY + 0.0625f + sampleY * 0.125f) / scale;
                    for (uint sampleX = 0; sampleX < 8; sampleX++)
                    {
                        float glyphX = (
                            pixelX + 0.0625f + sampleX * 0.125f -
                            subpixelX) / scale;
                        if (GlyphWindingCpu(
                                glyphX,
                                glyphY,
                                record,
                                segments) != 0)
                        {
                            coveredSamples++;
                        }
                    }
                }

                uint value = (uint)MathF.Round(
                    coveredSamples * 3.984375f,
                    MidpointRounding.AwayFromZero);
                coverage[checked((int)(y * width + x))] =
                    (byte)Math.Min(value, 255U);
            }
        }
    }

    private static int GlyphWindingCpu(
        float sampleX,
        float sampleY,
        GpuGlyphRecord record,
        ReadOnlySpan<GpuSegment> segments)
    {
        int winding = 0;
        uint end = checked(record.StartSegment + record.SegmentCount);
        for (uint index = record.StartSegment; index < end; index++)
        {
            GpuSegment segment = segments[checked((int)index)];
            Vector2 a = segment.P0;
            Vector2 b = segment.P1;
            if (segment.SegmentType == 0U)
            {
                if (a.Y == b.Y)
                {
                    continue;
                }

                if (a.Y <= sampleY && b.Y > sampleY)
                {
                    float t = (sampleY - a.Y) / (b.Y - a.Y);
                    float crossingX = a.X + t * (b.X - a.X);
                    winding += sampleX < crossingX ? 1 : 0;
                }
                else if (a.Y > sampleY && b.Y <= sampleY)
                {
                    float t = (sampleY - a.Y) / (b.Y - a.Y);
                    float crossingX = a.X + t * (b.X - a.X);
                    winding -= sampleX < crossingX ? 1 : 0;
                }
                continue;
            }

            Vector2 c = segment.P2;
            if (segment.SegmentType == 1U)
            {
                int rootCount = SolveQuadraticCpu(
                    a.Y - 2f * b.Y + c.Y,
                    2f * (b.Y - a.Y),
                    a.Y - sampleY,
                    out float quadraticRoot0,
                    out float quadraticRoot1);
                for (int rootIndex = 0; rootIndex < rootCount; rootIndex++)
                {
                    float t = rootIndex == 0
                        ? quadraticRoot0
                        : quadraticRoot1;
                    if (t < -0.01f || t > 1.01f)
                    {
                        continue;
                    }
                    float evaluatedT = Math.Clamp(t, 0.00001f, 0.99999f);
                    float derivativeY =
                        2f * (1f - evaluatedT) * (b.Y - a.Y) +
                        2f * evaluatedT * (c.Y - b.Y);
                    if (!IsWindingRootValid(
                            t,
                            derivativeY,
                            sampleY,
                            a.Y,
                            c.Y))
                    {
                        continue;
                    }
                    float clampedT = Math.Clamp(t, 0f, 1f);
                    float oneMinusT = 1f - clampedT;
                    float crossingX =
                        oneMinusT * oneMinusT * a.X +
                        2f * oneMinusT * clampedT * b.X +
                        clampedT * clampedT * c.X;
                    winding += sampleX < crossingX
                        ? derivativeY > 0f ? 1 : derivativeY < 0f ? -1 : 0
                        : 0;
                }
                continue;
            }

            Vector2 d = segment.P3;
            float ca = -a.Y + 3f * b.Y - 3f * c.Y + d.Y;
            float cb = 3f * a.Y - 6f * b.Y + 3f * c.Y;
            float cc = -3f * a.Y + 3f * b.Y;
            int cubicRootCount = SolveCubicCpu(
                ca,
                cb,
                cc,
                a.Y - sampleY,
                out float root0,
                out float root1,
                out float root2);
            for (int rootIndex = 0; rootIndex < cubicRootCount; rootIndex++)
            {
                float t = rootIndex switch
                {
                    0 => root0,
                    1 => root1,
                    _ => root2
                };
                if (t < -0.01f || t > 1.01f)
                {
                    continue;
                }
                float evaluatedT = Math.Clamp(t, 0.00001f, 0.99999f);
                float derivativeY =
                    3f * ca * evaluatedT * evaluatedT +
                    2f * cb * evaluatedT + cc;
                if (!IsWindingRootValid(
                        t,
                        derivativeY,
                        sampleY,
                        a.Y,
                        d.Y))
                {
                    continue;
                }
                float clampedT = Math.Clamp(t, 0f, 1f);
                float oneMinusT = 1f - clampedT;
                float crossingX =
                    oneMinusT * oneMinusT * oneMinusT * a.X +
                    3f * oneMinusT * oneMinusT * clampedT * b.X +
                    3f * oneMinusT * clampedT * clampedT * c.X +
                    clampedT * clampedT * clampedT * d.X;
                winding += sampleX < crossingX
                    ? derivativeY > 0f ? 1 : derivativeY < 0f ? -1 : 0
                    : 0;
            }
        }

        return winding;
    }

    private static int SolveQuadraticCpu(
        float a,
        float b,
        float c,
        out float root0,
        out float root1)
    {
        root0 = 0f;
        root1 = 0f;
        if (MathF.Abs(a) < 0.00001f)
        {
            if (MathF.Abs(b) <= 0.00001f)
            {
                return 0;
            }
            root0 = -c / b;
            return 1;
        }

        float discriminant = b * b - 4f * a * c;
        if (discriminant == 0f)
        {
            root0 = -b / (2f * a);
            return 1;
        }
        if (discriminant <= 0f)
        {
            return 0;
        }
        float root = MathF.Sqrt(discriminant);
        root0 = (-b - root) / (2f * a);
        root1 = (-b + root) / (2f * a);
        return 2;
    }

    private static int SolveCubicCpu(
        float aIn,
        float bIn,
        float cIn,
        float dIn,
        out float root0,
        out float root1,
        out float root2)
    {
        root0 = 0f;
        root1 = 0f;
        root2 = 0f;
        if (MathF.Abs(aIn) < 0.00001f)
        {
            return SolveQuadraticCpu(bIn, cIn, dIn, out root0, out root1);
        }

        float a = bIn / aIn;
        float b = cIn / aIn;
        float c = dIn / aIn;
        float p = b - a * a / 3f;
        float q = c - a * b / 3f + 2f * a * a * a / 27f;
        float discriminant = q * q / 4f + p * p * p / 27f;
        if (discriminant > 0f)
        {
            float root = MathF.Sqrt(discriminant);
            float u = ShaderCbrtCpu(-q / 2f + root);
            float v = ShaderCbrtCpu(-q / 2f - root);
            root0 = u + v - a / 3f;
            return 1;
        }
        if (p < 0f)
        {
            const float pi = 3.14159265359f;
            float radius = 2f * MathF.Sqrt(-p / 3f);
            float ratio = Math.Clamp(
                -q / (2f * MathF.Sqrt(-p * p * p / 27f)),
                -1f,
                1f);
            float theta = MathF.Acos(ratio);
            root0 = radius * MathF.Cos(theta / 3f) - a / 3f;
            root1 = radius * MathF.Cos((theta + 2f * pi) / 3f) - a / 3f;
            root2 = radius * MathF.Cos((theta + 4f * pi) / 3f) - a / 3f;
            return 3;
        }

        root0 = -a / 3f;
        return 1;
    }

    private static float ShaderCbrtCpu(float value) => value < 0f
        ? -MathF.Pow(-value, 1f / 3f)
        : MathF.Pow(value, 1f / 3f);

    private static bool IsWindingRootValid(
        float t,
        float derivativeY,
        float sampleY,
        float startY,
        float endY)
    {
        if (t < 0.005f)
        {
            return derivativeY > 0f
                ? sampleY >= startY
                : derivativeY < 0f && sampleY < startY;
        }
        if (t > 0.995f)
        {
            return derivativeY > 0f
                ? sampleY < endY
                : derivativeY < 0f && sampleY >= endY;
        }
        return true;
    }
}
