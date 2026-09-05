using System.Numerics;
using ProGPU.Backend;

namespace ProGPU.Vector;

public enum TileImageAddressMode
{
    Clamp,
    Repeat,
    MirrorRepeat
}

/// <summary>
/// Encodes one premultiplied, zero-origin GPU tile page for Texture.wgsl.
/// The occupied extent excludes texture-pool padding. Callers retain the page
/// through submission and explicitly select this shader-sampling primitive.
/// This does not allocate, capture a source, or select a fallback policy.
/// </summary>
public static class TileImageVertices
{
    public const float PatchKind = -2f;

    /// <summary>
    /// Writes four vertices transactionally. Output is (x,y,width,height);
    /// outputToTile maps target coordinates to normalized base-tile coordinates.
    /// Only base-level nearest/linear sampling is supported, never a downgrade
    /// from cubic, Fant, mipmapped or anisotropic sampling. O(1) time/workspace.
    /// </summary>
    public static bool TryWriteQuad(Span<VectorVertex> destination, Vector4 output,
        Matrix3x2 outputToTile, uint tileWidth, uint tileHeight,
        uint textureWidth, uint textureHeight, TileImageAddressMode addressU,
        TileImageAddressMode addressV, bool nearest, float opacity)
    {
        const uint maximumExactExtent = 1u << 24;
        if (destination.Length < 4 || tileWidth == 0 || tileHeight == 0 ||
            tileWidth > textureWidth || tileHeight > textureHeight ||
            tileWidth > maximumExactExtent || tileHeight > maximumExactExtent ||
            (uint)addressU > 2 || (uint)addressV > 2 ||
            !float.IsFinite(opacity) || opacity < 0 || opacity > 1 ||
            !float.IsFinite(output.X) || !float.IsFinite(output.Y) ||
            !float.IsFinite(output.Z) || !float.IsFinite(output.W) || output.Z <= 0 || output.W <= 0 ||
            !float.IsFinite(outputToTile.M11) || !float.IsFinite(outputToTile.M12) ||
            !float.IsFinite(outputToTile.M21) || !float.IsFinite(outputToTile.M22) ||
            !float.IsFinite(outputToTile.M31) || !float.IsFinite(outputToTile.M32))
            return false;

        Span<VectorVertex> vertices = stackalloc VectorVertex[4];
        ReadOnlySpan<Vector2> corners = stackalloc Vector2[4] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        for (int index = 0; index < 4; index++)
        {
            Vector2 position = new Vector2(output.X, output.Y) + corners[index] * new Vector2(output.Z, output.W);
            Vector2 uv = Vector2.Transform(position, outputToTile);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                return false;
            vertices[index] = new VectorVertex(position, new(tileWidth, tileHeight, 0, opacity), uv,
                PatchKind, new(nearest ? GpuImageSamplingPolicy.ExplicitNearestCoefficient :
                    GpuImageSamplingPolicy.ExplicitLinearCoefficient, 0),
                (float)addressU, (float)addressV);
        }
        vertices.CopyTo(destination);
        return true;
    }
}
