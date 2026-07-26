namespace ProGPU.Voxel;

/// <summary>
/// Sparse chunked voxel storage. Reads and writes are O(1) average; remeshing is explicit.
/// </summary>
public sealed class VoxelWorld
{
    private readonly Dictionary<VoxelChunkPosition, VoxelChunk> _chunks = new();

    public IReadOnlyCollection<VoxelChunk> Chunks => _chunks.Values;

    public int ChunkCount => _chunks.Count;

    public int ContentVersion { get; private set; }

    public VoxelBlock GetBlock(int x, int y, int z)
    {
        var chunkPosition = ToChunkPosition(x, y, z);
        return _chunks.TryGetValue(chunkPosition, out var chunk)
            ? chunk.GetLocal(ToLocal(x), ToLocal(y), ToLocal(z))
            : VoxelBlock.Air;
    }

    public bool SetBlock(int x, int y, int z, VoxelBlock block)
    {
        var chunkPosition = ToChunkPosition(x, y, z);
        if (!_chunks.TryGetValue(chunkPosition, out var chunk))
        {
            if (block == VoxelBlock.Air)
            {
                return false;
            }

            chunk = new VoxelChunk(chunkPosition);
            _chunks.Add(chunkPosition, chunk);
        }

        var localX = ToLocal(x);
        var localY = ToLocal(y);
        var localZ = ToLocal(z);
        if (!chunk.SetLocal(localX, localY, localZ, block))
        {
            return false;
        }

        if (localX == 0) MarkChunkMeshDirty(new(chunkPosition.X - 1, chunkPosition.Y, chunkPosition.Z));
        if (localX == VoxelChunk.Size - 1) MarkChunkMeshDirty(new(chunkPosition.X + 1, chunkPosition.Y, chunkPosition.Z));
        if (localY == 0) MarkChunkMeshDirty(new(chunkPosition.X, chunkPosition.Y - 1, chunkPosition.Z));
        if (localY == VoxelChunk.Size - 1) MarkChunkMeshDirty(new(chunkPosition.X, chunkPosition.Y + 1, chunkPosition.Z));
        if (localZ == 0) MarkChunkMeshDirty(new(chunkPosition.X, chunkPosition.Y, chunkPosition.Z - 1));
        if (localZ == VoxelChunk.Size - 1) MarkChunkMeshDirty(new(chunkPosition.X, chunkPosition.Y, chunkPosition.Z + 1));

        unchecked
        {
            ContentVersion++;
        }
        return true;
    }

    public VoxelChunk GetOrCreateChunk(VoxelChunkPosition position)
    {
        if (!_chunks.TryGetValue(position, out var chunk))
        {
            chunk = new VoxelChunk(position);
            _chunks.Add(position, chunk);
        }

        return chunk;
    }

    public bool TryGetChunk(VoxelChunkPosition position, out VoxelChunk chunk) =>
        _chunks.TryGetValue(position, out chunk!);

    public VoxelMesh GetOrBuildMesh(VoxelChunk chunk)
    {
        if (!chunk.IsMeshDirty && chunk.Mesh is not null)
        {
            return chunk.Mesh;
        }

        var mesh = VoxelGreedyMesher.Build(this, chunk);
        chunk.AcceptMesh(mesh);
        return mesh;
    }

    public void BuildAllMeshes()
    {
        foreach (var chunk in _chunks.Values)
        {
            GetOrBuildMesh(chunk);
        }
    }

    public bool ContainsBlock(VoxelBlock block)
    {
        foreach (var chunk in _chunks.Values)
        {
            if (chunk.Contains(block))
            {
                return true;
            }
        }

        return false;
    }

    public int FindSurfaceY(int x, int z, int startY = 127)
    {
        for (var y = startY; y >= -64; y--)
        {
            if (VoxelBlockCatalog.IsSolid(GetBlock(x, y, z)))
            {
                return y;
            }
        }

        return 0;
    }

    public static VoxelChunkPosition ToChunkPosition(int x, int y, int z) =>
        new(FloorDiv(x, VoxelChunk.Size), FloorDiv(y, VoxelChunk.Size), FloorDiv(z, VoxelChunk.Size));

    public static int ToLocal(int coordinate)
    {
        var result = coordinate % VoxelChunk.Size;
        return result < 0 ? result + VoxelChunk.Size : result;
    }

    private void MarkChunkMeshDirty(VoxelChunkPosition position)
    {
        if (_chunks.TryGetValue(position, out var neighbor))
        {
            neighbor.MarkMeshDirty();
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
