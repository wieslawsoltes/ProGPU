using System.Numerics;
using ProGPU.Scene;

namespace SkiaSharp;

public enum SKVertexMode
{
    Triangles,
    TriangleStrip,
    TriangleFan
}

public class SKVertices : SKObject
{
    internal VertexMesh2D Mesh { get; }

    internal SKVertices(VertexMesh2D mesh)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Mesh = mesh;
    }

    public static SKVertices CreateCopy(
        SKVertexMode vmode,
        SKPoint[] positions,
        SKColor[]? colors) =>
        CreateCopy(vmode, positions, null, colors, null);

    public static SKVertices CreateCopy(
        SKVertexMode vmode,
        SKPoint[] positions,
        SKPoint[]? texs,
        SKColor[]? colors) =>
        CreateCopy(vmode, positions, texs, colors, null);

    public static SKVertices CreateCopy(
        SKVertexMode vmode,
        SKPoint[] positions,
        SKPoint[]? texs,
        SKColor[]? colors,
        ushort[]? indices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (texs is not null && positions.Length != texs.Length)
        {
            throw new ArgumentException(
                "The number of texture coordinates must match the number of vertices.",
                nameof(texs));
        }

        if (colors is not null && positions.Length != colors.Length)
        {
            throw new ArgumentException(
                "The number of colors must match the number of vertices.",
                nameof(colors));
        }

        var meshPositions = GC.AllocateUninitializedArray<Vector2>(positions.Length);
        for (var index = 0; index < positions.Length; index++)
        {
            meshPositions[index] = new Vector2(positions[index].X, positions[index].Y);
        }

        var meshTextureCoordinates = texs is null
            ? Array.Empty<Vector2>()
            : GC.AllocateUninitializedArray<Vector2>(texs.Length);
        if (texs is not null)
        {
            for (var index = 0; index < texs.Length; index++)
            {
                meshTextureCoordinates[index] = new Vector2(texs[index].X, texs[index].Y);
            }
        }

        var meshColors = colors is null
            ? Array.Empty<Vector4>()
            : GC.AllocateUninitializedArray<Vector4>(colors.Length);
        if (colors is not null)
        {
            for (var index = 0; index < colors.Length; index++)
            {
                var color = colors[index];
                meshColors[index] = new Vector4(
                    color.Red / 255f,
                    color.Green / 255f,
                    color.Blue / 255f,
                    color.Alpha / 255f);
            }
        }

        var meshIndices = indices is null ? Array.Empty<ushort>() : (ushort[])indices.Clone();
        return new SKVertices(VertexMesh2D.CreateOwned(
            (VertexMeshTopology)vmode,
            meshPositions,
            meshTextureCoordinates,
            meshColors,
            meshIndices));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
