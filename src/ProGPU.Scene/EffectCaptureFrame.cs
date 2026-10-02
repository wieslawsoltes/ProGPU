using System;

namespace ProGPU.Scene;

/// <summary>
/// Pure logical and physical frame of an effect input capture. This value owns
/// no texture, device, source visual or lease and does not admit an effect.
/// </summary>
public readonly struct EffectCaptureFrame
{
    private EffectCaptureFrame(Rect paddedBounds, float logicalWidth, float logicalHeight,
        uint logicalRenderWidth, uint logicalRenderHeight, uint pixelWidth, uint pixelHeight, float dpiScale)
    {
        PaddedBounds = paddedBounds;
        LogicalWidth = logicalWidth;
        LogicalHeight = logicalHeight;
        LogicalRenderWidth = logicalRenderWidth;
        LogicalRenderHeight = logicalRenderHeight;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        DpiScale = dpiScale;
    }

    /// <summary>Original padded float rectangle, without minimum-size or pixel rounding.</summary>
    public Rect PaddedBounds { get; }
    /// <summary>Original padded width with the compositor's one-logical-unit minimum.</summary>
    public float LogicalWidth { get; }
    /// <summary>Original padded height with the compositor's one-logical-unit minimum.</summary>
    public float LogicalHeight { get; }
    public uint LogicalRenderWidth { get; }
    public uint LogicalRenderHeight { get; }
    public uint PixelWidth { get; }
    public uint PixelHeight { get; }
    public float DpiScale { get; }

    /// <summary>
    /// Resolves a shader input using ceil(max(0, shaderPadding)), then the same
    /// float padded-bounds, minimum extent and physical ceiling as the compositor.
    /// Empty, nonfinite or unrepresentable captures fail without publishing a frame.
    /// DPI must be the actual positive finite capture DPI, not an inferred image DPI.
    /// </summary>
    public static bool TryCreate(Rect contentBounds, float shaderPadding, float dpiScale,
        out EffectCaptureFrame frame) => TryCreate(contentBounds, shaderPadding, null, dpiScale, out frame);

    /// <summary>
    /// An explicit raster-padding override uses max(0,padding), without ceiling;
    /// a nonfinite override selects zero, matching Visual.EffectRasterPadding.
    /// Null retains the shader's padding policy.
    /// </summary>
    public static bool TryCreate(Rect contentBounds, float shaderPadding, float? rasterPaddingOverride,
        float dpiScale, out EffectCaptureFrame frame)
    {
        float padding = rasterPaddingOverride is { } requested
            ? ResolveRasterPadding(requested) : ResolveShaderPadding(shaderPadding);
        return TryCreateResolved(contentBounds, padding, padding, dpiScale, out frame);
    }

    internal static float ResolveShaderPadding(float padding) => MathF.Ceiling(MathF.Max(0f, padding));

    internal static float ResolveRasterPadding(float padding) => float.IsFinite(padding) ? MathF.Max(0f, padding) : 0f;

    // Other effects already resolve their own, possibly independent X/Y padding.
    // This is the original shared compositor arithmetic, not a new raster policy.
    internal static bool TryCreateResolved(Rect contentBounds, float paddingX, float paddingY,
        float dpiScale, out EffectCaptureFrame frame)
    {
        frame = default;
        if (contentBounds.IsEmpty || !IsFinite(contentBounds) || !float.IsFinite(paddingX) ||
            !float.IsFinite(paddingY) || !float.IsFinite(dpiScale) || dpiScale <= 0f)
            return false;

        var paddedBounds = new Rect(
            contentBounds.X - paddingX,
            contentBounds.Y - paddingY,
            contentBounds.Width + paddingX * 2f,
            contentBounds.Height + paddingY * 2f);
        if (!IsFinite(paddedBounds)) return false;
        float logicalWidth = MathF.Max(1f, paddedBounds.Width);
        float logicalHeight = MathF.Max(1f, paddedBounds.Height);
        // Preserve float multiplication BEFORE ceiling. Multiplication in double
        // or ceiling the logical dimension first can change the physical extent.
        if (!TryCeilingDimension(logicalWidth, out uint logicalRenderWidth) ||
            !TryCeilingDimension(logicalHeight, out uint logicalRenderHeight) ||
            !TryCeilingDimension(logicalWidth * dpiScale, out uint pixelWidth) ||
            !TryCeilingDimension(logicalHeight * dpiScale, out uint pixelHeight))
            return false;

        frame = new(paddedBounds, logicalWidth, logicalHeight, logicalRenderWidth, logicalRenderHeight,
            pixelWidth, pixelHeight, dpiScale);
        return true;
    }

    private static bool IsFinite(Rect bounds) => float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height);

    private static bool TryCeilingDimension(float extent, out uint result)
    {
        result = 0;
        float ceiling = MathF.Ceiling(extent);
        // Comparing in double is essential: converting uint.MaxValue to float
        // rounds up to 2^32 and would admit an unrepresentable uint dimension.
        if (!float.IsFinite(ceiling) || ceiling <= 0f || (double)ceiling > uint.MaxValue) return false;
        result = (uint)ceiling;
        return true;
    }
}
