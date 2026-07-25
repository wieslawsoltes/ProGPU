namespace ProGPU.Voxel;

public enum VoxelBlock : ushort
{
    Air = 0,
    Grass = 1,
    Dirt = 2,
    Stone = 3,
    Sand = 4,
    Wood = 5,
    Leaves = 6,
    Water = 7
}

public readonly record struct VoxelBlockDefinition(
    VoxelBlock Id,
    string Name,
    bool IsSolid,
    bool IsOpaque);

public static class VoxelBlockCatalog
{
    public const int PlaceableCount = 7;

    public static VoxelBlockDefinition Get(VoxelBlock block) => block switch
    {
        VoxelBlock.Grass => new(block, "Grass", true, true),
        VoxelBlock.Dirt => new(block, "Dirt", true, true),
        VoxelBlock.Stone => new(block, "Stone", true, true),
        VoxelBlock.Sand => new(block, "Sand", true, true),
        VoxelBlock.Wood => new(block, "Wood", true, true),
        VoxelBlock.Leaves => new(block, "Leaves", true, true),
        VoxelBlock.Water => new(block, "Water", false, true),
        _ => new(VoxelBlock.Air, "Air", false, false)
    };

    public static bool IsSolid(VoxelBlock block) => block is
        VoxelBlock.Grass or
        VoxelBlock.Dirt or
        VoxelBlock.Stone or
        VoxelBlock.Sand or
        VoxelBlock.Wood or
        VoxelBlock.Leaves;

    public static bool IsOpaque(VoxelBlock block) => block != VoxelBlock.Air;

    public static VoxelBlock FromHotbarSlot(int slot) => slot switch
    {
        1 => VoxelBlock.Grass,
        2 => VoxelBlock.Dirt,
        3 => VoxelBlock.Stone,
        4 => VoxelBlock.Sand,
        5 => VoxelBlock.Wood,
        6 => VoxelBlock.Leaves,
        7 => VoxelBlock.Water,
        _ => VoxelBlock.Grass
    };
}
