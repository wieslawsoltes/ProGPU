using System;
using System.Numerics;

namespace ProGPU.Scene;

/// <summary>
/// The independent physical frame of a source bitmap-cache shader sampler.
/// It is not an effect-input frame or an ordinary cached-picture paint mapping.
/// Construction performs no rendering and proves no source/device ownership.
/// </summary>
public readonly struct CacheSamplerRasterFrame
{
    private CacheSamplerRasterFrame(double x, double y, double width, double height,
        double renderScale, float primaryX, float primaryY, uint maximumWidth, uint maximumHeight,
        uint pixelWidth, uint pixelHeight, Matrix4x4 sourceToRaster, bool empty)
    {
        SourceX = x; SourceY = y; SourceWidth = width; SourceHeight = height;
        RenderScale = renderScale; PrimaryDpiScaleX = primaryX; PrimaryDpiScaleY = primaryY;
        MaximumTextureWidth = maximumWidth; MaximumTextureHeight = maximumHeight;
        PixelWidth = pixelWidth; PixelHeight = pixelHeight; SourceToRaster = sourceToRaster;
        IsEmpty = empty; IsValid = true;
    }

    public bool IsValid { get; }
    /// <summary>Absent realization: the owned sampler is one transparent texel.</summary>
    public bool IsEmpty { get; }
    public double SourceX { get; }
    public double SourceY { get; }
    public double SourceWidth { get; }
    public double SourceHeight { get; }
    public double RenderScale { get; }
    public float PrimaryDpiScaleX { get; }
    public float PrimaryDpiScaleY { get; }
    public uint MaximumTextureWidth { get; }
    public uint MaximumTextureHeight { get; }
    public uint PixelWidth { get; }
    public uint PixelHeight { get; }
    public Matrix4x4 SourceToRaster { get; }

    /// <summary>
    /// Retains original double bounds while narrowing their four endpoints to
    /// the source float realization. A known null/empty source supplies an empty
    /// finite rectangle; missing bounds must be rejected by its source owner.
    /// Scale is the selected, already-coerced nonnegative cache policy. Primary
    /// scales and texture limits must come from their actual owned providers.
    /// </summary>
    public static bool TryCreate(double x, double y, double width, double height,
        double renderScale, float primaryX, float primaryY, uint maximumWidth, uint maximumHeight,
        out CacheSamplerRasterFrame frame)
    {
        frame = default;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width < 0 || height < 0 ||
            !double.IsFinite(renderScale) || renderScale < 0 ||
            !float.IsFinite(primaryX) || !float.IsFinite(primaryY) || primaryX <= 0 || primaryY <= 0 ||
            maximumWidth == 0 || maximumHeight == 0) return false;

        float left = (float)x, top = (float)y;
        float right = (float)(x + width), bottom = (float)(y + height);
        float localWidth = right - left, localHeight = bottom - top;
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) ||
            !float.IsFinite(bottom) || !float.IsFinite(localWidth) || !float.IsFinite(localHeight) ||
            localWidth < 0 || localHeight < 0) return false;

        bool empty = localWidth == 0 || localHeight == 0 || renderScale == 0;
        uint pixelsX = 1, pixelsY = 1;
        Matrix4x4 transform = Matrix4x4.Identity;
        if (!empty)
        {
            if (!TryResolveAxis(localWidth, renderScale, primaryX, maximumWidth, out pixelsX, out float scaleX) ||
                !TryResolveAxis(localHeight, renderScale, primaryY, maximumHeight, out pixelsY, out float scaleY))
                return false;
            empty = pixelsX == 0 || pixelsY == 0;
            if (empty) pixelsX = pixelsY = 1;
            else
            {
                float offsetX = -left * scaleX, offsetY = -top * scaleY;
                if (!float.IsFinite(offsetX) || !float.IsFinite(offsetY)) return false;
                // Separate original float scale/translation; do not compose a
                // double transform and narrow its final aggregate instead.
                transform = new Matrix4x4(scaleX, 0, 0, 0, 0, scaleY, 0, 0,
                    0, 0, 1, 0, offsetX, offsetY, 0, 1);
            }
        }

        frame = new(x, y, width, height, renderScale, primaryX, primaryY, maximumWidth, maximumHeight,
            pixelsX, pixelsY, transform, empty);
        return true;
    }

    private static bool TryResolveAxis(float extent, double renderScale, float primaryScale,
        uint maximum, out uint pixels, out float contentScale)
    {
        pixels = 0; contentScale = 0;
        double scaledExtent = (double)extent * renderScale;
        double physicalExtent = scaledExtent * primaryScale;
        if (!double.IsFinite(physicalExtent) || physicalExtent < 0 || physicalExtent > uint.MaxValue)
            return false;

        uint integral = (uint)physicalExtent;
        float roundedIntegral = integral, roundedExtent = (float)physicalExtent;
        float difference = roundedIntegral - roundedExtent;
        float relative = difference / (roundedExtent == 0 ? 1f : roundedExtent);
        // Source float relative-near-integer contract, not generic ceil or an
        // epsilon that changes geometry/invertibility admission. Ten binary32
        // unit roundoffs are exactly representable; comparison remains strict.
        const float roundingThreshold = 10f / 8388608f;
        if (!(MathF.Abs(relative) < roundingThreshold))
        {
            if (integral == uint.MaxValue) return false;
            integral++;
        }

        pixels = Math.Min(integral, maximum);
        if (pixels == 0) return true;
        double effectivePrimary = primaryScale;
        if (integral > maximum) effectivePrimary *= (double)maximum / integral;
        contentScale = (float)(renderScale * effectivePrimary);
        return float.IsFinite(contentScale) && (pixels == 0 || contentScale > 0);
    }
}
