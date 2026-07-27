namespace ProGPU.Voxel;

/// <summary>
/// A fixed-size, densely packed chunk. Mutations are O(1); enumeration is O(ChunkVolume).
/// Instances are intentionally thread-confined so gameplay can update without locks.
/// </summary>
public sealed class VoxelChunk
{
    public const int Size = 16;
    public const int Volume = Size * Size * Size;

    private readonly ushort[] _blocks = new ushort[Volume];
    private int _nonAirCount;

    public VoxelChunk(VoxelChunkPosition position)
    {
        Position = position;
    }

    public VoxelChunkPosition Position { get; }

    public int ContentVersion { get; private set; }

    public int MeshVersion { get; private set; }

    public int NonAirCount => _nonAirCount;

    public bool IsEmpty => _nonAirCount == 0;

    public bool IsMeshDirty { get; private set; } = true;

    public VoxelMesh? Mesh { get; private set; }

    public VoxelBlock GetLocal(int x, int y, int z)
    {
        if ((uint)x >= Size || (uint)y >= Size || (uint)z >= Size)
        {
            return VoxelBlock.Air;
        }

        return (VoxelBlock)_blocks[ToIndex(x, y, z)];
    }

    public bool SetLocal(int x, int y, int z, VoxelBlock block)
    {
        if ((uint)x >= Size || (uint)y >= Size || (uint)z >= Size)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Chunk-local coordinates must be in [0, 16).");
        }

        var index = ToIndex(x, y, z);
        var previous = (VoxelBlock)_blocks[index];
        if (previous == block)
        {
            return false;
        }

        if (previous == VoxelBlock.Air)
        {
            _nonAirCount++;
        }
        else if (block == VoxelBlock.Air)
        {
            _nonAirCount--;
        }

        _blocks[index] = (ushort)block;
        ContentVersion++;
        MarkMeshDirty();
        return true;
    }

    public bool Contains(VoxelBlock block) =>
        Array.IndexOf(_blocks, (ushort)block) >= 0;

    public void MarkMeshDirty()
    {
        if (!IsMeshDirty)
        {
            IsMeshDirty = true;
            MeshVersion++;
        }
    }

    internal void AcceptMesh(VoxelMesh mesh)
    {
        Mesh = mesh;
        IsMeshDirty = false;
    }

    private static int ToIndex(int x, int y, int z) => x + Size * (z + Size * y);
}
