using System.Numerics;

namespace ProGPU.Voxel;

public readonly record struct VoxelChunkPosition(int X, int Y, int Z)
{
    public Vector3 WorldOrigin => new(
        X * VoxelChunk.Size,
        Y * VoxelChunk.Size,
        Z * VoxelChunk.Size);
}
