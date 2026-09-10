using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPictureSerializationCompatibilityTests
{
    [Fact]
    public void ArchiveRebuildsOnlyIntrinsicStrokeGeometryCaches()
    {
        var recorder = new GpuPictureRecorder();
        var context = recorder.BeginRecording(new Rect(0f, 0f, 64f, 64f));
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            thickness: 3f,
            lineJoin: PenLineJoin.Round,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Triangle,
            dashCap: PenLineCap.Square,
            dashArray: [2d, 1d]);
        context.DrawLine(pen, new Vector2(2f, 4f), new Vector2(18f, 4f));
        context.DrawQuadraticBezier(
            pen,
            new Vector2(2f, 10f),
            new Vector2(10f, 2f),
            new Vector2(18f, 10f));
        context.DrawCubicBezier(
            pen,
            new Vector2(2f, 16f),
            new Vector2(6f, 8f),
            new Vector2(14f, 24f),
            new Vector2(18f, 16f));
        context.DrawPolyline(
            pen,
            [new Vector2(2f, 24f), new Vector2(10f, 18f), new Vector2(18f, 24f)]);
        context.DrawSpline(
            pen,
            [new Vector2(2f, 32f), new Vector2(8f, 26f), new Vector2(14f, 38f), new Vector2(20f, 32f)],
            [0d, 0d, 0d, 1d, 2d, 2d, 2d],
            degree: 2);
        var maskPath = RenderCommandGeometryCache.CreateLinePath(
            new Vector2(2f, 42f),
            new Vector2(20f, 42f));
        context.PushOpacityMask(
            maskPath,
            pen,
            new Rect(0f, 38f, 24f, 8f),
            Matrix4x4.Identity);
        context.PopOpacityMask();

        using var gpuPicture = recorder.EndRecording();
        using var picture = new SKPicture(
            gpuPicture,
            new SKRect(0f, 0f, 64f, 64f));
        using var data = picture.Serialize();
        using var copy = SKPicture.Deserialize(data);

        var commands = copy!.Picture.Commands;
        Assert.NotNull(commands[0].GeometryCache?.StrokePath);
        Assert.NotNull(commands[1].GeometryCache?.StrokePath);
        Assert.NotNull(commands[2].GeometryCache?.StrokePath);
        Assert.Null(Assert.IsType<RenderCommandGeometryCache>(
            commands[3].GeometryCache).StrokePath);
        Assert.Null(commands[4].GeometryCache);
        Assert.NotNull(commands[5].GeometryCache?.StrokePath);
        Assert.Same(commands[5].Path, commands[5].GeometryCache?.StrokePath);

        foreach (var index in new[] { 0, 1, 2, 5 })
        {
            var cache = Assert.IsType<RenderCommandGeometryCache>(commands[index].GeometryCache);
            Assert.True(cache.TryGetDashedStrokePath(
                commands[index].Pen!,
                out var firstPath,
                out var firstPen));
            Assert.True(cache.TryGetDashedStrokePath(
                commands[index].Pen!,
                out var secondPath,
                out var secondPen));
            Assert.Same(firstPath, secondPath);
            Assert.Same(firstPen, secondPen);
        }
    }

    [Fact]
    public void VectorArchiveRoundTripsPathsBrushesPensAndBuffers()
    {
        var path = new PathGeometry { FillRule = FillRule.EvenOdd };
        var figure = new PathFigure(new Vector2(1f, 2f), isClosed: true)
        {
            IsFilled = false,
        };
        figure.Segments.Add(new LineSegment(new Vector2(3f, 4f), isSmoothJoin: true));
        figure.Segments.Add(new QuadraticBezierSegment(new Vector2(5f, 6f), new Vector2(7f, 8f)));
        figure.Segments.Add(new CubicBezierSegment(
            new Vector2(9f, 10f),
            new Vector2(11f, 12f),
            new Vector2(13f, 14f)));
        figure.Segments.Add(new ArcSegment(
            new Vector2(15f, 16f),
            new Vector2(4f, 5f),
            30f,
            isLargeArc: true,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);

        var stops = new[]
        {
            new GradientStop(new Vector4(1f, 0f, 0f, 1f), 0f),
            new GradientStop(new Vector4(0f, 0f, 1f, 0.5f), 1f),
        };
        var brush = new LinearGradientBrush(new Vector2(2f, 3f), new Vector2(20f, 30f), stops)
        {
            Opacity = 0.75f,
            CoordinateTransform = Matrix4x4.CreateTranslation(4f, 6f, 0f),
            SpreadMethod = GradientSpreadMethod.Reflect,
            ColorInterpolationMode = GradientColorInterpolationMode.ScRgbLinearInterpolation,
        };
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0.2f, 0.3f, 0.4f, 0.5f)),
            thickness: 3f,
            lineJoin: PenLineJoin.Round,
            miterLimit: 7f,
            startLineCap: PenLineCap.Square,
            endLineCap: PenLineCap.Round,
            dashCap: PenLineCap.Triangle,
            dashArray: [2d, 4d],
            dashOffset: 1.5d,
            strokeTransformMode: PenStrokeTransformMode.Fixed);
        var command = new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = 42,
            Rect = new Rect(1f, 2f, 30f, 40f),
            Brush = brush,
            Pen = pen,
            Path = path,
            Transform = Matrix4x4.CreateScale(2f, 3f, 1f),
            IsEdgeAliased = true,
            IsPenThicknessLocal = true,
            PathSampleGrid = 8,
            PathCoverageGamma = 0.72f,
        };
        var gpuPicture = new GpuPicture(
            [command],
            [new Vector2(1f, 2f)],
            [3d],
            [new Line3D(Vector3.One, new Vector3(4f, 5f, 6f))],
            [7f]);
        var cull = new SKRect(2f, 3f, 102f, 83f);
        using var picture = new SKPicture(gpuPicture, cull);
        using var data = picture.Serialize();
        using var copy = SKPicture.Deserialize(data);

        Assert.NotNull(copy);
        Assert.Equal(cull, copy.CullRect);
        Assert.NotEqual(picture.UniqueId, copy.UniqueId);
        Assert.Equal(gpuPicture.PointBuffer, copy.Picture.PointBuffer);
        Assert.Equal(gpuPicture.DoubleBuffer, copy.Picture.DoubleBuffer);
        Assert.Equal(gpuPicture.FloatBuffer, copy.Picture.FloatBuffer);
        Assert.Equal(gpuPicture.Line3DBuffer[0].Start, copy.Picture.Line3DBuffer[0].Start);

        var actual = Assert.Single(copy.Picture.Commands);
        Assert.Equal(RenderCommandType.DrawPath, actual.Type);
        Assert.Equal(42, actual.HitTestId);
        Assert.Equal(command.Transform, actual.Transform);
        Assert.True(actual.IsEdgeAliased);
        Assert.True(actual.IsPenThicknessLocal);
        Assert.Equal(8u, actual.PathSampleGrid);
        Assert.Equal(0.72f, actual.PathCoverageGamma);
        var actualBrush = Assert.IsType<LinearGradientBrush>(actual.Brush);
        Assert.Equal(brush.StartPoint, actualBrush.StartPoint);
        Assert.Equal(brush.EndPoint, actualBrush.EndPoint);
        Assert.Equal(brush.CoordinateTransform, actualBrush.CoordinateTransform);
        Assert.Equal(brush.SpreadMethod, actualBrush.SpreadMethod);
        Assert.Equal(brush.ColorInterpolationMode, actualBrush.ColorInterpolationMode);
        Assert.Equal(brush.Opacity, actualBrush.Opacity);
        Assert.Equal(stops.Length, actualBrush.Stops.Length);
        Assert.Equal(pen.DashArray, actual.Pen!.DashArray);
        Assert.Equal(PenLineCap.Triangle, actual.Pen.DashCap);
        Assert.Equal(PenStrokeTransformMode.Fixed, actual.Pen.StrokeTransformMode);
        Assert.Equal(FillRule.EvenOdd, actual.Path!.FillRule);
        Assert.False(Assert.Single(actual.Path.Figures).IsFilled);
        Assert.IsType<ArcSegment>(actual.Path.Figures[0].Segments[^1]);
        Assert.Same(actual.Path, actual.GeometryCache?.StrokePath);
        Assert.Same(actual.Path, actual.GeometryCache?.FillPath);
    }

    [Fact]
    public void VersionTwoArchiveDefaultsPenStrokeTransformModeToNormal()
    {
        var command = new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            Position = new Vector2(2f, 4f),
            Position2 = new Vector2(18f, 4f),
            Pen = new Pen(
                new SolidColorBrush(Vector4.One),
                3f,
                strokeTransformMode: PenStrokeTransformMode.Fixed),
            IsPenThicknessLocal = true
        };
        var picture = new GpuPicture([command], [], [], [], []);
        var bytes = PictureArchive.Serialize(
            picture,
            new SKRect(0f, 0f, 24f, 8f),
            archiveVersion: 2);

        using var copy = SKPicture.Deserialize(bytes);

        Assert.NotNull(copy);
        var actual = Assert.Single(copy.Picture.Commands);
        Assert.Equal(PenStrokeTransformMode.Normal, actual.Pen!.StrokeTransformMode);
    }

    [Fact]
    public void VersionThreeArchiveDefaultsHatchCoordinateTransformToIdentity()
    {
        var brush = new HatchPatternBrush(0.5f, 8f, 0f, Vector4.One)
        {
            CoordinateTransform = Matrix4x4.CreateTranslation(3f, 5f, 0f),
        };
        var picture = new GpuPicture(
            [new RenderCommand
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(0f, 0f, 24f, 8f),
                Brush = brush,
            }],
            [],
            [],
            [],
            []);
        byte[] bytes = PictureArchive.Serialize(
            picture,
            new SKRect(0f, 0f, 24f, 8f),
            archiveVersion: 3);

        using SKPicture? copy = SKPicture.Deserialize(bytes);

        Assert.NotNull(copy);
        var actual = Assert.IsType<HatchPatternBrush>(
            Assert.Single(copy.Picture.Commands).Brush);
        Assert.Equal(Matrix4x4.Identity, actual.CoordinateTransform);
    }

    [Fact]
    public void VersionFiveArchiveRoundTripsBoundedHatchPatternGrammar()
    {
        var brush = new HatchPatternSetBrush(
            [
                new HatchPatternLineFamily(
                    new Vector2(2f, 3f),
                    Vector2.UnitX,
                    4f,
                    5f,
                    0,
                    6,
                    8.5f),
                new HatchPatternLineFamily(
                    new Vector2(-1f, 7f),
                    Vector2.UnitY,
                    -2f,
                    9f,
                    6,
                    0,
                    0f),
            ],
            [2f, -1f, 0f, -0.5f, 3f, -2f],
            0f,
            new Vector4(0.1f, 0.2f, 0.3f, 1f))
        {
            Opacity = 0.75f,
            CoordinateTransform = Matrix4x4.CreateTranslation(11f, 13f, 0f),
        };
        using var picture = new SKPicture(
            new GpuPicture(
                [new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Brush = brush,
                }],
                [], [], [], []),
            new SKRect(0f, 0f, 20f, 20f));

        using SKData data = picture.Serialize();
        using SKPicture? copy = SKPicture.Deserialize(data);

        Assert.NotNull(copy);
        HatchPatternSetBrush actual = Assert.IsType<HatchPatternSetBrush>(
            Assert.Single(copy.Picture.Commands).Brush);
        Assert.Equal(brush.Opacity, actual.Opacity);
        Assert.Equal(brush.Color, actual.Color);
        Assert.Equal(brush.CoordinateTransform, actual.CoordinateTransform);
        Assert.Equal(brush.Families.ToArray(), actual.Families.ToArray());
        Assert.Equal(brush.Dashes.ToArray(), actual.Dashes.ToArray());
    }

    [Fact]
    public void ArchiveRoundTripsNestedPicturesAndVertexMeshes()
    {
        var child = new GpuPicture(
            [new RenderCommand { Type = RenderCommandType.DrawRect, Rect = new Rect(1f, 2f, 3f, 4f) }],
            [],
            [],
            [],
            []);
        var mesh = new VertexMesh2D(
            VertexMeshTopology.Triangles,
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [Vector4.One, Vector4.UnitX, Vector4.UnitY],
            [0, 1, 2]);
        var parent = new GpuPicture(
            [
                new RenderCommand { Type = RenderCommandType.DrawPicture, Picture = child },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawVertexMesh,
                    VertexMesh = mesh,
                    VertexColorBlendMode = VertexColorBlendMode.Modulate,
                    GlyphIndices = [3, 5],
                    GlyphPositions = [new Vector2(7f, 8f), new Vector2(9f, 10f)],
                },
            ],
            [],
            [],
            [],
            []);
        using var picture = new SKPicture(parent, new SKRect(0f, 0f, 64f, 64f));
        using var data = picture.Serialize();
        using var copy = SKPicture.Deserialize(data.AsSpan());

        Assert.NotNull(copy);
        Assert.Equal(2, copy.ApproximateOperationCount);
        Assert.Equal(2, copy.GetApproximateOperationCount(includeNested: true));
        Assert.NotNull(copy.Picture.Commands[0].Picture);
        Assert.Equal(RenderCommandType.DrawRect, copy.Picture.Commands[0].Picture!.Commands[0].Type);
        var actualMesh = copy.Picture.Commands[1].VertexMesh;
        Assert.NotNull(actualMesh);
        Assert.Equal(mesh.Positions.ToArray(), actualMesh.Positions.ToArray());
        Assert.Equal(mesh.TextureCoordinates.ToArray(), actualMesh.TextureCoordinates.ToArray());
        Assert.Equal(mesh.Colors.ToArray(), actualMesh.Colors.ToArray());
        Assert.Equal(mesh.Indices.ToArray(), actualMesh.Indices.ToArray());
        Assert.Equal([3, 5], copy.Picture.Commands[1].GlyphIndices);
        Assert.Equal(
            [new Vector2(7f, 8f), new Vector2(9f, 10f)],
            copy.Picture.Commands[1].GlyphPositions!);
    }

    [Fact]
    public void ArchiveRoundTripsEveryBuiltInPortableBrush()
    {
        var stops = new[]
        {
            new GradientStop(Vector4.One, 0f),
            new GradientStop(Vector4.Zero, 1f),
        };
        var conical = new TwoPointConicalGradientBrush(Vector2.Zero, 2f, Vector2.One, 8f, stops)
        {
            OutsideColor = new Vector4(0.1f, 0.2f, 0.3f, 0.4f),
        };
        var backdrop = new BackdropMaterialBrush
        {
            Kind = BackdropMaterialKind.Mica,
            Source = BackdropMaterialSource.Texture,
            TintOpacity = 0.25f,
            UseFallback = true,
        };
        Brush[] brushes =
        [
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            new RadialGradientBrush(Vector2.One, new Vector2(2f, 3f), 4f, 5f, stops),
            conical,
            new SweepGradientBrush(new Vector2(6f, 7f), stops)
            {
                StartAngle = -45f,
                EndAngle = 765f,
                SpreadMethod = GradientSpreadMethod.Reflect,
            },
            new PerlinNoiseBrush(true, new Vector2(0.1f, 0.2f), 4, 3f, new Vector2(32f, 48f)),
            new HatchPatternBrush(30f, 5f, 2f, Vector4.One)
            {
                CoordinateTransform = Matrix4x4.CreateTranslation(2f, 3f, 0f),
            },
            new CrossHatchBrush(45f, 6f, 3f, Vector4.UnitW)
            {
                CoordinateTransform = Matrix4x4.CreateScale(2f, 4f, 1f),
            },
            new ThemeResourceBrush("AccentBrush"),
            backdrop,
        ];
        for (var index = 0; index < brushes.Length; index++)
        {
            brushes[index].Opacity = 0.5f + index * 0.01f;
        }

        var commands = brushes
            .Select((brush, index) => new RenderCommand
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(index, index, 10f, 10f),
                Brush = brush,
            })
            .ToArray();
        using var picture = new SKPicture(
            new GpuPicture(commands, [], [], [], []),
            new SKRect(0f, 0f, 100f, 100f));
        using var data = picture.Serialize();
        using var copy = SKPicture.Deserialize(data);

        Assert.NotNull(copy);
        Assert.Equal(brushes.Select(brush => brush.GetType()), copy.Picture.Commands.Select(command => command.Brush!.GetType()));
        for (var index = 0; index < brushes.Length; index++)
        {
            Assert.Equal(brushes[index].Opacity, copy.Picture.Commands[index].Brush!.Opacity);
        }
        var actualConical = Assert.IsType<TwoPointConicalGradientBrush>(copy.Picture.Commands[2].Brush);
        Assert.Equal(conical.OutsideColor, actualConical.OutsideColor);
        var actualSweep = Assert.IsType<SweepGradientBrush>(copy.Picture.Commands[3].Brush);
        Assert.Equal(-45f, actualSweep.StartAngle);
        Assert.Equal(765f, actualSweep.EndAngle);
        Assert.Equal(GradientSpreadMethod.Reflect, actualSweep.SpreadMethod);
        var actualNoise = Assert.IsType<PerlinNoiseBrush>(copy.Picture.Commands[4].Brush);
        Assert.Equal(4, actualNoise.NumOctaves);
        Assert.Equal(new Vector2(32f, 48f), actualNoise.TileSize);
        var actualHatch = Assert.IsType<HatchPatternBrush>(copy.Picture.Commands[5].Brush);
        Assert.Equal(Matrix4x4.CreateTranslation(2f, 3f, 0f), actualHatch.CoordinateTransform);
        var actualCrossHatch = Assert.IsType<CrossHatchBrush>(copy.Picture.Commands[6].Brush);
        Assert.Equal(Matrix4x4.CreateScale(2f, 4f, 1f), actualCrossHatch.CoordinateTransform);
        Assert.Equal("AccentBrush", Assert.IsType<ThemeResourceBrush>(copy.Picture.Commands[7].Brush).ResourceKey);
        var actualBackdrop = Assert.IsType<BackdropMaterialBrush>(copy.Picture.Commands[8].Brush);
        Assert.Equal(BackdropMaterialKind.Mica, actualBackdrop.Kind);
        Assert.Equal(BackdropMaterialSource.Texture, actualBackdrop.Source);
        Assert.True(actualBackdrop.UseFallback);
    }

    [Fact]
    public void SerializationOverloadsShareOnePortableArchive()
    {
        using var picture = RecordSimplePicture();
        using var expected = picture.Serialize();

        using var managedOutput = new MemoryStream();
        picture.Serialize(managedOutput);
        Assert.Equal(expected.ToArray(), managedOutput.ToArray());
        managedOutput.Position = 0;
        using var managedCopy = SKPicture.Deserialize(managedOutput);
        Assert.NotNull(managedCopy);

        using var skiaOutput = new SKDynamicMemoryWStream();
        picture.Serialize(skiaOutput);
        using var skiaData = skiaOutput.CopyToData();
        Assert.Equal(expected.ToArray(), skiaData.ToArray());

        using var skiaInputStream = new MemoryStream(skiaData.ToArray());
        using var skiaInput = new SKManagedStream(skiaInputStream);
        using var skiaCopy = SKPicture.Deserialize(skiaInput);
        Assert.NotNull(skiaCopy);

        using var pointerCopy = SKPicture.Deserialize(expected.Data, checked((int)expected.Size));
        Assert.NotNull(pointerCopy);
        Assert.Equal(picture.CullRect, pointerCopy.CullRect);
    }

    [Fact]
    public void InvalidArchivesReturnNullAndNullInputsThrow()
    {
        Assert.Null(SKPicture.Deserialize(ReadOnlySpan<byte>.Empty));
        Assert.Null(SKPicture.Deserialize(new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<ArgumentNullException>(() => SKPicture.Deserialize((SKData)null!));
        Assert.Throws<ArgumentNullException>(() => SKPicture.Deserialize((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => SKPicture.Deserialize((SKStream)null!));
        Assert.Throws<ArgumentNullException>(() => SKPicture.Deserialize(IntPtr.Zero, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SKPicture.Deserialize((IntPtr)1, -1));

        using var picture = RecordSimplePicture();
        Assert.Throws<ArgumentNullException>(() => picture.Serialize((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => picture.Serialize((SKWStream)null!));

        using var data = picture.Serialize();
        var bytes = data.ToArray();
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.Null(SKPicture.Deserialize(bytes.AsSpan(0, length)));
        }
        Assert.Null(SKPicture.Deserialize(bytes.Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void UnsupportedGpuAndExtensionResourcesFailExplicitly()
    {
        var picture = new GpuPicture(
            [new RenderCommand { Type = RenderCommandType.DrawExtension, DataParam = new object() }],
            [],
            [],
            [],
            []);
        using var wrapper = new SKPicture(picture, new SKRect(0f, 0f, 10f, 10f));

        var exception = Assert.Throws<NotSupportedException>(() => wrapper.Serialize());
        Assert.Contains("custom extension", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SKPicture RecordSimplePicture()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 40f, 30f));
        using var paint = new SKPaint { Color = SKColors.CornflowerBlue };
        canvas.DrawRect(new SKRect(2f, 3f, 12f, 13f), paint);
        return recorder.EndRecording();
    }
}
