using System;
using System.Numerics;

namespace ProGPU.Text;

public unsafe partial class GlyphAtlas
{
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
                    if (useSimd &&
                        System.Numerics.Vector.IsHardwareAccelerated)
                    {
                        coveredSamples += CountCoveredSamplesSimd(
                            pixelX,
                            glyphY,
                            scale,
                            subpixelX,
                            record,
                            segments);
                    }
                    else
                    {
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
                }

                uint value = (uint)MathF.Round(
                    coveredSamples * 3.984375f,
                    MidpointRounding.AwayFromZero);
                coverage[checked((int)(y * width + x))] =
                    (byte)Math.Min(value, 255U);
            }
        }

        return coverage;
    }

    private static uint CountCoveredSamplesSimd(
        float pixelX,
        float sampleY,
        float scale,
        float subpixelX,
        GpuGlyphRecord record,
        ReadOnlySpan<GpuSegment> segments)
    {
        int laneCount = Vector<float>.Count;
        Span<float> sampleScratch = stackalloc float[laneCount];
        uint covered = 0;
        for (int sampleOffset = 0; sampleOffset < 8;
             sampleOffset += laneCount)
        {
            int activeLanes = Math.Min(laneCount, 8 - sampleOffset);
            sampleScratch.Fill(float.PositiveInfinity);
            for (int lane = 0; lane < activeLanes; lane++)
            {
                sampleScratch[lane] = (
                    pixelX + 0.0625f +
                    (sampleOffset + lane) * 0.125f - subpixelX) / scale;
            }
            var sampleXs = new Vector<float>(sampleScratch);
            Vector<int> windings = GlyphWindingRowSimd(
                sampleXs,
                sampleY,
                record,
                segments);
            for (int lane = 0; lane < activeLanes; lane++)
            {
                covered += windings[lane] != 0 ? 1U : 0U;
            }
        }
        return covered;
    }

    private static Vector<int> GlyphWindingRowSimd(
        Vector<float> sampleXs,
        float sampleY,
        GpuGlyphRecord record,
        ReadOnlySpan<GpuSegment> segments)
    {
        Vector<int> windings = Vector<int>.Zero;
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
                    AccumulateCrossingSimd(
                        a.X + t * (b.X - a.X),
                        1,
                        sampleXs,
                        ref windings);
                }
                else if (a.Y > sampleY && b.Y <= sampleY)
                {
                    float t = (sampleY - a.Y) / (b.Y - a.Y);
                    AccumulateCrossingSimd(
                        a.X + t * (b.X - a.X),
                        -1,
                        sampleXs,
                        ref windings);
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
                    AccumulateCrossingSimd(
                        crossingX, direction, sampleXs, ref windings);
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
                AccumulateCrossingSimd(
                    crossingX, direction, sampleXs, ref windings);
            }
        }
        return windings;
    }

    private static void AccumulateCrossingSimd(
        float crossingX,
        int direction,
        Vector<float> sampleXs,
        ref Vector<int> windings)
    {
        if (direction == 0)
        {
            return;
        }
        Vector<int> mask = System.Numerics.Vector.LessThan(
            sampleXs,
            new Vector<float>(crossingX));
        windings += System.Numerics.Vector.BitwiseAnd(
            mask,
            new Vector<int>(direction));
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
