using System;
using System.Numerics;

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
        PixelsPerUnit = new Vector2(dpiScale);
        RasterBounds = paddedBounds;
        OutputEdges = new Vector4(paddedBounds.X, paddedBounds.Y, paddedBounds.Right, paddedBounds.Bottom);
        TextureUvBounds = new Vector4(0, 0, 1, 1);
        SourceToRaster = Matrix4x4.Identity;
    }

    /// <summary>Original padded float rectangle, without minimum-size or pixel rounding.</summary>
    public Rect PaddedBounds { get; }
    /// <summary>Raster projection width; legacy frames retain their one-logical-unit minimum.</summary>
    public float LogicalWidth { get; }
    /// <summary>Raster projection height; legacy frames retain their one-logical-unit minimum.</summary>
    public float LogicalHeight { get; }
    public uint LogicalRenderWidth { get; }
    public uint LogicalRenderHeight { get; }
    public uint PixelWidth { get; }
    public uint PixelHeight { get; }
    public float DpiScale { get; }
    /// <summary>True only for a source frame with a retained outward physical lattice.</summary>
    public bool HasPhysicalOrigin { get; private init; }
    /// <summary>Actual target projection-to-viewport scale, independent of semantic DPI and visual transforms.</summary>
    public Vector2 PixelsPerUnit { get; private init; }
    /// <summary>Signed, float-exact integer origin of the complete source texture, before source rebasing.</summary>
    public Vector2 PhysicalOrigin { get; private init; }
    /// <summary>Complete texture rectangle in rebased local coordinates, distinct from output coverage.</summary>
    public Rect RasterBounds { get; private init; }
    /// <summary>Independent rebased left/top/right/bottom output edges; do not reconstruct far edges from a rounded width.</summary>
    public Vector4 OutputEdges { get; private init; }
    /// <summary>Independent left/top/right/bottom UVs for the original output coverage in the complete texture.</summary>
    public Vector4 TextureUvBounds { get; private init; }
    /// <summary>Rebased source-local to physical capture coordinates; not a replacement for semantic DPI.</summary>
    public Matrix4x4 SourceToRaster { get; private init; }
    /// <summary>Exact floating logical projection extent; integer bookkeeping dimensions must not replace it.</summary>
    public Vector2 ProjectionExtent => new(LogicalWidth, LogicalHeight);

    /// <summary>
    /// Resolves the same orthographic projection and normalized viewport used by
    /// the actual compositor host frame. This is not window/monitor DPI or an
    /// extraction of the source visual's local or ancestor affine transforms.
    /// </summary>
    public static bool TryResolveSourcePixelsPerUnit(uint logicalWidth, uint logicalHeight,
        uint targetWidth, uint targetHeight, RenderTargetViewport viewport, out Vector2 pixelsPerUnit)
    {
        pixelsPerUnit = default;
        if (logicalWidth == 0 || logicalHeight == 0 || targetWidth == 0 || targetHeight == 0 || !viewport.IsValid)
            return false;
        var projection = new Matrix4x4(
            2f / logicalWidth, 0, 0, 0,
            0, -2f / logicalHeight, 0, 0,
            0, 0, 1, 0,
            -1, 1, 0, 1);
        return TryResolveSourcePixelsPerUnit(projection, viewport.Clamp(targetWidth, targetHeight), out pixelsPerUnit);
    }

    internal static bool TryResolveSourcePixelsPerUnit(Matrix4x4 projection,
        RenderTargetViewport viewport, out Vector2 pixelsPerUnit)
    {
        pixelsPerUnit = default;
        if (!viewport.IsValid || !float.IsFinite(projection.M11) || !float.IsFinite(projection.M22) ||
            projection.M12 != 0 || projection.M21 != 0 || projection.M14 != 0 || projection.M24 != 0 ||
            projection.M44 != 1)
            return false;
        // Preserve projection coefficient rounding before the viewport scale.
        // viewport/logicalSize is not an interchangeable arithmetic shortcut.
        float halfWidth = viewport.Width * .5f, halfHeight = viewport.Height * .5f;
        var result = new Vector2(MathF.Abs(projection.M11) * halfWidth, MathF.Abs(projection.M22) * halfHeight);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || result.X <= 0 || result.Y <= 0)
            return false;
        pixelsPerUnit = result;
        return true;
    }

    /// <summary>
    /// Creates a complete outward source texture in the actual per-axis target
    /// mapping. The separate semantic DPI is retained for text, snapping and
    /// nested effects. Original edges resolve before source-to-local rebasing.
    /// Old scalar and explicit raster-override overloads remain independent.
    /// </summary>
    public static bool TryCreateSource(ShaderEffectSourceCapture source, Vector2 sourceTranslation,
        Vector2 pixelsPerUnit, float dpiScale, out EffectCaptureFrame frame)
    {
        frame = default;
        if (!source.IsValid || !float.IsFinite(sourceTranslation.X) || !float.IsFinite(sourceTranslation.Y) ||
            !float.IsFinite(pixelsPerUnit.X) || !float.IsFinite(pixelsPerUnit.Y) ||
            pixelsPerUnit.X <= 0 || pixelsPerUnit.Y <= 0 || !float.IsFinite(dpiScale) || dpiScale <= 0)
            return false;

        float left = (float)source.X - (float)source.PaddingLeft;
        float top = (float)source.Y - (float)source.PaddingTop;
        float right = (float)(source.X + source.Width) + (float)source.PaddingRight;
        float bottom = (float)(source.Y + source.Height) + (float)source.PaddingBottom;
        float width = right - left, height = bottom - top;
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom) ||
            !float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            return false;

        float physicalLeft = left * pixelsPerUnit.X, physicalTop = top * pixelsPerUnit.Y;
        float physicalRight = right * pixelsPerUnit.X, physicalBottom = bottom * pixelsPerUnit.Y;
        float x = MathF.Floor(physicalLeft), y = MathF.Floor(physicalTop);
        float farX = MathF.Ceiling(physicalRight), farY = MathF.Ceiling(physicalBottom);
        const float exactIntegerLimit = 1 << 24;
        // Endpoints are float integers, but their difference can require one
        // more bit. Reject that exact integer span before narrowing it again.
        double pixelWidthExact = (double)farX - x, pixelHeightExact = (double)farY - y;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(farX) || !float.IsFinite(farY) ||
            x < -exactIntegerLimit || y < -exactIntegerLimit || farX > exactIntegerLimit || farY > exactIntegerLimit ||
            pixelWidthExact <= 0 || pixelHeightExact <= 0 ||
            pixelWidthExact > exactIntegerLimit || pixelHeightExact > exactIntegerLimit)
            return false;

        float pixelWidth = (float)pixelWidthExact, pixelHeight = (float)pixelHeightExact;
        float logicalWidth = pixelWidth / pixelsPerUnit.X, logicalHeight = pixelHeight / pixelsPerUnit.Y;
        var raster = new Rect(x / pixelsPerUnit.X + sourceTranslation.X,
            y / pixelsPerUnit.Y + sourceTranslation.Y, logicalWidth, logicalHeight);
        var padded = new Rect(left + sourceTranslation.X, top + sourceTranslation.Y, width, height);
        var outputEdges = new Vector4(padded.X, padded.Y, right + sourceTranslation.X, bottom + sourceTranslation.Y);
        float translatedX = sourceTranslation.X * pixelsPerUnit.X, translatedY = sourceTranslation.Y * pixelsPerUnit.Y;
        var sourceToRaster = new Matrix4x4(
            pixelsPerUnit.X, 0, 0, 0,
            0, pixelsPerUnit.Y, 0, 0,
            0, 0, 1, 0,
            -x - translatedX, -y - translatedY, 0, 1);
        if (!IsFinite(raster) || !IsFinite(padded) || !float.IsFinite(outputEdges.Z) || !float.IsFinite(outputEdges.W) ||
            !float.IsFinite(sourceToRaster.M41) ||
            !float.IsFinite(sourceToRaster.M42) ||
            !float.IsFinite(2f / logicalWidth) || !float.IsFinite(-2f / logicalHeight) ||
            !TryCeilingDimension(logicalWidth, out uint logicalRenderWidth) ||
            !TryCeilingDimension(logicalHeight, out uint logicalRenderHeight))
            return false;

        frame = new EffectCaptureFrame(padded, logicalWidth, logicalHeight,
            logicalRenderWidth, logicalRenderHeight, (uint)pixelWidth, (uint)pixelHeight, dpiScale)
        {
            HasPhysicalOrigin = true,
            PixelsPerUnit = pixelsPerUnit,
            PhysicalOrigin = new Vector2(x, y),
            RasterBounds = raster,
            OutputEdges = outputEdges,
            TextureUvBounds = new Vector4((physicalLeft - x) / pixelWidth, (physicalTop - y) / pixelHeight,
                (physicalRight - x) / pixelWidth, (physicalBottom - y) / pixelHeight),
            SourceToRaster = sourceToRaster
        };
        return true;
    }

    // Cache identity includes the complete capture mapping, not merely texture
    // dimensions. Keep signed source/physical zero identity without boxing a
    // struct on every retained effect draw.
    internal bool HasSameCapture(in EffectCaptureFrame other) =>
        HasPhysicalOrigin == other.HasPhysicalOrigin &&
        LogicalRenderWidth == other.LogicalRenderWidth && LogicalRenderHeight == other.LogicalRenderHeight &&
        PixelWidth == other.PixelWidth && PixelHeight == other.PixelHeight &&
        Same(DpiScale, other.DpiScale) && Same(LogicalWidth, other.LogicalWidth) && Same(LogicalHeight, other.LogicalHeight) &&
        Same(PaddedBounds.X, other.PaddedBounds.X) && Same(PaddedBounds.Y, other.PaddedBounds.Y) &&
        Same(PaddedBounds.Width, other.PaddedBounds.Width) && Same(PaddedBounds.Height, other.PaddedBounds.Height) &&
        Same(RasterBounds.X, other.RasterBounds.X) && Same(RasterBounds.Y, other.RasterBounds.Y) &&
        Same(RasterBounds.Width, other.RasterBounds.Width) && Same(RasterBounds.Height, other.RasterBounds.Height) &&
        Same(OutputEdges.X, other.OutputEdges.X) && Same(OutputEdges.Y, other.OutputEdges.Y) &&
        Same(OutputEdges.Z, other.OutputEdges.Z) && Same(OutputEdges.W, other.OutputEdges.W) &&
        Same(PixelsPerUnit.X, other.PixelsPerUnit.X) && Same(PixelsPerUnit.Y, other.PixelsPerUnit.Y) &&
        Same(PhysicalOrigin.X, other.PhysicalOrigin.X) && Same(PhysicalOrigin.Y, other.PhysicalOrigin.Y) &&
        Same(TextureUvBounds.X, other.TextureUvBounds.X) && Same(TextureUvBounds.Y, other.TextureUvBounds.Y) &&
        Same(TextureUvBounds.Z, other.TextureUvBounds.Z) && Same(TextureUvBounds.W, other.TextureUvBounds.W) &&
        Same(SourceToRaster.M41, other.SourceToRaster.M41) && Same(SourceToRaster.M42, other.SourceToRaster.M42);

    private static bool Same(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

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

    /// <summary>
    /// Creates the source frame from separately narrowed original endpoints and
    /// four independently narrowed padding values, without ceiling the padding.
    /// The existing managed logical minimum and physical extent policy remain.
    /// </summary>
    public static bool TryCreateSource(ShaderEffectSourceCapture source, float dpiScale,
        out EffectCaptureFrame frame) => TryCreateSource(source, Vector2.Zero, null, dpiScale, out frame);

    /// <summary>
    /// Applies the source host's actual source-to-local translation after source
    /// endpoint inflation. It cannot change the capture extent. An explicit
    /// raster override retains the legacy symmetric override policy.
    /// </summary>
    public static bool TryCreateSource(ShaderEffectSourceCapture source, Vector2 sourceTranslation,
        float? rasterPaddingOverride, float dpiScale, out EffectCaptureFrame frame)
    {
        frame = default;
        if (!source.IsValid ||
            !float.IsFinite(sourceTranslation.X) || !float.IsFinite(sourceTranslation.Y))
            return false;

        float x = (float)source.X, y = (float)source.Y;
        if (rasterPaddingOverride is { } requested)
        {
            // Explicit legacy overrides still use narrowed origin/size and the
            // original width + padding*2 arithmetic. Do not reinterpret them as
            // source padding or make an invalid source descriptor valid.
            var content = new Rect(x + sourceTranslation.X, y + sourceTranslation.Y,
                (float)source.Width, (float)source.Height);
            float padding = ResolveRasterPadding(requested);
            return TryCreateResolved(content, padding, padding, dpiScale, out frame);
        }

        float right = (float)(source.X + source.Width);
        float bottom = (float)(source.Y + source.Height);
        float left = x - (float)source.PaddingLeft;
        float top = y - (float)source.PaddingTop;
        right += (float)source.PaddingRight;
        bottom += (float)source.PaddingBottom;
        float width = right - left, height = bottom - top;
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) ||
            !float.IsFinite(bottom) || width <= 0f || height <= 0f)
            return false;

        // The source sinks have already translated their recorded geometry.
        // Preserve that actual translation, not an inferred -rounded-origin.
        return TryCreatePadded(new Rect(left + sourceTranslation.X, top + sourceTranslation.Y,
            width, height), dpiScale, out frame);
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
        return TryCreatePadded(paddedBounds, dpiScale, out frame);
    }

    private static bool TryCreatePadded(Rect paddedBounds, float dpiScale, out EffectCaptureFrame frame)
    {
        frame = default;
        if (!IsFinite(paddedBounds) || !float.IsFinite(dpiScale) || dpiScale <= 0f) return false;
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
