using System.Numerics;

namespace ProGPU.Scene;

/// <summary>Private certificate for the actual root pass, not a glyph admission policy.</summary>
internal static class GlyphCoverageFramePolicy
{
    // O(1) work/storage. The shader separately proves each original glyph's four
    // corners and rendering mode. Unknown/translated/offscreen passes keep zero.
    // CanvasSize is deliberately not evidence: its meaning differs by provider.
    internal static float GetRootCertificate(
        uint logicalWidth, uint logicalHeight,
        uint physicalWidth, uint physicalHeight,
        float dpiScale, uint sampleCount, bool hasGpuTransforms,
        in Matrix4x4 projection, Vector4 viewport)
    {
        float logicalWidthFloat = logicalWidth;
        float logicalHeightFloat = logicalHeight;
        float physicalWidthFloat = physicalWidth;
        float physicalHeightFloat = physicalHeight;
        if (logicalWidth == 0 || logicalHeight == 0 ||
            physicalWidth == 0 || physicalHeight == 0 ||
            !float.IsFinite(dpiScale) || dpiScale <= 0f ||
            sampleCount != 1 || hasGpuTransforms ||
            (double)logicalWidthFloat != logicalWidth ||
            (double)logicalHeightFloat != logicalHeight ||
            (double)physicalWidthFloat != physicalWidth ||
            (double)physicalHeightFloat != physicalHeight ||
            logicalWidth * dpiScale != physicalWidth ||
            logicalHeight * dpiScale != physicalHeight ||
            viewport != new Vector4(0f, 0f, physicalWidth, physicalHeight))
        {
            return 0f;
        }

        var expectedProjection = new Matrix4x4(
            2f / logicalWidth, 0f, 0f, 0f,
            0f, -2f / logicalHeight, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1f, 1f, 0f, 1f);
        // Positive tags at the same existing offset belong to bounded image
        // source/ROP passes. A negative exact token cannot select those paths.
        return projection == expectedProjection ? -1f : 0f;
    }
}
