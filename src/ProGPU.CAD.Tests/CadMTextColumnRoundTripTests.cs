using ACadSharp;
using ACadSharp.Entities;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMTextColumnRoundTripTests
{
    [Theory]
    [InlineData(CadDocumentFormat.Dxf, ColumnType.StaticColumns, false)]
    [InlineData(CadDocumentFormat.Dwg, ColumnType.StaticColumns, false)]
    [InlineData(CadDocumentFormat.Dxf, ColumnType.DynamicColumns, false)]
    [InlineData(CadDocumentFormat.Dwg, ColumnType.DynamicColumns, false)]
    [InlineData(CadDocumentFormat.Dxf, ColumnType.DynamicColumns, true)]
    [InlineData(CadDocumentFormat.Dwg, ColumnType.DynamicColumns, true)]
    public async Task LoadedColumnsRetainPositionedGlyphsAndNativePicture(
        CadDocumentFormat format, ColumnType type, bool autoHeight)
    {
        var document = new CadDocument();
        document.Header.Version = ACadVersion.AC1032;
        var text = new MText
        {
            Value = @"FIRST\NSECOND",
            Height = 4,
            RectangleWidth = 40,
            RectangleHeight = 50,
            AttachmentPoint = AttachmentPointType.TopLeft,
        };
        text.ColumnData.ColumnType = type;
        text.ColumnData.ColumnCount = 2;
        text.ColumnData.Width = 40;
        text.ColumnData.Gutter = 5;
        text.ColumnData.FlowReversed = true;
        text.ColumnData.AutoHeight = autoHeight;
        if (type == ColumnType.DynamicColumns && !autoHeight)
            text.ColumnData.Heights.AddRange([50, 60]);
        document.Entities.Add(text);
        var options = new CadSnapshotOptions
        {
            TextFontResolver = new CadFontManagerTextResolver(InterFontFamily.Regular),
        };
        var compiler = new CadSnapshotCompiler();
        var session = new CadDocumentSession(document);
        CadDocumentSnapshot before = compiler.Compile(session, options);
        Assert.Single(before.MTexts.ToArray());

        using var output = new MemoryStream();
        var store = new CadDocumentStore();
        await store.SaveAsync(session, output, format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        output.Position = 0;
        var loaded = await store.LoadAsync(output, format);
        MText restored = loaded.Session.Read(value => value.Entities.OfType<MText>().Single());
        Assert.Equal(2, restored.ColumnData.ColumnCount);
        Assert.Equal(text.ColumnData.Heights, restored.ColumnData.Heights);
        CadDocumentSnapshot after = compiler.Compile(loaded.Session, options);
        CadMTextPrimitive primitive = Assert.Single(after.MTexts.ToArray());
        Assert.Equal(2, primitive.ColumnCount);
        Assert.Equal(before.TextGlyphPositions.ToArray(), after.TextGlyphPositions.ToArray());
        Assert.Equal(before.MTexts.Span[0].ContentWidth, primitive.ContentWidth);
        Assert.Equal(before.MTexts.Span[0].ContentHeight, primitive.ContentHeight);
        using var scene = new CadPlanSceneCompiler().Compile(after);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawGlyphRun);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 96U, 1U,
            out NativeCompiledPicture? native, out NativePictureCompileFailure failure), failure.ToString());
        Assert.NotNull(native);
        Assert.True(native.NativeDrawCount > 0);
    }

    [Fact]
    public void CopiedColumnHeightEditsDoNotMutateOriginal()
    {
        var text = new MText();
        text.ColumnData.ColumnType = ColumnType.DynamicColumns;
        text.ColumnData.ColumnCount = 2;
        text.ColumnData.Heights.AddRange([10, 20]);
        var clone = (MText)text.Clone();
        Assert.NotSame(text.ColumnData, clone.ColumnData);
        Assert.NotSame(text.ColumnData.Heights, clone.ColumnData.Heights);
        clone.ColumnData.Heights[0] = 100;
        clone.ColumnData.Heights.Add(30);
        Assert.Equal(new double[] { 10, 20 }, text.ColumnData.Heights);
    }
}
