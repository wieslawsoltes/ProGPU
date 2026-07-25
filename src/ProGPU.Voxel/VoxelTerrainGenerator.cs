namespace ProGPU.Voxel;

public readonly record struct VoxelTerrainSettings(
    int Seed,
    int ChunkRadius = 3,
    int BaseHeight = 20,
    int HeightVariation = 14,
    bool BuildMeshes = true);

/// <summary>
/// Deterministic fractal value-noise terrain generation.
/// Time is O(W² * H); world storage is O(number of non-air chunks).
/// </summary>
public static class VoxelTerrainGenerator
{
    public static VoxelWorld Generate(VoxelTerrainSettings settings)
    {
        if (settings.ChunkRadius < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Chunk radius must be positive.");
        }

        var world = new VoxelWorld();
        var radius = settings.ChunkRadius * VoxelChunk.Size;

        for (var z = -radius; z < radius; z++)
        {
            for (var x = -radius; x < radius; x++)
            {
                var continental = FractalNoise(x * 0.018f, z * 0.018f, settings.Seed, 4);
                var detail = FractalNoise(x * 0.065f, z * 0.065f, settings.Seed + 7919, 3);
                var height = settings.BaseHeight +
                    (int)MathF.Round((continental * 0.75f + detail * 0.25f) * settings.HeightVariation);
                height = Math.Max(4, height);
                var sandy = height <= settings.BaseHeight - 2;

                for (var y = 0; y <= height; y++)
                {
                    var depth = height - y;
                    var block = depth switch
                    {
                        0 when sandy => VoxelBlock.Sand,
                        0 => VoxelBlock.Grass,
                        <= 3 when sandy => VoxelBlock.Sand,
                        <= 3 => VoxelBlock.Dirt,
                        _ => VoxelBlock.Stone
                    };
                    world.SetBlock(x, y, z, block);
                }

                var treeHash = Hash(x, 0, z, settings.Seed + 104729);
                if (!sandy &&
                    height > settings.BaseHeight - 1 &&
                    (treeHash & 0x1ffu) == 0u &&
                    x > -radius + 3 && x < radius - 3 &&
                    z > -radius + 3 && z < radius - 3)
                {
                    AddTree(world, x, height + 1, z, treeHash);
                }
            }
        }

        if (settings.BuildMeshes)
        {
            world.BuildAllMeshes();
        }

        return world;
    }

    private static void AddTree(VoxelWorld world, int x, int y, int z, uint hash)
    {
        var trunkHeight = 4 + (int)((hash >> 12) & 1u);
        for (var trunkY = 0; trunkY < trunkHeight; trunkY++)
        {
            world.SetBlock(x, y + trunkY, z, VoxelBlock.Wood);
        }

        var crownY = y + trunkHeight - 2;
        for (var dy = 0; dy <= 3; dy++)
        {
            var radius = dy is 0 or 3 ? 1 : 2;
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) == radius && Math.Abs(dz) == radius && ((hash + (uint)(dx * 7 + dz * 13 + dy * 17)) & 1u) == 0u)
                    {
                        continue;
                    }

                    if (world.GetBlock(x + dx, crownY + dy, z + dz) == VoxelBlock.Air)
                    {
                        world.SetBlock(x + dx, crownY + dy, z + dz, VoxelBlock.Leaves);
                    }
                }
            }
        }
    }

    private static float FractalNoise(float x, float z, int seed, int octaves)
    {
        var sum = 0f;
        var amplitude = 1f;
        var frequency = 1f;
        var normalization = 0f;

        for (var octave = 0; octave < octaves; octave++)
        {
            sum += SmoothValueNoise(x * frequency, z * frequency, seed + octave * 1013) * amplitude;
            normalization += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.03f;
        }

        return sum / normalization;
    }

    private static float SmoothValueNoise(float x, float z, int seed)
    {
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var tx = Smooth(x - x0);
        var tz = Smooth(z - z0);

        var a = HashToSignedUnit(Hash(x0, 0, z0, seed));
        var b = HashToSignedUnit(Hash(x0 + 1, 0, z0, seed));
        var c = HashToSignedUnit(Hash(x0, 0, z0 + 1, seed));
        var d = HashToSignedUnit(Hash(x0 + 1, 0, z0 + 1, seed));
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
    }

    private static float Smooth(float value) => value * value * (3f - 2f * value);

    private static float Lerp(float a, float b, float amount) => a + (b - a) * amount;

    private static float HashToSignedUnit(uint value) =>
        (value & 0x00ffffffu) / 8388607.5f - 1f;

    private static uint Hash(int x, int y, int z, int seed)
    {
        var value = unchecked((uint)seed);
        value ^= unchecked((uint)x) * 0x9e3779b1u;
        value = (value << 13) | (value >> 19);
        value ^= unchecked((uint)y) * 0x85ebca77u;
        value = (value << 11) | (value >> 21);
        value ^= unchecked((uint)z) * 0xc2b2ae3du;
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }
}
