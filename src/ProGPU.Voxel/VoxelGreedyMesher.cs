using System.Numerics;

namespace ProGPU.Voxel;

/// <summary>
/// Builds exposed chunk surfaces by greedily merging equal coplanar faces.
/// Time is O(3 * (S + 1) * S²), or O(S³), and temporary storage is O(S²).
/// </summary>
public static class VoxelGreedyMesher
{
    private readonly record struct FaceKey(VoxelBlock Block, sbyte Direction)
    {
        public bool IsEmpty => Block == VoxelBlock.Air;
    }

    public static VoxelMesh Build(VoxelWorld world, VoxelChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(chunk);

        var vertices = new List<VoxelVertex>(1024);
        var indices = new List<uint>(1536);
        var mask = new FaceKey[VoxelChunk.Size * VoxelChunk.Size];
        var origin = chunk.Position.WorldOrigin;
        var originX = (int)origin.X;
        var originY = (int)origin.Y;
        var originZ = (int)origin.Z;
        var visibleFaces = 0;
        var mergedQuads = 0;

        Span<int> coordinate = stackalloc int[3];

        for (var axis = 0; axis < 3; axis++)
        {
            var uAxis = (axis + 1) % 3;
            var vAxis = (axis + 2) % 3;

            for (var slice = 0; slice <= VoxelChunk.Size; slice++)
            {
                var maskIndex = 0;
                for (var v = 0; v < VoxelChunk.Size; v++)
                {
                    for (var u = 0; u < VoxelChunk.Size; u++)
                    {
                        coordinate[axis] = slice - 1;
                        coordinate[uAxis] = u;
                        coordinate[vAxis] = v;
                        var negative = GetWorldBlock(world, originX, originY, originZ, coordinate);

                        coordinate[axis] = slice;
                        var positive = GetWorldBlock(world, originX, originY, originZ, coordinate);

                        var negativeOpaque = VoxelBlockCatalog.IsOpaque(negative);
                        var positiveOpaque = VoxelBlockCatalog.IsOpaque(positive);
                        if (negativeOpaque == positiveOpaque)
                        {
                            mask[maskIndex++] = default;
                        }
                        else if (negativeOpaque)
                        {
                            mask[maskIndex++] = new FaceKey(negative, 1);
                            visibleFaces++;
                        }
                        else
                        {
                            mask[maskIndex++] = new FaceKey(positive, -1);
                            visibleFaces++;
                        }
                    }
                }

                for (var v = 0; v < VoxelChunk.Size; v++)
                {
                    for (var u = 0; u < VoxelChunk.Size;)
                    {
                        var start = u + v * VoxelChunk.Size;
                        var face = mask[start];
                        if (face.IsEmpty)
                        {
                            u++;
                            continue;
                        }

                        var width = 1;
                        while (u + width < VoxelChunk.Size && mask[start + width] == face)
                        {
                            width++;
                        }

                        var height = 1;
                        var canGrow = true;
                        while (v + height < VoxelChunk.Size && canGrow)
                        {
                            var rowStart = start + height * VoxelChunk.Size;
                            for (var x = 0; x < width; x++)
                            {
                                if (mask[rowStart + x] != face)
                                {
                                    canGrow = false;
                                    break;
                                }
                            }

                            if (canGrow)
                            {
                                height++;
                            }
                        }

                        EmitQuad(vertices, indices, axis, uAxis, vAxis, slice, u, v, width, height, face);
                        mergedQuads++;

                        for (var clearV = 0; clearV < height; clearV++)
                        {
                            var rowStart = start + clearV * VoxelChunk.Size;
                            for (var clearU = 0; clearU < width; clearU++)
                            {
                                mask[rowStart + clearU] = default;
                            }
                        }

                        u += width;
                    }
                }
            }
        }

        return new VoxelMesh(
            vertices.ToArray(),
            indices.ToArray(),
            chunk.MeshVersion,
            visibleFaces,
            mergedQuads);
    }

    private static VoxelBlock GetWorldBlock(
        VoxelWorld world,
        int originX,
        int originY,
        int originZ,
        ReadOnlySpan<int> coordinate) =>
        world.GetBlock(
            originX + coordinate[0],
            originY + coordinate[1],
            originZ + coordinate[2]);

    private static void EmitQuad(
        List<VoxelVertex> vertices,
        List<uint> indices,
        int axis,
        int uAxis,
        int vAxis,
        int slice,
        int u,
        int v,
        int width,
        int height,
        FaceKey face)
    {
        Span<float> p = stackalloc float[3];
        Span<float> du = stackalloc float[3];
        Span<float> dv = stackalloc float[3];
        p[axis] = slice;
        p[uAxis] = u;
        p[vAxis] = v;
        du[uAxis] = width;
        dv[vAxis] = height;

        var p0 = new Vector3(p[0], p[1], p[2]);
        var uVector = new Vector3(du[0], du[1], du[2]);
        var vVector = new Vector3(dv[0], dv[1], dv[2]);
        var p1 = p0 + uVector;
        var p2 = p1 + vVector;
        var p3 = p0 + vVector;
        var faceIndex = GetFaceIndex(axis, face.Direction);
        var packed = PackMaterial(face.Block, faceIndex, GetFaceLight(faceIndex));
        var baseIndex = (uint)vertices.Count;

        vertices.Add(new VoxelVertex(p0, Vector2.Zero, packed));
        vertices.Add(new VoxelVertex(p1, new Vector2(width, 0), packed));
        vertices.Add(new VoxelVertex(p2, new Vector2(width, height), packed));
        vertices.Add(new VoxelVertex(p3, new Vector2(0, height), packed));

        if (face.Direction > 0)
        {
            indices.Add(baseIndex);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 3);
        }
        else
        {
            indices.Add(baseIndex);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 3);
            indices.Add(baseIndex + 2);
        }
    }

    private static uint PackMaterial(VoxelBlock block, uint face, uint light) =>
        (uint)block | (face << 8) | (light << 16);

    private static uint GetFaceIndex(int axis, sbyte direction) => (axis, direction) switch
    {
        (0, > 0) => 0,
        (0, < 0) => 1,
        (1, > 0) => 2,
        (1, < 0) => 3,
        (2, > 0) => 4,
        _ => 5
    };

    private static uint GetFaceLight(uint face) => face switch
    {
        2 => 255,
        3 => 105,
        0 => 218,
        1 => 175,
        4 => 202,
        _ => 158
    };
}
