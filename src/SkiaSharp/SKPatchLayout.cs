using System.Numerics;
using ProGPU.Scene;

namespace SkiaSharp;

internal static class SKPatchLayout
{
    private const float PartitionSize = 10f;

    internal static VertexMesh2D CreateMesh(
        SKPoint[] cubics,
        SKColor[]? colors,
        SKPoint[]? textureCoordinates,
        Matrix4x4 transform)
    {
        GetLevelOfDetail(cubics, transform, out var lodX, out var lodY);
        if (lodX == 0 || lodY == 0)
        {
            return VertexMesh2D.CreateOwned(
                VertexMeshTopology.Triangles,
                [],
                [],
                [],
                []);
        }

        var vertexCount = checked((lodX + 1) * (lodY + 1));
        if (vertexCount > 10000 || lodX > 200 || lodY > 200)
        {
            var total = lodX + lodY;
            var weightX = (float)lodX / total;
            var weightY = (float)lodY / total;
            lodX = Math.Max(1, (int)MathF.Floor(weightX * 200f));
            lodY = Math.Max(1, (int)MathF.Floor(weightY * 200f));
            vertexCount = checked((lodX + 1) * (lodY + 1));
        }

        var positions = GC.AllocateUninitializedArray<Vector2>(vertexCount);
        var meshColors = colors == null
            ? Array.Empty<Vector4>()
            : GC.AllocateUninitializedArray<Vector4>(vertexCount);
        var meshTextureCoordinates = textureCoordinates == null
            ? Array.Empty<Vector2>()
            : GC.AllocateUninitializedArray<Vector2>(vertexCount);
        var indices = GC.AllocateUninitializedArray<ushort>(checked(lodX * lodY * 6));

        var top0 = ToVector(cubics[0]);
        var top1 = ToVector(cubics[1]);
        var top2 = ToVector(cubics[2]);
        var top3 = ToVector(cubics[3]);
        var right1 = ToVector(cubics[4]);
        var right2 = ToVector(cubics[5]);
        var bottom3 = ToVector(cubics[6]);
        var bottom2 = ToVector(cubics[7]);
        var bottom1 = ToVector(cubics[8]);
        var bottom0 = ToVector(cubics[9]);
        var left2 = ToVector(cubics[10]);
        var left1 = ToVector(cubics[11]);
        var color0 = colors == null ? default : ToPremultiplied(colors[0]);
        var color1 = colors == null ? default : ToPremultiplied(colors[1]);
        var color2 = colors == null ? default : ToPremultiplied(colors[2]);
        var color3 = colors == null ? default : ToPremultiplied(colors[3]);
        var texture0 = textureCoordinates == null ? default : ToVector(textureCoordinates[0]);
        var texture1 = textureCoordinates == null ? default : ToVector(textureCoordinates[1]);
        var texture2 = textureCoordinates == null ? default : ToVector(textureCoordinates[2]);
        var texture3 = textureCoordinates == null ? default : ToVector(textureCoordinates[3]);

        var stride = lodY + 1;
        for (var x = 0; x <= lodX; x++)
        {
            var u = (float)x / lodX;
            var topPoint = EvaluateCubic(top0, top1, top2, top3, u);
            var bottomPoint = EvaluateCubic(bottom0, bottom1, bottom2, bottom3, u);
            for (var y = 0; y <= lodY; y++)
            {
                var v = (float)y / lodY;
                var leftPoint = EvaluateCubic(top0, left1, left2, bottom0, v);
                var rightPoint = EvaluateCubic(top3, right1, right2, bottom3, v);
                var ruledVertical = Vector2.Lerp(topPoint, bottomPoint, v);
                var ruledHorizontal = Vector2.Lerp(leftPoint, rightPoint, u);
                var bilinearCorners = Bilerp(top0, top3, bottom0, bottom3, u, v);
                var vertexIndex = x * stride + y;
                positions[vertexIndex] = ruledVertical + ruledHorizontal - bilinearCorners;

                if (colors != null)
                {
                    meshColors[vertexIndex] = ToStraight(Bilerp(
                        color0,
                        color1,
                        color3,
                        color2,
                        u,
                        v));
                }
                if (textureCoordinates != null)
                {
                    meshTextureCoordinates[vertexIndex] = Bilerp(
                        texture0,
                        texture1,
                        texture3,
                        texture2,
                        u,
                        v);
                }

                if (x < lodX && y < lodY)
                {
                    var index = 6 * (x * lodY + y);
                    indices[index] = checked((ushort)(x * stride + y));
                    indices[index + 1] = checked((ushort)(x * stride + y + 1));
                    indices[index + 2] = checked((ushort)((x + 1) * stride + y + 1));
                    indices[index + 3] = indices[index];
                    indices[index + 4] = indices[index + 2];
                    indices[index + 5] = checked((ushort)((x + 1) * stride + y));
                }
            }
        }

        return VertexMesh2D.CreateOwned(
            VertexMeshTopology.Triangles,
            positions,
            meshTextureCoordinates,
            meshColors,
            indices);
    }

    internal static void GetLevelOfDetail(
        SKPoint[] cubics,
        Matrix4x4 transform,
        out int lodX,
        out int lodY)
    {
        var topLength = ApproximateLength(cubics, transform, 0, 1, 2, 3);
        var rightLength = ApproximateLength(cubics, transform, 3, 4, 5, 6);
        var bottomLength = ApproximateLength(cubics, transform, 9, 8, 7, 6);
        var leftLength = ApproximateLength(cubics, transform, 0, 11, 10, 9);
        if (!float.IsFinite(topLength) ||
            !float.IsFinite(rightLength) ||
            !float.IsFinite(bottomLength) ||
            !float.IsFinite(leftLength))
        {
            lodX = 0;
            lodY = 0;
            return;
        }

        lodX = Math.Max(8, (int)(MathF.Max(topLength, bottomLength) / PartitionSize));
        lodY = Math.Max(8, (int)(MathF.Max(leftLength, rightLength) / PartitionSize));
    }

    private static float ApproximateLength(
        SKPoint[] cubics,
        Matrix4x4 transform,
        int index0,
        int index1,
        int index2,
        int index3)
    {
        var p0 = Vector2.Transform(ToVector(cubics[index0]), transform);
        var p1 = Vector2.Transform(ToVector(cubics[index1]), transform);
        var p2 = Vector2.Transform(ToVector(cubics[index2]), transform);
        var p3 = Vector2.Transform(ToVector(cubics[index3]), transform);
        return Vector2.Distance(p0, p1) + Vector2.Distance(p1, p2) + Vector2.Distance(p2, p3);
    }

    private static Vector2 EvaluateCubic(
        Vector2 point0,
        Vector2 point1,
        Vector2 point2,
        Vector2 point3,
        float t)
    {
        var oneMinusT = 1f - t;
        var oneMinusTSquared = oneMinusT * oneMinusT;
        var tSquared = t * t;
        return point0 * (oneMinusTSquared * oneMinusT) +
            point1 * (3f * oneMinusTSquared * t) +
            point2 * (3f * oneMinusT * tSquared) +
            point3 * (tSquared * t);
    }

    private static Vector2 Bilerp(
        Vector2 topLeft,
        Vector2 topRight,
        Vector2 bottomLeft,
        Vector2 bottomRight,
        float u,
        float v) =>
        Vector2.Lerp(
            Vector2.Lerp(topLeft, topRight, u),
            Vector2.Lerp(bottomLeft, bottomRight, u),
            v);

    private static Vector4 Bilerp(
        Vector4 topLeft,
        Vector4 topRight,
        Vector4 bottomLeft,
        Vector4 bottomRight,
        float u,
        float v) =>
        Vector4.Lerp(
            Vector4.Lerp(topLeft, topRight, u),
            Vector4.Lerp(bottomLeft, bottomRight, u),
            v);

    private static Vector2 ToVector(SKPoint point) => new(point.X, point.Y);

    private static Vector4 ToPremultiplied(SKColor color)
    {
        var alpha = color.Alpha / 255f;
        return new Vector4(
            color.Red / 255f * alpha,
            color.Green / 255f * alpha,
            color.Blue / 255f * alpha,
            alpha);
    }

    private static Vector4 ToStraight(Vector4 color) => color.W > 0f
        ? new Vector4(color.X / color.W, color.Y / color.W, color.Z / color.W, color.W)
        : Vector4.Zero;
}
