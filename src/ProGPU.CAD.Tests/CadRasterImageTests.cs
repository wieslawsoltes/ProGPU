using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadRasterImageTests
{
    [Fact]
    public void SnapshotRetainsSharedDefinitionClipEffectsAndSourceContext()
    {
        var document = new CadDocument();
        RasterImage first = CreateImage(out ImageDefinition definition);
        RasterImage second = CreateImage(definition);
        second.InsertPoint = new XYZ(140, 200, 7);
        document.Entities.Add(first);
        document.Entities.Add(second);

        var session = new CadDocumentSession(
            document,
            CadDocumentFormat.Dxf,
            "/drawings/site/plan.dxf");
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                DrawingBackgroundColor = new CadColor32(12, 34, 56, 7),
            });

        Assert.Equal("/drawings/site/plan.dxf", snapshot.SourceName);
        Assert.Equal(2, snapshot.RasterImages.Length);
        CadRasterImageResource resource = Assert.Single(
            snapshot.RasterImageResources.ToArray());
        CadRasterImagePrimitive image = snapshot.RasterImages.Span[0];
        Assert.Equal(CadEntityKind.RasterImage, snapshot.Entities.Span[0].Kind);
        Assert.Equal("textures/site.png", resource.FileName);
        Assert.Equal(8, resource.PixelWidth);
        Assert.Equal(6, resource.PixelHeight);
        Assert.True(resource.IsLoaded);
        Assert.Equal(0, image.ResourceIndex);
        Assert.Equal(0, snapshot.RasterImages.Span[1].ResourceIndex);
        Assert.Equal(new CadPoint3D(100, 200, 7), image.Origin);
        Assert.Equal(new CadPoint3D(2, 0, 0), image.UVector);
        Assert.Equal(new CadPoint3D(0, 3, 0), image.VVector);
        Assert.Equal(60, image.Brightness);
        Assert.Equal(70, image.Contrast);
        Assert.Equal(25, image.Fade);
        Assert.True(image.TransparencyIsOn);
        Assert.True(image.IsHighQuality);
        Assert.Equal(new CadColor32(12, 34, 56), image.FadeColor);
        Assert.Equal(
            [
                new CadWipeoutClipPoint(0, 0),
                new CadWipeoutClipPoint(4, 0),
                new CadWipeoutClipPoint(4, 3),
                new CadWipeoutClipPoint(0, 3),
                new CadWipeoutClipPoint(0, 0),
                new CadWipeoutClipPoint(4, 0),
                new CadWipeoutClipPoint(4, 3),
                new CadWipeoutClipPoint(0, 3),
            ],
            snapshot.RasterImageClipPoints.ToArray());
        Assert.Equal(new CadPoint3D(100, 200, 7), snapshot.Entities.Span[0].Bounds.Min);
        Assert.Equal(new CadPoint3D(108, 209, 7), snapshot.Entities.Span[0].Bounds.Max);
    }

    [Fact]
    public void MissingLeaseIsDiagnosedOnceWhileFramesReplayManagedNativeAndPrint()
    {
        var document = new CadDocument();
        RasterImage first = CreateImage(out ImageDefinition definition);
        document.Entities.Add(first);
        RasterImage second = CreateImage(definition);
        second.InsertPoint = new XYZ(140, 200, 7);
        document.Entities.Add(second);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var resolver = new RejectingResolver();

        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            snapshot,
            new CadPlanSceneOptions { RasterImageSourceResolver = resolver });

        Assert.Equal(2, resolver.ResolveCount);
        Assert.Equal(1, scene.Statistics.UnsupportedRasterImageCount);
        Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE006", scene.Diagnostics.Span[0].Code);
        Assert.Equal(2, scene.DrawingContext.Commands.Count);
        Assert.All(
            scene.DrawingContext.Commands.ToArray(),
            command => Assert.Equal(RenderCommandType.DrawPath, command.Type));
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(2, native.SourceCommandCount);

        using CadPrintPlan print = new CadPrintPlanCompiler(resolver).Compile(
            new CadSnapshotCompiler().Compile(
                new CadDocumentSession(document),
                new CadSnapshotOptions
                {
                    DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
                    DrawingBackgroundColor = new CadColor32(255, 255, 255),
                }));
        Assert.Equal(1, print.SceneStatistics.UnsupportedRasterImageCount);
        Assert.Equal(2, print.SceneStatistics.RecordedCommandCount);
    }

    [Fact]
    public void ImageFrameThreeDisplaysButDoesNotPlotAndDraftQualityIsRetained()
    {
        var document = new CadDocument();
        document.RootDictionary.Add(new RasterVariables
        {
            Name = CadDictionary.AcadImageVars,
            FrameType = ImageFrameType.DisplayNoPlotted,
            DisplayQuality = ImageDisplayQuality.Draft,
        });
        document.Entities.Add(CreateImage(out _));
        var session = new CadDocumentSession(document);
        var compiler = new CadSnapshotCompiler();

        CadRasterImagePrimitive screen = compiler.Compile(session).RasterImages.Span[0];
        CadRasterImagePrimitive plot = compiler.Compile(
            session,
            new CadSnapshotOptions
            {
                DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
            }).RasterImages.Span[0];

        Assert.True(screen.DrawFrame);
        Assert.False(screen.IsHighQuality);
        Assert.False(plot.DrawFrame);
    }

    [Fact]
    public void InvertedClipAndHiddenFrameUseExactPointAndBoundsSelection()
    {
        var document = new CadDocument();
        document.RootDictionary.Add(new RasterVariables
        {
            Name = CadDictionary.AcadImageVars,
            FrameType = ImageFrameType.NoDisplayOrPlotted,
        });
        RasterImage image = CreateImage(out _);
        image.ClipMode = ClipMode.Inside;
        document.Entities.Add(image);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadEntityHeader header = snapshot.Entities.Span[0];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);

        Assert.True(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(114, 215, 7),
            0.01).IsHit);
        Assert.False(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(104, 204, 7),
            0.01).IsHit);
        Assert.True(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(100, 200, 7.005),
            0.01).IsHit);
        Assert.True(CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(113, 214, 6.9),
                new CadPoint3D(115, 216, 7.1)),
            CadBoundsSelectionMode.Crossing).IsHit);
        Assert.False(CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(103, 203, 6.9),
                new CadPoint3D(105, 205, 7.1)),
            CadBoundsSelectionMode.Crossing).IsHit);
    }

    [Fact]
    public void GenericEditingMovesRotatesScalesDuplicatesAndUndoRedo()
    {
        var document = new CadDocument();
        RasterImage image = CreateImage(out _);
        document.Entities.Add(image);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadTranslateEntitiesCommand(
            [image.Handle],
            new CadPoint3D(10, -20, 5)));
        history.Execute(new CadRotateEntitiesCommand(
            [image.Handle],
            new CadPoint3D(0, 0, 1),
            Math.PI / 2));
        history.Execute(new CadScaleEntitiesCommand([image.Handle], 2));
        history.Execute(new CadDuplicateModelSpaceEntityCommand(
            image.Handle,
            new CadPoint3D(50, 0, 0)));

        CadRasterImagePrimitive[] edited =
            new CadSnapshotCompiler().Compile(session).RasterImages.ToArray();
        Assert.Equal(2, edited.Length);
        Assert.True(IsNear(edited[0].Origin, new CadPoint3D(-360, 220, 24)));
        Assert.True(IsNear(edited[0].UVector, new CadPoint3D(0, 4, 0)));
        Assert.True(IsNear(edited[0].VVector, new CadPoint3D(-6, 0, 0)));
        Assert.True(IsNear(edited[1].Origin, edited[0].Origin + new CadPoint3D(50, 0, 0)));
        Assert.True(history.TryUndo(out _));
        Assert.Single(new CadSnapshotCompiler().Compile(session).RasterImages.ToArray());
        Assert.True(history.TryRedo(out _));
        Assert.Equal(2, new CadSnapshotCompiler().Compile(session).RasterImages.Length);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task MetadataClipEffectsAndFramePolicySurviveDxfAndDwg(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        document.RootDictionary.Add(new RasterVariables
        {
            Name = CadDictionary.AcadImageVars,
            FrameType = ImageFrameType.DisplayNoPlotted,
            DisplayQuality = ImageDisplayQuality.Draft,
        });
        RasterImage source = CreateImage(out _);
        source.ClipMode = ClipMode.Inside;
        document.Entities.Add(source);
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"site.{format.ToString().ToLowerInvariant()}");
        CadRasterImagePrimitive restored = Assert.Single(
            new CadSnapshotCompiler().Compile(loaded.Session).RasterImages.ToArray());

        Assert.True(restored.IsClipped);
        Assert.True(restored.IsInverted);
        Assert.True(restored.DrawFrame);
        Assert.False(restored.IsHighQuality);
        Assert.Equal(60, restored.Brightness);
        Assert.Equal(70, restored.Contrast);
        Assert.Equal(25, restored.Fade);
        Assert.Equal(4, restored.ClipPointCount);
        Assert.Equal(
            ImageFrameType.DisplayNoPlotted,
            loaded.Session.Read(value =>
                value.GetCadObjects<RasterVariables>().Single().FrameType));
    }

    [Fact]
    public void CatalogResolvesRawHandleAndDocumentRelativePathsWithoutIo()
    {
        using var catalog = new CadRasterImageCatalog();
        var source = new RejectingTextureSource();
        catalog.RegisterSource(
            "/drawings/site/textures/site.png",
            source,
            definitionHandle: 42);
        ICadRasterImageSourceResolver snapshot = catalog.CreateResolverSnapshot();
        var byHandle = new CadRasterImageRequest(
            null,
            new CadRasterImageResource(42, "missing.png", 8, 6, true));
        var byRelativePath = new CadRasterImageRequest(
            "/drawings/site/plan.dxf",
            new CadRasterImageResource(0, "textures\\site.png", 8, 6, true));

        Assert.True(snapshot.TryResolve(byHandle, out var handleSource));
        Assert.Same(source, handleSource);
        Assert.True(snapshot.TryResolve(byRelativePath, out var pathSource));
        Assert.Same(source, pathSource);
    }

    [Fact]
    public void EncodedSourceRejectsUnboundedPayloadsBeforeDecode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadEncodedRasterImageSource(
                [1, 2, 3],
                new CadEncodedRasterImageOptions { MaxEncodedBytes = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadEncodedRasterImageSource(
                [1],
                new CadEncodedRasterImageOptions { MaxDecodedPixels = 0 }));
    }

    private static RasterImage CreateImage(out ImageDefinition definition)
    {
        definition = new ImageDefinition
        {
            Name = "Site image",
            FileName = "textures/site.png",
            Size = new XY(8, 6),
            DefaultSize = new XY(1, 1),
            IsLoaded = true,
        };
        return CreateImage(definition);
    }

    private static RasterImage CreateImage(ImageDefinition definition)
    {
        var image = new RasterImage(definition)
        {
            InsertPoint = new XYZ(100, 200, 7),
            UVector = new XYZ(2, 0, 0),
            VVector = new XYZ(0, 3, 0),
            Size = new XY(8, 6),
            ClippingState = true,
            ClipMode = ClipMode.Outside,
            Flags = ImageDisplayFlags.ShowImage |
                ImageDisplayFlags.ShowNotAlignedImage |
                ImageDisplayFlags.UseClippingBoundary |
                ImageDisplayFlags.TransparencyIsOn,
            Brightness = 60,
            Contrast = 70,
            Fade = 25,
        };
        image.ClipBoundaryVertices.AddRange([
            new XY(-0.5, -0.5),
            new XY(3.5, -0.5),
            new XY(3.5, 2.5),
            new XY(-0.5, 2.5),
        ]);
        return image;
    }

    private static bool IsNear(CadPoint3D actual, CadPoint3D expected) =>
        (actual - expected).Length <= 1e-9;

    private sealed class RejectingResolver : ICadRasterImageSourceResolver
    {
        private readonly RejectingTextureSource _source = new();

        public int ResolveCount { get; private set; }

        public bool TryResolve(
            in CadRasterImageRequest request,
            out IProGpuTextureLeaseSource source)
        {
            ResolveCount++;
            source = _source;
            return true;
        }
    }

    private sealed class RejectingTextureSource : IProGpuTextureLeaseSource
    {
        public bool TryGetGpuTexture(out GpuTexture texture)
        {
            texture = null!;
            return false;
        }

        public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
        {
            lease = null!;
            return false;
        }
    }
}
