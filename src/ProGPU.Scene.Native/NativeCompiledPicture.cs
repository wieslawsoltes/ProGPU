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
        int brushCount,
        int gradientStopCount)
    {
        Storage = storage;
        Length = length;
        SceneId = sceneId;
        Generation = generation;
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
        BrushCount = brushCount;
        GradientStopCount = gradientStopCount;
    }

    private byte[] Storage { get; }

    public int Length { get; }

    public ulong SceneId { get; }

    public ulong Generation { get; }

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

    public int BrushCount { get; }

    public int GradientStopCount { get; }

    public ReadOnlyMemory<byte> Memory => Storage.AsMemory(0, Length);

    public ReadOnlySpan<byte> Stream => Storage.AsSpan(0, Length);
}
