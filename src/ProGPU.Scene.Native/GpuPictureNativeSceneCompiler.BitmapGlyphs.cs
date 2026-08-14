using System.Numerics;
using ProGPU.Backend.Native;
using ProGPU.Text;

namespace ProGPU.Scene.Native;

public static partial class GpuPictureNativeSceneCompiler
{
    private static bool TryAppendBitmapGlyph(
        ushort glyphIndex,
        in DecodedBitmapGlyphData decoded,
        Vector2 sourcePosition,
        in RenderCommand command,
        float targetDpiScale,
        float targetRasterSize,
        float atlasToLogicalScale,
        float fontScaleX,
        float nativeItalicSkew,
        float boldOffset,
        Vector2 basisX,
        Vector2 basisY,
        Matrix3x2 activeTransform,
        bool transformedPlacement,
        List<NativeSceneColorGlyphBitmap> bitmaps,
        List<byte> pixels,
        List<NativePositionedGlyph> glyphs,
        Dictionary<ushort, uint> bitmapIndices,
        ref NativeImageRect bounds,
        ref bool hasBounds,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (decoded.RgbaPixels is not { Length: > 0 } ||
            decoded.Width == 0U || decoded.Height == 0U ||
            decoded.Width > 16_384U || decoded.Height > 16_384U ||
            !float.IsFinite(decoded.BearX) ||
            !float.IsFinite(decoded.BearY) ||
            !float.IsFinite(decoded.RenderWidth) ||
            !float.IsFinite(decoded.RenderHeight) ||
            !float.IsFinite(decoded.RasterScale) ||
            decoded.RasterScale <= 0f)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        uint rowBytes;
        int requiredBytes;
        try
        {
            rowBytes = checked(decoded.Width * 4U);
            requiredBytes = checked((int)(rowBytes * decoded.Height));
        }
        catch (OverflowException)
        {
            error = NativePictureCompileError.CapacityExceeded;
            return false;
        }
        if (requiredBytes != decoded.RgbaPixels.Length)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }

        if (!bitmapIndices.TryGetValue(glyphIndex, out uint bitmapIndex))
        {
            bitmapIndex = checked((uint)bitmaps.Count);
            bitmapIndices.Add(glyphIndex, bitmapIndex);
            ulong pixelOffset = checked((ulong)pixels.Count);
            bitmaps.Add(new NativeSceneColorGlyphBitmap(
                pixelOffset,
                decoded.Width,
                decoded.Height,
                rowBytes,
                decoded.BearX,
                decoded.BearY,
                decoded.RenderWidth,
                decoded.RenderHeight));
            pixels.AddRange(decoded.RgbaPixels);
        }

        Vector2 transformedPosition = Vector2.Transform(
            sourcePosition + command.Position,
            activeTransform);
        (_, Vector2 snappedPosition) = ResolveGlyphPlacement(
            transformedPosition,
            targetDpiScale,
            targetRasterSize,
            transformedPlacement,
            command.TextHintingMode);
        float glyphScale = atlasToLogicalScale * decoded.RasterScale;
        int passCount = command.IsBold ? 2 : 1;
        for (int pass = 0; pass < passCount; pass++)
        {
            float nativeBoldOffset = pass * boldOffset / fontScaleX;
            glyphs.Add(new NativePositionedGlyph(
                bitmapIndex,
                snappedPosition,
                basisX,
                basisY,
                Vector4.One,
                glyphScale,
                nativeBoldOffset,
                nativeItalicSkew));
            NativeImageRect glyphBounds = CalculateBitmapGlyphBounds(
                decoded,
                glyphScale,
                snappedPosition,
                basisX,
                basisY,
                nativeBoldOffset,
                nativeItalicSkew,
                targetDpiScale);
            bounds = hasBounds ? Union(bounds, glyphBounds) : glyphBounds;
            hasBounds = true;
        }
        return true;
    }

    private static NativeImageRect CalculateBitmapGlyphBounds(
        in DecodedBitmapGlyphData decoded,
        float glyphScale,
        Vector2 origin,
        Vector2 basisX,
        Vector2 basisY,
        float boldOffset,
        float italicSkew,
        float dpiScale)
    {
        float x0 = decoded.BearX / dpiScale * glyphScale + boldOffset;
        float y0 = decoded.BearY / dpiScale * glyphScale;
        float width = (decoded.RenderWidth > 0f
            ? decoded.RenderWidth
            : decoded.Width) / dpiScale * glyphScale;
        float height = (decoded.RenderHeight > 0f
            ? decoded.RenderHeight
            : decoded.Height) / dpiScale * glyphScale;
        float x1 = x0 + width;
        float y1 = y0 + height;
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            origin + (x0 - y0 * italicSkew) * basisX + y0 * basisY,
            origin + (x1 - y0 * italicSkew) * basisX + y0 * basisY,
            origin + (x1 - y1 * italicSkew) * basisX + y1 * basisY,
            origin + (x0 - y1 * italicSkew) * basisX + y1 * basisY
        };
        float minimumX = corners[0].X;
        float minimumY = corners[0].Y;
        float maximumX = minimumX;
        float maximumY = minimumY;
        for (int index = 1; index < corners.Length; index++)
        {
            minimumX = MathF.Min(minimumX, corners[index].X);
            minimumY = MathF.Min(minimumY, corners[index].Y);
            maximumX = MathF.Max(maximumX, corners[index].X);
            maximumY = MathF.Max(maximumY, corners[index].Y);
        }
        float padding = GlyphBoundsPadding / dpiScale;
        return new NativeImageRect(
            minimumX - padding,
            minimumY - padding,
            maximumX - minimumX + padding * 2f,
            maximumY - minimumY + padding * 2f);
    }
}
