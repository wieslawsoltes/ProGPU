using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadGradientHatchCopyTests
{
    [Theory]
    [InlineData(CadDocumentFormat.Dxf, false)]
    [InlineData(CadDocumentFormat.Dxf, true)]
    [InlineData(CadDocumentFormat.Dwg, false)]
    public async Task CopyUndoRedoAndSavePreserveBothGradientHatches(CadDocumentFormat format, bool binaryDxf)
    {
        var hatch = new Hatch { IsSolid = true };
        var outline = new Hatch.BoundaryPath.Polyline { IsClosed = true };
        outline.Vertices.AddRange([new XYZ(0, 0, 0), new XYZ(20, 0, 0), new XYZ(20, 10, 0), new XYZ(0, 10, 0)]);
        var boundary = new Hatch.BoundaryPath();
        boundary.Edges.Add(outline);
        hatch.Paths.Add(boundary);
        hatch.GradientColor = new HatchGradientPattern("LINEAR")
        {
            Enabled = true,
            Angle = 0.4,
            Shift = 0.25,
            Colors = [
                new GradientColor { Value = 0, Color = new Color(255, 0, 0) },
                new GradientColor { Value = 1, Color = new Color(0, 0, 255) },
            ],
        };
        var document = new CadDocument();
        document.Entities.Add(hatch);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadDuplicateModelSpaceEntityCommand(hatch.Handle, new CadPoint3D(30, 0, 0)));

        Hatch copy = document.Entities.OfType<Hatch>().Single(value => value != hatch);
        Assert.Equal(2, hatch.GradientColor.Colors.Count);
        Assert.Equal(2, copy.GradientColor.Colors.Count);
        Assert.NotSame(hatch.GradientColor.Colors, copy.GradientColor.Colors);
        Assert.NotSame(hatch.GradientColor.Colors[0], copy.GradientColor.Colors[0]);
        Assert.True(history.TryUndo(out _));
        Assert.Single(document.Entities);
        Assert.Equal(2, hatch.GradientColor.Colors.Count);
        Assert.True(history.TryRedo(out _));

        using var output = new MemoryStream();
        var store = new CadDocumentStore();
        await store.SaveAsync(session, output, format,
            new CadSaveOptions { AllowUncertifiedWrite = true, BinaryDxf = binaryDxf });
        output.Position = 0;
        var loaded = await store.LoadAsync(output, format);
        Hatch[] restored = loaded.Session.Read(value => value.Entities.OfType<Hatch>().ToArray());
        Assert.Equal(2, restored.Length);
        foreach (Hatch value in restored)
        {
            Assert.True(value.GradientColor.Enabled);
            Assert.Equal("LINEAR", value.GradientColor.Name);
            Assert.Equal(0.4, value.GradientColor.Angle, 8);
            Assert.Equal(0.25, value.GradientColor.Shift, 8);
            Assert.Equal(2, value.GradientColor.Colors.Count);
            Assert.Equal(new Color(255, 0, 0), value.GradientColor.Colors[0].Color);
            Assert.Equal(new Color(0, 0, 255), value.GradientColor.Colors[1].Color);
        }
    }
}
