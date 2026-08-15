using ProGPU.Backend.Native;

namespace ProGPU.Scene.Native;

/// <summary>
/// Owns one immutable pointer-free native semantic scene snapshot.
/// </summary>
public sealed class NativeCompiledPicture
{
    internal NativeCompiledPicture(
        byte[] storage,
        int length,
        ulong sceneId,
        ulong generation,
        float targetDpiScale,
        int sourceCommandCount,
        int nativeCommandCount,
        int nativeDrawCount,
        int analyticPrimitiveCount,
        int geometryPrimitiveCount,
        int pathCount,
        int pathSegmentCount,
        int pointBatchCount,
        int pointCount,
        int vertexMeshCount,
        int meshVertexCount,
        int meshIndexCount,
        int strokeCount,
        int strokePointCount,
        int strokeDoubleCount,
        int glyphOutlineCount,
        int glyphSegmentCount,
        int colorGlyphBitmapCount,
        int colorGlyphPixelBytes,
        int positionedGlyphCount,
        int textStyleCount,
        int line3DCount,
        int brushCount,
        int gradientStopCount,
        NativeSceneExternalImageBinding[] externalImages)
    {
        Storage = storage;
        _externalImages = externalImages;
        Length = length;
        SceneId = sceneId;
        Generation = generation;
        TargetDpiScale = targetDpiScale;
        SourceCommandCount = sourceCommandCount;
        NativeCommandCount = nativeCommandCount;
        NativeDrawCount = nativeDrawCount;
        AnalyticPrimitiveCount = analyticPrimitiveCount;
        GeometryPrimitiveCount = geometryPrimitiveCount;
        PathCount = pathCount;
        PathSegmentCount = pathSegmentCount;
        PointBatchCount = pointBatchCount;
        PointCount = pointCount;
        VertexMeshCount = vertexMeshCount;
        MeshVertexCount = meshVertexCount;
        MeshIndexCount = meshIndexCount;
        StrokeCount = strokeCount;
        StrokePointCount = strokePointCount;
        StrokeDoubleCount = strokeDoubleCount;
        GlyphOutlineCount = glyphOutlineCount;
        GlyphSegmentCount = glyphSegmentCount;
        ColorGlyphBitmapCount = colorGlyphBitmapCount;
        ColorGlyphPixelBytes = colorGlyphPixelBytes;
        PositionedGlyphCount = positionedGlyphCount;
        TextStyleCount = textStyleCount;
        Line3DCount = line3DCount;
        BrushCount = brushCount;
        GradientStopCount = gradientStopCount;
    }

    private byte[] Storage { get; }

    private readonly NativeSceneExternalImageBinding[] _externalImages;

    public int Length { get; }

    public ulong SceneId { get; }

    public ulong Generation { get; }

    /// <summary>
    /// Gets the physical target scale used to compile target-sensitive glyph
    /// raster records.
    /// </summary>
    public float TargetDpiScale { get; }

    /// <summary>
    /// Gets the total command count across the root picture and every
    /// recursively flattened retained child picture.
    /// </summary>
    public int SourceCommandCount { get; }

    public int NativeCommandCount { get; }

    public int NativeDrawCount { get; }

    public int AnalyticPrimitiveCount { get; }

    public int GeometryPrimitiveCount { get; }

    public int PathCount { get; }

    public int PathSegmentCount { get; }

    public int PointBatchCount { get; }

    public int PointCount { get; }

    public int VertexMeshCount { get; }

    public int MeshVertexCount { get; }

    public int MeshIndexCount { get; }

    public int StrokeCount { get; }

    public int StrokePointCount { get; }

    public int StrokeDoubleCount { get; }

    public int GlyphOutlineCount { get; }

    public int GlyphSegmentCount { get; }

    public int ColorGlyphBitmapCount { get; }

    public int ColorGlyphPixelBytes { get; }

    public int PositionedGlyphCount { get; }

    public int TextStyleCount { get; }

    public int Line3DCount { get; }

    public int BrushCount { get; }

    public int GradientStopCount { get; }

    /// <summary>
    /// Gets the live same-device image bindings required before this retained
    /// scene snapshot is installed. The scene stream itself stays pointer-free.
    /// </summary>
    public ReadOnlySpan<NativeSceneExternalImageBinding> ExternalImages =>
        _externalImages;

    public ReadOnlyMemory<byte> Memory => Storage.AsMemory(0, Length);

    public ReadOnlySpan<byte> Stream => Storage.AsSpan(0, Length);
}
