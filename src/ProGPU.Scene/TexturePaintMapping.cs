using System.Numerics;

namespace ProGPU.Scene;

internal static class TexturePaintMapping
{
    // Shared by managed replay and the native scene compiler. The caller has
    // already admitted the original hinted target frame and texture brush.
    internal static bool TryExtendSnappedTextureCommand(RenderCommand sourcePaint,
        Rect storageBounds, Matrix4x4 transform, float dpiScale, out RenderCommand storagePaint)
    {
        // Canonical texture snapping moves destination endpoints but not UVs.
        // Bake that ORIGINAL domain's mapping before enlarging coverage storage;
        // snapping the new storage endpoints would subtly move/scale the brush.
        Vector2 sourceMinimum = SnapPoint(Vector2.Transform(
            new Vector2(sourcePaint.Rect.X, sourcePaint.Rect.Y), transform), dpiScale);
        Vector2 sourceMaximum = SnapPoint(Vector2.Transform(
            new Vector2(sourcePaint.Rect.Right, sourcePaint.Rect.Bottom), transform), dpiScale);
        Vector2 sourceExtent = sourceMaximum - sourceMinimum;
        storagePaint = sourcePaint;
        if (sourceExtent.X <= 0f || sourceExtent.Y <= 0f)
            return false; // Preserve canonical zero-area snapped destination paint.

        Vector2 storageMinimum = Vector2.Transform(new Vector2(storageBounds.X, storageBounds.Y), transform);
        Vector2 storageMaximum = Vector2.Transform(new Vector2(storageBounds.Right, storageBounds.Bottom), transform);
        Vector2 sourceScale = new Vector2(sourcePaint.SrcRect.Width, sourcePaint.SrcRect.Height) / sourceExtent;
        Vector2 sampleMinimum = new Vector2(sourcePaint.SrcRect.X, sourcePaint.SrcRect.Y) +
            (storageMinimum - sourceMinimum) * sourceScale;
        Vector2 sampleExtent = (storageMaximum - storageMinimum) * sourceScale;
        if (!float.IsFinite(sampleMinimum.X) || !float.IsFinite(sampleMinimum.Y) ||
            !float.IsFinite(sampleExtent.X) || !float.IsFinite(sampleExtent.Y))
            throw new InvalidOperationException("Original snapped texture sampling overflows the admitted storage frame.");
        storagePaint.Rect = storageBounds;
        storagePaint.SrcRect = new Rect(sampleMinimum, sampleExtent);
        storagePaint.SnapTextureToPixels = false; // Original snap is already baked into the sample map.
        return true;
    }

    internal static Vector2 SnapPoint(Vector2 value, float dpiScale) =>
        new(MathF.Round(value.X * dpiScale) / dpiScale,
            MathF.Round(value.Y * dpiScale) / dpiScale);
}
