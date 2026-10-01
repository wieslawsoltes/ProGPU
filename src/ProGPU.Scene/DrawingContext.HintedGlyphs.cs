using System.Numerics;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene;

public partial class DrawingContext
{
    /// <summary>
    /// Measures the selected original physical outlines in their retained writer
    /// positions plus origin. This is ink, not advance/input or raster-padding
    /// bounds. No-ink selections succeed with hasInk false and default bounds;
    /// invalid/nonfinite mappings return false without publishing partial bounds.
    /// </summary>
    public static bool TryGetHintedGlyphInkBounds(HintedGlyphGeometry geometry,
        Vector2 origin, out Rect bounds, out bool hasInk)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        bounds = default;
        hasInk = false;
        using IDisposable use = geometry.RetainForRecording();
        var command = new RenderCommand
        {
            Type = RenderCommandType.DrawHintedGlyphs,
            HintedGlyphGeometry = geometry,
            GlyphRangeCount = geometry.OccurrenceCount,
            Position = origin
        };
        if (!HintedGlyphCommandGeometry.TryGetInkBounds(command, out Rect measured, out bool ink))
            return false;
        bounds = measured;
        hasInk = ink;
        return true;
    }

    /// <summary>
    /// Records every original positioned occurrence, without shaping, font
    /// decoding or pixel snapping. inkBounds is the source's unchanged logical
    /// brush/ink domain including origin, before the optional finite translation.
    /// Replay requires the exact prepared target DPI and an identity basis.
    /// </summary>
    public void DrawHintedGlyphs(HintedGlyphGeometry geometry, Vector2 origin,
        Rect inkBounds, Brush brush, Matrix4x4 transform = default,
        TextRenderingMode textRenderingMode = TextRenderingMode.Grayscale)
        => DrawHintedGlyphs(geometry, origin, inkBounds, brush, transform, textRenderingMode, 0);

    /// <summary>Records original glyphs with the source's unchanged hit owner.</summary>
    public void DrawHintedGlyphs(HintedGlyphGeometry geometry, Vector2 origin,
        Rect inkBounds, Brush brush, Matrix4x4 transform,
        TextRenderingMode textRenderingMode, int hitTestId)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        DrawHintedGlyphsRange(geometry, 0, geometry.OccurrenceCount, origin,
            inkBounds, brush, transform, textRenderingMode, hitTestId);
    }

    /// <summary>Records an explicit occurrence range; a zero count is empty.</summary>
    public void DrawHintedGlyphsRange(HintedGlyphGeometry geometry, int start, int count,
        Vector2 origin, Rect inkBounds, Brush brush, Matrix4x4 transform = default,
        TextRenderingMode textRenderingMode = TextRenderingMode.Grayscale)
        => DrawHintedGlyphsRange(geometry, start, count, origin, inkBounds, brush,
            transform, textRenderingMode, 0);

    /// <summary>Records an explicit range with the source's unchanged hit owner.</summary>
    public void DrawHintedGlyphsRange(HintedGlyphGeometry geometry, int start, int count,
        Vector2 origin, Rect inkBounds, Brush brush, Matrix4x4 transform,
        TextRenderingMode textRenderingMode, int hitTestId)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(brush);
        geometry.EnsureRecordingAdmission();
        Matrix4x4 actualTransform = transform == default ? Matrix4x4.Identity : transform;
        var command = new RenderCommand
        {
            Type = RenderCommandType.DrawHintedGlyphs,
            HintedGlyphGeometry = geometry, GlyphRangeStart = start, GlyphRangeCount = count,
            Position = origin, Rect = inkBounds, Brush = brush,
            Transform = transform, TextRenderingMode = textRenderingMode, HitTestId = hitTestId
        };
        if (!HintedGlyphCommandGeometry.IsIdentityBasis(actualTransform) ||
            !HintedGlyphCommandGeometry.TryValidate(command, geometry.DpiScale,
                new Matrix3x2(1f, 0f, 0f, 1f, actualTransform.M41, actualTransform.M42),
                out _, out _))
            throw new ArgumentException("Original hinted geometry requires a valid explicit range, unchanged source ink bounds and finite identity frame.");
        if (count == 0) return;

        // Reserve before acquiring ownership. A command callback may throw
        // after Add has published; keep that command's resource retained until
        // the caller clears/disposes the context instead of leaving a dangling
        // reference. All ordinary validation failures precede publication.
        Commands.EnsureCapacity(checked(Commands.Count + 1));
        if (!HasRetainedResourceIdentity(geometry))
        {
            IDisposable sourceUse = geometry.RetainForRecording();
            RetainedResourceLease? retained = null;
            try
            {
                retained = RetainedResourceLease.Create(sourceUse, geometry);
                (_retainedResources ??= new List<RetainedResourceLease>()).Add(retained);
            }
            catch (Exception failure)
            {
                try { if (retained is null) sourceUse.Dispose(); else retained.Dispose(); }
                catch (Exception cleanup)
                {
                    try { failure.Data["HintedGlyphRecordingCleanupFailure"] = cleanup; }
                    catch { } // Diagnostics cannot replace the recording error.
                }
                throw;
            }
        }
        Commands.Add(command);
    }
}
