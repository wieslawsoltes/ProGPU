using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class TileImageVerticesTests
{
    [Theory]
    [InlineData(TileImageSampling.Nearest, -128f)]
    [InlineData(TileImageSampling.Linear, -64f)]
    [InlineData(TileImageSampling.Fant, -32f)]
    public void OccupiedExtentIsIndependentOfPoolSize(TileImageSampling sampling, float coefficient)
    {
        Span<VectorVertex> vertices = stackalloc VectorVertex[5];
        vertices[4].BrushIndex = 42;
        for (int u = 0; u < 3; u++)
        for (int v = 0; v < 3; v++)
        {
            Assert.True(TileImageVertices.TryWriteQuad(vertices, new(8, 4, 32, 16),
                new(0.125f, 0, 0, 0.25f, -2, -2), 3, 5, 64, 64,
                (TileImageAddressMode)u, (TileImageAddressMode)v, sampling, 0.5f));
            Assert.Equal(new Vector2(-1, -1), vertices[0].TexCoord);
            Assert.Equal(new Vector2(3, 3), vertices[2].TexCoord);
            Assert.Equal(new Vector4(3, 5, 0, 0.5f), vertices[2].Color);
            Assert.Equal(-2, vertices[2].BrushIndex);
            Assert.Equal(coefficient, vertices[2].ShapeSize.X);
            Assert.Equal(u, vertices[2].CornerRadius);
            Assert.Equal(v, vertices[2].StrokeThickness);
            Assert.Equal(42, vertices[4].BrushIndex);
        }
    }

    [Fact]
    public void InvalidMappingPreservesDestination()
    {
        Span<VectorVertex> vertices = stackalloc VectorVertex[4];
        vertices[0].BrushIndex = 42;
        Assert.False(TileImageVertices.TryWriteQuad(vertices, new(0, 0, 8, 8),
            Matrix3x2.Identity, 65, 5, 64, 64, TileImageAddressMode.Repeat,
            TileImageAddressMode.Clamp, false, 1));
        Assert.False(TileImageVertices.TryWriteQuad(vertices, new(float.MaxValue, 0, float.MaxValue, 8),
            Matrix3x2.Identity, 3, 5, 64, 64, TileImageAddressMode.Repeat,
            TileImageAddressMode.Clamp, false, 1));
        Assert.Equal(42, vertices[0].BrushIndex);
    }
}
