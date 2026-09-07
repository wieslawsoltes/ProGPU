using ACadSharp.Entities;
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
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }
}
