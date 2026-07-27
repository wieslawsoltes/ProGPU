using System.Numerics;

namespace ProGPU.Voxel;

public readonly record struct VoxelRaycastHit(
    (int X, int Y, int Z) Block,
    (int X, int Y, int Z) Previous,
    (int X, int Y, int Z) Normal,
    float Distance,
    VoxelBlock BlockType);

/// <summary>
/// Grid DDA traversal. Time is O(number of crossed voxel boundaries), storage is O(1).
/// </summary>
public static class VoxelRaycaster
{
    public static bool TryCast(
        VoxelWorld world,
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out VoxelRaycastHit hit)
    {
        ArgumentNullException.ThrowIfNull(world);
        hit = default;
        if (maxDistance <= 0f || direction.LengthSquared() < 1e-12f)
        {
            return false;
        }

        direction = Vector3.Normalize(direction);
        var x = (int)MathF.Floor(origin.X);
        var y = (int)MathF.Floor(origin.Y);
        var z = (int)MathF.Floor(origin.Z);
        var previous = (X: x, Y: y, Z: z);
        var stepX = Math.Sign(direction.X);
        var stepY = Math.Sign(direction.Y);
        var stepZ = Math.Sign(direction.Z);
        var deltaX = direction.X == 0f ? float.PositiveInfinity : MathF.Abs(1f / direction.X);
        var deltaY = direction.Y == 0f ? float.PositiveInfinity : MathF.Abs(1f / direction.Y);
        var deltaZ = direction.Z == 0f ? float.PositiveInfinity : MathF.Abs(1f / direction.Z);
        var maxX = InitialBoundaryDistance(origin.X, direction.X, x, stepX);
        var maxY = InitialBoundaryDistance(origin.Y, direction.Y, y, stepY);
        var maxZ = InitialBoundaryDistance(origin.Z, direction.Z, z, stepZ);
        var normal = (X: 0, Y: 0, Z: 0);
        var distance = 0f;

        while (distance <= maxDistance)
        {
            var block = world.GetBlock(x, y, z);
            if (block != VoxelBlock.Air && block != VoxelBlock.Water)
            {
                hit = new VoxelRaycastHit((x, y, z), previous, normal, distance, block);
                return true;
            }

            previous = (x, y, z);
            if (maxX <= maxY && maxX <= maxZ)
            {
                x += stepX;
                distance = maxX;
                maxX += deltaX;
                normal = (-stepX, 0, 0);
            }
            else if (maxY <= maxZ)
            {
                y += stepY;
                distance = maxY;
                maxY += deltaY;
                normal = (0, -stepY, 0);
            }
            else
            {
                z += stepZ;
                distance = maxZ;
                maxZ += deltaZ;
                normal = (0, 0, -stepZ);
            }
        }

        return false;
    }

    private static float InitialBoundaryDistance(float origin, float direction, int cell, int step)
    {
        if (step == 0)
        {
            return float.PositiveInfinity;
        }

        var boundary = step > 0 ? cell + 1f : cell;
        return (boundary - origin) / direction;
    }
}
