using System.Numerics;

namespace ProGPU.Scene;

/// <summary>Shared raster-origin policy; original layout arrays remain caller-owned.</summary>
internal static class IntegerGlyphRasterOrigin
{
    internal static bool TryResolve(Vector2 logical, float dpi, out Vector2 origin)
    {
        origin = default;
        if (!float.IsFinite(dpi) || dpi <= 0 ||
            !float.IsFinite(logical.X) || !float.IsFinite(logical.Y)) return false;
        Vector2 physical = logical * dpi;
        if (!float.IsFinite(physical.X) || !float.IsFinite(physical.Y)) return false;
        Vector2 candidate = new Vector2(Round(physical.X), Round(physical.Y)) / dpi;
        if (!float.IsFinite(candidate.X) || !float.IsFinite(candidate.Y)) return false;
        origin = candidate;
        return true;
    }

    private static float Round(float value)
    {
        // Adding .5 before flooring would change large, already-integral floats.
        // The fractional comparison preserves those values and signed half ties.
        float floor = MathF.Floor(value);
        return value - floor >= .5f ? floor + 1f : floor;
    }

    internal static bool IsDescriptorSupported(in RenderCommand command, Matrix4x4 frame, float dpi)
    {
        if (!float.IsFinite(dpi) || dpi <= 0f ||
            !float.IsFinite(command.Position.X) || !float.IsFinite(command.Position.Y)) return false;
        if (command.Type != RenderCommandType.DrawGlyphRun ||
            command.TextRenderingMode != TextRenderingMode.Aliased ||
            !command.PreferGlyphAtlas || command.UseVectorGlyphRendering ||
            command.UseLogicalGlyphAtlasResolution || command.Rotation != 0f ||
            command.IsBold || command.IsItalic || command.HasFontTransform ||
            command.Font is not { HasCffOutlines: false, HasColorGlyphs: false, HasBitmapGlyphs: false } ||
            !float.IsFinite(command.FontSize) || command.FontSize <= 0f ||
            frame.M11 <= 0f || frame.M22 <= 0f || !float.IsFinite(frame.M11) || !float.IsFinite(frame.M22) ||
            frame.M12 != 0f || frame.M13 != 0f || frame.M14 != 0f ||
            frame.M21 != 0f || frame.M23 != 0f || frame.M24 != 0f ||
            frame.M31 != 0f || frame.M32 != 0f || frame.M33 != 1f || frame.M34 != 0f ||
            frame.M43 != 0f || frame.M44 != 1f || !float.IsFinite(frame.M41) || !float.IsFinite(frame.M42) ||
            command.GlyphIndices is not { } indices || command.GlyphPositions is not { } positions)
            return false;
        int start = command.GlyphRangeCount > 0 ? command.GlyphRangeStart : 0;
        int count = command.GlyphRangeCount > 0 ? command.GlyphRangeCount : Math.Min(indices.Length, positions.Length);
        if (start < 0 || count <= 0 || start > indices.Length - count || start > positions.Length - count)
            return false;
        if (!float.IsFinite(command.FontSize * dpi * frame.M11) ||
            !float.IsFinite(command.FontSize * dpi * frame.M22)) return false;
        return true;
    }

    internal static bool IsSupported(in RenderCommand command, Matrix4x4 frame, float dpi)
    {
        if (!IsDescriptorSupported(command, frame, dpi)) return false;
        Vector2[] positions = command.GlyphPositions!;
        int start = command.GlyphRangeCount > 0 ? command.GlyphRangeStart : 0;
        int count = command.GlyphRangeCount > 0 ? command.GlyphRangeCount :
            Math.Min(command.GlyphIndices!.Length, positions.Length);
        // Whole selected-range validation precedes any atlas/style/list mutation.
        // Recording checks only the descriptor, retaining O(1) borrowed-array publication.
        for (int i = start; i < start + count; i++)
            if (!TryResolve(Vector2.Transform(positions[i] + command.Position, frame), dpi, out _))
                return false;
        return true;
    }
}
