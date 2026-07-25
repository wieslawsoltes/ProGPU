using System.Numerics;
using System.Runtime.InteropServices;

namespace ProGPU.Voxel;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct VoxelVertex
{
    public VoxelVertex(Vector3 position, Vector2 textureCoordinate, uint material)
    {
        Position = position;
        TextureCoordinate = textureCoordinate;
        Material = material;
    }

    public Vector3 Position { get; }

    public Vector2 TextureCoordinate { get; }

    /// <summary>
    /// Packed as material[0..7], face[8..10], light[16..23].
    /// </summary>
    public uint Material { get; }
}

public sealed class VoxelMesh
{
    public VoxelMesh(
        VoxelVertex[] vertices,
        uint[] indices,
        int version,
        int visibleFaceCount,
        int mergedQuadCount)
    {
        Vertices = vertices;
        Indices = indices;
        Version = version;
        VisibleFaceCount = visibleFaceCount;
        MergedQuadCount = mergedQuadCount;
    }

    public VoxelVertex[] Vertices { get; }

    public uint[] Indices { get; }

    public int Version { get; }

    public int VisibleFaceCount { get; }

    public int MergedQuadCount { get; }

    public int TriangleCount => Indices.Length / 3;
}
