using ACadSharp.Entities;
using ACadSharp.Objects;
using ProGPU.CAD.Sample;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadSampleDocumentRoundTripTests
{
    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task RepresentativeScenePreservesEveryEntityIncludingRasterImage(
        CadDocumentFormat format)
    {
        var canvas = new CadSampleCanvas();
        try
        {
            CadDocumentSession session = Assert.IsType<CadDocumentSession>(canvas.CurrentSession);
            string[] originalTypes = session.Read(document => document.Entities
                .Select(entity => entity.GetType().Name).Order().ToArray());
            var store = new CadDocumentStore();
            using var output = new MemoryStream();

            CadSaveResult saved = await store.SaveAsync(session, output, format,
                new CadSaveOptions { AllowUncertifiedWrite = true });
            Assert.DoesNotContain(saved.Diagnostics,
                diagnostic => diagnostic.Message.Contains("Invalid entity", StringComparison.Ordinal));
            output.Position = 0;
            CadLoadResult restored = await store.LoadAsync(output, format);

            Assert.Equal(originalTypes, restored.Session.Read(document => document.Entities
                .Select(entity => entity.GetType().Name).Order().ToArray()));
            RasterImage image = restored.Session.Read(document =>
                Assert.Single(document.Entities.OfType<RasterImage>()));
            Assert.Equal(2, image.ClipBoundaryVertices.Count);
            Assert.Equal("progpu-cad-sample.png", image.Definition.FileName);
            MText text = restored.Session.Read(document =>
                Assert.Single(document.Entities.OfType<MText>()));
            Assert.Equal(@"Column one\NColumn two", text.Value);
            Assert.Equal(2, text.ColumnData.ColumnCount);
            Assert.Equal(new double[] { 12, 12 }, text.ColumnData.Heights);

            // Saving an unrelated edit must not replace the image's reactor or
            // leave old handles in its definition's persistent backlink list.
            ImageDefinitionReactor reactor = Assert.Single(image.Definition.Reactors.OfType<ImageDefinitionReactor>());
            ulong reactorHandle = reactor.Handle;
            for (int i = 0; i < 3; i++)
            {
                restored.Session.Edit("Add line", document => document.Entities.Add(
                    new Line(CSMath.XYZ.Zero, CSMath.XYZ.AxisX)));
                using var resaved = new MemoryStream();
                await store.SaveAsync(restored.Session, resaved, format,
                    new CadSaveOptions { AllowUncertifiedWrite = true });
                Assert.Same(reactor, Assert.Single(image.Definition.Reactors.OfType<ImageDefinitionReactor>()));
                Assert.Equal(reactorHandle, reactor.Handle);
                resaved.Position = 0;
                CadLoadResult again = await store.LoadAsync(resaved, format);
                RasterImage retainedImage = again.Session.Read(document =>
                    Assert.Single(document.Entities.OfType<RasterImage>()));
                Assert.Equal(reactorHandle,
                    Assert.Single(retainedImage.Definition.Reactors.OfType<ImageDefinitionReactor>()).Handle);
            }
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }
}
