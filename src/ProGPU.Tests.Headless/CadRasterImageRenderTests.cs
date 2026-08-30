using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;
using ProGPU.Backend;
using ProGPU.CAD;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class CadRasterImageRenderTests
{
    [Fact]
    public void ImageLeaseIsSharedAcrossDpiAndZoomReplayAndNativePicture()
    {
        HeadlessWindow window = HeadlessWindow.Shared;
        var definition = new ImageDefinition
        {
            Name = "pixel",
            FileName = "pixel.png",
            Size = new XY(1, 1),
        };
        var image = new RasterImage(definition)
        {
            InsertPoint = XYZ.Zero,
            UVector = new XYZ(32, 0, 0),
            VVector = new XYZ(0, 18, 0),
            Size = new XY(1, 1),
            ClippingState = true,
            ClipMode = ClipMode.Outside,
            Flags = ImageDisplayFlags.ShowImage |
                ImageDisplayFlags.UseClippingBoundary |
                ImageDisplayFlags.TransparencyIsOn,
            Brightness = 55,
            Contrast = 60,
            Fade = 10,
        };
        image.ClipBoundaryVertices.AddRange([
            new XY(-0.5, -0.5),
            new XY(0.5, 0.5),
        ]);
        var document = new CadDocument();
        document.Entities.Add(image);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var catalog = new CadRasterImageCatalog();
        CadEncodedRasterImageSource source = catalog.RegisterEncoded(
            "pixel.png",
            EncodedPixel);
        var compiler = new CadPlanSceneCompiler();
        CadRecordedPlanScene dpi96 = compiler.Compile(
            snapshot,
            new CadPlanSceneOptions
            {
                PhysicalDpi = 96,
                RasterImageSourceResolver = catalog,
                RasterImageContext = window.Context,
            });
        CadRecordedPlanScene dpi192 = compiler.Compile(
            snapshot,
            new CadPlanSceneOptions
            {
                PhysicalDpi = 192,
                RasterImageSourceResolver = catalog,
                RasterImageContext = window.Context,
            });
        GpuPicture picture = dpi96.CreatePicture();
        var zoomReplay = new DrawingContext();
        try
        {
            RenderCommand firstImage = Assert.Single(
                dpi96.DrawingContext.Commands.ToArray(),
                command => command.Type == RenderCommandType.DrawExtension);
            RenderCommand secondImage = Assert.Single(
                dpi192.DrawingContext.Commands.ToArray(),
                command => command.Type == RenderCommandType.DrawExtension);
            Assert.Same(firstImage.Texture, secondImage.Texture);
            Assert.Equal(firstImage.Rect, secondImage.Rect);
            Assert.Equal(firstImage.SrcRect, secondImage.SrcRect);
            Assert.Equal(firstImage.Transform, secondImage.Transform);
            Assert.Equal(TextureSamplingMode.Linear, firstImage.TextureSamplingMode);
            Assert.Equal(1, dpi96.DrawingContext.RetainedResourceCount);
            Assert.Equal(1, dpi192.DrawingContext.RetainedResourceCount);

            zoomReplay.DrawPicture(picture, Matrix4x4.CreateScale(1.25f));
            zoomReplay.DrawPicture(picture, Matrix4x4.CreateScale(4.0f));
            Assert.Equal(1, zoomReplay.RetainedResourceCount);
            Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
                picture,
                96U,
                1U,
                out NativeCompiledPicture? native,
                out NativePictureCompileFailure failure),
                failure.ToString());
            Assert.NotNull(native);
            Assert.True(source.TryGetGpuTexture(window.Context, out GpuTexture texture));

            catalog.Dispose();
            Assert.False(texture.IsDisposed);
            dpi96.Dispose();
            dpi192.Dispose();
            Assert.False(texture.IsDisposed);
            picture.Dispose();
            zoomReplay.Clear();
            Assert.True(texture.IsDisposed);
        }
        finally
        {
            picture.Dispose();
            dpi96.Dispose();
            dpi192.Dispose();
            zoomReplay.Clear();
            catalog.Dispose();
        }
    }

    [Fact]
    public void CanceledCompilationReleasesAcquiredImageLease()
    {
        HeadlessWindow window = HeadlessWindow.Shared;
        var document = new CadDocument();
        document.Entities.Add(CreateImage());
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var catalog = new CadRasterImageCatalog();
        CadEncodedRasterImageSource source = catalog.RegisterEncoded(
            "pixel.png",
            EncodedPixel);
        using var cancellation = new CancellationTokenSource();
        var resolver = new CancelingResolver(
            catalog.CreateResolverSnapshot(),
            cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            new CadPlanSceneCompiler().Compile(
                snapshot,
                new CadPlanSceneOptions
                {
                    RasterImageSourceResolver = resolver,
                    RasterImageContext = window.Context,
                },
                cancellation.Token));
        Assert.True(source.TryGetGpuTexture(window.Context, out GpuTexture texture));

        catalog.Dispose();

        Assert.True(texture.IsDisposed);
    }

    private static RasterImage CreateImage()
    {
        var definition = new ImageDefinition
        {
            Name = "pixel",
            FileName = "pixel.png",
            Size = new XY(1, 1),
        };
        return new RasterImage(definition)
        {
            UVector = new XYZ(32, 0, 0),
            VVector = new XYZ(0, 18, 0),
            Size = new XY(1, 1),
            Flags = ImageDisplayFlags.ShowImage |
                ImageDisplayFlags.TransparencyIsOn,
        };
    }

    private sealed class CancelingResolver(
        ICadRasterImageSourceResolver inner,
        CancellationTokenSource cancellation) : ICadRasterImageSourceResolver
    {
        public bool TryResolve(
            in CadRasterImageRequest request,
            out IProGpuTextureLeaseSource source)
        {
            bool resolved = inner.TryResolve(request, out source);
            cancellation.Cancel();
            return resolved;
        }
    }

    private static ReadOnlySpan<byte> EncodedPixel =>
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x04, 0x00, 0x00, 0x00, 0xb5, 0x1c, 0x0c,
        0x02, 0x00, 0x00, 0x00, 0x0b, 0x49, 0x44, 0x41,
        0x54, 0x78, 0xda, 0x63, 0xfc, 0xff, 0x1f, 0x00,
        0x02, 0xeb, 0x01, 0xf5, 0x8f, 0x59, 0x73, 0xe8,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44,
        0xae, 0x42, 0x60, 0x82,
    ];
}
