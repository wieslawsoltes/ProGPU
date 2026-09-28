using System.Numerics;

namespace ProGPU.Scene;

internal static class PathAtlasPixelMapping
{
    // An admitted quad is a translation between two integer pixel lattices.
    // Check every corner, not just an axis-aligned bounding box or a scale label.
    // The exact integer range also makes subtraction and WGSL i32 transport safe.
    internal static bool IsExact(ReadOnlySpan<Vector2> positions, ReadOnlySpan<Vector2> atlas)
    {
        if (positions.Length != 4 || atlas.Length != 4) return false;
        Vector2 offset = atlas[0] - positions[0];
        for (int i = 0; i < 4; i++)
        {
            if (!IsIntegerPoint(positions[i]) || !IsIntegerPoint(atlas[i])
                || atlas[i] - positions[i] != offset)
                return false;
        }
        return true;
    }

    private static bool IsIntegerPoint(Vector2 point) =>
        MathF.Abs(point.X) <= 8388608f && MathF.Abs(point.Y) <= 8388608f
        && MathF.Truncate(point.X) == point.X && MathF.Truncate(point.Y) == point.Y;
}
