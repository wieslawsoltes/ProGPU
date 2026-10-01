using System.Numerics;
using ProGPU.Text;

namespace ProGPU.Scene;

// Shared recorded/managed/native admission. Physical Y-up geometry is mapped
// only for logical bounds; raster records and positioned occurrences stay exact.
internal static class HintedGlyphCommandGeometry
{
    internal static bool IsIdentityBasis(Matrix4x4 value) =>
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        value == Matrix4x4.CreateTranslation(value.M41, value.M42, 0f);

    internal static bool IsIdentityBasis(Matrix3x2 value) =>
        value.M11 == 1f && value.M12 == 0f && value.M21 == 0f && value.M22 == 1f &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);

    internal static bool TryGetRange(in RenderCommand command, out int start, out int count)
    {
        start = command.GlyphRangeStart;
        count = command.GlyphRangeCount;
        HintedGlyphGeometry? geometry = command.HintedGlyphGeometry;
        return command.Type == RenderCommandType.DrawHintedGlyphs &&
            geometry is not null && geometry.HasRenderStorage &&
            start >= 0 && count >= 0 && start <= geometry.OccurrenceCount &&
            count <= geometry.OccurrenceCount - start;
    }

    internal static bool TryValidate(in RenderCommand command, float targetDpiScale,
        Matrix3x2 effectiveTransform, out int start, out int count)
    {
        start = count = 0;
        if (!TryGetRange(command, out start, out count) ||
            BitConverter.SingleToInt32Bits(command.HintedGlyphGeometry!.DpiScale) !=
                BitConverter.SingleToInt32Bits(targetDpiScale) ||
            !IsIdentityBasis(effectiveTransform) || command.UseGpuTransforms ||
            command.Rotation != 0f || command.IsBold || command.IsItalic ||
            command.HasFontTransform ||
            command.TextRenderingMode is not (TextRenderingMode.Grayscale or TextRenderingMode.Aliased) ||
            !IsFiniteNonnegative(command.Rect) ||
            !TryGetInkBounds(command, out Rect ink, out bool hasInk))
            return false;
        if (hasInk && (ink.X < command.Rect.X || ink.Y < command.Rect.Y ||
            ink.Right > command.Rect.Right || ink.Bottom > command.Rect.Bottom))
            return false;
        // Translation is admitted but every selected ink endpoint must remain
        // finite in the actual target frame. No epsilon or resnap is used.
        return !hasInk || (float.IsFinite(ink.X + effectiveTransform.M31) &&
            float.IsFinite(ink.Right + effectiveTransform.M31) &&
            float.IsFinite(ink.Y + effectiveTransform.M32) &&
            float.IsFinite(ink.Bottom + effectiveTransform.M32));
    }

    internal static bool TryGetInkBounds(in RenderCommand command, out Rect bounds, out bool hasInk)
    {
        bounds = default;
        hasInk = false;
        if (!TryGetRange(command, out int start, out int count) ||
            !float.IsFinite(command.Position.X) || !float.IsFinite(command.Position.Y))
            return false;
        HintedGlyphGeometry geometry = command.HintedGlyphGeometry!;
        ReadOnlySpan<HintedGlyphOccurrence> occurrences = geometry.RenderOccurrences;
        ReadOnlySpan<GpuGlyphRecord> outlines = geometry.RenderOutlines;
        float inverseDpi = 1f / geometry.DpiScale;
        Vector2 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        int end = start + count; // Range admission proves this sum fits.
        for (int index = start; index < end; index++)
        {
            HintedGlyphOccurrence occurrence = occurrences[index];
            Vector2 origin = occurrence.Position + command.Position;
            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y)) return false;
            if (occurrence.OutlineIndex == uint.MaxValue) continue;
            if (occurrence.OutlineIndex >= (uint)outlines.Length) return false;
            GpuGlyphRecord outline = outlines[(int)occurrence.OutlineIndex];
            if (outline.SegmentCount == 0 ||
                !float.IsFinite(outline.MinX) || !float.IsFinite(outline.MaxX) ||
                !float.IsFinite(outline.MinY) || !float.IsFinite(outline.MaxY) ||
                outline.MinX > outline.MaxX || outline.MinY > outline.MaxY)
                return false;
            Vector2 low = origin + new Vector2(outline.MinX, -outline.MaxY) * inverseDpi;
            Vector2 high = origin + new Vector2(outline.MaxX, -outline.MinY) * inverseDpi;
            if (!float.IsFinite(low.X) || !float.IsFinite(low.Y) ||
                !float.IsFinite(high.X) || !float.IsFinite(high.Y)) return false;
            minimum = Vector2.Min(minimum, low);
            maximum = Vector2.Max(maximum, high);
            hasInk = true;
        }
        if (hasInk) bounds = new Rect(minimum, maximum - minimum);
        return !hasInk || IsFiniteNonnegative(bounds);
    }

    private static bool IsFiniteNonnegative(Rect bounds) =>
        float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height) &&
        bounds.Width >= 0f && bounds.Height >= 0f &&
        float.IsFinite(bounds.Right) && float.IsFinite(bounds.Bottom);

    // Private culling/coverage storage only. This is not glyph input geometry
    // or a replacement for the authoritative source brush/ink domain.
    internal static bool TryGetRasterBounds(in RenderCommand command, out Rect bounds, out bool hasInk)
    {
        bounds = default;
        hasInk = false;
        if (!TryGetRange(command, out int start, out int count)) return false;
        HintedGlyphGeometry geometry = command.HintedGlyphGeometry!;
        ReadOnlySpan<HintedGlyphOccurrence> occurrences = geometry.RenderOccurrences;
        ReadOnlySpan<GpuGlyphRecord> outlines = geometry.RenderOutlines;
        float inverseDpi = 1f / geometry.DpiScale;
        Vector2 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        try
        {
            for (int index = start; index < start + count; index++)
            {
                HintedGlyphOccurrence occurrence = occurrences[index];
                if (occurrence.OutlineIndex == uint.MaxValue) continue;
                if (occurrence.OutlineIndex >= (uint)outlines.Length) return false;
                GpuGlyphRecord outline = outlines[(int)occurrence.OutlineIndex];
                var raster = GlyphAtlas.GetHintedRasterBounds(in outline);
                Vector2 origin = occurrence.Position + command.Position;
                Vector2 offset = new Vector2(raster.XStart, raster.YStart) * inverseDpi;
                Vector2 extent = new Vector2(raster.Width, raster.Height) * inverseDpi;
                Vector2 low = origin + offset;
                // Match Text.wgsl: form the physical offset plus extent before
                // adding the original logical position. Reassociation changes
                // endpoints at large/fractional representable source frames.
                Vector2 high = origin + (offset + extent);
                if (!float.IsFinite(low.X) || !float.IsFinite(low.Y) ||
                    !float.IsFinite(high.X) || !float.IsFinite(high.Y)) return false;
                minimum = Vector2.Min(minimum, low);
                maximum = Vector2.Max(maximum, high);
                hasInk = true;
            }
        }
        catch (OverflowException) { return false; }
        if (hasInk) bounds = new Rect(minimum, maximum - minimum);
        return !hasInk || IsFiniteNonnegative(bounds);
    }
}
