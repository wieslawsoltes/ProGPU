using System.Numerics;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasPatchCompatibilityTests
{
    [Fact]
    public void DrawPatchValidatesNativeArrayLengths()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 32f, 32f);
        using var paint = new SKPaint();
        var cubics = CreateRectanglePatch();

        Assert.Throws<ArgumentNullException>(() => canvas.DrawPatch(null!, null, null, paint));
        Assert.Throws<ArgumentException>(() => canvas.DrawPatch(new SKPoint[11], null, null, paint));
        Assert.Throws<ArgumentException>(() => canvas.DrawPatch(cubics, new SKColor[3], null, paint));
        Assert.Throws<ArgumentException>(() => canvas.DrawPatch(cubics, null, new SKPoint[3], paint));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawPatch(cubics, null, null, null!));
    }

    [Fact]
    public void RectanglePatchRecordsOneAdaptiveMeshWithCornerMetadata()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 32f, 32f);
        using var paint = new SKPaint { IsAntialias = false };
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White };
        var textureCoordinates = new[]
        {
            new SKPoint(0f, 0f),
            new SKPoint(1f, 0f),
            new SKPoint(1f, 1f),
            new SKPoint(0f, 1f)
        };

        canvas.DrawPatch(
            CreateRectanglePatch(),
            colors,
            textureCoordinates,
            SKBlendMode.SrcOver,
            paint);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawVertexMesh, command.Type);
        Assert.Equal(VertexColorBlendMode.SrcOver, command.VertexColorBlendMode);
        Assert.True(command.IsEdgeAliased);
        var mesh = Assert.IsType<VertexMesh2D>(command.VertexMesh);
        Assert.Equal(81, mesh.Positions.Length);
        Assert.Equal(384, mesh.Indices.Length);
        Assert.Equal(new Vector2(0f, 0f), mesh.Positions.Span[0]);
        Assert.Equal(new Vector2(10f, 0f), mesh.Positions.Span[72]);
        Assert.Equal(new Vector2(10f, 10f), mesh.Positions.Span[80]);
        Assert.Equal(new Vector2(0f, 10f), mesh.Positions.Span[8]);
        AssertVectorNear(new Vector2(5f, 5f), mesh.Positions.Span[40]);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), mesh.Colors.Span[0]);
        Assert.Equal(new Vector2(1f, 1f), mesh.TextureCoordinates.Span[80]);
    }

    [Fact]
    public void PatchLevelOfDetailUsesDeviceSpaceAndCapsIndexCount()
    {
        var cubics = CreateRectanglePatch();
        SKPatchLayout.GetLevelOfDetail(
            cubics,
            Matrix4x4.CreateScale(20f),
            out var lodX,
            out var lodY);
        Assert.Equal(20, lodX);
        Assert.Equal(20, lodY);

        var capped = SKPatchLayout.CreateMesh(
            cubics,
            colors: null,
            textureCoordinates: null,
            Matrix4x4.CreateScale(1000f));
        Assert.Equal(10201, capped.Positions.Length);
        Assert.Equal(60000, capped.Indices.Length);
    }

    [Fact]
    public void PatchColorsInterpolateInPremultipliedSpace()
    {
        var mesh = SKPatchLayout.CreateMesh(
            CreateRectanglePatch(),
            [
                new SKColor(255, 0, 0, 255),
                new SKColor(0, 0, 255, 0),
                new SKColor(0, 0, 255, 0),
                new SKColor(255, 0, 0, 255)
            ],
            textureCoordinates: null,
            Matrix4x4.Identity);

        var center = mesh.Colors.Span[40];
        AssertVectorNear(new Vector4(1f, 0f, 0f, 0.5f), center);
    }

    private static SKPoint[] CreateRectanglePatch() =>
    [
        new SKPoint(0f, 0f),
        new SKPoint(10f / 3f, 0f),
        new SKPoint(20f / 3f, 0f),
        new SKPoint(10f, 0f),
        new SKPoint(10f, 10f / 3f),
        new SKPoint(10f, 20f / 3f),
        new SKPoint(10f, 10f),
        new SKPoint(20f / 3f, 10f),
        new SKPoint(10f / 3f, 10f),
        new SKPoint(0f, 10f),
        new SKPoint(0f, 20f / 3f),
        new SKPoint(0f, 10f / 3f)
    ];

    private static void AssertVectorNear(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(Vector2.Distance(expected, actual), 0f, 0.0001f);
    }

    private static void AssertVectorNear(Vector4 expected, Vector4 actual)
    {
        Assert.InRange(Vector4.Distance(expected, actual), 0f, 0.0001f);
    }
}
