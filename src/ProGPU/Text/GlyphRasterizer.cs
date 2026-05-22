using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Text;

public struct RasterGlyph
{
    public int Width;
    public int Height;
    public int BearX;
    public int BearY;
    public byte[] AlphaMap;
}

public static class GlyphRasterizer
{
    public const float SdfBaseSize = 64f;

    public static RasterGlyph Rasterize(PathGeometry outline, TtfFont font, float emSize = SdfBaseSize)
    {
        // 1. Calculate scaling factors using the fixed SdfBaseSize
        float scale = SdfBaseSize / font.UnitsPerEm;

        // Extract and flatten contours, scaling coordinates with a fine tolerance of 0.15f
        var flattenedContours = outline.Flatten(0.15f);
        var scaledContours = new List<List<Vector2>>();
        
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        // Convert TTF coordinates (Y-up) to screen coordinates (Y-down) and find bounds
        foreach (var contour in flattenedContours)
        {
            var scaledContour = new List<Vector2>(contour.Count);
            foreach (var pt in contour)
            {
                var spt = new Vector2(pt.X * scale, -pt.Y * scale);
                scaledContour.Add(spt);

                minX = Math.Min(minX, spt.X);
                maxX = Math.Max(maxX, spt.X);
                minY = Math.Min(minY, spt.Y);
                maxY = Math.Max(maxY, spt.Y);
            }
            scaledContours.Add(scaledContour);
        }

        // If glyph is empty
        if (scaledContours.Count == 0 || minX > maxX || minY > maxY)
        {
            return new RasterGlyph { Width = 0, Height = 0, BearX = 0, BearY = 0, AlphaMap = Array.Empty<byte>() };
        }

        // 2. Add padding/margin of 8px on all sides of the glyph bounding box
        int padding = 8;
        int xStart = (int)Math.Floor(minX) - padding;
        int xEnd = (int)Math.Ceiling(maxX) + padding;
        int yStart = (int)Math.Floor(minY) - padding;
        int yEnd = (int)Math.Ceiling(maxY) + padding;

        int width = xEnd - xStart;
        int height = yEnd - yStart;

        byte[] alphaMap = new byte[width * height];

        // 3. Ray-casting polygon intersection checker with Even-Odd rule
        bool IsPointInContours(Vector2 p)
        {
            bool inside = false;
            foreach (var contour in scaledContours)
            {
                int count = contour.Count;
                for (int i = 0, j = count - 1; i < count; j = i++)
                {
                    Vector2 v1 = contour[i];
                    Vector2 v2 = contour[j];

                    if (((v1.Y > p.Y) != (v2.Y > p.Y)) &&
                        (p.X < (v2.X - v1.X) * (p.Y - v1.Y) / (v2.Y - v1.Y) + v1.X))
                    {
                        inside = !inside;
                    }
                }
            }
            return inside;
        }

        // 4. Compute SDF: For each pixel, compute the exact signed distance to all line segments
        float spread = 8.0f;

        for (int y = 0; y < height; y++)
        {
            float pixelY = yStart + y + 0.5f; // Sample at pixel center
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                float pixelX = xStart + x + 0.5f; // Sample at pixel center
                Vector2 p = new Vector2(pixelX, pixelY);

                float minDistanceSq = float.MaxValue;

                foreach (var contour in scaledContours)
                {
                    int count = contour.Count;
                    if (count < 2) continue;

                    bool isExplicitlyClosed = contour[0] == contour[count - 1];
                    int numSegments = isExplicitlyClosed ? count - 1 : count;

                    for (int i = 0; i < numSegments; i++)
                    {
                        Vector2 v1 = contour[i];
                        Vector2 v2 = contour[(i + 1) % count];
                        if (v1 == v2) continue;

                        float distSq = SegmentDistanceSquared(p, v1, v2);
                        if (distSq < minDistanceSq)
                        {
                            minDistanceSq = distSq;
                        }
                    }
                }

                float minDist = (minDistanceSq == float.MaxValue) ? 0f : (float)Math.Sqrt(minDistanceSq);
                bool inside = IsPointInContours(p);

                float signedDist = inside ? minDist : -minDist;
                float sdf = 0.5f + 0.5f * (signedDist / spread);
                float clampedSdf = Math.Clamp(sdf, 0.0f, 1.0f);

                alphaMap[rowOffset + x] = (byte)Math.Round(clampedSdf * 255.0f);
            }
        }

        return new RasterGlyph
        {
            Width = width,
            Height = height,
            BearX = xStart,
            BearY = yStart,
            AlphaMap = alphaMap
        };
    }

    private static float SegmentDistanceSquared(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        Vector2 ap = p - a;
        float abLenSq = ab.LengthSquared();
        if (abLenSq < 1e-6f)
        {
            return ap.LengthSquared();
        }
        float t = Vector2.Dot(ap, ab) / abLenSq;
        t = Math.Clamp(t, 0f, 1f);
        Vector2 closest = a + t * ab;
        return Vector2.DistanceSquared(p, closest);
    }
}
