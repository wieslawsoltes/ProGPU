using System.Numerics;

namespace ProGPU.Voxel;

/// <summary>
/// Conservative homogeneous-clip AABB test. It performs fixed O(1) work with no allocation.
/// </summary>
public static class VoxelFrustum
{
    public static bool Intersects(Matrix4x4 viewProjection, Vector3 minimum, Vector3 maximum)
    {
        Span<Vector4> corners = stackalloc Vector4[8];
        corners[0] = Transform(minimum.X, minimum.Y, minimum.Z, viewProjection);
        corners[1] = Transform(maximum.X, minimum.Y, minimum.Z, viewProjection);
        corners[2] = Transform(minimum.X, maximum.Y, minimum.Z, viewProjection);
        corners[3] = Transform(maximum.X, maximum.Y, minimum.Z, viewProjection);
        corners[4] = Transform(minimum.X, minimum.Y, maximum.Z, viewProjection);
        corners[5] = Transform(maximum.X, minimum.Y, maximum.Z, viewProjection);
        corners[6] = Transform(minimum.X, maximum.Y, maximum.Z, viewProjection);
        corners[7] = Transform(maximum.X, maximum.Y, maximum.Z, viewProjection);

        for (var plane = 0; plane < 6; plane++)
        {
            var allOutside = true;
            for (var i = 0; i < corners.Length; i++)
            {
                var point = corners[i];
                var outside = plane switch
                {
                    0 => point.X < -point.W,
                    1 => point.X > point.W,
                    2 => point.Y < -point.W,
                    3 => point.Y > point.W,
                    4 => point.Z < 0f,
                    _ => point.Z > point.W
                };
                if (!outside)
                {
                    allOutside = false;
                    break;
                }
            }

            if (allOutside)
            {
                return false;
            }
        }

        return true;
    }

    private static Vector4 Transform(float x, float y, float z, Matrix4x4 matrix) =>
        Vector4.Transform(new Vector4(x, y, z, 1f), matrix);

}
